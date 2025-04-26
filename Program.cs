global using static BitFab.KW1281Test.Program;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using BitFab.KW1281Test.Logging;
using CommandLine;
using CommandLine.Text;
using static BitFab.KW1281Test.CommandLineOptions;

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
                tester.Parse(args);
            }
            catch (UnableToProceedException)
            {
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

        void Parse(string[] args)
        {
            // Get all verb types dynamically from nested classes
            var verbTypes = typeof(CommandLineOptions)
                .GetNestedTypes(BindingFlags.Public)
                .Where(t => t.GetCustomAttribute<VerbAttribute>() != null)
                .ToArray();

            // Configure the parser
            var parser = new Parser(with =>
            {
                with.CaseSensitive = false;
                with.EnableDashDash = true;
                with.HelpWriter = null;
            });

            // Reorder arguments: make sure verb is first if present
            var reorderedArgs = args
                .OrderBy(arg => !verbTypes
                    .Any(t => t.GetCustomAttribute<VerbAttribute>()?.Name.Equals(arg, StringComparison.OrdinalIgnoreCase) == true))
                .ToArray();

            // Parse the arguments
            var parserResult = parser.ParseArguments(reorderedArgs, verbTypes);
            parserResult
                .WithParsed(Run)
                .WithNotParsed(errors => DisplayHelp(parserResult, errors));
        }

        static void DisplayHelp<T>(ParserResult<T> result, IEnumerable<Error> errors)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
            var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2025 Greg Menounos";
            var appName = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "KW1281Test";

            HelpText helpText;

            if (errors.IsVersion())
            {
                Log.WriteLine($"\u001b[32m{appName}: Yesterday's diagnostics... Today.\u001b[0m");
                Log.WriteLine(copyright);
                Log.WriteLine($"Version: {version} (https://github.com/gmenounos/kw1281test/releases)");
                Log.WriteLine($"OS Version: {Environment.OSVersion}");
                Log.WriteLine($".NET Version: {Environment.Version}");
                Log.WriteLine($"Culture: {CultureInfo.InstalledUICulture}");
                return;
            } else {
                helpText = HelpText.AutoBuild(result, h =>
                {
                    h.Heading = $"\u001b[32m{appName}: Yesterday's diagnostics... Today.\u001b[0m";
                    h.Copyright = copyright;
                    h.AddPreOptionsLine($"Version: {version} (https://github.com/gmenounos/kw1281test/releases)");
                    h.AddPreOptionsLine("");
                    h.AddPreOptionsLine("Common options (required unless otherwise specified):");
                    h.AddPreOptionsLine("");

                    foreach (var prop in typeof(CommonOptions).GetProperties())
                    {
                        if (prop.GetCustomAttributes(typeof(OptionAttribute), true).FirstOrDefault() is OptionAttribute optionAttr)
                        {
                            if (optionAttr.Hidden)
                                continue;

                            var shortName = !string.IsNullOrEmpty(optionAttr.ShortName) ? $"-{optionAttr.ShortName}, " : "";
                            var longName = $"--{optionAttr.LongName}";
                            var required = optionAttr.Required ? " (required)" : "";
                            h.AddPreOptionsLine($"  {shortName}{longName}\t{optionAttr.HelpText}{required}");
                            h.AddPreOptionsLine("");
                        }
                    }

                    h.AddPreOptionsLine("Verbs / Options:");
                    return h;
                }, e => e);

                Log.WriteLine(helpText);
            }
        }

        void Run(object options)
        {
            string portName = null;
            int baudRate = 0;
            int controllerAddress = 0;
            var addressValuePairs = new List<KeyValuePair<ushort, byte>>();
            bool silent = false;
            bool abort = false;

            // Check if the command inherits from CommonOptions
            if (options is CommonOptions commonOptions)
            {
                portName = commonOptions.Port;
                baudRate = int.Parse(commonOptions.Baud);
                controllerAddress = int.Parse(commonOptions.ControllerAddress, NumberStyles.HexNumber);
                silent = commonOptions.Silent;
            }
            else if (options is AutoScanOptions autoScanOptions)
            {
                portName = autoScanOptions.Port;
                silent = autoScanOptions.Silent;
            }
            else
            {
                Log.WriteLine("Invalid command. Use --help for usage information.");
                return;
            }

            // Skip the startup message when the application is silent
            if (!silent)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("KW1281Test: Yesterday's diagnostics...");
                Thread.Sleep(2000);
                Console.WriteLine("Today.");
                Thread.Sleep(2000);
                Console.ResetColor();
                Console.WriteLine();
            }

            var version = GetType().GetTypeInfo().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "Unknown";
            Log.WriteLine($"Version {version} (https://github.com/gmenounos/kw1281test/releases)");
            Log.WriteLine($"Command Line: {string.Join(" ", Environment.GetCommandLineArgs())}");
            Log.WriteLine($"OSVersion: {Environment.OSVersion}");
            Log.WriteLine($".NET Version: {Environment.Version}");
            Log.WriteLine($"Culture: {CultureInfo.InstalledUICulture}");

            try
            {
                // This seems to increase the accuracy of our timing loops
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
            }
            catch (Win32Exception)
            {
                // Ignore if we don't have permission to increase our priority
            }

            // Create tester instance
            using var @interface = OpenPort(portName, baudRate);
            var tester = new Tester(@interface, controllerAddress);

            // Get ControllerInfo
            ControllerInfo ecuInfo = tester.Kwp1281Wakeup();

            // Dispatch to the appropriate command
            switch (options)
            {
                case ActuatorTestOptions _:
                    tester.ActuatorTest();
                    break;

                case AdaptationReadOptions args:
                    tester.AdaptationRead(byte.Parse(args.Channel), ushort.Parse(args.Login), ecuInfo.WorkshopCode);
                    break;

                case AdaptationSaveOptions args:
                    tester.AdaptationSave(byte.Parse(args.Channel), ushort.Parse(args.Value), ushort.Parse(args.Login), ecuInfo.WorkshopCode);
                    break;

                case AdaptationTestOptions args:
                    tester.AdaptationTest(byte.Parse(args.Channel), ushort.Parse(args.Value), ushort.Parse(args.Login), ecuInfo.WorkshopCode);
                    break;

                case AutoScanOptions _:
                    AutoScan(@interface);
                    break;

                case BasicSettingOptions args:
                    tester.BasicSettingRead(byte.Parse(args.Group));
                    break;

                case ClarionVWPremium4SafeCodeOptions _:
                    tester.ClarionVWPremium4SafeCode();
                    break;

                case ClearFaultCodesOptions _:
                    tester.ClearFaultCodes();
                    break;

                case DelcoVWPremium5SafeCodeOptions _:
                    tester.DelcoVWPremium5SafeCode();
                    break;

                case DumpCcmRomOptions args:
                    tester.DumpCcmRom(args.Filename);
                    break;

                case DumpClusterNecRomOptions args:
                    tester.DumpClusterNecRom(args.Filename);
                    break;

                case DumpEdc15EepromOptions args:
                    {
                        var eeprom = tester.ReadWriteEdc15Eeprom(args.Filename);
                        Edc15VM.DisplayEepromInfo(eeprom);
                    }
                    break;

                case DumpEepromOptions args:
                    tester.DumpEeprom(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), args.Filename);
                    break;

                case DumpMarelliMemOptions args:
                    tester.DumpMarelliMem(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), ecuInfo, args.Filename);
                    break;

                case DumpMemOptions args:
                    tester.DumpMem(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), args.Filename);
                    break;

                case DumpRamOptions args:
                    tester.DumpRam(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), args.Filename);
                    break;

                case DumpRBxMemOddOptions args:
                    tester.DumpRBxMem(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), args.Filename, evenParityWakeup: false);
                    break;

                case DumpRBxMemOptions args:
                    tester.DumpRBxMem(Utils.ParseUint(args.Start), Utils.ParseUint(args.Length), args.Filename);
                    break;

                case FindLoginsOptions args:
                    tester.FindLogins(ushort.Parse(args.Login), ecuInfo.WorkshopCode);
                    break;

                case GetClusterIdOptions args:
                    tester.GetClusterId();
                    break;

                case GetSKCOptions _:
                    tester.GetSkc();
                    break;

                case GroupReadOptions args:
                    tester.GroupRead(byte.Parse(args.Group));
                    break;

                case LoadEepromOptions args:
                    tester.LoadEeprom(Utils.ParseUint(args.Start), args.Filename);
                    break;

                case MapEepromOptions args:
                    tester.MapEeprom(args.Filename);
                    break;

                case ReadEepromOptions args:
                    tester.ReadEeprom(Utils.ParseUint(args.Address));
                    break;

                case ReadFaultCodesOptions _:
                    tester.ReadFaultCodes();
                    break;

                case ReadIdentOptions _:
                    tester.ReadIdent();
                    break;

                case ReadRamOptions args:
                    tester.ReadRam(Utils.ParseUint(args.Address));
                    break;

                case ReadRomOptions args:
                    tester.ReadRom(Utils.ParseUint(args.Address));
                    break;

                case ReadSoftwareVersionOptions _:
                    tester.ReadSoftwareVersion();
                    break;

                case ResetOptions _:
                    tester.Reset();
                    break;

                case SetSoftwareCodingOptions args:
                    if ((int)Utils.ParseUint(args.Coding) > 32767)
                    {
                        Log.WriteLine("SoftwareCoding cannot be greater than 32767.");
                        abort = true;
                        return;
                    }
                    if ((int)Utils.ParseUint(args.Workshop) > 99999)
                    {
                        Log.WriteLine("WorkshopCode cannot be greater than 99999.");
                        abort = true;
                        return;
                    }
                    tester.SetSoftwareCoding((int)Utils.ParseUint(args.Coding), (int)Utils.ParseUint(args.Workshop));
                    break;

                case ToggleRB4ModeOptions _:
                    tester.ToggleRB4Mode();
                    break;

                case WriteEdc15EepromOptions args:
                    if (args.Filename == null)
                    {
                        var dateString = DateTime.Now.ToString("s").Replace(':', '-');
                        args.Filename = $"EDC15_EEPROM_{dateString}.bin";
                    }

                    if (!ParseAddressesAndValues(args.AddressValuePairs, out addressValuePairs))
                    {
                        return;
                    }

                    tester.ReadWriteEdc15Eeprom(args.Filename, addressValuePairs);
                    break;

                case WriteEepromOptions args:
                    tester.WriteEeprom(Utils.ParseUint(args.Address), (byte)Utils.ParseUint(args.Value));
                    break;

                default:
                    Log.WriteLine("Unknown command. Use --help for usage information.");
                    return;
            }

            if (!abort)
            {
                tester.EndCommunication();
            }
        }

        private static void AutoScan(IInterface @interface)
        {
            var kwp1281Addresses = new List<string>();
            var kwp2000Addresses = new List<string>();
            foreach (var evenParity in new bool[] { false, true })
            {
                var parity = evenParity ? "(EvenParity)" : "";
                for (var address = 0; address < 0x80; address++)
                {
                    var tester = new Tester(@interface, address);
                    try
                    {
                        Log.WriteLine($"Attempting to wake up controller at address {address:X}{parity}...");
                        tester.Kwp1281Wakeup(evenParity, failQuietly: true);
                        tester.EndCommunication();
                        kwp1281Addresses.Add($"{address:X}{parity}");
                    }
                    catch (UnableToProceedException)
                    {
                    }
                    catch (UnexpectedProtocolException)
                    {
                        kwp2000Addresses.Add($"{address:X}{parity}");
                    }
                }
            }

            Log.WriteLine($"AutoScan Results:");
            Log.WriteLine($"KWP1281: {string.Join(' ', kwp1281Addresses)}");
            Log.WriteLine($"KWP2000: {string.Join(' ', kwp2000Addresses)}");
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
            {
                return false;
            }

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
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                portName.StartsWith("/dev/", StringComparison.CurrentCultureIgnoreCase))
            {
                Log.WriteLine($"Opening Linux serial port {portName}");
                return new LinuxInterface(portName, baudRate);
            }
            else
            {
                Log.WriteLine($"Opening Generic serial port {portName}");
                return new GenericInterface(portName, baudRate);
            }
        }
    }
}