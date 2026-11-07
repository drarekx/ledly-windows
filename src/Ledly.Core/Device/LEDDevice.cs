using System.Diagnostics;

namespace Ledly;

public enum DeviceStatusKind { disconnected, connecting, connected, failed }

/// <summary>Firmware: the hello line ("Ada Ledly 2 pin 6 max 400 baud 1000000"), null for a silent Adalight sketch.</summary>
public sealed record DeviceStatus(DeviceStatusKind Kind, string? Firmware = null, string? Error = null)
{
    public static readonly DeviceStatus Disconnected = new(DeviceStatusKind.disconnected);
    public static readonly DeviceStatus Connecting = new(DeviceStatusKind.connecting);
}

/// <summary>Connection to the Arduino that drives the strip (Adalight protocol). Only used from the engine thread.</summary>
public sealed class LEDDevice
{
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public bool IsConnected => Status.Kind == DeviceStatusKind.connected;
    public string? Port { get; private set; }
    public int FramesSent { get; private set; }
    /// <summary>Baud rate of the open connection (0 when closed).</summary>
    public int Baud { get; private set; }

    SerialConnection? _port;
    /// <summary>Ledly firmware answers 'k' after each frame; we wait for it before sending the next.</summary>
    bool _acknowledges;
    bool _awaitingAck;
    long _lastSend;
    byte[] _frame = [];
    readonly byte[] _reply = new byte[64];

    /// <summary>Blocking: opens, resets, waits for the hello, tries the known baud rates.</summary>
    /// <remarks>
    /// Tries the rate that worked last time first (so reconnecting usually takes one reset), then the
    /// current firmware's and the older ones (v1 ran at 500000). Waits up to ~2.5 s per rate.
    /// </remarks>
    public void Connect(string port)
    {
        Disconnect();
        Status = DeviceStatus.Connecting;
        var last = LastBaud;
        var rates = new List<int>();
        if (last > 0) rates.Add(last);
        foreach (var rate in Firmware.LegacyBauds.Prepend(Firmware.Baud))
            if (!rates.Contains(rate)) rates.Add(rate);

        string? hello = null;
        SerialConnection? opened = null;
        var openedRate = 0;
        SerialException? openError = null;
        foreach (var rate in rates)
        {
            var connection = new SerialConnection(port);
            try
            {
                connection.Open(rate);
            }
            catch (UnsupportedBaudException e)
            {
                // System.IO.Ports may refuse non-standard rates on macOS: try the next one.
                openError ??= e;
                continue;
            }
            catch (SerialException e)
            {
                Fail(e.Message);
                return;
            }
            hello = WaitForHello(connection);
            if (hello is not null)
            {
                opened = connection;
                openedRate = rate;
                if (rate != last) LastBaud = rate;
                break;
            }
            connection.Close();
        }
        if (opened is null)
        {
            // A silent Adalight sketch (not ours): assume the current rate.
            var connection = new SerialConnection(port);
            try
            {
                connection.Open(Firmware.Baud);
            }
            catch (SerialException e)
            {
                Fail((openError ?? e).Message);
                return;
            }
            opened = connection;
            openedRate = Firmware.Baud;
        }
        _port = opened;
        Port = port;
        Baud = openedRate;
        _acknowledges = hello?.Contains("Ledly") == true;
        _awaitingAck = false;
        Status = new DeviceStatus(DeviceStatusKind.connected, hello);
    }

    static int LastBaud
    {
        get { try { return Settings.Shared.LastBaud; } catch (InvalidOperationException) { return 0; } }
        set { try { Settings.Shared.LastBaud = value; } catch (InvalidOperationException) { } }
    }

    /// <summary>
    /// Resets the Nano so our sketch says hello; the bootloader runs for ~1 s first. Opening the port
    /// already pulses DTR, but not when it was closed a moment ago, so pulse it explicitly.
    /// </summary>
    static string? WaitForHello(SerialConnection port)
    {
        port.SetDtr(false);
        Thread.Sleep(100);
        port.SetDtr(true);
        port.FlushInput();
        var deadline = DateTime.UtcNow.AddSeconds(2.5);
        while (true)
        {
            var left = (deadline - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) return null;
            if (port.ReadLine(left) is { } line && line.StartsWith("Ada", StringComparison.Ordinal)) return line;
        }
    }

    /// <summary>"Ada Ledly 2 pin 6 max 400 baud 1000000" → 2. Null for other Adalight firmware.</summary>
    public static int? LedlyVersion(string? hello)
    {
        var parts = Split(hello);
        if (parts.Length < 3 || parts[1] != "Ledly") return null;
        return int.TryParse(parts[2], out var v) ? v : null;
    }

    /// <summary>"… pin 6 …" → 6.</summary>
    public static int? LedlyPin(string? hello)
    {
        var parts = Split(hello);
        var i = Array.IndexOf(parts, "pin");
        if (i < 0 || i + 1 >= parts.Length) return null;
        return int.TryParse(parts[i + 1], out var v) ? v : null;
    }

    static string[] Split(string? hello) =>
        hello?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public void Disconnect()
    {
        Close();
        if (Status.Kind != DeviceStatusKind.disconnected) Status = DeviceStatus.Disconnected;
    }

    void Fail(string message)
    {
        Close();
        Status = new DeviceStatus(DeviceStatusKind.failed, Error: message);
    }

    void Close()
    {
        _port?.Close();
        _port = null;
        Port = null;
        Baud = 0;
    }

    /// <summary>Adalight header: "Ada", LED count − 1 big-endian, checksum hi ^ lo ^ 0x55.</summary>
    public static void WriteHeader(Span<byte> destination, int ledCount)
    {
        var count = ledCount - 1;
        byte hi = (byte)(count >> 8 & 0xFF), lo = (byte)(count & 0xFF);
        destination[0] = 0x41; // 'A'
        destination[1] = 0x64; // 'd'
        destination[2] = 0x61; // 'a'
        destination[3] = hi;
        destination[4] = lo;
        destination[5] = (byte)(hi ^ lo ^ 0x55);
    }

    /// <summary>Sends one frame; false when skipped (waiting for the 'k') or failed.</summary>
    public bool Send(RGB[] colors)
    {
        if (_port is not { } port || !IsConnected || colors.Length == 0) return false;
        if (_awaitingAck)
        {
            var n = port.Read(_reply, 0);
            if (Array.IndexOf(_reply, (byte)0x6B, 0, n) >= 0) // 'k'
                _awaitingAck = false;
            else if (Stopwatch.GetElapsedTime(_lastSend).TotalSeconds < 0.25)
                return false;
            else
                _awaitingAck = false; // Lost ack (or the board rebooted): carry on.
        }

        var length = 6 + colors.Length * 3;
        if (_frame.Length != length) _frame = new byte[length];
        WriteHeader(_frame, colors.Length);
        var o = 6;
        foreach (var c in colors)
        {
            _frame[o++] = (byte)c.R8;
            _frame[o++] = (byte)c.G8;
            _frame[o++] = (byte)c.B8;
        }
        try
        {
            port.Write(_frame);
        }
        catch (SerialException e)
        {
            Fail(e.Message);
            return false;
        }
        _lastSend = Stopwatch.GetTimestamp();
        FramesSent++;
        _awaitingAck = _acknowledges;
        return true;
    }
}
