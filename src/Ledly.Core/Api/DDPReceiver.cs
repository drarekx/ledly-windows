using System.Net;
using System.Net.Sockets;

namespace Ledly;

/// <summary>
/// Receives LED frames over DDP (Distributed Display Protocol, UDP, port 4048 by default),
/// the way WLED does. No authentication, like WLED.
///
/// Header (10 bytes, 14 with a timecode), multi-byte fields big-endian:
///   0  flags     0x40 = version 1, 0x01 = push (show the frame), 0x10 = timecode present
///   1  sequence  low 4 bits
///   2  type      0x0B = RGB 8 bit (0x01 and 0x00 are accepted as RGB too)
///   3  dest. ID  1 = default output (255 = all)
///   4  offset    32 bits, in bytes from the first LED
///   8  length    16 bits, data bytes in this packet
/// A frame may span several packets; it is shown when the one with "push" arrives.
/// </summary>
public sealed class DDPReceiver
{
    /// <summary>Called on the receive thread with the frame's bytes (R, G, B per LED from LED 0) and the sender.</summary>
    public Action<byte[], string>? OnFrame { get; set; }
    /// <summary>Status, error message (when failed), port.</summary>
    public Action<ServerStatusKind, string?, int>? OnStatus { get; set; }

    const int MaxBytes = 65_535 * 3;
    readonly object _gate = new();
    Socket? _socket;
    CancellationTokenSource? _cts;
    byte[] _buffer = [];
    /// <summary>Highest byte written since the last push.</summary>
    int _written;

    /// <summary><paramref name="localOnly"/> binds to the loopback interface, like the HTTP API.</summary>
    public void Start(int port, bool localOnly)
    {
        Stop();
        lock (_gate)
        {
            Socket socket;
            try
            {
                if (localOnly)
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    Prepare(socket);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                else
                {
                    try
                    {
                        socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                        Prepare(socket);
                        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                    }
                    catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
                    {
                        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        Prepare(socket);
                        socket.Bind(new IPEndPoint(IPAddress.Any, port));
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write($"ddp: {e.Message}");
                OnStatus?.Invoke(ServerStatusKind.failed, e.Message, port);
                return;
            }
            _socket = socket;
            _cts = new CancellationTokenSource();
            _buffer = [];
            _written = 0;
            var ct = _cts.Token;
            // Async receive: disposing a socket blocked in a synchronous ReceiveFrom can hang on macOS.
            _ = Task.Run(() => ReceiveLoop(socket, ct));
            OnStatus?.Invoke(ServerStatusKind.running, null, port);
        }
    }

    static void Prepare(Socket socket)
    {
        // On Windows SO_REUSEADDR would let another app steal the port; elsewhere it allows a quick rebind.
        if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
        else socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public void Stop()
    {
        lock (_gate)
        {
            var had = _socket is not null;
            _cts?.Cancel();
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            _cts = null;
            if (had) OnStatus?.Invoke(ServerStatusKind.stopped, null, 0);
        }
    }

    async Task ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var packet = new byte[65_536];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            int n;
            EndPoint from;
            try
            {
                var result = await socket.ReceiveFromAsync(packet, SocketFlags.None, any, ct);
                n = result.ReceivedBytes;
                from = result.RemoteEndPoint;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;   // Windows reports ICMP port-unreachable on UDP sockets; ignore.
            }
            catch
            {
                return;     // closed
            }
            if (n <= 0) continue;
            try
            {
                // One sender at a time touches the frame buffer.
                lock (_gate) Handle(packet.AsSpan(0, n), Host(from));
            }
            catch (Exception e)
            {
                Log.Write($"ddp: {e.Message}");
            }
        }
    }

    void Handle(ReadOnlySpan<byte> bytes, string sender)
    {
        if (bytes.Length < 10) return;
        var flags = bytes[0];
        if ((flags & 0xC0) != 0x40) return;            // version 1
        if ((flags & 0x02) != 0) return;               // queries aren't answered
        var type = bytes[2];
        if (type != 0x0B && type != 0x01 && type != 0x00) return;
        var destination = bytes[3];
        if (destination != 1 && destination != 255) return;
        var offset = (long)bytes[4] << 24 | (long)bytes[5] << 16 | (long)bytes[6] << 8 | bytes[7];
        var length = bytes[8] << 8 | bytes[9];
        var header = (flags & 0x10) != 0 ? 14 : 10;
        var payload = Math.Min(length, bytes.Length - header);
        if (payload < 0 || offset + payload > MaxBytes) return;
        var start = (int)offset;

        if (_buffer.Length < start + payload) Array.Resize(ref _buffer, start + payload);
        bytes.Slice(header, payload).CopyTo(_buffer.AsSpan(start));
        _written = Math.Max(_written, start + payload);

        if ((flags & 0x01) != 0)
        {
            OnFrame?.Invoke(_buffer.AsSpan(0, _written).ToArray(), sender);
            _written = 0;
        }
    }

    static string Host(EndPoint endpoint)
    {
        if (endpoint is not IPEndPoint ip) return endpoint.ToString() ?? "";
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        return address.ToString();
    }
}
