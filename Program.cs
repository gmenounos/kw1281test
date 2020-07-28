using BitFab.KW1281Test.Blocks;
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
            uint address = 0;
            uint length = 0;
            byte value = 0;

            if (string.Compare(command, "ReadEeprom", true) == 0)
            {
                if (args.Length < 5)
                {
                    ShowUsage();
                    return;
                }

                address = ParseUint(args[4]);
            }
            else if (string.Compare(command, "DumpEeprom", true) == 0 ||
                     string.Compare(command, "DumpRom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = ParseUint(args[4]);
                length = ParseUint(args[5]);
            }
            else if (string.Compare(command, "WriteEeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = ParseUint(args[4]);
                value = (byte)ParseUint(args[5]);
            }

            Console.WriteLine($"Opening serial port {portName}");
            using (IInterface @interface = new Interface(portName, baudRate))
            {
                IKW1281Dialog kwp1281 = new KW1281Dialog(@interface);

                Console.WriteLine("Sending wakeup message");
                var ecuInfo = kwp1281.WakeUp((byte)controllerAddress);
                Console.WriteLine($"ECU: {ecuInfo}");

                if (controllerAddress == (int)ControllerAddress.Cluster)
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

                if (string.Compare(command, "ReadEeprom", true) == 0)
                {
                    UnlockControllerForEepromReadWrite(kwp1281, (ControllerAddress)controllerAddress);

                    var blockBytes = kwp1281.ReadEeprom((ushort)address, 1);
                    if (blockBytes == null)
                    {
                        Console.WriteLine("EEPROM read failed");
                    }
                    else
                    {
                        value = blockBytes[0];
                        Console.WriteLine(
                            $"Address {address} (${address:X4}): Value {value} (${value:X2})");
                    }
                }

                if (string.Compare(command, "WriteEeprom", true) == 0)
                {
                    UnlockControllerForEepromReadWrite(kwp1281, (ControllerAddress)controllerAddress);

                    kwp1281.WriteEeprom((ushort)address, value);
                }

                if (string.Compare(command, "DumpEeprom", true) == 0)
                {
                    if (controllerAddress == (int)ControllerAddress.Cluster)
                    {
                        DumpClusterEeprom(kwp1281, (ushort)address, (ushort)length);
                    }
                    else if (controllerAddress == (int)ControllerAddress.CCM)
                    {
                        DumpCcmEeprom(kwp1281, (ushort)address, (ushort)length);
                    }
                    else
                    {
                        Console.WriteLine("Only supported for cluster and CCM");
                    }
                }

                if (string.Compare(command, "DumpRom", true) == 0)
                {
                    if (controllerAddress == (int)ControllerAddress.Cluster)
                    {
                        DumpClusterRom(kwp1281, address, length);
                    }
                    else
                    {
                        Console.WriteLine("Only supported for cluster");
                    }
                }

                if (string.Compare(command, "MapEeprom", true) == 0)
                {
                    if (controllerAddress == (int)ControllerAddress.Cluster)
                    {
                        MapClusterEeprom(kwp1281);
                    }
                    else if (controllerAddress == (int)ControllerAddress.CCM)
                    {
                        MapCcmEeprom(kwp1281);
                    }
                    else
                    {
                        Console.WriteLine("Only supported for cluster and CCM");
                    }
                }

                if (string.Compare(command, "Reset", true) == 0)
                {
                    if (controllerAddress == (int)ControllerAddress.Cluster)
                    {
                        kwp1281.CustomReset();
                    }
                    else
                    {
                        Console.WriteLine("Only supported for cluster");
                    }
                }

                if (string.Compare(command, "DelcoVWPremium5SafeCode", true) == 0)
                {
                    if (controllerAddress == (int)ControllerAddress.RadioManufacturing)
                    {
                        // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
                        const string secret = "DELCO";
                        var code = (ushort)(secret[4] * 256 + secret[3]);
                        var workshopCode = (ushort)(secret[1] * 256 + secret[0]);
                        var unknown = (byte)secret[2];

                        kwp1281.Login(code, workshopCode, unknown);
                        var bytes = kwp1281.ReadRomEeprom(0x0014, 2);
                        Console.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
                    }
                    else
                    {
                        Console.WriteLine("Only supported for radio manufacturing address 7C");
                    }
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
                var blockBytes = kwp1281.ReadEeprom(addr, blockSize);
                blockBytes = Enumerable.Repeat(
                    blockBytes == null ? (byte)0 : (byte)0xFF,
                    blockSize).ToList();
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
                var blockBytes = kwp1281.ReadEeprom(addr, blockSize);
                blockBytes = Enumerable.Repeat(
                    blockBytes == null ? (byte)0 : (byte)0xFF,
                    blockSize).ToList();
                bytes.AddRange(blockBytes);
            }
            var dumpFileName = "ccm_eeprom_map.bin";
            Console.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private static void DumpCcmEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.CCM);

            const byte maxReadLength = 12;

            var bytes = ReadEeprom(kwp1281, startAddress, length, maxReadLength);
            SaveEeprom(bytes, $"ccm_eeprom_${startAddress:X4}.bin");
        }

        private static void UnlockControllerForEepromReadWrite(
            IKW1281Dialog kwp1281, ControllerAddress controllerAddress)
        {
            if (controllerAddress == ControllerAddress.CCM)
            {
                kwp1281.Login(code: 19283, workshopCode: 222); // This is what VDS-PRO uses
            }
            else if (controllerAddress == ControllerAddress.Cluster)
            {
                Console.WriteLine("Sending Custom \"Unlock partial EEPROM read\" block");
                var response = kwp1281.SendCustom(new List<byte> { 0x9D, 0x39, 0x34, 0x34, 0x40 });

                Console.WriteLine("Sending Custom \"Are you unlocked?\" block");
                response = kwp1281.SendCustom(new List<byte> { 0x96, 0x04 });
                var responseBlocks = response.Where(b => !b.IsAckNak).ToList();
                if (responseBlocks.Count == 1 && responseBlocks[0] is CustomBlock)
                {
                    // Custom 0x04 means need to do Seed/Key
                    // Custom 0x07 means unlocked
                    if (responseBlocks[0].Body.ToArray()[0] == 0x07)
                    {
                        return;
                    }
                }

                // Perform Seed/Key authentication
#if false
                Console.WriteLine("Sending Custom \"Seed request\" block");
                response = kwp1281.SendCustom(new List<byte> { 0x96, 0x01 });
                foreach (var block in response.Where(b => !b.IsAckNak))
                {
                    Console.WriteLine($"Block: {Dump(block.Body)}");
                }

                Console.WriteLine("Sending Custom \"Key response\" block");
                response = kwp1281.SendCustom(new List<byte> { 0x96, 0x02, 0x07, 0x57, 0x1F, 0x00, 0xA4, 0x00, 0x44, 0x00 });
#endif
            }
        }

        private static void SaveEeprom(List<byte> bytes, string fileName)
        {
            Console.WriteLine($"Saving EEPROM dump to {fileName}");
            File.WriteAllBytes(fileName, bytes.ToArray());
        }

        private static List<byte> ReadEeprom(
            IKW1281Dialog kwp1281, ushort startAddr, ushort length, byte maxReadLength)
        {
            var bytes = new List<byte>();
            bool succeeded = true;

            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = kwp1281.ReadEeprom((ushort)addr, (byte)readLength);
                if (blockBytes == null)
                {
                    blockBytes = Enumerable.Repeat((byte)0, readLength).ToList();
                    succeeded = false;
                }
                bytes.AddRange(blockBytes);
            }

            if (!succeeded)
            {
                Console.WriteLine();
                Console.WriteLine("**********************************************************************");
                Console.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Console.WriteLine("**********************************************************************");
                Console.WriteLine();
            }

            return bytes;
        }

        private static void DumpClusterEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            var identInfo = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            var bytes = ReadEeprom(kwp1281, startAddress, length, 16);
            var dumpFileName = identInfo.ToString().Replace(' ', '_') + $"_${startAddress:X4}_eeprom.bin";
            Console.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private static void DumpClusterRom(IKW1281Dialog kwp1281, uint startAddress, uint length)
        {
            const byte blockSize = 0x10;

            var dumpFileName = $"cluster_rom_${startAddress:X6}.bin";
            Console.WriteLine($"Saving ROM dump to {dumpFileName}");
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                for (uint addr = startAddress; addr < startAddress + length; addr += blockSize)
                {
                    var readLength = (byte)Math.Min(startAddress + length - addr, blockSize);
                    var blockBytes = kwp1281.CustomReadRom(addr, readLength);
                    if (blockBytes.Count != readLength)
                    {
                        throw new InvalidOperationException(
                            $"Expected 0x{readLength:X2} bytes from CustomReadRom() but received 0x{blockBytes.Count:X2} bytes");
                    }
                    fs.Write(blockBytes.ToArray());
                    fs.Flush();
                }
            }
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

        private static uint ParseUint(string numberString)
        {
            uint number;

            if (numberString.StartsWith("$"))
            {
                number = uint.Parse(numberString.Substring(1), NumberStyles.HexNumber);
            }
            else
            {
                number = uint.Parse(numberString);
            }

            return number;
        }

        private static void ShowUsage()
        {
            Console.WriteLine("Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]");
            Console.WriteLine("       PORT    = COM1|COM2|etc.");
            Console.WriteLine("       BAUD    = 10400|9600|etc.");
            Console.WriteLine("       ADDRESS = The controller address, e.g. 17 (cluster), 46 (CCM), 56 (radio)");
            Console.WriteLine("       COMMAND = ReadIdent");
            Console.WriteLine("                 ReadSoftwareVersion");
            Console.WriteLine("                 ReadEeprom ADDRESS");
            Console.WriteLine("                            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)");
            Console.WriteLine("                 WriteEeprom ADDRESS VALUE");
            Console.WriteLine("                             ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)");
            Console.WriteLine("                             VALUE   = Value in decimal (e.g. 138) or hex (e.g. $8A)");
            Console.WriteLine("                 DumpEeprom START LENGTH");
            Console.WriteLine("                            START  = Start address in decimal (e.g. 0) or hex (e.g. $0)");
            Console.WriteLine("                            LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. $800)");
            Console.WriteLine("                 DumpRom START LENGTH");
            Console.WriteLine("                         START  = Start address in decimal (e.g. 8192) or hex (e.g. $2000)");
            Console.WriteLine("                         LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. $10000)");
            Console.WriteLine("                 MapEeprom");
            Console.WriteLine("                 Reset");
            Console.WriteLine("                 DelcoVWPremium5SafeCode");
        }
    }
}
