Set-StrictMode -Version Latest

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-SteamInstallPath {
    $registryKeys = @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\Software\WOW6432Node\Valve\Steam",
        "HKLM:\Software\Valve\Steam"
    )

    foreach ($registryKey in $registryKeys) {
        try {
            $installPath = (Get-ItemProperty -Path $registryKey -Name InstallPath -ErrorAction Stop).InstallPath
            if ($installPath -and (Test-Path -LiteralPath $installPath)) {
                return (Resolve-ExistingPath -Path $installPath)
            }
        }
        catch {
        }
    }

    $fallbacks = @(
        (Join-Path ${env:ProgramFiles(x86)} "Steam"),
        (Join-Path $env:ProgramFiles "Steam"),
        (Join-Path $env:LOCALAPPDATA "Programs\Steam"),
        "C:\Steam",
        "D:\Steam",
        "E:\Steam"
    ) | Where-Object { $_ }

    foreach ($candidate in ($fallbacks | Select-Object -Unique)) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-ExistingPath -Path $candidate)
        }
    }

    return $null
}

function ConvertFrom-SteamVdfPath {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    return $Value.Replace("\\", "\")
}

function Get-SteamLibraryPaths {
    param(
        [string]$SteamInstallPath
    )

    $steamRoot = $SteamInstallPath
    if ([string]::IsNullOrWhiteSpace($steamRoot)) {
        $steamRoot = Get-SteamInstallPath
    }

    if ([string]::IsNullOrWhiteSpace($steamRoot)) {
        return @()
    }

    $libraryPaths = [System.Collections.Generic.List[string]]::new()
    $libraryPaths.Add((Resolve-ExistingPath -Path $steamRoot))

    $libraryFoldersPath = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $libraryFoldersPath) {
        $content = Get-Content -LiteralPath $libraryFoldersPath
        foreach ($line in $content) {
            if ($line -match '^\s*"path"\s*"(?<path>.+)"\s*$') {
                $path = ConvertFrom-SteamVdfPath -Value $Matches.path
                if ($path -and (Test-Path -LiteralPath $path)) {
                    $libraryPaths.Add((Resolve-ExistingPath -Path $path))
                }
                continue
            }

            if ($line -match '^\s*"\d+"\s*"(?<path>.+)"\s*$') {
                $path = ConvertFrom-SteamVdfPath -Value $Matches.path
                if ($path -and (Test-Path -LiteralPath $path)) {
                    $libraryPaths.Add((Resolve-ExistingPath -Path $path))
                }
            }
        }
    }

    return @($libraryPaths | Select-Object -Unique)
}

function Test-Sts2GameDir {
    param(
        [AllowNull()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    $exePath = Join-Path $Path "SlayTheSpire2.exe"
    return (Test-Path -LiteralPath $exePath)
}

function Resolve-Sts2GameDir {
    param(
        [string]$RequestedPath,
        [switch]$AllowMissing
    )

    if ([string]::IsNullOrWhiteSpace($RequestedPath)) {
        $RequestedPath = [Environment]::GetEnvironmentVariable("STS2_GAME_DIR")
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (Test-Sts2GameDir -Path $RequestedPath) {
            return (Resolve-ExistingPath -Path $RequestedPath)
        }

        throw "Slay the Spire 2 executable not found under '$RequestedPath'."
    }

    foreach ($libraryPath in (Get-SteamLibraryPaths)) {
        $commonDir = Join-Path $libraryPath "steamapps\common"
        if (-not (Test-Path -LiteralPath $commonDir)) {
            continue
        }

        $preferredPath = Join-Path $commonDir "Slay the Spire 2"
        if (Test-Sts2GameDir -Path $preferredPath) {
            return (Resolve-ExistingPath -Path $preferredPath)
        }
    }

    if ($AllowMissing) {
        return $null
    }

    throw "Could not locate Slay the Spire 2 in any Steam library. Pass -GameDir to specify it manually."
}

function Resolve-Sts2ModsRoot {
    param(
        [Parameter(Mandatory)]
        [string]$GameDir
    )

    foreach ($candidate in @(
        (Join-Path $GameDir "mods"),
        (Join-Path $GameDir "Mods")
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-ExistingPath -Path $candidate)
        }
    }

    return (Join-Path $GameDir "mods")
}

function Resolve-DotnetExecutable {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw "dotnet executable not found."
    }

    return $dotnetCommand.Source
}

function Resolve-GodotExecutable {
    $candidates = [System.Collections.Generic.List[string]]::new()

    foreach ($envVarName in @("STS2_GODOT_EXE", "GODOT_EXE", "GODOT4_EXE")) {
        $envCandidate = [Environment]::GetEnvironmentVariable($envVarName)
        if (-not [string]::IsNullOrWhiteSpace($envCandidate)) {
            $candidates.Add($envCandidate)
        }
    }

    $godotCommand = Get-Command godot -ErrorAction SilentlyContinue
    if ($godotCommand) {
        $candidates.Add($godotCommand.Source)
    }

    $godot4Command = Get-Command godot4 -ErrorAction SilentlyContinue
    if ($godot4Command) {
        $candidates.Add($godot4Command.Source)
    }

    foreach ($libraryPath in (Get-SteamLibraryPaths)) {
        foreach ($candidate in @(
            (Join-Path $libraryPath "steamapps\common\Godot Engine\godot.windows.opt.tools.64.exe"),
            (Join-Path $libraryPath "steamapps\common\Godot Engine\Godot_v4.6-stable_win64.exe")
        )) {
            $candidates.Add($candidate)
        }
    }

    $resolved = @($candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique)
    if (-not $resolved) {
        throw "Godot executable not found. Set STS2_GODOT_EXE to your Godot 4 executable if auto-detection fails."
    }

    return $resolved[0]
}

function Update-GodotAssetImports {
    param(
        [Parameter(Mandatory)]
        [string]$GodotExecutable,
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    & $GodotExecutable --headless --editor --quit --path $ProjectRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Godot asset import failed."
    }
}

function Set-PckCompatibilityHeader {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [int]$EngineMinorVersion = 5
    )

    [byte[]]$pckBytes = [System.IO.File]::ReadAllBytes($Path)
    if ($pckBytes.Length -lt 16) {
        throw "PCK header too small."
    }

    $pckBytes[8] = 4
    $pckBytes[9] = 0
    $pckBytes[10] = 0
    $pckBytes[11] = 0
    $pckBytes[12] = [byte]$EngineMinorVersion
    $pckBytes[13] = 0
    $pckBytes[14] = 0
    $pckBytes[15] = 0

    [System.IO.File]::WriteAllBytes($Path, $pckBytes)
}

function Get-PckCompatibilityHeader {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    [byte[]]$pckBytes = [System.IO.File]::ReadAllBytes($Path)
    if ($pckBytes.Length -lt 16) {
        throw "PCK header too small."
    }

    [pscustomobject]@{
        Major = [System.BitConverter]::ToInt32($pckBytes, 8)
        Minor = [System.BitConverter]::ToInt32($pckBytes, 12)
    }
}

function Assert-PckCompatibilityHeader {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [int]$ExpectedMajor = 4,
        [int]$MaxMinor = 5
    )

    $header = Get-PckCompatibilityHeader -Path $Path
    if ($header.Major -ne $ExpectedMajor -or $header.Minor -gt $MaxMinor) {
        throw "PCK compatibility header is $($header.Major).$($header.Minor), expected <= Godot $ExpectedMajor.$MaxMinor."
    }

    return $header
}
