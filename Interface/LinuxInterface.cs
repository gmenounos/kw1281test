using System;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Ports;


namespace BitFab.KW1281Test.Interface
{
    public class LinuxInterface : IInterface
    {

        const int PARENB = 0x1000; // Enable parity bit
        const int PARODD = 0x2000; // Use odd parity rather than even parity

        private const string libc = "libc";

        // Linux ioctl function
        [DllImport(libc, SetLastError = true)]
        private static extern int ioctl(int fd, uint request, ref int data);

        // Native method declarations
        [DllImport(libc)]
        private static extern int open(string pathname, int flags);

        [DllImport(libc)]
        private static extern int close(int fd);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int read(int fd, byte[] buf, int count);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int write(int fd, byte[] buf, int count);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int tcflush(int fd, int queue);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int cfsetispeed(IntPtr termios, int speed);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int cfsetospeed(IntPtr termios, int speed);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int tcgetattr(int fd, IntPtr termios);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern int tcsetattr(int fd, int optional_actions, IntPtr termios);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr malloc(int size);

        [DllImport(libc, CallingConvention = CallingConvention.StdCall)]
        private static extern void free(IntPtr ptr);


        // Define the termios structure to interact with terminal I/O settings
        [StructLayout(LayoutKind.Sequential)]
        public struct Termios
        {
            public uint c_iflag;
            public uint c_oflag;
            public uint c_cflag;
            public uint c_lflag;
            public byte c_line;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] c_cc;
            public uint c_ispeed;
            public uint c_ospeed;
        }



        public const int TIOCSBRK = 0x5427;
        public const int TIOCCBRK = 0x5428;
        private const int TIOCM_RTS = 0x004;
        private const uint TIOCMGET = 0x5415;
        private const uint TIOCMSET = 0x5418;
        private const int TIOCM_DTR = 0x002;
        private const int O_RDWR = 2;
        private const int O_NOCTTY = 256;
        private const int N_TTY = 0x0010;
        private const int TCIOFLUSH = 2;
        private const int TCSANOW = 0;

        private int _fd = -1;
        private IntPtr _termios;

        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }


        public LinuxInterface(string portName, int baudRate)
        {

            _fd = open(portName, O_RDWR | O_NOCTTY);
            if (_fd == -1)
            {
                throw new IOException($"Failed to open port {portName}");
            }

            // Allocate struct and memory
            Termios term = new Termios();
            _termios = Marshal.AllocHGlobal(Marshal.SizeOf<Termios>());

            // Get tty congiguration
            int r = tcgetattr(_fd, _termios);
            term = Marshal.PtrToStructure<Termios>(_termios);

            // Update termio struct with timeouts
            term.c_iflag = 0;
            term.c_oflag = 0;
            term.c_lflag = 0;

            var timeout = ((IInterface)this).DefaultTimeoutMilliseconds;
            term.c_cc[5] = (byte)(timeout / 100); // VTIME 
            term.c_cc[6] = 0; // VMIN

            // Get a C pointer to struct
            Marshal.StructureToPtr(term, _termios, false);

            // Update configuration
            if (tcsetattr(_fd, TCSANOW, _termios) == -1)
            {
                throw new IOException("Failed to apply uart configuration");
            }

            SetBaudRate(baudRate);
        }

        public void Dispose()
        {
            if (_fd != -1)
            {
                close(_fd);
                _fd = -1;
            }
            if (_termios != IntPtr.Zero)
            {
                free(_termios);
                _termios = IntPtr.Zero;
            }
        }

        public byte ReadByte()
        {
            // Console.WriteLine("XX ReadByte");

            byte[] buffer = new byte[1];
            int bytesRead = read(_fd, buffer, 1);
            if (bytesRead != 1)
            {
                throw new IOException("Failed to read byte from UART");
            }
            return buffer[0];
        }

        public void WriteByteRaw(byte b)
        {
            byte[] buffer = { b };
            int bytesWritten = write(_fd, buffer, 1);
            if (bytesWritten != 1)
            {
                throw new IOException("Failed to write byte to UART");
            }
        }

        public void SetBreak(bool on)
        {
            uint iov = TIOCSBRK;
            if (!on)
            {
                iov = TIOCCBRK;
            }

            int data = 0;

            if (ioctl(_fd, iov, ref data) == -1)
            {
                throw new IOException("Failed to break/unbrake the uart line");
            }

        }

        public void ClearReceiveBuffer()
        {
            tcflush(_fd, TCIOFLUSH);
        }

        public void SetBaudRate(int baudRate)
        {
            int br = 0;

            switch (baudRate)
            {
                case 2400:
                    br = 11;
                    break;
                case 4800:
                    br = 12;
                    break;
                case 9600:
                    br = 13;
                    break;
                case 19200:
                    br = 14;
                    break;
                case 38400:
                    br = 14;
                    break;
                default:
                    throw new IOException("Unknown/invalid baudrate");

            }
            if ((cfsetispeed(_termios, br) == -1) || (cfsetospeed(_termios, br) == -1))
            {
                throw new IOException("Failed to update uart baudrate");
            }


            if (tcsetattr(_fd, TCSANOW, _termios) == -1)
            {
                throw new IOException("Failed to apply uart configuration");
            }

        }




        public void SetParity(Parity parity)
        {
            int cflag = 0;

            // Get current termios settings
            tcgetattr(_fd, _termios);

            int c_cflag_offset = (int)Marshal.OffsetOf<Termios>("c_cflag");

            // Set parity
            switch (parity)
            {
                case Parity.None:
                    cflag &= ~PARENB; // Disable parity
                    break;
                case Parity.Odd:
                    cflag |= PARENB;  // Enable parity
                    cflag |= PARODD;  // Set odd parity
                    break;
                case Parity.Even:
                    cflag |= PARENB;  // Enable parity
                    cflag &= ~PARODD; // Set even parity
                    break;
                case Parity.Mark:
                    // Mark parity is not supported on Linux, set as None
                    cflag &= ~PARENB; // Disable parity
                    break;
                case Parity.Space:
                    // Space parity is not supported on Linux, set as None
                    cflag &= ~PARENB; // Disable parity
                    break;
            }

            // Apply new parity setting
            Marshal.WriteByte(_termios + c_cflag_offset, (byte)cflag);
            // Update termios settings
            if (tcsetattr(_fd, TCSANOW, _termios) == -1)
            {
                throw new IOException("Failed to apply uart baudrate configuration");
            }
        }

        public void SetDtr(bool on)
        {
            // Get the current control lines state
            int controlLinesState = 0;

            if (ioctl(_fd, TIOCMGET, ref controlLinesState) == -1)
            {
                throw new IOException("Failed to get control lines state.");
            }


            // Set DTR flag
            if (on)
                controlLinesState |= TIOCM_DTR;
            else
                controlLinesState &= ~TIOCM_DTR;

            // Set the modified control lines state
            if (ioctl(_fd, TIOCMSET, ref controlLinesState) == -1)
            {
                throw new IOException("Failed to set DTR");
            }
        }

        public void SetRts(bool on)
        {
            // Get the current control lines state
            int controlLinesState = 0;
            if (ioctl(_fd, TIOCMGET, ref controlLinesState) == -1)
            {
                throw new IOException("Failed to get uart line state");
            }

            // Set RTS flag
            if (on)
                controlLinesState |= TIOCM_RTS;
            else
                controlLinesState &= ~TIOCM_RTS;

            // Set the modified control lines state
            if (ioctl(_fd, TIOCMSET, ref controlLinesState) == -1)
            {
                throw new IOException("Failed to set RTS");
            }
        }
    }
}