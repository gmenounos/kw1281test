using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
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

    public static bool IsSupportedIdent(IEnumerable<string> ident, out string reason)
    {
        reason = string.Empty;

        if (ident == null || !ident.Any())
        {
            reason = "Missing identification string";
            return false;
        }

        var first = ident.First();

        if (first.Contains("B5_K")) // UK-NSI
        {
            reason = "UK-NSI clusters are not supported";
            return false;
        }

        if (!first.Contains("B5-K")) // not VDO
        {
            reason = "Not a VDO B5 cluster";
            return false;
        }

        return true;
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
        }

        if (!succeeded)
        {
            throw new InvalidOperationException("Unable to login to cluster");
        }
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
        uint? optionalAddress, uint? optionalLength, string? optionalFileName)
    {
        var address = optionalAddress ?? 0;
        var length = optionalLength ?? 0x80;
        var filename = optionalFileName ?? $"VDO_AudiA4B5_0x{address:X4}_eeprom.bin";
        DumpEeprom((ushort)address, (ushort)length, maxReadLength: 8, filename);
        ClusterFinalizer();
        ClusterRest();

        return filename;
    }

    private void DumpEeprom(ushort startAddr, ushort length, byte maxReadLength, string fileName)
    {
        bool succeeded = true;

        using (var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                byte readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                List<byte>? blockBytes = _kw1281Dialog.ReadEeprom((ushort)addr, readLength);
                if (blockBytes == null)
                {
                    blockBytes = Enumerable.Repeat((byte)0, readLength).ToList();
                    succeeded = false;
                }
                fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                fs.Flush();
            }
        }

        if (!succeeded)
        {
            Log.WriteLine();
            Log.WriteLine("**********************************************************************");
            Log.WriteLine("*** Warning: Some bytes could not be read and were replaced with 0 ***");
            Log.WriteLine("**********************************************************************");
            Log.WriteLine();
        }
    }

    public void WriteEeprom(byte[] bytes)
    {
        WriteEeprom((ushort)0, bytes, maxWriteLength: 8);
        ClusterFinalizer();
        ClusterRest();
    }

    private void WriteEeprom(
        ushort startAddr, byte[] bytes, uint maxWriteLength)
    {
        var succeeded = true;
        var length = bytes.Length;
        for (uint addr = startAddr; addr < (startAddr + length); addr += maxWriteLength)
        {
            var writeLength = (byte)Math.Min(startAddr + length - addr, maxWriteLength);
            if (!_kw1281Dialog.WriteEeprom(
                    (ushort)addr,
                    bytes.Skip((int)(addr - startAddr)).Take(writeLength).ToList()))
            {
                succeeded = false;
            }
        }

        if (!succeeded)
        {
            Log.WriteLine("EEPROM write failed. You should probably try again.");
        }
    }

    private readonly IKW1281Dialog _kw1281Dialog;

    public AudiA4B5VdoClusterWithoutImmo(IKW1281Dialog kw1281Dialog)
    {
        _kw1281Dialog = kw1281Dialog;
    }
}
