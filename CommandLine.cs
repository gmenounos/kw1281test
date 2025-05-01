using System.Collections.Generic;
using CommandLine;

namespace BitFab.KW1281Test
{
    // Base options for commands that require port, baud, and address
    public class CommonOptions
    {
        [Value(0, MetaName = "port", Required = true, HelpText = "COM1|COM2|etc. (Windows), /dev/ttyXXXX (Linux), AABBCCDD (macOS/Linux FTDI cable serial number)")]
        public required string PortName { get; set; }

        [Value(1, MetaName = "baud", Required = true, HelpText = "Baud rate, e.g., 10400, 9600, etc.")]
        public string? BaudRate { get; set; }

        [Value(2, MetaName = "unit", Required = true, HelpText = "Unit/Controller address, e.g., 1 (ECU), 17 (cluster), 46 (CCM), 56 (radio)")]
        public string? ControllerAddress { get; set; }

        [Option('s', "silent", Required = false, Hidden = true)]
        public bool Silent { get; set; }
    }

    public class CommandLineOptions
    {
        // Verbs for each command
        [Verb("ActuatorTest", HelpText = "Runs an actuator test on the given controller.")]
        public class ActuatorTestOptions : CommonOptions
        {
        }

        [Verb("AdaptationRead", HelpText = "Reads the adaptation from the given channel of the controller.")]
        public class AdaptationReadOptions : CommonOptions
        {
            [Value(3, MetaName = "channel", Required = true, HelpText = "Channel number (0-99)")]
            public required string Channel { get; set; }

            [Value(4, MetaName = "login", Required = false, HelpText = "Optional login code (0-65535)")]
            public string? Login { get; set; }
        }

        [Verb("AdaptationSave", HelpText = "Saves the adaptation value to the specified channel of the controller.")]
        public class AdaptationSaveOptions : CommonOptions
        {
            [Value(3, MetaName = "channel", Required = true, HelpText = "Channel number (0-99)")]
            public required string Channel { get; set; }

            [Value(4, MetaName = "value", Required = true, HelpText = "Channel value (0-65535)")]
            public required string Value { get; set; }

            [Value(5, MetaName = "login", Required = false, HelpText = "Optional login code (0-65535)")]
            public string? Login { get; set; }
        }

        [Verb("AdaptationTest", HelpText = "Tests an adaptation value in the specified channel of the controller.")]
        public class AdaptationTestOptions : CommonOptions
        {
            [Value(3, MetaName = "channel", Required = true, HelpText = "Channel number (0-99)")]
            public required string Channel { get; set; }

            [Value(4, MetaName = "value", Required = true, HelpText = "Channel value (0-65535)")]
            public required string Value { get; set; }

            [Value(5, MetaName = "login", Required = false, HelpText = "Optional login code (0-65535)")]
            public string? Login { get; set; }
        }

        [Verb("AutoScan", HelpText = "Scans for all available controllers automatically.")]
        public class AutoScanOptions
        {
            [Value(0, MetaName = "port", Required = true, HelpText = "COM1|COM2|etc. (Windows), /dev/ttyXXXX (Linux), AABBCCDD (macOS/Linux FTDI cable serial number)")]
            public required string Port { get; set; }

            [Option('s', "silent", Required = false, Hidden = true)]
            public bool Silent { get; set; }
        }

        [Verb("BasicSetting", HelpText = "Activates basic setting mode for the specified group on the controller.")]
        public class BasicSettingOptions : CommonOptions
        {
            [Value(3, MetaName = "group", Required = true, HelpText = "Group number (0-255). Group 0: Raw controller data")]
            public required string Group { get; set; }
        }

        [Verb("ClarionVWPremium4SafeCode", HelpText = "Retrieves the safe code for a Clarion VW Premium 4 radio.")]
        public class ClarionVWPremium4SafeCodeOptions : CommonOptions
        {
        }

        [Verb("ClearFaultCodes", HelpText = "Clears all fault codes stored in the controller.")]
        public class ClearFaultCodesOptions : CommonOptions
        {
        }

        [Verb("DelcoVWPremium5SafeCode", HelpText = "Retrieves the safe code for a Delco VW Premium 5 radio.")]
        public class DelcoVWPremium5SafeCodeOptions : CommonOptions
        {
        }

        [Verb("DumpCcmRom", HelpText = "Dumps the ROM of the CCM to a file or output.", Hidden = true)]
        public class DumpCcmRomOptions : CommonOptions
        {
            [Value(3, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpClusterNecRom", HelpText = "Dumps the NEC ROM of the cluster to a file or output.", Hidden = true)]
        public class DumpClusterNecRomOptions : CommonOptions
        {
            [Value(3, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpEdc15Eeprom", HelpText = "Dumps the EEPROM contents of an EDC15 controller to a file or output.")]
        public class DumpEdc15EepromOptions : CommonOptions
        {
            [Value(3, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpEeprom", HelpText = "Dumps a specified range of EEPROM contents to a file or output.")]
        public class DumpEepromOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 0) or hex (e.g., $0)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 2048) or hex (e.g., $800)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpMarelliMem", HelpText = "Dumps a specified range of Marelli memory contents to a file or output.")]
        public class DumpMarelliMemOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 3072) or hex (e.g., $C00)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 1024) or hex (e.g., $400)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpMem", HelpText = "Dumps a specified range of memory contents to a file or output.")]
        public class DumpMemOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 8192) or hex (e.g., $2000)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 65536) or hex (e.g., $10000)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpRam", HelpText = "Dumps a specified range of ram contents to a file or output.")]
        public class DumpRamOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 8192) or hex (e.g., $2000)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 65536) or hex (e.g., $10000)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpRBxMem", HelpText = "Dumps a specified range of RBx memory contents to a file or output.")]
        public class DumpRBxMemOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 66560) or hex (e.g., $10400)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 1024) or hex (e.g., $400)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("DumpRBxMemOdd", HelpText = "Dumps a specified range of RBx memory contents to a file or output, using odd parity.", Hidden = true)]
        public class DumpRBxMemOddOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 66560) or hex (e.g., $10400)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "length", Required = true, HelpText = "Number of bytes in decimal (e.g., 1024) or hex (e.g., $400)")]
            public required string Length { get; set; }

            [Value(5, MetaName = "filename", Required = false, HelpText = "Optional filename for output")]
            public string? Filename { get; set; }
        }

        [Verb("FindLogins", HelpText = "Finds logins, using a known good login.")]
        public class FindLoginsOptions : CommonOptions
        {
            [Value(3, MetaName = "login", Required = true, HelpText = "Optional login code (0-65535)")]
            public required string Login { get; set; }
        }

        [Verb("GetClusterId", HelpText = "Get's the ID of the Cluster.", Hidden = true)]
        public class GetClusterIdOptions : CommonOptions
        {
        }

        [Verb("GetSKC", HelpText = "Retrieves the SKC (Secret Key Code) from the controller.")]
        public class GetSKCOptions : CommonOptions
        {
        }

        [Verb("GroupRead", HelpText = "Reads data from the specified group on the controller.")]
        public class GroupReadOptions : CommonOptions
        {
            [Value(3, MetaName = "group", Required = true, HelpText = "Group number (0-255). Group 0: Raw controller data")]
            public required string Group { get; set; }
        }

        [Verb("LoadEeprom", HelpText = "Loads binary data from a file into the EEPROM at the specified start address.")]
        public class LoadEepromOptions : CommonOptions
        {
            [Value(3, MetaName = "start", Required = true, HelpText = "Start address in decimal (e.g., 0) or hex (e.g., $0)")]
            public required string Start { get; set; }

            [Value(4, MetaName = "filename", Required = true, HelpText = "Name of file containing binary data to load into EEPROM")]
            public required string Filename { get; set; }
        }

        [Verb("MapEeprom", HelpText = "Maps the entire EEPROM contents of the controller.")]
        public class MapEepromOptions : CommonOptions
        {
            [Value(3, MetaName = "filename", Required = false, HelpText = "Name of file containing map data of the EEPROM")]
            public string? Filename { get; set; }
        }

        [Verb("ReadEeprom", HelpText = "Reads the value stored at a specific EEPROM address.")]
        public class ReadEepromOptions : CommonOptions
        {
            [Value(3, MetaName = "address", Required = true, HelpText = "Address in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Address { get; set; }
        }

        [Verb("ReadFaultCodes", HelpText = "Reads and displays all fault codes stored in the controller.")]
        public class ReadFaultCodesOptions : CommonOptions
        {
        }

        [Verb("ReadIdent", HelpText = "Reads the identification data from the controller.")]
        public class ReadIdentOptions : CommonOptions
        {
        }

        [Verb("ReadRam", HelpText = "Reads the value stored at a specific RAM address.")]
        public class ReadRamOptions : CommonOptions
        {
            [Value(3, MetaName = "address", Required = true, HelpText = "Address in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Address { get; set; }
        }

        [Verb("ReadRom", HelpText = "Reads the value stored at a specific ROM address.")]
        public class ReadRomOptions : CommonOptions
        {
            [Value(3, MetaName = "address", Required = true, HelpText = "Address in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Address { get; set; }
        }

        [Verb("ReadSoftwareVersion", HelpText = "Reads the software version of the controller.")]
        public class ReadSoftwareVersionOptions : CommonOptions
        {
        }

        [Verb("Reset", HelpText = "Restarts / Resets the controller.")]
        public class ResetOptions : CommonOptions
        {
        }

        [Verb("SetSoftwareCoding", HelpText = "Sets the software coding and workshop code for the controller.")]
        public class SetSoftwareCodingOptions : CommonOptions
        {
            [Value(3, MetaName = "coding", Required = true, HelpText = "Software coding in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Coding { get; set; }

            [Value(4, MetaName = "workshop", Required = true, HelpText = "Workshop code in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Workshop { get; set; }
        }

        [Verb("ToggleRB4Mode", HelpText = "Switches the cluster between New Mode (Mode 4) and Adapted Mode (Mode 6).")]
        public class ToggleRB4ModeOptions : CommonOptions
        {
        }

        [Verb("WriteEdc15Eeprom", HelpText = "Writes one or more values to specified addresses in an EDC15 EEPROM.")]
        public class WriteEdc15EepromOptions : CommonOptions
        {
            [Value(3, MetaName = "address-value-pairs", Required = true, HelpText = "EEPROM address-value pairs in decimal (0-511) or hex ($00-$1FF) seperated with space.")]
            public required List<string> AddressValuePairs { get; set; }

            [Option('f', "filename", Required = false, Hidden = true)]
            public string? Filename { get; set; }
        }

        [Verb("WriteEeprom", HelpText = "Writes a value to a specific EEPROM address.")]
        public class WriteEepromOptions : CommonOptions
        {
            [Value(3, MetaName = "address", Required = true, HelpText = "Address in decimal (e.g., 4361) or hex (e.g., $1109)")]
            public required string Address { get; set; }

            [Value(4, MetaName = "value", Required = true, HelpText = "Value in decimal (e.g., 138) or hex (e.g., $8A)")]
            public required string Value { get; set; }
        }
    }
}