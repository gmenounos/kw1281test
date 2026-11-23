using BitFab.KW1281Test.Kwp2000;
using Shouldly;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class Kwp2000AddressingTests
{
    [TestMethod]
    public void AbsTriesWakeupAddressThenJ2178Address()
    {
        Kwp2000Addressing.Candidates(0x03, 2027).ShouldBe(new byte[] { 0x03, 0x28 });
    }

    [TestMethod]
    [DataRow((byte)0x01, 2031, (byte)0x10)] // EDC16
    [DataRow((byte)0x01, 2027, (byte)0x01)] // EDC15
    [DataRow((byte)0x17, 2027, (byte)0x17)] // no fallback: wakeup address only
    public void ConfirmedOrUnknownAddressesAreNotProbed(byte wakeup, int keyword, byte expected)
    {
        Kwp2000Addressing.Candidates(wakeup, keyword).ShouldBe(new[] { expected });
    }
}
