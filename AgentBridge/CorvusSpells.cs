using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.CorvusSpells;
using Il2CppAssets.Scripts.Simulation.Corvus.Spells;
using Il2CppAssets.Scripts.Simulation.Corvus.TowerManager;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppNinjaKiwi.Localization;


namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int MaxScheduledCorvusActions = 32;
    private const int MaxCorvusTerminalOutcomes = 64;
    private const int MaxCorvusIdempotencyEntries = 512;

    private static readonly CorvusSpellType[] CorvusSpellTypes =
    [
        CorvusSpellType.Aggression,
        CorvusSpellType.Malevolence,
        CorvusSpellType.Spear,
        CorvusSpellType.Storm,
        CorvusSpellType.AncestralMight,
        CorvusSpellType.Echo,
        CorvusSpellType.Ember,
        CorvusSpellType.Frostbound,
        CorvusSpellType.Haste,
        CorvusSpellType.Nourishment,
        CorvusSpellType.Overload,
        CorvusSpellType.Recovery,
        CorvusSpellType.Repel,
        CorvusSpellType.SoulBarrier,
        CorvusSpellType.Trample,
        CorvusSpellType.Vision
    ];

    private sealed class CorvusContext
    {
        public InGame InGame { get; init; } = null!;
        public UnityToSimulation Bridge { get; init; } = null!;
        public CorvusManager Manager { get; init; } = null!;
        public TowerToSimulation HeroTower { get; init; } = null!;
        public int InputId { get; init; }
        public int Round { get; init; }
        public bool RoundsActive { get; init; }
        public int HeroLevel { get; init; }
        public GameModel? GameModel { get; init; }
    }

    private sealed class ParsedCorvusRequest
    {
        public string SpellId { get; init; } = "";
        public CorvusSpellType SpellType { get; init; }
        public CorvusSpellModel Model { get; init; } = null!;
        public bool? Enabled { get; init; }
        public bool WhenReady { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public float? DelayFromNow { get; init; }
        public string? IdempotencyKey { get; init; }
    }

    private sealed class CorvusScheduleWork
    {
        public string? ScheduleId { get; init; }
        public string HeroTowerId { get; init; } = "";
        public string SpellId { get; init; } = "";
        public CorvusSpellType SpellType { get; init; }
        public bool? Enabled { get; init; }
        public bool WhenReady { get; init; }
        public bool HasTiming { get; init; }
        public int? TargetRound { get; init; }
        public float? DelaySeconds { get; init; }
        public string Status { get; set; } = "pending";
        public bool Executed { get; set; }
        public bool Changed { get; set; }
        public int? ManaBefore { get; set; }
        public int? ManaAfter { get; set; }
        public int? ExecutionRound { get; set; }
        public float? ExecutionSeconds { get; set; }
        public string? WaitingFor { get; set; }
        public CorvusFailureV1? Failure { get; set; }
        public CorvusActionResultV1? Result { get; set; }
        public bool TerminalRecorded { get; set; }
    }

    private sealed class CorvusIdempotencyEntry
    {
        public string Fingerprint { get; init; } = "";
        public string? ScheduleId { get; init; }
        public CorvusActionResultV1? CachedResult { get; init; }
    }

    private static readonly List<CorvusScheduleWork> scheduledCorvusActions = new();
    private static readonly Queue<ScheduledCorvusActionInfoV1> corvusTerminalOutcomes = new();
    private static readonly Dictionary<string, CorvusIdempotencyEntry> corvusIdempotency = new(StringComparer.Ordinal);
    private static int corvusScheduleCounter;

    // Capture a native spell start for the exact manager and spell. Mana
    // changes alone are not proof: continuous effects can drain mana.
    private static CorvusManager? executingCorvusManager;
    private static CorvusSpellType? executingCorvusSpell;
    private static bool corvusCastStarted;

    private static BridgeResultV1 HandleInspectCorvus(BridgeRequestV1 request)
    {
        if (!TryGetCorvusContext(out var context, out var error))
            return ErrorResult(request, error!.Value.Code, error.Value.Message, error.Value.Retryable);

        try
        {
            var spells = new List<CorvusSpellInfoV1>(CorvusSpellTypes.Length);
            foreach (var spellType in CorvusSpellTypes)
            {
                if (!TryResolveCorvusModel(context, spellType, out var model) || model == null)
                    return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Native Corvus spell model '{spellType}' is unavailable.", true);
                spells.Add(BuildCorvusSpellInfo(context, spellType, model));
            }

            return SuccessResult(request, new InspectCorvusResultV1
            {
                Source = "active-simulation",
                HeroTowerId = context.HeroTower.Id.ToString(),
                HeroLevel = Math.Max(1, context.HeroLevel),
                Mana = Math.Max(0, context.Manager.AvailableMana),
                MaxMana = Math.Max(0, CorvusManager.maxMana),
                IsRecovering = context.Manager.isRecovering,
                IsStunned = context.Manager.IsStunned(),
                IsManaDraining = context.Manager.IsManaDraining(),
                Spells = spells
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Native Corvus state is unavailable: {ex.Message}", true);
        }
    }

    private static BridgeResultV1 HandleCastCorvusSpell(BridgeRequestV1 request) =>
        HandleCorvusAction(request, isSet: false);

    private static BridgeResultV1 HandleSetCorvusSpell(BridgeRequestV1 request) =>
        HandleCorvusAction(request, isSet: true);

    private static BridgeResultV1 HandleCorvusAction(BridgeRequestV1 request, bool isSet)
    {
        if (!TryGetCorvusContext(out var context, out var contextError))
            return ErrorResult(request, contextError!.Value.Code, contextError.Value.Message, contextError.Value.Retryable);
        if (!TryReadCorvusRequest(request.Payload, context, isSet, out var parsed, out var code, out var message, resolveTiming: false))
            return ErrorResult(request, code, message, false);

        string fingerprint = CorvusFingerprint(parsed, isSet);
        if (parsed.IdempotencyKey is { Length: > 0 } key && corvusIdempotency.TryGetValue(key, out var previous))
        {
            if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
                return ErrorResult(request, "IDEMPOTENCY_KEY_REUSED", $"Idempotency key '{key}' was already used for different Corvus input.", false);
            if (previous.CachedResult != null)
                return SuccessResult(request, previous.CachedResult);
            if (previous.ScheduleId != null)
            {
                var live = scheduledCorvusActions.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (live != null)
                    return SuccessResult(request, BuildCorvusActionResult(live, context));
                var terminal = corvusTerminalOutcomes.FirstOrDefault(item => item.ScheduleId == previous.ScheduleId);
                if (terminal?.Result != null)
                    return SuccessResult(request, terminal.Result);
                return ErrorResult(request, "IDEMPOTENCY_OUTCOME_EVICTED", $"Idempotency outcome for key '{key}' is no longer retained; inspect the match before retrying.", false);
            }
        }

        if (parsed.IdempotencyKey is { Length: > 0 } newKey && !corvusIdempotency.ContainsKey(newKey) &&
            corvusIdempotency.Count >= MaxCorvusIdempotencyEntries)
            return ErrorResult(request, "IDEMPOTENCY_CAPACITY_EXCEEDED", "This match has reached its 512-key Corvus idempotency limit. Existing keys remain protected from replay.", false);

        if (!TryReadCorvusRequest(request.Payload, context, isSet, out parsed, out code, out message))
            return ErrorResult(request, code, message, false);
        UpdateUiState(false);
        if (!uiState.Ready)
            return ErrorResult(request, "UI_BLOCKED", "A UI interruption blocks Corvus actions.", false);
        if (context.InGame.MatchLost || context.InGame.WaitingForVictoryScreen)
            return ErrorResult(request, "MATCH_ENDED", "Corvus actions are unavailable after the match ends.", false);
        if (parsed.Enabled is not false && context.HeroLevel < parsed.Model.unlocksAtLevel)
            return ErrorResult(request, "CORVUS_SPELL_LOCKED", $"Corvus spell '{parsed.SpellId}' unlocks at level {parsed.Model.unlocksAtLevel}.", false);

        bool desiredNoOp = isSet && parsed.Enabled == context.Manager.IsSpellActive(parsed.SpellType);
        bool hasExplicitTiming = parsed.HasTiming;
        bool deferredByPause = parsed.WhenReady && !CanProcessGeraldoAutomatically(context.InGame);
        if (desiredNoOp && !hasExplicitTiming)
        {
            var immediateNoOp = CompleteCorvusNoOp(parsed, context);
            RememberCorvusIdempotency(parsed.IdempotencyKey, fingerprint, null, immediateNoOp);
            return SuccessResult(request, immediateNoOp);
        }

        if (!hasExplicitTiming && !desiredNoOp)
        {
            var readiness = ReadCorvusReadiness(context, parsed.SpellType, parsed.Model, parsed.Enabled);
            if (readiness.Code == null && !deferredByPause)
            {
                var immediate = ExecuteCorvusAction(new CorvusScheduleWork
                {
                    SpellId = parsed.SpellId,
                    SpellType = parsed.SpellType,
                    HeroTowerId = context.HeroTower.Id.ToString(),
                    Enabled = parsed.Enabled,
                    WhenReady = parsed.WhenReady
                }, context, parsed.Model);
                RememberCorvusIdempotency(parsed.IdempotencyKey, fingerprint, null, immediate);
                return SuccessResult(request, immediate);
            }
            if (readiness.Code != null && (!parsed.WhenReady || !readiness.Transient))
            {
                var failedWork = new CorvusScheduleWork
                {
                    SpellId = parsed.SpellId,
                    SpellType = parsed.SpellType,
                    HeroTowerId = context.HeroTower.Id.ToString(),
                    Enabled = parsed.Enabled,
                    WhenReady = parsed.WhenReady,
                    Status = "failed",
                    Failure = new CorvusFailureV1 { Code = readiness.Code, Message = readiness.Message }
                };
                var failed = BuildCorvusActionResult(failedWork, context);
                RememberCorvusIdempotency(parsed.IdempotencyKey, fingerprint, null, failed);
                return SuccessResult(request, failed);
            }
        }

        if (scheduledCorvusActions.Count >= MaxScheduledCorvusActions)
            return ErrorResult(request, "SCHEDULE_QUEUE_FULL", "This match has reached its 32 pending Corvus action limit.", false);

        var work = new CorvusScheduleWork
        {
            ScheduleId = $"corvus-{++corvusScheduleCounter:D4}-{Guid.NewGuid():N}",
            SpellId = parsed.SpellId,
            SpellType = parsed.SpellType,
            HeroTowerId = context.HeroTower.Id.ToString(),
            Enabled = parsed.Enabled,
            WhenReady = parsed.WhenReady,
            HasTiming = parsed.HasTiming,
            TargetRound = parsed.TargetRound,
            DelaySeconds = parsed.DelaySeconds,
            Status = parsed.WhenReady && !parsed.HasTiming ? "waiting" : "pending",
            WaitingFor = deferredByPause ? "paused" : parsed.WhenReady && !parsed.HasTiming ? "native_readiness" : null
        };
        scheduledCorvusActions.Add(work);
        RememberCorvusIdempotency(parsed.IdempotencyKey, fingerprint, work.ScheduleId, null);
        return SuccessResult(request, BuildCorvusActionResult(work, context));
    }

    private static BridgeResultV1 HandleCancelScheduledCorvusAction(BridgeRequestV1 request)
    {
        if (!TryReadScheduleCancellation(request.Payload, out var scheduleId, out var cancellationError))
            return ErrorResult(request, "INVALID_ARGUMENT", cancellationError!, false);

        bool all = scheduleId == null || string.Equals(scheduleId, "all", StringComparison.OrdinalIgnoreCase);
        if (all)
        {
            int count = scheduledCorvusActions.Count;
            foreach (var work in scheduledCorvusActions.ToArray())
            {
                work.Status = "cancelled";
                work.WaitingFor = null;
                work.Result = BuildCorvusActionResult(work, TryGetCorvusContext(out var context, out _) ? context : null);
                EnqueueCorvusTerminal(work);
            }
            scheduledCorvusActions.Clear();
            return SuccessResult(request, new CancelScheduledCorvusActionResultV1 { Cancelled = count > 0, Count = count });
        }

        var item = scheduledCorvusActions.FirstOrDefault(candidate => candidate.ScheduleId == scheduleId);
        if (item == null)
            return ErrorResult(request, "SCHEDULE_NOT_FOUND", $"Scheduled Corvus action with ID '{scheduleId}' was not found.", false);
        item.Status = "cancelled";
        item.WaitingFor = null;
        item.Result = BuildCorvusActionResult(item, TryGetCorvusContext(out var itemContext, out _) ? itemContext : null);
        scheduledCorvusActions.Remove(item);
        EnqueueCorvusTerminal(item);
        return SuccessResult(request, new CancelScheduledCorvusActionResultV1 { Cancelled = true, Count = 1 });
    }

    private static void ProcessScheduledCorvusActions()
    {
        if (scheduledCorvusActions.Count == 0) return;
        var inGame = InGame.instance;
        if (inGame?.bridge == null || !inGame.IsInGame()) return;
        if (!CanProcessGeraldoAutomatically(inGame)) return;
        if (!TryGetCorvusContext(out var context, out var error))
        {
            while (scheduledCorvusActions.Count > 0)
                RetireCorvusWork(scheduledCorvusActions[0], "failed",
                    new CorvusFailureV1 { Code = error!.Value.Code, Message = error.Value.Message }, null);
            return;
        }
        int round = context.Round;
        string heroId = context.HeroTower.Id.ToString();
        float? elapsed = context.RoundsActive && hasRoundElapsedTime ? currentRoundElapsedSeconds : null;
        for (int i = 0; i < scheduledCorvusActions.Count; i++)
        {
            var work = scheduledCorvusActions[i];
            if (heroId != work.HeroTowerId)
            {
                RetireCorvusWork(work, "failed", new CorvusFailureV1 {
                    Code = "CORVUS_NOT_FOUND", Message = "The Corvus hero pinned by this action was replaced."
                }, null);
                i--;
                continue;
            }
            if (work.HasTiming)
            {
                if (round > work.TargetRound!.Value)
                {
                    RetireCorvusWork(work, "expired", new CorvusFailureV1 {
                        Code = "TARGET_ROUND_PASSED", Message = $"Target round {work.TargetRound} passed before execution."
                    }, context);
                    i--;
                    continue;
                }
                if (round < work.TargetRound.Value || !elapsed.HasValue || elapsed.Value < work.DelaySeconds!.Value)
                {
                    work.Status = "pending";
                    work.WaitingFor = round < work.TargetRound.Value ? "target_round" :
                        !elapsed.HasValue ? "round_start" : "round_clock";
                    continue;
                }
            }
            // Hero upgrades can replace spell models while an action waits.
            if (!TryResolveCorvusModel(context, work.SpellType, out var model) || model == null)
            {
                RetireCorvusWork(work, "failed", new CorvusFailureV1 {
                    Code = "UNKNOWN_CORVUS_SPELL", Message = $"Native spell '{work.SpellId}' is unavailable."
                }, context);
                i--;
                continue;
            }
            if (work.Enabled.HasValue && work.Enabled.Value == context.Manager.IsSpellActive(work.SpellType))
            {
                CompleteCorvusNoOp(work, context);
                scheduledCorvusActions.RemoveAt(i--);
                EnqueueCorvusTerminal(work);
                continue;
            }
            var readiness = ReadCorvusReadiness(context, work.SpellType, model, work.Enabled);
            if (readiness.Code != null)
            {
                if (work.WhenReady && readiness.Transient)
                {
                    work.Status = "waiting";
                    work.WaitingFor = readiness.WaitingFor ?? "native_readiness";
                    continue;
                }
                RetireCorvusWork(work, "failed", new CorvusFailureV1 {
                    Code = readiness.Code, Message = readiness.Message
                }, context);
                i--;
                continue;
            }
            ExecuteCorvusAction(work, context, model);
            scheduledCorvusActions.RemoveAt(i);
            EnqueueCorvusTerminal(work);
            return;
        }
    }

    private static void RetireCorvusWork(CorvusScheduleWork work, string status, CorvusFailureV1 failure, CorvusContext? context)
    {
        work.Status = status;
        work.Failure = failure;
        work.WaitingFor = null;
        work.Result = BuildCorvusActionResult(work, context);
        RemoveCorvusWork(work);
        EnqueueCorvusTerminal(work);
    }

    private static void RemoveCorvusWork(CorvusScheduleWork work)
    {
        scheduledCorvusActions.Remove(work);
    }

    private static void EnqueueCorvusTerminal(CorvusScheduleWork work)
    {
        if (work.TerminalRecorded || work.ScheduleId == null)
            return;
        work.TerminalRecorded = true;
        while (corvusTerminalOutcomes.Count >= MaxCorvusTerminalOutcomes)
            corvusTerminalOutcomes.Dequeue();
        corvusTerminalOutcomes.Enqueue(SnapshotCorvusAction(work));
        int round = work.TargetRound ?? (InGame.instance?.bridge?.GetCurrentRound() + 1 ?? 1);
        AppendRoundActionOutcome(work.ScheduleId, "corvus", round, work.Status, work.Failure?.Code, work.ExecutionSeconds);
    }

    private static CorvusActionResultV1 ExecuteCorvusAction(CorvusScheduleWork work, CorvusContext context, CorvusSpellModel model)
    {
        work.WaitingFor = null;
        work.Failure = null;
        try
        {
            work.ManaBefore = context.Manager.AvailableMana;
            var before = context.Bridge.GetCorvusSpellStatus(work.SpellType, context.InputId);
            bool previousImmediate = context.Bridge.IsImmediateMode;
            bool started;
            executingCorvusManager = context.Manager;
            executingCorvusSpell = work.SpellType;
            corvusCastStarted = false;
            try
            {
                context.Bridge.IsImmediateMode = true;
                if (work.Enabled is false)
                    context.Manager.CancelContinuousSpell(work.SpellType);
                else
                    context.Bridge.ActivateCorvusSpell(context.InputId, model);
                started = corvusCastStarted;
            }
            finally
            {
                context.Bridge.IsImmediateMode = previousImmediate;
                executingCorvusManager = null;
                executingCorvusSpell = null;
            }
            work.ManaAfter = context.Manager.AvailableMana;
            var after = context.Bridge.GetCorvusSpellStatus(work.SpellType, context.InputId);
            bool confirmed = work.Enabled.HasValue
                ? after.isActive == work.Enabled.Value && before.isActive != after.isActive
                : started || (!before.isActive && after.isActive) || before.cooldownPercent != after.cooldownPercent;
            work.ExecutionRound = context.Round;
            work.ExecutionSeconds = context.RoundsActive && hasRoundElapsedTime ? currentRoundElapsedSeconds : null;
            work.Executed = confirmed;
            work.Changed = confirmed;
            work.Status = confirmed ? "completed" : "verification_pending";
            if (!confirmed)
                work.Failure = new CorvusFailureV1 {
                    Code = "NATIVE_OUTCOME_UNCONFIRMED",
                    Message = "Native submission lacks a matching cast-start event or spell state/cooldown transition; it will not be replayed."
                };
        }
        catch (Exception ex)
        {
            work.Status = "verification_pending";
            work.Failure = new CorvusFailureV1 {
                Code = "NATIVE_OUTCOME_UNCONFIRMED", Message = $"Native Corvus outcome is unknown: {ex.Message}"
            };
            try { work.ManaAfter = context.Manager.AvailableMana; } catch { }
        }
        work.Result = BuildCorvusActionResult(work, context);
        return work.Result;
    }

    private static CorvusActionResultV1 CompleteCorvusNoOp(ParsedCorvusRequest parsed, CorvusContext context) =>
        CompleteCorvusNoOp(new CorvusScheduleWork {
            HeroTowerId = context.HeroTower.Id.ToString(), SpellId = parsed.SpellId,
            SpellType = parsed.SpellType, Enabled = parsed.Enabled
        }, context);

    private static CorvusActionResultV1 CompleteCorvusNoOp(CorvusScheduleWork work, CorvusContext context)
    {
        work.Status = "completed";
        work.WaitingFor = null;
        work.ManaBefore = work.ManaAfter = context.Manager.AvailableMana;
        work.ExecutionRound = context.Round;
        work.ExecutionSeconds = context.RoundsActive && hasRoundElapsedTime ? currentRoundElapsedSeconds : null;
        work.Result = BuildCorvusActionResult(work, context);
        return work.Result;
    }

    private readonly record struct CorvusReadiness(string? Code, string Message, bool Transient, string? WaitingFor);


    private static CorvusReadiness ReadCorvusReadiness(CorvusContext context, CorvusSpellType spellType, CorvusSpellModel model,
        bool? enabled, CorvusSpellStatus? knownStatus = null)
    {
        if (enabled is false)
            return new(null, "", false, null);
        if (context.HeroLevel < model.unlocksAtLevel)
            return new("CORVUS_SPELL_LOCKED", $"Corvus spell '{spellType}' is locked until level {model.unlocksAtLevel}.", false, null);

        CorvusSpellStatus status;
        try { status = knownStatus ?? context.Bridge.GetCorvusSpellStatus(spellType, context.InputId); }
        catch (Exception ex) { return new("NATIVE_STATE_UNAVAILABLE", $"Native Corvus spell readiness is unavailable: {ex.Message}", false, null); }
        // Native canSpellBeCast remains true for some already-active cast spells;
        // ActivateCorvusSpell then silently ignores the request.
        if (!enabled.HasValue && status.isActive)
            return new("CORVUS_SPELL_ACTIVE", "This Corvus cast spell is already active.", true, "active");
        if (status.cooldownPercent > 0f)
            return new("CORVUS_SPELL_COOLDOWN", "The native Corvus spell cooldown is active.", true, "cooldown");
        if (status.canSpellBeCast)
            return new(null, "", false, null);

        try
        {
            if (context.Manager.IsStunned()) return new("CORVUS_STUNNED", "Corvus is stunned under the native spell readiness state.", true, "stunned");
            if (context.Manager.isRecovering) return new("CORVUS_RECOVERING", "Corvus is recovering under the native spell readiness state.", true, "recovering");
            if (context.Manager.AvailableMana < Math.Max(0, model.initialManaCost)) return new("INSUFFICIENT_MANA", "Corvus does not have enough native mana for this spell.", true, "mana");
        }
        catch { }
        return new("SPELL_NOT_READY", "The native Corvus spell is not currently ready.", true, "native_readiness");
    }

    private static bool TryReadCorvusRequest(JsonElement payload, CorvusContext context, bool isSet,
        out ParsedCorvusRequest parsed, out string code, out string message, bool resolveTiming = true)
    {
        parsed = new ParsedCorvusRequest();
        code = "INVALID_ARGUMENT";
        message = "Corvus payload must be an object.";
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in payload.EnumerateObject())
        {
            bool known = property.Name is "spellId" or "enabled" or "round" or "delaySeconds" or "delayFromNow" or "whenReady" or "idempotencyKey";
            if (!known || (!isSet && property.Name == "enabled"))
            {
                message = $"Unknown Corvus field '{property.Name}'.";
                return false;
            }
        }
        if (!payload.TryGetProperty("spellId", out var spellProp) || spellProp.ValueKind != JsonValueKind.String)
        {
            message = "spellId must be a native Corvus spell enum name.";
            return false;
        }
        string spellId = spellProp.GetString() ?? "";
        if (spellId.Length is 0 or > 128 || !Enum.TryParse<CorvusSpellType>(spellId, ignoreCase: false, out var spellType) ||
            spellType == CorvusSpellType.None || spellType.ToString() != spellId || Array.IndexOf(CorvusSpellTypes, spellType) < 0)
        {
            code = "UNKNOWN_CORVUS_SPELL";
            message = $"Unknown native Corvus spell '{spellId}'.";
            return false;
        }
        if (!TryResolveCorvusModel(context, spellType, out var model) || model == null)
        {
            code = "UNKNOWN_CORVUS_SPELL";
            message = $"Native Corvus spell '{spellId}' is unavailable in the active game model.";
            return false;
        }
        bool continuous = IsContinuousSpell(spellType);
        if (isSet != continuous)
        {
            code = "CORVUS_SPELL_KIND_MISMATCH";
            message = isSet ? $"Corvus spell '{spellId}' is a cast spell; set_corvus_spell accepts continuous spells only." : $"Corvus spell '{spellId}' is continuous; cast_corvus_spell accepts cast spells only.";
            return false;
        }

        bool? enabled = null;
        if (isSet)
        {
            if (!payload.TryGetProperty("enabled", out var enabledProp) || enabledProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                message = "enabled must be a required boolean for set_corvus_spell.";
                return false;
            }
            enabled = enabledProp.GetBoolean();
        }

        bool whenReady = false;
        if (payload.TryGetProperty("whenReady", out var readyProp))
        {
            if (readyProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                message = "whenReady must be a boolean.";
                return false;
            }
            whenReady = readyProp.GetBoolean();
        }

        bool hasRound = payload.TryGetProperty("round", out var roundProp);
        int? round = null;
        if (hasRound && (roundProp.ValueKind != JsonValueKind.Number || !roundProp.TryGetInt32(out int roundValue) || roundValue <= 0))
        {
            message = "round must be a positive integer.";
            return false;
        }
        if (hasRound) round = roundProp.GetInt32();

        bool hasDelay = payload.TryGetProperty("delaySeconds", out var delayProp);
        float? delay = null;
        if (hasDelay && (delayProp.ValueKind != JsonValueKind.Number || !delayProp.TryGetSingle(out float delayValue) || !float.IsFinite(delayValue) || delayValue < 0f))
        {
            message = "delaySeconds must be finite and nonnegative.";
            return false;
        }
        if (hasDelay) delay = delayProp.GetSingle();

        bool hasFromNow = payload.TryGetProperty("delayFromNow", out var fromNowProp);
        float? fromNow = null;
        if (hasFromNow)
        {
            if (hasRound || hasDelay || fromNowProp.ValueKind != JsonValueKind.Number || !fromNowProp.TryGetSingle(out float fromNowValue) || !float.IsFinite(fromNowValue) || fromNowValue < 0f)
            {
                message = "delayFromNow cannot be combined with round or delaySeconds and must be finite and nonnegative.";
                return false;
            }
            fromNow = fromNowValue;
            if (resolveTiming)
            {
                if (!context.RoundsActive || !hasRoundElapsedTime)
                {
                    code = "ROUND_TIME_UNAVAILABLE";
                    message = "delayFromNow requires an active round with a native round clock.";
                    return false;
                }
                round = context.Round;
                delay = currentRoundElapsedSeconds + fromNowValue;
                if (!float.IsFinite(delay.Value))
                {
                    message = "Resolved delay exceeds the native clock range.";
                    return false;
                }
            }
        }

        bool hasTiming = hasRound || hasDelay || hasFromNow;
        if (resolveTiming && hasTiming && !hasFromNow)
        {
            round ??= context.Round;
            delay ??= 0f;
            if (round.Value < context.Round)
            {
                code = "TARGET_ROUND_PASSED";
                message = $"Target round {round.Value} has already passed (current round is {context.Round}).";
                return false;
            }
            if (round.Value == context.Round && context.RoundsActive && hasRoundElapsedTime && delay.Value < currentRoundElapsedSeconds)
            {
                code = "DELAY_PASSED";
                message = $"Delay {delay.Value:F2}s has already passed in round {context.Round}.";
                return false;
            }
        }

        string? idempotencyKey = null;
        if (payload.TryGetProperty("idempotencyKey", out var keyProp))
        {
            if (keyProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyProp.GetString()) || keyProp.GetString()!.Length > 128)
            {
                message = "idempotencyKey must contain 1-128 non-whitespace characters.";
                return false;
            }
            idempotencyKey = keyProp.GetString();
        }

        parsed = new ParsedCorvusRequest
        {
            SpellId = spellId,
            SpellType = spellType,
            Model = model,
            Enabled = enabled,
            WhenReady = whenReady,
            HasTiming = hasTiming,
            TargetRound = round,
            DelaySeconds = delay,
            DelayFromNow = fromNow,
            IdempotencyKey = idempotencyKey
        };
        return true;
    }

    private static string CorvusFingerprint(ParsedCorvusRequest parsed, bool isSet) => string.Join("|",
        isSet ? "set" : "cast", parsed.SpellId, parsed.Enabled?.ToString() ?? "cast",
        parsed.WhenReady.ToString(), parsed.TargetRound?.ToString(CultureInfo.InvariantCulture) ?? "-",
        parsed.DelaySeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        parsed.DelayFromNow?.ToString("R", CultureInfo.InvariantCulture) ?? "-");

    private static void RememberCorvusIdempotency(string? key, string fingerprint, string? scheduleId, CorvusActionResultV1? result)
    {
        if (string.IsNullOrEmpty(key)) return;
        corvusIdempotency[key] = new CorvusIdempotencyEntry { Fingerprint = fingerprint, ScheduleId = scheduleId, CachedResult = result };
    }

    private static bool TryGetCorvusContext(out CorvusContext context, out (string Code, string Message, bool Retryable)? error)
    {
        context = null!;
        error = null;
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
        {
            error = ("NO_ACTIVE_GAME", "Corvus spell actions require an active match.", false);
            return false;
        }
        var bridge = inGame.bridge;
        if (bridge == null || bridge.Simulation == null)
        {
            error = ("SIMULATION_UNAVAILABLE", "The active simulation bridge is unavailable.", true);
            return false;
        }
        try
        {
            int inputId = bridge.GetInputId();
            var simulation = bridge.Simulation;
            var manager = simulation.GetCorvusManager(inputId);
            var nativeHero = manager?.GetCorvus();
            TowerToSimulation? hero = null;
            var towers = inGame.GetAllTowerToSim();
            if (towers != null)
                foreach (var candidate in towers)
                {
                    if (candidate?.hero == null || candidate.owner != inputId ||
                        !string.Equals(candidate.Def?.baseId, "Corvus", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var candidateSim = candidate.GetSimTower();
                    if (nativeHero != null && candidateSim != null && candidateSim.Pointer == nativeHero.Pointer)
                    {
                        hero = candidate;
                        break;
                    }
                }
            if (manager == null || nativeHero == null || hero == null)
            {
                error = ("CORVUS_NOT_FOUND", "The native Corvus hero or manager is unavailable for the active player.", false);
                return false;
            }
            context = new CorvusContext
            {
                InGame = inGame,
                Bridge = bridge,
                Manager = manager,
                HeroTower = hero,
                InputId = inputId,
                Round = bridge.GetCurrentRound() + 1,
                RoundsActive = bridge.AreRoundsActive(),
                HeroLevel = hero.hero.level,
                GameModel = inGame.GetGameModel()
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ("NATIVE_STATE_UNAVAILABLE", $"Native Corvus state is unavailable: {ex.Message}", true);
            return false;
        }
    }

    private static bool TryResolveCorvusModel(CorvusContext context, CorvusSpellType spellType, out CorvusSpellModel? model)
    {
        model = null;
        try
        {
            model = context.Manager.LookupSpellByType(spellType)?.corvusSpellModel;
            if (model != null) return true;
        }
        catch { }

        try
        {
            var seen = new HashSet<CorvusSpellType>();
            foreach (var candidate in EnumerateCorvusModels(context))
            {
                if (candidate == null || !seen.Add(candidate.spellType)) continue;
                if (candidate.spellType == spellType && context.HeroLevel < candidate.unlocksAtLevel)
                {
                    model = candidate;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static IEnumerable<CorvusSpellModel> EnumerateCorvusModels(CorvusContext context)
    {
        if (context.HeroTower.Def?.behaviors != null)
            foreach (var behavior in context.HeroTower.Def.behaviors)
                if (behavior.TryCast<CorvusSpellModel>() is { } spell) yield return spell;

        var gameModel = context.GameModel;
        if (gameModel?.towers == null) yield break;
        foreach (var tower in gameModel.towers)
        {
            if (tower?.baseId != "Corvus" || tower.behaviors == null) continue;
            foreach (var behavior in tower.behaviors)
                if (behavior.TryCast<CorvusSpellModel>() is { } spell) yield return spell;
        }
    }

    private static CorvusSpellInfoV1 BuildCorvusSpellInfo(CorvusContext context, CorvusSpellType spellType, CorvusSpellModel model)
    {
        var status = context.Bridge.GetCorvusSpellStatus(spellType, context.InputId);
        bool continuous = IsContinuousSpell(spellType);
        var continuousModel = model.TryCast<CorvusContinuousSpellModel>();
        var instantModel = model.TryCast<CorvusInstantSpellModel>();
        var readiness = ReadCorvusReadiness(context, spellType, model, continuous ? true : null, status);
        return new CorvusSpellInfoV1
        {
            SpellId = spellType.ToString(),
            Name = ReadCorvusTitle(model),
            Kind = continuous ? "continuous" : "cast",
            UnlockLevel = Math.Max(1, model.unlocksAtLevel),
            Unlocked = context.HeroLevel >= model.unlocksAtLevel,
            InitialManaCost = Math.Max(0, model.initialManaCost),
            OngoingManaCost = continuousModel == null ? null : Math.Max(0, continuousModel.ongoingManaCost),
            ManaDrainIntervalSeconds = continuousModel == null ? null : FiniteNonnegative(continuousModel.manaDrainInterval),
            DurationSeconds = instantModel == null ? null : FiniteNonnegative(instantModel.duration),
            CooldownSeconds = instantModel == null ? null : FiniteNonnegative(instantModel.cooldown),
            Active = status.isActive,
            CanCast = readiness.Code == null,
            CooldownPercent = ClampFraction(status.cooldownPercent),
            ActiveDurationRemainingPercent = ClampFraction(status.activeDurationRemainingPercent),
            UnavailableReason = readiness.WaitingFor ?? readiness.Code
        };
    }

    private static string ReadCorvusTitle(CorvusSpellModel model)
    {
        try
        {
            var localization = LocalizationManager.Instance;
            if (localization != null)
                foreach (string key in new[] { model.locsId + ".name", model.locsId + ".title", model.locsId, model.name })
                    if (!string.IsNullOrWhiteSpace(key) && localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value))
                        return value;
        }
        catch { }
        return model.name ?? model.spellType.ToString();
    }

    private static float? FiniteNonnegative(float value) => float.IsFinite(value) && value >= 0f ? value : null;

    private static float ClampFraction(float value) => float.IsFinite(value)
        ? Math.Clamp(value, 0f, 1f)
        : throw new InvalidOperationException("Native Corvus timing fraction is not finite.");

    private static bool IsContinuousSpell(CorvusSpellType spellType) => spellType is
        CorvusSpellType.Aggression or CorvusSpellType.Malevolence or CorvusSpellType.Spear or CorvusSpellType.Storm;

    private static CorvusActionResultV1 BuildCorvusActionResult(CorvusScheduleWork work, CorvusContext? context)
    {
        CorvusSpellInfoV1? spell = null;
        try
        {
            if (context != null && TryResolveCorvusModel(context, work.SpellType, out var model) && model != null)
                spell = BuildCorvusSpellInfo(context, work.SpellType, model);
        }
        catch { }
        return new CorvusActionResultV1
        {
            Status = work.Status,
            Executed = work.Executed,
            Changed = work.Changed,
            ScheduleId = work.ScheduleId,
            HeroTowerId = work.HeroTowerId,
            SpellId = work.SpellId,
            Enabled = work.Enabled,
            ManaBefore = work.ManaBefore,
            ManaAfter = work.ManaAfter,
            Spell = spell,
            Failure = work.Failure,
            WaitingFor = work.WaitingFor,
            ExecutionRound = work.ExecutionRound,
            ExecutionSeconds = work.ExecutionSeconds
        };
    }

    private static ScheduledCorvusActionInfoV1 SnapshotCorvusAction(CorvusScheduleWork work) => new()
    {
        ScheduleId = work.ScheduleId ?? "",
        HeroTowerId = work.HeroTowerId,
        SpellId = work.SpellId,
        Enabled = work.Enabled,
        WhenReady = work.WhenReady,
        TargetRound = work.TargetRound,
        DelaySeconds = work.DelaySeconds,
        Status = work.Status,
        WaitingFor = work.WaitingFor,
        Failure = work.Failure,
        Result = work.Result
    };


    private static List<ScheduledCorvusActionInfoV1> SnapshotScheduledCorvusActions()
    {
        var result = new List<ScheduledCorvusActionInfoV1>(scheduledCorvusActions.Count + corvusTerminalOutcomes.Count);
        foreach (var work in scheduledCorvusActions) result.Add(SnapshotCorvusAction(work));
        result.AddRange(corvusTerminalOutcomes);
        return result;
    }

    private static void ResetScheduledCorvusActions()
    {
        scheduledCorvusActions.Clear();
        corvusTerminalOutcomes.Clear();
        corvusIdempotency.Clear();
        corvusScheduleCounter = 0;
        executingCorvusManager = null;
        executingCorvusSpell = null;
        corvusCastStarted = false;
    }


    [HarmonyPatch(typeof(CorvusSpell), nameof(CorvusSpell.OnSpellCastStarted))]
    internal static class CorvusSpellCastSimPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CorvusSpell __instance)
        {
            try
            {
                if (executingCorvusManager != null && executingCorvusSpell == __instance.SpellType &&
                    __instance.Corvus?.Pointer == executingCorvusManager.Pointer)
                    corvusCastStarted = true;
            }
            catch (Exception ex) { BTD_Mod_Helper.ModHelper.Warning<AgentBridgeMod>($"Native Corvus cast event capture failed: {ex.Message}"); }
        }
    }
}
