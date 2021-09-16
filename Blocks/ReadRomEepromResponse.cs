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
            Log.Write("Received \"Read ROM/EEPROM Response\" block:");
            foreach (var b in Body)
            {
                Log.Write($" {b:X2}");
            }

            Log.WriteLine();
        }
    }
}
