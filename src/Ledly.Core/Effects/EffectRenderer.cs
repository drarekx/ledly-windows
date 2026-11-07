namespace Ledly;

public static class EffectKindInfo
{
    /// <summary>Order shown in the app, the menus and /api/effects: related effects together.</summary>
    public static readonly EffectKind[] Ordered =
    [
        EffectKind.rainbowSwirl, EffectKind.doubleSwirl, EffectKind.atomicSwirl, EffectKind.rainbowWaves, EffectKind.rainbowMood,
        EffectKind.colorWaves, EffectKind.seaWaves,
        EffectKind.moodBlobs, EffectKind.moodBlue, EffectKind.moodGreen, EffectKind.moodRed, EffectKind.moodCold, EffectKind.moodWarm,
        EffectKind.breath, EffectKind.candle, EffectKind.fire, EffectKind.aurora, EffectKind.plasma, EffectKind.knightRider,
        EffectKind.snake, EffectKind.meteor, EffectKind.colorWipe, EffectKind.sparks,
        EffectKind.police, EffectKind.policeSolid, EffectKind.strobe, EffectKind.notify, EffectKind.christmas, EffectKind.lightClock,
        EffectKind.cinemaBrighten, EffectKind.cinemaDim, EffectKind.shutdown,
        EffectKind.musicPulse, EffectKind.musicSpectrum, EffectKind.musicWaves, EffectKind.musicMeter,
    ];

    /// <summary>Spanish name, the translation key.</summary>
    public static string TitleKey(this EffectKind kind) => kind switch
    {
        EffectKind.rainbowSwirl => "Remolino arcoíris",
        EffectKind.doubleSwirl => "Doble remolino",
        EffectKind.rainbowWaves => "Ondas arcoíris",
        EffectKind.rainbowMood => "Arcoíris suave",
        EffectKind.breath => "Respiración",
        EffectKind.candle => "Vela",
        EffectKind.fire => "Fuego",
        EffectKind.knightRider => "Coche fantástico",
        EffectKind.police => "Policía",
        EffectKind.strobe => "Estroboscopio",
        EffectKind.sparks => "Chispas",
        EffectKind.moodBlobs => "Manchas de color",
        EffectKind.seaWaves => "Olas del mar",
        EffectKind.plasma => "Plasma",
        EffectKind.snake => "Serpiente",
        EffectKind.colorWipe => "Barrido",
        EffectKind.christmas => "Navidad",
        EffectKind.lightClock => "Reloj",
        EffectKind.aurora => "Aurora",
        EffectKind.meteor => "Meteoro",
        EffectKind.musicPulse => "Pulso musical",
        EffectKind.musicSpectrum => "Ecualizador",
        EffectKind.musicWaves => "Ondas musicales",
        EffectKind.musicMeter => "Vúmetro",
        EffectKind.atomicSwirl => "Remolino atómico",
        EffectKind.colorWaves => "Ondas de color",
        EffectKind.moodBlue => "Manchas azules",
        EffectKind.moodGreen => "Manchas verdes",
        EffectKind.moodRed => "Manchas rojas",
        EffectKind.moodCold => "Manchas frías",
        EffectKind.moodWarm => "Manchas cálidas",
        EffectKind.policeSolid => "Policía (fija)",
        EffectKind.cinemaBrighten => "Cine: encender",
        EffectKind.cinemaDim => "Cine: apagar",
        EffectKind.notify => "Aviso",
        EffectKind.shutdown => "Apagado del sistema",
        _ => kind.ToString(),
    };

    /// <summary>Name in the current language.</summary>
    public static string Title(this EffectKind kind) => L.Tr(kind.TitleKey());

    /// <summary>English name, what WLED clients get.</summary>
    public static string EnglishName(this EffectKind kind) => L.TrEnglish(kind.TitleKey());

    /// <summary>WLED effect number: the position in the enum.</summary>
    public static int WledIndex(this EffectKind kind) => (int)kind;

    /// <summary>Effects that follow the system audio.</summary>
    public static bool IsMusic(this EffectKind kind) => kind is EffectKind.musicPulse or EffectKind.musicSpectrum
        or EffectKind.musicWaves or EffectKind.musicMeter;

    /// <summary>Effects that use the main color.</summary>
    public static bool UsesColor(this EffectKind kind) => kind is EffectKind.breath or EffectKind.knightRider
        or EffectKind.strobe or EffectKind.sparks or EffectKind.snake or EffectKind.colorWipe or EffectKind.meteor
        or EffectKind.colorWaves or EffectKind.notify;
}

/// <summary>A running effect. Stateful effects (fire, sparks…) keep their state between frames.</summary>
public sealed class EffectRenderer
{
    const float Pi = MathF.PI;
    static readonly EqualizerOptions DefaultEqualizer = new();

    public EffectKind Kind { get; }
    readonly LEDZone[] _zones;
    /// <summary>Angle of each LED around the screen centre, 0…1, clockwise from the top.</summary>
    readonly float[] _angles;
    readonly float[] _heat;
    readonly float[] _levels;
    readonly float[] _phases;
    double? _lastTime;
    readonly Random _rng = new();
    // Music effects
    readonly float[] _smoothBands = new float[AudioLevels.BandCount];
    float _smoothBass;
    float _smoothLevel;
    float _peakHold;
    DateTime _seenBeat = DateTime.MinValue;
    readonly List<(double Born, float Hue)> _waves = [];
    float _wavesHueShift;
    double? _firstTime;
    EqualizerRenderer? _equalizer;
    int[]? _topIndices;
    int[][]? _fireRows;

    public EffectRenderer(EffectKind kind, LEDZone[] zones)
    {
        Kind = kind;
        _zones = zones;
        var n = zones.Length;
        _angles = new float[n];
        _heat = new float[n];
        _levels = new float[n];
        _phases = new float[n];
        for (var i = 0; i < n; i++)
        {
            var a = MathF.Atan2((float)(zones[i].Position.X - 0.5), (float)(0.5 - zones[i].Position.Y)) / (2 * Pi);
            _angles[i] = a < 0 ? a + 1 : a;
            _phases[i] = Rand();
        }
    }

    float Rand() => (float)_rng.NextDouble();
    float Rand(float lo, float hi) => lo + (hi - lo) * Rand();

    /// <summary>Non-negative remainder, safe for huge (wrapped) frame counters.</summary>
    static int Mod(int a, int n) => (a % n + n) % n;

    /// <summary><paramref name="time"/> in seconds, <paramref name="speed"/> 0.1…3. <paramref name="audio"/> feeds the music effects.</summary>
    public RGB[] Render(double time, double speed, RGB color, AudioLevels? audio = null, EqualizerOptions? equalizer = null)
    {
        var n = _zones.Length;
        if (n == 0) return [];
        audio ??= AudioLevels.Silent;
        var dt = (float)Math.Min(Math.Max(time - (_lastTime ?? time), 0), 0.1);
        _lastTime = time;
        var t = (float)(time * speed);
        var sp = (float)speed;
        var output = new RGB[n];
        if (Kind.IsMusic()) Follow(audio, dt);

        switch (Kind)
        {
            case EffectKind.rainbowSwirl:
                for (var i = 0; i < n; i++) output[i] = RGB.FromHSV(_angles[i] + t * 0.15f);
                break;

            case EffectKind.doubleSwirl:
                // Two rainbows turning in opposite directions, blended.
                for (var i = 0; i < n; i++)
                {
                    var a = RGB.FromHSV(_angles[i] * 2 + t * 0.12f);
                    var b = RGB.FromHSV(-_angles[i] * 2 + t * 0.07f + 0.5f);
                    output[i] = a.Mix(b, (MathF.Sin(_angles[i] * 4 * Pi + t) + 1) / 2);
                }
                break;

            case EffectKind.rainbowWaves:
                // The whole frame breathes outwards from the centre of the top edge.
                for (var i = 0; i < n; i++)
                {
                    float dx = (float)(_zones[i].Position.X - 0.5), dy = (float)_zones[i].Position.Y;
                    output[i] = RGB.FromHSV(MathF.Sqrt(dx * dx + dy * dy) * 0.8f - t * 0.2f);
                }
                break;

            case EffectKind.rainbowMood:
                Array.Fill(output, RGB.FromHSV(t * 0.03f));
                break;

            case EffectKind.breath:
            {
                // Eased sine, never fully dark.
                var s = (MathF.Sin(t * 1.3f) + 1) / 2;
                Array.Fill(output, color * (0.06f + 0.94f * s * s));
                break;
            }

            case EffectKind.candle:
            {
                var warm = new RGB(1, 0.42f, 0.08f);
                for (var i = 0; i < n; i++)
                {
                    // Each LED drifts towards a random target: soft flicker.
                    if (Rand() < dt * 8 * sp) _phases[i] = Rand(0.35f, 1);
                    _levels[i] += (_phases[i] - _levels[i]) * MathF.Min(1, dt * 12);
                    output[i] = warm * _levels[i];
                }
                break;
            }

            case EffectKind.fire:
            {
                // 1-D heat simulation running from the bottom up both sides of the screen.
                var cooling = 0.9f * dt * sp * 6;
                for (var i = 0; i < n; i++) _heat[i] = MathF.Max(0, _heat[i] - Rand(0, cooling));
                foreach (var row in FireRows())
                {
                    // Heat rises: every cell takes some of the cell below it.
                    for (var k = row.Length - 1; k >= 1; k--)
                    {
                        var below = k >= 2 ? (_heat[row[k - 1]] + _heat[row[k - 2]] * 0.6f) / 1.6f : _heat[row[k - 1]];
                        _heat[row[k]] += (below - _heat[row[k]]) * MathF.Min(1, dt * 18 * sp);
                    }
                    if (row.Length > 0 && Rand() < dt * 20 * sp)
                        _heat[row[0]] = MathF.Min(1, _heat[row[0]] + Rand(0.5f, 1));
                }
                for (var i = 0; i < n; i++) output[i] = HeatColor(_heat[i]);
                break;
            }

            case EffectKind.knightRider:
            {
                // A bright eye bounces along the top edge (or the whole strip if there is no top).
                var top = TopIndices();
                var count = top.Length == 0 ? n : top.Length;
                var m = (float)count;
                var p = (1 - MathF.Cos(t * 1.6f)) / 2 * (m - 1);
                var width = MathF.Max(2, m * 0.08f);
                for (var k = 0; k < count; k++)
                {
                    var i = top.Length == 0 ? k : top[k];
                    output[i] = color * MathF.Max(0, 1 - MathF.Abs(k - p) / width);
                }
                break;
            }

            case EffectKind.police:
            {
                RGB red = new(1, 0, 0), blue = new(0, 0.1f, 1);
                var phase = (int)(t * 4) % 4;
                var on = phase == 0 || phase == 2;
                for (var i = 0; i < n; i++)
                {
                    if (_zones[i].Position.X < 0.5) output[i] = on && phase == 0 ? red : (phase == 1 ? red * 0.15f : RGB.Black);
                    else output[i] = on && phase == 2 ? blue : (phase == 3 ? blue * 0.15f : RGB.Black);
                }
                break;
            }

            case EffectKind.strobe:
                Array.Fill(output, t * 8 % 1 < 0.2f ? color : RGB.Black);
                break;

            case EffectKind.sparks:
                for (var i = 0; i < n; i++)
                {
                    _levels[i] = MathF.Max(0, _levels[i] - dt * 3);
                    if (Rand() < dt * 0.6f * sp) _levels[i] = 1;
                    var glow = _levels[i] * _levels[i];
                    output[i] = color.Mix(RGB.White, glow) * glow;
                }
                break;

            case EffectKind.moodBlobs:
            case EffectKind.moodBlue:
            case EffectKind.moodGreen:
            case EffectKind.moodRed:
            case EffectKind.moodCold:
            case EffectKind.moodWarm:
                MoodBlobs(output, t);
                break;

            case EffectKind.seaWaves:
                for (var i = 0; i < n; i++)
                {
                    var a = _angles[i] * 2 * Pi;
                    var w = (MathF.Sin(a * 3 + t * 1.2f) + MathF.Sin(a * 5 - t * 0.7f) * 0.5f + 1.5f) / 3;
                    output[i] = new RGB(0, 0.25f + 0.45f * w, 0.55f + 0.45f * w) * (0.3f + 0.7f * w);
                }
                break;

            case EffectKind.plasma:
                for (var i = 0; i < n; i++)
                {
                    float x = (float)_zones[i].Position.X * 6, y = (float)_zones[i].Position.Y * 6;
                    var v = MathF.Sin(x + t) + MathF.Sin(y * 1.3f - t * 0.8f) + MathF.Sin((x + y) * 0.7f + t * 0.6f);
                    output[i] = RGB.FromHSV(v / 6 + t * 0.05f);
                }
                break;

            case EffectKind.snake:
            {
                var length = Math.Max(4, n / 8);
                var head = Mod((int)(t * 30), n);
                for (var k = 0; k < length; k++)
                {
                    var l = 1 - (float)k / length;
                    output[Mod(head - k, n)] = color * (l * l);
                }
                break;
            }

            case EffectKind.colorWipe:
            {
                // Fills the strip with one hue, then the next one wipes over it.
                var cycle = t * 0.25f;
                var progress = cycle - MathF.Floor(cycle);
                var even = (int)cycle % 2 == 0;
                var current = color.MaxChannel > 0 && even ? color : RGB.FromHSV(MathF.Floor(cycle) * 0.23f);
                var previous = even ? RGB.FromHSV((MathF.Floor(cycle) - 1) * 0.23f) : color;
                var edge = (int)(progress * n);
                for (var i = 0; i < n; i++) output[i] = i <= edge ? current : previous;
                break;
            }

            case EffectKind.christmas:
            {
                RGB red = new(1, 0, 0), green = new(0, 0.8f, 0.1f), white = new(1, 0.85f, 0.6f);
                var shift = (int)(t * 2);
                for (var i = 0; i < n; i++)
                {
                    var k = Mod(i / 3 + shift, 3);
                    output[i] = k == 0 ? red : (k == 1 ? green : white * 0.6f);
                }
                break;
            }

            case EffectKind.lightClock:
            {
                // Hour, minute and second hands drawn on the frame of the screen.
                var now = DateTime.Now;
                var s = (now.Second + (now.Ticks % TimeSpan.TicksPerSecond) / (float)TimeSpan.TicksPerSecond) / 60;
                var m = (now.Minute + s) / 60;
                var h = (now.Hour % 12 + m) / 12;
                for (var i = 0; i < n; i++)
                {
                    output[i] = (new RGB(1, 0.1f, 0.05f) * Hand(_angles[i], h, 0.05f)
                        + new RGB(0.1f, 1, 0.2f) * Hand(_angles[i], m, 0.03f)
                        + new RGB(0.15f, 0.3f, 1) * Hand(_angles[i], s, 0.02f)
                        + new RGB(0.03f, 0.03f, 0.03f)).Clamped();
                }
                break;
            }

            case EffectKind.aurora:
                for (var i = 0; i < n; i++)
                {
                    var a = _angles[i] * 2 * Pi;
                    var v = (MathF.Sin(a * 2 + t * 0.4f) + MathF.Sin(a * 3.7f - t * 0.25f) + 2) / 4;
                    var hue = 0.33f + 0.25f * v + 0.05f * MathF.Sin(t * 0.1f);
                    output[i] = RGB.FromHSV(hue, 0.85f, 0.15f + 0.85f * v * v);
                }
                break;

            case EffectKind.meteor:
            {
                for (var i = 0; i < n; i++)
                {
                    // Random decay leaves an uneven trail.
                    if (Rand() < 0.6f) _levels[i] *= MathF.Max(0, 1 - dt * 4);
                }
                var head = Mod((int)(t * 40), n);
                for (var k = 0; k < 3; k++) _levels[(head + k) % n] = 1;
                for (var i = 0; i < n; i++) output[i] = color.Mix(RGB.White, _levels[i] > 0.95f ? 0.6f : 0) * _levels[i];
                break;
            }

            case EffectKind.atomicSwirl:
                // Three narrow white-blue arcs spinning round the frame on a dark base.
                for (var i = 0; i < n; i++)
                {
                    var a = (_angles[i] - t * 0.04f) * 3 * 2 * Pi;
                    var v = MathF.Pow(MathF.Max(0, MathF.Cos(a)), 8);
                    var tint = new RGB(0.55f, 0.75f, 1).Mix(RGB.White, (MathF.Sin(t * 0.5f + _angles[i] * 6) + 1) / 2);
                    output[i] = tint * v + new RGB(0, 0.01f, 0.04f);
                }
                break;

            case EffectKind.colorWaves:
                for (var i = 0; i < n; i++)
                {
                    var v = (MathF.Sin((_angles[i] * 3 - t * 0.3f) * 2 * Pi) + 1) / 2;
                    output[i] = color * (0.12f + 0.88f * v * v);
                }
                break;

            case EffectKind.policeSolid:
            {
                // Halves swap red and blue, no flashing.
                var swap = (int)(t * 2) % 2 == 0;
                RGB red = new(1, 0, 0), blue = new(0, 0.1f, 1);
                for (var i = 0; i < n; i++) output[i] = (_zones[i].Position.X < 0.5) == swap ? red : blue;
                break;
            }

            case EffectKind.cinemaBrighten:
            case EffectKind.cinemaDim:
            {
                // Five-second fade to (or from) warm white, then it stays.
                var warm = new RGB(1, 0.62f, 0.3f);
                var p = MathF.Min(1, Elapsed(time) * sp / 5);
                var eased = p * p * (3 - 2 * p);
                Array.Fill(output, warm * (Kind == EffectKind.cinemaBrighten ? eased : 1 - eased));
                break;
            }

            case EffectKind.notify:
            {
                // Three quick flashes, then a pause; repeats every 3 s.
                var e = Elapsed(time) * sp % 3;
                Array.Fill(output, e < 1.2f && e % 0.4f < 0.2f ? color : RGB.Black);
                break;
            }

            case EffectKind.shutdown:
            {
                // Red sweep round the strip, two blinks, then off.
                var e = Elapsed(time) * sp;
                var red = new RGB(1, 0, 0);
                for (var i = 0; i < n; i++)
                {
                    if (e < 1.5f) output[i] = (float)i / n < e / 1.5f ? red : RGB.Black;
                    else if (e < 2.5f) output[i] = (e - 1.5f) % 0.5f < 0.25f ? red : RGB.Black;
                    else if (e < 3.5f) output[i] = red * (3.5f - e);
                }
                break;
            }

            case EffectKind.musicPulse:
            {
                // The whole frame breathes with the bass; each beat nudges the hue.
                var hue = (float)time * 0.02f * sp + _wavesHueShift;
                var l = 0.05f + 0.95f * _smoothBass * _smoothBass;
                for (var i = 0; i < n; i++) output[i] = RGB.FromHSV(hue + _angles[i] * 0.15f, 1, l);
                break;
            }

            case EffectKind.musicSpectrum:
                _equalizer ??= new EqualizerRenderer(_zones);
                output = _equalizer.Render(audio, equalizer ?? DefaultEqualizer, time, dt, speed, color);
                break;

            case EffectKind.musicWaves:
            {
                // Every beat sends a ring of color from the bottom centre over the whole frame.
                var travel = 0.9f * sp;
                _waves.RemoveAll(w => (float)(time - w.Born) * travel > 1.6f);
                var background = RGB.FromHSV(_wavesHueShift) * (0.04f + 0.1f * _smoothLevel);
                for (var i = 0; i < n; i++)
                {
                    var d = DistanceFromBottom(i);
                    var acc = RGB.Black;
                    foreach (var wave in _waves)
                    {
                        var front = (float)(time - wave.Born) * travel;
                        var k = MathF.Max(0, 1 - MathF.Abs(d - front) / 0.18f) * MathF.Max(0, 1 - front / 1.6f);
                        acc += RGB.FromHSV(wave.Hue) * k;
                    }
                    output[i] = (acc + background).Clamped();
                }
                break;
            }

            case EffectKind.musicMeter:
                // VU meter up both sides, green → yellow → red, with a falling peak dot.
                for (var i = 0; i < n; i++)
                {
                    var u = Height(i);
                    var b = RGB.FromHSV(0.33f - 0.33f * u);
                    if (u <= _smoothLevel) output[i] = b;
                    else if (MathF.Abs(u - _peakHold) < 0.04f) output[i] = RGB.White * 0.7f;
                    else output[i] = b * 0.03f;
                }
                break;
        }
        return output;
    }

    static float Hand(float angle, float pos, float width)
    {
        var d = MathF.Abs(angle - pos);
        d = MathF.Min(d, 1 - d);
        return MathF.Max(0, 1 - d / width);
    }

    /// <summary>Seconds since this effect started (one-shot effects like the cinema fades).</summary>
    float Elapsed(double time)
    {
        _firstTime ??= time;
        return (float)(time - _firstTime.Value);
    }

    /// <summary>Soft colored blobs drifting round the frame.</summary>
    void MoodBlobs(RGB[] output, float t)
    {
        var (blobs, saturation) = Kind switch
        {
            EffectKind.moodBlobs => (3, 1f),
            EffectKind.moodBlue or EffectKind.moodGreen => (5, 0.9f),
            EffectKind.moodCold => (5, 0.8f),
            _ => (5, 0.95f),
        };
        Span<RGB> tints = stackalloc RGB[blobs];
        Span<float> centers = stackalloc float[blobs];
        for (var b = 0; b < blobs; b++)
        {
            var wobble = MathF.Sin(t * 0.05f + b * 1.3f);
            var hue = Kind switch
            {
                EffectKind.moodBlue => 0.6f + 0.07f * wobble,
                EffectKind.moodGreen => 0.33f + 0.07f * wobble,
                EffectKind.moodRed => 0.99f + 0.04f * wobble,
                EffectKind.moodCold => 0.58f + 0.12f * wobble,
                EffectKind.moodWarm => 0.05f + 0.05f * wobble,
                _ => t * 0.01f + b * 0.33f,
            };
            tints[b] = RGB.FromHSV(hue, saturation);
            centers[b] = ((float)b / blobs + t * 0.02f * (b % 3 + 1)) % 1;
        }
        for (var i = 0; i < output.Length; i++)
        {
            var acc = RGB.Black;
            for (var b = 0; b < blobs; b++)
            {
                var d = MathF.Abs(_angles[i] - centers[b]);
                d = MathF.Min(d, 1 - d);
                var w = MathF.Max(0, 1 - d * blobs * 1.3f);
                acc += tints[b] * (w * w);
            }
            output[i] = acc.Clamped();
        }
    }

    /// <summary>Smooths the raw levels: rises at once, falls over ~150 ms (bands) or ~250 ms (bass, level).</summary>
    void Follow(AudioLevels audio, float dt)
    {
        float Approach(float current, float target, float fall) =>
            target > current ? target : current + (target - current) * MathF.Min(1, dt / fall);
        for (var b = 0; b < AudioLevels.BandCount; b++)
            _smoothBands[b] = Approach(_smoothBands[b], audio.Bands[b], 0.15f);
        _smoothBass = Approach(_smoothBass, audio.Bass, 0.25f);
        _smoothLevel = Approach(_smoothLevel, audio.Level, 0.25f);
        _peakHold = _smoothLevel > _peakHold ? _smoothLevel : MathF.Max(0, _peakHold - dt * 0.4f);
        if (audio.LastBeat > _seenBeat)
        {
            _seenBeat = audio.LastBeat;
            _wavesHueShift += 0.13f;
            _waves.Add((_lastTime ?? 0, _wavesHueShift));
            if (_waves.Count > 8) _waves.RemoveAt(0);
        }
    }

    /// <summary>0 at the bottom of the screen, 1 at the top; the top edge counts as the top.</summary>
    float Height(int i) => _zones[i].Side switch
    {
        ScreenSide.top => 1,
        ScreenSide.bottom => 0,
        _ => (float)(1 - _zones[i].Position.Y),
    };

    float DistanceFromBottom(int i)
    {
        float dx = (float)(_zones[i].Position.X - 0.5), dy = (float)(1 - _zones[i].Position.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>LEDs of one side; top/bottom left → right, left/right bottom → top.</summary>
    int[] Side(ScreenSide side)
    {
        var list = new List<int>();
        for (var i = 0; i < _zones.Length; i++) if (_zones[i].Side == side) list.Add(i);
        if (side is ScreenSide.left or ScreenSide.right)
            list.Sort((a, b) => _zones[b].Position.Y.CompareTo(_zones[a].Position.Y));
        else
            list.Sort((a, b) => _zones[a].Position.X.CompareTo(_zones[b].Position.X));
        return list.ToArray();
    }

    int[] TopIndices() => _topIndices ??= Side(ScreenSide.top);

    /// <summary>
    /// Columns of LEDs for the fire: left and right edges, bottom to top, plus the top edge
    /// split from both ends towards the middle.
    /// </summary>
    int[][] FireRows()
    {
        if (_fireRows is not null) return _fireRows;
        var top = TopIndices();
        var half = top.Length / 2;
        var topRight = top[half..];
        Array.Reverse(topRight);
        List<int[]> rows = [[.. Side(ScreenSide.left), .. top[..half]], [.. Side(ScreenSide.right), .. topRight]];
        for (var i = 0; i < _zones.Length; i++) if (_zones[i].Side == ScreenSide.bottom) rows.Add([i]);
        rows.RemoveAll(r => r.Length == 0);
        return _fireRows = rows.ToArray();
    }

    static RGB HeatColor(float h)
    {
        var x = Math.Clamp(h, 0, 1);
        if (x < 0.33f) return new(x * 3, 0, 0);
        if (x < 0.66f) return new(1, (x - 0.33f) * 3 * 0.6f, 0);
        return new(1, 0.6f + (x - 0.66f) * 1.2f, (x - 0.66f) * 1.5f);
    }
}
