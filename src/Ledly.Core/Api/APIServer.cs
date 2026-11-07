namespace Ledly;

// CONTRACT STUB — replaced by the API port. Keep these public signatures.

public enum ServerStatusKind { stopped, running, failed }

/// <summary>HTTP/1.1 + WebSocket server: native API, WLED subset, app API for the web UI, static web UI; DDP; mDNS.</summary>
public sealed class APIServer
{
    static APIServer? _shared;
    public static APIServer Shared => _shared ?? throw new InvalidOperationException("APIServer.Start first");
    public ServerStatusKind Status { get; private set; }
    public int Port { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Starts listening per Settings and follows their changes.</summary>
    public static APIServer Start(IPlatform platform) => _shared = new APIServer();
    /// <summary>Base URL of the web UI on loopback (always available while the app runs).</summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}/";
}
