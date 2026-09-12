using System;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void RefreshTelemetryForSandboxReplay(int round)
    {
        if (!string.Equals(InGameData.CurrentGame?.selectedMode, Il2CppAssets.Scripts.Models.Difficulty.ModeType.Sandbox,
                StringComparison.OrdinalIgnoreCase))
            return;
        if (telemetryRound?.Round != round)
            return;

        // Explicit same-round Sandbox controls represent a fresh attempt.  Drop
        // the mutable sampler, but retain the previous terminal report until this
        // attempt reaches its own terminal state.
        telemetryRound = null;
        telemetryPathIndices.Clear();
        telemetryGeneration = matchGeneration;
    }

    private static BridgeResultV1 HandleStartRound(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot start round: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
        UpdateUiState(false);
        if (!uiState.Ready)
            return ErrorResult(request, "UI_BLOCKED", "A UI interruption is blocking round start. Inspect status.ui.", false);

        try
        {
            bridge.StartRound();
            return SuccessResult(request, new
            {
                Started = true,
                Round = bridge.GetCurrentRound() + 1
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "START_ROUND_FAILED", $"Failed to start round: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSetRound(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot set round: no active match.", false);

        if (request.Payload.ValueKind != JsonValueKind.Object ||
            !request.Payload.TryGetProperty("round", out var roundProp) ||
            roundProp.ValueKind != JsonValueKind.Number)
        {
            return ErrorResult(request, "INVALID_ARGUMENT", "Argument 'round' (integer >= 1) is required.", false);
        }

        int targetRound = roundProp.GetInt32();
        if (targetRound < 1)
            return ErrorResult(request, "INVALID_ROUND", "Target round must be >= 1.", false);

        try
        {
            ResetRoundClock();
            inGame.OnSetRound(targetRound);
            inGame.UpdateRoundForMap(targetRound, true);

            if (inGame.bridge != null)
            {
                inGame.bridge.SetRound(targetRound - 1);
            }

            try
            {
                var bloonMenu = UnityEngine.Object.FindObjectOfType<Il2CppAssets.Scripts.Unity.UI_New.InGame.BloonMenu.BloonMenu>();
                if (bloonMenu != null)
                {
                    bloonMenu.SetRoundText(targetRound);
                    bloonMenu.OnRoundValueChanged(targetRound.ToString());
                }
            }
            catch { }
            RefreshTelemetryForSandboxReplay(targetRound);

            int currentRound = inGame.bridge?.GetCurrentRound() != null
                ? inGame.bridge.GetCurrentRound() + 1
                : targetRound;

            return SuccessResult(request, new
            {
                Round = targetRound,
                CurrentRound = currentRound
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SET_ROUND_FAILED", $"Failed to set round: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleAdvanceRound(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot advance round: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        try
        {
            ResetRoundClock();
            int currentRound = bridge.GetCurrentRound() + 1;
            int nextRound = currentRound + 1;

            if (bridge.Simulation != null)
            {
                bridge.Simulation.RoundEnd(currentRound - 1, currentRound - 1);
            }

            if (autoCollectDrops)
            {
                CollectActiveDrops();
            }

            inGame.OnSetRound(nextRound);
            inGame.UpdateRoundForMap(nextRound, true);
            bridge.SetRound(nextRound - 1);

            try
            {
                var bloonMenu = UnityEngine.Object.FindObjectOfType<Il2CppAssets.Scripts.Unity.UI_New.InGame.BloonMenu.BloonMenu>();
                if (bloonMenu != null)
                {
                    bloonMenu.SetRoundText(nextRound);
                    bloonMenu.OnRoundValueChanged(nextRound.ToString());
                }
            }
            catch { }

            return SuccessResult(request, new
            {
                Advanced = true,
                CompletedRound = currentRound,
                NewRound = nextRound,
                Cash = inGame.GetCash()
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "ADVANCE_ROUND_FAILED", $"Failed to advance round: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSandboxSpawnRound(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot spawn sandbox round: no active match.", false);

        try
        {
            var bloonMenu = UnityEngine.Object.FindObjectOfType<Il2CppAssets.Scripts.Unity.UI_New.InGame.BloonMenu.BloonMenu>();
            if (bloonMenu != null)
            {
                int replayRound = inGame.bridge?.GetCurrentRound() + 1 ?? 1;
                if (request.Payload.ValueKind == JsonValueKind.Object &&
                    request.Payload.TryGetProperty("round", out var roundProp) &&
                    roundProp.ValueKind == JsonValueKind.Number)
                {
                    replayRound = roundProp.GetInt32();
                    inGame.OnSetRound(replayRound);
                    inGame.UpdateRoundForMap(replayRound, true);
                    if (inGame.bridge != null) inGame.bridge.SetRound(replayRound - 1);
                    bloonMenu.SetRoundText(replayRound);
                    bloonMenu.OnRoundValueChanged(replayRound.ToString());
                }

                ResetRoundClock();
                RefreshTelemetryForSandboxReplay(replayRound);
                bloonMenu.CheatSpawnRound();
                return SuccessResult(request, new { Spawned = true });
            }
            return ErrorResult(request, "SANDBOX_UNAVAILABLE", "BloonMenu is not active or match is not in Sandbox mode.", false);
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SPAWN_ROUND_FAILED", $"Failed to spawn round in sandbox: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSandboxClearBloons(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot clear bloons: no active match.", false);

        try
        {
            var bloonMenu = UnityEngine.Object.FindObjectOfType<Il2CppAssets.Scripts.Unity.UI_New.InGame.BloonMenu.BloonMenu>();
            if (bloonMenu != null)
            {
                bloonMenu.OnClickedDestroyBloons();
                return SuccessResult(request, new { Cleared = true });
            }
            return ErrorResult(request, "SANDBOX_UNAVAILABLE", "BloonMenu is not active or match is not in Sandbox mode.", false);
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "CLEAR_BLOONS_FAILED", $"Failed to clear bloons: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSetGameSpeed(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot set game speed: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        bool fastForward = true;
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("fastForward", out var ffProp))
        {
            if (ffProp.ValueKind == JsonValueKind.True || ffProp.ValueKind == JsonValueKind.False)
                fastForward = ffProp.GetBoolean();
        }

        try
        {
            bridge.SetFastForward(fastForward);
            return SuccessResult(request, new
            {
                FastForward = fastForward
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SET_SPEED_FAILED", $"Failed to set game speed: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandlePauseMatch(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot pause: no active match.", false);

        try
        {
            isMatchPaused = true;
            try { TimeManager.gamePaused = true; } catch { }
            UnityEngine.Time.timeScale = 0f;

            var bridge = inGame.bridge;
            int? round = null;
            try { round = bridge != null ? bridge.GetCurrentRound() + 1 : null; } catch { }

            return SuccessResult(request, new
            {
                Paused = true,
                Round = round,
                RoundElapsedSeconds = hasRoundElapsedTime ? (float?)currentRoundElapsedSeconds : null
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "PAUSE_FAILED", $"Failed to pause match: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleResumeMatch(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot resume: no active match.", false);

        try
        {
            isMatchPaused = false;
            try { TimeManager.gamePaused = false; } catch { }
            try { TimeManager.ResetTimeScale(); } catch { }

            bool fastForward = false;
            try { fastForward = TimeManager.FastForwardActive; } catch { }
            if (UnityEngine.Time.timeScale == 0f)
            {
                UnityEngine.Time.timeScale = fastForward ? 3f : 1f;
            }

            var bridge = inGame.bridge;
            int? round = null;
            try { round = bridge != null ? bridge.GetCurrentRound() + 1 : null; } catch { }

            return SuccessResult(request, new
            {
                Resumed = true,
                FastForward = fastForward,
                Round = round,
                RoundElapsedSeconds = hasRoundElapsedTime ? (float?)currentRoundElapsedSeconds : null
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "RESUME_FAILED", $"Failed to resume match: {ex.Message}", false);
        }
    }
}
