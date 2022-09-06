using BitFab.KW1281Test.Kwp2000;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test.Cluster
{
    class BoschRBxCluster : ICluster
    {
        public void UnlockForEepromReadWrite()
        {
            SecurityAccess(0xFB);
        }

        public string DumpEeprom(
            uint? optionalAddress, uint? optionalLength, string? optionalFileName)
        {
            uint address = optionalAddress ?? 0x10400;
            uint length = optionalLength ?? 0x400;
            string filename = optionalFileName ?? $"RBx_${address:X6}_mem.bin";

            _kwp2000.DumpMem(address, length, filename);

            return filename;
        }

        public bool SecurityAccess(byte accessMode)
        {
            const byte identificationOption = 0x94;
            var responseMsg = _kwp2000.SendReceive(Service.readEcuIdentification, new byte[] { identificationOption });
            if (responseMsg.Body[0] != identificationOption)
            {
                throw new InvalidOperationException($"Received unexpected identificationOption: {responseMsg.Body[0]:X2}");
            }
            Log.WriteLine(Utils.DumpAscii(responseMsg.Body.Skip(1)));

            const int maxTries = 16;
            for (var i = 0; i < maxTries; i++)
            {
                responseMsg = _kwp2000.SendReceive(Service.securityAccess, new byte[] { accessMode });
                if (responseMsg.Body[0] != accessMode)
                {
                    throw new InvalidOperationException($"Received unexpected accessMode: {responseMsg.Body[0]:X2}");
                }
                var seedBytes = responseMsg.Body.Skip(1).ToArray();
                var seed = (uint)(
                    (seedBytes[0] << 24) |
                    (seedBytes[1] << 16) |
                    (seedBytes[2] << 8) |
                    seedBytes[3]);
                var key = CalcRBxKey(seed);

                try
                {
                    responseMsg = _kwp2000.SendReceive(Service.securityAccess,
                        new[] {
                            (byte)(accessMode + 1),
                            (byte)((key >> 24) & 0xFF),
                            (byte)((key >> 16) & 0xFF),
                            (byte)((key >> 8) & 0xFF),
                            (byte)(key & 0xFF)
                        });

                    Log.WriteLine("Success!!!");
                    return true;
                }
                catch (NegativeResponseException)
                {
                    if (i < (maxTries - 1))
                    {
                        Log.WriteLine("Trying again.");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Toggle an Audi A4 RB4 cluster between Adapted mode (6) and New mode (4).
        /// Cluster should already be logged in and unlocked for EEPROM read/write.
        /// </summary>
        public void ToggleRB4Mode()
        {
            _kwp2000.StartDiagnosticSession(0x84, 0x14);

            Thread.Sleep(350);

            byte[] bytes = _kwp2000.ReadMemoryByAddress(0x010450, 2);
            if (bytes[0] != (byte)'A' && bytes[1] != (byte)'U')
            {
                Log.WriteLine("Cluster is not an Audi cluster!");
            }
            else
            {
                try
                {
                    bytes = _kwp2000.ReadMemoryByAddress(0x010000, 0x10);
                    Log.WriteLine("Cluster is in New mode (4).");
                }
                catch (NegativeResponseException)
                {
                    Log.WriteLine("Cluster is in Adapted mode (6).");
                }

                Log.WriteLine("Toggling cluster mode...");

                foreach (var address in new uint[] { 0x01044F, 0x01052F, 0x01062F })
                {
                    bytes = _kwp2000.ReadMemoryByAddress(address, 1);
                    bytes[0] ^= 0x12;
                    _kwp2000.WriteMemoryByAddress(address, 1, bytes);
                }
            }

            Log.WriteLine("Resetting cluster...");

            _kwp2000.EcuReset(0x01);
        }

        static uint CalcRBxKey(uint seed)
        {
            uint key = 0x03249272 + (seed ^ 0xf8253947);
            return key;
        }

        private readonly KW2000Dialog _kwp2000;

        public BoschRBxCluster(KW2000Dialog kwp2000)
        {
            _kwp2000 = kwp2000;
        }
    }
}
