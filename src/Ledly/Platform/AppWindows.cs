using System.Diagnostics;
using Ledly.Windows.Interop;

namespace Ledly.Windows.Platform;

/// <summary>A visible top-level window of some app.</summary>
sealed record AppWindow(IntPtr Handle, uint ProcessId, string ProcessName, string Title, RECT Bounds)
{
    public long Area => (long)Bounds.Width * Bounds.Height;
}

/// <summary>Visible, uncloaked, normal top-level windows (EnumWindows), and the apps that own them.</summary>
static class AppWindows
{
    const int MinWidth = 160, MinHeight = 120;
    static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "LockApp", "SystemSettings", "ApplicationFrameHost", "Widgets", "WidgetBoard",
    };

    public static List<AppWindow> All()
    {
        var own = (uint)Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        var list = new List<AppWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsCapturable(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == own || pid == 0) return true;
                if (!names.TryGetValue(pid, out var name))
                {
                    name = ProcessName(pid);
                    names[pid] = name;
                }
                if (name.Length == 0) return true;
                list.Add(new AppWindow(hwnd, pid, name, Native.WindowText(hwnd), Native.WindowBounds(hwnd)));
            }
            catch
            {
                // A window that vanished midway: skip it.
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsCapturable(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        var exStyle = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var r = Native.WindowBounds(hwnd);
        return r.Width >= MinWidth && r.Height >= MinHeight && Native.GetWindowTextLength(hwnd) > 0;
    }

    static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The app's largest visible window, if any (Mac: <c>ScreenGrabber.mainWindow</c>).</summary>
    public static AppWindow? MainWindow(string processName) =>
        All().Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .MaxBy(w => w.Area);

    /// <summary>Apps with a visible window, by process name, named by their file description.</summary>
    public static List<AppInfo> Apps() =>
        All().Where(w => !Ignored.Contains(w.ProcessName))
            .GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppInfo(g.Key, DisplayName(g.Key, g.First().ProcessId)))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>"Google Chrome" for chrome.exe (its FileDescription), or the process name.</summary>
    public static string DisplayName(string processName, uint? pid = null)
    {
        lock (Descriptions)
        {
            if (Descriptions.TryGetValue(processName, out var cached)) return cached;
        }
        var name = processName;
        try
        {
            if (pid is null)
            {
                var processes = Process.GetProcessesByName(processName);
                pid = (uint?)processes.FirstOrDefault()?.Id;
                foreach (var p in processes) p.Dispose();
            }
            if (pid is { } id && Native.ProcessImagePath(id) is { } path)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) name = description;
            }
        }
        catch
        {
            // Protected process: keep the process name.
        }
        lock (Descriptions) Descriptions[processName] = name;
        return name;
    }
}
