using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CombatAutoHost;

[ModInitializer("Initialize")]
public static class ModEntry
{
    public const string ModId = "CombatAutoHost";

    private static readonly object InitLock = new();

    private static bool _initialized;
    private static Harmony? _harmony;

    public static string InstallDirectory { get; private set; } = AppContext.BaseDirectory;
    public static string CurrentVersion { get; private set; } = "0.0.0";

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            InstallDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
            CurrentVersion = LoadVersion();
            _harmony = new Harmony($"openai.codex.sts2.{ModId.ToLowerInvariant()}");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            _initialized = true;

            Log.Info($"{ModId} initialized. version={CurrentVersion}", 2);
        }
    }

    private static string LoadVersion()
    {
        try
        {
            string manifestPath = Path.Combine(InstallDirectory, $"{ModId}.json");
            if (!File.Exists(manifestPath))
            {
                manifestPath = Path.Combine(InstallDirectory, "mod_manifest.json");
            }

            if (!File.Exists(manifestPath))
            {
                return "0.0.0";
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.TryGetProperty("version", out JsonElement versionElement))
            {
                string? version = versionElement.GetString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{ModId}: failed to read version from manifest json: {ex.Message}");
        }

        return "0.0.0";
    }
}
