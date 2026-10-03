using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// Transport profile for VasyaDiagnost/Car2diag FTDI adapters (USB VID_0403&amp;PID_FA3F).
    /// The adapter firmware performs controller wake-up on request and then
    /// exposes a transparent KW1281 byte stream.
    /// </summary>
    internal sealed class VasyaInterface : IInterface, IAssistedWakeupInterface
    {
        /// <summary>USB PID of VasyaDiagnost adapters.</summary>
        private const uint VasyaProductId = 0xFA3F;

        private readonly FtdiInterface _inner;

        public VasyaInterface(string serialNumber, int baudRate)
        {
            _inner = new FtdiInterface(serialNumber, baudRate, vendorProfile: true,
                customProductId: VasyaProductId);
        }

        public int DefaultTimeoutMilliseconds => (int)TimeSpan.FromSeconds(8).TotalMilliseconds;

        public int ReadTimeout
        {
            get => _inner.ReadTimeout;
            set => _inner.ReadTimeout = value;
        }

        public int WriteTimeout
        {
            get => _inner.WriteTimeout;
            set => _inner.WriteTimeout = value;
        }

        public byte ReadByte() => _inner.ReadByte();

        public void WriteByteRaw(byte b) => _inner.WriteByteRaw(b);

        public int AssistedWakeUp(byte controllerAddress, bool evenParity)
        {
            if (evenParity)
            {
                throw new NotSupportedException(
                    "VasyaDiagnost assisted wake-up does not currently support KWP2000 even parity.");
            }

            // The vendor application talks to the adapter at 115200 baud and
            // asks its firmware to perform the slow controller wake-up. The
            // adapter then becomes a transparent byte transport for KW1281.
            _inner.SetBaudRate(AssistedWakeupProtocol.AdapterCommandBaudRate);
            _inner.SetParity(Parity.None);
            _inner.SetLatencyTimer(1);
            _inner.SetDtr(false);
            _inner.SetRts(false);
            _inner.PurgeBuffers();

            return AssistedWakeupProtocol.WakeUp(_inner, controllerAddress, "VasyaDiagnost");
        }

        public void SetBreak(bool on) => _inner.SetBreak(on);

        public void ClearReceiveBuffer() => _inner.ClearReceiveBuffer();

        public void SetBaudRate(int baudRate) => _inner.SetBaudRate(baudRate);

        public void SetParity(Parity parity) => _inner.SetParity(parity);

        public void SetDtr(bool on) => _inner.SetDtr(on);

        public void SetRts(bool on) => _inner.SetRts(on);

        public void Dispose() => _inner.Dispose();
    }
}
