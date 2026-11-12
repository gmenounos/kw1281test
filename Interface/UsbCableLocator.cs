using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BitFab.KW1281Test.Interface;

/// <summary>Which Windows driver currently serves the cable.</summary>
internal enum UsbCableDriver
{
    /// <summary>No cable with this serial number is connected.</summary>
    NotConnected,

    /// <summary>The cable is connected but has no driver at all.</summary>
    None,

    /// <summary>
    /// The stock Windows WinUSB driver, used by our own transport. Any installer will do
    /// (e.g. Zadig): the device path is taken from the device's own settings.
    /// </summary>
    WinUsb,

    /// <summary>Ross-Tech's own driver ("Ross-Tech Direct USB Interface").</summary>
    RossTech,

    /// <summary>Some other driver, e.g. the FTDI or Ross-Tech Virtual COM Port driver.</summary>
    Other,
}

/// <summary>A found cable: device instance id, driver service and WinUSB interface GUIDs.</summary>
internal sealed record UsbCableInfo(string InstanceId, string? Service, UsbCableDriver Driver)
{
    /// <summary>Interface GUIDs registered by the driver INF (WinUSB devices are opened by them).</summary>
    public IReadOnlyList<Guid> InterfaceGuids { get; init; } = [];
}

/// <summary>
/// Finds a Ross-Tech HEX cable among the Windows devices and tells which driver it uses,
/// so the transport knows how to open it.
/// </summary>
internal static class UsbCableLocator
{
    /// <summary>USB PIDs of Ross-Tech HEX cables: FA24 for the HEX-USB, FA44/FA46 for others.</summary>
    public static readonly IReadOnlyList<int> RossTechProductIds = [0xFA24, 0xFA44, 0xFA46];

    private static readonly Regex InstanceIdPattern = new(
        @"\AUSB\\VID_0403&PID_(FA24|FA44|FA46)\\([A-Za-z0-9]+)\z",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>What the Ross-Tech cable with this serial number uses right now.</summary>
    public static UsbCableInfo FindRossTechCable(string serialNumber)
    {
        if (!OperatingSystem.IsWindows())
            return new UsbCableInfo(string.Empty, null, UsbCableDriver.NotConnected);

        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == InvalidHandle)
            return new UsbCableInfo(string.Empty, null, UsbCableDriver.NotConnected);

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buffer = new StringBuilder(512);
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                buffer.Clear();
                if (!SetupDiGetDeviceInstanceId(set, ref data, buffer, (uint)buffer.Capacity, out _))
                    continue;

                var instanceId = buffer.ToString();
                var match = InstanceIdPattern.Match(instanceId);
                if (!match.Success ||
                    !string.Equals(match.Groups[2].Value, serialNumber, StringComparison.OrdinalIgnoreCase))
                    continue;

                var service = ReadStringProperty(set, ref data, SPDRP_SERVICE);
                return new UsbCableInfo(instanceId, service, ClassifyService(service))
                {
                    InterfaceGuids = ReadInterfaceGuids(set, ref data),
                };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return new UsbCableInfo(string.Empty, null, UsbCableDriver.NotConnected);
    }

    internal static UsbCableDriver ClassifyService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service)) return UsbCableDriver.None;
        if (string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase)) return UsbCableDriver.WinUsb;
        // The Ross-Tech driver service is RT-USB (RTUS64 in 64-bit builds).
        if (service.StartsWith("RT-USB", StringComparison.OrdinalIgnoreCase) ||
            service.StartsWith("RTUS", StringComparison.OrdinalIgnoreCase))
            return UsbCableDriver.RossTech;
        return UsbCableDriver.Other;
    }

    /// <summary>
    /// The WinUSB interface path for CreateFile. The interface GUID comes from the device's
    /// own settings, written by whatever INF installed WinUSB.
    /// </summary>
    internal static string? FindWinUsbDevicePath(UsbCableInfo cable)
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var guid in cable.InterfaceGuids)
        {
            var interfaceGuid = guid;
            // With DIGCF_DEVICEINTERFACE the second argument can be a device instance id,
            // which limits the set to the interfaces of that device only.
            var set = SetupDiGetClassDevs(ref interfaceGuid, cable.InstanceId, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == InvalidHandle) continue;

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
                };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, 0, ref interfaceData))
                    continue;

                SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;

                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // cbSize is the size of the structure header, not of the whole buffer: 8 in
                    // a 64-bit process and 6 in a 32-bit one (a DWORD and one WCHAR, packed).
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        return null;
    }

    private static List<Guid> ReadInterfaceGuids(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var result = new List<Guid>();
        var key = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == InvalidHandle || key == IntPtr.Zero) return result;

        try
        {
            // An INF writes either a list (DeviceInterfaceGUIDs, REG_MULTI_SZ) or a single
            // value (DeviceInterfaceGUID, REG_SZ); both are seen in the wild.
            foreach (var name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
            {
                var text = ReadRegistryString(key, name);
                if (text == null) continue;
                foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Guid.TryParse(part.Trim(), out var guid)) result.Add(guid);
                }
            }
        }
        finally
        {
            RegCloseKey(key);
        }

        return result;
    }

    private static string? ReadRegistryString(IntPtr key, string name)
    {
        uint size = 0;
        if (RegQueryValueEx(key, name, IntPtr.Zero, out _, null, ref size) != 0 || size == 0) return null;
        var bytes = new byte[size];
        if (RegQueryValueEx(key, name, IntPtr.Zero, out _, bytes, ref size) != 0) return null;
        return Encoding.Unicode.GetString(bytes, 0, (int)size).TrimEnd('\0');
    }

    private static string? ReadStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var bytes = new byte[512];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, bytes, (uint)bytes.Length, out var required))
            return null;
        return Encoding.Unicode.GetString(bytes, 0, (int)Math.Min(required, (uint)bytes.Length)).TrimEnd('\0');
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_ALLCLASSES = 0x00000004;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint SPDRP_SERVICE = 0x00000004;
    private const uint DICS_FLAG_GLOBAL = 0x00000001;
    private const uint DIREG_DEV = 0x00000001;
    private const uint KEY_READ = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder instanceId, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data,
        uint property, out uint regType, byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid,
        uint index, ref SP_DEVICE_INTERFACE_DATA interfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA interfaceData,
        IntPtr detail, uint detailSize, out uint required, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data,
        uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(IntPtr key, string name, IntPtr reserved, out uint type,
        byte[]? data, ref uint size);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr key);
}
