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
        /// Returns the entire raw block bytes.
        /// </summary>
        public List<byte> Bytes { get; }

        public byte Title => Bytes[2];

        /// <summary>
        /// Returns the body of the block, excluding the length, counter, title and end bytes.
        /// </summary>
        public List<byte> Body => Bytes.Skip(3).Take(Bytes.Count - 4).ToList();

        public bool IsAck => Title == (byte)BlockTitle.ACK;

        public bool IsNak => Title == (byte)BlockTitle.NAK;

        public bool IsAckNak => IsAck || IsNak;
    }
}
