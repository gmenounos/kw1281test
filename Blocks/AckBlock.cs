using System;
using System.Collections.Generic;

namespace BitFab.Kwp1281Test.Blocks
{
    internal class AckBlock : Block
    {
        public AckBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Console.WriteLine("Received ACK block");
        }
    }
}
