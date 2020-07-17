using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class AckBlock : Block
    {
        public AckBlock(List<byte> bytes) : base(bytes)
        {
            IsAckNak = true;
            // Dump();
        }

        private void Dump()
        {
            Console.WriteLine("Received ACK block");
        }
    }
}
