using System;
using System.Collections.Generic;

namespace BitFab.KW1281Test.Blocks
{
    internal class CustomBlock : Block
    {
        public CustomBlock(List<byte> bytes) : base(bytes)
        {
            // Dump();
        }

        private void Dump()
        {
            Console.Write("Received Custom block:");
            for (var i = 3; i < Bytes.Count - 1; i++)
            {
                Console.Write($" {Bytes[i]:X2}");
            }

            Console.WriteLine();
        }
    }
}