namespace Ledly;

/// <summary>Everything the output loop needs, snapshotted from <see cref="Settings"/>.</summary>
public sealed record EngineConfig
{
    public bool IsOn { get; init; } = true;
    public LightMode Mode { get; init; } = LightMode.screen;
    public EffectKind Effect { get; init; } = EffectKind.rainbowSwirl;
    public double EffectSpeed { get; init; } = 1;
    public EqualizerOptions Equalizer { get; init; } = new();
    public RGB Color { get; init; } = RGB.White;
    public ColorAdjustments Adjustments { get; init; } = new();
    public LEDZone[] Zones { get; init; } = [];
    public double SmoothingMs { get; init; } = 150;
    public SmoothingKind Smoothing { get; init; } = SmoothingKind.spring;
    public bool AntiFlicker { get; init; } = true;
    /// <summary>mA the strip may draw; 0 = no limit.</summary>
    public int PowerLimit { get; init; }
    public string? PortPath { get; init; }
    public string DisplayId { get; init; } = "";
    public string? CaptureApp { get; init; }
    public int CaptureFps { get; init; } = 30;
    public SamplingMode Sampling { get; init; } = SamplingMode.linear;
    public bool BlackBorders { get; init; } = true;
}

/// <summary>A temporary look pushed through the API (<c>duration</c>), shown on top of the normal mode.</summary>
public sealed record Overlay(LightMode Mode, EffectKind Effect, RGB Color, DateTime Until);

/// <summary>Lights used while setting up the strip.</summary>
public abstract record TestPattern
{
    /// <summary>First LED green, second blue, last red: shows where the strip starts and which way it runs.</summary>
    public sealed record Direction : TestPattern;
    /// <summary>One color per side of the screen.</summary>
    public sealed record Sides : TestPattern;
    /// <summary>The whole strip shows this input color through the color correction (calibration).</summary>
    public sealed record Calibration(RGB Color) : TestPattern;
    /// <summary>One bright LED at Index (with dim neighbours) on a strip of Count, for the layout wizard.</summary>
    public sealed record Cursor(int Index, int Count) : TestPattern;
}

public enum FlashStateKind { idle, running, done, failed }

public sealed record FlashState(FlashStateKind Kind, FlashStep? Step = null, string? Error = null)
{
    public static readonly FlashState Idle = new(FlashStateKind.idle);
}

/// <summary>
/// Drives the strip: picks the source, smooths, corrects colors and sends frames to the device.
/// Public members are thread-safe; <see cref="StateChanged"/> fires (from any thread) when
/// device, capture, live, overlay, flash or port state changes, and <see cref="FrameReady"/> about
/// 15 times a second with the colors while <see cref="ColorListeners"/> &gt; 0.
/// </summary>
public sealed class LEDEngine
{
    static LEDEngine? _shared;
    public static LEDEngine Shared => _shared ?? throw new InvalidOperationException("LEDEngine.Start first");

    public IPlatform Platform { get; }
    readonly EngineQueue _queue = new("ledly.engine", OutputLoop.Rate);
    readonly OutputLoop _loop;
    Timer? _portTimer;
    Timer? _configTimer;
    volatile bool _sleeping;

    public DeviceStatus DeviceStatus { get; private set; } = DeviceStatus.Disconnected;
    public CaptureStatus GrabberStatus { get; private set; } = CaptureStatus.Stopped;
    public FlashState FlashState { get; private set; } = FlashState.Idle;
    public double Fps { get; private set; }
    public IReadOnlyList<SerialPortInfo> Ports { get; private set; } = [];
    public bool OverlayActive { get; private set; }
    /// <summary>What the screen capture is looking at.</summary>
    public string CaptureSource { get; private set; } = "";
    /// <summary>Frames from the network (DDP) are showing; null when not. Holds the sender's address.</summary>
    public string? LiveSource { get; private set; }
    /// <summary>Estimated current draw of the strip (mA).</summary>
    public double Milliamps { get; private set; }
    /// <summary>The power limit is dimming the strip right now.</summary>
    public bool PowerLimited { get; private set; }

    public event Action? StateChanged;
    public event Action<RGB[]>? FrameReady;

    int _colorListeners;
    /// <summary>Clients that want <see cref="FrameReady"/> (web UI previews, WebSocket subscribers).</summary>
    public int ColorListeners => Volatile.Read(ref _colorListeners);
    public void AddColorListener() => Interlocked.Increment(ref _colorListeners);
    public void RemoveColorListener() => Interlocked.Decrement(ref _colorListeners);

    TestPattern? _testPattern;
    public TestPattern? TestPattern
    {
        get => _testPattern;
        set
        {
            _testPattern = value;
            _queue.Post(() => _loop.TestPattern = value);
        }
    }

    DateTime _previewWantedUntil = DateTime.MinValue;

    LEDEngine(IPlatform platform)
    {
        Platform = platform;
        _loop = new OutputLoop(_queue, platform);
    }

    /// <summary>Creates the engine and starts the output loop. Settings must be loaded.</summary>
    public static LEDEngine Start(IPlatform platform)
    {
        var engine = new LEDEngine(platform);
        _shared = engine;
        engine.Run();
        return engine;
    }

    void Run()
    {
        _loop.Publish = Apply;
        _loop.SourceChanged = source =>
        {
            if (CaptureSource == source) return;
            CaptureSource = source;
            StateChanged?.Invoke();
        };
        _loop.Start();

        // Coalesce bursts of settings changes (sliders) into one config push.
        _configTimer = new Timer(_ => PushConfig());
        Settings.Shared.Changed += _ => _configTimer.Change(30, Timeout.Infinite);
        PushConfig();

        RefreshPorts();
        _portTimer = new Timer(_ =>
        {
            RefreshPorts();
            // The capture preview stops a while after the last request.
            if (_previewWantedUntil < DateTime.UtcNow) _loop.SetCapturePreview(false);
        }, null, 2000, 2000);
    }

    /// <summary>Turns the strip off and waits a moment so the last frame reaches it (app quitting).</summary>
    public void Shutdown()
    {
        _portTimer?.Dispose();
        try { _queue.Invoke(_loop.Shutdown); } catch (Exception e) { Log.Write($"shutdown: {e.Message}"); }
        Settings.Shared.SaveNow();
    }

    /// <summary>Called by the platform when the PC or its screens go to sleep / wake up.</summary>
    public void SetSleeping(bool asleep)
    {
        _sleeping = asleep && Settings.Shared.OffWhenAsleep;
        PushConfig();
    }

    public void PushConfig()
    {
        var s = Settings.Shared;
        var c = new EngineConfig
        {
            IsOn = s.IsOn && !_sleeping,
            Mode = s.Mode,
            Effect = s.Effect,
            EffectSpeed = s.EffectSpeed,
            Equalizer = s.Equalizer,
            Color = s.Color,
            Adjustments = s.Adjustments,
            Zones = s.Layout.Zones(),
            SmoothingMs = s.SmoothingMs,
            Smoothing = s.Smoothing,
            AntiFlicker = s.AntiFlicker,
            PowerLimit = s.PowerLimitEnabled ? s.PowerLimitMilliamps : 0,
            PortPath = s.PortPath,
            DisplayId = s.DisplayId,
            CaptureApp = s.CaptureApp,
            CaptureFps = s.CaptureFps,
            Sampling = s.Sampling,
            BlackBorders = s.BlackBorders,
        };
        _queue.Post(() => _loop.Config = c);
    }

    public void RefreshPorts()
    {
        IReadOnlyList<SerialPortInfo> list;
        try { list = Platform.ListSerialPorts(); }
        catch (Exception e) { Log.Write($"ports: {e.Message}"); return; }
        if (!list.SequenceEqual(Ports))
        {
            Ports = list;
            StateChanged?.Invoke();
        }
        // First run: pick the Arduino automatically.
        if (Settings.Shared.PortPath is null && list.FirstOrDefault(p => p.LooksLikeArduino) is { } nano)
            Settings.Shared.PortPath = nano.Path;
    }

    public void Reconnect() => _queue.Post(_loop.ForceReconnect);

    /// <summary>Shows a look for <paramref name="seconds"/>, then returns to the normal mode.</summary>
    public void ShowOverlay(LightMode mode, EffectKind? effect, RGB? color, double seconds)
    {
        var s = Settings.Shared;
        var overlay = new Overlay(mode, effect ?? s.Effect, color ?? s.Color, DateTime.UtcNow.AddSeconds(seconds));
        OverlayActive = true;
        _queue.Post(() => _loop.Overlay = overlay);
        StateChanged?.Invoke();
    }

    public void ClearOverlay()
    {
        OverlayActive = false;
        _queue.Post(() => _loop.Overlay = null);
        StateChanged?.Invoke();
    }

    /// <summary>A frame received over the network: R, G, B per LED from the start of the strip.</summary>
    public void ReceiveLive(byte[] bytes, string source) => _queue.Post(() => _loop.ReceiveLive(bytes, source));

    /// <summary>Drops real-time mode at once (late packets are ignored for a moment).</summary>
    public void EndLive()
    {
        LiveSource = null;
        _queue.Post(_loop.EndLive);
        StateChanged?.Invoke();
    }

    /// <summary>Uploads the firmware for <paramref name="pin"/> to the board on <paramref name="port"/>. Returns at once.</summary>
    public void InstallFirmware(int pin, string port)
    {
        var hex = Firmware.Hex(pin);
        if (hex is null)
        {
            SetFlash(new FlashState(FlashStateKind.failed, Error: L.Tr("No se encuentra el firmware para el pin D{0}.", pin)));
            return;
        }
        SetFlash(new FlashState(FlashStateKind.running, new FlashStep(FlashStepKind.connecting)));
        _queue.Post(() =>
        {
            _loop.Flashing = true;
            _loop.Device.Disconnect();
            _loop.PublishNow();
            // The upload blocks for several seconds: run it off the engine thread, which keeps ticking (and idling).
            Task.Run(() =>
            {
                string? error = null;
                try
                {
                    new Flasher(port, step => SetFlash(new FlashState(FlashStateKind.running, step))).Flash(hex);
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                // Give the fresh sketch time to boot before connecting again.
                Thread.Sleep(500);
                _queue.Post(() =>
                {
                    _loop.Flashing = false;
                    _loop.ForceReconnect();
                });
                SetFlash(error is null ? new FlashState(FlashStateKind.done) : new FlashState(FlashStateKind.failed, Error: error));
            });
        });
    }

    public void ResetFlashState() => SetFlash(FlashState.Idle);

    void SetFlash(FlashState state)
    {
        FlashState = state;
        StateChanged?.Invoke();
    }

    /// <summary>Audio seen by the music effects; null while none is showing.</summary>
    public AudioLevels? AudioLevels() => _queue.Invoke(_loop.AudioLevels);

    /// <summary>The frame last sent to the strip.</summary>
    public RGB[] CurrentColors() => _queue.Invoke(() => _loop.Output);

    /// <summary>Latest capture preview; asking keeps previews coming for a few seconds.</summary>
    public (byte[] Png, int Width, int Height, RectD Content)? CapturePreview()
    {
        _previewWantedUntil = DateTime.UtcNow.AddSeconds(3);
        _loop.SetCapturePreview(true);
        return _loop.LatestPreview;
    }

    void Apply(OutputLoop.Snapshot s)
    {
        var changed = false;
        if (DeviceStatus != s.Device) { DeviceStatus = s.Device; changed = true; }
        if (GrabberStatus != s.Grabber) { GrabberStatus = s.Grabber; changed = true; }
        if (Math.Abs(Fps - s.Fps) >= 0.5) Fps = s.Fps;
        if (OverlayActive != s.Overlay) { OverlayActive = s.Overlay; changed = true; }
        if (LiveSource != s.LiveSource) { LiveSource = s.LiveSource; changed = true; }
        if (Math.Abs(Milliamps - s.Milliamps) >= 5) Milliamps = s.Milliamps;
        if (PowerLimited != s.PowerLimited) { PowerLimited = s.PowerLimited; changed = true; }
        if (changed) StateChanged?.Invoke();
        // Snapshots come every third tick (~17 Hz); the colors go to listeners at that rate.
        if (ColorListeners > 0) FrameReady?.Invoke(s.Colors);
    }
}
