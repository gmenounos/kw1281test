using BitFab.KW1281Test.Airbag;
using Shouldly;

namespace BitFab.KW1281Test.Tests.Airbag;

/// <summary>
/// The fill ranges used by ClearCrashData. Getting a bound wrong here is expensive to find
/// out on hardware: the module is left reporting a permanent DTC 65535 that nothing but
/// restoring a saved EEPROM dump will clear, and its own ClearFaultCodes refuses to run
/// while it is in that state. Hence the invariants live here.
/// </summary>
[TestClass]
public class AirbagClearRangeTests
{
    private static Vw51AirbagModule.ModuleVersion[] AllVersions =>
        Enum.GetValues<Vw51AirbagModule.ModuleVersion>();

    [TestMethod]
    public void NoRangeTouchesAProtectedTrack()
    {
        foreach (var version in AllVersions)
        {
            var tracks = Vw51AirbagModule.GetProtectedTracks(version);

            foreach (var (start, end) in Vw51AirbagModule.GetClearRanges(version))
            {
                foreach (var (trackStart, trackEnd) in tracks)
                {
                    var overlaps = start <= trackEnd && end >= trackStart;

                    overlaps.ShouldBeFalse(
                        $"{version}: range 0x{start:X3}-0x{end:X3} overlaps the protected " +
                        $"track 0x{trackStart:X3}-0x{trackEnd:X3}");
                }
            }
        }
    }

    [TestMethod]
    public void NoRangeRunsPastTheEndOfEeprom()
    {
        foreach (var version in AllVersions)
        {
            var size = Vw51AirbagModule.GetEepromSize(version);

            foreach (var (start, end) in Vw51AirbagModule.GetClearRanges(version))
            {
                start.ShouldBeGreaterThanOrEqualTo(0);
                start.ShouldBeLessThanOrEqualTo(end,
                    $"{version}: range 0x{start:X3}-0x{end:X3} is inverted");
                end.ShouldBeLessThan(size,
                    $"{version}: range 0x{start:X3}-0x{end:X3} runs past {size} bytes");
            }
        }
    }

    [TestMethod]
    public void NoVersionFillsItsWholeEeprom()
    {
        // The 1C0909601 entry used to start with 0x000-0x30F while that part's EEPROM is
        // 0x310 bytes, so the fill took the software coding, the workshop code and the
        // equipment block with it.
        foreach (var version in AllVersions)
        {
            var size = Vw51AirbagModule.GetEepromSize(version);
            var covered = Vw51AirbagModule.GetClearRanges(version).Sum(r => r.End - r.Start + 1);

            covered.ShouldBeLessThan(size,
                $"{version}: the fill covers {covered} of {size} EEPROM bytes");
        }
    }

    [TestMethod]
    public void Vw51ClearsTheLogButNotTheTrackThatFollowsIt()
    {
        var ranges = Vw51AirbagModule.GetClearRanges(Vw51AirbagModule.ModuleVersion.VW51);

        ranges[0].Start.ShouldBe(0x000);
        ranges[0].End.ShouldBe(0x02F,
            "0x030-0x03F is a track: the flag bytes at 0x030-0x031 are repaired by reading " +
            "them first, never by filling, and 0x032 onwards is the counter whose tag walks");

        ranges.ShouldAllBe(r => r.End < 0x030 || r.Start > 0x05B);
    }

    [TestMethod]
    public void Vw61CrashRangeStartsAfterTheRequiredRecord()
    {
        // 0x156-0x15F are the log's stamp counters and 0x160-0x169 is a record every
        // healthy module carries; a fill that starts before 0x16A brings back 65535.
        var crash = Vw51AirbagModule
            .GetClearRanges(Vw51AirbagModule.ModuleVersion.VW61)
            .Where(r => r.Start > 0x100)
            .ToArray();

        crash.ShouldNotBeEmpty();
        crash.Min(r => r.Start).ShouldBeGreaterThan(0x169);
        crash.ShouldContain(r => r.Start <= 0x1EA && r.End >= 0x215,
            "the duplicated crash record spans 0x1EA-0x215");
    }

    [TestMethod]
    public void ValidationRejectsTheOldVw51Range()
    {
        var bad = new[] { (0x000, 0x04F) };

        var ex = Should.Throw<InvalidOperationException>(
            () => Vw51AirbagModule.ValidateClearRanges(bad, Vw51AirbagModule.ModuleVersion.VW51));

        ex.Message.ShouldContain("65535");
    }

    [TestMethod]
    public void ValidationRejectsFillingTheVw61StampCounters()
    {
        // This exact range was tried on a live module and brought 65535 straight back:
        // bytes 1-2 of every fault log stamp are read from 0x157-0x158.
        var bad = new[] { (0x156, 0x16F) };

        Should.Throw<InvalidOperationException>(
            () => Vw51AirbagModule.ValidateClearRanges(bad, Vw51AirbagModule.ModuleVersion.VW61));
    }

    [TestMethod]
    public void ValidationRejectsRunningPastTheEndOfEeprom()
    {
        var bad = new[] { (0x1F0, 0x2FF) };

        Should.Throw<InvalidOperationException>(
            () => Vw51AirbagModule.ValidateClearRanges(bad, Vw51AirbagModule.ModuleVersion.VW51));
    }

    [TestMethod]
    public void EveryShippedRangeSetPassesValidation()
    {
        foreach (var version in AllVersions)
        {
            Vw51AirbagModule.ValidateClearRanges(
                Vw51AirbagModule.GetClearRanges(version), version);
        }
    }
}
