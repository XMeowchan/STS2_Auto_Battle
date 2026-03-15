# Releasing

This project ships two release artifacts:

- `CombatAutoHost-Setup-x.y.z.exe`
- `CombatAutoHost-portable-x.y.z.zip`

The portable zip is the manual/portable distribution artifact. The installer exe installs the mod directly into the Slay the Spire 2 `mods` folder.

## Local release flow

1. Update `mod_manifest.json` version.
2. Build both release artifacts:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1
```

This will:

- build the mod DLL and PCK
- stage the installer payload
- build the portable zip
- build the installer exe
- generate release notes in `dist\release\`

## GitHub upload

To upload assets directly to GitHub, set a token and run:

```powershell
$env:GITHUB_TOKEN="your-token"
powershell -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1 -Upload
```

The script will:

- create release `v<version>` if it does not exist
- otherwise replace existing assets for that release

## GitHub Actions release workflow

The release workflow should run on a Windows self-hosted runner because it depends on:

- Slay the Spire 2 local game assemblies
- Godot
- Inno Setup 6

Recommended runner setup:

- Windows x64
- Slay the Spire 2 installed
- Godot installed
- Inno Setup 6 installed
- labels including `self-hosted`, `windows`, and `sts2`
