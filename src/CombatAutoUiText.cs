using System;
using System.Collections.Generic;
using System.Text.Json;
using GFileAccess = Godot.FileAccess;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CombatAutoHost;

internal static class CombatAutoUiText
{
    private const string DefaultLocale = "en_us";
    private const string LocalizationBasePath = "res://CombatAutoHost/localization";

    public const string ButtonInactiveKey = "button.inactive";
    public const string ButtonActiveKey = "button.active";
    public const string ToastEnabledKey = "toast.enabled";
    public const string ToastDisabledKey = "toast.disabled";
    public const string ToastModeBalancedKey = "toast.mode_balanced";
    public const string ToastModeDefensiveKey = "toast.mode_defensive";
    public const string ToastModeAggressiveKey = "toast.mode_aggressive";
    public const string ToastUpdateReadyKey = "toast.update_ready";
    public const string ToastUpdateInstalledKey = "toast.update_installed";
    public const string ToastUpdateFailedKey = "toast.update_failed";

    private static readonly Dictionary<string, string> FallbackTranslations = new(StringComparer.Ordinal)
    {
        [ButtonInactiveKey] = "AUTO",
        [ButtonActiveKey] = "AUTO ON",
        [ToastEnabledKey] = "Combat Auto Host Enabled",
        [ToastDisabledKey] = "Combat Auto Host Disabled",
        [ToastModeBalancedKey] = "Autoplay Mode: Balanced",
        [ToastModeDefensiveKey] = "Autoplay Mode: Defensive",
        [ToastModeAggressiveKey] = "Autoplay Mode: Aggressive",
        [ToastUpdateReadyKey] = "Update {0} downloaded. Restart the game to apply it.",
        [ToastUpdateInstalledKey] = "Combat Auto Host updated to {0}.",
        [ToastUpdateFailedKey] = "Auto-update failed: {0}"
    };

    private static Dictionary<string, string>? _cachedTranslations;
    private static string? _cachedLocale;

    public static string Get(string key)
    {
        IReadOnlyDictionary<string, string> translations = GetTranslations();
        if (translations.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return FallbackTranslations.TryGetValue(key, out string? fallback) ? fallback : key;
    }

    public static string GetModeToast(AutoPlayMode mode)
    {
        return mode switch
        {
            AutoPlayMode.Balanced => Get(ToastModeBalancedKey),
            AutoPlayMode.Defensive => Get(ToastModeDefensiveKey),
            _ => Get(ToastModeAggressiveKey)
        };
    }

    public static string Format(string key, params object[] args)
    {
        string value = Get(key);
        return args.Length == 0 ? value : string.Format(value, args);
    }

    private static IReadOnlyDictionary<string, string> GetTranslations()
    {
        string locale = GetCurrentLocale();
        if (_cachedTranslations != null && string.Equals(_cachedLocale, locale, StringComparison.Ordinal))
        {
            return _cachedTranslations;
        }

        _cachedLocale = locale;
        _cachedTranslations = LoadTranslations(locale);
        return _cachedTranslations;
    }

    private static string GetCurrentLocale()
    {
        string locale = TranslationServer.GetLocale();
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultLocale;
        }

        return locale.ToLowerInvariant().Replace('-', '_');
    }

    private static Dictionary<string, string> LoadTranslations(string locale)
    {
        foreach (string path in GetLocalizationPaths(locale))
        {
            Dictionary<string, string>? translations = TryLoadTranslations(path);
            if (translations != null && translations.Count > 0)
            {
                return translations;
            }
        }

        return new Dictionary<string, string>(FallbackTranslations, StringComparer.Ordinal);
    }

    private static IEnumerable<string> GetLocalizationPaths(string locale)
    {
        yield return $"{LocalizationBasePath}/{locale}.json";

        if (locale.StartsWith("zh", StringComparison.Ordinal))
        {
            yield return $"{LocalizationBasePath}/zh_cn.json";
        }

        yield return $"{LocalizationBasePath}/{DefaultLocale}.json";
    }

    private static Dictionary<string, string>? TryLoadTranslations(string path)
    {
        try
        {
            if (!GFileAccess.FileExists(path))
            {
                return null;
            }

            using var file = GFileAccess.Open(path, GFileAccess.ModeFlags.Read);
            if (file == null)
            {
                return null;
            }

            string json = file.GetAsText();
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            Dictionary<string, string>? translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return translations == null
                ? null
                : new Dictionary<string, string>(translations, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn($"CombatAutoHost: failed to load localization '{path}': {ex.Message}");
            return null;
        }
    }
}
