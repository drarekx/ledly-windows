using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Ledly;

/// <summary>
/// Minimal mDNS / DNS-SD responder (RFC 6762/6763) that announces the API as a WLED device
/// (<c>_wled._tcp</c>, plus <c>_http._tcp</c>), the way the Mac app does with Bonjour, so Home Assistant's
/// WLED discovery and the WLED apps find it. Shares UDP 5353 with the system's own responder
/// (SO_REUSEADDR). Fails soft: if the port can't be used it logs and stays quiet.
/// </summary>
public sealed class Mdns
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const int MdnsPort = 5353;
    const ushort TypeA = 1, TypePTR = 12, TypeTXT = 16, TypeSRV = 33, TypeANY = 255;
    const ushort ClassIN = 1, CacheFlush = 0x8000;
    const uint HostTtl = 120, ServiceTtl = 4500;

    readonly object _gate = new();
    Socket? _socket;
    CancellationTokenSource? _cts;
    Timer? _refresh;
    string _instance = "Ledly";
    string _host = "ledly.local";
    int _port;
    string _mac = "";
    List<(IPAddress Address, IPAddress Mask, int Index)> _interfaces = [];

    public bool IsRunning { get { lock (_gate) return _socket is not null; } }

    static readonly string[] ServiceTypes = ["_wled._tcp.local", "_http._tcp.local"];

    /// <summary>Starts (or retargets) the announcement for an HTTP server on <paramref name="port"/>.</summary>
    public void Start(int port, string instance, string mac)
    {
        Stop();
        lock (_gate)
        {
            _port = port;
            _instance = instance;
            _mac = mac;
            var suffix = mac.Length >= 6 ? mac[^6..] : mac;
            _host = $"ledly-{suffix}.local";
            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            }
            catch (Exception e)
            {
                Log.Write($"mdns: can't use UDP {MdnsPort}: {e.Message}");
                return;
            }
            _socket = socket;
            JoinGroups();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => ReceiveLoop(socket, ct));   // async: a blocked synchronous receive can hang Dispose on macOS
            // Addresses change (Wi-Fi, VPN, DHCP): check every 30 s and announce again when they do.
            // (NetworkChange events are avoided: re-subscribing could hang on macOS.)
            _refresh = new Timer(_ => RefreshInterfaces(), null, 30_000, 30_000);
            // Announce twice, a second apart (RFC 6762 §8.3).
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 2 && !ct.IsCancellationRequested; i++)
                {
                    Announce(HostTtl, ServiceTtl);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }, ct);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_socket is null) return;
            _refresh?.Dispose();
            _refresh = null;
            try { Announce(0, 0); } catch { }   // goodbye
            _cts?.Cancel();
            try { _socket.Dispose(); } catch { }
            _socket = null;
            _cts = null;
        }
    }

    void RefreshInterfaces()
    {
        bool changed;
        lock (_gate)
        {
            if (_socket is null) return;
            var before = string.Join(',', _interfaces.Select(i => i.Address));
            try { JoinGroups(); } catch (Exception e) { Log.Write($"mdns: {e.Message}"); return; }
            changed = before != string.Join(',', _interfaces.Select(i => i.Address));
        }
        if (changed) Announce(HostTtl, ServiceTtl);
    }

    void JoinGroups()
    {
        var list = new List<(IPAddress, IPAddress, int)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            int index;
            try { index = props.GetIPv4Properties()?.Index ?? -1; } catch { index = -1; }
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                if (u.Address.GetAddressBytes()[0] == 169 && u.Address.GetAddressBytes()[1] == 254) continue;
                list.Add((u.Address, u.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), index));
                try
                {
                    _socket!.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, u.Address));
                }
                catch (SocketException)
                {
                    // Already a member on this interface.
                }
            }
        }
        _interfaces = list;
    }

    async Task ReceiveLoop(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            EndPoint from;
            int n;
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                n = result.ReceivedBytes;
                from = result.RemoteEndPoint;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch
            {
                return;
            }
            try
            {
                HandleQuery(buffer.AsSpan(0, n), (IPEndPoint)from);
            }
            catch (Exception e)
            {
                Log.Write($"mdns: {e.Message}");
            }
        }
    }

    // MARK: queries

    void HandleQuery(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        if (packet.Length < 12) return;
        var id = (ushort)(packet[0] << 8 | packet[1]);
        var flags = packet[2] << 8 | packet[3];
        if ((flags & 0x8000) != 0) return;          // a response, not a query
        var qdCount = packet[4] << 8 | packet[5];
        var offset = 12;
        var questions = new List<(string Name, ushort Type, ushort Class)>();
        for (var i = 0; i < qdCount; i++)
        {
            var name = ReadName(packet, ref offset);
            if (name is null || offset + 4 > packet.Length) return;
            var type = (ushort)(packet[offset] << 8 | packet[offset + 1]);
            var cls = (ushort)(packet[offset + 2] << 8 | packet[offset + 3]);
            offset += 4;
            questions.Add((name, type, cls));
        }

        var local = LocalAddressFor(from.Address);
        if (local is null) return;
        var answers = new List<byte[]>();
        var additional = new List<byte[]>();
        var instanceNames = ServiceTypes.Select(InstanceName).ToArray();
        foreach (var (name, type, _) in questions)
        {
            var anyType = type == TypeANY;
            if (Same(name, "_services._dns-sd._udp.local") && (type == TypePTR || anyType))
            {
                foreach (var s in ServiceTypes) answers.Add(Ptr("_services._dns-sd._udp.local", s, ServiceTtl));
            }
            for (var k = 0; k < ServiceTypes.Length; k++)
            {
                if (Same(name, ServiceTypes[k]) && (type == TypePTR || anyType))
                {
                    answers.Add(Ptr(ServiceTypes[k], instanceNames[k], ServiceTtl));
                    additional.Add(Srv(instanceNames[k], HostTtl));
                    additional.Add(Txt(instanceNames[k], k, ServiceTtl));
                    additional.Add(A(local, HostTtl));
                }
                if (Same(name, instanceNames[k]))
                {
                    if (type == TypeSRV || anyType) answers.Add(Srv(instanceNames[k], HostTtl));
                    if (type == TypeTXT || anyType) answers.Add(Txt(instanceNames[k], k, ServiceTtl));
                    if (type is TypeSRV or TypeANY) additional.Add(A(local, HostTtl));
                }
            }
            if (Same(name, _host) && (type == TypeA || anyType)) answers.Add(A(local, HostTtl));
        }
        if (answers.Count == 0) return;
        additional = additional.Distinct(ByteArrayComparer.Instance).Where(a => !answers.Contains(a, ByteArrayComparer.Instance)).ToList();

        // Legacy unicast queries (source port isn't 5353) get a direct reply that echoes the question.
        var legacy = from.Port != MdnsPort;
        var response = Message(legacy ? id : (ushort)0, legacy ? questions : null, answers, additional);
        lock (_gate)
        {
            if (_socket is null) return;
            try
            {
                if (legacy) _socket.SendTo(response, from);
                else SendMulticast(response, local);
            }
            catch (Exception e)
            {
                Log.Write($"mdns send: {e.Message}");
            }
        }
    }

    /// <summary>This PC's IPv4 address on the subnet of <paramref name="remote"/> (the first one otherwise).</summary>
    IPAddress? LocalAddressFor(IPAddress remote)
    {
        var interfaces = _interfaces;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            var r = remote.GetAddressBytes();
            foreach (var (address, mask, _) in interfaces)
            {
                var a = address.GetAddressBytes();
                var m = mask.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (r[i] & m[i]))) return address;
            }
        }
        return interfaces.Count > 0 ? interfaces[0].Address : null;
    }

    void SendMulticast(byte[] message, IPAddress local)
    {
        _socket!.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        _socket.SendTo(message, new IPEndPoint(Group, MdnsPort));
    }

    /// <summary>Unsolicited response with every record, on every interface (TTL 0 = goodbye).</summary>
    void Announce(uint hostTtl, uint serviceTtl)
    {
        lock (_gate)
        {
            if (_socket is null) return;
            foreach (var (address, _, _) in _interfaces)
            {
                var answers = new List<byte[]>();
                for (var k = 0; k < ServiceTypes.Length; k++)
                {
                    var instance = InstanceName(ServiceTypes[k]);
                    answers.Add(Ptr(ServiceTypes[k], instance, serviceTtl));
                    answers.Add(Srv(instance, hostTtl));
                    answers.Add(Txt(instance, k, serviceTtl));
                    answers.Add(Ptr("_services._dns-sd._udp.local", ServiceTypes[k], serviceTtl));
                }
                answers.Add(A(address, hostTtl));
                try
                {
                    SendMulticast(Message(0, null, answers, []), address);
                }
                catch (Exception e)
                {
                    Log.Write($"mdns announce on {address}: {e.Message}");
                }
            }
        }
    }

    string InstanceName(string serviceType) => $"{_instance}.{serviceType}";

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b, StringComparison.OrdinalIgnoreCase);

    // MARK: records

    byte[] Ptr(string name, string target, uint ttl) => Record(name, TypePTR, ClassIN, ttl, Name(target));

    byte[] Srv(string instance, uint ttl)
    {
        var target = Name(_host);
        var data = new byte[6 + target.Length];
        data[4] = (byte)(_port >> 8);
        data[5] = (byte)_port;
        target.CopyTo(data, 6);
        return Record(instance, TypeSRV, ClassIN | CacheFlush, ttl, data);
    }

    byte[] Txt(string instance, int serviceIndex, uint ttl)
    {
        // WLED publishes its MAC; Home Assistant uses it as the device's unique id.
        string[] entries = serviceIndex == 0 ? [$"mac={_mac}"] : ["path=/"];
        var data = new List<byte>();
        foreach (var e in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(e);
            data.Add((byte)Math.Min(bytes.Length, 255));
            data.AddRange(bytes.Take(255));
        }
        return Record(instance, TypeTXT, ClassIN | CacheFlush, ttl, data.ToArray());
    }

    byte[] A(IPAddress address, uint ttl) => Record(_host, TypeA, ClassIN | CacheFlush, ttl, address.GetAddressBytes());

    static byte[] Record(string name, ushort type, ushort cls, uint ttl, byte[] data)
    {
        var n = Name(name);
        var r = new byte[n.Length + 10 + data.Length];
        n.CopyTo(r, 0);
        var o = n.Length;
        r[o] = (byte)(type >> 8); r[o + 1] = (byte)type;
        r[o + 2] = (byte)(cls >> 8); r[o + 3] = (byte)cls;
        r[o + 4] = (byte)(ttl >> 24); r[o + 5] = (byte)(ttl >> 16); r[o + 6] = (byte)(ttl >> 8); r[o + 7] = (byte)ttl;
        r[o + 8] = (byte)(data.Length >> 8); r[o + 9] = (byte)data.Length;
        data.CopyTo(r, o + 10);
        return r;
    }

    static byte[] Name(string name)
    {
        var result = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            result.Add((byte)Math.Min(bytes.Length, 63));
            result.AddRange(bytes.Take(63));
        }
        result.Add(0);
        return result.ToArray();
    }

    static byte[] Message(ushort id, List<(string Name, ushort Type, ushort Class)>? questions, List<byte[]> answers, List<byte[]> additional)
    {
        var m = new List<byte>
        {
            (byte)(id >> 8), (byte)id,
            0x84, 0x00,                                           // response, authoritative
            0, (byte)(questions?.Count ?? 0),
            (byte)(answers.Count >> 8), (byte)answers.Count,
            0, 0,
            (byte)(additional.Count >> 8), (byte)additional.Count,
        };
        foreach (var (name, type, cls) in questions ?? [])
        {
            m.AddRange(Name(name));
            m.Add((byte)(type >> 8)); m.Add((byte)type);
            var c = (ushort)(cls & 0x7FFF);
            m.Add((byte)(c >> 8)); m.Add((byte)c);
        }
        foreach (var a in answers) m.AddRange(a);
        foreach (var a in additional) m.AddRange(a);
        return m.ToArray();
    }

    /// <summary>Reads a possibly compressed DNS name; null when malformed.</summary>
    static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var guard = 0; guard < 128; guard++)
        {
            if (position >= packet.Length) return null;
            int len = packet[position];
            if (len == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length) return null;
                var pointer = (len & 0x3F) << 8 | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if (position + 1 + len > packet.Length) return null;
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, len)));
            position += 1 + len;
        }
        return null;
    }

    sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var h = new HashCode();
            h.AddBytes(obj);
            return h.ToHashCode();
        }
    }
}
