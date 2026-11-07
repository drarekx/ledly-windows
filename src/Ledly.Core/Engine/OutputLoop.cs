namespace Ledly;

/// <summary>The real-time part. Everything here runs on the engine queue unless noted.</summary>
public sealed class OutputLoop
{
    public sealed record Snapshot(RGB[] Colors, DeviceStatus Device, CaptureStatus Grabber, double Fps, bool Overlay,
        string? LiveSource, double Milliamps, bool PowerLimited);

    public const double Rate = 50;
    /// <summary>No network frame for this long ends real-time mode.</summary>
    static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(2.5);

    readonly EngineQueue _queue;
    public LEDDevice Device { get; } = new();
    public Action<Snapshot>? Publish { get; set; }
    public Action<string>? SourceChanged { get; set; }

    readonly ScreenGrabber _grabber;
    readonly AudioAnalyzer _audio;
    CaptureStatus _grabberStatus = CaptureStatus.Stopped;
    RGB[] _screenColors = [];
    EffectRenderer? _renderer;
    readonly Smoother _smoother = new();
    float _milliamps;
    bool _powerLimited;
    RGB[] _lastSent = [];
    DateTime _lastSentAt = DateTime.MinValue;
    readonly DateTime _startTime = DateTime.UtcNow;
    DateTime _lastTick = DateTime.UtcNow;
    int _frameCount;
    int _sentCount;
    DateTime _fpsWindowStart = DateTime.UtcNow;
    double _measuredFps;
    DateTime _lastConnectAttempt = DateTime.MinValue;
    /// <summary>Rainbow shown for a moment at launch.</summary>
    readonly DateTime _bootUntil = DateTime.UtcNow.AddSeconds(2.5);
    EffectRenderer? _bootRenderer;

    (RGB[] Colors, string Source, DateTime At)? _live;
    /// <summary>After EndLive, packets still in flight are ignored until then.</summary>
    DateTime _liveBlockedUntil = DateTime.MinValue;

    public TestPattern? TestPattern { get; set; }
    public bool Flashing { get; set; }

    /// <summary>Last frame computed for the strip (8-bit values).</summary>
    public RGB[] Output { get; private set; } = [];

    EngineConfig _config = new();
    public EngineConfig Config
    {
        get => _config;
        set
        {
            var old = _config;
            _config = value;
            ConfigChanged(old);
        }
    }

    Overlay? _overlay;
    public Overlay? Overlay
    {
        get => _overlay;
        set
        {
            _overlay = value;
            UpdateGrabber();
        }
    }

    public OutputLoop(EngineQueue queue, IPlatform platform)
    {
        _queue = queue;
        _grabber = new ScreenGrabber(platform.CreateScreenCapture());
        _audio = new AudioAnalyzer(platform.CreateAudioCapture());
        _grabber.Colors += colors => _queue.Post(() => _screenColors = colors);
        _grabber.StatusChanged += status => _queue.Post(() => _grabberStatus = status);
        _grabber.SourceChanged += source => SourceChanged?.Invoke(source);
    }

    public void Start() => _queue.SetTick(Tick);

    /// <summary>Thread-safe.</summary>
    public void SetCapturePreview(bool wanted) => _grabber.PreviewWanted = wanted;

    /// <summary>Thread-safe.</summary>
    public (byte[] Png, int Width, int Height, RectD Content)? LatestPreview => _grabber.LatestPreview;

    public void Shutdown()
    {
        _grabber.Update(null);
        _audio.Stop();
        if (Device.IsConnected)
        {
            Device.Send(new RGB[Math.Max(Config.Zones.Length, 1)]);
            Thread.Sleep(50);
        }
        Device.Disconnect();
    }

    public void ReceiveLive(byte[] bytes, string source)
    {
        var now = DateTime.UtcNow;
        if (now < _liveBlockedUntil) return;
        var n = Config.Zones.Length;
        var colors = new RGB[n];
        for (var i = 0; i < Math.Min(n, bytes.Length / 3); i++)
            colors[i] = RGB.From8(bytes[i * 3], bytes[i * 3 + 1], bytes[i * 3 + 2]);
        SetLive((colors, source, now));
    }

    public void EndLive()
    {
        SetLive(null);
        _liveBlockedUntil = DateTime.UtcNow.AddSeconds(1);
    }

    void SetLive((RGB[] Colors, string Source, DateTime At)? value)
    {
        var was = _live is not null;
        _live = value;
        if (was != (value is not null))
        {
            UpdateGrabber();
            PublishNow();
        }
    }

    public AudioLevels? AudioLevels() => _audio.IsRunning ? _audio.Current() : null;

    public void ForceReconnect()
    {
        Device.Disconnect();
        _lastConnectAttempt = DateTime.MinValue;
    }

    public void PublishNow() =>
        Publish?.Invoke(new Snapshot(Output, Device.Status, _grabberStatus, _measuredFps, _overlay is not null,
            _live?.Source, _milliamps, _powerLimited));

    void ConfigChanged(EngineConfig old)
    {
        if (Config.PortPath != old.PortPath) ForceReconnect();
        if (!Config.Zones.SequenceEqual(old.Zones) || Config.Effect != old.Effect)
        {
            _renderer = null;
            _bootRenderer = null;
        }
        UpdateGrabber();
    }

    LightMode EffectiveMode => _overlay?.Mode ?? Config.Mode;

    void UpdateGrabber()
    {
        // System audio only while a music effect is showing; nothing to capture while network frames show.
        var music = Config.IsOn && _live is null && EffectiveMode == LightMode.effect && (_overlay?.Effect ?? Config.Effect).IsMusic();
        if (music && !_audio.IsRunning) _audio.Start();
        if (!music && _audio.IsRunning) _audio.Stop();

        var wanted = Config.IsOn && _live is null && EffectiveMode == LightMode.screen && Config.Zones.Length > 0;
        _grabber.Update(wanted
            ? new GrabberConfig(Config.DisplayId, Config.CaptureApp, Config.CaptureFps,
                Config.Zones.Select(z => z.Rect).ToArray(), Config.Sampling, Config.BlackBorders)
            : null);
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastTick).TotalSeconds, 0.2);
        _lastTick = now;
        if (Flashing) return;

        if (_overlay is not null && _overlay.Until < now) Overlay = null;
        if (_live is { } live && now - live.At > LiveTimeout) SetLive(null);
        MaintainConnection(now);

        var n = Config.Zones.Length;
        if (TestPattern is TestPattern.Cursor cursor) n = cursor.Count;
        var target = TargetColors(now, n);
        // Screen mode uses the user's smoothing; everything else a short exponential fade.
        // Network frames are shown as they come: the sender does its own animation.
        var screen = EffectiveMode == LightMode.screen && _bootUntil < now && TestPattern is null && _live is null;
        var tau = _live is not null && TestPattern is null ? 0.001
            : screen ? Math.Max(Config.SmoothingMs, 1) / 1000 : 0.06;
        _smoother.Step(target, dt, tau, screen ? Config.Smoothing : SmoothingKind.exponential);
        var scale = Config.PowerLimit > 0 ? PowerModel.Scale(_smoother.Current, Config.PowerLimit) : 1;
        Output = _smoother.Quantized(Config.AntiFlicker, scale);
        _milliamps = PowerModel.Milliamps(Output);
        _powerLimited = scale < 0.999f;

        // A frame identical to the last one is only repeated twice a second (static color, paused video,
        // lights off): keeps the strip alive without busying the port and the Nano.
        if (Output.SequenceEqual(_lastSent) && now - _lastSentAt < TimeSpan.FromSeconds(0.5))
        {
            // skip
        }
        else if (Device.Send(Output))
        {
            _sentCount++;
            _lastSent = Output;
            _lastSentAt = now;
        }

        _frameCount++;
        if (now - _fpsWindowStart >= TimeSpan.FromSeconds(1))
        {
            _measuredFps = _sentCount / (now - _fpsWindowStart).TotalSeconds;
            _sentCount = 0;
            _fpsWindowStart = now;
        }
        if (_frameCount % 3 == 0) PublishNow();
    }

    void MaintainConnection(DateTime now)
    {
        if (Config.PortPath is not { } path)
        {
            if (Device.Status.Kind != DeviceStatusKind.disconnected) Device.Disconnect();
            return;
        }
        if (Device.IsConnected) return;
        if (now - _lastConnectAttempt <= TimeSpan.FromSeconds(3)) return;
        _lastConnectAttempt = now;
        // Connecting blocks for up to a few seconds (reset + hello); the strip has nothing to show meanwhile.
        Device.Connect(path);
        _lastTick = DateTime.UtcNow;
        PublishNow();
    }

    RGB[] TargetColors(DateTime now, int n)
    {
        if (n <= 0) return [];
        if (TestPattern is { } pattern) return Test(pattern, n);

        RGB[] raw;
        var time = (now - _startTime).TotalSeconds;
        if (_live is { } live)
        {
            // Network frames light the strip even when it is off; "off" comes back when they stop.
            raw = live.Colors.Length == n ? live.Colors : new RGB[n];
        }
        else if (!Config.IsOn)
        {
            return new RGB[n];
        }
        else if (_bootUntil > now)
        {
            _bootRenderer ??= new EffectRenderer(EffectKind.rainbowSwirl, Config.Zones);
            raw = _bootRenderer.Render(time, 3, Config.Color);
        }
        else
        {
            switch (EffectiveMode)
            {
                case LightMode.screen:
                {
                    var current = _smoother.Current;
                    if (_screenColors.Length == n)
                    {
                        var adjustments = Config.Adjustments;
                        return _screenColors.Select(c => adjustments.Apply(c, useBacklight: true)).ToArray();
                    }
                    return current.Length == n ? current : new RGB[n];
                }
                case LightMode.effect:
                {
                    var kind = _overlay?.Effect ?? Config.Effect;
                    if (_renderer?.Kind != kind) _renderer = new EffectRenderer(kind, Config.Zones);
                    raw = _renderer.Render(time, Config.EffectSpeed, _overlay?.Color ?? Config.Color,
                        kind.IsMusic() ? _audio.Current() : Ledly.AudioLevels.Silent, Config.Equalizer);
                    break;
                }
                default:
                    raw = Enumerable.Repeat(_overlay?.Color ?? Config.Color, n).ToArray();
                    break;
            }
        }
        // Effects and solid colors are already "LED" colors: skip gamma and saturation.
        var a = Config.Adjustments;
        a.Gamma = 1;
        a.Saturation = 1;
        return raw.Select(c => a.Apply(c, useBacklight: false)).ToArray();
    }

    RGB[] Test(TestPattern pattern, int n)
    {
        const float dim = 0.5f;
        switch (pattern)
        {
            case TestPattern.Direction:
            {
                var output = Enumerable.Repeat(new RGB(0.04f, 0.04f, 0.04f), n).ToArray();
                output[0] = new RGB(0, 1, 0) * dim;
                if (n > 1) output[1] = new RGB(0, 0, 1) * dim;
                if (n > 2) output[n - 1] = new RGB(1, 0, 0) * dim;
                return output;
            }
            case TestPattern.Sides:
                return Config.Zones.Select(z => z.Side switch
                {
                    ScreenSide.top => new RGB(1, 0, 0) * dim,
                    ScreenSide.right => new RGB(0, 1, 0) * dim,
                    ScreenSide.bottom => new RGB(0, 0, 1) * dim,
                    _ => new RGB(1, 1, 0) * dim,
                }).ToArray();
            case TestPattern.Cursor(var index, _):
            {
                var output = new RGB[n];
                foreach (var k in new[] { -2, -1, 1, 2 })
                    if (index + k >= 0 && index + k < n)
                        output[index + k] = new RGB(0.15f, 0.25f, 1) * (Math.Abs(k) == 1 ? 0.25f : 0.08f);
                if (index >= 0 && index < n) output[index] = new RGB(1, 1, 1) * 0.6f;
                return output;
            }
            case TestPattern.Calibration(var color):
            {
                // Same path as effects and solid colors: calibration, white balance, gains, brightness.
                var a = Config.Adjustments;
                a.Gamma = 1;
                a.Saturation = 1;
                return Enumerable.Repeat(a.Apply(color, useBacklight: false), n).ToArray();
            }
            default:
                return new RGB[n];
        }
    }
}
