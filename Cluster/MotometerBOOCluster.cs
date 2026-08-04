using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BitFab.KW1281Test.Cluster;

internal class MotometerBOOCluster : ICluster
{
    public void UnlockForEepromReadWrite()
    {
        string softwareVersion = GetClusterInfo();
        string versionKey = softwareVersion.Length >= 10 ?
            softwareVersion[..10] : softwareVersion;
        if (!VersionToLogin.TryGetValue(versionKey, out ushort login))
        {
            Log.WriteLine("Warning: Unknown software version. Login may fail.");
            login = 11899;
        }

        _kwp1281!.Login(login, workshopCode: 0);

        Log.WriteLine($"Sending Custom $08 $15 block");
        if (SendCustom(0x08, 0x15))
        {
            return;
        }

        if (VersionToUnlockCode.TryGetValue(versionKey, out var knownCode))
        {
            Log.WriteLine($"Sending Custom ${knownCode.first:X2} ${knownCode.second:X2} block");
            if (SendCustom(knownCode.first, knownCode.second))
            {
                return;
            }
        }

        Log.WriteLine("Known unlock codes failed. Trying all combinations (this may take a while)...");

        string progressFile = GetBruteForceProgressFile(versionKey);

        var (startFirst, startSecond) = LoadBruteForceProgress(progressFile);
        if (startFirst > 0 || startSecond > 0)
        {
            Log.WriteLine(
                $"Resuming from ${startFirst:X2} ${startSecond:X2} " +
                $"(progress saved from a previous, cancelled run for {versionKey}).");
        }

        for (int first = startFirst; first < 0x100; first++)
        {
            Log.WriteLine($"Trying ${first:X2} $00-$FF");

            for (int second = (first == startFirst ? startSecond : 0); second < 0x100; second++)
            {
                File.WriteAllText(progressFile, $"{first} {second}");

                if (SendCustom(first, second))
                {
                    Log.WriteLine($"Combination ${first:X2} ${second:X2} Succeeded.");
                    Log.WriteLine("Please report this to the program author.");
                    File.Delete(progressFile);
                    return;
                }
            }
        }

        File.Delete(progressFile);
        Log.WriteLine("All combinations failed. EEPROM access will likely fail.");
    }

    /// <summary>
    /// Each cluster software version gets its own checkpoint file, since the correct
    /// unlock combination (if any) differs per version.
    /// </summary>
    private static string GetBruteForceProgressFile(string softwareVersion)
    {
        var sanitized = softwareVersion.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c, '_');
        }

        return $"boo_unlock_bruteforce_progress_{sanitized}.txt";
    }

    /// <summary>
    /// Reads the ($first, $second) combination to resume from, if a previous brute-force
    /// run was cancelled (e.g. via Ctrl+C) partway through. Returns (0, 0) if there's no
    /// saved progress, or if it's unreadable.
    /// </summary>
    private static (int first, int second) LoadBruteForceProgress(string progressFile)
    {
        if (!File.Exists(progressFile))
        {
            return (0, 0);
        }

        var parts = File.ReadAllText(progressFile).Trim().Split(' ');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int first) &&
            int.TryParse(parts[1], out int second))
        {
            return (first, second);
        }

        return (0, 0);
    }

    private bool SendCustom(int first, int second)
    {
        _kwp1281.SendBlock(new List<byte> { 0x1B, (byte)first, (byte)second });
        var block = _kwp1281.ReceiveBlocks().FirstOrDefault();

        if (block is NakBlock)
        {
            return false;
        }
        else if (block is AckBlock)
        {
            return true;
        }
        else
        {
            throw new InvalidOperationException(
                $"Expected ACK or NAK block but got: {block}");
        }
    }

    private readonly Dictionary<string, (byte first, byte second)> VersionToUnlockCode = new()
    {
        { "h1340_06.2", (0x45, 0x1C) },
    };

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
        string filename = optionalFileName ?? $"BOOMM0_0x{address:X6}_eeprom.bin";

#if false
        var identInfo = _kwp1281.ReadIdent().First().ToString()
            .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
            .Replace(' ', '_');

        var dumpFileName = filename ?? $"{identInfo}_0x{startAddress:X4}_eeprom.bin";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            dumpFileName = dumpFileName.Replace(c, 'X');
        }
        foreach (var c in Path.GetInvalidPathChars())
        {
            dumpFileName = dumpFileName.Replace(c, 'X');
        }

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        DumpEeprom(startAddress, length, maxReadLength: 16, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");

        return dumpFileName;
#endif
        throw new NotImplementedException();
    }

    private string GetClusterInfo()
    {
        Log.WriteLine("Sending 0x43 block");

        _kwp1281.SendBlock([0x43]);
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
