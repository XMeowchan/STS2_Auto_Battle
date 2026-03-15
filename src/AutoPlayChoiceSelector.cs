using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatAutoHost;

internal sealed class AutoPlayChoiceSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        List<CardModel> cards = options
            .Where(static card => card != null)
            .Distinct()
            .ToList();
        if (cards.Count == 0)
        {
            return Task.FromResult((IEnumerable<CardModel>)System.Array.Empty<CardModel>());
        }

        int count = System.Math.Min(maxSelect, cards.Count);
        if (count < minSelect)
        {
            count = System.Math.Min(minSelect, cards.Count);
        }

        bool preferLowValue = ShouldPreferLowValue(cards);
        List<CardModel> selected = (preferLowValue
                ? cards.OrderBy(GetKeepValue)
                : cards.OrderByDescending(GetKeepValue))
            .Take(count)
            .ToList();

        LogSelection(cards, selected, preferLowValue ? "prefer_low_value" : "prefer_high_value");
        return Task.FromResult((IEnumerable<CardModel>)selected);
    }

    public CardModel? GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        CardModel? selected = options
            .OrderByDescending(option => GetKeepValue(option.Card))
            .Select(option => option.Card)
            .FirstOrDefault();

        if (selected != null)
        {
            Log.Info($"CombatAutoHost[AI_PICK] reward selected={selected.Title}");
        }

        return selected;
    }

    private static bool ShouldPreferLowValue(IReadOnlyList<CardModel> cards)
    {
        if (cards.Any(IsClearlyDisposable))
        {
            return true;
        }

        return cards.All(static card => card.Pile?.Type == PileType.Hand)
            && cards.Any(static card => !card.CanPlay(out _, out _));
    }

    private static bool IsClearlyDisposable(CardModel card)
    {
        return card.Type == CardType.Status
            || card.Type == CardType.Curse
            || card.Keywords.Contains(CardKeyword.Unplayable)
            || card.Keywords.Contains(CardKeyword.Ethereal);
    }

    private static decimal GetKeepValue(CardModel card)
    {
        decimal score = 0m;

        if (card.Type == CardType.Curse)
        {
            return -100m;
        }

        if (card.Type == CardType.Status)
        {
            return -80m;
        }

        if (card.Keywords.Contains(CardKeyword.Unplayable))
        {
            score -= 60m;
        }

        if (card.Keywords.Contains(CardKeyword.Ethereal))
        {
            score -= 20m;
        }

        if (card.Type == CardType.Power)
        {
            score += 30m;
        }
        else if (card.Type == CardType.Attack)
        {
            score += 22m;
        }
        else if (card.Type == CardType.Skill)
        {
            score += 18m;
        }

        if (card.GainsBlock)
        {
            score += 10m;
        }

        if (card.ShouldRetainThisTurn)
        {
            score += 12m;
        }

        if (card.IsUpgradable)
        {
            score += 4m;
        }

        int resourceCost = System.Math.Max(0, card.EnergyCost.GetAmountToSpend()) + System.Math.Max(0, card.GetStarCostWithModifiers());
        score += resourceCost * 2m;

        if (card.CanPlay(out _, out _))
        {
            score += 8m;
        }

        return score;
    }

    private static void LogSelection(IReadOnlyList<CardModel> options, IReadOnlyList<CardModel> selected, string mode)
    {
        StringBuilder builder = new();
        builder.Append("CombatAutoHost[AI_PICK] mode=").Append(mode);
        builder.Append(" options=");
        builder.Append(string.Join(", ", options.Select(static card => card.Title)));
        builder.Append(" selected=");
        builder.Append(string.Join(", ", selected.Select(static card => card.Title)));
        Log.Info(builder.ToString());
    }
}
