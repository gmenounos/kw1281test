using System.Globalization;
using BitFab.KW1281Test.Blocks;
using Shouldly;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class SensorValueTests
{
    private static string F(byte id, byte a, byte b)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return new SensorValue(id, a, b).ToString(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void SteeringAndYawRateFormulasKeepTheSign()
    {
        // 67-70 used to be read as unsigned: -1 showed up as 163837.5 Deg.
        F(67, 0xFF, 0xFF).ShouldBe("-2.5 Deg");
        F(68, 0xFF, 0xFF).ShouldBe("-0.1 deg/s");
        F(69, 0xFF, 0xFF).ShouldBe("-0.3 bar");
        F(70, 0x00, 0x0A).ShouldBe("1.9 m/s²");
    }

    [TestMethod]
    public void Formula34IsCrankAngleNotPower()
    {
        F(34, 10, 143).ShouldBe("1.5 Deg k/w");
    }

    [TestMethod]
    public void EspBrakePressureFormula83IsSigned16BitHundredths()
    {
        // ESP 1C0907379M, group 5: 83 00 42 = 0.66 bar.
        F(83, 0x00, 0x42).ShouldBe("0.66 bar");
        F(83, 0xFF, 0xF6).ShouldBe("-0.10 bar");
    }

    [TestMethod]
    public void NewFormulasAreDecodedInsteadOfRawBytes()
    {
        F(81, 0xFF, 0xF6).ShouldBe("-0.4 Deg");
        F(116, 0x04, 0xD2).ShouldBe("1234 rpm");
        F(127, 0xAD, 0x9A).ShouldBe("2026.11.21");
        F(171, 16, 160).ShouldBe("1.00 g");
    }

    [TestMethod]
    public void UnknownFormulaShowsRawBytes()
    {
        F(250, 1, 2).ShouldBe("(250 1 2)");
    }
}
