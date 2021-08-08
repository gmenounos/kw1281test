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
            Logger.Write($"Received ${Title:X2} block:");
            foreach (var b in Bytes)
            {
                Logger.Write($" 0x{b:X2}");
            }
            Logger.WriteLine();
        }
    }
}