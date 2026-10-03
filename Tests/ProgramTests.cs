namespace BitFab.KW1281Test.Tests;

[TestClass]
public class ProgramTests
{
    [TestMethod]
    public void ParseAddressesAndValues_NumberOfArgumentsIsOdd_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(["1"], out var addressValuePairs);
        
        Assert.IsFalse(returnValue);
    }

    [TestMethod]
    public void ParseAddressesAndValues_ValidArguments_ReturnsList()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["1", "25", "17", "42"], out var addressValuePairs);
        
        Assert.IsTrue(returnValue);
        Assert.HasCount(2, addressValuePairs);
        Assert.AreEqual(new KeyValuePair<ushort, byte>(1, 25), addressValuePairs[0]);
        Assert.AreEqual(new KeyValuePair<ushort, byte>(17, 42), addressValuePairs[1]);
    }
    
    [TestMethod]
    public void ParseAddressesAndValues_AddressTooLarge_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["512", "25", "17", "42"], out var addressValuePairs);
        
        Assert.IsFalse(returnValue);
    }
    
    [TestMethod]
    public void ParseAddressesAndValues_ValueTooLarge_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["1", "25", "17", "256"], out var addressValuePairs);
        
        Assert.IsFalse(returnValue);
    }

    [TestMethod]
    public void ExtractFastEepromFlag_FlagAbsent_LeavesArgumentsAlone()
    {
        var remaining = Program.ExtractFastEepromFlag(
            ["COM1", "10400", "17", "DumpEeprom", "0", "2048"], out var fast);

        Assert.IsFalse(fast);
        Assert.AreEqual(6, remaining.Length);
        Assert.AreEqual("COM1", remaining[0]);
    }

    [TestMethod]
    [DataRow("-FastEeprom")]
    [DataRow("-fasteeprom")]
    [DataRow("--fast-eeprom")]
    public void ExtractFastEepromFlag_FlagPresent_IsRemovedFromArguments(string flag)
    {
        var remaining = Program.ExtractFastEepromFlag(
            [flag, "COM1", "10400", "17", "DumpEeprom"], out var fast);

        Assert.IsTrue(fast);
        CollectionAssert.AreEqual(
            new[] { "COM1", "10400", "17", "DumpEeprom" }, remaining);
    }

    [TestMethod]
    public void ExtractFastEepromFlag_FlagAfterPositionalArguments_StillFound()
    {
        // The flag has to work wherever it is typed, or it becomes a trap.
        var remaining = Program.ExtractFastEepromFlag(
            ["COM1", "10400", "17", "DumpEeprom", "-FastEeprom"], out var fast);

        Assert.IsTrue(fast);
        Assert.AreEqual(4, remaining.Length);
        Assert.AreEqual("DumpEeprom", remaining[^1]);
    }
}
