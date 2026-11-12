using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// A Ross-Tech HEX cable installed with Ross-Tech's Virtual COM Port driver, so it shows
    /// up as an ordinary COM port. This path needs no vendor library at all.
    ///
    /// The protocol is the same as over D2XX: the cable firmware wakes the controller on
    /// command $53 $07 $84 ... and is transparent afterwards.
    /// </summary>
    internal sealed class RossTechSerialInterface : IInterface, IAssistedWakeupInterface
    {
        private readonly GenericInterface _inner;
        private readonly int _baudRate;

        public RossTechSerialInterface(string portName, int baudRate)
        {
            _inner = new GenericInterface(portName, baudRate);
            _baudRate = baudRate;
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
                    "Ross-Tech assisted wake-up does not currently support KWP2000 even parity.");
            }

            // Talk to the cable at its command speed, not at the K-line speed.
            _inner.SetBaudRate(AssistedWakeupProtocol.AdapterCommandBaudRate);
            _inner.SetParity(Parity.None);
            _inner.SetDtr(false);
            _inner.SetRts(false);
            _inner.ClearReceiveBuffer();

            var protocol = AssistedWakeupProtocol.WakeUp(_inner, controllerAddress, "Ross-Tech");

            // From here on the cable is transparent and the exchange runs at the K-line speed.
            _inner.SetBaudRate(_baudRate);
            return protocol;
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
