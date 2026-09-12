using System;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static BridgeResultV1 HandleActivateAbility(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot activate ability: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        if (!TryReadAbilitySelector(request.Payload, out var abilityId, out int abilityIndex) ||
            !AbilityTargetV1.TryRead(request.Payload, out var target))
            return ErrorResult(request, "INVALID_ARGUMENT", "Provide one ability selector (abilityId or abilityIndex) and a valid typed target.", false);

        var (ok, finalId, name, error) = TryActivateAbilityInternal(inGame, bridge, abilityId, abilityIndex, target);
        if (!ok)
        {
            bool retryable = error == "ABILITY_NOT_READY";
            return ErrorResult(request, error ?? "ACTIVATE_FAILED", $"Failed to activate ability: {error}", retryable);
        }

        return SuccessResult(request, new
        {
            Activated = true,
            AbilityId = finalId,
            Name = name,
            Target = target
        });
    }

    private static BridgeResultV1 HandleScheduleAbility(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot schedule ability: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        if (!TryReadAbilitySelector(request.Payload, out var abilityId, out int abilityIndex) ||
            !AbilityTargetV1.TryRead(request.Payload, out var target))
            return ErrorResult(request, "INVALID_ARGUMENT", "Provide one ability selector (abilityId or abilityIndex) and a valid typed target.", false);
        var ability = ResolveAbility(inGame, abilityId, abilityIndex, out string? abilityError, out int resolvedIndex);
        if (ability == null)
            return ErrorResult(request, abilityError ?? "ABILITY_NOT_FOUND", "Could not resolve the scheduled ability.", false);
        abilityError = ValidateAbilityTarget(bridge, ability, target, out _, prepareNativeTarget: false);
        if (abilityError != null)
            return ErrorResult(request, abilityError, "The scheduled ability target is not valid; no ability was scheduled.", false);

        if (bridge.AreRoundsActive() && !hasRoundElapsedTime)
            return ErrorResult(request, "ROUND_TIME_UNAVAILABLE", "The active round's native clock is unavailable; no ability was scheduled.", false);

        int currentRound = bridge.GetCurrentRound() + 1;
        int targetRound = ReadInt(request.Payload, "round", currentRound);
        if (targetRound <= 0) targetRound = currentRound;

        float? delayFromNow = null;
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("delayFromNow", out var dfnProp))
        {
            if (dfnProp.ValueKind == JsonValueKind.Number && dfnProp.TryGetDouble(out var dfn))
                delayFromNow = (float)dfn;
        }

        float delaySeconds;
        if (delayFromNow.HasValue)
        {
            targetRound = currentRound;
            delaySeconds = currentRoundElapsedSeconds + Math.Max(0f, delayFromNow.Value);
        }
        else
        {
            delaySeconds = ReadFloat(request.Payload, "delaySeconds", 0f);
        }

        if (!float.IsFinite(delaySeconds) || delaySeconds < 0 ||
            (delayFromNow.HasValue && (!float.IsFinite(delayFromNow.Value) || delayFromNow.Value < 0)))
            return ErrorResult(request, "INVALID_ARGUMENT", "Ability delays must be finite and nonnegative.", false);

        if (targetRound < currentRound)
        {
            return ErrorResult(request, "TARGET_ROUND_PASSED", $"Target round {targetRound} has already passed (current round is {currentRound}).", false);
        }

        if (targetRound == currentRound && delaySeconds < currentRoundElapsedSeconds && !delayFromNow.HasValue)
        {
            return ErrorResult(request, "DELAY_PASSED", $"Delay {delaySeconds:F2}s has already passed in round {currentRound} (elapsed: {currentRoundElapsedSeconds:F2}s). Use delayFromNow or a future delay.", false);
        }

        bool autoRetry = true;
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("autoRetry", out var arProp))
        {
            if (arProp.ValueKind == JsonValueKind.True || arProp.ValueKind == JsonValueKind.False)
                autoRetry = arProp.GetBoolean();
        }

        string scheduleId = $"sched_{++scheduleCounter}_{Guid.NewGuid().ToString("N")[..6]}";

        var scheduledItem = new ScheduledAbilityInfoV1
        {
            ScheduleId = scheduleId,
            AbilityId = AbilityId(ability),
            AbilityIndex = resolvedIndex,
            Target = target,
            TargetRound = targetRound,
            DelaySeconds = delaySeconds,
            AutoRetry = autoRetry,
            ScheduledAtUtc = DateTime.UtcNow
        };

        scheduledAbilities.Add(scheduledItem);

        return SuccessResult(request, new
        {
            Scheduled = true,
            ScheduleId = scheduleId,
            TargetRound = targetRound,
            DelaySeconds = delaySeconds,
            CurrentRound = currentRound,
            CurrentRoundElapsedSeconds = currentRoundElapsedSeconds,
            AbilityId = scheduledItem.AbilityId,
            AbilityIndex = scheduledItem.AbilityIndex,
            AutoRetry = autoRetry,
            Target = target
        });
    }

    private static bool TryReadScheduleCancellation(JsonElement payload, out string? scheduleId, out string? error)
    {
        scheduleId = null;
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Cancellation payload must be an object.";
            return false;
        }

        bool hasSelector = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name != "scheduleId")
            {
                error = $"Unknown cancellation field '{property.Name}'.";
                return false;
            }
            if (hasSelector)
            {
                error = "scheduleId may be specified only once.";
                return false;
            }
            hasSelector = true;
        }

        if (!payload.TryGetProperty("scheduleId", out var scheduleProp))
            return true;
        if (scheduleProp.ValueKind != JsonValueKind.String)
        {
            error = "scheduleId must be a nonblank string when provided.";
            return false;
        }

        var value = scheduleProp.GetString();
        if (string.IsNullOrWhiteSpace(value) || value!.Length > 128)
        {
            error = "scheduleId must contain 1-128 non-whitespace characters.";
            return false;
        }

        scheduleId = value.Trim();
        return true;
    }

    private static BridgeResultV1 HandleCancelScheduledAbility(BridgeRequestV1 request)
    {
        if (!TryReadScheduleCancellation(request.Payload, out var scheduleId, out var cancellationError))
            return ErrorResult(request, "INVALID_ARGUMENT", cancellationError!, false);

        if (scheduleId == null || string.Equals(scheduleId, "all", StringComparison.OrdinalIgnoreCase))
        {
            int count = scheduledAbilities.Count;
            scheduledAbilities.Clear();
            return SuccessResult(request, new
            {
                Cancelled = true,
                Count = count
            });
        }

        int removed = scheduledAbilities.RemoveAll(x => x.ScheduleId == scheduleId);
        if (removed == 0)
        {
            return ErrorResult(request, "SCHEDULE_NOT_FOUND", $"Scheduled ability with ID '{scheduleId}' not found.", false);
        }

        return SuccessResult(request, new
        {
            Cancelled = true,
            ScheduleId = scheduleId
        });
    }

    private static void ProcessScheduledAbilities(InGame inGame, UnityToSimulation bridge, int round, float elapsedSeconds)
    {
        if (scheduledAbilities.Count == 0)
            return;
        var now = DateTime.UtcNow;

        for (int i = 0; i < scheduledAbilities.Count; i++)
        {
            var item = scheduledAbilities[i];
            if (item.Triggered)
            {
                if (item.TriggeredAtUtc is { } completed && now - completed >= ScheduledAbilityRetention)
                    scheduledAbilities.RemoveAt(i--);
                continue;
            }

            if (round > item.TargetRound)
            {
                item.Triggered = true;
                item.TriggeredAtUtc = now;
                item.Error = $"Target round {item.TargetRound} passed before ability triggered (current round: {round}).";
                AppendRoundActionOutcome(item.ScheduleId, "ability", item.TargetRound, "expired", item.Error);
                continue;
            }

            if (round != item.TargetRound)
                continue;

            if (elapsedSeconds < item.DelaySeconds)
                continue;

            var (ok, finalId, name, error) = TryActivateAbilityInternal(inGame, bridge, item.AbilityId, -1, item.Target);
            if (ok)
            {
                item.Triggered = true;
                item.TriggeredAtUtc = now;
                AppendRoundActionOutcome(item.ScheduleId, "ability", item.TargetRound, "completed");
                ModHelper.Msg<AgentBridgeMod>($"[AgentBridge] Scheduled ability {item.ScheduleId} ({name}) triggered at round {round} ({elapsedSeconds:F2}s).");
            }
            else
            {
                if (item.AutoRetry && error == "ABILITY_NOT_READY")
                {
                    // Retry next frame
                }
                else
                {
                    item.Triggered = true;
                    item.TriggeredAtUtc = now;
                    item.Error = error ?? "Failed to activate ability.";
                    AppendRoundActionOutcome(item.ScheduleId, "ability", item.TargetRound, "failed", item.Error);
                    ModHelper.Warning<AgentBridgeMod>($"[AgentBridge] Scheduled ability {item.ScheduleId} failed: {item.Error}");
                }
            }
        }

    }
}
