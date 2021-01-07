using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class ActuatorTestResponseBlock : Block
    {
        public ActuatorTestResponseBlock(List<byte> bytes) : base(bytes)
        {
            Dump();
        }

        public string ActuatorName
        {
            get
            {
                var id = Utils.Dump(Body).Trim();
                if (_idToName.TryGetValue(id, out string name))
                {
                    return name;
                }
                return id;
            }
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

        private static readonly Dictionary<string, string> _idToName = new()
        {
            { "02 96", "Tachometer" },
            { "02 95", "Coolant Temp Gauge" },
            { "02 98", "Fuel Gauge" },
            { "02 97", "Speedometer" },
            { "03 1E", "Segment Test" },
            { "02 72", "Glow Plug Warning" },
            { "02 F2", "Coolant Temp Warning" },
            { "02 F3", "Oil Pressure Warning" },
            { "01 F5", "Oil Level Warning" },
            { "04 16", "Brake Pad Warning" },
            { "04 3B", "Low Washer Fluid Warning" },
            { "04 3A", "Low Fuel Warning" },
            { "01 F6", "Immobilizer Warning" },
            { "04 17", "Brake Warning" },
            { "02 99", "Seatbelt Warning" },
            { "02 9A", "Gong" },
            { "03 FF", "Chime" },
            { "04 AB", "End" },
        };
    }
}
