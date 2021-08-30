using System;
using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    class GenericInterface : IInterface
    {
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
                ReadTimeout = (int)TimeSpan.FromSeconds(2).TotalMilliseconds,
                WriteTimeout = (int)TimeSpan.FromSeconds(2).TotalMilliseconds
            };

            _port.Open();
        }

        public void Dispose()
        {
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

        public void SetBreakOn()
        {
            _port.BreakState = true;
        }

        public void SetBreakOff()
        {
            _port.BreakState = false;
        }

        public void ClearReceiveBuffer()
        {
            _port.DiscardInBuffer();
        }

        public void SetBaudRate(int baudRate)
        {
            _port.BaudRate = baudRate;
        }

        private readonly SerialPort _port;

        private readonly byte[] _buf = new byte[1];
    }
}
