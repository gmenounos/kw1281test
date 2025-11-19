using BitFab.KW1281Test.Cluster;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BitFab.KW1281Test;

internal class Tester
{
    private readonly IKwpCommon _kwpCommon;
    private readonly IKW1281Dialog _kwp1281;
    private readonly int _controllerAddress;


    public Tester(IInterface @interface, int controllerAddress)
    {
        _kwpCommon = new KwpCommon(@interface);
        _kwp1281 = new KW1281Dialog(_kwpCommon);
        _controllerAddress = controllerAddress;
    }

    public ControllerInfo Kwp1281Wakeup(bool evenParityWakeup = false, bool failQuietly = false)
    {
        Log.WriteLine("Sending wakeup message");

        var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParityWakeup, failQuietly);

        if (kwpVersion != 1281)
        {
            throw new UnexpectedProtocolException("Expected KWP1281 protocol.");
        }

        var ecuInfo = _kwp1281.Connect();
        Log.WriteLine($"ECU: {ecuInfo}");
        return ecuInfo;
    }

    public KW2000Dialog Kwp2000Wakeup(bool evenParityWakeup = false)
    {
        Log.WriteLine("Sending wakeup message");

        var kwpVersion = _kwpCommon!.WakeUp((byte)_controllerAddress, evenParityWakeup);

        if (kwpVersion == 1281)
        {
            throw new UnexpectedProtocolException("Expected KWP2000 protocol.");
        }

        var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

        return kwp2000;
    }

    public void EndCommunication()
    {
        _kwp1281.EndCommunication();
    }

    // Begin top-level commands

    public void ActuatorTest()
    {
        using KW1281KeepAlive keepAlive = new(_kwp1281);

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

    public void AdaptationRead(
        byte channel,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationRead(channel);
    }

    public void AdaptationSave(
        byte channel, ushort channelValue,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationSave(channel, channelValue, workshopCode);
    }

    public void AdaptationTest(
        byte channel, ushort channelValue,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationTest(channel, channelValue);
    }

    public void BasicSettingRead(byte groupNumber)
    {
        var succeeded = _kwp1281.GroupRead(groupNumber, useBasicSetting: true);
    }

    public void ClarionVWPremium4SafeCode()
    {
        if (_controllerAddress != (int)ControllerAddress.Radio)
        {
            Log.WriteLine("Only supported for radio address 56");
            return;
        }

        // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
        const byte readWriteSafeCode = 0xF0;
        const byte read = 0x00;
        _kwp1281.SendBlock(new List<byte> { readWriteSafeCode, read });

        var block = _kwp1281.ReceiveBlocks().FirstOrDefault(b => !b.IsAckNak);

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

    public void ClearFaultCodes()
    {
        var faultCodes = _kwp1281.ClearFaultCodes(_controllerAddress);

        if (faultCodes != null)
        {
            if (faultCodes.Count == 0)
            {
                Log.WriteLine("Fault codes cleared.");
            }
            else
            {
                Log.WriteLine("Fault codes:");
                foreach (var faultCode in faultCodes)
                {
                    Log.WriteLine($"    {faultCode}");
                }
            }
        }
        else
        {
            Log.WriteLine("Failed to clear fault codes.");
        }
    }

    public void DelcoVWPremium5SafeCode()
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

        _kwp1281.Login(code, workshopCode);
        var bytes = _kwp1281.ReadRomEeprom(0x0014, 2);
        Log.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
    }

    public void DumpCcmRom(string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.CCM &&
            _controllerAddress != (int)ControllerAddress.CentralLocking)
        {
            Log.WriteLine("Only supported for CCM and Central Locking");
            return;
        }

        UnlockControllerForEepromReadWrite();

        var dumpFileName = filename ?? "ccm_rom_dump.bin";
        const byte blockSize = 8;

        Log.WriteLine($"Saving CCM ROM to {dumpFileName}");

        var succeeded = true;
        using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
        {
            for (var seg = 0; seg < 16; seg++)
            {
                for (var msb = 0; msb < 16; msb++)
                {
                    for (var lsb = 0; lsb < 256; lsb += blockSize)
                    {
                        var blockBytes = _kwp1281.ReadCcmRom((byte)seg, (byte)msb, (byte)lsb, blockSize);
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

    public void DumpClusterNecRom(string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster");
            return;
        }

        var dumpFileName = filename ?? "cluster_nec_rom_dump.bin";
        const byte blockSize = 16;

        Log.WriteLine($"Saving cluster NEC ROM to {dumpFileName}");

        bool succeeded = true;
        using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
        {
            var cluster = new VdoCluster(_kwp1281);

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

    public void FindLogins(ushort goodLogin, int workshopCode)
    {
        const int start = 0;
        for (int login = start; login <= 65535; login++)
        {
            _kwp1281.Login(goodLogin, workshopCode);

            try
            {
                Log.WriteLine($"Trying {login:D5}");
                _kwp1281.Login((ushort)login, workshopCode);
                Log.WriteLine($"{login:D5} succeeded");
                continue;
            }
            catch(TimeoutException)
            {
                _kwp1281.SetDisconnected();
                try
                {
                    Kwp1281Wakeup();
                }
                catch(InvalidOperationException)
                {
                    _kwp1281.SetDisconnected();
                    Kwp1281Wakeup();
                }
            }
        }
    }

    public byte[] ReadWriteEdc15Eeprom(
        string? filename, List<KeyValuePair<ushort, byte>>? addressValuePairs = null)
    {
        _kwp1281.EndCommunication();

        Thread.Sleep(1000);

        // Now wake it up again, hopefully in KW2000 mode
        _kwpCommon!.Interface.SetBaudRate(10400);
        var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParity: false);
        if (kwpVersion < 2000)
        {
            throw new InvalidOperationException(
                $"Unable to wake up ECU in KW2000 mode. KW version: {kwpVersion}");
        }
        Log.WriteLine($"KW Version: {kwpVersion}");

        var edc15 = new Edc15VM(_kwpCommon, _controllerAddress);

        var dumpFileName = filename ?? $"EDC15_EEPROM.bin";

        return edc15.ReadWriteEeprom(dumpFileName, addressValuePairs);
    }

    public void DumpEeprom(uint address, uint length, string? filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                DumpClusterEeprom((ushort)address, (ushort)length, filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                DumpCcmEeprom((ushort)address, (ushort)length, filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void DumpMarelliMem(
        uint address, uint length, ControllerInfo ecuInfo, string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for clusters");
        }
        else
        {
            ICluster cluster = new MarelliCluster(_kwp1281, ecuInfo.Text);
            cluster.DumpEeprom(address, length, filename);
        }
    }

    public void DumpMem(uint address, uint length, string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster");
            return;
        }

        DumpClusterMem(address, length, filename);
    }

    public void DumpRam(uint startAddr, uint length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        const int maxReadLength = 8;
        bool succeeded = true;
        string dumpFileName = filename ?? $"ram_0x{startAddr:X4}.bin";

        using (var fs = File.Create(dumpFileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadRam((ushort)addr, (byte)readLength);
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

    public void DumpRom(uint startAddr, uint length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        const int maxReadLength = 8;
        bool succeeded = true;
        string dumpFileName = filename ?? $"rom_0x{startAddr:X4}.bin";

        using (var fs = File.Create(dumpFileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadRomEeprom((ushort)addr, (byte)readLength);
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

    /// <summary>
    /// Dumps the memory of a Bosch RB4/RB8 cluster to a file.
    /// </summary>
    /// <returns>The dump file name or null if the EEPROM was not dumped.</returns>
    public string? DumpRBxMem(
        uint address, uint length, string? filename,
        bool evenParityWakeup = true)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster (address 17)");
            return null;
        }

        var kwp2000 = Kwp2000Wakeup(evenParityWakeup);

        var dumpFileName = filename ?? $"RBx_0x{address:X6}_mem.bin";

        ICluster cluster = new BoschRBxCluster(kwp2000);
        cluster.UnlockForEepromReadWrite();
        cluster.DumpEeprom(address, length, dumpFileName);

        return dumpFileName;
    }

    /// <summary>
    /// Connects to the cluster and gets its unique ID. This is normally done by the radio in
    /// order to detect if its been moved to a different vehicle.
    /// </summary>
    public void GetClusterId()
    {
#if false
        if (_controllerAddress != 0x3F)
        {
            Log.WriteLine("Only supported for special cluster address $3F");
            return;
        }
#endif

        _kwp1281.SendBlock(new List<byte>
        {
            (byte)BlockTitle.SecurityAccessMode1,

            // The radio would send 4 random values for obfuscation, but the cluster ignores
            // them so we'll just send 0's.
            0x00, 0x00, 0x00, 0x00 // Challenge
        });

        var block = _kwp1281.ReceiveBlocks().FirstOrDefault(b => !b.IsAckNak);

        if (block == null)
        {
            Log.WriteLine("No response received from cluster.");
        }
        else if (block.Title != (byte)BlockTitle.SecurityAccessMode2)
        {
            Log.WriteLine(
                $"Unexpected response received from cluster. Block title: ${block.Title:X2}");
        }
        else
        {
            (byte id1, byte id2) = DecodeClusterId(block.Body[0], block.Body[1], block.Body[2], block.Body[3]);
            Log.WriteLine($"Cluster Id: ${id1:X2} ${id2:X2}");
        }
    }

    public void GetSkc()
    {
        if (_controllerAddress is (int)ControllerAddress.Cluster or (int)ControllerAddress.Immobilizer)
        {
            var ecuInfo = Kwp1281Wakeup();

            if (ecuInfo.Text.Contains("4B0920") ||
                ecuInfo.Text.Contains("4Z7920") ||
                ecuInfo.Text.Contains("8D0920") ||
                ecuInfo.Text.Contains("8Z0920"))
            {
                var family = ecuInfo.Text[..2] switch
                {
                    "8D" => "A4",
                    "8Z" => "A2",
                    _ => "C5"
                };

                Log.WriteLine($"Cluster is Audi {family}");

                var cluster = new AudiC5Cluster(_kwp1281);

                cluster.UnlockForEepromReadWrite();
                var dumpFileName = cluster.DumpEeprom(0, 0x800, $"Audi{family}.bin");

                var buf = File.ReadAllBytes(dumpFileName);

                var skc = Utils.GetShort(buf, 0x7E2);
                var skc2 = Utils.GetShort(buf, 0x7E4);
                var skc3 = Utils.GetShort(buf, 0x7E6);
                if (skc != skc2 || skc != skc3)
                {
                    Log.WriteLine($"Warning: redundant SKCs do not match: {skc:D5} {skc2:D5} {skc3:D5}");
                }
                else
                {
                    Log.WriteLine($"SKC: {skc:D5}");
                }
            }
            else if (ecuInfo.Text.Contains("VDO"))
            {
                var cluster = new VdoCluster(_kwp1281);
                string[] partNumberGroups = FindAndParsePartNumber(ecuInfo.Text);
                if (partNumberGroups.Length == 4)
                {
                    string dumpFileName;
                    ushort startAddress;
                    byte[] buf;
                    ushort? skc;
                    if (partNumberGroups[1] == "919") // Non-CAN
                    {
                        startAddress = 0x1FA;
                        dumpFileName = DumpClusterEeprom(startAddress, length: 6, filename: null);
                        buf = File.ReadAllBytes(dumpFileName);
                        skc = Utils.GetBcd(buf, 0);
                        ushort skc2 = Utils.GetBcd(buf, 2);
                        ushort skc3 = Utils.GetBcd(buf, 4);
                        if (skc != skc2 || skc != skc3)
                        {
                            Log.WriteLine($"Warning: redundant SKCs do not match: {skc:D5} {skc2:D5} {skc3:D5}");
                        }
                    }
                    else if (partNumberGroups[1] == "920") // CAN
                    {
                        startAddress = 0x90;
                        dumpFileName = DumpClusterEeprom(startAddress, length: 0x7C, filename: null);
                        buf = File.ReadAllBytes(dumpFileName);
                        skc = VdoCluster.GetSkc(buf, startAddress);
                    }
                    else
                    {
                        Log.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                        return;
                    }

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
                    Log.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                }
            }
            else if (ecuInfo.Text.Contains("RB4"))
            {
                // Need to quit KWP1281 before switching to KWP2000
                _kwp1281.EndCommunication();
                Thread.Sleep(TimeSpan.FromSeconds(2));

                var dumpFileName = DumpRBxMem(0x10046, 2, filename: null);
                var buf = File.ReadAllBytes(dumpFileName!);
                if (buf.Length == 2)
                {
                    var skc = Utils.GetShort(buf, 0);
                    Log.WriteLine($"SKC: {skc:D5}");
                }
                else
                {
                    Log.WriteLine("Unable to read SKC. Cluster not in New mode (4)?");
                }
            }
            else if (ecuInfo.Text.Contains("RB8"))
            {
                // Need to quit KWP1281 before switching to KWP2000
                _kwp1281.EndCommunication();
                Thread.Sleep(TimeSpan.FromSeconds(2));

                var dumpFileName = DumpRBxMem(0x1040E, 2, filename: null);
                var buf = File.ReadAllBytes(dumpFileName!);
                var skc = Utils.GetShort(buf, 0);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("M73"))
            {
                ICluster cluster = new MarelliCluster(_kwp1281, ecuInfo.Text);

                string dumpFileName = cluster.DumpEeprom(
                    address: null, length: null, dumpFileName: null);
                byte[] buf = File.ReadAllBytes(dumpFileName);
                ushort? skc = MarelliCluster.GetSkc(buf);
                if (skc.HasValue)
                {
                    Log.WriteLine($"SKC: {skc:D5}");
                }
                else
                {
                    Log.WriteLine($"Unable to determine SKC for cluster: {ecuInfo.Text}");
                }
            }
            else if (ecuInfo.Text.Contains("BOO") || ecuInfo.Text.Contains("MM0"))
            {
                ICluster cluster = new MotometerBOOCluster(_kwp1281!);

                cluster.UnlockForEepromReadWrite();

                var dumpFileName = DumpBOOClusterEeprom(
                    startAddress: 0, length: 0x10, filename: null);

                var buf = File.ReadAllBytes(dumpFileName);
                var skc = Utils.GetBcd(buf, 0x08);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("VWZ3Z0"))
            {
                // IMMO BOX 1 1H0 953 257 and 7M0 953 257 support based on sniffed communication.
                // 7M0 953 257 can be both IMMO BOX 1 or IMMO BOX 2.

                var blockBytes = _kwp1281.ReadRomEeprom(0x0190, 176);
                if (blockBytes == null)
                {
                    Log.WriteLine("ROM read failed");
                    return;
                }
                else if (blockBytes.Count == 0)
                {

                    if (ecuInfo.Text.Contains("1H0"))
                    {
                        Log.WriteLine("Failed to read SKC. Immo appears to be locked. You have to use an adapted key.");
                        return;
                    }
                    else if (ecuInfo.Text.Contains("6H0") || ecuInfo.Text.Contains("7M0"))
                    {
                        // This part adds IMMO BOX 2 experimental support (could not test this with real box).
                        // Should work for 6H0 953 257 and 7M0 953 257

                        Log.WriteLine("Trying to unlock IMMO BOX 2. This function is experimental and may not work...");

                        // Unlock ROM
                        _kwp1281.SendBlock([0xCB, 0x5D, 0x3B, 0xD3, 0x8A]);

                        // Send custom read command
                        blockBytes = _kwp1281.ReadSecureImmoAccess([0x02, 0x00, 0x65, 0x34, 0x9D]);

                        if (blockBytes == null || blockBytes.Count == 0)
                        {
                            Log.WriteLine("Failed to read SKC. Immo appears to be locked. You have to use an adapted key.");
                            return;
                        }
                    }
                    else
                    {
                        Log.WriteLine("Failed to read SKC for non 1H0/6H0/7M0 ECU.");
                        return;
                    }
                }

                var skc = Utils.GetShortBE(blockBytes.ToArray(), 1);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("AGD"))
            {
                Log.WriteLine($"Unsupported Magneti Marelli AGD cluster: {ecuInfo.Text}");
            }
            else
            {
                Log.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
            }
        }
        else if (_controllerAddress == (int)ControllerAddress.Ecu)
        {
            var ecuInfo = Kwp1281Wakeup();
            var eeprom = ReadWriteEdc15Eeprom(filename: null);
            Edc15VM.DisplayEepromInfo(eeprom);
        }
        else
        {
            Log.WriteLine(
                "GetSKC only supported for clusters (address 17), Immo boxes (address 25) and ECUs (address 1)");
        }
    }

    /// <summary>
    /// Takes the info returned when connecting to the ECU, finds the ECU part number and
    /// splits into its components. For example, if the ECU info is this:
    ///     "1J5920926CX   KOMBI+WEGFAHRSP VDO V01"
    /// Then the part number would be identified as "1J5920926CX", which would be split into
    /// its 4 components: "1J5", "920", "926", "CX"
    /// </summary>
    /// <param name="ecuInfo"></param>
    /// <returns>A 4-element string array if the part number was found, otherwise an empty
    /// string array.</returns>
    internal static string[] FindAndParsePartNumber(string ecuInfo)
    {
        var match = Regex.Match(
            ecuInfo,
            "\\b(\\d[a-zA-Z][0-9a-zA-Z])(9\\d{2})(\\d{3})([a-zA-Z]{0,2})\\b");

        if (match.Success)
        {
            return (match.Groups as IReadOnlyList<Group>).Skip(1).Select(g => g.Value).ToArray();
        }
        else
        {
            return Array.Empty<string>();
        }
    }

    public void GroupRead(byte groupNumber)
    {
        var succeeded = _kwp1281.GroupRead(groupNumber);
    }

    public void LoadEeprom(uint address, string filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                LoadClusterEeprom((ushort)address, filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                LoadCcmEeprom((ushort)address, filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void MapEeprom(string? filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                MapClusterEeprom(filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                MapCcmEeprom(filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void ReadEeprom(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadEeprom((ushort)address, 1);
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

    public void ReadRam(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadRam((ushort)address, 1);
        if (blockBytes == null)
        {
            Log.WriteLine("RAM read failed");
        }
        else
        {
            var value = blockBytes[0];
            Log.WriteLine(
                $"Address {address} (${address:X4}): Value {value} (${value:X2})");
        }
    }

    public void ReadRom(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadRomEeprom((ushort)address, 1);
        if (blockBytes == null)
        {
            Log.WriteLine("ROM read failed");
        }
        else
        {
            var value = blockBytes[0];
            Log.WriteLine(
                $"Address {address} (${address:X4}): Value {value} (${value:X2})");
        }
    }

    public void ReadFaultCodes()
    {
        var faultCodes = _kwp1281.ReadFaultCodes();
        if (faultCodes != null)
        {
            Log.WriteLine("Fault codes:");
            foreach (var faultCode in faultCodes)
            {
                Log.WriteLine($"    {faultCode}");
            }
        }
    }

    public void ReadIdent()
    {
        foreach (var identInfo in _kwp1281.ReadIdent())
        {
            Log.WriteLine($"Ident: {identInfo}");
        }
    }

    public void ReadSoftwareVersion()
    {
        if (_controllerAddress == (int)ControllerAddress.Cluster)
        {
            var cluster = new VdoCluster(_kwp1281);
            cluster.CustomReadSoftwareVersion();
        }
        else
        {
            Log.WriteLine("Only supported for cluster");
        }
    }

    public void Reset()
    {
        if (_controllerAddress == (int)ControllerAddress.Cluster)
        {
            var cluster = new VdoCluster(_kwp1281);
            cluster.CustomReset();
        }
        else
        {
            Log.WriteLine("Only supported for cluster");
        }
    }

    public void SetSoftwareCoding(
        int softwareCoding, int workshopCode)
    {
        var succeeded = _kwp1281.SetSoftwareCoding(_controllerAddress, softwareCoding, workshopCode);
        if (succeeded)
        {
            Log.WriteLine("Software coding set.");
        }
        else
        {
            Log.WriteLine("Failed to set software coding.");
        }
    }

    public void ToggleRB4Mode()
    {
        var kwp2000 = Kwp2000Wakeup(evenParityWakeup: true);

        BoschRBxCluster cluster = new(kwp2000);
        cluster.UnlockForEepromReadWrite();
        cluster.ToggleRB4Mode();
    }

    public void WriteEeprom(uint address, byte value)
    {
        UnlockControllerForEepromReadWrite();

        _kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
    }

    // End top-level commands

    private string DumpBOOClusterEeprom(ushort startAddress, ushort length, string? filename)
    {
        var identInfo = _kwp1281.ReadIdent().First().ToString()
            .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
            .Replace(' ', '_')
            .Replace('.', '_')
            .Replace(":", "");

        var dumpFileName = filename ?? $"{identInfo}_0x{startAddress:X4}_eeprom.bin";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            dumpFileName = dumpFileName.Replace(c, 'X');
        }
        foreach (var c in Path.GetInvalidPathChars())
        {
            dumpFileName = dumpFileName.Replace(c, 'X');
        }

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        DumpEeprom(startAddress, length, maxReadLength: 16, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");

        return dumpFileName;
    }

    private string DumpClusterEeprom(
        ushort startAddress, ushort length, string? filename)
    {
        var identInfo = _kwp1281.ReadIdent().First().ToString()
            .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
            .Replace(' ', '_').Replace(":", "");

        ICluster cluster = new VdoCluster(_kwp1281);
        cluster.UnlockForEepromReadWrite();

        var dumpFileName = filename ?? $"{identInfo}_0x{startAddress:X4}_eeprom.bin";

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        cluster.DumpEeprom(startAddress, length, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");

        return dumpFileName;
    }

    private void MapCcmEeprom(string? filename)
    {
        UnlockControllerForEepromReadWrite();

        var bytes = new List<byte>();
        const byte blockSize = 1;
        for (int addr = 0; addr <= 65535; addr += blockSize)
        {
            var blockBytes = _kwp1281.ReadEeprom((ushort)addr, blockSize);
            blockBytes = Enumerable.Repeat(
                blockBytes == null ? (byte)0 : (byte)0xFF,
                blockSize).ToList();
            bytes.AddRange(blockBytes);
        }
        var dumpFileName = filename ?? "ccm_eeprom_map.bin";
        Log.WriteLine($"Saving EEPROM map to {dumpFileName}");
        File.WriteAllBytes(dumpFileName, bytes.ToArray());
    }

    private void MapClusterEeprom(string? filename)
    {
        var cluster = new VdoCluster(_kwp1281);

        var map = cluster.MapEeprom();

        var mapFileName = filename ?? "eeprom_map.bin";
        Log.WriteLine($"Saving EEPROM map to {mapFileName}");
        File.WriteAllBytes(mapFileName, map.ToArray());
    }

    private void DumpCcmEeprom(ushort startAddress, ushort length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        var dumpFileName = filename ?? $"ccm_eeprom_0x{startAddress:X4}.bin";

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        DumpEeprom(startAddress, length, maxReadLength: 8, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");
    }

    private void UnlockControllerForEepromReadWrite()
    {
        switch ((ControllerAddress)_controllerAddress)
        {
            case ControllerAddress.CCM:
            case ControllerAddress.CentralLocking:
                _kwp1281.Login(
                    code: 19283,
                    workshopCode: 222); // This is what VDS-PRO uses
                break;

            case ControllerAddress.CentralElectric:
                _kwp1281.Login(
                    code: 21318,
                    workshopCode: 222); // This is what VDS-PRO uses
                break;

            case ControllerAddress.Cluster:
                // TODO:UnlockCluster() is only needed for EEPROM read, not memory read
                var cluster = new VdoCluster(_kwp1281);
                var (isUnlocked, softwareVersion) = cluster.Unlock();
                if (!isUnlocked)
                {
                    Log.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
                }

                if (!cluster.RequiresSeedKey())
                {
                    Log.WriteLine(
                        "Cluster is unlocked for ROM/EEPROM access. Skipping Seed/Key login.");
                    return;
                }

                cluster.SeedKeyAuthenticate(softwareVersion);
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

    private void DumpEeprom(
        ushort startAddr, uint length, byte maxReadLength, string fileName)
    {
        bool succeeded = true;

        using (var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadEeprom((ushort)addr, (byte)readLength) ?? [];
                if (blockBytes.Count < readLength)
                {
                    blockBytes.AddRange(Enumerable.Repeat((byte)0, readLength - blockBytes.Count));
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

    private void WriteEeprom(
        ushort startAddr, byte[] bytes, uint maxWriteLength)
    {
        var succeeded = true;
        var length = bytes.Length;
        for (uint addr = startAddr; addr < (startAddr + length); addr += maxWriteLength)
        {
            var writeLength = (byte)Math.Min(startAddr + length - addr, maxWriteLength);
            if (!_kwp1281.WriteEeprom(
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

    private void LoadCcmEeprom(ushort address, string filename)
    {
        _ = _kwp1281.ReadIdent();

        UnlockControllerForEepromReadWrite();

        if (!File.Exists(filename))
        {
            Log.WriteLine($"File {filename} does not exist.");
            return;
        }

        Log.WriteLine($"Reading {filename}");
        var bytes = File.ReadAllBytes(filename);

        Log.WriteLine("Writing to cluster...");
        WriteEeprom(address, bytes, 8);
    }

    private void LoadClusterEeprom(ushort address, string filename)
    {
        _ = _kwp1281.ReadIdent();

        UnlockControllerForEepromReadWrite();

        if (!File.Exists(filename))
        {
            Log.WriteLine($"File {filename} does not exist.");
            return;
        }

        Log.WriteLine($"Reading {filename}");
        var bytes = File.ReadAllBytes(filename);

        Log.WriteLine("Writing to cluster...");
        WriteEeprom(address, bytes, 16);
    }

    private void DumpClusterMem(uint startAddress, uint length, string? filename)
    {
        var cluster = new VdoCluster(_kwp1281);
        if (!cluster.RequiresSeedKey())
        {
            Log.WriteLine(
                "Cluster is unlocked for memory access. Skipping Seed/Key login.");
        }
        else
        {
            var (isUnlocked, softwareVersion) = cluster.Unlock();
            if (!isUnlocked)
            {
                Log.WriteLine("Unknown cluster software version. Memory access will likely fail.");
            }
            cluster.SeedKeyAuthenticate(softwareVersion);
        }

        var dumpFileName = filename ?? $"cluster_mem_0x{startAddress:X6}.bin";
        Log.WriteLine($"Saving memory dump to {dumpFileName}");

        cluster.DumpMem(dumpFileName, startAddress, length);

        Log.WriteLine($"Saved memory dump to {dumpFileName}");
    }

    private static (byte, byte) DecodeClusterId(byte b1, byte b2, byte b3, byte b4)
    {
        // For obfuscation, the cluster adds the values below, so we need to subtract them:
        bool carry = true;
        (b1, carry) = Utils.SubtractWithCarry(b1, 0xE7, carry);
        (b2, carry) = Utils.SubtractWithCarry(b2, 0xBD, carry);
        (b3, carry) = Utils.SubtractWithCarry(b3, 0x18, carry);
        (b4, carry) = Utils.SubtractWithCarry(b4, 0x00, carry);

        b1 ^= b3;
        b2 ^= b4;

        // Count the number of 0 bits in b1 and b2

        byte zeroCount = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((b1 >> i) & 1) == 0)
            {
                zeroCount++;
            }
            if (((b2 >> i) & 1) == 0)
            {
                zeroCount++;
            }
        }

        // Right-rotate b3 and b4 zeroCount times:
        for (int i = 0; i < zeroCount; i++)
        {
            carry = (b4 & 1) != 0;
            (b3, carry) = Utils.RightRotate(b3, carry);
            (b4, carry) = Utils.RightRotate(b4, carry);
        }

        b1 ^= b3;
        b2 ^= b4;

        return (b1, b2);
    }
}
