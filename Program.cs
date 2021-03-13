using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Interface;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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

            if (args.Length < 4)
            {
                ShowUsage();
                return;
            }

            string portName = args[0];
            var baudRate = int.Parse(args[1]);
            _controllerAddress = int.Parse(args[2], NumberStyles.HexNumber);
            var command = args[3];
            uint address = 0;
            uint length = 0;
            byte value = 0;
            bool evenParityWakeup = false;
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

            using var @interface = OpenPort(portName, baudRate);

            _kwpCommon = new KwpCommon(@interface);

            Logger.WriteLine("Sending wakeup message");
            var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParityWakeup);

            IKW1281Dialog kwp1281 = null;
            KW2000Dialog kwp2000 = null;
            ControllerInfo ecuInfo = null;
            if (kwpVersion == 1281)
            {
                kwp1281 = new KW1281Dialog(_kwpCommon);

                ecuInfo = kwp1281.ReadEcuInfo();
                Logger.WriteLine($"ECU: {ecuInfo}");
            }
            else
            {
                kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);
            }

            switch (command.ToLower())
            {
                case "actuatortest":
                    ActuatorTest(kwp1281);
                    break;

                case "clearfaultcodes":
                    ClearFaultCodes(kwp1281);
                    break;

                case "delcovwpremium5safecode":
                    DelcoVWPremium5SafeCode(kwp1281);
                    break;

                case "dumpccmrom":
                    DumpCcmRom(kwp1281);
                    break;

                case "dumpclusternecrom":
                    DumpClusterNecRom(kwp1281);
                    break;

                case "dumpeeprom":
                    DumpEeprom(kwp1281, address, length);
                    break;

                case "dumpmarellimem":
                    DumpMarelliMem(kwp1281, ecuInfo, (ushort)address, (ushort)length);
                    return;

                case "dumpmem":
                    DumpMem(kwp1281, address, length);
                    break;

                case "dumprb8eeprom":
                    DumpRB8Eeprom(kwp2000, address, length, kwpVersion);
                    break;

                case "loadeeprom":
                    LoadEeprom(kwp1281, address);
                    break;

                case "mapeeprom":
                    MapEeprom(kwp1281);
                    break;

                case "readeeprom":
                    ReadEeprom(kwp1281, address);
                    break;

                case "readfaultcodes":
                    ReadFaultCodes(kwp1281);
                    break;

                case "readident":
                    ReadIdent(kwp1281);
                    break;

                case "readsoftwareversion":
                    ReadSoftwareVersion(kwp1281);
                    break;

                case "reset":
                    Reset(kwp1281);
                    break;

                case "setsoftwarecoding":
                    SetSoftwareCoding(kwp1281, softwareCoding, workshopCode);
                    break;

                case "writeeeprom":
                    WriteEeprom(kwp1281, address, value);
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

        private void DumpMarelliMem(
            IKW1281Dialog kwp1281, ControllerInfo ecuInfo, ushort address, ushort count)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for clusters");
                return;
            }

            byte entryH; // High byte of code entry point
            byte regBlockH; // High byte of register block

            if (ecuInfo.Text.Contains("M73 V07"))
            {
                entryH = 0x02;
                regBlockH = 0x08;
            }
            else if (
                ecuInfo.Text.Contains("M73 V08") ||
                ecuInfo.Text.Contains("M73 D09") || // Audi TT 8N2920980A
                ecuInfo.Text.Contains("M73 D14")) // Audi TT 8N2920980A
            {
                entryH = 0x18;
                regBlockH = 0x20;
            }
            else
            {
                Logger.WriteLine("Unsupported cluster software version");
                return;
            }

            Logger.WriteLine("Sending block 0x6C");
            kwp1281.SendBlock(new List<byte> { 0x6C });

            Thread.Sleep(250);

            Logger.WriteLine("Writing data to cluster microcontroller");
            var data = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x50, 0x34,
                entryH, 0x00, // Entry point $xx00
            };
            if (!WriteMarelliBlockAndReadAck(data))
            {
                return;
            }

            // Now we write a small memory dump program to the 68HC12 processor

            Logger.WriteLine("Writing memory dump program to cluster microcontroller");

            var startH = (byte)(address / 256);
            var startL = (byte)(address % 256);

            var end = address + count;
            var endH = (byte)(end / 256);
            var endL = (byte)(end % 256);

            var program = new byte[]
            {
                entryH, 0x00, // Address $xx00 

                0x14, 0x50,                     // orcc #$50
                0x07, 0x32,                     // bsr FeedWatchdog

                // Set baud rate to 9600
                0xC7,                           // clrb
                0x7B, regBlockH, 0xC8,          // stab $xxC8   ; SC1BDH
                0xC6, 0x34,                     // ldab #$34
                0x7B, regBlockH, 0xC9,          // stab $xxC9   ; SC1BDL

                // Enable transmit, disable UART interrupts
                0xC6, 0x08,                     // ldab #$08
                0x7B, regBlockH, 0xCB,          // stab $xxCB   ; SC1CR2

                0xCE, startH, startL,           // ldx #start
                // SendLoop:
                0xA6, 0x30,                     // ldaa 1,X+
                0x07, 0x0F,                     // bsr SendByte
                0x8E, endH, endL,               // cpx #end
                0x26, 0xF7,                     // bne SendLoop
                // Poison the watchdog to force a reboot
                0xCC, 0x11, 0x11,               // ldd #$1111
                0x7B, regBlockH, 0x17,          // stab $xx17   ; COPRST
                0x7A, regBlockH, 0x17,          // staa $xx17   ; COPRST
                0x3D,                           // rts

                // SendByte:
                0xF6, regBlockH, 0xCC,          // ldab $xxCC   ; SC1SR1
                0x7A, regBlockH, 0xCF,          // staa $xxCF   ; SC1DRL
                // TxBusy:
                0x07, 0x06,                     // bsr FeedWatchdog
                // Loop until TC (Transmit Complete) bit is set
                0x1F, regBlockH, 0xCC, 0x40, 0xF9,   // brclr $xxCC,$40,TxBusy   ; SC1SR1
                0x3D,                           // rts

                // FeedWatchdog:
                0xCC, 0x55, 0xAA,               // ldd #$55AA
                0x7B, regBlockH, 0x17,          // stab $xx17   ; COPRST
                0x7A, regBlockH, 0x17,          // staa $xx17   ; COPRST
                0x3D,                           // rts
            };
            if (!WriteMarelliBlockAndReadAck(program))
            {
                return;
            }

            Logger.WriteLine("Receiving memory dump");

            var mem = new List<byte>();
            for (int i = 0; i < count; i++)
            {
                var b = _kwpCommon.ReadByte();
                mem.Add(b);
            }

            var dumpFileName = _filename ?? $"marelli_mem_${address:X4}.bin";

            File.WriteAllBytes(dumpFileName, mem.ToArray());
            Logger.WriteLine($"Saved memory dump to {dumpFileName}");

            Logger.WriteLine("Done");
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

        private void DumpRB8Eeprom(KW2000Dialog kwp2000, uint address, uint length, int kwpVersion)
        {
            if (_controllerAddress != (int)ControllerAddress.Cluster)
            {
                Logger.WriteLine("Only supported for cluster (address 17)");
                return;
            }

            if (kwpVersion < 2000)
            {
                Logger.WriteLine($"Cluster protocol is KWP{kwpVersion} but needs to be KWP2xxx");
                return;
            }

            var dumpFileName = _filename ?? $"RB8_${address:X6}_eeprom.bin";

            kwp2000.SecurityAccess(0xFB);
            kwp2000.DumpEeprom(address, length, dumpFileName);
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

        private bool WriteMarelliBlockAndReadAck(byte[] data)
        {
            var count = (ushort)(data.Length + 2); // Count includes 2-byte checksum
            var countH = (byte)(count / 256);
            var countL = (byte)(count % 256);
            _kwpCommon.WriteByte(countH);
            _kwpCommon.WriteByte(countL);

            var sum = (ushort)(countH + countL);
            foreach (var b in data)
            {
                _kwpCommon.WriteByte(b);
                sum += b;
            }
            _kwpCommon.WriteByte((byte)(sum / 256));
            _kwpCommon.WriteByte((byte)(sum % 256));

            var expectedAck = new byte[] { 0x03, 0x09, 0x00, 0x0C };

            Logger.WriteLine("Receiving ACK");
            var ack = new List<byte>();
            for (int i = 0; i < 4; i++)
            {
                var b = _kwpCommon.ReadByte();
                ack.Add(b);
            }
            if (!ack.SequenceEqual(expectedAck))
            {
                Logger.WriteLine($"Expected ACK but received {Utils.Dump(ack)}");
                return false;
            }

            return true;
        }

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
            var identInfo = kwp1281.ReadIdent().First().ToString().Replace(' ', '_').Replace(":", "");

            UnlockControllerForEepromReadWrite(kwp1281);

            var dumpFileName = _filename ?? $"{identInfo}_${startAddress:X4}_eeprom.bin";

            Logger.WriteLine($"Saving EEPROM dump to {dumpFileName}");
            DumpEeprom(kwp1281, startAddress, length, maxReadLength: 16, dumpFileName);
            Logger.WriteLine($"Saved EEPROM dump to {dumpFileName}");
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
        DumpClusterNecRom
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
    }
}
