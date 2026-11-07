namespace Ledly;

// CONTRACT STUB — replaced by the audio port. Keep these public signatures.

public sealed class AudioLevels
{
    public const int BandCount = 16;
    public float[] Bands { get; init; } = new float[BandCount];
    public float Level { get; init; }
    public float Bass { get; init; }
    public DateTime LastBeat { get; init; } = DateTime.MinValue;
    public float[] LeftBands { get; init; } = new float[BandCount];
    public float[] RightBands { get; init; } = new float[BandCount];
    public float LeftLevel { get; init; }
    public float RightLevel { get; init; }
    public static readonly AudioLevels Silent = new();
    public static AudioLevels Demo(double time) => throw new NotImplementedException();
}

/// <summary>FFT of the system audio fed by an <see cref="IAudioCapture"/>.</summary>
public sealed class AudioAnalyzer(IAudioCapture capture)
{
    public bool IsRunning => capture.IsRunning;
    public void Start() => throw new NotImplementedException();
    public void Stop() => throw new NotImplementedException();
    /// <summary>Latest levels; thread-safe.</summary>
    public AudioLevels Current() => throw new NotImplementedException();
}
