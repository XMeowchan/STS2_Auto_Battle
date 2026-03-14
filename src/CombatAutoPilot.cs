using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatAutoHost;

internal sealed class CombatAutoPilot : IDisposable
{
    private const int MaxCardsPerTurn = 60;
    private const int IdlePollDelayMs = 100;
    private const int PostCardDelayMs = 120;
    private const int SuccessfulTurnDelayMs = 180;
    private const int EmptyTurnDelayMs = 250;

    private CancellationTokenSource? _cts;
    private int _runVersion;

    public bool IsActive { get; private set; }

    public event Action<bool>? StateChanged;

    public void Toggle()
    {
        if (IsActive)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        CancellationTokenSource cts = new();
        _cts = cts;
        int runVersion = unchecked(++_runVersion);
        SetActive(true);
        TaskHelper.RunSafely(RunAsync(cts, runVersion));
    }

    public void Stop()
    {
        CancellationTokenSource? cts = _cts;
        _cts = null;
        cts?.Cancel();
        SetActive(false);
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task RunAsync(CancellationTokenSource cts, int runVersion)
    {
        CancellationToken ct = cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!ShouldRemainRunning())
                {
                    break;
                }

                if (!CanAutoPlayNow())
                {
                    await Task.Delay(IdlePollDelayMs, ct);
                    continue;
                }

                Player? player = GetLocalPlayer();
                if (player == null)
                {
                    await Task.Delay(IdlePollDelayMs, ct);
                    continue;
                }

                bool playedAnyCard = false;
                int cardsPlayed = 0;
                HashSet<CardModel> attemptedCards = new();

                while (!ct.IsCancellationRequested && cardsPlayed < MaxCardsPerTurn && CanAutoPlayNow())
                {
                    CardModel? nextCard = GetNextPlayableCard(player, attemptedCards);
                    if (nextCard == null)
                    {
                        break;
                    }

                    attemptedCards.Add(nextCard);

                    try
                    {
                        await CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), nextCard, GetPreferredTarget(nextCard));
                        playedAnyCard = true;
                        cardsPlayed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"CombatAutoHost: failed to auto-play {nextCard.Id.Entry}: {ex.Message}");
                    }

                    await Task.Delay(PostCardDelayMs, ct);
                }

                if (ct.IsCancellationRequested || !ShouldRemainRunning())
                {
                    break;
                }

                if (CanAutoPlayNow())
                {
                    try
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"CombatAutoHost: failed to end turn automatically: {ex.Message}");
                    }
                }

                await Task.Delay(playedAnyCard ? SuccessfulTurnDelayMs : EmptyTurnDelayMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"CombatAutoHost: combat autopilot crashed.\n{ex}");
        }
        finally
        {
            cts.Dispose();

            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            if (_runVersion == runVersion)
            {
                SetActive(false);
            }
        }
    }

    private static Player? GetLocalPlayer()
    {
        if (RunManager.Instance == null)
        {
            return null;
        }

        var state = RunManager.Instance.DebugOnlyGetState();
        if (state == null)
        {
            return null;
        }

        return LocalContext.GetMe(state);
    }

    private static CardModel? GetNextPlayableCard(Player player, HashSet<CardModel> attemptedCards)
    {
        CardPile hand = PileType.Hand.GetPile(player);
        foreach (CardModel card in hand.Cards)
        {
            if (attemptedCards.Contains(card))
            {
                continue;
            }

            if (card.CanPlay(out _, out _))
            {
                return card;
            }
        }

        return null;
    }

    private static Creature? GetPreferredTarget(CardModel card)
    {
        if (card.TargetType != TargetType.AnyEnemy)
        {
            return null;
        }

        CombatState? combatState = card.CombatState;
        if (combatState == null)
        {
            return null;
        }

        foreach (Creature enemy in combatState.HittableEnemies)
        {
            if (enemy.IsAlive)
            {
                return enemy;
            }
        }

        return null;
    }

    private static bool CanAutoPlayNow()
    {
        if (!ShouldRemainRunning())
        {
            return false;
        }

        if (!CombatManager.Instance.IsPlayPhase)
        {
            return false;
        }

        if (NCombatRoom.Instance?.Ui?.Hand?.IsInCardSelection ?? false)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldRemainRunning()
    {
        if (CombatManager.Instance == null || !CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null || room.Mode != CombatRoomMode.ActiveCombat)
        {
            return false;
        }

        if (ActiveScreenContext.Instance == null)
        {
            return false;
        }

        return ActiveScreenContext.Instance.IsCurrent(room);
    }

    private void SetActive(bool isActive)
    {
        if (IsActive == isActive)
        {
            return;
        }

        IsActive = isActive;
        StateChanged?.Invoke(isActive);
    }
}
