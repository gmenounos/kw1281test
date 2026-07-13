using Shouldly;

namespace BitFab.KW1281Test.Tests;

[TestClass]
public class UtilsTests
{
    [TestMethod]
    [DataRow(new byte[0], "")]
    [DataRow(new byte[] { 31 }, "$1F")]
    [DataRow(new byte[] { 32 }, " ")]
    [DataRow(new byte[] { 126 }, "~")]
    [DataRow(new byte[] { 127 }, "$7F")]
    [DataRow(new byte[] { (byte)'A', (byte)'B' }, "AB")]
    [DataRow(new byte[] { (byte)'A', (byte)'B', 0x07 }, "AB $07")]
    [DataRow(new byte[] { (byte)'A', (byte)'B', 0x07, 0x09 }, "AB $07 $09")]
    [DataRow(new byte[] { (byte)'A', (byte)'B', 0x07, 0x09, (byte)'C', (byte)'D' }, "AB $07 $09 CD")]
    [DataRow(new byte[] { 0x03, 0x04, (byte)'A', (byte)'B', 0x05, 0x06 }, "$03 $04 AB $05 $06")]
    public void DumpMixedContent(byte[] content, string expectedDump)
    {
        var actualDump = Utils.DumpMixedContent(content);
        actualDump.ShouldBe(expectedDump);
    }
}
