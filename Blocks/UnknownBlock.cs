using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class UnknownBlock : Block
    {
        public UnknownBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Log.Write($"Received ${Title:X2} block:");
            foreach (var b in Bytes)
            {
                Log.Write($" 0x{b:X2}");
            }
            Log.WriteLine();
        }
    }
}