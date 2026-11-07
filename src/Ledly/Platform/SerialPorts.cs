using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ledly.Windows.Platform;

/// <summary>COM ports with their device names and USB ids (SetupAPI), USB-serial chips used by Nanos first.</summary>
static partial class SerialPorts
{
    /// <summary>USB vendors of the chips found on Arduino Nanos and clones.</summary>
    static readonly Dictionary<string, string> ArduinoVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1A86"] = "CH340", ["0403"] = "FTDI", ["2341"] = "Arduino", ["2A03"] = "Arduino", ["10C4"] = "CP210x",
    };

    public static IReadOnlyList<SerialPortInfo> List()
    {
        var found = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        var usb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (port, friendly, hardwareId) in SetupApiPorts())
            {
                var match = UsbId().Match(hardwareId);
                var isUsb = hardwareId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || hardwareId.StartsWith("FTDIBUS", StringComparison.OrdinalIgnoreCase) || match.Success;
                var vid = match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
                var pid = match.Success ? match.Groups[2].Value.ToUpperInvariant() : null;
                var arduino = (vid is not null && ArduinoVendors.ContainsKey(vid)) || hardwareId.StartsWith("FTDIBUS", StringComparison.OrdinalIgnoreCase);
                var description = vid is not null ? $"USB {vid}:{pid}" : null;
                var name = string.IsNullOrWhiteSpace(friendly) ? port : friendly;
                if (!name.Contains(port, StringComparison.OrdinalIgnoreCase)) name = $"{name} ({port})";
                found[port] = new SerialPortInfo(port, name, arduino, description);
                if (isUsb) usb.Add(port);
            }
        }
        catch (Exception e)
        {
            Log.Write($"setupapi: {e.Message}");
        }
        // Ports SetupAPI didn't describe (virtual drivers, odd setups).
        try
        {
            foreach (var port in SerialPort.GetPortNames())
                found.TryAdd(port, new SerialPortInfo(port, port, false));
        }
        catch
        {
            // No serial ports at all.
        }
        return found.Values
            .OrderByDescending(p => p.LooksLikeArduino)
            .ThenByDescending(p => usb.Contains(p.Path))
            .ThenBy(p => PortNumber(p.Path))
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static int PortNumber(string port) =>
        port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(port.AsSpan(3), out var n) ? n : int.MaxValue;

    [GeneratedRegex(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbId();

    // SetupAPI -----------------------------------------------------------------------------------

    static readonly Guid GUID_DEVCLASS_PORTS = new("4d36e978-e325-11ce-bfc1-08002be10318");
    const uint DIGCF_PRESENT = 0x2;
    const uint SPDRP_HARDWAREID = 0x1, SPDRP_FRIENDLYNAME = 0xC;
    const uint DICS_FLAG_GLOBAL = 1, DIREG_DEV = 1, KEY_READ = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint type, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint profile, uint keyType, uint access);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    static IEnumerable<(string Port, string Friendly, string HardwareId)> SetupApiPorts()
    {
        var guid = GUID_DEVCLASS_PORTS;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                var port = PortName(set, ref data);
                if (port is null || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;
                var friendly = Property(set, ref data, SPDRP_FRIENDLYNAME) ?? port;
                var hardwareId = Property(set, ref data, SPDRP_HARDWAREID) ?? "";
                yield return (port, friendly, hardwareId);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>REG_SZ or the first string of a REG_MULTI_SZ.</summary>
    static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out var required);
        if (required == 0) return null;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, required, out _)) return null;
        var text = System.Text.Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    static string? PortName(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var key = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == IntPtr.Zero || key == new IntPtr(-1)) return null;
        using var registry = RegistryKey.FromHandle(new SafeRegistryHandle(key, ownsHandle: true));
        return registry.GetValue("PortName") as string;
    }
}
