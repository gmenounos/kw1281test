using System;
using System.Collections.Generic;
using System.Linq;

namespace BitFab.KW1281Test.Cluster
{
    internal class MotometerBOOCluster : ICluster
    {
        public void UnlockForEepromReadWrite()
        {
            string softwareVersion = GetClusterInfo();
            ushort login;
            if (!softwareVersion.StartsWith("h9340"))
            {
                login = 11899;
            }
            else
            {
                // TODO: Add more logins for the various BOO software versions
                Log.WriteLine("Warning: Unknown software version. Login may fail.");
                login = 11899;
            }

            _kwp1281!.Login(login, workshopCode: 0);

            // TODO: If 0x08 0x15 doesn't work, try all 64K values

            Log.WriteLine("Sending Custom 0x08 0x15 block");
            _kwp1281.SendBlock(new List<byte> { 0x1B, 0x08, 0x15 });
            _ = _kwp1281.ReceiveBlocks();
        }

        public string DumpEeprom(
            uint? optionalAddress, uint? optionalLength, string? optionalFileName)
        {
            uint address = optionalAddress ?? 0;
            uint length = optionalLength ?? 0x100;
            string filename = optionalFileName ?? $"BOO_${address:X6}_eeprom.bin";

            throw new NotImplementedException();
        }

        private string GetClusterInfo()
        {
            Log.WriteLine("Sending 0x43 block");

            _kwp1281.SendBlock(new List<byte> { 0x43 });
            var blocks = _kwp1281.ReceiveBlocks().Where(b => !b.IsAckNak).ToList();
            foreach (var block in blocks)
            {
                Log.WriteLine($"{Utils.DumpAscii(block.Body)}");
            }

            return Utils.DumpAscii(blocks[0].Body);
        }

        private readonly IKW1281Dialog _kwp1281;

        public MotometerBOOCluster(IKW1281Dialog kwp1281)
        {
            _kwp1281 = kwp1281;
        }
    }
}
