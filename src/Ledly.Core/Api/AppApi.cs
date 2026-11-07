using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Ledly;

// The app API (/api/app/*) and the embedded web UI. Contract: docs/app-api.md.
public sealed partial class APIServer
{
    readonly Timer _testWatchdog;
    DateTime _lastTestCall = DateTime.UtcNow;

    readonly object _cacheGate = new();
    (DateTime At, IReadOnlyList<DisplayInfo> Value)? _displays;
    (DateTime At, IReadOnlyList<AppInfo> Value)? _apps;
    (DateTime At, IReadOnlyList<string> Value)? _addresses;
    static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(2);

    static HttpResponse Ok(int status = 200) => HttpResponse.Json(new JsonObject { ["ok"] = true }, status);

    HttpResponse HandleApp(HttpRequest req)
    {
        var method = req.Method == "HEAD" ? "GET" : req.Method;
        var sub = req.Path.Length > "/api/app".Length ? req.Path["/api/app".Length..] : "/";
        switch (method, sub)
        {
            case ("GET", "/settings"):
                return HttpResponse.Json(AppSettings());
            case ("PATCH" or "POST" or "PUT", "/settings"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                var patch = (JsonObject)body.DeepClone();
                if (patch.ContainsKey("launchAtLogin"))
                {
                    if (Bool(patch["launchAtLogin"]) is not { } login) return HttpResponse.Error("launchAtLogin must be a boolean", 400);
                    patch.Remove("launchAtLogin");
                    try { _platform.LaunchAtLogin = login; }
                    catch (Exception e) { return HttpResponse.Error(e.Message, 500); }
                }
                if (Settings.Shared.Patch(patch) is { } error) return HttpResponse.Error(error, 400);
                return HttpResponse.Json(AppSettings());
            }
            case ("POST", "/settings/reset-image"):
                Settings.Shared.ResetImage();
                return HttpResponse.Json(AppSettings());

            case ("GET", "/status"):
                return HttpResponse.Json(AppStatus());
            case ("GET", "/meta"):
                return HttpResponse.Json(AppMeta());

            case ("POST", "/test"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                return SetTestPattern(body);
            }
            case ("POST", "/layout/solve"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                return SolveLayout(body);
            }

            case ("POST", "/device/reconnect"):
                LEDEngine.Shared.Reconnect();
                return Ok();
            case ("POST", "/device/refresh-ports"):
                LEDEngine.Shared.RefreshPorts();
                return Ok();
            case ("POST", "/device/flash"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                var engine = LEDEngine.Shared;
                if (engine.FlashState.Kind == FlashStateKind.running) return HttpResponse.Error("already flashing", 409);
                var pin = Number(body["pin"]) is { } p ? (int)p : Settings.Shared.DataPin;
                var port = Str(body["port"]) ?? Settings.Shared.PortPath;
                if (string.IsNullOrEmpty(port)) return HttpResponse.Error(L.Tr("Elige primero el puerto del Arduino."), 400);
                if (!Firmware.Pins.Contains(pin)) return HttpResponse.Error($"invalid pin {pin}", 400);
                engine.InstallFirmware(pin, port);
                return Ok(202);
            }
            case ("POST", "/device/flash/reset"):
                LEDEngine.Shared.ResetFlashState();
                return Ok();

            case ("GET", "/capture.png"):
                return CapturePng();

            case ("POST", "/overlay/clear"):
                LEDEngine.Shared.ClearOverlay();
                return Ok();
            case ("POST", "/live/end"):
                LEDEngine.Shared.EndLive();
                return Ok();

            case ("POST", "/update/check"):
                Background(() => Updater.Shared.CheckAsync(userInitiated: true), "update check");
                return Ok(202);
            case ("POST", "/update/install"):
                Background(() => Updater.Shared.InstallAsync(), "update install");
                return Ok(202);
            case ("POST", "/update/skip"):
                Updater.Shared.Skip();
                return Ok();

            case ("POST", "/open"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                if (Str(body["url"]) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return HttpResponse.Error("url must be an http(s) URL", 400);
                _platform.OpenUrl(uri.AbsoluteUri);
                return Ok();
            }
            case ("POST", "/quit"):
            {
                if (!req.IsLoopback) return HttpResponse.Error("only from this PC", 403);
                var response = Ok();
                response.AfterSend = () => Task.Run(async () =>
                {
                    await Task.Delay(100);
                    _platform.Quit();
                });
                return response;
            }
            default:
                return HttpResponse.Json(new JsonObject { ["error"] = "not found", ["path"] = req.Path }, 404);
        }
    }

    static void Background(Func<Task> work, string what)
    {
        Task task;
        try { task = work(); }
        catch (Exception e)
        {
            Log.Write($"{what}: {e.Message}");
            throw;
        }
        task.ContinueWith(t => Log.Write($"{what}: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
    }

    JsonObject AppSettings()
    {
        var json = Settings.Shared.ToJson();
        bool? login;
        try { login = _platform.LaunchAtLogin; }
        catch { login = null; }
        json["launchAtLogin"] = login ?? false;
        return json;
    }

    // MARK: status

    /// <summary>Everything the web UI shows that isn't a setting. Each part is guarded so a failing one doesn't hide the rest.</summary>
    public JsonObject AppStatus()
    {
        var engine = LEDEngine.Shared;
        var s = Settings.Shared;
        return new JsonObject
        {
            ["version"] = Updater.CurrentVersion,
            ["platform"] = _platform.Name,
            ["device"] = Guard(AppDevice),
            ["ports"] = Guard(() => new JsonArray(engine.Ports.Select(p => (JsonNode)new JsonObject
            {
                ["path"] = p.Path, ["name"] = p.Name, ["looksLikeArduino"] = p.LooksLikeArduino, ["description"] = p.Description,
            }).ToArray())),
            ["displays"] = Guard(() => new JsonArray(Displays().Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["name"] = d.Name, ["width"] = d.Width, ["height"] = d.Height, ["primary"] = d.Primary,
            }).ToArray())),
            ["apps"] = Guard(() => new JsonArray(Apps().Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["name"] = a.Name }).ToArray())),
            ["capture"] = new JsonObject
            {
                ["status"] = engine.GrabberStatus.Kind.ToString(),
                ["message"] = engine.GrabberStatus.Message,
                ["source"] = engine.CaptureSource,
            },
            ["fps"] = Math.Round(engine.Fps * 10, MidpointRounding.AwayFromZero) / 10,
            ["power"] = PowerJson(),
            ["overlay"] = engine.OverlayActive,
            ["live"] = engine.LiveSource is not null,
            ["liveSource"] = engine.LiveSource,
            ["flash"] = FlashJson(engine.FlashState),
            ["api"] = new JsonObject
            {
                // The listener also runs (loopback only) while the API is off; the UI shows it as stopped.
                ["status"] = (s.ApiEnabled ? Status : ServerStatusKind.stopped).ToString(),
                ["port"] = Port,
                ["error"] = Error,
                ["clients"] = Clients,
                ["addresses"] = Guard(() => new JsonArray(Addresses().Select(a => (JsonNode)a).ToArray())),
            },
            ["ddp"] = new JsonObject
            {
                ["status"] = DdpStatus.ToString(),
                ["port"] = s.DdpPort,
                ["error"] = DdpError,
            },
            ["update"] = Guard(UpdateJson),
            ["audio"] = AudioJson(),
        };
    }

    static JsonNode? Guard(Func<JsonNode?> part)
    {
        try { return part(); }
        catch (Exception e)
        {
            if (e is not NotImplementedException) Log.Write($"api status: {e.Message}");
            return null;
        }
    }

    static JsonObject AppDevice()
    {
        var status = LEDEngine.Shared.DeviceStatus;
        int? version = null, pin = null;
        try
        {
            version = LEDDevice.LedlyVersion(status.Firmware);
            pin = LEDDevice.LedlyPin(status.Firmware);
        }
        catch (NotImplementedException)
        {
        }
        return new JsonObject
        {
            ["status"] = status.Kind == DeviceStatusKind.failed ? "error" : status.Kind.ToString(),
            ["port"] = Settings.Shared.PortPath,
            ["firmware"] = status.Firmware,
            ["error"] = status.Error,
            ["ledlyVersion"] = version,
            ["ledlyPin"] = pin,
            ["firmwareOutdated"] = status.Kind == DeviceStatusKind.connected && version is { } v && v < Firmware.Version,
        };
    }

    static JsonObject FlashJson(FlashState flash) => new()
    {
        ["state"] = flash.Kind.ToString(),
        ["step"] = flash.Step?.Kind.ToString(),
        ["progress"] = flash.Step is { } step ? Math.Round(step.Progress, 3) : 0,
        ["error"] = flash.Error,
    };

    static JsonNode UpdateJson()
    {
        var u = Updater.Shared;
        return new JsonObject
        {
            ["state"] = u.State.ToString(),
            ["available"] = u.Available,
            ["notes"] = u.Notes,
            ["error"] = u.Error,
        };
    }

    IReadOnlyList<DisplayInfo> Displays() => Cached(ref _displays, _platform.ListDisplays);
    IReadOnlyList<AppInfo> Apps() => Cached(ref _apps, _platform.ListCaptureApps);
    IReadOnlyList<string> Addresses() => Cached(ref _addresses, LocalAddresses);

    IReadOnlyList<T> Cached<T>(ref (DateTime At, IReadOnlyList<T> Value)? cache, Func<IReadOnlyList<T>> load)
    {
        lock (_cacheGate)
        {
            if (cache is { } c && DateTime.UtcNow - c.At < CacheTime) return c.Value;
        }
        var value = load();
        lock (_cacheGate) cache = (DateTime.UtcNow, value);
        return value;
    }

    /// <summary>This PC's IPv4 addresses on the network (not loopback, not link-local).</summary>
    static IReadOnlyList<string> LocalAddresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                var a = u.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(a)) continue;
                var b = a.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                var text = a.ToString();
                if (!list.Contains(text)) list.Add(text);
            }
        }
        return list;
    }

    // MARK: meta

    static JsonObject AppMeta()
    {
        return new JsonObject
        {
            ["effects"] = Guard(() => new JsonArray(EffectKindInfo.Ordered.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.ToString(),
                ["titleKey"] = e.TitleKey(),
                ["music"] = e.IsMusic(),
                ["usesColor"] = e.UsesColor(),
                ["wled"] = WledNumber(e),
            }).ToArray())),
            ["equalizer"] = new JsonObject
            {
                ["layouts"] = Names<EqualizerLayout>(),
                ["styles"] = Names<EqualizerStyle>(),
                ["colors"] = Names<EqualizerColors>(),
                ["presets"] = new JsonArray(EqualizerOptions.Presets.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["titleKey"] = p.TitleKey, ["layout"] = p.Options.Layout.ToString(),
                    ["style"] = p.Options.Style.ToString(), ["colors"] = p.Options.Colors.ToString(),
                }).ToArray()),
            },
            ["firmware"] = new JsonObject
            {
                ["version"] = Firmware.Version,
                ["pins"] = new JsonArray(Firmware.Pins.Select(p => (JsonNode)p).ToArray()),
            },
            ["languages"] = Names<AppLanguage>(),
            ["calibrationCorners"] = new JsonArray(Enum.GetValues<CalibrationCorner>().Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.ToString(), ["titleKey"] = c.TitleKey(), ["input"] = c.Input().Hex,
            }).ToArray()),
            ["modes"] = Names<LightMode>(),
            ["startCorners"] = Names<StartCorner>(),
            ["sampling"] = Names<SamplingMode>(),
            ["smoothing"] = Names<SmoothingKind>(),
            ["repo"] = Updater.Repo,
        };
    }

    static JsonArray Names<T>() where T : struct, Enum => new(Enum.GetNames<T>().Select(n => (JsonNode)n).ToArray());

    // MARK: actions

    HttpResponse SetTestPattern(JsonObject body)
    {
        var engine = LEDEngine.Shared;
        _lastTestCall = DateTime.UtcNow;
        var pattern = Str(body["pattern"]);
        TestPattern? value;
        switch (pattern)
        {
            case null:
                value = null;
                break;
            case "direction":
                value = new TestPattern.Direction();
                break;
            case "sides":
                value = new TestPattern.Sides();
                break;
            case "calibration":
            {
                if (body.ContainsKey("color") && ParseColor(body["color"]) is null) return HttpResponse.Error("color must be \"#RRGGBB\"", 400);
                value = new TestPattern.Calibration(ParseColor(body["color"]) ?? RGB.White);
                break;
            }
            case "cursor":
            {
                var count = Number(body["count"]) is { } c ? (int)c : Settings.Shared.Layout.Count;
                var index = Number(body["index"]) is { } i ? (int)i : 0;
                if (count <= 0) return HttpResponse.Error("count must be positive", 400);
                value = new TestPattern.Cursor(((index % count) + count) % count, count);
                break;
            }
            default:
                return HttpResponse.Error($"unknown pattern {pattern}", 400);
        }
        engine.TestPattern = value;
        return Ok();
    }

    /// <summary>Wizard patterns stop by themselves if the UI goes quiet for 30 s (window closed, UI crashed).</summary>
    void TestWatchdog()
    {
        try
        {
            if (DateTime.UtcNow - _lastTestCall < TimeSpan.FromSeconds(30)) return;
            var engine = LEDEngine.Shared;
            if (engine.TestPattern is TestPattern.Cursor or TestPattern.Calibration) engine.TestPattern = null;
        }
        catch
        {
            // Engine not started yet.
        }
    }

    static HttpResponse SolveLayout(JsonObject body)
    {
        if (Number(body["count"]) is not { } countValue) return HttpResponse.Error("count is required", 400);
        if (body["corners"] is not JsonObject corners) return HttpResponse.Error("corners is required", 400);
        var marks = new Dictionary<StartCorner, int>();
        foreach (var (key, value) in corners)
        {
            if (ParseEnum<StartCorner>(key) is not { } corner) return HttpResponse.Error($"unknown corner {key}", 400);
            if (Number(value) is not { } index) return HttpResponse.Error($"corner {key} must be a number", 400);
            marks[corner] = (int)index;
        }
        var gap = Bool(body["hasBottomGap"]) ?? false;
        var solved = Settings.Shared.Layout.Solved((int)countValue, marks, gap);
        if (solved is null)
            return HttpResponse.Error(L.Tr("Las esquinas marcadas no cuadran con una tira que dé la vuelta a la pantalla. Comprueba que marcaste cada esquina en su sitio y que la luz estaba en el último LED."), 400);
        return HttpResponse.Json(new JsonObject { ["layout"] = System.Text.Json.JsonSerializer.SerializeToNode(solved, Settings.Json) });
    }

    static HttpResponse CapturePng()
    {
        var preview = LEDEngine.Shared.CapturePreview();
        if (preview is not { } p) return new HttpResponse(204);
        var response = new HttpResponse(200, "image/png", p.Png);
        var r = p.Content;
        // Content is already in the image's pixels.
        static int I(double v) => (int)Math.Round(v);
        response.Headers["X-Content-Rect"] = string.Create(CultureInfo.InvariantCulture, $"{I(r.X)},{I(r.Y)},{I(r.Width)},{I(r.Height)}");
        response.Headers["X-Image-Size"] = string.Create(CultureInfo.InvariantCulture, $"{p.Width},{p.Height}");
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Access-Control-Expose-Headers"] = "X-Content-Rect, X-Image-Size";
        return response;
    }

    // MARK: static web UI

    static readonly Lazy<Dictionary<string, string>> WebResources = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in typeof(APIServer).Assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (normalized.StartsWith("web/", StringComparison.Ordinal)) map[normalized] = name;
        }
        return map;
    });

    static readonly ConcurrentDictionary<string, byte[]> WebCache = new();

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8", [".webmanifest"] = "application/manifest+json",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".ico"] = "image/x-icon",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".txt"] = "text/plain; charset=utf-8", [".wasm"] = "application/wasm",
    };

    static HttpResponse ServeStatic(string path)
    {
        var relative = path is "/" or "/app" ? "index.html" : path["/app/".Length..];
        if (relative.Split('/').Any(part => part is ".." or "." || part.Length == 0))
            return HttpResponse.Error("not found", 404);
        var key = "web/" + relative;
        if (!WebResources.Value.TryGetValue(key, out var resource))
        {
            if (relative == "index.html") return HttpResponse.Text(FallbackPage, "text/html; charset=utf-8");
            return HttpResponse.Json(new JsonObject { ["error"] = "not found", ["path"] = path }, 404);
        }
        var bytes = WebCache.GetOrAdd(resource, name =>
        {
            using var stream = typeof(APIServer).Assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });
        var type = ContentTypes.GetValueOrDefault(Path.GetExtension(relative), "application/octet-stream");
        var response = new HttpResponse(200, type, bytes);
        response.Headers["Cache-Control"] = "no-cache";
        return response;
    }

    /// <summary>Shown at / when the build has no web UI embedded.</summary>
    const string FallbackPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Ledly</title>
        <style>body{background:#121214;color:#ECECEE;font:15px system-ui,sans-serif;padding:40px}a{color:#7AA2FF}</style></head>
        <body><h1>Ledly</h1><p>This build has no web UI. API reference: <a href="/api/docs">/api/docs</a></p></body></html>
        """;
}
