using System.Collections.Generic;

namespace BitFab.KW1281Test.Kwp2000
{
    /// <summary>
    /// Maps the 5-baud wakeup address a controller answered at to the physical address it
    /// expects in the ISO 14230-2 message header (the "target" byte in an 8x/Cx-format header,
    /// and the "source" byte of its replies).
    ///
    /// For most VAG KWP2000 controllers the two are the same byte. They are NOT the same for
    /// EDC16: the 5-baud wakeup goes to 0x01 but every framed request afterwards must be
    /// addressed to 0x10 (ECU replies come from 0x10). A request addressed to 0x01 is silently
    /// ignored by that ECU -- which shows up as the "handshake completes, first request gets
    /// nothing" failure. EDC15 at the same wakeup address 0x01 keeps using 0x01, so the wakeup
    /// address alone can't pick the header address.
    ///
    /// The discriminator used is the KWP2000 keyword the controller returned in its wakeup key
    /// bytes -- a value the controller itself sends, not something inferred: EDC15 answers
    /// KW 2027 (key bytes 6B 8F), EDC16 answers KW 2031 (EF 8F). Anything not listed keeps the
    /// wakeup address, which is the ISO convention.
    /// </summary>
    internal static class Kwp2000Addressing
    {
        /// <summary>(wakeup address, KWP2000 keyword) -> header/physical address.</summary>
        private static readonly IReadOnlyDictionary<(byte WakeupAddress, int Keyword), byte> Confirmed =
            new Dictionary<(byte, int), byte>
            {
                // EDC16: 5-baud 0x01, KW 2031, header 0x10.
                [(0x01, 2031)] = 0x10,
                // EDC15: 5-baud 0x01, KW 2027, header 0x01 (same address).
                [(0x01, 2027)] = 0x01,
            };

        /// <summary>
        /// Header address to use for a controller that answered a 5-baud wakeup at
        /// <paramref name="wakeupAddress"/> with KWP2000 keyword <paramref name="keyword"/>
        /// (the value <see cref="IKwpCommon.WakeUp"/> returns).
        /// </summary>
        public static byte HeaderAddress(byte wakeupAddress, int keyword)
        {
            return Confirmed.TryGetValue((wakeupAddress, keyword), out var address)
                ? address
                : wakeupAddress;
        }
    }
}
