namespace Ledly;

/// <summary>Color correction applied to every frame just before it goes to the strip.</summary>
public struct ColorAdjustments
{
    /// <summary>0…1, multiplies the output.</summary>
    public float Brightness = 1;
    /// <summary>Output = input ^ gamma. 1 = untouched; higher darkens mid tones.</summary>
    public float Gamma = 1.5f;
    /// <summary>0 = grey, 1 = untouched, 2 = double.</summary>
    public float Saturation = 1;
    /// <summary>White point in Kelvin; 6600 = neutral.</summary>
    public float Temperature = 6600;
    /// <summary>Minimum output (0…0.2) so the strip never goes fully dark in screen mode.</summary>
    public float Backlight = 0;
    public float RedGain = 1, GreenGain = 1, BlueGain = 1;
    /// <summary>Advanced calibration (corners of the RGB cube); null when off.</summary>
    public PreparedCalibration? Calibration = null;
    /// <summary>Precomputed white-balance multipliers.</summary>
    public RGB WhiteBalance { get; private set; } = RGB.White;

    public ColorAdjustments() { }

    public void Prepare() => WhiteBalance = KelvinToRGB(Temperature);

    public readonly RGB Apply(RGB input, bool useBacklight)
    {
        var c = input.Clamped();
        // Calibration first: it describes the strip, so the rest works on corrected colors.
        if (Calibration is not null) c = Calibration.Apply(c);
        if (Saturation != 1)
        {
            var grey = c.Luminance;
            c = new RGB(grey + (c.R - grey) * Saturation, grey + (c.G - grey) * Saturation, grey + (c.B - grey) * Saturation).Clamped();
        }
        if (Gamma != 1) c = new RGB(MathF.Pow(c.R, Gamma), MathF.Pow(c.G, Gamma), MathF.Pow(c.B, Gamma));
        c = new RGB(c.R * WhiteBalance.R * RedGain, c.G * WhiteBalance.G * GreenGain, c.B * WhiteBalance.B * BlueGain);
        if (useBacklight && Backlight > 0)
        {
            // Lift towards a dim version of the same hue, warm white if the input is black.
            var floor = Backlight;
            if (c.MaxChannel < floor)
            {
                var tint = c.MaxChannel > 0.001f ? c * (1 / c.MaxChannel) : new RGB(1, 0.8f, 0.6f);
                c = tint * floor;
            }
        }
        return (c * Brightness).Clamped();
    }

    /// <summary>Approximate white point of a black body (Tanner Helland's fit), normalised so the brightest channel is 1.</summary>
    public static RGB KelvinToRGB(float kelvin)
    {
        var t = Math.Clamp(kelvin, 1000, 40000) / 100;
        float r, g, b;
        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861f * MathF.Log(t) - 161.1195681661f;
            b = t <= 19 ? 0 : 138.5177312231f * MathF.Log(t - 10) - 305.0447927307f;
        }
        else
        {
            r = 329.698727446f * MathF.Pow(t - 60, -0.1332047592f);
            g = 288.1221695283f * MathF.Pow(t - 60, -0.0755148492f);
            b = 255;
        }
        var c = new RGB(r / 255, g / 255, b / 255).Clamped();
        var peak = MathF.Max(c.MaxChannel, 0.001f);
        return c * (1 / peak);
    }
}

/// <summary>Corners of the RGB cube, the colors the advanced calibration adjusts. Names are the JSON keys.</summary>
public enum CalibrationCorner { red, green, blue, cyan, magenta, yellow, white, black }

public static class CalibrationCornerExt
{
    public static RGB Input(this CalibrationCorner c) => c switch
    {
        CalibrationCorner.red => new(1, 0, 0),
        CalibrationCorner.green => new(0, 1, 0),
        CalibrationCorner.blue => new(0, 0, 1),
        CalibrationCorner.cyan => new(0, 1, 1),
        CalibrationCorner.magenta => new(1, 0, 1),
        CalibrationCorner.yellow => new(1, 1, 0),
        CalibrationCorner.white => new(1, 1, 1),
        _ => new(0, 0, 0),
    };

    /// <summary>Spanish name, the translation key.</summary>
    public static string TitleKey(this CalibrationCorner c) => c switch
    {
        CalibrationCorner.red => "Rojo",
        CalibrationCorner.green => "Verde",
        CalibrationCorner.blue => "Azul",
        CalibrationCorner.cyan => "Cian",
        CalibrationCorner.magenta => "Magenta",
        CalibrationCorner.yellow => "Amarillo",
        CalibrationCorner.white => "Blanco",
        _ => "Negro",
    };
}

/// <summary>
/// Advanced calibration: the color the strip should show for each corner of the RGB cube.
/// Colors in between are interpolated trilinearly. Targets are stored as "#RRGGBB" by corner name;
/// a missing corner is untouched.
/// </summary>
public sealed class ColorCalibration
{
    public bool Enabled { get; set; }
    public Dictionary<string, string> Targets { get; set; } = new();

    public RGB Target(CalibrationCorner corner) =>
        Targets.TryGetValue(corner.ToString(), out var hex) && RGB.FromHex(hex) is { } c ? c : corner.Input();

    public void SetTarget(RGB color, CalibrationCorner corner)
    {
        if (color.Hex == corner.Input().Hex) Targets.Remove(corner.ToString());
        else Targets[corner.ToString()] = color.Hex;
    }

    public bool IsIdentity => Targets.Count == 0;

    public PreparedCalibration? Prepared()
    {
        if (!Enabled || IsIdentity) return null;
        CalibrationCorner[] order = [CalibrationCorner.black, CalibrationCorner.red, CalibrationCorner.green, CalibrationCorner.blue,
            CalibrationCorner.cyan, CalibrationCorner.magenta, CalibrationCorner.yellow, CalibrationCorner.white];
        return new PreparedCalibration(order.Select(Target).ToArray());
    }

    public ColorCalibration Clone() => new() { Enabled = Enabled, Targets = new(Targets) };
}

/// <summary>Precomputed corners in the order black, red, green, blue, cyan, magenta, yellow, white.</summary>
public sealed class PreparedCalibration(RGB[] corners)
{
    public RGB[] Corners { get; } = corners;

    /// <summary>Trilinear interpolation between the eight corners.</summary>
    public RGB Apply(RGB c)
    {
        float r = c.R, g = c.G, b = c.B, nr = 1 - r, ng = 1 - g, nb = 1 - b;
        Span<float> w = stackalloc float[8]
        {
            nr * ng * nb, r * ng * nb, nr * g * nb, nr * ng * b,
            nr * g * b, r * ng * b, r * g * nb, r * g * b,
        };
        var o = RGB.Black;
        for (var k = 0; k < 8; k++) o += Corners[k] * w[k];
        return o.Clamped();
    }
}
