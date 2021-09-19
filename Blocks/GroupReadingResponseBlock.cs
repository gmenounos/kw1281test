using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Blocks
{
    internal class GroupReadingResponseBlock : Block
    {
        public GroupReadingResponseBlock(List<byte> bytes) : base(bytes)
        {
            SensorValues = new List<SensorValue>();

            var bodyBytes = new List<byte>(Body);
            while (bodyBytes.Count > 2)
            {
                var valueBytes = bodyBytes.Take(3).ToArray();
                SensorValues.Add(
                    new SensorValue(valueBytes[0], valueBytes[1], valueBytes[2]));
                bodyBytes = bodyBytes.Skip(3).ToList();
            }

            if (bodyBytes.Count > 0)
            {
                throw new InvalidOperationException(
                    $"GroupReadingResponse body ({Utils.DumpBytes(Body)}) should be a multiple of 3 bytes long.");
            }
        }

        public List<SensorValue> SensorValues { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach(var sensorValue in SensorValues)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }
                sb.Append(sensorValue.ToString());
            }

            return sb.ToString();
        }
    }
}
 