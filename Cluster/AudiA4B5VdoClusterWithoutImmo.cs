using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Cluster;

/// <summary>
/// e.g.
/// 8D0919033C  
/// B5-KOMBIINSTRUMENT D08       
/// Codierung: 00083
/// Werkstatt - Code: 000001
///
/// This class represents a VDO instrument cluster without an immobilizer,
/// as used in early Audi A4 B5 model years (approximately up to 1997).
///
/// These clusters can be identified by the fact that no indicator lights
/// in the instrument cluster turn on when only the ignition is switched on.
///
/// This implementation targets the standard (small display) variant.
/// It should work similarly with the large display variant, however
/// clusters with a large display are very rare in the early production
/// years of the Audi A4, and no hardware was available for testing.
///
/// Note about identification:
/// The label "B5-KOMBI..." (with a hyphen '-') is a good identification marker for VDO cluster.
///
/// There are instrument clusters with the exact same spare part number manufactured by UK-NSI.
/// These clusters use an underscore '_' instead of a hyphen and are labeled as "B5_KOMBI...".
///
/// Despite identical part numbers, VDO and UK-NSI clusters differ internally and
/// must be handled differently. This is only for VDO
/// </summary>
internal class AudiA4B5VdoClusterWithoutImmo : ICluster
{
    public static bool IsB5Kombi(List<ControllerIdent> identList)
    {
        string ident = ParseIdentList(identList);

        return ident.Contains("B5_K") || // UK-NSI
            ident.Contains("B5-K"); // VDO
    }

    public static bool IsSupported(List<ControllerIdent> identList, out string reasonNotSupported)
    {
        string ident = ParseIdentList(identList);

        if (ident.Contains("B5-K")) // VDO
        {
            reasonNotSupported = string.Empty;
            return true;
        }
        else if (ident.Contains("B5_K")) // UK-NSI
        {
            reasonNotSupported = "UK-NSI clusters are not supported";
            return false;
        }
        else
        {
            reasonNotSupported = "Not a VDO B5 cluster";
            return false;
        }
    }

    public void UnlockForEepromReadWrite()
    {
        Log.WriteLine("Sending custom login block to switch Mode");
        _kw1281Dialog.SendBlock([0x1B, 0x00,  (byte)'M', (byte)'O', (byte)'D', (byte)'E']);
        var resultBlock = _kw1281Dialog.ReceiveBlock();
        if (resultBlock is NakBlock)
        {
            throw new InvalidOperationException(
                $"Expected ACK block but received: {resultBlock}");
        }

        string[] passwords =
        [
            "Poseidon",
            "Herkules",
            "Polyphem",
        ];

        var succeeded = false;
        foreach (var password in passwords)
        {
            Log.WriteLine("Sending custom login block");
            var blockBytes = new List<byte>([0x1B, 0x01]);
            blockBytes.AddRange(Encoding.ASCII.GetBytes(password));
            _kw1281Dialog.SendBlock(blockBytes);

            var block = _kw1281Dialog.ReceiveBlock();
            if (block is NakBlock)
            {
                continue;
            }
            else if (block is not AckBlock)
            {
                throw new InvalidOperationException(
                    $"Expected ACK block but received: {block}");
            }

            succeeded = true;
            break;
        }

        if (!succeeded)
        {
            throw new InvalidOperationException("Unable to login to cluster");
        }
    }

    private static string ParseIdentList(List<ControllerIdent> identList)
    {
        //{8D0919033C  B5-KOMBIINSTRUMENT  D08
        //Software Coding 00083, Workshop Code: 00001}
        return identList
            .Select(x => x.ToString())
            .FirstOrDefault() ?? "";
    }

    /// <summary>
    /// After the instrument cluster is unlocked, two EEPROM cells are
    /// automatically modified internally.
    ///
    /// As a result, the dot of the trip odometer starts blinking.
    /// Without resetting these EEPROM cells to their original values,
    /// the blinking will not stop.
    /// </summary>
    private void ClusterFinalizer()
    {
        _kw1281Dialog.WriteEeprom(0x7A, [0x00, 0x00]);
    }

    private void ClusterRest()
    {
        _kw1281Dialog.SendBlock([0x1B, 0x19]);
    }

    public string DumpEeprom(
        uint? address, uint? length, string? dumpFileName)
    {
        address ??= 0;
        length ??= 0x80;
        dumpFileName ??= $"VDO_AudiA4B5_0x{address:X4}_eeprom.bin";

        Utils.WriteDump(
            (addr, len) => _kw1281Dialog.ReadEeprom((ushort)addr, len),
            (ushort)address, (ushort)length, maxReadLength: 8, dumpFileName);
        ClusterFinalizer();
        ClusterRest();

        return dumpFileName;
    }

    public void WriteEeprom(uint? address, byte[] bytes)
    {
        address ??= 0;

        Utils.LoadDump(
            (addr, values) => _kw1281Dialog.WriteEeprom(addr, values),
            (uint)address, bytes, maxWriteLength: 8);
        ClusterFinalizer();
        ClusterRest();
    }

    private readonly IKW1281Dialog _kw1281Dialog;

    public AudiA4B5VdoClusterWithoutImmo(IKW1281Dialog kw1281Dialog)
    {
        _kw1281Dialog = kw1281Dialog;
    }
}
