using System.IO.Ports;
using Ledly;

// Development host: runs the core (engine, API, web UI) on any OS with a fake screen and audio,
// so everything but the Windows-specific parts can be tried from a Mac:
//   dotnet run --project src/Ledly.Dev            → http://127.0.0.1:8765/
//   dotnet run --project src/Ledly.Dev -- --data /tmp/ledly-dev --port 8766
//   dotnet run --project src/Ledly.Dev -- --flash /dev/cu.usbserial-XXXX 6

var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ledly-dev");
int? port = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
}

var platform = new DevPlatform(data);
Directory.CreateDirectory(data);
Log.FilePath = Path.Combine(data, "ledly.log");
if (Cli.TryRun(args, platform) is { } exitCode) return exitCode;

var settings = Settings.Load(data);
if (port is { } p) settings.ApiPort = p;
Flasher.BackupFolder = Path.Combine(data, "Backups");
var engine = LEDEngine.Start(platform);
APIServer.Start(platform);
Updater.Start(platform);
Console.WriteLine($"Ledly dev host — http://127.0.0.1:{settings.ApiPort}/  (data: {data}, Ctrl+C to quit)");

var quit = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
platform.QuitRequested += quit.Set;
quit.Wait();
engine.Shutdown();
return 0;

sealed class DevPlatform(string dataDirectory) : IPlatform
{
    public event Action? QuitRequested;
    public string Name => "dev";
    public string DataDirectory => dataDirectory;
    public IScreenCapture CreateScreenCapture() => new FakeCapture();
    public IAudioCapture CreateAudioCapture() => new FakeAudio();

    public IReadOnlyList<SerialPortInfo> ListSerialPorts() => SerialPort.GetPortNames()
        .Where(p => !p.Contains("Bluetooth") && !p.Contains("debug-console") && !p.StartsWith("/dev/tty."))
        .Select(p => new SerialPortInfo(p, Path.GetFileName(p), p.Contains("usbserial") || p.Contains("wchusb") || p.Contains("usbmodem")))
        .ToList();

    public IReadOnlyList<DisplayInfo> ListDisplays() =>
        [new("fake1", "Pantalla de prueba", 2560, 1440, true), new("fake2", "Segunda pantalla", 1920, 1080, false)];
    public IReadOnlyList<AppInfo> ListCaptureApps() => [new("chrome", "Google Chrome"), new("vlc", "VLC media player")];
    bool _login;
    public bool SupportsLaunchAtLogin => true;
    public bool LaunchAtLogin { get => _login; set => _login = value; }
    public void OpenUrl(string url) => Console.WriteLine($"open {url}");
    public void ShowWindow() => Console.WriteLine("show window");
    public void Quit() => QuitRequested?.Invoke();
    public string? InstallUpdate(string packagePath, string version) => $"dev host: would install {version} from {packagePath}";
}

/// <summary>Animated test picture: moving hue gradient with letterbox bars.</summary>
sealed class FakeCapture : IScreenCapture
{
    public event CaptureFrameHandler? Frame;
    public event Action<CaptureStatus>? StatusChanged;
    public event Action<string>? SourceChanged;
    Timer? _timer;
    readonly byte[] _pixels = new byte[256 * 144 * 4];
    readonly DateTime _start = DateTime.UtcNow;
    readonly object _gate = new();

    public void Update(CaptureTarget? target)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (target is null)
            {
                StatusChanged?.Invoke(CaptureStatus.Stopped);
                return;
            }
            StatusChanged?.Invoke(new CaptureStatus(CaptureStatusKind.running));
            SourceChanged?.Invoke(target.App is { } app ? $"Ventana de {app}" : $"Pantalla {target.DisplayId}");
            _timer = new Timer(_ => Render(), null, 0, 1000 / Math.Max(target.Fps, 1));
        }
    }

    void Render()
    {
        const int w = 256, h = 144, bar = 18;
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        lock (_pixels)
        {
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                if (y < bar || y >= h - bar)
                {
                    _pixels[o] = _pixels[o + 1] = _pixels[o + 2] = 0;
                }
                else
                {
                    var c = RGB.FromHSV((float)(x / (double)w + y / (double)h * 0.3 + t * 0.1), 0.9f, 0.9f);
                    _pixels[o] = (byte)c.B8;
                    _pixels[o + 1] = (byte)c.G8;
                    _pixels[o + 2] = (byte)c.R8;
                }
                _pixels[o + 3] = 255;
            }
            var frame = new CaptureFrame(_pixels, w, h, w * 4);
            Frame?.Invoke(in frame);
        }
    }

    public void Dispose() => Update(null);
}

/// <summary>120 bpm kick plus a wandering tone.</summary>
sealed class FakeAudio : IAudioCapture
{
    public event Action<ReadOnlyMemory<float>, int, int>? Samples;
    Timer? _timer;
    long _n;
    public bool IsRunning => _timer is not null;

    public void Start() => _timer ??= new Timer(_ => Generate(), null, 0, 20);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    void Generate()
    {
        const int rate = 48000, count = 960;
        var buffer = new float[count * 2];
        for (var i = 0; i < count; i++, _n++)
        {
            var t = _n / (double)rate;
            var beat = t * 2 % 1;
            var kick = Math.Exp(-beat * 12) * Math.Sin(2 * Math.PI * 55 * t);
            var tone = 0.3 * Math.Sin(2 * Math.PI * (440 + 300 * Math.Sin(t * 0.5)) * t);
            buffer[i * 2] = (float)(kick * 0.8 + tone);
            buffer[i * 2 + 1] = (float)(kick * 0.8 + tone * 0.5);
        }
        Samples?.Invoke(buffer, 2, rate);
    }

    public void Dispose() => Stop();
}
