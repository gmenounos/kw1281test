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
            Console.WriteLine($"KW1281Test v0.25-beta (https://github.com/gmenounos/kw1281test/releases)");

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
            string filename = null;
            bool evenParityWakeup = false;

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
                     string.Compare(command, "DumpMem", true) == 0 ||
                     string.Compare(command, "DumpRB8Eeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = ParseUint(args[4]);
                length = ParseUint(args[5]);

                if (string.Compare(command, "DumpRB8Eeprom", true) == 0)
                {
                    evenParityWakeup = true;
                }
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
            else if (string.Compare(command, "LoadEeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = ParseUint(args[4]);
                filename = args[5];
            }

            Console.WriteLine($"Opening serial port {portName}");
            using IInterface @interface = new Interface(portName, baudRate);
            IKwpCommon kwpCommon = new KwpCommon(@interface);

            Console.WriteLine("Sending wakeup message");
            var kwpVersion = kwpCommon.WakeUp((byte)controllerAddress, evenParityWakeup);

            IKW1281Dialog kwp1281 = null;
            KW2000Dialog kwp2000 = null;
            if (kwpVersion == 1281)
            {
                kwp1281 = new KW1281Dialog(kwpCommon);

                var ecuInfo = kwp1281.ReadEcuInfo();
                Console.WriteLine($"ECU: {ecuInfo}");

                if (controllerAddress == (int)ControllerAddress.Cluster)
                {
                    kwp1281.CustomUnlockAdditionalCommands();
                }
            }
            else
            {
                kwp2000 = new KW2000Dialog(kwpCommon, (byte)controllerAddress);
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

                kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
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

            if (string.Compare(command, "DumpRB8Eeprom", true) == 0)
            {
                if (controllerAddress == (int)ControllerAddress.Cluster)
                {
                    if (kwpVersion >= 2000)
                    {
                        DumpClusterEepromRB8(kwp2000, address, length);
                    }
                    else
                    {
                        Console.WriteLine($"Cluster protocol is KWP{kwpVersion} but needs to be KWP2xxx");
                    }
                }
                else
                {
                    Console.WriteLine("Only supported for cluster (address 17)");
                }
            }

            if (string.Compare(command, "LoadEeprom", true) == 0)
            {
                if (controllerAddress == (int)ControllerAddress.Cluster)
                {
                    LoadClusterEeprom(kwp1281, (ushort)address, filename);
                }
                else
                {
                    Console.WriteLine("Only supported for cluster");
                }
            }

            if (string.Compare(command, "DumpMem", true) == 0)
            {
                if (controllerAddress == (int)ControllerAddress.Cluster)
                {
                    DumpClusterMem(kwp1281, address, length);
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

            if (kwpVersion == 1281)
            {
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

            var dumpFileName = $"ccm_eeprom_${startAddress:X4}.bin";

            Console.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 12, dumpFileName);
            Console.WriteLine($"Saved EEPROM dump to {dumpFileName}");
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
                // TODO:UnlockCluster() is only needed for EEPROM read, not memory read
                if (!UnlockCluster(kwp1281))
                {
                    Console.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
                }

                if (!ClusterRequiresSeedKey(kwp1281))
                {
                    Console.WriteLine(
                        "Cluster is unlocked for EEPROM access. Skipping Seed/Key login.");
                    return;
                }

                ClusterSeedKeyAuthenticate(kwp1281);
            }
        }

        private static void ClusterSeedKeyAuthenticate(IKW1281Dialog kwp1281)
        {
            // Perform Seed/Key authentication
            Console.WriteLine("Sending Custom \"Seed request\" block");
            var response = kwp1281.SendCustom(new List<byte> { 0x96, 0x01 });

            var responseBlocks = response.Where(b => !b.IsAckNak).ToList();
            if (responseBlocks.Count == 1 && responseBlocks[0] is CustomBlock customBlock)
            {
                Console.WriteLine($"Block: {Dump(customBlock.Body)}");

                var keyBytes = KeyFinder.FindKey(customBlock.Body.ToArray());

                Console.WriteLine("Sending Custom \"Key response\" block");

                var keyResponse = new List<byte> { 0x96, 0x02 };
                keyResponse.AddRange(keyBytes);

                response = kwp1281.SendCustom(keyResponse);
            }
        }

        private static bool ClusterRequiresSeedKey(IKW1281Dialog kwp1281)
        {
            Console.WriteLine("Sending Custom \"Need Seed/Key?\" block");
            var response = kwp1281.SendCustom(new List<byte> { 0x96, 0x04 });
            var responseBlocks = response.Where(b => !b.IsAckNak).ToList();
            if (responseBlocks.Count == 1 && responseBlocks[0] is CustomBlock)
            {
                // Custom 0x04 means need to do Seed/Key
                // Custom 0x07 means unlocked
                if (responseBlocks[0].Body.First() == 0x07)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool UnlockCluster(IKW1281Dialog kwp1281)
        {
            var versionBlocks = kwp1281.CustomReadSoftwareVersion();

            // Now we need to send an unlock code that is unique to each ROM version
            Console.WriteLine("Sending Custom \"Unlock partial EEPROM read\" block");
            var softwareVersion = versionBlocks[0].Body;
            var unlockCodes = GetClusterUnlockCodes(softwareVersion);
            var unlocked = false;
            foreach (var unlockCode in unlockCodes)
            {
                var unlockCommand = new List<byte> { 0x9D };
                unlockCommand.AddRange(unlockCode);
                var unlockResponse = kwp1281.SendCustom(unlockCommand);
                if (unlockResponse.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Received multiple responses from unlock request.");
                }
                if (unlockResponse[0].IsAck)
                {
                    Console.WriteLine(
                        $"Unlock code for software version {KW1281Dialog.DumpMixedContent(softwareVersion)} is {Dump(unlockCode)}");
                    if (unlockCodes.Length > 1)
                    {
                        Console.WriteLine("Please report this to the program maintainer.");
                    }
                    unlocked = true;
                    break;
                }
                else if (!unlockResponse[0].IsNak)
                {
                    throw new InvalidOperationException(
                        $"Received non-ACK/NAK ${unlockResponse[0].Title:X2} from unlock request.");
                }
            }
            return unlocked;
        }

        /// <summary>
        /// Different cluster models have different unlock codes. Return the appropriate one based
        /// on the cluster's software version.
        /// </summary>
        private static byte[][] GetClusterUnlockCodes(List<byte> softwareVersion)
        {
            if (Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK501MH", 0x10, 0x01)))
            {
                return new[] { new byte[] { 0x39, 0x34, 0x34, 0x40 } };
            }
            else if (
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK501MH", 0x00, 0x01)) ||
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK501LL", 0x00, 0x01)))
            {
                return new[] { new byte[] { 0x36, 0x3D, 0x3E, 0x47 } };
            }
            else if (Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK503LL", 0x00, 0x09)))
            {
                return new[] { new byte[] { 0x3E, 0x35, 0x3D, 0x3A } };
            }
            else if (Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VBKX00MH", 0x00, 0x01)))
            {
                return new[] { new byte[] { 0x3A, 0x39, 0x31, 0x43 } };
            }
            else
            {
                return _clusterUnlockCodes;
            }
        }

        private static readonly byte[][] _clusterUnlockCodes = new []
        {
            new byte[] { 0x37, 0x39, 0x3C, 0x47 },
            new byte[] { 0x3A, 0x39, 0x31, 0x43 },
            new byte[] { 0x3B, 0x33, 0x3E, 0x37 },
            new byte[] { 0x3B, 0x46, 0x23, 0x1D },
            new byte[] { 0x31, 0x39, 0x34, 0x46 },
            new byte[] { 0x31, 0x44, 0x35, 0x43 },
            new byte[] { 0x32, 0x37, 0x3E, 0x31 },
            new byte[] { 0x33, 0x34, 0x46, 0x4A },
            new byte[] { 0x34, 0x3F, 0x43, 0x39 },
            new byte[] { 0x35, 0x3B, 0x39, 0x3D },
            new byte[] { 0x35, 0x3C, 0x31, 0x3C },
            new byte[] { 0x35, 0x3D, 0x04, 0x01 },
            new byte[] { 0x35, 0x3D, 0x47, 0x3E },
            new byte[] { 0x35, 0x40, 0x3F, 0x38 },
            new byte[] { 0x35, 0x43, 0x31, 0x38 },
            new byte[] { 0x35, 0x47, 0x34, 0x3C },
            new byte[] { 0x36, 0x3B, 0x36, 0x3D },
            new byte[] { 0x36, 0x3D, 0x3E, 0x47 },
            new byte[] { 0x36, 0x3F, 0x45, 0x42 },
            new byte[] { 0x36, 0x40, 0x36, 0x3D },
            new byte[] { 0x37, 0x39, 0x3C, 0x47 },
            new byte[] { 0x37, 0x3B, 0x32, 0x02 },
            new byte[] { 0x37, 0x3D, 0x43, 0x43 },
            new byte[] { 0x38, 0x34, 0x34, 0x37 },
            new byte[] { 0x38, 0x37, 0x3E, 0x31 },
            new byte[] { 0x38, 0x39, 0x39, 0x40 },
            new byte[] { 0x38, 0x39, 0x3A, 0x47 },
            new byte[] { 0x38, 0x3F, 0x40, 0x35 },
            new byte[] { 0x38, 0x43, 0x38, 0x3F },
            new byte[] { 0x38, 0x47, 0x34, 0x3A },
            new byte[] { 0x39, 0x34, 0x34, 0x40 },
        };

        private static IEnumerable<byte> ClusterVersion(
            string software, byte romMajor, byte romMinor)
        {
            var versionBytes = new List<byte>(Encoding.ASCII.GetBytes(software))
            {
                romMajor,
                romMinor
            };
            return versionBytes;
        }

        private static void DumpEeprom(
            IKW1281Dialog kwp1281, ushort startAddr, ushort length, byte maxReadLength, string fileName)
        {
            bool succeeded = true;

            using (var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough))
            {
                for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
                {
                    var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                    var blockBytes = kwp1281.ReadEeprom((ushort)addr, (byte)readLength);
                    if (blockBytes == null)
                    {
                        blockBytes = Enumerable.Repeat((byte)0, readLength).ToList();
                        succeeded = false;
                    }
                    fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                    fs.Flush();
                }
            }

            if (!succeeded)
            {
                Console.WriteLine();
                Console.WriteLine("**********************************************************************");
                Console.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Console.WriteLine("**********************************************************************");
                Console.WriteLine();
            }
        }

        private static void WriteEeprom(
            IKW1281Dialog kwp1281, ushort startAddr, byte[] bytes, uint maxWriteLength)
        {
            var succeeded = true;
            var length = bytes.Length;
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxWriteLength)
            {
                var writeLength = (byte)Math.Min(startAddr + length - addr, maxWriteLength);
                if (!kwp1281.WriteEeprom(
                    (ushort)addr,
                    bytes.Skip((int)(addr - startAddr)).Take(writeLength).ToList()))
                {
                    succeeded = false;
                }
            }

            if (!succeeded)
            {
                Console.WriteLine("EEPROM write failed. You should probably try again.");
            }
        }

        private static void DumpClusterEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            var identInfo = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            var dumpFileName = identInfo.ToString().Replace(' ', '_') + $"_${startAddress:X4}_eeprom.bin";
			
            Console.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 16, dumpFileName);
            Console.WriteLine($"Saved EEPROM dump to {dumpFileName}");
        }

        private static void DumpClusterEepromRB8(KW2000Dialog kwp2000, uint address, uint length)
        {
            kwp2000.SecurityAccess(0xFB);
            kwp2000.DumpEeprom(address, length);
        }

        private static void LoadClusterEeprom(IKW1281Dialog kwp1281, ushort address, string filename)
        {
            var identInfo = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            if (!File.Exists(filename))
            {
                Console.WriteLine($"File {filename} does not exist.");
                return;
            }

            Console.WriteLine($"Reading {filename}");
            var bytes = File.ReadAllBytes(filename);

            Console.WriteLine("Writing to cluster...");
            WriteEeprom(kwp1281, address, bytes, 16);
        }

        private static void DumpClusterMem(IKW1281Dialog kwp1281, uint startAddress, uint length)
        {
            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            const byte blockSize = 0x10;

            var dumpFileName = $"cluster_mem_${startAddress:X6}.bin";
            Console.WriteLine($"Saving memory dump to {dumpFileName}");
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                for (uint addr = startAddress; addr < startAddress + length; addr += blockSize)
                {
                    var readLength = (byte)Math.Min(startAddress + length - addr, blockSize);
                    var blockBytes = kwp1281.CustomReadMemory(addr, readLength);
                    if (blockBytes.Count != readLength)
                    {
                        throw new InvalidOperationException(
                            $"Expected {readLength} bytes from CustomReadMemory() but received {blockBytes.Count} bytes");
                    }
                    fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                    fs.Flush();
                }
            }
            Console.WriteLine($"Saved memory dump to {dumpFileName}");
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
            Console.WriteLine("                 LoadEeprom START FILENAME");
            Console.WriteLine("                            START  = Start address in decimal (e.g. 0) or hex (e.g. $0)");
            Console.WriteLine("                            FILENAME = Name of file containing binary data to load into EEPROM");
            Console.WriteLine("                 DumpMem START LENGTH");
            Console.WriteLine("                         START  = Start address in decimal (e.g. 8192) or hex (e.g. $2000)");
            Console.WriteLine("                         LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. $10000)");
            Console.WriteLine("                 MapEeprom");
            Console.WriteLine("                 Reset");
            Console.WriteLine("                 DelcoVWPremium5SafeCode");
            Console.WriteLine("                 DumpRB8Eeprom START LENGTH");
            Console.WriteLine("                               START  = Start address in decimal (e.g. 0) or hex (e.g. $0)");
            Console.WriteLine("                               LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. $800)");
        }
    }
}
