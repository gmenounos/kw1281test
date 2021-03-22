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
            Logger.WriteLine("Sending wakeup message");
            var kwpVersion = _kwpCommon.FastInit((byte)_controllerAddress);

            var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

            Kwp2000Message resp;

            // Need to decrease these timing parameters to get the ECU to wake up.
            kwp2000.P3 = 0;
            kwp2000.P4 = 0;
            kwp2000.SendMessage(DiagnosticService.startCommunication, Array.Empty<byte>());
            kwp2000.P3 = 6;
            try
            {
                resp = kwp2000.SendReceive(DiagnosticService.testerPresent, Array.Empty<byte>());
            }
            catch (InvalidOperationException)
            {
                // Ignore "Unexpected DestAddress: 00"
            }

            kwp2000.P3 = 55;

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

            var loader = Edc15VM.Loader;
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

            var dumpFileName = filename ?? $"EDC15_EEPROM.bin";
            File.WriteAllBytes(dumpFileName, eeprom.ToArray());
            Logger.WriteLine($"Saved EEPROM to {dumpFileName}");

            resp = kwp2000.ReceiveMessage();

            // Custom loader command to reboot the ECU to return it to normal operation.
            kwp2000.SendMessage(
                    (DiagnosticService)0xA2, Array.Empty<byte>(),
                excludeAddresses: true);
            resp = kwp2000.ReceiveMessage();

            b = _kwpCommon.Interface.ReadByte();
            if (b == 0x55)
            {
                Logger.WriteLine($"Reboot successful!");
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
        private static byte[] Loader
        {
            get
            {
                var assembly = Assembly.GetEntryAssembly();
                var resourceStream = assembly.GetManifestResourceStream(
                    "BitFab.KW1281Test.EDC15.Loader.bin");
                var buf = new byte[resourceStream.Length];
                resourceStream.Read(buf, 0, (int)resourceStream.Length);
                return buf;
            }
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
