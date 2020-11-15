using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class AckBlock : Block
    {
        public AckBlock(List<byte> bytes) : base(bytes)
        {
            // Dump();
        }

        private void Dump()
        {
            Logger.WriteLine("Received ACK block");
        }
    }
}
