using BitFab.KW1281Test.EDC15;
using Shouldly;
using System.Globalization;

namespace BitFab.KW1281Test.Tests.EDC15;

/// <summary>
/// Characterization (golden) tests for <c>Edc15VM.LVL41Auth</c>, the EDC15
/// security-access (level 0x41) seed→key algorithm. The golden file is generated
/// from the *current* implementation on the first run and then locks the behavior
/// in, so any later refactor that changes the output fails the build.
/// </summary>
[TestClass]
public class Lvl41AuthTests
{
    // Same constants Edc15VM.ReadWriteEeprom uses.
    private const uint Key = 0x508DA647;
    private const uint Key3 = 0x3800000;

    // Hand-picked seeds: all-zeros, mixed, and 0x80 placements to force both
    // branches of the inner loop (bit-15 set vs. clear) across the 5 iterations.
    private static readonly byte[][] FixedSeeds =
    [
        [0x00, 0x00, 0x00, 0x00],
        [0x01, 0x02, 0x03, 0x04],
        [0x00, 0x00, 0x00, 0x01],
        [0xFF, 0xFF, 0xFF, 0xFF],
        [0x80, 0x00, 0x00, 0x00],
        [0x00, 0x80, 0x00, 0x00],
        [0x00, 0x00, 0x80, 0x00],
        [0x12, 0x34, 0x56, 0x78],
    ];

    /// <summary>Full, deterministic seed battery (no randomness → stable golden file).</summary>
    private static IReadOnlyList<byte[]> AllSeeds()
    {
        var seeds = new List<byte[]>(FixedSeeds);
        for (int i = 1; i <= 32; i++)
        {
            seeds.Add([(byte)i, (byte)(i * 7), (byte)(i * 13), (byte)(i * 19)]);
        }
        return seeds;
    }

    [TestMethod]
    [DataRow("00000000", "00000000")]
    [DataRow("01020304", "20406080")]
    [DataRow("00000001", "00000020")]
    [DataRow("FFFFFFFF", "4E191408")]
    [DataRow("80000000", "69159E1C")]
    [DataRow("00800000", "10000000")]
    [DataRow("00008000", "00100000")]
    [DataRow("12345678", "AC433E94")]
    [DataRow("01070D13", "20E1A260")]
    [DataRow("020E1A26", "41C344C0")]
    [DataRow("03152739", "62A4E720")]
    [DataRow("041C344C", "83868980")]
    [DataRow("0523415F", "A4682BE0")]
    [DataRow("062A4E72", "C549CE40")]
    [DataRow("07315B85", "E62B70A0")]
    [DataRow("08386898", "A14A438C")]
    [DataRow("093F75AB", "81A9E5EC")]
    [DataRow("0A4682BE", "EE97074C")]
    [DataRow("0B4D8FD1", "CFF6AAAC")]
    [DataRow("0C549CE4", "2CD4CC0C")]
    [DataRow("0D5BA9F7", "0D326E6C")]
    [DataRow("0E62B60A", "6A1191CC")]
    [DataRow("0F69C31D", "4B7F332C")]
    [DataRow("1070D030", "E4D3F794")]
    [DataRow("1177DD43", "C43259F4")]
    [DataRow("127EEA56", "A514BB54")]
    [DataRow("1385F769", "9A771CB4")]
    [DataRow("148C047C", "7B497E14")]
    [DataRow("1593118F", "58ABC074")]
    [DataRow("169A1EA2", "398A25D4")]
    [DataRow("17A12BB5", "1EEC8734")]
    [DataRow("18A838C8", "5989B818")]
    [DataRow("19AF45DB", "79661A78")]
    [DataRow("1AB652EE", "1A44FCD8")]
    [DataRow("1BBD5F01", "3B254138")]
    [DataRow("1CC46C14", "D4032398")]
    [DataRow("1DCB7927", "F5E185F8")]
    [DataRow("1ED2863A", "96DE6658")]
    [DataRow("1FD9934D", "B7BCC8B8")]
    [DataRow("20E0A060", "6FC0BFA4")]
    public void MatchesGoldenRow(string seedString, string keyString)
    {
        var seedWord = uint.Parse(seedString, NumberStyles.HexNumber);
        var keyWord = uint.Parse(keyString, NumberStyles.HexNumber);

        byte[] seed = [(byte)(seedWord >> 24), (byte)(seedWord >> 16), (byte)(seedWord >> 8), (byte)seedWord];
        byte[] expected = [(byte)(keyWord >> 24), (byte)(keyWord >> 16), (byte)(keyWord >> 8), (byte)keyWord];
        Edc15VM.LVL41Auth(Key, Key3, seed).ShouldBe(expected, $"seed 0x{seedWord:X8}");
    }

    [TestMethod]
    [DataRow(0x00, 0x00, 0x00, 0x00)]
    [DataRow(0x01, 0x02, 0x03, 0x04)]
    [DataRow(0xFF, 0xFF, 0xFF, 0xFF)]
    [DataRow(0x80, 0x00, 0x00, 0x00)]
    public void AlwaysReturnsFourBytes(int b0, int b1, int b2, int b3)
    {
        Edc15VM.LVL41Auth(Key, Key3, [(byte)b0, (byte)b1, (byte)b2, (byte)b3]).Length.ShouldBe(4);
    }

    [TestMethod]
    [DataRow(0x01, 0x02, 0x03, 0x04)]
    [DataRow(0xFF, 0xFF, 0xFF, 0xFF)]
    [DataRow(0x00, 0x80, 0x00, 0x01)]
    public void IsDeterministic(int b0, int b1, int b2, int b3)
    {
        byte[] seed = [(byte)b0, (byte)b1, (byte)b2, (byte)b3];
        var r1 = Edc15VM.LVL41Auth(Key, Key3, seed);
        var r2 = Edc15VM.LVL41Auth(Key, Key3, seed);
        r1.SequenceEqual(r2).ShouldBeTrue("LVL41Auth must be a pure function of its inputs");
    }
}