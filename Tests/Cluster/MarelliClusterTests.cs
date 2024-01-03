using BitFab.KW1281Test.Cluster;
using FluentAssertions;

namespace BitFab.KW1281Test.Tests.Cluster;

[TestClass]
public class MarelliClusterTests
{
    [TestMethod]
    public void GetSkc_ImmoIdStartsWithVWZ_ReturnsSkc()
    {
        var eeprom = File.ReadAllBytes("Cluster/VWZ_02755.bin");
        var skc = MarelliCluster.GetSkc(eeprom);
        skc.Should().Be(2755);
    }
    
    [TestMethod]
    public void GetSkc_ImmoIdStartsWithAUZ_ReturnsSkc()
    {
        var eeprom = File.ReadAllBytes("Cluster/AUZ_03997.bin");
        var skc = MarelliCluster.GetSkc(eeprom);
        skc.Should().Be(3997);
    }

    [TestMethod]
    public void GetSkc_NoImmoIdButKeyCountPattern_ReturnsSkc()
    {
        var eeprom = File.ReadAllBytes("Cluster/06032.bin");
        var skc = MarelliCluster.GetSkc(eeprom);
        skc.Should().Be(6032);
    }

    [TestMethod]
    public void GetSkc_NoImmoIdAndNoKeyCountPattern_ReturnsNull()
    {
        var eeprom = new byte[1024];
        var skc = MarelliCluster.GetSkc(eeprom);
        skc.Should().BeNull();
    }
}