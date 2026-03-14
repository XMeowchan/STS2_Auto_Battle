# STS2 Auto Battle

`STS2 Auto Battle` is a Slay the Spire 2 mod that adds an `AUTO` button to the combat HUD and lets the local player auto-play a turn with the base-game button style.

## Features

- Reuses the existing combat ping button as the visual template
- Auto-plays playable cards for the local player and ends the turn automatically
- Supports English and Simplified Chinese button/toast text
- Builds a `.dll` and `.pck` package for direct mod installation

## Requirements

- Windows
- .NET 9 SDK
- Godot 4.x executable
- Slay the Spire 2 installed locally

## Environment Variables

You can pass paths via script parameters, or set these environment variables once:

- `STS2_GAME_DIR`: Slay the Spire 2 install directory
- `STS2_GODOT_EXE`: Godot 4 executable path

Example:

```powershell
$env:STS2_GAME_DIR = "D:\Steam\steamapps\common\Slay the Spire 2"
$env:STS2_GODOT_EXE = "D:\Steam\steamapps\common\Godot Engine\godot.windows.opt.tools.64.exe"
```

## Build

Build just the managed assembly:

```powershell
dotnet build .\src\CombatAutoHost.csproj -c Release
```

Build the release artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-mod-artifacts.ps1 -Configuration Release
```

Skip the Godot import refresh when iterating on code-only changes:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-mod-artifacts.ps1 -Configuration Release -SkipImport
```

## Install Into The Game

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-mod.ps1 -Configuration Release
```

That copies:

- `CombatAutoHost.dll`
- `CombatAutoHost.pck`
- `mod_manifest.json`

into the game's `mods\CombatAutoHost` folder.

## GitHub Actions

The repository includes a build workflow for a Windows self-hosted runner.

Recommended runner setup:

- Labels: `self-hosted`, `windows`, `sts2`
- Slay the Spire 2 installed on the runner machine
- Optional repo variables:
  - `STS2_GAME_DIR`
  - `STS2_GODOT_EXE`

If the variables are not set, the scripts still try to auto-detect both paths on the runner.

## Release Flow

- Push a tag like `v0.2.0` to trigger `Publish Release`
- Or run the `Publish Release` workflow manually and provide a tag name
- The workflow builds the mod, creates `dist/CombatAutoHost-<version>.zip`, and uploads it to the GitHub release

You can also package locally after building:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1 -Configuration Release
```
