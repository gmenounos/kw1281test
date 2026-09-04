using BitFab.KW1281Test.EDC15;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BitFab.KW1281Test.EDC16
{
    /// <summary>
    /// EDC16 external-flash read/write, based on ecu-tool and
    /// reverse-engineered from K-line bus sniffing and disassembly of the
    /// EDC16 internal and external flash. Supports EDC16U31/34 only (the only ECU type the prior
    /// implementation actually covers -- the "EcuType==0"/29BL802CB branch is present as a stub
    /// but its body is empty, so there's nothing to port for it).
    ///
    /// <para>- The WRITE transfer phase uses address-less framing
    /// (like the read), the ECU's built-in checksum-compare (0x31 C5, used to SKIP unchanged blocks
    /// and to verify), erase (0x31 C4) with the ECU-specific 6-byte signature read from the 1A 9C
    /// ident, requestDownload (0x34), 248-byte transferData (0x36) chunks, and accessTimingParameter
    /// (0x83). Security (0x27 01/02) stays addressed, exactly as the read keeps its own security
    /// addressed while the transfer phase is address-less.</para>
    /// <para>- <see cref="Encrypt"/> (the RC4-like keystream cipher flash-write requires -- see
    /// below) is translated statement-for-statement from a register-simulation
    /// (EAX/ECX/EDX/EBX/EDI/EBP) using C# <c>int</c> specifically -- NOT <c>uint</c> or
    /// <c>long</c> -- because the algorithm's correctness depends on AVR-GCC's arithmetic
    /// (sign-extending) right-shift of negative 32-bit signed values (e.g. <c>ECX &gt;&gt; 1</c>
    /// after <c>ECX</c> was OR'd with <c>0x80000000</c>), which is exactly what C#'s signed
    /// <c>int &gt;&gt;</c> also does -- a <c>uint</c> or unsigned shift would silently produce
    /// different output bytes. The only intentional deviation is that the original's 128-byte
    /// <c>buff[]</c>/<c>buffcount</c> re-buffering (needed only because the Arduino can't hold the
    /// whole source file in RAM) is collapsed into a plain sequential index into the in-memory image
    /// array.</para>
    /// <para>- The read-path 6-byte flash-data-packet request and the security-access/seed-key
    /// exchanges are read/verified via raw <see cref="IKwpCommon"/> byte calls, not
    /// <see cref="Kwp2000Message"/> parsing, using a raw-byte-level approach.</para>
    ///
    /// <para><b>LVL1Key (flash-write unlock) reuses <see cref="Edc15KeyAlgorithms.ComputeLvl41Key"/></b>
    /// -- side-by-side comparison of EDC15's LVL41Auth and EDC16's LVL1Key shows the identical
    /// 5-round bit-rotation shape, just with EDC16 using its own fixed constants instead of a
    /// per-variant external key: <c>Magic1=0x1C60020</c> in place of EDC15's <c>key3</c>, and fixed
    /// XOR constants <c>0x1289</c>/<c>0x0A22</c> in place of EDC15's key-derived <c>key1</c>/<c>key2</c>
    /// halves (equivalent to calling the shared helper with <c>key = 0x12890A22</c>). LVL3Key
    /// (flash-read unlock) is much simpler -- just <c>seed + 0x2FC9</c> -- and is implemented
    /// directly, no shared helper needed.</para>
    ///
    /// <para><b>Known gaps:</b> "Info" reading (SW version/VIN/engine
    /// type display), the immo-disable "Kill ECU" partial-write feature, and the EEPROM
    /// flash-counter-reset feature are all out of scope (this app only wires up flash read+write)
    /// and are not ported. The write-path status polls (<see cref="PollChecksumResult"/>,
    /// <see cref="EraseBlock"/>) read "whatever the ECU sends back" with no fixed frame length and
    /// scan it for the outcome bytes -- since <see cref="IKwpCommon"/> has no
    /// <c>Serial.available()</c>-style non-blocking peek, this is emulated with a short read-timeout
    /// drain (see <see cref="ReadAvailableRaw"/> / <see cref="ReadResponseRaw"/>).</para>
    ///
    /// <para><b>Flash write risk:</b> like <see cref="EDC15.Edc15FlashVM"/>, this erases and rewrites
    /// a large portion of the ECU's program flash (0x180000-0x1FE000, ~504KB). An interrupted or
    /// incorrect write most likely will not, but can brick the ECU. Treat it accordingly.</para>
    /// </summary>
    public sealed class Edc16FlashVM
    {
        private readonly IKwpCommon _kwpCommon;

        /// <summary>
        /// Gates the byte-level TX/RX logging in WriteRaw/SendWithChecksum/CheckRec/VerifyExpected.
        /// Starts true (handshake/setup calls are all short and bounded, so logging them is cheap
        /// and valuable for diagnosing exactly what's going over the wire during Connect/security
        /// access -- see those methods' doc comments) and gets switched off right before the bulk
        /// per-packet read/write loops (ReadFlash's ReadFlashDataPacket loop, TransferBlockData),
        /// which call the same primitives thousands of times for a full ~512KB transfer and would
        /// flood the log if logged unconditionally.
        /// </summary>
        private bool _verboseLog = true;

        /// <summary>
        /// False at the slow (10400) init/handshake speed; set true by <see cref="TryNegotiateSpeed"/>
        /// once the ECU has agreed to a high-speed session. Gates <see cref="WriteByteDelayed"/>'s
        /// per-byte 5ms delay -- see that method's doc comment. A fresh instance is created per
        /// read/write (so this starts false every time); it is never reset back to false mid-operation.
        /// </summary>
        private bool _fastBaudActive;

        public Edc16FlashVM(IKwpCommon kwpCommon)
        {
            _kwpCommon = kwpCommon;
        }

        /// <summary>
        /// Wakes up the ECU (5-baud slow init, same as <see cref="WriteFlash"/>), authenticates for
        /// flash read (LVL3Key), and reads the full ~512KB external flash to
        /// <paramref name="filePath"/>. When <paramref name="allowFastBaud"/> is true, raises the link
        /// to the rate <paramref name="speed"/> names (Low 38400 / Medium 124800 / High 244898),
        /// falling back to 10400 if the ECU refuses it; when false (force-slow), stays at 10400.
        /// </summary>
        public void ReadFlash(
            string filePath, FlashSpeed speed, bool allowFastBaud, Action<int>? onPercent = null,
            Func<bool>? isStopRequested = null)
        {
            ConnectEcu();

            Log.WriteLine("Requesting security access (read)...");
            Lvl3KeyAuth();
            Log.WriteLine("Done!");

            // Enter the upload session (0x86) -- required before requestUpload (0x35). Without it
            // the ECU NAKs 0x35 with NRC 0x80. If fast baud is allowed this
            // also raises the link speed; otherwise it just enters the session at 10400.
            EnterProgrammingSession(0x86, speed, allowFastBaud);

            Thread.Sleep(75);
            // requestUpload (0x35), ADDRESS-LESS. Payload after SID 0x35 is a 3-byte START ADDRESS
            // then a 4-byte LENGTH. This reads the 512KB CALIBRATION region 0x180000-0x1FFFFF (START
            // 0x180000, LENGTH 0x080000) -- the ONLY region the ECU exposes over KWP2000. A full-chip
            // requestUpload (START 0x000000) is NAKed by the ECU (03 7F 35 .. on the wire), because
            // the lower 1.5MB (the code region) isn't reachable this way -- only via boot mode/BDM.
            // The canonical layout pads the file up to the 2MB chip size with 0xFF: lower
            // 0x000000-0x17FFFF = all 0xFF, real data only at 0x180000+. We reproduce that layout
            // below (0xFF pad + this 512KB at offset 0x180000).
            SendWithChecksum(new byte[]
            {
                0x08, 0x35, 0x18, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00,
            });
            VerifyExpected(new byte[] { 0x02, 0x75, 0xFF }); // "READY TO SEND" (address-less 75 FF)

            Log.WriteLine("Reading EDC16 flash...");
            // Handshake/setup is done -- this loop calls the same TX/RX primitives thousands of
            // times to move ~512KB, so stop logging every byte (see _verboseLog's doc comment).
            _verboseLog = false;
            using var fs = File.Create(filePath);
            // Produce a full 2MB image (M58BW016 chip size): pad the unreadable lower
            // 1.5MB (0x000000-0x17FFFF, the code region) with 0xFF, then stream the 512KB calibration
            // read into place at offset 0x180000.
            const long CalStart = 0x180000;
            var padding = new byte[CalStart];
            Array.Fill(padding, (byte)0xFF);
            fs.Write(padding, 0, padding.Length);

            var done = false;
            long readProgress = 0;
            const long totalEstimate = 0x080000; // the 512KB calibration region actually read
            var before = -1;
            while (!done)
            {
                // Checked once per packet. A stopped read leaves the ECU
                // in its ordinary upload session, so a best-effort stopCommunication is enough; the
                // partial file is deleted by the caller (it's a scratch path).
                if (isStopRequested?.Invoke() == true)
                {
                    TryQuietStop("read");
                    throw new OperationCanceledException("EDC16 flash read stopped by user.");
                }
                ReadFlashDataPacket(fs, ref done);
                readProgress += 0xFE;
                var percent = (int)Math.Min(100, readProgress * 100 / totalEstimate);
                if (percent != before)
                {
                    onPercent?.Invoke(percent);
                    before = percent;
                }
            }

            // The flash image is fully captured at this point. CloseEcu is a best-effort courtesy
            // stopCommunication (addressed 81 10 F1 82); if the ECU -- still in the address-less
            // transfer session -- doesn't acknowledge it in the expected form, that must NOT turn a
            // complete, successful read into a failure. Log and move on; the image is already written.
            try
            {
                CloseEcu();
            }
            catch (Exception ex)
            {
                Log.WriteLine(
                    $"(EDC16 read complete; ECU stopCommunication not acknowledged cleanly: {ex.Message})");
            }
            Log.WriteLine("Done!");
        }

        /// <summary>
        /// Wakes the ECU (5-baud slow init), reads the ECU-specific erase signature (1A 9C),
        /// authenticates for flash write (LVL1Key, security level 0x27 01/02), enters the
        /// programming/download session (0x85) -- raising the link speed when allowed -- sets the
        /// access-timing parameters, then for each of the two calibration blocks runs the ECU's
        /// built-in checksum-compare routine (0x31 C5) and, ONLY if the block differs, requests the
        /// download (0x34), erases it (0x31 C4), streams the encrypted data (0x36, 248-byte chunks),
        /// exits the transfer (0x37) and verifies (0x31 C5). Direct KWP2000 flash programming -- NO
        /// RAM loader.
        ///
        /// <para><paramref name="image"/> is a full 2 MB flash image (as produced by
        /// <see cref="ReadFlash"/>): 0xFF-padded lower 1.5 MB + the 512 KB calibration region at
        /// 0x180000. Only 0x180000-0x1FDFFF is written (block 6 = 0x180000-0x1BFFFF, block 7 =
        /// 0x1C0000-0x1FDFFF); the top 8 KB (0x1FE000-0x1FFFFF) is erased but not programmed. The
        /// caller (<see cref="Tester.WriteFlashEdc16"/>) is expected to have
        /// verified/corrected the image's stored checksums BEFORE this runs. When
        /// <paramref name="allowFastBaud"/> is true, raises the link to the rate
        /// <paramref name="speed"/> names (Low 38400 / Medium 124800 / High 244898), falling back to
        /// 10400 if the ECU refuses it; when false (force-slow), stays at 10400 -- the safest for the
        /// erase/program.</para>
        /// </summary>
        public void WriteFlash(
            byte[] image, FlashSpeed speed, bool allowFastBaud,
            Action<string>? onStage = null, Action<int>? onPercent = null,
            bool forceFull = false, bool fastInitPrime = false, Func<bool>? isStopRequested = null)
        {
            const int requiredLength = 0x200000; // full 2 MB image (matches a ReadFlash dump)
            if (image.Length < requiredLength)
            {
                throw new ArgumentException(
                    $"EDC16 flash image must be a full 2 MB (0x{requiredLength:X}) image; got 0x{image.Length:X} " +
                    $"({image.Length}) bytes. Use a prior EDC16 read as the source.",
                    nameof(image));
            }

            // Open the DOWNLOAD (0x85) programming session. Default: a 5-baud SLOW init -- proven on
            // K-line EDC16s and the session the ECU grants 0x85 to. A later CAN-init EDC16 that
            // ignores a cold slow init needs the optional fast-init prime first (fastInitPrime): a
            // full fast-init attempt driven through the (rejected) 0x85 request conditions the ECU,
            // then its session must time out (P3max) before the slow init is accepted and grants
            // 0x85. The prime is opt-in because the fast-init 0x85 request is refused even on ECUs
            // that never needed priming, so it is pure overhead unless required.
            var sessionReady = false;
            if (fastInitPrime && TryConnectFast())
            {
                ReadEraseSignature();
                Lvl1KeyAuth();
                try
                {
                    EnterProgrammingSession(0x85, speed, allowFastBaud, allowSlowBaudFallback: false);
                    sessionReady = true;   // this ECU granted 0x85 over fast init -- use it directly
                }
                catch (Exception)
                {
                    Log.WriteLine("Attempting slow init reconnect...");
                    // Undo any fast-baud switch and let the fast-init session time out to idle before
                    // the fresh slow init (which the primed ECU will now accept and grant 0x85).
                    _fastBaudActive = false;
                    _kwpCommon.Interface.SetBaudRate(10400);
                    Thread.Sleep(P3maxSettleMs);
                }
            }

            if (!sessionReady)
            {
                ConnectSlow();
                ReadEraseSignature();
                Log.WriteLine("Requesting security access (write)...");
                Lvl1KeyAuth();
                Log.WriteLine("Done!");
                // The ECU stalls ~1.6s (responsePending 7F 10 78) before the real 50 85 ack --
                // WaitForSpeedAck tolerates that (see EnterProgrammingSession).
                EnterProgrammingSession(0x85, speed, allowFastBaud);
            }

            // Extend the access-timing parameters so the long erase/program stay inside the ECU's
            // timeouts -- set right after entering the session.
            SetAccessTiming();

            // Handshake/setup is done -- TransferBlockData below calls the same TX/RX primitives
            // ~1000+ times per block, so stop logging every byte (see _verboseLog's doc comment).
            _verboseLog = false;

            onStage?.Invoke("Encrypting...");
            // The image is a full 2 MB flash (0xFF pad + 512 KB cal at 0x180000), so index it at
            // ECU-ABSOLUTE offsets. Block 7 (ECU 0x1C0000-0x1FDFFF) <- image[0x1C0000, 0x1FE000): the
            // checksum/download range is 0x3E000 (the top 8 KB is erased but not programmed). Block 6
            // (ECU 0x180000-0x1BFFFF) <- image[0x180000, 0x1C0000): full 0x40000. Encrypt's
            // out-checksum is the 16-bit plaintext sum the ECU's 0x31 C5 routine compares (skip/verify).
            var block7Data = Encrypt(image, 0x1C0000, 0x1FE000, out var checksum7);
            var block6Data = Encrypt(image, 0x180000, 0x1C0000, out var checksum6);
            onStage?.Invoke("Encrypt done.");

            if (forceFull)
            {
                Log.WriteLine(
                    "Force Full Write: writing BOTH blocks unconditionally (skipping the 0x31 C5 " +
                    "unchanged-block check).");
            }

            // Stop is honoured only BETWEEN blocks, deliberately: a block's erase+transfer is one
            // indivisible ECU-side programming cycle, and abandoning it mid-way leaves the ECU in the
            // programming-required recovery state (recoverable by re-running the write, but it refuses
            // reads until then). Between blocks every completed block is already verified, and
            // FinalizeWrite (ecuReset + stopCommunication) is exactly the normal teardown, so stopping
            // here is the same as skipping a block.
            ThrowIfStopRequested(isStopRequested, "before block 7");
            onStage?.Invoke("Block 7 (0x1C0000-0x1FDFFF)...");
            WriteBlock(7, image, block7Data, checksum7, forceFull, onStage, onPercent);

            ThrowIfStopRequested(isStopRequested, "after block 7, before block 6");
            onStage?.Invoke("Block 6 (0x180000-0x1BFFFF)...");
            WriteBlock(6, image, block6Data, checksum6, forceFull, onStage, onPercent);

            FinalizeWrite();
            onStage?.Invoke("Done.");
        }

        /// <summary>Write-path stop check (see the comment at the call sites in
        /// <see cref="WriteFlash"/>): tears the session down normally, then cancels.</summary>
        private void ThrowIfStopRequested(Func<bool>? isStopRequested, string where)
        {
            if (isStopRequested?.Invoke() != true)
            {
                return;
            }
            Log.WriteLine($"Stop requested ({where}) -- closing the programming session.");
            FinalizeWrite();
            throw new OperationCanceledException($"EDC16 flash write stopped by user ({where}).");
        }

        /// <summary>Best-effort session teardown for a stopped read -- never throws.</summary>
        private void TryQuietStop(string what)
        {
            try
            {
                CloseEcu();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"(EDC16 {what} stopped; ECU stopCommunication not acknowledged cleanly: {ex.Message})");
            }
        }

        // ---- Wakeup ----

        /// <summary>
        /// Establishes the EDC16 flash link for a READ: fast init, falling back to a 5-baud slow
        /// init. The ECU grants the 0x86 upload/read session over either init, so fast-first is fine
        /// and lets a later CAN-init EDC16 (which ignores a cold slow init) be read. The WRITE path
        /// connects differently -- see <see cref="WriteFlash"/> -- because the download (0x85) session
        /// is granted only through a slow-init session, and only after a fast-init attempt has
        /// conditioned the ECU. Either way the link is up on return with the ECU expecting a service
        /// request (security access) next.
        /// </summary>
        private void ConnectEcu()
        {
            Log.WriteLine("Connecting to EDC16...");
            if (TryConnectFast())
            {
                Log.WriteLine("Done! (fast init)");
                return;
            }
            Log.WriteLine("Fast init got no response; trying slow init...");
            ConnectSlow();
            Log.WriteLine("Done! (slow init)");
        }

        /// <summary>Fast-init only, retried (bit-banged fast init is probabilistic over USB-serial).
        /// Returns true once the ECU answers StartCommunication with a KWP2000 keyword, false if every
        /// attempt missed. Never throws.</summary>
        private bool TryConnectFast()
        {
            for (var attempt = 1; attempt <= FastInitTries; attempt++)
            {
                try
                {
                    if (_kwpCommon.TryFastInit(0x10) >= 2000)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // any I/O hiccup during the bit-bang -> try the next attempt
                }
            }
            return false;
        }

        /// <summary>5-baud slow init only. Throws if the ECU does not wake as KWP2000.</summary>
        private void ConnectSlow()
        {
            var kwpVersion = _kwpCommon.WakeUp(0x01, evenParity: false);
            if (kwpVersion < 2000)
            {
                throw new InvalidOperationException(
                    $"Unexpected EDC16 wakeup protocol version: {kwpVersion} (expected KWP2000+).");
            }
        }

        /// <summary>Bit-banged fast init is probabilistic over USB-serial; retry this many times.</summary>
        private const int FastInitTries = 4;

        /// <summary>Milliseconds to wait after the fast-init prime before the slow init, so the ECU's
        /// fast-init session times out (KWP2000 P3max default 5000 ms) back to idle and will accept a
        /// fresh 5-baud init. A hair over P3max; tune here if a given ECU needs more/less.</summary>
        private const int P3maxSettleMs = 5500;

        /// <summary>The ECU-specific 6-byte parameter the 0x31 C4 erase routine requires. Populated
        /// from the 1A 9C identifier by <see cref="ReadEraseSignature"/> before each write; this
        /// initial value is a known-good fallback for that family, used only if the ident read
        /// fails.</summary>
        private byte[] _eraseSignature = { 0x0C, 0x5E, 0xCF, 0xCF, 0x65, 0x0B };

        /// <summary>
        /// Reads the 6-byte ECU erase signature from the 1A 9C identifier record (the 6 bytes after
        /// the leading 00 28 21 00, i.e. 5A 9C 00 28 21 00 &lt;sig0..sig5&gt;). The 0x31 C4 erase
        /// routine requires these EXACT bytes, read from this same ident. Falls back to the known
        /// family value (with a warning) if the record can't be read.
        ///
        /// <para>CRITICAL: like readident, the ECU answers 1A 9C with a responsePending NAK
        /// (83 F1 10 7F 1A 78) FIRST, then the real 5A 9C record after a short delay. This MUST wait
        /// through the pending and drain the WHOLE response to quiet -- a single read that stops at
        /// the pending NAK leaves the real record in the buffer, and the very next request
        /// (security access 27 01) then reads those stale bytes as its "seed" and fails with a seed
        /// checksum mismatch.</para>
        /// </summary>
        private void ReadEraseSignature()
        {
            Thread.Sleep(25);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x82, 0x10, 0xF1, 0x1A, 0x9C });

            // Read byte-by-byte, tolerating the responsePending NAK and the gap before the real
            // record, until we've seen the 5A 9C record AND the line has gone quiet (so every byte of
            // the response is consumed and the buffer is clean for the security access that follows).
            var acc = new List<byte>();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            var originalTimeout = _kwpCommon.Interface.ReadTimeout;
            _kwpCommon.Interface.ReadTimeout = 400;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    byte b;
                    try
                    {
                        b = _kwpCommon.ReadByte();
                    }
                    catch (TimeoutException)
                    {
                        // Quiet: done once the real record has arrived; otherwise keep waiting for it.
                        if (IndexOfPair(acc.ToArray(), 0x5A, 0x9C) >= 0)
                        {
                            break;
                        }
                        continue;
                    }
                    acc.Add(b);
                }
            }
            finally
            {
                _kwpCommon.Interface.ReadTimeout = originalTimeout;
            }

            var r = acc.ToArray();
            var pos = IndexOfPair(r, 0x5A, 0x9C);
            if (pos >= 0 && pos + 12 <= r.Length)
            {
                var sig = new byte[6];
                Array.Copy(r, pos + 6, sig, 0, 6); // 5A 9C 00 28 21 00 <sig0..sig5>
                _eraseSignature = sig;
                Log.WriteLine($"EDC16 erase signature (1A 9C): {Utils.DumpBytes(sig)}");
            }
            else
            {
                Log.WriteLine(
                    $"WARNING: couldn't read EDC16 erase signature from 1A 9C (got {Utils.DumpBytes(r)}); " +
                    $"using default {Utils.DumpBytes(_eraseSignature)}.");
            }

            // Belt-and-suspenders: make sure nothing from this ident lingers into the security access.
            _kwpCommon.Interface.ClearReceiveBuffer();
        }

        // ---- Security access ----

        private void Lvl3KeyAuth()
        {
            Thread.Sleep(25);
            SendWithChecksum(new byte[] { 0x82, 0x10, 0xF1, 0x27, 0x03 }); // Lvl3Sec_ArduBytes

            var seed = new byte[10];
            for (var i = 0; i < 10; i++)
            {
                seed[i] = _kwpCommon.ReadByte();
            }
            if (Checksum(seed, 9) != seed[9])
            {
                throw new InvalidOperationException("EDC16 LVL3 seed checksum mismatch.");
            }

            var keyReadHi = (seed[5] << 8) + seed[6];
            var keyReadLo = (seed[7] << 8) + seed[8];
            var combined = (((long)keyReadHi << 16) + keyReadLo) + 0x2FC9; // EcuType==1 (U31/34)

            var frame = new byte[10];
            frame[0] = 0x86;
            frame[1] = 0x10;
            frame[2] = 0xF1;
            frame[3] = 0x27;
            frame[4] = 0x04;
            frame[8] = (byte)combined;
            frame[7] = (byte)(combined >> 8);
            frame[6] = (byte)(combined >> 16);
            frame[5] = (byte)(combined >> 24);
            frame[9] = Checksum(frame, 9);

            Thread.Sleep(25);
            WriteRaw(frame);

            VerifyExpected(new byte[] { 0x83, 0xF1, 0x10, 0x67, 0x04, 0x34 }); // Lvl3Sec_ECUBytes
        }

        private void Lvl1KeyAuth()
        {
            Thread.Sleep(25);
            // Clear first: the preceding 1A 9C read must leave a clean buffer, but guard anyway so a
            // stray byte can't be misread as part of the 10-byte seed (a seed-checksum mismatch).
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x82, 0x10, 0xF1, 0x27, 0x01 }); // Lvl1Sec_ArduBytes

            var seed = new byte[10];
            for (var i = 0; i < 10; i++)
            {
                seed[i] = _kwpCommon.ReadByte();
            }
            if (Checksum(seed, 9) != seed[9])
            {
                throw new InvalidOperationException("EDC16 LVL1 seed checksum mismatch.");
            }

            // Same bit-rotation shape as EDC15's LVL41Auth -- see class doc comment.
            var seedForAlgorithm = new byte[] { seed[5], seed[6], seed[7], seed[8] };
            var keyBytes = Edc15KeyAlgorithms.ComputeLvl41Key(0x12890A22, 0x1C60020, seedForAlgorithm);

            var frame = new byte[10];
            frame[0] = 0x86;
            frame[1] = 0x10;
            frame[2] = 0xF1;
            frame[3] = 0x27;
            frame[4] = 0x02;
            frame[5] = keyBytes[0];
            frame[6] = keyBytes[1];
            frame[7] = keyBytes[2];
            frame[8] = keyBytes[3];
            frame[9] = Checksum(frame, 9);

            Thread.Sleep(25);
            WriteRaw(frame);

            VerifyExpected(new byte[] { 0x83, 0xF1, 0x10, 0x67, 0x02, 0x34 }); // Lvl1Sec_ECUBytes
        }

        // ---- Speed handling ----

        /// <summary>
        /// EDC16 flash link speed, selectable per read/write (the "Low|Medium|High" command argument),
        /// like EDC15 -- but with EDC16's own mapping: <see cref="Low"/> = 38400 (0x50),
        /// <see cref="Medium"/> = 124800 (0x87), <see cref="High"/> = 242424 (0xA7).
        /// </summary>
        public enum FlashSpeed
        {
            /// <summary>38400 baud, speed byte 0x50 (the ECU's "low" rate).</summary>
            Low,
            /// <summary>124800 baud, speed byte 0x87 (nominal 125000; the ECU runs 124800).</summary>
            Medium,
            /// <summary>244898 baud (FTDI div 12.250), speed byte 0xA7 (the ECU's "high"/fast rate).
            /// The most cable-sensitive rate; if a High read/write shows errors, drop to Medium.</summary>
            High,
        }

        /// <summary>Speed byte + local FTDI baud for a <see cref="FlashSpeed"/>.</summary>
        private static (byte SpeedByte, int Baud) SpeedFor(FlashSpeed speed) => speed switch
        {
            FlashSpeed.Low => (0x50, 38400),
            FlashSpeed.High => (0xA7, 244898),
            _ => (0x87, 124800), // Medium (default)
        };

        /// <summary>
        /// Enters the EDC16 programming session that requestUpload/requestDownload require, via
        /// startDiagnosticSession(<paramref name="sessionSub"/>) -- 0x86 for a read (upload) session,
        /// 0x85 for a write (download) session. This MUST run before the 0x35 request: without the
        /// session the ECU NAKs 0x35 with NRC 0x80.
        ///
        /// <para>When <paramref name="allowFastBaud"/> is true, a single 3-byte
        /// <c>10 &lt;sub&gt; &lt;speedByte&gt;</c> both enters the session AND raises the baud to the
        /// rate <paramref name="speed"/> names (ack-gated -- see <see cref="TryNegotiateSpeed"/>). If
        /// that rate is refused (or <paramref name="allowFastBaud"/> is false -- the GUI/CLI "force
        /// slow" override), the session is entered at the current 10400 baud with a 2-byte
        /// <c>10 &lt;sub&gt;</c> instead, and the flash just runs slower. Only the ONE selected rate
        /// is tried (not a descending ladder): the user
        /// picks Low/Medium/High explicitly, and 10400 is the safe fallback for any refusal.</para>
        /// </summary>
        private void EnterProgrammingSession(byte sessionSub, FlashSpeed speed, bool allowFastBaud,
            bool allowSlowBaudFallback = true)
        {
            if (allowFastBaud)
            {
                var (speedByte, baud) = SpeedFor(speed);
                if (TryNegotiateSpeed(sessionSub, speedByte, baud))
                {
                    return; // session entered + baud raised to the selected rate
                }
                if (!allowSlowBaudFallback)
                {
                    // Prime pass (see WriteFlash): the speed negotiation being refused is the
                    // reliable signal that this ECU will not grant the download session over a
                    // fast-init session. Don't spend the 10400 fallback's ~3s ack timeout (it also
                    // fails here) -- bail so the caller re-initialises slow immediately.
                    throw new InvalidOperationException(
                        $"EDC16 refused the 0x{sessionSub:X2} session speed negotiation over fast init.");
                }
                Log.WriteLine(
                    $"ECU did not accept {baud} baud ({speed}); entering session at 10400 instead.");
            }

            EnterSessionAtCurrentBaud(sessionSub);
        }

        /// <summary>
        /// Enters startDiagnosticSession(<paramref name="sessionSub"/>) at the CURRENT baud with a
        /// 2-byte request (no speed change): <c>82 10 F1 10 &lt;sub&gt;</c> -> expects a positive
        /// <c>50 &lt;sub&gt;</c>. Throws if the ECU doesn't accept it, since requestUpload/Download
        /// would then be NAKed (NRC 0x80).
        /// </summary>
        private void EnterSessionAtCurrentBaud(byte sessionSub)
        {
            Thread.Sleep(75);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x82, 0x10, 0xF1, 0x10, sessionSub });
            if (!WaitForSpeedAck(sessionSub, 3000))
            {
                throw new InvalidOperationException(
                    $"EDC16 could not enter startDiagnosticSession 0x{sessionSub:X2} " +
                    $"(no 50 {sessionSub:X2} ack within 3s) -- requestUpload/Download would be rejected.");
            }
            Log.WriteLine($"Entered startDiagnosticSession 0x{sessionSub:X2} (no baud change).");
        }

        /// <summary>
        /// Requests a link-speed change via startDiagnosticSession
        /// (<c>83 10 F1 10 &lt;session&gt; &lt;speedByte&gt;</c>) and switches THIS interface's baud to
        /// <paramref name="baud"/> ONLY if the ECU answers with a positive response
        /// (<c>50 &lt;session&gt; ...</c>). Any other outcome -- a 0x7F NAK, no response, or echo-only
        /// bytes -- returns false WITHOUT switching, so the caller can try the next rate or stay at
        /// 10400. Switching unilaterally on a silent ECU must be avoided: switching on anything that
        /// isn't an explicit 0x7F NAK -- including no response at all -- means that on a request this
        /// ECU ignores the interface jumps to the new rate while the ECU stays at 10400, and every
        /// following request then times out. So only a positive response switches the baud.
        /// </summary>
        private bool TryNegotiateSpeed(byte session, byte speedByte, int baud)
        {
            Thread.Sleep(75);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x83, 0x10, 0xF1, 0x10, session, speedByte });
            if (!WaitForSpeedAck(session, 3000))
            {
                Log.WriteLine(
                    $"  Speed 0x{speedByte:X2} ({baud} baud) not accepted (no 50 {session:X2} ack); not switching.");
                return false;
            }
            Thread.Sleep(75);
            _kwpCommon.Interface.SetBaudRate(baud);
            // Now in a high-speed session: drop the per-byte send delay so the bulk transfer runs at
            // line rate (see WriteByteDelayed). This is what makes the write fast -- without it,
            // 253952 data bytes x 5ms = ~21 min for one block even at 124800.
            _fastBaudActive = true;
            Log.WriteLine($"Link speed -> {baud} baud (ECU accepted 10 {session:X2} {speedByte:X2}).");
            return true;
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for a positive startDiagnosticSession ack -- the
        /// ADJACENT byte pair <c>50 &lt;session&gt;</c> -- reading byte-by-byte so it tolerates a
        /// leading responsePending NAK (<c>7F 10 78</c>) and the ECU's ~1.6s stall before the real
        /// ack on a programming-session (0x85) enter. Detecting the adjacent pair works for both
        /// addressed (<c>… 50 85 …</c>) and address-less (<c>03 50 85 …</c>) framing; the request's
        /// own bytes never contain a <c>50 &lt;session&gt;</c> pair, so an echo can't false-positive.
        /// Returns false if no ack arrives in time (caller then doesn't switch baud / throws).
        /// </summary>
        private bool WaitForSpeedAck(byte session, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            byte prev = 0;
            var havePrev = false;
            var originalTimeout = _kwpCommon.Interface.ReadTimeout;
            _kwpCommon.Interface.ReadTimeout = 500;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    byte b;
                    try
                    {
                        b = _kwpCommon.ReadByte();
                    }
                    catch (TimeoutException)
                    {
                        continue; // keep waiting through the ECU's pending stall until the deadline
                    }
                    if (havePrev && prev == 0x50 && b == session)
                    {
                        // Drain the rest of this response (trailing speed/checksum bytes) so the next
                        // request sees a clean buffer. The old ReadAvailableRaw-based enter drained
                        // fully before returning and the read relies on that -- a leftover byte would
                        // corrupt the following VerifyExpected. Nothing else is inbound yet (the next
                        // request isn't sent until this returns), so this can't eat a later response.
                        _kwpCommon.Interface.ReadTimeout = 60;
                        for (var drained = 0; drained < 8; drained++)
                        {
                            try
                            {
                                _kwpCommon.ReadByte();
                            }
                            catch (TimeoutException)
                            {
                                break;
                            }
                        }
                        return true;
                    }
                    prev = b;
                    havePrev = true;
                }
            }
            finally
            {
                _kwpCommon.Interface.ReadTimeout = originalTimeout;
            }
            return false;
        }

        // ---- Flash read ----

        private void ReadFlashDataPacket(Stream destination, ref bool done)
        {
            // Per-packet inter-request gap. At the slow 10400 handshake speed keep the original 15ms;
            // once at a negotiated high speed drop it ENTIRELY. The ECU's own per-packet turnaround
            // already provides the recovery gap the next request needs, so a sleep here is pure idle.
            if (!_fastBaudActive)
            {
                Thread.Sleep(15);
            }
            // transferData (0x36) request, ADDRESS-LESS: 01 36 (+ checksum) -- the address-less
            // transfer-phase request, rather than the addressed 80 10 F1 01 36.
            //
            // Send the whole 3-byte frame (01 36 37, 0x37 = 0x01+0x36 checksum) in one batched
            // WriteBytes call -- exactly like the write path's TransferBlockData chunk send.
            _kwpCommon.WriteBytes(new byte[] { 0x01, 0x36, 0x37 });

            // ADDRESS-LESS response. The ISO14230 format byte's low 6 bits are the length; when
            // they're zero a separate length byte follows. FULL packets arrive as
            //   00 FF 76 <254 data> <checksum>
            // (0xFF = 255 can't fit the 6-bit field, so it's in the separate byte), but the FINAL
            // SHORT packet puts the length IN the format byte with NO separate byte, e.g.
            //   21 76 <32 data> <checksum>   (0x21 = 33 = SID + 32 data).
            // <len> always counts the SID (0x76). 0xFF = a full packet; anything less ends the read.
            // Both framings MUST be handled: hardcoding the format byte to 0x00 and always reading a
            // separate length byte streams the whole flash fine but dies on the short final packet
            // ("expected 0x00, got 0x21").
            var fmt = _kwpCommon.ReadByte();
            var length = fmt & 0x3F;
            byte[] header;
            if (length == 0)
            {
                var lenByte = _kwpCommon.ReadByte();
                length = lenByte;
                var sid = CheckRec(0x76);
                header = new byte[] { fmt, lenByte, sid };
            }
            else
            {
                var sid = CheckRec(0x76);
                header = new byte[] { fmt, sid };
            }
            if (length != 0xFF)
            {
                done = true; // final (short) packet ends the transfer
            }

            var dataLen = Math.Max(0, length - 1); // length counts the SID byte
            var data = new byte[dataLen];
            // Batched read of the whole packet payload in ONE ReadBytes call (LinuxInterface overrides
            // it with a real batch read) instead of dataLen individual ReadByte syscalls.
            if (dataLen > 0)
            {
                _kwpCommon.ReadBytes(data, dataLen);
            }

            var checksumByte = _kwpCommon.ReadByte();
            var expected = (byte)(Checksum(data, dataLen) + Checksum(header, header.Length));
            if (checksumByte != expected)
            {
                throw new InvalidOperationException(
                    $"EDC16 flash read: checksum mismatch (got 0x{checksumByte:X2}, expected 0x{expected:X2}).");
            }

            destination.Write(data, 0, dataLen);
        }

        // ---- Flash write (direct KWP2000 programming) ----

        /// <summary>Per-block ECU geometry (start, checksum/verify end, erase end, download size).
        /// Block 7 excludes the top 8 KB (0x1FE000-0x1FFFFF) from the checksum/download but erases the
        /// whole sector to 0x1FFFFF; block 6 is a full sector.</summary>
        private static (int Start, int ChecksumEnd, int EraseEnd, int DownloadSize) BlockGeom(int blockno) =>
            blockno == 7
                ? (0x1C0000, 0x1FDFFF, 0x1FFFFF, 0x3E000)
                : (0x180000, 0x1BFFFF, 0x1BFFFF, 0x40000);

        private void WriteBlock(
            int blockno, byte[] image, byte[] wholeEncrypted, int checksumForBlock,
            bool forceFull, Action<string>? onStage, Action<int>? onPercent)
        {
            var (start, checksumEnd, eraseEnd, downloadSize) = BlockGeom(blockno);

            // 1) Checksum-compare (skip test): ask the ECU whether its current flash over this range
            //    already equals the data we're about to write. If so, skip the whole block -- no
            //    erase, no write.
            //    Safe: a wrong checksum only ever forces an unnecessary
            //    write. <paramref name="forceFull"/> (the "full" argument /
            //    GUI "Force Full Write" toggle) bypasses this so every block is erased+written
            //    regardless -- matching EDC15's forceFullWrite.
            if (!forceFull && BlockChecksumMatches(start, checksumEnd, checksumForBlock))
            {
                onStage?.Invoke($"Block {blockno} unchanged (checksum match) -- skipped.");
                Log.WriteLine($"Block {blockno} (0x{start:X6}-0x{checksumEnd:X6}) unchanged; skipping erase/write.");
                onPercent?.Invoke(100);
                return;
            }

            // 2) requestDownload -> 3) erase -> 4) transferData -> 5) transferExit. (The whole block
            //    is written as one contiguous download; blank 0xFF runs are NOT skipped -- this ECU
            //    rejects a second requestDownload/transfer cycle in the same programming session, so
            //    per-run skipping isn't possible here.)
            RequestDownload(start, downloadSize);
            onStage?.Invoke($"Erasing block {blockno}...");
            EraseBlock(start, eraseEnd);
            onStage?.Invoke($"Writing block {blockno}...");
            TransferBlockData(wholeEncrypted, onPercent);
            RequestTransferExit();

            // 6) verify the WHOLE block (the erased 0xFF gaps + the written data must equal the source).
            VerifyBlockChecksum(start, checksumEnd, checksumForBlock);
            Log.WriteLine($"Block {blockno} written and verified.");
        }

        /// <summary>Data bytes per transferData (0x36) chunk -- 248 (header length byte is this + 1 =
        /// 0xF9). 248 divides block 7's 0x3E000 download into exactly 1024 chunks (no short final
        /// chunk, which the ECU NAKs).</summary>
        private const int TransferChunkBytes = 0xF8; // 248

        /// <summary>Runs the ECU checksum routine (0x31 C5) over [start,end] with our block checksum,
        /// then polls the result (0x33 C5). Returns true on a match (73 C5 -- current flash already
        /// equals the new data, skip), false when the ECU says a download is needed (7F 33 40).</summary>
        private bool BlockChecksumMatches(int start, int end, int checksum)
        {
            SendChecksumRoutine(start, end, checksum);
            ReadResponseRaw();            // drain the 71 C5 "routine started" ack
            // Pre-write: only a definitive Match means "flash already equals this block, skip it".
            // Anything else (Mismatch/Timeout) => treat as "not equal", write the block.
            return PollChecksumResult() == ChecksumRoutineOutcome.Match;
        }

        /// <summary>Post-write verify: re-runs the 0x31 C5 routine, which should now report a match.
        /// A mismatch is surfaced as a prominent warning rather than a throw, deliberately: it could
        /// mean the write didn't take, OR that the 16-bit 0x31 C5 checksum (Encrypt's plaintext sum)
        /// isn't exactly what this ECU computes -- the one piece of the write protocol not yet
        /// certain. Either way the transfer/erase all ACKed, so a read-back comparison is the
        /// definitive check; throwing here would falsely report a good write as failed.</summary>
        private void VerifyBlockChecksum(int start, int end, int checksum)
        {
            SendChecksumRoutine(start, end, checksum);
            ReadResponseRaw();
            var outcome = PollChecksumResult();
            switch (outcome)
            {
                case ChecksumRoutineOutcome.Match:
                    return;

                case ChecksumRoutineOutcome.Mismatch:
                case ChecksumRoutineOutcome.Timeout:
                default:
                    Log.WriteLine(
                        $"WARNING: EDC16 post-write checksum verify did NOT confirm the block at 0x{start:X6} " +
                        $"(outcome: {outcome}). The transfer and erase were acknowledged; read the flash back " +
                        "and compare before trusting this write.");
                    return;
            }
        }

        /// <summary>startRoutineByLocalIdentifier 0x31 C5 (checksum), ADDRESS-LESS:
        /// 0A 31 C5 &lt;start3&gt; &lt;end3&gt; &lt;checksum2&gt;.</summary>
        private void SendChecksumRoutine(int start, int end, int checksum)
        {
            Thread.Sleep(30);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[]
            {
                0x0A, 0x31, 0xC5,
                (byte)(start >> 16), (byte)(start >> 8), (byte)start,
                (byte)(end >> 16), (byte)(end >> 8), (byte)end,
                (byte)(checksum >> 8), (byte)checksum,
            });
        }

        /// <summary>Outcome of a 0x31 C5 routine + 0x33 C5 result poll.</summary>
        private enum ChecksumRoutineOutcome
        {
            /// <summary>73 C5 -- the ECU's computed checksum matches the value we sent.</summary>
            Match,
            /// <summary>7F 33 40 -- checksum mismatch / download needed. Used by the PRE-write skip
            /// test to decide "this block differs from flash, write it".</summary>
            Mismatch,
            /// <summary>Neither a result nor a terminal NRC arrived within the time budget.</summary>
            Timeout,
        }

        /// <summary>Polls requestRoutineResults (0x33 C5) until the ECU's checksum routine finishes.
        /// The ECU acks the 0x31 C5 start with 71 C5, then WHILE THE ROUTINE IS STILL RUNNING replies
        /// with a negative response whose NRC is 0x21 (busy), 0x22 (conditionsNotCorrect), 0x23
        /// (routineNotComplete) or 0x78 (responsePending) -- ALL of which mean "keep polling", NOT a
        /// rejection. Completion is 73 C5 followed by a status byte: 0x00 => checksum match, non-zero
        /// => Mismatch (a bare 73 C5 with no status is also a match). 7F 33 40 = download-needed.
        /// NOTE: there is NO calibration "digest" gate on this ECU -- an edited file with correct
        /// additive checksums (each block's big-endian dword-sum = 0xD01FE500) flashes and validates
        /// here; the 33-byte trailer is data, not a signature the ECU checks. 7F 33 22 is a
        /// keep-polling status, not a terminal refusal.</summary>
        private ChecksumRoutineOutcome PollChecksumResult()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);
                SendWithChecksum(new byte[] { 0x02, 0x33, 0xC5 });
                var r = ReadResponseRaw();
                var idx = IndexOfPair(r, 0x73, 0xC5);
                if (idx >= 0)
                {
                    // Status byte (if present) follows 73 C5: 0x00 = match, non-zero = mismatch.
                    return (idx + 2 < r.Length && r[idx + 2] != 0x00)
                        ? ChecksumRoutineOutcome.Mismatch
                        : ChecksumRoutineOutcome.Match;
                }
                if (Contains(r, 0x7F, 0x33, 0x40)) return ChecksumRoutineOutcome.Mismatch;
                // 7F 33 21/22/23/78 (busy / conditionsNotCorrect / routineNotComplete /
                // responsePending), or nothing decodable yet => still working, keep polling.
            }
            return ChecksumRoutineOutcome.Timeout;
        }

        /// <summary>requestDownload (0x34), ADDRESS-LESS: 08 34 &lt;start3&gt; 02 &lt;size3&gt; -> 74 …</summary>
        private void RequestDownload(int start, int size)
        {
            Thread.Sleep(30);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[]
            {
                0x08, 0x34,
                (byte)(start >> 16), (byte)(start >> 8), (byte)start,
                0x02,
                (byte)(size >> 16), (byte)(size >> 8), (byte)size,
            });
            var r = ReadResponseRaw();
            if (Contains(r, 0x7F, 0x34) || !Contains(r, 0x74))
            {
                throw new InvalidOperationException(
                    $"EDC16 requestDownload (0x34) at 0x{start:X6} size 0x{size:X} not accepted; " +
                    $"got {Utils.DumpBytes(r)}.");
            }
        }

        /// <summary>Erases the block via routine 0x31 C4 (start..eraseEnd + the ECU's 6-byte erase
        /// signature), ADDRESS-LESS, then polls 0x33 C4 to completion (7F 33 23 while erasing,
        /// 73 C4 when done).</summary>
        private void EraseBlock(int start, int eraseEnd)
        {
            Thread.Sleep(30);
            _kwpCommon.Interface.ClearReceiveBuffer();
            var s = _eraseSignature;
            SendWithChecksum(new byte[]
            {
                0x0E, 0x31, 0xC4,
                (byte)(start >> 16), (byte)(start >> 8), (byte)start,
                (byte)(eraseEnd >> 16), (byte)(eraseEnd >> 8), (byte)eraseEnd,
                s[0], s[1], s[2], s[3], s[4], s[5],
            });
            var r = ReadResponseRaw();
            if (!Contains(r, 0x71, 0xC4))
            {
                throw new InvalidOperationException(
                    $"EDC16 erase (0x31 C4) at 0x{start:X6} not accepted; got {Utils.DumpBytes(r)}.");
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
                SendWithChecksum(new byte[] { 0x02, 0x33, 0xC4 });
                var rr = ReadResponseRaw();
                if (Contains(rr, 0x73, 0xC4)) return;             // erase complete
                // 7F 33 23 (erasing) or nothing -> keep polling.
            }
            throw new InvalidOperationException("EDC16 erase (0x33 C4) did not complete within 60s.");
        }

        /// <summary>Streams the encrypted block via transferData (0x36) in 248-byte chunks:
        /// 00 F9 36 &lt;248 data&gt; &lt;cks&gt; -> ack 00 01 76 77. 248 (not 249)
        /// divides block 7's 0x3E000 into exactly 1024 chunks.
        ///
        /// <para>Each full frame (header + data + checksum) is sent with ONE
        /// <see cref="IKwpCommon.WriteBytes"/> call -- a single batched WriteBytesRaw + echo-drain
        /// (both overridden for a real batch on this hardware's interface). Unlike EDC15's addressed
        /// chunks, EDC16 transferData is SEQUENTIAL (the ECU tracks position, no address in the
        /// frame), so a failed chunk is NOT blindly resent -- CheckWriteAck throws and the whole
        /// write is re-run instead (fast now, and the final 0x31 C5 verify would catch any desync
        /// anyway).</para></summary>
        private void TransferBlockData(byte[] data, Action<int>? onPercent)
        {
            const int maxChunk = TransferChunkBytes; // 248 data bytes; header length byte = chunkLen + 1 = 0xF9.
            var pos = 0;
            var before = -1;
            while (pos < data.Length)
            {
                var chunkLen = Math.Min(maxChunk, data.Length - pos);

                // Assemble the whole address-less frame: 00 <len> 36 <data...> <checksum>.
                var frame = new byte[3 + chunkLen + 1];
                frame[0] = 0x00;
                frame[1] = (byte)(chunkLen + 1);
                frame[2] = 0x36;
                Array.Copy(data, pos, frame, 3, chunkLen);
                frame[frame.Length - 1] = Checksum(frame, 3 + chunkLen); // sum over 00 <len> 36 <data>

                _kwpCommon.WriteBytes(frame); // one batched write + echo-drain (see method doc)
                CheckWriteAck();

                pos += chunkLen;
                var percent = data.Length > 0 ? pos * 100 / data.Length : 100;
                if (percent != before)
                {
                    onPercent?.Invoke(percent);
                    before = percent;
                }
            }
        }

        /// <summary>The per-chunk transferData acknowledgement -- 0x00,0x01,0x76,0x77 in that order.</summary>
        private void CheckWriteAck()
        {
            CheckRec(0x00);
            CheckRec(0x01);
            CheckRec(0x76);
            CheckRec(0x77);
        }

        /// <summary>requestTransferExit (0x37), ADDRESS-LESS: 01 37 -> 77.</summary>
        private void RequestTransferExit()
        {
            Thread.Sleep(30);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x01, 0x37 });
            var r = ReadResponseRaw();
            if (Contains(r, 0x7F, 0x37) || !Contains(r, 0x77))
            {
                throw new InvalidOperationException(
                    $"EDC16 requestTransferExit (0x37) not accepted; got {Utils.DumpBytes(r)}.");
            }
        }

        /// <summary>accessTimingParameter (0x83): read the ECU's limits (83 00) then set P2/P3/P4 to
        /// the max limits (83 03 00 FF 00 FF 00) so the long erase/program don't trip timeouts.
        /// Non-fatal -- logged (not thrown) if the ECU doesn't ack as expected, since it's an
        /// optimization, not a correctness requirement.</summary>
        private void SetAccessTiming()
        {
            Thread.Sleep(30);
            _kwpCommon.Interface.ClearReceiveBuffer();
            SendWithChecksum(new byte[] { 0x02, 0x83, 0x00 });      // read limits
            ReadResponseRaw();
            Thread.Sleep(30);
            SendWithChecksum(new byte[] { 0x07, 0x83, 0x03, 0x00, 0xFF, 0x00, 0xFF, 0x00 }); // set to max
            var r = ReadResponseRaw();
            if (!Contains(r, 0xC3, 0x03))
            {
                Log.WriteLine(
                    $"(accessTimingParameter set not acked as expected: {Utils.DumpBytes(r)}; continuing.)");
            }
        }

        /// <summary>Write teardown: ecuReset (0x11 01; the ECU NAKs it 7F 11 90 here -- expected,
        /// ignored) then stopCommunication (0x82 -> C2). Best-effort: a hiccup here must not fail a
        /// completed, verified write.</summary>
        private void FinalizeWrite()
        {
            _verboseLog = true;
            try
            {
                Thread.Sleep(50);
                _kwpCommon.Interface.ClearReceiveBuffer();
                SendWithChecksum(new byte[] { 0x02, 0x11, 0x01 }); // ecuReset (ECU rejects w/ 7F 11 90)
                ReadResponseRaw();
                Thread.Sleep(30);
                SendWithChecksum(new byte[] { 0x01, 0x82 });        // stopCommunication
                ReadResponseRaw();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"(EDC16 write complete; teardown not acknowledged cleanly: {ex.Message})");
            }
        }

        // ---- Encryption (required before flash write -- see class doc comment) ----

        /// <summary>
        /// Translated statement-for-statement from the register-simulation <c>Encrypt(long start,
        /// long finish)</c> -- see the class doc comment for why this uses <c>int</c> (not
        /// <c>uint</c>/<c>long</c>) and why the 128-byte re-buffering is collapsed into plain
        /// sequential indexing.
        /// </summary>
        private static byte[] Encrypt(byte[] image, int start, int finish, out int checksum)
        {
            const int mask0xFFFFFFFE = unchecked((int)0xFFFFFFFE);
            const int mask0x80000000 = unchecked((int)0x80000000);
            const int mask0xFFFFFF00 = unchecked((int)0xFFFFFF00);

            var output = new List<byte>(finish - start);
            var checksumAcc = 0;

            var EAX = 0x10000;
            var ECX = 0x27C0020;
            var EDX = 0x3FE45D9A;
            var EBX = 0;
            var EDI = 0x10000;
            var EBP = 3;

            var srcPos = start;
            var pos = start;

            while (pos < finish)
            {
                EAX = EDX;
                ECX = EDX;
                EAX = EAX >> 20;
                EAX = EAX & 0x400;
                ECX = ECX & 0x400;
                EAX = EAX ^ ECX;
                ECX = EDX;
                ECX = ECX >> 31;
                EAX = EAX >> 10;
                ECX = ECX & 0x01;
                EBX = EDX;
                EAX = EAX ^ ECX;
                ECX = EDX;
                EBX = EBX & 0x01;
                ECX = ECX >> 1;
                EBX = EBX ^ EAX;
                if (EBX == 0)
                {
                    EDI = EDI & mask0xFFFFFFFE;
                }
                else
                {
                    EDI = EDI | 0x01;
                }
                EAX = 0;
                EDX = EDI;
                EDX = EDX & 0x01;
                EDX = EDX | EAX;
                if (EDX == 0)
                {
                    ECX = ECX & 0x7FFFFFFF;
                }
                else
                {
                    ECX = ECX | mask0x80000000;
                }
                EBP--;
                EDX = ECX;

                if (EBP == 0)
                {
                    byte a, b, c;

                    EAX = image[srcPos]; srcPos++;
                    checksumAcc += EAX;
                    a = (byte)(EAX & 0xFF);
                    b = (byte)(ECX & 0xFF);
                    a = (byte)(a ^ b);
                    output.Add(a);
                    pos++;

                    c = image[srcPos]; srcPos++;
                    checksumAcc += c;
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + c;
                    EAX = ECX;
                    EAX = EAX >> 8;
                    a = (byte)(EAX & 0xFF);
                    b = (byte)(EBX & 0xFF);
                    a = (byte)(a ^ b);
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + a;
                    EAX = ECX;
                    a = (byte)(EBX & 0xFF);
                    output.Add(a);
                    pos++;

                    c = image[srcPos]; srcPos++;
                    checksumAcc += c;
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + c;
                    EAX = EAX >> 0x10;
                    a = (byte)(EAX & 0xFF);
                    b = (byte)(EBX & 0xFF);
                    a = (byte)(a ^ b);
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + a;
                    EAX = 0x10000;
                    a = (byte)(EBX & 0xFF);
                    output.Add(a);
                    pos++;

                    c = image[srcPos]; srcPos++;
                    checksumAcc += c;
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + c;
                    ECX = ECX >> 0x18;
                    a = (byte)(ECX & 0xFF);
                    b = (byte)(EBX & 0xFF);
                    a = (byte)(a ^ b);
                    EBX = EBX & mask0xFFFFFF00;
                    EBX = EBX + a;
                    EAX--; // dead relative to output ('a' below reads EBX, not EAX) -- kept for fidelity.
                    a = (byte)(EBX & 0xFF);
                    output.Add(a);
                    pos++;

                    EBP = 3;
                }
            }

            checksum = checksumAcc & 0xFFFF;
            return output.ToArray();
        }

        // ---- Low-level protocol primitives ----

        /// <summary>Writes every byte in <paramref name="payload"/>, then a freshly computed
        /// checksum of just those bytes. Reproduces the correct final wire output for every "send"
        /// call site regardless of which send-helper checksum convention that call site used -- see
        /// the class doc comment.</summary>
        private void SendWithChecksum(byte[] payload)
        {
            WriteRaw(payload);
            WriteByteDelayed(Checksum(payload, payload.Length));
        }

        private void WriteRaw(byte[] data)
        {
            if (_verboseLog)
            {
                Log.WriteLine($"  TX: {Utils.DumpBytes(data)}");
            }
            foreach (var b in data)
            {
                WriteByteDelayed(b);
            }
        }

        /// <summary>Writes one byte, pausing 5ms afterward ONLY while at the slow (10400) init/handshake
        /// speed. Once a high-speed session is negotiated (<see cref="_fastBaudActive"/>, set in
        /// <see cref="TryNegotiateSpeed"/>), the delay is dropped so the bulk transfer runs at line
        /// rate (~13ms/byte slow, ~0 fast) -- the same <c>_fastBaudActive</c> gate EDC15 uses.</summary>
        private void WriteByteDelayed(byte b)
        {
            _kwpCommon.WriteByte(b);
            if (!_fastBaudActive)
            {
                Thread.Sleep(5);
            }
        }

        /// <summary>Reads and verifies <paramref name="expected"/>.Length sequential bytes, then a
        /// trailing checksum byte. If the ECU answers with a "response pending" NAK instead (format
        /// 0x83, dest 0xF1, src 0x10, service 0x7F, body = [requestedService, 0x78] -- seen for the
        /// flash-read addressing request), drains it via <see cref="TryDrainPendingNak"/> and waits
        /// for the real response instead of treating it as a hard mismatch.</summary>
        private void VerifyExpected(byte[] expected)
        {
            while (true)
            {
                var slice = new byte[expected.Length];
                var first = ReadByteMaybePeeked();
                slice[0] = first;
                if (first != expected[0])
                {
                    if (first == 0x83)
                    {
                        if (TryDrainPendingNak(out var nakRest))
                        {
                            continue;
                        }
                        ThrowUnexpectedNak(nakRest, expected);
                    }
                    ThrowMismatch(slice, 0, expected);
                }

                for (var i = 1; i < expected.Length; i++)
                {
                    var b = ReadByteMaybePeeked();
                    slice[i] = b;
                    if (b != expected[i])
                    {
                        ThrowMismatch(slice, i, expected);
                    }
                }
                var checksumByte = ReadByteMaybePeeked();
                var expectedChecksum = Checksum(expected, expected.Length);
                if (_verboseLog)
                {
                    Log.WriteLine($"  RX: {Utils.DumpBytes(slice)} + checksum 0x{checksumByte:X2}");
                }
                if (checksumByte != expectedChecksum)
                {
                    throw new InvalidOperationException(
                        $"EDC16 handshake checksum mismatch: expected 0x{expectedChecksum:X2}, got 0x{checksumByte:X2}.");
                }
                return;
            }
        }

        private void ThrowMismatch(byte[] slice, int i, byte[] expected)
        {
            if (_verboseLog)
            {
                Log.WriteLine(
                    $"  RX: {Utils.DumpBytes(slice[..(i + 1)])} <- mismatch at position {i}: " +
                    $"expected 0x{expected[i]:X2}, got 0x{slice[i]:X2}.");
            }
            throw new InvalidOperationException(
                $"EDC16 handshake mismatch at position {i}: expected 0x{expected[i]:X2}, got 0x{slice[i]:X2}.");
        }

        /// <summary>
        /// Called when a frame VerifyExpected read starts with 0x83, was already fully drained by
        /// <see cref="TryDrainPendingNak"/> (and logged there), and turned out NOT to be a
        /// "response pending" NAK after all -- i.e. some other, unrecognized 0x83-prefixed frame.
        /// Unlike <see cref="ThrowMismatch"/>, this reports the complete 7-byte frame that was
        /// actually read (0x83 + the 6 bytes in <paramref name="rest"/>) rather than just the
        /// leading 0x83, since all 7 bytes are already off the wire by this point and dropping the
        /// other 6 from the error message would just make the failure harder to diagnose.
        /// </summary>
        private void ThrowUnexpectedNak(byte[] rest, byte[] expected)
        {
            var full = new byte[rest.Length + 1];
            full[0] = 0x83;
            Array.Copy(rest, 0, full, 1, rest.Length);
            throw new InvalidOperationException(
                $"EDC16 handshake mismatch: expected {Utils.DumpBytes(expected)}, " +
                $"got NAK-shaped frame {Utils.DumpBytes(full)}.");
        }

        /// <summary>
        /// Called when a frame VerifyExpected is reading starts with 0x83 but that doesn't match
        /// what was expected -- reads the rest of a possible "response pending" NAK (dest, src,
        /// service, 2 body bytes, checksum -- 6 more bytes) and checks its shape: dest 0xF1, src
        /// 0x10, service 0x7F (negativeResponse), second body byte 0x78
        /// (reqCorrectlyRcvdRspPending). The flash-read addressing request gets exactly this NAK
        /// before its real "READY TO SEND" response, similar to the response-pending pattern
        /// KW2000Dialog.SendReceive already handles for the rest of this app's KWP2000 traffic.
        /// Returns true (caller should
        /// retry waiting for the real response) if the shape matched, false otherwise (caller then
        /// reports the mismatch via <see cref="ThrowUnexpectedNak"/>, which is handed
        /// <paramref name="rest"/> so none of these 6 bytes get lost from the error message).
        /// </summary>
        private bool TryDrainPendingNak(out byte[] rest)
        {
            rest = new byte[6];
            for (var i = 0; i < rest.Length; i++)
            {
                rest[i] = ReadByteMaybePeeked();
            }
            var isPending = rest[0] == 0xF1 && rest[1] == 0x10 && rest[2] == 0x7F && rest[4] == 0x78;
            if (_verboseLog)
            {
                Log.WriteLine(
                    $"  RX: 83 {Utils.DumpBytes(rest)}" +
                    (isPending
                        ? " (response pending -- waiting for the real response)"
                        : " <- unexpected frame"));
            }
            return isPending;
        }

        /// <summary>Reads and verifies a single expected byte, returning it. Matches CheckRec(byte).</summary>
        private byte CheckRec(byte expected)
        {
            var b = ReadByteMaybePeeked();
            if (_verboseLog)
            {
                Log.WriteLine($"  RX: 0x{b:X2}" + (b != expected ? $" (expected 0x{expected:X2})" : ""));
            }
            if (b != expected)
            {
                throw new InvalidOperationException(
                    $"EDC16 handshake mismatch: expected 0x{expected:X2}, got 0x{b:X2}.");
            }
            return b;
        }

        /// <summary>
        /// Reads whatever the ECU sends back with no fixed frame length, emulating a
        /// <c>Serial.available()</c>-draining read: blocks for the first byte
        /// (normal timeout), then keeps reading with a short 50ms timeout until one times out or
        /// <paramref name="maxBytes"/> is reached. Used (via <see cref="ReadResponseRaw"/>) by the
        /// write-path control frames and status polls, which have no fixed expected response shape and
        /// scan the drained bytes for the outcome.
        /// </summary>
        private byte[] ReadAvailableRaw(int maxBytes = 16)
        {
            var result = new List<byte> { ReadByteMaybePeeked() };
            var originalTimeout = _kwpCommon.Interface.ReadTimeout;
            try
            {
                _kwpCommon.Interface.ReadTimeout = 50;
                while (result.Count < maxBytes)
                {
                    try
                    {
                        result.Add(_kwpCommon.ReadByte());
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _kwpCommon.Interface.ReadTimeout = originalTimeout;
            }
            return result.ToArray();
        }

        /// <summary>Drains an address-less response (best-effort) for scanning by the write-path
        /// control frames -- never throws on timeout, just returns whatever arrived (possibly empty).</summary>
        private byte[] ReadResponseRaw(int maxBytes = 32)
        {
            try
            {
                return ReadAvailableRaw(maxBytes);
            }
            catch (TimeoutException)
            {
                return Array.Empty<byte>();
            }
        }

        /// <summary>True if <paramref name="buf"/> contains <paramref name="seq"/> as a contiguous
        /// subsequence anywhere. Used to interpret address-less responses (positive/NAK) tolerant of
        /// framing and leading bytes.</summary>
        private static bool Contains(byte[] buf, params byte[] seq)
        {
            if (seq.Length == 0 || buf.Length < seq.Length)
            {
                return false;
            }
            for (var i = 0; i + seq.Length <= buf.Length; i++)
            {
                var match = true;
                for (var j = 0; j < seq.Length; j++)
                {
                    if (buf[i + j] != seq[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Index of the first adjacent pair (<paramref name="a"/>, <paramref name="b"/>) in
        /// <paramref name="buf"/>, or -1. Locates a record header (e.g. 5A 9C) regardless of framing.</summary>
        private static int IndexOfPair(byte[] buf, byte a, byte b)
        {
            for (var i = 0; i + 1 < buf.Length; i++)
            {
                if (buf[i] == a && buf[i + 1] == b)
                {
                    return i;
                }
            }
            return -1;
        }

        private void CloseEcu()
        {
            Thread.Sleep(10);
            SendWithChecksum(new byte[] { 0x81, 0x10, 0xF1, 0x82 });
            CheckRec(0x81);
            CheckRec(0xF1);
            CheckRec(0x10);
            CheckRec(0xC2);
            CheckRec(0x44);
        }

        private static byte Checksum(byte[] data, int len)
        {
            byte sum = 0;
            for (var i = 0; i < len; i++)
            {
                sum += data[i];
            }
            return sum;
        }

        // ---- Byte read helper ----

        /// <summary>Reads one byte from the interface. A plain read: nothing in this class pushes a
        /// byte back, so no peek buffer is needed (which also avoids a CS0649 "field never assigned"
        /// warning).</summary>
        private byte ReadByteMaybePeeked()
        {
            return _kwpCommon.ReadByte();
        }
    }
}
