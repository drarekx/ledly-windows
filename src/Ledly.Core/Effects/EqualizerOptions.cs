namespace Ledly;

/// <summary>Where the equalizer's axis runs around the screen. Names are the JSON values.</summary>
public enum EqualizerLayout { fromBottom, fromTop, stereo, ring, quad }

/// <summary>How levels are drawn along the axis.</summary>
public enum EqualizerStyle { spectrum, bars, level, flashes }

public enum EqualizerColors { rainbow, @fixed, heat, traffic, cycle }

public sealed record EqualizerOptions
{
    public EqualizerLayout Layout { get; init; } = EqualizerLayout.fromBottom;
    public EqualizerStyle Style { get; init; } = EqualizerStyle.spectrum;
    public EqualizerColors Colors { get; init; } = EqualizerColors.rainbow;
    /// <summary>Multiplies the levels: 0.5 = calmer, 2 = everything lights up.</summary>
    public double Sensitivity { get; init; } = 1;

    /// <summary>Quick combinations. TitleKey is the Spanish name (translation key).</summary>
    public sealed record Preset(string Id, string TitleKey, EqualizerOptions Options);

    public static readonly Preset[] Presets =
    [
        new("classic", "Clásico", new() { Layout = EqualizerLayout.fromBottom, Style = EqualizerStyle.bars, Colors = EqualizerColors.traffic }),
        new("aurora", "Aurora", new() { Layout = EqualizerLayout.fromBottom, Style = EqualizerStyle.spectrum, Colors = EqualizerColors.rainbow }),
        new("stereo", "Estéreo", new() { Layout = EqualizerLayout.stereo, Style = EqualizerStyle.level, Colors = EqualizerColors.traffic }),
        new("fire", "Fuego", new() { Layout = EqualizerLayout.fromBottom, Style = EqualizerStyle.spectrum, Colors = EqualizerColors.heat }),
        new("rain", "Lluvia", new() { Layout = EqualizerLayout.fromTop, Style = EqualizerStyle.bars, Colors = EqualizerColors.cycle }),
        new("halo", "Halo", new() { Layout = EqualizerLayout.ring, Style = EqualizerStyle.spectrum, Colors = EqualizerColors.cycle }),
        new("club", "Club", new() { Layout = EqualizerLayout.quad, Style = EqualizerStyle.flashes, Colors = EqualizerColors.rainbow }),
        new("mono", "Monocolor", new() { Layout = EqualizerLayout.stereo, Style = EqualizerStyle.bars, Colors = EqualizerColors.@fixed }),
    ];
}
