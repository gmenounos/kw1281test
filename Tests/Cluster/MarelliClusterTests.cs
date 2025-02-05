using BitFab.KW1281Test.Cluster;

namespace BitFab.KW1281Test.Tests.Cluster;

[TestClass]
public class MarelliClusterTests
{
    [TestMethod]
    [DataRow("VWZ_02755.bin", 02755)]    // ImmoId starts with "VWZ"
    [DataRow("AUZ_03997.bin", 03997)]    // ImmoId starts with "AUZ"
    [DataRow("06032.bin", 06032)]        // No ImmoId but key count pattern
    public void GetSkc_ReturnsCorrectSkc(
        string fileName, int expectedSkc)
    {
        var eeprom = File.ReadAllBytes($"Cluster/{fileName}");
        var skc = MarelliCluster.GetSkc(eeprom);
        Assert.AreEqual((ushort?)expectedSkc, skc);
    }
    
    [TestMethod]
    public void GetSkc_NoImmoIdAndNoKeyCountPattern_ReturnsNull()
    {
        var eeprom = new byte[1024];
        var skc = MarelliCluster.GetSkc(eeprom);
        Assert.IsNull(skc);
    }
}