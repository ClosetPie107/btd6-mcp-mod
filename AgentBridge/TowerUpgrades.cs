using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

internal sealed class UpgradeFailureV1
{
    public int Index { get; init; }
    public int Path { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class UpgradeExecutionV1
{
    public int Index { get; init; }
    public int Path { get; init; }
    public int Tier { get; init; }
    public string UpgradeId { get; init; } = "";
    public float Cost { get; init; }
    public int ExecutionRound { get; init; }
    public float? ExecutionSeconds { get; init; }
    public float? LatenessSeconds { get; init; }
}

internal sealed class UnifiedUpgradeResultV1
{
    public bool Completed { get; init; }
    public List<int> AppliedPaths { get; init; } = [];
    public List<UpgradeExecutionV1> AppliedSteps { get; init; } = [];
    public TowerInfoV1? Tower { get; init; }
    public double? Cash { get; init; }
    public UpgradeFailureV1? Failure { get; init; }
    public string Status { get; init; } = "completed";
    public string? ScheduleId { get; init; }
    public int NextIndex { get; init; }
    public float? NextUpgradeCost { get; init; }
    public string? WaitingFor { get; init; }
}

internal sealed class ScheduledUpgradeInfoV1
{
    public string ScheduleId { get; init; } = "";
    public string TowerId { get; init; } = "";
    public int[] UpgradeSequence { get; init; } = [];
    public List<int> AppliedPaths { get; init; } = [];
    public List<UpgradeExecutionV1> AppliedSteps { get; init; } = [];
    public bool WhenAffordable { get; init; }
    public int? TargetRound { get; init; }
    public float? DelaySeconds { get; init; }
    public bool Triggered { get; init; }
    public string Status { get; init; } = "scheduled";
    public int NextIndex { get; init; }
    public float? NextUpgradeCost { get; init; }
    public string? WaitingFor { get; init; }
    public UpgradeFailureV1? Failure { get; init; }
    public TowerInfoV1? Tower { get; init; }
    public double? Cash { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}

public sealed partial class AgentBridgeMod
{
    private const int MaxScheduledUpgrades = 32;
    private const int MaxUpgradeTerminalOutcomes = 64;
    private const int MaxUpgradeSequenceLength = 15;

    private sealed class UpgradePlanStep
    {
        public int Index { get; init; }
        public int Path { get; init; }
        public int ExpectedTier { get; init; }
        public string ExpectedUpgradeId { get; init; } = "";
        public float QuotedCost { get; set; }
        public int[] ExpectedTiersBefore { get; init; } = [0, 0, 0];
    }

    private sealed class ScheduledUpgradeWork
    {
        public string ScheduleId { get; init; } = "";
        public string TowerId { get; init; } = "";
        public TowerToSimulation? NativeTower { get; init; }
        public int[] UpgradeSequence { get; init; } = [];
        public List<UpgradePlanStep> Plan { get; init; } = [];
        public List<int> AppliedPaths { get; } = [];
        public List<UpgradeExecutionV1> AppliedSteps { get; } = [];
        public bool WhenAffordable { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public bool Triggered { get; set; }
        public int NextIndex { get; set; }
        public string Status { get; set; } = "scheduled";
        public string? WaitingFor { get; set; }
        public UpgradeFailureV1? Failure { get; set; }
        public TowerInfoV1? Tower { get; set; }
        public double? Cash { get; set; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? CompletedAtUtc { get; set; }
        public string? IdempotencyKey { get; init; }
    }

    private sealed class UpgradeIdempotencyEntry
    {
        public string Fingerprint { get; init; } = "";
        public string? ScheduleId { get; init; }
        public UnifiedUpgradeResultV1? CachedResult { get; init; }
    }

    private readonly struct UpgradeStepFailure
    {
        public UpgradeStepFailure(string code, string message, bool unknownOutcome = false)
        {
            Code = code;
            Message = message;
            UnknownOutcome = unknownOutcome;
        }

        public string Code { get; }
        public string Message { get; }
        public bool UnknownOutcome { get; }
    }

    private static readonly List<ScheduledUpgradeWork> scheduledUpgrades = new();
    private static readonly Queue<ScheduledUpgradeInfoV1> upgradeTerminalOutcomes = new();
    private static readonly Dictionary<string, UpgradeIdempotencyEntry> upgradeIdempotency = new(StringComparer.Ordinal);
    private static int upgradeScheduleCounter;

    private static IEnumerator<BridgeResultV1?> RunUnifiedUpgradeTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
        {
            yield return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot upgrade tower: no active match.", false);
            yield break;
        }
        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
        {
            yield return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
            yield break;
        }
        if (TryReadRawUpgradeIdentity(request.Payload, out var rawKey, out var rawFingerprint) &&
            TryReplayUpgradeIdempotency(request, rawKey!, rawFingerprint, inGame, gameModel, out var replay))
        {
            yield return replay;
            yield break;
        }
        if (!TryReadUpgradeRequest(request.Payload, bridge, out var parsed, out var parseCode, out var parseMessage))
        {
            yield return ErrorResult(request, parseCode, parseMessage, false);
            yield break;
        }
        string fingerprint = UpgradeFingerprint(parsed);
        if (parsed.IdempotencyKey is { Length: > 0 } key &&
            upgradeIdempotency.ContainsKey(key))
        {
            if (TryReplayUpgradeIdempotency(request, key, fingerprint, inGame, gameModel, out var cached))
                yield return cached;
            yield break;
        }
        if (parsed.HasTiming || parsed.WhenAffordable)
        {
            yield return HandleUnifiedUpgradeTower(request);
            yield break;
        }
        if (!TryFindTower(inGame, parsed.TowerId, out var tower))
        {
            yield return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{parsed.TowerId}' not found.", false);
            yield break;
        }
        if (!TryBuildUpgradePlan(tower!, bridge, gameModel, parsed.Paths, out var plan, out var planCode, out var planMessage))
        {
            yield return ErrorResult(request, planCode, planMessage, false);
            yield break;
        }

        var appliedPaths = new List<int>(plan.Count);
        var appliedSteps = new List<UpgradeExecutionV1>(plan.Count);
        long generation = matchGeneration;
        for (int i = 0; i < plan.Count; i++)
        {
            int executionRound = bridge.GetCurrentRound() + 1;
            float? executionSeconds = bridge.AreRoundsActive() && hasRoundElapsedTime ? currentRoundElapsedSeconds : null;
            var step = plan[i];
            if (!ValidateExpectedTowerState(tower!, step, plan, i, out var stateFailure))
            {
                var result = ImmediateFailureResult(inGame, gameModel, tower!, appliedPaths, appliedSteps, step,
                    new UpgradeStepFailure(stateFailure.Code, stateFailure.Message), false);
                RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, null, result);
                yield return SuccessResult(request, result);
                yield break;
            }
            if (!TryGetUpgradeQuote(tower!, bridge, gameModel, step.Path, step.ExpectedTier, step.ExpectedUpgradeId,
                    step.ExpectedTiersBefore, out var cost, out var quoteCode, out var quoteMessage))
            {
                var result = ImmediateFailureResult(inGame, gameModel, tower!, appliedPaths, appliedSteps, step,
                    new UpgradeStepFailure(quoteCode, quoteMessage), false);
                RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, null, result);
                yield return SuccessResult(request, result);
                yield break;
            }
            step.QuotedCost = cost;
            double cash = inGame.GetCash();
            if (cash < cost)
            {
                var result = ImmediateFailureResult(inGame, gameModel, tower!, appliedPaths, appliedSteps, step,
                    new UpgradeStepFailure("INSUFFICIENT_CASH",
                        $"Cannot afford upgrade {step.Index}: costs ${cost:F0}, have ${cash:F0}."), false);
                RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, null, result);
                yield return SuccessResult(request, result);
                yield break;
            }
            if (!TryApplyUpgradeStep(inGame, bridge, gameModel, tower!, step, executionRound,
                    executionSeconds ?? 0f, 0f, out var failure))
            {
                var result = ImmediateFailureResult(inGame, gameModel, tower!, appliedPaths, appliedSteps, step,
                    failure, failure.UnknownOutcome);
                RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, null, result);
                yield return SuccessResult(request, result);
                yield break;
            }
            appliedPaths.Add(step.Path);
            appliedSteps.Add(new UpgradeExecutionV1
            {
                Index = step.Index,
                Path = step.Path,
                Tier = step.ExpectedTier,
                UpgradeId = step.ExpectedUpgradeId,
                Cost = step.QuotedCost,
                ExecutionRound = executionRound,
                ExecutionSeconds = executionSeconds,
                LatenessSeconds = null
            });
            yield return null;
            if (generation != matchGeneration || !IsActiveGame())
            {
                yield return ErrorResult(request, "MATCH_CHANGED", "The match changed during the upgrade sequence; no further purchases were submitted.", false,
                    new { AppliedPaths = appliedPaths.ToArray() });
                yield break;
            }
        }

        var completed = new UnifiedUpgradeResultV1
        {
            Completed = true,
            AppliedPaths = appliedPaths,
            AppliedSteps = appliedSteps,
            Tower = ConvertTower(tower!, gameModel),
            Cash = inGame.GetCash(),
            Status = "completed",
            NextIndex = plan.Count
        };
        RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, null, completed);
        yield return SuccessResult(request, completed);
    }

    private static BridgeResultV1 HandleUnifiedUpgradeTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot upgrade tower: no active match.", false);

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
        if (TryReadRawUpgradeIdentity(request.Payload, out var rawKey, out var rawFingerprint) &&
            TryReplayUpgradeIdempotency(request, rawKey!, rawFingerprint, inGame, gameModel, out var replay))
            return replay!;

        if (!TryReadUpgradeRequest(request.Payload, bridge, out var parsed, out var parseCode, out var parseMessage))
            return ErrorResult(request, parseCode, parseMessage, false);

        string fingerprint = UpgradeFingerprint(parsed);
        if (parsed.IdempotencyKey is { Length: > 0 } key)
        {
            if (upgradeIdempotency.TryGetValue(key, out var previous))
            {
                if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return ErrorResult(request, "IDEMPOTENCY_KEY_REUSED", $"Idempotency key '{key}' was already used for different upgrade input.", false);

                var live = previous.ScheduleId == null
                    ? null
                    : scheduledUpgrades.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (live != null)
                    return SuccessResult(request, BuildUpgradeResult(live, inGame, gameModel));
                var terminal = previous.ScheduleId == null
                    ? null
                    : upgradeTerminalOutcomes.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (terminal != null)
                    return SuccessResult(request, TerminalResult(terminal));
                return ErrorResult(request, "IDEMPOTENCY_OUTCOME_EVICTED",
                    $"Idempotency outcome for key '{key}' is no longer retained; inspect the match before retrying.", false);
            }
        }

        if (!TryFindTower(inGame, parsed.TowerId, out var tower))
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{parsed.TowerId}' not found.", false);

        if (!TryBuildUpgradePlan(tower!, bridge, gameModel, parsed.Paths, out var plan, out var planCode, out var planMessage))
            return ErrorResult(request, planCode, planMessage, false);


        if (scheduledUpgrades.Count >= MaxScheduledUpgrades)
            return ErrorResult(request, "SCHEDULE_QUEUE_FULL", $"At most {MaxScheduledUpgrades} upgrade schedules may be pending.", false);
        if (scheduledUpgrades.Any(item => string.Equals(item.TowerId, parsed.TowerId, StringComparison.Ordinal)))
            return ErrorResult(request, "UPGRADE_ALREADY_SCHEDULED", $"Tower '{parsed.TowerId}' already has a pending upgrade schedule.", false);

        string scheduleId = $"upgrade_{++upgradeScheduleCounter}_{Guid.NewGuid():N}";
        if (scheduleId.Length > 128)
            scheduleId = scheduleId[..128];
        var work = new ScheduledUpgradeWork
        {
            ScheduleId = scheduleId,
            TowerId = parsed.TowerId,
            NativeTower = tower,
            UpgradeSequence = parsed.Paths.ToArray(),
            Plan = plan,
            WhenAffordable = parsed.WhenAffordable,
            HasTiming = parsed.HasTiming,
            TargetRound = parsed.TargetRound,
            DelaySeconds = parsed.DelaySeconds,
            CreatedAtUtc = DateTime.UtcNow,
            IdempotencyKey = parsed.IdempotencyKey
        };
        scheduledUpgrades.Add(work);

        // Affordability-only schedules buy the currently affordable prefix during
        // the request. Timed schedules never buy before their native trigger.
        if (!work.HasTiming && work.WhenAffordable && CanProcessUpgradeAutomatically(inGame))
            ProcessOneUpgradeWork(inGame, bridge, gameModel, work, bridge.GetCurrentRound() + 1,
                bridge.AreRoundsActive() && hasRoundElapsedTime ? currentRoundElapsedSeconds : null, false);

        if (work.Status is "completed" or "failed" or "expired")
        {
            scheduledUpgrades.Remove(work);
            var terminal = SnapshotScheduledUpgrade(work);
            RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, work.ScheduleId, TerminalResult(terminal));
            EnqueueUpgradeTerminal(terminal);
            return SuccessResult(request, TerminalResult(terminal));
        }

        var resultForCaller = BuildUpgradeResult(work, inGame, gameModel);
        RememberUpgradeIdempotency(parsed.IdempotencyKey, fingerprint, work.ScheduleId, resultForCaller);
        return SuccessResult(request, resultForCaller);
    }

    private static BridgeResultV1 HandleCancelScheduledUpgrade(BridgeRequestV1 request)
    {
        if (!TryReadScheduleCancellation(request.Payload, out var scheduleId, out var cancellationError))
            return ErrorResult(request, "INVALID_ARGUMENT", cancellationError!, false);

        if (scheduleId == null || string.Equals(scheduleId, "all", StringComparison.OrdinalIgnoreCase))
        {
            int count = scheduledUpgrades.Count;
            foreach (var work in scheduledUpgrades.ToArray())
            {
                work.Status = "cancelled";
                work.WaitingFor = null;
                work.CompletedAtUtc = DateTime.UtcNow;
                EnqueueUpgradeTerminal(SnapshotScheduledUpgrade(work));
            }
            scheduledUpgrades.Clear();
            return SuccessResult(request, new { Cancelled = count > 0, Count = count });
        }

        var item = scheduledUpgrades.FirstOrDefault(candidate => candidate.ScheduleId == scheduleId);
        if (item == null)
            return ErrorResult(request, "SCHEDULE_NOT_FOUND", $"Scheduled upgrade with ID '{scheduleId}' not found.", false);

        item.Status = "cancelled";
        item.WaitingFor = null;
        item.CompletedAtUtc = DateTime.UtcNow;
        scheduledUpgrades.Remove(item);
        EnqueueUpgradeTerminal(SnapshotScheduledUpgrade(item));
        return SuccessResult(request, new
        {
            Cancelled = true,
            Count = 1,
            ScheduleId = scheduleId
        });

    }
    private static bool CanProcessUpgradeAutomatically(InGame inGame)
    {
        if (isMatchPaused || TimeManager.gamePaused || UnityEngine.Time.timeScale <= 0f)
            return false;
        try
        {
            if (inGame.MatchLost || inGame.WaitingForVictoryScreen)
                return false;
            UpdateUiState(false);
            return uiState.Ready;
        }
        catch
        {
            return false;
        }
    }

    private static void ProcessScheduledUpgrades()
    {
        if (scheduledUpgrades.Count == 0) return;
        var inGame = InGame.instance;
        var bridge = inGame?.bridge;
        if (inGame == null || bridge == null || !inGame.IsInGame())
            return;

        int round;
        bool roundsActive;
        try
        {
            round = bridge.GetCurrentRound() + 1;
            roundsActive = bridge.AreRoundsActive();
        }
        catch { return; }
        // -1 is an intentional between-round sentinel. A zero-delay schedule
        // must wait for the target round's native start, while affordability-only
        // schedules may still buy between rounds.
        float elapsed = roundsActive && hasRoundElapsedTime ? currentRoundElapsedSeconds : -1f;
        ProcessScheduledUpgrades(inGame, bridge, round, elapsed);
    }


    private static void ProcessScheduledUpgrades(InGame inGame, UnityToSimulation bridge, int round, float elapsedSeconds)
    {
        if (scheduledUpgrades.Count == 0 || inGame == null || bridge == null || !inGame.IsInGame())
            return;
        if (isMatchPaused || TimeManager.gamePaused || UnityEngine.Time.timeScale <= 0f)
            return;

        try
        {
            if (inGame.MatchLost || inGame.WaitingForVictoryScreen)
                return;
            UpdateUiState(false);
            if (!uiState.Ready)
                return;
        }
        catch
        {
            return;
        }

        var gameModel = inGame.GetGameModel();
        if (gameModel == null)
            return;

        // The list is capped, and an invocation dispatches at most one native
        // purchase. Expiry and external-change failures may retire several items,
        // but never cause a second native mutation in this update.
        for (int i = 0; i < scheduledUpgrades.Count && i < MaxScheduledUpgrades; i++)
        {
            var work = scheduledUpgrades[i];
            if (!TryFindScheduledTower(inGame, work, out var tower))
            {
                FailUpgradeWork(work, new UpgradeFailureV1
                {
                    Index = work.NextIndex,
                    Path = NextPath(work),
                    Code = "TOWER_NOT_FOUND",
                    Message = $"Tower '{work.TowerId}' is no longer present."
                }, "failed", inGame, gameModel);
                RemoveCompletedWorkAt(i--);
                continue;
            }

            if (work.HasTiming && work.Triggered && work.TargetRound.HasValue && round > work.TargetRound.Value)
            {
                FailUpgradeWork(work, new UpgradeFailureV1
                {
                    Index = work.NextIndex,
                    Path = NextPath(work),
                    Code = "TARGET_ROUND_PASSED",
                    Message = $"Target round {work.TargetRound.Value} ended before the upgrade sequence completed."
                }, "expired", inGame, gameModel);
                RemoveCompletedWorkAt(i--);
                continue;
            }

            if (!ValidateExpectedTowerState(tower!, work, out var stateFailure))
            {
                FailUpgradeWork(work, stateFailure, "failed", inGame, gameModel);
                RemoveCompletedWorkAt(i--);
                continue;
            }

            if (work.NextIndex >= work.Plan.Count)
            {
                CompleteUpgradeWork(work, inGame, gameModel);
                RemoveCompletedWorkAt(i--);
                continue;
            }

            if (work.HasTiming && !work.Triggered)
            {
                if (!work.TargetRound.HasValue || !work.DelaySeconds.HasValue)
                {
                    FailUpgradeWork(work, new UpgradeFailureV1
                    {
                        Index = work.NextIndex,
                        Path = NextPath(work),
                        Code = "INVALID_SCHEDULE",
                        Message = "Scheduled upgrade timing is incomplete."
                    }, "failed", inGame, gameModel);
                    RemoveCompletedWorkAt(i--);
                    continue;
                }
                if (round > work.TargetRound.Value)
                {
                    FailUpgradeWork(work, new UpgradeFailureV1
                    {
                        Index = work.NextIndex,
                        Path = NextPath(work),
                        Code = "TARGET_ROUND_PASSED",
                        Message = $"Target round {work.TargetRound.Value} passed before the upgrade schedule triggered (current round: {round})."
                    }, "expired", inGame, gameModel);
                    RemoveCompletedWorkAt(i--);
                    continue;
                }
                if (elapsedSeconds < 0f || round < work.TargetRound.Value || elapsedSeconds < work.DelaySeconds.Value)
                {
                    work.Status = "scheduled";
                    work.WaitingFor = "time";
                    continue;
                }
                work.Triggered = true;
            }

            var step = work.Plan[work.NextIndex];
            if (!TryGetUpgradeQuote(tower!, bridge, gameModel, step.Path, step.ExpectedTier, step.ExpectedUpgradeId,
                    step.ExpectedTiersBefore, out var cost, out var quoteCode, out var quoteMessage))
            {
                FailUpgradeWork(work, new UpgradeFailureV1
                {
                    Index = step.Index,
                    Path = step.Path,
                    Code = quoteCode,
                    Message = quoteMessage
                }, "failed", inGame, gameModel);
                RemoveCompletedWorkAt(i--);
                continue;
            }
            step.QuotedCost = cost;
            work.WaitingFor = null;

            double cash = inGame.GetCash();
            if (cash < cost)
            {
                if (!work.WhenAffordable)
                {
                    FailUpgradeWork(work, new UpgradeFailureV1
                    {
                        Index = step.Index,
                        Path = step.Path,
                        Code = "INSUFFICIENT_CASH",
                        Message = $"Cannot afford upgrade {step.Index}: costs ${cost:F0}, have ${cash:F0}."
                    }, "failed", inGame, gameModel);
                    RemoveCompletedWorkAt(i--);
                    continue;
                }

                if (work.HasTiming && round > work.TargetRound.GetValueOrDefault(round))
                {
                    FailUpgradeWork(work, new UpgradeFailureV1
                    {
                        Index = step.Index,
                        Path = step.Path,
                        Code = "AFFORDABILITY_EXPIRED",
                        Message = $"The target round {work.TargetRound} ended before upgrade {step.Index} became affordable."
                    }, "expired", inGame, gameModel);
                    RemoveCompletedWorkAt(i--);
                    continue;
                }

                work.Status = "pending";
                work.WaitingFor = "cash";
                continue;
            }

            float lateness = work.HasTiming && elapsedSeconds >= 0f
                ? Math.Max(0f, elapsedSeconds - work.DelaySeconds.GetValueOrDefault())
                : 0f;
            if (!TryApplyUpgradeStep(inGame, bridge, gameModel, tower!, step, round,
                    elapsedSeconds >= 0f ? elapsedSeconds : 0f, lateness, out var failure))
            {
                FailUpgradeWork(work, new UpgradeFailureV1
                {
                    Index = step.Index,
                    Path = step.Path,
                    Code = failure.Code,
                    Message = failure.Message
                }, "failed", inGame, gameModel, failure.UnknownOutcome);
                RemoveCompletedWorkAt(i--);
                return;
            }

            work.AppliedPaths.Add(step.Path);
            work.AppliedSteps.Add(new UpgradeExecutionV1
            {
                Index = step.Index,
                Path = step.Path,
                Tier = step.ExpectedTier,
                UpgradeId = step.ExpectedUpgradeId,
                Cost = step.QuotedCost,
                ExecutionRound = round,
                ExecutionSeconds = elapsedSeconds >= 0f ? elapsedSeconds : null,
                LatenessSeconds = work.HasTiming && elapsedSeconds >= 0f ? lateness : null
            });
            work.NextIndex++;
            if (work.NextIndex < work.Plan.Count)
                RefreshNextUpgradeCost(work, tower!, bridge, gameModel);
            work.Status = work.NextIndex >= work.Plan.Count ? "completed" : (work.Triggered ? "executing" : "pending");
            work.WaitingFor = null;
            if (work.NextIndex >= work.Plan.Count)
            {
                CompleteUpgradeWork(work, inGame, gameModel);
                RemoveCompletedWorkAt(i--);
            }
            return;
        }
    }

    private static void ProcessOneUpgradeWork(InGame inGame, UnityToSimulation bridge, GameModel gameModel,
        ScheduledUpgradeWork work, int round, float? elapsedSeconds, bool buyPrefix)
    {
        if (!TryFindScheduledTower(inGame, work, out var tower))
        {
            work.Status = "failed";
            work.Failure = new UpgradeFailureV1
            {
                Index = work.NextIndex,
                Path = NextPath(work),
                Code = "TOWER_NOT_FOUND",
                Message = $"Tower '{work.TowerId}' is no longer present."
            };
            work.Cash = inGame.GetCash();
            return;
        }

        while (work.NextIndex < work.Plan.Count)
        {
            var step = work.Plan[work.NextIndex];
            if (!ValidateExpectedTowerState(tower!, work, out var stateFailure))
            {
                work.Status = "failed";
                work.Failure = stateFailure;
                work.Cash = inGame.GetCash();
                work.Tower = ConvertTower(tower!, gameModel);
                return;
            }
            if (!TryGetUpgradeQuote(tower!, bridge, gameModel, step.Path, step.ExpectedTier, step.ExpectedUpgradeId,
                    step.ExpectedTiersBefore, out var cost, out var quoteCode, out var quoteMessage))
            {
                work.Status = "failed";
                work.Failure = new UpgradeFailureV1
                {
                    Index = step.Index,
                    Path = step.Path,
                    Code = quoteCode,
                    Message = quoteMessage
                };
                work.Cash = inGame.GetCash();
                work.Tower = ConvertTower(tower!, gameModel);
                return;
            }
            step.QuotedCost = cost;
            if (inGame.GetCash() < cost)
            {
                work.Status = "pending";
                work.WaitingFor = "cash";
                return;
            }

            if (!TryApplyUpgradeStep(inGame, bridge, gameModel, tower!, step, round, elapsedSeconds ?? 0f, 0f, out var failure))
            {
                work.Status = "failed";
                work.Failure = new UpgradeFailureV1
                {
                    Index = step.Index,
                    Path = step.Path,
                    Code = failure.Code,
                    Message = failure.Message
                };
                work.Cash = failure.UnknownOutcome ? null : inGame.GetCash();
                work.Tower = failure.UnknownOutcome ? null : ConvertTower(tower!, gameModel);
                return;
            }

            work.AppliedPaths.Add(step.Path);
            work.AppliedSteps.Add(new UpgradeExecutionV1
            {
                Index = step.Index,
                Path = step.Path,
                Tier = step.ExpectedTier,
                UpgradeId = step.ExpectedUpgradeId,
                Cost = step.QuotedCost,
                ExecutionRound = round,
                ExecutionSeconds = elapsedSeconds,
                LatenessSeconds = null
            });
            work.NextIndex++;
            if (work.NextIndex < work.Plan.Count)
                RefreshNextUpgradeCost(work, tower!, bridge, gameModel);
            work.Status = work.NextIndex >= work.Plan.Count ? "completed" : (work.Triggered ? "executing" : "pending");
            work.WaitingFor = null;
            if (!buyPrefix)
                return;
        }

        work.Status = "completed";
        work.Cash = inGame.GetCash();
        work.Tower = ConvertTower(tower!, gameModel);
    }

    private static void ResetScheduledUpgrades()
    {
        scheduledUpgrades.Clear();
        upgradeTerminalOutcomes.Clear();
        upgradeIdempotency.Clear();
        upgradeScheduleCounter = 0;
    }

    private static List<ScheduledUpgradeInfoV1> SnapshotScheduledUpgrades()
    {
        var snapshot = new List<ScheduledUpgradeInfoV1>(scheduledUpgrades.Count + upgradeTerminalOutcomes.Count);
        foreach (var work in scheduledUpgrades)
            snapshot.Add(SnapshotScheduledUpgrade(work));
        snapshot.AddRange(upgradeTerminalOutcomes);
        return snapshot;
    }

    private static void RemoveCompletedWorkAt(int index)
    {
        if (index >= 0 && index < scheduledUpgrades.Count)
            scheduledUpgrades.RemoveAt(index);
    }

    private static int NextPath(ScheduledUpgradeWork work) => work.NextIndex >= 0 && work.NextIndex < work.UpgradeSequence.Length
        ? work.UpgradeSequence[work.NextIndex]
        : -1;

    private static void CompleteUpgradeWork(ScheduledUpgradeWork work, InGame inGame, GameModel gameModel)
    {
        work.Status = "completed";
        work.WaitingFor = null;
        work.CompletedAtUtc = DateTime.UtcNow;
        work.Cash = inGame.GetCash();
        if (TryFindScheduledTower(inGame, work, out var tower))
            work.Tower = ConvertTower(tower!, gameModel);
        EnqueueUpgradeTerminal(SnapshotScheduledUpgrade(work));
    }

    private static void FailUpgradeWork(ScheduledUpgradeWork work, UpgradeFailureV1 failure, string status,
        InGame inGame, GameModel gameModel, bool unknownOutcome = false)
    {
        work.Status = status;
        work.WaitingFor = null;
        work.Failure = failure;
        work.CompletedAtUtc = DateTime.UtcNow;
        if (!unknownOutcome)
        {
            work.Cash = inGame.GetCash();
            if (TryFindScheduledTower(inGame, work, out var tower))
                work.Tower = ConvertTower(tower!, gameModel);
        }
        else
        {
            work.Cash = null;
            work.Tower = null;
        }
        EnqueueUpgradeTerminal(SnapshotScheduledUpgrade(work));
    }

    private static void EnqueueUpgradeTerminal(ScheduledUpgradeInfoV1 terminal)
    {
        while (upgradeTerminalOutcomes.Count >= MaxUpgradeTerminalOutcomes)
            upgradeTerminalOutcomes.Dequeue();
        upgradeTerminalOutcomes.Enqueue(terminal);
        int round = terminal.TargetRound ?? (InGame.instance?.bridge?.GetCurrentRound() + 1 ?? 1);
        AppendRoundActionOutcome(terminal.ScheduleId, "upgrade", round, terminal.Status, terminal.Failure?.Code,
            terminal.AppliedSteps.Count > 0 ? terminal.AppliedSteps[^1].ExecutionSeconds : null);
    }

    private static ScheduledUpgradeInfoV1 SnapshotScheduledUpgrade(ScheduledUpgradeWork work)
    {
        float? nextCost = null;
        if (work.NextIndex >= 0 && work.NextIndex < work.Plan.Count)
            nextCost = work.Plan[work.NextIndex].QuotedCost;
        return new ScheduledUpgradeInfoV1
        {
            ScheduleId = work.ScheduleId,
            TowerId = work.TowerId,
            UpgradeSequence = work.UpgradeSequence.ToArray(),
            AppliedPaths = work.AppliedPaths.ToList(),
            AppliedSteps = work.AppliedSteps.ToList(),
            WhenAffordable = work.WhenAffordable,
            TargetRound = work.TargetRound,
            DelaySeconds = work.DelaySeconds,
            Triggered = work.Triggered,
            Status = work.Status,
            NextIndex = work.NextIndex,
            NextUpgradeCost = nextCost,
            WaitingFor = work.WaitingFor,
            Failure = work.Failure,
            Tower = work.Tower,
            Cash = work.Cash,
            CreatedAtUtc = work.CreatedAtUtc,
            CompletedAtUtc = work.CompletedAtUtc
        };
    }

    private static UnifiedUpgradeResultV1 BuildUpgradeResult(ScheduledUpgradeWork work, InGame inGame, GameModel gameModel)
    {
        var snapshot = SnapshotScheduledUpgrade(work);
        if (snapshot.Status is "completed" or "failed" or "expired" or "cancelled")
            return TerminalResult(snapshot);
        if (snapshot.Tower == null && TryFindScheduledTower(inGame, work, out var tower))
            snapshot = SnapshotScheduledUpgradeWithTower(work, ConvertTower(tower!, gameModel), inGame.GetCash());
        return new UnifiedUpgradeResultV1
        {
            Completed = false,
            AppliedPaths = snapshot.AppliedPaths,
            AppliedSteps = snapshot.AppliedSteps,
            Tower = snapshot.Tower,
            Cash = snapshot.Cash ?? inGame.GetCash(),
            Status = snapshot.Status,
            ScheduleId = snapshot.ScheduleId,
            NextIndex = snapshot.NextIndex,
            NextUpgradeCost = snapshot.NextUpgradeCost,
            WaitingFor = snapshot.WaitingFor,
            Failure = snapshot.Failure
        };
    }

    private static ScheduledUpgradeInfoV1 SnapshotScheduledUpgradeWithTower(ScheduledUpgradeWork work, TowerInfoV1 tower, double cash)
    {
        var snapshot = SnapshotScheduledUpgrade(work);
        return new ScheduledUpgradeInfoV1
        {
            ScheduleId = snapshot.ScheduleId,
            TowerId = snapshot.TowerId,
            UpgradeSequence = snapshot.UpgradeSequence,
            AppliedPaths = snapshot.AppliedPaths,
            AppliedSteps = snapshot.AppliedSteps,
            WhenAffordable = snapshot.WhenAffordable,
            TargetRound = snapshot.TargetRound,
            DelaySeconds = snapshot.DelaySeconds,
            Triggered = snapshot.Triggered,
            Status = snapshot.Status,
            NextIndex = snapshot.NextIndex,
            NextUpgradeCost = snapshot.NextUpgradeCost,
            WaitingFor = snapshot.WaitingFor,
            Failure = snapshot.Failure,
            Tower = tower,
            Cash = cash,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            CompletedAtUtc = snapshot.CompletedAtUtc
        };
    }

    private static UnifiedUpgradeResultV1 TerminalResult(ScheduledUpgradeInfoV1 terminal) => new()
    {
        Completed = terminal.Status == "completed",
        AppliedPaths = terminal.AppliedPaths,
        AppliedSteps = terminal.AppliedSteps,
        Tower = terminal.Tower,
        Cash = terminal.Cash,
        Failure = terminal.Failure,
        Status = terminal.Status,
        ScheduleId = terminal.ScheduleId,
        NextIndex = terminal.NextIndex,
        NextUpgradeCost = terminal.NextUpgradeCost,
        WaitingFor = terminal.WaitingFor
    };


    private static UnifiedUpgradeResultV1 ImmediateFailureResult(InGame inGame, GameModel gameModel, TowerToSimulation tower,
        List<int> appliedPaths, List<UpgradeExecutionV1> appliedSteps, UpgradePlanStep failedStep,
        UpgradeStepFailure failure, bool unknownOutcome)
    {
        TowerInfoV1? info = null;
        double? cash = null;
        if (!unknownOutcome)
        {
            info = ConvertTower(tower, gameModel);
            cash = inGame.GetCash();
        }
        return new UnifiedUpgradeResultV1
        {
            Completed = false,
            AppliedPaths = appliedPaths,
            AppliedSteps = appliedSteps,
            Tower = info,
            Cash = cash,
            Failure = new UpgradeFailureV1
            {
                Index = failedStep.Index,
                Path = failedStep.Path,
                Code = failure.Code,
                Message = failure.Message
            },
            Status = "failed",
            NextIndex = failedStep.Index
        };
    }

    private static bool TryApplyUpgradeStep(InGame inGame, UnityToSimulation bridge, GameModel gameModel,
        TowerToSimulation tower, UpgradePlanStep step, int executionRound, float executionSeconds,
        float latenessSeconds, out UpgradeStepFailure failure)
    {
        failure = default;
        bool callbackReceived = false;
        bool nativeSuccess = false;
        bool previousImmediate = bridge.IsImmediateMode;
        try
        {
            bridge.IsImmediateMode = true;
            bridge.UpgradeTower(bridge.GetInputId(), tower.Id, step.Path, 0.0,
                (Il2CppSystem.Action<bool>)((bool result) =>
                {
                    callbackReceived = true;
                    nativeSuccess = result;
                }));
        }
        catch (Exception exception)
        {
            failure = new UpgradeStepFailure("UPGRADE_OUTCOME_UNKNOWN",
                $"Native upgrade outcome for index {step.Index} is unknown: {exception.Message}", true);
            return false;
        }
        finally
        {
            bridge.IsImmediateMode = previousImmediate;
        }

        if (!callbackReceived)
        {
            failure = new UpgradeStepFailure("UPGRADE_OUTCOME_UNKNOWN",
                $"Native upgrade outcome for index {step.Index} was not reported.", true);
            return false;
        }
        if (!nativeSuccess)
        {
            failure = new UpgradeStepFailure("UPGRADE_FAILED",
                $"Native upgrade rejected tower {tower.Id} on path {step.Path}.");
            return false;
        }

        if (!TryReadTiers(tower, out var tiers) || !TiersMatchAfterStep(tiers, step))
        {
            failure = new UpgradeStepFailure("UPGRADE_OUTCOME_UNKNOWN",
                $"Native upgrade index {step.Index} reported success but the expected tower tier was not observed.", true);
            return false;
        }
        return true;
    }

    private static bool TiersMatchAfterStep(int[] tiers, UpgradePlanStep step)
    {
        if (tiers.Length != 3 || step.Path < 0 || step.Path > 2)
            return false;
        for (int i = 0; i < 3; i++)
        {
            int expected = step.ExpectedTiersBefore[i] + (i == step.Path ? 1 : 0);
            if (tiers[i] != expected)
                return false;
        }
        return true;
    }

    private static bool ValidateExpectedTowerState(TowerToSimulation tower, ScheduledUpgradeWork work, out UpgradeFailureV1 failure)
    {
        if (work.NextIndex >= work.Plan.Count)
        {
            failure = new UpgradeFailureV1 { Index = work.NextIndex, Path = -1, Code = "NONE", Message = "No upgrade remains." };
            return true;
        }
        return ValidateExpectedTowerState(tower, work.Plan[work.NextIndex], work.Plan, work.NextIndex, out failure);
    }

    private static bool ValidateExpectedTowerState(TowerToSimulation tower, UpgradePlanStep step,
        List<UpgradePlanStep> plan, int index, out UpgradeFailureV1 failure)
    {
        if (!TryReadTiers(tower, out var tiers))
        {
            failure = new UpgradeFailureV1 { Index = index, Path = step.Path, Code = "TOWER_STATE_UNAVAILABLE", Message = "The tower tier state is unavailable." };
            return false;
        }
        for (int i = 0; i < 3; i++)
        {
            if (tiers[i] != step.ExpectedTiersBefore[i])
            {
                failure = new UpgradeFailureV1
                {
                    Index = index,
                    Path = step.Path,
                    Code = "TOWER_CHANGED",
                    Message = $"Tower '{tower.Id}' changed outside this schedule; expected tiers [{string.Join(",", step.ExpectedTiersBefore)}], observed [{string.Join(",", tiers)}]."
                };
                return false;
            }
        }

        var currentUpgrade = FindUpgrade(tower, InGame.instance?.GetGameModel(), step.Path, step.ExpectedTier, tiers);
        if (currentUpgrade == null || !string.Equals(currentUpgrade.name, step.ExpectedUpgradeId, StringComparison.Ordinal))
        {
            failure = new UpgradeFailureV1
            {
                Index = index,
                Path = step.Path,
                Code = "UPGRADE_CHANGED",
                Message = $"The expected upgrade at index {index} is no longer available."
            };
            return false;
        }
        failure = new UpgradeFailureV1 { Index = index, Path = step.Path, Code = "NONE", Message = "" };
        return true;
    }

    private static bool TryBuildUpgradePlan(TowerToSimulation tower, UnityToSimulation bridge, GameModel gameModel,
        int[] paths, out List<UpgradePlanStep> plan, out string code, out string message)
    {
        plan = new List<UpgradePlanStep>(paths.Length);
        code = "";
        message = "";
        if (tower.hero != null || tower.IsNotUpgradeable || tower.Def == null || tower.Def.tiers == null)
        {
            code = "UPGRADE_UNAVAILABLE";
            message = $"Tower '{tower.Id}' cannot be upgraded.";
            return false;
        }
        if (!TryReadTiers(tower, out var projectedTiers))
        {
            code = "TOWER_STATE_UNAVAILABLE";
            message = $"Tower '{tower.Id}' tier state is unavailable.";
            return false;
        }

        for (int index = 0; index < paths.Length; index++)
        {
            int path = paths[index];
            if (path < 0 || path > 2)
            {
                code = "INVALID_PATH";
                message = $"upgradeSequence[{index}] must be 0, 1, or 2.";
                return false;
            }
            int expectedTier = projectedTiers[path] + 1;
            if (expectedTier > 5)
            {
                code = "UPGRADE_UNAVAILABLE";
                message = $"Tower '{tower.Id}' cannot reach tier {expectedTier} on path {path}.";
                return false;
            }
            int nonZeroPaths = projectedTiers.Count(value => value > 0) + (projectedTiers[path] == 0 ? 1 : 0);
            if (nonZeroPaths > 2)
            {
                code = "UPGRADE_UNAVAILABLE";
                message = $"Crosspath restrictions prevent three nonzero paths after projected tiers [{string.Join(",", projectedTiers)}].";
                return false;
            }
            if (projectedTiers.Where((value, candidatePath) => candidatePath != path && value >= 3).Any() && expectedTier >= 3)
            {
                code = "UPGRADE_UNAVAILABLE";
                message = $"Crosspath restrictions prevent tier {expectedTier} on path {path} after projected tiers [{string.Join(",", projectedTiers)}].";
                return false;
            }

            var upgrade = FindUpgrade(tower, gameModel, path, expectedTier, projectedTiers);
            if (upgrade == null)
            {
                code = "UPGRADE_UNAVAILABLE";
                message = $"Tower '{tower.Id}' has no upgrade for path {path}, tier {expectedTier}.";
                return false;
            }
            if (bridge.IsUpgradeLocked(tower.Id, path, expectedTier))
            {
                code = "UPGRADE_UNAVAILABLE";
                message = $"Tower '{tower.Id}' has upgrade path {path}, tier {expectedTier} locked.";
                return false;
            }
            float quote = 0f;
            if (index == 0 && !TryGetUpgradeQuote(tower, bridge, gameModel, path, expectedTier, upgrade.name,
                    projectedTiers, out quote, out code, out message))
                return false;

            plan.Add(new UpgradePlanStep
            {
                Index = index,
                Path = path,
                ExpectedTier = expectedTier,
                ExpectedUpgradeId = upgrade.name,
                QuotedCost = quote,
                ExpectedTiersBefore = projectedTiers.ToArray()
            });
            projectedTiers[path]++;
        }
        return true;
    }

    private static bool TryGetUpgradeQuote(TowerToSimulation tower, UnityToSimulation bridge, GameModel gameModel,
        int path, int tier, string expectedUpgradeId, int[]? expectedTiers,
        out float quote, out string code, out string message)
    {
        quote = 0f;
        code = "UPGRADE_UNAVAILABLE";
        message = $"Tower '{tower.Id}' has no available upgrade on path {path}.";
        var upgrade = FindUpgrade(tower, gameModel, path, tier, expectedTiers);
        if (upgrade == null || !string.Equals(upgrade.name, expectedUpgradeId, StringComparison.Ordinal))
        {
            code = "UPGRADE_CHANGED";
            message = $"The expected upgrade on path {path}, tier {tier} is unavailable.";
            return false;
        }
        bool isCurrentState = expectedTiers is { Length: >= 3 } &&
            TryReadTiers(tower, out var actualTiers) &&
            actualTiers.SequenceEqual(expectedTiers!.Take(3));
        if (isCurrentState)
        {
            float? available;
            try { available = GetAvailableUpgradeCost(tower, path, gameModel); }
            catch (Exception exception)
            {
                code = "PRICE_UNAVAILABLE";
                message = $"Native price lookup failed for upgrade {path}/{tier}: {exception.Message}";
                return false;
            }
            if (!available.HasValue)
            {
                message = $"Tower '{tower.Id}' has no available upgrade on path {path}.";
                return false;
            }
            quote = available.Value;
            return true;
        }
        if (bridge.IsUpgradeLocked(tower.Id, path, tier) || tower.IsUpgradeBlocked(path, tier, out _))
        {
            message = $"Tower '{tower.Id}' has upgrade path {path}, tier {tier} locked or blocked.";
            return false;
        }

        float baseCost = upgrade.cost;

        float nativeCost;
        try { nativeCost = tower.GetUpgradeCost(path, tier, baseCost, false); }
        catch (Exception exception)
        {
            code = "PRICE_UNAVAILABLE";
            message = $"Native price lookup failed for upgrade {path}/{tier}: {exception.Message}";
            return false;
        }
        if (!float.IsFinite(nativeCost) || nativeCost < 0 || nativeCost >= int.MaxValue)
        {
            code = "PRICE_UNAVAILABLE";
            message = $"Native price lookup returned an unavailable price for upgrade {path}/{tier}.";
            return false;
        }
        int rounded = Il2CppAssets.Scripts.Simulation.SMath.Math.RoundToNearestInt(nativeCost, 5);
        if (rounded < 0)
        {
            code = "PRICE_UNAVAILABLE";
            message = $"Native price lookup returned an unavailable price for upgrade {path}/{tier}.";
            return false;
        }
        quote = rounded;
        return true;
    }

    private static void RefreshNextUpgradeCost(ScheduledUpgradeWork work, TowerToSimulation tower,
        UnityToSimulation bridge, GameModel gameModel)
    {
        if (work.NextIndex >= work.Plan.Count)
            return;
        var next = work.Plan[work.NextIndex];
        if (TryGetUpgradeQuote(tower, bridge, gameModel, next.Path, next.ExpectedTier,
                next.ExpectedUpgradeId, next.ExpectedTiersBefore, out var cost, out _, out _))
            next.QuotedCost = cost;
    }
    private static Il2CppAssets.Scripts.Models.Towers.Upgrades.UpgradeModel? FindUpgrade(
        TowerToSimulation tower, GameModel? gameModel, int path, int tier, int[]? projectedTiers = null)
    {
        if (gameModel == null || tower.Def == null)
            return null;

        if (projectedTiers is { Length: >= 3 } && !string.IsNullOrEmpty(tower.Def.baseId))
        {
            try
            {
                var projectedModel = gameModel.GetTower(tower.Def.baseId,
                    projectedTiers[0], projectedTiers[1], projectedTiers[2]);
                if (projectedModel != null)
                    return projectedModel.GetUpgrade(path, tier);
            }
            catch { }
        }

        if (tower.Def.upgrades == null)
            return null;
        foreach (var next in tower.Def.upgrades)
        {
            var upgrade = gameModel.GetUpgrade(next.upgrade);
            if (upgrade != null && upgrade.path == path && upgrade.tier == tier)
                return upgrade;
        }
        return null;
    }

    private static bool TryReadTiers(TowerToSimulation tower, out int[] tiers)
    {
        tiers = [0, 0, 0];
        try
        {
            if (tower.Def?.tiers == null || tower.Def.tiers.Length < 3)
                return false;
            tiers = tower.Def.tiers.Take(3).ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindTower(InGame inGame, string towerId, out TowerToSimulation? tower)
    {
        tower = null;
        try
        {
            var towers = inGame.GetAllTowerToSim();
            if (towers == null) return false;
            tower = towers.FirstOrDefault(candidate => candidate != null && candidate.Id.ToString() == towerId);
        }
        catch { }
        return tower != null;
    }
    private static bool TryFindScheduledTower(InGame inGame, ScheduledUpgradeWork work, out TowerToSimulation? tower)
    {
        tower = work.NativeTower;
        try
        {
            if (tower != null && tower.Id.ToString() == work.TowerId && tower.Def != null)
                return true;
        }
        catch { }
        return TryFindTower(inGame, work.TowerId, out tower);
    }
    private static bool TryReadRawUpgradeIdentity(JsonElement payload, out string? key, out string fingerprint)
    {
        key = null;
        fingerprint = "";
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("idempotencyKey", out var keyProp) ||
            keyProp.ValueKind != JsonValueKind.String)
            return false;
        key = keyProp.GetString();
        if (string.IsNullOrWhiteSpace(key) || key!.Length > 128)
            return false;
        string towerId = "";
        if (payload.TryGetProperty("towerId", out var towerProp))
            towerId = towerProp.ValueKind == JsonValueKind.String ? towerProp.GetString() ?? "" : towerProp.GetRawText();
        else
            towerId = "<missing>";
        string paths = "-";
        if (payload.TryGetProperty("upgradeSequence", out var pathsProp) && pathsProp.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>(MaxUpgradeSequenceLength);
            foreach (var value in pathsProp.EnumerateArray())
                values.Add(value.TryGetInt32(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : value.GetRawText());
            paths = string.Join(",", values);
        }

        bool whenAffordable = payload.TryGetProperty("whenAffordable", out var affordableProp) &&
            affordableProp.ValueKind == JsonValueKind.True;
        bool hasRound = payload.TryGetProperty("round", out var roundProp);
        bool hasDelay = payload.TryGetProperty("delaySeconds", out var delayProp);
        bool hasDelayFromNow = payload.TryGetProperty("delayFromNow", out var fromNowProp);
        string fromNow = hasDelayFromNow ? RawUpgradeNumber(fromNowProp) : "-";
        string round = hasDelayFromNow ? "-" : hasRound ? RawUpgradeNumber(roundProp) : "-";
        string delay = hasDelayFromNow ? "-" : hasDelay ? RawUpgradeNumber(delayProp) : "-";
        fingerprint = string.Join("\u001f", towerId, paths,
            whenAffordable ? "1" : "0",
            hasRound || hasDelay || hasDelayFromNow ? "1" : "0",
            fromNow, round, delay);
        return true;
    }

    private static string RawUpgradeNumber(JsonElement value)
    {
        if (value.TryGetInt32(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetSingle(out var single) && float.IsFinite(single))
            return single.ToString("R", CultureInfo.InvariantCulture);
        if (value.TryGetDouble(out var number) && double.IsFinite(number))
            return number.ToString("R", CultureInfo.InvariantCulture);
        return value.GetRawText();
    }

    private static bool TryReplayUpgradeIdempotency(BridgeRequestV1 request, string key,
        string fingerprint, InGame inGame, GameModel gameModel, out BridgeResultV1? replay)
    {
        replay = null;
        if (!upgradeIdempotency.TryGetValue(key, out var previous))
        {
            if (upgradeIdempotency.Count < 512) return false;
            replay = ErrorResult(request, "IDEMPOTENCY_CAPACITY_EXCEEDED",
                "This match has reached its 512-key idempotency limit. Existing keys remain protected from replay.", false);
            return true;
        }
        if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            replay = ErrorResult(request, "IDEMPOTENCY_KEY_REUSED",
                $"Idempotency key '{key}' was already used for different upgrade input.", false);
            return true;
        }

        var live = previous.ScheduleId == null
            ? null
            : scheduledUpgrades.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
        if (live != null)
        {
            replay = SuccessResult(request, BuildUpgradeResult(live, inGame, gameModel));
            return true;
        }
        if (previous.CachedResult != null)
        {
            replay = SuccessResult(request, previous.CachedResult);
            return true;
        }
        var terminal = previous.ScheduleId == null
            ? null
            : upgradeTerminalOutcomes.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
        replay = terminal != null
            ? SuccessResult(request, TerminalResult(terminal))
            : ErrorResult(request, "IDEMPOTENCY_OUTCOME_EVICTED",
                $"Idempotency outcome for key '{key}' is no longer retained; inspect the match before retrying.", false);
        return true;
    }


    private sealed class ParsedUpgradeRequest
    {
        public string TowerId { get; init; } = "";
        public int[] Paths { get; init; } = [];
        public bool WhenAffordable { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public float? DelayFromNow { get; init; }
        public string? IdempotencyKey { get; init; }
    }

    private static bool TryReadUpgradeRequest(JsonElement payload, UnityToSimulation bridge,
        out ParsedUpgradeRequest parsed, out string code, out string message)
    {
        parsed = new ParsedUpgradeRequest();
        code = "INVALID_ARGUMENT";
        message = "Upgrade payload must be an object.";
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (!payload.TryGetProperty("towerId", out var towerProp) || towerProp.ValueKind != JsonValueKind.String)
        {
            message = "towerId must be provided.";
            return false;
        }
        string towerId = towerProp.GetString() ?? "";
        if (towerId.Trim().Length == 0 || towerId.Length > 128)
        {
            message = "towerId must contain 1-128 non-whitespace characters.";
            return false;
        }

        if (!payload.TryGetProperty("upgradeSequence", out var pathsProp) || pathsProp.ValueKind != JsonValueKind.Array)
        {
            message = "upgradeSequence must contain 1-15 ordered path indexes.";
            return false;
        }
        var paths = new List<int>(MaxUpgradeSequenceLength);
        foreach (var value in pathsProp.EnumerateArray())
        {
            if (paths.Count >= MaxUpgradeSequenceLength || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var path) || path < 0 || path > 2)
            {
                message = "upgradeSequence must contain 1-15 integers, each 0, 1, or 2.";
                return false;
            }
            paths.Add(path);
        }
        if (paths.Count == 0)
        {
            message = "upgradeSequence must contain 1-15 ordered path indexes.";
            return false;
        }

        bool whenAffordable = false;
        if (payload.TryGetProperty("whenAffordable", out var affordableProp))
        {
            if (affordableProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                message = "whenAffordable must be a boolean.";
                return false;
            }
            whenAffordable = affordableProp.GetBoolean();
        }

        bool hasRound = payload.TryGetProperty("round", out var roundProp);
        int? targetRound = null;
        if (hasRound)
        {
            if (roundProp.ValueKind != JsonValueKind.Number || !roundProp.TryGetInt32(out var round) || round <= 0)
            {
                message = "round must be a positive integer.";
                return false;
            }
            targetRound = round;
        }
        bool hasDelay = payload.TryGetProperty("delaySeconds", out var delayProp);
        float? delaySeconds = null;
        float? delayFromNow = null;
        if (hasDelay)
        {
            if (delayProp.ValueKind != JsonValueKind.Number || !delayProp.TryGetSingle(out var delay) || !float.IsFinite(delay) || delay < 0)
            {
                message = "delaySeconds must be finite and nonnegative.";
                return false;
            }
            delaySeconds = delay;
        }

        bool hasDelayFromNow = payload.TryGetProperty("delayFromNow", out var fromNowProp);
        if (hasDelayFromNow)
        {
            if (targetRound.HasValue || hasDelay)
            {
                message = "delayFromNow cannot be combined with round or delaySeconds.";
                return false;
            }
            if (fromNowProp.ValueKind != JsonValueKind.Number ||
                !fromNowProp.TryGetSingle(out var fromNow) ||
                !float.IsFinite(fromNow) || fromNow < 0)
            {
                message = "delayFromNow must be finite and nonnegative.";
                return false;
            }
            delayFromNow = fromNow;
            if (!bridge.AreRoundsActive() || !hasRoundElapsedTime)
            {
                code = "ROUND_TIME_UNAVAILABLE";
                message = "delayFromNow requires an active round with a native round clock.";
                return false;
            }
            targetRound = bridge.GetCurrentRound() + 1;
            delaySeconds = currentRoundElapsedSeconds + fromNow;
        }
        else if (hasRound || hasDelay)
        {
            targetRound ??= bridge.GetCurrentRound() + 1;
            delaySeconds ??= 0f;
            int currentRound = bridge.GetCurrentRound() + 1;
            if (targetRound.Value < currentRound)
            {
                code = "TARGET_ROUND_PASSED";
                message = $"Target round {targetRound.Value} has already passed (current round is {currentRound}).";
                return false;
            }
            if (targetRound.Value == currentRound && bridge.AreRoundsActive() && hasRoundElapsedTime && delaySeconds.Value < currentRoundElapsedSeconds)
            {
                code = "DELAY_PASSED";
                message = $"Delay {delaySeconds.Value:F2}s has already passed in round {currentRound} (elapsed: {currentRoundElapsedSeconds:F2}s).";
                return false;
            }
        }

        string? idempotencyKey = null;
        if (payload.TryGetProperty("idempotencyKey", out var keyProp))
        {
            if (keyProp.ValueKind != JsonValueKind.String)
            {
                message = "idempotencyKey must be a string.";
                return false;
            }
            idempotencyKey = keyProp.GetString();
            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            {
                message = "idempotencyKey must contain 1-128 non-whitespace characters.";
                return false;
            }
        }

        parsed = new ParsedUpgradeRequest
        {
            TowerId = towerId,
            Paths = paths.ToArray(),
            WhenAffordable = whenAffordable,
            HasTiming = hasRound || hasDelay || hasDelayFromNow,
            TargetRound = targetRound,
            DelaySeconds = delaySeconds,
            DelayFromNow = delayFromNow,
            IdempotencyKey = idempotencyKey
        };
        return true;
    }

    private static string UpgradeFingerprint(ParsedUpgradeRequest request)
    {
        return string.Join("\u001f", request.TowerId,
            string.Join(",", request.Paths),
            request.WhenAffordable ? "1" : "0",
            request.HasTiming ? "1" : "0",
            request.DelayFromNow?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
            request.DelayFromNow.HasValue ? "-" : request.TargetRound?.ToString(CultureInfo.InvariantCulture) ?? "-",
            request.DelayFromNow.HasValue ? "-" : request.DelaySeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
    }

    private static void RememberUpgradeIdempotency(string? key, string fingerprint, string? scheduleId, UnifiedUpgradeResultV1 result)
    {
        if (string.IsNullOrEmpty(key)) return;
        upgradeIdempotency[key] = new UpgradeIdempotencyEntry
        {
            Fingerprint = fingerprint,
            ScheduleId = scheduleId,
            CachedResult = scheduleId == null ? result : null
        };
    }
}
