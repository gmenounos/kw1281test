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
