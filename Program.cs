using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace BitFab.KW1281Test
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                Logger.Open("KW1281Test.log");

                var tester = new Program();
                tester.Run(args);
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Unhandled exception: {ex}");
            }
            finally
            {
                Logger.Close();
            }
        }

        void Run(string[] args)
        {
            var version = GetType().GetTypeInfo().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
            Logger.WriteLine($"KW1281Test {version} (https://github.com/gmenounos/kw1281test/releases)");

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

            Logger.WriteLine($"Opening serial port {portName}");
            using IInterface @interface = new Interface(portName, baudRate);
            IKwpCommon kwpCommon = new KwpCommon(@interface);

            Logger.WriteLine("Sending wakeup message");
            var kwpVersion = kwpCommon.WakeUp((byte)controllerAddress, evenParityWakeup);

            IKW1281Dialog kwp1281 = null;
            KW2000Dialog kwp2000 = null;
            if (kwpVersion == 1281)
            {
                kwp1281 = new KW1281Dialog(kwpCommon);

                var ecuInfo = kwp1281.ReadEcuInfo();
                Logger.WriteLine($"ECU: {ecuInfo}");

                if (controllerAddress == (int)ControllerAddress.Cluster)
                {
                    kwp1281.CustomUnlockAdditionalCommands();
                }
            }
            else
            {
                kwp2000 = new KW2000Dialog(kwpCommon, (byte)controllerAddress);
            }

            switch (command.ToLower())
            {
                case "actuatortest":
                    ActuatorTest(kwp1281, controllerAddress);
                    break;

                case "delcovwpremium5safecode":
                    DelcoVWPremium5SafeCode(kwp1281, controllerAddress);
                    break;

                case "dumpccmrom":
                    DumpCcmRom(kwp1281, controllerAddress);
                    break;

                case "dumpclusternecrom":
                    DumpClusterNecRom(kwp1281, controllerAddress);
                    break;

                case "dumpeeprom":
                    DumpEeprom(kwp1281, controllerAddress, address, length);
                    break;

                case "dumpmem":
                    DumpMem(kwp1281, controllerAddress, address, length);
                    break;

                case "dumprb8eeprom":
                    DumpRB8Eeprom(kwp2000, controllerAddress, address, length, kwpVersion);
                    break;

                case "loadeeprom":
                    LoadEeprom(kwp1281, controllerAddress, address, filename);
                    break;

                case "mapeeprom":
                    MapEeprom(kwp1281, controllerAddress);
                    break;

                case "readeeprom":
                    value = ReadEeprom(kwp1281, controllerAddress, address, value);
                    break;

                case "readfaultcodes":
                    ReadFaultCodes(kwp1281, controllerAddress);
                    break;

                case "readident":
                    ReadIdent(kwp1281);
                    break;

                case "readsoftwareversion":
                    ReadSoftwareVersion(kwp1281);
                    break;

                case "reset":
                    Reset(kwp1281, controllerAddress);
                    break;

                case "writeeeprom":
                    WriteEeprom(kwp1281, controllerAddress, address, value);
                    break;

                default:
                    ShowUsage();
                    break;
            }

            if (kwpVersion == 1281)
            {
                kwp1281.EndCommunication();
            }
        }

        // Begin top-level commands

        private static void ActuatorTest(IKW1281Dialog kwp1281, int controllerAddress)
        {
            using KW1281KeepAlive keepAlive = new(kwp1281);

            ConsoleKeyInfo keyInfo;
            do
            {
                var response = keepAlive.ActuatorTest(0x00);
                if (response == null)
                {
                    Logger.WriteLine("End of test.");
                    break;
                }
                Logger.WriteLine($"Actuator Test: {response.ActuatorName}");

                Console.Write("Press a key or 'Q' to quit");
                keyInfo = Console.ReadKey(intercept: true);
                Console.WriteLine();
            } while (keyInfo.Key != ConsoleKey.Q);
        }

        private static void DelcoVWPremium5SafeCode(IKW1281Dialog kwp1281, int controllerAddress)
        {
            if (controllerAddress != (int)ControllerAddress.RadioManufacturing)
            {
                Logger.WriteLine("Only supported for radio manufacturing address 7C");
                return;
            }

            // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
            const string secret = "DELCO";
            var code = (ushort)(secret[4] * 256 + secret[3]);
            var workshopCode = (ushort)(secret[1] * 256 + secret[0]);
            var unknown = (byte)secret[2];

            kwp1281.Login(code, workshopCode, unknown);
            var bytes = kwp1281.ReadRomEeprom(0x0014, 2);
            Logger.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
        }

        private static void DumpCcmRom(IKW1281Dialog kwp1281, int controllerAddress)
        {
            if (controllerAddress != (int)ControllerAddress.CCM)
            {
                Logger.WriteLine("Only supported for CCM");
                return;
            }

            kwp1281.Login(19283, 222);

            var dumpFileName = "ccm_rom_dump.bin";
            const byte blockSize = 8;

            Logger.WriteLine($"Saving CCM ROM to {dumpFileName}");

            bool succeeded = true;
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                for (int seg = 0; seg < 16; seg++)
                {
                    for (int msb = 0; msb < 16; msb++)
                    {
                        for (int lsb = 0; lsb < 256; lsb += blockSize)
                        {
                            var blockBytes = kwp1281.ReadCcmRom((byte)seg, (byte)msb, (byte)lsb, blockSize);
                            if (blockBytes == null)
                            {
                                blockBytes = Enumerable.Repeat((byte)0, blockSize).ToList();
                                succeeded = false;
                            }
                            else if (blockBytes.Count < blockSize)
                            {
                                blockBytes.AddRange(Enumerable.Repeat((byte)0, blockSize - blockBytes.Count));
                                succeeded = false;
                            }

                            fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                            fs.Flush();
                        }
                    }
                }
            }

            if (!succeeded)
            {
                Logger.WriteLine();
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine();
            }
        }

        private static void DumpClusterNecRom(IKW1281Dialog kwp1281, int controllerAddress)
        {
            if (controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            var dumpFileName = "cluster_nec_rom_dump.bin";
            const byte blockSize = 16;

            Logger.WriteLine($"Saving cluster NEC ROM to {dumpFileName}");

            bool succeeded = true;
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                {
                    for (int address = 0; address < 65536; address += blockSize)
                    {
                        var blockBytes = kwp1281.CustomReadNecRom((ushort)address, blockSize);
                        if (blockBytes == null)
                        {
                            blockBytes = Enumerable.Repeat((byte)0, blockSize).ToList();
                            succeeded = false;
                        }
                        else if (blockBytes.Count < blockSize)
                        {
                            blockBytes.AddRange(Enumerable.Repeat((byte)0, blockSize - blockBytes.Count));
                            succeeded = false;
                        }

                        fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                        fs.Flush();
                    }
                }
            }

            if (!succeeded)
            {
                Logger.WriteLine();
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine();
            }
        }

        private void DumpEeprom(IKW1281Dialog kwp1281, int controllerAddress, uint address, uint length)
        {
            switch (controllerAddress)
            {
                case (int)ControllerAddress.Cluster:
                    DumpClusterEeprom(kwp1281, (ushort)address, (ushort)length);
                    break;
                case (int)ControllerAddress.CCM:
                    DumpCcmEeprom(kwp1281, (ushort)address, (ushort)length);
                    break;
                default:
                    Logger.WriteLine("Only supported for cluster and CCM");
                    break;
            }
        }

        private void DumpMem(IKW1281Dialog kwp1281, int controllerAddress, uint address, uint length)
        {
            if (controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            DumpClusterMem(kwp1281, address, length);
        }

        private static void DumpRB8Eeprom(KW2000Dialog kwp2000, int controllerAddress, uint address, uint length, int kwpVersion)
        {
            if (controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster (address 17)");
                return;
            }

            if (kwpVersion < 2000)
            {
                Logger.WriteLine($"Cluster protocol is KWP{kwpVersion} but needs to be KWP2xxx");
                return;
            }

            kwp2000.SecurityAccess(0xFB);
            kwp2000.DumpEeprom(address, length);
        }

        private void LoadEeprom(IKW1281Dialog kwp1281, int controllerAddress, uint address, string filename)
        {
            if (controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            LoadClusterEeprom(kwp1281, (ushort)address, filename);
        }

        private static void MapEeprom(IKW1281Dialog kwp1281, int controllerAddress)
        {
            switch (controllerAddress)
            {
                case (int)ControllerAddress.Cluster:
                    MapClusterEeprom(kwp1281);
                    break;
                case (int)ControllerAddress.CCM:
                    MapCcmEeprom(kwp1281);
                    break;
                default:
                    Logger.WriteLine("Only supported for cluster and CCM");
                    break;
            }
        }

        private byte ReadEeprom(IKW1281Dialog kwp1281, int controllerAddress, uint address, byte value)
        {
            UnlockControllerForEepromReadWrite(kwp1281, (ControllerAddress)controllerAddress);

            var blockBytes = kwp1281.ReadEeprom((ushort)address, 1);
            if (blockBytes == null)
            {
                Logger.WriteLine("EEPROM read failed");
            }
            else
            {
                value = blockBytes[0];
                Logger.WriteLine(
                    $"Address {address} (${address:X4}): Value {value} (${value:X2})");
            }

            return value;
        }

        private static void ReadFaultCodes(IKW1281Dialog kwp1281, int controllerAddress)
        {
            var faultCodes = kwp1281.ReadFaultCodes();
            Logger.WriteLine("Fault codes:");
            foreach(var faultCode in faultCodes)
            {
                Logger.WriteLine($"    {faultCode}");
            }
        }

        private static void ReadIdent(IKW1281Dialog kwp1281)
        {
            var identInfo = kwp1281.ReadIdent();
            Logger.WriteLine($"Ident: {identInfo}");
        }

        private static void ReadSoftwareVersion(IKW1281Dialog kwp1281)
        {
            kwp1281.CustomReadSoftwareVersion();
        }

        private static void Reset(IKW1281Dialog kwp1281, int controllerAddress)
        {
            if (controllerAddress == (int)ControllerAddress.Cluster)
            {
                kwp1281.CustomReset();
            }
            else
            {
                Logger.WriteLine("Only supported for cluster");
            }
        }

        private void WriteEeprom(IKW1281Dialog kwp1281, int controllerAddress, uint address, byte value)
        {
            UnlockControllerForEepromReadWrite(kwp1281, (ControllerAddress)controllerAddress);

            kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
        }

        // End top-level commands

        private static void MapClusterEeprom(IKW1281Dialog kwp1281)
        {
            // Unlock partial EEPROM read
            _ = kwp1281.SendCustom(new List<byte> { 0x9D, 0x39, 0x34, 0x34, 0x40 });

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
            Logger.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private static void MapCcmEeprom(IKW1281Dialog kwp1281)
        {
            kwp1281.Login(19283, 222);

            var bytes = new List<byte>();
            const byte blockSize = 1;
            for (int addr = 0; addr <= 65535; addr += blockSize)
            {
                var blockBytes = kwp1281.ReadEeprom((ushort)addr, blockSize);
                blockBytes = Enumerable.Repeat(
                    blockBytes == null ? (byte)0 : (byte)0xFF,
                    blockSize).ToList();
                bytes.AddRange(blockBytes);
            }
            var dumpFileName = "ccm_eeprom_map.bin";
            Logger.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private void DumpCcmEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.CCM);

            var dumpFileName = $"ccm_eeprom_${startAddress:X4}.bin";

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 12, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");
        }

        private void UnlockControllerForEepromReadWrite(
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
                    Logger.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
                }

                if (!ClusterRequiresSeedKey(kwp1281))
                {
                    Logger.WriteLine(
                        "Cluster is unlocked for EEPROM access. Skipping Seed/Key login.");
                    return;
                }

                ClusterSeedKeyAuthenticate(kwp1281);
            }
        }

        private static void ClusterSeedKeyAuthenticate(IKW1281Dialog kwp1281)
        {
            // Perform Seed/Key authentication
            Logger.WriteLine("Sending Custom \"Seed request\" block");
            var response = kwp1281.SendCustom(new List<byte> { 0x96, 0x01 });

            var responseBlocks = response.Where(b => !b.IsAckNak).ToList();
            if (responseBlocks.Count == 1 && responseBlocks[0] is CustomBlock customBlock)
            {
                Logger.WriteLine($"Block: {Utils.Dump(customBlock.Body)}");

                var keyBytes = KeyFinder.FindKey(customBlock.Body.ToArray());

                Logger.WriteLine("Sending Custom \"Key response\" block");

                var keyResponse = new List<byte> { 0x96, 0x02 };
                keyResponse.AddRange(keyBytes);

                response = kwp1281.SendCustom(keyResponse);
            }
        }

        private static bool ClusterRequiresSeedKey(IKW1281Dialog kwp1281)
        {
            Logger.WriteLine("Sending Custom \"Need Seed/Key?\" block");
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

        private bool UnlockCluster(IKW1281Dialog kwp1281)
        {
            var versionBlocks = kwp1281.CustomReadSoftwareVersion();

            // Now we need to send an unlock code that is unique to each ROM version
            Logger.WriteLine("Sending Custom \"Unlock partial EEPROM read\" block");
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
                    Logger.WriteLine(
                        $"Unlock code for software version {KW1281Dialog.DumpMixedContent(softwareVersion)} is {Utils.Dump(unlockCode)}");
                    if (unlockCodes.Length > 1)
                    {
                        Logger.WriteLine("Please report this to the program maintainer.");
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
        private byte[][] GetClusterUnlockCodes(List<byte> softwareVersion)
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
            else if (
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK503LL", 0x00, 0x09)) ||
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VWK503MH", 0x00, 0x09))) // 1J0920927 V02
            {
                return new[] { new byte[] { 0x3E, 0x35, 0x3D, 0x3A } };
            }
            else if (Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VBKX00MH", 0x00, 0x01)))
            {
                return new[] { new byte[] { 0x3A, 0x39, 0x31, 0x43 } };
            }
            else if (Enumerable.SequenceEqual(softwareVersion, ClusterVersion("V599LLA ", 0x00, 0x01))) // 1J0920800L V59
            {
                return new[] { new byte[] { 0x38, 0x3F, 0x40, 0x35 } };
            }
            else if (
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VAT500LL", 0x20, 0x01)) || // 1J0920905L V01
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VAT500MH", 0x10, 0x01)) || // 1J0920925D V06
                Enumerable.SequenceEqual(softwareVersion, ClusterVersion("VAT500MH", 0x20, 0x01)))   // 1J5920925C V09
            {
                return new[] { new byte[] { 0x01, 0x04, 0x3D, 0x35 } };
            }
            else
            {
                return _clusterUnlockCodes;
            }
        }

        private readonly byte[][] _clusterUnlockCodes = new[]
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
            new byte[] { 0x01, 0x04, 0x3D, 0x35 },
            new byte[] { 0x3E, 0x35, 0x3D, 0x3A },
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
                Logger.WriteLine();
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Logger.WriteLine("**********************************************************************");
                Logger.WriteLine();
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
                Logger.WriteLine("EEPROM write failed. You should probably try again.");
            }
        }

        private void DumpClusterEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            var identInfo = kwp1281.ReadIdent().ToString().Replace(' ', '_').Replace(":", "");

            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            var dumpFileName = $"{identInfo}_${startAddress:X4}_eeprom.bin";

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 16, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");
        }

        private void LoadClusterEeprom(IKW1281Dialog kwp1281, ushort address, string filename)
        {
            _ = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            if (!File.Exists(filename))
            {
                Logger.WriteLine($"File {filename} does not exist.");
                return;
            }

            Logger.WriteLine($"Reading {filename}");
            var bytes = File.ReadAllBytes(filename);

            Logger.WriteLine("Writing to cluster...");
            WriteEeprom(kwp1281, address, bytes, 16);
        }

        private void DumpClusterMem(IKW1281Dialog kwp1281, uint startAddress, uint length)
        {
            UnlockControllerForEepromReadWrite(kwp1281, ControllerAddress.Cluster);

            const byte blockSize = 15;

            var dumpFileName = $"cluster_mem_${startAddress:X6}.bin";
            Logger.WriteLine($"Saving memory dump to {dumpFileName}");
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
            Logger.WriteLine($"Saved memory dump to {dumpFileName}");
        }

        private static uint ParseUint(string numberString)
        {
            uint number;

            if (numberString.StartsWith("$"))
            {
                number = uint.Parse(numberString[1..], NumberStyles.HexNumber);
            }
            else
            {
                number = uint.Parse(numberString);
            }

            return number;
        }

        private static void ShowUsage()
        {
            Logger.WriteLine("Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]");
            Logger.WriteLine("       PORT    = COM1|COM2|etc.");
            Logger.WriteLine("       BAUD    = 10400|9600|etc.");
            Logger.WriteLine("       ADDRESS = The controller address, e.g. 17 (cluster), 46 (CCM), 56 (radio)");
            Logger.WriteLine("       COMMAND =");
            Logger.WriteLine("                 ActuatorTest");
            Logger.WriteLine("                 DelcoVWPremium5SafeCode");
            Logger.WriteLine("                 DumpCcmRom");
            Logger.WriteLine("                 DumpClusterNecRom");
            Logger.WriteLine("                 DumpEeprom START LENGTH");
            Logger.WriteLine("                            START  = Start address in decimal (e.g. 0) or hex (e.g. $0)");
            Logger.WriteLine("                            LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. $800)");
            Logger.WriteLine("                 DumpMem START LENGTH");
            Logger.WriteLine("                         START  = Start address in decimal (e.g. 8192) or hex (e.g. $2000)");
            Logger.WriteLine("                         LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. $10000)");
            Logger.WriteLine("                 DumpRB8Eeprom START LENGTH");
            Logger.WriteLine("                               START  = Start address in decimal (e.g. 66560) or hex (e.g. $10400)");
            Logger.WriteLine("                               LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. $400)");
            Logger.WriteLine("                 LoadEeprom START FILENAME");
            Logger.WriteLine("                            START  = Start address in decimal (e.g. 0) or hex (e.g. $0)");
            Logger.WriteLine("                            FILENAME = Name of file containing binary data to load into EEPROM");
            Logger.WriteLine("                 MapEeprom");
            Logger.WriteLine("                 ReadFaultCodes");
            Logger.WriteLine("                 ReadIdent");
            Logger.WriteLine("                 ReadEeprom ADDRESS");
            Logger.WriteLine("                            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)");
            Logger.WriteLine("                 ReadSoftwareVersion");
            Logger.WriteLine("                 Reset");
            Logger.WriteLine("                 WriteEeprom ADDRESS VALUE");
            Logger.WriteLine("                             ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)");
            Logger.WriteLine("                             VALUE   = Value in decimal (e.g. 138) or hex (e.g. $8A)");
        }
    }
}
