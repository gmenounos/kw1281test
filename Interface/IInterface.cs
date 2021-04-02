using System;

namespace BitFab.KW1281Test.Interface
{
    public interface IInterface : IDisposable
    {
        /// <summary>
        /// Read a byte from the interface.
        /// </summary>
        /// <returns>The byte.</returns>
        byte ReadByte();

        /// <summary>
        /// Write a byte to the interface but do not read/discard its echo.
        /// </summary>
        void WriteByteRaw(byte b);

        void SetBreakOn();

        void SetBreakOff();

        void ClearReceiveBuffer();

        void SetBaudRate(int baudRate);
    }
}
