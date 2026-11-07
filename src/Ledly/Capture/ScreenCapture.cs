using Ledly.Windows.Interop;
using Ledly.Windows.Platform;

namespace Ledly.Windows.Capture;

/// <summary>
/// <see cref="IScreenCapture"/> for Windows: a monitor, or the largest visible window of an app
/// (re-checked every 2 s, like the Mac app's <c>watchApp</c>), with Windows.Graphics.Capture and
/// GDI as fallback. Frames arrive about 256 px wide at most <see cref="CaptureTarget.Fps"/> a second.
/// </summary>
sealed class ScreenCapture : IScreenCapture
{
    public event CaptureFrameHandler? Frame;
    public event Action<CaptureStatus>? StatusChanged;
    public event Action<string>? SourceChanged;

    readonly object _gate = new();
    readonly Timer _watch;
    CaptureTarget? _target;
    ICaptureSession? _session;
    D3DDevice? _device;
    /// <summary>Window being captured in app mode (and its size), to notice when another one becomes the main one.</summary>
    (IntPtr Handle, int Width, int Height)? _window;
    string? _source;
    bool _disposed;
    /// <summary>Events raised while holding the lock, delivered after releasing it.</summary>
    readonly List<Action> _pending = [];

    public ScreenCapture()
    {
        _watch = new Timer(_ => Watch(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Update(CaptureTarget? target)
    {
        try
        {
            lock (_gate) UpdateLocked(target);
        }
        finally
        {
            Flush();
        }
    }

    void UpdateLocked(CaptureTarget? target)
    {
        if (_disposed) return;
        var old = _target;
        _target = target;
        if (target is null)
        {
            Stop();
            Raise(() => StatusChanged?.Invoke(CaptureStatus.Stopped));
            return;
        }
        if (_session is null || old is null || old.DisplayId != target.DisplayId || old.App != target.App || old.Fps != target.Fps)
        {
            try
            {
                Restart();
            }
            catch (Exception e)
            {
                // The watch timer (already armed) tries again in 2 s.
                Log.Write($"capture: restart: {e.Message}");
            }
        }
    }

    /// <summary>Every 2 s while capturing: restart when the session broke or the app's main window changed.</summary>
    void Watch()
    {
        try
        {
            lock (_gate) WatchLocked();
        }
        finally
        {
            Flush();
        }
    }

    void WatchLocked()
    {
        if (_disposed || _target is null) return;
        try
        {
            if (_session is null || _session.Broken)
            {
                Restart();
                return;
            }
            if (_target.App is { } app)
            {
                var best = AppWindows.MainWindow(app);
                var changed = (best, _window) switch
                {
                    (null, null) => false,
                    ({ } w, { } c) => w.Handle != c.Handle || Math.Abs(w.Bounds.Width - c.Width) > 2 || Math.Abs(w.Bounds.Height - c.Height) > 2,
                    _ => true,
                };
                if (changed) Restart();
            }
        }
        catch (Exception e)
        {
            Log.Write($"capture: watch: {e.Message}");
        }
    }

    void Stop()
    {
        _watch.Change(Timeout.Infinite, Timeout.Infinite);
        _session?.Dispose();
        _session = null;
        _window = null;
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    void Restart()
    {
        var target = _target!;
        _session?.Dispose();
        _session = null;
        Raise(() => StatusChanged?.Invoke(new CaptureStatus(CaptureStatusKind.starting)));
        // Retry every 2 s whatever happens (display unplugged, window closed, GPU reset…).
        _watch.Change(2000, 2000);

        var monitor = Displays.Find(target.DisplayId);
        AppWindow? window = null;
        string? appName = null;
        if (target.App is { } app)
        {
            window = AppWindows.MainWindow(app);
            appName = AppWindows.DisplayName(app, window?.ProcessId);
        }
        if (window is null && monitor is null)
        {
            _window = null;
            var message = L.Tr("No se encuentra la pantalla.");
            Raise(() => StatusChanged?.Invoke(new CaptureStatus(CaptureStatusKind.failed, message)));
            return;
        }

        string source;
        if (window is not null)
        {
            _window = (window.Handle, window.Bounds.Width, window.Bounds.Height);
            source = window.Title.Length == 0 ? L.Tr("Ventana de {0}", appName) : L.Tr("Ventana de {0}: {1}", appName, window.Title);
        }
        else
        {
            _window = null;
            source = appName is not null
                ? L.Tr("{0} no tiene ventanas visibles: capturando {1}", appName, monitor!.Name)
                : monitor!.Name;
        }

        try
        {
            _session = StartSession(window, monitor, target.Fps);
        }
        catch (Exception e)
        {
            Log.Write($"capture: {e.Message}");
            var message = e.Message;
            Raise(() => StatusChanged?.Invoke(new CaptureStatus(CaptureStatusKind.failed, message)));
            return;
        }
        if (_source != source)
        {
            _source = source;
            Raise(() => SourceChanged?.Invoke(source));
        }
        Raise(() => StatusChanged?.Invoke(new CaptureStatus(CaptureStatusKind.running)));
    }

    void Raise(Action action) => _pending.Add(action);

    void Flush()
    {
        Action[] actions;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            actions = [.. _pending];
            _pending.Clear();
        }
        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception e) { Log.Write($"capture: event: {e.Message}"); }
        }
    }

    ICaptureSession StartSession(AppWindow? window, DisplayMonitor? monitor, int fps)
    {
        if (WgcSession.IsSupported)
        {
            try
            {
                if (_device is { Lost: true })
                {
                    _device.Dispose();
                    _device = null;
                }
                _device ??= new D3DDevice();
                var item = window is not null ? WinRTInterop.CreateForWindow(window.Handle) : WinRTInterop.CreateForMonitor(monitor!.Handle);
                return new WgcSession(_device, item, fps, Deliver);
            }
            catch (Exception e)
            {
                // Some windows can't be captured (elevated, protected), or there is no usable GPU.
                Log.Write($"capture: Windows.Graphics.Capture failed, using GDI: {e.Message}");
            }
        }
        return window is not null ? GdiSession.ForWindow(window.Handle, fps, Deliver) : GdiSession.ForMonitor(monitor!, fps, Deliver);
    }

    void Deliver(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var frame = new CaptureFrame(pixels, width, height, stride);
        Frame?.Invoke(in frame);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _watch.Dispose();
            _device?.Dispose();
            _device = null;
        }
    }
}
