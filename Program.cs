global using static BitFab.KW1281Test.Program;

using BitFab.KW1281Test.Cluster;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using BitFab.KW1281Test.Logging;
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
        public static ILog Log { get; private set; } = new ConsoleLog();

        static void Main(string[] args)
        {
            try
            {
                Log = new FileLog("KW1281Test.log");

                var tester = new Program();
                tester.Run(args);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"Caught: {ex.GetType()} {ex.Message}");
                Log.WriteLine($"Unhandled exception: {ex}");
            }
            finally
            {
                Log.Close();
            }
        }

        void Run(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("KW1281Test: Yesterday's diagnostics...");
            Thread.Sleep(2000);
            Console.WriteLine("Today.");
            Thread.Sleep(2000);
            Console.ResetColor();
            Console.WriteLine();

            var version = GetType().GetTypeInfo().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion;
            Log.WriteLine($"Version {version} (https://github.com/gmenounos/kw1281test/releases)");
            Log.WriteLine($"Args: {string.Join(' ', args)}");
            Log.WriteLine($"OSVersion: {Environment.OSVersion}");
            Log.WriteLine($".NET Version: {Environment.Version}");
            Log.WriteLine($"Culture: {CultureInfo.InstalledUICulture}");

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
            byte channel = 0;
            ushort channelValue = 0;
            ushort? login = null;
            byte groupNumber = 0;

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
                    Log.WriteLine("SoftwareCoding cannot be greater than 32767.");
                    return;
                }
                workshopCode = (int)Utils.ParseUint(args[5]);
                if (workshopCode > 99999)
                {
                    Log.WriteLine("WorkshopCode cannot be greater than 99999.");
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
            else if (string.Compare(command, "AdaptationRead", true) == 0)
            {
                if (args.Length < 5)
                {
                    ShowUsage();
                    return;
                }

                channel = byte.Parse(args[4]);

                if (args.Length > 5)
                {
                    login = ushort.Parse(args[5]);
                }
            }
            else if (
                string.Compare(command, "AdaptationSave", true) == 0 ||
                string.Compare(command, "AdaptationTest", true) == 0)
            {
                if (args.Length < 6)
                {
                    ShowUsage();
                    return;
                }

                channel = byte.Parse(args[4]);
                channelValue = ushort.Parse(args[5]);

                if (args.Length > 6)
                {
                    login = ushort.Parse(args[6]);
                }
            }
            else if (
                string.Compare(command, "BasicSettings", true) == 0 ||
                string.Compare(command, "GroupReading", true) == 0)
            {
                if (args.Length < 5)
                {
                    ShowUsage();
                    return;
                }

                groupNumber = byte.Parse(args[4]);
            }

            using var @interface = OpenPort(portName, baudRate);

            _kwpCommon = new KwpCommon(@interface);

            switch (command.ToLower())
            {
                case "actuatortest":
                    Kwp1281Wakeup();
                    ActuatorTest(_kwp1281!);
                    break;

                case "adaptationread":
                    var ecuInfo = Kwp1281Wakeup();
                    AdaptationRead(_kwp1281!, channel, login, ecuInfo.WorkshopCode);
                    break;

                case "adaptationsave":
                    ecuInfo = Kwp1281Wakeup();
                    AdaptationSave(_kwp1281!, channel, channelValue, login, ecuInfo.WorkshopCode);
                    break;

                case "adaptationtest":
                    ecuInfo = Kwp1281Wakeup();
                    AdaptationTest(_kwp1281!, channel, channelValue, login, ecuInfo.WorkshopCode);
                    break;

                case "basicsettings":
                    ecuInfo = Kwp1281Wakeup();
                    BasicSettings(_kwp1281!, groupNumber);
                    break;

                case "clarionvwpremium4safecode":
                    Kwp1281Wakeup();
                    ClarionVWPremium4SafeCode(_kwp1281!);
                    break;

                case "clearfaultcodes":
                    Kwp1281Wakeup();
                    ClearFaultCodes(_kwp1281!);
                    break;

                case "delcovwpremium5safecode":
                    Kwp1281Wakeup();
                    DelcoVWPremium5SafeCode(_kwp1281!);
                    break;

                case "dumpccmrom":
                    Kwp1281Wakeup();
                    DumpCcmRom(_kwp1281!);
                    break;

                case "dumpclusternecrom":
                    Kwp1281Wakeup();
                    DumpClusterNecRom(_kwp1281!);
                    break;

                case "dumpedc15eeprom":
                    DumpEdc15Eeprom();
                    break;

                case "dumpeeprom":
                    Kwp1281Wakeup();
                    DumpEeprom(_kwp1281!, address, length);
                    break;

                case "dumpmarellimem":
                    ecuInfo = Kwp1281Wakeup();
                    DumpMarelliMem(ref _kwp1281, address, length, ecuInfo);
                    return;

                case "dumpmem":
                    Kwp1281Wakeup();
                    DumpMem(_kwp1281!, address, length);
                    break;

                case "dumprb8eeprom":
                    DumpRB8Eeprom(Kwp2000Wakeup(evenParityWakeup: true), address, length);
                    break;

                case "getskc":
                    GetSkc();
                    break;

                case "groupreading":
                    ecuInfo = Kwp1281Wakeup();
                    GroupReading(_kwp1281!, groupNumber);
                    break;

                case "loadeeprom":
                    Kwp1281Wakeup();
                    LoadEeprom(_kwp1281!, address);
                    break;

                case "mapeeprom":
                    Kwp1281Wakeup();
                    MapEeprom(_kwp1281!);
                    break;

                case "readeeprom":
                    Kwp1281Wakeup();
                    ReadEeprom(_kwp1281!, address);
                    break;

                case "readfaultcodes":
                    Kwp1281Wakeup();
                    ReadFaultCodes(_kwp1281!);
                    break;

                case "readident":
                    Kwp1281Wakeup();
                    ReadIdent(_kwp1281!);
                    break;

                case "readsoftwareversion":
                    Kwp1281Wakeup();
                    ReadSoftwareVersion(_kwp1281!);
                    break;

                case "reset":
                    Kwp1281Wakeup();
                    Reset(_kwp1281!);
                    break;

                case "setsoftwarecoding":
                    Kwp1281Wakeup();
                    SetSoftwareCoding(_kwp1281!, softwareCoding, workshopCode);
                    break;

                case "writeeeprom":
                    Kwp1281Wakeup();
                    WriteEeprom(_kwp1281!, address, value);
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
            Log.WriteLine("Sending wakeup message");

            var kwpVersion = _kwpCommon!.WakeUp((byte)_controllerAddress, evenParityWakeup);

            if (kwpVersion != 1281)
            {
                throw new InvalidOperationException("Expected KWP1281 protocol.");
            }

            _kwp1281 = new KW1281Dialog(_kwpCommon, out ControllerInfo ecuInfo);
            Log.WriteLine($"ECU: {ecuInfo}");

            return ecuInfo;
        }

        private KW2000Dialog Kwp2000Wakeup(bool evenParityWakeup = false)
        {
            Log.WriteLine("Sending wakeup message");

            var kwpVersion = _kwpCommon!.WakeUp((byte)_controllerAddress, evenParityWakeup);

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
                Log.WriteLine($"Opening FTDI serial port {portName}");
                return new FtdiInterface(portName, baudRate);
            }
            else
            {
                Log.WriteLine($"Opening serial port {portName}");
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
                    Log.WriteLine("End of test.");
                    break;
                }
                Log.WriteLine($"Actuator Test: {response.ActuatorName}");

                // Press any key to advance to next test or press Q to exit
                Console.Write("Press 'N' to advance to next test or 'Q' to quit");
                do
                {
                    keyInfo = Console.ReadKey(intercept: true);
                } while (keyInfo.Key != ConsoleKey.N && keyInfo.Key != ConsoleKey.Q);
                Console.WriteLine();
            } while (keyInfo.Key != ConsoleKey.Q);
        }

        private void AdaptationRead(
            IKW1281Dialog kwp1281, byte channel,
            ushort? login, int workshopCode)
        {
            if (login.HasValue)
            {
                kwp1281.Login(login.Value, workshopCode);
            }
            kwp1281.AdaptationRead(channel);
        }

        private void AdaptationSave(
            IKW1281Dialog kwp1281, byte channel, ushort channelValue,
            ushort? login, int workshopCode)
        {
            if (login.HasValue)
            {
                kwp1281.Login(login.Value, workshopCode);
            }
            if (channel != 0 && !kwp1281.AdaptationTest(channel, channelValue))
            {
                return;
            }

            kwp1281.AdaptationSave(channel, channelValue);
        }

        private void AdaptationTest(
            IKW1281Dialog kwp1281, byte channel, ushort channelValue,
            ushort? login, int workshopCode)
        {
            if (login.HasValue)
            {
                kwp1281.Login(login.Value, workshopCode);
            }
            kwp1281.AdaptationTest(channel, channelValue);
        }

        private void BasicSettings(IKW1281Dialog iKW1281Dialog, byte groupNumber)
        {
            throw new NotImplementedException();
        }

        private void ClarionVWPremium4SafeCode(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.Radio)
            {
                Log.WriteLine("Only supported for radio address 56");
                return;
            }

            // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
            const byte readWriteSafeCode = 0xF0;
            const byte read = 0x00;
            kwp1281.SendBlock(new List<byte> { readWriteSafeCode, read });

            var block = kwp1281.ReceiveBlocks().FirstOrDefault(b => !b.IsAckNak);

            if (block == null)
            {
                Log.WriteLine("No response received from radio.");
            }
            else if (block.Title != readWriteSafeCode)
            {
                Log.WriteLine(
                    $"Unexpected response received from radio. Block title: ${block.Title:X2}");
            }
            else
            {
                var safeCode = block.Body[0] * 256 + block.Body[1];
                Log.WriteLine($"Safe code: {safeCode:X4}");
            }
        }

        private void ClearFaultCodes(IKW1281Dialog kwp1281)
        {
            var succeeded = kwp1281.ClearFaultCodes(_controllerAddress);
            if (succeeded)
            {
                Log.WriteLine("Fault codes cleared.");
            }
            else
            {
                Log.WriteLine("Failed to clear fault codes.");
            }
        }

        private void DelcoVWPremium5SafeCode(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.RadioManufacturing)
            {
                Log.WriteLine("Only supported for radio manufacturing address 7C");
                return;
            }

            // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
            const string secret = "DELCO";
            var code = (ushort)(secret[4] * 256 + secret[3]);
            var workshopCode = secret[2] * 65536 + secret[1] * 256 + secret[0];

            kwp1281.Login(code, workshopCode);
            var bytes = kwp1281.ReadRomEeprom(0x0014, 2);
            Log.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
        }

        private void DumpCcmRom(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.CCM &&
                _controllerAddress != (int)ControllerAddress.CentralLocking)
            {
                Log.WriteLine("Only supported for CCM and Central Locking");
                return;
            }

            kwp1281.Login(19283, 222);

            var dumpFileName = _filename ?? "ccm_rom_dump.bin";
            const byte blockSize = 8;

            Log.WriteLine($"Saving CCM ROM to {dumpFileName}");

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
                Log.WriteLine();
                Log.WriteLine("**********************************************************************");
                Log.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Log.WriteLine("**********************************************************************");
                Log.WriteLine();
            }
        }

        private void DumpClusterNecRom(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Log.WriteLine("Only supported for cluster");
                return;
            }

            var dumpFileName = _filename ?? "cluster_nec_rom_dump.bin";
            const byte blockSize = 16;

            Log.WriteLine($"Saving cluster NEC ROM to {dumpFileName}");

            bool succeeded = true;
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                var cluster = new VdoCluster(kwp1281);

                for (int address = 0; address < 65536; address += blockSize)
                {
                    var blockBytes = cluster.CustomReadNecRom((ushort)address, blockSize);
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

            if (!succeeded)
            {
                Log.WriteLine();
                Log.WriteLine("**********************************************************************");
                Log.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Log.WriteLine("**********************************************************************");
                Log.WriteLine();
            }
        }

        private string DumpEdc15Eeprom()
        {
            Kwp1281Wakeup();
            _kwp1281!.EndCommunication();

            Thread.Sleep(1000);

            // Now wake it up again, hopefully in KW2000 mode
            _kwpCommon!.Interface.SetBaudRate(10400);
            var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParity: false);
            if (kwpVersion < 2000)
            {
                throw new InvalidOperationException(
                    $"Unable to wake up ECU in KW2000 mode. KW version: {kwpVersion}");
            }
            Console.WriteLine($"KW Version: {kwpVersion}");

            var edc15 = new Edc15VM(_kwpCommon, _controllerAddress);

            var dumpFileName = _filename ?? $"EDC15_EEPROM.bin";

            edc15.DumpEeprom(dumpFileName);

            _kwp1281 = null;

            return dumpFileName;
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
                    Log.WriteLine("Only supported for cluster, CCM and Central Locking");
                    break;
            }
        }

        private void DumpMarelliMem(ref IKW1281Dialog? kwp1281, uint address, uint length, ControllerInfo ecuInfo)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Log.WriteLine("Only supported for clusters");
            }
            else
            {
                MarelliCluster.DumpMem(
                    kwp1281!, ecuInfo.Text, _filename, (ushort)address, (ushort)length);
                kwp1281 = null; // Don't try to send EndCommunication block
            }
        }

        private void DumpMem(IKW1281Dialog kwp1281, uint address, uint length)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Log.WriteLine("Only supported for cluster");
                return;
            }

            DumpClusterMem(kwp1281, address, length);
        }

        /// <summary>
        /// Dumps the EEPROM of a Bosch RB8 cluster to a file.
        /// </summary>
        /// <returns>The dump file name or null if the EEPROM was not dumped.</returns>
        private string? DumpRB8Eeprom(KW2000Dialog kwp2000, uint address, uint length)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Log.WriteLine("Only supported for cluster (address 17)");
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
                                Log.WriteLine($"Cluster is non-Immo so there is no SKC.");
                                return;
                            case "20": // CAN
                                break;
                            default:
                                Log.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                                return;
                        }

                        const int startAddress = 0x90;
                        var dumpFileName = DumpClusterEeprom(_kwp1281!, startAddress, length: 0x7C);
                        var buf = File.ReadAllBytes(dumpFileName);
                        var skc = VdoCluster.GetSkc(buf, startAddress);
                        if (skc.HasValue)
                        {
                            Log.WriteLine($"SKC: {skc:D5}");
                        }
                        else
                        {
                            Log.WriteLine($"Unable to determine SKC.");
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
                    _kwp1281!.EndCommunication();
                    _kwp1281 = null;
                    Thread.Sleep(TimeSpan.FromSeconds(2));

                    var dumpFileName = DumpRB8Eeprom(
                        Kwp2000Wakeup(evenParityWakeup: true), 0x1040E, 2);
                    var buf = File.ReadAllBytes(dumpFileName!);
                    var skc = Utils.GetShort(buf, 0);
                    Log.WriteLine($"SKC: {skc:D5}");
                }
                else if (ecuInfo.Text.Contains("M73"))
                {
                    var buf = MarelliCluster.DumpMem(_kwp1281!, ecuInfo.Text);
                    _kwp1281 = null; // Don't try to send EndCommunication block
                    if (buf.Length == 0x400)
                    {
                        var skc = Utils.GetShortBE(buf, 0x313);
                        Log.WriteLine($"SKC: {skc:D5}");
                    }
                    else if (buf.Length == 0x800)
                    {
                        var skc = Utils.GetShortBE(buf, 0x348);
                        Log.WriteLine($"SKC: {skc:D5}");
                    }
                    else
                    {
                        Console.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
                    }
                }
                else
                {
                    Console.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
                }
            }
            else if (_controllerAddress == (int)ControllerAddress.Ecu)
            {
                var dumpFileName = DumpEdc15Eeprom();
                var buf = File.ReadAllBytes(dumpFileName);
                var skc = Utils.GetShort(buf, 0x012E);
                var immo1 = buf[0x1B0];
                var immo2 = buf[0x1DE];
                var immoStatus = immo1 == 0x60 && immo2 == 0x60 ? "Off" : "On";
                Log.WriteLine($"SKC: {skc:D5}");
                Log.WriteLine($"Immo is {immoStatus} (${immo1:X2}, ${immo2:X2})");
            }
            else
            {
                Log.WriteLine("Only supported for clusters (address 17) and ECUs (address 1)");
            }
        }

        private void GroupReading(IKW1281Dialog kwp1281, byte groupNumber)
        {
            var succeeded = kwp1281.GroupReading(groupNumber);
        }

        private void LoadEeprom(IKW1281Dialog kwp1281, uint address)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Log.WriteLine("Only supported for cluster");
                return;
            }

            LoadClusterEeprom(kwp1281, (ushort)address, _filename!);
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
                    Log.WriteLine("Only supported for cluster, CCM and Central Locking");
                    break;
            }
        }

        private void ReadEeprom(IKW1281Dialog kwp1281, uint address)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            var blockBytes = kwp1281.ReadEeprom((ushort)address, 1);
            if (blockBytes == null)
            {
                Log.WriteLine("EEPROM read failed");
            }
            else
            {
                var value = blockBytes[0];
                Log.WriteLine(
                    $"Address {address} (${address:X4}): Value {value} (${value:X2})");
            }
        }

        private static void ReadFaultCodes(IKW1281Dialog kwp1281)
        {
            var faultCodes = kwp1281.ReadFaultCodes();
            if (faultCodes != null)
            {
                Log.WriteLine("Fault codes:");
                foreach (var faultCode in faultCodes)
                {
                    Log.WriteLine($"    {faultCode}");
                }
            }
        }

        private static void ReadIdent(IKW1281Dialog kwp1281)
        {
            foreach (var identInfo in kwp1281.ReadIdent())
            {
                Log.WriteLine($"Ident: {identInfo}");
            }
        }

        private void ReadSoftwareVersion(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress == (int)ControllerAddress.Cluster)
            {
                var cluster = new VdoCluster(kwp1281);
                cluster.CustomReadSoftwareVersion();
            }
            else
            {
                Log.WriteLine("Only supported for cluster");
            }
        }

        private void Reset(IKW1281Dialog kwp1281)
        {
            if (_controllerAddress == (int)ControllerAddress.Cluster)
            {
                var cluster = new VdoCluster(kwp1281);
                cluster.CustomReset();
            }
            else
            {
                Log.WriteLine("Only supported for cluster");
            }
        }

        private void SetSoftwareCoding(
            IKW1281Dialog kwp1281, int softwareCoding, int workshopCode)
        {
            var succeeded = kwp1281.SetSoftwareCoding(_controllerAddress, softwareCoding, workshopCode);
            if (succeeded)
            {
                Log.WriteLine("Software coding set.");
            }
            else
            {
                Log.WriteLine("Failed to set software coding.");
            }
        }

        private void WriteEeprom(IKW1281Dialog kwp1281, uint address, byte value)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
        }

        // End top-level commands

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
            Log.WriteLine($"Saving EEPROM map to {dumpFileName}");
            File.WriteAllBytes(dumpFileName, bytes.ToArray());
        }

        private void MapClusterEeprom(IKW1281Dialog kwp1281)
        {
            var cluster = new VdoCluster(kwp1281);

            var map = cluster.MapEeprom();

            var mapFileName = _filename ?? "eeprom_map.bin";
            Log.WriteLine($"Saving EEPROM map to {mapFileName}");
            File.WriteAllBytes(mapFileName, map.ToArray());
        }

        private void DumpCcmEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            UnlockControllerForEepromReadWrite(kwp1281);

            var dumpFileName = _filename ?? $"ccm_eeprom_${startAddress:X4}.bin";

            Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 12, dumpFileName);
            Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");
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
                    var cluster = new VdoCluster(kwp1281);
                    if (!cluster.Unlock())
                    {
                        Log.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
                    }

                    if (!cluster.RequiresSeedKey())
                    {
                        Log.WriteLine(
                            "Cluster is unlocked for ROM/EEPROM access. Skipping Seed/Key login.");
                        return;
                    }

                    cluster.SeedKeyAuthenticate();
                    if (cluster.RequiresSeedKey())
                    {
                        Log.WriteLine("Failed to unlock cluster.");
                    }
                    else
                    {
                        Log.WriteLine("Cluster is unlocked for ROM/EEPROM access.");
                    }
                    break;
            }
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
                Log.WriteLine();
                Log.WriteLine("**********************************************************************");
                Log.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
                Log.WriteLine("**********************************************************************");
                Log.WriteLine();
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
                Log.WriteLine("EEPROM write failed. You should probably try again.");
            }
        }

        private string DumpClusterEeprom(IKW1281Dialog kwp1281, ushort startAddress, ushort length)
        {
            var identInfo = kwp1281.ReadIdent().First().ToString()
                .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
                .Replace(' ', '_').Replace(":", "");

            UnlockControllerForEepromReadWrite(kwp1281);

            var dumpFileName = _filename ?? $"{identInfo}_${startAddress:X4}_eeprom.bin";

            Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 16, dumpFileName);
            Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");

            return dumpFileName;
        }

        private void LoadClusterEeprom(IKW1281Dialog kwp1281, ushort address, string filename)
        {
            _ = kwp1281.ReadIdent();

            UnlockControllerForEepromReadWrite(kwp1281);

            if (!File.Exists(filename))
            {
                Log.WriteLine($"File {filename} does not exist.");
                return;
            }

            Log.WriteLine($"Reading {filename}");
            var bytes = File.ReadAllBytes(filename);

            Log.WriteLine("Writing to cluster...");
            WriteEeprom(kwp1281, address, bytes, 16);
        }

        private void DumpClusterMem(IKW1281Dialog kwp1281, uint startAddress, uint length)
        {
            var cluster = new VdoCluster(kwp1281);
            if (!cluster.RequiresSeedKey())
            {
                Log.WriteLine(
                    "Cluster is unlocked for memory access. Skipping Seed/Key login.");
            }
            else
            {
                cluster.SeedKeyAuthenticate();
            }

            var dumpFileName = _filename ?? $"cluster_mem_${startAddress:X6}.bin";
            Log.WriteLine($"Saving memory dump to {dumpFileName}");

            cluster.DumpMem(dumpFileName, startAddress, length);

            Log.WriteLine($"Saved memory dump to {dumpFileName}");
        }

        private static void ShowUsage()
        {
            Log.WriteLine(@"Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
    PORT = COM1|COM2|etc.
    BAUD = 10400|9600|etc.
    ADDRESS = The controller address, e.g. 1 (ECU), 17 (cluster), 46 (CCM), 56 (radio)
    COMMAND =
        ActuatorTest
        AdaptationRead CHANNEL [LOGIN]
            CHANNEL = Channel number (0-99)
            LOGIN = Optional login (0-65535)
        AdaptationSave CHANNEL VALUE [LOGIN]
            CHANNEL = Channel number (0-99)
            VALUE = Channel value (0-65535)
            LOGIN = Optional login (0-65535)
        AdaptationTest CHANNEL VALUE [LOGIN]
            CHANNEL = Channel number (0-99)
            VALUE = Channel value (0-65535)
            LOGIN = Optional login (0-65535)
        BasicSettings GROUP
            GROUP = Group number (1-255)
        ClarionVWPremium4SafeCode
        ClearFaultCodes
        DelcoVWPremium5SafeCode
        DumpEdc15Eeprom [FILENAME]
            FILENAME = Optional filename
        DumpEeprom START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 0) or hex (e.g. $0)
            LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. $800)
            FILENAME = Optional filename
        DumpMarelliMem START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 3072) or hex (e.g. $C00)
            LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. $400)
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
        GroupReading GROUP
            GROUP = Group number (1-255)
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

        private IKwpCommon? _kwpCommon;
        private int _controllerAddress;
        private string? _filename = null;
        private IKW1281Dialog? _kwp1281;
    }
}
