using BitFab.KW1281Test.Kwp2000;
using BitFab.KW1281Test.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace BitFab.KW1281Test.EDC15
{
    public class Edc15VM
    {
        public byte[] ReadWriteEeprom(
            string? filename,
            List<KeyValuePair<ushort, byte>>? addressValuePairs = null,
            Action<byte[]>? onPostWriteReadback = null)
        {
            addressValuePairs ??= [];

            var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);

            _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x89]);

            _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x85]);

            const byte accMod = 0x41;
            var resp = kwp2000.SendReceive(DiagnosticService.securityAccess, [accMod]);

            // ECU normally doesn't require seed/key authentication the first time it wakes up in
            // KWP2000 mode so sending an empty key is sufficient.
            var buf = new List<byte> { accMod + 1 };

            if (!resp.Body.SequenceEqual(new byte[] { accMod, 0x00, 0x00 }))
            {
                // Normally we'll only get here if we wake up the ECU and it's already in KWP2000 mode,
                // which can happen if a previous download attempt did not complete. In that case we
                // need to calculate and send back a real key.
                var seedBuf = resp.Body.Skip(1).Take(4).ToArray();
                var keyBuf = Edc15KeyAlgorithms.ComputeLvl41Key(0x508DA647, 0x3800000, seedBuf);

                buf.AddRange(keyBuf);
            }
            _ = kwp2000.SendReceive(DiagnosticService.securityAccess, buf.ToArray());

            var loader = Edc15VM.GetEepromLoader();
            var len = loader.Length;

            // Ask the ECU to accept our loader and store it in RAM
            _ = kwp2000.SendReceive(DiagnosticService.requestDownload, [
                0x40, 0xE0, 0x00, // Load address 0x40E000
                0x00, // Not compressed, not encrypted
                (byte)(len >> 16), (byte)(len >> 8), (byte)(len & 0xFF) // Length
                ],
            excludeAddresses: true);

            // Break the loader into blocks and send each one
            var maxBlockLen = resp.Body[0];
            var s = new MemoryStream(loader);
            while (true)
            {
                Thread.Sleep(5);

                var blockBytes = new byte[maxBlockLen];
                var readCount = s.Read(blockBytes, 0, maxBlockLen - 1);
                if (readCount == 0)
                {
                    break;
                }

                SendTransferDataWithRetry(kwp2000, blockBytes.Take(readCount).ToArray());
            }

            // Ask the ECU to execute our loader
            kwp2000.SendMessage(
                DiagnosticService.startRoutineByLocalIdentifier, [0x02],
                excludeAddresses: true);
            _ = kwp2000.ReceiveMessage();

            // Initial 0xA6 "Dump EEPROM": on a pure read this IS the operation; on a write it is NOT
            // needed anymore. Historically a write depended on the dump because the loader set up its
            // EEPROM-bus GPIO directions (DP2.8 data, DP2.9 clock) only inside the dump routine E1C6,
            // so a write with no dump in front of it never drove the clock line and hung. The loader
            // now initialises those directions itself at the start of every write command (the EInit
            // routine added to CmdA7/CmdA8/CmdA9/CmdAA in Loader-EEPROM.a66 /
            // Loader-EEPROM.bin, mirroring E1C6's exact preamble), so a write drives the bus on its
            // own. We therefore dump ONLY on a read, or on a write where the caller explicitly asked
            // for a pre-write backup (a non-null filename); a plain write (clone, Write Full, immo
            // change) does no dump at all.
            var isWrite = addressValuePairs.Count > 0;
            var eeprom = Array.Empty<byte>();
            if (!isWrite || !string.IsNullOrEmpty(filename))
            {
                eeprom = DumpEepromInSession(kwp2000);
                var saveTo = filename ?? "EDC15_EEPROM.bin";
                File.WriteAllBytes(saveTo, eeprom);
                Log.WriteLine($"Saved EEPROM to {saveTo}");
            }

            // Now write any supplied values, via the batched 0xA9/0xAA "write N bytes" commands
            // (WriteEepromBatched, grouped by EEPROM page and chunked to <=255 pairs). This is the
            // only write path now -- the stock loader's one-byte-per-command 0xA7/0xA8 path (and the
            // stock Loader.bin itself) were removed once the batched loader became the single EEPROM
            // loader for both reads and writes. VerboseLog=false routes SendMessage/ReceiveMessage's
            // routine per-field trace lines to the log file instead of the screen (see VerboseLog's
            // own doc comment) so a large write doesn't cause the spinner/log choppiness the flash
            // path had before its own equivalent fix.
            kwp2000.VerboseLog = false;
            if (addressValuePairs.Count > 0)
            {
                WriteEepromBatched(kwp2000, addressValuePairs);
            }

            // In-session verification read-back: if the caller asked for it and we actually wrote
            // something, dump the EEPROM one more time -- while the loader is still resident and
            // BEFORE the 0xA2 reboot below. This is the whole point of doing it here: the main ECU
            // firmware hasn't run yet, so nothing has revalidated the immobilizer records and
            // rewritten their status/checksum bytes (the "self-correcting" offsets a post-reboot
            // read-back sees). What comes back is exactly what we just wrote, so the caller can do a
            // plain byte-for-byte compare with no special-offset allowance. We only capture the image
            // and hand it back -- the caller runs the actual comparison/notification later (it can
            // happen after the reboot; only the read itself has to be pre-reboot).
            if (onPostWriteReadback != null && addressValuePairs.Count > 0)
            {
                Log.WriteLine("Reading EEPROM back for verification (in loader session, before reboot)...");
                eeprom = DumpEepromInSession(kwp2000);
                onPostWriteReadback(eeprom);
            }

            // Custom loader command to reboot the ECU to return it to normal operation.
            kwp2000.SendMessage(
                    (DiagnosticService)0xA2, [],
                excludeAddresses: true);
            _ = kwp2000.ReceiveMessage();

            var b = _kwpCommon.Interface.ReadByte();
            if (b == 0x55)
            {
                Log.WriteLine($"Reboot successful!");
            }

            return eeprom;
        }

        /// <summary>
        /// The K-line can drop a byte mid-transfer during the (slow, multi-chunk) loader upload.
        /// Retry the individual chunk rather than aborting the whole read/write. Used for every
        /// loader upload in this class (EEPROM loader and the sector-erase loader). Incorporates
        /// gmenounos/kw1281test#185.
        /// </summary>
        private static void SendTransferDataWithRetry(
            KW2000Dialog kwp2000, byte[] data, int maxAttempts = 3)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _ = kwp2000.SendReceive(
                        DiagnosticService.transferData, data, excludeAddresses: true);
                    return;
                }
                catch (TimeoutException) when (attempt < maxAttempts)
                {
                    Log.WriteLine(
                        $"Timed out sending loader block (attempt {attempt}/{maxAttempts})." +
                        " Retrying...");
                    Thread.Sleep(200);
                }
            }
        }

        /// <summary>Issues the loader's 0xA6 "Dump EEPROM" command once, on an already-running loader
        /// session, and returns all 512 bytes it clocks back. Factored out of
        /// <see cref="ReadWriteEeprom"/> so the same wire dance (first ACK, 512 raw bytes, trailing
        /// ACK -- see the loader's CmdA6 -> E1C6 -> Cmd36x path) can be used both for the pre-write
        /// dump and for the optional post-write verification read-back, without duplicating it.
        /// Assumes the loader is already uploaded and started (as it is by the time
        /// <see cref="ReadWriteEeprom"/> calls this).</summary>
        private byte[] DumpEepromInSession(KW2000Dialog kwp2000)
        {
            kwp2000.SendMessage((DiagnosticService)0xA6, [], excludeAddresses: true);
            var resp = kwp2000.ReceiveMessage();
            if (!resp.IsPositiveResponse(DiagnosticService.transferData))
            {
                throw new InvalidOperationException("Dump EEPROM failed.");
            }

            var eeprom = new byte[512];
            for (var i = 0; i < 512; i++)
            {
                eeprom[i] = _kwpCommon.Interface.ReadByte();
            }

            _ = kwp2000.ReceiveMessage(); // trailing ACK the loader sends after the 512-byte dump
            return eeprom;
        }

        /// <summary>
        /// Builds and sends one message in this loader's OWN native wire format -- <c>[length]
        /// [service][data...][checksum]</c>, written directly via <c>_kwpCommon.WriteBytes</c>,
        /// completely bypassing <see cref="KW2000Dialog"/>/<see cref="Kwp2000Message"/>.
        ///
        /// <para>Superseded <c>SendCustomCommandAndAwaitAck</c> (this method's previous approach,
        /// which went through <c>KW2000Dialog.SendMessage</c>) for the same reason documented on
        /// <see cref="FlashProgramChunkSize"/>: once this loader is running, its own receive
        /// routine (E128 in docs/Loader-sector-erase.a66) only ever understands a single length
        /// byte, never the 2-byte long-form header <c>Kwp2000Message.CalcFormatByte</c> silently
        /// switches to above 63 bytes. Building the wire bytes directly here removes THAT specific
        /// ceiling, but not every ceiling. Callers
        /// own picking an actually-safe size for their own use of this method -- this method itself
        /// only refuses what genuinely cannot fit on the wire.
        /// Matches <see cref="Edc15FlashVM"/>'s own precedent for talking to a loader this
        /// way (<c>SendFlashWriteChunk</c>/<c>ReadFlashDataPacket</c>, which never go through
        /// KW2000Dialog either, for the identical reason -- they just talk to a different loader
        /// binary with a different address-offset convention, so their code isn't reused directly
        /// here).</para>
        /// </summary>
        private void SendLoaderMessageRaw(byte service, byte[] data)
        {
            if (data.Length > 253)
            {
                throw new ArgumentException(
                    $"data.Length must be <= 253 (length byte would overflow) -- got {data.Length}.",
                    nameof(data));
            }

            var buf = new byte[2 + data.Length + 1];
            buf[0] = (byte)(1 + data.Length);
            buf[1] = service;
            Array.Copy(data, 0, buf, 2, data.Length);

            byte checksum = 0;
            unchecked
            {
                for (var i = 0; i < buf.Length - 1; i++)
                {
                    checksum += buf[i];
                }
            }
            buf[^1] = checksum;

            _kwpCommon.WriteBytes(buf);
        }

        /// <summary>
        /// Reads one response in this loader's own native wire format directly off the wire,
        /// bypassing <see cref="KW2000Dialog.ReceiveMessage"/> entirely -- not just for symmetry
        /// with <see cref="SendLoaderMessageRaw"/>, but to sidestep a real landmine that method
        /// would hit for large/variable-length reads: <c>KW2000Dialog.ReceiveMessage</c>'s
        /// short-form parsing happens to compute the same result as this loader's actual framing
        /// (length byte = 1 + dataLength) for any length byte that ISN'T an exact multiple of 64 --
        /// but if it ever IS (0/64/128/192), ReceiveMessage misreads that as "long form, read one
        /// more length byte", something this loader never does. Fixed-size ACKs (length byte
        /// always 1) never come close to that, which is why the erase/program ACKs below are fine
        /// either way -- but <see cref="ReadFlashRangeInSession"/>'s variable-length read responses
        /// realistically could hit it depending on chunk-size math, so this avoids the whole
        /// category of bug by never assuming anything beyond "first byte is the true length,
        /// always" -- exactly what this loader's own E15E send routine actually implements.
        /// Verifies the checksum itself (E15E's own algorithm: length + service + all data bytes,
        /// truncated to 8 bits) rather than trusting the wire -- this loader has no other integrity
        /// check, and silently accepting a corrupted response is worse than throwing.
        /// </summary>
        private (byte Service, byte[] Data) ReceiveLoaderResponseRaw()
        {
            var length = _kwpCommon.ReadByte();
            var service = _kwpCommon.ReadByte();
            var dataLen = length - 1;
            var data = new byte[Math.Max(0, dataLen)];
            if (dataLen > 0)
            {
                _kwpCommon.ReadBytes(data, dataLen);
            }
            var checksumByte = _kwpCommon.ReadByte();

            byte expected = length;
            unchecked
            {
                expected += service;
                foreach (var b in data)
                {
                    expected += b;
                }
            }
            if (checksumByte != expected)
            {
                throw new InvalidOperationException(
                    $"Loader response checksum mismatch (got 0x{checksumByte:X2}, " +
                    $"expected 0x{expected:X2}).");
            }

            return (service, data);
        }

        /// <summary>
        /// Sends one loader command via <see cref="SendLoaderMessageRaw"/> and waits for its
        /// response via <see cref="ReceiveLoaderResponseRaw"/>, resending the identical request on
        /// timeout/checksum failure (clearing the receive buffer first, same as
        /// <see cref="KW2000Dialog.SendReceive"/>'s own resend-on-timeout and
        /// <see cref="Edc15FlashVM.SendFlashWriteChunk"/>'s identical retry loop) -- safe to resend
        /// for every current caller (CmdB0 erase, Cmd36 program, Cmd23 read all either genuinely
        /// idempotent or read-only). Throws if <paramref name="expectedResponseService"/> doesn't
        /// match after all attempts are exhausted -- see call sites for why the expected byte
        /// varies (0x76 for anything acked via the shared SendACK routine, 0x36 bare for Cmd23's
        /// own directly-built response).
        /// </summary>
        private (byte Service, byte[] Data) SendLoaderCommandRaw(
            byte service, byte[] data, byte expectedResponseService, string description,
            int maxAttempts = 10)
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                SendLoaderMessageRaw(service, data);
                try
                {
                    var response = ReceiveLoaderResponseRaw();
                    if (response.Service != expectedResponseService)
                    {
                        throw new InvalidOperationException(
                            $"{description} failed: unexpected response service " +
                            $"0x{response.Service:X2} (expected 0x{expectedResponseService:X2}).");
                    }
                    return response;
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
                {
                    Log.WriteFileOnly(
                        $"  {description} attempt {attempt}/{maxAttempts} failed ({ex.Message}); " +
                        "clearing the receive buffer and resending...\n");
                    _kwpCommon.Interface.ClearReceiveBuffer();
                }
            }

            throw new TimeoutException($"No valid response to {description} after {maxAttempts} attempts.");
        }

        /// <summary>
        /// Erases one flash sector (CmdB0) then programs <paramref name="bytes"/> into it (Cmd36,
        /// <see cref="FlashProgramChunkSize"/>-byte chunks by default, skipping all-0xFF chunks since
        /// the sector was just erased). If <paramref name="verify"/> is true, does the full in-session
        /// read-back verify afterward. Assumes the sector loader is ALREADY running (see
        /// <see cref="ConnectAndStartSectorLoader"/>); the caller owns the connect and the
        /// <see cref="TryCloseLoader"/> teardown. Extracted from <see cref="EraseAndRestoreSector"/> so
        /// the per-sector writer can reuse the exact proven erase+program path. <paramref name="bytes"/>
        /// must be non-empty and even-length.
        /// </summary>
        private void EraseAndProgramSector(uint cpuAddress, byte[] bytes, bool verify)
        {
            if (bytes.Length == 0 || bytes.Length % 2 != 0)
            {
                throw new ArgumentException(
                    "bytes must be a non-empty, even-length array -- Cmd36/E1C4 programs whole " +
                    $"16-bit words -- got {bytes.Length} bytes.", nameof(bytes));
            }

            // 0xB0: erase the sector at cpuAddress -- see docs/Loader-sector-erase.a66's CmdB0.
            // Validated against 0x76 (not 0xF0/0xB0+0x40) because CmdB0's handler is `CALL SendACK;
            // CALL SectorErase; JMPR CC_UC,Cmd36x` -- it calls the SAME shared SendACK routine
            // every custom command in this loader family uses (written once for Cmd36 "Program
            // flash", reused unmodified), which always tags its ACK 0x76 regardless of which
            // command triggered it.
            Log.WriteLine($"Erasing sector at 0x{cpuAddress:X}...");
            SendLoaderCommandRaw(0xB0, [
                (byte)((cpuAddress >> 16) & 0xFF),
                (byte)((cpuAddress >> 8) & 0xFF),
                (byte)(cpuAddress & 0xFF),
            ], expectedResponseService: 0x76, $"Erase flash sector at 0x{cpuAddress:X}");

            // That first ACK does NOT mean the erase is actually finished -- and, critically, it is
            // NOT the only ACK this command sends. CmdB0's handler above ends with
            // `JMPR CC_UC,Cmd36x`, and Cmd36x ITSELF starts with another `CALL SendAck` before it
            // jumps back to the main receive loop. So CmdB0 (like CmdA5/CmdA6/CmdA7/CmdA8, which all
            // share this exact SendACK-then-operation-then-Cmd36x shape) sends TWO 0x76 ACKs per
            // invocation: one immediately (before SectorErase even starts), and a second one only
            // after SectorErase's blocking JEDEC poll loop actually finishes.
            //
            // We do NOT sleep a fixed interval before reading that second ACK. SectorErase in
            // docs/Loader-sector-erase.a66 polls the flash ITSELF (its SE_Poll loop reads the erase
            // target back until it reads all-0xFF -- erase complete) before it falls through to
            // Cmd36x's SendAck, so the second ACK physically cannot arrive until the erase is done.
            // That makes the blocking read below the completion signal on its own: it returns as
            // soon as the erase finishes (typically well under a second for one Am29F400BT sector),
            // instead of always paying a flat wait. This is the main per-sector speedup -- the old
            // code did `Thread.Sleep(4000)` here (cargo-culted from Edc15FlashVM.WriteFlash's
            // whole-chip CmdA5 erase, which needs a single ~4s wait), which cost ~4s PER differing
            // sector and was almost entirely dead time. All we need is a read timeout wide enough to
            // cover a worst-case single-sector erase (Am29F400BT datasheet: ~15s absolute max, ~0.7s
            // typical); 20s matches the whole-chip recovery path's own erase-wait budget. Restored in
            // a finally so a slow/failed erase can't leave the interface stuck on the widened timeout.
            var originalReadTimeout = _kwpCommon.Interface.ReadTimeout;
            byte secondEraseAckService;
            try
            {
                _kwpCommon.Interface.ReadTimeout = 20_000;
                (secondEraseAckService, _) = ReceiveLoaderResponseRaw();
            }
            finally
            {
                _kwpCommon.Interface.ReadTimeout = originalReadTimeout;
            }
            if (secondEraseAckService != 0x76)
            {
                throw new InvalidOperationException(
                    $"Erase flash sector at 0x{cpuAddress:X} (post-erase ACK) failed: " +
                    $"unexpected response service 0x{secondEraseAckService:X2}.");
            }

            // 0x36 ("Program flash"/Cmd36/E1C4) -- happens to share transferData's own service
            // byte (0x36), since the loader's dispatch simply reinterprets an incoming
            // transferData-shaped message as its own command once it's running (same as every
            // other Cmd* here). Immediately restores bytes -- see this method's own doc
            // comment for why that's not optional.
            //
            // <b>Chunked, NOT sent as one message covering the whole sector</b> -- see
            // FlashProgramChunkSize's own doc comment for the full chunk-size history (240 and 249
            // were both tried and rejected by real hardware; 58 is the only value actually proven
            // safe, and independent evidence from MPPS's own captured traffic on this ECU family
            // shows it also chunks in roughly this same range).
            //
            // <b>Skips chunks that are entirely 0xFF</b> -- same reasoning as
            // Edc15FlashVM.WriteFlashBlock's own skipBlankChunks: NOR flash programming can only
            // clear bits (1->0), never set them, and this sector was JUST erased (all-0xFF)
            // immediately above, so writing 0xFF anywhere in it is always a verified no-op
            // regardless of chip-specific behavior -- not something specific to boot mode, per the
            // boot-mode flasher's own use of the identical optimization (Reference/Logs/Write.log).
            // A calibration sector's padding/unused regions are often long 0xFF runs, so this can
            // skip a meaningful fraction of the round trips below.
            Log.WriteLine(
                $"Reprogramming original {bytes.Length} byte(s) at 0x{cpuAddress:X} " +
                $"({FlashProgramChunkSize}-byte chunks)...");
            var offset = 0;
            var skippedBlankBytes = 0;
            while (offset < bytes.Length)
            {
                var chunkLen = Math.Min(FlashProgramChunkSize, bytes.Length - offset);
                var chunkAddress = cpuAddress + (uint)offset;

                var isBlank = true;
                for (var i = offset; i < offset + chunkLen; i++)
                {
                    if (bytes[i] != 0xFF)
                    {
                        isBlank = false;
                        break;
                    }
                }

                if (isBlank)
                {
                    skippedBlankBytes += chunkLen;
                }
                else
                {
                    var programData = new byte[3 + chunkLen];
                    programData[0] = (byte)((chunkAddress >> 16) & 0xFF);
                    programData[1] = (byte)((chunkAddress >> 8) & 0xFF);
                    programData[2] = (byte)(chunkAddress & 0xFF);
                    Array.Copy(bytes, offset, programData, 3, chunkLen);

                    SendLoaderCommandRaw(
                        0x36, programData, expectedResponseService: 0x76,
                        $"Reprogram flash at 0x{chunkAddress:X} ({chunkLen} byte(s))");
                }

                offset += chunkLen;
            }
            if (skippedBlankBytes > 0)
            {
                Log.WriteLine($"Skipped {skippedBlankBytes} all-0xFF byte(s) (already erased).");
            }

            // Full in-session verify, restored: Cmd23/E23E's actual bug (wrong address-high-byte
            // convention -- see ReadFlashRangeInSession's own doc comment for the full story of how
            // that was found and confirmed on real hardware) is now fixed, so this is trusted as
            // authoritative again, throwing on a real (retry-confirmed) mismatch. One retry per
            // mismatch is kept as cheap insurance against a genuine transient K-line glitch -- not
            // because the address bug is expected to still be lurking, but because that's normal
            // hygiene for any real-hardware read, same as everywhere else in this file.
            if (verify)
            {
                Log.WriteLine("Verifying (in-session, via the loader's own read command)...");
                var readBack = ReadFlashRangeInSession(cpuAddress, bytes.Length);
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (readBack[i] != bytes[i])
                    {
                        var mismatchAddress = cpuAddress + (uint)i;
                        var retryByte = ReadFlashRangeInSession(mismatchAddress, 1)[0];
                        if (retryByte == bytes[i])
                        {
                            Log.WriteLine(
                                $"Transient verify mismatch at 0x{mismatchAddress:X} (wrote " +
                                $"0x{bytes[i]:X2}, first read back 0x{readBack[i]:X2}) did not " +
                                $"reproduce on retry (0x{retryByte:X2}) -- continuing.");
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"Verify failed at 0x{mismatchAddress:X}: wrote 0x{bytes[i]:X2}, " +
                            $"read back 0x{readBack[i]:X2} (confirmed on retry: 0x{retryByte:X2}).");
                    }
                }
                Log.WriteLine("Verified: read-back matches the original content exactly.");
            }
        }

        /// <summary>
        /// Connects (same connect/security-access/upload/kickoff sequence as
        /// <see cref="ConnectAndStartSectorLoader"/>), erases the flash
        /// sector starting at <paramref name="cpuAddress"/> (CPU-bus addressed -- see
        /// <see cref="Edc15FlashVM.SectorFlashCpuBase"/> for the flash-relative-to-CPU-bus offset
        /// this must already include), then IMMEDIATELY reprograms <paramref name="originalBytes"/>
        /// back into that exact address range using the loader's existing <c>0x36</c> ("Program
        /// flash") command.
        ///
        /// <para>Deliberately does both in one call, not erase alone -- an erase-only test leaves
        /// the sector as all-<c>0xFF</c> with no valid data until something reprograms it, and if
        /// that reprogram step then fails or is never attempted, recovering means falling back to
        /// the ECU's lower-level boot-mode flashing path instead of this KWP2000-based one.
        /// Restoring the sector's own original content immediately keeps the net effect on the ECU
        /// a no-op if everything works, while still genuinely exercising both the new <c>0xB0</c>
        /// erase command AND the existing, reused (not new) <c>0x36</c> program command end to
        /// end -- this project has never exercised <c>0x36</c>/<c>E1C4</c> via Edc15VM before
        /// either, so this validates that too, not just the erase command.</para>
        ///
        /// <para><b>Full in-session read-back verify, via <see cref="ReadFlashRangeInSession"/>
        /// (the same still-running loader's own Cmd23).</para>
        /// </summary>
        public void EraseAndRestoreSector(uint cpuAddress, byte[] originalBytes)
        {
            ConnectAndStartSectorLoader();
            try
            {
                EraseAndProgramSector(cpuAddress, originalBytes, verify: true);
            }
            finally
            {
                TryCloseLoader();
            }
        }

        /// <summary>
        /// Per-sector flash write against an ALREADY-RUNNING sector loader (caller owns the connect
        /// -- e.g. via <see cref="Edc15FlashVM"/>'s Connect uploading the sector loader, or
        /// <see cref="ConnectAndStartSectorLoader"/>). For each sector, compares the loader's CmdB2
        /// checksum of the ECU's current content against <see cref="ComputeSectorChecksum"/> of the
        /// same range of <paramref name="image"/>; only sectors that DIFFER (or all, if
        /// <paramref name="forceFull"/>) are erased and reprogrammed via the proven
        /// <see cref="EraseAndProgramSector"/>. When <paramref name="verify"/> is set, re-queries each
        /// written sector's checksum afterward and throws on any mismatch. Always tells the loader to
        /// shut down (<see cref="TryCloseLoader"/>) on the way out.
        /// <para>Relies on the CmdB2 checksum command in the uploaded loader
        /// (docs/Loader-sector-erase.a66); the ECU checksum and <see cref="ComputeSectorChecksum"/>
        /// agree by construction (identical 16-bit LE-word sum).</para>
        /// </summary>
        public void WritePerSectorToRunningLoader(
            byte[] image, bool verify, bool forceFull,
            Action<string>? onStage = null, Action<int>? onPercent = null,
            Func<bool>? isStopRequested = null)
        {
            if (image.Length < 0x80000)
            {
                throw new ArgumentException(
                    $"image is {image.Length} bytes; expected at least 0x80000 (a full EDC15 flash image).",
                    nameof(image));
            }

            var sectors = Edc15FlashVM.FlashSectors;
            try
            {
                var toWrite = new List<int>();
                onStage?.Invoke(forceFull
                    ? "Full write requested -- every sector will be erased and rewritten."
                    : "Checking sector checksums to find changed sectors...");
                for (var i = 0; i < sectors.Length; i++)
                {
                    var (start, end) = sectors[i];
                    if (forceFull)
                    {
                        toWrite.Add(i);
                        continue;
                    }

                    var ecu = QuerySectorChecksumViaLoader((uint)start, (uint)end);
                    var file = ComputeSectorChecksum(image.AsSpan((int)start, (int)(end - start)));
                    if (ecu == file)
                    {
                        Log.WriteLine(
                            $"Sector {i} (0x{start:X}-0x{end:X}): unchanged (checksum 0x{ecu:X4}) -- skipping.");
                    }
                    else
                    {
                        Log.WriteLine(
                            $"Sector {i} (0x{start:X}-0x{end:X}): differs (ECU 0x{ecu:X4}, file 0x{file:X4}) -- will write.");
                        toWrite.Add(i);
                    }
                }

                if (toWrite.Count == 0)
                {
                    onStage?.Invoke("All sectors already match the image -- nothing to write.");
                    onPercent?.Invoke(100);
                    return;
                }

                onStage?.Invoke($"Writing {toWrite.Count} of {sectors.Length} sector(s)...");
                for (var k = 0; k < toWrite.Count; k++)
                {
                    if (isStopRequested?.Invoke() == true)
                    {
                        throw new OperationCanceledException("Stopped before writing the next sector.");
                    }

                    var i = toWrite[k];
                    var (start, end) = sectors[i];
                    var cpuStart = (uint)(start + Edc15FlashVM.SectorFlashCpuBase);
                    var sectorBytes = new byte[end - start];
                    Array.Copy(image, (int)start, sectorBytes, 0, sectorBytes.Length);
                    EraseAndProgramSector(cpuStart, sectorBytes, verify: false);
                    onPercent?.Invoke((int)((k + 1) * 100L / toWrite.Count));
                }

                if (verify)
                {
                    onStage?.Invoke("Verifying written sectors (re-querying checksums)...");
                    var bad = 0;
                    foreach (var i in toWrite)
                    {
                        var (start, end) = sectors[i];
                        var ecu = QuerySectorChecksumViaLoader((uint)start, (uint)end);
                        var file = ComputeSectorChecksum(image.AsSpan((int)start, (int)(end - start)));
                        if (ecu != file)
                        {
                            bad++;
                            Log.WriteLine(
                                $"VERIFY FAILED sector {i} (0x{start:X}-0x{end:X}): ECU 0x{ecu:X4} != file 0x{file:X4}.");
                        }
                    }
                    if (bad > 0)
                    {
                        throw new InvalidOperationException(
                            $"{bad} sector(s) failed post-write checksum verification.");
                    }
                    onStage?.Invoke("Verify passed: all written sectors match the image.");
                }
            }
            finally
            {
                TryCloseLoader();
            }
        }

        /// <summary>
        /// Uploads GetSectorEraseLoader() to 0x40E000 and launches it, assuming the caller has
        /// already opened the programming session + security access on <paramref name="kwp2000"/>.
        /// Shared by <see cref="ConnectAndStartSectorLoader"/> (which does its own session/security
        /// first) and Edc15FlashVM.Connect's uploadLoaderOverride (session/security done in
        /// ConnectOnce), so the per-sector write inherits Connect's recovery + speed + retry.
        /// </summary>
        internal static void UploadAndStartSectorLoader(KW2000Dialog kwp2000)
        {
            var loader = GetSectorEraseLoader();
            var len = loader.Length;
            var downloadResponse = kwp2000.SendReceive(DiagnosticService.requestDownload, [
                0x40, 0xE0, 0x00, 0x00, (byte)(len >> 16), (byte)(len >> 8), (byte)(len & 0xFF)
                ], excludeAddresses: true);
            var maxBlockLen = downloadResponse.Body[0];
            var s = new MemoryStream(loader);
            while (true)
            {
                Thread.Sleep(5);
                var blockBytes = new byte[maxBlockLen];
                var readCount = s.Read(blockBytes, 0, maxBlockLen - 1);
                if (readCount == 0)
                {
                    break;
                }

                SendTransferDataWithRetry(kwp2000, blockBytes.Take(readCount).ToArray());
            }

            kwp2000.SendMessage(
                DiagnosticService.startRoutineByLocalIdentifier, [0x02], excludeAddresses: true);
            _ = kwp2000.ReceiveMessage();
        }

        /// <summary>
        /// Shared connect step for the proven sector-erase loader (extracted from
        /// <see cref="EraseAndRestoreSector"/> so <see cref="QueryAllSectorChecksums"/> and any
        /// future per-sector writer can reuse it): assumes the caller has ALREADY done stage 1 (the
        /// standard KW1281 wakeup) and stage 2 (EndCommunication + a 10400 KW2000-mode wakeup), then
        /// starts the programming session, requests security access, uploads
        /// <see cref="GetSectorEraseLoader"/>, and launches it. On return the loader is running and
        /// all further traffic goes through <see cref="SendLoaderCommandRaw"/> (its own native wire
        /// format), not KW2000Dialog.
        /// </summary>
        private void ConnectAndStartSectorLoader()
        {
            var kwp2000 = new KW2000Dialog(_kwpCommon, (byte)_controllerAddress);
            kwp2000.VerboseLog = false;

            _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x89]);
            _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, [0x85]);

            const byte accMod = 0x41;
            var resp = kwp2000.SendReceive(DiagnosticService.securityAccess, [accMod]);

            var buf = new List<byte> { accMod + 1 };
            if (!resp.Body.SequenceEqual(new byte[] { accMod, 0x00, 0x00 }))
            {
                var seedBuf = resp.Body.Skip(1).Take(4).ToArray();
                var keyBuf = Edc15KeyAlgorithms.ComputeLvl41Key(0x508DA647, 0x3800000, seedBuf);
                buf.AddRange(keyBuf);
            }
            _ = kwp2000.SendReceive(DiagnosticService.securityAccess, buf.ToArray());

            UploadAndStartSectorLoader(kwp2000);
        }

        /// <summary>
        /// The client-side twin of the loader's CmdB2/ChecksumRange (docs/Loader-sector-erase.a66):
        /// a 16-bit "rotate-left-1 then add" rolling checksum over <paramref name="data"/>. MUST stay
        /// byte-for-byte equivalent to the assembly, since the whole point is that the value computed
        /// here over the binary equals the value the running loader computes over the ECU's flash for
        /// the same range. This is a change-detection checksum only -- NOT the ECU calibration checksum.
        /// </summary>
        public static ushort ComputeSectorChecksum(ReadOnlySpan<byte> data)
        {
            ushort acc = 0;
            foreach (var b in data)
            {
                acc = (ushort)((acc << 1) | (acc >> 15)); // ROL 1
                acc = (ushort)(acc + b);                  // += byte (16-bit wrap)
            }
            return acc;
        }

        /// <summary>
        /// CPU-bus base the loader's <b>read</b> path (Cmd23/E23E/E290) addresses this flash chip at:
        /// 0x080000, i.e. address-high byte = (flash-relative offset &gt;&gt; 16) + 0x08. This is NOT
        /// the same as the write/erase base <see cref="Edc15FlashVM.SectorFlashCpuBase"/> (0x200000)
        /// that CmdB0/Cmd36 use -- see <see cref="ReadFlashRangeInSession"/>'s doc comment for the
        /// real-hardware bug hunt that established this. CmdB2/ChecksumRange reads flash via the same
        /// E290, so it MUST be given read-convention addresses, not 0x200000-based ones.
        /// </summary>
        private const uint FlashReadCpuBase = 0x080000;

        /// <summary>
        /// Asks the RUNNING sector loader for its CmdB2 checksum of the flash-relative range
        /// [<paramref name="flashStart"/>, <paramref name="flashEnd"/>) (0x00000-0x80000). Converts to
        /// the loader's read-convention CPU addresses (<see cref="FlashReadCpuBase"/>) internally --
        /// the same convention <see cref="ReadFlashRangeInSession"/> uses -- so the loader checksums
        /// the correct flash content. Assumes <see cref="ConnectAndStartSectorLoader"/> has run.
        /// Returns the 2-byte checksum, comparable directly against <see cref="ComputeSectorChecksum"/>
        /// over the same flash-relative bytes.
        /// </summary>
        private ushort QuerySectorChecksumViaLoader(uint flashStart, uint flashEnd)
        {
            var cpuStart = flashStart + FlashReadCpuBase;
            var cpuEnd = flashEnd + FlashReadCpuBase;
            var body = new byte[]
            {
                (byte)((cpuStart >> 16) & 0xFF), (byte)((cpuStart >> 8) & 0xFF), (byte)(cpuStart & 0xFF),
                (byte)((cpuEnd >> 16) & 0xFF), (byte)((cpuEnd >> 8) & 0xFF), (byte)(cpuEnd & 0xFF),
            };
            var (_, data) = SendLoaderCommandRaw(
                0xB2, body, expectedResponseService: 0xB2,
                $"Checksum flash range 0x{flashStart:X}-0x{flashEnd:X} (cpu 0x{cpuStart:X}-0x{cpuEnd:X})");
            if (data.Length < 2)
            {
                throw new InvalidOperationException(
                    $"Checksum query for 0x{flashStart:X}-0x{flashEnd:X} returned {data.Length} data " +
                    "byte(s), expected 2.");
            }
            return (ushort)((data[0] << 8) | data[1]);
        }

        /// <summary>
        /// Connects/uploads the sector loader (via <see cref="ConnectAndStartSectorLoader"/>, so the
        /// caller must have already done the KW1281 + KW2000 wakeup) and queries the loader's CmdB2
        /// checksum for every entry in <see cref="Edc15FlashVM.FlashSectors"/>, returning one ushort
        /// per sector (in sector order). Read-only: touches no flash contents. Used by the temporary
        /// diagnostic command to validate the loader's checksum command + <see cref="ComputeSectorChecksum"/>
        /// parity on real hardware before the per-sector write ever relies on it.
        /// </summary>
        public ushort[] QueryAllSectorChecksums()
        {
            ConnectAndStartSectorLoader();
            try
            {
                var sectors = Edc15FlashVM.FlashSectors;
                var result = new ushort[sectors.Length];
                for (var i = 0; i < sectors.Length; i++)
                {
                    var (start, end) = sectors[i];
                    result[i] = QuerySectorChecksumViaLoader((uint)start, (uint)end);
                    Log.WriteLine($"Sector {i} (0x{start:X}-0x{end:X}): ECU checksum 0x{result[i]:X4}");
                }
                return result;
            }
            finally
            {
                TryCloseLoader();
            }
        }

        /// <summary>
        /// Best-effort loader shutdown: sends CmdA2 ("Reboot") -- the same custom command
        /// <see cref="ReadWriteEeprom"/> already uses to return the ECU to normal operation when
        /// it's done -- and never throws itself, so it can't mask whatever real error is already
        /// propagating out of <see cref="EraseAndRestoreSector"/>'s try block. Mirrors
        /// <see cref="Edc15FlashVM.TryCloseEcu"/>'s exact reasoning and doc comment almost word for
        /// word: without this, a mid-operation failure (or even just success followed by never
        /// telling the loader to stop) leaves it running with no idea the tester is done, and the
        /// NEXT wakeup attempt has to fight past that instead of getting a clean slate.
        /// </summary>
        private void TryCloseLoader()
        {
            try
            {
                SendLoaderCommandRaw(0xA2, [], expectedResponseService: 0x76, "Close loader (reboot)");

                // CmdA2's ACK (above) is followed by a RAW 0x55 byte (via XmitRL7, not enveloped --
                // see CmdA2's own definition in docs/Loader-sector-erase.a66), the same shape
                // ReadWriteEeprom's own identical reboot step already expects.
                var b = _kwpCommon.Interface.ReadByte();
                if (b == 0x55)
                {
                    Log.WriteLine("Loader closed (ECU rebooted to normal operation).");
                }
                else
                {
                    Log.WriteLine($"(Loader close: expected a final 0x55 after reboot, got 0x{b:X2}.)");
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"(Best-effort loader close after failure also failed: {ex.Message})");
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes starting at <paramref name="cpuAddress"/> using
        /// the loader's own Cmd23/E23E ("Read flash") command. Assumes the sector-erase loader
        /// (<see cref="GetSectorEraseLoader"/>) is ALREADY uploaded and running -- this does not
        /// connect, upload, or kick anything off; call it only right after
        /// <see cref="EraseAndRestoreSector"/> (or another method that leaves this exact loader
        /// running), on the same physical connection.
        ///
        /// <para>This is what lets <see cref="EraseAndRestoreSector"/> verify without
        /// disconnecting and re-uploading a different loader (<see cref="Edc15FlashVM"/>'s own,
        /// separate flash loader) through a fresh wakeup.</para>
        /// </summary>
        public byte[] ReadFlashRangeInSession(uint cpuAddress, int length)
        {
            if (length <= 0)
            {
                throw new ArgumentException($"length must be positive -- got {length}.", nameof(length));
            }

            var result = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var chunkLen = Math.Min(ReadFlashRangeChunkSize, length - offset);
                var chunkAddress = cpuAddress + (uint)offset;

                // Address-high byte uses Loader-flash.bin's convention
                // (flash-relative-offset-high-byte + 0x08), NOT this loader's usual
                // SectorFlashCpuBase(0x200000)-based one CmdB0/Cmd36 use -- see this method's own
                // doc comment for why.
                // Mid/low bytes are unaffected -- SectorFlashCpuBase's low 16 bits are zero, so
                // cpuAddress and the flash-relative offset share the same low 16 bits regardless of
                // which convention the high byte uses.
                var flashRelativeHighByte =
                    (byte)(((chunkAddress - (uint)Edc15FlashVM.SectorFlashCpuBase) >> 16) & 0xFF);
                var addrHigh = (byte)(flashRelativeHighByte + 0x08);

                var (_, data) = SendLoaderCommandRaw(
                    0x23,
                    [
                        addrHigh,
                        (byte)((chunkAddress >> 8) & 0xFF),
                        (byte)(chunkAddress & 0xFF),
                        (byte)chunkLen,
                    ],
                    expectedResponseService: 0x36,
                    $"Read flash at 0x{chunkAddress:X} ({chunkLen} byte(s))");

                if (data.Length != chunkLen)
                {
                    throw new InvalidOperationException(
                        $"Read flash at 0x{chunkAddress:X}: expected {chunkLen} byte(s) back, " +
                        $"got {data.Length}.");
                }

                Array.Copy(data, 0, result, offset, chunkLen);
                offset += chunkLen;
            }

            return result;
        }

        /// <summary>
        /// Max data bytes per <see cref="ReadFlashRangeInSession"/> request. Kept equal to
        /// <see cref="FlashProgramChunkSize"/>.
        /// </summary>
        private const int ReadFlashRangeChunkSize = 58;

        /// <summary>
        /// Max data bytes per <see cref="EraseAndRestoreSector"/> program-flash chunk.
        ///
        /// <para><b>Settled on the bench</b> (via a temporary chunk-size sweep diagnostic, since
        /// removed). Every chunk size from 66 up to 250 was
        /// tried on real hardware and ALL succeeded. Crucially, bigger was NOT faster: sizes 66..~200 timed the
        /// same, and 250 was actually SLOWER, confirming that at 124800 baud the data bytes dominate and
        /// per-chunk overhead is tiny, while a near-max frame just adds latency.</para>
        ///
        /// <para>So the size is chosen not for throughput but to avoid a short trailing chunk: <b>128</b>
        /// divides every EDC15 flash sector exactly (all are powers of two -- seven 64KB, one 32KB, two
        /// 8KB, one 16KB; 8192/128 = 64), so no sector ever ends on a runt chunk. Still even (required:
        /// Loader.a66's E1C4 programs whole 16-bit words, and an odd chunk size silently drops the last
        /// byte of every full chunk via E1C4's <c>SUB R4,#4 / SHR R4,#01H / SUB R4,#1</c> word-count
        /// arithmetic). It's ~2x fewer round trips than 66 with no measured downside, and comfortably
        /// clear of the size where 250 slowed down. Do not exceed the 254-byte frame ceiling (1-byte
        /// length field). The real speed lever was removing the per-sector erase wait, not this.</para>
        /// </summary>
        private const int FlashProgramChunkSize = 128;

        /// <summary>
        /// Writes addressValuePairs to EEPROM using the loader's 0xA9/0xAA batched "write N bytes"
        /// commands (Loader-EEPROM.bin). Pairs are grouped into maximal runs of the same EEPROM page (address &lt;=
        /// 0xFF =&gt; page 0, else page 1), preserving the caller's original order across runs,
        /// and each run is further chunked to at most 255 pairs -- the loader's count parameter
        /// (E3W0/E3W1's R4, moved in from a single byte in the command buffer) can't represent
        /// more than that in one command.
        /// </summary>
        private void WriteEepromBatched(
            KW2000Dialog kwp2000, List<KeyValuePair<ushort, byte>> addressValuePairs)
        {
            const int maxPairsPerChunk = 255;

            var i = 0;
            while (i < addressValuePairs.Count)
            {
                var page1 = addressValuePairs[i].Key > 0xFF;

                var runEnd = i + 1;
                while (runEnd < addressValuePairs.Count &&
                       (addressValuePairs[runEnd].Key > 0xFF) == page1 &&
                       runEnd - i < maxPairsPerChunk)
                {
                    runEnd++;
                }

                var count = runEnd - i;
                var service = (DiagnosticService)(page1 ? 0xAA : 0xA9);

                kwp2000.SendMessage(service, [(byte)count], excludeAddresses: true);
                var resp = kwp2000.ReceiveMessage();
                if (!resp.IsPositiveResponse(DiagnosticService.transferData))
                {
                    throw new InvalidOperationException($"Write EEPROM (batched) failed.");
                }

                var raw = new byte[count * 2];
                for (var j = 0; j < count; j++)
                {
                    var pair = addressValuePairs[i + j];
                    raw[j * 2] = (byte)(pair.Key & 0xFF);
                    raw[j * 2 + 1] = pair.Value;
                }

                _kwpCommon.WriteBytes(raw);
                Log.WriteLine(
                    $"Sent {count} pairs to page {(page1 ? 1 : 0)}: {Utils.DumpBytes(raw)}",
                    LogDest.File);

                resp = kwp2000.ReceiveMessage();
                if (!resp.IsPositiveResponse(DiagnosticService.transferData))
                {
                    throw new InvalidOperationException($"Write EEPROM (batched) failed.");
                }

                i = runEnd;
            }
        }

        /// <summary>
        /// The values DisplayEepromInfo computes from an EDC15 EEPROM image and displays it.
        /// </summary>
        public readonly record struct Edc15EepromInfo(
            ushort Skc,
            double OdometerKm,
            string Vin,
            string ImmoNumber,
            string ImmoId,
            bool ImmoIsOn);

        public static Edc15EepromInfo GetEepromInfo(ReadOnlySpan<byte> eeprom)
        {
            var skc = Utils.GetShort(eeprom, 0x12E);

            double odometerKm =
                eeprom[0x1BF] +
                (eeprom[0x1C0] << 8) +
                (eeprom[0x1C1] << 16) +
                ((eeprom[0x1C2] & 0x3F) << 24);
            odometerKm /= 100.0;

            var vin = Utils.DumpAscii(eeprom.Slice(0x140, 17).ToArray());
            var immoNumber = Utils.DumpAscii(eeprom.Slice(0x131, 14).ToArray());
            var immoId = Utils.DumpBytes(eeprom.Slice(0x126, 7).ToArray());

            const ushort immo1Addr = 0x1B0;
            var immo1 = eeprom[immo1Addr];
            const ushort immo2Addr = 0x1DE;
            var immo2 = eeprom[immo2Addr];
            var immoIsOn = !(immo1 == 0x60 && immo2 == 0x60);

            return new Edc15EepromInfo(skc, odometerKm, vin, immoNumber, immoId, immoIsOn);
        }

        public static void DisplayEepromInfo(ReadOnlySpan<byte> eeprom)
        {
            var info = GetEepromInfo(eeprom);
            Log.WriteLine($"SKC: {info.Skc:D5}");
            Log.WriteLine($"Odometer: {info.OdometerKm} km");
            Log.WriteLine($"VIN: {info.Vin}");
            Log.WriteLine($"Immo Number: {info.ImmoNumber}");
            Log.WriteLine($"Immo Id: {info.ImmoId}");
            const ushort immo1Addr = 0x1B0;
            const ushort immo2Addr = 0x1DE;
            var immoStatus = info.ImmoIsOn ? "On" : "Off";
            Log.WriteLine(
                $"Immo is {immoStatus} (${immo1Addr:X3}=${eeprom[immo1Addr]:X2}, " +
                $"${immo2Addr:X3}=${eeprom[immo2Addr]:X2})");
        }

        /// <summary>
        /// The single EEPROM loader used for both reads and writes: reads/writes the serial EEPROM,
        /// exposes the 0xA9/0xAA "write N bytes" batched-write commands, and sets up its own
        /// EEPROM-bus GPIO directions at the start of every write (the EInit routine) so a write
        /// needs no dump in front of it. See Loader-EEPROM.a66 (assembled to
        /// Loader-EEPROM.bin with Tools/c166/reasm.py, verified byte-identical to Keil, and
        /// validated on hardware). Replaced the stock single-byte-write Loader.bin, which is
        /// gone -- this loader is a strict superset of it.
        /// </summary>
        private static byte[] GetEepromLoader() =>
            GetLoaderFromResource("BitFab.KW1281Test.EDC15.Loader-EEPROM.bin");

        /// <summary>
        /// Same loader as GetEepromLoader().
        ///
        /// Needs to be assembled separately (Keil C166) from docs/Loader-sector-erase.a66 and the
        /// resulting Loader-sector-erase.bin dropped in at
        /// KWHack.Core/vendor-kw1281test/EDC15/Loader-sector-erase.bin (plus a matching
        /// EmbeddedResource entry in KWHack.Core.csproj, mirroring Loader-EEPROM.bin's) before
        /// this method -- or anything that calls it, like <see cref="ConnectAndStartSectorLoader"/> --
        /// will work.
        /// </summary>
        private static byte[] GetSectorEraseLoader() =>
            GetLoaderFromResource("BitFab.KW1281Test.EDC15.Loader-sector-erase.bin");

        /// <summary>
        /// Shared by GetEepromLoader()/GetSectorEraseLoader(): loads the named embedded resource and
        /// patches it exactly the way the ECU's boot ROM requires -- see the "must be EFCD8631"
        /// comment below. This patching is entirely a function of the resource's own bytes and
        /// length, so it applies unchanged to any loader binary of any size/content, including a
        /// larger one with new commands appended (no manual checksum work needed when swapping in
        /// a different compiled loader).
        /// </summary>
        private static byte[] GetLoaderFromResource(string resourceName)
        {
            // Use the assembly that actually contains the embedded resource. On Android
            // Assembly.GetEntryAssembly() can return null, so GetEntryAssembly() is not safe here.
            var assembly = typeof(Edc15VM).Assembly;
            var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                throw new InvalidOperationException(
                    $"Unable to load {resourceName} embedded resource.");
            }

            var loaderLength = resourceStream.Length + 4; // Add 4 bytes for checksum correction
            loaderLength = (loaderLength + 7) / 8 * 8; // Round up to a multiple of 8 bytes
            var buf = new byte[loaderLength];

            resourceStream.ReadExactly(buf, 0, (int)resourceStream.Length);

            // In order for this loader to be executed by the ECU, the checksum of all the bytes
            // must be EFCD8631.

            // Patch the loader with the location of the end (actually 1 byte past the end)
            ushort loaderEnd = (ushort)(0xE000 + loaderLength);
            buf[0x0E] = (byte)(loaderEnd & 0xFF);
            buf[0x0F] = (byte)(loaderEnd >> 8);

            // Take the checksum of the loader up to but not including the checksum correction
            ushort r6 = 0xEFCD;
            ushort r1 = 0x8631;
            Checksum(ref r6, ref r1, buf.Take(buf.Length - 4).ToArray());

            // Calculate the checksum correction bytes and insert them at the end of the loader
            var padding = CalcPadding(r6, r1);
            Array.Copy(padding, 0, buf, buf.Length - 4, 4);

            return buf;
        }

        /// <summary>
        /// Calculate the checksum correction padding needed to result in a checksum of EFCD8631
        /// </summary>
        /// <param name="r6"></param>
        /// <param name="r1"></param>
        /// <returns></returns>
        private static byte[] CalcPadding(ushort r6, ushort r1)
        {
            var paddingH = (ushort)(0xDF9B ^ r6);
            var paddingL = (ushort)(r1 - 0xAB85);

            return
            [
                (byte)(paddingL & 0xFF),
                (byte)(paddingL >> 8),
                (byte)(paddingH & 0xFF),
                (byte)(paddingH >> 8)
            ];
        }

        /// <summary>
        /// EDC15 checksum algorithm (sub_1584).
        /// Calculates a 32-bit checksum of an array of bytes based on an initial 32-bit seed.
        /// Based on https://www.ecuconnections.com/forum/viewtopic.php?f=211&t=49704&sid=5cf324c44d2c74d372984f428ffea5ed
        /// </summary>
        /// <param name="r6">Input: High word of seed, Output: High word of checksum</param>
        /// <param name="r1">Input: Low word of seed, Output: Low word of checksum</param>
        /// <param name="buf">Buffer to calculate checksum for</param>
        static void Checksum(ref ushort r6, ref ushort r1, byte[] buf)
        {
            int r3 = 0; // Buffer index
            int r0 = buf.Length;
            while (true)
            {
                r1 ^= GetBuf(buf, r3); r3 += 2;
                r1 = Rol(r1, r6, out ushort c);
                r6 = (ushort)(r6 - GetBuf(buf, r3) - c); r3 += 2;
                r6 ^= r1;
                if (r3 >= r0)
                {
                    break;
                }

                r1 = (ushort)(r1 - GetBuf(buf, r3) - 1); r3 += 2;
                r1 += 0xDAAD;
                r6 ^= GetBuf(buf, r3); r3 += 2;
                r6 = Ror(r6, r1);
                if (r3 >= r0)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Rotates a 16-bit value right by count bits.
        /// </summary>
        private static ushort Ror(ushort value, ushort count)
        {
            count &= 0xF;
            value = (ushort)((value >> count) | (value << (16 - count)));
            return value;
        }

        /// <summary>
        /// Rotates a 16-bit value left by count bits. Carry will be equal to the last bit rotated
        /// or 0 if the low 4 bits of count are 0;
        /// </summary>
        private static ushort Rol(ushort value, ushort count, out ushort carry)
        {
            count &= 0xF;
            value = (ushort)((value << count) | (value >> (16 - count)));
            carry = ((value & 1) == 0 || (count == 0)) ? (ushort)0 : (ushort)1;
            return value;
        }

        private static ushort GetBuf(byte[] buf, int ix)
        {
            return (ushort)(buf[ix] + (buf[ix + 1] << 8));
        }

        private readonly IKwpCommon _kwpCommon;
        private readonly int _controllerAddress;

        public Edc15VM(IKwpCommon kwpCommon, int controllerAddress)
        {
            _kwpCommon = kwpCommon;
            _controllerAddress = controllerAddress;
        }
    }
}
