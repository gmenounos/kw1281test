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
            if (softwareVersion.Length < 10 ||
                !VersionToLogin.TryGetValue(softwareVersion[..10], out ushort login))
            {
                Log.WriteLine("Warning: Unknown software version. Login may fail.");
                login = 11899;
            }

            _kwp1281!.Login(login, workshopCode: 0);

            // TODO: If 0x08 0x15 doesn't work, try all 64K values

            Log.WriteLine("Sending Custom 0x08 0x15 block");
            _kwp1281.SendBlock(new List<byte> { 0x1B, 0x08, 0x15 });
            _ = _kwp1281.ReceiveBlocks();
        }

        private readonly Dictionary<string, ushort> VersionToLogin = new()
        {
            { "a0prj008.1", 10164 },
            { "A0prj008.2", 10164 },
            { "a4prj010.1", 21597 },
            { "a4prj012.1", 21597 },
            { "h1340_05.2", 21701 },
            { "h1340_06.2", 21601 },
            { "h9340_08.1", 11899 },
            { "h9340_08.2", 11899 },
            { "h9340_09.1", 11899 },
            { "h9340_10.1", 11899 },
            { "h9340_10.2", 11899 },
            { "h9340_10.3", 11899 },
            { "h9340_11.2", 11899 },
            { "se110_05.2", 13473 },
            { "v9119_07.1", 19126 },
            { "v9119_07.3", 11064 },
            { "v9230_03.1", 10501 },
            { "v9230_03.2", 10501 },
            { "v9230_05.1", 44479 },
            { "v9230_05.2", 44479 },
            { "v9230_06.3", 23775 },
            { "v9230_07.2", 10164 },
            { "v9230_08.1", 10164 },
            { "v9230_08.2", 10164 },
            { "vw110_04.2", 08721 },
            { "VW230_06.1", 47165 },
            { "vw340_07.2", 05555 },
        };

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
