using BitFab.KW1281Test.Cluster;
using BitFab.KW1281Test.EDC15;
using BitFab.KW1281Test.Interface;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace BitFab.KW1281Test;

internal class Tester
{
    private readonly IKwpCommon _kwpCommon;
    private readonly IKW1281Dialog _kwp1281;
    private readonly int _controllerAddress;


    public Tester(IInterface @interface, int controllerAddress)
    {
        _kwpCommon = new KwpCommon(@interface);
        _kwp1281 = new KW1281Dialog(_kwpCommon);
        _controllerAddress = controllerAddress;
    }

    public ControllerInfo Kwp1281Wakeup(bool evenParityWakeup = false, bool failQuietly = false)
    {
        Log.WriteLine("Sending wakeup message");

        var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParityWakeup, failQuietly);

        if (kwpVersion != 1281)
        {
            throw new UnexpectedProtocolException("Expected KWP1281 protocol.");
        }

        var ecuInfo = _kwp1281.Connect();
        Log.WriteLine($"ECU: {ecuInfo}");
        return ecuInfo;
    }

    public KW2000Dialog Kwp2000Wakeup(bool evenParityWakeup = false)
    {
        Log.WriteLine("Sending wakeup message");

        var kwpVersion = _kwpCommon!.WakeUp((byte)_controllerAddress, evenParityWakeup);

        if (kwpVersion == 1281)
        {
            throw new UnexpectedProtocolException("Expected KWP2000 protocol.");
        }

        var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

        return kwp2000;
    }

    public void EndCommunication()
    {
        _kwp1281.EndCommunication();
    }

    // Begin top-level commands

    public void ActuatorTest()
    {
        using KW1281KeepAlive keepAlive = new(_kwp1281);

        ConsoleKeyInfo keyInfo;
        do
        {
            var response = keepAlive.ActuatorTest(0x00);
            if (response == null || response.ActuatorName == "End")
            {
                Log.WriteLine("End of test.");
                break;
            }
            Log.WriteLine($"Actuator Test: {response.ActuatorName}");

            // Press any key to advance to next test or press Q to exit
            Console.Write("Press 'N' to advance to next test or 'Q' to quit");
            do
            {
                keyInfo = Console.ReadKey(intercept: true);
            } while (keyInfo.Key != ConsoleKey.N && keyInfo.Key != ConsoleKey.Q);
            Console.WriteLine();
        } while (keyInfo.Key != ConsoleKey.Q);
    }

    public void AdaptationRead(
        byte channel,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationRead(channel);
    }

    public void AdaptationSave(
        byte channel, ushort channelValue,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationSave(channel, channelValue, workshopCode);
    }

    public void AdaptationTest(
        byte channel, ushort channelValue,
        ushort? login, int workshopCode)
    {
        if (login.HasValue)
        {
            _kwp1281.Login(login.Value, workshopCode);
        }
        _kwp1281.AdaptationTest(channel, channelValue);
    }

    public void BasicSettingRead(byte groupNumber)
    {
        var succeeded = _kwp1281.GroupRead(groupNumber, useBasicSetting: true);
    }

    public void ClarionVWPremium4SafeCode()
    {
        if (_controllerAddress != (int)ControllerAddress.Radio)
        {
            Log.WriteLine("Only supported for radio address 56");
            return;
        }

        // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
        const byte readWriteSafeCode = 0xF0;
        const byte read = 0x00;
        _kwp1281.SendBlock(new List<byte> { readWriteSafeCode, read });

        var block = _kwp1281.ReceiveBlocks().FirstOrDefault(b => !b.IsAckNak);

        if (block == null)
        {
            Log.WriteLine("No response received from radio.");
        }
        else if (block.Title != readWriteSafeCode)
        {
            Log.WriteLine(
                $"Unexpected response received from radio. Block title: ${block.Title:X2}");
        }
        else
        {
            var safeCode = block.Body[0] * 256 + block.Body[1];
            Log.WriteLine($"Safe code: {safeCode:X4}");
        }
    }

    public void ClearFaultCodes()
    {
        var faultCodes = _kwp1281.ClearFaultCodes(_controllerAddress);

        if (faultCodes != null)
        {
            if (faultCodes.Count == 0)
            {
                Log.WriteLine("Fault codes cleared.");
            }
            else
            {
                Log.WriteLine("Fault codes:");
                foreach (var faultCode in faultCodes)
                {
                    Log.WriteLine($"    {faultCode}");
                }
            }
        }
        else
        {
            Log.WriteLine("Failed to clear fault codes.");
        }
    }

    public void DelcoVWPremium5SafeCode()
    {
        if (_controllerAddress != (int)ControllerAddress.RadioManufacturing)
        {
            Log.WriteLine("Only supported for radio manufacturing address 7C");
            return;
        }

        // Thanks to Mike Naberezny for this (https://github.com/mnaberez)
        const string secret = "DELCO";
        var code = (ushort)(secret[4] * 256 + secret[3]);
        var workshopCode = secret[2] * 65536 + secret[1] * 256 + secret[0];

        _kwp1281.Login(code, workshopCode);
        var bytes = _kwp1281.ReadRomEeprom(0x0014, 2);
        if (bytes != null)
        {
            Log.WriteLine($"Safe code: {bytes[0]:X2}{bytes[1]:X2}");
        }
        else
        {
            Log.WriteLine($"Unable to determine Safe code.");
        }
    }

    public void DumpCcmRom(string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.CCM &&
            _controllerAddress != (int)ControllerAddress.CentralLocking)
        {
            Log.WriteLine("Only supported for CCM and Central Locking");
            return;
        }

        UnlockControllerForEepromReadWrite();

        var dumpFileName = filename ?? "ccm_rom_dump.bin";
        const byte blockSize = 8;

        Log.WriteLine($"Saving CCM ROM to {dumpFileName}");

        var succeeded = true;
        using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
        {
            for (var seg = 0; seg < 16; seg++)
            {
                for (var msb = 0; msb < 16; msb++)
                {
                    for (var lsb = 0; lsb < 256; lsb += blockSize)
                    {
                        var blockBytes = _kwp1281.ReadCcmRom((byte)seg, (byte)msb, (byte)lsb, blockSize);
                        if (blockBytes == null)
                        {
                            blockBytes = Enumerable.Repeat((byte)0, blockSize).ToList();
                            succeeded = false;
                        }
                        else if (blockBytes.Count < blockSize)
                        {
                            blockBytes.AddRange(Enumerable.Repeat((byte)0, blockSize - blockBytes.Count));
                            succeeded = false;
                        }

                        fs.Write(blockBytes.ToArray(), 0, blockBytes.Count);
                        fs.Flush();
                    }
                }
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

    public void DumpClusterNecRom(string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster");
            return;
        }

        var dumpFileName = filename ?? "cluster_nec_rom_dump.bin";
        const byte blockSize = 16;

        Log.WriteLine($"Saving cluster NEC ROM to {dumpFileName}");

        bool succeeded = true;
        using (var fs = File.Create(dumpFileName, blockSize, FileOptions.WriteThrough))
        {
            var cluster = new VdoCluster(_kwp1281);

            for (int address = 0; address < 65536; address += blockSize)
            {
                var blockBytes = cluster.CustomReadNecRom((ushort)address, blockSize);
                if (blockBytes == null)
                {
                    blockBytes = Enumerable.Repeat((byte)0, blockSize).ToList();
                    succeeded = false;
                }
                else if (blockBytes.Count < blockSize)
                {
                    blockBytes.AddRange(Enumerable.Repeat((byte)0, blockSize - blockBytes.Count));
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

    public void FindLogins(ushort goodLogin, int workshopCode)
    {
        const int start = 0;
        for (int login = start; login <= 65535; login++)
        {
            _kwp1281.Login(goodLogin, workshopCode);

            try
            {
                Log.WriteLine($"Trying {login:D5}");
                _kwp1281.Login((ushort)login, workshopCode);
                Log.WriteLine($"{login:D5} succeeded");
                continue;
            }
            catch(TimeoutException)
            {
                _kwp1281.SetDisconnected();
                try
                {
                    Kwp1281Wakeup();
                }
                catch(InvalidOperationException)
                {
                    _kwp1281.SetDisconnected();
                    Kwp1281Wakeup();
                }
            }
        }
    }

    public byte[] ReadWriteEdc15Eeprom(
        string? filename,
        List<KeyValuePair<ushort, byte>>? addressValuePairs = null,
        Action<byte[]>? onPostWriteReadback = null)
    {
        // Session-level retry (incorporates gmenounos/kw1281test#185): the K-line occasionally drops
        // the whole KW2000 session mid-way through the slow loader upload. Rather than fail the whole
        // command on a transient timeout, restart the wakeup -> session -> loader-upload sequence up
        // to maxAttempts times. Safe for writes too: each attempt re-uploads a fresh loader and, for
        // a write, re-sends the same absolute (address, value) pairs -- an EEPROM re-write is
        // idempotent -- and the in-session verify (onPostWriteReadback) runs on the successful attempt.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _kwp1281.EndCommunication();

                Thread.Sleep(1000);

                // Now wake it up again, hopefully in KW2000 mode
                _kwpCommon!.Interface.SetBaudRate(10400);
                var kwpVersion = _kwpCommon.WakeUp((byte)_controllerAddress, evenParity: false);
                if (kwpVersion < 2000)
                {
                    throw new InvalidOperationException(
                        $"Unable to wake up ECU in KW2000 mode. KW version: {kwpVersion}");
                }
                Log.WriteLine($"KW Version: {kwpVersion}");

                var edc15 = new Edc15VM(_kwpCommon, _controllerAddress);

                // Pass filename through as-is (nullable): on a pure read ReadWriteEeprom falls back to a
                // default name; on a WRITE a null filename means "don't take a pre-write dump" rather
                // than silently writing one to a default file.
                return edc15.ReadWriteEeprom(filename, addressValuePairs, onPostWriteReadback);
            }
            catch (TimeoutException) when (attempt < maxAttempts)
            {
                Log.WriteLine(
                    $"Timed out talking to ECU over KW2000 (attempt {attempt}/{maxAttempts})." +
                    " Restarting session...");
            }
        }
    }

    /// <summary>
    /// Like <see cref="ReadWriteEdc15Eeprom"/>, but the address/value pairs to write come from
    /// reading a raw binary file starting at <paramref name="startAddress"/>, rather than being
    /// typed in one by one -- mirrors <see cref="LoadEeprom"/>'s shape (a START address plus an
    /// input file) applied to EDC15's KWP2000 write path. Writes via the batched Loader-EEPROM.bin
    /// path. Takes no pre-write dump: the loader sets up its own EEPROM-bus GPIO directions at the
    /// start of every write command (the EInit routine, see <see cref="Edc15VM.ReadWriteEeprom"/>).
    /// <paramref name="onPostWriteReadback"/>, when set, receives the loader's in-session, pre-reboot
    /// read-back of the EEPROM for verification.
    /// </summary>
    public byte[] LoadEdc15Eeprom(
        uint startAddress, string inputFilename,
        Action<byte[]>? onPostWriteReadback = null)
    {
        if (!File.Exists(inputFilename))
        {
            Log.WriteLine($"File {inputFilename} does not exist.");
            return [];
        }

        Log.WriteLine($"Reading {inputFilename}");
        var bytes = File.ReadAllBytes(inputFilename);

        if (startAddress + bytes.Length > 512)
        {
            throw new ArgumentException(
                $"File is {bytes.Length} bytes starting at 0x{startAddress:X}, which extends " +
                "past the EEPROM's 512-byte range (0-0x1FF). Trim the file or choose a lower " +
                "start address.");
        }

        var addressValuePairs = new List<KeyValuePair<ushort, byte>>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            addressValuePairs.Add(new KeyValuePair<ushort, byte>((ushort)(startAddress + i), bytes[i]));
        }

        Log.WriteLine(
            $"Writing {bytes.Length} bytes to EDC15 EEPROM starting at 0x{startAddress:X}...");

        return ReadWriteEdc15Eeprom(null, addressValuePairs, onPostWriteReadback);
    }


    /// <summary>
    /// Formats a duration for the elapsed-time line every flash read/write logs on exit (success,
    /// failure, or Stop -- see each call site's try/finally). "Xm Ys" below an hour (the common
    /// case for a read); adds "Xh " once a write is slow enough to cross that threshold, rather
    /// than switching to a fixed h:mm:ss format that would print a misleading "0h" on every read.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = (int)elapsed.TotalSeconds;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}h {minutes}m {seconds}s" : $"{minutes}m {seconds}s";
    }

    /// <summary>
    /// Reads the full EDC15 external program flash (~512KB) to <paramref name="filename"/>. Unlike
    /// <see cref="ReadWriteEdc15Eeprom"/> (which reuses the app's normal KW2000 wakeup), this uses
    /// its own ISO14230 fast-init handshake internally -- see <see cref="Edc15FlashVM.Connect"/>.
    /// </summary>
    /// <param name="onChecksumWarning">
    /// Invoked once, after a successful read, only if <see cref="Edc15Checksum"/> recognizes the
    /// dumped file's layout AND finds at least one region whose stored checksum doesn't match the
    /// file's actual contents -- e.g. a read that completed but was subtly corrupted. Purely
    /// informational (this never blocks or retries the read); a file whose layout isn't recognized
    /// at all is silently skipped rather than reported as a warning, since "unrecognized" isn't
    /// evidence of anything wrong. See <see cref="Edc15Checksum"/>'s own doc comment for why.
    /// </param>
    public void DumpEdc15Flash(
        Edc15FlashVM.Variant variant, string? filename, Action<string>? onChecksumWarning = null,
        Func<bool>? isStopRequested = null,
        Edc15FlashVM.FlashSpeed flashSpeed = Edc15FlashVM.FlashSpeed.Medium)
    {
        // Covers the WHOLE command, connect included, not just the bulk transfer -- matches what
        // the "=== Read EDC15 Flash ... ===" header already logged by the caller means by "the
        // command". Logged in a finally so a failed or Stopped read still reports how long it ran
        // before giving up, not just a successful one.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var edc15Flash = new Edc15FlashVM(_kwpCommon);
            edc15Flash.Connect(variant, flashSpeed, isStopRequested);

            var dumpFileName = filename ?? "EDC15_Flash.bin";
            Log.WriteLine($"Reading EDC15 flash to {dumpFileName}...");
            edc15Flash.ReadFlash(
                dumpFileName,
                onPercent: percent => Log.WriteLine($"{percent}%"),
                isStopRequested: isStopRequested);
            Log.WriteLine("Done!");

            try
            {
                var readBytes = File.ReadAllBytes(dumpFileName);
                var checksumResult = Edc15Checksum.Verify(readBytes);
                if (checksumResult.Supported && !checksumResult.Valid)
                {
                    var message =
                        $"The checksums stored in {dumpFileName} don't match its contents " +
                        $"({checksumResult.RegionsMismatched} of {checksumResult.RegionsChecked} " +
                        $"region(s), {checksumResult.Algorithm} algorithm). This can mean the read " +
                        "was corrupted or interrupted partway through -- consider reading again.";
                    Log.WriteLine($"\nWARNING: {message}\n");
                    onChecksumWarning?.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                // Checksum verification is a safety net on top of an already-successful read, not a
                // requirement for one -- a problem here (e.g. this app's checksum algorithm doesn't
                // recognize the file at all) must never make an otherwise-successful read look like a
                // failure to the caller.
                Log.WriteLine($"(Checksum verification skipped: {ex.Message})\n");
            }
        }
        finally
        {
            Log.WriteLine($"Read time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }

    /// <summary>
    /// Erases and writes the full EDC15 external program flash from <paramref name="filename"/>.
    /// DESTRUCTIVE: an interrupted or incorrect write can brick the ECU -- see the safety notes on
    /// <see cref="Edc15FlashVM"/> and <see cref="Edc15FlashVM.WriteFlash"/>.
    /// </summary>
    /// <param name="confirmChecksumCorrection">
    /// Invoked BEFORE any connection to the ECU is opened, only if <see cref="Edc15Checksum"/>
    /// recognizes <paramref name="filename"/>'s layout AND finds at least one region whose stored
    /// checksum doesn't match its contents. Returning true corrects the checksums in memory (and
    /// persists the correction back to <paramref name="filename"/>) before flashing; returning
    /// false (or a null callback) flashes the file exactly as given. A file whose layout isn't
    /// recognized at all is never touched and never prompted about -- see
    /// <see cref="Edc15Checksum"/>'s own doc comment for why.
    /// </param>
    /// <param name="forceFullWrite">
    /// The user-facing "Force Full Write" toggle -- when true, disables
    /// <see cref="Edc15FlashVM.WriteFlash"/>'s default skip-blank-chunk optimization (see that
    /// method's own doc comment) so every single chunk is written regardless of content. Defaults
    /// to false (skip enabled), matching how <see cref="CommandRunner.Run"/>'s forceSlowSpeed
    /// parameter defaults to leaving its own optimization on.
    /// </param>
    public void LoadEdc15Flash(
        Edc15FlashVM.Variant variant, string filename,
        Func<string, bool>? confirmChecksumCorrection = null,
        Func<bool>? isStopRequested = null, bool forceFullWrite = false,
        Edc15FlashVM.FlashSpeed flashSpeed = Edc15FlashVM.FlashSpeed.Medium,
        bool verify = true,
        bool allowUnverifiedChecksum = false,
        Func<bool>? confirmWriteUnverified = null)
    {
        // See DumpEdc15Flash's identical stopwatch for why this covers the whole command (checksum
        // pre-check included -- it's local file I/O, negligible next to the ECU write itself) and
        // is logged in a finally, so a failed or Stopped write still reports how long it ran.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var image = File.ReadAllBytes(filename);

            // Validate the image BEFORE any ECU communication. Connect (below) uploads the RAM loader
            // and leaves it running, so every reason to reject a file has to be caught here, up front --
            // otherwise a bad file is only discovered after the loader is already on the ECU, leaving it
            // hanging (exactly the failure this guards against). (Boot-mode write guards the same way;
            // see LoadEdc15FlashBoot.)
            //
            // 1) Size: a real EDC15 external flash is EXACTLY 0x80000 (512 KB). Anything else isn't a
            //    full flash image -- and, just as importantly, can't be checksum-checked at all
            //    (Edc15Checksum requires this exact length and reports Supported=false otherwise), so a
            //    wrong-size file would sail straight past the checksum gate below. Reject it outright.
            const int Edc15FlashSize = 0x80000;
            if (image.Length != Edc15FlashSize)
            {
                Log.WriteLine(
                    $"ERROR: {filename} is {image.Length} bytes; an EDC15 flash image must be exactly " +
                    $"0x{Edc15FlashSize:X} ({Edc15FlashSize}) bytes -- this doesn't look like a full flash " +
                    "file. Nothing was sent to the ECU. Aborting.");
                return;
            }

            // The deliberate opt-in for writing a file this app can't verify (an unrecognized checksum
            // layout) or can't correct without a prompt: either a pre-authorized flag (the CLI /
            // command-tab 'unverified' argument) or an in-the-moment confirmation (the GUI's "unverified
            // file -- continue?" dialog). Absent both, such a file is refused below.
            bool ProceedUnverified() =>
                allowUnverifiedChecksum || (confirmWriteUnverified?.Invoke() ?? false);

            // 2) Checksums: writing a file whose stored checksums are wrong must be an explicit,
            //    acknowledged decision -- never something that scrolls past. Verify/VerifyAndCorrect
            //    operate on (and correct) the very same in-memory image that gets flashed below.
            Edc15Checksum.Result checksumResult;
            try
            {
                checksumResult = Edc15Checksum.Verify(image);
            }
            catch (Exception ex)
            {
                // A throw here is a bug in our own checksum code, not evidence the file is fine. Don't
                // write blind on the back of it -- refuse and say why (nothing has touched the ECU yet).
                Log.WriteLine(
                    $"ERROR: EDC15 checksum verification threw ({ex.Message}); refusing to write without a " +
                    "verified file. Nothing was sent to the ECU. Aborting.");
                return;
            }

            if (checksumResult.Supported && !checksumResult.Valid)
            {
                var message =
                    $"The checksums stored in {filename} don't match its contents " +
                    $"({checksumResult.RegionsMismatched} of {checksumResult.RegionsChecked} " +
                    $"region(s), {checksumResult.Algorithm} algorithm). Flashing this file as-is will " +
                    "write those same incorrect checksums to the ECU. Correct them first?";
                Log.WriteLine($"\nWARNING: {message}\n");

                if (confirmChecksumCorrection == null)
                {
                    // No interactive correct/continue prompt available (e.g. a headless/automation run).
                    // Only proceed if the caller explicitly opted in to writing an unverified file
                    // (the 'unverified' arg); otherwise we do NOT silently write a known-bad file.
                    if (ProceedUnverified())
                    {
                        Log.WriteLine(
                            "Writing the file with its original (invalid) checksums -- explicitly requested.\n");
                    }
                    else
                    {
                        Log.WriteLine(
                            "ERROR: this file's stored checksums are invalid and there's no confirmation " +
                            "prompt available to approve writing it as-is. Correct the checksums first, run " +
                            "it from the GUI where you can choose to correct or continue, or pass 'unverified' " +
                            "to write it anyway. Nothing was sent to the ECU. Aborting.");
                        return;
                    }
                }
                else if (confirmChecksumCorrection.Invoke(message))
                {
                    var corrected = Edc15Checksum.VerifyAndCorrect(image);
                    File.WriteAllBytes(filename, image);
                    Log.WriteLine($"Corrected {corrected.RegionsMismatched} checksum region(s) in {filename}.\n");
                }
                else
                {
                    // The user saw the prompt and explicitly chose to write the file unchanged.
                    Log.WriteLine("Continuing with the file's original (uncorrected) checksums (confirmed).\n");
                }
            }
            else if (!checksumResult.Supported)
            {
                // Correct size, but the checksum layout isn't one this app recognizes -- so it can
                // neither verify nor correct it. We can't confirm the file is even a sane EDC15 image,
                // so by default we refuse rather than push an unverifiable file to the ECU. The only way
                // past this is the deliberate opt-in (ProceedUnverified) -- never silent.
                if (ProceedUnverified())
                {
                    Log.WriteLine(
                        $"NOTE: {filename}'s checksum layout isn't recognized, so it can't be verified -- " +
                        "writing it as-is at explicit request.\n");
                }
                else
                {
                    Log.WriteLine(
                        $"ERROR: couldn't verify {filename} -- its checksum layout isn't one this app " +
                        "recognizes, so it can't be checked or corrected and might not be a valid EDC15 " +
                        "flash image at all. Refusing to write an unverifiable file. Pass 'unverified' " +
                        "to write it anyway. Nothing was sent to the ECU. " +
                        "Aborting.");
                    return;
                }
            }

            var edc15Flash = new Edc15FlashVM(_kwpCommon);
            var effectiveSpeed = flashSpeed;

            if (forceFullWrite)
            {
                // Force Full Write -> the proven whole-chip path: erase the entire chip, then write
                // every chunk (no skip). Uses the standard whole-chip loader, not the sector loader.
                edc15Flash.Connect(variant, effectiveSpeed, isStopRequested);
                Log.WriteLine($"Writing full EDC15 flash from {filename} (whole-chip erase + write)...");
                edc15Flash.WriteFlash(
                    image,
                    onStage: stage => Log.WriteLine(stage),
                    onPercent: percent => Log.WriteLine($"{percent}%"),
                    isStopRequested: isStopRequested,
                    skipBlankChunks: false);
            }
            else
            {
                // Default -> per-sector: query each sector's CmdB2 checksum, erase+write only the
                // sectors that differ from the image, optionally re-verifying by checksum. Uploads the
                // sector-erase loader (with the checksum command) through the same recovery/speed-aware
                // Connect. Requires the assembled CmdB2 loader (docs/Loader-sector-erase.a66).
                Log.WriteLine($"Writing EDC15 flash from {filename} (per-sector: only changed sectors)...");
                edc15Flash.WriteFlashPerSectorChecksum(
                    image, effectiveSpeed, verify, forceFull: false,
                    onStage: stage => Log.WriteLine(stage),
                    onPercent: percent => Log.WriteLine($"{percent}%"),
                    isStopRequested: isStopRequested);
            }
            Log.WriteLine("Done!");
        }
        finally
        {
            Log.WriteLine($"Write time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }

    /// Reads the full EDC15 external flash (512KB) via the microcontroller's own hardware boot
    /// mode -- a completely different, lower-level path than <see cref="DumpEdc15Flash"/> (which
    /// talks KWP2000 to the ECU's normal firmware, transparently handling recovery mode itself).
    /// Requires the ECU to already be physically placed into boot mode before this is
    /// called (a manual hardware procedure this app can't trigger itself) and the interface to
    /// already be at 28800 baud (see CommandCatalog.FixedBaudCommands).
    /// </summary>
    public void DumpEdc15FlashBoot(
        string? filename, Action<string>? onChecksumWarning = null, Func<bool>? isStopRequested = null)
    {
        // See DumpEdc15Flash's identical stopwatch reasoning.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var bootMode = new Edc15BootModeVM(_kwpCommon.Interface);
            bootMode.Connect(isStopRequested);

            var dumpFileName = filename ?? "EDC15_Flash_BootMode.bin";
            Log.WriteLine($"Reading EDC15 flash (boot mode) to {dumpFileName}...");
            bootMode.ReadFlash(
                dumpFileName,
                onPercent: percent => Log.WriteLine($"{percent}%"),
                isStopRequested: isStopRequested);
            Log.WriteLine("Done!");

            // Same checksum sanity-check as DumpEdc15Flash -- the flash
            // content is identical regardless of which path read it, so a mismatch
            // here is just as meaningful a "this read may be corrupted, consider redoing it" signal.
            try
            {
                var readBytes = File.ReadAllBytes(dumpFileName);
                var checksumResult = Edc15Checksum.Verify(readBytes);
                if (checksumResult.Supported && !checksumResult.Valid)
                {
                    var message =
                        $"The checksums stored in {dumpFileName} don't match its contents " +
                        $"({checksumResult.RegionsMismatched} of {checksumResult.RegionsChecked} " +
                        $"region(s), {checksumResult.Algorithm} algorithm). This can mean the read " +
                        "was corrupted or interrupted partway through -- consider reading again.";
                    Log.WriteLine($"\nWARNING: {message}\n");
                    onChecksumWarning?.Invoke(message);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"(Checksum verification skipped: {ex.Message})\n");
            }
        }
        finally
        {
            Log.WriteLine($"Read time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }

    /// <summary>
    /// Writes a full 512KB EDC15 external-flash image via the microcontroller's own hardware boot
    /// mode -- the write counterpart to <see cref="DumpEdc15FlashBoot"/>, and a completely different,
    /// lower-level path than <see cref="LoadEdc15Flash"/> (which talks KWP2000 to the ECU's normal
    /// firmware). Requires the ECU to already be physically placed into boot mode and the interface
    /// to already be at 28800 baud (see CommandCatalog.FixedBaudCommands). Whole-chip-erases the flash
    /// (validated on hardware), then programs the image. See
    /// <see cref="Edc15BootModeVM.EraseAndWriteFlash"/> for the register-level protocol.
    /// </summary>
    public void LoadEdc15FlashBoot(string filename, Func<bool>? isStopRequested = null)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var image = File.ReadAllBytes(filename);
            if (image.Length != Edc15BootModeVM.FlashSize)
            {
                Log.WriteLine(
                    $"ERROR: {filename} is {image.Length} bytes; a boot-mode flash image must be " +
                    $"exactly {Edc15BootModeVM.FlashSize} (0x{Edc15BootModeVM.FlashSize:X}) bytes. Aborting.");
                return;
            }

            var bootMode = new Edc15BootModeVM(_kwpCommon.Interface);
            bootMode.ConnectForWrite(isStopRequested);

            Log.WriteLine($"Writing EDC15 flash (boot mode, whole-chip erase) from {filename}...");
            bootMode.EraseAndWriteFlash(
                image,
                onPercent: percent => Log.WriteLine($"{percent}%"),
                isStopRequested: isStopRequested);
            Log.WriteLine("Done!");
        }
        finally
        {
            Log.WriteLine($"Write time: {FormatElapsed(stopwatch.Elapsed)}");
        }
    }

    public void DumpEeprom(uint address, uint length, string? filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                ClusterDumpEeprom((ushort)address, (ushort)length, filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                CcmDumpEeprom((ushort)address, (ushort)length, filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void DumpMarelliMem(
        uint address, uint length, ControllerInfo ecuInfo, string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for clusters");
        }
        else
        {
            ICluster cluster = new MarelliCluster(_kwp1281, ecuInfo.Text);
            cluster.DumpEeprom(address, length, filename);
        }
    }

    public void DumpMem(uint address, uint length, string? filename)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster");
            return;
        }

        ClusterDumpMem(address, length, filename);
    }

    public void DumpRam(uint startAddr, uint length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        const int maxReadLength = 8;
        bool succeeded = true;
        string dumpFileName = filename ?? $"ram_0x{startAddr:X4}.bin";

        using (var fs = File.Create(dumpFileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadRam((ushort)addr, (byte)readLength);
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

    public void DumpRom(uint startAddr, uint length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        const int maxReadLength = 8;
        bool succeeded = true;
        string dumpFileName = filename ?? $"rom_0x{startAddr:X4}.bin";

        using (var fs = File.Create(dumpFileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadRomEeprom((ushort)addr, (byte)readLength);
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

    /// <summary>
    /// Dumps the memory of a Bosch RB4/RB8 cluster to a file.
    /// </summary>
    /// <returns>The dump file name or null if the EEPROM was not dumped.</returns>
    public string? DumpRBxMem(
        uint address, uint length, string? filename,
        bool evenParityWakeup = true)
    {
        if (_controllerAddress != (int)ControllerAddress.Cluster)
        {
            Log.WriteLine("Only supported for cluster (address 17)");
            return null;
        }

        var kwp2000 = Kwp2000Wakeup(evenParityWakeup);

        var dumpFileName = filename ?? $"RBx_0x{address:X6}_mem.bin";

        ICluster cluster = new BoschRBxCluster(kwp2000);
        cluster.UnlockForEepromReadWrite();
        cluster.DumpEeprom(address, length, dumpFileName);

        return dumpFileName;
    }

    /// <summary>
    /// Connects to the cluster and gets its unique ID. This is normally done by the radio in
    /// order to detect if its been moved to a different vehicle.
    /// </summary>
    public void GetClusterId()
    {
#if false
        if (_controllerAddress != 0x3F)
        {
            Log.WriteLine("Only supported for special cluster address $3F");
            return;
        }
#endif

        _kwp1281.SendBlock(new List<byte>
        {
            (byte)BlockTitle.SecurityAccessMode1,

            // The radio would send 4 random values for obfuscation, but the cluster ignores
            // them so we'll just send 0's.
            0x00, 0x00, 0x00, 0x00 // Challenge
        });

        var block = _kwp1281.ReceiveBlocks().FirstOrDefault(b => !b.IsAckNak);

        if (block == null)
        {
            Log.WriteLine("No response received from cluster.");
        }
        else if (block.Title != (byte)BlockTitle.SecurityAccessMode2)
        {
            Log.WriteLine(
                $"Unexpected response received from cluster. Block title: ${block.Title:X2}");
        }
        else
        {
            (byte id1, byte id2) = DecodeClusterId(block.Body[0], block.Body[1], block.Body[2], block.Body[3]);
            Log.WriteLine($"Cluster Id: ${id1:X2} ${id2:X2}");
        }
    }

    public void GetSkc()
    {
        if (_controllerAddress is (int)ControllerAddress.Cluster or (int)ControllerAddress.Immobilizer)
        {
            var ecuInfo = Kwp1281Wakeup();

            if (ecuInfo.Text.Contains("M73"))
            {
                ICluster cluster = new MarelliCluster(_kwp1281, ecuInfo.Text);

                string dumpFileName = cluster.DumpEeprom(
                    address: null, length: null, dumpFileName: null);
                byte[] buf = File.ReadAllBytes(dumpFileName);
                ushort? skc = MarelliCluster.GetSkc(buf);
                if (skc.HasValue)
                {
                    Log.WriteLine($"SKC: {skc:D5}");
                }
                else
                {
                    Log.WriteLine($"Unable to determine SKC for cluster: {ecuInfo.Text}");
                }
            }
            else if (ecuInfo.Text.Contains("4B0920") ||
                     ecuInfo.Text.Contains("4Z7920") ||
                     ecuInfo.Text.Contains("8D0920") ||
                     ecuInfo.Text.Contains("8Z0920"))
            {
                var family = ecuInfo.Text[..2] switch
                {
                    "8D" => "A4",
                    "8Z" => "A2",
                    _ => "C5"
                };

                Log.WriteLine($"Cluster is Audi {family}");

                var cluster = new AudiC5Cluster(_kwp1281);

                cluster.UnlockForEepromReadWrite();
                var dumpFileName = cluster.DumpEeprom(0, 0x800, $"Audi{family}.bin");

                var buf = File.ReadAllBytes(dumpFileName);

                var skc = Utils.GetShort(buf, 0x7E2);
                var skc2 = Utils.GetShort(buf, 0x7E4);
                var skc3 = Utils.GetShort(buf, 0x7E6);
                if (skc != skc2 || skc != skc3)
                {
                    Log.WriteLine($"Warning: redundant SKCs do not match: {skc:D5} {skc2:D5} {skc3:D5}");
                }
                else
                {
                    Log.WriteLine($"SKC: {skc:D5}");
                }
            }
            else if (
                ecuInfo.Text.Contains("VDO") ||
                ecuInfo.Text.Contains("V2721446") ||
                ecuInfo.Text.Contains("V2823466"))
            {
                var cluster = new VdoCluster(_kwp1281);
                string[] partNumberGroups = FindAndParsePartNumber(ecuInfo.Text);
                if (partNumberGroups.Length == 4)
                {
                    string dumpFileName;
                    ushort startAddress;
                    byte[] buf;
                    ushort? skc;
                    if (partNumberGroups[1] == "919") // Non-CAN
                    {
                        startAddress = 0x1FA;
                        dumpFileName = ClusterDumpEeprom(startAddress, length: 6, filename: null);
                        buf = File.ReadAllBytes(dumpFileName);
                        skc = Utils.GetBcd(buf, 0);
                        ushort skc2 = Utils.GetBcd(buf, 2);
                        ushort skc3 = Utils.GetBcd(buf, 4);
                        if (skc != skc2 || skc != skc3)
                        {
                            Log.WriteLine($"Warning: redundant SKCs do not match: {skc:D5} {skc2:D5} {skc3:D5}");
                        }
                    }
                    else if (partNumberGroups[1] == "920") // CAN
                    {
                        startAddress = 0x90;
                        dumpFileName = ClusterDumpEeprom(startAddress, length: 0x7C, filename: null);
                        buf = File.ReadAllBytes(dumpFileName);
                        skc = VdoCluster.GetSkc(buf, startAddress);
                    }
                    else
                    {
                        Log.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                        return;
                    }

                    if (skc.HasValue)
                    {
                        Log.WriteLine($"SKC: {skc:D5}");
                    }
                    else
                    {
                        Log.WriteLine($"Unable to determine SKC.");
                    }
                }
                else
                {
                    Log.WriteLine($"Unknown cluster: {ecuInfo.Text}");
                }
            }
            else if (ecuInfo.Text.Contains("RB4"))
            {
                // Need to quit KWP1281 before switching to KWP2000
                _kwp1281.EndCommunication();
                Thread.Sleep(TimeSpan.FromSeconds(2));

                var dumpFileName = DumpRBxMem(0x10046, 2, filename: null);
                var buf = File.ReadAllBytes(dumpFileName!);
                if (buf.Length == 2)
                {
                    var skc = Utils.GetShort(buf, 0);
                    Log.WriteLine($"SKC: {skc:D5}");
                }
                else
                {
                    Log.WriteLine("Unable to read SKC. Cluster not in New mode (4)?");
                }
            }
            else if (ecuInfo.Text.Contains("RB8"))
            {
                // Need to quit KWP1281 before switching to KWP2000
                _kwp1281.EndCommunication();
                Thread.Sleep(TimeSpan.FromSeconds(2));

                var dumpFileName = DumpRBxMem(0x1040E, 2, filename: null);
                var buf = File.ReadAllBytes(dumpFileName!);
                var skc = Utils.GetShort(buf, 0);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("BOO") || ecuInfo.Text.Contains("MM0"))
            {
                ICluster cluster = new MotometerBOOCluster(_kwp1281!);

                cluster.UnlockForEepromReadWrite();

                var dumpFileName = BOOClusterDumpEeprom(
                    startAddress: 0, length: 0x10, filename: null);

                var buf = File.ReadAllBytes(dumpFileName);
                var skc = Utils.GetBcd(buf, 0x08);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("VWZ3Z0"))
            {
                // IMMO BOX 1 1H0 953 257 and 7M0 953 257 support based on sniffed communication.
                // 7M0 953 257 can be both IMMO BOX 1 or IMMO BOX 2.

                var blockBytes = _kwp1281.ReadRomEeprom(0x0190, 176);
                if (blockBytes == null)
                {
                    Log.WriteLine("ROM read failed");
                    return;
                }
                else if (blockBytes.Count == 0)
                {

                    if (ecuInfo.Text.Contains("1H0"))
                    {
                        Log.WriteLine("Failed to read SKC. Immo appears to be locked. You have to use an adapted key.");
                        return;
                    }
                    else if (ecuInfo.Text.Contains("6H0") || ecuInfo.Text.Contains("7M0"))
                    {
                        // This part adds IMMO BOX 2 experimental support (could not test this with real box).
                        // Should work for 6H0 953 257 and 7M0 953 257

                        Log.WriteLine("Trying to unlock IMMO BOX 2. This function is experimental and may not work...");

                        // Unlock ROM
                        _kwp1281.SendBlock([0xCB, 0x5D, 0x3B, 0xD3, 0x8A]);

                        // Send custom read command
                        blockBytes = _kwp1281.ReadSecureImmoAccess([0x02, 0x00, 0x65, 0x34, 0x9D]);

                        if (blockBytes == null || blockBytes.Count == 0)
                        {
                            Log.WriteLine("Failed to read SKC. Immo appears to be locked. You have to use an adapted key.");
                            return;
                        }
                    }
                    else
                    {
                        Log.WriteLine("Failed to read SKC for non 1H0/6H0/7M0 ECU.");
                        return;
                    }
                }

                var skc = Utils.GetShortBE(blockBytes.ToArray(), 1);
                Log.WriteLine($"SKC: {skc:D5}");
            }
            else if (ecuInfo.Text.Contains("AGD"))
            {
                Log.WriteLine($"Unsupported Magneti Marelli AGD cluster: {ecuInfo.Text}");
            }
            else
            {
                Log.WriteLine($"Unsupported cluster: {ecuInfo.Text}");
            }
        }
        else if (_controllerAddress == (int)ControllerAddress.Ecu)
        {
            var ecuInfo = Kwp1281Wakeup();
            var eeprom = ReadWriteEdc15Eeprom(filename: null);
            Edc15VM.DisplayEepromInfo(eeprom);
        }
        else
        {
            Log.WriteLine(
                "GetSKC only supported for clusters (address 17), Immo boxes (address 25) and ECUs (address 1)");
        }
    }

    /// <summary>
    /// Takes the info returned when connecting to the ECU, finds the ECU part number and
    /// splits into its components. For example, if the ECU info is this:
    ///     "1J5920926CX   KOMBI+WEGFAHRSP VDO V01"
    /// Then the part number would be identified as "1J5920926CX", which would be split into
    /// its 4 components: "1J5", "920", "926", "CX"
    /// </summary>
    /// <param name="ecuInfo"></param>
    /// <returns>A 4-element string array if the part number was found, otherwise an empty
    /// string array.</returns>
    internal static string[] FindAndParsePartNumber(string ecuInfo)
    {
        var match = Regex.Match(
            ecuInfo,
            "\\b(\\d[a-zA-Z][0-9a-zA-Z])(9\\d{2})(\\d{3})([a-zA-Z]{0,2})\\b");

        if (match.Success)
        {
            return (match.Groups as IReadOnlyList<Group>).Skip(1).Select(g => g.Value).ToArray();
        }
        else
        {
            return Array.Empty<string>();
        }
    }

    public void GroupRead(byte groupNumber)
    {
        var succeeded = _kwp1281.GroupRead(groupNumber);
    }

    public void LoadEeprom(uint address, string filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                ClusterLoadEeprom((ushort)address, filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                CcmLoadEeprom((ushort)address, filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void MapEeprom(string? filename)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                ClusterMapEeprom(filename);
                break;
            case (int)ControllerAddress.CCM:
            case (int)ControllerAddress.CentralElectric:
            case (int)ControllerAddress.CentralLocking:
                CcmMapEeprom(filename);
                break;
            default:
                Log.WriteLine("Only supported for cluster, CCM, Central Locking and Central Electric");
                break;
        }
    }

    public void ReadEeprom(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadEeprom((ushort)address, 1);
        if (blockBytes == null)
        {
            Log.WriteLine("EEPROM read failed");
        }
        else
        {
            var value = blockBytes[0];
            Log.WriteLine(
                $"Address {address} (${address:X4}): Value {value} (${value:X2})");
        }
    }

    public void ReadRam(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadRam((ushort)address, 1);
        if (blockBytes == null)
        {
            Log.WriteLine("RAM read failed");
        }
        else
        {
            var value = blockBytes[0];
            Log.WriteLine(
                $"Address {address} (${address:X4}): Value {value} (${value:X2})");
        }
    }

    public void ReadRom(uint address)
    {
        UnlockControllerForEepromReadWrite();

        var blockBytes = _kwp1281.ReadRomEeprom((ushort)address, 1);
        if (blockBytes == null)
        {
            Log.WriteLine("ROM read failed");
        }
        else
        {
            var value = blockBytes[0];
            Log.WriteLine(
                $"Address {address} (${address:X4}): Value {value} (${value:X2})");
        }
    }

    public void ReadFaultCodes()
    {
        var faultCodes = _kwp1281.ReadFaultCodes();
        if (faultCodes != null)
        {
            Log.WriteLine("Fault codes:");
            foreach (var faultCode in faultCodes)
            {
                Log.WriteLine($"    {faultCode}");
            }
        }
    }

    public void ReadIdent()
    {
        foreach (var identInfo in _kwp1281.ReadIdent())
        {
            Log.WriteLine($"Ident: {identInfo}");
        }
    }

    public void ReadSoftwareVersion()
    {
        if (_controllerAddress == (int)ControllerAddress.Cluster)
        {
            var cluster = new VdoCluster(_kwp1281);
            cluster.CustomReadSoftwareVersion();
        }
        else
        {
            Log.WriteLine("Only supported for cluster");
        }
    }

    public void Reset()
    {
        if (_controllerAddress == (int)ControllerAddress.Cluster)
        {
            var cluster = new VdoCluster(_kwp1281);
            cluster.CustomReset();
        }
        else
        {
            Log.WriteLine("Only supported for cluster");
        }
    }

    public void SetSoftwareCoding(
        int softwareCoding, int workshopCode)
    {
        var succeeded = _kwp1281.SetSoftwareCoding(_controllerAddress, softwareCoding, workshopCode);
        if (succeeded)
        {
            Log.WriteLine("Software coding set.");
        }
        else
        {
            Log.WriteLine("Failed to set software coding.");
        }
    }

    public void ToggleRB4Mode()
    {
        var kwp2000 = Kwp2000Wakeup(evenParityWakeup: true);

        BoschRBxCluster cluster = new(kwp2000);
        cluster.UnlockForEepromReadWrite();
        cluster.ToggleRB4Mode();
    }

    public void WriteEeprom(uint address, byte value)
    {
        UnlockControllerForEepromReadWrite();

        _kwp1281.WriteEeprom((ushort)address, new List<byte> { value });
    }

    public void WriteRam(uint address, byte value)
    {
        switch (_controllerAddress)
        {
            case (int)ControllerAddress.Cluster:
                ClusterWriteRam((ushort)address, value);
                break;
            default:
                Log.WriteLine("Only supported for cluster");
                break;
        }

    }

    // End top-level commands

    private void ClusterWriteRam(ushort address, byte value)
    {
        // TODO: Verify cluster is VDO

        var vdoCluster = new VdoCluster(_kwp1281);
        if (!vdoCluster.RequiresSeedKey())
        {
            Log.WriteLine(
                "Cluster is unlocked for memory access. Skipping Seed/Key login.");
    }
        else
    {
            var (isUnlocked, softwareVersion) = vdoCluster.Unlock();
            if (!isUnlocked)
        {
                Log.WriteLine("Unknown cluster software version. Memory access will likely fail.");
        }
            vdoCluster.SeedKeyAuthenticate(softwareVersion);
        }

        vdoCluster.WriteRam(address, value);
    }

    private string BOOClusterDumpEeprom(ushort startAddress, ushort length, string? filename)
    {
        var identInfo = _kwp1281.ReadIdent().First().ToString()
            .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
            .Replace(' ', '_')
            .Replace('.', '_')
            .Replace(":", "");

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
    }

    private string ClusterDumpEeprom(
        ushort startAddress, ushort length, string? filename)
    {
        var identInfo = _kwp1281.ReadIdent().First().ToString()
            .Split(Environment.NewLine).First() // Sometimes ReadIdent() can return multiple lines
            .Replace(' ', '_').Replace(":", "");

        ICluster cluster = new VdoCluster(_kwp1281);
        cluster.UnlockForEepromReadWrite();

        var dumpFileName = filename ?? $"{identInfo}_0x{startAddress:X4}_eeprom.bin";

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        cluster.DumpEeprom(startAddress, length, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");

        return dumpFileName;
    }

    private void CcmMapEeprom(string? filename)
    {
        UnlockControllerForEepromReadWrite();

        var bytes = new List<byte>();
        const byte blockSize = 1;
        for (int addr = 0; addr <= 65535; addr += blockSize)
        {
            var blockBytes = _kwp1281.ReadEeprom((ushort)addr, blockSize);
            blockBytes = Enumerable.Repeat(
                blockBytes == null ? (byte)0 : (byte)0xFF,
                blockSize).ToList();
            bytes.AddRange(blockBytes);
        }
        var dumpFileName = filename ?? "ccm_eeprom_map.bin";
        Log.WriteLine($"Saving EEPROM map to {dumpFileName}");
        File.WriteAllBytes(dumpFileName, bytes.ToArray());
    }

    private void ClusterMapEeprom(string? filename)
    {
        var cluster = new VdoCluster(_kwp1281);

        var map = cluster.MapEeprom();

        var mapFileName = filename ?? "eeprom_map.bin";
        Log.WriteLine($"Saving EEPROM map to {mapFileName}");
        File.WriteAllBytes(mapFileName, map.ToArray());
    }

    private void CcmDumpEeprom(ushort startAddress, ushort length, string? filename)
    {
        UnlockControllerForEepromReadWrite();

        var dumpFileName = filename ?? $"ccm_eeprom_0x{startAddress:X4}.bin";

        Log.WriteLine($"Saving EEPROM dump to {dumpFileName}");
        DumpEeprom(startAddress, length, maxReadLength: 8, dumpFileName);
        Log.WriteLine($"Saved EEPROM dump to {dumpFileName}");
    }

    private void UnlockControllerForEepromReadWrite()
    {
        switch ((ControllerAddress)_controllerAddress)
        {
            case ControllerAddress.CCM:
            case ControllerAddress.CentralLocking:
                _kwp1281.Login(
                    code: 19283,
                    workshopCode: 222); // This is what VDS-PRO uses
                break;

            case ControllerAddress.CentralElectric:
                _kwp1281.Login(
                    code: 21318,
                    workshopCode: 222); // This is what VDS-PRO uses
                break;

            case ControllerAddress.Cluster:
                // TODO:UnlockCluster() is only needed for EEPROM read, not memory read
                var vdoCluster = new VdoCluster(_kwp1281);
                var (isUnlocked, softwareVersion) = vdoCluster.Unlock();
                if (!isUnlocked)
                {
                    Log.WriteLine("Unknown cluster software version. EEPROM access will likely fail.");
                }

                if (!vdoCluster.RequiresSeedKey())
                {
                    Log.WriteLine(
                        "Cluster is unlocked for ROM/EEPROM access. Skipping Seed/Key login.");
                    return;
                }

                vdoCluster.SeedKeyAuthenticate(softwareVersion);
                if (vdoCluster.RequiresSeedKey())
                {
                    Log.WriteLine("Failed to unlock cluster.");
                }
                else
                {
                    Log.WriteLine("Cluster is unlocked for ROM/EEPROM access.");
                }
                break;
        }
    }

    private void DumpEeprom(
        ushort startAddr, uint length, byte maxReadLength, string fileName)
    {
        bool succeeded = true;

        using (var fs = File.Create(fileName, maxReadLength, FileOptions.WriteThrough))
        {
            for (uint addr = startAddr; addr < (startAddr + length); addr += maxReadLength)
            {
                var readLength = (byte)Math.Min(startAddr + length - addr, maxReadLength);
                var blockBytes = _kwp1281.ReadEeprom((ushort)addr, (byte)readLength) ?? [];
                if (blockBytes.Count < readLength)
                {
                    blockBytes.AddRange(Enumerable.Repeat((byte)0, readLength - blockBytes.Count));
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

    private void WriteEeprom(
        ushort startAddr, byte[] bytes, uint maxWriteLength)
    {
        var succeeded = true;
        var length = bytes.Length;
        for (uint addr = startAddr; addr < (startAddr + length); addr += maxWriteLength)
        {
            var writeLength = (byte)Math.Min(startAddr + length - addr, maxWriteLength);
            if (!_kwp1281.WriteEeprom(
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

    private void CcmLoadEeprom(ushort address, string filename)
    {
        _ = _kwp1281.ReadIdent();

        UnlockControllerForEepromReadWrite();

        if (!File.Exists(filename))
        {
            Log.WriteLine($"File {filename} does not exist.");
            return;
        }

        Log.WriteLine($"Reading {filename}");
        var bytes = File.ReadAllBytes(filename);

        Log.WriteLine("Writing to cluster...");
        WriteEeprom(address, bytes, 8);
    }

    private void ClusterLoadEeprom(ushort address, string filename)
    {
        _ = _kwp1281.ReadIdent();

        UnlockControllerForEepromReadWrite();

        if (!File.Exists(filename))
        {
            Log.WriteLine($"File {filename} does not exist.");
            return;
        }

        Log.WriteLine($"Reading {filename}");
        var bytes = File.ReadAllBytes(filename);

        Log.WriteLine("Writing to cluster...");
        WriteEeprom(address, bytes, 16);
    }

    private void ClusterDumpMem(uint startAddress, uint length, string? filename)
    {
        // TODO: Verify cluster is VDO

        var vdoCluster = new VdoCluster(_kwp1281);
        if (!vdoCluster.RequiresSeedKey())
        {
            Log.WriteLine(
                "Cluster is unlocked for memory access. Skipping Seed/Key login.");
        }
        else
        {
            var (isUnlocked, softwareVersion) = vdoCluster.Unlock();
            if (!isUnlocked)
            {
                Log.WriteLine("Unknown cluster software version. Memory access will likely fail.");
            }
            vdoCluster.SeedKeyAuthenticate(softwareVersion);
        }

        var dumpFileName = filename ?? $"cluster_mem_0x{startAddress:X6}.bin";
        Log.WriteLine($"Saving memory dump to {dumpFileName}");

        vdoCluster.DumpMem(dumpFileName, startAddress, length);

        Log.WriteLine($"Saved memory dump to {dumpFileName}");
    }

    private static (byte, byte) DecodeClusterId(byte b1, byte b2, byte b3, byte b4)
    {
        // For obfuscation, the cluster adds the values below, so we need to subtract them:
        bool carry = true;
        (b1, carry) = Utils.SubtractWithCarry(b1, 0xE7, carry);
        (b2, carry) = Utils.SubtractWithCarry(b2, 0xBD, carry);
        (b3, carry) = Utils.SubtractWithCarry(b3, 0x18, carry);
        (b4, carry) = Utils.SubtractWithCarry(b4, 0x00, carry);

        b1 ^= b3;
        b2 ^= b4;

        // Count the number of 0 bits in b1 and b2

        byte zeroCount = 0;
        for (int i = 0; i < 8; i++)
        {
            if (((b1 >> i) & 1) == 0)
            {
                zeroCount++;
            }
            if (((b2 >> i) & 1) == 0)
            {
                zeroCount++;
            }
        }

        // Right-rotate b3 and b4 zeroCount times:
        for (int i = 0; i < zeroCount; i++)
        {
            carry = (b4 & 1) != 0;
            (b3, carry) = Utils.RightRotate(b3, carry);
            (b4, carry) = Utils.RightRotate(b4, carry);
        }

        b1 ^= b3;
        b2 ^= b4;

        return (b1, b2);
    }
}
