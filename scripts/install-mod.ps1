param(
    [string]$GameDir,
    [string]$Configuration = "Release",
    [switch]$SkipImport
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Sts2InstallHelpers.ps1")

& (Join-Path $PSScriptRoot "build-mod-artifacts.ps1") -Configuration $Configuration -GameDir $GameDir -SkipImport:$SkipImport
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

$resolvedGameDir = Resolve-Sts2GameDir -RequestedPath $GameDir
$modsRoot = Resolve-Sts2ModsRoot -GameDir $resolvedGameDir
$modId = "CombatAutoHost"
$manifestPath = Join-Path $projectRoot "$modId.json"
$targetModDir = Join-Path $modsRoot $modId
$buildOut = Join-Path $projectRoot "src\bin\$Configuration"

New-Item -ItemType Directory -Force -Path $targetModDir | Out-Null

Remove-Item -LiteralPath (Join-Path $targetModDir "mod_manifest.json") -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $buildOut "$modId.dll") -Destination (Join-Path $targetModDir "$modId.dll") -Force
Copy-Item -LiteralPath (Join-Path $buildOut "$modId.pck") -Destination (Join-Path $targetModDir "$modId.pck") -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $targetModDir "$modId.json") -Force

Write-Host "Detected game dir: $resolvedGameDir"
Write-Host "Installed $modId to $targetModDir"
