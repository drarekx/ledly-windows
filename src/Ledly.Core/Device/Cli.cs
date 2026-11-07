namespace Ledly;

// CONTRACT STUB — replaced by the device port. Keep these public signatures.

/// <summary>Command-line tools shared by every host: <c>--flash PORT PIN</c>, <c>--read-flash PORT OUT.hex</c>.</summary>
public static class Cli
{
    /// <summary>Runs a command-line tool when <paramref name="args"/> asks for one and returns its exit code; null = start the app.</summary>
    public static int? TryRun(string[] args, IPlatform platform) => null;
}
