<#
.SYNOPSIS
    Builds Ledly for Windows: a self-contained, single-file Ledly.exe zipped with its SHA-256.

.EXAMPLE
    .\build.ps1                          # version from Directory.Build.props, win-x64
    .\build.ps1 -Version 1.1.0
    .\build.ps1 -Version 1.1.0 -Runtime win-x64,win-arm64

    Output: build\Ledly-<runtime>.zip and build\Ledly-<runtime>.zip.sha256 (the hash alone, like the Mac release).
#>
param(
    [string]$Version = "",
    [string[]]$Runtime = @("win-x64"),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $Version) {
    $props = [xml](Get-Content "Directory.Build.props")
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') { throw "Bad version: $Version" }
$numeric = ($Version -split '[-+]')[0]

$out = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($rid in $Runtime) {
    Write-Host "Building Ledly $Version ($rid)..." -ForegroundColor Cyan
    $publish = Join-Path $out "publish-$rid"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    dotnet publish src/Ledly/Ledly.csproj -c $Configuration -r $rid --self-contained true `
        -p:Version=$numeric -p:InformationalVersion=$Version -p:FileVersion=$numeric.0 `
        -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($rid)" }

    $exe = Join-Path $publish "Ledly.exe"
    if (-not (Test-Path $exe)) { throw "Ledly.exe missing in $publish" }

    $zip = Join-Path $out "Ledly-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.sha256" -Value $hash -NoNewline -Encoding ascii

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "  $zip ($size MB)  sha256 $hash" -ForegroundColor Green
}
