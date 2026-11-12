using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BitFab.KW1281Test.Cluster;

partial class MarelliCluster : ICluster
{
    public void UnlockForEepromReadWrite()
    {
        // Nothing to do
    }

    public string DumpEeprom(uint? address, uint? length, string? dumpFileName)
    {
        address ??= GetDefaultAddress();
        if (length is null)
        {
            if (address == 0x0C00)
            {
                length = 1024;
            }
            else
            {
                length = 2048;
            }
        }

        dumpFileName ??= $"marelli_mem_0x{address:X4}.bin";

        var bytes = DumpMem(dumpFileName, (ushort)address, (ushort)length);
        if (bytes.Length == 0)
        {
            File.Delete(dumpFileName);
        }

        return dumpFileName;
    }

    /// <summary>
    /// Dumps the cluster's flash. On the 128 KB DG128A parts this walks all eight 16 KB
    /// pages through the PPAGE window; on the 64 KB parts it reads the flat $1000-$FFFF
    /// range. The low 4 KB of the 64 KB parts is RAM/registers/EEPROM, not flash.
    /// </summary>
    public string DumpFlash(string? dumpFileName, int? dumpBaud = null)
    {
        int hostBaud = dumpBaud ?? 9600;
        byte divisor = BaudToDivisor(hostBaud, 8_000_000);
        Log.WriteLine($"Dump baud: {hostBaud} (divisor ${divisor:X2}, actual {BaudFromDivisor(divisor, 8_000_000)}).");
        if (hostBaud > 9600)
        {
            Log.WriteLine("The raw dump has no checksum; verify the result (CRC-32) after a fast dump.");
        }

        if (HasSmallEeprom())
        {
            dumpFileName ??= "marelli_flash_64k.bin";
            Log.WriteLine("Known cluster. Appears to have the small EEPROM.");
            Log.WriteLine("64 KB flat flash: dumping $1000-$FFFF (60 KB).");
            Log.WriteLine("The low 4 KB ($0000-$0FFF) is RAM, registers and EEPROM, not flash.");
            return DumpFlatFlash(dumpFileName, divisor, hostBaud);
        }

        if (HasLargeEeprom() || VinRegex.IsMatch(_ecuInfo))
        {
            dumpFileName ??= "marelli_flash_128k.bin";
            Log.WriteLine(HasLargeEeprom()
                ? "Known cluster. Appears to have the large EEPROM."
                : "Unknown cluster. Appears to have the large EEPROM.");
            Log.WriteLine("128 KB paged flash: dumping 8 x 16 KB pages through the PPAGE window.");
            return DumpBankedFlash(
                dumpFileName,
                IsFirstGenAudiTT() ? (byte)0x00 : (byte)0x18,
                divisor,
                hostBaud);
        }

        if (_ecuInfo.Contains("Ident.-Nr. Wegfahrs.:"))
        {
            dumpFileName ??= "marelli_flash_64k.bin";
            Log.WriteLine("Unknown cluster. Appears to have the small EEPROM.");
            Log.WriteLine("64 KB flat flash: dumping $1000-$FFFF (60 KB).");
            Log.WriteLine("The low 4 KB ($0000-$0FFF) is RAM, registers and EEPROM, not flash.");
            return DumpFlatFlash(dumpFileName, divisor, hostBaud);
        }

        Log.WriteLine();
        Log.WriteLine("Unsupported Marelli cluster version.");
        Log.WriteLine("Flash dump is only supported for known small/large EEPROM clusters.");
        throw new UnableToProceedException();
    }

    private static readonly Regex VinRegex = VINRegex();

    private ushort GetDefaultAddress()
    {
        if (HasSmallEeprom())
        {
            Log.WriteLine("Known cluster. Appears to have the small EEPROM.");
            return 0x0C00; // 3072
        }
        else if (HasLargeEeprom())
        {
            Log.WriteLine("Known cluster. Appears to have the large EEPROM.");
            return 0x3800; // 14336
        }
        else if (VinRegex.IsMatch(_ecuInfo))
        {
            Log.WriteLine("Unknown cluster. Appears to have the large EEPROM.");
            return 0x3800; // 14336
        }
        else if (_ecuInfo.Contains("Ident.-Nr. Wegfahrs.:"))
        {
            Log.WriteLine("Unknown cluster. Appears to have the small EEPROM.");
            return 0x0C00; // 3072
        }
        else
        {
            Log.WriteLine();
            Log.WriteLine("Unsupported Marelli cluster version.");
            Log.WriteLine("You can try the following commands to see if either produces a dump file.");
            Log.WriteLine("Then please contact the program author with the results.");
            Log.WriteLine();

            var prefix = string.Join(' ', Program.CommandAndArgs.Take(4));
            Log.WriteLine($"{prefix} DumpMarelliMem 3072 1024");
            Log.WriteLine($"{prefix} DumpMarelliMem 14336 2048");

            throw new UnableToProceedException();
        }
    }

    /// <summary>
    /// Dumps memory from a Marelli cluster to a file.
    /// </summary>
    private byte[] DumpMem(
        string filename,
        ushort address,
        ushort count)
    {
        byte entryH; // High byte of code entry point
        byte regBlockH; // High byte of register block

        if (address == 0x3800 && IsFirstGenAudiTT())
        {
            Log.WriteLine("Cluster appears to be first gen Audi TT with large EEPROM.");
            entryH = 0x00; // $0000
            regBlockH = 0x20;
        }
        else if (HasSmallEeprom())
        {
            entryH = 0x02; // $0200
            regBlockH = 0x08; // $0800
        }
        else if (HasLargeEeprom())
        {
            entryH = 0x18; // $1800
            regBlockH = 0x20; // $2000
        }
        else if (address == 3072 && count == 1024)
        {
            Log.WriteLine("Untested cluster version! You may need to disconnect your battery if this fails.");

            entryH = 0x02;
            regBlockH = 0x08;
        }
        else if (address == 14336 && count == 2048)
        {
            Log.WriteLine("Untested cluster version! You may need to disconnect your battery if this fails.");

            entryH = 0x18;
            regBlockH = 0x20;
        }
        else
        {
            Log.WriteLine("Unsupported cluster software version");
            return [];
        }

        Log.WriteLine($"entryH: 0x{entryH:X2}, regBlockH: 0x{regBlockH:X2}, count: 0x{count:X4}");

        if (!EnterLoader(entryH))
        {
            return [];
        }

        // Now we write a small memory dump program to the 68HC12 processor

        Log.WriteLine("Writing memory dump program to cluster microcontroller");
        Log.WriteLine($"(Entry: ${entryH:X2}00, RegBlock: ${regBlockH:X2}00, Start: ${address:X4}, Count: ${count:X4})");

        var program = BuildFlatDumpProgram(entryH, regBlockH, address, count, 0x34);
        if (!WriteMarelliBlockAndReadAck(program))
        {
            return [];
        }

        Log.WriteLine("Receiving memory dump");

        var mem = ReadDumpBytes(count);

        File.WriteAllBytes(filename, mem);
        Log.WriteLine($"Saved memory dump to {filename}");

        Log.WriteLine("Done");

        _kwp1281.SetDisconnected(); // Don't try to send EndCommunication block

        return mem;
    }

    /// <summary>
    /// Enters the 0x6C RAM loader and sends the header block, retrying with the unlock
    /// frame if the loader does not acknowledge the header.
    /// </summary>
    private bool EnterLoader(byte entryH, byte baudDivisor = 0x34, int hostBaud = 9600)
    {
        Log.WriteLine("Sending block 0x6C");
        _kwp1281.SendBlock([0x6C]);

        Thread.Sleep(200);
        _kwp1281.KwpCommon.Interface.ClearReceiveBuffer();   // optional, defensive

        var oldReadTimeout = _kwp1281.KwpCommon.Interface.ReadTimeout;
        _kwp1281.KwpCommon.Interface.ReadTimeout = 500;
        try
        {
            // Note that the loader header block should not contain any 0x0A bytes or the first
            // one will be interpreted as the beginning of an unlock frame by certain clusters.
            if (!WriteLoaderHeaderBlock(entryH, baudDivisor))
            {
                return false;
            }
        }
        catch (TimeoutException)
        {
            Log.WriteLine("Timed out. Cluster may need an unlock frame.");
            SendUnlockFrame();

            // Now try sending the header block again
            if (!WriteLoaderHeaderBlock(entryH, baudDivisor))
            {
                return false;
            }
        }
        finally
        {
            _kwp1281.KwpCommon.Interface.ReadTimeout = oldReadTimeout;
        }

        // The loader sent the header ACK at the old baud and has now programmed SC1BDL
        // from the header. Switch the tester to match before the load block.
        if (hostBaud != 9600)
        {
            Log.WriteLine($"Switching to {hostBaud} baud for the dump.");
            _kwp1281.KwpCommon.Interface.SetBaudRate(hostBaud);
        }

        return true;
    }

    private byte[] ReadDumpBytes(int count)
    {
        var kwpCommon = _kwp1281.KwpCommon;
        var mem = new byte[count];
        for (int i = 0; i < count; i++)
        {
            mem[i] = kwpCommon.ReadByte();
        }

        return mem;
    }

    /// <summary>
    /// Builds the small RAM program that dumps a flat 16-bit address range.
    /// The first two bytes of the returned array are the load address; the loader
    /// consumes them and writes the rest of the program to that address.
    /// </summary>
    private static byte[] BuildFlatDumpProgram(
        byte entryH, byte regBlockH, ushort address, ushort count, byte baudDivisor)
    {
        var startH = (byte)(address / 256);
        var startL = (byte)(address % 256);

        var end = address + count;
        var endH = (byte)(end / 256);
        var endL = (byte)(end % 256);

        return
        [
            entryH, 0x00, // Address $xx00

            0x14, 0x50,                     // orcc #$50
            0x07, 0x32,                     // bsr FeedWatchdog

            // Set baud rate to 9600
            0xC7,                           // clrb
            0x7B, regBlockH, 0xC8,          // stab SC1BDH
            0xC6, baudDivisor,              // ldab #<dump divisor>
            0x7B, regBlockH, 0xC9,          // stab SC1BDL

            // Enable transmit, disable UART interrupts
            0xC6, 0x08,                     // ldab #$08
            0x7B, regBlockH, 0xCB,          // stab SC1CR2

            0xCE, startH, startL,           // ldx #start
            // SendLoop:
            0xA6, 0x30,                     // ldaa 1,X+
            0x07, 0x0F,                     // bsr SendByte
            0x8E, endH, endL,               // cpx #end
            0x26, 0xF7,                     // bne SendLoop
            // Poison the watchdog to force a reboot
            0xCC, 0x11, 0x11,               // ldd #$1111
            0x7B, regBlockH, 0x17,          // stab COPRST
            0x7A, regBlockH, 0x17,          // staa COPRST
            0x3D,                           // rts

            // SendByte:
            0xF6, regBlockH, 0xCC,          // ldab SC1SR1
            0x7A, regBlockH, 0xCF,          // staa SC1DRL
            // TxBusy:
            0x07, 0x06,                     // bsr FeedWatchdog
            // Loop until TC (Transmit Complete) bit is set
            0x1F, regBlockH, 0xCC, 0x40, 0xF9,   // brclr SC1SR1,$40,TxBusy
            0x3D,                           // rts

            // FeedWatchdog:
            0xCC, 0x55, 0xAA,               // ldd #$55AA
            0x7A, regBlockH, 0x17,          // staa COPRST
            0x7B, regBlockH, 0x17,          // stab COPRST
            0x3D,                           // rts
        ];
    }

    /// <summary>
    /// Dumps 128 KB of paged flash by setting PPAGE for each 16 KB page and reading the
    /// $8000-$BFFF window. Also reads the fixed page 6/7 mappings so the host can verify
    /// that the bank switch worked.
    /// </summary>
    private string DumpBankedFlash(
        string filename, byte entryH, byte baudDivisor, int hostBaud)
    {
        const byte regBlockH = 0x20;    // DG128A: registers at $2000
        const byte ppageOffset = 0xFF;  // DG128A PPAGE is at register base + $FF
        const int pageSize = 0x4000;    // 16 KB
        const int pageCount = 8;        // 128 KB
        const int flashSize = pageSize * pageCount;
        const int transferSize = flashSize + (2 * pageSize); // + fixed page 6/7 self-check

        if (!EnterLoader(entryH, baudDivisor, hostBaud))
        {
            return string.Empty;
        }

        Log.WriteLine("Writing flash dump program to cluster microcontroller");
        Log.WriteLine($"(Entry: ${entryH:X2}00, RegBlock: ${regBlockH:X2}00, Pages: 0-{pageCount - 1}, PPAGE: ${regBlockH:X2}{ppageOffset:X2})");

        var program = BuildBankedFlashProgram(entryH, regBlockH, baudDivisor);
        if (!WriteMarelliBlockAndReadAck(program))
        {
            return string.Empty;
        }

        Log.WriteLine($"Receiving {transferSize} bytes ({flashSize / 1024} KB flash + {2 * pageSize / 1024} KB self-check)");
        var mem = ReadDumpBytes(transferSize);

        // Pages 6 and 7 are also fixed at $4000-$7FFF and $C000-$FFFF. The program reads
        // them a second time through the fixed mapping, so matching copies prove that the
        // PPAGE switch actually selected the requested page.
        bool page6Matches = mem.AsSpan(6 * pageSize, pageSize)
            .SequenceEqual(mem.AsSpan(flashSize, pageSize));
        bool page7Matches = mem.AsSpan(7 * pageSize, pageSize)
            .SequenceEqual(mem.AsSpan(flashSize + pageSize, pageSize));

        if (page6Matches && page7Matches)
        {
            Log.WriteLine("Self-check passed: PPAGE pages 6/7 match the fixed pages.");
        }
        else
        {
            if (!page6Matches)
            {
                Log.WriteLine("WARNING: page 6 read via PPAGE does not match fixed page 6 at $4000-$7FFF.");
                Log.WriteLine("(MISC.ROMHM can disable the fixed $4000-$7FFF mapping; verify page 7 instead.)");
            }

            if (!page7Matches)
            {
                Log.WriteLine("WARNING: page 7 read via PPAGE does not match fixed page 7 at $C000-$FFFF.");
            }
        }

        File.WriteAllBytes(filename, mem[..flashSize]);
        Log.WriteLine($"Saved flash dump to {filename}");

        Log.WriteLine("Done");

        _kwp1281.SetDisconnected(); // Don't try to send EndCommunication block

        return filename;
    }

    /// <summary>
    /// Builds the RAM program that dumps all eight 16 KB flash pages through the PPAGE
    /// window, then reads the fixed page 6/7 mappings for the bank-switch self-check.
    /// The first two bytes of the returned array are the load address.
    /// </summary>
    private static byte[] BuildBankedFlashProgram(
        byte entryH, byte regBlockH, byte baudDivisor)
    {
        const byte ppageOffset = 0xFF; // DG128A: PPAGE is at register base + $FF

        return
        [
            entryH, 0x00, // Address $xx00

            0x14, 0x50,                     // orcc #$50
            0x07, 0x58,                     // bsr FeedWatchdog

            // Set baud rate to 9600
            0xC7,                           // clrb
            0x7B, regBlockH, 0xC8,          // stab SC1BDH
            0xC6, baudDivisor,              // ldab #<dump divisor>
            0x7B, regBlockH, 0xC9,          // stab SC1BDL

            // Enable transmit, disable UART interrupts
            0xC6, 0x08,                     // ldab #$08
            0x7B, regBlockH, 0xCB,          // stab SC1CR2

            // Dump pages 0-7 through the $8000-$BFFF window
            0xCD, 0x00, 0x00,               // ldy #$0000
            // PageLoop:
            0xB7, 0x61,                     // tfr y,b
            0x7B, regBlockH, ppageOffset,   // stab PPAGE
            0xCE, 0x80, 0x00,               // ldx #$8000
            // WindowSend:
            0xA6, 0x30,                     // ldaa 1,X+
            0x07, 0x2D,                     // bsr SendByte
            0x8E, 0xC0, 0x00,               // cpx #$C000
            0x26, 0xF7,                     // bne WindowSend
            0x02,                           // iny
            0x8D, 0x00, 0x08,               // cpy #$0008
            0x26, 0xE9,                     // bne PageLoop

            // Self-check: page 6 via the fixed $4000-$7FFF mapping
            0xCE, 0x40, 0x00,               // ldx #$4000
            // Fixed6Send:
            0xA6, 0x30,                     // ldaa 1,X+
            0x07, 0x1B,                     // bsr SendByte
            0x8E, 0x80, 0x00,               // cpx #$8000
            0x26, 0xF7,                     // bne Fixed6Send

            // Self-check: page 7 via the fixed $C000-$FFFF mapping
            0xCE, 0xC0, 0x00,               // ldx #$C000
            // Fixed7Send:
            0xA6, 0x30,                     // ldaa 1,X+
            0x07, 0x0F,                     // bsr SendByte
            0x8E, 0x00, 0x00,               // cpx #$0000 (end $10000 wraps)
            0x26, 0xF7,                     // bne Fixed7Send

            // Poison the watchdog to force a reboot
            0xCC, 0x11, 0x11,               // ldd #$1111
            0x7B, regBlockH, 0x17,          // stab COPRST
            0x7A, regBlockH, 0x17,          // staa COPRST
            0x3D,                           // rts

            // SendByte:
            0xF6, regBlockH, 0xCC,          // ldab SC1SR1
            0x7A, regBlockH, 0xCF,          // staa SC1DRL
            // TxBusy:
            0x07, 0x06,                     // bsr FeedWatchdog
            // Loop until TC (Transmit Complete) bit is set
            0x1F, regBlockH, 0xCC, 0x40, 0xF9,   // brclr SC1SR1,$40,TxBusy
            0x3D,                           // rts

            // FeedWatchdog:
            0xCC, 0x55, 0xAA,               // ldd #$55AA
            0x7A, regBlockH, 0x17,          // staa COPRST
            0x7B, regBlockH, 0x17,          // stab COPRST
            0x3D,                           // rts
        ];
    }

    /// <summary>
    /// Dumps the flat 60 KB flash of the small-EEPROM (68HC912D60A-class) clusters.
    /// $1000-$FFFF is flash; the low 4 KB is RAM, registers and EEPROM.
    /// </summary>
    private string DumpFlatFlash(string filename, byte baudDivisor, int hostBaud)
    {
        const byte entryH = 0x02;      // upload and run at $0200
        const byte regBlockH = 0x08;   // registers at $0800
        const ushort start = 0x1000;   // first flash byte on this part
        const ushort count = 0xF000;   // $1000-$FFFF; end $10000 wraps to $0000

        if (!EnterLoader(entryH, baudDivisor, hostBaud))
        {
            return string.Empty;
        }

        Log.WriteLine("Writing flash dump program to cluster microcontroller");
        Log.WriteLine($"(Entry: ${entryH:X2}00, RegBlock: ${regBlockH:X2}00, Start: ${start:X4}, Count: ${count:X4})");

        var program = BuildFlatDumpProgram(entryH, regBlockH, start, count, baudDivisor);
        if (!WriteMarelliBlockAndReadAck(program))
        {
            return string.Empty;
        }

        Log.WriteLine("Receiving flash dump");
        var mem = ReadDumpBytes(count);

        File.WriteAllBytes(filename, mem);
        Log.WriteLine($"Saved flash dump to {filename}");

        Log.WriteLine("Done");

        _kwp1281.SetDisconnected(); // Don't try to send EndCommunication block

        return filename;
    }

    private void SendUnlockFrame()
    {
        Log.WriteLine("Switching to 10400 baud.");
        _kwp1281.KwpCommon.Interface.SetBaudRate(10400);
        Thread.Sleep(50);

        byte[] secret = [(byte)'G', (byte)'O', (byte)'U', (byte)'R', (byte)'O', (byte)'N'];

        // Any values for p0/p1 will work as long as hi/lo are calculated correctly
        byte p0 = 0;
        byte p1 = 0;
        var p = secret[p0 % 6] * secret[5 - (p1 % 6)];
        byte hi = (byte)(secret[p1 % 6] ^ (p >> 8));
        byte lo = (byte)((secret[5 - (p0 % 6)] + (p & 0xFF)) & 0xFF);

        byte[] unlockFrame =
        [
            0x0A, // Marker byte (the loader discards all bytes until it receives an 0x0A)
            p0,
            p1,
            hi,
            lo,
            0x00, 0x00, 0x00, // Unused
            0xFF, // Written to EEPROM location $3FFC
            0x00, 0x00, // Unused
        ];

        Log.WriteLine("Writing 11-byte unlock frame.");
        foreach (var b in unlockFrame)
        {
            _kwp1281.KwpCommon.WriteByte(b);
        }

        Log.WriteLine("Switching to 9600 baud.");
        _kwp1281.KwpCommon.Interface.SetBaudRate(9600);
        Thread.Sleep(300);
    }

    private bool WriteLoaderHeaderBlock(byte entryH, byte baudDivisor)
    {
        Log.WriteLine("Writing loader header block to cluster microcontroller");
        var data = new byte[]
        {
            0x00, 0x01, // Block count (1)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // Unused
            0x50, // inter-byte ACK delay
            0x50, // initial ACK delay
            baudDivisor, // new baud divisor (SC1BDL)
            entryH, 0x00, // Entry point $xx00
        };
        if (!WriteMarelliBlockAndReadAck(data))
        {
            return false;
        }

        return true;
    }

    private bool IsFirstGenAudiTT()
    {
        return _ecuInfo.StartsWith("8N");
    }

    private bool WriteMarelliBlockAndReadAck(byte[] data)
    {
        var kwpCommon = _kwp1281.KwpCommon;

        var count = (ushort)(data.Length + 2); // Count includes 2-byte checksum
        var countH = (byte)(count / 256);
        var countL = (byte)(count % 256);
        kwpCommon.WriteByte(countH);
        kwpCommon.WriteByte(countL);

        var sum = (ushort)(countH + countL);
        foreach (var b in data)
        {
            kwpCommon.WriteByte(b);
            sum += b;
        }
        kwpCommon.WriteByte((byte)(sum / 256));
        kwpCommon.WriteByte((byte)(sum % 256));

        var expectedAck = new byte[] { 0x03, 0x09, 0x00, 0x0C };

        Log.WriteLine("Receiving ACK");
        var ack = new List<byte>();
        for (int i = 0; i < 4; i++)
        {
            var b = kwpCommon.ReadByte();
            ack.Add(b);
        }
        if (!ack.SequenceEqual(expectedAck))
        {
            Log.WriteLine($"Expected ACK but received {Utils.Dump(ack)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// SCI baud = bus / (16 * divisor), with SC1BDH = 0 and the divisor in SC1BDL.
    /// The 60 KB and 128 KB Marelli parts run an 8 MHz bus.
    /// </summary>
    private static byte BaudToDivisor(int baud, int busClock)
    {
        int divisor = (int)Math.Round((double)busClock / (16 * baud));
        return (byte)Math.Clamp(divisor, 1, 255);
    }

    private static int BaudFromDivisor(byte divisor, int busClock) =>
        (int)Math.Round((double)busClock / (16 * divisor));

    private readonly string[] _smallEepromEcus =
    [
        "1C0919861C",   // KOMBI+WEGFAHRS. M73 V23
        "1C0920800",    // Beetle 1C0920800C M73 V07
        "1C0920806",    // Beetle 1C0920806G M73 V03
        "1C0920901",    // Beetle 1C0920901C M73 V07
        "1C0920905",    // Beetle 1C0920905F M73 V03
        "1C0920906",    // Beetle 1C0920906A M73 V03
        "8N1919860C  KOMBI+WEGFAHRS. M73 D22",  // Audi TT
        "8N1919860C  KOMBI+WEGFAHRS. M73 D23",  // Audi TT
        "8N1919880E  KOMBI+WEGFAHRS. M73 D23",  // Audi TT
        "8N1920930B  KOMBI+WEGFAHRS. M73 D23",  // Audi TT
    ];

    private bool HasSmallEeprom() => _smallEepromEcus.Any(model => _ecuInfo.Contains(model));

    private readonly string[] _largeEepromEcus =
    [
        "1C0920820F",   // KOMBI+WEGFAHRS. M73 V08 (Beetle 2002, Australia)
        "1C0920821",    // KOMBI+WEGFAHRS. M73 V08 (Beetle 2003)
        "1C0920851M",   // KOMBI+WEGFAHRS. M73 V02 (Beetle)
        "1C0920921",    // Beetle 1C0920921G M73 V08
        "1C0920940C",   // KOMBI+WEGFAHRS. M73 V05
        "1C0920941",    // Beetle 1C0920941LX M73 V03
        "1C0920951",    // Beetle 1C0920951A M73 V02
        "8D0920900R",   // KOMBI+WEGFAHRS. M73 D54 (Audi A4 B5 2001)
        "8L0920900B",   // KOMBI+WEGFAHRS. M73 D13 (Audi A3 8L 2002, ASZ diesel engine)
        "8L0920900E",   // KOMBI+WEGFAHRS. M73 D56
        "8N1919860C  KOMBI+WEGFAHRS. M73 D29",  // Audi TT
        "8N1919880E  KOMBI+WEGFAHRS. M73 D26",  // Audi TT
        "8N1919880E  KOMBI+WEGFAHRS. M73 D30",  // Audi TT
        "8N1920860J  KOMBI+WEGFAHRS. M73 D56",  // Audi TT
        "8N1920880D  KOMBI+WEGFAHRS. M73 D10",  // Audi TT
        "8N1920880H  KOMBI+WEGFAHRS. M73 D56",  // Audi TT
        "8N1920930E  KOMBI+WEGFAHRS. M73 D10",  // Audi TT
        "8N1920930J  KOMBI+WEGFAHRS. M73 D56",  // Audi TT
        "8N1920980A  KOMBI+WEGFAHRS. M73 D04",  // Audi TT (needs unlock frame)
        "8N1920980E  KOMBI+WEGFAHRS. M73 D14",  // Audi TT
        "8N2919910A  KOMBI+WEGFAHRS. M73 D29",  // Audi TT
        "8N2920930C  KOMBI+WEGFAHRS. M73 D55",  // Audi TT
        "8N2920930G  KOMBI+WEGFAHRS. M73 D07",  // Audi TT (needs unlock frame)
        "8N2920930G  KOMBI+WEGFAHRS. M73 D08",  // Audi TT (needs unlock frame)
        "8N2920980A  KOMBI+WEGFAHRS. M73 D14",  // Audi TT
    ];

    private bool HasLargeEeprom() => _largeEepromEcus.Any(model => _ecuInfo.Contains(model));

    /// <summary>
    /// Search for the SKC using the 2 methods described here:
    /// https://github.com/gmenounos/kw1281test/issues/50#issuecomment-1770255129
    /// </summary>
    public static ushort? GetSkc(byte[] buf)
    {
        // If the EEPROM contains a 14-digit Immobilizer ID then the SKC should be immediately prior to that
        var immoIdOffset = FindImmobilizerId(buf);
        if (immoIdOffset is >= 2)
        {
            return Utils.GetShortBE(buf, immoIdOffset.Value-2);
        }

        // Otherwise search for 00,01,0F or 00,02,0F or 00,03,0F or 00,04,0F and the SKC should be immediately prior
        var keyCountOffset = FindKeyCount(buf);
        if (keyCountOffset is >= 2)
        {
            return Utils.GetShortBE(buf, keyCountOffset.Value-2);
        }

        return null;
    }

    /// <summary>
    /// Search the buffer for a 14 byte long string of uppercase letters and numbers beginning with VWZ or AUZ
    /// </summary>
    private static int? FindImmobilizerId(IReadOnlyList<byte> buf)
    {
        for (var i = 0; i <= buf.Count - 14; i++)
        {
            if (!(buf[i] == 'V' && buf[i + 1] == 'W') &&
                !(buf[i] == 'A' && buf[i + 1] == 'U'))
            {
                continue;
            }

            if (buf[i + 2] != 'Z')
            {
                continue;
            }

            var isValid = true;
            for (var j = 3; j < 14; j++)
            {
                var b = buf[i + j];
                if (b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z')
                {
                    continue;
                }

                isValid = false;
                break;
            }

            if (isValid)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Search the buffer for the 3 byte sequence
    /// 00,01,0F or 00,02,0F or 00,03,0F or 00,04,0F (preferred)
    /// or 00,00,0F.
    /// (2nd digit is probably the number of keys)
    /// </summary>
    private static int? FindKeyCount(IReadOnlyList<byte> buf)
    {
        for (var i = 0; i <= buf.Count - 3; i++)
        {
            if (buf[i] != 0)
            {
                continue;
            }

            if (buf[i + 1] is < 1 or > 4)
            {
                continue;
            }

            if (buf[i + 2] != 0x0F)
            {
                continue;
            }

            return i;
        }

        for (var i = 0; i <= buf.Count - 3; i++)
        {
            if (buf[i] != 0)
            {
                continue;
            }

            if (buf[i + 1] != 0)
            {
                continue;
            }

            if (buf[i + 2] != 0x0F)
            {
                continue;
            }

            return i;
        }

        return null;
    }

    private readonly IKW1281Dialog _kwp1281;
    private readonly string _ecuInfo;

    public MarelliCluster(IKW1281Dialog kwp1281, string ecuInfo)
    {
        _kwp1281 = kwp1281;
        _ecuInfo = ecuInfo;
    }

    [GeneratedRegex(
        @"\b[A-HJ-NPR-Z0-9]{17}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VINRegex();
}
