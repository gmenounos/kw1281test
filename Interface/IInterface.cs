using System;

namespace BitFab.KW1281Test.Interface
{
    public interface IInterface : IDisposable
    {

        /// default time out in seconds
        uint _defaultTimeOut = 8;

        
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

        void SetDtr(bool on);

        void SetRts(bool on);
    }
}
