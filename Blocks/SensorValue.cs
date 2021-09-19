using System;

namespace BitFab.KW1281Test.Blocks
{
    public class SensorValue
    {
        public byte SensorID { get; }

        public byte A { get; }

        public byte B { get; }

        public SensorValue(byte sensorID, byte a, byte b)
        {
            SensorID = sensorID;
            A = a;
            B = b;
        }

        public override string ToString()
        {
            // https://www.blafusel.de/obd/obd2_kw1281.html#7
            return SensorID switch
            {
                1 => $"{0.2 * A * B} rpm",
                5 => $"{A * (B-100) * 0.1} °C",
                7 => $"{0.01 * A * B} km/h",
                16 => $"{Convert.ToString(A, 2)} {Convert.ToString(B, 2)}",
                17 => $"\"{(char)A}{(char)B}\"",
                19 => $"{A * B * 0.01} l",
                21 => $"{0.001 * A * B} V",
                33 => $"{(A == 0 ? 100*B : 100*B/A)} %",
                36 => $"{A * 2560 + B * 10} km",
                39 => $"{B/256*A} mg/h",
                44 => $"{A:D2}:{B:D2}",
                64 => $"{A+B} \u2126", // Ohm
                _ => $"({SensorID} {A} {B})",
            };
        }
    }
}
