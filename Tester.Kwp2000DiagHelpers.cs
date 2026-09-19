using System;
using System.Collections.Generic;
using System.Threading;
using BitFab.KW1281Test.Blocks;
using BitFab.KW1281Test.Kwp2000;
    using Service = BitFab.KW1281Test.Kwp2000.DiagnosticService;

namespace BitFab.KW1281Test
{
    internal partial class Tester
    {
    private IKW2000Dialog CreateKwp2000Dialog(int kwpVersion)
    {
        var headerAddress = Kwp2000Addressing.HeaderAddress((byte)_controllerAddress, kwpVersion);
        if (headerAddress != (byte)_controllerAddress)
        {
            Log.WriteLine(
                $"KW {kwpVersion} at wakeup address ${_controllerAddress:X2}: " +
                $"using header address ${headerAddress:X2}");
        }
        return new KW2000Dialog(_kwpCommon, headerAddress);
    }

    private static void StartDefaultDiagSession(IKW2000Dialog kwp2000)
    {
        var subfunctions = new byte[] { 0x89, 0x81, 0x85 };
        for (var i = 0; i < subfunctions.Length; i++)
        {
            var isLastAttempt = i == subfunctions.Length - 1;
            try
            {
                kwp2000.SendReceive(Service.startDiagnosticSession, new[] { subfunctions[i] });
                return;
            }
            catch (TimeoutException) when (!isLastAttempt)
            {
                Log.WriteLine(
                    $"No response to startDiagnosticSession(0x{subfunctions[i]:X2}); " +
                    $"trying 0x{subfunctions[i + 1]:X2} instead...");
            }
        }
    }

    public List<(byte GroupNumber, List<SensorValue>? Values)> GroupReadMany(IReadOnlyList<byte> groupNumbers)
    {
        var results = new List<(byte, List<SensorValue>?)>();
        foreach (var groupNumber in groupNumbers)
        {
            _kwp1281.SendBlock([(byte)BlockTitle.GroupRead, groupNumber]);
            var responseBlock = _kwp1281.ReceiveBlock();
            results.Add(
                responseBlock is GroupReadResponseBlock groupReading
                    ? (groupNumber, groupReading.SensorValues)
                    : (groupNumber, null));
        }
        return results;
    }

    public void KeepAlive()
    {
        _kwp1281.KeepAlive();
    }
    }
}
