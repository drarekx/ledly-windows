using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Ledly;

/// <summary>One parsed HTTP/1.1 request.</summary>
public sealed class HttpRequest
{
    public required string Method { get; init; }
    /// <summary>Decoded path without query and without a trailing slash (except "/").</summary>
    public required string Path { get; init; }
    public required Dictionary<string, string> Query { get; init; }
    /// <summary>Header names in lower case.</summary>
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }
    public required string Version { get; init; }
    public required IPAddress Remote { get; init; }

    public bool IsLoopback => IPAddress.IsLoopback(Remote);

    public bool IsWebSocketUpgrade =>
        Headers.TryGetValue("upgrade", out var u) && u.Equals("websocket", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP/1.1 keeps the connection unless asked not to; HTTP/1.0 only when asked.</summary>
    public bool KeepAlive
    {
        get
        {
            var connection = Headers.GetValueOrDefault("connection", "");
            if (connection.Contains("close", StringComparison.OrdinalIgnoreCase)) return false;
            return Version == "HTTP/1.1" || connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The body as a JSON object; an empty body is <c>{}</c>. Null when it isn't a JSON object.</summary>
    public JsonObject? JsonBody
    {
        get
        {
            if (Body.Length == 0 || Body.All(b => b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')) return new JsonObject();
            try { return JsonNode.Parse(Body) as JsonObject; }
            catch { return null; }
        }
    }

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>An HTTP response; JSON by default.</summary>
public sealed class HttpResponse
{
    public int Status { get; set; }
    public string ContentType { get; set; } = "application/json";
    public byte[] Body { get; set; } = [];
    public Dictionary<string, string> Headers { get; } = new();
    /// <summary>Runs once the response has been written (e.g. quitting the app).</summary>
    public Action? AfterSend { get; set; }

    public HttpResponse(int status, string contentType = "application/json", byte[]? body = null)
    {
        Status = status;
        ContentType = contentType;
        Body = body ?? [];
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string JsonString(JsonNode? node) => node?.ToJsonString(JsonOptions) ?? "null";

    public static HttpResponse Json(JsonNode? node, int status = 200) =>
        new(status, "application/json", Encoding.UTF8.GetBytes(JsonString(node)));

    public static HttpResponse Error(string message, int status) => Json(new JsonObject { ["error"] = message }, status);

    public static HttpResponse Text(string text, string contentType, int status = 200) =>
        new(status, contentType, Encoding.UTF8.GetBytes(text));

    static readonly Dictionary<int, string> Reasons = new()
    {
        [101] = "Switching Protocols", [200] = "OK", [202] = "Accepted", [204] = "No Content", [400] = "Bad Request",
        [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found", [405] = "Method Not Allowed", [409] = "Conflict",
        [413] = "Payload Too Large", [500] = "Internal Server Error", [503] = "Service Unavailable",
    };

    public byte[] Serialize(bool keepAlive)
    {
        var all = new SortedDictionary<string, string>(Headers, StringComparer.Ordinal);
        if (Status != 101)
        {
            all["Content-Type"] = ContentType;
            all["Content-Length"] = Body.Length.ToString(CultureInfo.InvariantCulture);
            all["Connection"] = keepAlive ? "keep-alive" : "close";
            all["Access-Control-Allow-Origin"] = "*";
            all["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, OPTIONS";
            all["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        }
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(Reasons.GetValueOrDefault(Status, "OK")).Append("\r\n");
        foreach (var (k, v) in all) head.Append(k).Append(": ").Append(v).Append("\r\n");
        head.Append("\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        var result = new byte[headBytes.Length + Body.Length];
        headBytes.CopyTo(result, 0);
        Body.CopyTo(result, headBytes.Length);
        return result;
    }
}

/// <summary>Reads requests one after another from a connection (keep-alive, pipelining).</summary>
sealed class HttpReader(Stream stream, IPAddress remote)
{
    public const int MaxSize = 1_000_000;
    byte[] _buffer = new byte[8192];
    int _count;

    public sealed class TooLargeException : Exception;

    /// <summary>Bytes received after the last request (the start of a WebSocket stream).</summary>
    public ReadOnlySpan<byte> Leftover => _buffer.AsSpan(0, _count);

    /// <summary>Next request, or null when the peer closed the connection.</summary>
    public async Task<HttpRequest?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var end = _buffer.AsSpan(0, _count).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var head = Encoding.UTF8.GetString(_buffer, 0, end);
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
                }
                var length = int.TryParse(headers.GetValueOrDefault("content-length", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : 0;
                if (length > MaxSize) throw new TooLargeException();
                var bodyStart = end + 4;
                while (_count - bodyStart < length)
                {
                    if (!await FillAsync(ct)) return null;
                }
                var body = _buffer.AsSpan(bodyStart, length).ToArray();
                // Keep whatever came after this request.
                var consumed = bodyStart + length;
                Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _count - consumed);
                _count -= consumed;
                if (parts.Length < 2) return await ReadAsync(ct);
                return Parse(parts[0], parts[1], parts.Length > 2 ? parts[2] : "HTTP/1.0", headers, body);
            }
            if (_count > MaxSize) throw new TooLargeException();
            if (!await FillAsync(ct)) return null;
        }
    }

    async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_count == _buffer.Length) Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaxSize * 2 + 16384));
        if (_count == _buffer.Length) throw new TooLargeException();
        var n = await stream.ReadAsync(_buffer.AsMemory(_count), ct);
        if (n <= 0) return false;
        _count += n;
        return true;
    }

    HttpRequest Parse(string method, string target, string version, Dictionary<string, string> headers, byte[] body)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = target;
        // Absolute-form targets (proxies): keep only the path.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var slash = path.IndexOf('/', path.IndexOf("//", StringComparison.Ordinal) + 2);
            path = slash < 0 ? "/" : path[slash..];
        }
        var hash = path.IndexOf('#');
        if (hash >= 0) path = path[..hash];
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            AddPairs(query, path[(q + 1)..]);
            path = path[..q];
        }
        path = Uri.UnescapeDataString(path);
        // WLED's legacy API writes parameters as /win&T=1&A=128.
        var amp = path.IndexOf('&');
        if (amp >= 0)
        {
            AddPairs(query, path[(amp + 1)..], unescape: false);
            path = path[..amp];
        }
        if (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
        if (path.Length == 0) path = "/";
        return new HttpRequest
        {
            Method = method.ToUpperInvariant(), Path = path, Query = query, Headers = headers, Body = body,
            Version = version.ToUpperInvariant(), Remote = remote,
        };
    }

    static void AddPairs(Dictionary<string, string> query, string text, bool unescape = true)
    {
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            if (unescape)
            {
                key = Uri.UnescapeDataString(key.Replace('+', ' '));
                value = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            query[key] = value;
        }
    }
}

/// <summary>Minimal RFC 6455 server side: text frames (fragmented too), ping/pong, close.</summary>
sealed class WebSocketClient
{
    public const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    const int MaxMessage = 1_000_000;

    public static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + Guid)));

    public Action<WebSocketClient, string>? OnText;
    public Action<WebSocketClient>? OnClose;

    /// <summary>{"subscribe":"leds"} — Mac API's live colors.</summary>
    public bool WantsLEDs;
    /// <summary>{"subscribe":"colors"} — the web UI's live colors.</summary>
    public bool WantsColors;
    /// <summary>{"subscribe":"app"} — app-status pushes for the web UI.</summary>
    public bool WantsApp;
    /// <summary>Counted in <see cref="LEDEngine.ColorListeners"/>.</summary>
    public bool CountedAsListener;

    public string Path { get; }
    public bool IsLoopback { get; }

    readonly Stream _stream;
    readonly TcpClient _tcp;
    // Slow clients drop old messages instead of blocking the broadcaster.
    readonly Channel<byte[]> _out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
    });
    int _closed;

    public WebSocketClient(TcpClient tcp, Stream stream, string path, bool isLoopback)
    {
        _tcp = tcp;
        _stream = stream;
        Path = path;
        IsLoopback = isLoopback;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void SendText(string text) => Enqueue(0x1, Encoding.UTF8.GetBytes(text));

    public void Close()
    {
        if (IsClosed) return;
        Enqueue(0x8, []);
        _out.Writer.TryComplete();
    }

    void Enqueue(byte opcode, byte[] payload)
    {
        if (IsClosed) return;
        _out.Writer.TryWrite(Frame(opcode, payload));
    }

    static byte[] Frame(byte opcode, byte[] payload)
    {
        var n = payload.Length;
        var headerLength = n < 126 ? 2 : n <= 0xFFFF ? 4 : 10;
        var frame = new byte[headerLength + n];
        frame[0] = (byte)(0x80 | opcode);
        if (n < 126)
        {
            frame[1] = (byte)n;
        }
        else if (n <= 0xFFFF)
        {
            frame[1] = 126;
            frame[2] = (byte)(n >> 8);
            frame[3] = (byte)n;
        }
        else
        {
            frame[1] = 127;
            for (var i = 0; i < 8; i++) frame[2 + i] = (byte)((long)n >> (56 - 8 * i));
        }
        payload.CopyTo(frame, headerLength);
        return frame;
    }

    /// <summary>Runs until the connection closes. <paramref name="leftover"/> = bytes already read after the handshake.</summary>
    public async Task RunAsync(byte[] leftover, CancellationToken ct)
    {
        var writer = Task.Run(() => WriteLoopAsync(ct), CancellationToken.None);
        try
        {
            await ReadLoopAsync(leftover, ct);
        }
        catch
        {
            // Connection reset, cancelled, protocol error.
        }
        MarkClosed();
        _out.Writer.TryComplete();
        try { await writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(ct))
            {
                await _stream.WriteAsync(frame, ct);
                if ((frame[0] & 0x0F) == 0x8) break;
            }
        }
        catch
        {
        }
        MarkClosed();
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    void MarkClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { OnClose?.Invoke(this); } catch (Exception e) { Log.Write($"ws close: {e.Message}"); }
    }

    async Task ReadLoopAsync(byte[] leftover, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(16384, leftover.Length * 2)];
        var count = leftover.Length;
        leftover.CopyTo(buffer, 0);
        var message = new List<byte>();
        byte messageOpcode = 0;
        while (!IsClosed)
        {
            // Drain complete frames.
            while (true)
            {
                if (count < 2) break;
                var fin = (buffer[0] & 0x80) != 0;
                var opcode = (byte)(buffer[0] & 0x0F);
                var masked = (buffer[1] & 0x80) != 0;
                long length = buffer[1] & 0x7F;
                var offset = 2;
                if (length == 126)
                {
                    if (count < 4) break;
                    length = buffer[2] << 8 | buffer[3];
                    offset = 4;
                }
                else if (length == 127)
                {
                    if (count < 10) break;
                    length = 0;
                    for (var i = 2; i < 10; i++) length = length << 8 | buffer[i];
                    offset = 10;
                }
                if (length > MaxMessage) return;
                var maskOffset = offset;
                if (masked) offset += 4;
                if (count < offset + length)
                {
                    if (buffer.Length < offset + length) Array.Resize(ref buffer, (int)(offset + length) + 1024);
                    break;
                }
                var payload = buffer.AsSpan(offset, (int)length).ToArray();
                if (masked)
                {
                    for (var i = 0; i < payload.Length; i++) payload[i] ^= buffer[maskOffset + i % 4];
                }
                var consumed = offset + (int)length;
                Buffer.BlockCopy(buffer, consumed, buffer, 0, count - consumed);
                count -= consumed;

                switch (opcode)
                {
                    case 0x0:
                    case 0x1:
                    case 0x2:
                        if (opcode != 0x0)
                        {
                            message.Clear();
                            messageOpcode = opcode;
                        }
                        message.AddRange(payload);
                        if (message.Count > MaxMessage) return;
                        if (fin)
                        {
                            if (messageOpcode == 0x1) OnText?.Invoke(this, Encoding.UTF8.GetString(message.ToArray()));
                            message.Clear();
                        }
                        break;
                    case 0x8:
                        Close();
                        return;
                    case 0x9:
                        Enqueue(0xA, payload);
                        break;
                }
            }
            if (count == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var n = await _stream.ReadAsync(buffer.AsMemory(count), ct);
            if (n <= 0) return;
            count += n;
        }
    }
}
