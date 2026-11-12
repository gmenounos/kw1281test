using BitFab.KW1281Test.Interface;

namespace BitFab.KW1281Test.Tests.Interface
{
    /// <summary>
    /// Frames captured from a Ross-Tech HEX-USB (serial RT000001) through a logging shim over
    /// Ross-Tech's own RT-USB.dll, and from VasyaDiagnost sessions. Both cables speak the same
    /// adapter-assisted wake-up protocol.
    /// </summary>
    [TestClass]
    public class AssistedWakeupProtocolTests
    {
        [TestMethod]
        public void ClusterWakeupIncludesOddParityAndUpdatedChecksum()
        {
            // Captured: request=530784971E035A -> connected to 1U0920811C on real hardware.
            CollectionAssert.AreEqual(
                Convert.FromHexString("530784971E035A"),
                AssistedWakeupProtocol.CreateWakeupRequest(0x17));
        }

        [TestMethod]
        public void TheRequestThatTheCableRefusedIsNotWhatWeSend()
        {
            // Captured: request=530784171E03DA (address byte without odd parity) -> 4D05FF03B4.
            var request = AssistedWakeupProtocol.CreateWakeupRequest(0x17);

            Assert.AreNotEqual(0x17, request[3], "the wake byte must carry odd parity");
            Assert.AreEqual(0x97, request[3]);
        }

        [TestMethod]
        public void AddressWithOddParityAlreadySetKeepsOriginalRequest()
        {
            CollectionAssert.AreEqual(
                Convert.FromHexString("530784461E038B"),
                AssistedWakeupProtocol.CreateWakeupRequest(0x46));
        }

        [TestMethod]
        public void ChecksumIsTheXorOfEveryPrecedingByte()
        {
            var request = AssistedWakeupProtocol.CreateWakeupRequest(0x17);

            Assert.AreEqual(0, AssistedWakeupProtocol.CalculateXor(request, request.Length));
        }

        [TestMethod]
        [DataRow("4D0A8490280000018AF0")] // Ross-Tech HEX-USB
        [DataRow("4D0A84BC280000018ADC")] // same cable, another session
        public void CapturedSuccessfulWakeupReportsKw1281(string frame)
        {
            var bytes = new Queue<byte>(Convert.FromHexString(frame));

            Assert.AreEqual(1281, AssistedWakeupProtocol.ReadWakeupResponse(bytes.Dequeue, "Ross-Tech"));
            Assert.IsEmpty(bytes);
        }

        [TestMethod]
        public void CapturedWakeupResponseLeavesFirstKw1281ByteUnread()
        {
            // VasyaDiagnost: the first byte of the controller's ident block follows the reply.
            var bytes = new Queue<byte>(Convert.FromHexString("4D0A849E280000018AFE0F"));

            Assert.AreEqual(1281, AssistedWakeupProtocol.ReadWakeupResponse(bytes.Dequeue, "VasyaDiagnost"));
            CollectionAssert.AreEqual(new byte[] { 0x0f }, bytes.ToArray());
        }

        [TestMethod]
        public void CapturedRejectionIsConsumedAndNamesTheCableAndTheErrorCode()
        {
            var bytes = new Queue<byte>(Convert.FromHexString("4D05FF03B4"));

            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => AssistedWakeupProtocol.ReadWakeupResponse(bytes.Dequeue, "Ross-Tech"));

            StringAssert.Contains(error.Message, "Ross-Tech");
            StringAssert.Contains(error.Message, "error $03");
            StringAssert.Contains(error.Message, "4D05FF03B4");
            Assert.IsEmpty(bytes);
        }

        [TestMethod]
        [DataRow("4D05FF03B5")]
        [DataRow("4D0A849E280000018AFF")]
        public void CorruptResponseIsNotAccepted(string frame)
        {
            var bytes = new Queue<byte>(Convert.FromHexString(frame));
            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => AssistedWakeupProtocol.ReadWakeupResponse(bytes.Dequeue, "VasyaDiagnost"));
            StringAssert.Contains(error.Message, "checksum");
        }

        [TestMethod]
        [DataRow("4D00", "length")]
        [DataRow("4D03", "length")]
        [DataRow("4D058400CC", "no keywords")]
        [DataRow("4D058500CD", "command")]
        [DataRow("53", "marker")]
        public void MalformedResponseIsNotDecodedAsKeywords(string frame, string reason)
        {
            var bytes = new Queue<byte>(Convert.FromHexString(frame));
            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => AssistedWakeupProtocol.ReadWakeupResponse(bytes.Dequeue, "VasyaDiagnost"));
            StringAssert.Contains(error.Message, reason);
        }

        [TestMethod]
        public void TruncatedResponsePreservesReadTimeout()
        {
            var bytes = new Queue<byte>(Convert.FromHexString("4D0A84"));
            Assert.ThrowsExactly<TimeoutException>(() => AssistedWakeupProtocol.ReadWakeupResponse(
                () => bytes.TryDequeue(out var value) ? value : throw new TimeoutException("Read timed out"),
                "VasyaDiagnost"));
        }

        [TestMethod]
        public void AdapterCommandChannelUsesTheCapturedBaudRate()
        {
            // Before the successful 53 07 84 ... the D2XX port was switched to 115200;
            // 9600/10400 is the K-line speed behind the cable firmware.
            Assert.AreEqual(115200, AssistedWakeupProtocol.AdapterCommandBaudRate);
        }
    }
}
