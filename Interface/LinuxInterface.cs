using System;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Ports;

using tcflag_t = System.UInt32;
using cc_t = System.Byte;
using speed_t = System.UInt32;

namespace BitFab.KW1281Test.Interface;

public class LinuxInterface : IInterface
{
    private const uint CBAUD  = 0x100F; // Clear normal baudrates
    private const uint CBAUDEX = 0x1000;
    private const uint BOTHER = 0x1000; // Other baudrate
    private const int IBSHIFT = 16; // Shift from CBAUD to CIBAUD
    private const uint PARENB = 0x0100; // Enable parity bit
    private const uint PARODD = 0x0200; // Use odd parity rather than even parity

    private const string libc = "libc";

#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time

    // Linux ioctl function
    [DllImport(libc, SetLastError = true)]
    private static extern int ioctl(int fd, int request, ref int data);

    [DllImport(libc, SetLastError = true)]
    private static extern int ioctl(int fd, uint request, IntPtr data);

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

#pragma warning restore SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time

    private const int NCCS = 19;

    // Define the termios structure to interact with terminal I/O settings
    [StructLayout(LayoutKind.Sequential)]
    public struct Termios
    {
        public tcflag_t c_iflag;    // input mode flags
        public tcflag_t c_oflag;    // output mode flags
        public tcflag_t c_cflag;    // control mode flags
        public tcflag_t c_lflag;    // local mode flags
        public cc_t c_line;         // line discipline

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NCCS)]
        public cc_t[] c_cc;         // control characters

        public speed_t c_ispeed;    // input speed
        public speed_t c_ospeed;    // output speed
    }

    private const int _IOC_NRBITS =	8;
    private const int _IOC_TYPEBITS	= 8;
    private const int _IOC_SIZEBITS = 14;

    private const int _IOC_NRSHIFT = 0;
    private const int _IOC_TYPESHIFT = (_IOC_NRSHIFT + _IOC_NRBITS);
    private const int _IOC_SIZESHIFT = (_IOC_TYPESHIFT + _IOC_TYPEBITS);
    private const int _IOC_DIRSHIFT = (_IOC_SIZESHIFT + _IOC_SIZEBITS);

    private const int _IOC_READ = 2;
    private const int _IOC_WRITE = 1;

#pragma warning disable IDE1006 // Naming Styles
    private static uint _IOC(int dir, int type, int nr, int size)
    {
        return (uint)((dir << _IOC_DIRSHIFT) |
            (type << _IOC_TYPESHIFT) |
            (nr   << _IOC_NRSHIFT) |
            (size << _IOC_SIZESHIFT));
    }

    private static int _IOC_TYPECHECK(Type type)
    {
        return Marshal.SizeOf(type);
    }

    private static uint _IOR(int type, int nr, Type size)
    {
        return _IOC(_IOC_READ, type, nr, _IOC_TYPECHECK(size));
    }

    private static uint _IOW(int type, int nr, Type size)
    {
        return _IOC(_IOC_WRITE, type, nr, _IOC_TYPECHECK(size));
    }
#pragma warning restore IDE1006 // Naming Styles

    private static readonly uint TCGETS2 = _IOR('T', 0x2A, typeof(Termios));
    private static readonly uint TCSETS2 = _IOW('T', 0x2B, typeof(Termios));

    private const int TIOCSBRK = 0x5427;
    private const int TIOCCBRK = 0x5428;
    private const int TIOCM_RTS = 0x004;
    private const int TIOCMGET = 0x5415;
    private const int TIOCMSET = 0x5418;
    private const int TIOCM_DTR = 0x002;
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 00000400;
    private const int TCIFLUSH = 0; // Discard data received but not yet read

    private const int VTIME = 5;
    private const int VMIN = 6;

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
        _termios = Marshal.AllocHGlobal(Marshal.SizeOf<Termios>());

        var termios = GetTtyConfiguration();

        // Update termio struct with timeouts
        termios.c_iflag = 0;
        termios.c_oflag = 0;
        termios.c_lflag = 0;

        var timeout = ((IInterface)this).DefaultTimeoutMilliseconds;
        termios.c_cc[VTIME] = (byte)(timeout / 100);
        termios.c_cc[VMIN] = 0;

        SetTtyConfiguration(termios);

        SetBaudRate(baudRate);
    }

    private Termios GetTtyConfiguration()
    {
        if (ioctl(_fd, TCGETS2, _termios) == -1)
        {
            throw new IOException("Failed to get the UART configuration");
        }
        var termios = Marshal.PtrToStructure<Termios>(_termios);
        return termios;
    }

    private void SetTtyConfiguration(Termios termios)
    {
        // Get a C pointer to struct
        Marshal.StructureToPtr(termios, _termios, fDeleteOld: true);

        // Update configuration
        if (ioctl(_fd, TCSETS2, _termios) == -1)
        {
            throw new IOException("Failed to set the UART configuration");
        }
    }

    private readonly byte[] _buffer = new byte[1];

    public byte ReadByte()
    {
        // Console.WriteLine("XX ReadByte");

        int bytesRead = read(_fd, _buffer, 1);
        if (bytesRead != 1)
        {
            throw new IOException("Failed to read byte from UART");
        }
        return _buffer[0];
    }

    public void WriteByteRaw(byte b)
    {
        _buffer[0] = b;
        int bytesWritten = write(_fd, _buffer, 1);
        if (bytesWritten != 1)
        {
            throw new IOException("Failed to write byte to UART");
        }
    }

    public void SetBreak(bool on)
    {
         var iov = on ? TIOCSBRK : TIOCCBRK;

        int data = 0;

        if (ioctl(_fd, iov, ref data) == -1)
        {
            throw new IOException("Failed to set/clear UART break");
        }
    }

    public void ClearReceiveBuffer()
    {
        if (tcflush(_fd, TCIFLUSH) == -1)
        {
            throw new IOException("Failed to clear the UART receive buffer");
        }
    }

    public void SetBaudRate(int baudRate)
    {
        var termios = GetTtyConfiguration();

        // Output speed
        termios.c_cflag &= ~(CBAUD | CBAUDEX);
        termios.c_cflag |= BOTHER;
        termios.c_ospeed = (uint)baudRate;

        // Input speed
        termios.c_cflag &= ~((CBAUD | CBAUDEX) << IBSHIFT);
        termios.c_cflag |= (BOTHER << IBSHIFT);
        termios.c_ispeed = (uint)baudRate;

        SetTtyConfiguration(termios);
    }

    public void SetParity(Parity parity)
    {
        var termios = GetTtyConfiguration();

        // Set parity
        switch (parity)
        {
            case Parity.None:
                termios.c_cflag &= ~PARENB; // Disable parity
                break;
            case Parity.Odd:
                termios.c_cflag |= PARENB;  // Enable parity
                termios.c_cflag |= PARODD;  // Set odd parity
                break;
            case Parity.Even:
                termios.c_cflag |= PARENB;  // Enable parity
                termios.c_cflag &= ~PARODD; // Set even parity
                break;
            case Parity.Mark:
                // Mark parity is not supported on Linux, set as None
                termios.c_cflag &= ~PARENB; // Disable parity
                break;
            case Parity.Space:
                // Space parity is not supported on Linux, set as None
                termios.c_cflag &= ~PARENB; // Disable parity
                break;
        }

        SetTtyConfiguration(termios);
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
        {
            controlLinesState |= TIOCM_DTR;
        }
        else
        {
            controlLinesState &= ~TIOCM_DTR;
        }

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

    public void Dispose()
    {
        // Dispose of unmanaged resources.
        Dispose(true);

        // Suppress finalization.
        GC.SuppressFinalize(this);
    }

    private bool _disposed = false;

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // TODO: Dispose managed state (managed objects).
        }

        // Free unmanaged resources (unmanaged objects) and override a finalizer below.

        if (_termios != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_termios);
            _termios = IntPtr.Zero;
        }
        if (_fd != -1)
        {
            _ = close(_fd);
            _fd = -1;
        }

        // TODO: Set large fields to null.

        _disposed = true;
    }
}
