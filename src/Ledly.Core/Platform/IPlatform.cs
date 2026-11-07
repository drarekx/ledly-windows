namespace Ledly;

/// <summary>
/// What changes per operating system. The Windows app implements it in <c>src/Ledly</c>; the
/// development host (<c>src/Ledly.Dev</c>) has a fake one so the core runs on any OS.
/// </summary>
public interface IPlatform
{
    /// <summary>"windows", "dev"…</summary>
    string Name { get; }
    /// <summary>Settings, log, firmware backups (e.g. %APPDATA%\Ledly).</summary>
    string DataDirectory { get; }

    IScreenCapture CreateScreenCapture();
    IAudioCapture CreateAudioCapture();

    /// <summary>Serial ports present now (USB first).</summary>
    IReadOnlyList<SerialPortInfo> ListSerialPorts();
    IReadOnlyList<DisplayInfo> ListDisplays();
    /// <summary>Apps with a visible window that can be captured alone.</summary>
    IReadOnlyList<AppInfo> ListCaptureApps();

    bool SupportsLaunchAtLogin { get; }
    bool LaunchAtLogin { get; set; }

    void OpenUrl(string url);
    /// <summary>Shows the app window (from the API, e.g. a second launch).</summary>
    void ShowWindow();
    /// <summary>Quits the app cleanly (strip off, settings saved).</summary>
    void Quit();

    /// <summary>
    /// Replaces the running app with the downloaded, verified update package and restarts it.
    /// Returns an error message, or null when the swap was scheduled (the app then quits).
    /// </summary>
    string? InstallUpdate(string packagePath, string version);
}

public sealed record SerialPortInfo(string Path, string Name, bool LooksLikeArduino, string? Description = null);

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, bool Primary);

/// <summary>Id is what goes in <see cref="Settings.CaptureApp"/> (process name on Windows).</summary>
public sealed record AppInfo(string Id, string Name);

/// <summary>What the platform capture should look at. Null target = stop.</summary>
public sealed record CaptureTarget(string DisplayId, string? App, int Fps);

public enum CaptureStatusKind { stopped, starting, running, noPermission, failed }

public readonly record struct CaptureStatus(CaptureStatusKind Kind, string? Message = null)
{
    public static readonly CaptureStatus Stopped = new(CaptureStatusKind.stopped);
}

/// <summary>
/// One captured frame in BGRA (8 bits per channel, top-down). The platform downscales it to about
/// 256 px wide when it can do so cheaply; the core copes with any size. <see cref="Pixels"/> is only
/// valid during the <see cref="IScreenCapture.Frame"/> call.
/// </summary>
public readonly ref struct CaptureFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride)
{
    public ReadOnlySpan<byte> Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
}

public delegate void CaptureFrameHandler(in CaptureFrame frame);

/// <summary>Screen or window capture. Events fire on the capture's own thread.</summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>Starts, retargets or (null) stops. Called from the engine thread.</summary>
    void Update(CaptureTarget? target);
    /// <summary>At most <see cref="CaptureTarget.Fps"/> times a second.</summary>
    event CaptureFrameHandler? Frame;
    event Action<CaptureStatus>? StatusChanged;
    /// <summary>What is being captured ("Pantalla DELL U2720Q", "Ventana de Chrome: …"), on change.</summary>
    event Action<string>? SourceChanged;
}

/// <summary>System audio (what is playing), for the music effects.</summary>
public interface IAudioCapture : IDisposable
{
    bool IsRunning { get; }
    void Start();
    void Stop();
    /// <summary>Interleaved float samples (−1…1), channel count and sample rate. On the capture thread.</summary>
    event Action<ReadOnlyMemory<float>, int, int>? Samples;
}
