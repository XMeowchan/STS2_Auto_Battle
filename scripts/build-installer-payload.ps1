param(
    [string]$Configuration = "Release",
    [string]$PayloadRoot = $(Join-Path (Split-Path -Parent $PSScriptRoot) "dist\installer\payload"),
    [string]$GameDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Sts2InstallHelpers.ps1")

$projectRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw (Join-Path $projectRoot "mod_manifest.json") | ConvertFrom-Json
$modId = [string]$manifest.pck_name
if ([string]::IsNullOrWhiteSpace($modId)) {
    $modId = "CombatAutoHost"
}

$buildOut = Join-Path $projectRoot "src\bin\$Configuration"
$stagedModDir = Join-Path $PayloadRoot $modId

if (-not $SkipBuild) {
    $buildArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "build-mod-artifacts.ps1"),
        "-Configuration", $Configuration
    )
    if ($GameDir) {
        $buildArgs += @("-GameDir", $GameDir)
    }

    & powershell @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "build-mod-artifacts failed."
    }
}

$requiredArtifacts = @(
    (Join-Path $buildOut "$modId.dll"),
    (Join-Path $buildOut "$modId.pck"),
    (Join-Path $projectRoot "mod_manifest.json")
)

foreach ($artifactPath in $requiredArtifacts) {
    if (-not (Test-Path -LiteralPath $artifactPath)) {
        throw "Missing release artifact: $artifactPath"
    }
}

Set-PckCompatibilityHeader -Path (Join-Path $buildOut "$modId.pck") -EngineMinorVersion 5
$pckHeader = Assert-PckCompatibilityHeader -Path (Join-Path $buildOut "$modId.pck") -ExpectedMajor 4 -MaxMinor 5
Write-Host ("Verified PCK compatibility header: Godot {0}.{1}" -f $pckHeader.Major, $pckHeader.Minor)

if (Test-Path -LiteralPath $stagedModDir) {
    Remove-Item -LiteralPath $stagedModDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagedModDir | Out-Null
Copy-Item (Join-Path $buildOut "$modId.dll") (Join-Path $stagedModDir "$modId.dll") -Force
Copy-Item (Join-Path $buildOut "$modId.pck") (Join-Path $stagedModDir "$modId.pck") -Force
Set-PckCompatibilityHeader -Path (Join-Path $stagedModDir "$modId.pck") -EngineMinorVersion 5
Copy-Item (Join-Path $projectRoot "mod_manifest.json") (Join-Path $stagedModDir "mod_manifest.json") -Force

Write-Host "Staged installer payload: $stagedModDir"
