using System;
using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Cluster
{
    public static class VdoKeyFinder
    {
        /// <summary>
        /// Takes a 10-byte seed block and desired access level and generates an 8-byte key block.
        /// </summary>
        public static byte[] FindKey(byte[] seed, int accessLevel)
        {
            if (seed.Length != 10)
            {
                throw new InvalidOperationException(
                    $"Unexpected seed length: {seed.Length} (Expected 10)");
            }

            byte[] secret;
            switch (seed[8])
            {
                case 0x01 when seed[9] == 0x00:
                    secret = VWK501Secrets[accessLevel];
                    break;
                case 0x03 when seed[9] == 0x00:
                case 0x09 when seed[9] == 0x00:
                    secret = VSQX01Secrets[accessLevel];
                    break;
                default:
                    Log.WriteLine(
                        $"Unexpected seed suffix: ${seed[8]:X2} ${seed[9]:X2}");
                    secret = VWK501Secrets[accessLevel]; // Try something
                    break;
            }

            Log.WriteLine($"Access level {accessLevel} secret: {Utils.DumpBytes(secret)}");
            
            var key = CalculateKey(
                [seed[1], seed[3], seed[5], seed[7]],
                secret);

            return [(byte)accessLevel, key[0], key[1], 0x00, key[2], 0x00, key[3], 0x00, 0x00];
        }

        /// <summary>
        /// Table of secrets, one for each access level.
        /// </summary>
        private static readonly byte[][] VWK501Secrets =
        [
            [0xe5, 0x7c, 0x20, 0xb3],   // AccessLevel 0
            [0x67, 0xb8, 0xf0, 0xe2],
            [0x59, 0xd0, 0x4f, 0xcb],
            [0x46, 0x83, 0xb6, 0x27],
            [0xc9, 0xde, 0xe3, 0xca],
            [0x7f, 0x50, 0x44, 0xbc],
            [0x4b, 0xd0, 0x7f, 0xad],
            [0x55, 0x16, 0xa8, 0x94]    // AccessLevel 7
        ];
        
        /// <summary>
        /// Table of secrets, one for each access level.
        /// </summary>
        private static readonly byte[][] VSQX01Secrets =
        [
            [0x4c, 0x29, 0x92, 0x1b],   // AccessLevel 0
            [0x42, 0x0a, 0x0b, 0x66],
            [0x1c, 0x4c, 0x91, 0x4d],
            [0xe2, 0xfd, 0xa2, 0x28],
            [0x48, 0x34, 0x58, 0x71],
            [0xb1, 0xf5, 0xd0, 0xb8],
            [0xac, 0xfc, 0x5e, 0x6c],
            [0x98, 0xe1, 0x56, 0x5f]    // AccessLevel 7
        ];
        
        /// <summary>
        /// Takes a 4-byte seed and calculates a 4-byte key.
        /// </summary>
        private static byte[] CalculateKey(
            IReadOnlyList<byte> seed,
            IReadOnlyList<byte> secret)
        {
            var work = new byte[] { 0x07, seed[0], seed[1], seed[2], seed[3], 0x00, 0x00 };
            var secretBuf = secret.ToArray();

            Scramble(work);

            var y = work[1] & 0x07;
            var temp = y + 1;

            var a = LeftRotate(0x01, y);

            do
            {
                var set = ((secretBuf[0] ^ secretBuf[1] ^ secretBuf[2] ^ secretBuf[3]) & 0x40) != 0;
                secretBuf[3] = SetOrClearBits(secretBuf[3], a, set);

                RightRotateFirst4Bytes(secretBuf, 0x01);
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

                LeftRotateSecondAndThirdBytes(work, (byte)(work[3] & 0x07));

                y = x << 1;

                var carry = true;
                (work[1], carry) = Utils.SubtractWithCarry(work[1], secretBuf[y], carry);
                (work[2], _) = Utils.SubtractWithCarry(work[2], secretBuf[y + 1], carry);
            }

            Scramble(work);

            return [work[1], work[2], work[3], work[4]];
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
                var carry = (buf[0] & 0x01) != 0;
                (buf[3], carry) = Utils.RightRotate(buf[3], carry);
                (buf[2], carry) = Utils.RightRotate(buf[2], carry);
                (buf[1], carry) = Utils.RightRotate(buf[1], carry);
                (buf[0], _) = Utils.RightRotate(buf[0], carry);
                count--;
            }
        }

        private static void LeftRotateSecondAndThirdBytes(
            byte[] key, int count)
        {
            while (count != 0)
            {
                var carry = (key[2] & 0x80) != 0;
                (key[1], carry) = Utils.LeftRotate(key[1], carry);
                (key[2], _) = Utils.LeftRotate(key[2], carry);
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
                var carry = (value & 0x80) != 0;
                (value, _) = Utils.LeftRotate(value, carry);
                count--;
            }

            return value;
        }
    }
}
