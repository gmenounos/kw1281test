using FluentAssertions;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class ProgramTests
{
    [TestMethod]
    public void ParseAddressesAndValues_NumberOfArgumentsIsOdd_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(["1"], out var addressValuePairs);
        
        returnValue.Should().BeFalse();
    }

    [TestMethod]
    public void ParseAddressesAndValues_ValidArguments_ReturnsList()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["1", "25", "17", "42"], out var addressValuePairs);
        
        returnValue.Should().BeTrue();
        addressValuePairs.Should().Equal(
        [
            new KeyValuePair<ushort, byte>(1, 25),
            new KeyValuePair<ushort, byte>(17, 42),
        ]);
    }
    
    [TestMethod]
    public void ParseAddressesAndValues_AddressTooLarge_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["512", "25", "17", "42"], out var addressValuePairs);
        
        returnValue.Should().BeFalse();
    }
    
    [TestMethod]
    public void ParseAddressesAndValues_ValueTooLarge_ReturnsFalse()
    {
        var returnValue = Program.ParseAddressesAndValues(
            ["1", "25", "17", "256"], out var addressValuePairs);
        
        returnValue.Should().BeFalse();
    }
}