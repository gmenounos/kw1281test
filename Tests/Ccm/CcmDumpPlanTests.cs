using BitFab.KW1281Test.Ccm;
using Shouldly;

namespace BitFab.KW1281Test.Tests.Ccm;

[TestClass]
public class CcmDumpPlanTests
{
    [TestMethod]
    [DataRow("1C0959799F")]
    [DataRow("1J0959799J")]
    [DataRow("ECU: 1C0 959 799 AG")]
    [DataRow("1c0959799")]
    [DataRow("1C0 959 799 F  KOMFORTGER. HLO 0001")]
    public void MappedPlanIsUsedForComfortModules(string ecuInfo)
    {
        var plan = CcmDumpPlan.Create(ecuInfo, 0, CcmDumpPlan.MappedFileLength, preferFast: true);

        plan.IsMapped.ShouldBeTrue();
        plan.FastReadSkippedReason.ShouldBeNull();
        plan.FileLength.ShouldBe(CcmDumpPlan.MappedFileLength);
        plan.Ranges.Count.ShouldBe(5);
        plan.BytesToRead.ShouldBe(5 * 0x200);
    }

    [TestMethod]
    public void MappedPlanReadsTheFiveWindowsThatHoldData()
    {
        var plan = CcmDumpPlan.Create("1C0959799F", 0, CcmDumpPlan.MappedFileLength, preferFast: true);

        plan.Ranges.Select(r => r.Address)
            .ShouldBe([0x1000, 0x2000, 0x3000, 0x4000, 0x5000]);
        plan.Ranges.ShouldAllBe(r => r.Length == 0x200);
    }

    [TestMethod]
    public void LegacyDumpSizeAlsoGetsTheMap()
    {
        var plan = CcmDumpPlan.Create("1C0959799F", 0, CcmDumpPlan.LegacyFileLength, preferFast: true);

        plan.IsMapped.ShouldBeTrue();
        // The dump is written in the mapped layout, not the requested 21 KiB.
        plan.FileLength.ShouldBe(CcmDumpPlan.MappedFileLength);
    }

    [TestMethod]
    [DataRow("1C0959801A", "only covers")] // central locking, different memory map
    [DataRow("6N0959799", "only covers")]  // other comfort module family
    [DataRow("1J0920926", "only covers")]  // a cluster, not a comfort module
    [DataRow("", "only covers")]
    public void UnknownControllersAreReadSequentially(string ecuInfo, string reasonFragment)
    {
        var plan = CcmDumpPlan.Create(ecuInfo, 0, CcmDumpPlan.MappedFileLength, preferFast: true);

        plan.IsMapped.ShouldBeFalse();
        plan.FastReadSkippedReason.ShouldContain(reasonFragment);
        plan.Ranges.Count.ShouldBe(1);
        plan.BytesToRead.ShouldBe(CcmDumpPlan.MappedFileLength);
    }

    [TestMethod]
    public void FastReadCanBeTurnedOff()
    {
        var plan = CcmDumpPlan.Create("1C0959799F", 0, CcmDumpPlan.MappedFileLength, preferFast: false);

        plan.IsMapped.ShouldBeFalse();
        plan.FastReadSkippedReason.ShouldBe("the fast read option is off");
    }

    [TestMethod]
    public void ANonZeroStartFallsBackToASequentialRead()
    {
        var plan = CcmDumpPlan.Create("1C0959799F", 0x1000, 0x200, preferFast: true);

        plan.IsMapped.ShouldBeFalse();
        plan.FastReadSkippedReason.ShouldContain("starts at 0000");
        plan.StartAddress.ShouldBe(0x1000);
    }

    [TestMethod]
    public void AnUnexpectedLengthFallsBackToASequentialRead()
    {
        var plan = CcmDumpPlan.Create("1C0959799F", 0, 0x800, preferFast: true);

        plan.IsMapped.ShouldBeFalse();
        plan.FastReadSkippedReason.ShouldContain("dump size");
    }

    [TestMethod]
    [DataRow(0x2000, "42")]
    [DataRow(0x2FFF, "42")]
    [DataRow(0x3041, "52")]
    [DataRow(0x4100, "62")]
    [DataRow(0x5000, "72")]
    public void DoorWindowsMapToTheirDiagnosticAddress(int address, string controller)
    {
        CcmDumpPlan.DoorControllerForAddress(address).ShouldBe(controller);
    }

    [TestMethod]
    [DataRow(0x0FFF)]
    [DataRow(0x1000)] // the module's own EEPROM, not a door
    [DataRow(0x6000)]
    public void AddressesOutsideTheDoorWindowsHaveNoDoor(int address)
    {
        CcmDumpPlan.DoorControllerForAddress(address).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(-1, 0x200)]
    [DataRow(0x10000, 0x200)]
    [DataRow(0, 0)]
    [DataRow(0xFF00, 0x200)]
    public void RangesOutsideTheAddressSpaceAreRejected(int start, int length)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => CcmDumpPlan.Create("1C0959799F", start, length, preferFast: true));
    }
}
