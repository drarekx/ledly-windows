namespace Ledly;

/// <summary>Command-line tools shared by every host: <c>--flash PORT PIN</c>, <c>--read-flash PORT OUT.hex</c>.</summary>
public static class Cli
{
    /// <summary>Runs a command-line tool when <paramref name="args"/> asks for one and returns its exit code; null = start the app.</summary>
    public static int? TryRun(string[] args, IPlatform platform)
    {
        // `--read-flash PORT OUT.hex`: dumps the board's firmware (read only).
        var index = Array.IndexOf(args, "--read-flash");
        if (index >= 0)
        {
            if (index + 2 >= args.Length) return Usage();
            try
            {
                var data = new Flasher(args[index + 1], _ => { }).ReadFlash();
                File.WriteAllText(args[index + 2], Flasher.MakeHex(data));
                Console.WriteLine($"Read {data.Length} bytes into {args[index + 2]}");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                return 1;
            }
        }

        // `--flash PORT PIN`: backs up the board's firmware and installs the embedded one for that pin.
        index = Array.IndexOf(args, "--flash");
        if (index >= 0)
        {
            if (index + 2 >= args.Length || !int.TryParse(args[index + 2], out var pin)) return Usage();
            if (Firmware.Hex(pin) is not { } hex)
            {
                Console.Error.WriteLine($"Error: no firmware for pin D{pin} (pins {Firmware.Pins[0]}–{Firmware.Pins[^1]})");
                return 1;
            }
            Flasher.BackupFolder = Path.Combine(platform.DataDirectory, "Backups");
            FlashStepKind? lastKind = null;
            var lastPercent = -1;
            var flasher = new Flasher(args[index + 1], step =>
            {
                var percent = (int)(step.Progress * 100);
                if (step.Kind == lastKind && percent / 10 == lastPercent / 10) return;
                lastKind = step.Kind;
                lastPercent = percent;
                Console.WriteLine(step.Kind is FlashStepKind.connecting or FlashStepKind.done ? $"{step.Kind}" : $"{step.Kind} {percent}%");
            });
            try
            {
                flasher.Flash(hex);
                var backup = flasher.BackupPath is { } path ? $"backup in {path}" : "nothing to back up";
                Console.WriteLine($"Installed {Firmware.FileName(pin)}; {backup}");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                return 1;
            }
        }
        return null;
    }

    static int Usage()
    {
        Console.Error.WriteLine("Usage: --flash PORT PIN | --read-flash PORT OUT.hex");
        return 2;
    }
}
