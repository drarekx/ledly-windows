using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Ledly.Windows.Platform;

/// <summary>
/// Swaps the install folder for a downloaded release: unzips it, checks it holds Ledly.exe, and
/// starts a hidden PowerShell that waits for this process to exit, copies the new files over the
/// old ones (retrying while they're still locked) and starts the new Ledly.exe.
/// </summary>
static class UpdateInstaller
{
    public static string? Prepare(string packagePath, string version)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return L.Tr("No se sabe dónde está instalado Ledly.");
        var installDir = Path.GetDirectoryName(exePath)!;
        var staging = Path.Combine(Path.GetTempPath(), $"Ledly-update-{version}-{Guid.NewGuid().ToString("N")[..8]}");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(packagePath, staging);
            // The zip may hold the files at its root or inside one folder.
            var source = Directory.EnumerateFiles(staging, "Ledly.exe", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .OrderBy(d => d!.Length)
                .FirstOrDefault();
            if (source is null)
            {
                TryDelete(staging);
                return L.Tr("La actualización descargada no contiene Ledly.exe.");
            }
            if (!CanWrite(installDir))
            {
                TryDelete(staging);
                return L.Tr("No se puede escribir en {0}.", installDir);
            }

            var exeName = Path.GetFileName(exePath);
            var script = $$"""
                $ErrorActionPreference = 'SilentlyContinue'
                Wait-Process -Id {{Environment.ProcessId}} -Timeout 60
                Start-Sleep -Milliseconds 500
                $src = {{Quote(source)}}
                $dst = {{Quote(installDir)}}
                for ($i = 0; $i -lt 20; $i++) {
                    try {
                        Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force -ErrorAction Stop
                        break
                    } catch {
                        Start-Sleep -Seconds 1
                    }
                }
                Start-Process -FilePath (Join-Path $dst {{Quote(exeName)}})
                Remove-Item -LiteralPath {{Quote(staging)}} -Recurse -Force
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                TryDelete(staging);
                return L.Tr("No se pudo iniciar la actualización.");
            }
            Log.Write($"update: {version} staged in {staging}, replacing {installDir} after exit");
            return null;
        }
        catch (Exception e)
        {
            TryDelete(staging);
            return e.Message;
        }
    }

    /// <summary>PowerShell single-quoted literal.</summary>
    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".ledly-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { /* temp folder */ }
    }
}
