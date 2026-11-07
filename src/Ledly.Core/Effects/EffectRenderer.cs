namespace Ledly;

// CONTRACT STUB — replaced by the effects port. Keep these public signatures.

public static class EffectKindInfo
{
    /// <summary>Order shown in the app, the menus and /api/effects.</summary>
    public static readonly EffectKind[] Ordered = Enum.GetValues<EffectKind>();
    /// <summary>Spanish name, the translation key.</summary>
    public static string TitleKey(this EffectKind kind) => throw new NotImplementedException();
    public static bool IsMusic(this EffectKind kind) => throw new NotImplementedException();
}

/// <summary>Procedural effects over each LED's position.</summary>
public sealed class EffectRenderer(EffectKind kind, LEDZone[] zones)
{
    public EffectKind Kind { get; } = kind;
    public RGB[] Render(double time, double speed, RGB color, AudioLevels? audio = null, EqualizerOptions? equalizer = null)
        => throw new NotImplementedException();
}
