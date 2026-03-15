using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatAutoHost;

internal static class AutoPlayComboRules
{
    public static IReadOnlyList<IComboRule> Create()
    {
        return new IComboRule[]
        {
            new IceCreamRule(),
            new ChemicalXRule(),
            new ArtOfWarRule(),
            new PocketwatchRule(),
            new BrilliantScarfRule(),
            new VelvetChokerRule(),
            new PenNibRule(),
            new RainbowRingRule(),
            new RazorToothRule(),
            new MummifiedHandRule(),
            new VoidFormRule(),
            new MasterPlannerRule(),
            new CharonsAshesRule(),
            new BansheesCryRule(),
            new SlowRule(),
            new DebuffSequencingRule(),
            new SturdyClampRule(),
            new RippleBasinRule(),
            new ParryingShieldRule(),
            new VambraceRule()
        };
    }

    private sealed class IceCreamRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<IceCream>() && metrics.EnergyCost > 0)
            {
                state.AddCombo(2m);
            }
        }
    }

    private sealed class ChemicalXRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<ChemicalX>() && (metrics.Candidate.Card.EnergyCost.CostsX || metrics.Candidate.Card.HasStarCostX))
            {
                state.AddCombo(18m + metrics.ExpectedXValue * 2m);
            }
        }
    }

    private sealed class ArtOfWarRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<ArtOfWar>() && metrics.IsAttack && metrics.EnergyCost + metrics.StarCost > 0)
            {
                state.AddCombo(1m);
            }
        }
    }

    private sealed class PocketwatchRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<Pocketwatch>() && context.CardsPlayedThisTurn >= 3 && metrics.Cards > 0)
            {
                state.AddCombo(2m + metrics.Cards);
            }
        }
    }

    private sealed class BrilliantScarfRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (!context.HasRelic<BrilliantScarf>()) return;
            if (context.CardsPlayedThisTurn == 4) { state.AddCombo(14m + (metrics.EnergyCost + metrics.StarCost) * 3m); return; }
            if (context.CardsPlayedThisTurn == 3 && context.HasHighValueFollowUp(metrics.Candidate.Card, 2)) state.AddCombo(8m);
        }
    }

    private sealed class VelvetChokerRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            int remainingSlots = 6 - context.CardsPlayedThisTurn;
            if (context.HasRelic<VelvetChoker>() && remainingSlots <= 2 && (metrics.EnergyCost + metrics.StarCost) <= 1 && state.ImmediateScore < 14m)
            {
                state.AddPenalty(6m + (2 - System.Math.Max(0, remainingSlots)));
            }
        }
    }

    private sealed class PenNibRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            int attackCount = context.AttacksPlayedThisCombat % 10;
            if (context.HasRelic<PenNib>() && metrics.IsAttack && attackCount == 9)
            {
                state.AddCombo(18m + metrics.RawDamageTotal * 0.75m);
                if (metrics.EffectiveDamageTotal < 10 && !metrics.HasLethal) state.AddPenalty(10m);
            }
        }
    }

    private sealed class RainbowRingRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (!context.HasRelic<RainbowRing>()) return;
            if (context.AttacksPlayedThisTurn == 0 && metrics.IsAttack) state.AddCombo(9m);
            if (context.SkillsPlayedThisTurn == 0 && metrics.IsSkill) state.AddCombo(9m);
            if (context.PowersPlayedThisTurn == 0 && metrics.IsPower) state.AddCombo(9m);
        }
    }

    private sealed class RazorToothRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<RazorTooth>() && metrics.IsUpgradable && (metrics.IsAttack || metrics.IsSkill))
            {
                state.AddCombo(metrics.ShouldRetain ? 9m : 6m);
            }
        }
    }

    private sealed class MummifiedHandRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (!context.HasRelic<MummifiedHand>() || !metrics.IsPower) return;
            state.AddCombo(context.HasHighValueFollowUp(metrics.Candidate.Card, 2, static card => card.Type != CardType.Power) ? 18m : 8m);
        }
    }

    private sealed class VoidFormRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            VoidFormPower? power = context.GetPower<VoidFormPower>();
            if (power == null || context.CardsPlayedThisTurn >= power.Amount) return;
            if (metrics.EnergyCost + metrics.StarCost >= 2) state.AddCombo(14m + (metrics.EnergyCost + metrics.StarCost) * 2m);
            else if (metrics.EnergyCost + metrics.StarCost == 0 && state.ImmediateScore < 16m) state.AddPenalty(4m);
        }
    }

    private sealed class MasterPlannerRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.GetPower<MasterPlannerPower>() != null && metrics.IsSkill) state.AddCombo(metrics.ShouldRetain ? 7m : 4m);
        }
    }

    private sealed class CharonsAshesRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (!context.HasRelic<CharonsAshes>() || !metrics.IsExhausting) return;
            decimal bonus = context.Enemies.Count > 1 ? 18m : 9m;
            if (context.Enemies.Any(static enemy => enemy.CurrentHp <= 6)) bonus += 6m;
            state.AddCombo(bonus);
        }
    }

    private sealed class BansheesCryRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (metrics.Candidate.Card is BansheesCry)
            {
                if (context.EtherealPlayedThisCombat > 0) state.AddCombo(10m + context.EtherealPlayedThisCombat * 2m);
                return;
            }

            if (metrics.IsEthereal && context.HandCards.Any(static card => card is BansheesCry)) state.AddCombo(6m);
        }
    }

    private sealed class SlowRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            int slow = metrics.Candidate.Target?.GetPowerAmount<SlowPower>() ?? 0;
            if (metrics.IsAttack && slow > 0 && !metrics.HasLethal && metrics.EffectiveDamageTotal < 12) state.AddPenalty(3m + slow);
        }
    }

    private sealed class DebuffSequencingRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (metrics.Candidate.Target == null) return;
            if (metrics.HasDebuff && context.HandCards.Any(card => card != metrics.Candidate.Card && card.CanPlay(out _, out _) && card.Type == CardType.Attack)) { state.AddCombo(8m); return; }
            if (!metrics.IsAttack || metrics.HasLethal) return;
            foreach (CardModel other in context.HandCards)
            {
                if (other == metrics.Candidate.Card || !other.CanPlay(out _, out _)) continue;
                DynamicVarSet vars = other.DynamicVars;
                vars.ClearPreview();
                other.UpdateDynamicVarPreview(CardPreviewMode.None, metrics.Candidate.Target, vars);
                if (AutoPlayScoring.GetPreviewInt(vars, "Weak", "WeakPower") > 0 || AutoPlayScoring.GetPreviewInt(vars, "Vulnerable", "VulnerablePower") > 0 || AutoPlayScoring.GetPreviewInt(vars, "Poison", "PoisonPower") > 0)
                {
                    state.AddPenalty(6m);
                    return;
                }
            }
        }
    }

    private sealed class SturdyClampRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<SturdyClamp>() && metrics.TotalBlock > 0 && context.CurrentBlock >= 10 && state.ImmediateScore < 18m) state.AddPenalty(6m);
        }
    }

    private sealed class RippleBasinRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.Mode == AutoPlayMode.Defensive && context.HasRelic<RippleBasin>() && metrics.IsAttack && context.AttacksPlayedThisTurn == 0 && !metrics.HasLethal && metrics.EffectiveDamageTotal < 5) state.AddPenalty(2m);
        }
    }

    private sealed class ParryingShieldRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<ParryingShield>() && metrics.TotalBlock > 0 && context.CurrentBlock >= 10 && state.ImmediateScore < 16m) state.AddPenalty(5m);
        }
    }

    private sealed class VambraceRule : IComboRule
    {
        public void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state)
        {
            if (context.HasRelic<Vambrace>() && metrics.TotalBlock > 0 && !context.GainedBlockThisCombat) state.AddCombo(12m + metrics.TotalBlock * 0.5m);
        }
    }
}
