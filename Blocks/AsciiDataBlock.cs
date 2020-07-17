using System;
using System.Collections.Generic;

namespace BitFab.Kwp1281Test.Blocks
{
    internal class AsciiDataBlock : Block
    {
        public AsciiDataBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Console.Write("Received Ascii data block: \"");
            for (var i = 3; i < Bytes.Count - 1; i++)
            {
                Console.Write((char)(Bytes[i] & 0x7F));
            }
            Console.Write("\"");

            if (Bytes[3] > 0x7F)
            {
                Console.Write(" (More data available via ReadIdent)");
            }

            Console.WriteLine();
        }
    }
}
