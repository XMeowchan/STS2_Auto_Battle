using System;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CombatAutoHost;

internal sealed class AutoPlaySettings
{
    public AutoPlayMode Mode { get; set; } = AutoPlayMode.Balanced;
}

internal static class AutoPlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly object Sync = new();

    private static AutoPlaySettings? _settings;

    public static event Action<AutoPlayMode>? ModeChanged;

    public static AutoPlayMode CurrentMode
    {
        get
        {
            lock (Sync)
            {
                return GetSettingsUnsafe().Mode;
            }
        }
    }

    public static AutoPlayMode CycleMode()
    {
        AutoPlayMode newMode;
        lock (Sync)
        {
            AutoPlaySettings settings = GetSettingsUnsafe();
            settings.Mode = settings.Mode.Next();
            SaveUnsafe(settings);
            newMode = settings.Mode;
        }

        ModeChanged?.Invoke(newMode);
        return newMode;
    }

    private static AutoPlaySettings GetSettingsUnsafe()
    {
        _settings ??= LoadSettingsUnsafe();
        return _settings;
    }

    private static AutoPlaySettings LoadSettingsUnsafe()
    {
        string path = GetSettingsPath();
        try
        {
            if (!File.Exists(path))
            {
                return new AutoPlaySettings();
            }

            AutoPlaySettings? loaded = JsonSerializer.Deserialize<AutoPlaySettings>(File.ReadAllText(path), JsonOptions);
            return loaded ?? new AutoPlaySettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost: failed to load autoplay settings: {ex.Message}");
            return new AutoPlaySettings();
        }
    }

    private static void SaveUnsafe(AutoPlaySettings settings)
    {
        string path = GetSettingsPath();
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost: failed to save autoplay settings: {ex.Message}");
        }
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(ProjectSettings.GlobalizePath("user://"), "mods", ModEntry.ModId, "settings.json");
    }
}
