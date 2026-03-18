using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using HttpClient = System.Net.Http.HttpClient;
using HttpCompletionOption = System.Net.Http.HttpCompletionOption;
using HttpMethod = System.Net.Http.HttpMethod;
using HttpRequestMessage = System.Net.Http.HttpRequestMessage;
using HttpResponseMessage = System.Net.Http.HttpResponseMessage;

namespace CombatAutoHost;

[HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
internal static class AutoUpdateGameReadyPatch
{
    private const string ControllerName = "CombatAutoHostAutoUpdateController";

    private static void Postfix(NGame __instance)
    {
        if (__instance.FindChild(ControllerName, recursive: false, owned: false) is AutoUpdateController)
        {
            return;
        }

        AutoUpdateController controller = new()
        {
            Name = ControllerName
        };
        __instance.AddChild(controller);
    }
}

internal sealed class AutoUpdateController : Node
{
    public override void _Ready()
    {
        AutoUpdateService.Start();
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        while (AutoUpdateService.TryDequeueToast(out string? message))
        {
            if (NGame.Instance != null && !string.IsNullOrWhiteSpace(message))
            {
                NGame.Instance.AddChildSafely(NFullscreenTextVfx.Create(message));
            }
        }
    }
}

internal static class AutoUpdateService
{
    private const string RepoOwner = "XMeowchan";
    private const string RepoName = "STS2_Auto_Battle";
    private const string PortableAssetPrefix = "CombatAutoHost-portable-";
    private const string PortableAssetSuffix = ".zip";
    private const string UserAgent = "CombatAutoHost-AutoUpdater";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly object Sync = new();
    private static readonly Queue<string> ToastQueue = new();

    private static bool _started;
    private static bool _installerStartedThisSession;

    public static void Start()
    {
        lock (Sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        TaskHelper.RunSafely(RunStartupAsync());
    }

    public static bool TryDequeueToast(out string? message)
    {
        lock (Sync)
        {
            if (ToastQueue.Count == 0)
            {
                message = null;
                return false;
            }

            message = ToastQueue.Dequeue();
            return true;
        }
    }

    private static async Task RunStartupAsync()
    {
        try
        {
            AutoUpdateState state = LoadState();
            HandleInstallStatus(state);
            EnsurePendingInstallerStarted(state);
            await CheckForUpdatesAsync(state);
            SaveState(state);
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost[UPDATE] startup failed: {ex.Message}");
        }
    }

    private static async Task CheckForUpdatesAsync(AutoUpdateState state)
    {
        if (!state.Enabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.PendingInstallVersion) && File.Exists(state.PendingZipPath))
        {
            Log.Info($"CombatAutoHost[UPDATE] pending install detected for {state.PendingInstallVersion}");
            return;
        }

        GitHubRelease? release = await GetLatestReleaseAsync();
        if (release == null)
        {
            return;
        }

        string? latestVersion = NormalizeVersion(release.TagName);
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return;
        }

        state.LastCheckedUtc = DateTimeOffset.UtcNow.ToString("O");
        state.LastCheckedVersion = latestVersion;

        if (CompareVersions(latestVersion, ModEntry.CurrentVersion) <= 0)
        {
            Log.Info($"CombatAutoHost[UPDATE] current version {ModEntry.CurrentVersion} is up to date.");
            return;
        }

        GitHubReleaseAsset? asset = release.Assets.FirstOrDefault(static asset =>
            asset.Name.StartsWith(PortableAssetPrefix, StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith(PortableAssetSuffix, StringComparison.OrdinalIgnoreCase));
        if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            Log.Warn($"CombatAutoHost[UPDATE] no portable asset found for release {release.TagName}.");
            return;
        }

        string downloadsDir = GetDownloadsDirectory();
        Directory.CreateDirectory(downloadsDir);

        string zipPath = Path.Combine(downloadsDir, $"CombatAutoHost-portable-{latestVersion}.zip");
        await DownloadFileAsync(asset.BrowserDownloadUrl, zipPath);

        state.PendingInstallVersion = latestVersion;
        state.PendingZipPath = zipPath;
        state.PendingAssetUrl = asset.BrowserDownloadUrl;
        SaveState(state);

        EnsurePendingInstallerStarted(state);
        EnqueueToast(CombatAutoUiText.Format(CombatAutoUiText.ToastUpdateReadyKey, latestVersion));
        Log.Info($"CombatAutoHost[UPDATE] downloaded update {latestVersion} to {zipPath}");
    }

    private static void HandleInstallStatus(AutoUpdateState state)
    {
        string statusPath = GetStatusPath();
        if (!File.Exists(statusPath))
        {
            return;
        }

        try
        {
            AutoUpdateInstallStatus? status = JsonSerializer.Deserialize<AutoUpdateInstallStatus>(File.ReadAllText(statusPath), JsonOptions);
            if (status == null)
            {
                return;
            }

            if (status.Success && string.Equals(status.Version, ModEntry.CurrentVersion, StringComparison.Ordinal))
            {
                if (!string.Equals(state.LastNotifiedInstalledVersion, status.Version, StringComparison.Ordinal))
                {
                    EnqueueToast(CombatAutoUiText.Format(CombatAutoUiText.ToastUpdateInstalledKey, status.Version ?? ModEntry.CurrentVersion));
                    state.LastNotifiedInstalledVersion = status.Version;
                }

                state.PendingInstallVersion = null;
                state.PendingZipPath = null;
                state.PendingAssetUrl = null;
                state.LastInstalledVersion = status.Version;
                state.LastInstallError = null;
                File.Delete(statusPath);
                return;
            }

            if (!status.Success)
            {
                string error = string.IsNullOrWhiteSpace(status.Message) ? "unknown error" : status.Message;
                if (!string.Equals(state.LastInstallError, error, StringComparison.Ordinal))
                {
                    EnqueueToast(CombatAutoUiText.Format(CombatAutoUiText.ToastUpdateFailedKey, error));
                    state.LastInstallError = error;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost[UPDATE] failed to read install status: {ex.Message}");
        }
    }

    private static void EnsurePendingInstallerStarted(AutoUpdateState state)
    {
        if (_installerStartedThisSession || string.IsNullOrWhiteSpace(state.PendingInstallVersion) || !File.Exists(state.PendingZipPath))
        {
            return;
        }

        try
        {
            string scriptPath = EnsureInstallerScript();
            string targetDir = ModEntry.InstallDirectory;
            string statusPath = GetStatusPath();

            if (File.Exists(statusPath))
            {
                File.Delete(statusPath);
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                    $"-ZipPath \"{state.PendingZipPath}\" " +
                    $"-TargetDir \"{targetDir}\" " +
                    $"-StatusPath \"{statusPath}\" " +
                    $"-Version \"{state.PendingInstallVersion}\" " +
                    $"-ParentPid {System.Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            _installerStartedThisSession = true;
            Log.Info($"CombatAutoHost[UPDATE] installer watcher started for {state.PendingInstallVersion}");
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost[UPDATE] failed to launch installer watcher: {ex.Message}");
        }
    }

    private static async Task<GitHubRelease?> GetLatestReleaseAsync()
    {
        string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"CombatAutoHost[UPDATE] latest release request failed: {(int)response.StatusCode}");
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions);
    }

    private static async Task DownloadFileAsync(string url, string outputPath)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        string tempPath = outputPath + ".tmp";
        await using Stream responseStream = await response.Content.ReadAsStreamAsync();
        await using FileStream fileStream = File.Create(tempPath);
        await responseStream.CopyToAsync(fileStream);
        fileStream.Close();

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        File.Move(tempPath, outputPath);
    }

    private static void SaveState(AutoUpdateState state)
    {
        string path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static AutoUpdateState LoadState()
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new AutoUpdateState();
        }

        try
        {
            AutoUpdateState? state = JsonSerializer.Deserialize<AutoUpdateState>(File.ReadAllText(path), JsonOptions);
            return state ?? new AutoUpdateState();
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost[UPDATE] failed to load state: {ex.Message}");
            return new AutoUpdateState();
        }
    }

    private static string EnsureInstallerScript()
    {
        string scriptPath = Path.Combine(GetUpdaterDirectory(), "install-pending-update.ps1");
        string script = """
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][string]$TargetDir,
    [Parameter(Mandatory)][string]$StatusPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][int]$ParentPid
)

$ErrorActionPreference = "Stop"

function Write-Status([bool]$Success, [string]$Message) {
    $payload = @{
        version = $Version
        success = $Success
        message = $Message
        completedAtUtc = [DateTime]::UtcNow.ToString("O")
    } | ConvertTo-Json -Depth 4

    $dir = Split-Path -Parent $StatusPath
    if ($dir) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    Set-Content -LiteralPath $StatusPath -Value $payload -Encoding UTF8
}

try {
    for ($i = 0; $i -lt 7200; $i++) {
        if (-not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) {
            break
        }

        Start-Sleep -Seconds 1
    }

    if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) {
        Write-Status $false "timed out waiting for the game to exit"
        exit 1
    }

    if (-not (Test-Path -LiteralPath $ZipPath)) {
        Write-Status $false "downloaded update package is missing"
        exit 1
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("CombatAutoHost-update-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $tempRoot -Force

    $sourceRoot = Join-Path $tempRoot "CombatAutoHost"
    if (-not (Test-Path -LiteralPath $sourceRoot)) {
        $dirs = @(Get-ChildItem -LiteralPath $tempRoot -Directory)
        if ($dirs.Count -eq 1) {
            $sourceRoot = $dirs[0].FullName
        }
        else {
            $sourceRoot = $tempRoot
        }
    }

    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    Remove-Item -LiteralPath (Join-Path $TargetDir 'mod_manifest.json') -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $TargetDir $_.Name) -Recurse -Force
    }

    Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Status $true "installed"
}
catch {
    Write-Status $false $_.Exception.Message
    exit 1
}
""";

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        return scriptPath;
    }

    private static string GetUpdaterDirectory()
    {
        return Path.Combine(ProjectSettings.GlobalizePath("user://"), "mods", ModEntry.ModId, "updater");
    }

    private static string GetDownloadsDirectory()
    {
        return Path.Combine(GetUpdaterDirectory(), "downloads");
    }

    private static string GetStatePath()
    {
        return Path.Combine(GetUpdaterDirectory(), "state.json");
    }

    private static string GetStatusPath()
    {
        return Path.Combine(GetUpdaterDirectory(), "install-status.json");
    }

    private static string? NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string normalized = tag.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOf('-');
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return normalized;
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out Version? leftVersion) && Version.TryParse(right, out Version? rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnqueueToast(string message)
    {
        lock (Sync)
        {
            ToastQueue.Enqueue(message);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class AutoUpdateState
    {
        public bool Enabled { get; set; } = true;
        public string? LastCheckedUtc { get; set; }
        public string? LastCheckedVersion { get; set; }
        public string? PendingInstallVersion { get; set; }
        public string? PendingZipPath { get; set; }
        public string? PendingAssetUrl { get; set; }
        public string? LastInstalledVersion { get; set; }
        public string? LastNotifiedInstalledVersion { get; set; }
        public string? LastInstallError { get; set; }
    }

    private sealed class AutoUpdateInstallStatus
    {
        public string? Version { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? CompletedAtUtc { get; set; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
        public List<GitHubReleaseAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubReleaseAsset
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
