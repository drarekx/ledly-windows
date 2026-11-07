using System.Buffers.Binary;
using System.IO.Compression;

namespace Ledly;

/// <summary>What the grabber needs to turn a frame into one color per LED.</summary>
public sealed record GrabberConfig(string DisplayId, string? App, int Fps, RectD[] Zones, SamplingMode Sampling, bool BlackBorders);

/// <summary>Turns platform frames into one color per zone (mean / linear / dominant, black border crop) and keeps preview images.</summary>
public sealed class ScreenGrabber
{
    /// <summary>Width the analysis works at; larger frames are subsampled to about this.</summary>
    const int AnalysisWidth = 256;
    const int PreviewMaxWidth = 320;
    static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(0.15);

    readonly IScreenCapture _capture;
    readonly object _lock = new();
    GrabberConfig? _config;
    /// <summary>What the platform capture was last told to look at (engine thread).</summary>
    CaptureTarget? _target;
    bool _previewWanted;
    (byte[] Png, int Width, int Height, RectD Content)? _latestPreview;
    // Capture thread only.
    readonly FrameAnalyzer _analysis = new();
    DateTime _lastPreview = DateTime.MinValue;

    /// <summary>One color per zone, on the capture thread.</summary>
    public event Action<RGB[]>? Colors;
    public event Action<CaptureStatus>? StatusChanged;
    public event Action<string>? SourceChanged;

    public ScreenGrabber(IScreenCapture capture)
    {
        _capture = capture;
        capture.Frame += OnFrame;
        capture.StatusChanged += status => StatusChanged?.Invoke(status);
        capture.SourceChanged += source => SourceChanged?.Invoke(source);
    }

    /// <summary>Thread-safe. Only while true are preview images made (a few per second).</summary>
    public bool PreviewWanted
    {
        get { lock (_lock) return _previewWanted; }
        set { lock (_lock) _previewWanted = value; }
    }

    /// <summary>Latest preview as PNG and the content rect left after cropping black borders, in the image's pixels.</summary>
    public (byte[] Png, int Width, int Height, RectD Content)? LatestPreview
    {
        get { lock (_lock) return _latestPreview; }
    }

    /// <summary>Starts, reconfigures or (null) stops. The capture only restarts when display, app or fps change.</summary>
    public void Update(GrabberConfig? config)
    {
        lock (_lock) _config = config;
        if (config is null)
        {
            if (_target is not null)
            {
                _target = null;
                _capture.Update(null);
            }
            return;
        }
        var target = new CaptureTarget(config.DisplayId, config.App, Math.Clamp(config.Fps, 5, 60));
        if (_target != target)
        {
            _target = target;
            _capture.Update(target);
        }
    }

    void OnFrame(in CaptureFrame captured)
    {
        GrabberConfig? config;
        bool previewWanted;
        lock (_lock)
        {
            config = _config;
            previewWanted = _previewWanted;
        }
        if (config is null || captured.Width <= 0 || captured.Height <= 0) return;
        if (captured.Pixels.Length < (captured.Height - 1) * captured.Stride + captured.Width * 4) return;

        // Full-resolution frames are read with a stride so the cost stays like a ~256 px wide image.
        var step = Math.Max(1, (int)Math.Round(captured.Width / (double)AnalysisWidth));
        var frame = new BGRAFrame(captured.Pixels, (captured.Width + step - 1) / step, (captured.Height + step - 1) / step,
            captured.Stride * step, 4 * step);
        var colors = _analysis.Colors(frame, config);
        Colors?.Invoke(colors);

        var now = DateTime.UtcNow;
        if (previewWanted && now - _lastPreview > PreviewInterval)
        {
            _lastPreview = now;
            var preview = Preview(frame, _analysis.Content);
            lock (_lock) _latestPreview = preview;
        }
    }

    /// <summary>Copies the frame, downscaled to at most 320 px wide, into a PNG (the platform reuses its buffer).</summary>
    static (byte[] Png, int Width, int Height, RectD Content) Preview(in BGRAFrame f, RectD content)
    {
        var k = (f.Width + PreviewMaxWidth - 1) / PreviewMaxWidth;
        int w = (f.Width + k - 1) / k, h = (f.Height + k - 1) / k;
        var rgb = new byte[w * h * 3];
        var o = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = f.Offset(x * k, y * k);
            rgb[o++] = f.Pixels[p + 2];
            rgb[o++] = f.Pixels[p + 1];
            rgb[o++] = f.Pixels[p];
        }
        return (Png.EncodeRGB(rgb, w, h), w, h,
            new RectD(content.X / k, content.Y / k, content.Width / k, content.Height / k));
    }
}

/// <summary>A BGRA image, possibly a strided view (every n-th pixel and row) of a larger one.</summary>
readonly ref struct BGRAFrame(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, int pixelBytes)
{
    public readonly ReadOnlySpan<byte> Pixels = pixels;
    public readonly int Width = width;
    public readonly int Height = height;
    public readonly int RowBytes = rowBytes;
    public readonly int PixelBytes = pixelBytes;

    public int Offset(int x, int y) => y * RowBytes + x * PixelBytes;

    /// <summary>Channel value at or below which a pixel counts as black (5 %).</summary>
    public bool IsBlack(int x, int y, byte threshold)
    {
        var o = Offset(x, y);
        return Pixels[o] <= threshold && Pixels[o + 1] <= threshold && Pixels[o + 2] <= threshold;
    }
}

/// <summary>Turns frames into zone colors; keeps the black border detector's state between frames.</summary>
sealed class FrameAnalyzer
{
    static readonly float[] ToLinear = BuildLinear();

    static float[] BuildLinear()
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            lut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return lut;
    }

    static float ToSRGB(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    readonly BlackBorderDetector _border = new();
    /// <summary>Area of the last frame used for the zones, after cropping black borders (frame pixels).</summary>
    public RectD Content { get; private set; }

    public RGB[] Colors(in BGRAFrame frame, GrabberConfig config)
    {
        var content = new RectD(0, 0, frame.Width, frame.Height);
        if (config.BlackBorders)
        {
            var (h, v) = _border.Process(frame);
            content = new RectD(v, h, Math.Max(0, frame.Width - 2 * v), Math.Max(0, frame.Height - 2 * h));
        }
        else
        {
            _border.Reset();
        }
        Content = content;
        var zones = config.Zones;
        var output = new RGB[zones.Length];
        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            var x0 = (int)Math.Floor(content.X + zone.X * content.Width);
            var x1 = Math.Max(x0 + 1, (int)Math.Ceiling(content.X + zone.MaxX * content.Width));
            var y0 = (int)Math.Floor(content.Y + zone.Y * content.Height);
            var y1 = Math.Max(y0 + 1, (int)Math.Ceiling(content.Y + zone.MaxY * content.Height));
            output[i] = Average(frame, Math.Max(0, x0), Math.Min(frame.Width, x1),
                Math.Max(0, y0), Math.Min(frame.Height, y1), config.Sampling);
        }
        return output;
    }

    static RGB Average(in BGRAFrame f, int x0, int x1, int y0, int y1, SamplingMode mode)
    {
        if (x1 <= x0 || y1 <= y0) return RGB.Black;
        var px = f.Pixels;
        var count = (float)((x1 - x0) * (y1 - y0));
        switch (mode)
        {
            case SamplingMode.linear:
            {
                var lut = ToLinear;
                float r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var o = f.Offset(x, y);
                    b += lut[px[o]]; g += lut[px[o + 1]]; r += lut[px[o + 2]];
                }
                return new(ToSRGB(r / count), ToSRGB(g / count), ToSRGB(b / count));
            }

            case SamplingMode.dominant:
            {
                // Average of the pixels that stand out most (bright and saturated).
                float best = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    best = MathF.Max(best, Score(px, f.Offset(x, y)));
                var cut = best * 0.7f;
                long r = 0, g = 0, b = 0, n = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var o = f.Offset(x, y);
                    if (Score(px, o) < cut) continue;
                    b += px[o]; g += px[o + 1]; r += px[o + 2]; n++;
                }
                if (n == 0) return RGB.Black;
                var k = n * 255f;
                return new(r / k, g / k, b / k);
            }

            default:   // mean
            {
                long r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var o = f.Offset(x, y);
                    b += px[o]; g += px[o + 1]; r += px[o + 2];
                }
                return new(r / count / 255, g / count / 255, b / count / 255);
            }
        }
    }

    static float Score(ReadOnlySpan<byte> px, int o)
    {
        int b = px[o], g = px[o + 1], r = px[o + 2];
        int hi = Math.Max(b, Math.Max(g, r)), lo = Math.Min(b, Math.Min(g, r));
        return hi * (0.4f + (hi - lo) / 255f);
    }
}

/// <summary>Finds letterbox/pillarbox bars and only trusts a new size once it has been stable for a while.</summary>
sealed class BlackBorderDetector
{
    /// <summary>Channel value at or below which a pixel counts as black (5 %).</summary>
    const byte Threshold = 13;
    /// <summary>Frames a new border must hold before it is used.</summary>
    const int ConfirmFrames = 50;

    (int H, int V) _current;
    (int H, int V) _candidate;
    int _stableFor;

    public void Reset()
    {
        _current = (0, 0);
        _candidate = (0, 0);
        _stableFor = 0;
    }

    /// <summary>Returns the (horizontal bars height, vertical bars width) to crop, in pixels.</summary>
    public (int H, int V) Process(in BGRAFrame f)
    {
        if (Detect(f) is not { } found) return _current;
        if (Math.Abs(found.H - _candidate.H) <= 1 && Math.Abs(found.V - _candidate.V) <= 1)
        {
            _stableFor++;
        }
        else
        {
            _candidate = found;
            _stableFor = 0;
        }
        // Content growing into the bars is applied faster than new bars appearing.
        var needed = _candidate.H < _current.H || _candidate.V < _current.V ? 10 : ConfirmFrames;
        if (_stableFor >= needed)
        {
            // One extra pixel so the soft edge of the picture doesn't bleed in.
            _current = (_candidate.H > 0 ? _candidate.H + 1 : 0, _candidate.V > 0 ? _candidate.V + 1 : 0);
        }
        return _current;
    }

    /// <summary>Null when the frame is (almost) entirely black: nothing to learn from it.</summary>
    static (int H, int V)? Detect(in BGRAFrame f)
    {
        int w = f.Width, h = f.Height;
        int x1 = w / 3, x2 = w / 2, x3 = w * 2 / 3;
        int y1 = h / 3, y2 = h / 2, y3 = h * 2 / 3;
        bool Row(in BGRAFrame fr, int y) => fr.IsBlack(x1, y, Threshold) && fr.IsBlack(x2, y, Threshold) && fr.IsBlack(x3, y, Threshold);
        bool Column(in BGRAFrame fr, int x) => fr.IsBlack(x, y1, Threshold) && fr.IsBlack(x, y2, Threshold) && fr.IsBlack(x, y3, Threshold);

        var top = 0;
        while (top < h / 2 && Row(f, top)) top++;
        if (top >= h / 2) return null;
        var bottom = 0;
        while (bottom < h / 2 && Row(f, h - 1 - bottom)) bottom++;
        var left = 0;
        while (left < w / 2 && Column(f, left)) left++;
        if (left >= w / 2) return null;
        var right = 0;
        while (right < w / 2 && Column(f, w - 1 - right)) right++;
        return (Math.Min(top, bottom), Math.Min(left, right));
    }
}

/// <summary>Minimal PNG writer: 8-bit RGB, no filtering, zlib from the framework.</summary>
static class Png
{
    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static byte[] EncodeRGB(byte[] rgb, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;    // bit depth
        header[9] = 2;    // RGB
        header[10] = 0;   // deflate
        header[11] = 0;   // adaptive filtering (filter byte per row)
        header[12] = 0;   // no interlace
        Chunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = width * 3;
                for (var y = 0; y < height; y++)
                {
                    z.WriteByte(0);   // filter: none
                    z.Write(rgb, y * row, row);
                }
            }
            Chunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        s.Write(buffer);
        Span<byte> typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        s.Write(buffer);
    }
}
