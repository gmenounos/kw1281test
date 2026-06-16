using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class SecurityAccessMode2Block : Block
    {
        public SecurityAccessMode2Block(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Log.Write("Received \"Security Access Mode 2\" block:");
            foreach (var b in Body)
            {
                Log.Write($" ${b:X2}");
            }

            Log.WriteLine();
        }
    }
}
