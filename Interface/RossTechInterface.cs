using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;

namespace BitFab.KW1281Test.Interface
{
    /// <summary>
    /// Transport profile for Ross-Tech HEX adapters (HEX-USB, HEX-V2), USB VID_0403&amp;PID_FA24.
    ///
    /// The cable speaks the same adapter-assisted wake-up protocol as VasyaDiagnost - the
    /// request is $53 $07 $84 &lt;address&gt; $1E $03 &lt;xor&gt; and the reply is a $4D frame - which
    /// is unsurprising, since the Vasya firmware reproduces Ross-Tech's. Only the port profile
    /// differs, so the framing itself lives in <see cref="AssistedWakeupProtocol"/>.
    ///
    /// The cable itself is a plain FT232R, so on Windows the driver already bound to it
    /// decides how we reach it; nothing gets installed:
    ///   - Ross-Tech's own driver ("Ross-Tech Direct USB Interface"): this is FTDI's FTDIBUS
    ///     driver built for Ross-Tech's product ids, exposing the standard D2XX interface.
    ///     FTDI's own D2XX library works with it (verified with ftd2xx 3.2.21.1, x86 and x64);
    ///     without it we fall back to Ross-Tech's copy of the library (RT-USB.dll), which
    ///     ships with the driver package (32-bit only) and with VCDS;
    ///   - WinUSB (e.g. installed with Zadig): through our own FTDI transport,
    ///     <see cref="WinUsbFtdiInterface"/>, with no vendor library at all.
    /// A cable on Ross-Tech's Virtual COM Port driver is a COM port instead; see
    /// <see cref="RossTechSerialInterface"/>.
    ///
    /// The USB command channel runs at 115200, just like the Vasya profile. This was confirmed
    /// by successful traces from a HEX-USB (serial RT000001, PID FA24) through a logging shim
    /// over Ross-Tech's own RT-USB.dll. The 9600/10400 baud given on the command line is the
    /// K-line speed handled behind the adapter firmware, not the baud rate of command $84.
    /// </summary>
    internal sealed class RossTechInterface : IInterface, IAssistedWakeupInterface
    {
        /// <summary>USB PID of the Ross-Tech HEX-USB; VasyaDiagnost uses FA3F.</summary>
        private const uint RossTechProductId = 0xFA24;

        private readonly IFtdiPort _inner;

        public RossTechInterface(string serialNumber, int baudRate)
        {
            _inner = OpenPort(serialNumber, baudRate);
        }

        private static IFtdiPort OpenPort(string serialNumber, int baudRate)
        {
            // On macOS the FTDI library opens the cable by itself once it knows the PID.
            if (!OperatingSystem.IsWindows())
            {
                return OpenOverVendorLibrary(serialNumber, baudRate, library: null);
            }

            var cable = UsbCableLocator.FindRossTechCable(serialNumber);
            Log.WriteLine(
                $"Ross-Tech HEX {serialNumber}: driver {cable.Driver} (service {cable.Service ?? "none"})");

            switch (cable.Driver)
            {
                case UsbCableDriver.NotConnected:
                    throw new IOException(
                        $"Ross-Tech HEX cable {serialNumber} was not found. Check that it is plugged in.");

                case UsbCableDriver.WinUsb:
                    Log.WriteLine($"Opening Ross-Tech HEX {serialNumber} over WinUSB");
                    return new WinUsbFtdiInterface(cable, baudRate);

                case UsbCableDriver.RossTech:
                    // KW1281_FTDI_DLL, when set, is loaded as is (see FT). Otherwise FTDI's own
                    // D2XX library, if installed, and only then Ross-Tech's copy of it.
                    var configured = Environment.GetEnvironmentVariable("KW1281_FTDI_DLL");
                    if (!string.IsNullOrWhiteSpace(configured) || IsFtdiLibraryAvailable())
                    {
                        if (string.IsNullOrWhiteSpace(configured))
                            Log.WriteLine("Using FTDI's D2XX library with the Ross-Tech driver");
                        return OpenOverVendorLibrary(serialNumber, baudRate, library: null);
                    }

                    var library = FindRossTechLibrary();
                    if (library == null)
                    {
                        throw new IOException(
                            "The Ross-Tech HEX cable uses the Ross-Tech driver, but no D2XX library was found. "
                            + "Put FTDI's ftd2xx64.dll (x64) or ftd2xx.dll (x86) next to kw1281test, set "
                            + "KW1281_FTDI_DLL to its full path, or switch the cable to the WinUSB driver.");
                    }
                    return OpenOverVendorLibrary(serialNumber, baudRate, library);

                case UsbCableDriver.Other:
                    throw new IOException(
                        $"The Ross-Tech HEX cable uses the {cable.Service} driver. If that is the "
                        + "Virtual COM Port driver, use PORT hex:COMx; otherwise install the Ross-Tech "
                        + "driver or WinUSB (e.g. with Zadig).");

                default:
                    throw new IOException(
                        "The Ross-Tech HEX cable has no driver. Install the Ross-Tech driver that "
                        + "comes with the cable, or WinUSB (e.g. with Zadig).");
            }
        }

        /// <summary>
        /// Opens the cable through a D2XX library: FTDI's own when <paramref name="library"/> is
        /// null, otherwise the given one (Ross-Tech's RT-USB.dll exports the same FT_* functions).
        /// The same profile as VasyaDiagnost: both are FTDI devices with a vendor product id.
        /// </summary>
        private static IFtdiPort OpenOverVendorLibrary(string serialNumber, int baudRate, string? library) =>
            new FtdiInterface(serialNumber, baudRate, vendorProfile: true,
                preferredLibrary: library,
                customProductId: RossTechProductId);

        /// <summary>Whether FTDI's own D2XX library for this process can be loaded.</summary>
        private static bool IsFtdiLibraryAvailable()
        {
            var name = Environment.Is64BitProcess ? "ftd2xx64.dll" : "ftd2xx.dll";
            if (!NativeLibrary.TryLoad(name, typeof(RossTechInterface).Assembly,
                    DllImportSearchPath.SafeDirectories, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }

        /// <summary>
        /// The path to Ross-Tech's D2XX library for this process architecture, or null.
        /// Looks next to the program, in the Ross-Tech driver package in the Windows driver
        /// store, and where VCDS is installed. The PE header is checked, so a library for
        /// another architecture is never picked.
        /// </summary>
        internal static string? FindRossTechLibrary(
            string? applicationDirectory = null,
            IEnumerable<string>? searchDirectories = null,
            Architecture? processArchitecture = null)
        {
            // An explicit directory list is used by the tests and does not depend on the OS.
            if (!OperatingSystem.IsWindows() && searchDirectories == null) return null;

            var architecture = processArchitecture ?? RuntimeInformation.ProcessArchitecture;

            // VCDS ships RT-USB.dll for x86 and RTUS64.dll for x64; the other names show up
            // in older installations.
            var names = LibraryNamesFor(architecture);

            var directories = new List<string>
            {
                applicationDirectory ?? AppContext.BaseDirectory,
            };

            if (searchDirectories != null)
            {
                directories.AddRange(searchDirectories);
            }
            else
            {
                directories.AddRange(FindDriverPackageDirectories());
                directories.AddRange(FindInstalledVcdsDirectories());
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory, name);
                    if (!seen.Add(candidate) || !File.Exists(candidate)) continue;
                    if (!IsCompatiblePeLibrary(candidate, architecture))
                    {
                        Log.WriteLine($"Ignoring Ross-Tech library for another architecture: {candidate}");
                        continue;
                    }

                    Log.WriteLine($"Found the Ross-Tech library for {architecture}: {candidate}");
                    return candidate;
                }
            }

            Log.WriteLine($"A compatible Ross-Tech D2XX library for {architecture} was not found.");
            return null;
        }

        /// <summary>Library names in order of preference for this architecture.</summary>
        private static string[] LibraryNamesFor(Architecture architecture) =>
            architecture == Architecture.X86
                ? ["RT-USB.dll", "RTUS64.dll", "RT-USB64.dll"]
                : ["RTUS64.dll", "RT-USB64.dll", "RT-USB.dll"];

        /// <summary>
        /// Ross-Tech driver package folders in the Windows driver store. The driver installer
        /// also copies RT-USB.DLL into System32, but that copy is 32-bit and a 32-bit process
        /// never sees it there (its System32 is SysWOW64). The driver store has no such
        /// redirection, so the path is the same for a process of any bitness.
        /// </summary>
        internal static IEnumerable<string> FindDriverPackageDirectories(string? fileRepository = null)
        {
            if (fileRepository == null)
            {
                if (!OperatingSystem.IsWindows()) yield break;
                fileRepository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            }

            string[] packages;
            try
            {
                // rt-usb.inf_x86_..., rt-usb64.inf_amd64_... depending on the driver build.
                packages = Directory.EnumerateDirectories(fileRepository, "rt-usb*").ToArray();
            }
            catch
            {
                yield break;
            }

            foreach (var package in packages)
                yield return package;
        }

        private static IEnumerable<string> FindInstalledVcdsDirectories()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            // The VCDS installer records where VCDS.exe went.
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string? exePath = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Ross-Tech\VCDS\Release\VCDS.exe");
                    exePath = key?.GetValue(null) as string;
                }
                catch
                {
                    // No access or no key: try the next source.
                }

                var directory = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
                if (!string.IsNullOrWhiteSpace(directory)) yield return directory;
            }

            // The usual install locations, including side-by-side versions ("VCDS 18.2.0").
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Path.GetPathRoot(Environment.SystemDirectory),
                     })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var rossTechRoot = Path.Combine(root, "Ross-Tech");
                if (!Directory.Exists(rossTechRoot)) continue;

                string[] versioned;
                try
                {
                    versioned = Directory.EnumerateDirectories(rossTechRoot, "VCDS*").ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var directory in versioned)
                    yield return directory;
            }
        }

        internal static bool IsCompatiblePeLibrary(string path, Architecture architecture)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;
                stream.Position = 0x3C;
                var peOffset = reader.ReadInt32();
                if (peOffset < 0 || peOffset > stream.Length - 6) return false;
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) return false;
                var machine = reader.ReadUInt16();
                var expectedMachine = architecture switch
                {
                    Architecture.X86 => 0x014C,
                    Architecture.X64 => 0x8664,
                    Architecture.Arm => 0x01C4,
                    Architecture.Arm64 => 0xAA64,
                    _ => 0,
                };
                return expectedMachine != 0 && machine == expectedMachine;
            }
            catch
            {
                return false;
            }
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

            // This is the speed of the adapter firmware's command channel. After a successful
            // reply the firmware itself drives the K-line at the requested 9600/10400.
            _inner.SetBaudRate(AssistedWakeupProtocol.AdapterCommandBaudRate);
            _inner.SetParity(Parity.None);
            _inner.SetLatencyTimer(2);
            _inner.SetDtr(false);
            _inner.SetRts(false);
            _inner.PurgeBuffers();

            return AssistedWakeupProtocol.WakeUp(_inner, controllerAddress, "Ross-Tech");
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
