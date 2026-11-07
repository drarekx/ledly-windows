using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ledly;

public enum ServerStatusKind { stopped, running, failed }

/// <summary>
/// HTTP/1.1 + WebSocket server on raw sockets: native API (<c>/api/...</c>), a WLED-compatible subset
/// (<c>/json/...</c>, <c>/win</c>, <c>/ws</c>) so Home Assistant and WLED clients can drive the strip,
/// the app API for the web UI (<c>/api/app/...</c>, see docs/app-api.md) and the web UI itself; plus
/// DDP (UDP) and the mDNS announcement. Port of the Mac app's APIServer.swift.
///
/// Network policy: <see cref="Settings.ApiLocalOnly"/> binds to 127.0.0.1. With the API disabled it still
/// listens on 127.0.0.1 (the app window needs it) but only answers the web UI, <c>/api/app/*</c>,
/// <c>/api/ws</c> and the docs. Loopback clients never need the token.
/// </summary>
public sealed partial class APIServer
{
    static APIServer? _shared;
    public static APIServer Shared => _shared ?? throw new InvalidOperationException("APIServer.Start first");

    /// <summary>State of the HTTP listener (it also runs, on loopback, while the API is disabled).</summary>
    public ServerStatusKind Status { get; private set; }
    public int Port { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Open WebSocket connections.</summary>
    public int Clients { get { lock (_gate) return _sockets.Count; } }

    public ServerStatusKind DdpStatus { get; private set; }
    public int DdpPort { get; private set; }
    public string? DdpError { get; private set; }

    /// <summary>Base URL of the web UI on loopback (always available while the app runs).</summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}/";

    readonly IPlatform _platform;
    readonly object _gate = new();
    readonly List<WebSocketClient> _sockets = [];
    readonly HashSet<TcpClient> _connections = [];
    readonly DateTime _startDate = DateTime.UtcNow;
    readonly DDPReceiver _ddp = new();
    readonly Mdns _mdns = new();
    (bool Enabled, int Port, bool LocalOnly)? _ddpConfig;
    (int Port, bool Loopback)? _bound;
    (bool Enabled, int Port, string Mac)? _mdnsConfig;
    TcpListener? _listener;
    CancellationTokenSource? _listenCts;

    readonly Timer _settingsTimer;
    readonly Timer _stateTimer;
    int _settingsPending, _statePending;
    long _lastColorsTicks;

    /// <summary>Starts listening per Settings and follows their changes.</summary>
    public static APIServer Start(IPlatform platform)
    {
        var server = new APIServer(platform);
        _shared = server;
        server.Run();
        return server;
    }

    APIServer(IPlatform platform)
    {
        _platform = platform;
        _settingsTimer = new Timer(_ => SettingsTick());
        _stateTimer = new Timer(_ => StateTick());
        _testWatchdog = new Timer(_ => TestWatchdog(), null, 5000, 5000);
    }

    void Run()
    {
        _ddp.OnFrame = (bytes, source) =>
        {
            try { LEDEngine.Shared.ReceiveLive(bytes, source); } catch (Exception e) { Log.Write($"ddp frame: {e.Message}"); }
        };
        _ddp.OnStatus = (status, error, port) =>
        {
            DdpStatus = status;
            DdpError = error;
            DdpPort = port;
            ScheduleState();
        };
        // Bursts of changes (sliders) are coalesced: at most one apply + broadcast every 100 ms.
        Settings.Shared.Changed += _ =>
        {
            if (Interlocked.Exchange(ref _settingsPending, 1) == 0) _settingsTimer.Change(100, Timeout.Infinite);
        };
        try
        {
            LEDEngine.Shared.StateChanged += ScheduleState;
            LEDEngine.Shared.FrameReady += BroadcastColors;
        }
        catch (Exception e)
        {
            Log.Write($"api: engine not available: {e.Message}");
        }
        ApplySettings();
    }

    void ScheduleState()
    {
        if (Interlocked.Exchange(ref _statePending, 1) == 0) _stateTimer.Change(100, Timeout.Infinite);
    }

    void SettingsTick()
    {
        Interlocked.Exchange(ref _settingsPending, 0);
        try
        {
            ApplySettings();
            BroadcastState(settingsChanged: true);
        }
        catch (Exception e)
        {
            Log.Write($"api settings: {e}");
        }
    }

    void StateTick()
    {
        Interlocked.Exchange(ref _statePending, 0);
        try { BroadcastState(); } catch (Exception e) { Log.Write($"api state: {e}"); }
    }

    // MARK: - Listener

    /// <summary>DDP follows the API's network policy: "This PC only" binds it to loopback too.</summary>
    void ApplyDDP()
    {
        var s = Settings.Shared;
        var config = (s.DdpEnabled, s.DdpPort, s.ApiLocalOnly);
        if (_ddpConfig == config) return;
        _ddpConfig = config;
        if (s.DdpEnabled)
        {
            _ddp.Start(s.DdpPort, s.ApiLocalOnly);
        }
        else
        {
            _ddp.Stop();
            try { LEDEngine.Shared.EndLive(); } catch { }
        }
    }

    readonly object _applyGate = new();

    void ApplySettings()
    {
        lock (_applyGate)
        {
            ApplyDDP();
            var s = Settings.Shared;
            var config = (s.ApiPort, s.ApiLocalOnly || !s.ApiEnabled);
            if (_bound != config)
            {
                _bound = config;
                Restart(config.ApiPort, config.Item2);
            }
            ApplyMdns();
        }
    }

    void ApplyMdns()
    {
        var s = Settings.Shared;
        var enabled = s.ApiEnabled && s.WledDiscovery && !s.ApiLocalOnly && Status == ServerStatusKind.running;
        var config = (enabled, Port, s.DeviceId);
        if (_mdnsConfig == config) return;
        _mdnsConfig = config;
        // Off the settings path: socket work must never hold up the listener restart.
        _ = Task.Run(() =>
        {
            lock (_mdns)
            {
                if (_mdnsConfig is not { } wanted || _mdnsApplied == wanted) return;
                _mdnsApplied = wanted;
                try
                {
                    if (wanted.Enabled) _mdns.Start(wanted.Port, "Ledly", wanted.Mac);
                    else _mdns.Stop();
                }
                catch (Exception e)
                {
                    Log.Write($"mdns: {e.Message}");
                }
            }
        });
    }

    (bool Enabled, int Port, string Mac)? _mdnsApplied;

    void Restart(int port, bool loopback)
    {
        _listenCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        // Drop every open connection: a remote keep-alive connection must not outlive "this PC only".
        List<WebSocketClient> sockets;
        List<TcpClient> connections;
        lock (_gate)
        {
            sockets = [.. _sockets];
            connections = [.. _connections];
            _connections.Clear();
        }
        foreach (var client in sockets) client.Close();
        foreach (var c in connections) { try { c.Dispose(); } catch { } }

        var cts = new CancellationTokenSource();
        _listenCts = cts;
        Port = port;
        try
        {
            TcpListener listener;
            if (loopback)
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                PrepareListener(listener);
                listener.Start();
            }
            else
            {
                try
                {
                    listener = new TcpListener(IPAddress.IPv6Any, port);
                    listener.Server.DualMode = true;
                    PrepareListener(listener);
                    listener.Start();
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
                {
                    listener = new TcpListener(IPAddress.Any, port);
                    PrepareListener(listener);
                    listener.Start();
                }
            }
            _listener = listener;
            Status = ServerStatusKind.running;
            Error = null;
            Log.Write($"api: listening on {(loopback ? "127.0.0.1" : "*")}:{port}{(Settings.Shared.ApiEnabled ? "" : " (app only)")}");
            _ = Task.Run(() => AcceptLoop(listener, cts.Token));
        }
        catch (Exception e)
        {
            Status = ServerStatusKind.failed;
            Error = e.Message;
            Log.Write($"api: can't listen on {port}: {e.Message}");
        }
        ScheduleState();
    }

    static void PrepareListener(TcpListener listener)
    {
        // On Windows SO_REUSEADDR would let another process bind the same port; elsewhere it allows a quick restart.
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        else listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                if (ct.IsCancellationRequested) return;
                Log.Write($"api accept: {e.Message}");
                continue;
            }
            _ = Task.Run(() => Serve(client, ct));
        }
    }

    async Task Serve(TcpClient client, CancellationToken listenerToken)
    {
        lock (_gate) _connections.Add(client);
        var handedOver = false;
        try
        {
            client.NoDelay = true;
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var stream = client.GetStream();
            var reader = new HttpReader(stream, remote);
            var first = true;
            while (!listenerToken.IsCancellationRequested)
            {
                HttpRequest? req;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(listenerToken))
                {
                    cts.CancelAfter(first ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(120));
                    try
                    {
                        req = await reader.ReadAsync(cts.Token);
                    }
                    catch (HttpReader.TooLargeException)
                    {
                        await stream.WriteAsync(HttpResponse.Error("too large", 413).Serialize(false), CancellationToken.None);
                        return;
                    }
                }
                if (req is null) return;
                first = false;

                if (req.IsWebSocketUpgrade)
                {
                    var leftover = reader.Leftover.ToArray();
                    var ws = Upgrade(req, client, stream, out var refusal);
                    if (ws is null)
                    {
                        await stream.WriteAsync(refusal!.Serialize(false), CancellationToken.None);
                        return;
                    }
                    lock (_gate) _connections.Remove(client);
                    handedOver = true;
                    await RunWebSocket(ws, leftover, listenerToken);
                    return;
                }

                HttpResponse response;
                try
                {
                    response = Handle(req);
                }
                catch (Exception e)
                {
                    Log.Write($"api {req.Method} {req.Path}: {e}");
                    response = HttpResponse.Error(e is NotImplementedException ? "not available yet" : e.Message, 500);
                }
                if (req.Method == "HEAD") response.Body = [];
                var keepAlive = req.KeepAlive && !listenerToken.IsCancellationRequested;
                await stream.WriteAsync(response.Serialize(keepAlive), CancellationToken.None);
                if (response.AfterSend is { } after)
                {
                    try { after(); } catch (Exception e) { Log.Write($"api after: {e.Message}"); }
                }
                if (!keepAlive) return;
            }
        }
        catch
        {
            // Reset, timeout, closed by a restart.
        }
        finally
        {
            if (!handedOver)
            {
                lock (_gate) _connections.Remove(client);
                try { client.Dispose(); } catch { }
            }
        }
    }

    // MARK: - Routing

    static bool IsDocsPath(string path) => path is "/api/docs" or "/api/openapi.json";
    static bool IsStaticPath(string path) => path == "/" || path == "/app" || path.StartsWith("/app/", StringComparison.Ordinal);
    static bool IsAppPath(string path) => path == "/api/app" || path.StartsWith("/api/app/", StringComparison.Ordinal);

    static bool Authorized(HttpRequest req)
    {
        var token = Settings.Shared.ApiToken;
        if (token.Length == 0 || req.IsLoopback) return true;
        return req.Header("authorization") == $"Bearer {token}" || req.Query.GetValueOrDefault("token") == token;
    }

    HttpResponse Handle(HttpRequest req)
    {
        if (req.Method == "OPTIONS") return new HttpResponse(204);
        var path = req.Path;
        var isDocs = IsDocsPath(path);
        var isApp = IsAppPath(path);
        var isStatic = IsStaticPath(path);
        // With the API off only the window's needs are served (the listener is on loopback then).
        if (!Settings.Shared.ApiEnabled && !(isDocs || isApp || isStatic))
            return HttpResponse.Error("API disabled", 403);
        // WLED clients don't send tokens, so a token only guards the native API.
        if (path.StartsWith("/api", StringComparison.Ordinal) && !isDocs && !Authorized(req))
            return HttpResponse.Error("unauthorized", 401);
        if (isApp) return HandleApp(req);
        if (isStatic && req.Method is "GET" or "HEAD") return ServeStatic(path);
        var method = req.Method == "HEAD" ? "GET" : req.Method;

        switch (method, path)
        {
            case ("GET", "/api/docs"):
                return HttpResponse.Text(APIDocs.Html, "text/html; charset=utf-8");
            case ("GET", "/api/openapi.json"):
                return HttpResponse.Json(APIDocs.OpenAPI(Settings.Shared.ApiPort));
            case ("GET", "/api"):
                return HttpResponse.Json(ApiIndex());
            case ("GET", "/api/state"):
                return HttpResponse.Json(NativeState());
            case ("POST" or "PUT" or "PATCH", "/api/state"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Error("invalid JSON", 400);
                if (ApplyNative(body) is { } error) return HttpResponse.Error(error, 400);
                return HttpResponse.Json(NativeState());
            }
            case ("GET", "/api/effects"):
                return HttpResponse.Json(new JsonArray(EffectKindInfo.Ordered.Select(e => (JsonNode)new JsonObject
                {
                    ["id"] = e.ToString(), ["name"] = L.Tr(e.TitleKey()), ["usesColor"] = e.UsesColor(),
                }).ToArray()));
            case ("GET", "/api/leds"):
            {
                var colors = LEDEngine.Shared.CurrentColors();
                return HttpResponse.Json(new JsonObject { ["count"] = colors.Length, ["colors"] = HexArray(colors) });
            }
            case ("GET", "/api/device"):
                return HttpResponse.Json(DeviceInfo());
            case ("POST", "/api/device/reconnect"):
                LEDEngine.Shared.Reconnect();
                return HttpResponse.Json(DeviceInfo());
            case ("GET", "/api/layout"):
                return HttpResponse.Json(LayoutJson());

            // WLED compatibility
            case ("GET", "/json"):
                return HttpResponse.Json(new JsonObject
                {
                    ["state"] = WledState(), ["info"] = WledInfo(), ["effects"] = WledEffects(), ["palettes"] = new JsonArray("Default"),
                });
            case ("GET", "/json/state"):
                return HttpResponse.Json(WledState());
            case ("POST", "/json/state" or "/json"):
            {
                if (req.JsonBody is not { } body) return HttpResponse.Json(new JsonObject { ["error"] = 9 }, 400);
                ApplyWled(body);
                if (Bool(body["v"]) == true) return HttpResponse.Json(WledState());
                return HttpResponse.Json(new JsonObject { ["success"] = true });
            }
            case ("GET", "/json/info"):
                return HttpResponse.Json(WledInfo());
            case ("GET", "/json/eff"):
                return HttpResponse.Json(WledEffects());
            case ("GET", "/json/pal"):
                return HttpResponse.Json(new JsonArray("Default"));
            case ("GET", "/json/si"):
                return HttpResponse.Json(new JsonObject { ["state"] = WledState(), ["info"] = WledInfo() });
            case ("GET", "/win"):
                ApplyWledQuery(req.Query);
                return HttpResponse.Text(WledXml(), "text/xml");
            default:
                return HttpResponse.Json(new JsonObject { ["error"] = "not found", ["path"] = path }, 404);
        }
    }

    static JsonObject ApiIndex() => new()
    {
        ["name"] = "Ledly",
        ["version"] = Updater.CurrentVersion,
        ["docs"] = "/api/docs",
        ["openapi"] = "/api/openapi.json",
        ["endpoints"] = new JsonArray(
            "GET /api/state", "POST /api/state", "GET /api/effects", "GET /api/leds",
            "GET /api/device", "POST /api/device/reconnect", "GET /api/layout", "WS /api/ws",
            "GET /api/docs", "GET /api/openapi.json", "WS /ws (WLED)",
            "GET /json (WLED)", "POST /json/state (WLED)", "/api/app/* (app window)"),
    };

    // MARK: - Native API

    public JsonObject NativeState()
    {
        var s = Settings.Shared;
        var engine = LEDEngine.Shared;
        var eq = s.Equalizer;
        return new JsonObject
        {
            ["on"] = s.IsOn,
            ["mode"] = s.Mode.ToString(),
            ["effect"] = s.Effect.ToString(),
            ["speed"] = s.EffectSpeed,
            ["equalizer"] = new JsonObject
            {
                ["layout"] = eq.Layout.ToString(), ["style"] = eq.Style.ToString(),
                ["colors"] = eq.Colors.ToString(), ["sensitivity"] = eq.Sensitivity,
            },
            ["color"] = s.ColorHex,
            ["brightness"] = (int)Math.Round(s.Brightness * 100, MidpointRounding.AwayFromZero),
            ["overlay"] = engine.OverlayActive,
            ["live"] = engine.LiveSource is not null,
            ["liveSource"] = engine.LiveSource,
            ["leds"] = s.Layout.Count,
            ["fps"] = Math.Round(engine.Fps * 10, MidpointRounding.AwayFromZero) / 10,
            ["power"] = PowerJson(),
            ["device"] = DeviceInfo(),
            ["capture"] = CaptureStatusText(engine.GrabberStatus),
            ["captureSource"] = engine.CaptureSource,
            ["audio"] = AudioJson(),
        };
    }

    static JsonObject PowerJson()
    {
        var s = Settings.Shared;
        var engine = LEDEngine.Shared;
        return new JsonObject
        {
            ["milliamps"] = (int)Math.Round(engine.Milliamps, MidpointRounding.AwayFromZero),
            ["limited"] = engine.PowerLimited,
            ["limit"] = s.PowerLimitEnabled ? s.PowerLimitMilliamps : 0,
        };
    }

    static JsonNode? AudioJson()
    {
        AudioLevels? a;
        try { a = LEDEngine.Shared.AudioLevels(); }
        catch { a = null; }
        if (a is null) return null;
        static double R(float v) => Math.Round(v * 100.0, MidpointRounding.AwayFromZero) / 100;
        return new JsonObject
        {
            ["level"] = R(a.Level),
            ["bass"] = R(a.Bass),
            ["bands"] = new JsonArray(a.Bands.Select(b => (JsonNode)R(b)).ToArray()),
            ["lastBeat"] = a.LastBeat == DateTime.MinValue ? null : Iso8601(a.LastBeat),
        };
    }

    static string Iso8601(DateTime date) =>
        (date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Returns an error message, or null when applied.</summary>
    static string? ApplyNative(JsonObject body)
    {
        var s = Settings.Shared;
        LightMode? mode = null;
        if (Str(body["mode"]) is { } modeText)
        {
            if (ParseEnum<LightMode>(modeText) is not { } m) return $"unknown mode {modeText}";
            mode = m;
        }
        EffectKind? effect = null;
        if (Str(body["effect"]) is { } effectText)
        {
            if (ParseEnum<EffectKind>(effectText) is not { } e) return $"unknown effect {effectText}";
            effect = e;
            mode ??= LightMode.effect;
        }
        RGB? color = null;
        if (body.TryGetPropertyValue("color", out var colorNode))
        {
            if (ParseColor(colorNode) is not { } c) return "color must be \"#RRGGBB\" or [r,g,b]";
            color = c;
            if (mode is null && effect is null) mode = LightMode.color;
        }
        EqualizerOptions? equalizer = null;
        if (body["equalizer"] is JsonObject eqBody)
        {
            var options = s.Equalizer;
            if (Str(eqBody["preset"]) is { } presetId)
            {
                var match = EqualizerOptions.Presets.FirstOrDefault(p => p.Id == presetId);
                if (match is null) return $"unknown preset {presetId}";
                options = options with { Layout = match.Options.Layout, Style = match.Options.Style, Colors = match.Options.Colors };
            }
            if (Str(eqBody["layout"]) is { } layoutText)
            {
                if (ParseEnum<EqualizerLayout>(layoutText) is not { } v) return $"unknown layout {layoutText}";
                options = options with { Layout = v };
            }
            if (Str(eqBody["style"]) is { } styleText)
            {
                if (ParseEnum<EqualizerStyle>(styleText) is not { } v) return $"unknown style {styleText}";
                options = options with { Style = v };
            }
            if (Str(eqBody["colors"]) is { } colorsText)
            {
                if (ParseEnum<EqualizerColors>(colorsText) is not { } v) return $"unknown colors {colorsText}";
                options = options with { Colors = v };
            }
            if (Number(eqBody["sensitivity"]) is { } sensitivity) options = options with { Sensitivity = Math.Clamp(sensitivity, 0.5, 2.5) };
            equalizer = options;
            // Setting the equalizer shows it.
            if (effect is null)
            {
                effect = EffectKind.musicSpectrum;
                mode ??= LightMode.effect;
            }
        }
        // `duration` (seconds): show it for a while, then go back to what was on.
        if (Number(body["duration"]) is { } duration && duration > 0)
        {
            LEDEngine.Shared.ShowOverlay(mode ?? LightMode.color, effect, color, Math.Min(duration, 3600));
            return null;
        }
        if (Bool(body["clear"]) == true) LEDEngine.Shared.ClearOverlay();
        if (Bool(body["live"]) is { } live)
        {
            if (live) return "real-time mode starts by itself when DDP frames arrive";
            LEDEngine.Shared.EndLive();
        }
        if (Bool(body["on"]) is { } on) s.IsOn = on;
        if (Bool(body["toggle"]) == true) s.IsOn = !s.IsOn;
        if (mode is { } newMode) s.Mode = newMode;
        if (effect is { } newEffect) s.Effect = newEffect;
        if (equalizer is not null) s.Equalizer = equalizer;
        if (color is { } newColor) s.Color = newColor;
        if (Number(body["speed"]) is { } speed) s.EffectSpeed = Math.Clamp(speed, 0.1, 3);
        if (Number(body["brightness"]) is { } brightness) s.Brightness = Math.Clamp(brightness / 100, 0, 1);
        if ((mode is not null || color is not null || effect is not null) && !body.ContainsKey("on")) s.IsOn = true;
        return null;
    }

    static JsonObject DeviceInfo()
    {
        var s = Settings.Shared;
        var info = new JsonObject { ["port"] = s.PortPath, ["pin"] = s.DataPin };
        var status = LEDEngine.Shared.DeviceStatus;
        switch (status.Kind)
        {
            case DeviceStatusKind.disconnected:
                info["status"] = "disconnected";
                break;
            case DeviceStatusKind.connecting:
                info["status"] = "connecting";
                break;
            case DeviceStatusKind.connected:
                info["status"] = "connected";
                info["firmware"] = status.Firmware;
                break;
            case DeviceStatusKind.failed:
                info["status"] = "error";
                info["error"] = status.Error ?? "";
                break;
        }
        return info;
    }

    static JsonObject LayoutJson()
    {
        var l = Settings.Shared.Layout;
        return new JsonObject
        {
            ["top"] = l.Top, ["right"] = l.Right, ["bottom"] = l.Bottom, ["left"] = l.Left,
            ["start"] = l.Start.ToString(), ["clockwise"] = l.Clockwise, ["offset"] = l.Offset,
            ["bottomGap"] = l.BottomGap, ["count"] = l.Count,
        };
    }

    static string CaptureStatusText(CaptureStatus status) => status.Kind switch
    {
        CaptureStatusKind.stopped => "stopped",
        CaptureStatusKind.starting => "starting",
        CaptureStatusKind.running => "running",
        CaptureStatusKind.noPermission => "no-permission",
        _ => $"error: {status.Message}",
    };


    // MARK: - WLED compatibility

    static readonly EffectKind[] AllEffects = Enum.GetValues<EffectKind>();

    /// <summary>WLED effect ids: 0 = Solid (color mode), then ours in enum order, then "Ambilight" (screen mode).</summary>
    static JsonArray WledEffects()
    {
        var names = new List<JsonNode?> { "Solid" };
        names.AddRange(AllEffects.Select(e => (JsonNode)L.TrEnglish(e.TitleKey())));
        names.Add("Ambilight");
        return new JsonArray(names.ToArray());
    }

    /// <summary>WLED effect number of <paramref name="kind"/> (its enum position + 1; 0 is Solid).</summary>
    public static int WledNumber(EffectKind kind) => Array.IndexOf(AllEffects, kind) + 1;

    static int WledFx
    {
        get
        {
            var s = Settings.Shared;
            return s.Mode switch
            {
                LightMode.color => 0,
                LightMode.effect => WledNumber(s.Effect),
                _ => AllEffects.Length + 1,
            };
        }
    }

    static void SetWledFx(int fx)
    {
        var s = Settings.Shared;
        if (fx == 0)
        {
            s.Mode = LightMode.color;
        }
        else if (fx >= 1 && fx <= AllEffects.Length)
        {
            s.Effect = AllEffects[fx - 1];
            s.Mode = LightMode.effect;
        }
        else if (fx == AllEffects.Length + 1)
        {
            s.Mode = LightMode.screen;
        }
    }

    static JsonObject WledState()
    {
        var s = Settings.Shared;
        var c = s.Color;
        var count = s.Layout.Count;
        var speed = (int)Math.Round((s.EffectSpeed - 0.1) / 2.9 * 255, MidpointRounding.AwayFromZero);
        return new JsonObject
        {
            ["on"] = s.IsOn,
            ["bri"] = (int)Math.Round(s.Brightness * 255, MidpointRounding.AwayFromZero),
            ["transition"] = 7,
            ["ps"] = -1, ["pl"] = -1,
            ["nl"] = new JsonObject { ["on"] = false, ["dur"] = 60, ["mode"] = 1, ["tbri"] = 0, ["rem"] = -1 },
            ["udpn"] = new JsonObject { ["send"] = false, ["recv"] = false },
            ["lor"] = 0,
            ["live"] = LEDEngine.Shared.LiveSource is not null,
            ["mainseg"] = 0,
            ["seg"] = new JsonArray(new JsonObject
            {
                ["id"] = 0, ["start"] = 0, ["stop"] = count, ["len"] = count, ["grp"] = 1, ["spc"] = 0, ["of"] = 0,
                ["on"] = s.IsOn, ["frz"] = false, ["bri"] = 255, ["cct"] = 127,
                ["col"] = new JsonArray(new JsonArray(c.R8, c.G8, c.B8), new JsonArray(0, 0, 0), new JsonArray(0, 0, 0)),
                ["fx"] = WledFx, ["sx"] = speed, ["ix"] = 128, ["pal"] = 0, ["c1"] = 128, ["c2"] = 128, ["c3"] = 16,
                ["sel"] = true, ["rev"] = false, ["mi"] = false, ["o1"] = false, ["o2"] = false, ["o3"] = false, ["si"] = 0, ["m12"] = 0,
            }),
        };
    }

    JsonObject WledInfo()
    {
        var s = Settings.Shared;
        var engine = LEDEngine.Shared;
        var live = engine.LiveSource;
        return new JsonObject
        {
            ["ver"] = "0.14.4",
            ["vid"] = 2405180,
            ["leds"] = new JsonObject
            {
                ["count"] = s.Layout.Count, ["pwr"] = (int)Math.Round(engine.Milliamps, MidpointRounding.AwayFromZero), ["fps"] = (int)engine.Fps,
                ["maxpwr"] = s.PowerLimitEnabled ? s.PowerLimitMilliamps : 0, ["maxseg"] = 1,
                ["rgbw"] = false, ["wv"] = 0, ["cct"] = 0, ["seglc"] = new JsonArray(1), ["lc"] = 1,
            },
            ["str"] = false,
            ["name"] = "Ledly",
            ["udpport"] = 21324,
            ["live"] = live is not null,
            ["liveseg"] = -1,
            ["lm"] = live is not null ? "DDP" : "",
            ["lip"] = live ?? "",
            ["ws"] = Clients,
            ["fxcount"] = AllEffects.Length + 2,
            ["palcount"] = 1,
            ["cpalcount"] = 0,
            ["maps"] = new JsonArray(new JsonObject { ["id"] = 0 }),
            ["wifi"] = new JsonObject { ["bssid"] = "", ["rssi"] = 0, ["signal"] = 100, ["channel"] = 0 },
            ["fs"] = new JsonObject { ["u"] = 0, ["t"] = 0, ["pmt"] = 0 },
            ["ndc"] = 0,
            ["arch"] = _platform.Name,
            ["core"] = $"Ledly {Updater.CurrentVersion}",
            ["lwip"] = 0,
            ["freeheap"] = 100_000,
            ["uptime"] = (int)(DateTime.UtcNow - _startDate).TotalSeconds,
            ["time"] = Iso8601(DateTime.UtcNow),
            ["opt"] = 0,
            ["brand"] = "WLED",
            ["product"] = "Ledly",
            ["mac"] = s.DeviceId,
            ["ip"] = "",
        };
    }

    static void ApplyWled(JsonObject body)
    {
        var s = Settings.Shared;
        // WLED leaves real-time mode with {"live": false}.
        if (Bool(body["live"]) == false) LEDEngine.Shared.EndLive();
        if (Bool(body["on"]) is { } on) s.IsOn = on;
        if (Str(body["on"]) == "t") s.IsOn = !s.IsOn;
        if (Number(body["bri"]) is { } bri)
        {
            s.Brightness = Math.Clamp(bri / 255, 0, 1);
            if (bri > 0 && !body.ContainsKey("on")) s.IsOn = true;
        }
        var segments = body["seg"] switch
        {
            JsonArray list => list.OfType<JsonObject>().ToList(),
            JsonObject one => [one],
            _ => new List<JsonObject>(),
        };
        foreach (var seg in segments)
        {
            if (Bool(seg["on"]) is { } segOn) s.IsOn = segOn;
            if (seg["col"] is JsonArray cols && cols.Count > 0 && ParseColor(cols[0]) is { } color)
            {
                s.Color = color;
                if (!seg.ContainsKey("fx") && s.Mode == LightMode.screen) s.Mode = LightMode.color;
            }
            if (Number(seg["fx"]) is { } fx) SetWledFx((int)fx);
            if (Number(seg["sx"]) is { } sx) s.EffectSpeed = 0.1 + sx / 255 * 2.9;
        }
    }

    /// <summary>Legacy HTTP API: /win&amp;T=1&amp;A=128&amp;R=255&amp;G=0&amp;B=0&amp;FX=3</summary>
    static void ApplyWledQuery(Dictionary<string, string> q)
    {
        var s = Settings.Shared;
        if (q.TryGetValue("T", out var t)) s.IsOn = t == "2" ? !s.IsOn : t == "1";
        if (q.TryGetValue("A", out var a) && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var av))
            s.Brightness = Math.Clamp(av / 255, 0, 1);
        if (q.ContainsKey("R") || q.ContainsKey("G") || q.ContainsKey("B"))
        {
            var c = s.Color;
            int Channel(string key, int current) =>
                q.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : current;
            s.Color = RGB.From8(Math.Clamp(Channel("R", c.R8), 0, 255), Math.Clamp(Channel("G", c.G8), 0, 255), Math.Clamp(Channel("B", c.B8), 0, 255));
        }
        if (q.TryGetValue("FX", out var fx) && int.TryParse(fx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fxv)) SetWledFx(fxv);
        if (q.TryGetValue("SX", out var sx) && double.TryParse(sx, NumberStyles.Float, CultureInfo.InvariantCulture, out var sxv))
            s.EffectSpeed = 0.1 + sxv / 255 * 2.9;
    }

    static string WledXml()
    {
        var s = Settings.Shared;
        var c = s.Color;
        var ac = s.IsOn ? (int)(s.Brightness * 255) : 0;
        var sx = (int)((s.EffectSpeed - 0.1) / 2.9 * 255);
        return $"<?xml version=\"1.0\" ?><vs><ac>{ac}</ac><cl>{c.R8}</cl><cl>{c.G8}</cl><cl>{c.B8}</cl><fx>{WledFx}</fx><sx>{sx}</sx><ds>Ledly</ds></vs>";
    }

    // MARK: - WebSocket

    static WebSocketClient? Upgrade(HttpRequest req, TcpClient tcp, Stream stream, out HttpResponse? refusal)
    {
        refusal = null;
        if (req.Path is not ("/ws" or "/api/ws") || req.Header("sec-websocket-key") is not { } key)
        {
            refusal = HttpResponse.Error("not found", 404);
            return null;
        }
        // `/ws` is WLED's socket and stays open like the rest of the WLED API; `/api/ws` needs the token.
        if (req.Path == "/ws" && !Settings.Shared.ApiEnabled)
        {
            refusal = HttpResponse.Error("API disabled", 403);
            return null;
        }
        if (req.Path == "/api/ws" && !Authorized(req))
        {
            refusal = HttpResponse.Error("unauthorized", 401);
            return null;
        }
        var response = new HttpResponse(101);
        response.Headers["Upgrade"] = "websocket";
        response.Headers["Connection"] = "Upgrade";
        response.Headers["Sec-WebSocket-Accept"] = WebSocketClient.AcceptKey(key);
        stream.Write(response.Serialize(false));
        return new WebSocketClient(tcp, stream, req.Path, req.IsLoopback);
    }

    async Task RunWebSocket(WebSocketClient client, byte[] leftover, CancellationToken ct)
    {
        client.OnText = Received;
        client.OnClose = c =>
        {
            lock (_gate) _sockets.Remove(c);
            UpdateListener(c, closing: true);
            ScheduleState();
        };
        lock (_gate) _sockets.Add(client);
        try
        {
            client.SendText(HttpResponse.JsonString(StateMessage()));
        }
        catch (Exception e)
        {
            Log.Write($"ws hello: {e.Message}");
        }
        ScheduleState();
        await client.RunAsync(leftover, ct);
    }

    /// <summary>Messages: {"subscribe":"leds"|"colors"|"app"} ("app" also gets app-status and app-settings pushes), {"unsubscribe":…}, or any /api/state or WLED /json/state body.</summary>
    void Received(WebSocketClient client, string text)
    {
        JsonObject? body;
        try { body = JsonNode.Parse(text) as JsonObject; }
        catch { return; }
        if (body is null) return;
        try
        {
            if (Str(body["subscribe"]) is { } sub)
            {
                SetSubscription(client, sub, true);
                return;
            }
            if (Str(body["unsubscribe"]) is { } unsub)
            {
                SetSubscription(client, unsub, false);
                return;
            }
            if (!Settings.Shared.ApiEnabled)
            {
                client.SendText(HttpResponse.JsonString(new JsonObject { ["error"] = "API disabled" }));
                return;
            }
            if (body.ContainsKey("seg") || body.ContainsKey("bri") || body.ContainsKey("v") || body.ContainsKey("lor"))
                ApplyWled(body);
            else if (ApplyNative(body) is { } error)
                client.SendText(HttpResponse.JsonString(new JsonObject { ["error"] = error }));
        }
        catch (Exception e)
        {
            Log.Write($"ws message: {e.Message}");
            client.SendText(HttpResponse.JsonString(new JsonObject { ["error"] = e.Message }));
        }
    }

    void SetSubscription(WebSocketClient client, string what, bool on)
    {
        switch (what)
        {
            case "leds": client.WantsLEDs = on; break;
            case "colors": client.WantsColors = on; break;
            case "app":
                client.WantsApp = on;
                if (on)
                {
                    client.SendText(HttpResponse.JsonString(AppStatusMessage()));
                    client.SendText(HttpResponse.JsonString(AppSettingsMessage()));
                }
                break;
        }
        UpdateListener(client, closing: false);
    }

    /// <summary>Each client that wants colors counts once in <see cref="LEDEngine.ColorListeners"/>.</summary>
    static void UpdateListener(WebSocketClient client, bool closing)
    {
        lock (client)
        {
            var wants = !closing && (client.WantsLEDs || client.WantsColors);
            if (wants == client.CountedAsListener) return;
            client.CountedAsListener = wants;
            try
            {
                if (wants) LEDEngine.Shared.AddColorListener();
                else LEDEngine.Shared.RemoveColorListener();
            }
            catch (Exception e)
            {
                Log.Write($"ws listeners: {e.Message}");
            }
        }
    }

    JsonObject StateMessage() => new()
    {
        ["state"] = NativeState(),
        ["wled"] = new JsonObject { ["state"] = WledState(), ["info"] = WledInfo() },
    };

    JsonObject AppStatusMessage()
    {
        var message = new JsonObject { ["type"] = "app-status" };
        var status = AppStatus();
        foreach (var key in status.Select(kv => kv.Key).ToList())
        {
            var value = status[key];
            status.Remove(key);
            message[key] = value;
        }
        return message;
    }

    /// <summary>{"type":"app-settings","settings":{…}} for the web UI (same object as GET /api/app/settings).</summary>
    JsonObject AppSettingsMessage() => new() { ["type"] = "app-settings", ["settings"] = AppSettings() };

    void BroadcastState(bool settingsChanged = false)
    {
        List<WebSocketClient> sockets;
        lock (_gate) sockets = [.. _sockets];
        if (sockets.Count == 0) return;
        var text = HttpResponse.JsonString(StateMessage());
        string? app = null, appSettings = null;
        foreach (var client in sockets)
        {
            client.SendText(text);
            if (!client.WantsApp) continue;
            client.SendText(app ??= HttpResponse.JsonString(AppStatusMessage()));
            if (settingsChanged) client.SendText(appSettings ??= HttpResponse.JsonString(AppSettingsMessage()));
        }
    }

    /// <summary>Live LED colors for subscribed WebSocket clients, ~15 fps.</summary>
    void BroadcastColors(RGB[] colors)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastColorsTicks);
        if (now - last < 60 || Interlocked.CompareExchange(ref _lastColorsTicks, now, last) != last) return;
        List<WebSocketClient> sockets;
        lock (_gate) sockets = _sockets.Where(c => c.WantsLEDs || c.WantsColors).ToList();
        if (sockets.Count == 0) return;
        string? leds = null, appColors = null;
        foreach (var client in sockets)
        {
            if (client.WantsLEDs) client.SendText(leds ??= HttpResponse.JsonString(new JsonObject { ["leds"] = HexArray(colors) }));
            if (client.WantsColors)
                client.SendText(appColors ??= HttpResponse.JsonString(new JsonObject { ["type"] = "colors", ["colors"] = HexArray(colors) }));
        }
    }

    // MARK: - Helpers

    static JsonArray HexArray(RGB[] colors) => new(colors.Select(c => (JsonNode)c.Hex).ToArray());

    public static RGB? ParseColor(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue when Str(value) is { } text:
                return RGB.FromHex(text);
            case JsonArray list when list.Count >= 3 && Number(list[0]) is { } r && Number(list[1]) is { } g && Number(list[2]) is { } b:
                return From8(r, g, b);
            case JsonObject dict when Number(dict["r"]) is { } r && Number(dict["g"]) is { } g && Number(dict["b"]) is { } b:
                return From8(r, g, b);
            default:
                return null;
        }

        static RGB From8(double r, double g, double b) =>
            RGB.From8(Math.Clamp((int)r, 0, 255), Math.Clamp((int)g, 0, 255), Math.Clamp((int)b, 0, 255));
    }

    /// <summary>A JSON number (or numeric string). JSON booleans are not numbers: <c>true</c> ≠ 1.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<float>(out var f)) return f;
                if (v.TryGetValue<decimal>(out var m)) return (double)m;
                return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
            case JsonValueKind.String:
                return double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
            default:
                return null;
        }
    }

    /// <summary>A JSON boolean only (0 and 1 are not booleans).</summary>
    public static bool? Bool(JsonNode? node) => node is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Exact raw name of an enum member (no numbers, case-sensitive).</summary>
    public static T? ParseEnum<T>(string? text) where T : struct, Enum =>
        text is not null && Enum.GetNames<T>().Contains(text) ? Enum.Parse<T>(text) : null;
}
