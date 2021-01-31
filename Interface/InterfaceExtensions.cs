using System;
using System.Diagnostics;

namespace BitFab.KW1281Test.Interface
{
    static class InterfaceExtensions
    {
        /// <summary>
        /// Send a byte at 5 baud manually to the interface. The byte will be sent as
        /// 1 start bit, 7 data bits, 1 parity bit (even or odd), 1 stop bit.
        /// https://www.blafusel.de/obd/obd2_kw1281.html
        /// </summary>
        /// <param name="b">The byte to send.</param>
        /// <param name="evenParity">
        /// False for odd parity (KWP1281), true for even parity (KWP2000).</param>
        public static void BitBang5Baud(this IInterface @interface, byte b, bool evenParity)
        {
            // Disable garbage collection int this time-critical method
            bool noGc = GC.TryStartNoGCRegion(1024 * 1024);

            const int bitsPerSec = 5;
            long ticksPerBit = Stopwatch.Frequency / bitsPerSec;

            var stopWatch = new Stopwatch();
            long maxTick = 0;

            // Delay the appropriate amount and then set/clear the TxD line
            void BitBang(bool bit)
            {
                while (stopWatch.ElapsedTicks < maxTick)
                    ;
                if (bit)
                {
                    @interface.SetBreakOff();
                }
                else
                {
                    @interface.SetBreakOn();
                }

                maxTick += ticksPerBit;
            }

            bool parity = !evenParity; // XORed with each bit to calculate parity bit

            stopWatch.Start();

            BitBang(false); // Start bit

            for (int i = 0; i < 7; i++)
            {
                bool bit = (b & 1) == 1;
                parity ^= bit;
                b >>= 1;

                BitBang(bit);
            }

            BitBang(parity);

            BitBang(true); // Stop bit

            if (noGc)
            {
                GC.EndNoGCRegion();
            }

            // Throw away anything that might be in the receive buffer
            @interface.ClearReceiveBuffer();
        }


        /// <summary>
        /// Write a byte to the interface and read/discard its echo.
        /// </summary>
        public static void WriteByteAndDiscardEcho(this IInterface @interface, byte b)
        {
            @interface.WriteByteRaw(b);
            var echo = @interface.ReadByte();
            if (echo != b)
            {
                throw new InvalidOperationException($"Wrote 0x{b:X2} to port but echo was 0x{echo:X2}");
            }
        }
    }
}
