using System.Collections.Generic;
using System.Text;

namespace BitFab.KW1281Test
{
    internal static class Utils
    {
        public static string Dump(IEnumerable<byte> body)
        {
            var sb = new StringBuilder();
            foreach (var b in body)
            {
                sb.Append($" {b:X2}");
            }
            return sb.ToString();
        }
    }
}
