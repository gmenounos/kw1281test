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
                    mode = 'A';

                    sb.Append((char)b);
                }
                else
                {
                    if (mode == 'A')
                    {
                        sb.Append(' ');
                    }
                    mode = 'X';

                    sb.Append($"${b:X2} ");
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
        public static short GetShort(byte[] buf, int offset)
        {
            return (short)(buf[offset] + buf[offset + 1] * 256);
        }

        /// <summary>
        /// Big-Endian version of GetShort
        /// </summary>
        public static short GetShortBE(byte[] buf, int offset)
        {
            return (short)(buf[offset] * 256 + buf[offset + 1]);
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
    }
}
