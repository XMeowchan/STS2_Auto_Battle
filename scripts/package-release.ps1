param(
    [string]$Configuration = "Release",
    [string]$OutputDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $projectRoot "src"
$manifestPath = Join-Path $projectRoot "mod_manifest.json"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$modId = [string]$manifest.pck_name

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $projectRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$manifest.version
}

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith("v")) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}

$buildOut = Join-Path $srcDir "bin\$Configuration"
$dllPath = Join-Path $buildOut "$modId.dll"
$pckPath = Join-Path $buildOut "$modId.pck"

foreach ($artifactPath in @($dllPath, $pckPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $artifactPath)) {
        throw "Missing release input: $artifactPath"
    }
}

$stagingRoot = Join-Path $OutputDir "_staging"
$packageRoot = Join-Path $stagingRoot $modId
$zipPath = Join-Path $OutputDir "$modId-$normalizedVersion.zip"

Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $packageRoot "$modId.dll") -Force
Copy-Item -LiteralPath $pckPath -Destination (Join-Path $packageRoot "$modId.pck") -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $packageRoot "mod_manifest.json") -Force

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Compress-Archive -Path $packageRoot -DestinationPath $zipPath -Force

Write-Host "Packaged release zip: $zipPath"
