using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BitFab.KW1281Test
{
    internal static class Utils
    {
        public static string Dump(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($" {b:X2}");
            }
            return sb.ToString();
        }

        // TODO: Merge with Dump()
        public static string DumpBytes(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($"${b:X2} ");
            }
            return sb.ToString();
        }

        public static string DumpDecimal(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append($" {b:D3}");
            }
            return sb.ToString();
        }

        public static string DumpAscii(IEnumerable<byte> bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        public static string DumpMixedContent(IEnumerable<byte> content)
        {
            char mode = '?';
            var sb = new StringBuilder();
            foreach (var b in content)
            {
                if (b >= 32 && b <= 126)
                {
                    if (mode == 'X')
                    {
                        sb.Append(' ');
                    }
                    mode = 'A';

                    sb.Append((char)b);
                }
                else
                {
                    if (mode != '?')
                    {
                        sb.Append(' ');
                    }
                    mode = 'X';

                    sb.Append($"${b:X2}");
                }
            }
            return sb.ToString();
        }

        public static uint ParseUint(string numberString)
        {
            uint number;

            if (numberString.StartsWith("$"))
            {
                number = uint.Parse(numberString[1..], NumberStyles.HexNumber);
            }
            else if (numberString.ToLower().StartsWith("0x"))
            {
                number = uint.Parse(numberString[2..], NumberStyles.HexNumber);
            }
            else
            {
                number = uint.Parse(numberString);
            }

            return number;
        }

        /// <summary>
        /// Little-Endian
        /// </summary>
        public static ushort GetShort(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] + buf[offset + 1] * 256);
        }

        /// <summary>
        /// Big-Endian version of GetShort
        /// </summary>
        public static ushort GetShortBE(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] * 256 + buf[offset + 1]);
        }

        /// <summary>
        /// Little-Endian Binary Coded Decimal
        /// </summary>
        public static ushort GetBcd(byte[] buf, int offset)
        {
            var binary = GetShort(buf, offset);

            ushort bcd = (ushort)
                (
                    (binary >> 12) * 1000 +
                    ((binary >> 8) & 0x0F) * 100 +
                    ((binary >> 4) & 0x0F) * 10 +
                    (binary & 0x0F)
                );

            return bcd;
        }

        /// <summary>
        /// Little-Endian
        /// </summary>
        public static byte[] GetBytes(uint value)
        {
            var bytes = new byte[4];

            bytes[0] = (byte)(value & 0xFF);
            value >>= 8;
            bytes[1] = (byte)(value & 0xFF);
            value >>= 8;
            bytes[2] = (byte)(value & 0xFF);
            value >>= 8;
            bytes[3] = (byte)(value);

            return bytes;
        }

        /// <summary>
        /// Rotate a byte right.
        /// </summary>
        public static (byte result, bool carry) RightRotate(
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

        /// <summary>
        /// Left-Rotate a value.
        /// </summary>
        public static (byte result, bool carry) LeftRotate(
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

        public static (byte result, bool carry) SubtractWithCarry(
            byte minuend, byte subtrahend, bool carry)
        {
            int result = minuend - subtrahend - (carry ? 0 : 1);
            carry = !(result < 0);

            return ((byte)result, carry);
        }

        public static byte AdjustParity(
            byte b, bool evenParity)
        {
            bool parity = !evenParity; // XORed with each bit to calculate parity bit

            for (int i = 0; i < 7; i++)
            {
                bool bit = ((b >> i) & 1) == 1;
                parity ^= bit;
            }

            if (parity)
            {
                return (byte)(b | 0x80);
            }
            else
            {
                return (byte)(b & 0x7F);
            }
        }
    }
}
