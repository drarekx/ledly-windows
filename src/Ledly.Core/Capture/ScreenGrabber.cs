namespace Ledly;

// CONTRACT STUB — replaced by the capture port. Keep these public signatures.

public sealed record GrabberConfig(string DisplayId, string? App, int Fps, RectD[] Zones, SamplingMode Sampling, bool BlackBorders);

/// <summary>Turns platform frames into one color per zone (mean / linear / dominant, black border crop) and keeps preview images.</summary>
public sealed class ScreenGrabber(IScreenCapture capture)
{
    /// <summary>One color per zone, on the capture thread.</summary>
    public event Action<RGB[]>? Colors;
    public event Action<CaptureStatus>? StatusChanged;
    public event Action<string>? SourceChanged;

    /// <summary>Thread-safe. Only while true are preview images made (a few per second).</summary>
    public bool PreviewWanted { get; set; }

    /// <summary>Latest preview as PNG and the content rect left after cropping black borders, in the image's pixels.</summary>
    public (byte[] Png, int Width, int Height, RectD Content)? LatestPreview => throw new NotImplementedException();

    /// <summary>Starts, reconfigures or (null) stops.</summary>
    public void Update(GrabberConfig? config) => throw new NotImplementedException();
}
