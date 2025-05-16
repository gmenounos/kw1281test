using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.Cluster
{
    class MarelliCluster : ICluster
    {
        public void UnlockForEepromReadWrite()
        {
            // Nothing to do
        }

        public string DumpEeprom(uint? address, uint? length, string? dumpFileName, string prefix = default)
        {
            address ??= GetDefaultAddress(prefix: prefix);
            dumpFileName ??= $"marelli_mem_${address:X4}.bin";

            _ = DumpMem(dumpFileName, (ushort)address, (ushort?)length);

            return dumpFileName;
        }

        private ushort GetDefaultAddress(string prefix = default)
        {
            if (HasSmallEeprom())
            {
                return 3072; // $0C00
            }
            else if (HasLargeEeprom())
            {
                return 14336; // $3800
            }
            else
            {
                Log.WriteLine();
                Log.WriteLine("Unsupported Marelli cluster version.");
                Log.WriteLine("You can try the following commands to see if either produces a dump file.");
                Log.WriteLine("Then please contact the program author with the results.");
                Log.WriteLine();

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
            ushort? count = null)
        {
            byte entryH; // High byte of code entry point
            byte regBlockH; // High byte of register block

            if (_ecuInfo.Contains("M73 D0"))    // Audi TT
            {
                entryH = 0x00; // $0000
                regBlockH = (byte)((address == 0x3800) ? 0x20 : 0x08);
                count ??= (ushort)((address == 0x3800) ? 0x800 : 0x400);
            }
            else if (HasSmallEeprom())
            {
                entryH = 0x02; // $0200
                regBlockH = 0x08; // $0800
                count ??= 1024; // $0400
            }
            else if (HasLargeEeprom())
            {
                entryH = 0x18; // $1800
                regBlockH = 0x20; // $2000
                count ??= 2048; // $0800
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

            Log.WriteLine("Sending block 0x6C");
            _kwp1281.SendBlock([0x6C]);

            Thread.Sleep(250);

            Log.WriteLine("Writing data to cluster microcontroller");
            var data = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x50, 0x34,
                entryH, 0x00, // Entry point $xx00
            };
            if (!WriteMarelliBlockAndReadAck(data))
            {
                return [];
            }

            // Now we write a small memory dump program to the 68HC12 processor

            Log.WriteLine("Writing memory dump program to cluster microcontroller");
            Log.WriteLine($"(Entry: ${entryH:X2}00, RegBlock: ${regBlockH:X2}00, Start: ${address:X4}, Count: ${count:X4})");

            var startH = (byte)(address / 256);
            var startL = (byte)(address % 256);

            var end = address + count;
            var endH = (byte)(end / 256);
            var endL = (byte)(end % 256);

            var program = new byte[]
            {
                entryH, 0x00, // Address $xx00

                0x14, 0x50,                     // orcc #$50
                0x07, 0x32,                     // bsr FeedWatchdog

                // Set baud rate to 9600
                0xC7,                           // clrb
                0x7B, regBlockH, 0xC8,          // stab SC1BDH
                0xC6, 0x34,                     // ldab #$34
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
                0x7B, regBlockH, 0x17,          // stab COPRST
                0x7A, regBlockH, 0x17,          // staa COPRST
                0x3D,                           // rts
            };
            if (!WriteMarelliBlockAndReadAck(program))
            {
                return Array.Empty<byte>();
            }

            Log.WriteLine("Receiving memory dump");

            var kwpCommon = _kwp1281.KwpCommon;
            var mem = new List<byte>();
            for (int i = 0; i < count; i++)
            {
                var b = kwpCommon.ReadByte();
                mem.Add(b);
            }

            File.WriteAllBytes(filename, mem.ToArray());
            Log.WriteLine($"Saved memory dump to {filename}");

            Log.WriteLine("Done");

            _kwp1281.SetDisconnected(); // Don't try to send EndCommunication block

            return mem.ToArray();
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

        private readonly string[] _smallEepromEcus =
        [
            "1C0920800",    // Beetle 1C0920800C M73 V07
            "1C0920806",    // Beetle 1C0920806G M73 V03
            "1C0920901",    // Beetle 1C0920901C M73 V07
            "1C0920905",    // Beetle 1C0920905F M73 V03
            "1C0920906",    // Beetle 1C0920906A M73 V03
            "8N1919880E KOMBI+WEGFAHRS. M73 D23",   // Audi TT
            "8N1920930",    // Audi TT 8N1920930B M73 D23
        ];

        private bool HasSmallEeprom() => _smallEepromEcus.Any(model => _ecuInfo.Contains(model));

        private readonly string[] _largeEepromEcus =
        [
            "1C0920921",    // Beetle 1C0920921G M73 V08
            "1C0920941",    // Beetle 1C0920941LX M73 V03
            "1C0920951",    // Beetle 1C0920951A M73 V02
            "8D0920900R",   // KOMBI+WEGFAHRS. M73 D54 (Audi A4 B5 2001)
            "8L0920900B",   // KOMBI+WEGFAHRS. M73 D13, Audi A3 8L 2002 (ASZ diesel engine)
            "8L0920900E",   // KOMBI+WEGFAHRS. M73 D56
            "8N1919880E KOMBI+WEGFAHRS. M73 D26",   // Audi TT
            "8N1920980",    // Audi TT 8N1920980E M73 D14
            "8N2919910A",   // KOMBI+WEGFAHRS. M73 D29, Audi TT
            "8N2920930",    // Audi TT 8N2920930C M73 D55
            "8N2920980",    // Audi TT 8N2920980A M73 D14
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
            for (var i = 0; i < buf.Count - 14; i++)
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
        /// Search the buffer for the 3 byte sequence 00,01,0F or 00,02,0F or 00,03,0F or 00,04,0F
        /// (2nd digit is probably the number of keys)
        /// </summary>
        private static int? FindKeyCount(IReadOnlyList<byte> buf)
        {
            for (var i = 0; i < buf.Count - 3; i++)
            {
                if (buf[i] != 0)
                {
                    continue;
                }

                if (buf[i + 1] != 1 && buf[i + 1] != 2 && buf[i + 1] != 3 && buf[i + 1] != 4)
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
    }
}
