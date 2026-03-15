using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
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

internal sealed record CardEvaluationResult(CardCandidate Candidate, CandidateMetrics Metrics, decimal ImmediateScore, decimal ComboScore, decimal TotalScore, CardReasonFlags ReasonFlags);

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
        List<CardEvaluationResult> candidates = EvaluateCandidates(context, attemptedCards);
        if (candidates.Count == 0)
        {
            LogCandidateDecision(context, attemptedCards, candidates, null, "no_playable_candidates");
            return null;
        }

        CardEvaluationResult? selected = TryPickLethalCandidate(candidates);
        string stage = "lethal";

        if (selected == null)
        {
            selected = TryPickThreatResponseCandidate(context, candidates);
            stage = "threat_response";
        }

        if (selected == null)
        {
            selected = TryPickPowerCandidate(context, candidates);
            stage = "power";
        }

        if (selected == null)
        {
            selected = TryPickSetupCandidate(context, candidates);
            stage = "setup";
        }

        if (selected == null)
        {
            selected = TryPickAttackCandidate(candidates);
            stage = "attack";
        }

        selected ??= PickHighestScoreCandidate(candidates);
        if (stage == "attack" && !ReferenceEquals(selected, candidates.FirstOrDefault(static result => result.Metrics.IsAttack)))
        {
            stage = "highest_score";
        }

        LogCandidateDecision(context, attemptedCards, candidates, selected, stage);
        return selected;
    }

    public CardEvaluationResult? PickFallbackCandidate(Player player, HashSet<CardModel> attemptedCards)
    {
        AutoPlayContext context = AutoPlayContext.Create(player);
        List<CardEvaluationResult> candidates = EvaluateCandidates(context, attemptedCards);
        if (candidates.Count == 0)
        {
            return null;
        }

        CardEvaluationResult? bestBlockCandidate = null;
        int bestPreventedDamage = -1;
        int bestBlockResourceSpend = -1;
        int bestBlockExtraValue = -1;

        CardEvaluationResult? bestSpendCandidate = null;
        int bestSpend = -1;
        int bestSpendValue = -1;

        foreach (CardEvaluationResult result in candidates)
        {
            CandidateMetrics metrics = result.Metrics;

            int resourceSpend = metrics.EnergyCost + metrics.StarCost;
            int extraValue = metrics.TotalBlock
                + metrics.EffectiveDamageTotal
                + metrics.Cards * 2
                + (metrics.HasDebuff ? 4 : 0)
                + (metrics.HasBuff ? 3 : 0)
                + (metrics.IsPower ? 2 : 0);
            int blockValue = Math.Max(metrics.TotalBlock, metrics.Candidate.Card.GainsBlock ? 1 : 0);

            if (context.ThreatenedHpLoss > 0 && blockValue > 0)
            {
                int preventedDamage = Math.Min(blockValue, context.ThreatenedHpLoss);
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

    private static List<CardEvaluationResult> EvaluateCandidates(AutoPlayContext context, HashSet<CardModel> attemptedCards)
    {
        List<CardEvaluationResult> results = new();
        foreach (CardCandidate candidate in EnumerateCandidates(context, attemptedCards))
        {
            CandidateMetrics metrics = AutoPlayScoring.BuildMetrics(context, candidate);
            results.Add(EvaluateCandidate(context, candidate, metrics));
        }

        return results;
    }

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
            case TargetType.AnyEnemy:
                foreach (Creature creature in context.Enemies)
                {
                    if (card.IsValidTarget(creature))
                    {
                        yield return creature;
                    }
                }

                yield break;
            case TargetType.AnyAlly:
                foreach (Creature creature in context.LivingCreatures)
                {
                    if (card.IsValidTarget(creature))
                    {
                        yield return creature;
                    }
                }

                yield break;
            case TargetType.Self:
            case TargetType.AnyPlayer:
            case TargetType.Osty:
            case TargetType.None:
            case TargetType.AllEnemies:
            case TargetType.AllAllies:
            case TargetType.RandomEnemy:
                yield return null;
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

        return new CardEvaluationResult(candidate, metrics, decimal.Round(state.ImmediateScore, 2), decimal.Round(state.ComboScore, 2), decimal.Round(state.TotalScore, 2), state.Flags);
    }

    private static CardEvaluationResult? TryPickLethalCandidate(IReadOnlyList<CardEvaluationResult> candidates)
    {
        return candidates
            .Where(static result => result.Metrics.HasLethal)
            .OrderByDescending(static result => result.Metrics.KillCount)
            .ThenBy(static result => result.Metrics.Overkill)
            .ThenByDescending(static result => result.TotalScore)
            .FirstOrDefault();
    }

    private static CardEvaluationResult? TryPickThreatResponseCandidate(AutoPlayContext context, IReadOnlyList<CardEvaluationResult> candidates)
    {
        if (context.ThreatenedHpLoss <= 0)
        {
            return null;
        }

        CardEvaluationResult? bestBlock = candidates
            .Where(result => GetThreatReduction(context, result) > 0)
            .OrderByDescending(result => GetThreatReduction(context, result))
            .ThenByDescending(static result => result.Metrics.Cards)
            .ThenByDescending(static result => result.Metrics.Weak)
            .ThenByDescending(static result => result.Metrics.EnergyCost + result.Metrics.StarCost)
            .ThenByDescending(static result => result.TotalScore)
            .FirstOrDefault();
        if (bestBlock != null)
        {
            return bestBlock;
        }

        return candidates
            .Where(static result => result.Metrics.Cards > 0 || result.Metrics.Weak > 0 || result.Metrics.HasBuff || result.Metrics.IsPower)
            .OrderByDescending(result => GetThreatUtilityScore(context, result))
            .ThenByDescending(static result => result.TotalScore)
            .FirstOrDefault();
    }

    private static CardEvaluationResult? TryPickSetupCandidate(AutoPlayContext context, IReadOnlyList<CardEvaluationResult> candidates)
    {
        if (context.ThreatenedHpLoss > 0)
        {
            return null;
        }

        CardEvaluationResult? bestSetup = candidates
            .Where(static result => IsSetupCandidate(result.Metrics))
            .OrderByDescending(result => GetSetupScore(context, result))
            .FirstOrDefault();
        if (bestSetup == null)
        {
            return null;
        }

        if (context.CardsPlayedThisTurn <= 1)
        {
            return bestSetup;
        }

        if (GetSetupScore(context, bestSetup) >= 18m)
        {
            return bestSetup;
        }

        return null;
    }

    private static CardEvaluationResult? TryPickPowerCandidate(AutoPlayContext context, IReadOnlyList<CardEvaluationResult> candidates)
    {
        if (context.ThreatenedHpLoss > 0)
        {
            return null;
        }

        CardEvaluationResult? bestPower = candidates
            .Where(static result => result.Metrics.IsPower)
            .OrderByDescending(result => GetPowerScore(context, result))
            .FirstOrDefault();
        if (bestPower == null)
        {
            return null;
        }

        if (context.RoundNumber <= 3 || context.CardsPlayedThisTurn == 0 || context.PowersPlayedThisTurn == 0)
        {
            return bestPower;
        }

        if (GetPowerScore(context, bestPower) >= 20m)
        {
            return bestPower;
        }

        return null;
    }

    private static CardEvaluationResult PickHighestScoreCandidate(IReadOnlyList<CardEvaluationResult> candidates)
    {
        return candidates
            .OrderByDescending(static result => result.TotalScore)
            .First();
    }

    private static CardEvaluationResult? TryPickAttackCandidate(IReadOnlyList<CardEvaluationResult> candidates)
    {
        return candidates
            .Where(static result => result.Metrics.IsAttack)
            .OrderByDescending(static result => GetAttackScore(result))
            .FirstOrDefault();
    }

    private static int GetThreatReduction(AutoPlayContext context, CardEvaluationResult result)
    {
        int blockValue = Math.Max(result.Metrics.TotalBlock, result.Candidate.Card.GainsBlock ? 1 : 0);
        return Math.Min(blockValue, context.ThreatenedHpLoss);
    }

    private static decimal GetThreatUtilityScore(AutoPlayContext context, CardEvaluationResult result)
    {
        CandidateMetrics metrics = result.Metrics;
        decimal score = result.TotalScore;
        score += GetThreatReduction(context, result) * 4m;
        score += metrics.Cards * 8m;
        score += metrics.Weak * 10m;
        score += metrics.Dexterity * 6m;
        score += metrics.GainEnergy * 5m;
        score += metrics.GainStars * 5m;
        if (metrics.IsPower)
        {
            score += 6m;
        }

        return score;
    }

    private static bool IsSetupCandidate(CandidateMetrics metrics)
    {
        return !metrics.IsAttack && (metrics.IsPower || metrics.Cards > 0 || metrics.HasDebuff || metrics.HasBuff);
    }

    private static decimal GetSetupScore(AutoPlayContext context, CardEvaluationResult result)
    {
        CandidateMetrics metrics = result.Metrics;
        decimal score = result.TotalScore;
        score += metrics.Cards * 8m;
        score += (metrics.Weak + metrics.Vulnerable) * 6m;
        score += metrics.Poison * 4m;
        score += (metrics.Strength + metrics.Dexterity) * 5m;
        score += (metrics.GainEnergy + metrics.GainStars) * 5m;
        if (metrics.IsPower)
        {
            score += context.RoundNumber <= 2 ? 18m : 8m;
        }

        if (context.HasHighValueFollowUp(result.Candidate.Card, 0, static card => card.Type == CardType.Attack))
        {
            score += 8m;
        }

        return score;
    }

    private static decimal GetPowerScore(AutoPlayContext context, CardEvaluationResult result)
    {
        CandidateMetrics metrics = result.Metrics;
        decimal score = result.TotalScore;
        score += context.RoundNumber <= 2 ? 28m : (context.RoundNumber == 3 ? 16m : 6m);
        score += metrics.Cards * 6m;
        score += (metrics.GainEnergy + metrics.GainStars) * 7m;
        score += (metrics.Strength + metrics.Dexterity) * 6m;
        score += (metrics.Weak + metrics.Vulnerable + metrics.Poison) * 5m;
        return score;
    }

    private static decimal GetAttackScore(CardEvaluationResult result)
    {
        CandidateMetrics metrics = result.Metrics;
        decimal score = result.TotalScore;
        score += metrics.EffectiveDamageTotal * 0.75m;
        score += metrics.KillCount * 30m;
        return score;
    }

    private static bool ShouldSkipCard(CardModel card)
    {
        return SkippedByAutoplayIds.Contains(card.Id) || card.TargetType == TargetType.TargetedNoCreature || card.Type == CardType.Quest;
    }

    private static void LogCandidateDecision(AutoPlayContext context, HashSet<CardModel> attemptedCards, IReadOnlyList<CardEvaluationResult> candidates, CardEvaluationResult? selected, string stage)
    {
        StringBuilder header = new();
        header.Append("CombatAutoHost[AI] ");
        header.Append("turn=").Append(context.RoundNumber);
        header.Append(" hp=").Append(context.CurrentHp).Append('/').Append(context.MaxHp);
        header.Append(" block=").Append(context.CurrentBlock);
        header.Append(" incoming=").Append(context.IncomingAttackDamage);
        header.Append(" threatened=").Append(context.ThreatenedHpLoss);
        header.Append(" energy=").Append(context.Energy);
        header.Append(" stars=").Append(context.Stars);
        header.Append(" played=").Append(context.CardsPlayedThisTurn);
        header.Append(" hand=").Append(context.HandCards.Count);
        header.Append(" stage=").Append(stage);
        Log.Info(header.ToString());

        foreach (CardModel card in context.HandCards)
        {
            if (attemptedCards.Contains(card))
            {
                Log.Info($"CombatAutoHost[AI] hand {card.Title}: skipped=attempted");
                continue;
            }

            if (ShouldSkipCard(card))
            {
                Log.Info($"CombatAutoHost[AI] hand {card.Title}: skipped=filtered type={card.Type} target={card.TargetType}");
                continue;
            }

            if (!card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer))
            {
                string preventerText = preventer?.Id.Entry ?? "-";
                Log.Info($"CombatAutoHost[AI] hand {card.Title}: skipped=unplayable reason={reason} preventer={preventerText}");
                continue;
            }

            List<CardEvaluationResult> cardCandidates = candidates
                .Where(result => ReferenceEquals(result.Candidate.Card, card))
                .OrderByDescending(static result => result.TotalScore)
                .ToList();

            if (cardCandidates.Count == 0)
            {
                Log.Info($"CombatAutoHost[AI] hand {card.Title}: skipped=no_valid_target targetType={card.TargetType}");
                continue;
            }

            foreach (CardEvaluationResult result in cardCandidates)
            {
                string marker = ReferenceEquals(selected, result) ? "SELECT" : "option";
                Log.Info($"CombatAutoHost[AI] {marker} {DescribeCandidate(result, selected, stage)}");
            }
        }
    }

    private static string DescribeCandidate(CardEvaluationResult result, CardEvaluationResult? selected, string stage)
    {
        CandidateMetrics metrics = result.Metrics;
        string target = result.Candidate.Target?.Name ?? result.Candidate.Card.TargetType.ToString();
        StringBuilder text = new();
        text.Append(result.Candidate.Card.Title).Append(" -> ").Append(target);
        text.Append($" | total={result.TotalScore:0.##}");
        text.Append($" immediate={result.ImmediateScore:0.##}");
        text.Append($" combo={result.ComboScore:0.##}");
        text.Append($" cost={metrics.EnergyCost}e/{metrics.StarCost}s");
        text.Append($" dmg={metrics.EffectiveDamageTotal}");
        text.Append($" block={metrics.TotalBlock}");
        text.Append($" draw={metrics.Cards}");
        text.Append($" weak={metrics.Weak}");
        text.Append($" vuln={metrics.Vulnerable}");
        text.Append($" poison={metrics.Poison}");
        text.Append($" str={metrics.Strength}");
        text.Append($" dex={metrics.Dexterity}");
        text.Append($" gainE={metrics.GainEnergy}");
        text.Append($" gainS={metrics.GainStars}");
        text.Append($" type={result.Candidate.Card.Type}");
        text.Append($" flags={result.ReasonFlags}");

        if (!ReferenceEquals(selected, result) && selected != null)
        {
            decimal delta = selected.TotalScore - result.TotalScore;
            text.Append($" | not_selected=selected_{stage}");
            text.Append($" selected_card={selected.Candidate.Card.Title}");
            text.Append($" selected_total={selected.TotalScore:0.##}");
            text.Append($" delta={delta:0.##}");
        }

        return text.ToString();
    }
}
