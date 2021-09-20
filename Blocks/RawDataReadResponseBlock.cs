using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class RawDataReadResponseBlock : Block
    {
        public RawDataReadResponseBlock(List<byte> bytes) : base(bytes)
        {
        }

        public override string ToString()
        {
            return $"Raw Data:{Utils.DumpDecimal(Body)}";
        }
    }
}