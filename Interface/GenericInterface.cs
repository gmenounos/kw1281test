using System.IO.Ports;

namespace BitFab.KW1281Test.Interface
{
    class GenericInterface : IInterface
    {
        public GenericInterface(string portName, int baudRate)
        {
            var timeout = ((IInterface)this).DefaultTimeoutMilliseconds;
            _port = new SerialPort(portName)
            {
                BaudRate = baudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                RtsEnable = false,
                DtrEnable = true,
                ReadTimeout = timeout,
                WriteTimeout = timeout
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

        public void SetParity(Parity parity)
        {
            _port.Parity = parity;
        }

        public void SetDtr(bool on)
        {
            _port.DtrEnable = on;
        }

        public void SetRts(bool on)
        {
            _port.RtsEnable = on;
        }

        public int ReadTimeout
        {
            get => _port.ReadTimeout;
            set => _port.ReadTimeout = value;
        }

        public int WriteTimeout
        {
            get => _port.WriteTimeout;
            set => _port.WriteTimeout = value;
        }

        private readonly SerialPort _port;

        private readonly byte[] _buf = new byte[1];
    }
}
