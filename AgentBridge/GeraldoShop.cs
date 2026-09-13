using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.GeraldoItems;
using Il2CppAssets.Scripts.Simulation.GeraldoItems;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Simulation.Input;
using Il2CppAssets.Scripts.Simulation.Powers;
using Il2CppAssets.Scripts.Simulation.Towers;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppNinjaKiwi.Localization;
using UnityEngine;
using SimTower = Il2CppAssets.Scripts.Simulation.Towers.Tower;

namespace AgentBridge;

internal sealed class GeraldoItemInfoV1
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string Category { get; init; } = "none";
    public string TargetKind { get; init; } = "none";
    public int? Cost { get; init; }
    public int Stock { get; init; }
    public int MaxStock { get; init; }
    public int UnlockLevel { get; init; }
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public int RoundsToReplenish { get; init; }
    public int? ReplenishingFromRound { get; init; }
    public bool CanBeActivatedBetweenRounds { get; init; }
}

internal sealed class InspectGeraldoResultV1
{
    public string Source { get; init; } = "active-simulation";
    public string HeroTowerId { get; init; } = "";
    public int HeroLevel { get; init; }
    public double Cash { get; init; }
    public List<GeraldoItemInfoV1> Items { get; init; } = [];
}

internal sealed class GeraldoTargetV1
{
    public string Kind { get; init; } = "point";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? X { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Y { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TowerId { get; init; }
}

internal sealed class CanUseGeraldoItemResultV1
{
    public bool Valid { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string ItemId { get; init; } = "";
    public string Category { get; init; } = "none";
    public GeraldoTargetV1? Target { get; init; }
    public int? Cost { get; init; }
    public double Cash { get; init; }
    public int Stock { get; init; }
}

internal sealed class GeraldoFailureV1
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class UseGeraldoItemResultV1
{
    public string Status { get; init; } = "failed";
    public bool Purchased { get; init; }
    public string? ScheduleId { get; init; }
    public string ItemId { get; init; } = "";
    public GeraldoTargetV1? Target { get; init; }
    public int? Cost { get; init; }
    public double? CashBefore { get; init; }
    public double? CashAfter { get; init; }
    public int? StockBefore { get; init; }
    public int? StockAfter { get; init; }
    public string[] CreatedTowerIds { get; init; } = [];
    public string? AffectedTowerId { get; init; }
    public GeraldoFailureV1? Failure { get; init; }
    public string? WaitingFor { get; init; }
    public int? ExecutionRound { get; init; }
    public float? ExecutionSeconds { get; init; }
}

internal sealed class ScheduledGeraldoPurchaseInfoV1
{
    public string ScheduleId { get; init; } = "";
    public string ItemId { get; init; } = "";
    public GeraldoTargetV1? Target { get; init; }
    public bool WhenAffordable { get; init; }
    public int? TargetRound { get; init; }
    public float? DelaySeconds { get; init; }
    public string Status { get; init; } = "pending";
    public string? WaitingFor { get; init; }
    public GeraldoFailureV1? Failure { get; init; }
    public UseGeraldoItemResultV1? Result { get; init; }
}

public sealed partial class AgentBridgeMod
{
    private const int MaxScheduledGeraldoPurchases = 32;
    private const int MaxGeraldoTerminalOutcomes = 64;
    private const int MaxGeraldoIdempotencyEntries = 512;

    private sealed class GeraldoContext
    {
        public InGame InGame { get; init; } = null!;
        public UnityToSimulation Bridge { get; init; } = null!;
        public Simulation Simulation { get; init; } = null!;
        public GeraldoPurchaseManager Manager { get; init; } = null!;
        public GeraldoShopInventory Inventory { get; init; } = null!;
        public TowerToSimulation HeroTower { get; init; } = null!;
        public SimTower HeroSimTower { get; init; } = null!;
        public int InputId { get; init; }
        public int Round { get; init; }
        public bool RoundsActive { get; init; }
        public int HeroLevel { get; init; }
        public Vector2 HeroPosition { get; init; }
    }

    private sealed class ParsedGeraldoRequest
    {
        public string ItemId { get; init; } = "";
        public GeraldoTargetV1? Target { get; init; }
        public bool WhenAffordable { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public float? DelayFromNow { get; init; }
        public string? IdempotencyKey { get; init; }
    }

    private sealed class GeraldoScheduleWork
    {
        public string ScheduleId { get; init; } = "";
        public string ItemId { get; init; } = "";
        public GeraldoTargetV1? Target { get; init; }
        public bool WhenAffordable { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public string Status { get; set; } = "pending";
        public string? WaitingFor { get; set; }
        public GeraldoFailureV1? Failure { get; set; }
        public int? Cost { get; set; }
        public UseGeraldoItemResultV1? Result { get; set; }
    }

    private sealed class GeraldoIdempotencyEntry
    {
        public string Fingerprint { get; init; } = "";
        public string? ScheduleId { get; init; }
        public UseGeraldoItemResultV1? CachedResult { get; init; }
    }

    private static readonly List<GeraldoScheduleWork> scheduledGeraldoPurchases = new();
    private static readonly Queue<ScheduledGeraldoPurchaseInfoV1> geraldoTerminalOutcomes = new();
    private static readonly Dictionary<string, GeraldoIdempotencyEntry> geraldoIdempotency = new(StringComparer.Ordinal);
    private static int geraldoScheduleCounter;

    private static BridgeResultV1 HandleInspectGeraldo(BridgeRequestV1 request)
    {
        if (!TryGetGeraldoContext(out var context, out var error))
            return ErrorResult(request, error!.Value.Code, error.Value.Message, error.Value.Retryable);

        var items = new List<GeraldoItemInfoV1>();
        try
        {
            foreach (var model in EnumerateGeraldoModels(context))
            {
                if (model == null || string.IsNullOrWhiteSpace(model.name)) continue;
                if (items.Count >= 16) break;
                var stock = context.Inventory.GetStockItem(model.name);
                int stockRemaining = stock?.remaining ?? 0;
                int? cost = TryGetModifiedCost(context.Inventory, model.name);
                bool nativeAvailable = false;
                try { nativeAvailable = context.Manager.CanPurchase(model, context.InputId, context.Simulation); }
                catch { }
                string? unavailable = nativeAvailable ? null : "Native Geraldo purchase eligibility currently rejects this item.";
                if (stock == null) unavailable ??= "Native Geraldo stock state is unavailable.";
                else if (stockRemaining <= 0) unavailable = "The native Geraldo item is out of stock.";
                else if (cost is null) unavailable = "The native Geraldo item price is unavailable.";
                else if (context.InGame.GetCash() < cost.Value) unavailable = "Insufficient cash for the native Geraldo item price.";
                items.Add(new GeraldoItemInfoV1
                {
                    ItemId = model.name,
                    Name = ReadGeraldoTitle(model),
                    Description = ReadGeraldoDescription(model),
                    Category = GeraldoCategory(model),
                    TargetKind = GeraldoTargetKind(model),
                    Cost = cost,
                    Stock = Math.Max(0, stockRemaining),
                    MaxStock = Math.Max(0, model.maxQuantity),
                    UnlockLevel = Math.Max(1, model.levelUnlockedAt),
                    Available = nativeAvailable && stockRemaining > 0 && cost.HasValue && context.InGame.GetCash() >= cost.Value,
                    UnavailableReason = unavailable,
                    RoundsToReplenish = Math.Max(0, model.roundsToReplenish),
                    ReplenishingFromRound = stock?.replenishingFromRound >= 0 ? stock.replenishingFromRound + 1 : null,
                    CanBeActivatedBetweenRounds = model.canBeActivatedBetweenRounds
                });
            }
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "GERALDO_INVENTORY_UNAVAILABLE", $"Native Geraldo inventory could not be read: {ex.Message}", true);
        }

        return SuccessResult(request, new InspectGeraldoResultV1
        {
            Source = "active-simulation",
            HeroTowerId = context.HeroTower.Id.ToString(),
            HeroLevel = Math.Max(0, context.HeroLevel),
            Cash = context.InGame.GetCash(),
            Items = items
        });
    }

    private static BridgeResultV1 HandleCanUseGeraldoItem(BridgeRequestV1 request)
    {
        if (!TryGetGeraldoContext(out var context, out var error))
            return ErrorResult(request, error!.Value.Code, error.Value.Message, error.Value.Retryable);
        if (!TryReadGeraldoRequest(request.Payload, context.Bridge, false, out var parsed, out var code, out var message))
            return ErrorResult(request, code, message, false);
        if (!TryResolveGeraldoModel(context, parsed.ItemId, out var model))
            return ErrorResult(request, "UNKNOWN_GERALDO_ITEM", $"Unknown native Geraldo item '{parsed.ItemId}'.", false);

        var check = CheckGeraldoEligibility(context, model!, parsed.Target, out var cost, out var stock);
        return SuccessResult(request, new CanUseGeraldoItemResultV1
        {
            Valid = check.Code == null,
            Code = check.Code,
            Message = check.Message,
            ItemId = parsed.ItemId,
            Category = GeraldoCategory(model!),
            Target = parsed.Target,
            Cost = cost,
            Cash = context.InGame.GetCash(),
            Stock = stock
        });
    }

    private static BridgeResultV1 HandleUseGeraldoItem(BridgeRequestV1 request)
    {
        if (!TryGetGeraldoContext(out var context, out var error))
            return ErrorResult(request, error!.Value.Code, error.Value.Message, error.Value.Retryable);
        if (!TryReadGeraldoRequest(request.Payload, context.Bridge, true, out var parsed, out var code, out var message, resolveTiming: false))
            return ErrorResult(request, code, message, false);
        if (!TryResolveGeraldoModel(context, parsed.ItemId, out var model))
            return ErrorResult(request, "UNKNOWN_GERALDO_ITEM", $"Unknown native Geraldo item '{parsed.ItemId}'.", false);

        string fingerprint = GeraldoFingerprint(parsed);
        if (parsed.IdempotencyKey is { Length: > 0 } key && geraldoIdempotency.TryGetValue(key, out var previous))
        {
            if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                return ErrorResult(request, "IDEMPOTENCY_KEY_REUSED", $"Idempotency key '{key}' was already used for different Geraldo input.", false);
            if (previous.CachedResult != null) return SuccessResult(request, previous.CachedResult);
            if (previous.ScheduleId != null)
            {
                var live = scheduledGeraldoPurchases.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (live != null) return SuccessResult(request, BuildGeraldoResult(live, context));
                var terminal = geraldoTerminalOutcomes.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (terminal?.Result != null) return SuccessResult(request, terminal.Result);
                return ErrorResult(request, "IDEMPOTENCY_OUTCOME_EVICTED", $"Idempotency outcome for key '{key}' is no longer retained; inspect the match before retrying.", false);
            }
        }
        if (parsed.IdempotencyKey is { Length: > 0 } newKey &&
            !geraldoIdempotency.ContainsKey(newKey) &&
            geraldoIdempotency.Count >= MaxGeraldoIdempotencyEntries)
            return ErrorResult(request, "IDEMPOTENCY_CAPACITY_EXCEEDED", "This match has reached its 512-key Geraldo idempotency limit. Existing keys remain protected from replay.", false);
        if (!TryReadGeraldoRequest(request.Payload, context.Bridge, true, out parsed, out code, out message))
            return ErrorResult(request, code, message, false);

        var eligibility = CheckGeraldoEligibility(context, model!, parsed.Target, out var cost, out var stock, scheduling: parsed.HasTiming);
        bool affordabilityOnly = parsed.WhenAffordable && !parsed.HasTiming;
        if (eligibility.Code != null && !((affordabilityOnly || parsed.HasTiming) && eligibility.Code == "INSUFFICIENT_CASH"))
        {
            var failed = BuildImmediateGeraldoFailure(parsed, cost, context.InGame.GetCash(), stock, eligibility.Code, eligibility.Message);
            RememberGeraldoIdempotency(parsed.IdempotencyKey, fingerprint, null, failed);
            return SuccessResult(request, failed);
        }

        if (parsed.HasTiming || parsed.WhenAffordable)
        {
            if (scheduledGeraldoPurchases.Count >= MaxScheduledGeraldoPurchases)
                return ErrorResult(request, "SCHEDULE_QUEUE_FULL", $"At most {MaxScheduledGeraldoPurchases} Geraldo purchases may be pending.", false);
            string scheduleId = $"geraldo_{++geraldoScheduleCounter}_{Guid.NewGuid():N}";
            var work = new GeraldoScheduleWork
            {
                ScheduleId = scheduleId,
                ItemId = parsed.ItemId,
                Target = parsed.Target,
                WhenAffordable = parsed.WhenAffordable,
                HasTiming = parsed.HasTiming,
                TargetRound = parsed.TargetRound,
                DelaySeconds = parsed.DelaySeconds,
                Cost = cost,
            };
            scheduledGeraldoPurchases.Add(work);
            RememberGeraldoIdempotency(parsed.IdempotencyKey, fingerprint, work.ScheduleId, null);

            if (!work.HasTiming && work.WhenAffordable && CanProcessGeraldoAutomatically(context.InGame))
                ProcessOneGeraldoPurchase(context, work);
            if (work.Status is "completed" or "failed" or "expired" or "verification_pending")
            {
                scheduledGeraldoPurchases.Remove(work);
                var terminal = SnapshotGeraldoPurchase(work);
                EnqueueGeraldoTerminal(work, terminal);
                return SuccessResult(request, terminal.Result!);
            }
            return SuccessResult(request, BuildGeraldoResult(work, context));
        }

        var immediate = ExecuteGeraldoPurchase(context, model!, parsed.Target, cost);
        RememberGeraldoIdempotency(parsed.IdempotencyKey, fingerprint, null, immediate);
        return SuccessResult(request, immediate);
    }

    private static BridgeResultV1 HandleCancelScheduledGeraldoPurchase(BridgeRequestV1 request)
    {
        if (!TryReadScheduleCancellation(request.Payload, out var scheduleId, out var cancellationError))
            return ErrorResult(request, "INVALID_ARGUMENT", cancellationError!, false);

        if (scheduleId == null || string.Equals(scheduleId, "all", StringComparison.OrdinalIgnoreCase))
        {
            int count = scheduledGeraldoPurchases.Count;
            foreach (var work in scheduledGeraldoPurchases.ToArray())
            {
                work.Status = "cancelled";
                work.WaitingFor = null;
                work.Result = BuildCancelledGeraldoResult(work);
                EnqueueGeraldoTerminal(work, SnapshotGeraldoPurchase(work));
            }
            scheduledGeraldoPurchases.Clear();
            return SuccessResult(request, new { Cancelled = count > 0, Count = count });
        }

        var item = scheduledGeraldoPurchases.FirstOrDefault(candidate => candidate.ScheduleId == scheduleId);
        if (item == null)
            return ErrorResult(request, "SCHEDULE_NOT_FOUND", $"Scheduled Geraldo purchase with ID '{scheduleId}' not found.", false);
        item.Status = "cancelled";
        item.WaitingFor = null;
        item.Result = BuildCancelledGeraldoResult(item);
        scheduledGeraldoPurchases.Remove(item);
        EnqueueGeraldoTerminal(item, SnapshotGeraldoPurchase(item));
        return SuccessResult(request, new { Cancelled = true, Count = 1 });
    }

    private static bool CanProcessGeraldoAutomatically(InGame inGame)
    {
        if (isMatchPaused || TimeManager.gamePaused || UnityEngine.Time.timeScale <= 0f) return false;
        try
        {
            if (inGame.MatchLost || inGame.WaitingForVictoryScreen) return false;
            UpdateUiState(false);
            return uiState.Ready;
        }
        catch { return false; }
    }

    private static void ProcessScheduledGeraldoPurchases()
    {
        if (scheduledGeraldoPurchases.Count == 0) return;
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame() || inGame.bridge == null) return;
        if (!CanProcessGeraldoAutomatically(inGame)) return;
        var bridge = inGame.bridge;
        int round;
        bool active;
        try { round = bridge.GetCurrentRound() + 1; active = bridge.AreRoundsActive(); }
        catch { return; }
        float elapsed = active && hasRoundElapsedTime ? currentRoundElapsedSeconds : -1f;
        ProcessScheduledGeraldoPurchases(inGame, bridge, round, elapsed);
    }

    private static void ProcessScheduledGeraldoPurchases(InGame inGame, UnityToSimulation bridge, int round, float elapsedSeconds)
    {
        if (scheduledGeraldoPurchases.Count == 0 || !CanProcessGeraldoAutomatically(inGame)) return;
        if (!TryGetGeraldoContext(out var context, out var contextError))
        {
            var failure = new GeraldoFailureV1
            {
                Code = contextError?.Code ?? "GERALDO_NOT_FOUND",
                Message = contextError?.Message ?? "The native Geraldo hero or shop inventory is unavailable."
            };
            foreach (var work in scheduledGeraldoPurchases.ToArray())
                RetireGeraldoWorkWithoutContext(work, failure);
            scheduledGeraldoPurchases.Clear();
            return;
        }
        for (int i = 0; i < scheduledGeraldoPurchases.Count && i < MaxScheduledGeraldoPurchases; i++)
        {
            var work = scheduledGeraldoPurchases[i];
            if (!TryResolveGeraldoModel(context, work.ItemId, out var model))
            {
                RetireGeraldoWork(work, "failed", new GeraldoFailureV1 { Code = "UNKNOWN_GERALDO_ITEM", Message = $"Native Geraldo item '{work.ItemId}' is no longer present." }, context);
                scheduledGeraldoPurchases.RemoveAt(i--);
                continue;
            }
            if (work.HasTiming && work.TargetRound.HasValue && round > work.TargetRound.Value)
            {
                RetireGeraldoWork(work, "expired", new GeraldoFailureV1 { Code = "TARGET_ROUND_PASSED", Message = $"Target round {work.TargetRound.Value} ended before the Geraldo purchase executed." }, context);
                scheduledGeraldoPurchases.RemoveAt(i--);
                continue;
            }
            if (work.HasTiming)
            {
                if (!work.TargetRound.HasValue || !work.DelaySeconds.HasValue)
                {
                    RetireGeraldoWork(work, "failed", new GeraldoFailureV1 { Code = "INVALID_SCHEDULE", Message = "Scheduled Geraldo timing is incomplete." }, context);
                    scheduledGeraldoPurchases.RemoveAt(i--);
                    continue;
                }
                if (elapsedSeconds < 0f || round < work.TargetRound.Value || elapsedSeconds < work.DelaySeconds.Value)
                {
                    work.Status = "waiting";
                    work.WaitingFor = "time";
                    continue;
                }
            }

            var eligibility = CheckGeraldoEligibility(context, model!, work.Target, out var cost, out var stock,
                deferUnaffordableTargetCheck: work.WhenAffordable);
            work.Cost = cost;
            if (eligibility.Code != null)
            {
                if (eligibility.Code == "INSUFFICIENT_CASH" && work.WhenAffordable)
                {
                    work.Status = "waiting";
                    work.WaitingFor = "cash";
                    continue;
                }
                RetireGeraldoWork(work, "failed", new GeraldoFailureV1 { Code = eligibility.Code, Message = eligibility.Message! }, context);
                scheduledGeraldoPurchases.RemoveAt(i--);
                continue;
            }

            work.Status = "pending";
            work.WaitingFor = null;
            var result = ExecuteGeraldoPurchase(context, model!, work.Target, cost);
            work.Result = WithGeraldoScheduleId(result, work.ScheduleId);
            work.Status = result.Status;
            work.Failure = result.Failure;
            if (result.Status is "completed" or "failed" or "verification_pending")
            {
                EnqueueGeraldoTerminal(work, SnapshotGeraldoPurchase(work));
                scheduledGeraldoPurchases.RemoveAt(i);
            }
            return;
        }
    }

    private static void ProcessOneGeraldoPurchase(GeraldoContext context, GeraldoScheduleWork work)
    {
        if (!TryResolveGeraldoModel(context, work.ItemId, out var model))
        {
            work.Status = "failed";
            work.Failure = new GeraldoFailureV1 { Code = "UNKNOWN_GERALDO_ITEM", Message = $"Native Geraldo item '{work.ItemId}' is no longer present." };
            work.Result = BuildGeraldoResult(work, context);
            return;
        }
        var eligibility = CheckGeraldoEligibility(context, model!, work.Target, out var cost, out var stock,
            deferUnaffordableTargetCheck: work.WhenAffordable);
        work.Cost = cost;
        if (eligibility.Code != null && !(eligibility.Code == "INSUFFICIENT_CASH" && work.WhenAffordable))
        {
            work.Status = "failed";
            work.Failure = new GeraldoFailureV1 { Code = eligibility.Code, Message = eligibility.Message! };
            work.Result = BuildGeraldoResult(work, context);
            return;
        }
        if (eligibility.Code == "INSUFFICIENT_CASH")
        {
            work.Status = "waiting";
            work.WaitingFor = "cash";
            work.Result = BuildGeraldoResult(work, context);
            return;
        }
        work.Result = WithGeraldoScheduleId(ExecuteGeraldoPurchase(context, model!, work.Target, cost), work.ScheduleId);
        work.Status = work.Result.Status;
        work.Failure = work.Result.Failure;
    }

    private static void RetireGeraldoWorkWithoutContext(GeraldoScheduleWork work, GeraldoFailureV1 failure)
    {
        work.Status = "failed";
        work.Failure = failure;
        work.WaitingFor = null;
        work.Result = new UseGeraldoItemResultV1
        {
            Status = work.Status, Purchased = false, ScheduleId = work.ScheduleId, ItemId = work.ItemId,
            Target = work.Target, Cost = work.Cost, CashBefore = null, CashAfter = null,
            StockBefore = null, StockAfter = null, CreatedTowerIds = [],
            AffectedTowerId = work.Target?.Kind == "tower" ? work.Target.TowerId : null,
            Failure = work.Failure, WaitingFor = null, ExecutionRound = null, ExecutionSeconds = null
        };
        EnqueueGeraldoTerminal(work, SnapshotGeraldoPurchase(work));
    }

    private static void RetireGeraldoWork(GeraldoScheduleWork work, string status, GeraldoFailureV1 failure, GeraldoContext context)
    {
        work.Status = status;
        work.Failure = failure;
        work.WaitingFor = null;
        work.Result = null;
        work.Result = BuildGeraldoResult(work, context);
        EnqueueGeraldoTerminal(work, SnapshotGeraldoPurchase(work));
    }

    private static void EnqueueGeraldoTerminal(GeraldoScheduleWork work, ScheduledGeraldoPurchaseInfoV1 terminal)
    {
        while (geraldoTerminalOutcomes.Count >= MaxGeraldoTerminalOutcomes) geraldoTerminalOutcomes.Dequeue();
        geraldoTerminalOutcomes.Enqueue(terminal);
        int round = work.TargetRound ?? (InGame.instance?.bridge?.GetCurrentRound() + 1 ?? 1);
        AppendRoundActionOutcome(work.ScheduleId, "geraldo", round, work.Status, work.Failure?.Code,
            terminal.Result?.ExecutionSeconds);
    }

    private static ScheduledGeraldoPurchaseInfoV1 SnapshotGeraldoPurchase(GeraldoScheduleWork work) => new()
    {
        ScheduleId = work.ScheduleId,
        ItemId = work.ItemId,
        Target = work.Target,
        WhenAffordable = work.WhenAffordable,
        TargetRound = work.TargetRound,
        DelaySeconds = work.DelaySeconds,
        Status = work.Status,
        WaitingFor = work.WaitingFor,
        Failure = work.Failure,
        Result = work.Result
    };

    private static UseGeraldoItemResultV1 WithGeraldoScheduleId(UseGeraldoItemResultV1 result, string scheduleId) => new()
    {
        Status = result.Status, Purchased = result.Purchased, ScheduleId = scheduleId, ItemId = result.ItemId,
        Target = result.Target, Cost = result.Cost, CashBefore = result.CashBefore, CashAfter = result.CashAfter,
        StockBefore = result.StockBefore, StockAfter = result.StockAfter, CreatedTowerIds = result.CreatedTowerIds,
        AffectedTowerId = result.AffectedTowerId, Failure = result.Failure, WaitingFor = result.WaitingFor,
        ExecutionRound = result.ExecutionRound, ExecutionSeconds = result.ExecutionSeconds
    };

    private static UseGeraldoItemResultV1 BuildGeraldoResult(GeraldoScheduleWork work, GeraldoContext context)
    {
        if (work.Result != null && (work.Status is "completed" or "failed" or "expired" or "cancelled" or "verification_pending"))
            return work.Result;
        return new UseGeraldoItemResultV1
        {
            Status = work.Status,
            Purchased = false,
            ScheduleId = work.ScheduleId,
            ItemId = work.ItemId,
            Target = work.Target,
            Cost = work.Cost,
            CashBefore = FiniteCash(TryGetCash(context.InGame)),
            CashAfter = null,
            StockBefore = TryGetStock(context.Inventory, work.ItemId),
            StockAfter = null,
            CreatedTowerIds = [],
            AffectedTowerId = work.Target?.Kind == "tower" ? work.Target.TowerId : null,
            Failure = work.Failure,
            WaitingFor = work.WaitingFor,
            ExecutionRound = null,
            ExecutionSeconds = null
        };
    }

    private static UseGeraldoItemResultV1 BuildCancelledGeraldoResult(GeraldoScheduleWork work) => new()
    {
        Status = "cancelled", Purchased = false, ScheduleId = work.ScheduleId, ItemId = work.ItemId,
        Target = work.Target, Cost = work.Cost, CashBefore = null, CashAfter = null,
        StockBefore = null, StockAfter = null, CreatedTowerIds = [], AffectedTowerId = work.Target?.Kind == "tower" ? work.Target.TowerId : null,
        Failure = null, WaitingFor = null, ExecutionRound = null, ExecutionSeconds = null
    };

    private static UseGeraldoItemResultV1 BuildImmediateGeraldoFailure(ParsedGeraldoRequest parsed, int? cost, double cash, int stock, string code, string? message) => new()
    {
        Status = "failed", Purchased = false, ScheduleId = null, ItemId = parsed.ItemId, Target = parsed.Target,
        Cost = cost, CashBefore = cash, CashAfter = cash, StockBefore = stock, StockAfter = stock,
        CreatedTowerIds = [], AffectedTowerId = parsed.Target?.Kind == "tower" ? parsed.Target.TowerId : null,
        Failure = new GeraldoFailureV1 { Code = code, Message = message ?? "Native Geraldo eligibility rejected the purchase." }, WaitingFor = null,
        ExecutionRound = null, ExecutionSeconds = null
    };

    private static UseGeraldoItemResultV1 ExecuteGeraldoPurchase(GeraldoContext context, GeraldoItemModel model,
        GeraldoTargetV1? target, int? expectedCost)
    {
        double? cashBefore = TryGetCash(context.InGame);
        var beforeIds = ReadTowerIds(context.InGame);
        int? stockBefore = TryGetStock(context.Inventory, model.name);
        int? cost = expectedCost ?? TryGetModifiedCost(context.Inventory, model.name);
        Vector2 location = ResolvePurchaseLocation(context, target);
        try
        {
            bool previousImmediate = context.Bridge.IsImmediateMode;
            try
            {
                context.Bridge.IsImmediateMode = true;
                if (GeraldoCategory(model) == "tower_placement")
                {
                    var towerModel = model.GetGeraldoItemTowerModel(context.Simulation, context.InputId);
                    context.Bridge.CreateTowerAt(context.InputId, location, towerModel,
                        Il2CppAssets.Scripts.ObjectId.Invalid, false, (Il2CppSystem.Action<bool>)(_ => { }));
                }
                else
                {
                    context.Bridge.PurchaseGeraldoItem(context.InputId, location, model);
                }
            }
            finally { context.Bridge.IsImmediateMode = previousImmediate; }
        }
        catch (Exception ex)
        {
            return BuildPurchaseResult("verification_pending", false, model.name, target, cost, cashBefore, null,
                stockBefore, null, beforeIds, context.InGame, target?.Kind == "tower" ? target.TowerId : null,
                new GeraldoFailureV1 { Code = "GERALDO_OUTCOME_UNKNOWN", Message = $"Native Geraldo purchase outcome is unknown: {ex.Message}" }, context);
        }

        double? cashAfter = TryGetCash(context.InGame);
        int? stockAfter = TryGetStock(context.Inventory, model.name);
        var afterIds = ReadTowerIds(context.InGame);
        string[] created = afterIds.Except(beforeIds, StringComparer.Ordinal).Take(128).ToArray();
        bool stockChanged = stockBefore.HasValue && stockAfter.HasValue && stockAfter.Value == stockBefore.Value - 1;
        bool cashChanged = cost.HasValue && cashBefore.HasValue && cashAfter.HasValue &&
            Math.Abs((cashBefore.Value - cashAfter.Value) - cost.Value) < 0.01;
        bool expectedObservable = stockChanged && (cost.GetValueOrDefault() == 0 || cashChanged);
        if (GeraldoCategory(model) == "tower_placement" && created.Length == 0)
            expectedObservable = false;
        if (!expectedObservable)
        {
            return BuildPurchaseResult("verification_pending", false, model.name, target, cost, cashBefore, cashAfter,
                stockBefore, stockAfter, beforeIds, context.InGame, target?.Kind == "tower" ? target.TowerId : null,
                new GeraldoFailureV1 { Code = "GERALDO_OUTCOME_UNKNOWN", Message = "Native Geraldo purchase returned without the expected stock, cash, or tower state change." }, context, created);
        }
        return BuildPurchaseResult("completed", true, model.name, target, cost, cashBefore, cashAfter,
            stockBefore, stockAfter, beforeIds, context.InGame, target?.Kind == "tower" ? target.TowerId : null, null, context, created);
    }

    private static UseGeraldoItemResultV1 BuildPurchaseResult(string status, bool purchased, string itemId, GeraldoTargetV1? target,
        int? cost, double? cashBefore, double? cashAfter, int? stockBefore, int? stockAfter, HashSet<string> beforeIds,
        InGame inGame, string? affectedTowerId, GeraldoFailureV1? failure, GeraldoContext context, string[]? created = null) => new()
    {
        Status = status, Purchased = purchased, ScheduleId = null, ItemId = itemId, Target = target, Cost = cost,
        CashBefore = FiniteCash(cashBefore), CashAfter = FiniteCash(cashAfter), StockBefore = stockBefore, StockAfter = stockAfter,
        CreatedTowerIds = created ?? ReadTowerIds(inGame).Except(beforeIds, StringComparer.Ordinal).Take(128).ToArray(),
        AffectedTowerId = affectedTowerId, Failure = failure, WaitingFor = null,
        ExecutionRound = status == "completed" ? context.Round : null,
        ExecutionSeconds = status == "completed" && context.RoundsActive && hasRoundElapsedTime ? currentRoundElapsedSeconds : null
    };

    private static (string? Code, string? Message) CheckGeraldoEligibility(GeraldoContext context, GeraldoItemModel model,
        GeraldoTargetV1? target, out int? cost, out int stock, bool scheduling = false, bool deferUnaffordableTargetCheck = false)
    {
        cost = TryGetModifiedCost(context.Inventory, model.name);
        stock = Math.Max(0, TryGetStock(context.Inventory, model.name) ?? 0);
        if (!TargetMatchesModel(model, target, out var targetCode, out var targetMessage)) return (targetCode, targetMessage);
        try
        {
            UpdateUiState(false);
            if (!uiState.Ready) return ("UI_BLOCKED", "A UI interruption blocks Geraldo purchases.");
            if (context.InGame.MatchLost || context.InGame.WaitingForVictoryScreen)
                return ("MATCH_ENDED", "Geraldo purchases are unavailable after defeat or while awaiting victory.");
            if (context.HeroSimTower.IsStunned || context.HeroSimTower.AreAbilitiesDisabled)
                return ("GERALDO_STUNNED", "Geraldo is stunned or disabled by the native simulation.");
            if (context.HeroLevel < model.levelUnlockedAt)
                return ("ITEM_LOCKED", $"This item unlocks at Geraldo level {model.levelUnlockedAt}.");
            if (!scheduling && !context.RoundsActive && !model.canBeActivatedBetweenRounds)
                return ("BETWEEN_ROUNDS_NOT_ALLOWED", "This native Geraldo item cannot be activated between rounds.");
            var mode = InGameData.CurrentGame?.selectedMode;
            if (model.bannedForModesList != null)
                foreach (var bannedMode in model.bannedForModesList)
                    if (string.Equals(bannedMode.Trim(), mode, StringComparison.OrdinalIgnoreCase))
                        return ("MODE_RESTRICTED", "The active mode bans this native Geraldo item.");
            if (context.Inventory.HasHitMaxPurchaseLimit(model.name))
                return ("PURCHASE_LIMIT", "The native Geraldo purchase limit has been reached.");
            if (stock <= 0) return ("OUT_OF_STOCK", "The native Geraldo item is out of stock.");
            if (cost == null) return ("PRICE_UNAVAILABLE", "The native Geraldo item price is unavailable.");
            double cash = context.InGame.GetCash();
            if (!double.IsFinite(cash)) return ("CASH_UNAVAILABLE", "The native wallet is unavailable.");
            // Waiting for cash must not construct native item behaviors every frame.
            // Full target validation ran at submission and runs again before spending.
            if (deferUnaffordableTargetCheck && cash < cost.Value)
                return ("INSUFFICIENT_CASH", "Insufficient cash for the native Geraldo item price.");
            Vector2 location = ResolvePurchaseLocation(context, target);
            if (!IsPointWithinMap(context.Bridge, location.x, location.y))
                return ("INVALID_TARGET", "The requested Geraldo item location is outside the map.");
            if (GeraldoCategory(model) == "tower_placement")
            {
                var towerModel = model.GetGeraldoItemTowerModel(context.Simulation, context.InputId);
                if (towerModel == null || !towerModel.isGeraldoItem || towerModel.geraldoItemName != model.name)
                    return ("NATIVE_STATE_UNAVAILABLE", "The native Geraldo placement model is unavailable or untagged.");
                if (!context.Bridge.CanPlaceTowerAt(location, towerModel, context.InputId, Il2CppAssets.Scripts.ObjectId.Invalid))
                    return ("INVALID_TARGET", "Native tower footprint or terrain rules reject this Geraldo placement.");
            }
            if (!CheckGeraldoLocationIsolated(context, location, model))
                return ("INVALID_TARGET", "Native Geraldo location validation rejected the requested target.");
            if (target?.Kind == "tower")
            {
                // CheckLocation selects a native tower by location. Read that selection
                // rather than assuming any valid nearby recipient is the requested one.
                var nativeItem = new GeraldoItem();
                try
                {
                    nativeItem.Initialise(model, context.Simulation);
                    var point = new Il2CppAssets.Scripts.Simulation.SMath.Vector2(location.x, location.y);
                    bool selectedRequestedTower = false;
                    foreach (var behavior in nativeItem.behaviors)
                    {
                        var highlighting = behavior.TryCast<GeraldoTowerHighlightingBehavior>();
                        if (highlighting == null) continue;
                        if (!behavior.CheckLocation(point, context.InputId))
                            return ("INVALID_TARGET", "Native Geraldo behavior rejects the requested tower.");
                        var selected = highlighting.lastHighlightedTower;
                        if (selected == null || selected.Id.ToString() != target.TowerId)
                            return ("INVALID_TARGET", "Native Geraldo selection does not resolve to the requested tower.");
                        selectedRequestedTower = true;
                    }
                    if (!selectedRequestedTower)
                        return ("NATIVE_LOCATION_UNAVAILABLE", "Native Geraldo tower selection could not be verified.");
                }
                finally
                {
                    nativeItem.Destroy();
                }
            }
            if (cash < cost.Value)
                return ("INSUFFICIENT_CASH", $"Cannot afford native Geraldo item: costs ${cost.Value}, have ${cash:F0}.");
            if (!context.Manager.CanPurchase(model, context.InputId, context.Simulation))
                return ("NATIVE_PURCHASE_UNAVAILABLE", "Native Geraldo purchase eligibility rejected this item.");
            return (null, null);
        }
        catch (Exception ex)
        {
            return ("NATIVE_STATE_UNAVAILABLE", $"Native Geraldo eligibility could not be verified: {ex.Message}");
        }
    }

    private static bool CheckGeraldoLocationIsolated(GeraldoContext context, Vector2 location, GeraldoItemModel model)
    {
        // Native CheckLocation reuses currentlyPlacingItem without checking its model.
        // Keep an existing UI selection alive, but never use it for an independent
        // bridge quote or leave the quote's temporary item behind (even on rejection).
        var previousItem = context.Manager.currentlyPlacingItem;
        context.Manager.currentlyPlacingItem = null;
        try
        {
            return context.Bridge.CheckGeraldoPurchaseLocation(location, context.InputId, model);
        }
        finally
        {
            try { context.Manager.StopPlacing(); }
            finally { context.Manager.currentlyPlacingItem = previousItem; }
        }
    }

    private static bool TargetMatchesModel(GeraldoItemModel model, GeraldoTargetV1? target, out string code, out string message)
    {
        string kind = GeraldoTargetKind(model);
        if (kind == "unsupported")
        {
            code = "UNSUPPORTED_ITEM"; message = $"Native Geraldo item '{model.name}' is not supported by this bridge.";
            return false;
        }
        if (kind == "none")
        {
            if (target != null) { code = "INVALID_TARGET_KIND"; message = "This Geraldo item does not accept a target."; return false; }
            code = ""; message = ""; return true;
        }
        if (target == null) { code = "TARGET_REQUIRED"; message = "A native Geraldo target is required for this item."; return false; }
        if (!string.Equals(target.Kind, kind, StringComparison.Ordinal))
        {
            code = "INVALID_TARGET_KIND"; message = $"This Geraldo item requires a '{kind}' target."; return false;
        }
        if (target.Kind == "tower")
        {
            var inGame = InGame.instance;
            var tower = inGame?.GetAllTowerToSim()?.FirstOrDefault(t => t != null && t.Id.ToString() == target.TowerId);
            if (tower == null) { code = "TOWER_NOT_FOUND"; message = $"Tower with ID '{target.TowerId}' was not found."; return false; }
            if (tower.owner != InGame.instance?.bridge?.GetInputId()) { code = "TOWER_NOT_OWNED"; message = "The native Geraldo item target must belong to the active player."; return false; }
            if (model.name == "Fertilizer" && tower.Def?.baseId != "BananaFarm")
            { code = "INVALID_TARGET"; message = "Fertilizer requires an eligible Banana Farm."; return false; }
        }
        code = ""; message = ""; return true;
    }

    private static Vector2 ResolvePurchaseLocation(GeraldoContext context, GeraldoTargetV1? target)
    {
        if (target?.Kind == "point") return new Vector2(target.X!.Value, target.Y!.Value);
        if (target?.Kind == "tower")
        {
            var tower = context.InGame.GetAllTowerToSim()?.FirstOrDefault(t => t != null && t.Id.ToString() == target.TowerId);
            if (tower != null) return new Vector2(tower.simPosition.x, tower.simPosition.y);
            throw new InvalidOperationException($"Geraldo target tower '{target.TowerId}' disappeared before execution.");
        }
        return context.HeroPosition;
    }

    private static bool TryGetGeraldoContext(out GeraldoContext context, out (string Code, string Message, bool Retryable)? error)
    {
        context = null!; error = null;
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame()) { error = ("NO_ACTIVE_GAME", "Geraldo shop actions require an active match.", false); return false; }
        var bridge = inGame.bridge;
        if (bridge == null || bridge.Simulation == null) { error = ("SIMULATION_UNAVAILABLE", "The active simulation bridge is unavailable.", true); return false; }
        try
        {
            int inputId = bridge.GetInputId();
            var simulation = bridge.Simulation;
            var manager = simulation.geraldoPurchaseManager;
            var inventory = bridge.GetGeraldoShopInventory(inputId);
            var nativeHero = manager?.GetGeraldoTower(inputId);
            var towers = inGame.GetAllTowerToSim();
            TowerToSimulation? hero = null;
            if (towers != null)
                foreach (var candidate in towers)
                {
                    if (candidate?.hero == null || candidate.owner != inputId) continue;
                    var candidateSim = candidate.GetSimTower();
                    if (nativeHero == null || candidateSim == null || candidateSim.Pointer == nativeHero.Pointer)
                    {
                        if (string.Equals(candidate.Def?.baseId, "Geraldo", StringComparison.OrdinalIgnoreCase)) { hero = candidate; break; }
                        hero ??= candidate;
                    }
                }
            if (manager == null || inventory == null || hero == null || nativeHero == null)
            {
                error = ("GERALDO_NOT_FOUND", "The native Geraldo hero or shop inventory is unavailable.", false); return false;
            }
            context = new GeraldoContext
            {
                InGame = inGame, Bridge = bridge, Simulation = simulation, Manager = manager, Inventory = inventory,
                HeroTower = hero, HeroSimTower = nativeHero, InputId = inputId,
                Round = bridge.GetCurrentRound() + 1, RoundsActive = bridge.AreRoundsActive(), HeroLevel = hero.hero.level,
                HeroPosition = new Vector2(hero.simPosition.x, hero.simPosition.y)
            };
            return true;
        }
        catch (Exception ex) { error = ("NATIVE_STATE_UNAVAILABLE", $"Native Geraldo state is unavailable: {ex.Message}", true); return false; }
    }

    private static IEnumerable<GeraldoItemModel> EnumerateGeraldoModels(GeraldoContext context)
    {
        if (context.Inventory.geraldoItemModelsByName != null)
            foreach (var pair in context.Inventory.geraldoItemModelsByName)
                if (pair.Value != null && GeraldoCategory(pair.Value) != "unsupported") yield return pair.Value;
    }

    private static bool TryResolveGeraldoModel(GeraldoContext context, string itemId, out GeraldoItemModel? model)
    {
        model = null;
        try
        {
            foreach (var candidate in EnumerateGeraldoModels(context))
                if (string.Equals(candidate.name, itemId, StringComparison.Ordinal)) { model = candidate; return true; }
        }
        catch { }
        return false;
    }

    private static int? TryGetModifiedCost(GeraldoShopInventory inventory, string itemId)
    {
        try { int cost = inventory.GetModifiedCost(itemId, true); return cost >= 0 ? cost : null; }
        catch { return null; }
    }

    private static int? TryGetStock(GeraldoShopInventory inventory, string itemId)
    {
        try { var stock = inventory.GetStockItem(itemId); return stock == null ? null : Math.Max(0, stock.remaining); }
        catch { return null; }
    }

    private static HashSet<string> ReadTowerIds(InGame inGame)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try { foreach (var tower in inGame.GetAllTowerToSim() ?? []) if (tower != null) ids.Add(tower.Id.ToString()); }
        catch { }
        return ids;
    }

    private static double? FiniteCash(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;

    private static string GeraldoCategory(GeraldoItemModel model) => model.name switch
    {
        "ShootyTurret" or "CreepyIdol" or "RareQuincyActionFigure" or "GenieBottle" or "ParagonPowerTotem" => "tower_placement",
        "JarOfPickles" or "SeeInvisibilityPotion" or "SharpeningStone" or "WornHerosCape" or "BottleHotSauce" or "Fertilizer" => "tower_target",
        "PetRabbit" => "geraldo_range",
        "StackOfOldNails" or "TubeOfAmazoGlue" or "BladeTrap" => "track",
        "RejuvPotion" => "none",
        _ => "unsupported"
    };
    private static string GeraldoTargetKind(GeraldoItemModel model) => GeraldoCategory(model) switch
    {
        "tower_target" => "tower",
        "tower_placement" or "track" => "point",
        "geraldo_range" or "none" => "none",
        _ => "unsupported"
    };

    private static string ReadGeraldoTitle(GeraldoItemModel model)
    {
        try
        {
            var localization = LocalizationManager.Instance;
            if (localization != null)
                foreach (string key in new[] { model.locsId + ".name", model.locsId + ".title", model.locsId, model.name })
                    if (!string.IsNullOrWhiteSpace(key) && localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        catch { }
        return model.name ?? "";
    }

    private static string? ReadGeraldoDescription(GeraldoItemModel model)
    {
        try
        {
            var localization = LocalizationManager.Instance;
            if (localization != null)
                foreach (string key in new[] { model.locsId + ".description", model.locsId + ".desc", model.locsId + ".Description" })
                    if (!string.IsNullOrWhiteSpace(key) && localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        catch { }
        return null;
    }

    private static bool TryReadGeraldoRequest(JsonElement payload, UnityToSimulation bridge, bool allowTiming,
        out ParsedGeraldoRequest parsed, out string code, out string message, bool resolveTiming = true)
    {
        parsed = new ParsedGeraldoRequest(); code = "INVALID_ARGUMENT"; message = "Geraldo payload must be an object.";
        if (payload.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in payload.EnumerateObject())
        {
            bool known = property.Name is "itemId" or "target" or "whenAffordable" or "round" or "delaySeconds" or "delayFromNow" or "idempotencyKey";
            if (!known || (!allowTiming && property.Name is not ("itemId" or "target")))
            {
                message = allowTiming
                    ? $"Unknown Geraldo field '{property.Name}'."
                    : "can_use_geraldo_item accepts only itemId and target.";
                return false;
            }
        }
        if (!payload.TryGetProperty("itemId", out var itemProp) || itemProp.ValueKind != JsonValueKind.String)
        { message = "itemId must be provided."; return false; }
        string itemId = itemProp.GetString() ?? "";
        if (itemId.Trim().Length == 0 || itemId.Length > 128 || itemId != itemId.Trim()) { message = "itemId must contain 1-128 non-whitespace characters."; return false; }
        GeraldoTargetV1? target = null;
        if (payload.TryGetProperty("target", out var targetProp))
        {
            if (!TryReadGeraldoTarget(targetProp, out target)) { message = "target must be exactly {kind:'point',x,y} or {kind:'tower',towerId}."; return false; }
        }
        bool whenAffordable = false;
        if (payload.TryGetProperty("whenAffordable", out var affordableProp))
        {
            if (affordableProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { message = "whenAffordable must be a boolean."; return false; }
            whenAffordable = affordableProp.GetBoolean();
        }
        bool hasRound = payload.TryGetProperty("round", out var roundProp);
        int? round = null;
        if (hasRound && (roundProp.ValueKind != JsonValueKind.Number || !roundProp.TryGetInt32(out var roundValue) || roundValue <= 0)) { message = "round must be a positive integer."; return false; }
        if (hasRound) round = roundProp.GetInt32();
        bool hasDelay = payload.TryGetProperty("delaySeconds", out var delayProp);
        float? delay = null;
        if (hasDelay && (delayProp.ValueKind != JsonValueKind.Number || !delayProp.TryGetSingle(out var delayValue) || !float.IsFinite(delayValue) || delayValue < 0f)) { message = "delaySeconds must be finite and nonnegative."; return false; }
        if (hasDelay) delay = delayProp.GetSingle();
        bool hasFromNow = payload.TryGetProperty("delayFromNow", out var fromNowProp);
        float? fromNow = null;
        if (hasFromNow)
        {
            if (hasRound || hasDelay || fromNowProp.ValueKind != JsonValueKind.Number || !fromNowProp.TryGetSingle(out var fromNowValue) || !float.IsFinite(fromNowValue) || fromNowValue < 0f) { message = "delayFromNow cannot be combined with round or delaySeconds and must be finite and nonnegative."; return false; }
            fromNow = fromNowValue;
            if (resolveTiming)
            {
                if (!bridge.AreRoundsActive() || !hasRoundElapsedTime) { code = "ROUND_TIME_UNAVAILABLE"; message = "delayFromNow requires an active round with a native round clock."; return false; }
                round = bridge.GetCurrentRound() + 1;
                delay = currentRoundElapsedSeconds + fromNowValue;
                if (!float.IsFinite(delay.Value)) { message = "Resolved delay exceeds the native clock range."; return false; }
            }
        }
        bool hasTiming = hasRound || hasDelay || hasFromNow;
        if (!allowTiming && hasTiming) { message = "Timing fields are not accepted by can_use_geraldo_item."; return false; }
        if (resolveTiming && hasTiming && fromNow == null)
        {
            round ??= bridge.GetCurrentRound() + 1; delay ??= 0f;
            int currentRound = bridge.GetCurrentRound() + 1;
            if (round.Value < currentRound) { code = "TARGET_ROUND_PASSED"; message = $"Target round {round.Value} has already passed (current round is {currentRound})."; return false; }
            if (round.Value == currentRound && bridge.AreRoundsActive() && hasRoundElapsedTime && delay.Value < currentRoundElapsedSeconds) { code = "DELAY_PASSED"; message = $"Delay {delay.Value:F2}s has already passed in round {currentRound}."; return false; }
        }
        string? idempotency = null;
        if (payload.TryGetProperty("idempotencyKey", out var keyProp))
        {
            if (keyProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyProp.GetString()) || keyProp.GetString()!.Length > 128) { message = "idempotencyKey must contain 1-128 non-whitespace characters."; return false; }
            idempotency = keyProp.GetString();
        }
        parsed = new ParsedGeraldoRequest { ItemId = itemId, Target = target, WhenAffordable = whenAffordable, HasTiming = hasTiming, TargetRound = round, DelaySeconds = delay, DelayFromNow = fromNow, IdempotencyKey = idempotency };
        return true;
    }

    private static bool TryReadGeraldoTarget(JsonElement payload, out GeraldoTargetV1? target)
    {
        target = null;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("kind", out var kindProp) || kindProp.ValueKind != JsonValueKind.String) return false;
        string kind = kindProp.GetString() ?? "";
        if (kind == "point")
        {
            if (payload.EnumerateObject().Count() != 3 || !payload.TryGetProperty("x", out var xProp) || !payload.TryGetProperty("y", out var yProp) || xProp.ValueKind != JsonValueKind.Number || yProp.ValueKind != JsonValueKind.Number || !xProp.TryGetDouble(out var x) || !yProp.TryGetDouble(out var y) || !float.IsFinite((float)x) || !float.IsFinite((float)y)) return false;
            target = new GeraldoTargetV1 { Kind = kind, X = (float)x, Y = (float)y }; return true;
        }
        if (kind == "tower")
        {
            if (payload.EnumerateObject().Count() != 2 || !payload.TryGetProperty("towerId", out var idProp) || idProp.ValueKind != JsonValueKind.String) return false;
            string id = idProp.GetString() ?? "";
            if (id.Length == 0 || id.Length > 128 || id.Trim() != id) return false;
            target = new GeraldoTargetV1 { Kind = kind, TowerId = id }; return true;
        }
        return false;
    }

    private static string GeraldoFingerprint(ParsedGeraldoRequest request) => string.Join("\u001f", request.ItemId,
        request.Target?.Kind ?? "-", request.Target?.X?.ToString("R", CultureInfo.InvariantCulture) ?? "-", request.Target?.Y?.ToString("R", CultureInfo.InvariantCulture) ?? "-", request.Target?.TowerId ?? "-",
        request.WhenAffordable ? "1" : "0", request.HasTiming ? "1" : "0", request.DelayFromNow?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        request.DelayFromNow.HasValue ? "-" : request.TargetRound?.ToString(CultureInfo.InvariantCulture) ?? "-", request.DelayFromNow.HasValue ? "-" : request.DelaySeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-");

    private static void RememberGeraldoIdempotency(string? key, string fingerprint, string? scheduleId, UseGeraldoItemResultV1? result)
    {
        if (string.IsNullOrEmpty(key)) return;
        geraldoIdempotency[key] = new GeraldoIdempotencyEntry { Fingerprint = fingerprint, ScheduleId = scheduleId, CachedResult = result };
    }

    private static void ResetScheduledGeraldoPurchases()
    {
        scheduledGeraldoPurchases.Clear(); geraldoTerminalOutcomes.Clear(); geraldoIdempotency.Clear(); geraldoScheduleCounter = 0;
    }

    private static List<ScheduledGeraldoPurchaseInfoV1> SnapshotScheduledGeraldoPurchases()
    {
        var result = new List<ScheduledGeraldoPurchaseInfoV1>(scheduledGeraldoPurchases.Count + geraldoTerminalOutcomes.Count);
        foreach (var work in scheduledGeraldoPurchases) result.Add(SnapshotGeraldoPurchase(work));
        result.AddRange(geraldoTerminalOutcomes);
        return result;
    }
}
