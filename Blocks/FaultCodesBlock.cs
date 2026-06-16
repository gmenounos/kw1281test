using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Blocks
{
    internal class FaultCodesBlock : Block
    {
        public FaultCodesBlock(List<byte> bytes) : base(bytes)
        {
            FaultCodes = new();

            IEnumerable<byte> data = Body;

            while (true)
            {
                var code = data.Take(3).ToArray();
                if (code.Length == 0)
                {
                    break;
                }

                var dtc = code[0] * 256 + code[1];
                var status = code[2];

                var faultCode = new FaultCode(dtc, status);
                if (!faultCode.Equals(FaultCode.None))
                {
                    FaultCodes.Add(faultCode);
                }

                data = data.Skip(3);
            }
        }

        public List<FaultCode> FaultCodes { get; }
    }

    internal struct FaultCode
    {
        public FaultCode(int dtc, int status)
        {
            Dtc = dtc;
            Status = status;
        }

        public override string ToString()
        {
            var status1 = Status & 0x7F;
            var status2 = (Status >> 7) * 10;
            return $"{Dtc:d5} - {status1:d2}-{status2:d2}";
        }

        public int Dtc { get; }

        public int Status { get; }

        public static readonly FaultCode None = new FaultCode(0xFFFF, 0x88);
    }
}
