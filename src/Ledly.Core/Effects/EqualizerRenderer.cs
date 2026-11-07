namespace Ledly;

/// <summary>Draws the configurable equalizer. Keeps its own smoothing so bars fall gently.</summary>
public sealed class EqualizerRenderer
{
    const int Bands = AudioLevels.BandCount;

    readonly LEDZone[] _zones;
    /// <summary>Position of each LED round the frame: 0 at the bottom centre, growing clockwise (seen from the front).</summary>
    readonly float[] _perimeter;
    // Index 0 mono, 1 left, 2 right.
    readonly float[][] _bands = [new float[Bands], new float[Bands], new float[Bands]];
    readonly float[][] _bandPeaks = [new float[Bands], new float[Bands], new float[Bands]];
    readonly float[] _levels = new float[3];
    readonly float[] _levelPeaks = new float[3];

    public EqualizerRenderer(LEDZone[] zones)
    {
        _zones = zones;
        _perimeter = new float[zones.Length];
        // Walk the frame clockwise from the top-left corner, sides weighted 16:9.
        for (var i = 0; i < zones.Length; i++)
        {
            float x = (float)zones[i].Position.X, y = (float)zones[i].Position.Y;
            var raw = zones[i].Side switch
            {
                ScreenSide.top => 16 * x,
                ScreenSide.right => 16 + 9 * y,
                ScreenSide.bottom => 25 + 16 * (1 - x),
                _ => 41 + 9 * (1 - y),
            };
            var t = (raw - 33) / 50;   // bottom centre = 33
            _perimeter[i] = t - MathF.Floor(t);
        }
    }

    public RGB[] Render(AudioLevels audio, EqualizerOptions options, double time, float dt, double speed, RGB color)
    {
        Follow(audio, dt, (float)options.Sensitivity);
        var output = new RGB[_zones.Length];
        for (var i = 0; i < output.Length; i++)
        {
            var (p, channel) = Axis(i, options.Layout);
            output[i] = Draw(p, channel, options, time, speed, color);
        }
        return output;
    }

    /// <summary>Position on the axis (0 = start, bass) and channel (0 mono, 1 left, 2 right) of LED <paramref name="i"/>.</summary>
    (float, int) Axis(int i, EqualizerLayout layout)
    {
        var t = _perimeter[i];
        // 0 at the bottom centre, 1 at the top centre, the same on both halves.
        var fromBottom = t < 0.5f ? t * 2 : (1 - t) * 2;
        switch (layout)
        {
            case EqualizerLayout.fromTop: return (1 - fromBottom, 0);
            case EqualizerLayout.stereo: return (fromBottom, t < 0.5f ? 1 : 2);   // first half (left side seen from the front) = left
            case EqualizerLayout.ring: return (t, 0);
            case EqualizerLayout.quad:
            {
                var q = t % 0.5f * 2;   // 0…1 per half
                return (q < 0.5f ? q * 2 : (1 - q) * 2, 0);
            }
            default: return (fromBottom, 0);
        }
    }

    float Band(float[] b, float x)
    {
        var f = Math.Clamp(x, 0, 1) * (Bands - 1);
        var lo = (int)f;
        var hi = Math.Min(lo + 1, Bands - 1);
        return b[lo] + (b[hi] - b[lo]) * (f - lo);
    }

    RGB Draw(float p, int channel, EqualizerOptions options, double time, double speed, RGB color)
    {
        var b = _bands[channel];
        switch (options.Style)
        {
            case EqualizerStyle.bars:
            {
                // As many bars as fit with ~3 LEDs each would be ideal; 8 keeps them readable on short strips.
                const float count = 8;
                var k = MathF.Min(MathF.Floor(p * count), count - 1);
                var inBar = p * count - k;                  // 0…1 inside the bar
                var centre = (k + 0.5f) / count;
                var v = Band(b, centre);
                var peak = _bandPeaks[channel][(int)(centre * (Bands - 1))];
                if (inBar <= v) return Paint(centre, v, options, time, speed, color);
                if (MathF.Abs(inBar - peak) < 0.12f) return RGB.White * 0.6f;
                return RGB.Black;
            }

            case EqualizerStyle.level:
            {
                var v = _levels[channel];
                if (p <= v) return Paint(p, p, options, time, speed, color);
                if (MathF.Abs(p - _levelPeaks[channel]) < 0.035f) return RGB.White * 0.7f;
                return Paint(p, p, options, time, speed, color) * 0.03f;
            }

            case EqualizerStyle.flashes:
            {
                var v = Band(b, p);
                var f = MathF.Max(0, (v - 0.55f) / 0.45f);
                var k = f * f;
                return Paint(p, v, options, time, speed, color).Mix(RGB.White, k * 0.5f) * k;
            }

            default:   // spectrum: each frequency glows as loud as it is
            {
                var v = Band(b, p);
                return Paint(p, v, options, time, speed, color) * (v * v);
            }
        }
    }

    /// <summary>Color for axis position <paramref name="p"/> and intensity <paramref name="v"/>.</summary>
    static RGB Paint(float p, float v, EqualizerOptions options, double time, double speed, RGB color)
    {
        switch (options.Colors)
        {
            case EqualizerColors.@fixed: return color;
            case EqualizerColors.heat:
            {
                var x = Math.Clamp(v, 0, 1);
                if (x < 0.25f) return new(0.1f * x * 4, 0, 0.6f * x * 4);
                if (x < 0.6f) return new(0.1f + 0.9f * (x - 0.25f) / 0.35f, 0, 0.6f * (1 - (x - 0.25f) / 0.35f));
                if (x < 0.9f) return new(1, 0.8f * (x - 0.6f) / 0.3f, 0);
                return new(1, 0.8f + 0.2f * (x - 0.9f) / 0.1f, (x - 0.9f) / 0.1f * 0.7f);
            }
            case EqualizerColors.traffic: return RGB.FromHSV(0.33f - 0.33f * Math.Clamp(p, 0, 1));
            case EqualizerColors.cycle: return RGB.FromHSV(p * 0.35f + (float)(time * 0.06 * speed));
            default: return RGB.FromHSV(p * 0.8f);   // rainbow
        }
    }

    /// <summary>Rises at once, falls over ~150 ms; peaks hold and drop slowly.</summary>
    void Follow(AudioLevels audio, float dt, float gain)
    {
        for (var c = 0; c < 3; c++)
        {
            var source = c == 0 ? audio.Bands : c == 1 ? audio.LeftBands : audio.RightBands;
            var sourceLevel = c == 0 ? audio.Level : c == 1 ? audio.LeftLevel : audio.RightLevel;
            var bands = _bands[c];
            var peaks = _bandPeaks[c];
            for (var k = 0; k < Bands; k++)
            {
                var target = MathF.Min(1, source[k] * gain);
                bands[k] = target > bands[k] ? target : bands[k] + (target - bands[k]) * MathF.Min(1, dt / 0.15f);
                peaks[k] = bands[k] > peaks[k] ? bands[k] : MathF.Max(0, peaks[k] - dt * 0.5f);
            }
            var level = MathF.Min(1, sourceLevel * gain);
            _levels[c] = level > _levels[c] ? level : _levels[c] + (level - _levels[c]) * MathF.Min(1, dt / 0.25f);
            _levelPeaks[c] = _levels[c] > _levelPeaks[c] ? _levels[c] : MathF.Max(0, _levelPeaks[c] - dt * 0.4f);
        }
    }
}
