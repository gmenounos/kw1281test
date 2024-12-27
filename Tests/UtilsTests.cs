using FluentAssertions;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class UtilsTests
{
    [TestMethod]
    public void MileageHexToDecimal_ValidString8_ReturnsInteger()
    {
        // Source: https://github.com/gmenounos/vwcluster/blob/main/Odometer.md
        var mileageInDecimal = Utils.MileageHexToDecimal("fdff fdff feff feff feff feff feff feff");

        // 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x4A, 0xE8, 0x4A, 0xE8
        // VAG eeprom programmer 1.19g says: 97117
        var mileageInDecimal1 = Utils.MileageHexToDecimal("49e8 49e8 49e8 49e8 49e8 49e8 4ae8 4ae8");

        // 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA
        // VAG eeprom programmer 1.19g says: 284489
        var mileageInDecimal2 = Utils.MileageHexToDecimal("8ABA 8ABA 8ABA 8ABA 8BBA 8BBA 8BBA 8BBA");

        mileageInDecimal.Should().Be(20);
        mileageInDecimal1.Should().Be(97116);
        mileageInDecimal2.Should().Be(284488);
    }

    [TestMethod]
    public void MileageHexToDecimal_ValidString16_ReturnsInteger()
    {
        // Source: https://github.com/gmenounos/vwcluster/blob/main/Odometer.md
        var mileageInDecimal = Utils.MileageHexToDecimal("fd ff fd ff fe ff fe ff fe ff fe ff fe ff fe ff");

        // 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x4A, 0xE8, 0x4A, 0xE8
        // VAG eeprom programmer 1.19g says: 97117 (weird because it should be an even number)
        var mileageInDecimal1 = Utils.MileageHexToDecimal("49 e8 49 e8 49 e8 49 e8 49 e8 49 e8 4a e8 4a e8");

        // 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA
        // VAG eeprom programmer 1.19g says: 284489 (weird because it should be an even number)
        var mileageInDecimal2 = Utils.MileageHexToDecimal("8A BA 8A BA 8A BA 8A BA 8B BA 8B BA 8B BA 8B BA");

        mileageInDecimal.Should().Be(20);
        mileageInDecimal1.Should().Be(97116);
        mileageInDecimal2.Should().Be(284488);
    }

    [TestMethod]
    public void MileageHexToDecimal_ValidString1_ReturnsInteger()
    {
        // Source: https://github.com/gmenounos/vwcluster/blob/main/Odometer.md
        var mileageInDecimal = Utils.MileageHexToDecimal("fdfffdfffefffefffefffefffefffeff");

        // 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x4A, 0xE8, 0x4A, 0xE8
        // VAG eeprom programmer 1.19g says: 97117
        var mileageInDecimal1 = Utils.MileageHexToDecimal("49e849e849e849e849e849e84ae84ae8");

        // 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA
        // VAG eeprom programmer 1.19g says: 284489
        var mileageInDecimal2 = Utils.MileageHexToDecimal("8ABA8ABA8ABA8ABA8BBA8BBA8BBA8BBA");

        mileageInDecimal.Should().Be(20);
        mileageInDecimal1.Should().Be(97116);
        mileageInDecimal2.Should().Be(284488);
    }

    [TestMethod]
    public void MileageDecimalToHex_ValidInt_ReturnsHexString()
    {
        // Source: https://github.com/gmenounos/vwcluster/blob/main/Odometer.md
        var mileageInHex = Utils.MileageDecimalToHex(20);

        // 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x49, 0xE8, 0x4A, 0xE8, 0x4A, 0xE8
        // VAG eeprom programmer 1.19g says: 97117 (weird because it should be an even number)
        var mileageInHex1 = Utils.MileageDecimalToHex(97116);

        // 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8A, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA, 0x8B, 0xBA
        // VAG eeprom programmer 1.19g says: 284489 (weird because it should be an even number)
        var mileageInHex2 = Utils.MileageDecimalToHex(284488);

        mileageInHex.ToLower().Should().Be("fdff fdff feff feff feff feff feff feff");
        mileageInHex1.ToLower().Should().Be("49e8 49e8 49e8 49e8 49e8 49e8 4ae8 4ae8");
        mileageInHex2.ToLower().Should().Be("8aba 8aba 8aba 8aba 8bba 8bba 8bba 8bba");
    }
}