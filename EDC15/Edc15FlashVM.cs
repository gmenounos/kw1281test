using BitFab.KW1281Test.Kwp2000;
using BitFab.KW1281Test.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BitFab.KW1281Test.EDC15
{
    /// <summary>
    /// EDC15 external-flash read/write, ported from ecu-tool - ECU_Flasher_UNO_EDC15.ino
    /// (Bi0H4z4rD's EDC15 Toolbox V0.4) — a different capability than <see cref="Edc15VM"/>
    /// (which only reads/writes the small serial EEPROM). This talks to
    /// a DIFFERENT RAM loader (<c>Loader-flash.bin</c>, embedded resource,
    /// used for all three supported sub-variants (P/V/VM+); only the security-access
    /// key and the post-loader handshake bytes differ per variant.
    ///
    /// <para><b>Note (loader-upload/per-variant handshake only):</b> this part is a
    /// byte-for-byte port, not a reinterpretation. It's a long fixed sequence of expected bytes with
    /// no obvious semantic structure in several places (see <see cref="VariantInfos"/>) — those are
    /// transcribed verbatim from the reference source rather than reconstructed from first
    /// principles, and cross-checked here by confirming every expected-bytes array is consumed
    /// exactly to its end by the sequence of Send/Verify calls (see the comments at each call site)
    /// — the same self-consistency check that caught nothing wrong when tracing this against the
    /// original .ino.</para>
    ///
    /// <para><b>Flash write risk:</b> unlike the EEPROM writes elsewhere in this app (which touch a
    /// handful of well-understood bytes), this erases and rewrites the ECU's entire program flash.
    /// An interrupted or incorrect write can brick the ECU.
    /// </summary>
    public sealed class Edc15FlashVM
    {
        public enum Variant
        {
            /// <summary>EDC15P/P+.</summary>
            P,
            /// <summary>EDC15V (same family/key as Edc15VM's existing EEPROM support).</summary>
            V,
            /// <summary>EDC15VM+ (512KB flash only — the reference tool explicitly does not
            /// support the 1MB EDC15VM variant).</summary>
            VM,
            /// <summary>
            /// Not a real ECU variant -- tells <see cref="Connect"/> to try P/V/VM in turn (see its
            /// own doc comment for the order and why) instead of requiring the caller to already
            /// know which one their ECU actually is. Never a key into <see cref="VariantInfos"/>.
            /// </summary>
            Auto,
        }

        /// <summary>
        /// Link-speed profile for an EDC15 flash read/write session, chosen by the caller and
        /// negotiated (if not <see cref="Low"/>) right after security access via
        /// <see cref="TrySetSpeed"/>. Baud/speed-byte values are from
        /// <c>Reference/ecu-tool/KLINE/kline.cpp</c>'s <c>startDiagSession</c> ladder
        /// </summary>
        public enum FlashSpeed
        {
            /// <summary>No speed-up: stay at the 10400 baud the KWP2000 wakeup establishes. The
            /// default for recovery-mode (soft-bricked) sessions, and the safest/slowest option.</summary>
            Low,
            /// <summary>38400 baud, negotiated with speed byte <c>0x50</c> -- the rate MPPS uses for
            /// its "medium" speed setting. The default for ordinary flashing.</summary>
            Medium,
            /// <summary>124800 baud, negotiated with speed byte <c>0x87</c> -- MPPS's "fast" rate and
            /// the one the rest of this class's timing (<see cref="WriteByteDelayed"/> etc.) was
            /// originally tuned against.</summary>
            High,
        }

        private readonly record struct VariantInfo(
            long Key, byte[] ArduBytes, byte[] EcuBytes);

        private static readonly Dictionary<Variant, VariantInfo> VariantInfos = new()
        {
            // Combined 32-bit key = (Key1 << 16) | Key2 from ProcessKey()'s per-EcuType branch.
            // V's key (0x508DA647) is the same constant Edc15VM already uses for EEPROM access —
            // confirms this is the same seed/key algorithm shape (Edc15KeyAlgorithms.ComputeLvl41Key)
            // just reused here for flash access, with Key3 fixed at 0x3800000 for all three variants.
            [Variant.P] = new VariantInfo(
                0xDA1CF781,
                new byte[] { 0x08, 0x31, 0x02, 0x10, 0x00, 0x00, 0x0F, 0xBF, 0xFF,
                             0x05, 0x23, 0x08, 0x00, 0x00, 0x10,
                             0x05, 0x23, 0x10, 0x00, 0x00, 0x10,
                             0x08, 0x31, 0x02, 0x08, 0x00, 0x00, 0x0F, 0xBF, 0xFF },
                new byte[] { 0x03, 0x7F, 0x31, 0x22,
                             0x11, 0x36, 0x0A, 0x01, 0x04, 0x00, 0x05, 0x00, 0x0B, 0x00, 0x0C, 0x00, 0x0D, 0x00, 0x0F, 0x00, 0x10, 0x00,
                             0x11, 0x36, 0x0A, 0x01, 0x04, 0x00, 0x05, 0x00, 0x0B, 0x00, 0x0C, 0x00, 0x0D, 0x00, 0x0F, 0x00, 0x10, 0x00,
                             0x01, 0x7F }),
            [Variant.V] = new VariantInfo(
                0x508DA647,
                new byte[] { 0x08, 0x31, 0x02, 0x08, 0x00, 0x00, 0x0F, 0xBF, 0xFF },
                new byte[] { 0x03, 0x7F, 0x31, 0x22 }),
            [Variant.VM] = new VariantInfo(
                0xF25E6533,
                new byte[] { 0x08, 0x31, 0x02, 0x10, 0x00, 0x00, 0x0F, 0xBF, 0xFF,
                             0x05, 0x23, 0x08, 0x00, 0x00, 0x10,
                             0x05, 0x23, 0x10, 0x00, 0x00, 0x10,
                             0x08, 0x31, 0x02, 0x08, 0x00, 0x00, 0x0F, 0xBF, 0xFF },
                new byte[] { 0x03, 0x7F, 0x31, 0x22,
                             0x11, 0x36, 0xDB, 0x00, 0xF2, 0xF0, 0x40, 0x00, 0x26, 0xF0, 0x08, 0x00, 0x84, 0x00, 0x72, 0xFB, 0xF6, 0xF0,
                             0x11, 0x36, 0xDB, 0x00, 0xF2, 0xF0, 0x40, 0x00, 0x26, 0xF0, 0x08, 0x00, 0x84, 0x00, 0x72, 0xFB, 0xF6, 0xF0,
                             0x01, 0x7F }),
        };

        private const long FlashEnd = 0x7FEE2; // Full readable range for ReadFlash.
        private const long WriteBootStart = 0x7C000;
        private const long WriteBootEnd = 0x7FFFF;
        private const long WriteMainStart = 0x00000;
        private const long WriteMainEnd = 0x7BFFF;

        /// <summary>
        /// Extra delay (milliseconds) inserted after every chunk while writing the boot block
        /// (<see cref="WriteBootStart"/>-<see cref="WriteBootEnd"/>), but ONLY when
        /// <see cref="_fastBaudActive"/> is true -- see <see cref="WriteFlash"/>'s call into
        /// <see cref="WriteFlashBlock"/> for exactly where this gets applied.
        ///
        /// <para>Requested directly: the boot block is the single highest-stakes ~16KB of a write --
        /// if it's corrupted or left incomplete, the ECU won't boot at all and needs a full
        /// hardware boot-mode recovery to un-brick. The old, pre-batching version
        /// of this code paid a large ACCIDENTAL per-byte delay for every single write (see
        /// SendFlashWriteChunk's "Batched I/O" doc comment -- roughly 1ms+ of USB-transaction/syscall
        /// overhead per byte, from routing every byte through its own separate native write+echo-read
        /// call pair) which, whatever else it cost in raw throughput, also gave the loader/ECU more
        /// breathing room between bytes. Batching removed that overhead entirely (a deliberate,
        /// correct fix -- see that doc comment), so fast-mode writes now have essentially none of it
        /// left anywhere in the write. This restores a modest, INTENTIONAL amount of it, but only
        /// where the stakes are highest (the boot block, not the much larger and lower-risk main
        /// block) and only roughly, not to the same degree as the old accidental overhead or a full
        /// baud downshift.</para>
        ///
        /// <para>20ms is a rough half-speed target for the boot block specifically, not a measured
        /// value: at 124800 baud a ~256-byte chunk (250-byte max data + 5-byte header + 1-byte
        /// checksum) takes on the order of ~20ms of wire time alone (124800bps / ~10 bits-per-byte
        /// &#8776; 12480 bytes/sec &#8776; 0.08ms/byte &#215; 256), before any ACK round-trip or retry
        /// overhead on top. Adding one more ~20ms gap per chunk roughly doubles each chunk's total
        /// elapsed time, i.e. roughly halves throughput, for exactly the duration this doc comment
        /// says it should. Because the boot block is only 0x4000 (16,384) bytes -- about 65 chunks at
        /// 250 bytes each -- this adds at most a couple of extra seconds to a full write even in the
        /// worst case, negligible next to the minutes the much larger main block (0x7C000, ~31x
        /// bigger) takes either way.</para>
        /// </summary>
        private const int BootBlockFastModeInterChunkDelayMs = 20;

        private readonly IKwpCommon _kwpCommon;

        /// <summary>
        /// True once <see cref="Connect"/> has actually started sending the loader for the current
        /// attempt (set right before <see cref="SendLoader"/> is called, reset at the start of every
        /// <see cref="Connect"/> call) -- see that call site's own comment for why this specific
        /// point, rather than "wakeup succeeded" or "SendLoader returned", is the right threshold
        /// for deciding whether <see cref="TryCloseEcu"/> is worth attempting on failure.
        /// </summary>
        private bool _loaderMayBeRunning;

        /// <summary>
        /// True once <see cref="TrySetSpeed"/> has confirmed the ECU accepted the 124800-baud
        /// speed-up (reset at the start of every <see cref="ConnectOnce"/> attempt, alongside
        /// <see cref="_loaderMayBeRunning"/>) -- checked by <see cref="WriteByteDelayed"/> to decide
        /// whether the fixed inter-byte delay tuned for the slow negotiating speed is even needed.
        /// See that method's own doc comment for the real-hardware evidence a flat 5ms delay
        /// regardless of actual link speed made a full flash write take 30-60+ minutes.
        /// </summary>
        private bool _fastBaudActive;

        public Edc15FlashVM(IKwpCommon kwpCommon)
        {
            _kwpCommon = kwpCommon;
        }

        /// <summary>
        /// Wakes up the ECU (5-baud slow-init dance -- see class doc comment for why this differs
        /// from the reference tool's fast-init), authenticates with the variant-specific key,
        /// uploads the flash-capable RAM loader, and leaves the ECU ready for
        /// <see cref="ReadFlash"/>/<see cref="WriteFlash"/>.
        ///
        /// <para><paramref name="allowFastBaud"/> is currently a no-op here (see the doc comment
        /// right above the (deliberately unused) escalation site below for why) -- it's still
        /// threaded through from the caller so re-enabling it later is a one-line change once the
        /// real fix is implemented, not a re-plumbing job.</para>
        ///
        /// <paramref name="variant"/> == <see cref="Variant.Auto"/> tries V, then P, then VM (see
        /// <see cref="ConnectAuto"/>) instead of requiring the caller to already know which one
        /// their ECU is -- every other variant value connects directly, exactly as before.
        ///
        /// <para>Retries the whole wakeup-through-security-access sequence once (a fresh 5-baud
        /// slow init and all) if it fails before the loader upload starts -- see the doc comment
        /// on the retry loop itself, below, for the real-hardware evidence this is based on.</para>
        ///
        /// <para>Self-heals against a soft-bricked ECU (stuck answering every wakeup in KWP2000
        /// mode instead of the normal KW1281 identity, e.g. left that way by an earlier write --
        /// this app's own or another tool's -- that didn't complete cleanly): if <see cref="ConnectOnce"/>
        /// confirms that state (see its own doc comment for exactly how), this transparently
        /// switches to the same relaxed, no-KW1281-required connect <see cref="WriteFlashRecovery"/>/
        /// <see cref="ReadFlashRecovery"/> use, rather than surfacing the failure to the caller. This
        /// means every ordinary caller of this method -- dumpedc15flash/loadedc15flash (and, through
        /// <c>EcuFlashViewModel</c>/<c>EcuCloneViewModel</c>, the
        /// GUI ECU tab's Read/Write/Write-and-Verify Flash and Clone) -- recovers from this state
        /// automatically instead of failing outright, with no special handling needed at the call
        /// site: a soft-bricked ECU just makes this method take a little longer before succeeding
        /// normally.</para>
        /// </summary>
        /// <param name="isStopRequested">
        /// Polled between this method's own retry attempts, <see cref="ConnectAuto"/>'s per-variant
        /// loop, AND now (via <see cref="ConnectOnce"/> passing it through to
        /// <see cref="IKwpCommon.WakeUp"/>) between each individual wakeup's own internal up-to-3
        /// retries too -- if it returns true, this throws <see cref="OperationCanceledException"/>
        /// immediately instead of starting another attempt at whichever level. This param is for
        /// the CONNECT phase specifically; <see cref="ReadFlash"/> and <see cref="WriteFlash"/>
        /// take their own separate isStopRequested (checked once per packet, not shared with this
        /// one) now that the user has explicitly asked for Stop to reach those too, brick risk
        /// accepted. This exists because a real-hardware session
        /// (KWHack16.log) showed the Stop button appearing to do nothing: with no way to observe a
        /// stop request, a stuck Auto attempt had to grind through all 3 connect attempts for all 3
        /// variants -- up to 9 full wakeup sequences, each of which could itself burn up to 3 more
        /// tries with several 8-second read timeouts.
        /// </param>
        public void Connect(Variant variant, FlashSpeed speed, Func<bool>? isStopRequested = null) =>
            ConnectCore(variant, speed, isStopRequested, uploadLoaderOverride: null);

        private void ConnectCore(
            Variant variant, FlashSpeed speed, Func<bool>? isStopRequested,
            Action<KW2000Dialog, VariantInfo>? uploadLoaderOverride)
        {
            if (variant == Variant.Auto)
            {
                ConnectAuto(speed, isStopRequested, uploadLoaderOverride);
                return;
            }

            // A real-hardware capture showed the very first startDiagnosticSession
            // of a session -- sent right after a clean, correctly-keyworded 5-baud wakeup into
            // KWP2000 mode -- coming back as a well-formed, checksum-valid KWP2000 frame that's
            // simply addressed wrong (DestAddress 0x06 instead of this tester's own 0xF1, with an
            // NRC that doesn't correspond to anything we sent either), rather than a timeout or a
            // garbled/misaligned response. That specific shape doesn't look like ordinary K-line
            // noise or a byte-alignment glitch (both of which this class already has other,
            // narrower fixes for elsewhere -- see CheckRec's and ReadFlashDataPacket's spurious
            // leading-0x00 handling) -- it reads more like a stray, fully-formed frame from
            // elsewhere on the bus (a genuine multi-drop K-line cross-talk artifact, or some other
            // module's own power-on diagnostic chatter) landing at exactly the wrong moment, which
            // is the kind of thing more plausible right after a full, cold power-up of the whole
            // vehicle electrical system (confirmed via the user's own account: ECU fully
            // disconnected from power for 10+ minutes beforehand) than on a warm reset. This is
            // reasoned from one capture, not proven from a bus trace -- but critically, the exact
            // fix doesn't depend on nailing the mechanism: the user's own manual retry (tear this
            // attempt down, do a whole fresh wakeup) reliably recovered immediately every time, so
            // automating exactly that -- rather than chasing the byte-level cause further -- is the
            // most direct way to stop it from needing a human to notice and retry by hand.
            //
            // Scoped to ONLY retry failures that happen before the loader upload starts (checked via
            // _loaderMayBeRunning, same gate ConnectAuto/TryCloseEcu already use for a related but
            // distinct reason) -- once real bytes have gone toward the loader, a failure means
            // something different (a wrong-variant handshake mismatch, a dropped transferData
            // chunk, ...) that ConnectAuto's own per-variant fallback and TryCloseEcu's best-effort
            // cleanup already own; blindly retrying a fresh wakeup on top of that would just
            // duplicate/confuse that existing handling rather than address this specific failure
            // mode.
            //
            const int maxConnectAttempts = 3;
            for (var attempt = 1; attempt <= maxConnectAttempts; attempt++)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped before the next connect attempt.");
                }

                try
                {
                    ConnectOnce(variant, speed, isStopRequested, uploadLoaderOverride: uploadLoaderOverride);
                    return;
                }
                catch (Edc15SoftBrickDetectedException)
                {
                    // Confirmed soft-bricked (see ConnectOnce's own 9600-then-10400-probe
                    // detection) -- not an ordinary transient hiccup worth blind-retrying the same
                    // doomed normal-KW1281-first path against 2 more times, so stop this retry loop
                    // immediately and switch straight to recovery mode instead. This is what makes
                    // EVERY normal caller (dumpedc15flash/loadedc15flash) transparently
                    // self-heal against a soft-bricked ECU: the caller never even sees this
                    // exception, it just experiences a slightly slower, ordinary-looking successful
                    // connect.
                    Log.WriteLine(
                        "ECU is soft-bricked -- switching to recovery mode instead of the normal " +
                        "connect...");
                    ConnectForRecoveryWrite(variant, isStopRequested, uploadLoaderOverride);
                    return;
                }
                catch (Exception ex) when (attempt < maxConnectAttempts && !_loaderMayBeRunning)
                {
                    Log.WriteLine(
                        $"Connect attempt {attempt}/{maxConnectAttempts} failed before the loader " +
                        $"started ({ex.Message}); retrying with a fresh wakeup...");
                    Thread.Sleep(500);
                }
            }
        }


        /// <param name="requireKw1281First">
        /// True (every normal caller) means the standard two-step dance: a genuine KW1281 wakeup
        /// (must report protocol 1281 or this throws), a KW1281 Connect+EndCommunication, then a
        /// SECOND 5-baud wakeup that this time reports KWP2000 -- see the class doc comment's
        /// "Wakeup note". False skips straight to that second wakeup, forced to 10400, with no
        /// preceding KW1281 handshake at all -- only <see cref="ConnectForRecoveryWrite"/> uses
        /// this, for an ECU that's already answering EVERY 5-baud pulse with a KWP2000 keyword
        /// directly (a soft-bricked EDC15). Requiring protocol 1281 first is what makes ordinary Connect
        /// calls throw immediately on an ECU stuck exactly that way, rather than ever reaching the
        /// KWP2000 side at all.
        /// </param>
        /// <param name="skipLoaderSpecificSession">
        /// False (every normal caller) sends BOTH startDiagnosticSession(0x89) -- this app's own
        /// loader-specific subfunction -- and, right after, startDiagnosticSession(0x85) -- the
        /// ISO14230-STANDARD "ecuProgrammingSession" subfunction -- exactly as a normally-booted
        /// application expects (confirmed on real hardware: both succeed in sequence on a healthy
        /// ECU). True sends ONLY 0x85, skipping 0x89 entirely -- only
        /// <see cref="ConnectForRecoveryWrite"/> uses this. The reason: 0x89 throws immediately on a
        /// NAK, so on an ECU that rejects it, 0x85 never gets sent at all under the normal two-call
        /// sequence. A raw boot ROM whose entire purpose is flash programming is a
        /// plausible candidate to implement the STANDARD programming-session subfunction while not
        /// implementing this app's own extra application-layer pre-session gate
        /// </param>
        private void ConnectOnce(
            Variant variant, FlashSpeed speed, Func<bool>? isStopRequested = null,
            bool requireKw1281First = true, bool skipLoaderSpecificSession = false,
            Action<KW2000Dialog, VariantInfo>? uploadLoaderOverride = null)
        {
            _loaderMayBeRunning = false;
            _fastBaudActive = false;
            var info = VariantInfos[variant];

            int kwpVersion;
            if (requireKw1281First)
            {
                // Force 9600 baud before EVERY wakeup attempt. TrySetSpeed and the forced-10400 second
                // wakeup below both mutate this same interface's baud rate, and neither this method
                // nor Connect's retry loop ever set it back before trying again.
                _kwpCommon.Interface.SetBaudRate(9600);

                Log.WriteLine("Connecting to EDC15 (slow init)...");
                var kw1281 = new KW1281Dialog(_kwpCommon);

                Exception? primaryWakeupFailure = null;
                try
                {
                    // stopRetryingOnSyncByte: the moment the 9600 wakeup reads the soft-brick
                    // signature ($95/$B5), stop -- it's deterministic, so retrying it 3x (with a
                    // 1-second sleep each) just to fail the same way wastes ~3s before recovery is
                    // recognized. This makes the common soft-brick case switch to recovery almost
                    // immediately instead of after a full retry cycle.
                    kwpVersion = _kwpCommon.WakeUp(
                        0x01, evenParity: false, isStopRequested: isStopRequested,
                        stopRetryingOnSyncByte: IsSoftBrickSyncByte);
                    if (kwpVersion != 1281)
                    {
                        // A real response, just not the expected one, so this specific case throws immediately rather than falling
                        // into the general catch below.
                        throw new Edc15SoftBrickDetectedException(kwpVersion);
                    }
                }
                catch (Edc15SoftBrickDetectedException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The 9600 KW1281 wakeup didn't get a clean response at all, so before giving up, try ONE more thing: a fresh wakeup
                    // at 10400, KWP2000's own protocol-standard rate (the same rate
                    // ConnectForRecoveryWrite/SendKwp2000Raw use to talk to a stuck boot stub). If
                    // THAT gets a clean response reporting KWP2000 (version >= 2000), the ECU is
                    // confirmed soft-bricked, and switching to
                    // recovery mode is worth it. If the probe ALSO fails, this was a genuine
                    // communication problem
                    primaryWakeupFailure = ex;
                }

                if (primaryWakeupFailure != null)
                {
                    var syncByte = _kwpCommon.LastSyncByte;
                    if (!IsSoftBrickSyncByte(syncByte))
                    {
                        // An EDC15 in recovery mode answers the 9600 wakeup with a CONSISTENT $95/$B5
                        // sync byte. So don't switch
                        // to recovery on noise: rethrow and let Connect's retry loop simply try a
                        // fresh 9600 wakeup.
                        throw primaryWakeupFailure;
                    }

                    Log.WriteLine(
                        $"KW1281 wakeup at 9600 failed with the soft-brick sync byte ${syncByte:X2} " +
                        "(recovery-mode signature); confirming at 10400 and switching to recovery mode...");

                    int probeVersion;
                    try
                    {
                        _kwpCommon.Interface.SetBaudRate(10400);
                        probeVersion = _kwpCommon.WakeUp(
                            0x01, evenParity: false, failQuietly: true, isStopRequested: isStopRequested);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        throw primaryWakeupFailure;
                    }

                    if (probeVersion < 2000)
                    {
                        throw primaryWakeupFailure;
                    }

                    Log.WriteLine(
                        $"The ECU answered the 10400 probe in KWP2000 mode");
                    // Continue-in-place: the
                    // 10400 probe just above already woke the ECU in KWP2000 mode, which is exactly
                    // the state the normal second wakeup produces, so we proceed straight into the
                    // recovery session (0x85-only, Low speed) from here.
                    ContinueInKwp2000Session(info, FlashSpeed.Low, skipLoaderSpecificSession: true, uploadLoaderOverride);
                    return;
                }

                kw1281.Connect();
                kw1281.EndCommunication();

                Thread.Sleep(1000);
            }
            else
            {
                Log.WriteLine(
                    "Skipping the KW1281 handshake (recovery mode) -- going straight for a KWP2000 wakeup.");
            }

            // Unlike the first wakeup, the SECOND one (after EndCommunication, to flip into KWP2000
            // mode) DOES need to be forced to 10400 regardless of what the first one used
            _kwpCommon.Interface.SetBaudRate(10400);
            kwpVersion = _kwpCommon.WakeUp(0x01, evenParity: false, isStopRequested: isStopRequested);
            if (kwpVersion < 2000)
            {
                throw new InvalidOperationException(
                    $"Unable to wake up EDC15 in KW2000 mode. KW version: {kwpVersion}");
            }
            Log.WriteLine("Done!");

            ContinueInKwp2000Session(info, speed, skipLoaderSpecificSession, uploadLoaderOverride);
        }

        /// <summary>
        /// The shared "already awake in KWP2000 mode at the current baud" continuation reached by
        /// every <see cref="ConnectOnce"/> path: clears any stale loader bytes, opens the KW2000
        /// dialog, starts the diagnostic/programming session (0x89+0x85 normally, or 0x85 alone in
        /// recovery mode via <paramref name="skipLoaderSpecificSession"/>), requests security
        /// access, optionally negotiates a faster link speed (<see cref="TrySetSpeed"/>), and
        /// uploads the loader. Split out so the soft-brick probe in <see cref="ConnectOnce"/> can
        /// continue straight into it right after its own 10400 wakeup -- rather than throwing and
        /// forcing a whole fresh wakeup -- the "recognize recovery sooner without restarting init
        /// every time" optimization.
        /// </summary>
        private void ContinueInKwp2000Session(
            VariantInfo info, FlashSpeed speed, bool skipLoaderSpecificSession,
            Action<KW2000Dialog, VariantInfo>? uploadLoaderOverride)
        {
            // Cheap extra insurance against a previous, abandoned session's loader still having
            // something in flight: WakeUp() already clears the receive buffer once, right before
            // its own sync-byte read, but that's a couple of protocol steps before this, the first
            // real KWP2000 request of THIS session -- if a stale loader was still producing bytes
            // for a moment after that clear (see TryCloseEcu's doc comment for the real-hardware
            // case this fixes at the source), they'd otherwise still be sitting here waiting to be
            // misread as the response to this request.
            _kwpCommon.Interface.ClearReceiveBuffer();

            var kwp2000 = new KW2000Dialog(_kwpCommon, 0x01);
            // VerboseLog=false, matching Edc15VM.ReadWriteEeprom's own identical fix (see its doc
            // comment) -- this class never set it, leaving KW2000Dialog's default (true) active for
            // every startDiagnosticSession/security-access/loader-upload call below, an
            // inconsistency with every other user of this class. Routes SendMessage/ReceiveMessage's
            // routine per-field trace lines to LogDest.File instead of the screen; still fully
            // captured in the log file. Lower volume here than Edc15VM's EEPROM case.
            kwp2000.VerboseLog = false;

            if (skipLoaderSpecificSession)
            {
                Log.WriteLine(
                    "Skipping startDiagnosticSession(0x89) (recovery-write mode) -- trying the " +
                    "standard programming-session subfunction (0x85) alone...");
                _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x85 });
            }
            else
            {
                _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x89 });
                _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x85 });
            }

            Log.WriteLine("Requesting security access...");
            SeedAuth(kwp2000, info.Key);
            Log.WriteLine("Done!");

            // Negotiate a faster link speed BEFORE uploading the loader, not after
            TrySetSpeed(kwp2000, speed);

            Log.WriteLine("Sending flash loader...");
            // Set BEFORE calling SendLoader, not after -- once real bytes start going toward the
            // loader (even if the upload itself never finishes, e.g. a dropped transferData chunk),
            // CloseEcu's "tell the loader to stop" command becomes a reasonable recovery attempt on
            // a failure; before this point (still just session/security-access negotiation), the
            // ECU was never anywhere near running loader code, and sending CloseEcu's raw bytes at
            // it is just noise
            _loaderMayBeRunning = true;
            // uploadLoaderOverride lets a caller substitute a different RAM loader for this
            // session instead of the standard whole-chip-erase Loader-flash.bin -- currently
            // WriteFlashPerSectorChecksum does this (uploading the proven per-sector-erase loader
            // via Edc15VM.UploadAndStartSectorLoader) to drive per-sector checksum/erase/program.
            // Every whole-chip caller leaves this null and gets exactly the previous behavior.
            if (uploadLoaderOverride != null)
            {
                uploadLoaderOverride(kwp2000, info);
            }
            else
            {
                SendLoader(kwp2000, info);
            }
            Log.WriteLine("Loader running.");
        }

        /// <summary>
        /// Asks the ECU to switch to 124800 baud, and only switches this side's own UART rate to
        /// match if the ECU actually agrees -- called right after security access, before the
        /// loader upload, so (if it succeeds) the loader itself gets uploaded at the new speed
        /// instead of being uploaded slow and then having the link switched out from under it
        /// afterward.
        ///
        /// <para>This version is modeled directly on two independent, more trustworthy sources
        /// found while chasing that bug down: <c>TrySetSpeed</c>, which
        /// already implements exactly this pattern for EDC16 in this same codebase (send
        /// startDiagnosticSession with the existing subfunction plus an extra speed-selector
        /// parameter byte, switch local baud only on a positive response), and
        /// Reference/ecu-tool/KLINE/kline.cpp's <c>KLINE::startDiagSession</c>, a small, clean
        /// library implementation (used by the separate EDC15P_ReadFlash.ino/EDC15C2_ReadFlash.ino
        /// examples, distinct from the larger ECU_Flasher_UNO_EDC15.ino this class is otherwise
        /// ported from) that does the exact same thing: after the plain
        /// startDiagnosticSession(sub) succeeds, it sends startDiagnosticSession(sub, speedByte)
        /// and only calls Serial.begin(newSpeed) if the ECU responds positively (0x50), trying
        /// progressively slower speeds (125000, then 83500, 63500, 38400) if a faster one is
        /// refused, and simply staying at the current speed if every option is refused.</para>
        ///
        /// <para>Only tries 124800 (matching 0x87's meaning in both reference sources, and the only
        /// speed the rest of this class -- WriteByteDelayed's fixed delay, etc. -- was ever written
        /// and tuned against) rather than kline.cpp's full descending ladder; if the ECU refuses it,
        /// this just logs that and returns, leaving the caller at whatever baud it already had
        /// (matching kline.cpp's own "return 1 anyway" -- refusal isn't fatal, it just means no
        /// speed-up this time).</para>
        /// </summary>
        private void TrySetSpeed(KW2000Dialog kwp2000, FlashSpeed speed)
        {
            if (speed == FlashSpeed.Low)
            {
                // No negotiation at all -- stay at the 10400 baud the KWP2000 wakeup established.
                // This is the recovery-mode default and the safest choice.
                return;
            }

            // Speed byte + local baud per Reference/ecu-tool/KLINE/kline.cpp's startDiagSession
            // ladder, confirmed against tools/SnifferLogs/ (MPPS medium = 0x50, fast = 0x87). We
            // keep 124800 for High (the exact rate this class was always tuned to) rather than the
            // reference's nominal 125000, matching the previous behavior.
            var (speedByte, baud) = speed switch
            {
                FlashSpeed.Medium => ((byte)0x50, 38400),
                _ => ((byte)0x87, 124800),
            };

            try
            {
                Thread.Sleep(75);
                _ = kwp2000.SendReceive(DiagnosticService.startDiagnosticSession, new byte[] { 0x85, speedByte });
                Thread.Sleep(75);
                _kwpCommon.Interface.SetBaudRate(baud);
                // See WriteByteDelayed's own doc comment: the reference implementation
                // (Reference/ecu-tool/KLINE/kline.cpp's KLINE::write, around its `setspeed<2`
                // check) uses a per-byte delay of 13ms at the slow/negotiating speed but
                // effectively none (send_delay(0), ~3 microseconds) once a higher-speed session is
                // active -- this flag is this app's equivalent of that setspeed>=2 condition, and
                // is now set for BOTH Medium and High (any negotiated speed-up), not just 124800.
                _fastBaudActive = true;
                Log.WriteLine($"ECU agreed to {baud} baud.");
            }
            catch (Exception ex)
            {
                // Not fatal -- the ECU just doesn't want to go faster (or doesn't understand the
                // request), so stay at whatever baud Connect already has. Loader upload and the
                // bulk read/write loop both work fine slow; they're just slower.
                Log.WriteLine($"ECU declined {baud} baud ({ex.Message}); continuing at the current speed.");
            }
        }

        /// <summary>
        /// Tries each real variant's <see cref="Connect"/> in turn until one gets all the way
        /// through without throwing. Order is V, then P, then VM.
        ///
        /// Between attempts, TryCloseEcu tells whatever got uploaded (even a wrong-variant loader
        /// that never finished its handshake) to stop, for the same reason ReadFlash/WriteFlash
        /// now do this on failure -- see TryCloseEcu's doc comment. Without it, a failed attempt
        /// could leave a half-confused loader running that the next attempt's fresh wakeup then has
        /// to fight past instead of getting a clean slate. Only attempted if
        /// <see cref="_loaderMayBeRunning"/> -- i.e. the failed attempt actually got as far as
        /// sending the loader -- since CloseEcu's own bytes are meaningless (and, worse, just extra
        /// noise on an already-uncooperative line) against an ECU that never got anywhere near
        /// running loader code.
        /// </summary>
        private void ConnectAuto(FlashSpeed speed, Func<bool>? isStopRequested = null,
            Action<KW2000Dialog, VariantInfo>? uploadLoaderOverride = null)
        {
            var tryOrder = new[] { Variant.V, Variant.P, Variant.VM };
            for (var i = 0; i < tryOrder.Length; i++)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped before trying the next variant.");
                }

                var isLastAttempt = i == tryOrder.Length - 1;
                try
                {
                    ConnectCore(tryOrder[i], speed, isStopRequested, uploadLoaderOverride);
                    Log.WriteLine($"Auto-detected EDC15 variant: {tryOrder[i]}");
                    return;
                }
                catch (Exception ex) when (!isLastAttempt)
                {
                    Log.WriteLine($"Variant {tryOrder[i]} didn't work ({ex.Message}); trying next...");
                    if (_loaderMayBeRunning)
                    {
                        TryCloseEcu();
                    }
                }
            }
        }

        /// <summary>
        /// Connects for an actual read/write attempt starting from the same stuck-in-KWP2000-
        /// boot-stub state a soft-bricked EDC15 gets left in (e.g. after an interrupted/failed
        /// flash session) -- readident and
        /// loadedc15eeprom both fail immediately with "Expected KWP1281 protocol." on an ECU stuck
        /// this way, and this class's own ordinary <see cref="Connect"/> fails identically, since it
        /// also requires a genuine KW1281 wakeup reporting protocol 1281 before it will ever attempt
        /// the KWP2000 side at all): relaxed wakeup (no KW1281 prerequisite, see
        /// <see cref="ConnectOnce"/>'s <c>requireKw1281First</c> parameter) PLUS
        /// <see cref="ConnectOnce"/>'s <c>skipLoaderSpecificSession</c> (only the standard 0x85
        /// subfunction, not this app's own 0x89 gate -- see that parameter's own doc comment for the
        /// real-hardware reasoning: an ECU stuck this way NAK'd 0x89 outright, so the normal
        /// 0x89-then-0x85 sequence never even reaches 0x85). Used by
        /// <see cref="WriteFlashRecovery"/>/<see cref="ReadFlashRecovery"/>, this class's only
        /// callers of this combination.
        /// </summary>
        private void ConnectForRecoveryWrite(
            Variant variant, Func<bool>? isStopRequested = null,
            Action<KW2000Dialog, VariantInfo>? uploadLoaderOverride = null) =>
            ConnectOnce(
                variant, speed: FlashSpeed.Low, isStopRequested,
                requireKw1281First: false, skipLoaderSpecificSession: true,
                uploadLoaderOverride: uploadLoaderOverride);

        /// <summary>
        /// Last-resort recovery for a soft-bricked EDC15: actually redoes the write -- erase,
        /// program, the works -- starting from the stuck KWP2000 boot stub.
        /// </summary>
        public void WriteFlashRecovery(
            Variant variant, byte[] image, Action<string>? onStage = null, Action<int>? onPercent = null,
            Func<bool>? isStopRequested = null)
        {
            ConnectForRecoveryWrite(variant, isStopRequested);
            Log.WriteLine("Loader started from the KWP2000 boot stub -- writing flash...");
            WriteFlash(image, onStage, onPercent, isStopRequested);
        }

        /// <summary>
        /// Read counterpart to <see cref="WriteFlashRecovery"/> -- same relaxed/0x85-only connect,
        /// then an ordinary <see cref="ReadFlash"/>.
        /// </summary>
        public void ReadFlashRecovery(
            Variant variant, string filePath, Action<int>? onPercent = null, Func<bool>? isStopRequested = null)
        {
            ConnectForRecoveryWrite(variant, isStopRequested);
            Log.WriteLine("Loader started from the KWP2000 boot stub -- reading flash...");
            ReadFlash(filePath, onPercent, isStopRequested);
        }

        /// <summary>
        /// Sends exactly ONE arbitrary KWP2000 request (any service, any body bytes) after the same
        /// relaxed, KWP2000-only wakeup <see cref="ConnectForRecoveryWrite"/> uses (no KW1281
        /// prerequisite, no variant/key, no loader) -- but stops right after the wakeup, sending
        /// only the one request given rather than going on to a real read/write
        /// </summary>
        public void SendKwp2000Raw(byte service, byte[] body, Func<bool>? isStopRequested = null)
        {
            _kwpCommon.Interface.SetBaudRate(10400);
            Log.WriteLine("Waking up in KWP2000 mode (no KW1281 handshake, no loader)...");
            var kwpVersion = _kwpCommon.WakeUp(0x01, evenParity: false, isStopRequested: isStopRequested);
            if (kwpVersion < 2000)
            {
                throw new InvalidOperationException(
                    $"Unable to wake up EDC15 in KW2000 mode. KW version: {kwpVersion}");
            }
            Log.WriteLine("Done!");
            _kwpCommon.Interface.ClearReceiveBuffer();

            var kwp2000 = new KW2000Dialog(_kwpCommon, 0x01);

            Log.WriteLine(
                $"Sending service 0x{service:X2} with body [{string.Join(" ", body.Select(b => $"0x{b:X2}"))}]...");
            var response = kwp2000.SendReceive((DiagnosticService)service, body);
            Log.WriteLine(
                $"Accepted. Response body: [{string.Join(" ", response.Body.Select(b => $"0x{b:X2}"))}]");
        }

        /// <summary>
        /// Reads the full external flash (0x00000-0x7FEE2, ~512KB) into <paramref name="filePath"/>,
        /// verifying a checksum on every packet. Call <see cref="Connect"/> first.
        /// </summary>
        /// <param name="isStopRequested">
        /// Checked once per packet (a read is ~2000 packets total), between
        /// <see cref="SendAndReadFlashPacket"/> calls.
        /// </param>
        public void ReadFlash(
            string filePath, Action<int>? onPercent = null, Func<bool>? isStopRequested = null)
        {
            using var fs = File.Create(filePath);

            long readStart = 0;
            bool readDone = false;
            bool finished = false;
            int before = -1;

            var req = new byte[7];
            req[0] = 0x05;
            req[1] = 0x23;
            req[5] = 0xFE;

            // The whole loop runs inside try/finally so a failed/aborted read still gets a
            // best-effort CloseEcu()
            try
            {
                while (!finished)
                {
                    if (isStopRequested?.Invoke() == true)
                    {
                        throw new OperationCanceledException("Stopped during flash read.");
                    }

                    req[2] = (byte)(((readStart >> 16) & 0xFF) + 0x08);
                    req[3] = (byte)((readStart & 0xFFFF) >> 8);
                    req[4] = (byte)(readStart & 0xFF);
                    req[6] = Checksum(req, 6);
                    SendAndReadFlashPacket(req, fs);

                    if (readDone)
                    {
                        req[3] = 0xFF;
                        req[4] = 0xE0;
                        req[5] = 0x20;
                        req[6] = Checksum(req, 6);
                        SendAndReadFlashPacket(req, fs);
                        finished = true;
                    }

                    readStart += 0xFE;
                    if (readStart == FlashEnd)
                    {
                        readDone = true;
                    }

                    var percent = (int)(readStart * 100 / FlashEnd);
                    if (percent != before)
                    {
                        onPercent?.Invoke(Math.Min(percent, 100));
                        before = percent;
                    }
                }
            }
            finally
            {
                TryCloseEcu();
            }
        }

        /// <summary>
        /// Erases and writes the full external flash from <paramref name="image"/> (must be at
        /// least 0x80000 bytes -- boot block 0x7C000-0x7FFFF is written first, then the main block
        /// 0x00000-0x7BFFF, matching the reference tool exactly). Call <see cref="Connect"/> first.
        ///
        /// <para><b>Erase is whole-chip:</b> the single <c>0x02 0xA5 0x0F 0xB6</c>
        /// command below is everything the RAM loader this class uploads (Loader-flash.bin,
        /// ported from the public Reference/ecu-tool/ECU_Flasher_UNO_EDC15.ino, per this class's own
        /// header doc comment) exposes for erasing -- that reference's own WriteEDC15Flash() does
        /// the exact same single whole-chip erase, with no sector-selective variant anywhere in it.
        /// So unlike Edc15BootModeVM's boot-mode write path (a genuinely different program talking
        /// almost directly to the flash chip, confirmed via Reference/Logs/Write.log to support real
        /// per-sector erase against all 11 of the Am29F400BT's sectors), this loader has no erase
        /// primitive finer than "everything" to build a true skip-already-matching-sector feature
        /// on -- there's no sector to selectively skip erasing.</para>
        ///
        /// <para><b>What IS safe here, and what <see cref="skipBlankChunks"/> does:</b>
        /// NOR flash
        /// programming can only clear bits (1-&gt;0), never set them, so writing an all-0xFF chunk is
        /// always a no-op regardless of what's already there. Applying that same skip here, after
        /// this loader's own whole-chip erase, is exactly as safe and needs no new erase primitive --
        /// it just skips chunks that would be pure busywork either way.</para>
        /// </summary>
        /// <param name="isStopRequested">
        /// Passed through to <see cref="WriteFlashBlock"/>, which checks it once per chunk (~250
        /// bytes each). NOT checked around the erase command/its 4-second wait just above -- that's
        /// a single request-and-wait, not a loop, so there's no coherent mid-erase checkpoint to
        /// add.
        /// </param>
        /// <param name="skipBlankChunks">
        /// See <see cref="WriteFlashBlock"/>'s own doc comment on the same-named parameter. Defaults
        /// to true (skip); pass false for a "Force Full Write" override that writes every single
        /// chunk regardless of content, for anyone who'd rather not rely on this optimization for a
        /// specific write.
        /// </param>
        public void WriteFlash(
            byte[] image, Action<string>? onStage = null, Action<int>? onPercent = null,
            Func<bool>? isStopRequested = null, bool skipBlankChunks = true)
        {
            if (image.Length < 0x80000)
            {
                throw new ArgumentException(
                    $"EDC15 flash image must be at least 0x80000 (524288) bytes; got {image.Length}.",
                    nameof(image));
            }

            // See ReadFlash's identical try/finally for why this matters -- a failed write, just
            // like a failed read, should still tell the loader to stop rather than abandoning it
            // mid-protocol for the next attempt to run into.
            try
            {
                onStage?.Invoke("Erasing...");
                var eraseCmd = new byte[] { 0x02, 0xA5, 0x0F, 0xB6 };
                WriteRaw(eraseCmd);
                CheckAck();
                Thread.Sleep(4000); // Wait for the flash erase to complete.
                CheckAck();
                onStage?.Invoke("Erase done.");

                onStage?.Invoke("Writing boot block...");
                // Extra pacing between chunks, boot-block-only, fast-baud-only -- see
                // BootBlockFastModeInterChunkDelayMs's own doc comment for why. Slow mode already
                // gets natural per-byte pacing from WriteByteDelayed, so there's nothing to add
                // there -- this is specifically restoring some of the margin fast mode gave up.
                WriteFlashBlock(
                    image, WriteBootStart, WriteBootEnd, onPercent, isStopRequested,
                    interChunkDelayMs: _fastBaudActive ? BootBlockFastModeInterChunkDelayMs : 0,
                    skipBlankChunks: skipBlankChunks);

                onStage?.Invoke("Writing main block...");
                WriteFlashBlock(
                    image, WriteMainStart, WriteMainEnd, onPercent, isStopRequested,
                    skipBlankChunks: skipBlankChunks);
            }
            finally
            {
                TryCloseEcu();
            }
            onStage?.Invoke("Done.");
        }

        /// <summary>
        /// Per-sector checksum-driven write and the default path for a real recalibration: connects
        /// via <see cref="Connect"/> (inheriting its recovery detection, cold-power-up retry, and
        /// speed negotiation) but uploads the proven sector-erase loader instead of the whole-chip
        /// one (through <see cref="Connect"/>'s uploadLoaderOverride), then drives
        /// <see cref="Edc15VM.WritePerSectorToRunningLoader"/>: for each sector, compares the loader's
        /// CmdB2 checksum of the ECU against <see cref="Edc15VM.ComputeSectorChecksum"/> of the image
        /// and erases+writes only the differing sectors (or all, if <paramref name="forceFull"/>),
        /// optionally re-verifying by checksum afterward. Relies on the CmdB2 loader command -- see
        /// docs/Loader-sector-erase.a66.
        /// </summary>
        public void WriteFlashPerSectorChecksum(
            byte[] image, FlashSpeed speed, bool verify, bool forceFull,
            Action<string>? onStage = null, Action<int>? onPercent = null,
            Func<bool>? isStopRequested = null)
        {
            ConnectCore(
                Variant.V, speed, isStopRequested,
                uploadLoaderOverride: (kwp2000, info) => Edc15VM.UploadAndStartSectorLoader(kwp2000));
            onStage?.Invoke("Sector-erase loader running -- starting per-sector write...");
            var edc15 = new Edc15VM(_kwpCommon, 0x01);
            edc15.WritePerSectorToRunningLoader(image, verify, forceFull, onStage, onPercent, isStopRequested);
        }

        // ---- Session setup ----

        /// <summary>
        /// SecurityAccess (mode 0x41), mirroring <see cref="Edc15VM.ReadWriteEeprom"/>'s own
        /// handshake exactly. A 2-zero-byte seed
        /// means "already unlocked" (send an empty key back); a real 4-byte seed gets run through
        /// the variant's own key.
        /// </summary>
        private static void SeedAuth(KW2000Dialog kwp2000, long key)
        {
            const byte accessMode = 0x41;
            var seedResponse = kwp2000.SendReceive(DiagnosticService.securityAccess, new[] { accessMode });

            var keyMessage = new List<byte> { (byte)(accessMode + 1) };
            if (!seedResponse.Body.SequenceEqual(new byte[] { accessMode, 0x00, 0x00 }))
            {
                var seedBytes = seedResponse.Body.Skip(1).Take(4).ToArray();
                var keyBytes = Edc15KeyAlgorithms.ComputeLvl41Key(key, 0x3800000, seedBytes);
                keyMessage.AddRange(keyBytes);
            }
            _ = kwp2000.SendReceive(DiagnosticService.securityAccess, keyMessage.ToArray());
        }

        /// <summary>
        /// Uploads the RAM loader via requestDownload/transferData (both "excludeAddresses" short
        /// form -- same convention <see cref="Edc15VM.ReadWriteEeprom"/> already uses successfully
        /// for its own loader), then the per-variant post-loader handshake. The requestDownload
        /// params, post-loader config bytes, and per-variant handshake tables are unchanged from
        /// the original byte-for-byte port (see class doc comment) -- only HOW they're sent/verified
        /// changed (via KW2000Dialog now, which also gets the automatic resend-on-no-response retry
        /// -- see KW2000Dialog.SendReceive).
        /// </summary>
        private void SendLoader(KW2000Dialog kwp2000, VariantInfo info)
        {
            Thread.Sleep(25);
            var downloadResponse = kwp2000.SendReceive(
                DiagnosticService.requestDownload,
                new byte[] { 0x40, 0xE0, 0x00, 0x00, 0x00, 0x04, 0x20 }, // load addr 0x40E000, len 0x000420
                excludeAddresses: true);
            DelayLoaderStep();

            var assembly = typeof(Edc15FlashVM).Assembly;
            using var resourceStream = assembly.GetManifestResourceStream(
                "BitFab.KW1281Test.EDC15.Loader-flash.bin");
            if (resourceStream == null)
            {
                throw new InvalidOperationException(
                    "Unable to load BitFab.KW1281Test.EDC15.Loader-flash.bin embedded resource.");
            }
            var loader = new byte[resourceStream.Length];
            resourceStream.ReadExactly(loader, 0, loader.Length);
            if (loader.Length != 1016)
            {
                throw new InvalidOperationException(
                    $"Loader-flash.bin is {loader.Length} bytes, expected 1016.");
            }

            // Chunk size comes from the ECU's own requestDownload response, not a hardcoded
            // constant -- matching Edc15VM.ReadWriteEeprom's "var maxBlockLen = resp.Body[0];"
            // (the proven EEPROM loader-upload path), rather than always sending 254-byte chunks
            // regardless of what this specific ECU/variant actually said it can accept. The
            // response's single body byte is the maximum total transferData message length
            // (service byte + data together, per ISO14230's maxNumberOfBlockLength convention) --
            // one less than that is the actual data payload capacity per chunk. On the hardware
            // this was captured against, that value is 0xFF (255), giving the same 254-byte chunks
            // this used to hardcode -- so no observed behavior change for that ECU -- but a
            // different variant or ECU revision reporting a SMALLER value here would previously
            // have silently had 254-byte chunks pushed at it regardless, exceeding what it asked
            // for. That's a real candidate explanation for failures that show up later, at the
            // post-loader handshake, looking unrelated to the upload itself (a byte-for-byte
            // mismatch, not an upload error) -- if the ECU truncates or otherwise mishandles an
            // over-limit chunk without the transferData ACK itself reflecting that, the loader
            // that ends up running in RAM would be subtly corrupt despite every individual chunk
            // having been ACK'd. Falls back to 0xFF (matching the previous hardcoded behavior) if
            // the response is somehow empty, rather than risking a divide-by-zero-shaped chunk size.
            var maxBlockLen = downloadResponse.Body.Count > 0 ? downloadResponse.Body[0] : (byte)0xFF;
            var dataChunkSize = Math.Max(1, maxBlockLen - 1);
            for (var offset = 0; offset < loader.Length; offset += dataChunkSize)
            {
                var chunk = loader.Skip(offset).Take(dataChunkSize).ToArray();
                _ = kwp2000.SendReceive(DiagnosticService.transferData, chunk, excludeAddresses: true);
                DelayLoaderStep();
            }

            // Post-loader config bytes (undocumented) -- an 8-byte transferData payload the loader
            // apparently expects once it's fully uploaded, before the per-variant handshake below.
            _ = kwp2000.SendReceive(
                DiagnosticService.transferData,
                new byte[] { 0xC3, 0xC3, 0x89, 0xC5, 0x45, 0xDA, 0x63, 0x0B },
                excludeAddresses: true);
            DelayLoaderStep();

            // Per-variant handshake: raw send/verify against the variant's own byte tables, NOT
            // KW2000Dialog.SendReceive -- these responses include an EXPECTED negative response
            // (startRoutineByLocalIdentifier NAK'd with conditionsNotCorrect, body 0x31/0x22) that
            // Edc15VM.ReadWriteEeprom's own loader-execute step also gets and just ignores
            // (SendMessage+ReceiveMessage raw, not SendReceive, since SendReceive would throw on a
            // NAK). Uses a fresh 0-based position into the variant's own arrays.
            var vm = 0;
            var vw = 0;
            if (info.ArduBytes.Length == 9)
            {
                // V: a single 9/4 exchange.
                SendFromArray(info.ArduBytes, ref vm, 9);
                VerifyFromArray(info.EcuBytes, ref vw, 4);
            }
            else
            {
                // P and VM+: 9/4, then two 6/18 exchanges, then a final 9/2.
                SendFromArray(info.ArduBytes, ref vm, 9);
                VerifyFromArray(info.EcuBytes, ref vw, 4);
                SendFromArray(info.ArduBytes, ref vm, 6);
                VerifyFromArray(info.EcuBytes, ref vw, 18);
                SendFromArray(info.ArduBytes, ref vm, 6);
                VerifyFromArray(info.EcuBytes, ref vw, 18);
                SendFromArray(info.ArduBytes, ref vm, 9);
                VerifyFromArray(info.EcuBytes, ref vw, 2);
            }
            // vm/vw now equal info.ArduBytes.Length/info.EcuBytes.Length exactly for every variant.
        }

        private static void DelayLoaderStep() => Thread.Sleep(5); // ~4510us in the reference; rounded up.

        /// <summary>
        /// True if <paramref name="sync"/> is the CONSISTENT KW1281-wakeup sync byte a soft-bricked
        /// (recovery-mode) EDC15 answers with at 9600 baud. Confirmed on real hardware: two
        /// back-to-back Read-Identity cycles with the ECU deliberately left in recovery mode both
        /// produced "Unexpected sync byte: Expected $55, Actual $95" / "$B5" (Reference/Logs/KWHack.log).
        /// A healthy ECU answers $55; any OTHER value (or a read timeout, which leaves LastSyncByte at
        /// -1) is treated as ordinary connect/disconnect/power noise, NOT a soft-brick.
        /// </summary>
        private static bool IsSoftBrickSyncByte(int sync) => sync == 0x95 || sync == 0xB5;

        // ---- Per-sector flash geometry (used by the per-sector checksum write path) ----

        /// <summary>
        /// AMD Am29F400BT sector boundaries (flash-relative, 0x00000-based -- same origin as
        /// <see cref="WriteMainStart"/>/<see cref="WriteBootStart"/>), top-boot layout: seven
        /// 64KB sectors, then 32KB, 8KB, 8KB, and a final 16KB sector (matching
        /// <see cref="WriteBootStart"/>/<see cref="WriteBootEnd"/> exactly).
        /// </summary>
        internal static readonly (long Start, long End)[] FlashSectors =
        {
            (0x00000, 0x10000), (0x10000, 0x20000), (0x20000, 0x30000), (0x30000, 0x40000),
            (0x40000, 0x50000), (0x50000, 0x60000), (0x60000, 0x70000),
            (0x70000, 0x78000), (0x78000, 0x7A000), (0x7A000, 0x7C000), (0x7C000, 0x80000),
        };

        /// <summary>
        /// Fixed offset added to a flash-relative address (0x00000-0x7FFFF, same range
        /// <see cref="FlashSectors"/> uses) to get the CPU-bus address the per-sector loader's
        /// erase/checksum/write commands expect -- e.g. flash-relative sector 0x10000-0x20000 is
        /// addressed as 0x210000-0x220000. Almost certainly where this ECU's external bus/chip-select maps
        /// the flash chip for byte-addressed access -- a different, larger address space than the
        /// 0x40E000 external RAM <see cref="SendLoader"/> uploads loader CODE to (that's a CPU RAM
        /// window for executable code; this is the memory-mapped view of the flash chip itself).
        /// </summary>
        internal const long SectorFlashCpuBase = 0x200000;

        // ---- Flash read/write helpers ----

        /// <summary>
        /// Sends one flash-read request packet and reads back its response, retrying the whole
        /// request/response round trip (not just the read) up to <paramref name="maxAttempts"/>
        /// times on a protocol-level failure before giving up for real.
        /// </summary>
        private void SendAndReadFlashPacket(byte[] req, Stream destination, int maxAttempts = 10)
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                WriteRaw(req);
                try
                {
                    // File-only on every attempt except the last -- see ReadFlashDataPacket's
                    // unexpectedNakLogDest doc comment (matches CheckRec's identical fix on the
                    // write side, for the same real-hardware-reported bug).
                    ReadFlashDataPacket(destination, attempt < maxAttempts ? LogDest.File : LogDest.All);
                    return;
                }
                catch (InvalidOperationException ex) when (attempt < maxAttempts)
                {
                    // Async, file-only -- see WriteRaw's/CheckRec's doc comments and
                    // ILog.WriteFileOnly's own doc comment for why even LogDest.File alone
                    // wasn't safe enough here (real-hardware evidence: a noisy connection can hit
                    // this on a large fraction of ~2000+ packets).
                    Log.WriteFileOnly(
                        $"  Flash read packet attempt {attempt}/{maxAttempts} failed ({ex.Message}); " +
                        "clearing the receive buffer and resending the same request...\n");

                    // A failed ReadFlashDataPacket call throws as soon as it sees something it
                    // doesn't recognize, which can leave the rest of that malformed response
                    // (whatever it turns out to be) still sitting unread. Without clearing it out
                    // first, the retry's own response would get prepended by those stale bytes and
                    // almost certainly fail too -- turning one genuine glitch into a guaranteed
                    // cascade of failures instead of a clean second attempt.
                    _kwpCommon.Interface.ClearReceiveBuffer();
                }
            }
        }

        /// <summary>
        /// Reads one transferData-echoed flash-data response (matches the request WriteRaw just
        /// sent in ReadFlash's loop). Tolerates a "response pending" NAK the same way
        /// Edc16FlashVM.VerifyExpected does for its own handshake reads.
        /// </summary>
        /// <param name="destination">Where the packet's data bytes get written.</param>
        /// <param name="unexpectedNakLogDest">
        /// Where the "RX: ... &lt;- unexpected NAK" line goes when the NAK isn't the tolerated
        /// "response pending" shape -- right before this method throws. Same reasoning as
        /// CheckRec's mismatchLogDest: SendAndReadFlashPacket's caller should pass LogDest.File on
        /// every attempt except the last, or this line defeats that loop's own retry-suppression.
        /// The "response pending" line (not a failure -- the loop just keeps waiting) always stays
        /// visible regardless of this parameter.
        /// </param>
        private void ReadFlashDataPacket(
            Stream destination, LogDest unexpectedNakLogDest = LogDest.All)
        {
            while (true)
            {
                var lenByte = _kwpCommon.ReadByte();
                var serviceByte = _kwpCommon.ReadByte();

                // Matches the reference tool's CheckRec quirk (see Edc15FlashVM.CheckRec's own doc
                // comment for the full story): a stray leading 0x00 exactly where the service byte
                // is expected is spurious noise, not a real response, and gets silently skipped in
                // favor of the next byte.
                if (serviceByte == 0x00)
                {
                    // Async, file-only -- see WriteRaw's/CheckRec's doc comments and
                    // ILog.WriteFileOnly's own doc comment for why. This is a known,
                    // already-tolerated quirk, not a failure.
                    Log.WriteFileOnly(
                        "  RX: 00 (spurious leading byte, expected the service byte -- skipping it)\n");
                    serviceByte = _kwpCommon.ReadByte();
                }

                if (serviceByte == 0x7F)
                {
                    var nakBodyLen = Math.Max(0, lenByte - 1);
                    var nakBody = new byte[nakBodyLen];
                    for (var i = 0; i < nakBodyLen; i++)
                    {
                        nakBody[i] = _kwpCommon.ReadByte();
                    }
                    var nakChecksum = _kwpCommon.ReadByte();
                    var isPending = nakBodyLen >= 2 && nakBody[1] == 0x78;
                    if (isPending)
                    {
                        Log.WriteLine(
                            $"  RX: {lenByte:X2} 7F {Utils.DumpBytes(nakBody)} + checksum " +
                            $"0x{nakChecksum:X2} (response pending -- waiting for the real flash data)");
                        continue;
                    }
                    Log.WriteLine(
                        $"  RX: {lenByte:X2} 7F {Utils.DumpBytes(nakBody)} + checksum 0x{nakChecksum:X2} " +
                        "<- unexpected NAK",
                        unexpectedNakLogDest);
                    throw new InvalidOperationException(
                        $"EDC15 flash read: unexpected NAK ({Utils.DumpBytes(nakBody)}).");
                }

                if (serviceByte != 0x36)
                {
                    throw new InvalidOperationException(
                        $"Expected transferData (0x36) echo but got 0x{serviceByte:X2}.");
                }

                // Batched via KwpCommon.ReadBytes (one native call instead of dataLen individual
                // ReadByte calls -- up to ~250 per packet, ~2000 packets per read) rather than
                // KwpCommon.WriteBytes's sub-batched pacing -- see ReadBytes' own doc comment for
                // why there's no backpressure concern to preserve here: this is a pure passive drain
                // of data the ECU already decided to send (Loader.a66's E23E collects the whole
                // packet internally before transmitting any of it), so nothing this app does on the
                // read side can get ahead of or outrun the ECU's own transmission pace.
                var dataLen = lenByte - 1;
                var data = new byte[dataLen];
                _kwpCommon.ReadBytes(data, dataLen);

                var checksumByte = _kwpCommon.ReadByte();
                var expected = (byte)(Checksum(new[] { lenByte, serviceByte }, 2) + Checksum(data, dataLen));
                if (checksumByte != expected)
                {
                    throw new InvalidOperationException(
                        $"EDC15 flash read: checksum mismatch (got 0x{checksumByte:X2}, expected 0x{expected:X2}).");
                }

                destination.Write(data, 0, dataLen);
                return;
            }
        }

        /// <param name="interChunkDelayMs">
        /// Extra <c>Thread.Sleep</c> after every chunk's ACK is confirmed, before moving
        /// on to the next one -- 0 (the default) for the normal, unthrottled path. See
        /// <see cref="BootBlockFastModeInterChunkDelayMs"/>'s own doc comment for why
        /// <see cref="WriteFlash"/> passes a non-zero value here specifically for the boot block in
        /// fast mode. Not checked against <paramref name="isStopRequested"/> mid-sleep -- at up to
        /// 20ms this adds negligible latency to Stop's responsiveness (the loop's own
        /// isStopRequested check, right above, still runs once per chunk as always).
        /// </param>
        /// <param name="skipBlankChunks">
        /// If true (the default), a chunk whose SOURCE bytes (i.e. what's about to be written, not
        /// what's currently in the flash) are entirely 0xFF is not sent at all -- see
        /// <see cref="WriteFlash"/>'s own doc comment for the real-hardware precedent this is based
        /// on. Writing 0xFF to NOR flash is always a no-op regardless of the byte already there
        /// (programming can only clear bits, 1-&gt;0; a freshly-erased chip already reads all-0xFF
        /// everywhere), so skipping is safe purely from how NOR flash works, independent of whether
        /// this specific loader's erase happens to be whole-chip (it is -- see <see cref="WriteFlash"/>).
        /// </param>
        private void WriteFlashBlock(
            byte[] image, long blockStart, long blockEnd, Action<int>? onPercent,
            Func<bool>? isStopRequested = null, int interChunkDelayMs = 0, bool skipBlankChunks = true)
        {
            var pos = blockStart;
            int before = -1;
            while (blockEnd >= pos)
            {
                if (isStopRequested?.Invoke() == true)
                {
                    throw new OperationCanceledException("Stopped during flash write.");
                }

                var stringCount = 0;
                var chunkStart = pos;
                while (blockEnd >= pos && stringCount <= 0xF9)
                {
                    pos++;
                    stringCount++;
                }

                var data = new byte[stringCount];
                Array.Copy(image, chunkStart, data, 0, stringCount);

                if (skipBlankChunks && Array.TrueForAll(data, b => b == 0xFF))
                {
                    Log.WriteFileOnly(
                        $"  Chunk at 0x{chunkStart:X} is entirely 0xFF -- skipping (nothing to write).\n");
                }
                else
                {
                    var header = new byte[5];
                    header[0] = (byte)(stringCount + 4);
                    header[1] = 0x36;
                    header[2] = (byte)(((chunkStart >> 16) & 0xFF) + 0x20);
                    header[3] = (byte)((chunkStart & 0xFFFF) >> 8);
                    header[4] = (byte)(chunkStart & 0xFF);
                    var checksum = (byte)(Checksum(header, 5) + Checksum(data, stringCount));

                    SendFlashWriteChunk(header, data, checksum);

                    if (interChunkDelayMs > 0)
                    {
                        Thread.Sleep(interChunkDelayMs);
                    }
                }

                if (blockEnd == WriteMainEnd)
                {
                    var percent = (int)(pos * 100 / blockEnd);
                    if (percent != before)
                    {
                        onPercent?.Invoke(percent);
                        before = percent;
                    }
                }
            }
        }

        /// <summary>
        /// Sends one flash-write chunk (header + data + checksum) and verifies the loader's ACK,
        /// retrying the whole chunk (not just the ACK read) up to <paramref name="maxAttempts"/>
        /// times on failure before giving up for real -- the write-side counterpart to
        /// <see cref="SendAndReadFlashPacket"/>'s identical retry.
        ///
        /// <para>Resending the identical header+data+checksum is safe either way a single glitch
        /// could have happened: if the loader actually received and wrote this chunk correctly and
        /// only the ACK response back to this app got corrupted in transit, resending writes the
        /// same bytes to the same address again -- a no-op, not a double-write, since flash writes
        /// to an address aren't cumulative. If the loader's checksum check rejected the chunk (or
        /// never got it cleanly), it's still expecting this exact chunk, so resending is exactly
        /// what it needs. Either way, nothing about the loader's own internal state (its target
        /// address is embedded in the chunk header, not an incrementing counter this app has to
        /// stay in sync with) depends on this being the chunk's first attempt.</para>
        ///
        /// <para>Unlike the read side, this can't safely retry EVERY exception -- a deliberate Stop
        /// (<see cref="OperationCanceledException"/>) must propagate immediately, not get treated
        /// as "try again," so the retry guard explicitly excludes it.</para>
        /// </summary>
        private void SendFlashWriteChunk(byte[] header, byte[] data, byte checksum, int maxAttempts = 10)
        {
            var chunk = new byte[header.Length + data.Length + 1];
            Array.Copy(header, 0, chunk, 0, header.Length);
            Array.Copy(data, 0, chunk, header.Length, data.Length);
            chunk[^1] = checksum;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _kwpCommon.WriteBytes(chunk);

                    // File-only on every attempt except the last -- matches this loop's own
                    // "attempt N/M failed" line below. Without this, CheckAck's mismatch line would
                    // still show up visibly on a benign, about-to-be-retried first failure even
                    // though the summary line right below it is correctly hidden -- see CheckRec's
                    // mismatchLogDest doc comment for the real-hardware report that caught this.
                    CheckAck(attempt < maxAttempts ? LogDest.File : LogDest.All);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
                {
                    // Async, file-only -- see SendAndReadFlashPacket's identical change (read side)
                    // and ILog.WriteFileOnly's own doc comment for why even LogDest.File alone
                    // wasn't safe enough here.
                    Log.WriteFileOnly(
                        $"  Flash write chunk attempt {attempt}/{maxAttempts} failed ({ex.Message}); " +
                        "clearing the receive buffer and resending the same chunk...\n");

                    // Same reasoning as SendAndReadFlashPacket's identical clear: a failed CheckAck
                    // can leave the rest of a malformed response still sitting unread, and without
                    // clearing it out first, the retry's own ACK read would get prepended by those
                    // stale bytes and almost certainly fail too.
                    _kwpCommon.Interface.ClearReceiveBuffer();
                }
            }
        }

        // ---- Low-level protocol primitives (mirror the reference's iso_* helpers) ----

        /// <summary>Writes len sequential bytes from source[pos..), then a freshly computed
        /// checksum of just those bytes, and advances pos by len. Matches iso_sendstring(leng).</summary>
        private void SendFromArray(byte[] source, ref int pos, int len)
        {
            var slice = new byte[len];
            Array.Copy(source, pos, slice, 0, len);
            foreach (var b in slice)
            {
                WriteByteDelayed(b);
            }
            WriteByteDelayed(Checksum(slice, len));
            pos += len;
        }

        /// <summary>
        /// Writes one byte and pauses briefly afterward.
        ///
        /// <para>The reference implementation (Reference/ecu-tool/KLINE/kline.cpp's KLINE::write)
        /// does NOT use one fixed delay for this -- it explicitly branches on link speed: 13ms
        /// between bytes at the slow/negotiating speed (<c>setspeed&lt;2</c>), but effectively
        /// none (<c>send_delay(0)</c>, ~3 microseconds via delayMicroseconds) once a high-speed
        /// session is active. This app previously used a single flat 5ms delay unconditionally,
        /// which happened to be safe (5ms is within the 5-20ms the reference itself uses at the
        /// slow speed) but never got faster even once <see cref="TrySetSpeed"/> had already
        /// negotiated 124800 baud -- and WriteFlash writes roughly 512,000 bytes through this exact
        /// method, so 5ms/byte alone is ~43 minutes of pure artificial delay, matching a real
        /// write taking 30-60+ minutes to reach 32%. <see cref="_fastBaudActive"/> mirrors the
        /// reference's own setspeed>=2 condition: once set, this skips the Thread.Sleep entirely
        /// (matching the reference's ~3-microsecond gap, far below what Thread.Sleep can even
        /// resolve -- there's nothing meaningful to sleep for) and relies on
        /// <see cref="BitFab.KW1281Test.KwpCommon.WriteByte"/>'s own write-then-read-echo round
        /// trip, which is itself bounded by the actual UART timing at whatever baud is active, to
        /// provide real inter-byte spacing instead of an arbitrary extra wait.</para>
        /// </summary>
        private void WriteByteDelayed(byte b)
        {
            _kwpCommon.WriteByte(b);
            if (!_fastBaudActive)
            {
                Thread.Sleep(5);
            }
        }

        /// <summary>Reads and verifies len sequential bytes against expected[pos..), then a
        /// trailing checksum byte, and advances pos by len. Matches iso_readstring(leng). Only
        /// ever called during the handshake/setup phase (Connect, SecurityAccess, loader upload)
        /// -- never from the bulk flash-read/write loops (see ReadFlashDataPacket/WriteFlashBlock,
        /// which use _kwpCommon.ReadByte/WriteByteDelayed directly) -- so logging every byte here
        /// is safe and won't flood the log during an actual 512KB transfer.
        ///
        /// <para>On a mismatch this still throws (this gate gets crossed right before ReadFlash and
        /// WriteFlash trust the just-uploaded loader to do real flash I/O -- weakening it isn't a
        /// call to make unilaterally, see the caller's own risk notes), but it now reads and logs
        /// the ENTIRE <paramref name="len"/>-byte block plus checksum before throwing, rather than
        /// stopping at the very first differing byte. A real-hardware capture (VM variant) showed
        /// this stopping after exactly one byte of a 16-byte block ($DB expected vs $C3 received,
        /// nothing else observed) -- not enough to tell whether the rest of the block also differs
        /// (pointing at genuinely different ECU/loader-readback content than the reference tool's
        /// hardcoded expectation) or matches (pointing at something narrower, like a single
        /// don't-care status byte). This makes that distinguishable on the next capture without
        /// changing what ultimately happens on a mismatch.</para></summary>
        private void VerifyFromArray(byte[] expected, ref int pos, int len)
        {
            var slice = new byte[len];
            var mismatches = new List<int>();
            for (var i = 0; i < len; i++)
            {
                var b = _kwpCommon.ReadByte();
                slice[i] = b;
                if (b != expected[pos + i])
                {
                    mismatches.Add(i);
                }
            }
            var checksumByte = _kwpCommon.ReadByte();
            // Checksum of the bytes actually received, not `expected` -- still a meaningful framing
            // check (does this look like a real, intact 17-byte KWP2000-shaped frame the ECU
            // actually sent?) even when its content doesn't match what we expected.
            var receivedChecksum = Checksum(slice, len);

            if (mismatches.Count == 0)
            {
                Log.WriteLine($"  RX: {Utils.DumpBytes(slice)} + checksum 0x{checksumByte:X2}");
            }
            else
            {
                // `pos` is a ref parameter, so it can't be captured by the lambda below directly --
                // stash a plain local copy first.
                var basePos = pos;
                var detail = string.Join(", ",
                    mismatches.Select(i =>
                        $"pos {basePos + i}: expected 0x{expected[basePos + i]:X2}, got 0x{slice[i]:X2}"));
                Log.WriteLine(
                    $"  RX: {Utils.DumpBytes(slice)} + checksum 0x{checksumByte:X2} " +
                    $"<- {mismatches.Count} mismatch(es): {detail}");
                throw new InvalidOperationException(
                    $"EDC15 handshake mismatch ({mismatches.Count} byte(s) differ): {detail}");
            }

            if (checksumByte != receivedChecksum)
            {
                throw new InvalidOperationException(
                    $"EDC15 handshake checksum mismatch: expected 0x{receivedChecksum:X2}, got 0x{checksumByte:X2}.");
            }
            pos += len;
        }

        /// <summary>Reads and verifies a single expected byte. Matches CheckRec(byte) -- INCLUDING
        /// a quirk in the reference's version that this port originally missed: `if (b==0 &&
        /// p!=0) iso_read_byte();` -- if a non-zero byte is expected but a stray 0x00 shows up
        /// instead, it's treated as spurious/leading noise and silently skipped in favor of the
        /// next byte, rather than an immediate hard failure.</summary>
        /// <param name="mismatchLogDest">
        /// Where the "RX: ... (expected ...)" line goes if <paramref name="expected"/> doesn't
        /// match -- right before this method throws. Defaults to the visible destination, correct
        /// for standalone callers (CloseEcu, the one-shot erase-confirm CheckAck() calls in
        /// WriteFlash) where a mismatch is a real, rare, worth-seeing-immediately event. Callers
        /// inside a silent-retry loop (SendFlashWriteChunk's CheckAck() call) should instead pass
        /// LogDest.File on every attempt except the last -- otherwise this line defeats the whole
        /// point of that retry-suppression feature: the outer loop successfully hides its own
        /// "attempt N/M failed" summary line on a benign, about-to-be-retried first failure, but
        /// this line would still flash up unconditionally on screen every time.
        /// </param>
        private void CheckRec(byte expected, LogDest mismatchLogDest = LogDest.All)
        {
            var b = _kwpCommon.ReadByte();
            if (b == 0x00 && expected != 0x00)
            {
                // Async, file-only -- see ILog.WriteFileOnly's own doc comment for why even
                // the synchronous LogDest.File path this used to go through was still too much
                // (locking, buffer writes) on this hot a path, real-hardware-confirmed. This one's
                // an even bigger volume contributor than WriteRaw's TX line: CheckAck alone calls
                // CheckRec 3 times per successful flash-write chunk (~2000+ chunks per write, so
                // ~6000+ RX "lines" from this method alone), and this particular branch is a KNOWN,
                // already-tolerated quirk (see this method's own class-level doc comment), not a
                // failure.
                Log.WriteFileOnly(
                    "  RX: 0x00 (spurious leading byte, expected non-zero -- skipping it)\n");
                b = _kwpCommon.ReadByte();
            }
            if (b != expected)
            {
                Log.WriteLine($"  RX: 0x{b:X2} (expected 0x{expected:X2})", mismatchLogDest);
                throw new InvalidOperationException(
                    $"EDC15 handshake mismatch: expected 0x{expected:X2}, got 0x{b:X2}.");
            }
            Log.WriteFileOnly($"  RX: 0x{b:X2}\n");
        }

        /// <summary>Writes every byte in <paramref name="data"/> as-is (no automatic checksum --
        /// the caller must already have included one). Matches iso_write_byte(len, data). Every
        /// caller of this passes a short, fixed, already-known array (5-10 bytes: the
        /// startCommunication burst, erase command, close-ECU sequence) -- bulk flash write goes
        /// through WriteFlashBlock's own direct WriteByteDelayed loop instead, so logging the full
        /// array here on every call is safe and bounded.</summary>
        /// <summary>
        /// Writes a short, fixed-size message (read-flash requests -- 7 bytes, by far the highest-
        /// volume caller at ~2000+ calls per full read -- the erase command, CloseEcu, etc.) and
        /// discards the echoes, via <see cref="BitFab.KW1281Test.KwpCommon.WriteBytes"/> rather than
        /// the old per-byte <see cref="WriteByteDelayed"/> loop.
        ///</para>
        /// </summary>
        private void WriteRaw(byte[] data)
        {
            // Async, file-only. Still fully captured in the log FILE for later inspection
            // either way.
            Log.WriteFileOnly($"  TX: {Utils.DumpBytes(data)}\n");
            _kwpCommon.WriteBytes(data);
        }

        /// <summary>Reads and verifies the 3-byte 0x01,0x76,0x77 transferData acknowledgement.
        /// mismatchLogDest is forwarded to each CheckRec call -- see its own doc comment.</summary>
        private void CheckAck(LogDest mismatchLogDest = LogDest.All)
        {
            CheckRec(0x01, mismatchLogDest);
            CheckRec(0x76, mismatchLogDest);
            CheckRec(0x77, mismatchLogDest);
        }

        private void CloseEcu()
        {
            WriteRaw(new byte[] { 0x01, 0xA2, 0xA3 });
            CheckRec(0x55);
        }

        /// <summary>
        /// Best-effort <see cref="CloseEcu"/> for use in a <c>finally</c> block around ReadFlash/
        /// WriteFlash: tells the loader to stop, but never throws itself, so it can't mask whatever
        /// real error is already propagating and can't hang the failure path if the K-line is
        /// already in a bad state (CloseEcu's own CheckRec still has a bounded read timeout either
        /// way, so this can't hang forever, but there's no reason to let a *second*, less useful
        /// exception replace the first one).
        /// </summary>
        private void TryCloseEcu()
        {
            try
            {
                CloseEcu();
            }
            catch (Exception ex)
            {
                Log.WriteLine($"(Best-effort CloseEcu after failure also failed: {ex.Message})");
            }
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
    }
}
