using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FTHandle = System.IntPtr;

namespace BitFab.KW1281Test
{
    class FtdiInterface : IInterface
    {
        public FtdiInterface(string serialNumber, int baudRate)
        {
            var status = FT.OpenBySerialNumber(
                serialNumber, FT.OpenExFlags.BySerialNumber, out _handle);
            AssertOk(status);

            status = FT.SetBaudRate(_handle, (uint)baudRate);
            AssertOk(status);

            status = FT.SetDataCharacteristics(
                _handle,
                FT.Bits.Eight,
                FT.StopBits.One,
                FT.Parity.None);
            AssertOk(status);

            status = FT.SetFlowControl(
                _handle,
                FT.FlowControl.None, 0, 0);
            AssertOk(status);

            status = FT.ClrRts(_handle);
            AssertOk(status);

            status = FT.SetDtr(_handle);
            AssertOk(status);

            status = FT.SetTimeouts(
                _handle,
                (uint)TimeSpan.FromSeconds(5).TotalMilliseconds,
                (uint)TimeSpan.FromSeconds(5).TotalMilliseconds);
            AssertOk(status);
        }

        public void Dispose()
        {
            if (_handle != FTHandle.Zero)
            {
                var status = FT.Close(_handle);
                _handle = FTHandle.Zero;
                AssertOk(status);
            }
        }

        public byte ReadByte()
        {
            var status = FT.Read(_handle, _buf, 1, out uint countOfBytesRead);
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
            var status = FT.Write(_handle, _buf, 1, out uint countOfBytesWritten);
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
                var status = bit ? FT.SetBreakOff(_handle) : FT.SetBreakOn(_handle);
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
            var status = FT.Purge(_handle, FT.PurgeMask.RX);
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

        private FTHandle _handle = FTHandle.Zero;
        private readonly byte[] _buf = new byte[1];
    }

    static class FT
    {
#if BUILT_FOR_WINDOWS32
        const string D2XXDll = "ftd2xx.dll";
#elif BUILT_FOR_WINDOWS64
        const string D2XXDll = "ftd2xx64.dll";
#elif BUILT_FOR_MACOS
        const string D2XXDll = "libftd2xx.dylib";
#elif BUILT_FOR_LINUX
        const string D2XXDll = "libftd2xx.so";
#endif

        [DllImport(D2XXDll, EntryPoint = "FT_OpenEx")]
        public static extern Status OpenBySerialNumber(
#pragma warning disable CA2101 // Specify marshaling for P/Invoke string arguments
            [MarshalAs(UnmanagedType.LPStr)] string serialNumber,
#pragma warning restore CA2101 // Specify marshaling for P/Invoke string arguments
            OpenExFlags flags,
            out FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_Close")]
        public static extern Status Close(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_SetBaudRate")]
        public static extern Status SetBaudRate(
            FTHandle handle,
            uint baudRate);

        [DllImport(D2XXDll, EntryPoint = "FT_SetDataCharacteristics")]
        public static extern Status SetDataCharacteristics(
            FTHandle handle,
            Bits wordLength,
            StopBits stopBits,
            Parity parity);

        [DllImport(D2XXDll, EntryPoint = "FT_SetFlowControl")]
        public static extern Status SetFlowControl(
            FTHandle handle,
            FlowControl flowControl,
            byte xonChar,
            byte xoffChar);

        [DllImport(D2XXDll, EntryPoint = "FT_SetDtr")]
        public static extern Status SetDtr(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_ClrDtr")]
        public static extern Status ClrDtr(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_SetRts")]
        public static extern Status SetRts(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_ClrRts")]
        public static extern Status ClrRts(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_SetTimeouts")]
        public static extern Status SetTimeouts(
            FTHandle handle,
            uint readTimeoutMS,
            uint writeTimeoutMS);

        [DllImport(D2XXDll, EntryPoint = "FT_Purge")]
        public static extern Status Purge(
            FTHandle handle,
            PurgeMask mask);

        [DllImport(D2XXDll, EntryPoint = "FT_SetBreakOn")]
        public static extern Status SetBreakOn(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_SetBreakOff")]
        public static extern Status SetBreakOff(
            FTHandle handle);

        [DllImport(D2XXDll, EntryPoint = "FT_Read")]
        public static extern Status Read(
            FTHandle handle,
            byte[] buffer,
            uint countOfBytesToRead,
            out uint countOfBytesRead);

        [DllImport(D2XXDll, EntryPoint = "FT_Write")]
        public static extern Status Write(
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
