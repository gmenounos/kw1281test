﻿using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Cluster;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

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
                Logger.WriteLine($"Caught: {ex.GetType()} {ex.Message}");
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
            Logger.WriteLine($"Args: {string.Join(' ', args)}");
            Logger.WriteLine($"OSVersion: {Environment.OSVersion}");
            Logger.WriteLine($".NET Version: {Environment.Version}");
            Logger.WriteLine($"Culture: {CultureInfo.InstalledUICulture}");

            if (args.Length < 4)
            {
                ShowUsage();
                return;
            }

            // This seems to increase the accuracy of our timing loops
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;

            string portName = args[0];
            var baudRate = int.Parse(args[1]);
            _controllerAddress = int.Parse(args[2], NumberStyles.HexNumber);
            var command = args[3];
            uint address = 0;
            uint length = 0;
            byte value = 0;
            int softwareCoding = 0;
            int workshopCode = 0;

            if (string.Compare(command, "ReadEeprom", true) == 0)
            {
                if (args.Length < 5)
                {
                    ShowUsage();
                    return;
                }

                address = Utils.ParseUint(args[4]);
            }
            else if (string.Compare(command, "DumpMarelliMem", true) == 0 ||
                     string.Compare(command, "DumpEeprom", true) == 0 ||
                     string.Compare(command, "DumpMem", true) == 0 ||
                     string.Compare(command, "DumpRB8Eeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = Utils.ParseUint(args[4]);
                length = Utils.ParseUint(args[5]);

                if (args.Length > 6)
                {
                    _filename = args[6];
                }
            }
            else if (string.Compare(command, "WriteEeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = Utils.ParseUint(args[4]);
                value = (byte)Utils.ParseUint(args[5]);
            }
            else if (string.Compare(command, "LoadEeprom", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                address = Utils.ParseUint(args[4]);
                _filename = args[5];
            }
            else if (string.Compare(command, "SetSoftwareCoding", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                softwareCoding = (int)Utils.ParseUint(args[4]);
                if (softwareCoding > 32767)
                {
                    Logger.WriteLine("SoftwareCoding cannot be greater than 32767.");
                    return;
                }
                workshopCode = (int)Utils.ParseUint(args[5]);
                if (workshopCode > 99999)
                {
                    Logger.WriteLine("WorkshopCode cannot be greater than 99999.");
                    return;
                }
            }
            else if (string.Compare(command, "DumpEdc15Eeprom", true) == 0)
            {
                if (args.Length < 4)
                {
                    ShowUsage();
                    return;
                }

                if (args.Length > 4)
                {
                    _filename = args[4];
                }
            }

            using var @interface = OpenPort(portName, baudRate);

            _kwpCommon = new KwpCommon(@interface);

            switch (command.ToLower())
            {
                case "actuatortest":
                    Kwp1281Wakeup();
                    ActuatorTest(_kwp1281);
                    break;

                case "clearfaultcodes":
                    Kwp1281Wakeup();
                    ClearFaultCodes(_kwp1281);
                    break;

                case "delcovwpremium5safecode":
                    Kwp1281Wakeup();
                    DelcoVWPremium5SafeCode(_kwp1281);
                    break;

                case "dumpccmrom":
                    Kwp1281Wakeup();
                    DumpCcmRom(_kwp1281);
                    break;

                case "dumpclusternecrom":
                    Kwp1281Wakeup();
                    DumpClusterNecRom(_kwp1281);
                    break;

                case "dumpedc15eeprom":
                    DumpEdc15Eeprom();
                    break;

                case "dumpeeprom":
                    Kwp1281Wakeup();
                    DumpEeprom(_kwp1281, address, length);
                    break;

                case "dumpmarellimem":
                    var ecuInfo = Kwp1281Wakeup();
                    if (_controllerAddress != (int)ControllerAddress.Cluster)
                    {
                        Logger.WriteLine("Only supported for clusters");
                    }
                    else
                    {
                        MarelliCluster.DumpMem(
                            _kwp1281, ecuInfo, _filename, (ushort)address, (ushort)length);
                    }
                    return;

                case "dumpmem":
                    Kwp1281Wakeup();
                    DumpMem(_kwp1281, address, length);
                    break;

                case "dumprb8eeprom":
                    DumpRB8Eeprom(Kwp2000Wakeup(evenParityWakeup: true), address, length);
                    break;

                case "getskc":
                    GetSkc();
                    break;

                case "loadeeprom":
                    Kwp1281Wakeup();
                    LoadEeprom(_kwp1281, address);
                    break;

                case "mapeeprom":
                    Kwp1281Wakeup();
                    MapEeprom(_kwp1281);
                    break;

                case "readeeprom":
                    Kwp1281Wakeup();
                    ReadEeprom(_kwp1281, address);
                    break;

                case "readfaultcodes":
                    Kwp1281Wakeup();
                    ReadFaultCodes(_kwp1281);
                    break;

                case "readident":
                    Kwp1281Wakeup();
                    ReadIdent(_kwp1281);
                    break;

                case "readsoftwareversion":
                    Kwp1281Wakeup();
                    ReadSoftwareVersion(_kwp1281);
                    break;

                case "reset":
                    Kwp1281Wakeup();
                    Reset(_kwp1281);
                    break;

                case "setsoftwarecoding":
                    Kwp1281Wakeup();
                    SetSoftwareCoding(_kwp1281, softwareCoding, workshopCode);
                    break;

                case "writeeeprom":
                    Kwp1281Wakeup();
                    WriteEeprom(_kwp1281, address, value);
                    break;

                default:
                    ShowUsage();
                    break;
            }

            if (_kwp1281 != null)
            {
                _kwp1281.EndCommunication();
            }
        }

        private ControllerInfo Kwp1281Wakeup(bool evenParityWakeup = false)
        {
            Logger.WriteLine("Sending wakeup message");

            var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParityWakeup);

            if (kwpVersion != 1281)
            {
                throw new InvalidOperationException("Expected KWP1281 protocol.");
            }

            _kwp1281 = new KW1281Dialog(_kwpCommon);

            var ecuInfo = _kwp1281.ReadEcuInfo();
            Logger.WriteLine($"ECU: {ecuInfo}");

            return ecuInfo;
        }

        private KW2000Dialog Kwp2000Wakeup(bool evenParityWakeup = false)
        {
            Logger.WriteLine("Sending wakeup message");

            var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParityWakeup);

            if (kwpVersion == 1281)
            {
                throw new InvalidOperationException("Expected KWP2000 protocol.");
            }

            var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

            return kwp2000;
        }

        /// <summary>
        /// Opens the serial port.
        /// </summary>
        /// <param name="portName">
        /// Either the device name of a serial port (e.g. COM1, /dev/tty23)
        /// or an FTDI USB->Serial device serial number (2 letters followed by 6 letters/numbers).
        /// </param>
        /// <param name="baudRate"></param>
        /// <returns></returns>
        private static IInterface OpenPort(string portName, int baudRate)
        {
            if (Regex.IsMatch(portName.ToUpper(), @"\A[A-Z0-9]{8}\Z"))
            {
                Logger.WriteLine($"Opening FTDI serial port {portName}");
                return new FtdiInterface(portName, baudRate);
            }
            else
            {
                Logger.WriteLine($"Opening serial port {portName}");
                return new GenericInterface(portName, baudRate);
            }
        }

        // Begin top-level commands

        private static void ActuatorTest(IKW1281Dialog kwp1281)
        {
            using KW1281KeepAlive keepAlive = new(kwp1281);

            ConsoleKeyInfo keyInfo;
            do
            {
                var response = keepAlive.ActuatorTest(0x00);
                if (response == null || response.ActuatorName == "End")
                {
                    Logger.WriteLine("End of test.");
                    break;
                }
                Logger.WriteLine($"Actuator Test: {response.ActuatorName}");

                // Press any key to advance to next test or press Q to exit
                Console.Write("Press 'N' to advance to next test or 'Q' to quit");
                do
                {
                    keyInfo = Console.ReadKey(intercept: true);
                } while (keyInfo.Key != ConsoleKey.N && keyInfo.Key != ConsoleKey.Q);
                Console.WriteLine();
            } while (keyInfo.Key != ConsoleKey.Q);
        }

        private void ClearFaultCodes(IKW1281Dialog kwp1281)
        {
            var succeeded = kwp1281.ClearFaultCodes(_controllerAddress);
            if (succeeded)
            {
                Logger.WriteLine("Fault codes cleared.");
            }
            else
            {
                Logger.WriteLine("Failed to clear fault codes.");
            }
        }

        private void DelcoVWPremium5SafeCode(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.RadioManufacturing)
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

        private void DumpCcmRom(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.CCM &&
                _controllerAddress != (int)ControllerAddress.CentralLocking)
            {
                Logger.WriteLine("Only supported for CCM and Central Locking");
                return;
            }

            kwp1281.Login(19283, 222);

            var dumpFileName = _filename ?? "ccm_rom_dump.bin";
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

        private void DumpClusterNecRom(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            var dumpFileName = _filename ?? "cluster_nec_rom_dump.bin";
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

        private void DumpEdc15Eeprom()
        {
            Kwp1281Wakeup();
            _kwp1281.EndCommunication();

            Thread.Sleep(1000);

            // Now wake it up again, hopefully in KW2000 mode
            _kwpCommon.Interface.SetBaudRate(10400);
            var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParity: false);
            if (kwpVersion < 2000)
            {
                throw new InvalidOperationException(
                    $"Unable to wake up ECU in KW2000 mode. KW version: {kwpVersion}");
            }
            Console.WriteLine($"KW Version: {kwpVersion}");

            var edc15 = new Edc15VM(_kwpCommon, _controllerAddress);
            edc15.DumpEeprom(_filename);

            _kwp1281 = null;
        }

        private void DumpEeprom(IKW1281Dialog kwp1281, uint address, uint length)
        {
            switch (_controllerAddress)
            {
                case (int)ControllerAddress.Cluster:
                    DumpClusterEeprom(kwp1281, (ushort)address, (ushort)length);
                    break;
                case (int)ControllerAddress.CCM:
                case (int)ControllerAddress.CentralLocking:
                    DumpCcmEeprom(kwp1281, (ushort)address, (ushort)length);
                    break;
                default:
                    Logger.WriteLine("Only supported for cluster, CCM and Central Locking");
                    break;
            }
        }

        private void DumpMem(IKW1281Dialog kwp1281, uint address, uint length)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            DumpClusterMem(kwp1281, address, length);
        }

        /// <summary>
        /// Dumps the EEPROM of a Bosch RB8 cluster to a file.
        /// </summary>
        /// <returns>The dump file name or null if the EEPROM was not dumped.</returns>
        private string DumpRB8Eeprom(KW2000Dialog kwp2000, uint address, uint length)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster (address 17)");
                return null;
            }

            var dumpFileName = _filename ?? $"RB8_${address:X6}_eeprom.bin";

            BoschCluster.SecurityAccess(kwp2000, 0xFB);
            kwp2000.DumpEeprom(address, length, dumpFileName);

            return dumpFileName;
        }

        private void GetSkc()
        {
            if (_controllerAddress == (int)ControllerAddress.Cluster)
            {
                var ecuInfo = Kwp1281Wakeup();
                if (ecuInfo.Text.Contains("VDO"))
                {
                    var partNumberMatch = Regex.Match(
                        ecuInfo.Text,
                        "\\b(\\d[a-zA-Z])\\d9(\\d{2})\\d{3}[a-zA-Z][a-zA-Z]?\\b");
                    if (partNumberMatch.Success)
                    {
                        var family = partNumberMatch.Groups[1].Value;

                        switch(partNumberMatch.Groups[2].Value)
                        {
                            case "19": // Non-CAN
                                Logger.WriteLine($"Cluster is non-Immo so there is no SKC.");
                                return;
                            case "20": // CAN
                                break;
                            default:
                                Logger.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                                return;
                        }

                        const int startAddress = 0x90;
                        var dumpFileName = DumpClusterEeprom(_kwp1281, startAddress, length: 0x7C);
                        var buf = File.ReadAllBytes(dumpFileName);
                        var skc = VdoCluster.GetSkc(buf, startAddress);
                        if (!string.IsNullOrEmpty(skc))
                        {
                            Logger.WriteLine($"SKC: {skc}");
                        }
                        else
                        {
                            Logger.WriteLine($"Unable to determine SKC.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"ECU Info: {ecuInfo.Text}");
                    }
                }
                else if (ecuInfo.Text.Contains("RB8"))
                {
                    // Need to quit KWP1281 before switching to KWP2000
                    _kwp1281.EndCommunication();
                    _kwp1281 = null;
                    Thread.Sleep(TimeSpan.FromSeconds(2));

                    var dumpFileName = DumpRB8Eeprom(
                        Kwp2000Wakeup(evenParityWakeup: true), 0x1040E, 2);
                    var buf = File.ReadAllBytes(dumpFileName);
                    var skc = Utils.GetShort(buf, 0).ToString("D5");
                    Logger.WriteLine($"SKC: {skc}");
                }
                else if (ecuInfo.Text.Contains("M73"))
                {
                    // TODO: Marelli
                    Console.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
                }
                else
                {
                    Console.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
                }
            }
            else if (_controllerAddress == (int)ControllerAddress.Ecu)
            {
                // 038906012GN
                Logger.WriteLine("Not supported for ECUs yet.");
            }
            else
            {
                Logger.WriteLine("Only supported for clusters (address 17) and ECUs (address 1)");
            }
        }

        private void LoadEeprom(IKW1281Dialog kwp1281, uint address)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster");
                return;
            }

            LoadClusterEeprom(kwp1281, (ushort)address, _filename);
        }

        private void MapEeprom(IKW1281Dialog kwp1281)
        {
            switch (_controllerAddress)
            {
                case (int)ControllerAddress.Cluster:
                    MapClusterEeprom(kwp1281);
                    break;
                case (int)ControllerAddress.CCM:
                case (int)ControllerAddress.CentralLocking:
                    MapCcmEeprom(kwp1281);
                    break;
                default:
                    Logger.WriteLine("Only supported for cluster, CCM and Central Locking");
                    break;
            }
        }

        private void ReadEeprom(IKW1281Dialog kwp1281, uint address)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            var blockBytes = kwp1281.ReadEeprom((ushort)address, 1);
            if (blockBytes == null)
            {
                Logger.WriteLine("EEPROM read failed");
            }
            else
            {
                var value = blockBytes[0];
                Logger.WriteLine(
                    $"Address {address} (${address:X4}): Value {value} (${value:X2})");
            }
        }

        private static void ReadFaultCodes(IKW1281Dialog kwp1281)
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
            foreach (var identInfo in kwp1281.ReadIdent())
            {
                Logger.WriteLine($"Ident: {identInfo}");
            }
        }

        private static void ReadSoftwareVersion(IKW1281Dialog kwp1281)
        {
            kwp1281.CustomReadSoftwareVersion();
        }

        private void Reset(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress == (int)ControllerAddress.Cluster)
            {
                kwp1281.CustomReset();
            }
            else
            {
                Logger.WriteLine("Only supported for cluster");
            }
        }

        private void SetSoftwareCoding(
            IKW1281Dialog kwp1281, int softwareCoding, int workshopCode)
        {
            var succeeded = kwp1281.SetSoftwareCoding(_controllerAddress, softwareCoding, workshopCode);
            if (succeeded)
            {
                Logger.WriteLine("Software coding set.");
            }
            else
            {
                Logger.WriteLine("Failed to set software coding.");
            }
        }

        private void WriteEeprom(IKW1281Dialog kwp1281, uint address, byte value)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
        }

        // End top-level commands

        private void MapClusterEeprom(IKW1281Dialog kwp1281)
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
            var dumpFileName = _filename ?? "eeprom_map.bin";
            Logger.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private void MapCcmEeprom(IKW1281Dialog kwp1281)
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
            var dumpFileName = _filename ?? "ccm_eeprom_map.bin";
            Logger.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private void DumpCcmEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            var dumpFileName = _filename ?? $"ccm_eeprom_${startAddress:X4}.bin";

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 12, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");
        }

        private void UnlockControllerForEepromReadWrite(IKW1281Dialog kwp1281)
        {
            switch ((ControllerAddress)_controllerAddress)
            {
                case ControllerAddress.CCM:
                case ControllerAddress.CentralLocking:
                    kwp1281.Login(code: 19283, workshopCode: 222); // This is what VDS-PRO uses
                    break;

                case ControllerAddress.Cluster:
                    // TODO:UnlockCluster() is only needed for EEPROM read, not memory read
                    if (!VdoCluster.UnlockCluster(kwp1281))
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
                    break;
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

                var keyBytes = VdoKeyFinder.FindKey(customBlock.Body.ToArray());

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

        private string DumpClusterEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            var identInfo = kwp1281.ReadIdent().First().ToString()
                .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
                .Replace(' ', '_').Replace(":", "");

            UnlockControllerForEepromReadWrite(kwp1281);

            var dumpFileName = _filename ?? $"{identInfo}_${startAddress:X4}_eeprom.bin";

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 16, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");

            return dumpFileName;
        }

        private void LoadClusterEeprom(IKW1281Dialog kwp1281, ushort address, string filename)
        {
            _ = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281);

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
            UnlockControllerForEepromReadWrite(kwp1281);

            const byte blockSize = 15;

            var dumpFileName = _filename ?? $"cluster_mem_${startAddress:X6}.bin";
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

        private static void ShowUsage()
        {
            Logger.WriteLine(@"Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
    PORT = COM1|COM2|etc.
    BAUD = 10400|9600|etc.
    ADDRESS = The controller address, e.g. 1 (ECU), 17 (cluster), 46 (CCM), 56 (radio)
    COMMAND =
        ActuatorTest
        ClearFaultCodes
        DelcoVWPremium5SafeCode
        DumpMarelliMem START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 3072) or hex (e.g. $C00)
            LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. $400)
            FILENAME = Optional filename
        DumpCcmRom
        DumpEdc15Eeprom [FILENAME]
            FILENAME = Optional filename
        DumpEeprom START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 0) or hex (e.g. $0)
            LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. $800)
            FILENAME = Optional filename
        DumpMem START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 8192) or hex (e.g. $2000)
            LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. $10000)
            FILENAME = Optional filename
        DumpRB8Eeprom START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 66560) or hex (e.g. $10400)
            LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. $400)
            FILENAME = Optional filename
        GetSKC
        LoadEeprom START FILENAME
            START = Start address in decimal (e.g. 0) or hex (e.g. $0)
            FILENAME = Name of file containing binary data to load into EEPROM
        MapEeprom
        ReadFaultCodes
        ReadIdent
        ReadEeprom ADDRESS
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
        ReadSoftwareVersion
        Reset
        SetSoftwareCoding CODING WORKSHOP
            CODING = Software coding in decimal (e.g. 4361) or hex (e.g. $1109)
            WORKSHOP = Workshop code in decimal (e.g. 4361) or hex (e.g. $1109)
        WriteEeprom ADDRESS VALUE
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
            VALUE = Value in decimal (e.g. 138) or hex (e.g. $8A)");
        }

        private IKwpCommon _kwpCommon;
        private int _controllerAddress;
        private string _filename = null;
        private KW1281Dialog _kwp1281;
    }
}
