namespace Ledly;

/// <summary>How colors move towards their target in screen mode. Names are the JSON values.</summary>
public enum SmoothingKind
{
    /// <summary>Each frame closes a fixed fraction of the gap: reacts at once, eases out.</summary>
    exponential,
    /// <summary>Critically damped spring: eases in and out, so sudden cuts look like a fade.</summary>
    spring,
}

/// <summary>Smooths LED colors over time and turns them into the 8-bit values sent to the strip.</summary>
public sealed class Smoother
{
    RGB[] _current = [];
    RGB[] _velocity = [];
    /// <summary>Last 8-bit values sent, per channel (r, g, b, r, g, b…).</summary>
    int[] _sent = [];

    public RGB[] Current => _current;

    /// <summary>Moves <see cref="Current"/> towards <paramref name="target"/>. <paramref name="tau"/> is the time constant in seconds.</summary>
    public void Step(RGB[] target, double dt, double tau, SmoothingKind kind)
    {
        var n = target.Length;
        if (_current.Length != n)
        {
            _current = (RGB[])target.Clone();
            _velocity = new RGB[n];
            return;
        }
        if (kind == SmoothingKind.exponential)
        {
            var alpha = (float)(1 - Math.Exp(-dt / tau));
            for (var i = 0; i < n; i++) _current[i] = _current[i].Mix(target[i], alpha);
        }
        else
        {
            // x'' = ω²(target − x) − 2ω x'. ω = 2/τ gives about the same settle time as the exponential.
            var omega = 2 / tau;
            var steps = Math.Max(1, (int)Math.Ceiling(dt * omega / 0.3));   // keep ω·h small for stability
            var h = (float)(dt / steps);
            var w = (float)omega;
            for (var s = 0; s < steps; s++)
            {
                for (var i = 0; i < n; i++)
                {
                    var accel = (target[i] - _current[i]) * (w * w) + _velocity[i] * (-2 * w);
                    _velocity[i] += accel * h;
                    _current[i] += _velocity[i] * h;
                }
            }
        }
        // Snap when close enough, so fades really end (and the spring can't hover).
        for (var i = 0; i < n; i++)
        {
            var c = _current[i];
            var t = target[i];
            if (MathF.Abs(c.R - t.R) < 0.0015f && MathF.Abs(c.G - t.G) < 0.0015f && MathF.Abs(c.B - t.B) < 0.0015f)
            {
                _current[i] = t;
                _velocity[i] = RGB.Black;
            }
        }
    }

    /// <summary>Jumps straight to <paramref name="colors"/>.</summary>
    public void Reset(RGB[] colors)
    {
        _current = (RGB[])colors.Clone();
        _velocity = new RGB[colors.Length];
    }

    /// <summary>
    /// 8-bit output. With anti-flicker, a channel only changes once the new value is at least a whole step
    /// away from the one already showing. <paramref name="scale"/> dims the whole frame (power limit).
    /// </summary>
    public RGB[] Quantized(bool antiFlicker, float scale = 1)
    {
        var n = _current.Length;
        if (_sent.Length != n * 3) _sent = Enumerable.Repeat(-1, n * 3).ToArray();
        var output = new RGB[n];
        Span<int> bytes = stackalloc int[3];
        for (var i = 0; i < n; i++)
        {
            var c = (_current[i] * scale).Clamped();
            for (var k = 0; k < 3; k++)
            {
                var v = k == 0 ? c.R : k == 1 ? c.G : c.B;
                var exact = v * 255;
                var previous = _sent[i * 3 + k];
                bytes[k] = antiFlicker && previous >= 0 && MathF.Abs(exact - previous) < 1
                    ? previous
                    : (int)MathF.Round(exact, MidpointRounding.AwayFromZero);
                _sent[i * 3 + k] = bytes[k];
            }
            output[i] = RGB.From8(bytes[0], bytes[1], bytes[2]);
        }
        return output;
    }
}

/// <summary>Rough WS2812B current draw, to keep the strip within what the supply (or USB) can give.</summary>
public static class PowerModel
{
    public const float MilliampsPerChannel = 20;
    public const float IdleMilliamps = 1;

    public static float Milliamps(RGB[] colors)
    {
        var sum = colors.Length * IdleMilliamps;
        foreach (var raw in colors)
        {
            var c = raw.Clamped();
            sum += (c.R + c.G + c.B) * MilliampsPerChannel;
        }
        return sum;
    }

    /// <summary>Factor (≤ 1) that brings <paramref name="colors"/> down to <paramref name="limit"/> mA.</summary>
    public static float Scale(RGB[] colors, float limit)
    {
        var idle = colors.Length * IdleMilliamps;
        var lit = Milliamps(colors) - idle;
        if (lit <= 0 || idle + lit <= limit) return 1;
        return MathF.Max(0, (limit - idle) / lit);
    }
}
