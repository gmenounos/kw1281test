using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class ActuatorTestResponseBlock : Block
    {
        public ActuatorTestResponseBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        private void Dump()
        {
            Logger.Write("Received \"Actuator Test Response\" block:");
            foreach (var b in Body)
            {
                Logger.Write($" {b:X2}");
            }

            Logger.WriteLine();
        }
    }
}
