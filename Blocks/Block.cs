using System.Collections.Generic;

namespace BitFab.Kwp1281Test.Blocks
{
    /// <summary>
    /// KWP1281 block
    /// </summary>
    class Block
    {
        public Block(List<byte> bytes)
        {
            Bytes = bytes;
        }

        public List<byte> Bytes { get; }
    }
}
