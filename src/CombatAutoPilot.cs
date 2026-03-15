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
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
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
    private const int QueuePollDelayMs = 30;
    private const int QueueWaitTimeoutMs = 8000;

    private readonly IAutoPlayPolicy _policy = new ComboAwareAutoPlayPolicy();
    private readonly AutoPlayChoiceSelector _choiceSelector = new();

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
                    CardEvaluationResult? nextPlay = _policy.PickBestCandidate(player, attemptedCards);
                    if (nextPlay == null)
                    {
                        break;
                    }

                    CardModel nextCard = nextPlay.Candidate.Card;
                    attemptedCards.Add(nextCard);

                    try
                    {
                        Log.Info($"CombatAutoHost[AI_EXEC] try {DescribeCandidate(nextPlay.Candidate)} total={nextPlay.TotalScore:0.##} flags={nextPlay.ReasonFlags}");
                        if (await TryPlayCardAsync(nextPlay.Candidate, ct))
                        {
                            playedAnyCard = true;
                            cardsPlayed++;
                            Log.Info($"CombatAutoHost[AI_EXEC] queued {DescribeCandidate(nextPlay.Candidate)}");
                        }
                        else
                        {
                            Log.Info($"CombatAutoHost[AI_EXEC] rejected {DescribeCandidate(nextPlay.Candidate)}");
                        }
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
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, player.Creature.CombatState!.RoundNumber));
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

    private async Task<bool> TryPlayCardAsync(CardCandidate candidate, CancellationToken ct)
    {
        using IDisposable selectorScope = CardSelectCmd.PushSelector(_choiceSelector);
        if (!candidate.Card.TryManualPlay(candidate.Target))
        {
            return false;
        }

        await WaitForQueuedPlayAsync(candidate.Card, ct);
        return true;
    }

    private static string DescribeCandidate(CardCandidate candidate)
    {
        string target = candidate.Target?.Name ?? candidate.Card.TargetType.ToString();
        return $"{candidate.Card.Title} -> {target}";
    }

    private static async Task WaitForQueuedPlayAsync(CardModel card, CancellationToken ct)
    {
        bool sawQueueOrHandExit = false;
        int waitedMs = 0;

        while (!ct.IsCancellationRequested && waitedMs < QueueWaitTimeoutMs)
        {
            bool isQueued = NCardPlayQueue.Instance?.GetCardNode(card) != null;
            bool isStillInHand = card.Pile?.Type == PileType.Hand;

            if (isQueued || !isStillInHand)
            {
                sawQueueOrHandExit = true;
            }

            if (sawQueueOrHandExit && !isQueued)
            {
                return;
            }

            await Task.Delay(QueuePollDelayMs, ct);
            waitedMs += QueuePollDelayMs;
        }
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
