using BitFab.KW1281Test.Kwp2000;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BitFab.KW1281Test.EDC15
{
    public class Edc15VM
    {
        public void DumpEeprom(string filename)
        {
            var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

            var resp = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x89 });

            resp = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x85 });

            const byte accMod = 0x41;
            resp = kwp2000.SendReceive(DiagnosticService.securityAccess, new byte[] { accMod });

            // ECU normally doesn't require seed/key authentication the first time it wakes up in
            // KWP2000 mode so sending an empty key is sufficient.
            var buf = new List<byte> { accMod + 1 };

            if (!Enumerable.SequenceEqual(resp.Body, new byte[] { accMod, 0x00, 0x00 }))
            {
                // Normally we'll only get here if we wake up the ECU and it's already in KWP2000 mode,
                // which can happen if a previous download attempt did not complete. In that case we
                // need to calculate and send back a real key.
                var seedBuf = resp.Body.Skip(1).Take(4).ToArray();
                var keyBuf = LVL41Auth(0x508DA647, 0x3800000, seedBuf);

                buf.AddRange(keyBuf);
            }
            resp = kwp2000.SendReceive(DiagnosticService.securityAccess, buf.ToArray());

            var loader = Edc15VM.GetLoader();
            var len = loader.Length;

            // Ask the ECU to accept our loader and store it in RAM
            resp = kwp2000.SendReceive(DiagnosticService.requestDownload, new byte[]
            {
                0x40, 0xE0, 0x00, // Load address 0x40E000
                0x00, // Not compressed, not encrypted
                (byte)(len >> 16), (byte)(len >> 8), (byte)(len & 0xFF) // Length
            },
            excludeAddresses: true);

            // Break the loader into blocks and send each one
            var maxBlockLen = resp.Body[0];
            var s = new MemoryStream(loader);
            while (true)
            {
                Thread.Sleep(5);

                var blockBytes = new byte[maxBlockLen];
                var readCount = s.Read(blockBytes, 0, maxBlockLen - 1);
                if (readCount == 0)
                {
                    break;
                }

                resp = kwp2000.SendReceive(
                    DiagnosticService.transferData, blockBytes.Take(readCount).ToArray(),
                    excludeAddresses: true);
            }

            // Ask the ECU to execute our loader
            kwp2000.SendMessage(
                DiagnosticService.startRoutineByLocalIdentifier, new byte[] { 0x02 },
                excludeAddresses: true);
            resp = kwp2000.ReceiveMessage();

            // Custom loader command to send all 512 bytes of the EEPROM
            kwp2000.SendMessage(
                (DiagnosticService)0xA6, Array.Empty<byte>(),
                excludeAddresses: true);
            resp = kwp2000.ReceiveMessage();
            if (!resp.IsPositiveResponse(DiagnosticService.transferData))
            {
                throw new InvalidOperationException($"Dump EEPROM failed.");
            }

            var eeprom = new List<byte>();
            byte b;
            for (int i = 0; i < 512; i++)
            {
                b = _kwpCommon.Interface.ReadByte();
                eeprom.Add(b);
            }

            File.WriteAllBytes(filename, eeprom.ToArray());
            Log.WriteLine($"Saved EEPROM to {filename}");

            resp = kwp2000.ReceiveMessage();

            // Custom loader command to reboot the ECU to return it to normal operation.
            kwp2000.SendMessage(
                    (DiagnosticService)0xA2, Array.Empty<byte>(),
                excludeAddresses: true);
            resp = kwp2000.ReceiveMessage();

            b = _kwpCommon.Interface.ReadByte();
            if (b == 0x55)
            {
                Log.WriteLine($"Reboot successful!");
            }
        }

        /// <summary>
        /// This algorithm borrowed from https://github.com/fjvva/ecu-tool
        /// Thanks to Javier Vazquez Vidal https://github.com/fjvva
        /// </summary>
        private static byte[] LVL41Auth(long key, long key3, byte[] buf)
        {
            long key1;
            long key2;
            //long Key3 = 0x3800000;
            long tempstring;
            tempstring = buf[0];
            tempstring <<= 8;
            long keyread1 = tempstring + buf[1];
            tempstring = buf[2];
            tempstring <<= 8;
            long keyread2 = tempstring + buf[3];
            //Process the algorithm 
            key2 = key;
            key2 &= 0xFFFF;
            key >>= 16;
            key1 = key;
            for (byte counter = 0; counter < 5; counter++)
            {
                long temp1;
                long keyTemp = keyread1;
                keyTemp &= 0x8000;
                keyread1 <<= 1;
                temp1 = keyTemp & 0x0FFFF;
                if (temp1 == 0)
                {
                    long temp2 = keyread2 & 0xFFFF;
                    long temp3 = keyTemp & 0xFFFF0000;
                    keyTemp = temp2 + temp3;
                    keyread1 &= 0xFFFE;
                    temp2 = keyTemp & 0xFFFF;
                    temp2 >>= 0x0F;
                    keyTemp &= 0xFFFF0000;
                    keyTemp += temp2;
                    keyread1 |= keyTemp;
                    keyread2 <<= 0x01;
                }
                else
                {
                    long temp2;
                    long temp3;
                    keyTemp = keyread2 + keyread2;
                    keyread1 &= 0xFFFE;
                    temp2 = keyTemp & 0xFF;
                    temp2 |= 1;
                    temp3 = key3 & 0xFFFFFF00;
                    key3 = temp2 + temp3;
                    key3 &= 0xFFFF00FF;
                    key3 |= keyTemp;
                    temp2 = keyread2 & 0xFFFF;
                    temp3 = keyTemp & 0xFFFF0000;
                    keyTemp = temp2 + temp3;
                    temp2 = keyTemp & 0xFFFF;
                    temp2 >>= 0x0F;
                    keyTemp &= 0xFFFF0000;
                    keyTemp += temp2;
                    keyTemp |= keyread1;
                    key3 ^= key1;
                    keyTemp ^= key2;
                    keyread2 = key3;
                    keyread1 = keyTemp;
                }
            }
            //Done with the key generation 
            keyread2 &= 0xFFFF; // Clean first and second word from garbage
            keyread1 &= 0xFFFF;

            var keybuf = new byte[4];
            keybuf[1] = (byte)keyread1;
            keyread1 >>= 8;
            keybuf[0] = (byte)keyread1;
            keybuf[3] = (byte)keyread2;
            keyread2 >>= 8;
            keybuf[2] = (byte)keyread2;

            return keybuf;
        }

        /// <summary>
        /// Loader that can read/write the serial EEPROM.
        /// </summary>
        private static byte[] GetLoader()
        {
            var assembly = Assembly.GetEntryAssembly()!;
            var resourceStream = assembly.GetManifestResourceStream(
                "BitFab.KW1281Test.EDC15.Loader.bin");
            if (resourceStream == null)
            {
                throw new InvalidOperationException(
                    $"Unable to load BitFab.KW1281Test.EDC15.Loader.bin embedded resource.");
            }

            var loaderLength = resourceStream.Length + 4; // Add 4 bytes for checksum correction
            loaderLength = (loaderLength + 7) / 8 * 8; // Round up to a multiple of 8 bytes
            var buf = new byte[loaderLength];

            resourceStream.Read(buf, 0, (int)resourceStream.Length);

            // In order for this loader to be executed by the ECU, the checksum of all the bytes
            // must be EFCD8631.

            // Patch the loader with the location of the end (actually 1 byte past the end)
            ushort loaderEnd = (ushort)(0xE000 + loaderLength);
            buf[0x0E] = (byte)(loaderEnd & 0xFF);
            buf[0x0F] = (byte)(loaderEnd >> 8);

            // Take the checksum of the loader up to but not including the checksum correction
            ushort r6 = 0xEFCD;
            ushort r1 = 0x8631;
            Checksum(ref r6, ref r1, buf.Take(buf.Length - 4).ToArray());

            // Calculate the checksum correction bytes and insert them at the end of the loader
            var padding = CalcPadding(r6, r1);
            Array.Copy(padding, 0, buf, buf.Length - 4, 4);

            return buf;
        }

        /// <summary>
        /// Calculate the checksum correction padding needed to result in a checksum of EFCD8631
        /// </summary>
        /// <param name="r6"></param>
        /// <param name="r1"></param>
        /// <returns></returns>
        private static byte[] CalcPadding(ushort r6, ushort r1)
        {
            var paddingH = (ushort)(0xDF9B ^ r6);
            var paddingL = (ushort)(r1 - 0xAB85);

            return new[]
            {
                (byte)(paddingL & 0xFF),
                (byte)(paddingL >> 8),
                (byte)(paddingH & 0xFF),
                (byte)(paddingH >> 8),
            };
        }

        /// <summary>
        /// EDC15 checksum algorithm (sub_1584).
        /// Calculates a 32-bit checksum of an array of bytes based on an initial 32-bit seed.
        /// Based on https://www.ecuconnections.com/forum/viewtopic.php?f=211&t=49704&sid=5cf324c44d2c74d372984f428ffea5ed
        /// </summary>
        /// <param name="r6">Input: High word of seed, Output: High word of checksum</param>
        /// <param name="r1">Input: Low word of seed, Output: Low word of checksum</param>
        /// <param name="buf">Buffer to calculate checksum for</param>
        static void Checksum(ref ushort r6, ref ushort r1, byte[] buf)
        {
            int r3 = 0; // Buffer index
            int r0 = buf.Length;
            while (true)
            {
                r1 ^= GetBuf(buf, r3); r3 += 2;
                r1 = Rol(r1, r6, out ushort c);
                r6 = (ushort)(r6 - GetBuf(buf, r3) - c); r3 += 2;
                r6 ^= r1;
                if (r3 >= r0)
                {
                    break;
                }

                r1 = (ushort)(r1 - GetBuf(buf, r3) - 1); r3 += 2;
                r1 += 0xDAAD;
                r6 ^= GetBuf(buf, r3); r3 += 2;
                r6 = Ror(r6, r1);
                if (r3 >= r0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Rotates a 16-bit value right by count bits.
        /// </summary>
        private static ushort Ror(ushort value, ushort count)
        {
            count &= 0xF;
            value = (ushort)((value >> count) | (value << (16 - count)));
            return value;
        }

        /// <summary>
        /// Rotates a 16-bit value left by count bits. Carry will be equal to the last bit rotated
        /// or 0 if the low 4 bits of count are 0;
        /// </summary>
        private static ushort Rol(ushort value, ushort count, out ushort carry)
        {
            count &= 0xF;
            value = (ushort)((value << count) | (value >> (16 - count)));
            carry = ((value & 1) == 0 || (count == 0)) ? (ushort)0 : (ushort)1;
            return value;
        }

        private static ushort GetBuf(byte[] buf, int ix)
        {
            return (ushort)(buf[ix] + (buf[ix + 1] << 8));
        }

        private readonly IKwpCommon _kwpCommon;
        private readonly int _controllerAddress;

        public Edc15VM(IKwpCommon kwpCommon, int controllerAddress)
        {
            _kwpCommon = kwpCommon;
            _controllerAddress = controllerAddress;
        }
    }
}
