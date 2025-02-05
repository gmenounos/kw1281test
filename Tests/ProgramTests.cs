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
        Assert.AreEqual(2, addressValuePairs.Count);
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
}