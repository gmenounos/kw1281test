using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test
{
    internal static class Utils
    {
        public static string Dump(IEnumerable<byte> bytes, bool useDollarSign = false)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                if (useDollarSign)
                {
                    sb.Append($"${b:X2} ");
                }
                else
                {
                    sb.Append($" {b:X2}");
                }
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
                if (b is >= 32 and <= 126)
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

            if (numberString.StartsWith('$'))
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
        public static ushort GetShort(ReadOnlySpan<byte> buf, int offset)
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
            carry = result >= 0;

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

        /// <summary>
        /// Returns mileage in decimal format converted from hex out of a dump
        ///   Std location for mileage in VWK501: starting from 0xFC
        ///   Std location for mileage in VWK503: starting from 0x13A
        ///   Location can vary depending on the cluster type
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static int MileageHexToDecimal(string input)
        {
            // Split the input string into an array of hexadecimal strings
            var hexValues = input.Split(' ').ToList();
            var inputLength = hexValues.Count;
            var charCount = input.Trim().Replace(" ", "").Length;

            if (inputLength != 1 && inputLength != 8 && inputLength != 16 || charCount != 32)
            {
                throw new ArgumentException("Input must be 32 characters and in one of the following formats: 'FFFFFFFF...', 'FFFF FFFF F...', 'FF FF FF FF F...'.");
            }

            if (inputLength == 16)
            {
                for (int i = 0; i < inputLength; i += 2)
                {
                    hexValues[i / 2] = hexValues[i] + hexValues[i + 1];
                }
                hexValues = hexValues.Take(8).ToList();
            }
            else if (inputLength == 1)
            {
                var chunkSize = 4;
                hexValues = Enumerable.Range(0, (input.Length + chunkSize - 1) / chunkSize)
                                      .Select(i => input.Substring(i * chunkSize, Math.Min(chunkSize, input.Length - i * chunkSize)))
                                      .ToList();
            }

            // Sum the bitwise inverted 16-bit signed integers
            int sum = hexValues.Select(hex => ~BitConverter.ToInt16(new byte[] {
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16)
            }, 0)).Sum();

            // Multiply the sum by 2, since odometer values are stored as half the actual value
            return sum * 2;
        }

        /// <summary>
        /// Returns mileage in hex format, usable in dumps, converted decimal
        ///   Std location for mileage in VWK501: starting from 0xFC
        ///   Std location for mileage in VWK503: starting from 0x13A
        ///   Location can vary depending on the cluster type
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string MileageDecimalToHex(int input)
        {
            // Divide the input by 2, since odometer values are stored as half the actual value
            int halfValue = input / 2;

            // starting point per byte is 65535 / FFFF
            var resultList = Enumerable.Repeat(65535, 8).ToList();

            // Decrease each byte block by 1 until the half value is reached
            for (int i = 0; i < halfValue; i++)
            {
                resultList[i % 8] -= 1;
            }

            var resultHexListBeforeSwap = resultList.Select(b => b.ToString("X4")).ToList();

            // Swap bitwise inverted integers
            var resultHexListAfterSwap = resultHexListBeforeSwap
                .Select(hex => hex.Substring(2, 2) + hex.Substring(0, 2))
                .ToList();

            // Join the array of hexadecimal strings into a single string with space separator
            return string.Join(" ", resultHexListAfterSwap);
        }
    }
}
