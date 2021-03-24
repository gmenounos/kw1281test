using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Cluster
{
    class Vdo
    {
        public static bool UnlockCluster(IKW1281Dialog kwp1281)
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
        private static byte[][] GetClusterUnlockCodes(List<byte> softwareVersion)
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
        };
    }
}
