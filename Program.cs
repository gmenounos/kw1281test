﻿global using static BitFab.KW1281Test.Program;

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
using BitFab.KW1281Test.EDC15;
using System.Runtime.InteropServices;
using System.IO;

[assembly: InternalsVisibleTo("BitFab.KW1281Test.Tests")]

namespace BitFab.KW1281Test;

class Program
{
    public static ILog Log { get; private set; } = new ConsoleLog();

    internal static List<string> CommandAndArgs { get; private set; } = [];

    static void Main(string[] args)
    {
        try
        {
            Log = new FileLog("KW1281Test.log");

            CommandAndArgs.Add(
                Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]));
            CommandAndArgs.AddRange(args);

            var tester = new Program();
            tester.Run(args);
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
        Log.WriteLine($"Command Line: {string.Join(' ', CommandAndArgs)}");
        Log.WriteLine($"OSVersion: {Environment.OSVersion}");
        Log.WriteLine($".NET Version: {Environment.Version}");
        Log.WriteLine($"Culture: {CultureInfo.InstalledUICulture}");

        if (args.Length < 4)
        {
            ShowUsage();
            return;
        }

        try
        {
            // This seems to increase the accuracy of our timing loops
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime;
        }
        catch(Win32Exception)
        {
            // Ignore if we don't have permission to increase our priority
        }

        string portName = args[0];
        var baudRate = int.Parse(args[1]);
        int controllerAddress = int.Parse(args[2], NumberStyles.HexNumber);
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
        var addressValuePairs = new List<KeyValuePair<ushort, byte>>();

        if (string.Compare(command, "ReadEeprom", ignoreCase: true) == 0 ||
            string.Compare(command, "ReadRAM", ignoreCase: true) == 0 ||
            string.Compare(command, "ReadROM", ignoreCase: true) == 0)
        {
            if (args.Length < 5)
            {
                ShowUsage();
                return;
            }

            address = Utils.ParseUint(args[4]);
        }
        else if (string.Compare(command, "DumpMarelliMem", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpEeprom", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpMem", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpRam", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpRBxMem", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpRBxMemOdd", ignoreCase: true) == 0 ||
                 string.Compare(command, "DumpRom", ignoreCase: true) == 0)
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
        else if (string.Compare(command, "WriteEeprom", ignoreCase: true) == 0)
        {
            if (args.Length < 6)
            {
                ShowUsage();
                return;
            }

            address = Utils.ParseUint(args[4]);
            value = (byte)Utils.ParseUint(args[5]);
        }
        else if (string.Compare(command, "LoadEeprom", ignoreCase: true) == 0)
        {
            if (args.Length < 6)
            {
                ShowUsage();
                return;
            }

            address = Utils.ParseUint(args[4]);
            _filename = args[5];
        }
        else if (string.Compare(command, "SetSoftwareCoding", ignoreCase: true) == 0)
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
        else if (string.Compare(command, "DumpEdc15Eeprom", ignoreCase: true) == 0)
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
        else if (string.Compare(command, "WriteEdc15Eeprom", ignoreCase: true) == 0)
        {
            // WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]

            if (args.Length < 6)
            {
                ShowUsage();
                return;
            }

            var dateString = DateTime.Now.ToString("s").Replace(':', '-');
            _filename = $"EDC15_EEPROM_{dateString}.bin";
            
            if (!ParseAddressesAndValues(args.Skip(4).ToList(), out addressValuePairs))
            {
                ShowUsage();
                return;
            }
        }
        else if (string.Compare(command, "AdaptationRead", ignoreCase: true) == 0)
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
            string.Compare(command, "AdaptationSave", ignoreCase: true) == 0 ||
            string.Compare(command, "AdaptationTest", ignoreCase: true) == 0)
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
            string.Compare(command, "BasicSetting", ignoreCase: true) == 0 ||
            string.Compare(command, "GroupRead", ignoreCase: true) == 0)
        {
            if (args.Length < 5)
            {
                ShowUsage();
                return;
            }

            groupNumber = byte.Parse(args[4]);
        }
        else if (
            string.Compare(command, "FindLogins", ignoreCase: true) == 0)
        {
            if (args.Length < 5)
            {
                ShowUsage();
                return;
            }

            login = ushort.Parse(args[4]);
        }

        using var @interface = OpenPort(portName, baudRate);
        var tester = new Tester(@interface, controllerAddress);
        
        switch (command.ToLower())
        {
            case "autoscan":
                AutoScan(@interface);
                return;

            case "dumprbxmem":
                tester.DumpRBxMem(address, length, _filename);
                tester.EndCommunication();
                return;

            case "dumprbxmemodd":
                tester.DumpRBxMem(address, length, _filename, evenParityWakeup: false);
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
                tester.AdaptationRead(channel, login, ecuInfo.WorkshopCode);
                break;

            case "adaptationsave":
                tester.AdaptationSave(channel, channelValue, login, ecuInfo.WorkshopCode);
                break;

            case "adaptationtest":
                tester.AdaptationTest(channel, channelValue, login, ecuInfo.WorkshopCode);
                break;

            case "basicsetting":
                tester.BasicSettingRead(groupNumber);
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
                tester.DumpCcmRom(_filename);
                break;

            case "dumpclusternecrom":
                tester.DumpClusterNecRom(_filename);
                break;

            case "dumpedc15eeprom":
            {
                var eeprom = tester.ReadWriteEdc15Eeprom(_filename);
                Edc15VM.DisplayEepromInfo(eeprom);
            }
                break;

            case "dumpeeprom":
                tester.DumpEeprom(address, length, _filename);
                break;

            case "dumpmarellimem":
                tester.DumpMarelliMem(address, length, ecuInfo, _filename);
                return;

            case "dumpmem":
                tester.DumpMem(address, length, _filename);
                break;

            case "dumpram":
                tester.DumpRam(address, length, _filename);
                break;

            case "dumprom":
                tester.DumpRom(address, length, _filename);
                break;

            case "findlogins":
                tester.FindLogins(login!.Value, ecuInfo.WorkshopCode);
                break;

            case "getclusterid":
                tester.GetClusterId();
                break;

            case "groupread":
                tester.GroupRead(groupNumber);
                break;

            case "loadeeprom":
                tester.LoadEeprom(address, _filename!);
                break;

            case "mapeeprom":
                tester.MapEeprom(_filename);
                break;

            case "readeeprom":
                tester.ReadEeprom(address);
                break;

            case "readram":
                tester.ReadRam(address);
                break;

            case "readrom":
                tester.ReadRom(address);
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
                tester.SetSoftwareCoding(softwareCoding, workshopCode);
                break;

            case "writeedc15eeprom":
                tester.ReadWriteEdc15Eeprom(_filename, addressValuePairs);
                break;

            case "writeeeprom":
                tester.WriteEeprom(address, value);
                break;

            default:
                ShowUsage();
                break;
        }

        tester.EndCommunication();
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

    private static void ShowUsage()
    {
        Log.WriteLine("""
Usage: KW1281Test PORT BAUD ADDRESS COMMAND [args]
                
PORT = COM1|COM2|etc. (Windows)
    /dev/ttyXXXX (Linux)
    AABBCCDD (macOS/Linux FTDI cable serial number)
BAUD = 10400|9600|etc.
ADDRESS = Controller address, e.g. 1 (ECU), 17 (cluster), 46 (CCM), 56 (radio)
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
    AutoScan
    BasicSetting GROUP
        GROUP = Group number (0-255)
        (Group 0: Raw controller data)
    ClarionVWPremium4SafeCode
    ClearFaultCodes
    DelcoVWPremium5SafeCode
    DumpEdc15Eeprom [FILENAME]
        FILENAME = Optional filename
    DumpEeprom START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 0) or hex (e.g. 0x0)
        LENGTH = Number of bytes in decimal (e.g. 2048) or hex (e.g. 0x800)
        FILENAME = Optional filename
    DumpMarelliMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 3072) or hex (e.g. 0xC00)
        LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. 0x400)
        FILENAME = Optional filename
    DumpMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    DumpRam START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    DumpRBxMem START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 66560) or hex (e.g. 0x10400)
        LENGTH = Number of bytes in decimal (e.g. 1024) or hex (e.g. 0x400)
        FILENAME = Optional filename
    DumpRom START LENGTH [FILENAME]
        START = Start address in decimal (e.g. 8192) or hex (e.g. 0x2000)
        LENGTH = Number of bytes in decimal (e.g. 65536) or hex (e.g. 0x10000)
        FILENAME = Optional filename
    FindLogins LOGIN
        LOGIN = Known good login (0-65535)
    GetSKC
    GroupRead GROUP
        GROUP = Group number (0-255)
        (Group 0: Raw controller data)
    LoadEeprom START FILENAME
        START = Start address in decimal (e.g. 0) or hex (e.g. 0x0)
        FILENAME = Name of file containing binary data to load into EEPROM
    MapEeprom
    ReadFaultCodes
    ReadIdent
    ReadEeprom ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadRAM ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadROM ADDRESS
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ReadSoftwareVersion
    Reset
    SetSoftwareCoding CODING WORKSHOP
        CODING = Software coding in decimal (e.g. 4361) or hex (e.g. 0x1109)
        WORKSHOP = Workshop code in decimal (e.g. 4361) or hex (e.g. 0x1109)
    ToggleRB4Mode
    WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ... ADDRESSn VALUEn]
        ADDRESS = EEPROM address in decimal (0-511) or hex (0x00-0x1FF)
        VALUE = Value to be stored in decimal (0-255) or hex (0x00-0xFF)
    WriteEeprom ADDRESS VALUE
        ADDRESS = Address in decimal (e.g. 4361) or hex (e.g. 0x1109)
        VALUE = Value in decimal (e.g. 138) or hex (e.g. 0x8A)
""");
    }

    private string? _filename = null;
}
