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

[HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
internal static class CombatUiActivatePatch
{
    private static void Postfix(NCombatUi __instance)
    {
        try
        {
            CombatUiInjector.Attach(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"CombatAutoHost: failed to attach battle host button during activate.\n{ex}");
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

        NEndTurnButton? template = ui.GetNodeOrNull<NEndTurnButton>("%EndTurnButton");
        CombatHostedBattleButton button = CombatHostedBattleButton.Create(template);
        button.Name = ButtonName;

        if (template != null)
        {
            template.AddChild(button);
            button.ZIndex = template.ZIndex + 1;
        }
        else
        {
            ui.AddChild(button);
        }

        button.InitializeButton();
        Log.Info("CombatAutoHost: attached auto button to combat UI.");
    }
}
