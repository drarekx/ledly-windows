namespace Ledly;

/// <summary>What the music effects see of the audio, refreshed ~50 times a second. Immutable once published.</summary>
public sealed class AudioLevels
{
    public const int BandCount = 16;

    /// <summary>Log-spaced bands from ~40 Hz to ~16 kHz, 0…1 after automatic gain.</summary>
    public float[] Bands { get; init; } = new float[BandCount];
    /// <summary>Overall loudness, 0…1.</summary>
    public float Level { get; init; }
    /// <summary>Low end (kick drum, bass), 0…1.</summary>
    public float Bass { get; init; }
    /// <summary>Time of the last detected beat (UTC).</summary>
    public DateTime LastBeat { get; init; } = DateTime.MinValue;
    /// <summary>Same bands per channel (mono sources give the same values on both).</summary>
    public float[] LeftBands { get; init; } = new float[BandCount];
    public float[] RightBands { get; init; } = new float[BandCount];
    public float LeftLevel { get; init; }
    public float RightLevel { get; init; }

    public static readonly AudioLevels Silent = new();

    /// <summary>Fake but plausible levels for the effect previews in the window.</summary>
    public static AudioLevels Demo(double time)
    {
        var beatPhase = time * 2 % 1;   // 120 bpm
        var kick = (float)Math.Max(0, 1 - beatPhase * 3);
        var bands = new float[BandCount];
        var left = new float[BandCount];
        var right = new float[BandCount];
        for (var i = 0; i < BandCount; i++)
        {
            var x = (float)i / BandCount;
            var wobble = (float)(0.5 + 0.5 * Math.Sin(time * (2 + i * 0.7) + i));
            bands[i] = MathF.Min(1, (1 - x) * kick * 0.9f + wobble * 0.45f * (1 - x * 0.4f));
        }
        // Slightly different channels so the stereo layout shows something.
        for (var i = 0; i < BandCount; i++)
        {
            left[i] = MathF.Min(1, bands[i] * (float)(0.8 + 0.2 * Math.Sin(time * 1.3 + i)));
            right[i] = MathF.Min(1, bands[i] * (float)(0.8 + 0.2 * Math.Cos(time * 1.1 + i)));
        }
        var level = 0.4f + 0.4f * kick;
        return new AudioLevels
        {
            Bands = bands,
            Bass = kick,
            Level = level,
            LeftBands = left,
            RightBands = right,
            LeftLevel = level * (float)(0.85 + 0.15 * Math.Sin(time * 1.3)),
            RightLevel = level * (float)(0.85 + 0.15 * Math.Cos(time * 1.1)),
            LastBeat = DateTime.UtcNow.AddSeconds(-beatPhase / 2),
        };
    }
}

/// <summary>
/// FFT of the system audio fed by an <see cref="IAudioCapture"/>: 16 log bands per channel with automatic
/// gain, overall level, bass and beat detection.
/// </summary>
public sealed class AudioAnalyzer
{
    const int FftSize = 1024;
    /// <summary>Analyses per second, whatever packet size the capture delivers.</summary>
    const int AnalysisRate = 50;

    readonly IAudioCapture _capture;
    readonly object _lock = new();
    AudioLevels _levels = AudioLevels.Silent;
    volatile bool _active;

    // Analysis state (capture thread only).
    /// <summary>Last FftSize samples of each channel (left, right).</summary>
    readonly float[][] _rings = [new float[FftSize], new float[FftSize]];
    int _ringIndex;
    int _pending;
    int _sampleRate;
    int[] _bandEdges = [];
    /// <summary>Slowly decaying peak per band: the automatic gain.</summary>
    readonly float[] _peaks = new float[AudioLevels.BandCount];
    float _levelPeak = 1e-4f;
    readonly float[] _bassHistory = new float[40];
    int _bassCount;
    int _bassNext;
    DateTime _lastBeat = DateTime.MinValue;
    readonly Fft _fft = new(FftSize);
    readonly float[] _window = new float[FftSize];
    readonly float[] _leftMagnitudes = new float[FftSize / 2];
    readonly float[] _rightMagnitudes = new float[FftSize / 2];

    public AudioAnalyzer(IAudioCapture capture)
    {
        _capture = capture;
        Array.Fill(_peaks, 1e-4f);
        // Hann window, denormalized (peak 1), like vDSP's hanningDenormalized.
        for (var i = 0; i < FftSize; i++) _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / FftSize));
        capture.Samples += OnSamples;
    }

    public bool IsRunning => _capture.IsRunning;

    public void Start()
    {
        if (_capture.IsRunning) return;
        _active = true;
        _capture.Start();
    }

    public void Stop()
    {
        _active = false;
        if (_capture.IsRunning) _capture.Stop();
        lock (_lock) _levels = AudioLevels.Silent;
    }

    /// <summary>Latest levels; thread-safe.</summary>
    public AudioLevels Current()
    {
        lock (_lock) return _levels;
    }

    void OnSamples(ReadOnlyMemory<float> memory, int channels, int sampleRate)
    {
        if (!_active || channels <= 0 || sampleRate <= 0) return;
        if (sampleRate != _sampleRate) Configure(sampleRate);
        var samples = memory.Span;
        var frames = samples.Length / channels;
        if (frames == 0) return;
        var right = channels > 1 ? 1 : 0;   // mono → both sides
        float[] l = _rings[0], r = _rings[1];
        for (var f = 0; f < frames; f++)
        {
            var o = f * channels;
            l[_ringIndex] = samples[o];
            r[_ringIndex] = samples[o + right];
            _ringIndex = (_ringIndex + 1) % FftSize;
        }
        _pending += frames;
        // At most one analysis per packet (as on Mac), and no more than ~50 a second with small packets.
        if (_pending < sampleRate / AnalysisRate) return;
        _pending = 0;
        Analyze();
    }

    /// <summary>FFT bin index where each band starts, log-spaced 40 Hz … 16 kHz.</summary>
    void Configure(int sampleRate)
    {
        _sampleRate = sampleRate;
        var binHz = (double)sampleRate / FftSize;
        var edges = new int[AudioLevels.BandCount + 1];
        for (var i = 0; i <= AudioLevels.BandCount; i++)
        {
            var hz = 40 * Math.Pow(16_000.0 / 40, (double)i / AudioLevels.BandCount);
            edges[i] = Math.Max(1, (int)Math.Round(hz / binHz, MidpointRounding.AwayFromZero));
        }
        // At least one bin per band.
        for (var i = 1; i < edges.Length; i++) if (edges[i] <= edges[i - 1]) edges[i] = edges[i - 1] + 1;
        _bandEdges = edges;
        _pending = 0;
    }

    /// <summary>Windowed FFT magnitudes (scaled like vDSP's real FFT, 2× the DFT) and RMS of one channel's ring.</summary>
    float Spectrum(float[] ring, float[] magnitudes)
    {
        var re = _fft.Real;
        var im = _fft.Imag;
        double sum = 0;
        // Oldest → newest, windowed.
        for (var i = 0; i < FftSize; i++)
        {
            var s = ring[(_ringIndex + i) % FftSize];
            sum += s * s;
            re[i] = s * _window[i];
            im[i] = 0;
        }
        _fft.Forward();
        for (var k = 0; k < FftSize / 2; k++) magnitudes[k] = 2 * MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
        return (float)Math.Sqrt(sum / FftSize);
    }

    void Analyze()
    {
        const int n = FftSize;
        var leftRms = Spectrum(_rings[0], _leftMagnitudes);
        var rightRms = Spectrum(_rings[1], _rightMagnitudes);
        var rms = (leftRms + rightRms) / 2;
        var silent = rms < 1e-4f;   // nothing playing: don't let the gain blow up the noise floor

        var bands = new float[AudioLevels.BandCount];
        var leftBands = new float[AudioLevels.BandCount];
        var rightBands = new float[AudioLevels.BandCount];
        if (!silent)
        {
            for (var b = 0; b < AudioLevels.BandCount; b++)
            {
                var lo = Math.Min(_bandEdges[b], n / 2 - 1);
                var hi = Math.Min(Math.Max(_bandEdges[b + 1], lo + 1), n / 2);
                float l = 0, r = 0;
                for (var k = lo; k < hi; k++) { l += _leftMagnitudes[k]; r += _rightMagnitudes[k]; }
                l /= hi - lo;
                r /= hi - lo;
                // Automatic gain: compare with a peak that decays over ~10 s, shared by both channels
                // so they stay comparable.
                _peaks[b] = MathF.Max(MathF.Max(l, r), MathF.Max(_peaks[b] * 0.9985f, 1e-4f));
                // A square root reads closer to how loud it sounds than the raw magnitude.
                leftBands[b] = MathF.Min(1, MathF.Sqrt(l / _peaks[b]));
                rightBands[b] = MathF.Min(1, MathF.Sqrt(r / _peaks[b]));
                bands[b] = MathF.Min(1, MathF.Sqrt((l + r) / 2 / _peaks[b]));
            }
        }
        _levelPeak = MathF.Max(MathF.Max(leftRms, rightRms), MathF.Max(_levelPeak * 0.999f, 1e-4f));
        var level = silent ? 0 : MathF.Min(1, rms / _levelPeak);
        var bass = (bands[0] + bands[1] + bands[2] + bands[3]) / 4;

        // Beat: bass well above its recent average.
        _bassHistory[_bassNext] = bass;
        _bassNext = (_bassNext + 1) % _bassHistory.Length;
        _bassCount = Math.Min(_bassCount + 1, _bassHistory.Length);
        float total = 0;
        for (var i = 0; i < _bassCount; i++) total += _bassHistory[i];
        var average = total / _bassCount;

        var now = DateTime.UtcNow;
        if (bass > 0.3f && bass > average * 1.35f && (now - _lastBeat).TotalSeconds > 0.25) _lastBeat = now;

        var levels = new AudioLevels
        {
            Bands = bands,
            LeftBands = leftBands,
            RightBands = rightBands,
            LeftLevel = silent ? 0 : MathF.Min(1, leftRms / _levelPeak),
            RightLevel = silent ? 0 : MathF.Min(1, rightRms / _levelPeak),
            Level = level,
            Bass = bass,
            LastBeat = _lastBeat,
        };
        lock (_lock)
        {
            if (_active) _levels = levels;
        }
    }

    /// <summary>In-place iterative radix-2 complex FFT.</summary>
    sealed class Fft
    {
        readonly int _n;
        readonly int[] _reverse;
        readonly float[] _cos;
        readonly float[] _sin;
        public readonly float[] Real;
        public readonly float[] Imag;

        public Fft(int n)
        {
            _n = n;
            Real = new float[n];
            Imag = new float[n];
            _reverse = new int[n];
            var bits = (int)Math.Log2(n);
            for (var i = 0; i < n; i++)
            {
                var r = 0;
                for (var b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _reverse[i] = r;
            }
            _cos = new float[n / 2];
            _sin = new float[n / 2];
            for (var i = 0; i < n / 2; i++)
            {
                _cos[i] = (float)Math.Cos(-2 * Math.PI * i / n);
                _sin[i] = (float)Math.Sin(-2 * Math.PI * i / n);
            }
        }

        public void Forward()
        {
            float[] re = Real, im = Imag;
            for (var i = 0; i < _n; i++)
            {
                var j = _reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (var size = 2; size <= _n; size <<= 1)
            {
                var half = size >> 1;
                var step = _n / size;
                for (var start = 0; start < _n; start += size)
                {
                    for (var k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step], wi = _sin[k * step];
                        int a = start + k, b = a + half;
                        var tr = re[b] * wr - im[b] * wi;
                        var ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
