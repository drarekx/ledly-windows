using System.Runtime.InteropServices;
using System.Text;
using Ledly.Windows.Interop;

namespace Ledly.Windows.Platform;

/// <summary>A monitor as Windows sees it now.</summary>
sealed record DisplayMonitor(IntPtr Handle, string DeviceName, string Name, RECT Bounds, bool Primary);

/// <summary>Monitors (EnumDisplayMonitors) with their friendly names (DisplayConfig).</summary>
static class Displays
{
    public static List<DisplayMonitor> All()
    {
        var names = FriendlyNames();
        var list = new List<DisplayMonitor>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (Native.GetMonitorInfo(handle, ref info))
            {
                var device = info.szDevice ?? "";
                var name = names.GetValueOrDefault(device);
                if (string.IsNullOrWhiteSpace(name)) name = device.TrimStart('\\', '.');
                list.Add(new DisplayMonitor(handle, device, name!, info.rcMonitor, (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        // Primary first, then left to right.
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    /// <summary>The monitor for a <see cref="Settings.DisplayId"/> ("" or unknown = primary).</summary>
    public static DisplayMonitor? Find(string id)
    {
        var all = All();
        return all.FirstOrDefault(m => !string.IsNullOrEmpty(id) && string.Equals(m.DeviceName, id, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
    }

    // DISPLAYCONFIG_PATH_INFO is 72 bytes, DISPLAYCONFIG_MODE_INFO 64; only a few offsets are read.
    const int PathSize = 72, ModeSize = 64;
    const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>GDI device name (\\.\DISPLAY1) → monitor name ("DELL U2720Q").</summary>
    static Dictionary<string, string> FriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return result;
                var paths = new byte[pathCount * PathSize];
                var modes = new byte[modeCount * ModeSize];
                var status = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (status == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the setup changed in between
                if (status != 0) return result;
                for (var i = 0; i < pathCount; i++)
                {
                    var p = i * PathSize;
                    // sourceInfo: LUID adapterId @0, id @8. targetInfo: LUID adapterId @20, id @28.
                    var source = DeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, 84, paths, p, p + 8);
                    var target = DeviceInfo(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, 420, paths, p + 20, p + 28);
                    if (source is null || target is null) continue;
                    var gdiName = ReadString(source, 20, 32);
                    var friendly = ReadString(target, 36, 64);
                    if (gdiName.Length > 0 && friendly.Length > 0) result.TryAdd(gdiName, friendly);
                }
                break;
            }
        }
        catch (Exception e)
        {
            Log.Write($"displays: {e.Message}");
        }
        return result;
    }

    /// <summary>Fills a DISPLAYCONFIG_DEVICE_INFO_HEADER (type, size, adapterId, id) and asks for the packet.</summary>
    static byte[]? DeviceInfo(int type, int size, byte[] paths, int luidOffset, int idOffset)
    {
        var packet = new byte[size];
        BitConverter.TryWriteBytes(packet.AsSpan(0), type);
        BitConverter.TryWriteBytes(packet.AsSpan(4), size);
        Array.Copy(paths, luidOffset, packet, 8, 8);
        Array.Copy(paths, idOffset, packet, 16, 4);
        return Native.DisplayConfigGetDeviceInfo(packet) == 0 ? packet : null;
    }

    static string ReadString(byte[] buffer, int offset, int chars)
    {
        var s = Encoding.Unicode.GetString(buffer, offset, chars * 2);
        var end = s.IndexOf('\0');
        return (end >= 0 ? s[..end] : s).Trim();
    }
}
