param(
    [string]$Configuration = "Release",
    [string]$Repo = "XMeowchan/STS2_Auto_Battle",
    [string]$GameDir,
    [string]$TagName,
    [string]$NotesPath,
    [switch]$Upload,
    [switch]$Prerelease,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

function Get-Manifest {
    Get-Content -Raw (Join-Path $projectRoot "CombatAutoHost.json") | ConvertFrom-Json
}

function New-ReleaseNotes {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$PortableName,
        [Parameter(Mandatory)][string]$InstallerName
    )

    $manifest = Get-Manifest
    $content = @"
# $($manifest.name) $Version

## Assets

- $InstallerName
- $PortableName

## Install

- Use the installer exe for one-click installation into the Slay the Spire 2 mods folder.
- Use the portable zip for manual installation or portable distribution.
"@

    Set-Content -LiteralPath $OutputPath -Value $content -Encoding UTF8
}

function Invoke-BuildArtifacts {
    param(
        [Parameter(Mandatory)][string]$BuildConfiguration,
        [AllowNull()][string]$BuildGameDir
    )

    $buildArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "build-mod-artifacts.ps1"),
        "-Configuration", $BuildConfiguration
    )
    $portableArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "build-portable-package.ps1"),
        "-Configuration", $BuildConfiguration,
        "-SkipBuild"
    )
    $installerArgs = @(
        "-ExecutionPolicy", "Bypass",
        "-File", (Join-Path $PSScriptRoot "build-installer.ps1"),
        "-Configuration", $BuildConfiguration,
        "-SkipBuild"
    )

    if ($BuildGameDir) {
        $buildArgs += @("-GameDir", $BuildGameDir)
        $portableArgs += @("-GameDir", $BuildGameDir)
        $installerArgs += @("-GameDir", $BuildGameDir)
    }

    & powershell @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "build-mod-artifacts failed." }

    & powershell @portableArgs
    if ($LASTEXITCODE -ne 0) { throw "build-portable-package failed." }

    & powershell @installerArgs
    if ($LASTEXITCODE -ne 0) { throw "build-installer failed." }
}

function Get-ReleaseToken {
    foreach ($name in @("GITHUB_TOKEN", "GH_TOKEN", "GITHUB_RELEASE_TOKEN")) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Get-ReleaseHeaders {
    param([Parameter(Mandatory)][string]$Token)
    @{
        Authorization = "Bearer $Token"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "CombatAutoHost-ReleaseScript"
    }
}

function Get-ReleaseApiBase {
    param([Parameter(Mandatory)][string]$Repository)
    "https://api.github.com/repos/$Repository/releases"
}

function Get-ReleaseByTagViaApi {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$Token
    )

    $uri = "{0}/tags/{1}" -f (Get-ReleaseApiBase -Repository $Repository), $TagName
    try {
        Invoke-RestMethod -Method Get -Uri $uri -Headers (Get-ReleaseHeaders -Token $Token)
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 404) {
            return $null
        }

        throw
    }
}

function Ensure-ReleaseViaApi {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$NotesFile,
        [Parameter(Mandatory)][string]$Token,
        [switch]$IsPrerelease
    )

    $headers = Get-ReleaseHeaders -Token $Token
    $notes = [string](Get-Content -LiteralPath $NotesFile -Raw)
    $existing = Get-ReleaseByTagViaApi -Repository $Repository -TagName $TagName -Token $Token
    if ($existing) {
        $body = @{
            name = $Title
            body = $notes
            prerelease = [bool]$IsPrerelease
        } | ConvertTo-Json -Depth 6

        return Invoke-RestMethod -Method Patch -Uri ("{0}/{1}" -f (Get-ReleaseApiBase -Repository $Repository), $existing.id) -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8"
    }

    $createBody = @{
        tag_name = $TagName
        name = $Title
        body = $notes
        draft = $false
        prerelease = [bool]$IsPrerelease
    } | ConvertTo-Json -Depth 6

    Invoke-RestMethod -Method Post -Uri (Get-ReleaseApiBase -Repository $Repository) -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($createBody)) -ContentType "application/json; charset=utf-8"
}

function Remove-ExistingAssetsViaApi {
    param(
        [Parameter(Mandatory)][psobject]$Release,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string[]]$AssetNames
    )

    $headers = Get-ReleaseHeaders -Token $Token
    foreach ($asset in @($Release.assets)) {
        if ($AssetNames -contains $asset.name) {
            Invoke-RestMethod -Method Delete -Uri $asset.url -Headers $headers | Out-Null
        }
    }
}

function Upload-AssetsViaApi {
    param(
        [Parameter(Mandatory)][psobject]$Release,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string[]]$AssetPaths
    )

    $headers = Get-ReleaseHeaders -Token $Token
    $uploadBase = ($Release.upload_url -replace '\{\?name,label\}$', '')
    foreach ($assetPath in $AssetPaths) {
        $assetName = Split-Path $assetPath -Leaf
        $uploadUri = "{0}?name={1}" -f $uploadBase, [System.Uri]::EscapeDataString($assetName)
        Invoke-RestMethod -Method Post -Uri $uploadUri -Headers $headers -InFile $assetPath -ContentType "application/octet-stream" | Out-Null
    }
}

function Publish-ReleaseAssets {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string]$NotesFile,
        [Parameter(Mandatory)][string[]]$AssetPaths,
        [switch]$IsPrerelease
    )

    $token = Get-ReleaseToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "GITHUB_TOKEN, GH_TOKEN, or GITHUB_RELEASE_TOKEN must be set to upload release assets."
    }

    $release = Ensure-ReleaseViaApi -Repository $Repository -TagName $TagName -Title $Title -NotesFile $NotesFile -Token $token -IsPrerelease:$IsPrerelease
    Remove-ExistingAssetsViaApi -Release $release -Token $token -AssetNames ($AssetPaths | ForEach-Object { Split-Path $_ -Leaf })
    $release = Get-ReleaseByTagViaApi -Repository $Repository -TagName $TagName -Token $token
    Upload-AssetsViaApi -Release $release -Token $token -AssetPaths $AssetPaths
}

$manifest = Get-Manifest
$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "CombatAutoHost.json does not contain a version."
}

$modId = if ([string]::IsNullOrWhiteSpace([string]$manifest.id)) { "CombatAutoHost" } else { [string]$manifest.id }
$tag = if ([string]::IsNullOrWhiteSpace($TagName)) { "v$version" } else { $TagName.Trim() }
if ($tag -ne ("v$version")) {
    throw "Tag '$tag' does not match CombatAutoHost.json version '$version'."
}

if (-not $SkipBuild) {
    Invoke-BuildArtifacts -BuildConfiguration $Configuration -BuildGameDir $GameDir
}

$releaseDir = Join-Path $projectRoot "dist\release"
$installerDir = Join-Path $projectRoot "dist\installer\output"
$portablePath = Join-Path $releaseDir ("{0}-portable-{1}.zip" -f $modId, $version)
$installerPath = Join-Path $installerDir ("{0}-Setup-{1}.exe" -f $modId, $version)
foreach ($requiredPath in @($portablePath, $installerPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Missing release artifact: $requiredPath"
    }
}

if (-not $NotesPath) {
    $NotesPath = Join-Path $releaseDir ("RELEASE_NOTES-{0}.md" -f $tag)
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $NotesPath) | Out-Null
New-ReleaseNotes -Version $version -Tag $tag -OutputPath $NotesPath -PortableName (Split-Path $portablePath -Leaf) -InstallerName (Split-Path $installerPath -Leaf)

Write-Host "Release tag: $tag"
Write-Host "Release notes: $NotesPath"
Write-Host "Installer: $installerPath"
Write-Host "Portable: $portablePath"

if ($Upload) {
    Publish-ReleaseAssets -Repository $Repo -TagName $tag -Title $tag -NotesFile $NotesPath -AssetPaths @($installerPath, $portablePath) -IsPrerelease:$Prerelease
    Write-Host "Uploaded release assets to $Repo ($tag)."
} else {
    Write-Host "Upload skipped. Rerun with -Upload and a GitHub token to publish assets."
}
