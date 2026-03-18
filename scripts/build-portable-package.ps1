param(
    [string]$Configuration = "Release",
    [string]$GameDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$modId = "CombatAutoHost"
$manifestPath = Join-Path $projectRoot "$modId.json"

$payloadArgs = @(
    "-ExecutionPolicy", "Bypass",
    "-File", (Join-Path $PSScriptRoot "build-installer-payload.ps1"),
    "-Configuration", $Configuration
)
if ($GameDir) {
    $payloadArgs += @("-GameDir", $GameDir)
}
if ($SkipBuild) {
    $payloadArgs += "-SkipBuild"
}

& powershell @payloadArgs
if ($LASTEXITCODE -ne 0) {
    throw "build-installer-payload failed."
}

$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$payloadDir = Join-Path $projectRoot "dist\installer\payload\$modId"
if (-not (Test-Path -LiteralPath $payloadDir)) {
    throw "Portable payload directory not found: $payloadDir"
}

$releaseDir = Join-Path $projectRoot "dist\release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$zipPath = Join-Path $releaseDir ("{0}-portable-{1}.zip" -f $modId, $manifest.version)
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path $payloadDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Built portable package: $zipPath"
