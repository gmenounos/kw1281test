using BitFab.KW1281Test.Kwp2000;
using System;
using System.Linq;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test.Cluster
{
    class BoschRB8Cluster : ICluster
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
            string filename = optionalFileName ?? $"RB8_${address:X6}_eeprom.bin";

            _kwp2000.DumpEeprom(address, length, filename);

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
                var key = CalcRB8Key(seed);

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

        static uint CalcRB8Key(uint seed)
        {
            uint key = 0x03249272 + (seed ^ 0xf8253947);
            return key;
        }

        private readonly KW2000Dialog _kwp2000;

        public BoschRB8Cluster(KW2000Dialog kwp2000)
        {
            _kwp2000 = kwp2000;
        }
    }
}
