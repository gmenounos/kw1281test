using BitFab.KW1281Test.Interface;
using System;
using System.IO;

namespace BitFab.KW1281Test.EDC15
{
    /// <summary>
    /// EDC15 external-flash access via the C167 microcontroller's own hardware boot mode -- a
    /// lower-level path than <see cref="Edc15FlashVM"/> (KWP2000 to the application/loader firmware)
    /// or <see cref="Edc15VM"/> (the serial EEPROM). Reachable only when the ECU is physically placed
    /// into boot mode before power-up (a manual procedure this app can't trigger), then talked to at a
    /// fixed 28800 baud.
    ///
    /// <para><b>Project-native stack.</b> The three RAM programs this uploads are written from scratch in
    /// EDC15/Loader-boot-stage1.a66, Loader-boot-monitor.a66 and
    /// Loader-boot-flash.a66 and assembled with Keil (cross-checked by Tools/c166/reasm.py +
    /// c166dis.py). They implement the project-native protocol in docs/BootMode-Protocol.md; the
    /// images are bundled as the Loader-boot-*.bin embedded resources loaded below. What is dictated
    /// by hardware and therefore the same in any implementation: the C167 BSL contract (send 0x00,
    /// get 0xC5, upload 32 bytes to 0xFA40), the K-line byte-echo, the Am29F400 JEDEC cycles, and the
    /// EBC register values that map the flash on this board.</para>
    ///
    /// <para><b>Protocol.</b> Half-duplex over ASC0 with K-line local echo: every byte the host
    /// transmits is echoed back and consumed (<see cref="SendByte"/>); the Monitor's responses are
    /// read plainly. Each command is CMD, then ACK (0xB1) / NAK (0xBF), command-specific bytes, then a
    /// terminal OK (0xB2) / ERR (0xBE). Addresses are 3 bytes LE, counts 2 bytes LE; block reads and
    /// writes carry a trailing XOR checksum. See docs/BootMode-Protocol.md.</para>
    ///
    /// <para><b>Brick risk</b> is real on write (boot mode IS the recovery net). Read is passive.
    /// Keep a full backup before any erase/write.</para>
    /// </summary>
    public class Edc15BootModeVM
    {
        private const int BaudRate = 28800;
        public const int FlashSize = 0x80000;    // 512 KB Am29F400BT
        private const int ChunkSize = 0x200;     // 512-byte block for read and program
        private const int FlashCpuBase = 0x800000;

        // Monitor status bytes (docs/BootMode-Protocol.md).
        private const byte Ready = 0xB0, Ack = 0xB1, Ok = 0xB2, Err = 0xBE, Nak = 0xBF;
        // Monitor command opcodes.
        private const byte OpPing = 0x20, OpPeek = 0x21, OpPoke = 0x22, OpRead = 0x23,
                           OpWrite = 0x24, OpCall = 0x25;
        // Boot markers (our choices; the 0xC5 CPU id is silicon-fixed).
        private const byte Stage1ReadyMarker = 0xA5;
        private const byte CpuVersionC167 = 0xC5;

        // RAM layout (our choice, within this C167's on-chip RAM; see docs/BootMode-Protocol.md).
        private const int FlashHelperRamAddr = 0xF600;   // helper load address & entry
        private const int FlashHelperEntry   = 0xF600;
        private const int ProgramRamBuffer   = 0xFC00;   // 512-byte staging buffer for program

        // Flash-helper selectors (R8) and the board's write/read segment aliases. (The helper also
        // implements a sector-erase selector 0x01, but the C# only uses whole-chip erase now.)
        private const ushort HelperProgram = 0x00, HelperChipErase = 0x02, HelperAutoselect = 0x06;
        private const byte FlashWriteSegBase = 0x40;     // JEDEC command writes -> segment 0x40+segIdx
        private const byte FlashReadSegBase  = 0x80;     // read-back / DQ7 poll -> segment 0x80+segIdx

        // ---- assembled RAM programs, loaded from the bundled Loader-boot-*.bin embedded resources
        // Uploaded to the ECU's RAM as-is -- no patching (unlike the KWP2000 loaders' EFCD8631 correction). ----
        private static readonly byte[] Stage1 =
            LoadResource("BitFab.KW1281Test.EDC15.Loader-boot-stage1.bin");
        private static readonly byte[] Monitor =
            LoadResource("BitFab.KW1281Test.EDC15.Loader-boot-monitor.bin");
        private static readonly byte[] FlashHelper =
            LoadResource("BitFab.KW1281Test.EDC15.Loader-boot-flash.bin");

        /// <summary>Load a bundled boot-mode RAM program from its embedded resource.</summary>
        private static byte[] LoadResource(string resourceName)
        {
            var assembly = typeof(Edc15BootModeVM).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Unable to load {resourceName} embedded resource.");
            var buf = new byte[stream.Length];
            stream.ReadExactly(buf, 0, buf.Length);
            return buf;
        }

        // Board-fixed EBC config for READ mode (flash appears at 0x800000). Written via POKE.
        private static readonly (ushort Address, ushort Value)[] ReadModeRegisters =
        {
            (0xFF12, 0xE204), (0xFF0C, 0x04AD), (0xFE18, 0x3803), (0xFE1A, 0x2008),
            (0xFE1C, 0x0000), (0xFE1E, 0x0000), (0xFF14, 0x040D), (0xFF16, 0x04AD),
            (0xFF18, 0x0000), (0xFF1A, 0x0000),
        };

        // Board-fixed EBC config for WRITE mode (flash writable at seg 0x40, readable at 0x80).
        private static readonly (ushort Address, ushort Value)[] WriteModeRegisters =
        {
            (0xFF12, 0xE204), (0xFE18, 0x0000), (0xFE1A, 0x0000), (0xFE1C, 0x0000),
            (0xFE1E, 0x0000), (0xFF14, 0x0000), (0xFF16, 0x0000), (0xFF18, 0x0000),
            (0xFF1A, 0x0000), (0xFE1C, 0x4008), (0xFF18, 0x848E), (0xFF0C, 0x04AD),
        };

        private readonly IInterface _interface;

        public Edc15BootModeVM(IInterface @interface)
        {
            _interface = @interface;
        }

        // ==================== low-level K-line primitives ====================

        /// <summary>Send one byte to the Monitor and consume its K-line echo (verifying it).</summary>
        private void SendByte(byte b)
        {
            _interface.WriteByteRaw(b);
            var echo = _interface.ReadByte();
            if (echo != b)
            {
                throw new InvalidOperationException(
                    $"Boot-mode TX echo mismatch: sent 0x{b:X2}, got 0x{echo:X2}.");
            }
        }

        /// <summary>Send a block and consume+verify its echo in one operation.</summary>
        private void SendBytes(byte[] data)
        {
            _interface.WriteBytesRaw(data);
            var echo = new byte[data.Length];
            _interface.ReadBytes(echo, data.Length);
            for (var i = 0; i < data.Length; i++)
            {
                if (echo[i] != data[i])
                {
                    throw new InvalidOperationException(
                        $"Boot-mode TX echo mismatch at byte {i}: sent 0x{data[i]:X2}, got 0x{echo[i]:X2}.");
                }
            }
        }

        private byte Recv() => _interface.ReadByte();

        private void Expect(byte want, string what)
        {
            var got = Recv();
            if (got != want)
            {
                throw new InvalidOperationException($"{what}: expected 0x{want:X2}, got 0x{got:X2}.");
            }
        }

        private void SendAddr(int addr)
        {
            SendByte((byte)(addr & 0xFF));
            SendByte((byte)((addr >> 8) & 0xFF));
            SendByte((byte)((addr >> 16) & 0xFF));
        }

        private void SendCount(int count)
        {
            SendByte((byte)(count & 0xFF));
            SendByte((byte)((count >> 8) & 0xFF));
        }

        /// <summary>Send a command opcode and read the Monitor's ACK (throws on NAK/other).</summary>
        private void BeginCommand(byte op)
        {
            SendByte(op);
            var status = Recv();
            if (status == Nak)
            {
                throw new InvalidOperationException($"Monitor rejected command 0x{op:X2} (NAK).");
            }
            if (status != Ack)
            {
                throw new InvalidOperationException(
                    $"Command 0x{op:X2}: expected ACK 0x{Ack:X2}, got 0x{status:X2}.");
            }
        }

        // ==================== typed monitor commands ====================

        private void Ping()
        {
            BeginCommand(OpPing);
            Expect(Ok, "PING");
        }

        private ushort Peek(int addr)
        {
            BeginCommand(OpPeek);
            SendAddr(addr);
            var lo = Recv();
            var hi = Recv();
            Expect(Ok, "PEEK");
            return (ushort)(lo | (hi << 8));
        }

        private void Poke(int addr, ushort value)
        {
            BeginCommand(OpPoke);
            SendAddr(addr);
            SendCount(value);
            Expect(Ok, $"POKE 0x{addr:X4}");
        }

        /// <summary>READ <paramref name="count"/> bytes from <paramref name="addr"/> into
        /// <paramref name="buffer"/>, checking the Monitor's trailing XOR.</summary>
        private void ReadBlockInto(int addr, byte[] buffer, int count)
        {
            BeginCommand(OpRead);
            SendAddr(addr);
            SendCount(count);
            _interface.ReadBytes(buffer, count);
            var ecuXor = Recv();
            Expect(Ok, "READ");
            byte localXor = 0;
            for (var i = 0; i < count; i++) localXor ^= buffer[i];
            if (ecuXor != localXor)
            {
                throw new InvalidOperationException(
                    $"READ 0x{addr:X6} checksum mismatch: ECU 0x{ecuXor:X2}, computed 0x{localXor:X2}.");
            }
        }

        /// <summary>WRITE <paramref name="data"/> to RAM at <paramref name="addr"/> (byte writes with
        /// per-byte read-back verify in the Monitor), checking the trailing XOR and OK/ERR.</summary>
        private void WriteBlock(int addr, byte[] data)
        {
            BeginCommand(OpWrite);
            SendAddr(addr);
            SendCount(data.Length);
            SendBytes(data);
            var ecuXor = Recv();
            var status = Recv();
            byte localXor = 0;
            foreach (var b in data) localXor ^= b;
            if (status == Err)
            {
                throw new InvalidOperationException(
                    $"WRITE 0x{addr:X6} failed: the Monitor's read-back verify did not match.");
            }
            if (status != Ok)
            {
                throw new InvalidOperationException(
                    $"WRITE 0x{addr:X6}: expected OK 0x{Ok:X2}, got 0x{status:X2}.");
            }
            if (ecuXor != localXor)
            {
                throw new InvalidOperationException(
                    $"WRITE 0x{addr:X6} checksum mismatch: ECU 0x{ecuXor:X2}, computed 0x{localXor:X2}.");
            }
        }

        /// <summary>CALL a routine at <paramref name="entry"/> with R8..R15 = <paramref name="regs8"/>,
        /// returning the post-call R8..R15 image.</summary>
        private ushort[] Call(int entry, ushort[] regs8)
        {
            BeginCommand(OpCall);
            SendAddr(entry);
            var ctx = new byte[16];
            for (var i = 0; i < 8; i++)
            {
                ctx[i * 2] = (byte)(regs8[i] & 0xFF);
                ctx[i * 2 + 1] = (byte)(regs8[i] >> 8);
            }
            SendBytes(ctx);
            var outBytes = new byte[16];
            _interface.ReadBytes(outBytes, 16);
            Expect(Ok, "CALL");
            var result = new ushort[8];
            for (var i = 0; i < 8; i++)
            {
                result[i] = (ushort)(outBytes[i * 2] | (outBytes[i * 2 + 1] << 8));
            }
            return result;
        }

        // ==================== boot handshake ====================

        /// <summary>28800-baud BSL hello, upload Stage-1 and the Monitor, and confirm the Monitor is
        /// up (READY banner + a PING). Leaves the Monitor running.</summary>
        private void PerformBootHandshake(Func<bool>? isStopRequested)
        {
            _interface.SetBaudRate(BaudRate);
            _interface.ClearReceiveBuffer();
            _interface.ReadTimeout = 20_000;

            Log.WriteLine("Waiting for the ECU's boot-mode hello...");
            _interface.WriteByteRaw(0x00);              // BSL auto-baud
            var echo = _interface.ReadByte();
            if (echo != 0x00)
            {
                throw new UnableToProceedException();
            }
            var cpu = _interface.ReadByte();
            Log.WriteLine($"Got CPU version: 0x{cpu:X2}");
            if (cpu != CpuVersionC167)
            {
                Log.WriteLine(
                    $"WARNING: expected CPU version 0x{CpuVersionC167:X2} but got 0x{cpu:X2} -- " +
                    "continuing, but this stack is validated only against the C167 family.");
            }

            if (isStopRequested?.Invoke() == true)
            {
                throw new OperationCanceledException("Stopped during boot-mode connect.");
            }

            Log.WriteLine($"Uploading stage-1 ({Stage1.Length} bytes)...");
            SendBytes(Stage1);
            Expect(Stage1ReadyMarker, "Stage-1 ready marker");

            Log.WriteLine($"Uploading monitor ({Monitor.Length} bytes)...");
            SendBytes(Monitor);
            Expect(Ready, "Monitor READY banner");

            Ping();                                     // sanity check the command loop
            Log.WriteLine("Boot-mode monitor is up.");
        }

        // ==================== read path ====================

        /// <summary>Boot-mode connect for READ: handshake, then the read-mode EBC config.</summary>
        public void Connect(Func<bool>? isStopRequested = null)
        {
            PerformBootHandshake(isStopRequested);

            Log.WriteLine("Configuring external bus for flash read...");
            foreach (var (address, value) in ReadModeRegisters)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped during boot-mode connect.");
                }
                Poke(address, value);
                var readback = Peek(address);
                Log.WriteLine(
                    $"  set register 0x{address:X4} = 0x{value:X4}: " +
                    (readback == value ? "verified" : $"readback 0x{readback:X4}"));
            }
            Log.WriteLine("Boot-mode connect complete (read mode).");
        }

        /// <summary>Read the full 512 KB external flash to <paramref name="filename"/> as 1024
        /// 512-byte blocks from CPU-bus 0x800000, each XOR-checked by the Monitor.</summary>
        public void ReadFlash(
            string filename, Action<int>? onPercent = null, Func<bool>? isStopRequested = null)
        {
            using var fs = File.Create(filename);
            var buffer = new byte[ChunkSize];
            var before = -1;
            for (var offset = 0; offset < FlashSize; offset += ChunkSize)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped during boot-mode flash read.");
                }
                ReadBlockInto(FlashCpuBase + offset, buffer, ChunkSize);
                fs.Write(buffer, 0, ChunkSize);
                var percent = (offset + ChunkSize) * 100 / FlashSize;
                if (percent != before) { onPercent?.Invoke(percent); before = percent; }
            }
        }

        // ==================== write path ====================

        /// <summary>Boot-mode connect for WRITE: handshake, write-mode EBC config, and upload the
        /// flash helper to RAM 0xF600. Requires the ECU already in boot mode.
        public void ConnectForWrite(Func<bool>? isStopRequested = null)
        {
            PerformBootHandshake(isStopRequested);

            Log.WriteLine("Configuring external bus for flash write...");
            foreach (var (address, value) in WriteModeRegisters)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped during boot-mode connect.");
                }
                Poke(address, value);
            }

            Log.WriteLine($"Uploading flash helper ({FlashHelper.Length} bytes) to RAM 0x{FlashHelperRamAddr:X4}...");
            WriteBlock(FlashHelperRamAddr, FlashHelper);
            Log.WriteLine("Boot-mode connect complete (write mode).");
        }

        /// <summary>Whole-chip-erase and program a full 512 KB image via the flash helper. Call
        /// <see cref="ConnectForWrite"/> first.</summary>
        public void EraseAndWriteFlash(
            byte[] image, Action<int>? onPercent = null, Func<bool>? isStopRequested = null)
        {
            if (image.Length != FlashSize)
            {
                throw new ArgumentException(
                    $"image is {image.Length} bytes; boot-mode write needs exactly {FlashSize} (0x{FlashSize:X}).",
                    nameof(image));
            }

            // Autoselect: read the device ID (returned in R9). Logged, not hard-failed.
            var id = Call(FlashHelperEntry, new ushort[]
            {
                HelperAutoselect, FlashWriteSegBase, FlashReadSegBase, 1, 0, 0, 0, 0,
            });
            Log.WriteLine($"Flash device ID (autoselect): 0x{id[1]:X4} (R15=0x{id[7]:X4}).");

            // ---- whole-chip erase ----
            if (isStopRequested?.Invoke() == true) throw new OperationCanceledException("Stopped before erase.");
            Log.WriteLine("Erasing whole chip (JEDEC 0x10)...");
            var erase = Call(FlashHelperEntry, new ushort[]
            {
                HelperChipErase, FlashWriteSegBase, FlashReadSegBase, 0, 0, 0, 0, 0,
            });
            if (erase[7] != 0)
            {
                throw new InvalidOperationException(
                    $"Whole-chip erase failed (helper R15=0x{erase[7]:X4}; 0x31 = erase timeout). " +
                    "Nothing further was written; re-read to check the chip's state.");
            }
            Log.WriteLine("Whole-chip erase complete.");

            // ---- program ----
            var before = -1;
            for (var offset = 0; offset < FlashSize; offset += ChunkSize)
            {
                if (isStopRequested?.Invoke() == true) throw new OperationCanceledException("Stopped during flash write.");

                var block = new byte[ChunkSize];
                Array.Copy(image, offset, block, 0, ChunkSize);

                var blank = true;
                foreach (var b in block) { if (b != 0xFF) { blank = false; break; } }
                if (!blank)
                {
                    WriteBlock(ProgramRamBuffer, block);            // stage the block into RAM
                    var segIdx = (byte)(offset >> 16);
                    var destOff = (ushort)(offset & 0xFFFF);
                    var r = Call(FlashHelperEntry, new ushort[]
                    {
                        HelperProgram, (ushort)(FlashWriteSegBase + segIdx), (ushort)(FlashReadSegBase + segIdx),
                        destOff, ProgramRamBuffer, ChunkSize, 0, 0,
                    });
                    if (r[7] != 0)
                    {
                        throw new InvalidOperationException(
                            $"Program failed at flash offset 0x{offset:X5} (helper R15=0x{r[7]:X4}).");
                    }
                }

                var percent = (offset + ChunkSize) * 100 / FlashSize;
                if (percent != before) { onPercent?.Invoke(percent); before = percent; }
            }

            Log.WriteLine("Boot-mode flash write complete.");
        }
    }
}
