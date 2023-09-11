using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BitFab.KW1281Test.Cluster
{
    class VdoCluster : ICluster
    {
        public void UnlockForEepromReadWrite()
        {
            if (!Unlock())
            {
                Log.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
            }

            if (!RequiresSeedKey())
            {
                Log.WriteLine(
                    "Cluster is unlocked for ROM/EEPROM access. Skipping Seed/Key login.");
                return;
            }

            SeedKeyAuthenticate();
            if (RequiresSeedKey())
            {
                Log.WriteLine("Failed to unlock cluster.");
            }
            else
            {
                Log.WriteLine("Cluster is unlocked for ROM/EEPROM access.");
            }
        }

        public string DumpEeprom(
            uint? optionalAddress, uint? optionalLength, string? optionalFileName)
        {
            uint address = optionalAddress ?? 0;
            uint length = optionalLength ?? 0x800;
            string filename = optionalFileName ?? $"VDO_${address:X6}_eeprom.bin";

            DumpEeprom((ushort)address, (ushort)length, maxReadLength: 16, filename);

            return filename;
        }

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// </summary>
        public Dictionary<int, Block> CustomReadSoftwareVersion()
        {
            var versionBlocks = new Dictionary<int, Block>();

            Log.WriteLine("Sending Custom \"Read Software Version\" blocks");

            // The cluster can return 4 variations of software version, specified by the 2nd byte
            // of the block:
            // 0x00 - Cluster software version
            // 0x01 - Unknown
            // 0x02 - Unknown
            // 0x03 - Unknown
            for (byte variation = 0x00; variation < 0x04; variation++)
            {
                var blocks = SendCustom(new List<byte> { 0x84, variation });
                foreach (var block in blocks.Where(b => !b.IsAckNak))
                {
                    if (variation == 0x00 || variation == 0x03)
                    {
                        Log.WriteLine($"{variation:X2}: {DumpMixedContent(block)}");
                    }
                    else
                    {
                        Log.WriteLine($"{variation:X2}: {DumpBinaryContent(block)}");
                    }
                    versionBlocks[variation] = block;
                }
            }

            return versionBlocks;
        }

        public void CustomReset()
        {
            Log.WriteLine("Sending Custom Reset block");
            SendCustom(new List<byte> { 0x82 });
        }

        public List<byte> CustomReadMemory(uint address, byte count)
        {
            Log.WriteLine($"Sending Custom \"Read Memory\" block (Address: ${address:X6}, Count: ${count:X2})");
            var blocks = SendCustom(new List<byte>
            {
                0x86,
                count,
                (byte)(address & 0xFF),
                (byte)((address >> 8) & 0xFF),
                (byte)((address >> 16) & 0xFF),
            });
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                // Permissions issue?
                return new List<byte>();
            }
            return blocks[0].Body.ToList();
        }

        /// <summary>
        /// Read the low 64KB of the cluster's NEC controller ROM.
        /// For MFA clusters, that should cover the entire ROM.
        /// For FIS clusters, the ROM is 128KB and more work is needed to retrieve the high 64KB.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public List<byte> CustomReadNecRom(ushort address, byte count)
        {
            Log.WriteLine($"Sending Custom \"Read NEC ROM\" block (Address: ${address:X4}, Count: ${count:X2})");
            var blocks = SendCustom(new List<byte>
            {
                0xA6,
                count,
                (byte)(address & 0xFF),
                (byte)((address >> 8) & 0xFF),
            });
            blocks = blocks.Where(b => !b.IsAckNak).ToList();
            if (blocks.Count != 1)
            {
                throw new InvalidOperationException($"Custom \"Read NEC ROM\" returned {blocks.Count} blocks instead of 1");
            }
            return blocks[0].Body.ToList();
        }

        public List<byte> MapEeprom()
        {
            // Unlock partial EEPROM read
            Unlock();

            var map = new List<byte>();
            const byte blockSize = 1;
            for (ushort addr = 0; addr < 2048; addr += blockSize)
            {
                var blockBytes = _kwp1281.ReadEeprom(addr, blockSize);
                blockBytes = Enumerable.Repeat(
                    blockBytes == null ? (byte)0 : (byte)0xFF,
                    blockSize).ToList();
                map.AddRange(blockBytes);
            }

            return map;
        }

        public void DumpMem(string dumpFileName, uint startAddress, uint length)
        {
            const byte blockSize = 15;

            bool succeeded = true;
            using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
            {
                for (uint addr = startAddress; addr < startAddress + length; addr += blockSize)
                {
                    var readLength = (byte)Math.Min(startAddress + length - addr, blockSize);
                    var blockBytes = CustomReadMemory(addr, readLength);
                    if (blockBytes.Count != readLength)
                    {
                        succeeded = false;
                        blockBytes.AddRange(
                            Enumerable.Repeat((byte)0, readLength - blockBytes.Count));
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

        public List<Block> SendCustom(List<byte> blockCustomBytes)
        {
            if (blockCustomBytes[0] > 0x80 && !_additionalCustomCommandsUnlocked)
            {
                CustomUnlockAdditionalCommands();
                _additionalCustomCommandsUnlocked = true;
            }

            blockCustomBytes.Insert(0, (byte)BlockTitle.Custom);
            _kwp1281.SendBlock(blockCustomBytes);
            return _kwp1281.ReceiveBlocks();
        }

        public bool Unlock()
        {
            var versionBlocks = CustomReadSoftwareVersion();
            if (versionBlocks.Count == 0)
            {
                Log.WriteLine("Cluster did not return software version.");
                return false;
            }

            // Now we need to send an unlock code that is unique to each ROM version
            Log.WriteLine("Sending Custom \"Unlock partial EEPROM read\" block");
            var softwareVersion = SoftwareVersionToString(versionBlocks[0].Body);
            var unlockCodes = GetClusterUnlockCodes(softwareVersion);
            var unlocked = false;
            foreach (var unlockCode in unlockCodes)
            {
                var unlockCommand = new List<byte> { 0x9D };
                unlockCommand.AddRange(unlockCode);
                var unlockResponse = SendCustom(unlockCommand);
                if (unlockResponse.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Received multiple responses from unlock request.");
                }
                if (unlockResponse[0].IsAck)
                {
                    Log.WriteLine(
                        $"Unlock code for software version '{softwareVersion}' is{Utils.Dump(unlockCode)}");
                    if (unlockCodes.Length > 1)
                    {
                        Log.WriteLine("Please report this to the program maintainer.");
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

        public void SeedKeyAuthenticate()
        {
            // Perform Seed/Key authentication
            Log.WriteLine("Sending Custom \"Seed request\" block");
            var response = SendCustom(new List<byte> { 0x96, 0x01 });

            var responseBlocks = response.Where(b => !b.IsAckNak).ToList();
            if (responseBlocks.Count == 1 && responseBlocks[0] is CustomBlock customBlock)
            {
                Log.WriteLine($"Block: {Utils.Dump(customBlock.Body)}");

                var keyBytes = VdoKeyFinder.FindKey(customBlock.Body.ToArray());

                Log.WriteLine("Sending Custom \"Key response\" block");

                var keyResponse = new List<byte> { 0x96, 0x02 };
                keyResponse.AddRange(keyBytes);

                response = SendCustom(keyResponse);
            }
        }

        public bool RequiresSeedKey()
        {
            Log.WriteLine("Sending Custom \"Need Seed/Key?\" block");
            var response = SendCustom(new List<byte> { 0x96, 0x04 });
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

        /// <summary>
        /// Given a VDO cluster EEPROM dump, attempt to determine the SKC and return it if found.
        /// </summary>
        /// <param name="bytes">A portion of a VDO cluster EEPROM dump.</param>
        /// <param name="startAddress">The start address of bytes within the EEPROM.</param>
        /// <returns>The SKC or null if the SKC could not be determined.</returns>
        public ushort? GetSkc(byte[] bytes, int startAddress)
        {
            string text = Encoding.ASCII.GetString(bytes);

            // There are several EEPROM formats. We can determine the format by locating the
            // 14-character immobilizer ID and noting its offset in the dump.

            var immoMatch = Regex.Match(
                text,
                @"[A-Z]{2}Z\dZ0[A-Z]\d{7}");
            if (!immoMatch.Success)
            {
                Log.WriteLine("GetSkc: Unable to find Immobilizer ID in cluster dump.");
                return null;
            }

            ushort skc;
            var index = immoMatch.Index + startAddress;

            switch (index)
            {
                case 0x090:
                case 0x0AC:
                    // Immo2
                    skc = Utils.GetBcd(bytes, 0x0BA - startAddress);
                    return skc;
                case 0x0A2:
                    // VWK501
                    skc = Utils.GetShort(bytes, 0x0CC - startAddress);
                    return skc;
                case 0x0E0:
                    // VWK503
                    skc = Utils.GetShort(bytes, 0x10A - startAddress);
                    return skc;
                default:
                    Log.WriteLine(
                        $"GetSkc: Unknown EEPROM (Immobilizer offset: 0x{immoMatch.Index:X3})");
                    return null;
            }
        }

        /// <summary>
        /// http://www.maltchev.com/kiti/VAG_guide.txt
        /// This unlocks additional custom commands $81-$AF
        /// </summary>
        private void CustomUnlockAdditionalCommands()
        {
            Log.WriteLine("Sending Custom \"Unlock Additional Commands\" block");
            SendCustom(new List<byte> { 0x80, 0x01, 0x02, 0x03, 0x04 });
        }

        /// <summary>
        /// Different cluster models have different unlock codes. Return the appropriate one based
        /// on the cluster's software version.
        /// </summary>
        private static byte[][] GetClusterUnlockCodes(string softwareVersion)
        {
            switch(softwareVersion)
            {
                case "VAT500MH 01.10": // 1J0920925D V06
                case "VAT500LL 01.20": // 1J0920905L V01
                case "VAT500MH 01.20": // 1J5920925C V09
                    return ToArray(0x01, 0x04, 0x3D, 0x35);

                case "V798MLA 01.00": // 7D0920800F V01, 1J0919951C V55
                    return ToArray(0x02, 0x03, 0x05, 0x09);

                case "$00 $00 $13 $01": // 8D0919880M  B5-KOMBIINSTR. VDO D02
                    return ToArray(0x09, 0x06, 0x05, 0x02);

                case "VQMJ06LM 09.00": // 6Q0920903   KOMBI+WEGFAHRSP VDO V02
                    return ToArray(0x35, 0x3D, 0x47, 0x3E);

                case "VWK501LL 00.88": // 1J0920906L V58
                case "VWK501MH 00.88":
                case "VWK501LL 01.00":
                case "VWK501MH 01.00":
                    return ToArray(0x36, 0x3D, 0x3E, 0x47);

                case "V599HLA  00.91": // 7D0920841A V18
                case "V599LLA  00.91": // 7D0920801B V18
                case "V599LLA  01.00": // 1J0920800L V59
                case "V599MLA  01.00": // 7D0920821D V22
                case "V599LLA  03.00": // 1J0920900J V60
                    return ToArray(0x38, 0x3F, 0x40, 0x35);

                case "MPV501MH 01.00": // 7M3920820H V57
                    return ToArray(0x38, 0x47, 0x34, 0x3A);

                case "VWK501MH 00.92": // 3B0920827C V06
                case "VWK501MH 01.10":
                    return ToArray(0x39, 0x34, 0x34, 0x40);

                case "VBKX00MH 01.00":
                    return ToArray(0x3A, 0x39, 0x31, 0x43);

                case "SS5501LM 01.00": // 1M0920802D V05
                    return ToArray(0x3C, 0x34, 0x47, 0x35);

                case "VWK503LL 09.00":
                case "VWK503MH 09.00": // 1J0920927 V02
                    return ToArray(0x3E, 0x35, 0x3D, 0x3A);

                case "VMMJ08MH 09.00": // 1J5920826L V75
                    return ToArray(0x3E, 0x47, 0x3D, 0x48);

                case "VSQX01LM 01.10": // 6Q0920900  KOMBI+WEGFAHRSP VDO V18
                    return ToArray(0x43, 0x43, 0x3D, 0x37);

                default:
                    return _clusterUnlockCodes;
            };
        }

        private static string SoftwareVersionToString(List<byte> versionBytes)
        {
            if (versionBytes.Count < 9 || versionBytes.Count > 10)
            {
                return Utils.DumpMixedContent(versionBytes);
            }


            var asciiPart = Encoding.ASCII.GetString(versionBytes.ToArray()[0..^2]);
            return $"{asciiPart} {versionBytes[^1]:X2}.{versionBytes[^2]:X2}";
        }

        private static byte[][] ToArray(byte v1, byte v2, byte v3, byte v4)
        {
            return new[] { new byte[] { v1, v2, v3, v4 } };
        }

        private static readonly byte[][] _clusterUnlockCodes = new[]
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
            new byte[] { 0x3E, 0x47, 0x3D, 0x48 },
            new byte[] { 0x39, 0x43, 0x43, 0x43 },
            new byte[] { 0x3A, 0x31, 0x31, 0x36 },
            new byte[] { 0x3A, 0x34, 0x47, 0x38 },
            new byte[] { 0x3A, 0x39, 0x31, 0x43 },
            new byte[] { 0x3A, 0x39, 0x41, 0x43 },
            new byte[] { 0x3A, 0x3B, 0x35, 0x3C },
            new byte[] { 0x3A, 0x3B, 0x35, 0x4C },
            new byte[] { 0x3A, 0x3D, 0x35, 0x3E },
            new byte[] { 0x3B, 0x33, 0x3E, 0x37 },
            new byte[] { 0x3B, 0x3A, 0x37, 0x3E },
            new byte[] { 0x3B, 0x46, 0x23, 0x10 },
            new byte[] { 0x3B, 0x46, 0x23, 0x1B },
            new byte[] { 0x3B, 0x46, 0x23, 0x1D },
            new byte[] { 0x3B, 0x47, 0x03, 0x02 },
            new byte[] { 0x3C, 0x31, 0x3C, 0x35 },
            new byte[] { 0x3C, 0x34, 0x47, 0x35 },
            new byte[] { 0x3D, 0x36, 0x40, 0x36 },
            new byte[] { 0x3D, 0x39, 0x3B, 0x35 },
            new byte[] { 0x3E, 0x35, 0x3D, 0x3A },
            new byte[] { 0x3E, 0x35, 0x43, 0x30 },
            new byte[] { 0x3E, 0x35, 0x43, 0x39 },
            new byte[] { 0x3E, 0x35, 0x43, 0x40 },
            new byte[] { 0x3E, 0x35, 0x43, 0x41 },
            new byte[] { 0x3E, 0x35, 0x43, 0x42 },
            new byte[] { 0x3E, 0x35, 0x43, 0x43 },
            new byte[] { 0x3E, 0x35, 0x43, 0x44 },
            new byte[] { 0x3E, 0x39, 0x31, 0x43 },
            new byte[] { 0x3E, 0x39, 0x35, 0x40 },
            new byte[] { 0x3E, 0x39, 0x43, 0x34 },
            new byte[] { 0x3E, 0x3F, 0x40, 0x35 },
            new byte[] { 0x3F, 0x31, 0x3B, 0x47 },
            new byte[] { 0x3F, 0x38, 0x43, 0x38 },
            new byte[] { 0x3F, 0x43, 0x35, 0x3E },
            new byte[] { 0x40, 0x30, 0x3E, 0x39 },
            new byte[] { 0x40, 0x34, 0x34, 0x39 },
            new byte[] { 0x40, 0x39, 0x39, 0x38 },
            new byte[] { 0x40, 0x43, 0x35, 0x3E },
            new byte[] { 0x41, 0x43, 0x35, 0x3E },
            new byte[] { 0x42, 0x43, 0x35, 0x3E },
            new byte[] { 0x42, 0x45, 0x3F, 0x36 },
            new byte[] { 0x43, 0x31, 0x39, 0x3A },
            new byte[] { 0x43, 0x43, 0x35, 0x3E },
            new byte[] { 0x43, 0x43, 0x3D, 0x37 },
            new byte[] { 0x43, 0x43, 0x43, 0x39 },
            new byte[] { 0x43, 0x45, 0x31, 0x3D },
            new byte[] { 0x44, 0x43, 0x35, 0x3E },
            new byte[] { 0x45, 0x39, 0x34, 0x43 },
            new byte[] { 0x47, 0x3A, 0x39, 0x38 },
            new byte[] { 0x47, 0x3B, 0x31, 0x3F },
            new byte[] { 0x47, 0x3C, 0x39, 0x37 },
            new byte[] { 0x47, 0x3E, 0x3D, 0x36 },
            new byte[] { 0x09, 0x09, 0x09, 0x09 },
            new byte[] { 0x09, 0x09, 0x03, 0x02 },
            new byte[] { 0x09, 0x06, 0x05, 0x02 },
            new byte[] { 0x09, 0x06, 0x04, 0x09 },
            new byte[] { 0x09, 0x06, 0x03, 0x02 },
            new byte[] { 0x09, 0x05, 0x05, 0x08 },
            new byte[] { 0x09, 0x05, 0x03, 0x02 },
            new byte[] { 0x09, 0x05, 0x02, 0x03 },
            new byte[] { 0x09, 0x04, 0x03, 0x02 },
            new byte[] { 0x09, 0x03, 0x09, 0x06 },
            new byte[] { 0x09, 0x03, 0x03, 0x02 },
            new byte[] { 0x09, 0x02, 0x06, 0x06 },
            new byte[] { 0x09, 0x02, 0x03, 0x02 },
            new byte[] { 0x09, 0x01, 0x03, 0x02 },
            new byte[] { 0x09, 0x01, 0x01, 0x07 },
            new byte[] { 0x09, 0x00, 0x03, 0x02 },
            new byte[] { 0x08, 0x08, 0x09, 0x08 },
            new byte[] { 0x08, 0x06, 0x07, 0x06 },
            new byte[] { 0x08, 0x06, 0x03, 0x02 },
            new byte[] { 0x08, 0x05, 0x03, 0x02 },
            new byte[] { 0x08, 0x04, 0x03, 0x02 },
            new byte[] { 0x08, 0x04, 0x02, 0x02 },
            new byte[] { 0x08, 0x03, 0x03, 0x02 },
            new byte[] { 0x08, 0x02, 0x03, 0x05 },
            new byte[] { 0x08, 0x02, 0x03, 0x02 },
            new byte[] { 0x08, 0x02, 0x01, 0x04 },
            new byte[] { 0x08, 0x01, 0x06, 0x05 },
            new byte[] { 0x08, 0x01, 0x03, 0x02 },
            new byte[] { 0x08, 0x00, 0x03, 0x02 },
            new byte[] { 0x07, 0x07, 0x09, 0x07 },
            new byte[] { 0x07, 0x07, 0x09, 0x04 },
            new byte[] { 0x07, 0x06, 0x03, 0x02 },
            new byte[] { 0x07, 0x05, 0x03, 0x02 },
            new byte[] { 0x07, 0x04, 0x03, 0x02 },
            new byte[] { 0x07, 0x03, 0x05, 0x03 },
            new byte[] { 0x07, 0x03, 0x03, 0x02 },
            new byte[] { 0x07, 0x02, 0x03, 0x02 },
            new byte[] { 0x07, 0x01, 0x03, 0x02 },
            new byte[] { 0x07, 0x00, 0x06, 0x04 },
            new byte[] { 0x07, 0x00, 0x03, 0x02 },
            new byte[] { 0x06, 0x09, 0x05, 0x03 },
            new byte[] { 0x06, 0x09, 0x03, 0x09 },
            new byte[] { 0x06, 0x09, 0x01, 0x02 },
            new byte[] { 0x06, 0x06, 0x09, 0x06 },
            new byte[] { 0x06, 0x06, 0x03, 0x02 },
            new byte[] { 0x06, 0x05, 0x03, 0x02 },
            new byte[] { 0x06, 0x04, 0x07, 0x01 },
            new byte[] { 0x06, 0x04, 0x03, 0x02 },
            new byte[] { 0x06, 0x03, 0x03, 0x02 },
            new byte[] { 0x06, 0x02, 0x03, 0x02 },
            new byte[] { 0x06, 0x01, 0x03, 0x02 },
            new byte[] { 0x06, 0x00, 0x03, 0x02 },
            new byte[] { 0x06, 0x00, 0x03, 0x00 },
            new byte[] { 0x06, 0x00, 0x02, 0x02 },
            new byte[] { 0x05, 0x08, 0x05, 0x02 },
            new byte[] { 0x05, 0x06, 0x03, 0x02 },
            new byte[] { 0x05, 0x05, 0x09, 0x05 },
            new byte[] { 0x05, 0x05, 0x08, 0x09 },
            new byte[] { 0x05, 0x05, 0x03, 0x02 },
            new byte[] { 0x05, 0x04, 0x03, 0x02 },
            new byte[] { 0x05, 0x03, 0x03, 0x02 },
            new byte[] { 0x05, 0x02, 0x09, 0x02 },
            new byte[] { 0x05, 0x02, 0x03, 0x09 },
            new byte[] { 0x05, 0x02, 0x03, 0x02 },
            new byte[] { 0x05, 0x01, 0x04, 0x08 },
            new byte[] { 0x05, 0x01, 0x03, 0x02 },
            new byte[] { 0x05, 0x00, 0x03, 0x02 },
            new byte[] { 0x04, 0x07, 0x00, 0x07 },
            new byte[] { 0x04, 0x06, 0x03, 0x02 },
            new byte[] { 0x04, 0x05, 0x05, 0x02 },
            new byte[] { 0x04, 0x05, 0x03, 0x02 },
            new byte[] { 0x04, 0x04, 0x09, 0x04 },
            new byte[] { 0x04, 0x04, 0x03, 0x02 },
            new byte[] { 0x04, 0x03, 0x03, 0x02 },
            new byte[] { 0x04, 0x02, 0x06, 0x06 },
            new byte[] { 0x04, 0x02, 0x03, 0x02 },
            new byte[] { 0x04, 0x01, 0x03, 0x08 },
            new byte[] { 0x04, 0x01, 0x03, 0x02 },
            new byte[] { 0x04, 0x00, 0x03, 0x02 },
            new byte[] { 0x03, 0x08, 0x02, 0x05 },
            new byte[] { 0x03, 0x06, 0x03, 0x02 },
            new byte[] { 0x03, 0x05, 0x03, 0x02 },
            new byte[] { 0x03, 0x04, 0x03, 0x02 },
            new byte[] { 0x03, 0x03, 0x09, 0x03 },
            new byte[] { 0x03, 0x03, 0x08, 0x04 },
            new byte[] { 0x03, 0x03, 0x03, 0x02 },
            new byte[] { 0x03, 0x02, 0x05, 0x02 },
            new byte[] { 0x03, 0x02, 0x03, 0x02 },
            new byte[] { 0x03, 0x01, 0x03, 0x02 },
            new byte[] { 0x03, 0x00, 0x07, 0x01 },
            new byte[] { 0x03, 0x00, 0x03, 0x07 },
            new byte[] { 0x03, 0x00, 0x03, 0x02 },
            new byte[] { 0x02, 0x32, 0x3B, 0x37 },
            new byte[] { 0x02, 0x09, 0x04, 0x03 },
            new byte[] { 0x02, 0x09, 0x04, 0x02 },
            new byte[] { 0x02, 0x09, 0x02, 0x06 },
            new byte[] { 0x02, 0x06, 0x06, 0x09 },
            new byte[] { 0x02, 0x06, 0x03, 0x02 },
            new byte[] { 0x02, 0x05, 0x08, 0x01 },
            new byte[] { 0x02, 0x05, 0x06, 0x09 },
            new byte[] { 0x02, 0x05, 0x03, 0x02 },
            new byte[] { 0x02, 0x05, 0x00, 0x02 },
            new byte[] { 0x02, 0x04, 0x03, 0x02 },
            new byte[] { 0x02, 0x04, 0x00, 0x02 },
            new byte[] { 0x02, 0x03, 0x05, 0x09 },
            new byte[] { 0x02, 0x03, 0x03, 0x02 },
            new byte[] { 0x02, 0x02, 0x09, 0x02 },
            new byte[] { 0x02, 0x02, 0x04, 0x01 },
            new byte[] { 0x02, 0x02, 0x03, 0x02 },
            new byte[] { 0x02, 0x01, 0x03, 0x02 },
            new byte[] { 0x02, 0x00, 0x06, 0x01 },
            new byte[] { 0x02, 0x00, 0x03, 0x02 },
            new byte[] { 0x01, 0x08, 0x05, 0x02 },
            new byte[] { 0x01, 0x08, 0x03, 0x00 },
            new byte[] { 0x01, 0x08, 0x02, 0x05 },
            new byte[] { 0x01, 0x07, 0x00, 0x03 },
            new byte[] { 0x01, 0x06, 0x04, 0x02 },
            new byte[] { 0x01, 0x06, 0x03, 0x02 },
            new byte[] { 0x01, 0x06, 0x02, 0x00 },
            new byte[] { 0x01, 0x06, 0x00, 0x02 },
            new byte[] { 0x01, 0x05, 0x3D, 0x35 },
            new byte[] { 0x01, 0x05, 0x06, 0x08 },
            new byte[] { 0x01, 0x05, 0x03, 0x02 },
            new byte[] { 0x01, 0x04, 0x3D, 0x35 },
            new byte[] { 0x01, 0x04, 0x03, 0x02 },
            new byte[] { 0x01, 0x04, 0x02, 0x02 },
            new byte[] { 0x01, 0x03, 0x03, 0x02 },
            new byte[] { 0x01, 0x02, 0x03, 0x02 },
            new byte[] { 0x01, 0x01, 0x07, 0x09 },
            new byte[] { 0x01, 0x01, 0x05, 0x08 },
            new byte[] { 0x01, 0x01, 0x03, 0x07 },
            new byte[] { 0x01, 0x01, 0x03, 0x02 },
            new byte[] { 0x01, 0x01, 0x01, 0x06 },
            new byte[] { 0x01, 0x01, 0x01, 0x05 },
            new byte[] { 0x01, 0x01, 0x01, 0x04 },
            new byte[] { 0x01, 0x01, 0x01, 0x03 },
            new byte[] { 0x01, 0x01, 0x01, 0x02 },
            new byte[] { 0x01, 0x01, 0x01, 0x01 },
            new byte[] { 0x01, 0x01, 0x01, 0x00 },
            new byte[] { 0x01, 0x01, 0x00, 0x09 },
            new byte[] { 0x01, 0x01, 0x00, 0x08 },
            new byte[] { 0x01, 0x01, 0x00, 0x07 },
            new byte[] { 0x01, 0x01, 0x00, 0x06 },
            new byte[] { 0x01, 0x01, 0x00, 0x05 },
            new byte[] { 0x01, 0x01, 0x00, 0x04 },
            new byte[] { 0x01, 0x00, 0x09, 0x05 },
            new byte[] { 0x01, 0x00, 0x03, 0x02 },
            new byte[] { 0x00, 0x08, 0x02, 0x04 },
            new byte[] { 0x00, 0x07, 0x43, 0x35 },
            new byte[] { 0x00, 0x07, 0x03, 0x08 },
            new byte[] { 0x00, 0x07, 0x02, 0x04 },
            new byte[] { 0x00, 0x06, 0x03, 0x02 },
            new byte[] { 0x00, 0x05, 0x03, 0x02 },
            new byte[] { 0x00, 0x04, 0x06, 0x07 },
            new byte[] { 0x00, 0x04, 0x03, 0x02 },
            new byte[] { 0x00, 0x03, 0x04, 0x02 },
            new byte[] { 0x00, 0x03, 0x03, 0x02 },
            new byte[] { 0x00, 0x02, 0x09, 0x07 },
            new byte[] { 0x00, 0x02, 0x03, 0x02 },
            new byte[] { 0x00, 0x01, 0x03, 0x02 },
            new byte[] { 0x00, 0x00, 0x03, 0x02 },
            new byte[] { 0x00, 0x00, 0x00, 0x00 },
        };

        private static string DumpMixedContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            return Utils.DumpMixedContent(block.Body);
        }

        private static string DumpBinaryContent(Block block)
        {
            if (block.IsNak)
            {
                return "NAK";
            }

            return Utils.DumpBytes(block.Body);
        }

        private void DumpEeprom(
            ushort startAddr, ushort length, byte maxReadLength, string fileName)
        {
            bool succeeded = true;

            using (var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough))
            {
                for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
                {
                    byte readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                    List<byte>? blockBytes = _kwp1281.ReadEeprom((ushort)addr, readLength);
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

        private readonly IKW1281Dialog _kwp1281;
        private bool _additionalCustomCommandsUnlocked;

        public VdoCluster(IKW1281Dialog kwp1281)
        {
            _kwp1281 = kwp1281;
            _additionalCustomCommandsUnlocked = false;
        }
    }
}
