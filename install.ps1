# Ledly installer for Windows.
#   irm https://raw.githubusercontent.com/drarekx/ledly-windows/main/install.ps1 | iex
# Installs or updates Ledly in %LOCALAPPDATA%\Programs\Ledly, adds a Start menu shortcut and opens it.
# No administrator rights needed.

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is far faster without the progress bar

function Say($text) { Write-Host $text -ForegroundColor Cyan }
function Fail($text) { Write-Host "Error: $text" -ForegroundColor Red; throw $text }

$repo = "drarekx/ledly-windows"
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64" -or $env:PROCESSOR_ARCHITEW6432 -eq "ARM64") { "win-arm64" } else { "win-x64" }

if ([Environment]::OSVersion.Version.Build -lt 19041) {
    Fail "Ledly necesita Windows 10 versión 2004 (19041) o posterior."
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$headers = @{ "User-Agent" = "Ledly-installer" }

Say "Buscando la última versión de Ledly..."
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
$asset = $release.assets | Where-Object { $_.name -eq "Ledly-$arch.zip" } | Select-Object -First 1
if (-not $asset -and $arch -ne "win-x64") {
    # No native build for this architecture: the x64 one runs under emulation.
    $arch = "win-x64"
    $asset = $release.assets | Where-Object { $_.name -eq "Ledly-$arch.zip" } | Select-Object -First 1
}
if (-not $asset) { Fail "La versión $($release.tag_name) no incluye Ledly-$arch.zip." }
$hashAsset = $release.assets | Where-Object { $_.name -eq "$($asset.name).sha256" } | Select-Object -First 1

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("Ledly-install-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Say "Descargando Ledly $($release.tag_name) ($arch)..."
    $zip = Join-Path $tmp $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $headers -UseBasicParsing

    if ($hashAsset) {
        $hashFile = Join-Path $tmp "hash.txt"
        Invoke-WebRequest -Uri $hashAsset.browser_download_url -OutFile $hashFile -Headers $headers -UseBasicParsing
        $expected = (Get-Content -Raw $hashFile).Trim().Split(" ")[0].ToLowerInvariant()
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { Fail "La descarga no coincide con su SHA-256." }
    }

    $extract = Join-Path $tmp "files"
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $exe = Get-ChildItem -Path $extract -Filter "Ledly.exe" -Recurse | Select-Object -First 1
    if (-not $exe) { Fail "El archivo descargado no contiene Ledly.exe." }

    # Close a running copy so it can be replaced (it also frees the Arduino's serial port).
    $running = Get-Process -Name "Ledly" -ErrorAction SilentlyContinue
    if ($running) {
        Say "Cerrando Ledly..."
        # Ask it to quit (turns the strip off and saves); it lives in the tray, so there is no window to close.
        try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:8765/api/app/quit" -TimeoutSec 3 | Out-Null } catch { }
        $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
        Get-Process -Name "Ledly" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    $dest = Join-Path $env:LOCALAPPDATA "Programs\Ledly"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $exe.DirectoryName "*") -Destination $dest -Recurse -Force
    $target = Join-Path $dest "Ledly.exe"
    # Downloaded from the internet: drop the mark so SmartScreen doesn't ask every time.
    Unblock-File -Path $target -ErrorAction SilentlyContinue

    # Start menu shortcut.
    $programs = [Environment]::GetFolderPath("Programs")
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $programs "Ledly.lnk"))
    $link.TargetPath = $target
    $link.WorkingDirectory = $dest
    $link.IconLocation = "$target,0"
    $link.Description = "Ledly: ambilight para tu monitor"
    $link.Save()

    Say "Instalado en $dest"
    Start-Process -FilePath $target
    Write-Host "¡Listo! Ledly está en el área de notificación (junto al reloj)." -ForegroundColor Green
    Write-Host "Siguiente paso: conecta el Arduino Nano por USB y, en Ledly > Dispositivo, pulsa «Instalar firmware»."
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
