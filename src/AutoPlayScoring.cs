using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatAutoHost;

internal static class AutoPlayScoring
{
    private readonly record struct ScoreWeights(decimal DamageWeight, decimal BlockWeight, decimal DrawWeight, decimal DebuffWeight, decimal BuffWeight, decimal ResourceGainWeight, decimal PowerWeight, decimal EnergyCostPenalty, decimal StarCostPenalty, decimal ExhaustPenalty, decimal RetainPenalty);

    public static CandidateMetrics BuildMetrics(AutoPlayContext context, CardCandidate candidate)
    {
        DynamicVarSet vars = candidate.Card.DynamicVars;
        vars.ClearPreview();
        candidate.Card.UpdateDynamicVarPreview(CardPreviewMode.None, candidate.Target, vars);

        int damage = GetPreviewInt(vars, "Damage", "CalculatedDamage");
        int damage2 = GetPreviewInt(vars, "Damage2");
        int hits = Math.Max(1, GetPreviewInt(vars, "Hits", "CalculatedHits"));
        (int rawDamage, int effectiveDamage, int killCount, int overkill) = CalculateDamageTotals(context, candidate, damage, damage2, hits);

        return new CandidateMetrics
        {
            Candidate = candidate,
            Vars = vars,
            EnergyCost = GetExpectedEnergyCost(context, candidate.Card),
            StarCost = GetExpectedStarCost(context, candidate.Card),
            ExpectedXValue = GetExpectedXValue(context, candidate.Card),
            Damage = damage,
            Damage2 = damage2,
            Hits = hits,
            Block = GetPreviewInt(vars, "Block", "CalculatedBlock"),
            Block2 = GetPreviewInt(vars, "Block2"),
            Cards = GetPreviewInt(vars, "Cards", "CalculatedCards"),
            GainEnergy = GetPreviewInt(vars, "Energy"),
            GainStars = GetPreviewInt(vars, "Stars"),
            Weak = GetPreviewInt(vars, "Weak", "WeakPower"),
            Vulnerable = GetPreviewInt(vars, "Vulnerable", "VulnerablePower"),
            Poison = GetPreviewInt(vars, "Poison", "PoisonPower"),
            Strength = GetPreviewInt(vars, "Strength", "StrengthPower"),
            Dexterity = GetPreviewInt(vars, "Dexterity", "DexterityPower"),
            RawDamageTotal = rawDamage,
            EffectiveDamageTotal = effectiveDamage,
            KillCount = killCount,
            Overkill = overkill
        };
    }

    public static decimal ScoreImmediate(AutoPlayContext context, CandidateMetrics metrics)
    {
        ScoreWeights weights = GetWeights(context);
        decimal score = 0m;

        if (metrics.HasLethal)
        {
            score += 1000m + 12m * metrics.KillCount - 0.5m * metrics.Overkill;
        }

        score += metrics.EffectiveDamageTotal * weights.DamageWeight;
        score += metrics.TotalBlock * weights.BlockWeight;
        score += metrics.Cards * weights.DrawWeight;
        score += (metrics.Weak + metrics.Vulnerable) * weights.DebuffWeight;
        score += metrics.Poison * (weights.DebuffWeight - 0.5m);
        score += (metrics.Strength + metrics.Dexterity) * weights.BuffWeight;
        score += metrics.GainEnergy * weights.ResourceGainWeight;
        score += metrics.GainStars * (weights.ResourceGainWeight + 1m);

        if (metrics.IsPower)
        {
            decimal roundMultiplier = context.RoundNumber <= 1 ? 1m : (context.RoundNumber == 2 ? 0.7m : 0.35m);
            score += weights.PowerWeight * roundMultiplier;
        }

        int threatenedHpLoss = context.ThreatenedHpLoss;
        if (threatenedHpLoss > 0)
        {
            int preventedHpLoss = Math.Min(metrics.TotalBlock, threatenedHpLoss);
            decimal preventedHpLossWeight = context.Mode switch
            {
                AutoPlayMode.Defensive => 22m,
                AutoPlayMode.Aggressive => 14m,
                _ => 18m
            };
            score += preventedHpLoss * preventedHpLossWeight;

            if (!metrics.HasLethal)
            {
                int unblockedHpLoss = Math.Max(0, threatenedHpLoss - metrics.TotalBlock);
                decimal unblockedHpLossWeight = context.Mode switch
                {
                    AutoPlayMode.Defensive => 6.5m,
                    AutoPlayMode.Aggressive => 3m,
                    _ => 4.5m
                };
                score -= unblockedHpLoss * unblockedHpLossWeight;
            }
        }

        if (metrics.EnergyCost + metrics.StarCost > 0)
        {
            score += metrics.EnergyCost * 9m;
            score += metrics.StarCost * 11m;
        }

        score -= metrics.EnergyCost * weights.EnergyCostPenalty;
        score -= metrics.StarCost * weights.StarCostPenalty;
        if (metrics.ShouldRetain)
        {
            score -= weights.RetainPenalty;
        }

        if (metrics.IsExhausting)
        {
            score -= weights.ExhaustPenalty;
        }

        if (metrics.IsRandomTarget)
        {
            score -= 2m;
        }

        return score;
    }

    public static void AddImmediateReasons(CandidateMetrics metrics, CardEvaluationState state)
    {
        if (metrics.HasLethal) state.AddReason(CardReasonFlags.Lethal);
        if (metrics.EffectiveDamageTotal > 0) state.AddReason(CardReasonFlags.Damage);
        if (metrics.TotalBlock > 0) state.AddReason(CardReasonFlags.Block);
        if (metrics.Cards > 0) state.AddReason(CardReasonFlags.Draw);
        if (metrics.HasDebuff) state.AddReason(CardReasonFlags.Debuff);
        if (metrics.HasBuff) state.AddReason(CardReasonFlags.Buff);
        if (metrics.IsPower) state.AddReason(CardReasonFlags.Power);
    }

    public static int GetPreviewInt(DynamicVarSet vars, params string[] aliases)
    {
        foreach (string alias in aliases)
        {
            if (vars.TryGetValue(alias, out DynamicVar? value))
            {
                return (int)Math.Round(value.PreviewValue, MidpointRounding.AwayFromZero);
            }
        }

        return 0;
    }

    private static ScoreWeights GetWeights(AutoPlayContext context)
    {
        decimal hpRatio = (decimal)context.CurrentHp / Math.Max(1, context.MaxHp);
        decimal risk = 1m - hpRatio;
        decimal blockBias = context.CurrentBlock == 0 ? 0.6m : 0m;
        return context.Mode switch
        {
            AutoPlayMode.Defensive => new ScoreWeights(7m, 3.8m + risk * 2.2m + blockBias, 4.5m, 4.8m, 4m, 4m, 10m, 0.15m, 0.2m, 3m, 1.25m),
            AutoPlayMode.Aggressive => new ScoreWeights(9.5m, 1.6m + risk, 3.5m, 4m, 3.8m, 3.5m, 8m, 0.1m, 0.15m, 1.2m, 0.8m),
            _ => new ScoreWeights(8m, 2.6m + risk * 1.7m + blockBias, 4m, 4.4m, 4.2m, 4m, 11m, 0.12m, 0.18m, 2m, 1m)
        };
    }

    private static int GetExpectedEnergyCost(AutoPlayContext context, CardModel card)
    {
        return card.EnergyCost.CostsX ? context.Energy : Math.Max(0, card.EnergyCost.GetAmountToSpend());
    }

    private static int GetExpectedStarCost(AutoPlayContext context, CardModel card)
    {
        return card.HasStarCostX ? context.Stars : Math.Max(0, card.GetStarCostWithModifiers());
    }

    private static int GetExpectedXValue(AutoPlayContext context, CardModel card)
    {
        int bonus = context.HasRelic<ChemicalX>() ? 2 : 0;
        if (card.EnergyCost.CostsX) return context.Energy + bonus;
        if (card.HasStarCostX) return context.Stars + bonus;
        return 0;
    }

    private static (int RawDamage, int EffectiveDamage, int KillCount, int Overkill) CalculateDamageTotals(AutoPlayContext context, CardCandidate candidate, int damage, int damage2, int hits)
    {
        int baseDamage = Math.Max(0, damage) + Math.Max(0, damage2);
        if (baseDamage <= 0) return (0, 0, 0, 0);

        if (candidate.Card.TargetType == TargetType.AllEnemies)
        {
            int rawSum = 0;
            int effectiveSum = 0;
            int killCount = 0;
            int overkill = 0;
            foreach (Creature enemy in context.Enemies)
            {
                int raw = baseDamage * hits;
                int effective = Math.Max(0, raw - enemy.Block);
                rawSum += raw;
                effectiveSum += effective;
                if (effective >= enemy.CurrentHp)
                {
                    killCount++;
                    overkill += Math.Max(0, effective - enemy.CurrentHp);
                }
            }

            return (rawSum, effectiveSum, killCount, overkill);
        }

        if (candidate.Card.TargetType == TargetType.RandomEnemy)
        {
            if (context.Enemies.Count == 0) return (0, 0, 0, 0);
            int rawSum = 0;
            int effectiveSum = 0;
            int killCount = 0;
            foreach (Creature enemy in context.Enemies)
            {
                int raw = baseDamage * hits;
                int effective = Math.Max(0, raw - enemy.Block);
                rawSum += raw;
                effectiveSum += effective;
                if (effective >= enemy.CurrentHp) killCount++;
            }

            return (rawSum / context.Enemies.Count, effectiveSum / context.Enemies.Count, killCount > 0 ? 1 : 0, 0);
        }

        if (candidate.Target == null) return (0, 0, 0, 0);
        int totalRaw = baseDamage * hits;
        int totalEffective = Math.Max(0, totalRaw - candidate.Target.Block);
        bool lethal = totalEffective >= candidate.Target.CurrentHp;
        return (totalRaw, totalEffective, lethal ? 1 : 0, lethal ? Math.Max(0, totalEffective - candidate.Target.CurrentHp) : 0);
    }
}
