using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    class NakBlock : Block
    {
        public NakBlock(List<byte> bytes) : base(bytes)
        {
            IsAckNak = true;
            Dump();
        }

        private void Dump()
        {
            Console.WriteLine("Received NAK block");
        }
    }
}
