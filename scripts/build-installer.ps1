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
$appName = if ([string]::IsNullOrWhiteSpace([string]$manifest.name)) { "Combat Auto Host" } else { [string]$manifest.name }
$payloadDir = Join-Path $projectRoot "dist\installer\payload"
$issPath = Join-Path $projectRoot "installer\CombatAutoHost.iss"

$isccCandidates = @()
$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($isccCommand) {
    $isccCandidates += $isccCommand.Source
}
$isccCandidates += @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $isccPath) {
    throw "Inno Setup 6 not found. Install ISCC.exe, then rerun .\scripts\build-installer.ps1."
}

& $isccPath "/DAppVersion=$($manifest.version)" "/DAppName=$appName" "/DModId=$modId" "/DPayloadDir=$payloadDir" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed."
}

$installerPath = Join-Path $projectRoot ("dist\installer\output\{0}-Setup-{1}.exe" -f $modId, $manifest.version)
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer was not created: $installerPath"
}

Write-Host "Built installer: $installerPath"
