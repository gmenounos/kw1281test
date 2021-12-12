using System;
using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Cluster
{
    internal class MotometerBOOCluster
    {
        public void GetClusterInfo()
        {
            Log.WriteLine("Sending 0x43 block");

            _kwp1281.SendBlock(new List<byte> { 0x43 });
            var blocks = _kwp1281.ReceiveBlocks();
            foreach (var block in blocks.Where(b => !b.IsAckNak))
            {
                Log.WriteLine($"{Utils.DumpAscii(block.Body)}");
            }
        }

        internal void UnlockClusterForEepromRead()
        {
            Log.WriteLine("Sending Custom 0x08 0x15 block");
            _kwp1281.SendBlock(new List<byte> { 0x1B, 0x08, 0x15 });
            _ = _kwp1281.ReceiveBlocks();

        }

        private readonly IKW1281Dialog _kwp1281;

        public MotometerBOOCluster(IKW1281Dialog kwp1281)
        {
            _kwp1281 = kwp1281;
        }
    }
}
