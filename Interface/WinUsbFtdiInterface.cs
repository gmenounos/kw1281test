using System;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BitFab.KW1281Test.Interface;

/// <summary>
/// An FTDI chip through the stock Windows WinUSB driver - no cable vendor driver or library.
/// The chip protocol is in <see cref="FtdiProtocol"/>; the order and values of the requests
/// follow what Ross-Tech's library does in the captures.
///
/// WinUSB has to be bound to the device, e.g. with Zadig.
/// </summary>
internal sealed class WinUsbFtdiInterface : IFtdiPort
{
    private readonly SafeFileHandle _file;
    private IntPtr _winUsb;

    private readonly byte[] _packet = new byte[FtdiProtocol.PacketSize * 8];
    private readonly byte[] _rx = new byte[FtdiProtocol.PacketSize * 8];
    private int _rxPosition;
    private int _rxLength;

    private readonly byte[] _one = new byte[1];
    private Parity _parity = Parity.None;
    private uint _inPipeTimeout;
    private uint _outPipeTimeout;

    public WinUsbFtdiInterface(UsbCableInfo cable, int baudRate)
    {
        var path = UsbCableLocator.FindWinUsbDevicePath(cable)
            ?? throw new IOException(
                "The cable uses the WinUSB driver, but Windows does not report a path to it. "
                + "Reconnect the cable; if that does not help, reinstall WinUSB.");

        _file = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new IOException(
                $"Unable to open the cable (error {error}): {new Win32Exception(error).Message}. "
                + "Another program may be using it.");
        }

        if (!WinUsb_Initialize(_file, out _winUsb))
        {
            var error = Marshal.GetLastWin32Error();
            _file.Dispose();
            throw new IOException($"WinUSB did not accept the cable (error {error}): {new Win32Exception(error).Message}");
        }

        try
        {
            // The same order as the D2XX library on open: reset, purge, baud rate, line
            // properties, no flow control, RTS low, DTR high.
            Control(FtdiProtocol.Reset, FtdiProtocol.ResetSio);
            Control(FtdiProtocol.Reset, FtdiProtocol.PurgeRx);
            Control(FtdiProtocol.Reset, FtdiProtocol.PurgeTx);
            SetBaudRate(baudRate);
            SetParity(Parity.None);
            Control(FtdiProtocol.SetFlowControl, 0);
            SetRts(false);
            SetDtr(true);

            _readTimeout = _writeTimeout = ((IInterface)this).DefaultTimeoutMilliseconds;
            SetLatencyTimer(2);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_winUsb != IntPtr.Zero)
        {
            try { SetDtr(false); } catch { /* the cable may already be unplugged */ }
            WinUsb_Free(_winUsb);
            _winUsb = IntPtr.Zero;
        }
        _file.Dispose();
    }

    private int _readTimeout;

    public int ReadTimeout
    {
        get => _readTimeout;
        set => _readTimeout = value;
    }

    private int _writeTimeout;

    public int WriteTimeout
    {
        get => _writeTimeout;
        set => _writeTimeout = value;
    }

    public byte ReadByte()
    {
        if (_rxPosition < _rxLength) return _rx[_rxPosition++];

        // Even with no data the chip sends a packet of two status bytes once per latency
        // timer period (2 ms), so a read never blocks for long and our own timeout holds.
        var deadline = Stopwatch.GetTimestamp() + (long)_readTimeout * Stopwatch.Frequency / 1000;
        while (true)
        {
            if (ReadPacket() > 0) return _rx[_rxPosition++];
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Read timed out");
        }
    }

    /// <summary>One bulk IN read. Returns how many data bytes were added to the buffer.</summary>
    private int ReadPacket()
    {
        SetPipeTimeout(FtdiProtocol.BulkIn, ref _inPipeTimeout,
            (uint)Math.Clamp(_readTimeout, 1, 200));

        if (!WinUsb_ReadPipe(_winUsb, FtdiProtocol.BulkIn, _packet, (uint)_packet.Length, out var received, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_SEM_TIMEOUT) return 0;
            throw new IOException($"Reading from the cable failed (error {error}): {new Win32Exception(error).Message}");
        }

        _rxPosition = 0;
        _rxLength = FtdiProtocol.ExtractPayload(_packet.AsSpan(0, (int)received), _rx);
        return _rxLength;
    }

    public void WriteByteRaw(byte b)
    {
        _one[0] = b;
        SetPipeTimeout(FtdiProtocol.BulkOut, ref _outPipeTimeout, (uint)Math.Max(_writeTimeout, 1));
        if (!WinUsb_WritePipe(_winUsb, FtdiProtocol.BulkOut, _one, 1, out var written, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Writing to the cable failed (error {error}): {new Win32Exception(error).Message}");
        }

        if (written != 1)
        {
            throw new InvalidOperationException($"Expected to write 1 byte but wrote {written} bytes");
        }
    }

    public void SetBreak(bool on) =>
        Control(FtdiProtocol.SetData, FtdiProtocol.EncodeLineProperties(_parity, on));

    public void ClearReceiveBuffer()
    {
        Control(FtdiProtocol.Reset, FtdiProtocol.PurgeRx);
        DrainReceived();
    }

    public void PurgeBuffers()
    {
        Control(FtdiProtocol.Reset, FtdiProtocol.PurgeRx);
        Control(FtdiProtocol.Reset, FtdiProtocol.PurgeTx);
        DrainReceived();
    }

    /// <summary>
    /// Forget everything already received. The purge clears the chip's own buffer, but a
    /// packet sent before it may already be on its way over USB, so read until the first
    /// packet without data. The D2XX library in the capture does the same, repeating the
    /// purge up to ten times.
    /// </summary>
    private void DrainReceived()
    {
        _rxPosition = _rxLength = 0;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (ReadPacket() == 0) break;
        }
        _rxPosition = _rxLength = 0;
    }

    public void SetBaudRate(int baudRate)
    {
        var (value, index) = FtdiProtocol.EncodeBaudRate(baudRate);
        Control(FtdiProtocol.SetBaudRate, value, index);
    }

    public void SetParity(Parity parity)
    {
        Control(FtdiProtocol.SetData, FtdiProtocol.EncodeLineProperties(parity, breakOn: false));
        _parity = parity;
    }

    public void SetDtr(bool on) =>
        Control(FtdiProtocol.ModemControl, on ? FtdiProtocol.DtrOn : FtdiProtocol.DtrOff);

    public void SetRts(bool on) =>
        Control(FtdiProtocol.ModemControl, on ? FtdiProtocol.RtsOn : FtdiProtocol.RtsOff);

    public void SetLatencyTimer(byte milliseconds) =>
        Control(FtdiProtocol.SetLatencyTimer, milliseconds);

    private void Control(byte request, ushort value, ushort index = 0)
    {
        var setup = new WINUSB_SETUP_PACKET
        {
            RequestType = FtdiProtocol.RequestTypeOut,
            Request = request,
            Value = value,
            Index = index,
            Length = 0,
        };

        if (!WinUsb_ControlTransfer(_winUsb, setup, null, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"The cable rejected request {request:X2} (error {error}): {new Win32Exception(error).Message}");
        }
    }

    /// <summary>Changes the pipe timeout only when it really differs: each change is a separate driver call.</summary>
    private void SetPipeTimeout(byte pipe, ref uint current, uint milliseconds)
    {
        if (current == milliseconds) return;
        var value = milliseconds;
        if (!WinUsb_SetPipePolicy(_winUsb, pipe, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref value))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"WinUSB rejected the pipe timeout (error {error}): {new Win32Exception(error).Message}");
        }
        current = milliseconds;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TRANSFER_TIMEOUT = 0x03;
    private const int ERROR_SEM_TIMEOUT = 121;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WINUSB_SETUP_PACKET
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(IntPtr interfaceHandle, WINUSB_SETUP_PACKET setup,
        byte[]? buffer, uint bufferLength, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType,
        uint valueLength, ref uint value);
}
