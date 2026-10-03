using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// The USB protocol of the FTDI FT232R/FT232BM chip: vendor requests on endpoint zero and
    /// bulk data with two status bytes at the start of every incoming packet.
    ///
    /// Everything here was checked against USB captures of a Ross-Tech HEX-USB (0403:FA24,
    /// bcdDevice 0600, i.e. an FT232R) under its own driver: Ross-Tech's library sends exactly
    /// these requests with exactly these values; the cable adds nothing on top of FTDI.
    /// </summary>
    internal static class FtdiProtocol
    {
        /// <summary>bmRequestType: vendor, host to device.</summary>
        public const byte RequestTypeOut = 0x40;

        /// <summary>bmRequestType: vendor, device to host.</summary>
        public const byte RequestTypeIn = 0xC0;

        public const byte Reset = 0x00;
        public const byte ModemControl = 0x01;
        public const byte SetFlowControl = 0x02;
        public const byte SetBaudRate = 0x03;
        public const byte SetData = 0x04;
        public const byte GetModemStatus = 0x05;
        public const byte SetLatencyTimer = 0x09;

        public const ushort ResetSio = 0;
        public const ushort PurgeRx = 1;
        public const ushort PurgeTx = 2;

        public const ushort DtrOn = 0x0101;
        public const ushort DtrOff = 0x0100;
        public const ushort RtsOn = 0x0202;
        public const ushort RtsOff = 0x0200;

        public const byte BulkIn = 0x81;
        public const byte BulkOut = 0x02;

        /// <summary>Full-speed bulk packet size of the FT232R.</summary>
        public const int PacketSize = 64;

        /// <summary>Every incoming packet starts with two modem/line status bytes.</summary>
        public const int StatusBytes = 2;

        /// <summary>
        /// Baud rate divisor for the FT232R/FT232BM: 3 MHz base clock and a divisor with a
        /// fractional part in eighths. Returns the wValue/wIndex pair of SET_BAUD_RATE.
        ///
        /// Checked against the capture: 9600 → 4138/0, 10400 → 4120/0, 115200 → 001A/0.
        /// </summary>
        public static (ushort Value, ushort Index) EncodeBaudRate(int baudRate)
        {
            if (baudRate <= 0) throw new ArgumentOutOfRangeException(nameof(baudRate));

            const int clock = 3_000_000;
            uint encoded;
            if (baudRate >= clock)
            {
                encoded = 0;
            }
            else if (baudRate >= clock * 2 / 3)
            {
                encoded = 1;
            }
            else if (baudRate >= clock / 2)
            {
                encoded = 2;
            }
            else
            {
                // Divisor in eighths, rounded to the nearest.
                ReadOnlySpan<uint> fractionCode = [0, 3, 2, 4, 1, 5, 6, 7];
                var sixteenths = (long)clock * 16 / baudRate;
                var eighths = (uint)((sixteenths & 1) != 0 ? sixteenths / 2 + 1 : sixteenths / 2);
                if (eighths > 0x20000) eighths = 0x1FFFF;
                encoded = (eighths >> 3) | (fractionCode[(int)(eighths & 7)] << 14);
            }

            return ((ushort)(encoded & 0xFFFF), (ushort)(encoded >> 16));
        }

        /// <summary>wValue of SET_DATA: 8 data bits, 1 stop bit, parity and break.</summary>
        public static ushort EncodeLineProperties(Parity parity, bool breakOn)
        {
            ushort parityBits = parity switch
            {
                Parity.None => 0,
                Parity.Odd => 1,
                Parity.Even => 2,
                Parity.Mark => 3,
                Parity.Space => 4,
                _ => throw new ArgumentException($"Unsupported parity: {parity}", nameof(parity)),
            };

            return (ushort)(8 | (parityBits << 8) | (breakOn ? 1 << 14 : 0));
        }

        /// <summary>
        /// Picks the data out of a bulk IN transfer: one read can return several packets in a
        /// row, each starting with two status bytes. Returns how many data bytes were written
        /// to <paramref name="destination"/>.
        /// </summary>
        public static int ExtractPayload(ReadOnlySpan<byte> received, Span<byte> destination)
        {
            var written = 0;
            for (var offset = 0; offset < received.Length; offset += PacketSize)
            {
                var packet = received.Slice(offset, Math.Min(PacketSize, received.Length - offset));
                if (packet.Length <= StatusBytes) continue;

                var payload = packet[StatusBytes..];
                payload.CopyTo(destination[written..]);
                written += payload.Length;
            }
            return written;
        }
    }
}
