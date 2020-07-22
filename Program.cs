using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 4)
            {
                ShowUsage();
                return;
            }

            string portName = args[0];
            var baudRate = int.Parse(args[1]);
            var controllerAddress = int.Parse(args[2], NumberStyles.HexNumber);
            var command = args[3];

            Console.WriteLine($"Opening serial port {portName}");
            using (IInterface @interface = new Interface(portName, baudRate))
            {
                IKW1281Dialog kwp1281 = new KW1281Dialog(@interface);

                Console.WriteLine("Sending wakeup message");
                var ecuInfo = kwp1281.WakeUp((byte)controllerAddress);
                Console.WriteLine($"ECU: {ecuInfo}");

                if (controllerAddress == 0x17)
                {
                    kwp1281.CustomUnlockAdditionalCommands();
                }

                if (string.Compare(command, "ReadIdent", true) == 0)
                {
                    var identInfo = kwp1281.ReadIdent();
                    Console.WriteLine($"Ident: {identInfo}");
                }

                if (string.Compare(command, "ReadSoftwareVersion", true) == 0)
                {
                    kwp1281.CustomReadSoftwareVersion();
                }

                if (string.Compare(command, "MapEeprom", true) == 0)
                {
                    if (controllerAddress == 0x17)
                    {
                        MapClusterEeprom(kwp1281);
                    }
                    else if (controllerAddress == 0x46)
                    {
                        MapCcmEeprom(kwp1281);
                    }
                    else
                    {
                        Console.WriteLine("Not supported");
                    }
                }

                if (string.Compare(command, "ReadEeprom", true) == 0)
                {
                    if (controllerAddress == 0x17)
                    {
                        ReadClusterEeprom(kwp1281);
                    }
                    else if (controllerAddress == 0x46)
                    {
                        ReadCcmEeprom(kwp1281);
                    }
                    else
                    {
                        Console.WriteLine("Not supported");
                    }
                }

                if (string.Compare(command, "ReadRom", true) == 0)
                {
                    kwp1281.CustomReadRom(0x000000, 0x10);
                }

                if (string.Compare(command, "Reset", true) == 0)
                {
                    kwp1281.CustomReset();
                }

                if (string.Compare(command, "CrackDelcoVWPremium5", true) == 0)
                {
                    kwp1281.Login(0x4f43, 0x4544, 0x4c);  // "OCLED"
                    var bytes = kwp1281.ReadRomEeprom(0x0014, 2);
                    Console.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
                }

                kwp1281.EndCommunication();
            }
        }

        private static void MapClusterEeprom(IKW1281Dialog kwp1281)
        {
            // Unlock partial EEPROM read
            var response = kwp1281.SendCustom(new List<byte> { 0x9D, 0x39, 0x34, 0x34, 0x40 });

            var bytes = new List<byte>();
            const byte blockSize = 1;
            for (ushort addr = 0; addr < 2048; addr += blockSize)
            {
                var blockBytes = kwp1281.ReadEeprom(addr, blockSize, map: true);
                bytes.AddRange(blockBytes);
            }
            var dumpFileName = "eeprom_map.bin";
            Console.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private static void MapCcmEeprom(IKW1281Dialog kwp1281)
        {
            kwp1281.Login(19283, 222);

            var bytes = new List<byte>();
            const byte blockSize = 1;
            for (ushort addr = 0; addr < 65535; addr += blockSize)
            {
                var blockBytes = kwp1281.ReadEeprom(addr, blockSize, map: true);
                bytes.AddRange(blockBytes);
            }
            var dumpFileName = "ccm_eeprom_map.bin";
            Console.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private static void ReadCcmEeprom(IKW1281Dialog kwp1281)
        {
            kwp1281.Login(19283, 222);

            const int maxReadLength = 12;

            var bytes = ReadEeprom(kwp1281, 0x0000, 1, maxReadLength);
            SaveEeprom(bytes, "ccm_eeprom_0000.bin");

            bytes = ReadEeprom(kwp1281, 0x1000, 0x200, maxReadLength);
            SaveEeprom(bytes, "ccm_eeprom_1000.bin");

            bytes = ReadEeprom(kwp1281, 0x9000, 0x1000, maxReadLength);
            SaveEeprom(bytes, "ccm_eeprom_9000.bin");
        }

        private static void SaveEeprom(List<byte> bytes, string fileName)
        {
            Console.WriteLine($"Saving EEPROM dump to {fileName}");
            File.WriteAllBytes(fileName, bytes.ToArray());
        }

        private static List<byte> ReadEeprom(
            IKW1281Dialog kwp1281, int startAddr, int length, int maxReadLength)
        {
            var bytes = new List<byte>();

            for (int addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = kwp1281.ReadEeprom((ushort)addr, (byte)readLength);
                bytes.AddRange(blockBytes);
            }

            return bytes;
        }

        private static void ReadClusterEeprom(IKW1281Dialog kwp1281)
        {
            Console.WriteLine("Sending Custom \"Unlock partial EEPROM read\" block");
            var response = kwp1281.SendCustom(new List<byte> { 0x9D, 0x39, 0x34, 0x34, 0x40 });

#if false
            Console.WriteLine("Sending Custom \"Are you unlocked?\" block");
            response = kwp1281.SendCustom(new List<byte> { 0x96, 0x04 });
            // Custom 0x04 means need to do Seed/Key
            // Custom 0x07 means unlocked
#endif

#if false
            Console.WriteLine("Sending Custom \"Give me a seed\" block");
            response = kwp1281.SendCustom(new List<byte> { 0x96, 0x01 });
            foreach (var block in response.Where(b => !b.IsAckNak))
            {
                Console.WriteLine($"Block: {Dump(block.Body)}");
            }

            Console.WriteLine("Sending Custom \"Here is the key\" block");
            response = kwp1281.SendCustom(new List<byte> { 0x96, 0x02, 0x07, 0x57, 0x1F, 0x00, 0xA4, 0x00, 0x44, 0x00 });
#endif

            var blockBytes = kwp1281.ReadEeprom(0x020, 16);

#if false
            var bytes = new List<byte>();
            const byte blockSize = 16;
            for (ushort addr = 0; addr < 2048; addr += blockSize)
            {
                var blockBytes = kwp1281.ReadEeprom(addr, blockSize);
                bytes.AddRange(blockBytes);
            }
            var dumpFileName = identInfo.ToString().Replace(' ', '_') + "_eeprom.bin";
            Console.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
#endif
        }

        private static string Dump(IEnumerable<byte> body)
        {
            var sb = new StringBuilder();
            foreach (var b in body)
            {
                sb.Append($" {b:X2}");
            }
            return sb.ToString();
        }

        private static void ShowUsage()
        {
            Console.WriteLine("Usage: KW1281Test [PORT] [Baud] [Address] [Command]");
            Console.WriteLine("       [Command] = ReadIdent|ReadSoftwareVersion|Reset|ReadEeprom|ReadRom|MapEeprom|CrackDelcoVWPremium5");
        }
    }
}
