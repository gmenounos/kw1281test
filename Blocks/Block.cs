using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Blocks
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

        /// <summary>
        /// Returns the body of the block, excluding the length, counter, title and end bytes.
        /// </summary>
        public IEnumerable<byte> Body => Bytes.Skip(3).Take(Bytes.Count - 4);

        public List<byte> Bytes { get; }

        public bool IsAckNak { get; protected set; } = false;
    }
}
