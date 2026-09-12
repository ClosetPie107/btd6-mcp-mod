using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Profile;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader.Utils;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    // Checkpoint transfer is deliberately separate from the native in-memory checkpoint
    // dictionaries. ResetAllCheckpoints() may therefore clear automatic/manual checkpoints
    // when a match is replaced without discarding an imported archive.
    private const int MaxTransferCheckpoints = 128;
    private const int MaxImportedCheckpoints = 128;
    private const int MaxCheckpointPayloadBytes = 512 * 1024;
    private const int MaxTransferBundleBytes = 8 * 1024 * 1024;
    private const int MaxTransferStringLength = 512;
    private const int MaxNativeJsonDepth = 96;
    private const int MaxNativeJsonNodes = 100_000;
    private const int SupportedNativeMapModelVersion = 7;
    private const string NativeMapSaveType = "Assets.Scripts.Models.Profile.MapSaveDataModel, Assembly-CSharp";
    private const string NativeAssemblyName = "Assembly-CSharp";
    private const string NativeSchemaName = "MapSaveDataModel";

    private static readonly Dictionary<string, ImportedCheckpointState> importedCheckpoints = new(StringComparer.Ordinal);
    // Fidelity is provenance, not an inference from payload fields. A retained
    // checkpoint is exportable only after Main records that its capture happened at
    // a verified round boundary.
    private static readonly Dictionary<string, bool> customCheckpointFidelity = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, bool> roundCheckpointFidelity = new();

    private static void RecordCheckpointFidelity(string? label, int round, bool atBoundary)
    {
        if (round < 1) return;
        if (string.IsNullOrEmpty(label))
            roundCheckpointFidelity[round] = atBoundary;
        else
            customCheckpointFidelity[label] = atBoundary;
    }

    private static void ResetCheckpointFidelity()
    {
        customCheckpointFidelity.Clear();
        roundCheckpointFidelity.Clear();
    }

    private static bool TryGetCheckpointFidelity(string? label, int round, out bool atBoundary)
    {
        if (string.IsNullOrEmpty(label))
            return roundCheckpointFidelity.TryGetValue(round, out atBoundary);
        return customCheckpointFidelity.TryGetValue(label, out atBoundary);
    }

    private static readonly HashSet<string> importedBundleIds = new(StringComparer.Ordinal);

    // BTD6 56.3 map-save DTOs and the exact collection types used by native serialization.
    // It intentionally does not contain arbitrary Unity, reflection, or framework types.
    // A payload with a new native type must fail closed until this allowlist is researched.
    private static readonly HashSet<string> AllowedNativeSaveTypes = new(StringComparer.Ordinal)
    {
        "Assets.Scripts.Models.Profile.MapSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.PlayerSaveData, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.TowerHistory, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.TowerSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.LoanSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.PowerSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.PropSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.RoundRemoveableSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.ProjectileSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.MapGizmoSaveData, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.MapEventSaveData, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.MapEventTriggerSaveData, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.MapEventActionSaveData, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.TowerDiscountSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.TowerMaxCountPlacedHistory, Assembly-CSharp",
        "Assets.Scripts.Models.Profile.MutatorSaveDataModel, Assembly-CSharp",
        "Assets.Scripts.Models.Towers.TargetType, Assembly-CSharp",
        "Assets.Scripts.Simulation.Input.GeraldoShopInventory+GeraldoStockItem, Assembly-CSharp",
        "System.Collections.Generic.Dictionary`2[[System.Int32, mscorlib],[Assets.Scripts.Models.Profile.PlayerSaveData, Assembly-CSharp]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.Int32, mscorlib],[System.Int32, mscorlib]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[Assets.Scripts.Models.Profile.MapEventSaveData, Assembly-CSharp]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[Assets.Scripts.Models.Profile.MapEventTriggerSaveData, Assembly-CSharp]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[Assets.Scripts.Models.Profile.MapEventActionSaveData, Assembly-CSharp]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[Assets.Scripts.Models.Profile.TowerMaxCountPlacedHistory, Assembly-CSharp]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Collections.Generic.List`1[[Assets.Scripts.Models.Profile.MutatorSaveDataModel, Assembly-CSharp]], mscorlib]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Int32, mscorlib]], mscorlib",
        "System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.String, mscorlib]], mscorlib"
    };

    private static readonly JsonSerializerOptions TransferJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private sealed class ImportedCheckpointState
    {
        public ImportedCheckpointState(string exportId, CheckpointTransferEntryV1 entry)
        {
            ExportId = exportId;
            Entry = entry;
        }

        public string ExportId { get; }
        public CheckpointTransferEntryV1 Entry { get; }
    }

    private sealed class CheckpointCapture
    {
        public string Payload { get; init; } = "";
        public string? Label { get; init; }
        public int Round { get; init; }
        public DateTime TimestampUtc { get; init; }
        public string Source { get; init; } = "";
    }

    private sealed class NativePayloadInfo
    {
        public int ModelVersion { get; init; }
        public int Round { get; init; }
        public string GameVersion { get; init; } = "";
        public string MapName { get; init; } = "";
        public string MapDifficulty { get; init; } = "";
        public string ModeName { get; init; } = "";
        public string GameType { get; init; } = "";
    }

    private sealed class ImportSnapshot
    {
        public string[] CheckpointIds { get; init; } = Array.Empty<string>();
        public string[] Labels { get; init; } = Array.Empty<string>();
        public string[] BundleIds { get; init; } = Array.Empty<string>();
        public int Count { get; init; }
    }

    private sealed class TransferFailure
    {
        public string Code { get; init; } = "TRANSFER_FAILED";
        public string Message { get; init; } = "Checkpoint transfer failed.";
        public bool Retryable { get; init; }
        public object? Details { get; init; }
    }

    private sealed class ExportWorkerResult
    {
        public CheckpointExportBundleV1? Bundle { get; init; }
        public string Json { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long Bytes { get; init; }
        public bool Idempotent { get; init; }
        public TransferFailure? Failure { get; init; }
    }

    private sealed class ImportWorkerResult
    {
        public CheckpointExportBundleV1? Bundle { get; init; }
        public string Json { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public long Bytes { get; init; }
        public TransferFailure? Failure { get; init; }
    }

    private sealed class CheckpointValidationResult
    {
        public CheckpointExportBundleV1? Bundle { get; init; }
        public TransferFailure? Failure { get; init; }
    }

    private sealed class NativePayloadValidationResult
    {
        public NativePayloadInfo? Info { get; init; }
        public TransferFailure? Failure { get; init; }
    }

    private static string CheckpointExportsDirectory =>
        Path.Combine(MelonEnvironment.UserDataDirectory, "AgentBridge", "exports");

    private static string CheckpointExportRelativePath(string exportId) =>
        $"AgentBridge/exports/{exportId}.json";


    private static IEnumerator<BridgeResultV1?> RunExportCheckpoints(BridgeRequestV1 request)
    {
        if (!TryReadExportOptions(request.Payload, out bool includeCurrent, out bool includeSaved, out string? optionError))
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", optionError!, false);
            yield break;
        }

        string exportId = BuildOpaqueExportId(request.RequestId);
        if (!TryCaptureExportBundle(request, exportId, includeCurrent, includeSaved,
                out var bundle, out BridgeResultV1? captureFailure))
        {
            yield return captureFailure!;
            yield break;
        }

        string exportsDirectory = CheckpointExportsDirectory;
        Task<ExportWorkerResult> operation = Task.Run(() => WriteExportBundle(bundle!, exportsDirectory));
        while (!operation.IsCompleted)
            yield return null;

        ExportWorkerResult worker;
        Exception? workerException = null;
        try
        {
            worker = operation.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            worker = new ExportWorkerResult();
            workerException = ex;
        }
        if (workerException != null)
        {
            yield return ErrorResult(request, "EXPORT_FAILED", $"Export worker failed: {workerException.GetType().Name}: {workerException.Message}", true);
            yield break;
        }

        if (worker.Failure != null)
        {
            yield return ErrorResult(request, worker.Failure.Code, worker.Failure.Message,
                worker.Failure.Retryable, worker.Failure.Details);
            yield break;
        }

        var writtenBundle = worker.Bundle!;
        yield return SuccessResult(request, new CheckpointExportResultV1
        {
            Exported = true,
            ExportId = writtenBundle.ExportId,
            Path = CheckpointExportRelativePath(writtenBundle.ExportId),
            Checkpoints = writtenBundle.Checkpoints.Select(ToCheckpointMetadata).ToArray(),
            Sha256 = worker.Sha256,
            Bytes = worker.Bytes,
            Idempotent = worker.Idempotent
        });
    }

    private static IEnumerator<BridgeResultV1?> RunImportCheckpoints(BridgeRequestV1 request)
    {
        if (!TryReadImportExportId(request.Payload, out string exportId, out string? optionError))
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", optionError!, false);
            yield break;
        }

        string expectedGameVersion = Application.version;
        var snapshot = SnapshotImportedState();
        string exportsDirectory = CheckpointExportsDirectory;
        Task<ImportWorkerResult> operation = Task.Run(() => ReadAndValidateImportBundle(
            exportId, exportsDirectory, expectedGameVersion, snapshot));
        while (!operation.IsCompleted)
            yield return null;

        ImportWorkerResult worker;
        Exception? workerException = null;
        try
        {
            worker = operation.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            worker = new ImportWorkerResult();
            workerException = ex;
        }
        if (workerException != null)
        {
            yield return ErrorResult(request, "IMPORT_FAILED", $"Import worker failed: {workerException.GetType().Name}: {workerException.Message}", true);
            yield break;
        }

        if (worker.Failure != null)
        {
            yield return ErrorResult(request, worker.Failure.Code, worker.Failure.Message,
                worker.Failure.Retryable, worker.Failure.Details);
            yield break;
        }

        var bundle = worker.Bundle!;
        if (!TryCommitImportedBundle(bundle, out string? commitError))
        {
            yield return ErrorResult(request, "IMPORT_CONFLICT", commitError!, false);
            yield break;
        }

        yield return SuccessResult(request, new CheckpointImportResultV1
        {
            Imported = true,
            ExportId = bundle.ExportId,
            Checkpoints = bundle.Checkpoints.Select(ToCheckpointMetadata).ToArray()
        });
    }

    private static bool TryReadExportOptions(JsonElement payload, out bool includeCurrent,
        out bool includeSaved, out string? error)
    {
        includeCurrent = true;
        includeSaved = true;
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Expected an object with optional includeCurrent and includeSaved booleans.";
            return false;
        }

        bool seenIncludeCurrent = false;
        bool seenIncludeSaved = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name is not ("includeCurrent" or "includeSaved"))
            {
                error = $"Unknown export option '{property.Name}'.";
                return false;
            }

            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = $"Export option '{property.Name}' must be a boolean.";
                return false;
            }

            if (property.Name == "includeCurrent")
            {
                if (seenIncludeCurrent)
                {
                    error = "includeCurrent may be specified only once.";
                    return false;
                }
                includeCurrent = property.Value.GetBoolean();
                seenIncludeCurrent = true;
            }
            else
            {
                if (seenIncludeSaved)
                {
                    error = "includeSaved may be specified only once.";
                    return false;
                }
                includeSaved = property.Value.GetBoolean();
                seenIncludeSaved = true;
            }
        }

        if (!includeCurrent && !includeSaved)
        {
            error = "At least one of includeCurrent or includeSaved must be true.";
            return false;
        }

        return true;
    }
    private static bool TryReadImportExportId(JsonElement payload, out string exportId, out string? error)
    {
        exportId = "";
        error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Expected { exportId } for checkpoint import.";
            return false;
        }

        bool seenExportId = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name != "exportId")
            {
                error = $"Unknown import option '{property.Name}'. Only exportId is accepted.";
                return false;
            }
            if (seenExportId)
            {
                error = "exportId may be specified only once.";
                return false;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = "exportId must be a safe archive token.";
                return false;
            }
            seenExportId = true;
            exportId = property.Value.GetString() ?? "";
        }

        if (!IsSafeTransferToken(exportId))
        {
            error = "exportId must contain 1-128 letters, digits, '.', '_' or '-' and no path separators.";
            return false;
        }
        return true;
    }
    private static bool TryCaptureExportBundle(BridgeRequestV1 request, string exportId,
        bool includeCurrent, bool includeSaved, out CheckpointExportBundleV1? bundle,
        out BridgeResultV1? failure)
    {
        bundle = null;
        failure = null;
        var captures = new List<CheckpointCapture>();
        NativePayloadInfo? firstInfo = null;
        CheckpointMatchV1? match = null;

        if (includeCurrent)
        {
            var inGame = InGame.instance;
            if (inGame == null || !inGame.IsInGame())
            {
                failure = ErrorResult(request, "NO_ACTIVE_GAME", "Current checkpoint export requires an active match.", false);
                return false;
            }

            var bridge = inGame.bridge;
            if (bridge == null)
            {
                failure = ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
                return false;
            }

            bool roundsActive;
            try { roundsActive = bridge.AreRoundsActive(); }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "SIMULATION_UNAVAILABLE", $"Unable to inspect round state: {ex.Message}", true);
                return false;
            }

            // Pausing does not turn an active wave into a round boundary. This is
            // deliberately stricter than the legacy save_checkpoint command.
            if (roundsActive)
            {
                failure = ErrorResult(request, "ROUND_ACTIVE",
                    "Cannot export the current checkpoint while a round is active, including a paused wave. Wait between rounds.", false);
                return false;
            }
            try
            {
                if (inGame.MatchLost || inGame.WaitingForVictoryScreen)
                {
                    failure = ErrorResult(request, "MATCH_ENDED",
                        "Cannot export the current checkpoint after defeat or victory.", false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "SIMULATION_UNAVAILABLE",
                    $"Unable to inspect match state: {ex.Message}", true);
                return false;
            }

            int currentRound;
            try { currentRound = bridge.GetCurrentRound() + 1; }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "SIMULATION_UNAVAILABLE", $"Unable to read current round: {ex.Message}", true);
                return false;
            }

            MapSaveDataModel? saveModel;
            try { saveModel = inGame.CreateCurrentMapSave(currentRound - 1, inGame.MapDataSaveId); }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "CAPTURE_FAILED", $"Native map save capture failed: {ex.Message}", false);
                return false;
            }

            if (saveModel == null)
            {
                failure = ErrorResult(request, "CAPTURE_FAILED", "Game failed to generate current map save data.", false);
                return false;
            }

            try { saveModel.round = currentRound; }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "CAPTURE_FAILED", $"Native map save round assignment failed: {ex.Message}", false);
                return false;
            }

            string json;
            try
            {
                var settings = new Il2CppNewtonsoft.Json.JsonSerializerSettings
                {
                    TypeNameHandling = Il2CppNewtonsoft.Json.TypeNameHandling.Objects
                };
                json = Il2CppNewtonsoft.Json.JsonConvert.SerializeObject(saveModel, settings);
            }
            catch (Exception ex)
            {
                failure = ErrorResult(request, "CAPTURE_FAILED", $"Native map save serialization failed: {ex.Message}", false);
                return false;
            }

            var validation = ValidateNativePayload(json, currentRound, Application.version, true);
            if (validation.Failure != null)
            {
                failure = ErrorResult(request, validation.Failure.Code, validation.Failure.Message,
                    validation.Failure.Retryable, validation.Failure.Details);
                return false;
            }

            firstInfo = validation.Info;
            captures.Add(new CheckpointCapture
            {
                Payload = json,
                Round = currentRound,
                Source = "current",
                TimestampUtc = DateTime.UtcNow
            });
            match = CaptureLiveMatchMetadata(firstInfo);
        }

        if (includeSaved)
        {
            foreach (var pair in customJsonCheckpoints.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!CheckpointLabelValidation.IsValid(pair.Key))
                {
                    failure = ErrorResult(request, "INVALID_CHECKPOINT_LABEL",
                        $"Custom checkpoint '{pair.Key}' has an invalid label.", false);
                    return false;
                }
                if (!TryGetCheckpointFidelity(pair.Key, 0, out bool atBoundary) || !atBoundary)
                {
                    failure = ErrorResult(request, "UNSUPPORTED_FIDELITY",
                        $"Custom checkpoint '{pair.Key}' has no verified round-boundary capture provenance.", false,
                        new { Label = pair.Key, Reason = "capture_provenance_unavailable" });
                    return false;
                }
                var validation = ValidateNativePayload(pair.Value, null, Application.version, true);
                if (validation.Failure != null)
                {
                    failure = ErrorResult(request, "UNSUPPORTED_FIDELITY",
                        $"Custom checkpoint '{pair.Key}' cannot be exported safely: {validation.Failure.Message}", false,
                        new { Label = pair.Key, Reason = validation.Failure.Code });
                    return false;
                }

                firstInfo ??= validation.Info;
                captures.Add(new CheckpointCapture
                {
                    Payload = pair.Value,
                    Label = pair.Key,
                    Round = validation.Info!.Round,
                    Source = "custom",
                    TimestampUtc = customCheckpointTimestamps.TryGetValue(pair.Key, out var timestamp)
                        ? timestamp.ToUniversalTime() : DateTime.UtcNow
                });
            }

            foreach (var pair in roundJsonCheckpoints.OrderBy(pair => pair.Key))
            {
                if (!TryGetCheckpointFidelity(null, pair.Key, out bool atBoundary) || !atBoundary)
                {
                    failure = ErrorResult(request, "UNSUPPORTED_FIDELITY",
                        $"Round checkpoint {pair.Key} has no verified round-boundary capture provenance.", false,
                        new { Round = pair.Key, Reason = "capture_provenance_unavailable" });
                    return false;
                }
                var validation = ValidateNativePayload(pair.Value, pair.Key, Application.version, true);
                if (validation.Failure != null)
                {
                    failure = ErrorResult(request, "UNSUPPORTED_FIDELITY",
                        $"Round checkpoint {pair.Key} cannot be exported safely: {validation.Failure.Message}", false,
                        new { Round = pair.Key, Reason = validation.Failure.Code });
                    return false;
                }

                firstInfo ??= validation.Info;
                captures.Add(new CheckpointCapture
                {
                    Payload = pair.Value,
                    Round = pair.Key,
                    Source = "round",
                    TimestampUtc = roundCheckpointTimestamps.TryGetValue(pair.Key, out var timestamp)
                        ? timestamp.ToUniversalTime() : DateTime.UtcNow
                });
            }
        }

        if (captures.Count == 0)
        {
            failure = ErrorResult(request, "CHECKPOINT_NOT_FOUND", "No saved checkpoints are available for export.", false);
            return false;
        }
        if (captures.Count > MaxTransferCheckpoints)
        {
            failure = ErrorResult(request, "CHECKPOINT_CAPACITY_EXCEEDED",
                $"An export may contain at most {MaxTransferCheckpoints} checkpoints; found {captures.Count}.", false);
            return false;
        }

        firstInfo ??= new NativePayloadInfo { GameVersion = Application.version };
        match ??= BuildMatchMetadataFromPayload(firstInfo);
        if (string.IsNullOrEmpty(firstInfo.GameVersion) ||
            !string.Equals(firstInfo.GameVersion, Application.version, StringComparison.Ordinal))
        {
            failure = ErrorResult(request, "INCOMPATIBLE_GAME_VERSION",
                $"Checkpoint native game version '{firstInfo.GameVersion}' does not match BTD6 '{Application.version}'.", false);
            return false;
        }

        var entries = new List<CheckpointTransferEntryV1>(captures.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capture in captures.Select((capture, index) => (capture, index)))
        {
            if (capture.capture.Label != null && !CheckpointLabelValidation.IsValid(capture.capture.Label))
            {
                failure = ErrorResult(request, "INVALID_CHECKPOINT_LABEL",
                    "Export contains an invalid checkpoint label and was not written.", false);
                return false;
            }

            string checkpointId = BuildOpaqueCheckpointId(exportId, capture.capture.Source,
                capture.capture.Label, capture.capture.Round, capture.index);
            if (!ids.Add(checkpointId))
            {
                failure = ErrorResult(request, "CHECKPOINT_COLLISION", "Generated checkpoint IDs collided; export was not written.", false);
                return false;
            }

            entries.Add(new CheckpointTransferEntryV1
            {
                CheckpointId = checkpointId,
                Label = capture.capture.Label,
                Round = capture.capture.Round,
                TimestampUtc = capture.capture.TimestampUtc.ToUniversalTime(),
                Source = capture.capture.Source,
                Payload = capture.capture.Payload,
                Sha256 = ComputeSha256(capture.capture.Payload),
                Fidelity = "round_boundary"
            });
        }

        int modelVersion = firstInfo.ModelVersion;
        var native = new CheckpointNativeIdentityV1
        {
            Schema = NativeSchemaName,
            ModelVersion = modelVersion,
            RootType = NativeMapSaveType,
            Assembly = NativeAssemblyName,
            GameVersion = firstInfo.GameVersion
        };

        bundle = new CheckpointExportBundleV1
        {
            Version = 1,
            ExportId = exportId,
            RequestId = request.RequestId,
            CreatedAtUtc = DateTime.UtcNow,
            ProtocolVersion = ProtocolVersion,
            BridgeVersion = ModHelperData.Version,
            ModuleVersionId = typeof(AgentBridgeMod).Module.ModuleVersionId.ToString("D"),
            Btd6Version = Application.version,
            Native = native,
            Match = match,
            Checkpoints = entries
        };
        return true;
    }

    private static CheckpointMatchV1 CaptureLiveMatchMetadata(NativePayloadInfo? fallback)
    {
        string? map = null;
        string? difficulty = null;
        string? mode = null;
        string? hero = null;
        try
        {
            var current = InGameData.CurrentGame;
            map = current?.selectedMap;
            difficulty = current?.selectedDifficulty;
            mode = current?.selectedMode;
        }
        catch { }
        try { hero = Game.instance?.GetPlayerProfile()?.primaryHero; }
        catch { }

        map ??= fallback?.MapName;
        difficulty ??= fallback?.MapDifficulty;
        mode ??= fallback?.ModeName;
        return BuildMatch(map, difficulty, mode, hero, fallback?.GameType, matchId);
    }

    private static CheckpointMatchV1 BuildMatchMetadataFromPayload(NativePayloadInfo payload) =>
        BuildMatch(payload.MapName, payload.MapDifficulty, payload.ModeName, null, payload.GameType, null);

    private static string CanonicalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "";
        if (string.Equals(mode, "Clicks", StringComparison.OrdinalIgnoreCase)) return "CHIMPS";
        if (string.Equals(mode, "CHIMPS", StringComparison.OrdinalIgnoreCase)) return "CHIMPS";
        if (string.Equals(mode, "Sandbox", StringComparison.OrdinalIgnoreCase)) return "Sandbox";
        if (string.Equals(mode, "Impoppable", StringComparison.OrdinalIgnoreCase)) return "Impoppable";
        if (string.Equals(mode, "Standard", StringComparison.OrdinalIgnoreCase)) return "Standard";
        return mode;
    }
    private static CheckpointMatchV1 BuildMatch(string? map, string? difficulty, string? mode,
        string? hero, string? gameType, string? originalMatchId)
    {
        string canonicalMode = CanonicalizeMode(mode);
        return new CheckpointMatchV1
        {
            Map = map ?? "",
            Difficulty = difficulty ?? "",
            Mode = canonicalMode,
            Hero = hero,
            OriginalMatchId = originalMatchId,
            Rules = new CheckpointRulesV1
            {
                GameType = gameType ?? "",
                IsChimps = string.Equals(canonicalMode, "CHIMPS", StringComparison.OrdinalIgnoreCase),
                IsSandbox = string.Equals(canonicalMode, "Sandbox", StringComparison.OrdinalIgnoreCase),
                IsImpoppable = string.Equals(canonicalMode, "Impoppable", StringComparison.OrdinalIgnoreCase)
            }
        };
    }

    private static ExportWorkerResult WriteExportBundle(CheckpointExportBundleV1 bundle, string exportsDirectory)
    {
        try
        {
            string json = JsonSerializer.Serialize(bundle, TransferJsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.LongLength > MaxTransferBundleBytes)
                return new ExportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_TOO_LARGE",
                        Message = $"Serialized export is {bytes.LongLength} bytes; maximum is {MaxTransferBundleBytes}.",
                        Retryable = false
                    }
                };

            string exportPath = Path.Combine(exportsDirectory, $"{bundle.ExportId}.json");
            if (Directory.Exists(exportsDirectory) && !IsRegularTransferDirectory(exportsDirectory))
                return new ExportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_SYMLINK",
                        Message = "Checkpoint export directory must not be a symlink or reparse point.",
                        Retryable = false
                    }
                };
            Directory.CreateDirectory(exportsDirectory);
            if (!IsRegularTransferDirectory(exportsDirectory))
                return new ExportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_SYMLINK",
                        Message = "Checkpoint export directory must be a regular directory.",
                        Retryable = false
                    }
                };

            if (File.Exists(exportPath) && !IsRegularTransferPath(exportPath))
                return new ExportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_SYMLINK",
                        Message = "Checkpoint archive path must be a regular non-symlink file.",
                        Retryable = false
                    }
                };
            if (File.Exists(exportPath))
            {
                string existing = File.ReadAllText(exportPath, Encoding.UTF8);
                if (Encoding.UTF8.GetByteCount(existing) > MaxTransferBundleBytes)
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = "An existing archive with this export ID is oversized.",
                            Retryable = false
                        }
                    };
                if (!TryValidateBundleShape(existing, out string? existingShapeError))
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Existing export archive '{bundle.ExportId}' has invalid envelope shape: {existingShapeError}",
                            Retryable = false
                        }
                    };
                var existingBundle = JsonSerializer.Deserialize<CheckpointExportBundleV1>(existing, TransferJsonOptions);
                if (existingBundle == null || !string.Equals(existingBundle.RequestId, bundle.RequestId, StringComparison.Ordinal))
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Export archive '{bundle.ExportId}' already exists for a different request.",
                            Retryable = false
                        }
                    };
                var existingIntegrity = ValidateExistingExportBundle(existingBundle, bundle.Btd6Version);
                if (existingIntegrity != null)
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Existing export archive '{bundle.ExportId}' failed integrity validation: {existingIntegrity.Message}",
                            Retryable = false
                        }
                    };

                byte[] existingBytes = Encoding.UTF8.GetBytes(existing);
                return new ExportWorkerResult
                {
                    Bundle = existingBundle,
                    Json = existing,
                    Sha256 = ComputeSha256(existingBytes),
                    Bytes = existingBytes.LongLength,
                    Idempotent = true
                };
            }

            string temporaryPath = $"{exportPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // File.Move without overwrite is an atomic no-clobber publication on the
                // supported filesystem. A racing publisher is a collision, never overwrite.
                File.Move(temporaryPath, exportPath);
            }
            catch (IOException) when (File.Exists(exportPath))
            {
                if (!IsRegularTransferPath(exportPath))
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_SYMLINK",
                            Message = "Checkpoint archive path must be a regular non-symlink file.",
                            Retryable = false
                        }
                    };
                string existing = File.ReadAllText(exportPath, Encoding.UTF8);
                if (!TryValidateBundleShape(existing, out string? existingShapeError))
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Concurrent export archive has invalid envelope shape: {existingShapeError}",
                            Retryable = false
                        }
                    };
                var existingBundle = JsonSerializer.Deserialize<CheckpointExportBundleV1>(existing, TransferJsonOptions);
                if (existingBundle == null || !string.Equals(existingBundle.RequestId, bundle.RequestId, StringComparison.Ordinal))
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Export archive '{bundle.ExportId}' was concurrently published for a different request.",
                            Retryable = false
                        }
                    };
                var existingIntegrity = ValidateExistingExportBundle(existingBundle, bundle.Btd6Version);
                if (existingIntegrity != null)
                    return new ExportWorkerResult
                    {
                        Failure = new TransferFailure
                        {
                            Code = "EXPORT_COLLISION",
                            Message = $"Concurrent export archive '{bundle.ExportId}' failed integrity validation: {existingIntegrity.Message}",
                            Retryable = false
                        }
                    };
                byte[] existingBytes = Encoding.UTF8.GetBytes(existing);
                return new ExportWorkerResult
                {
                    Bundle = existingBundle,
                    Json = existing,
                    Sha256 = ComputeSha256(existingBytes),
                    Bytes = existingBytes.LongLength,
                    Idempotent = true
                };
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            return new ExportWorkerResult
            {
                Bundle = bundle,
                Json = json,
                Sha256 = ComputeSha256(bytes),
                Bytes = bytes.LongLength
            };
        }
        catch (Exception ex)
        {
            return new ExportWorkerResult
            {
                Failure = new TransferFailure
                {
                    Code = "EXPORT_IO_FAILED",
                    Message = $"Unable to durably publish checkpoint archive: {ex.GetType().Name}: {ex.Message}",
                    Retryable = true
                }
            };
        }
    }

    private static ImportWorkerResult ReadAndValidateImportBundle(string exportId, string exportsDirectory,
        string expectedGameVersion, ImportSnapshot snapshot)
    {
        try
        {
            string path = Path.Combine(exportsDirectory, $"{exportId}.json");
            if (!File.Exists(path))
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_NOT_FOUND",
                        Message = $"No checkpoint archive exists for exportId '{exportId}'.",
                        Retryable = false
                    }
                };

            if (!IsRegularTransferPath(path))
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_SYMLINK",
                        Message = "Checkpoint archive path must be a regular non-symlink file.",
                        Retryable = false
                    }
                };
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxTransferBundleBytes)
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "IMPORT_TOO_LARGE",
                        Message = $"Checkpoint archive size must be between 1 and {MaxTransferBundleBytes} bytes.",
                        Retryable = false
                    }
                };

            string json = File.ReadAllText(path, Encoding.UTF8);
            long bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes > MaxTransferBundleBytes)
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "IMPORT_TOO_LARGE",
                        Message = $"Checkpoint archive is {bytes} bytes; maximum is {MaxTransferBundleBytes}.",
                        Retryable = false
                    }
                };

            if (!TryValidateBundleShape(json, out string? shapeError))
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "CORRUPT_EXPORT",
                        Message = shapeError!,
                        Retryable = false
                    }
                };

            CheckpointExportBundleV1? bundle;
            try { bundle = JsonSerializer.Deserialize<CheckpointExportBundleV1>(json, TransferJsonOptions); }
            catch (Exception ex)
            {
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "CORRUPT_EXPORT",
                        Message = $"Checkpoint archive JSON is invalid: {ex.Message}",
                        Retryable = false
                    }
                };
            }

            if (bundle == null)
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure { Code = "CORRUPT_EXPORT", Message = "Checkpoint archive is empty.", Retryable = false }
                };
            if (!string.Equals(bundle.ExportId, exportId, StringComparison.Ordinal))
                return new ImportWorkerResult
                {
                    Failure = new TransferFailure
                    {
                        Code = "EXPORT_ID_MISMATCH",
                        Message = "Archive exportId does not match the requested safe token.",
                        Retryable = false
                    }
                };

            var validation = ValidateImportBundle(bundle, expectedGameVersion, snapshot);
            if (validation.Failure != null)
                return new ImportWorkerResult { Failure = validation.Failure };

            return new ImportWorkerResult
            {
                Bundle = bundle,
                Json = json,
                Sha256 = ComputeSha256(Encoding.UTF8.GetBytes(json)),
                Bytes = bytes
            };
        }
        catch (Exception ex)
        {
            return new ImportWorkerResult
            {
                Failure = new TransferFailure
                {
                    Code = "IMPORT_IO_FAILED",
                    Message = $"Unable to read checkpoint archive: {ex.GetType().Name}: {ex.Message}",
                    Retryable = true
                }
            };
        }
    }

    private static bool TryValidateBundleShape(string json, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxNativeJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            string[] rootNames =
            [
                "version", "exportId", "requestId", "createdAtUtc", "protocolVersion",
                "bridgeVersion", "moduleVersionId", "btd6Version", "native", "match", "checkpoints"
            ];
            if (!ValidateTransferObject(root, "bundle", rootNames, out error)) return false;
            if (root.TryGetProperty("native", out var native) &&
                !ValidateTransferObject(native, "native", ["schema", "modelVersion", "rootType", "assembly", "gameVersion"], out error))
                return false;
            if (root.TryGetProperty("match", out var match) &&
                !ValidateTransferObject(match, "match", ["map", "difficulty", "mode", "hero", "originalMatchId", "rules"], out error))
                return false;
            if (root.TryGetProperty("match", out match) && match.ValueKind == JsonValueKind.Object &&
                match.TryGetProperty("rules", out var rules) &&
                !ValidateTransferObject(rules, "match.rules", ["gameType", "isChimps", "isSandbox", "isImpoppable"], out error))
                return false;
            if (root.TryGetProperty("checkpoints", out var checkpoints))
            {
                if (checkpoints.ValueKind != JsonValueKind.Array)
                {
                    error = "Bundle checkpoints must be an array.";
                    return false;
                }
                foreach (var checkpoint in checkpoints.EnumerateArray())
                {
                    if (!ValidateTransferObject(checkpoint, "checkpoint",
                            ["checkpointId", "label", "round", "timestampUtc", "source", "payload", "sha256", "fidelity"], out error))
                        return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Checkpoint archive JSON is invalid: {ex.Message}";
            return false;
        }
    }

    private static bool ValidateTransferObject(JsonElement value, string context,
        IEnumerable<string> allowedNames, out string? error)
    {
        error = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = $"Bundle property '{context}' must be an object.";
            return false;
        }
        var allowed = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                error = $"Unknown bundle property '{context}.{property.Name}'.";
                return false;
            }
            if (!seen.Add(property.Name))
            {
                error = $"Duplicate bundle property '{context}.{property.Name}'.";
                return false;
            }
        }
        return true;

    }
    private static CheckpointValidationResult ValidateImportBundle(CheckpointExportBundleV1 bundle,
        string expectedGameVersion, ImportSnapshot snapshot)
    {
        if (bundle.Version != 1)
            return InvalidBundle("UNSUPPORTED_EXPORT_VERSION", "Only checkpoint export bundle version 1 is supported.");
        if (!IsSafeTransferToken(bundle.ExportId))
            return InvalidBundle("INVALID_EXPORT_ID", "Bundle exportId is not a safe opaque token.");
        if (!string.Equals(bundle.RequestId, "", StringComparison.Ordinal) && !IsSafeTransferToken(bundle.RequestId))
            return InvalidBundle("INVALID_REQUEST_ID", "Bundle requestId is not a safe request token.");
        if (bundle.ProtocolVersion != ProtocolVersion)
            return InvalidBundle("UNSUPPORTED_PROTOCOL", "Checkpoint bundle protocol version is unsupported.");
        if (!string.Equals(bundle.Btd6Version, expectedGameVersion, StringComparison.Ordinal))
            return InvalidBundle("INCOMPATIBLE_GAME_VERSION",
                $"Bundle BTD6 version '{bundle.Btd6Version}' does not match '{expectedGameVersion}'.");
        if (!IsBoundedTransferString(bundle.Btd6Version, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.BridgeVersion, MaxTransferStringLength) ||
            bundle.CreatedAtUtc.Kind != DateTimeKind.Utc)
            return InvalidBundle("INVALID_EXPORT_METADATA", "Bundle version or creation metadata is missing or unsafe.");

        if (string.IsNullOrWhiteSpace(bundle.ModuleVersionId) || !Guid.TryParse(bundle.ModuleVersionId, out _))
            return InvalidBundle("INVALID_NATIVE_IDENTITY", "Bundle moduleVersionId must be a GUID.");
        if (bundle.Native == null || !string.Equals(bundle.Native.Schema, NativeSchemaName, StringComparison.Ordinal) ||
            bundle.Native.ModelVersion != SupportedNativeMapModelVersion || !string.Equals(bundle.Native.RootType, NativeMapSaveType, StringComparison.Ordinal) ||
            !string.Equals(bundle.Native.Assembly, NativeAssemblyName, StringComparison.Ordinal) ||
            !string.Equals(bundle.Native.GameVersion, expectedGameVersion, StringComparison.Ordinal))
            return InvalidBundle("INCOMPATIBLE_NATIVE_SCHEMA",
                "Bundle native identity is not the exact BTD6 56.3 MapSaveDataModel schema.");
        if (bundle.Match == null || bundle.Match.Rules == null ||
            string.IsNullOrWhiteSpace(bundle.Match.Map) ||
            string.IsNullOrWhiteSpace(bundle.Match.Difficulty) ||
            string.IsNullOrWhiteSpace(bundle.Match.Mode))
            return InvalidBundle("INVALID_MATCH_METADATA", "Bundle match metadata and rules are required.");
        if (!IsBoundedTransferString(bundle.Match.Map, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.Match.Difficulty, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.Match.Mode, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.Match.Hero, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.Match.OriginalMatchId, MaxTransferStringLength) ||
            !IsBoundedTransferString(bundle.Match.Rules.GameType, MaxTransferStringLength))
            return InvalidBundle("INVALID_MATCH_METADATA", "Bundle match metadata contains an oversized or unsafe string.");
        if (bundle.Checkpoints == null || bundle.Checkpoints.Count is < 1 or > MaxTransferCheckpoints)
            return InvalidBundle("CHECKPOINT_CAPACITY_EXCEEDED",
                $"Bundle must contain between 1 and {MaxTransferCheckpoints} checkpoints.");
        if (snapshot.BundleIds.Contains(bundle.ExportId, StringComparer.Ordinal))
            return InvalidBundle("IMPORT_COLLISION", $"Export '{bundle.ExportId}' is already imported.");
        if (snapshot.Count + bundle.Checkpoints.Count > MaxImportedCheckpoints)
            return InvalidBundle("CHECKPOINT_CAPACITY_EXCEEDED",
                $"Importing this bundle would exceed the {MaxImportedCheckpoints}-checkpoint imported capacity.");

        var ids = new HashSet<string>(snapshot.CheckpointIds, StringComparer.Ordinal);
        var labels = new HashSet<string>(snapshot.Labels, StringComparer.OrdinalIgnoreCase);
        var bundleLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in bundle.Checkpoints)
        {
            if (checkpoint == null || !IsSafeTransferToken(checkpoint.CheckpointId))
                return InvalidBundle("INVALID_CHECKPOINT", "Every checkpoint needs a safe opaque checkpointId.");
            if (!ids.Add(checkpoint.CheckpointId))
                return InvalidBundle("IMPORT_COLLISION", $"Duplicate checkpointId '{checkpoint.CheckpointId}'.");
            if (checkpoint.Label != null)
            {
                if (!IsSafeTransferLabel(checkpoint.Label) || !labels.Add(checkpoint.Label) || !bundleLabels.Add(checkpoint.Label))
                    return InvalidBundle("IMPORT_COLLISION", $"Duplicate or invalid checkpoint label '{checkpoint.Label}'.");
            }
            if (checkpoint.Round is < 1 or > 10_000)
                return InvalidBundle("INVALID_CHECKPOINT", "Checkpoint round must be between 1 and 10000.");
            if (checkpoint.Source is not ("current" or "custom" or "round"))
                return InvalidBundle("INVALID_CHECKPOINT", "Checkpoint source must be current, custom, or round.");
            if (checkpoint.Source == "custom" && string.IsNullOrEmpty(checkpoint.Label))
                return InvalidBundle("INVALID_CHECKPOINT", "Custom checkpoints require a label.");
            if (checkpoint.Source == "round" && checkpoint.Label != null)
                return InvalidBundle("INVALID_CHECKPOINT", "Round checkpoints cannot have a label.");
            if (checkpoint.Fidelity != "round_boundary")
                return InvalidBundle("UNSUPPORTED_FIDELITY", "Only round_boundary checkpoint fidelity is restorable.");
            if (checkpoint.TimestampUtc == default || checkpoint.TimestampUtc.Kind != DateTimeKind.Utc)
                return InvalidBundle("INVALID_CHECKPOINT", "Checkpoint timestamp must be an explicit UTC timestamp.");
            if (string.IsNullOrEmpty(checkpoint.Payload) ||
                Encoding.UTF8.GetByteCount(checkpoint.Payload) > MaxCheckpointPayloadBytes)
                return InvalidBundle("CHECKPOINT_TOO_LARGE", $"Each payload is limited to {MaxCheckpointPayloadBytes} bytes.");
            if (!IsSha256(checkpoint.Sha256) ||
                !string.Equals(ComputeSha256(checkpoint.Payload), checkpoint.Sha256, StringComparison.OrdinalIgnoreCase))
                return InvalidBundle("CHECKPOINT_HASH_MISMATCH",
                    $"SHA-256 validation failed for checkpoint '{checkpoint.CheckpointId}'.");

            var native = ValidateNativePayload(checkpoint.Payload, checkpoint.Round, expectedGameVersion, true);
            if (native.Failure != null)
                return InvalidBundle(native.Failure.Code, $"Checkpoint '{checkpoint.CheckpointId}': {native.Failure.Message}");
            var info = native.Info!;
            if (info.ModelVersion != bundle.Native.ModelVersion ||
                (!string.IsNullOrEmpty(bundle.Native.GameVersion) && info.GameVersion != bundle.Native.GameVersion))
                return InvalidBundle("INCOMPATIBLE_NATIVE_SCHEMA",
                    $"Checkpoint '{checkpoint.CheckpointId}' does not match bundle native identity.");
            if (!string.IsNullOrEmpty(bundle.Match.Map) && info.MapName != bundle.Match.Map)
                return InvalidBundle("INCOMPATIBLE_MATCH", $"Checkpoint '{checkpoint.CheckpointId}' map does not match bundle metadata.");
            if (!string.IsNullOrEmpty(bundle.Match.Difficulty) && info.MapDifficulty != bundle.Match.Difficulty)
                return InvalidBundle("INCOMPATIBLE_MATCH", $"Checkpoint '{checkpoint.CheckpointId}' difficulty does not match bundle metadata.");
            if (!string.IsNullOrEmpty(bundle.Match.Mode) &&
                !string.Equals(CanonicalizeMode(info.ModeName), CanonicalizeMode(bundle.Match.Mode), StringComparison.OrdinalIgnoreCase))
                return InvalidBundle("INCOMPATIBLE_MATCH", $"Checkpoint '{checkpoint.CheckpointId}' mode does not match bundle metadata.");
        }

        return new CheckpointValidationResult { Bundle = bundle };
    }

    private static CheckpointValidationResult InvalidBundle(string code, string message) =>
        new() { Failure = new TransferFailure { Code = code, Message = message, Retryable = false } };

    private static TransferFailure? ValidateExistingExportBundle(CheckpointExportBundleV1 bundle, string expectedGameVersion)
    {
        var validation = ValidateImportBundle(bundle, expectedGameVersion, new ImportSnapshot());
        return validation.Failure;
    }

    private static bool TryCommitImportedBundle(CheckpointExportBundleV1 bundle, out string? error)
    {
        error = null;
        if (importedBundleIds.Contains(bundle.ExportId))
        {
            error = $"Export '{bundle.ExportId}' is already imported.";
            return false;
        }
        if (importedCheckpoints.Count + bundle.Checkpoints.Count > MaxImportedCheckpoints)
        {
            error = $"Importing this bundle would exceed the {MaxImportedCheckpoints}-checkpoint imported capacity.";
            return false;
        }

        var ids = new HashSet<string>(importedCheckpoints.Keys, StringComparer.Ordinal);
        var labels = new HashSet<string>(importedCheckpoints.Values
            .Where(value => value.Entry.Label != null)
            .Select(value => value.Entry.Label!), StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in bundle.Checkpoints)
        {
            if (!ids.Add(checkpoint.CheckpointId))
            {
                error = $"Checkpoint ID '{checkpoint.CheckpointId}' collides with an imported checkpoint.";
                return false;
            }
            if (checkpoint.Label != null && !labels.Add(checkpoint.Label))
            {
                error = $"Checkpoint label '{checkpoint.Label}' collides with an imported checkpoint.";
                return false;
            }
            if (checkpoint.Label != null && customJsonCheckpoints.ContainsKey(checkpoint.Label))
            {
                error = $"Checkpoint label '{checkpoint.Label}' collides with a live checkpoint.";
                return false;
            }
            if (roundJsonCheckpoints.ContainsKey(checkpoint.Round))
            {
                error = $"Checkpoint round '{checkpoint.Round}' collides with a live checkpoint.";
                return false;
            }
        }

        foreach (var checkpoint in bundle.Checkpoints)
            importedCheckpoints.Add(checkpoint.CheckpointId, new ImportedCheckpointState(bundle.ExportId, checkpoint));
        importedBundleIds.Add(bundle.ExportId);
        return true;
    }

    private static ImportSnapshot SnapshotImportedState() => new()
    {
        CheckpointIds = importedCheckpoints.Keys.ToArray(),
        Labels = importedCheckpoints.Values.Where(value => value.Entry.Label != null)
            .Select(value => value.Entry.Label!).ToArray(),
        BundleIds = importedBundleIds.ToArray(),
        Count = importedCheckpoints.Count
    };

    private static bool TryDeleteImportedCheckpoint(string checkpointId, out ImportedCheckpointInfoV1? deleted)
    {
        deleted = null;
        if (!importedCheckpoints.TryGetValue(checkpointId, out var state)) return false;
        importedCheckpoints.Remove(checkpointId);
        deleted = ToImportedCheckpointInfo(state);
        if (!importedCheckpoints.Values.Any(value => value.ExportId == state.ExportId))
            importedBundleIds.Remove(state.ExportId);
        return true;
    }

    // Main's list_checkpoints handler appends this immutable inventory to its existing
    // rounds/custom response. It must not expose the archive filesystem path.
    private static ImportedCheckpointInfoV1[] BuildImportedCheckpointInventory() => importedCheckpoints.Values
        .OrderBy(value => value.Entry.TimestampUtc)
        .ThenBy(value => value.Entry.CheckpointId, StringComparer.Ordinal)
        .Select(ToImportedCheckpointInfo)
        .ToArray();

    private static bool TryRestoreImportedCheckpoint(BridgeRequestV1 request, out BridgeResultV1 result)
    {
        result = null!;
        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            result = ErrorResult(request, "INVALID_ARGUMENT", "Restore payload must be an object.", false);
            return true;
        }
        string? checkpointId = null;
        string? label = null;
        int? round = null;
        bool seenCheckpointId = false;
        bool seenLabel = false;
        bool seenRound = false;
        foreach (var property in request.Payload.EnumerateObject())
        {
            switch (property.Name)
            {
                case "checkpointId" when property.Value.ValueKind == JsonValueKind.String:
                    if (seenCheckpointId)
                    {
                        result = ErrorResult(request, "INVALID_ARGUMENT", "checkpointId may be specified only once.", false);
                        return true;
                    }
                    checkpointId = property.Value.GetString();
                    seenCheckpointId = true;
                    break;
                case "label" when property.Value.ValueKind == JsonValueKind.String:
                    if (seenLabel)
                    {
                        result = ErrorResult(request, "INVALID_ARGUMENT", "label may be specified only once.", false);
                        return true;
                    }
                    label = property.Value.GetString();
                    seenLabel = true;
                    break;
                case "round" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var parsedRound):
                    if (seenRound)
                    {
                        result = ErrorResult(request, "INVALID_ARGUMENT", "round may be specified only once.", false);
                        return true;
                    }
                    round = parsedRound;
                    seenRound = true;
                    break;
                case "checkpointId":
                case "label":
                case "round":
                    result = ErrorResult(request, "INVALID_ARGUMENT", $"Restore selector '{property.Name}' has the wrong type.", false);
                    return true;
                default:
                    result = ErrorResult(request, "INVALID_ARGUMENT",
                        $"Unknown restore selector '{property.Name}'. Supported selectors are checkpointId, label, and round.", false);
                    return true;
            }
        }
        if ((seenCheckpointId ? 1 : 0) + (seenLabel ? 1 : 0) + (seenRound ? 1 : 0) > 1)
        {
            result = ErrorResult(request, "INVALID_ARGUMENT", "Provide at most one restore selector.", false);
            return true;
        }

        if (seenLabel && !CheckpointLabelValidation.IsValid(label))
        {
            result = ErrorResult(request, "INVALID_ARGUMENT",
                "label must be a non-empty string with at most 128 characters and no control characters.", false);
            return true;
        }

        if (seenRound && (!round.HasValue || round.Value < 1))
        {
            result = ErrorResult(request, "INVALID_ARGUMENT",
                "round must be an integer >= 1.", false);
            return true;
        }

        bool hasSelector = seenCheckpointId || seenLabel || seenRound;
        if (!hasSelector)
            return false;
        if (checkpointId != null && !IsSafeTransferToken(checkpointId))
        {
            result = ErrorResult(request, "INVALID_ARGUMENT", "checkpointId must be a safe opaque token.", false);
            return true;
        }

        ImportedCheckpointState? selected = null;
        if (!string.IsNullOrEmpty(checkpointId))
        {
            if (!importedCheckpoints.TryGetValue(checkpointId, out selected))
            {
                result = ErrorResult(request, "CHECKPOINT_NOT_FOUND", $"Imported checkpoint '{checkpointId}' was not found.", false);
                return true;
            }
        }
        else
        {
            var candidates = importedCheckpoints.Values.AsEnumerable();
            if (label != null)
                candidates = candidates.Where(value => string.Equals(value.Entry.Label, label, StringComparison.OrdinalIgnoreCase));
            if (round.HasValue)
                candidates = candidates.Where(value => value.Entry.Round == round.Value);
            var matches = candidates.ToArray();
            if (matches.Length == 0)
                return false;
            if (matches.Length != 1)
            {
                result = ErrorResult(request, "CHECKPOINT_AMBIGUOUS",
                    "The imported checkpoint selector matches more than one checkpoint; use checkpointId.", false,
                    new { MatchingCheckpointIds = matches.Select(value => value.Entry.CheckpointId).OrderBy(id => id).ToArray() });
                return true;
            }
            selected = matches[0];
        }

        if (selected!.Entry.Label != null && customJsonCheckpoints.ContainsKey(selected.Entry.Label) ||
            roundJsonCheckpoints.ContainsKey(selected.Entry.Round))
        {
            result = ErrorResult(request, "CHECKPOINT_AMBIGUOUS",
                "The selector also matches a live checkpoint; use checkpointId and remove the live collision.", false);
            return true;
        }

        // RestoreCheckpointJson repeats external validation immediately before
        // native deserialization and simulation mutation.
        result = RestoreCheckpointJson(request, selected.Entry.Payload);
        return true;
    }

    private static BridgeResultV1 HandleDeleteImportedCheckpoint(BridgeRequestV1 request)
    {
        if (request.Payload.ValueKind != JsonValueKind.Object ||
            !request.Payload.TryGetProperty("checkpointId", out var idProperty) ||
            idProperty.ValueKind != JsonValueKind.String ||
            !IsSafeTransferToken(idProperty.GetString() ?? ""))
            return ErrorResult(request, "INVALID_ARGUMENT", "checkpointId must be a safe imported checkpoint token.", false);

        string checkpointId = idProperty.GetString()!;
        if (!TryDeleteImportedCheckpoint(checkpointId, out var deleted))
            return ErrorResult(request, "CHECKPOINT_NOT_FOUND", $"Imported checkpoint '{checkpointId}' was not found.", false);

        return SuccessResult(request, new
        {
            Deleted = true,
            Checkpoint = deleted,
            RemainingImportedCount = importedCheckpoints.Count
        });
    }

    // Shared game-thread restore path for both legacy in-memory and imported checkpoints.
    // Main can replace the old native restore body with this helper.
    private static BridgeResultV1 RestoreCheckpointJson(BridgeRequestV1 request, string json, bool trustedInMemory = false)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot restore checkpoint: no active match.", false);
        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        NativePayloadValidationResult validation = ValidateNativePayload(json, null, Application.version,
            rejectTransientState: !trustedInMemory, validateNativeGraph: !trustedInMemory);
        if (validation.Failure != null)
            return ErrorResult(request, validation.Failure.Code, validation.Failure.Message,
                validation.Failure.Retryable, validation.Failure.Details);

        MapSaveDataModel? saveModel;
        try
        {
            var settings = new Il2CppNewtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Il2CppNewtonsoft.Json.TypeNameHandling.Objects
            };
            saveModel = Il2CppNewtonsoft.Json.JsonConvert.DeserializeObject<MapSaveDataModel>(json, settings);
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "CORRUPT_CHECKPOINT", $"Native checkpoint deserialization failed: {ex.Message}", false);
        }
        if (saveModel == null)
            return ErrorResult(request, "CORRUPT_CHECKPOINT", "Native checkpoint deserialized to null.", false);

        try
        {
            if (!saveModel.IsCompatible())
                return ErrorResult(request, "INCOMPATIBLE_CHECKPOINT", "Native MapSaveDataModel rejected the current BTD6 schema.", false);

            var currentGame = InGameData.CurrentGame;
            string currentMap = currentGame?.selectedMap ?? "";
            string currentDifficulty = currentGame?.selectedDifficulty ?? "";
            string currentMode = currentGame?.selectedMode ?? "";
            if (!string.IsNullOrEmpty(currentMap) && !string.Equals(saveModel.mapName, currentMap, StringComparison.Ordinal))
                return ErrorResult(request, "INCOMPATIBLE_MATCH", "Checkpoint map does not match the active match.", false);
            if (!string.IsNullOrEmpty(currentDifficulty) && !string.Equals(saveModel.mapDifficulty, currentDifficulty, StringComparison.Ordinal))
                return ErrorResult(request, "INCOMPATIBLE_MATCH", "Checkpoint difficulty does not match the active match.", false);
            if (!string.IsNullOrEmpty(currentMode) && !string.Equals(
                    CanonicalizeMode(saveModel.modeName), CanonicalizeMode(currentMode), StringComparison.OrdinalIgnoreCase))
                return ErrorResult(request, "INCOMPATIBLE_MATCH", "Checkpoint mode does not match the active match.", false);
            if (!string.IsNullOrEmpty(inGame.MapDataSaveId) && !string.IsNullOrEmpty(saveModel.savedMapsId) &&
                !string.Equals(saveModel.savedMapsId, inGame.MapDataSaveId, StringComparison.Ordinal))
                return ErrorResult(request, "INCOMPATIBLE_MATCH", "Checkpoint map save ID does not match the active match.", false);
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "INCOMPATIBLE_CHECKPOINT", $"Native checkpoint compatibility validation failed: {ex.Message}", false);
        }

        try
        {
            inGame.matchLost = false;
            inGame.waitingForVictoryScreen = false;
            var defeatScreen = inGame.GetComponentInChildren<Il2CppAssets.Scripts.Unity.UI_New.GameOver.DefeatScreen>(true);
            if (defeatScreen != null && defeatScreen.gameObject.activeSelf)
                defeatScreen.Close();
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"Defeat screen dismissal note: {ex.Message}");
        }

        try
        {
            bridge.ExecuteContinueFromCheckpoint(bridge.GetInputId(), new Il2CppAssets.Scripts.Utils.KonFuze(), ref saveModel, true, false);
            if (saveModel != null)
            {
                try { Game.instance.GetPlayerProfile()?.SetSavedMap(saveModel.savedMapsId, saveModel); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "RESTORE_FAILED", $"Native checkpoint restore failed: {ex.Message}", false);
        }

        ResetManagedMatchState();
        ResetNativeMatchTime();
        int restoredRound;
        try { restoredRound = bridge.GetCurrentRound() + 1; }
        catch { restoredRound = validation.Info?.Round ?? 0; }
        return SuccessResult(request, new
        {
            Restored = true,
            CheckpointId = FindImportedCheckpointId(json),
            Round = restoredRound,
            Cash = inGame.GetCash(),
            Health = inGame.GetHealth(),
            SelectedHero = Game.instance?.GetPlayerProfile()?.primaryHero
        });
    }

    private static string? FindImportedCheckpointId(string payload) => importedCheckpoints.Values
        .Where(value => ReferenceEquals(value.Entry.Payload, payload) || string.Equals(value.Entry.Payload, payload, StringComparison.Ordinal))
        .Select(value => value.Entry.CheckpointId)
        .FirstOrDefault();

    private static NativePayloadValidationResult ValidateNativePayload(string json, int? expectedRound,
        string expectedGameVersion, bool rejectTransientState, bool validateNativeGraph = true)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxCheckpointPayloadBytes)
            return NativeFailure("CHECKPOINT_TOO_LARGE", $"Native checkpoint payload exceeds {MaxCheckpointPayloadBytes} bytes.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxNativeJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return NativeFailure("CORRUPT_CHECKPOINT", "Native checkpoint root must be an object.");
            if (validateNativeGraph)
            {
                if (!root.TryGetProperty("$type", out var rootType) ||
                    rootType.ValueKind != JsonValueKind.String ||
                    !string.Equals(rootType.GetString(), NativeMapSaveType, StringComparison.Ordinal))
                    return NativeFailure("UNSUPPORTED_NATIVE_TYPE", "Native checkpoint root $type is not the exact MapSaveDataModel type.");

                int nodeCount = 0;
                if (!ValidateNativeTypeGraph(root, 0, ref nodeCount, out string? graphError))
                    return NativeFailure("UNSUPPORTED_NATIVE_TYPE", graphError!);
            }

            if (!TryReadInt(root, "version", out int modelVersion) || modelVersion != SupportedNativeMapModelVersion ||
                !TryReadInt(root, "round", out int round) || round < 1 || round > 10_000)
                return NativeFailure("CORRUPT_CHECKPOINT", $"Native checkpoint version must be {SupportedNativeMapModelVersion} and round must be valid.");
            if (expectedRound.HasValue && round != expectedRound.Value)
                return NativeFailure("CORRUPT_CHECKPOINT", $"Native checkpoint round {round} does not match expected round {expectedRound.Value}.");

            string gameVersion = ReadJsonString(root, "gameVersion");
            string mapName = ReadJsonString(root, "mapName");
            string mapDifficulty = ReadJsonString(root, "mapDifficulty");
            string modeName = ReadJsonString(root, "modeName");
            string gameType = root.TryGetProperty("gameType", out var gameTypeProperty)
                ? gameTypeProperty.GetRawText() : "";
            if (string.IsNullOrEmpty(gameVersion) || !string.Equals(gameVersion, expectedGameVersion, StringComparison.Ordinal))
                return NativeFailure("INCOMPATIBLE_GAME_VERSION",
                    $"Native checkpoint game version '{gameVersion}' does not match '{expectedGameVersion}'.");
            if (string.IsNullOrEmpty(mapName) || string.IsNullOrEmpty(mapDifficulty))
                return NativeFailure("CORRUPT_CHECKPOINT", "Native checkpoint map metadata is missing.");

            // Boundary saves may retain persistent projectiles and map removables.
            // Provenance guards capture timing; only active bloons contradict that fidelity.
            if (rejectTransientState && !TryRequireEmptyArray(root, "bloons", out string? transientError))
                return NativeFailure("UNSUPPORTED_FIDELITY", transientError!);

            return new NativePayloadValidationResult
            {
                Info = new NativePayloadInfo
                {
                    ModelVersion = modelVersion,
                    Round = round,
                    GameVersion = gameVersion,
                    MapName = mapName,
                    MapDifficulty = mapDifficulty,
                    ModeName = modeName,
                    GameType = gameType
                }
            };
        }
        catch (JsonException ex)
        {
            return NativeFailure("CORRUPT_CHECKPOINT", $"Native checkpoint JSON is invalid: {ex.Message}");
        }
        catch (Exception ex)
        {
            return NativeFailure("CORRUPT_CHECKPOINT", $"Native checkpoint validation failed: {ex.Message}");
        }
    }

    private static NativePayloadValidationResult NativeFailure(string code, string message) => new()
    {
        Failure = new TransferFailure { Code = code, Message = message, Retryable = false }
    };

    private static bool ValidateNativeTypeGraph(JsonElement value, int depth, ref int nodeCount, out string? error)
    {
        error = null;
        if (++nodeCount > MaxNativeJsonNodes)
        {
            error = "Native checkpoint JSON has too many values.";
            return false;
        }
        if (depth > MaxNativeJsonDepth)
        {
            error = "Native checkpoint JSON is too deeply nested.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    error = $"Native checkpoint contains duplicate property '{property.Name}'.";
                    return false;
                }
                if (property.Name is "$ref" or "$id" or "$values")
                {
                    error = $"Native checkpoint metadata property '{property.Name}' is not permitted.";
                    return false;
                }
                if (property.Name == "$type")
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "Native checkpoint $type must be a string.";
                        return false;
                    }
                    string typeName = property.Value.GetString() ?? "";
                    if (!AllowedNativeSaveTypes.Contains(typeName))
                    {
                        error = $"Native checkpoint type '{typeName}' is not in the exact BTD6 save allowlist.";
                        return false;
                    }
                }
                if (!ValidateNativeTypeGraph(property.Value, depth + 1, ref nodeCount, out error))
                    return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (!ValidateNativeTypeGraph(item, depth + 1, ref nodeCount, out error))
                    return false;
        }
        return true;
    }

    private static bool TryRequireEmptyArray(JsonElement root, string propertyName, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            error = $"Native checkpoint property '{propertyName}' is missing or not an array.";
            return false;
        }
        if (property.GetArrayLength() != 0)
        {
            error = $"Native checkpoint property '{propertyName}' contains active simulation objects.";
            return false;
        }
        return true;
    }

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static string ReadJsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    private static bool IsRegularTransferPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRegularTransferDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return Directory.Exists(path) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildOpaqueExportId(string requestId) =>
        "exp_" + ComputeSha256(requestId).Substring(0, 32);

    private static string BuildOpaqueCheckpointId(string exportId, string source, string? label, int round, int ordinal) =>
        "cp_" + ComputeSha256($"{exportId}|{source}|{label ?? ""}|{round}|{ordinal}").Substring(0, 32);

    private static bool IsSafeTransferLabel(string value) => CheckpointLabelValidation.IsValid(value);

    private static string ComputeSha256(string value) => ComputeSha256(Encoding.UTF8.GetBytes(value));

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsSafeTransferToken(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');


    private static bool IsBoundedTransferString(string? value, int maxLength) =>
        value == null || value.Length <= maxLength && value.All(character => !char.IsControl(character));

    private static CheckpointMetadataV1 ToCheckpointMetadata(CheckpointTransferEntryV1 entry) => new()
    {
        CheckpointId = entry.CheckpointId,
        Label = entry.Label,
        Round = entry.Round,
        TimestampUtc = entry.TimestampUtc,
        Source = entry.Source,
        Fidelity = entry.Fidelity
    };

    private static ImportedCheckpointInfoV1 ToImportedCheckpointInfo(ImportedCheckpointState state) => new()
    {
        ExportId = state.ExportId,
        CheckpointId = state.Entry.CheckpointId,
        Label = state.Entry.Label,
        Round = state.Entry.Round,
        TimestampUtc = state.Entry.TimestampUtc,
        Source = state.Entry.Source,
        Fidelity = state.Entry.Fidelity
    };

    private sealed class CheckpointExportResultV1
    {
        public bool Exported { get; init; }
        public string ExportId { get; init; } = "";
        public string Path { get; init; } = "";
        public CheckpointMetadataV1[] Checkpoints { get; init; } = Array.Empty<CheckpointMetadataV1>();
        public string Sha256 { get; init; } = "";
        public long Bytes { get; init; }
        public bool Idempotent { get; init; }
    }

    private sealed class CheckpointImportResultV1
    {
        public bool Imported { get; init; }
        public string ExportId { get; init; } = "";
        public CheckpointMetadataV1[] Checkpoints { get; init; } = Array.Empty<CheckpointMetadataV1>();
    }

    private sealed class CheckpointMetadataV1
    {
        public string CheckpointId { get; init; } = "";
        public string? Label { get; init; }
        public int Round { get; init; }
        public DateTime TimestampUtc { get; init; }
        public string Source { get; init; } = "";
        public string Fidelity { get; init; } = "";
    }

    private sealed class ImportedCheckpointInfoV1
    {
        public string ExportId { get; init; } = "";
        public string CheckpointId { get; init; } = "";
        public string? Label { get; init; }
        public int Round { get; init; }
        public DateTime TimestampUtc { get; init; }
        public string Source { get; init; } = "";
        public string Fidelity { get; init; } = "";
    }

    private sealed class CheckpointExportBundleV1
    {
        public int Version { get; init; }
        public string ExportId { get; init; } = "";
        public string RequestId { get; init; } = "";
        public DateTime CreatedAtUtc { get; init; }
        public int ProtocolVersion { get; init; }
        public string BridgeVersion { get; init; } = "";
        public string ModuleVersionId { get; init; } = "";
        public string Btd6Version { get; init; } = "";
        public CheckpointNativeIdentityV1 Native { get; init; } = new();
        public CheckpointMatchV1 Match { get; init; } = new();
        public List<CheckpointTransferEntryV1> Checkpoints { get; init; } = new();
    }

    private sealed class CheckpointNativeIdentityV1
    {
        public string Schema { get; init; } = "";
        public int ModelVersion { get; init; }
        public string RootType { get; init; } = "";
        public string Assembly { get; init; } = "";
        public string GameVersion { get; init; } = "";
    }

    private sealed class CheckpointMatchV1
    {
        public string Map { get; init; } = "";
        public string Difficulty { get; init; } = "";
        public string Mode { get; init; } = "";
        public string? Hero { get; init; }
        public string? OriginalMatchId { get; init; }
        public CheckpointRulesV1 Rules { get; init; } = new();
    }

    private sealed class CheckpointRulesV1
    {
        public string GameType { get; init; } = "";
        public bool IsChimps { get; init; }
        public bool IsSandbox { get; init; }
        public bool IsImpoppable { get; init; }
    }

    private sealed class CheckpointTransferEntryV1
    {
        public string CheckpointId { get; init; } = "";
        public string? Label { get; init; }
        public int Round { get; init; }
        public DateTime TimestampUtc { get; init; }
        public string Source { get; init; } = "";
        public string Payload { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public string Fidelity { get; init; } = "";
    }
}
