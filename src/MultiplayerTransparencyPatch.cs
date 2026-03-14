using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace CombatAutoHost;

[HarmonyPatch(typeof(ModManager), nameof(ModManager.GetModNameList))]
internal static class MultiplayerTransparencyPatch
{
    private static void Postfix(ref List<string>? __result)
    {
        if (__result == null)
        {
            return;
        }

        __result.RemoveAll(static modName => modName.StartsWith(ModEntry.ModId, StringComparison.Ordinal));
        if (__result.Count == 0)
        {
            __result = null;
        }
    }
}
