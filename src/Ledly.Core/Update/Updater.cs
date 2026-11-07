using System.Reflection;

namespace Ledly;

// CONTRACT STUB — replaced by the updater port. Keep these public signatures.

public enum UpdateStateKind { idle, checking, downloading, installing }

/// <summary>
/// Self-update from GitHub releases of <see cref="Repo"/>: each release carries <c>Ledly-win-x64.zip</c>
/// and <c>Ledly-win-x64.zip.sha256</c>. Downloads, checks the hash, then hands the package to
/// <see cref="IPlatform.InstallUpdate"/>.
/// </summary>
public sealed class Updater
{
    public const string Repo = "drarekx/ledly-windows";
    public const string AssetName = "Ledly-win-x64.zip";
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
    public event Action? Changed;

    /// <summary>Background checks: shortly after launch, then once a day (if <see cref="Settings.AutoCheckUpdates"/>).</summary>
    public static Updater Start(IPlatform platform) => throw new NotImplementedException();
    public Task CheckAsync(bool userInitiated) => throw new NotImplementedException();
    public Task InstallAsync() => throw new NotImplementedException();
    public void Skip() => throw new NotImplementedException();
    /// <summary>Numeric dotted comparison: −1, 0, 1.</summary>
    public static int Compare(string a, string b) => throw new NotImplementedException();
}
