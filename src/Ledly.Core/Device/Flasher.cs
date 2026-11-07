namespace Ledly;

// CONTRACT STUB — replaced by the device port. Keep these public signatures.

public static class Firmware
{
    public static readonly int[] Pins = Enumerable.Range(2, 12).ToArray();
    public const int Baud = 1_000_000;
    public static readonly int[] LegacyBauds = [500_000];
    public const int Version = 2;
    /// <summary>Intel HEX text of the firmware for <paramref name="pin"/> (embedded resource), or null.</summary>
    public static string? Hex(int pin) => throw new NotImplementedException();
}

public enum FlashStepKind { connecting, backingUp, writing, verifying, done }

public readonly record struct FlashStep(FlashStepKind Kind, double Progress = 0);

public sealed class FlashException(string message) : Exception(message);

/// <summary>STK500 v1 uploader (Optiboot 115200 / ATmegaBOOT 57600).</summary>
public sealed class Flasher(string port, Action<FlashStep> progress)
{
    /// <summary>Where the firmware found on the board is saved before it is overwritten. Set by the app at start.</summary>
    public static string BackupFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Ledly", "Backups");
    /// <summary>Blocking. Backs up the current firmware first. Throws <see cref="FlashException"/> with a translated message.</summary>
    public void Flash(string hex) => throw new NotImplementedException();
    public byte[] ReadFlash() => throw new NotImplementedException();
    public static string MakeHex(byte[] data) => throw new NotImplementedException();
    public static byte[] ParseHex(string text) => throw new NotImplementedException();
}
