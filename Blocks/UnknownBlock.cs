using System;
using System.Collections.Generic;

namespace BitFab.Kwp1281Test.Blocks
{
    internal class UnknownBlock : Block
    {
        public UnknownBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Console.Write("Received unknown block:");
            foreach (var b in Bytes)
            {
                Console.Write($" 0x{b:X2}");
            }
            Console.WriteLine();
        }
    }
}