using BitFab.KW1281Test.Kwp2000;
using System;
using System.Linq;
using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test.Cluster
{
    class BoschCluster
    {
        public static bool SecurityAccess(KW2000Dialog kwp2000, byte accessMode)
        {
            const byte identificationOption = 0x94;
            var responseMsg = kwp2000.SendReceive(Service.readEcuIdentification, new byte[] { identificationOption });
            if (responseMsg.Body[0] != identificationOption)
            {
                throw new InvalidOperationException($"Received unexpected identificationOption: {responseMsg.Body[0]:X2}");
            }
            Log.WriteLine(Utils.DumpAscii(responseMsg.Body.Skip(1)));

            const int maxTries = 16;
            for (var i = 0; i < maxTries; i++)
            {
                responseMsg = kwp2000.SendReceive(Service.securityAccess, new byte[] { accessMode });
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
                    responseMsg = kwp2000.SendReceive(Service.securityAccess,
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
            uint key =
                0xFB4ACBBA
                + (seed & 0x07DA06B8)
                + (~seed | 0x07DA06B8)
                - 2 * (seed & 0x00004000);
            return key;
        }
    }
}
