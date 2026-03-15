using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace CombatAutoHost;

[Flags]
internal enum CardReasonFlags
{
    None = 0,
    Lethal = 1,
    Damage = 2,
    Block = 4,
    Draw = 8,
    Debuff = 16,
    Buff = 32,
    Power = 64,
    Combo = 128,
    ResourceHold = 256,
    Penalty = 512
}

internal readonly record struct CardCandidate(CardModel Card, Creature? Target);

internal sealed record CardEvaluationResult(CardCandidate Candidate, decimal ImmediateScore, decimal ComboScore, decimal TotalScore, CardReasonFlags ReasonFlags);

internal interface IAutoPlayPolicy
{
    CardEvaluationResult? PickBestCandidate(Player player, HashSet<CardModel> attemptedCards);
    CardEvaluationResult? PickFallbackCandidate(Player player, HashSet<CardModel> attemptedCards);
    decimal GetTurnEndThreshold(AutoPlayMode mode);
}

internal interface IComboRule
{
    void Apply(AutoPlayContext context, CandidateMetrics metrics, CardEvaluationState state);
}

internal sealed class CardEvaluationState
{
    public decimal ImmediateScore;
    public decimal ComboScore;
    public CardReasonFlags Flags;
    public decimal TotalScore => ImmediateScore + ComboScore;
    public void AddReason(CardReasonFlags flag) => Flags |= flag;
    public void AddCombo(decimal amount, CardReasonFlags flags = CardReasonFlags.Combo) { ComboScore += amount; Flags |= flags; }
    public void AddPenalty(decimal amount) { ComboScore -= amount; Flags |= CardReasonFlags.Combo | CardReasonFlags.Penalty; }
}

internal sealed class CandidateMetrics
{
    public required CardCandidate Candidate { get; init; }
    public required DynamicVarSet Vars { get; init; }
    public required int EnergyCost { get; init; }
    public required int StarCost { get; init; }
    public required int ExpectedXValue { get; init; }
    public required int Damage { get; init; }
    public required int Damage2 { get; init; }
    public required int Hits { get; init; }
    public required int Block { get; init; }
    public required int Block2 { get; init; }
    public required int Cards { get; init; }
    public required int GainEnergy { get; init; }
    public required int GainStars { get; init; }
    public required int Weak { get; init; }
    public required int Vulnerable { get; init; }
    public required int Poison { get; init; }
    public required int Strength { get; init; }
    public required int Dexterity { get; init; }
    public required int RawDamageTotal { get; init; }
    public required int EffectiveDamageTotal { get; init; }
    public required int KillCount { get; init; }
    public required int Overkill { get; init; }
    public bool IsAttack => Candidate.Card.Type == CardType.Attack;
    public bool IsSkill => Candidate.Card.Type == CardType.Skill;
    public bool IsPower => Candidate.Card.Type == CardType.Power;
    public bool IsEthereal => Candidate.Card.Keywords.Contains(CardKeyword.Ethereal);
    public bool IsExhausting => Candidate.Card.ExhaustOnNextPlay || Candidate.Card.Keywords.Contains(CardKeyword.Exhaust);
    public bool ShouldRetain => Candidate.Card.ShouldRetainThisTurn;
    public bool IsUpgradable => Candidate.Card.IsUpgradable;
    public bool IsRandomTarget => Candidate.Card.TargetType == TargetType.RandomEnemy;
    public bool HasLethal => KillCount > 0;
    public int TotalBlock => Block + Block2;
    public bool HasDebuff => Weak > 0 || Vulnerable > 0 || Poison > 0;
    public bool HasBuff => Strength > 0 || Dexterity > 0 || GainEnergy > 0 || GainStars > 0;
}

internal sealed class AutoPlayContext
{
    public required Player Player { get; init; }
    public required CombatState CombatState { get; init; }
    public required AutoPlayMode Mode { get; init; }
    public required IReadOnlyList<CardModel> HandCards { get; init; }
    public required IReadOnlyList<Creature> LivingCreatures { get; init; }
    public required IReadOnlyList<Creature> Enemies { get; init; }
    public required int Energy { get; init; }
    public required int Stars { get; init; }
    public required int CurrentBlock { get; init; }
    public required int CurrentHp { get; init; }
    public required int MaxHp { get; init; }
    public required int IncomingAttackDamage { get; init; }
    public required int RoundNumber { get; init; }
    public required int CardsPlayedThisTurn { get; init; }
    public required int AttacksPlayedThisTurn { get; init; }
    public required int SkillsPlayedThisTurn { get; init; }
    public required int PowersPlayedThisTurn { get; init; }
    public required int AttacksPlayedThisCombat { get; init; }
    public required int EtherealPlayedThisCombat { get; init; }
    public required bool GainedBlockThisCombat { get; init; }

    public int ThreatenedHpLoss => Math.Max(0, IncomingAttackDamage - CurrentBlock);

    public T? GetRelic<T>() where T : RelicModel => Player.GetRelic<T>();
    public bool HasRelic<T>() where T : RelicModel => GetRelic<T>() != null;
    public T? GetPower<T>() where T : PowerModel => Player.Creature.GetPower<T>();
    public int GetPowerAmount<T>() where T : PowerModel => Player.Creature.GetPowerAmount<T>();

    public bool HasHighValueFollowUp(CardModel current, int minCost, Func<CardModel, bool>? filter = null)
    {
        foreach (CardModel card in HandCards)
        {
            if (card == current || (filter != null && !filter(card)) || !card.CanPlay(out _, out _))
            {
                continue;
            }

            int cost = card.EnergyCost.CostsX || card.HasStarCostX
                ? Math.Max(Energy + Stars, card.EnergyCost.GetAmountToSpend() + Math.Max(0, card.GetStarCostWithModifiers()))
                : card.EnergyCost.GetAmountToSpend() + Math.Max(0, card.GetStarCostWithModifiers());
            if (cost >= minCost)
            {
                return true;
            }
        }

        return false;
    }

    public static AutoPlayContext Create(Player player)
    {
        CombatState state = player.Creature.CombatState!;
        return new AutoPlayContext
        {
            Player = player,
            CombatState = state,
            Mode = AutoPlaySettingsStore.CurrentMode,
            HandCards = PileType.Hand.GetPile(player).Cards.ToList(),
            LivingCreatures = state.Creatures.Where(static creature => creature != null && creature.IsAlive).ToList(),
            Enemies = state.HittableEnemies.Where(static creature => creature != null && creature.IsAlive).ToList(),
            Energy = player.PlayerCombatState?.Energy ?? 0,
            Stars = player.PlayerCombatState?.Stars ?? 0,
            CurrentBlock = player.Creature.Block,
            CurrentHp = player.Creature.CurrentHp,
            MaxHp = Math.Max(1, player.Creature.MaxHp),
            IncomingAttackDamage = CalculateIncomingAttackDamage(player.Creature, state.HittableEnemies.Where(static creature => creature != null && creature.IsAlive).ToList()),
            RoundNumber = state.RoundNumber,
            CardsPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state) && entry.CardPlay.Card.Owner == player),
            AttacksPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state) && entry.CardPlay.Card.Owner == player && entry.CardPlay.Card.Type == CardType.Attack),
            SkillsPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state) && entry.CardPlay.Card.Owner == player && entry.CardPlay.Card.Type == CardType.Skill),
            PowersPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn(state) && entry.CardPlay.Card.Owner == player && entry.CardPlay.Card.Type == CardType.Power),
            AttacksPlayedThisCombat = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.CardPlay.Card.Owner == player && entry.CardPlay.Card.Type == CardType.Attack),
            EtherealPlayedThisCombat = CombatManager.Instance.History.CardPlaysFinished.Count(entry => entry.CardPlay.Card.Owner == player && entry.WasEthereal),
            GainedBlockThisCombat = CombatManager.Instance.History.Entries.OfType<BlockGainedEntry>().Any(entry => entry.Receiver == player.Creature && entry.Amount > 0)
        };
    }

    private static int CalculateIncomingAttackDamage(Creature playerCreature, IReadOnlyList<Creature> enemies)
    {
        int total = 0;
        foreach (Creature enemy in enemies)
        {
            if (enemy.Monster == null || !enemy.Monster.IntendsToAttack)
            {
                continue;
            }

            foreach (AbstractIntent intent in enemy.Monster.NextMove.Intents)
            {
                if (intent is AttackIntent attackIntent)
                {
                    total += attackIntent.GetTotalDamage(new[] { playerCreature }, enemy);
                }
            }
        }

        return total;
    }
}

internal sealed class ComboAwareAutoPlayPolicy : IAutoPlayPolicy
{
    private static readonly IReadOnlyList<IComboRule> ComboRules = AutoPlayComboRules.Create();
    private static readonly HashSet<ModelId> SkippedByAutoplayIds = new();

    public CardEvaluationResult? PickBestCandidate(Player player, HashSet<CardModel> attemptedCards)
    {
        AutoPlayContext context = AutoPlayContext.Create(player);
        CardEvaluationResult? best = null;
        foreach (CardCandidate candidate in EnumerateCandidates(context, attemptedCards))
        {
            CardEvaluationResult result = EvaluateCandidate(context, candidate);
            if (best == null || result.TotalScore > best.TotalScore)
            {
                best = result;
            }
        }

        return best;
    }

    public CardEvaluationResult? PickFallbackCandidate(Player player, HashSet<CardModel> attemptedCards)
    {
        AutoPlayContext context = AutoPlayContext.Create(player);

        CardEvaluationResult? bestBlockCandidate = null;
        int bestPreventedDamage = -1;
        int bestBlockResourceSpend = -1;
        int bestBlockExtraValue = -1;

        CardEvaluationResult? bestSpendCandidate = null;
        int bestSpend = -1;
        int bestSpendValue = -1;

        foreach (CardCandidate candidate in EnumerateCandidates(context, attemptedCards))
        {
            CandidateMetrics metrics = AutoPlayScoring.BuildMetrics(context, candidate);
            CardEvaluationResult result = EvaluateCandidate(context, candidate, metrics);

            int resourceSpend = metrics.EnergyCost + metrics.StarCost;
            int extraValue = metrics.TotalBlock + metrics.EffectiveDamageTotal + metrics.Cards * 2;

            if (context.ThreatenedHpLoss > 0 && metrics.TotalBlock > 0)
            {
                int preventedDamage = Math.Min(metrics.TotalBlock, context.ThreatenedHpLoss);
                if (preventedDamage > bestPreventedDamage
                    || (preventedDamage == bestPreventedDamage && resourceSpend > bestBlockResourceSpend)
                    || (preventedDamage == bestPreventedDamage && resourceSpend == bestBlockResourceSpend && extraValue > bestBlockExtraValue)
                    || (preventedDamage == bestPreventedDamage && resourceSpend == bestBlockResourceSpend && extraValue == bestBlockExtraValue && (bestBlockCandidate == null || result.TotalScore > bestBlockCandidate.TotalScore)))
                {
                    bestPreventedDamage = preventedDamage;
                    bestBlockResourceSpend = resourceSpend;
                    bestBlockExtraValue = extraValue;
                    bestBlockCandidate = result;
                }
            }

            if (resourceSpend > bestSpend
                || (resourceSpend == bestSpend && extraValue > bestSpendValue)
                || (resourceSpend == bestSpend && extraValue == bestSpendValue && (bestSpendCandidate == null || result.TotalScore > bestSpendCandidate.TotalScore)))
            {
                bestSpend = resourceSpend;
                bestSpendValue = extraValue;
                bestSpendCandidate = result;
            }
        }

        return bestBlockCandidate ?? bestSpendCandidate;
    }

    public decimal GetTurnEndThreshold(AutoPlayMode mode) => mode switch
    {
        AutoPlayMode.Defensive => -8m,
        AutoPlayMode.Aggressive => -20m,
        _ => -12m
    };

    private static IEnumerable<CardCandidate> EnumerateCandidates(AutoPlayContext context, HashSet<CardModel> attemptedCards)
    {
        foreach (CardModel card in context.HandCards)
        {
            if (attemptedCards.Contains(card) || !card.CanPlay(out _, out _) || ShouldSkipCard(card))
            {
                continue;
            }

            bool yielded = false;
            foreach (Creature? target in GetTargets(context, card))
            {
                yielded = true;
                yield return new CardCandidate(card, target);
            }

            if (!yielded && card.TargetType is TargetType.None or TargetType.AllEnemies or TargetType.AllAllies or TargetType.RandomEnemy)
            {
                yield return new CardCandidate(card, null);
            }
        }
    }

    private static IEnumerable<Creature?> GetTargets(AutoPlayContext context, CardModel card)
    {
        switch (card.TargetType)
        {
            case TargetType.Self:
                yield return card.Owner.Creature;
                yield break;
            case TargetType.Osty:
                if (card.Owner.Osty != null)
                {
                    yield return card.Owner.Osty;
                }

                yield break;
            case TargetType.AnyEnemy:
            case TargetType.AnyAlly:
            case TargetType.AnyPlayer:
                foreach (Creature creature in context.LivingCreatures)
                {
                    if (card.IsValidTarget(creature))
                    {
                        yield return creature;
                    }
                }

                yield break;
        }
    }

    private static CardEvaluationResult EvaluateCandidate(AutoPlayContext context, CardCandidate candidate)
    {
        CandidateMetrics metrics = AutoPlayScoring.BuildMetrics(context, candidate);
        return EvaluateCandidate(context, candidate, metrics);
    }

    private static CardEvaluationResult EvaluateCandidate(AutoPlayContext context, CardCandidate candidate, CandidateMetrics metrics)
    {
        CardEvaluationState state = new() { ImmediateScore = AutoPlayScoring.ScoreImmediate(context, metrics) };
        AutoPlayScoring.AddImmediateReasons(metrics, state);
        foreach (IComboRule rule in ComboRules)
        {
            rule.Apply(context, metrics, state);
        }

        return new CardEvaluationResult(candidate, decimal.Round(state.ImmediateScore, 2), decimal.Round(state.ComboScore, 2), decimal.Round(state.TotalScore, 2), state.Flags);
    }

    private static bool ShouldSkipCard(CardModel card)
    {
        return SkippedByAutoplayIds.Contains(card.Id) || card.TargetType == TargetType.TargetedNoCreature || card.Type == CardType.Quest;
    }
}
