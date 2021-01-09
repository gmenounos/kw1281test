using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Blocks
{
    internal class CodingWscBlock : Block
    {
        public CodingWscBlock(List<byte> bytes) : base(bytes)
        {
            var data = bytes.Skip(4).ToList();

            SoftwareCoding = (data[0] * 256 + data[1]) / 2;
            WorkshopCode = data[2] * 256 + data[3];
        }

        public override string ToString()
        {
            return $"Software Coding {SoftwareCoding:d5}, Workshop Code: {WorkshopCode:d5}";
        }

        public int SoftwareCoding { get; }

        public int WorkshopCode { get; }
    }
}
