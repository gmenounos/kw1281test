using FluentAssertions;

namespace BitFab.KW1281Test.Tests
{
    [TestClass]
    public class TesterTests
    {
        [TestMethod]
        [DataRow("Nothing to see here")]
        [DataRow("1J0920927   KOMBI+WEGFAHRSP VDO V01", "1J0", "920", "927", "")] // No alpha suffix
        [DataRow("1J5920926C   KOMBI+WEGFAHRSP VDO V01", "1J5", "920", "926", "C")] // 1 letter suffix
        [DataRow("1J5920926CX   KOMBI+WEGFAHRSP VDO V01", "1J5", "920", "926", "CX")] // 2 letter suffix
        [DataRow("1JE920827   KOMBI+WEGFAHRSP VDO V01", "1JE", "920", "827", "")] // 1st group ends in a letter
        public void FindAndParsePartNumber_ReturnsExpectedGroups(
            string ecuInfo, params string[] expectedGroups)
        {
            string[] actualGroups = Tester.FindAndParsePartNumber(ecuInfo);

            actualGroups.Should().BeEquivalentTo(expectedGroups);
        }
    }
}