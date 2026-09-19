namespace BitFab.KW1281Test.EDC15
{
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
        /// This algorithm borrowed from https://github.com/fjvva/ecu-tool
        /// Thanks to Javier Vazquez Vidal https://github.com/fjvva
        /// </summary>
        public static byte[] ComputeLvl41Key(long key, long key3, byte[] buf)
        {
            // long Key3 = 0x3800000;
            long tempstring = buf[0];
            tempstring <<= 8;
            var keyread1 = tempstring + buf[1];
            tempstring = buf[2];
            tempstring <<= 8;
            var keyread2 = tempstring + buf[3];
            // Process the algorithm
            var key2 = key;
            key2 &= 0xFFFF;
            key >>= 16;
            var key1 = key;
            for (byte counter = 0; counter < 5; counter++)
            {
                var keyTemp = keyread1;
                keyTemp &= 0x8000;
                keyread1 <<= 1;
                var temp1 = keyTemp & 0x0FFFF;
                if (temp1 == 0)
                {
                    var temp2 = keyread2 & 0xFFFF;
                    var temp3 = keyTemp & 0xFFFF0000;
                    keyTemp = temp2 + temp3;
                    keyread1 &= 0xFFFE;
                    temp2 = keyTemp & 0xFFFF;
                    temp2 >>= 0x0F;
                    keyTemp &= 0xFFFF0000;
                    keyTemp += temp2;
                    keyread1 |= keyTemp;
                    keyread2 <<= 0x01;
                }
                else
                {
                    keyTemp = keyread2 + keyread2;
                    keyread1 &= 0xFFFE;
                    var temp2 = keyTemp & 0xFF;
                    temp2 |= 1;
                    var temp3 = key3 & 0xFFFFFF00;
                    key3 = temp2 + temp3;
                    key3 &= 0xFFFF00FF;
                    key3 |= keyTemp;
                    temp2 = keyread2 & 0xFFFF;
                    temp3 = keyTemp & 0xFFFF0000;
                    keyTemp = temp2 + temp3;
                    temp2 = keyTemp & 0xFFFF;
                    temp2 >>= 0x0F;
                    keyTemp &= 0xFFFF0000;
                    keyTemp += temp2;
                    keyTemp |= keyread1;
                    key3 ^= key1;
                    keyTemp ^= key2;
                    keyread2 = key3;
                    keyread1 = keyTemp;
                }
            }
            //Done with the key generation
            keyread2 &= 0xFFFF; // Clean first and second word from garbage
            keyread1 &= 0xFFFF;

            var keybuf = new byte[4];
            keybuf[1] = (byte)keyread1;
            keyread1 >>= 8;
            keybuf[0] = (byte)keyread1;
            keybuf[3] = (byte)keyread2;
            keyread2 >>= 8;
            keybuf[2] = (byte)keyread2;

            return keybuf;
        }
    }
}
