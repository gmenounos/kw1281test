global using static BitFab.KW1281Test.Program;
using BitFab.KW1281Test.Cluster;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using BitFab.KW1281Test.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: InternalsVisibleTo("BitFab.KW1281Test.Tests")]

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

        private void Run(string[] args)
        {
            // Initial Setup
            DisplayInitialInfo(args);

            if (args.Length < 4)
            {
                if (args.Length == 2 && args[0].Contains("mileage", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (string.Equals(args[0], nameof(VdoCluster.VdoMileageHexToDecimal), StringComparison.InvariantCultureIgnoreCase))
                    {
                        Log.WriteLine($"Output mileage in decimal: {VdoCluster.VdoMileageHexToDecimal(args[1])}");
                    }
                    else if (string.Equals(args[0], nameof(VdoCluster.VdoMileageDecimalToHex), StringComparison.InvariantCultureIgnoreCase))
                    {
                        Log.WriteLine($"Output mileage in hex: {VdoCluster.VdoMileageDecimalToHex(int.Parse(args[1]))}");
                    }

                    // Rest of the program needs more than 2 arguments, so return here
                    return;
                }

                ShowUsage();
                return;
            }

            // Try setting the process priority
            TrySetRealTimeProcessPriority();

            var portName = args[0];
            var baudRate = int.Parse(args[1]);
            var controllerAddress = int.Parse(args[2], NumberStyles.HexNumber);
            var command = args[3];

            // Parse command-specific arguments
            if (!ParseCommandArguments(command, args, out CommandArguments commandArgs))
            {
                ShowUsage();
                return;
            }

            using var @interface = OpenPort(portName, baudRate);
            var tester = new Tester(@interface, controllerAddress);

            // Execute Command
            ExecuteCommand(command.ToLower(), tester, commandArgs);

            tester.EndCommunication();
        }

        private static bool ParseCommandArguments(string command, string[] args, out CommandArguments parsedArgs)
        {
            parsedArgs = new CommandArguments();
            uint address = 0;
            uint length = 0;
            byte value = 0;
            int softwareCoding = 0;
            int workshopCode = 0;
            byte channel = 0;
            ushort channelValue = 0;
            ushort? login = null;
            byte groupNumber = 0;
            string? filename = null;
            var addressValuePairs = new List<KeyValuePair<ushort, byte>>();

            if (string.Equals(command, "ReadEeprom", StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(command, "ReadRAM", StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(command, "ReadROM", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 5)
                    return false;

                address = Utils.ParseUint(args[4]);
            }
            else if (string.Equals(command, "DumpMarelliMem", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "DumpEeprom", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "DumpMem", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "DumpRam", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "DumpRBxMem", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "DumpRBxMemOdd", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 6)
                    return false;

                address = Utils.ParseUint(args[4]);
                length = Utils.ParseUint(args[5]);

                if (args.Length > 6)
                    filename = args[6];
            }
            else if (string.Equals(command, "WriteEeprom", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 6)
                    return false;

                address = Utils.ParseUint(args[4]);
                value = (byte)Utils.ParseUint(args[5]);
            }
            else if (string.Equals(command, "LoadEeprom", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 6)
                    return false;

                address = Utils.ParseUint(args[4]);
                filename = args[5];
            }
            else if (string.Equals(command, "SetSoftwareCoding", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 6)
                    return false;

                softwareCoding = (int)Utils.ParseUint(args[4]);
                if (softwareCoding > 32767)
                {
                    Log.WriteLine("SoftwareCoding cannot be greater than 32767.");
                    return false;
                }
                workshopCode = (int)Utils.ParseUint(args[5]);
                if (workshopCode > 99999)
                {
                    Log.WriteLine("WorkshopCode cannot be greater than 99999.");
                    return false;
                }
            }
            else if (string.Equals(command, "DumpEdc15Eeprom", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 4)
                    return false;

                if (args.Length > 4)
                    filename = args[4];
            }
            else if (string.Equals(command, "WriteEdc15Eeprom", StringComparison.CurrentCultureIgnoreCase))
            {
                // WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]

                if (args.Length < 6)
                    return false;

                var dateString = DateTime.Now.ToString("s").Replace(':', '-');
                filename = $"EDC15_EEPROM_{dateString}.bin";

                if (!ParseAddressesAndValues(args.Skip(4).ToList(), out addressValuePairs))
                    return false;
            }
            else if (string.Equals(command, "AdaptationRead", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 5)
                    return false;

                channel = byte.Parse(args[4]);

                if (args.Length > 5)
                    login = ushort.Parse(args[5]);
            }
            else if (string.Equals(command, "AdaptationSave", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "AdaptationTest", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 6)
                    return false;

                channel = byte.Parse(args[4]);
                channelValue = ushort.Parse(args[5]);

                if (args.Length > 6)
                    login = ushort.Parse(args[6]);
            }
            else if (string.Equals(command, "BasicSetting", StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(command, "GroupRead", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 5)
                    return false;

                groupNumber = byte.Parse(args[4]);
            }
            else if (string.Equals(command, "FindLogins", StringComparison.CurrentCultureIgnoreCase))
            {
                if (args.Length < 5)
                    return false;

                login = ushort.Parse(args[4]);
            }

            parsedArgs = new CommandArguments(address, length, value, softwareCoding, workshopCode, addressValuePairs, channel, channelValue, login, groupNumber, filename);
            return true;
        }

        private static void ExecuteCommand(string command, Tester tester, CommandArguments commandArgs)
        {
            switch (command.ToLower())
            {
                case "dumprbxmem":
                    tester.DumpRBxMem(commandArgs.Address, commandArgs.Length, commandArgs.Filename);
                    tester.EndCommunication();
                    return;

                case "dumprbxmemodd":
                    tester.DumpRBxMem(commandArgs.Address, commandArgs.Length, commandArgs.Filename, evenParityWakeup: false);
                    tester.EndCommunication();
                    return;

                case "getskc":
                    tester.GetSkc();
                    tester.EndCommunication();
                    return;

                case "togglerb4mode":
                    tester.ToggleRB4Mode();
                    tester.EndCommunication();
                    return;

                default:
                    break;
            }

            ControllerInfo ecuInfo = tester.Kwp1281Wakeup();

            switch (command.ToLower())
            {
                case "actuatortest":
                    tester.ActuatorTest();
                    break;

                case "adaptationread":
                    tester.AdaptationRead(commandArgs.Channel, commandArgs.Login, ecuInfo.WorkshopCode);
                    break;

                case "adaptationsave":
                    tester.AdaptationSave(commandArgs.Channel, commandArgs.ChannelValue, commandArgs.Login, ecuInfo.WorkshopCode);
                    break;

                case "adaptationtest":
                    tester.AdaptationTest(commandArgs.Channel, commandArgs.ChannelValue, commandArgs.Login, ecuInfo.WorkshopCode);
                    break;

                case "basicsetting":
                    tester.BasicSettingRead(commandArgs.GroupNumber);
                    break;

                case "clarionvwpremium4safecode":
                    tester.ClarionVWPremium4SafeCode();
                    break;

                case "clearfaultcodes":
                    tester.ClearFaultCodes();
                    break;

                case "delcovwpremium5safecode":
                    tester.DelcoVWPremium5SafeCode();
                    break;

                case "dumpccmrom":
                    tester.DumpCcmRom(commandArgs.Filename);
                    break;

                case "dumpclusternecrom":
                    tester.DumpClusterNecRom(commandArgs.Filename);
                    break;

                case "dumpedc15eeprom":
                    {
                        var eeprom = tester.ReadWriteEdc15Eeprom(commandArgs.Filename);
                        Edc15VM.DisplayEepromInfo(eeprom);
                    }
                    break;

                case "dumpeeprom":
                    tester.DumpEeprom(commandArgs.Address, commandArgs.Length, commandArgs.Filename);
                    break;

                case "dumpmarellimem":
                    tester.DumpMarelliMem(commandArgs.Address, commandArgs.Length, ecuInfo, commandArgs.Filename);
                    return;

                case "dumpmem":
                    tester.DumpMem(commandArgs.Address, commandArgs.Length, commandArgs.Filename);
                    break;

                case "dumpram":
                    tester.DumpRam(commandArgs.Address, commandArgs.Length, commandArgs.Filename);
                    break;

                case "findlogins":
                    tester.FindLogins(commandArgs.Login!.Value, ecuInfo.WorkshopCode);
                    break;

                case "getclusterid":
                    tester.GetClusterId();
                    break;

                case "groupread":
                    tester.GroupRead(commandArgs.GroupNumber);
                    break;

                case "loadeeprom":
                    tester.LoadEeprom(commandArgs.Address, commandArgs.Filename!);
                    break;

                case "mapeeprom":
                    tester.MapEeprom(commandArgs.Filename);
                    break;

                case "readeeprom":
                    tester.ReadEeprom(commandArgs.Address);
                    break;

                case "readram":
                    tester.ReadRam(commandArgs.Address);
                    break;

                case "readrom":
                    tester.ReadRom(commandArgs.Address);
                    break;

                case "readfaultcodes":
                    tester.ReadFaultCodes();
                    break;

                case "readident":
                    tester.ReadIdent();
                    break;

                case "readsoftwareversion":
                    tester.ReadSoftwareVersion();
                    break;

                case "reset":
                    tester.Reset();
                    break;

                case "setsoftwarecoding":
                    tester.SetSoftwareCoding(commandArgs.SoftwareCoding, commandArgs.WorkshopCode);
                    break;

                case "writeedc15eeprom":
                    tester.ReadWriteEdc15Eeprom(commandArgs.Filename, commandArgs.AddressValuePairs);
                    break;

                case "writeeeprom":
                    tester.WriteEeprom(commandArgs.Address, commandArgs.Value);
                    break;

                default:
                    ShowUsage();
                    break;
            }
        }

        private void DisplayInitialInfo(string[] args)
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
        }

        private static void TrySetRealTimeProcessPriority()
        {
            try
            {
                // This seems to increase the accuracy of our timing loops
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            }
            catch (Win32Exception)
            {
                // Ignore if we don't have permission to increase our priority
            }
        }

        /// <summary>
        /// Accept a series of string values in the format:
        /// ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]
        ///     ADDRESS = EEPROM address in decimal (0-511) or hex ($00-$1FF)
        ///     VALUE = Value to be stored at address in decimal (0-255) or hex ($00-$FF)
        /// </summary>
        internal static bool ParseAddressesAndValues(
            List<string> addressesAndValues,
            out List<KeyValuePair<ushort, byte>> addressValuePairs)
        {
            addressValuePairs = [];

            if (addressesAndValues.Count % 2 != 0)
                return false;

            for (var i = 0; i < addressesAndValues.Count; i += 2)
            {
                uint address;
                var valueToParse = addressesAndValues[i];
                try
                {
                    address = Utils.ParseUint(valueToParse);
                }
                catch (Exception)
                {
                    Log.WriteLine($"Invalid address (bad format): {valueToParse}.");
                    return false;
                }

                if (address > 0x1FF)
                {
                    Log.WriteLine($"Invalid address (too large): {valueToParse}.");
                    return false;
                }

                uint value;
                valueToParse = addressesAndValues[i + 1];
                try
                {
                    value = Utils.ParseUint(valueToParse);
                }
                catch (Exception)
                {
                    Log.WriteLine($"Invalid value (bad format): {valueToParse}.");
                    return false;
                }

                if (value > 0xFF)
                {
                    Log.WriteLine($"Invalid value (too large): {valueToParse}.");
                    return false;
                }

                addressValuePairs.Add(new KeyValuePair<ushort, byte>((ushort)address, (byte)value));
            }

            return true;
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

        private static void ShowUsage()
        {
            Log.WriteLine(@"
Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
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
        BasicSetting GROUP
            GROUP = Group number (0-255)
            (Group 0: Raw controller data)
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
        DumpRam START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 8192) or hex (e.g. $2000)
            LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. $10000)
            FILENAME = Optional filename
        DumpRBxMem START LENGTH [FILENAME]
            START = Start address in decimal (e.g. 66560) or hex (e.g. $10400)
            LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. $400)
            FILENAME = Optional filename
        FindLogins LOGIN
            LOGIN = Known good login (0-65535)
        GetSKC
        GroupRead GROUP
            GROUP = Group number (0-255)
            (Group 0: Raw controller data)
        LoadEeprom START FILENAME
            START = Start address in decimal (e.g. 0) or hex (e.g. $0)
            FILENAME = Name of file containing binary data to load into EEPROM
        MapEeprom
        ReadFaultCodes
        ReadIdent
        ReadEeprom ADDRESS
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
        ReadRAM ADDRESS
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
        ReadROM ADDRESS
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
        ReadSoftwareVersion
        Reset
        SetSoftwareCoding CODING WORKSHOP
            CODING = Software coding in decimal (e.g. 4361) or hex (e.g. $1109)
            WORKSHOP = Workshop code in decimal (e.g. 4361) or hex (e.g. $1109)
        ToggleRB4Mode
        WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]
            ADDRESS = EEPROM address in decimal (0-511) or hex ($00-$1FF)
            VALUE = Value to be stored at address in decimal (0-255) or hex ($00-$FF)
        WriteEeprom ADDRESS VALUE
            ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. $1109)
            VALUE = Value in decimal (e.g. 138) or hex (e.g. $8A)");

            Log.WriteLine(@"
Usage special mileage utils: KW1281Test COMMAND [arg]
    COMMAND =
        MileageDecimalToHex MILEAGE
            MILEAGE = Odometer value in decimal (e.g. 123456)
        MileageHexToDecimal MILEAGE
            MILEAGE = Odometer value in hex (e.g. FFFF FFFF FFFF FFFF FFFF FFFF FFFF FFFF)");
        }
    }
}
