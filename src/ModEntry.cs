using System.Reflection;
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

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            _harmony = new Harmony($"openai.codex.sts2.{ModId.ToLowerInvariant()}");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            _initialized = true;

            Log.Info($"{ModId} initialized.", 2);
        }
    }
}
