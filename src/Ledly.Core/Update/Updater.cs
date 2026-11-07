using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ledly;

public enum UpdateStateKind { idle, checking, downloading, installing }

/// <summary>
/// Self-update from GitHub releases of <see cref="Repo"/>: each release carries <c>Ledly-win-x64.zip</c>
/// and <c>Ledly-win-x64.zip.sha256</c>. Downloads, checks the hash, then hands the package to
/// <see cref="IPlatform.InstallUpdate"/>.
/// </summary>
public sealed class Updater
{
    public const string Repo = "drarekx/ledly-windows";
    /// <summary>Package for this machine's architecture.</summary>
    public static readonly string AssetName =
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "Ledly-win-arm64.zip" : "Ledly-win-x64.zip";
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    static Updater? _shared;
    public static Updater Shared => _shared ?? throw new InvalidOperationException("Updater.Start first");

    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public UpdateStateKind State { get; private set; }
    /// <summary>Newer version found (not skipped), or null.</summary>
    public string? Available { get; private set; }
    public string? Notes { get; private set; }
    /// <summary>Last error of a user-initiated check or an install.</summary>
    public string? Error { get; private set; }
    /// <summary>True after a successful check found nothing newer (the UI can say "up to date").</summary>
    public bool UpToDate { get; private set; }
    public event Action? Changed;

    readonly IPlatform _platform;
    readonly object _gate = new();
    readonly HttpClient _http;
    Timer? _timer;
    Uri? _zipUrl, _hashUrl;

    Updater(IPlatform platform)
    {
        _platform = platform;
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ledly-Windows", CurrentVersion));
    }

    /// <summary>Background checks: shortly after launch, then once a day (if <see cref="Settings.AutoCheckUpdates"/>).</summary>
    public static Updater Start(IPlatform platform)
    {
        if (_shared is { } existing) return existing;
        var updater = new Updater(platform);
        _shared = updater;
        // 10 s after launch, then every hour (checks only when a day has passed).
        updater._timer = new Timer(_ => updater.CheckIfDue(), null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        return updater;
    }

    void CheckIfDue()
    {
        Settings settings;
        try { settings = Settings.Shared; } catch (InvalidOperationException) { return; }
        if (!settings.AutoCheckUpdates) return;
        var last = settings.LastUpdateCheck ?? DateTime.MinValue;
        if (DateTime.UtcNow - last.ToUniversalTime() < CheckInterval) return;
        _ = CheckAsync(userInitiated: false);
    }

    /// <summary><paramref name="userInitiated"/>: also report errors, and ignore skipped versions.</summary>
    public async Task CheckAsync(bool userInitiated)
    {
        lock (_gate)
        {
            if (State != UpdateStateKind.idle) return;
            State = UpdateStateKind.checking;
            if (userInitiated) Error = null;
        }
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _http.SendAsync(request, cancel.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException(L.Tr("GitHub no respondió correctamente. Inténtalo más tarde."));
            var json = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
            var release = Release.Parse(json) ?? throw new UpdateException(L.Tr("GitHub no respondió correctamente. Inténtalo más tarde."));
            try { Settings.Shared.LastUpdateCheck = DateTime.UtcNow; } catch (InvalidOperationException) { }

            lock (_gate)
            {
                if (release.Draft || release.Prerelease || Compare(release.Version, CurrentVersion) <= 0)
                {
                    Available = null;
                    Notes = null;
                    UpToDate = true;
                }
                else if (!userInitiated && SkippedVersion() == release.Version)
                {
                    // Skipped by the user: only a manual check offers it again.
                }
                else
                {
                    var notes = release.Body.Trim();
                    if (notes.Length > 900) notes = notes[..900] + "…";
                    Available = release.Version;
                    Notes = notes.Length == 0 ? null : notes;
                    UpToDate = false;
                    _zipUrl = release.Asset(AssetName);
                    _hashUrl = release.Asset(AssetName + ".sha256");
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"Update check failed: {e.Message}");
            if (userInitiated) lock (_gate) Error = $"{L.Tr("No se pudo buscar actualizaciones")}: {Describe(e)}";
        }
        finally
        {
            lock (_gate) if (State == UpdateStateKind.checking) State = UpdateStateKind.idle;
            Changed?.Invoke();
        }
    }

    static string? SkippedVersion()
    {
        try { return Settings.Shared.SkippedVersion; } catch (InvalidOperationException) { return null; }
    }

    /// <summary>Downloads <see cref="Available"/>, checks its SHA-256 and hands it to the platform, which restarts the app.</summary>
    public async Task InstallAsync()
    {
        string version;
        Uri? zipUrl, hashUrl;
        lock (_gate)
        {
            if (State != UpdateStateKind.idle || Available is null) return;
            version = Available;
            zipUrl = _zipUrl;
            hashUrl = _hashUrl;
            Error = null;
            if (zipUrl is null || hashUrl is null)
            {
                Error = $"{L.Tr("No se puede actualizar")}: {L.Tr("La versión {0} no incluye los archivos de actualización.", version)}";
            }
            else
            {
                State = UpdateStateKind.downloading;
            }
        }
        Changed?.Invoke();
        if (zipUrl is null || hashUrl is null) return;

        var work = Path.Combine(Path.GetTempPath(), $"Ledly-update-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, AssetName);
            using (var response = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException(L.Tr("GitHub no respondió correctamente. Inténtalo más tarde."));
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file).ConfigureAwait(false);
            }

            var hashText = await _http.GetStringAsync(hashUrl).ConfigureAwait(false);
            var expected = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
            string actual;
            await using (var file = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)).ToLowerInvariant();
            if (expected.Length == 0 || expected != actual)
                throw new UpdateException(L.Tr("El archivo descargado no coincide con su huella SHA-256. No se ha instalado nada."));

            lock (_gate) State = UpdateStateKind.installing;
            Changed?.Invoke();
            // On success the platform schedules the swap and quits the app.
            if (_platform.InstallUpdate(zip, version) is { } error) throw new UpdateException(error);
        }
        catch (Exception e)
        {
            Log.Write($"Update install failed: {e.Message}");
            try { Directory.Delete(work, recursive: true); } catch { }
            lock (_gate)
            {
                State = UpdateStateKind.idle;
                Error = $"{L.Tr("La actualización falló")}: {Describe(e)}";
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Hides <see cref="Available"/> and stops offering it in background checks.</summary>
    public void Skip()
    {
        lock (_gate)
        {
            if (Available is not { } version) return;
            try { Settings.Shared.SkippedVersion = version; } catch (InvalidOperationException) { }
            Available = null;
            Notes = null;
        }
        Changed?.Invoke();
    }

    static string Describe(Exception e) => e switch
    {
        UpdateException => e.Message,
        TaskCanceledException or HttpRequestException => L.Tr("GitHub no respondió correctamente. Inténtalo más tarde."),
        _ => e.Message,
    };

    /// <summary>Numeric dotted comparison: −1, 0, 1 ("1.10.0" &gt; "1.9.2").</summary>
    public static int Compare(string a, string b)
    {
        var lhs = Parts(a);
        var rhs = Parts(b);
        for (var i = 0; i < Math.Max(lhs.Length, rhs.Length); i++)
        {
            var l = i < lhs.Length ? lhs[i] : 0;
            var r = i < rhs.Length ? rhs[i] : 0;
            if (l != r) return l < r ? -1 : 1;
        }
        return 0;

        // Leading digits of each component ("2-beta" → 2, "x" → 0).
        static int[] Parts(string version) => version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsAsciiDigit).ToArray()), out var n) ? n : 0)
            .ToArray();
    }

    sealed class UpdateException(string message) : Exception(message);

    sealed record Release(string Version, string Body, bool Draft, bool Prerelease, Dictionary<string, Uri> Assets)
    {
        public Uri? Asset(string name) => Assets.GetValueOrDefault(name);

        public static Release? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName) return null;
                var version = tagName.StartsWith('v') ? tagName[1..] : tagName;
                var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                var draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                var prerelease = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
                var assets = new Dictionary<string, Uri>();
                if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in list.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var n) && n.GetString() is { } name
                            && asset.TryGetProperty("browser_download_url", out var u)
                            && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url))
                            assets[name] = url;
                    }
                }
                return new Release(version, body, draft, prerelease, assets);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
