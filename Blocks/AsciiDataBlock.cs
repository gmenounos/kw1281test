using System;
using System.Collections.Generic;
using System.Text;

namespace BitFab.KW1281Test.Blocks
{
    internal class AsciiDataBlock : Block
    {
        public AsciiDataBlock(List<byte> bytes) : base(bytes)
        {
            // Dump();
        }

        public bool MoreDataAvailable => Bytes[3] > 0x7F;

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var b in Body)
            {
                sb.Append((char)(b & 0x7F));
            }
            return sb.ToString();
        }

        private void Dump()
        {
            Console.Write($"Received Ascii data block: \"{ToString()}\"");

            if (MoreDataAvailable)
            {
                Console.Write(" (More data available via ReadIdent)");
            }

            Console.WriteLine();
        }
    }
}
