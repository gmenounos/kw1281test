using BitFab.KW1281Test.Interface;
using System;
using System.Collections.Generic;
using System.Runtime;
using System.Threading;

namespace BitFab.KW1281Test
{
    public interface IKwpCommon
    {
        IInterface Interface { get; }

        /// <param name="isStopRequested">
        /// Optional; polled between this method's own internal retry attempts (see the
        /// implementation's doc comment) so a global Stop doesn't have to wait out this wakeup's
        /// entire up-to-3-tries/1-second-sleep-each retry ceiling before it's noticed -- only the
        /// single attempt already in flight when Stop was pressed. Defaults to null (never
        /// checked) so existing callers that don't care about interactive stopping are unaffected.
        /// </param>
        /// <param name="stopRetryingOnSyncByte">
        /// Optional; given the <see cref="LastSyncByte"/> just read on a failed attempt, returns true
        /// to STOP the internal retry loop immediately instead of burning the remaining tries and
        /// their 1-second sleeps. For a DETERMINISTIC condition that won't change on retry -- e.g. an
        /// EDC15 soft-brick answering 9600 with a consistent $95/$B5, which the caller then handles by
        /// switching to recovery mode -- retrying is pure wasted time. Null (the default) preserves the
        /// full 3-try behavior for every existing caller and every other kind of failure.
        /// </param>
        int WakeUp(
            byte controllerAddress, bool evenParity = false, bool failQuietly = false,
            Func<bool>? isStopRequested = null, Func<int, bool>? stopRetryingOnSyncByte = null);

        /// <summary>
        /// The raw sync byte read on the most recent <see cref="WakeUp"/> attempt (the value seen
        /// where $55 was expected), or -1 if no byte was read. Lets a caller distinguish a specific
        /// ECU-state signature -- e.g. an EDC15 soft-brick answering 9600 with a consistent $95/$B5 --
        /// from ordinary noise, which <see cref="WakeUp"/> itself flattens into a generic failure.
        /// </summary>
        int LastSyncByte { get; }

        byte ReadByte();

        /// <summary>
        /// Write a byte to the interface and receive its echo.
        /// </summary>
        /// <param name="b">The byte to write.</param>
        void WriteByte(byte b);

        /// <summary>
        /// Write every byte in <paramref name="bytes"/> and discard all of their echoes, in groups
        /// of <paramref name="subBatchSize"/> bytes at a time (one <see cref="Interface.IInterface.
        /// WriteBytesRaw"/> call + one <see cref="Interface.IInterface.ReadBytes"/> call per group)
        /// rather than <paramref name="bytes"/>.Length individual <see cref="WriteByte"/> round
        /// trips. See <see cref="EDC15.Edc15FlashVM.SendFlashWriteChunk"/> for the full history.
        ///
        /// <para>This used to write+drain the ENTIRE array as a single group (no sub-batching at
        /// all). A real-hardware capture (KWHack21.log) taken against that version showed a write
        /// getting considerably further than before (past 50%, up from 32%) but with a markedly
        /// higher rate of the loader NAK'ing individual chunks with a checksum error (0x7F where
        /// 0x76 was expected) -- a real rejection by the loader's own message-checksum check
        /// (see Loader.a66's E128/E082), not a framing/misalignment bug in this method. See
        /// <see cref="EDC15.Edc15FlashVM.WriteRaw"/>'s doc comment for the corrected understanding of
        /// WHY grouping helps at all (pipelining transmit/echo across a group instead of forcing a
        /// full round trip per byte) -- the same logic applies here to explain why an unbounded group
        /// size made NAKs worse: at some group size, whatever's actually marginal about this
        /// particular K-line connection (cable/grounding/noise -- unconfirmed) apparently can't keep
        /// up with an uninterrupted burst that long, without needing to invoke UART pacing at all.</para>
        ///
        /// <para>Tuning history: 16 (KWHack21.log's follow-up) got a full write to succeed
        /// (KWHack24.log) at maxAttempts=5, with only ~2.2% of chunks needing even one retry and just
        /// one chunk needing a second. That safety margin was spent on speed -- raised to 64, which
        /// (KWHack25.log) not only held up but improved: write time dropped from 9m47s to 3m28s, with
        /// 27/~2098 chunks needing one retry and, notably, ZERO needing a second (down from 1 at 16) --
        /// no sign this connection's NAK rate is sensitive to group size in the 16-64 range, at least
        /// on the hardware tested so far. Raised again to 128, which also held up -- at that point the
        /// user confirmed it was time to drop sub-batching entirely (and separately noted the earlier
        /// first-read-attempt failures, from around when this ladder started, had also stopped
        /// recurring -- consistent with those being unrelated transient connection hiccups rather than
        /// anything caused by this method's tuning). Defaulted to <see cref="int.MaxValue"/> rather
        /// than literally removing the sub-batching mechanism: with every real chunk this app ever
        /// builds far smaller than that (the largest, flash-write chunks, top out around 256 bytes --
        /// see <see cref="EDC15.Edc15FlashVM.WriteFlashBlock"/>'s 0xF9 cap), this always sends the
        /// whole buffer as one group in practice, while leaving the knob in place (and its full tuning
        /// history documented here) in case a future real-hardware regression ever needs it dialed
        /// back down again rather than reinventing this from scratch.</para>
        /// </summary>
        void WriteBytes(byte[] bytes, int subBatchSize = int.MaxValue);

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/> (from index
        /// 0) as one operation via <see cref="Interface.IInterface.ReadBytes"/>, rather than
        /// <paramref name="count"/> individual <see cref="ReadByte"/> calls. Unlike
        /// <see cref="WriteBytes"/>, this has no sub-batching/backpressure concern to worry about:
        /// the caller is purely draining data the ECU already decided to send (e.g.
        /// <see cref="EDC15.Edc15FlashVM.ReadFlashDataPacket"/>'s data loop) -- nothing this app does
        /// on the read side can outrun or get ahead of the ECU's own transmission, so there's no
        /// backpressure for a smaller read to preserve. Safe to read in one full-size call.
        /// </summary>
        void ReadBytes(byte[] buffer, int count);

        void ReadComplement(byte b);
    }

    internal class KwpCommon : IKwpCommon
    {
        public IInterface Interface { get; }

        /// <inheritdoc />
        public int LastSyncByte { get; private set; } = -1;

        public int WakeUp(
            byte controllerAddress, bool evenParity, bool failQuietly,
            Func<bool>? isStopRequested = null, Func<int, bool>? stopRetryingOnSyncByte = null)
        {
            LastSyncByte = -1;
            // Disable garbage collection int this time-critical method. On Mono (which is what
            // Android apps run on) this API isn't implemented at all -- it throws
            // NotImplementedException rather than just returning false like CoreCLR does when it
            // can't honor the request -- so this needs a try/catch, not just the false check below.
            //
            // The two failure modes are deliberately NOT treated the same: NotImplementedException
            // means Mono/Android, where this is simply never possible -- every single wakeup would
            // otherwise log the same "unable to disable GC" warning unconditionally, which isn't a
            // real signal of anything since there was never a chance of it working there to begin
            // with. A plain `false` with no exception is CoreCLR (Desktop) declining a specific
            // request it's otherwise capable of honoring, which IS worth surfacing.
            bool noGC;
            bool gcRegionApiUnsupported = false;
            try
            {
                noGC = GC.TryStartNoGCRegion(1024 * 1024);
            }
            catch (NotImplementedException)
            {
                noGC = false;
                gcRegionApiUnsupported = true;
            }
            catch (Exception)
            {
                noGC = false;
            }
            if (!noGC && !gcRegionApiUnsupported)
            {
                Log.WriteLine("Warning: Unable to disable GC so timing may be compromised.");
            }

            var protocolVersion = 0;
            Interface.ReadTimeout = (int)TimeSpan.FromSeconds(2).TotalMilliseconds;
            try
            {
                const int maxTries = 3;
                for (var i = 1; i <= maxTries; i++)
                {
                    try
                    {
                        protocolVersion = WakeUpNoRetry(controllerAddress, evenParity);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(ex.Message);

                        // A caller can mark the sync byte just read as a DETERMINISTIC condition that
                        // won't change on retry (e.g. an EDC15 soft-brick answers 9600 with a
                        // consistent $95/$B5 -- confirmed reliable on the first probe). When it does,
                        // stop immediately and rethrow so the caller can act on it (switch to recovery
                        // mode) instead of burning the remaining tries and their 1-second sleeps. Any
                        // other failure (including ordinary transient noise) still gets the full retry.
                        if (stopRetryingOnSyncByte?.Invoke(LastSyncByte) == true)
                        {
                            throw;
                        }

                        // Checked here, before committing to another attempt (and its 1-second
                        // sleep), rather than only where the caller's own outer retry loop checks
                        // between whole Connect attempts -- this wakeup already burns up to 3
                        // tries on its own, and without this a Stop press mid-wakeup wouldn't be
                        // noticed until all of them (plus whatever else the current Connect
                        // attempt goes on to try) had run their course.
                        if (i < maxTries && isStopRequested?.Invoke() == true)
                        {
                            throw new OperationCanceledException("Stopped during wakeup retry.");
                        }

                        if (i < maxTries)
                        {
                            Log.WriteLine("Retrying wakeup message...");
                            Thread.Sleep(TimeSpan.FromSeconds(1));
                        }
                        else
                        {
                            if (!failQuietly)
                            {
                                Log.WriteLine();
                                Log.WriteLine("Controller did not wake up.");
                                Log.WriteLine("    - Are you using a supported cable?");
                                Log.WriteLine("    - Is the cable plugged in and any necessary drivers installed?");
                                Log.WriteLine("    - Is the ignition on?");
                                Log.WriteLine("    - Is the controller address correct?");
                                Log.WriteLine("    - Is the baud rate correct (unexpected sync byte errors)? Try 10400, 9600, 4800.");
                                Log.WriteLine("You can try other software (e.g. VCDS-Lite) to verify that the cable/drivers/address are ok.");
                            }
                            throw new UnableToProceedException();
                        }
                    }
                }
            }
            finally
            {
                // Only end the region if WE actually started one. Don't gate this on
                // GCSettings.LatencyMode == GCLatencyMode.NoGCRegion: that's racy. A No-GC
                // region auto-exits (LatencyMode flips back on its own) the moment total
                // allocations exceed the 1MB budget reserved above -- which a large read
                // during wakeup can easily do -- so the mode check can read false even
                // though we successfully started a region, or read true and then have the
                // region exit between the check and the call. Either way EndNoGCRegion()
                // throws InvalidOperationException ("NoGCRegion mode must be set") when no
                // region is currently active. Guard on the noGC bool (did WE start one) and
                // swallow that specific exception in case it already auto-exited.
                if (noGC)
                {
                    try
                    {
                        GC.EndNoGCRegion();
                    }
                    catch (InvalidOperationException)
                    {
                        // Region already auto-exited because allocations exceeded the
                        // reserved budget. Nothing to end; not an error.
                    }
                }
                Interface.ReadTimeout = Interface.DefaultTimeoutMilliseconds;
            }

            return protocolVersion;
        }

        private int WakeUpNoRetry(byte controllerAddress, bool evenParity)
        {
            Thread.Sleep(300);

            BitBang5Baud(controllerAddress, evenParity);

            // Throw away anything that might be in the receive buffer
            Interface.ClearReceiveBuffer();

            Log.WriteLine("Reading sync byte");

            // Buffer logging in memory until we're done with the wakeup, which is sensitive to timing
            var logLines = new List<string>();

            var syncByte = Interface.ReadByte();
            LastSyncByte = syncByte;

            if (syncByte != 0x55)
            {
                throw new InvalidOperationException(
                    $"Unexpected sync byte: Expected $55, Actual ${syncByte:X2}");
            }

            int protocolVersion;
            try
            {
                var keywordLsb = Interface.ReadByte();
                logLines.Add($"Keyword Lsb ${keywordLsb:X2}");

                var keywordMsb = ReadByte();
                logLines.Add($"Keyword Msb ${keywordMsb:X2}");

                protocolVersion = ((keywordMsb & 0x7F) << 7) + (keywordLsb & 0x7F);
                logLines.Add($"Protocol is KW {protocolVersion} (8N1)");

                BusyWait.Delay(25);

                var complement = (byte)~keywordMsb;
                WriteByte(complement);
            }
            finally
            {
                foreach (var line in logLines)
                {
                    Log.WriteLine(line);
                }
            }

            if (protocolVersion >= 2000)
            {
                ReadComplement(
                    Utils.AdjustParity(controllerAddress, evenParity));
            }

            return protocolVersion;
        }


        public byte ReadByte()
        {
            return Interface.ReadByte();
        }

        public void WriteByte(byte b)
        {
            WriteByteAndDiscardEcho(b);
        }

        public void WriteBytes(byte[] bytes, int subBatchSize = int.MaxValue)
        {
            if (bytes.Length == 0)
            {
                return;
            }

            subBatchSize = Math.Max(1, subBatchSize);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = Math.Min(subBatchSize, bytes.Length - offset);
                var group = new byte[count];
                Array.Copy(bytes, offset, group, 0, count);

                Interface.WriteBytesRaw(group);
                var echoes = new byte[count];
                Interface.ReadBytes(echoes, count);

                offset += count;
            }
        }

        public void ReadBytes(byte[] buffer, int count)
        {
            Interface.ReadBytes(buffer, count);
        }

        public void ReadComplement(byte b)
        {
            var expectedComplement = (byte)~b;
            var actualComplement = Interface.ReadByte();
            if (actualComplement != expectedComplement)
            {
                throw new InvalidOperationException(
                    $"Received complement ${actualComplement:X2} but expected ${expectedComplement:X2}");
            }
        }

        /// <summary>
        /// Send a byte at 5 baud manually to the interface. The byte will be sent as
        /// 1 start bit, 7 data bits, 1 parity bit (even or odd), 1 stop bit.
        /// https://www.blafusel.de/obd/obd2_kw1281.html
        /// </summary>
        /// <param name="b">The byte to send.</param>
        /// <param name="evenParity">
        /// False for odd parity (KWP1281), true for even parity (KWP2000).</param>
        private void BitBang5Baud(byte b, bool evenParity)
        {
            b = Utils.AdjustParity(b, evenParity);

            const int bitsPerSec = 5;
            const long msPerBit = 1000 / bitsPerSec;

            var waiter = new BusyWait(msPerBit);

            // The first call to SetBreak takes extra time (at least with an FTDI cable on Linux)
            // so do that here outside of the timing loop. Since the break state should already be
            // false, this should have no effect other than to delay a couple milliseconds and it
            // makes the timing of the rest of the bits be more accurate.
            Interface.SetBreak(false);

            BitBang(false); // Start bit

            for (int i = 0; i < 8; i++)
            {
                bool bit = (b & 1) == 1;
                BitBang(bit);
                b >>= 1;
            }

            BitBang(true); // Stop bit

            BusyWait.Delay(msPerBit);
            return;

            // Delay the appropriate amount and then set/clear the TxD line
            void BitBang(bool bit)
            {
                waiter.DelayUntilNextCycle();
                Interface.SetBreak(!bit);
            }
        }

        /// <summary>
        /// Write a byte to the interface and read/discard its echo.
        /// </summary>
        private void WriteByteAndDiscardEcho(byte b)
        {
            Interface.WriteByteRaw(b);
            var echo = Interface.ReadByte();
#if false
            if (echo != b)
            {
                throw new InvalidOperationException($"Wrote 0x{b:X2} to port but echo was 0x{echo:X2}");
            }
#endif
        }

        public KwpCommon(IInterface @interface)
        {
            Interface = @interface;
        }
    }
}
