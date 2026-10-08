namespace BitFab.KW1281Test.EDC15;

/// <summary>
/// Seed/key security-access algorithms shared across EDC15 tooling. Pulled out of
/// <see cref="Edc15VM"/> (where this exact math already lived, as its own private
/// <c>LVL41Auth</c> method, doing EEPROM-only access via <c>accessMode 0x41</c>) so
/// <see cref="Edc15FlashVM"/> can reuse the identical algorithm for flash access — same
/// shape, different key constant per EDC15 sub-variant (P/V/VM+). This is a pure extraction:
/// the math itself is unchanged from Edc15VM's original private method.
/// </summary>
internal static class Edc15KeyAlgorithms
{
    /// <summary>
    /// Computes the EDC15 security access key (level 0x41) from the 4-byte seed.
    ///
    /// The seed is split into two 16-bit words which are mixed over five rounds.
    /// Each round shifts the low word left, then either carries the top bit of
    /// the high word into bit 0 of the low word (plain round), or performs a
    /// deeper mix that also folds in the two halves of the fixed auth key and
    /// rebuilds the control word (mixing round).
    ///
    /// Borrowed from https://github.com/fjvva/ecu-tool and simplified.
    /// Thanks to Javier Vazquez Vidal (https://github.com/fjvva).
    /// </summary>
    /// <param name="authKey">Fixed auth key; used as two 16-bit halves.</param>
    /// <param name="controlWord">Mutable working register of the mixer (0x03800000).</param>
    /// <param name="seed">The 4-byte seed received from the ECU.</param>
    /// <returns>The 4-byte key, packed as two big-endian 16-bit words.</returns>
    internal static byte[] ComputeLvl41Key(uint authKey, uint controlWord, byte[] seed)
    {
        // Split the seed into two 16-bit words (big-endian).
        uint lowWord = (uint)(seed[0] << 8) + seed[1];
        uint highWord = (uint)(seed[2] << 8) + seed[3];

        // Split the fixed auth key into its two 16-bit halves.
        uint keyHi = authKey >> 16;
        uint keyLo = authKey & 0xFFFF;

        for (var round = 0; round < 5; round++)
        {
            // Capture the low word's top bit before shifting it left; it
            // selects the round variant below.
            var topBit = lowWord & 0x8000;
            lowWord <<= 1;

            if (topBit == 0)
            {
                // Plain round: carry the top bit of the high word into bit 0
                // of the low word, and shift the high word left as well.
                lowWord &= 0xFFFE;
                lowWord |= (highWord & 0x8000) >> 15;
                highWord <<= 1;
            }
            else
            {
                // Mixing round: double the high word and rebuild the control
                // word around its low byte.
                var doubleHigh = highWord + highWord;
                lowWord &= 0xFFFE;

                // Low byte becomes (low byte of the doubled high word) | 1,
                // with the upper bytes of the old control word preserved.
                controlWord = ((doubleHigh & 0xFF) | 1) + (controlWord & 0xFFFFFF00);
                controlWord = (controlWord & 0xFFFF00FF) | doubleHigh;

                // Fold the overflow bits of the doubling back in, use the top
                // bit of the result for bit 0, then overlay the low word.
                var foldedHigh = (highWord & 0xFFFF) + (doubleHigh & 0xFFFF0000);
                var newLowWord = (foldedHigh & 0xFFFF0000) + ((foldedHigh & 0xFFFF) >> 15);
                newLowWord |= lowWord;

                // Mix in the two halves of the fixed auth key.
                controlWord ^= keyHi;
                newLowWord ^= keyLo;

                highWord = controlWord;
                lowWord = newLowWord;
            }
        }

        // Truncate to 16 bits and pack the key, big-endian per word.
        lowWord &= 0xFFFF;
        highWord &= 0xFFFF;

        return
        [
            (byte)(lowWord >> 8),   // low word, high byte
            (byte)lowWord,          // low word, low byte
            (byte)(highWord >> 8),  // high word, high byte
            (byte)highWord          // high word, low byte
        ];
    }
}
