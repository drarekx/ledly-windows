using System.IO.Ports;

namespace Ledly;

/// <summary>Serial port failure with a translated, user-facing message.</summary>
public class SerialException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The driver (or System.IO.Ports on this OS) rejected the baud rate; other rates may still work.</summary>
public sealed class UnsupportedBaudException(string message, Exception? inner = null) : SerialException(message, inner);

/// <summary>
/// 8N1 serial port over <see cref="SerialPort"/>, with the operations the Adalight link and the
/// STK500 uploader need: arbitrary baud, DTR/RTS pulse, reads with timeouts. Not thread-safe: use it
/// from one thread. Timeouts are in seconds, like the Mac app.
/// </summary>
public sealed class SerialConnection(string path) : IDisposable
{
    SerialPort? _port;
    int _readTimeoutMs = -2;

    public string Path { get; } = path;
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>Opens 8N1 at <paramref name="baud"/>. Throws <see cref="UnsupportedBaudException"/> or <see cref="SerialException"/>.</summary>
    public void Open(int baud)
    {
        Close();
        var port = new SerialPort(Path, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1000,
            WriteTimeout = 2000,
            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
        };
        try
        {
            port.Open();
        }
        catch (ArgumentOutOfRangeException e)
        {
            port.Dispose();
            throw new UnsupportedBaudException(L.Tr("El sistema no admite {0} baudios en {1}.", baud, Path), e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            port.Dispose();
            // Windows reports a port held by another app as "access denied".
            var reason = e is UnauthorizedAccessException && OperatingSystem.IsWindows()
                ? L.Tr("el puerto está en uso por otra aplicación")
                : e.InnerException?.Message ?? e.Message;
            throw new SerialException(L.Tr("No se pudo abrir {0}: {1}", Path, reason), e);
        }
        _port = port;
        _readTimeoutMs = port.ReadTimeout;
    }

    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null) return;
        try { port.Close(); } catch { /* already gone (unplugged) */ }
        port.Dispose();
    }

    public void Dispose() => Close();

    /// <summary>Raises or drops DTR and RTS together: the Nano's reset is wired to DTR through a capacitor.</summary>
    public void SetDtr(bool on)
    {
        if (_port is not { IsOpen: true } port) return;
        try
        {
            port.DtrEnable = on;
            port.RtsEnable = on;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // Unplugged: the next write reports it.
        }
    }

    /// <summary>Discards anything received but not read yet.</summary>
    public void FlushInput()
    {
        try { _port?.DiscardInBuffer(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
    }

    public void Write(byte[] bytes) => Write(bytes, bytes.Length);

    public void Write(byte[] bytes, int count)
    {
        if (_port is not { IsOpen: true } port) throw new SerialException(L.Tr("Error al escribir en el puerto: {0}", L.Tr("el puerto está cerrado")));
        try
        {
            port.Write(bytes, 0, count);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new SerialException(L.Tr("Error al escribir en el puerto: {0}", e.Message), e);
        }
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes, waiting at most <paramref name="timeout"/> seconds for the first one (0 = only what is already there).</summary>
    public int Read(Span<byte> buffer, double timeout)
    {
        if (_port is not { IsOpen: true } port || buffer.IsEmpty) return 0;
        try
        {
            if (timeout <= 0)
            {
                var available = port.BytesToRead;
                if (available <= 0) return 0;
                buffer = buffer[..Math.Min(buffer.Length, available)];
            }
            else
            {
                var ms = Math.Max(1, (int)Math.Ceiling(timeout * 1000));
                if (ms != _readTimeoutMs)
                {
                    port.ReadTimeout = ms;
                    _readTimeoutMs = ms;
                }
            }
            return port.BaseStream.Read(buffer);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>Reads up to <paramref name="max"/> bytes, waiting at most <paramref name="timeout"/> seconds for the first one.</summary>
    public byte[] Read(int max, double timeout)
    {
        var buffer = new byte[max];
        var n = Read(buffer, timeout);
        return n == buffer.Length ? buffer : buffer[..n];
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or throws on timeout.</summary>
    public byte[] ReadExactly(int count, double timeout)
    {
        var result = new byte[count];
        var got = 0;
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (got < count)
        {
            var left = (deadline - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) throw new SerialException(L.Tr("El dispositivo no responde."));
            got += Read(result.AsSpan(got), left);
        }
        return result;
    }

    /// <summary>Reads one text line (without the newline, trimmed), or null on timeout.</summary>
    public string? ReadLine(double timeout)
    {
        var line = new List<byte>();
        Span<byte> one = stackalloc byte[1];
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        while (true)
        {
            var left = (deadline - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) return null;
            if (Read(one, left) == 0) continue;
            if (one[0] == 0x0A) return System.Text.Encoding.UTF8.GetString(line.ToArray()).Trim();
            line.Add(one[0]);
            if (line.Count > 200) line.RemoveRange(0, 100);
        }
    }
}
