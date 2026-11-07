using System.Globalization;

namespace Ledly;

/// <summary>Linear-ish RGB with channels in 0…1, the working format of the LED pipeline.</summary>
public readonly record struct RGB(float R, float G, float B)
{
    public static readonly RGB Black = new(0, 0, 0);
    public static readonly RGB White = new(1, 1, 1);

    public static RGB From8(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    /// <summary>"#RRGGBB" or "RRGGBB"; null when invalid.</summary>
    public static RGB? FromHex(string? hex)
    {
        if (hex is null) return null;
        var text = hex.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return From8((int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF));
    }

    /// <summary>Hue 0…1 (wraps), saturation and value 0…1.</summary>
    public static RGB FromHSV(float hue, float saturation = 1, float value = 1)
    {
        var h = (hue - MathF.Floor(hue)) * 6;
        var i = (int)h % 6;
        var f = h - MathF.Floor(h);
        var p = value * (1 - saturation);
        var q = value * (1 - saturation * f);
        var t = value * (1 - saturation * (1 - f));
        return i switch
        {
            0 => new(value, t, p),
            1 => new(q, value, p),
            2 => new(p, value, t),
            3 => new(p, q, value),
            4 => new(t, p, value),
            _ => new(value, p, q),
        };
    }

    public string Hex => $"#{R8:X2}{G8:X2}{B8:X2}";
    public int R8 => Byte(R);
    public int G8 => Byte(G);
    public int B8 => Byte(B);

    public float MaxChannel => MathF.Max(R, MathF.Max(G, B));
    public float Luminance => 0.2126f * R + 0.7152f * G + 0.0722f * B;

    public static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);

    public static RGB operator *(RGB c, float k) => new(c.R * k, c.G * k, c.B * k);
    public static RGB operator +(RGB a, RGB b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
    public static RGB operator -(RGB a, RGB b) => new(a.R - b.R, a.G - b.G, a.B - b.B);

    public RGB Mix(RGB other, float t) => new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);

    public RGB Clamped() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    public override string ToString() => Hex;
}
