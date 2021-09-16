using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class AdaptationResponseBlock : Block
    {
        public AdaptationResponseBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        public byte ChannelNumber => Body[0];

        public ushort ChannelValue => (ushort)(Body[1] * 256 + Body[2]);

        private void Dump()
        {
            Log.Write("Received \"Adaptation Response\" block:");
            foreach (var b in Body)
            {
                Log.Write($" {b:X2}");
            }

            Log.WriteLine();
        }
    }
}
