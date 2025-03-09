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

        int WakeUp(byte controllerAddress, bool evenParity = false);

        byte ReadByte();

        /// <summary>
        /// Write a byte to the interface and receive its echo.
        /// </summary>
        /// <param name="b">The byte to write.</param>
        void WriteByte(byte b);

        void ReadComplement(byte b);
    }

    internal class KwpCommon : IKwpCommon
    {
        public IInterface Interface { get; }

        public int WakeUp(byte controllerAddress, bool evenParity)
        {
            // Disable garbage collection int this time-critical method
            var noGC = GC.TryStartNoGCRegion(1024 * 1024);
            if (!noGC)
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

                        if (i < maxTries)
                        {
                            Log.WriteLine("Retrying wakeup message...");
                            Thread.Sleep(TimeSpan.FromSeconds(1));
                        }
                        else
                        {
                            Log.WriteLine();
                            Log.WriteLine("Controller did not wake up.");
                            Log.WriteLine("    - Are you using a supported cable?");
                            Log.WriteLine("    - Is the cable plugged in and any necessary drivers installed?");
                            Log.WriteLine("    - Is the ignition on?");
                            Log.WriteLine("    - Is the controller address correct?");
                            Log.WriteLine("    - Is the baud rate correct (unexpected sync byte errors)? Try 10400, 9600, 4800.");
                            Log.WriteLine("You can try other software (e.g. VCDS-Lite) to verify that the cable/drivers/address are ok.");
                            throw new UnableToProceedException();
                        }
                    }
                }
            }
            finally
            {
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                {
                    GC.EndNoGCRegion();
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
