using System;
using System.Diagnostics;
using System.IO;
using BitFab.KW1281Test.EDC16;

namespace BitFab.KW1281Test
{
    internal partial class Tester
    {
    public void ReadFlashEdc16(
        string? filename, Edc16FlashVM.FlashSpeed speed = Edc16FlashVM.FlashSpeed.Medium,
        Func<bool>? isStopRequested = null)
    {
        // See DumpEdc15Flash's identical stopwatch doc comment.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var edc16Flash = new Edc16FlashVM(_kwpCommon);

            var dumpFileName = filename ?? "EDC16_Flash.bin";
            Log.WriteLine($"Reading EDC16 flash to {dumpFileName}...");
            edc16Flash.ReadFlash(
                dumpFileName,
                speed,
                allowFastBaud: true,
                onPercent: percent => Log.WriteLine($"{percent}%"),
                isStopRequested: isStopRequested);
            Log.WriteLine("Done!");
        }
        finally
        {
            Log.WriteLine($"Read time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }

    public void WriteFlashEdc16(
        string filename,
        Func<string, bool>? confirmChecksumCorrection = null,
        bool allowUnverifiedChecksum = false,
        bool forceFullWrite = false,
        Edc16FlashVM.FlashSpeed speed = Edc16FlashVM.FlashSpeed.Medium,
        bool fastInitPrime = false,
        Func<bool>? isStopRequested = null)
    {
        // See DumpEdc15Flash's identical stopwatch doc comment.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var image = File.ReadAllBytes(filename);

            // Validate the image BEFORE any ECU communication -- WriteFlash connects, erases and
            // programs, so every reason to reject a file must be caught here, up front, not after the
            // ECU is already mid-write. (Mirrors LoadEdc15Flash's checksum gate.)
            //
            // 1) Size: an EDC16U31/U34 flash is EXACTLY 2 MB; anything else isn't a full image and
            //    can't be checksum-checked, so reject it outright.
            const int Edc16FlashSize = 0x200000;
            if (image.Length != Edc16FlashSize)
            {
                Log.WriteLine(
                    $"ERROR: {filename} is {image.Length} bytes; an EDC16 (U31/U34) flash image must be " +
                    $"exactly 0x{Edc16FlashSize:X} ({Edc16FlashSize}) bytes -- this doesn't look like a full " +
                    "flash file. Nothing was sent to the ECU. Aborting.");
                return;
            }

            // 2) Checksums: writing a file whose stored checksums are wrong must be an explicit,
            //    acknowledged decision. Verify/VerifyAndCorrect operate on (and correct) the very same
            //    in-memory image that gets flashed below.
            Edc16Checksum.Result checksumResult;
            try
            {
                checksumResult = Edc16Checksum.Verify(image);
            }
            catch (Exception ex)
            {
                Log.WriteLine(
                    $"ERROR: EDC16 checksum verification threw ({ex.Message}); refusing to write without a " +
                    "verified file. Nothing was sent to the ECU. Aborting.");
                return;
            }

            if (checksumResult.Supported && !checksumResult.Valid)
            {
                var message =
                    $"The checksums stored in {filename} don't match its contents " +
                    $"({checksumResult.RegionsMismatched} of {checksumResult.RegionsChecked} region(s), " +
                    $"{checksumResult.Algorithm}). Flashing this file as-is writes those same incorrect " +
                    "checksums to the ECU (which can put it into limp/no-start).";
                Log.WriteLine($"\n{checksumResult.Describe()}\n");

                if (confirmChecksumCorrection == null)
                {
                    Log.WriteLine($"WARNING: {message}\n");
                    // No interactive prompt (headless/automation). Only proceed if the caller opted in.
                    if (allowUnverifiedChecksum)
                    {
                        Log.WriteLine(
                            "Writing the file with its original (invalid) checksums -- explicitly requested.\n");
                    }
                    else
                    {
                        Log.WriteLine(
                            "ERROR: this file's stored checksums are invalid and there's no confirmation prompt " +
                            "available to approve writing it as-is. Correct the checksums first, or pass 'unverified' " +
                            "to write it anyway. Nothing was sent to the ECU. Aborting.");
                        return;
                    }
                }
                else if (confirmChecksumCorrection.Invoke(message))
                {
                    var corrected = Edc16Checksum.VerifyAndCorrect(image);
                    File.WriteAllBytes(filename, image);
                    Log.WriteLine($"Corrected {corrected.RegionsMismatched} checksum region(s) in {filename}.\n");
                }
                else
                {
                    Log.WriteLine("Continuing with the file's original (uncorrected) checksums (confirmed).\n");
                }
            }
            else if (!checksumResult.Supported)
            {
                // Correct size, but no recognizable 0xCAFECADE checksum layout -- can't verify/correct.
                if (allowUnverifiedChecksum)
                {
                    Log.WriteLine(
                        $"NOTE: {filename}'s EDC16 checksum layout isn't recognized, so it can't be verified -- " +
                        "writing anyway (explicitly requested).\n");
                }
                else
                {
                    Log.WriteLine(
                        $"ERROR: {filename} is 2 MB but its EDC16 checksum layout isn't recognized (no 0xCAFECADE " +
                        "marker), so it can't be verified. Pass 'unverified' to write it anyway. Nothing was sent " +
                        "to the ECU. Aborting.");
                    return;
                }
            }
            else
            {
                Log.WriteLine(checksumResult.Describe());
            }

            var edc16Flash = new Edc16FlashVM(_kwpCommon);

            Log.WriteLine($"Writing EDC16 flash from {filename}...");
            edc16Flash.WriteFlash(
                image,
                speed,
                allowFastBaud: true,
                onStage: stage => Log.WriteLine(stage),
                onPercent: percent => Log.WriteLine($"{percent}%"),
                forceFull: forceFullWrite,
                fastInitPrime: fastInitPrime,
                isStopRequested: isStopRequested);
            Log.WriteLine("Done!");
        }
        finally
        {
            Log.WriteLine($"Write time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }
    }
}
