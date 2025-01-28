using BitFab.KW1281Test.Cluster;
using FluentAssertions;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class VdoMileageUtilsTests
{
    // Source: https://github.com/gmenounos/vwcluster/blob/main/Odometer.md

    // 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x4A, 0xE8, 0x4A, 0xE8
    // VAG eeprom programmer 1.19g says: 97117

    // 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA
    // VAG eeprom programmer 1.19g says: 284489


    [TestMethod]
    public void VdoMileageHexToDecimal_ValidString8_ReturnsInteger()
    {
        var mileageInDecimal = VdoCluster.VdoMileageHexToDecimal("fdff fdff feff feff feff feff feff feff");
        var mileageInDecimal1 = VdoCluster.VdoMileageHexToDecimal("49e8 49e8 49e8 49e8 49e8 49e8 4ae8 4ae8");
        var mileageInDecimal2 = VdoCluster.VdoMileageHexToDecimal("8ABA 8ABA 8ABA 8ABA 8BBA 8BBA 8BBA 8BBA");

        mileageInDecimal.Should().Be(21);
        mileageInDecimal1.Should().Be(97117);
        mileageInDecimal2.Should().Be(284489);
    }

    [TestMethod]
    public void VdoMileageHexToDecimal_ValidString16_ReturnsInteger()
    {
        var mileageInDecimal = VdoCluster.VdoMileageHexToDecimal("fd ff fd ff fe ff fe ff fe ff fe ff fe ff fe ff");
        var mileageInDecimal1 = VdoCluster.VdoMileageHexToDecimal("49 e8 49 e8 49 e8 49 e8 49 e8 49 e8 4a e8 4a e8");
        var mileageInDecimal2 = VdoCluster.VdoMileageHexToDecimal("8A BA 8A BA 8A BA 8A BA 8B BA 8B BA 8B BA 8B BA");

        mileageInDecimal.Should().Be(21);
        mileageInDecimal1.Should().Be(97117);
        mileageInDecimal2.Should().Be(284489);
    }

    [TestMethod]
    public void VdoMileageHexToDecimal_ValidString1_ReturnsInteger()
    {
        var mileageInDecimal = VdoCluster.VdoMileageHexToDecimal("fdfffdfffefffefffefffefffefffeff");
        var mileageInDecimal1 = VdoCluster.VdoMileageHexToDecimal("49e849e849e849e849e849e84ae84ae8");
        var mileageInDecimal2 = VdoCluster.VdoMileageHexToDecimal("8ABA8ABA8ABA8ABA8BBA8BBA8BBA8BBA");

        mileageInDecimal.Should().Be(21);
        mileageInDecimal1.Should().Be(97117);
        mileageInDecimal2.Should().Be(284489);
    }

    [TestMethod]
    public void VdoMileageDecimalToHex_ValidInt_ReturnsHexString()
    {
        var mileageInHex = VdoCluster.VdoMileageDecimalToHex(21);
        var mileageInHex1 = VdoCluster.VdoMileageDecimalToHex(97117);
        var mileageInHex2 = VdoCluster.VdoMileageDecimalToHex(284489);

        mileageInHex.ToLower().Should().Be("fdff fdff feff feff feff feff feff feff");
        mileageInHex1.ToLower().Should().Be("49e8 49e8 49e8 49e8 49e8 49e8 4ae8 4ae8");
        mileageInHex2.ToLower().Should().Be("8aba 8aba 8aba 8aba 8bba 8bba 8bba 8bba");
    }
}