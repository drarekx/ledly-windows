using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ledly;

/// <summary>What drives the strip while it is on. Names are the API/settings raw values.</summary>
public enum LightMode { screen, effect, color }

/// <summary>How the color of each screen zone is computed.</summary>
public enum SamplingMode
{
    /// <summary>Plain average.</summary>
    mean,
    /// <summary>Average in linear light: bright details count as much as they look.</summary>
    linear,
    /// <summary>Most saturated/brightest part of the zone wins.</summary>
    dominant,
}

/// <summary>UI language; "system" follows Windows.</summary>
public enum AppLanguage { system, en, es, ca }

/// <summary>
/// User preferences, persisted as JSON (<c>settings.json</c> in the data folder).
/// Thread-safe: any thread may read or set; <see cref="Changed"/> is raised on the setting thread
/// with the property name. Saving is debounced.
/// </summary>
public sealed class Settings
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    static Settings? _shared;
    public static Settings Shared => _shared ?? throw new InvalidOperationException("Settings.Load first");

    readonly object _lock = new();
    string? _path;
    Timer? _saveTimer;

    /// <summary>Raised after a property changes, with its C# name.</summary>
    public event Action<string>? Changed;

    /// <summary>Loads (or creates) the settings file in <paramref name="directory"/> and makes it <see cref="Shared"/>.</summary>
    public static Settings Load(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch
        {
            s = new();
        }
        s._path = path;
        if (string.IsNullOrEmpty(s.DeviceId))
        {
            s.DeviceId = Convert.ToHexString(RandomNumberGenerator()).ToLowerInvariant();
            s.SaveNow();
        }
        _shared = s;
        return s;

        static byte[] RandomNumberGenerator() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
    }

    // Output
    bool _isOn = true; public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
    LightMode _mode = LightMode.screen; public LightMode Mode { get => _mode; set => Set(ref _mode, value); }
    EffectKind _effect = EffectKind.rainbowSwirl; public EffectKind Effect { get => _effect; set => Set(ref _effect, value); }
    /// <summary>0.1…3, 1 = normal.</summary>
    double _effectSpeed = 1; public double EffectSpeed { get => _effectSpeed; set => Set(ref _effectSpeed, Math.Clamp(value, 0.1, 3)); }
    EqualizerOptions _equalizer = new(); public EqualizerOptions Equalizer { get => _equalizer; set => Set(ref _equalizer, value ?? new()); }
    string _colorHex = "#FF8A3D"; public string ColorHex { get => _colorHex; set => Set(ref _colorHex, RGB.FromHex(value)?.Hex ?? _colorHex); }
    /// <summary>0…1.</summary>
    double _brightness = 0.8; public double Brightness { get => _brightness; set => Set(ref _brightness, Math.Clamp(value, 0, 1)); }

    // Device
    /// <summary>COM port name ("COM3"); null = none.</summary>
    string? _portPath; public string? PortPath { get => _portPath; set => Set(ref _portPath, string.IsNullOrEmpty(value) ? null : value); }
    int _dataPin = 6; public int DataPin { get => _dataPin; set => Set(ref _dataPin, Math.Clamp(value, 2, 13)); }
    LEDLayout _layout = new(); public LEDLayout Layout { get => _layout; set => Set(ref _layout, value ?? new()); }

    // Screen capture
    /// <summary>Platform id of the captured display; empty = primary.</summary>
    string _displayId = ""; public string DisplayId { get => _displayId; set => Set(ref _displayId, value ?? ""); }
    /// <summary>Capture only this app's main window (process name); null = the whole display.</summary>
    string? _captureApp; public string? CaptureApp { get => _captureApp; set => Set(ref _captureApp, string.IsNullOrEmpty(value) ? null : value); }
    string? _captureAppName; public string? CaptureAppName { get => _captureAppName; set => Set(ref _captureAppName, value); }
    int _captureFps = 30; public int CaptureFps { get => _captureFps; set => Set(ref _captureFps, Math.Clamp(value, 5, 60)); }
    SamplingMode _sampling = SamplingMode.linear; public SamplingMode Sampling { get => _sampling; set => Set(ref _sampling, value); }
    bool _blackBorders = true; public bool BlackBorders { get => _blackBorders; set => Set(ref _blackBorders, value); }

    // Image
    /// <summary>Smoothing time constant in milliseconds (screen mode).</summary>
    double _smoothingMs = 150; public double SmoothingMs { get => _smoothingMs; set => Set(ref _smoothingMs, Math.Clamp(value, 0, 2000)); }
    SmoothingKind _smoothing = SmoothingKind.spring; public SmoothingKind Smoothing { get => _smoothing; set => Set(ref _smoothing, value); }
    /// <summary>Hold 8-bit levels that would toggle every frame (dim scenes).</summary>
    bool _antiFlicker = true; public bool AntiFlicker { get => _antiFlicker; set => Set(ref _antiFlicker, value); }
    double _gamma = 1.5; public double Gamma { get => _gamma; set => Set(ref _gamma, value); }
    double _saturation = 1; public double Saturation { get => _saturation; set => Set(ref _saturation, value); }
    double _temperature = 6600; public double Temperature { get => _temperature; set => Set(ref _temperature, value); }
    double _backlight; public double Backlight { get => _backlight; set => Set(ref _backlight, value); }
    double _redGain = 1; public double RedGain { get => _redGain; set => Set(ref _redGain, value); }
    double _greenGain = 1; public double GreenGain { get => _greenGain; set => Set(ref _greenGain, value); }
    double _blueGain = 1; public double BlueGain { get => _blueGain; set => Set(ref _blueGain, value); }
    /// <summary>Target color per corner of the RGB cube. Replace the whole object to change it.</summary>
    ColorCalibration _calibration = new(); public ColorCalibration Calibration { get => _calibration; set => Set(ref _calibration, value ?? new()); }

    // API
    bool _apiEnabled = true; public bool ApiEnabled { get => _apiEnabled; set => Set(ref _apiEnabled, value); }
    int _apiPort = 8765; public int ApiPort { get => _apiPort; set => Set(ref _apiPort, Math.Clamp(value, 1, 65535)); }
    /// <summary>Only accept connections from this PC.</summary>
    bool _apiLocalOnly; public bool ApiLocalOnly { get => _apiLocalOnly; set => Set(ref _apiLocalOnly, value); }
    /// <summary>Optional bearer token; empty = no auth.</summary>
    string _apiToken = ""; public string ApiToken { get => _apiToken; set => Set(ref _apiToken, value ?? ""); }
    bool _ddpEnabled = true; public bool DdpEnabled { get => _ddpEnabled; set => Set(ref _ddpEnabled, value); }
    int _ddpPort = 4048; public int DdpPort { get => _ddpPort; set => Set(ref _ddpPort, Math.Clamp(value, 1, 65535)); }
    /// <summary>Announce as a WLED device on the network (mDNS _wled._tcp).</summary>
    bool _wledDiscovery = true; public bool WledDiscovery { get => _wledDiscovery; set => Set(ref _wledDiscovery, value); }

    bool _powerLimitEnabled = true; public bool PowerLimitEnabled { get => _powerLimitEnabled; set => Set(ref _powerLimitEnabled, value); }
    /// <summary>Safe for a strip fed from a small supply; raise it to what the supply really gives.</summary>
    int _powerLimitMilliamps = 1000; public int PowerLimitMilliamps { get => _powerLimitMilliamps; set => Set(ref _powerLimitMilliamps, Math.Clamp(value, 100, 60000)); }

    /// <summary>Turn the strip off while the PC sleeps or the screens are off.</summary>
    bool _offWhenAsleep = true; public bool OffWhenAsleep { get => _offWhenAsleep; set => Set(ref _offWhenAsleep, value); }

    // App
    AppLanguage _language = AppLanguage.system; public AppLanguage Language { get => _language; set => Set(ref _language, value); }
    bool _autoCheckUpdates = true; public bool AutoCheckUpdates { get => _autoCheckUpdates; set => Set(ref _autoCheckUpdates, value); }
    DateTime? _lastUpdateCheck; public DateTime? LastUpdateCheck { get => _lastUpdateCheck; set => Set(ref _lastUpdateCheck, value); }
    string? _skippedVersion; public string? SkippedVersion { get => _skippedVersion; set => Set(ref _skippedVersion, value); }
    /// <summary>Last baud rate that worked, tried first when connecting.</summary>
    int _lastBaud; public int LastBaud { get => _lastBaud; set => Set(ref _lastBaud, value); }

    /// <summary>Stable fake MAC address for WLED clients that need a unique id (12 hex digits).</summary>
    string _deviceId = ""; public string DeviceId { get => _deviceId; set => Set(ref _deviceId, value ?? ""); }

    // Derived

    [JsonIgnore]
    public RGB Color
    {
        get => RGB.FromHex(ColorHex) ?? new RGB(1, 0.54f, 0.24f);
        set => ColorHex = value.Hex;
    }

    [JsonIgnore]
    public ColorAdjustments Adjustments
    {
        get
        {
            var a = new ColorAdjustments
            {
                Brightness = (float)Brightness,
                Gamma = (float)Gamma,
                Saturation = (float)Saturation,
                Temperature = (float)Temperature,
                Backlight = (float)Backlight,
                RedGain = (float)RedGain,
                GreenGain = (float)GreenGain,
                BlueGain = (float)BlueGain,
                Calibration = Calibration.Prepared(),
            };
            a.Prepare();
            return a;
        }
    }

    /// <summary>Back to the defaults for the image section only.</summary>
    public void ResetImage()
    {
        var d = new Settings();
        SmoothingMs = d.SmoothingMs;
        Smoothing = d.Smoothing;
        AntiFlicker = d.AntiFlicker;
        Gamma = d.Gamma;
        Saturation = d.Saturation;
        Temperature = d.Temperature;
        Backlight = d.Backlight;
        RedGain = d.RedGain;
        GreenGain = d.GreenGain;
        BlueGain = d.BlueGain;
    }

    // JSON

    /// <summary>Every setting as JSON (camelCase), as saved on disk.</summary>
    public JsonObject ToJson()
    {
        lock (_lock) return JsonSerializer.SerializeToNode(this, Json)!.AsObject();
    }

    static readonly Dictionary<string, PropertyInfo> PatchableProperties = typeof(Settings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.Name != nameof(DeviceId))
        .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p);

    /// <summary>
    /// Applies a partial JSON object (same keys as <see cref="ToJson"/>). Nested objects (layout, equalizer,
    /// calibration) are merged with their current value. Returns an error message, or null when applied.
    /// </summary>
    public string? Patch(JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (!PatchableProperties.TryGetValue(key, out var property)) return $"unknown setting {key}";
            try
            {
                var node = value;
                if (value is JsonObject partial && property.GetValue(this) is { } current
                    && JsonSerializer.SerializeToNode(current, property.PropertyType, Json) is JsonObject merged)
                {
                    foreach (var (k, v) in partial) merged[k] = v?.DeepClone();
                    node = merged;
                }
                var converted = node is null ? null : node.Deserialize(property.PropertyType, Json);
                property.SetValue(this, converted);
            }
            catch (Exception e)
            {
                return $"invalid value for {key}: {e.Message}";
            }
        }
        return null;
    }

    // Storage

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        lock (_lock)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
        }
        if (_path is null) return;
        ScheduleSave();
        Changed?.Invoke(name);
    }

    void ScheduleSave()
    {
        lock (_lock)
        {
            _saveTimer ??= new Timer(_ => SaveNow());
            _saveTimer.Change(300, Timeout.Infinite);
        }
    }

    /// <summary>Writes the file now (also called on quit).</summary>
    public void SaveNow()
    {
        if (_path is null) return;
        try
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(this, Json);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"settings save failed: {e.Message}");
        }
    }
}

/// <summary>Minimal log to stderr and the data folder's ledly.log.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Console.Error.WriteLine(line);
        if (FilePath is null) return;
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
