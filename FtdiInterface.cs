using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FTHandle = System.IntPtr;

namespace BitFab.KW1281Test
{
    class FtdiInterface : IInterface
    {
        private FT _ft = null;
        private FTHandle _handle = FTHandle.Zero;
        private readonly byte[] _buf = new byte[1];

        public FtdiInterface(string serialNumber, int baudRate)
        {
            _ft = new FT();

            var status = _ft.OpenBySerialNumber(
                serialNumber, FT.OpenExFlags.BySerialNumber, out _handle);
            AssertOk(status);

            status = _ft.SetBaudRate(_handle, (uint)baudRate);
            AssertOk(status);

            status = _ft.SetDataCharacteristics(
                _handle,
                FT.Bits.Eight,
                FT.StopBits.One,
                FT.Parity.None);
            AssertOk(status);

            status = _ft.SetFlowControl(
                _handle,
                FT.FlowControl.None, 0, 0);
            AssertOk(status);

            status = _ft.ClrRts(_handle);
            AssertOk(status);

            status = _ft.SetDtr(_handle);
            AssertOk(status);

            status = _ft.SetTimeouts(
                _handle,
                (uint)TimeSpan.FromSeconds(5).TotalMilliseconds,
                (uint)TimeSpan.FromSeconds(5).TotalMilliseconds);
            AssertOk(status);
        }

        public void Dispose()
        {
            if (_handle != FTHandle.Zero)
            {
                var status = _ft.Close(_handle);
                _handle = FTHandle.Zero;
                AssertOk(status);
            }

            if (_ft != null)
            {
                _ft.Dispose();
                _ft = null;
            }
        }

        public byte ReadByte()
        {
            var status = _ft.Read(_handle, _buf, 1, out uint countOfBytesRead);
            AssertOk(status);
            if (countOfBytesRead != 1)
            {
                throw new TimeoutException("Read timed out");
            }

            var b = _buf[0];
            return b;
        }

        public void WriteByte(byte b)
        {
            _buf[0] = b;
            var status = _ft.Write(_handle, _buf, 1, out uint countOfBytesWritten);
            AssertOk(status);
            if (countOfBytesWritten != 1)
            {
                throw new InvalidOperationException(
                    $"Expected to write 1 byte but wrote {countOfBytesWritten} bytes");
            }
            var echo = ReadByte();
            if (echo != b)
            {
                throw new InvalidOperationException($"Wrote 0x{b:X2} to port but echo was 0x{echo:X2}");
            }
        }

        public void BitBang5Baud(byte b, bool evenParity)
        {
            // Disable garbage collection int this time-critical method
            bool noGc = GC.TryStartNoGCRegion(1024 * 1024);

            const int bitsPerSec = 5;
            long ticksPerBit = Stopwatch.Frequency / bitsPerSec;

            var stopWatch = new Stopwatch();
            long maxTick = 0;

            // Delay the appropriate amount and then set/clear the TxD line
            void BitBang(bool bit)
            {
                while (stopWatch.ElapsedTicks < maxTick)
                    ;
                var status = bit ? _ft.SetBreakOff(_handle) : _ft.SetBreakOn(_handle);
                AssertOk(status);

                maxTick += ticksPerBit;
            }

            bool parity = !evenParity; // XORed with each bit to calculate parity bit

            stopWatch.Start();

            BitBang(false); // Start bit

            for (int i = 0; i < 7; i++)
            {
                bool bit = (b & 1) == 1;
                parity ^= bit;
                b >>= 1;

                BitBang(bit);
            }

            BitBang(parity);

            BitBang(true); // Stop bit

            if (noGc)
            {
                GC.EndNoGCRegion();
            }

            // Throw away anything that might be in the receive buffer
            var status = _ft.Purge(_handle, FT.PurgeMask.RX);
            AssertOk(status);
        }

        private void AssertOk(FT.Status status)
        {
            if (status != FT.Status.Ok)
            {
                throw new InvalidOperationException(
                    $"D2xx library returned {status} instead of Ok");
            }
        }
    }

    class FT : IDisposable
    {
        private IntPtr _d2xx = IntPtr.Zero;
        private readonly OpenBySerialNumberDelegate _openBySerialNumber;
        private readonly CloseDelegate _close;
        private readonly SetBaudRateDelegate _setBaudRate;
        private readonly SetDataCharacteristicsDelegate _setDataCharacteristics;
        private SetFlowControlDelegate _setFlowControl;
        private SetDtrDelegate _setDtr;
        private ClrDtrDelegate _clrDtr;
        private SetRtsDelegate _setRts;
        private ClrRtsDelegate _clrRts;
        private SetTimeoutsDelegate _setTimeouts;
        private PurgeDelegate _purge;
        private SetBreakOnDelegate _setBreakOn;
        private SetBreakOffDelegate _setBreakOff;
        private ReadDelegate _read;
        private WriteDelegate _write;

        public FT()
        {
            string libName;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                libName = "libftd2xx.dylib";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                libName = "libftd2xx.so";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                libName = Environment.Is64BitProcess ? "ftd2xx64.dll" : "ftd2xx.dll";
            }
            else
            {
                throw new InvalidOperationException($"Unknown OS: {RuntimeInformation.OSDescription}");
            }

            _d2xx = NativeLibrary.Load(
                libName, typeof(FT).Assembly, DllImportSearchPath.SafeDirectories);

            var export = NativeLibrary.GetExport(_d2xx, "FT_OpenEx");
            _openBySerialNumber = Marshal.GetDelegateForFunctionPointer<OpenBySerialNumberDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_Close");
            _close = Marshal.GetDelegateForFunctionPointer<CloseDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetBaudRate");
            _setBaudRate = Marshal.GetDelegateForFunctionPointer<SetBaudRateDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetDataCharacteristics");
            _setDataCharacteristics = Marshal.GetDelegateForFunctionPointer<SetDataCharacteristicsDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetFlowControl");
            _setFlowControl = Marshal.GetDelegateForFunctionPointer<SetFlowControlDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetDtr");
            _setDtr = Marshal.GetDelegateForFunctionPointer<SetDtrDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_ClrDtr");
            _clrDtr = Marshal.GetDelegateForFunctionPointer<ClrDtrDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetRts");
            _setRts = Marshal.GetDelegateForFunctionPointer<SetRtsDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_ClrRts");
            _clrRts = Marshal.GetDelegateForFunctionPointer<ClrRtsDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetTimeouts");
            _setTimeouts = Marshal.GetDelegateForFunctionPointer<SetTimeoutsDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_Purge");
            _purge = Marshal.GetDelegateForFunctionPointer<PurgeDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetBreakOn");
            _setBreakOn = Marshal.GetDelegateForFunctionPointer<SetBreakOnDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_SetBreakOff");
            _setBreakOff = Marshal.GetDelegateForFunctionPointer<SetBreakOffDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_Read");
            _read = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(export);
            export = NativeLibrary.GetExport(_d2xx, "FT_Write");
            _write = Marshal.GetDelegateForFunctionPointer<WriteDelegate>(export);
        }

        public void Dispose()
        {
            if (_d2xx != IntPtr.Zero)
            {
                NativeLibrary.Free(_d2xx);
                _d2xx = IntPtr.Zero;
            }
        }

        public Status OpenBySerialNumber(
            string serialNumber,
            OpenExFlags flags,
            out FTHandle handle)
        {
            return _openBySerialNumber(serialNumber, flags, out handle);
        }

        private delegate Status OpenBySerialNumberDelegate(
            [MarshalAs(UnmanagedType.LPStr)] string serialNumber,
            OpenExFlags flags,
            out FTHandle handle);

        public Status Close(
            FTHandle handle)
        {
            return _close(handle);
        }

        private delegate Status CloseDelegate(
            FTHandle handle);

        public Status SetBaudRate(
            FTHandle handle,
            uint baudRate)
        {
            return _setBaudRate(handle, baudRate);
        }

        private delegate Status SetBaudRateDelegate(
            FTHandle handle,
            uint baudRate);

        public Status SetDataCharacteristics(
            FTHandle handle,
            Bits wordLength,
            StopBits stopBits,
            Parity parity)
        {
            return _setDataCharacteristics(handle, wordLength, stopBits, parity);
        }

        private delegate Status SetDataCharacteristicsDelegate(
            FTHandle handle,
            Bits wordLength,
            StopBits stopBits,
            Parity parity);

        public Status SetFlowControl(
            FTHandle handle,
            FlowControl flowControl,
            byte xonChar,
            byte xoffChar)
        {
            return _setFlowControl(handle, flowControl, xonChar, xoffChar);
        }

        private delegate Status SetFlowControlDelegate(
            FTHandle handle,
            FlowControl flowControl,
            byte xonChar,
            byte xoffChar);

        public Status SetDtr(
            FTHandle handle)
        {
            return _setDtr(handle);
        }

        public delegate Status SetDtrDelegate(
            FTHandle handle);

        public Status ClrDtr(
            FTHandle handle)
        {
            return _clrDtr(handle);
        }

        private delegate Status ClrDtrDelegate(
            FTHandle handle);

        public Status SetRts(
            FTHandle handle)
        {
            return _setRts(handle);
        }

        private delegate Status SetRtsDelegate(
            FTHandle handle);

        public Status ClrRts(
            FTHandle handle)
        {
            return _clrRts(handle);
        }

        private delegate Status ClrRtsDelegate(
            FTHandle handle);

        public Status SetTimeouts(
            FTHandle handle,
            uint readTimeoutMS,
            uint writeTimeoutMS)
        {
            return _setTimeouts(handle, readTimeoutMS, writeTimeoutMS);
        }

        private delegate Status SetTimeoutsDelegate(
            FTHandle handle,
            uint readTimeoutMS,
            uint writeTimeoutMS);

        public Status Purge(
            FTHandle handle,
            PurgeMask mask)
        {
            return _purge(handle, mask);
        }

        private delegate Status PurgeDelegate(
            FTHandle handle,
            PurgeMask mask);

        public Status SetBreakOn(
            FTHandle handle)
        {
            return _setBreakOn(handle);
        }

        private delegate Status SetBreakOnDelegate(
            FTHandle handle);

        public Status SetBreakOff(
            FTHandle handle)
        {
            return _setBreakOff(handle);
        }

        private delegate Status SetBreakOffDelegate(
            FTHandle handle);

        public Status Read(
            FTHandle handle,
            byte[] buffer,
            uint countOfBytesToRead,
            out uint countOfBytesRead)
        {
            return _read(handle, buffer, countOfBytesToRead, out countOfBytesRead);
        }

        private delegate Status ReadDelegate(
            FTHandle handle,
            byte[] buffer,
            uint countOfBytesToRead,
            out uint countOfBytesRead);

        public Status Write(
            FTHandle handle,
            byte[] buffer,
            uint countOfBytesToWrite,
            out uint countOfBytesWritten)
        {
            return _write(handle, buffer, countOfBytesToWrite, out countOfBytesWritten);
        }

        private delegate Status WriteDelegate(
            FTHandle handle,
            byte[] buffer,
            uint countOfBytesToWrite,
            out uint countOfBytesWritten);

        public enum Status : uint
        {
            Ok = 0,
            InvalidHandle,
            DeviceNotFound,
            DeviceNotOpened,
            IOError,
            insufficient_resources,
            InvalidParameter,
            InvalidBaudRate,
            DeviceNotOpenedForErase,
            DeviceNotOpenedForWrite,
            FailedToWriteDevice,
            EepromReadFailed,
            EepromWriteFailed,
            EepromEraseFailed,
            EepromNotPresent,
            EepromNotProgrammed,
            InvalidArgs,
            NotSupported,
            OtherError,
            DeviceListNotReady,
        };

        [Flags]
        public enum OpenExFlags : uint
        {
            BySerialNumber = 1,
            ByDescription = 2,
            ByLocation = 4
        };

        public enum Bits : byte
        {
            Eight = 8,
            Seven = 7
        };

        public enum StopBits : byte
        {
            One = 0,
            Two = 2
        };

        public enum Parity : byte
        {
            None = 0,
            Odd = 1,
            Even = 2,
            Mark = 3,
            Space = 4
        };

        public enum FlowControl : ushort
        {
            None = 0x0000,
            RtsCts = 0x0100,
            DtrDsr = 0x0200,
            XonXoff = 0x0400
        };

        [Flags]
        public enum PurgeMask : uint
        {
            RX = 1,
            TX = 2
        };
    }
}
