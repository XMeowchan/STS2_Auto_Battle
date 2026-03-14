using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace CombatAutoHost;

[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi._Ready))]
internal static class CombatUiReadyPatch
{
    private static void Postfix(NCombatUi __instance)
    {
        try
        {
            CombatUiInjector.Attach(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"CombatAutoHost: failed to inject battle host button.\n{ex}");
        }
    }
}

internal static class CombatUiInjector
{
    private const string ButtonName = "CombatAutoHostButton";

    public static void Attach(NCombatUi ui)
    {
        if (ui.GetNodeOrNull<CombatHostedBattleButton>(ButtonName) != null)
        {
            return;
        }

        NPingButton? template = ui.GetNodeOrNull<NPingButton>("%PingButton");
        CombatHostedBattleButton button = CombatHostedBattleButton.Create(template);
        button.Name = ButtonName;

        ui.AddChild(button);
        if (template != null)
        {
            ui.MoveChild(button, template.GetIndex());
        }
    }
}
