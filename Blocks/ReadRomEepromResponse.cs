using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class ReadRomEepromResponse : Block
    {
        public ReadRomEepromResponse(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Console.Write("Received \"Read ROM/EEPROM Response\" block:");
            foreach (var b in Body)
            {
                Console.Write($" {b:X2}");
            }

            Console.WriteLine();
        }
    }
}
