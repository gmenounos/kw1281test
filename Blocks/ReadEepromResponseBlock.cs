using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class ReadEepromResponseBlock : Block
    {
        public ReadEepromResponseBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Log.Write("Received \"Read EEPROM Response\" block:");
            foreach (var b in Body)
            {
                Log.Write($" {b:X2}");
            }

            Log.WriteLine();
        }
    }
}