using System;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    public static void ResetAllCheckpoints()
    {
        roundJsonCheckpoints.Clear();
        customJsonCheckpoints.Clear();
        ResetCheckpointFidelity();
        customCheckpointTimestamps.Clear();
        roundCheckpointTimestamps.Clear();
    }

    public static void SaveRoundCheckpoint(int nextRound, int highestCompletedRound)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame()) return;

        using var timing = Measure("checkpoint_capture_serialize");
        try
        {
            var saveModel = inGame.CreateCurrentMapSave(highestCompletedRound, inGame.MapDataSaveId);
            if (saveModel == null) return;

            saveModel.round = nextRound;

            var settings = new Il2CppNewtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Il2CppNewtonsoft.Json.TypeNameHandling.Objects };
            var json = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveModel, settings);

            roundJsonCheckpoints[nextRound] = json;
            roundCheckpointTimestamps[nextRound] = DateTime.UtcNow;
            RecordCheckpointFidelity(null, nextRound, true);
            TrimRoundCheckpoints();
            ModHelper.Msg<AgentBridgeMod>($"Checkpoint saved for round {nextRound} (saveModel.round={saveModel.round}, highestCompleted={highestCompletedRound}).");
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"Failed to save round {nextRound} checkpoint: {ex.Message}");
        }
    }
    private static bool TryReadCheckpointLabel(JsonElement payload, out string? label, out string? error)
    {
        label = null;
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
            return true;

        bool supplied = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name != "label")
                continue;
            if (supplied)
            {
                error = "label may be specified only once.";
                return false;
            }

            supplied = true;
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = "label must be a non-empty string when supplied.";
                return false;
            }

            label = property.Value.GetString();
        }

        if (!CheckpointLabelValidation.TryValidate(label, supplied, out error))
            return false;
        return true;
    }

    private static BridgeResultV1 HandleSaveCheckpoint(BridgeRequestV1 request)
    {
        if (!TryReadCheckpointLabel(request.Payload, out string? label, out string? labelError))
            return ErrorResult(request, "INVALID_ARGUMENT", labelError!, false);

        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot save checkpoint: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        int currentRound = bridge.GetCurrentRound() + 1;
        bool roundsActive = bridge.AreRoundsActive();
        if (roundsActive && !isMatchPaused && UnityEngine.Time.timeScale > 0f)
        {
            return ErrorResult(request, "ROUND_ACTIVE", "Cannot save checkpoint while a round is actively running. Wait until between rounds or pause the match first.", false);
        }

        int highestCompleted = currentRound - 1;
        var saveModel = inGame.CreateCurrentMapSave(highestCompleted, inGame.MapDataSaveId);
        if (saveModel == null)
            return ErrorResult(request, "SAVE_FAILED", "Game failed to generate map save data.", false);

        saveModel.round = currentRound;

        var settings = new Il2CppNewtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Il2CppNewtonsoft.Json.TypeNameHandling.Objects };
        var json = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveModel, settings);


        if (!string.IsNullOrEmpty(label))
        {
            customJsonCheckpoints[label] = json;
            customCheckpointTimestamps[label] = DateTime.UtcNow;
            RecordCheckpointFidelity(label, currentRound, !roundsActive && !inGame.MatchLost);
            TrimCustomCheckpoints();
        }
        else
        {
            roundJsonCheckpoints[currentRound] = json;
            roundCheckpointTimestamps[currentRound] = DateTime.UtcNow;
            RecordCheckpointFidelity(null, currentRound, !roundsActive && !inGame.MatchLost);
            TrimRoundCheckpoints();
        }

        return SuccessResult(request, new
        {
            Saved = true,
            Round = currentRound,
            Label = label,
            TimestampUtc = DateTime.UtcNow.ToString("o")
        });
    }

    private static BridgeResultV1 HandleRestoreCheckpoint(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot restore checkpoint: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
        if (TryRestoreImportedCheckpoint(request, out var importedResult))
            return importedResult;

        int targetRound = -1;
        string? label = null;

        if (request.Payload.ValueKind == JsonValueKind.Object)
        {
            if (request.Payload.TryGetProperty("round", out var roundProp) &&
                roundProp.ValueKind == JsonValueKind.Number &&
                roundProp.TryGetInt32(out var r))
            {
                targetRound = r;
            }
            if (request.Payload.TryGetProperty("label", out var labelProp) &&
                labelProp.ValueKind == JsonValueKind.String)
            {
                label = labelProp.GetString();
            }
        }

        string? json = null;
        if (!string.IsNullOrEmpty(label) && customJsonCheckpoints.TryGetValue(label, out var customJson))
        {
            json = customJson;
        }
        else if (targetRound > 0 && roundJsonCheckpoints.TryGetValue(targetRound, out var roundJson))
        {
            json = roundJson;
        }
        else if (targetRound <= 0 && string.IsNullOrEmpty(label))
        {
            if (roundJsonCheckpoints.Count > 0)
            {
                targetRound = roundJsonCheckpoints.Keys.Max();
                json = roundJsonCheckpoints[targetRound];
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            return ErrorResult(request, "CHECKPOINT_NOT_FOUND",
                $"No checkpoint found for round '{targetRound}' or label '{label}'. Available rounds: [{string.Join(", ", roundJsonCheckpoints.Keys.OrderBy(k => k))}]", false);
        }

        return RestoreCheckpointJson(request, json, trustedInMemory: true);
    }

    private static BridgeResultV1 HandleListCheckpoints(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        int currentRound = -1;
        if (inGame != null && inGame.IsInGame() && inGame.bridge != null)
        {
            try { currentRound = inGame.bridge.GetCurrentRound() + 1; } catch { }
        }

        var availableRounds = roundJsonCheckpoints.Keys.OrderBy(k => k).ToArray();
        var customLabels = customJsonCheckpoints.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        var imported = BuildImportedCheckpointInventory();

        return SuccessResult(request, new
        {
            CurrentRound = currentRound > 0 ? currentRound : (int?)null,
            AvailableRoundCheckpoints = availableRounds,
            CustomCheckpoints = customLabels,
            ImportedCheckpoints = imported,
            Count = availableRounds.Length + customLabels.Length + imported.Length
        });
    }

    private static BridgeResultV1 HandleDeleteCheckpoint(BridgeRequestV1 request)
    {
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("checkpointId", out _))
            return HandleDeleteImportedCheckpoint(request);
        string? label = null;
        int? round = null;

        if (request.Payload.ValueKind == JsonValueKind.Object)
        {
            if (request.Payload.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                label = labelProp.GetString();
            if (request.Payload.TryGetProperty("round", out var roundProp))
            {
                if (roundProp.ValueKind != JsonValueKind.Number ||
                    !roundProp.TryGetInt32(out var parsedRound) ||
                    parsedRound <= 0)
                {
                    return ErrorResult(request, "INVALID_ARGUMENT", "Argument 'round' (integer >= 1) is required.", false);
                }

                round = parsedRound;
            }
        }
        if (label != null && !CheckpointLabelValidation.IsValid(label))
            return ErrorResult(request, "INVALID_ARGUMENT",
                "label must be a non-empty string with at most 128 characters and no control characters.", false);


        bool deleted = false;
        if (!string.IsNullOrEmpty(label))
        {
            if (customJsonCheckpoints.Remove(label))
            {
                customCheckpointTimestamps.Remove(label);
                customCheckpointFidelity.Remove(label);
                deleted = true;
            }
        }
        else if (round.HasValue && round.Value > 0)
        {
            if (roundJsonCheckpoints.Remove(round.Value))
            {
                roundCheckpointTimestamps.Remove(round.Value);
                roundCheckpointFidelity.Remove(round.Value);
                deleted = true;
            }
        }
        else
        {
            return ErrorResult(request, "INVALID_ARGUMENT", "Specify either 'label' or 'round' to delete.", false);
        }

        if (!deleted)
        {
            return ErrorResult(request, "CHECKPOINT_NOT_FOUND", $"Checkpoint '{(label ?? round?.ToString())}' not found.", false);
        }

        int remainingCount = customJsonCheckpoints.Count + roundJsonCheckpoints.Count;
        return SuccessResult(request, new
        {
            Deleted = true,
            Label = label,
            Round = round,
            RemainingCount = remainingCount
        });
    }
}
