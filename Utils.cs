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
    }
}
