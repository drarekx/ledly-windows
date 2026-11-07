using System.Diagnostics;
using Ledly.Windows.Audio;
using Ledly.Windows.Capture;
using Microsoft.Win32;

namespace Ledly.Windows.Platform;

/// <summary><see cref="IPlatform"/> for Windows. UI calls are marshalled to the WinForms thread.</summary>
sealed class WindowsPlatform(string dataDirectory) : IPlatform
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Ledly";

    /// <summary>Set by the tray app once the message loop exists.</summary>
    public Action? ShowWindowHandler { get; set; }
    public Action? QuitHandler { get; set; }
    public SynchronizationContext? UiContext { get; set; }

    public string Name => "windows";
    public string DataDirectory => dataDirectory;

    public IScreenCapture CreateScreenCapture() => new ScreenCapture();
    public IAudioCapture CreateAudioCapture() => new WasapiLoopback();

    public IReadOnlyList<SerialPortInfo> ListSerialPorts() => SerialPorts.List();

    public IReadOnlyList<DisplayInfo> ListDisplays() =>
        Displays.All().Select(m => new DisplayInfo(m.DeviceName, m.Name, m.Bounds.Width, m.Bounds.Height, m.Primary)).ToList();

    public IReadOnlyList<AppInfo> ListCaptureApps() => AppWindows.Apps();

    public bool SupportsLaunchAtLogin => Environment.ProcessPath is not null;

    public bool LaunchAtLogin
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string command && command.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\" --background");
                else key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            catch (Exception e)
            {
                Log.Write($"launch at login: {e.Message}");
            }
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            Log.Write($"open {url}: {e.Message}");
        }
    }

    public void ShowWindow() => OnUi(ShowWindowHandler);

    public void Quit() => OnUi(QuitHandler);

    public string? InstallUpdate(string packagePath, string version)
    {
        var error = UpdateInstaller.Prepare(packagePath, version);
        if (error is null) Quit();
        return error;
    }

    void OnUi(Action? action)
    {
        if (action is null) return;
        if (UiContext is { } context && SynchronizationContext.Current != context) context.Post(_ => action(), null);
        else action();
    }
}
