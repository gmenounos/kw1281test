using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    public interface IInterface : IDisposable
    {
        int DefaultTimeoutMilliseconds => (int)TimeSpan.FromSeconds(8).TotalMilliseconds;

        /// <summary>
        /// Read a byte from the interface.
        /// </summary>
        /// <returns>The byte.</returns>
        byte ReadByte();

        /// <summary>
        /// Write a byte to the interface but do not read/discard its echo.
        /// </summary>
        void WriteByteRaw(byte b);

        /// <summary>
        /// Write every byte in <paramref name="bytes"/> as one operation rather than one
        /// <see cref="WriteByteRaw"/> call per byte. Added for EDC15 flash writes, where the old
        /// one-native-call-per-byte pattern meant a large (~512KB) write issued over hundreds of
        /// thousands of individual synchronous serial/D2XX calls, each carrying its own fixed
        /// USB-transaction/syscall overhead independent of the configured UART baud rate.
        ///
        /// <para>Default implementation just loops <see cref="WriteByteRaw"/> -- zero behavior
        /// change for any interface that doesn't override it. An interface whose native layer can
        /// take an arbitrary length (e.g. an FTDI D2XX wrapper) may override this with a real
        /// single-call batch write.</para>
        /// </summary>
        void WriteBytesRaw(byte[] bytes)
        {
            foreach (var b in bytes)
            {
                WriteByteRaw(b);
            }
        }

        /// <summary>
        /// Read exactly <paramref name="count"/> bytes into <paramref name="buffer"/> (starting at
        /// index 0) as one operation rather than one <see cref="ReadByte"/> call per byte -- the
        /// read-side counterpart to <see cref="WriteBytesRaw"/>, used to drain the K-line echo of a
        /// batched write chunk. Default implementation just loops <see cref="ReadByte"/>.
        /// </summary>
        void ReadBytes(byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                buffer[i] = ReadByte();
            }
        }

        void SetBreak(bool on);

        void ClearReceiveBuffer();

        void SetBaudRate(int baudRate);

        void SetParity(Parity parity);

        void SetDtr(bool on);

        void SetRts(bool on);

        int ReadTimeout { get; set; }

        int WriteTimeout { get; set; }
    }
}
