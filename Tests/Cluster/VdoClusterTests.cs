using BitFab.KW1281Test.Cluster;
using Shouldly;

namespace BitFab.KW1281Test.Tests.Cluster
{
    [TestClass]
    public class VdoClusterTests
    {
        [TestMethod]
        [DataRow("MPV300LL 00.90", (byte[])[0x3F, 0x38, 0x43, 0x38])]
        [DataRow("MPV300LL 02.00", (byte[])[0x3B, 0x47, 0x03, 0x02])]
        [DataRow("MPV300LL 03.00", (byte[])[0x43, 0x43, 0x43, 0x39])]
        [DataRow("MPV300LL 04.00", (byte[])[0x38, 0x47, 0x34, 0x3A])]
        [DataRow("MPV300MH 01.00", null)]
        [DataRow("MPV500LL 00.90", (byte[])[0x3F, 0x38, 0x43, 0x38])]
        [DataRow("MPV501MH 01.00", (byte[])[0x38, 0x47, 0x34, 0x3A])]
        [DataRow("MPV501MH 06.00", null)]
        [DataRow("SS5500LM 01.00", (byte[])[0x40, 0x39, 0x39, 0x38])]
        [DataRow("SS5501LM 01.00", (byte[])[0x3C, 0x34, 0x47, 0x35])]
        [DataRow("SS5501LM 00.80", (byte[])[0x36, 0x3B, 0x36, 0x3D])]
        [DataRow("SS5501ML 01.00", (byte[])[0x3C, 0x34, 0x47, 0x35])]
        [DataRow("SS5501ML 00.80", (byte[])[0x36, 0x3B, 0x36, 0x3D])]
        [DataRow("S599CAA  00.80", (byte[])[0x3D, 0x39, 0x3B, 0x35])]
        [DataRow("VAT500LL 01.00", (byte[])[0x01, 0x04, 0x3D, 0x35])]
        [DataRow("VAT500LL 01.20", (byte[])[0x01, 0x04, 0x3D, 0x35])]
        [DataRow("VAT500MH 01.10", (byte[])[0x01, 0x04, 0x3D, 0x35])]
        [DataRow("VBK700LL 01.00", (byte[])[0x3A, 0x39, 0x31, 0x43])]
        [DataRow("VBK700LL 00.96", (byte[])[0x3A, 0x39, 0x31, 0x43])]
        [DataRow("VBKX00MH 01.00", (byte[])[0x3A, 0x39, 0x31, 0x43])]
        [DataRow("VWK501LL 01.00", (byte[])[0x36, 0x3D, 0x3E, 0x47])]
        [DataRow("VWK501MH 01.10", (byte[])[0x39, 0x34, 0x34, 0x40])]
        [DataRow("VWK503MH 09.00", (byte[])[0x3E, 0x35, 0x3D, 0x3A])]
        [DataRow("VWK503LL 09.00", (byte[])[0x3E, 0x35, 0x3D, 0x3A])]
        [DataRow("VSQX01LM 01.00", (byte[])[0x31, 0x39, 0x34, 0x46])]
        [DataRow("VSQX01LM 01.10", (byte[])[0x43, 0x43, 0x3D, 0x37])]
        [DataRow("VSQX01LM 01.20", (byte[])[0x3D, 0x36, 0x40, 0x36])]
        [DataRow("VT5X02LL 09.40", (byte[])[0x36, 0x3F, 0x45, 0x42])]
        [DataRow("VT5X02LL 09.00", (byte[])[0x38, 0x39, 0x3A, 0x47])]
        [DataRow("VQMJ06LM 09.00", (byte[])[0x35, 0x3D, 0x47, 0x3E])]
        [DataRow("VQMJ07LM 09.00", (byte[])[0x34, 0x3F, 0x43, 0x39])]
        [DataRow("VQMJ07LM 08.40", (byte[])[0x34, 0x3F, 0x43, 0x39])]
        [DataRow("VKQ501HH 09.00", (byte[])[0x34, 0x3F, 0x43, 0x39])]
        public void GetClusterUnlockCodes_ReturnsCorrectCode(
            string softwareVersion, byte[] unlockCode)
        {
            var actualUnlockCodes = VdoCluster.GetClusterUnlockCodes(softwareVersion);
            if (unlockCode == null)
            {
                actualUnlockCodes.Length.ShouldBeGreaterThan(1);
            }
            else
            {
                actualUnlockCodes.Length.ShouldBe(1);
                actualUnlockCodes[0].ShouldBe(unlockCode);
            }
        }

        [TestMethod]
        [DataRow((byte[])[0x01, 0x04, 0x3D, 0x35])]
        [DataRow((byte[])[0x31, 0x39, 0x34, 0x46])]
        [DataRow((byte[])[0x34, 0x3F, 0x43, 0x39])]
        [DataRow((byte[])[0x35, 0x3D, 0x47, 0x3E])]
        [DataRow((byte[])[0x36, 0x3B, 0x36, 0x3D])]
        [DataRow((byte[])[0x36, 0x3D, 0x3E, 0x47])]
        [DataRow((byte[])[0x36, 0x3F, 0x45, 0x42])]
        [DataRow((byte[])[0x38, 0x39, 0x3A, 0x47])]
        [DataRow((byte[])[0x38, 0x47, 0x34, 0x3A])]
        [DataRow((byte[])[0x39, 0x34, 0x34, 0x40])]
        [DataRow((byte[])[0x3A, 0x39, 0x31, 0x43])]
        [DataRow((byte[])[0x3B, 0x47, 0x03, 0x02])]
        [DataRow((byte[])[0x3C, 0x34, 0x47, 0x35])]
        [DataRow((byte[])[0x3D, 0x36, 0x40, 0x36])]
        [DataRow((byte[])[0x3D, 0x39, 0x3B, 0x35])]
        [DataRow((byte[])[0x3E, 0x35, 0x3D, 0x3A])]
        [DataRow((byte[])[0x3F, 0x38, 0x43, 0x38])]
        [DataRow((byte[])[0x40, 0x39, 0x39, 0x38])]
        [DataRow((byte[])[0x43, 0x43, 0x3D, 0x37])]
        [DataRow((byte[])[0x43, 0x43, 0x43, 0x39])]
        public void ClusterUnlockCodes_ContainsKnownCodes(byte[] unlockCode)
        {
            foreach (var code in VdoCluster.ClusterUnlockCodes)
            {
                if (code.SequenceEqual(unlockCode))
                {
                    return; // Found the code, no need to check further
                }
            }
            Assert.Fail($"Unlock code {BitConverter.ToString(unlockCode)} not found in known codes.");
        }

        [TestMethod]
        [DataRow(
            new byte[] { 0x22, 0xDB, 0x00, 0x73, 0x64, 0x6C, 0x33, 0x37, 0x06, 0x00 },
            new byte[] { 0x07, 0xBE, 0x59, 0x00, 0xBE, 0x00, 0xA0 })]
        [DataRow(
            new byte[] { 0x98, 0xD9, 0xC6, 0xAB, 0x4D, 0xE6, 0xEE, 0x73, 0x06, 0x00 },
            new byte[] { 0x07, 0xA1, 0x27, 0x00, 0x50, 0x00, 0xF2 })]
        [DataRow(
            new byte[] { 0x3D, 0x6D, 0x05, 0xD3, 0xE5, 0xF3, 0xFD, 0x11, 0x06, 0x00 },
            new byte[] { 0x07, 0xC0, 0x92, 0x00, 0xBB, 0x00, 0x55 })]
        [DataRow(
            new byte[] { 0x82, 0xE2, 0x8C, 0xBB, 0x0B, 0xFF, 0xCD, 0x72, 0x06, 0x00 },
            new byte[] { 0x07, 0xA6, 0xE7, 0x00, 0x31, 0x00, 0x2A })]
        public void FindKey_AudiC5Seed0600_MatchesRealCapturedKey(byte[] seed, byte[] expectedKey)
        {
            // Real seed/key pairs captured from separate Audi C5 cluster VVDI2 sessions —
            // all four from the same physical cluster (this secret is per-unit, not
            // per-firmware; a different physical cluster of the same family may need its own).
            var key = VdoKeyFinder.FindKey(seed, accessLevel: 7);

            key.ShouldBe(expectedKey);
        }

        [TestMethod]
        public void ClusterUnlockCodes_ContainsNoDuplicates()
        {
            var seenCodes = new HashSet<string>();

            foreach (var code in VdoCluster.ClusterUnlockCodes)
            {
                var codeString = BitConverter.ToString(code);
                if (seenCodes.Contains(codeString))
                {
                    Assert.Fail($"Duplicate unlock code found: {codeString}");
                }
                seenCodes.Add(codeString);
            }
        }
    }
}
