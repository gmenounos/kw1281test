using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    class GenericInterface : IInterface
    {
        private readonly TimeSpan DefaultTimeOut = TimeSpan.FromSeconds(8);

        public GenericInterface(string portName, int baudRate)
        {
            _port = new SerialPort(portName)
            {
                BaudRate = baudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                RtsEnable = false,
                DtrEnable = true,
                ReadTimeout = (int)DefaultTimeOut.TotalMilliseconds,
                WriteTimeout = (int)DefaultTimeOut.TotalMilliseconds
            };

            _port.Open();
        }

        public void Dispose()
        {
            SetDtr(false);
            _port.Close();
        }

        public byte ReadByte()
        {
            var b = (byte)_port.ReadByte();
            return b;
        }

        public void WriteByteRaw(byte b)
        {
            _buf[0] = b;
            _port.Write(_buf, 0, 1);
        }

        public void SetBreak(bool on)
        {
            _port.BreakState = on;
        }

        public void ClearReceiveBuffer()
        {
            _port.DiscardInBuffer();
        }

        public void SetBaudRate(int baudRate)
        {
            _port.BaudRate = baudRate;
        }

        public void SetDtr(bool on)
        {
            _port.DtrEnable = on;
        }

        public void SetRts(bool on)
        {
            _port.RtsEnable = on;
        }

        private readonly SerialPort _port;

        private readonly byte[] _buf = new byte[1];
    }
}
