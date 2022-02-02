using System;

namespace BitFab.KW1281Test.Cluster
{
    public class VdoKeyFinder
    {
        /// <summary>
        /// Takes a 10-byte seed block and generates an 8-byte key block.
        /// </summary>
        public static byte[] FindKey(byte[] seed)
        {
            if (seed.Length != 10)
            {
                throw new InvalidOperationException(
                    $"Unexpected seed length: {seed.Length} (Expected 10)");
            }
            if (seed[8] != 0x01 || seed[9] != 0x00)
            {
                Log.WriteLine(
                    $"Unexpected seed suffix: ${seed[8]:X2} ${seed[9]:X2}, (Expected $01 $00)");
            }

            var key = CalculateKey(new byte[] { seed[1], seed[3], seed[5], seed[7] });

            return new byte[] { 0x07, key[0], key[1], 0x00, key[2], 0x00, key[3], 0x00 };
        }

        /// <summary>
        /// Takes a 4-byte seed and calculates a 4-byte key.
        /// </summary>
        private static byte[] CalculateKey(byte[] seed)
        {
            var work = new byte[] { 0x07, seed[0], seed[1], seed[2], seed[3], 0x00, 0x00 };

            var obfu = new byte[] { 0x55, 0x16, 0xa8, 0x94 };

            Scramble(work);

            var y = work[1] & 0x07;
            var temp = y + 1;

            byte a = LeftRotate(0x01, y);

            do
            {
                var set = ((obfu[0] ^ obfu[1] ^ obfu[2] ^ obfu[3]) & 0x40) != 0;
                obfu[3] = SetOrClearBits(obfu[3], a, set);

                RightRotateFirst4Bytes(obfu, 0x01);
                temp--;
            }
            while (temp != 0);

            for (var x = 0; x < 2; x++)
            {
                work[5] = work[1];
                work[1] ^= work[3];

                work[6] = work[2];
                work[2] ^= work[4];

                work[4] = work[6];
                work[3] = work[5];

                LeftRotateMiddle2Bytes(work, (byte)(work[3] & 0x07));

                y = x << 1;

                bool carry = true;
                (work[1], carry) = SubtractWithCarry(work[1], obfu[y], carry);
                (work[2], carry) = SubtractWithCarry(work[2], obfu[y + 1], carry);
            }

            Scramble(work);

            return new byte[] { work[1], work[2], work[3], work[4] };
        }

        private static void Scramble(byte[] key)
        {
            key[5] = key[1];
            key[1] = key[2];
            key[2] = key[4];
            key[4] = key[3];
            key[3] = key[5];
        }

        private static byte SetOrClearBits(
            byte value, byte mask, bool set)
        {
            if (set)
            {
                return (byte)(value | mask);
            }
            else
            {
                return (byte)(value & (byte)(mask ^ 0xFF));
            }
        }

        /// <summary>
        /// Right-Rotate the first 4 bytes of a buffer count times.
        /// </summary>
        private static void RightRotateFirst4Bytes(
            byte[] buf, int count)
        {
            while (count != 0)
            {
                bool carry = (buf[0] & 0x01) != 0;
                (buf[3], carry) = RightRotate(buf[3], carry);
                (buf[2], carry) = RightRotate(buf[2], carry);
                (buf[1], carry) = RightRotate(buf[1], carry);
                (buf[0], carry) = RightRotate(buf[0], carry);
                count--;
            }
        }

        /// <summary>
        /// Rotate a byte right.
        /// </summary>
        private static (byte result, bool carry) RightRotate(
            byte value, bool carry)
        {
            var newCarry = (value & 0x01) != 0;
            if (carry)
            {
                return ((byte)((value >> 1) | 0x80), newCarry);
            }
            else
            {
                return ((byte)(value >> 1), newCarry);
            }
        }

        private static void LeftRotateMiddle2Bytes(
            byte[] key, int count)
        {
            while (count != 0)
            {
                bool carry = (key[2] & 0x80) != 0;
                (key[1], carry) = LeftRotate(key[1], carry);
                (key[2], carry) = LeftRotate(key[2], carry);
                count--;
            }
        }

        /// <summary>
        /// Left-Rotate a value count-times.
        /// </summary>
        private static byte LeftRotate(
            byte value, int count)
        {
            while (count != 0)
            {
                bool carry = (value & 0x80) != 0;
                (value, carry) = LeftRotate(value, carry);
                count--;
            }

            return value;
        }

        /// <summary>
        /// Left-Rotate a value.
        /// </summary>
        private static (byte result, bool carry) LeftRotate(
            byte value, bool carry)
        {
            var newCarry = (value & 0x80) != 0;
            if (carry)
            {
                return ((byte)((value << 1) | 0x01), newCarry);
            }
            else
            {
                return ((byte)(value << 1), newCarry);
            }
        }

        private static (byte result, bool carry) SubtractWithCarry(
            byte minuend, byte subtrahend, bool carry)
        {
            int result = minuend - subtrahend - (carry ? 0 : 1);
            carry = !(result < 0);

            return ((byte)result, carry);
        }
    }
}
