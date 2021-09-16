using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    class NakBlock : Block
    {
        public NakBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Log.WriteLine("Received NAK block");
        }
    }
}
