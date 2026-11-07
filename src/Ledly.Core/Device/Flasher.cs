using System.Globalization;
using System.Text;

namespace Ledly;

/// <summary>Firmware images embedded in the core, one per data pin.</summary>
public static class Firmware
{
    public static readonly int[] Pins = Enumerable.Range(2, 12).ToArray();
    public const int Baud = 1_000_000;
    /// <summary>Rates of older Ledly firmware, still accepted when connecting.</summary>
    public static readonly int[] LegacyBauds = [500_000];
    public const int Version = 2;

    /// <summary>Resource name of the image for <paramref name="pin"/> ("ledly-pin6.hex").</summary>
    public static string FileName(int pin) => $"ledly-pin{pin}.hex";

    /// <summary>Intel HEX text of the firmware for <paramref name="pin"/> (embedded resource), or null.</summary>
    public static string? Hex(int pin)
    {
        using var stream = typeof(Firmware).Assembly.GetManifestResourceStream($"firmware/{FileName(pin)}");
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public enum FlashStepKind { connecting, backingUp, writing, verifying, done }

public readonly record struct FlashStep(FlashStepKind Kind, double Progress = 0);

public sealed class FlashException(string message) : Exception(message)
{
    internal static FlashException BadHex(int line) => new(L.Tr("El archivo de firmware está dañado (línea {0}).", line));
    internal static FlashException TooBig(int size) => new(L.Tr("El firmware ocupa {0} bytes y no cabe en el ATmega328P.", size));
    internal static FlashException NoBootloader() => new(L.Tr("El bootloader no responde. Comprueba que es un Arduino Nano y prueba a pulsar reset justo al instalar."));
    internal static FlashException WrongChip(string signature) => new(L.Tr("El chip no es un ATmega328P (firma {0}).", signature));
    internal static FlashException Protocol(string step) => new(L.Tr("Fallo de comunicación con el bootloader ({0}).", step));
    internal static FlashException Verify(int address) => new(L.Tr("La verificación falló en la dirección 0x{0}.", address.ToString("X4", CultureInfo.InvariantCulture)));
}

/// <summary>
/// Uploads an Intel HEX image to an Arduino Nano through its serial bootloader
/// (STK500 v1: Optiboot at 115200 baud, or the old ATmegaBOOT at 57600).
/// </summary>
public sealed class Flasher(string port, Action<FlashStep> progress)
{
    const int PageSize = 128;
    /// <summary>32 KB minus the 2 KB bootloader area.</summary>
    const int FlashLimit = 30 * 1024;

    // STK500 v1 constants.
    const byte SyncCrc = 0x20;
    const byte InSync = 0x14;
    const byte Ok = 0x10;

    readonly SerialConnection _port = new(port);

    /// <summary>Where the firmware found on the board is saved before it is overwritten. Set by the app at start.</summary>
    public static string BackupFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Ledly", "Backups");

    /// <summary>Path of the last backup written by <see cref="Flash"/>, if any.</summary>
    public string? BackupPath { get; private set; }

    /// <summary>Blocking. Backs up the current firmware first. Throws <see cref="FlashException"/> with a translated message.</summary>
    public void Flash(string hex)
    {
        var image = ParseHex(hex);
        if (image.Length > FlashLimit) throw FlashException.TooBig(image.Length);
        try
        {
            progress(new FlashStep(FlashStepKind.connecting));
            Connect();
            CheckSignature();
            Command([0x50], step: "enter");
            SaveBackup();

            var pages = Enumerable.Range(0, (image.Length + PageSize - 1) / PageSize).Select(n => n * PageSize).ToArray();
            for (var n = 0; n < pages.Length; n++)
            {
                var address = pages[n];
                var page = image.AsSpan(address, Math.Min(PageSize, image.Length - address));
                LoadAddress(address);
                var size = page.Length;
                byte[] body = [0x64, (byte)(size >> 8), (byte)(size & 0xFF), 0x46, .. page];
                Command(body, step: "write");
                progress(new FlashStep(FlashStepKind.writing, (n + 1) / (double)pages.Length));
            }
            for (var n = 0; n < pages.Length; n++)
            {
                var address = pages[n];
                var expected = image.AsSpan(address, Math.Min(PageSize, image.Length - address));
                LoadAddress(address);
                var size = expected.Length;
                var read = Command([0x74, (byte)(size >> 8), (byte)(size & 0xFF), 0x46], replyLength: size, step: "read");
                if (!expected.SequenceEqual(read))
                {
                    var index = 0;
                    while (index < size && read[index] == expected[index]) index++;
                    throw FlashException.Verify(address + index);
                }
                progress(new FlashStep(FlashStepKind.verifying, (n + 1) / (double)pages.Length));
            }
            try { Command([0x51], step: "leave"); } catch (FlashException) { }
            progress(new FlashStep(FlashStepKind.done, 1));
        }
        finally
        {
            _port.Close();
        }
    }

    /// <summary>Reads the whole application area without writing anything (<c>--read-flash PORT OUT.hex</c>).</summary>
    public byte[] ReadFlash()
    {
        try
        {
            Connect();
            CheckSignature();
            Command([0x50], step: "enter");
            var data = ReadImage();
            try { Command([0x51], step: "leave"); } catch (FlashException) { }
            return data;
        }
        finally
        {
            _port.Close();
        }
    }

    void CheckSignature()
    {
        var signature = Command([0x75], replyLength: 3, step: "signature");
        // 1E 95 0F = ATmega328P, 1E 95 14 = ATmega328.
        if (signature[0] != 0x1E || signature[1] != 0x95 || (signature[2] != 0x0F && signature[2] != 0x14))
            throw FlashException.WrongChip(string.Join(' ', signature.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
    }

    /// <summary>Application flash with trailing erased (0xFF) bytes trimmed.</summary>
    byte[] ReadImage()
    {
        var data = new List<byte>(FlashLimit);
        for (var address = 0; address < FlashLimit; address += PageSize)
        {
            LoadAddress(address);
            data.AddRange(Command([0x74, 0, PageSize, 0x46], replyLength: PageSize, step: "read"));
            progress(new FlashStep(FlashStepKind.backingUp, (address + PageSize) / (double)FlashLimit));
        }
        var end = data.Count;
        while (end > 0 && data[end - 1] == 0xFF) end--;
        return data.GetRange(0, end).ToArray();
    }

    void SaveBackup()
    {
        var data = ReadImage();
        if (data.Length == 0) return;
        Directory.CreateDirectory(BackupFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd'T'HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(BackupFolder, $"nano-{stamp}.hex");
        File.WriteAllText(path, MakeHex(data), new UTF8Encoding(false));
        BackupPath = path;
    }

    void Connect()
    {
        foreach (var baud in new[] { 115_200, 57_600 })
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _port.Open(baud);
                }
                catch (SerialException e)
                {
                    throw new FlashException(e.Message);
                }
                ResetBoard();
                for (var i = 0; i < 6; i++)
                {
                    _port.FlushInput();
                    Write([0x30, SyncCrc]);
                    var reply = _port.Read(2, 0.25);
                    if (reply.Length >= 1 && reply[0] == InSync)
                    {
                        // Drain the rest and sync once more to be sure we're aligned.
                        _port.Read(16, 0.05);
                        _port.FlushInput();
                        try
                        {
                            Command([0x30], step: "sync");
                            return;
                        }
                        catch (FlashException) { }
                    }
                }
                _port.Close();
            }
        }
        throw FlashException.NoBootloader();
    }

    /// <summary>Pulses DTR/RTS: the Nano's reset is wired to DTR through a capacitor.</summary>
    void ResetBoard()
    {
        _port.SetDtr(false);
        Thread.Sleep(250);
        _port.SetDtr(true);
        Thread.Sleep(50);
    }

    void LoadAddress(int byteAddress)
    {
        var word = byteAddress / 2;
        Command([0x55, (byte)(word & 0xFF), (byte)(word >> 8)], step: "address");
    }

    void Write(byte[] bytes)
    {
        try
        {
            _port.Write(bytes);
        }
        catch (SerialException e)
        {
            throw new FlashException(e.Message);
        }
    }

    /// <summary>Sends <paramref name="body"/> + CRC_EOP and expects INSYNC, <paramref name="replyLength"/> bytes, OK.</summary>
    byte[] Command(byte[] body, int replyLength = 0, string step = "")
    {
        Write([.. body, SyncCrc]);
        byte[] reply;
        try
        {
            reply = _port.ReadExactly(replyLength + 2, 1);
        }
        catch (SerialException)
        {
            throw FlashException.Protocol(step);
        }
        if (reply[0] != InSync || reply[^1] != Ok) throw FlashException.Protocol(step);
        return reply[1..(1 + replyLength)];
    }

    /// <summary>Flat image → Intel HEX (16-byte data records).</summary>
    public static string MakeHex(byte[] data)
    {
        var text = new StringBuilder();
        for (var start = 0; start < data.Length; start += 16)
        {
            var chunk = data.AsSpan(start, Math.Min(16, data.Length - start));
            byte[] record = [(byte)chunk.Length, (byte)(start >> 8 & 0xFF), (byte)(start & 0xFF), 0, .. chunk];
            var sum = 0;
            foreach (var b in record) sum += b;
            var checksum = (byte)(256 - (sum & 0xFF));
            text.Append(':').Append(Convert.ToHexString(record)).Append(checksum.ToString("X2", CultureInfo.InvariantCulture)).Append('\n');
        }
        text.Append(":00000001FF\n");
        return text.ToString();
    }

    /// <summary>Intel HEX → flat image starting at address 0, padded with 0xFF.</summary>
    public static byte[] ParseHex(string text)
    {
        var memory = new Dictionary<int, byte>();
        var baseAddress = 0;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) continue;
            if (line[0] != ':' || line.Length < 11 || line.Length % 2 != 1) throw FlashException.BadHex(index + 1);
            byte[] bytes;
            try
            {
                bytes = Convert.FromHexString(line.AsSpan(1));
            }
            catch (FormatException)
            {
                throw FlashException.BadHex(index + 1);
            }
            var length = bytes[0];
            if (bytes.Length != length + 5) throw FlashException.BadHex(index + 1);
            var sum = 0;
            foreach (var b in bytes) sum += b;
            if ((sum & 0xFF) != 0) throw FlashException.BadHex(index + 1);
            var address = bytes[1] << 8 | bytes[2];
            var data = bytes.AsSpan(4, length);
            switch (bytes[3])
            {
                case 0x00:
                    for (var k = 0; k < data.Length; k++) memory[baseAddress + address + k] = data[k];
                    break;
                case 0x01:
                    break;
                case 0x02:
                    if (length < 2) throw FlashException.BadHex(index + 1);
                    baseAddress = (data[0] << 8 | data[1]) << 4;
                    break;
                case 0x04:
                    if (length < 2) throw FlashException.BadHex(index + 1);
                    baseAddress = (data[0] << 8 | data[1]) << 16;
                    break;
            }
        }
        if (memory.Count == 0) return [];
        var image = new byte[memory.Keys.Max() + 1];
        Array.Fill(image, (byte)0xFF);
        foreach (var (addr, b) in memory) image[addr] = b;
        return image;
    }
}
