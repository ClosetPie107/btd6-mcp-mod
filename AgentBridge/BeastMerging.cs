using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Simulation.Towers;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using NativeBeastHandlerLeash = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.BeastHandlerLeash;
using NativeTowerBehavior = Il2CppAssets.Scripts.Simulation.Towers.TowerBehavior;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int BeastMergePathCount = 3;
    private const int MaxBeastMergeCandidates = 64;
    private const int MaxBeastMergeValidationCandidates = 4096;


    private sealed class BeastHandlerRef
    {
        public required TowerToSimulation Tower { get; init; }
        public required NativeBeastHandlerLeash Leash { get; init; }
    }

    private sealed class BeastMergeReadback
    {
        public int? Power { get; init; }
        public float? PowerPercent { get; init; }
        public int? CurrentContributions { get; init; }
        public bool? LostThroughContribution { get; init; }
    }

    private static TowerToSimulation? FindBeastTower(InGame inGame, string towerId)
    {
        var towers = inGame.GetAllTowerToSim();
        if (towers == null) return null;
        for (int i = 0; i < towers.Count; i++)
        {
            var tower = towers[i];
            if (tower != null && tower.Id.ToString() == towerId) return tower;
        }
        return null;
    }

    private static bool TryGetBeastLeash(TowerToSimulation tower, out NativeBeastHandlerLeash? leash)
    {
        leash = null;
        try
        {
            if (tower.Def?.baseId != "BeastHandler") return false;
            var simTower = tower.GetSimTower();
            var behaviors = simTower?.Behaviors?.list;
            if (behaviors == null) return false;
            for (int i = 0; i < behaviors.Count; i++)
            {
                var candidate = behaviors[i]?.TryCast<NativeBeastHandlerLeash>();
                if (candidate != null)
                {
                    leash = candidate;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static List<BeastHandlerRef> GetBeastHandlers(InGame inGame)
    {
        var result = new List<BeastHandlerRef>();
        var towers = inGame.GetAllTowerToSim();
        if (towers == null) return result;
        for (int i = 0; i < towers.Count && result.Count < MaxBeastMergeCandidates; i++)
        {
            var tower = towers[i];
            if (tower == null || !TryGetBeastLeash(tower, out var leash) || leash == null) continue;
            result.Add(new BeastHandlerRef { Tower = tower, Leash = leash });
        }
        return result;
    }

    private static string? FindBeastHandlerId(IReadOnlyList<BeastHandlerRef> handlers, NativeBeastHandlerLeash? leash)
    {
        if (leash == null) return null;
        IntPtr pointer;
        try { pointer = leash.Pointer; }
        catch { return null; }
        for (int i = 0; i < handlers.Count; i++)
        {
            try
            {
                if (handlers[i].Leash.Pointer == pointer) return handlers[i].Tower.Id.ToString();
            }
            catch { }
        }
        return null;
    }

    private static string BeastIdForPath(int path) =>
        Il2CppAssets.Scripts.Models.Towers.Behaviors.BeastHandlerLeashModel.beastTowerTypeByPath[path];

    private static string? BeastMergeButtonForPath(TowerToSimulation tower, int path) =>
        tower.beastHandlerToSimulation?.GetBeastIndex(BeastIdForPath(path)) switch
        {
            // Native TSM merge buttons address the primary/secondary active pet,
            // not the upgrade path number or the beast base ID.
            0 => "MergeButton",
            1 => "MergeButtonSecond",
            _ => null
        };

    private static bool TryReadBeastPath(JsonElement payload, out int path)
    {
        path = -1;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("path", out var pathValue)
            && pathValue.ValueKind == JsonValueKind.Number
            && pathValue.TryGetInt32(out path)
            && path is >= 0 and < BeastMergePathCount;
    }

    private static BeastMergeReadback ReadBeastMergeState(NativeBeastHandlerLeash leash, string beastId)
    {
        int? power = null;
        float? powerPercent = null;
        int? contributions = null;
        bool? lost = null;
        try { power = leash.GetTotalPowerForBeast(beastId); }
        catch { }
        try
        {
            var value = leash.GetPowerPercentForBeast(beastId);
            if (float.IsFinite(value)) powerPercent = value;
        }
        catch { }
        try { contributions = leash.GetCurrentContributionsByBeast(beastId, -1); }
        catch { }
        try { lost = leash.IsBeastLostThroughContribution(beastId); }
        catch { }
        return new BeastMergeReadback
        {
            Power = power,
            PowerPercent = powerPercent,
            CurrentContributions = contributions,
            LostThroughContribution = lost
        };
    }

    private static object ReadBeastInput(
        TowerToSimulation tower,
        NativeBeastHandlerLeash leash,
        int path,
        IReadOnlyList<BeastHandlerRef> handlers)
    {
        string beastId = BeastIdForPath(path);
        string? inputClass = null;
        bool? inputAvailable = null;
        List<string>? validRecipientTowerIds = null;
        try
        {
            string? buttonId = BeastMergeButtonForPath(tower, path);
            var inputBehavior = buttonId == null ? null : tower.GetCustomInputTowerBehavior(buttonId);
            inputClass = inputBehavior?.GetTowerBehaviorCustomInputClass(buttonId);
            inputAvailable = inputBehavior != null
                && inputBehavior.Pointer == leash.Pointer
                && !string.IsNullOrEmpty(inputClass)
                && inputClass.EndsWith("BeastHandlerMergeInput", StringComparison.Ordinal);
            var data = buttonId == null ? null : tower.GetCustomInputDataFromTowerBehaviors(buttonId)?.TryCast<BeastHandlerCIData>();
            var ids = data?.validTowerIds;
            if (ids != null)
            {
                validRecipientTowerIds = new List<string>(Math.Min(MaxBeastMergeCandidates, ids.Count));
                for (int i = 0; i < ids.Count && validRecipientTowerIds.Count < MaxBeastMergeCandidates; i++)
                {
                    string id = ids[i].ToString();
                    if (!validRecipientTowerIds.Contains(id)) validRecipientTowerIds.Add(id);
                }
            }
        }
        catch
        {
            inputAvailable = null;
        }

        int? power = null;
        float? powerPercent = null;
        int? currentContributions = null;
        bool? lostThroughContribution = null;
        try { power = leash.GetTotalPowerForBeast(beastId); }
        catch { }
        try
        {
            var value = leash.GetPowerPercentForBeast(beastId);
            if (float.IsFinite(value)) powerPercent = value;
        }
        catch { }
        try { currentContributions = leash.GetCurrentContributionsByBeast(beastId, -1); }
        catch { }
        try { lostThroughContribution = leash.IsBeastLostThroughContribution(beastId); }
        catch { }

        string? currentBeastTowerId = null;
        try { currentBeastTowerId = leash.GetMatchingBeast(beastId, -1)?.Id.ToString(); }
        catch { }

        string? recipientTowerId = null;
        try { recipientTowerId = FindBeastHandlerId(handlers, leash.GetBeastHandlerContributingTowards(beastId)); }
        catch { }

        var donorTowerIds = new List<string>(8);
        try
        {
            var contributions = leash.contributingHandlersByBeast;
            if (contributions != null && contributions.TryGetValue(beastId, out var donors) && donors != null)
            {
                foreach (var donor in donors)
                {
                    if (donorTowerIds.Count >= MaxBeastMergeCandidates) break;
                    var donorId = FindBeastHandlerId(handlers, donor);
                    if (donorId != null && !donorTowerIds.Contains(donorId)) donorTowerIds.Add(donorId);
                }
            }
        }
        catch { }
        bool? nativeCanMerge = null;
        try { nativeCanMerge = tower.beastHandlerToSimulation?.CanBeastHandlerMergeBeast(beastId); }
        catch { }
        bool? validTowerExistsForMerge = null;
        try { validTowerExistsForMerge = leash.DoesValidTowerExistForMerge(beastId); }
        catch { }

        return new
        {
            Path = path,
            BeastId = beastId,
            Power = power,
            PowerPercent = powerPercent,
            CurrentBeastTowerId = currentBeastTowerId,
            RecipientTowerId = recipientTowerId,
            DonorTowerIds = donorTowerIds,
            CurrentContributions = currentContributions,
            LostThroughContribution = lostThroughContribution,
            NativeCanMerge = nativeCanMerge,
            ValidTowerExistsForMerge = validTowerExistsForMerge,
            NativeInputAvailable = inputAvailable,
            NativeInputClass = inputClass,
            ValidRecipientTowerIds = validRecipientTowerIds
        };
    }

    private static bool TryGetBeastMergeInput(
        TowerToSimulation tower,
        NativeBeastHandlerLeash leash,
        int path,
        out NativeTowerBehavior? behavior,
        out BeastHandlerCIData? data,
        out string? error)
    {
        behavior = null;
        data = null;
        error = null;
        string? buttonId = BeastMergeButtonForPath(tower, path);
        if (buttonId == null) { error = "BEAST_MERGE_INPUT_UNAVAILABLE"; return false; }
        try
        {
            behavior = tower.GetCustomInputTowerBehavior(buttonId);
            if (behavior == null || behavior.Pointer != leash.Pointer)
            {
                error = "BEAST_MERGE_INPUT_UNAVAILABLE";
                return false;
            }
            string inputClass = behavior.GetTowerBehaviorCustomInputClass(buttonId);
            if (string.IsNullOrEmpty(inputClass) || !inputClass.EndsWith("BeastHandlerMergeInput", StringComparison.Ordinal))
            {
                error = "BEAST_MERGE_INPUT_UNAVAILABLE";
                return false;
            }
            data = tower.GetCustomInputDataFromTowerBehaviors(buttonId)?.TryCast<BeastHandlerCIData>();
            if (data?.validTowerIds == null)
            {
                error = "BEAST_MERGE_INPUT_UNAVAILABLE";
                return false;
            }
            return true;
        }
        catch
        {
            error = "BEAST_MERGE_INPUT_UNAVAILABLE";
            return false;
        }
    }

    private static bool NativeCandidateContains(BeastHandlerCIData data, string towerId, out bool overflow)
    {
        overflow = false;
        try
        {
            var ids = data.validTowerIds;
            if (ids == null) return false;
            if (ids.Count > MaxBeastMergeValidationCandidates)
            {
                overflow = true;
                return false;
            }
            for (int i = 0; i < ids.Count; i++)
                if (ids[i].ToString() == towerId) return true;
        }
        catch { }
        return false;
    }

    private static bool BeastMergeReadbackChanged(BeastMergeReadback donorBefore, BeastMergeReadback recipientBefore,
        BeastMergeReadback donorAfter, BeastMergeReadback recipientAfter)
    {
        return donorBefore.LostThroughContribution != donorAfter.LostThroughContribution
            || donorBefore.CurrentContributions != donorAfter.CurrentContributions
            || donorBefore.Power != donorAfter.Power
            || recipientBefore.CurrentContributions != recipientAfter.CurrentContributions
            || recipientBefore.Power != recipientAfter.Power;
    }

    private static BridgeResultV1 HandleInspectBeastMerges(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot inspect Beast Handler merges without an active match.", false);

        string towerId = ReadString(request.Payload, "towerId", "").Trim();
        if (towerId.Length == 0 || towerId.Length > 128)
            return ErrorResult(request, "INVALID_TOWER_ID", "towerId must be a non-empty string of at most 128 characters.", false);

        var tower = FindBeastTower(inGame, towerId);
        if (tower == null || tower.Def == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);
        if (!TryGetBeastLeash(tower, out var leash) || leash == null)
            return ErrorResult(request, "NOT_BEAST_HANDLER", $"Tower '{towerId}' is not a Beast Handler.", false);

        var handlers = GetBeastHandlers(inGame);
        var paths = new List<object>(BeastMergePathCount);
        for (int path = 0; path < BeastMergePathCount; path++)
            paths.Add(ReadBeastInput(tower, leash, path, handlers));
        int[]? tiers = null;
        try { tiers = tower.Def.tiers?.Take(3).ToArray(); }
        catch { }
        return SuccessResult(request, new
        {
            Source = "active-simulation",
            ObservedAtUtc = DateTime.UtcNow,
            TowerId = tower.Id.ToString(),
            Owner = tower.owner,
            Tiers = tiers,
            Paths = paths
        });
    }

    private static BridgeResultV1 HandleMergeBeast(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot merge Beast Handler beasts without an active match.", false);
        if (request.Payload.ValueKind != JsonValueKind.Object)
            return ErrorResult(request, "INVALID_ARGUMENT", "The merge payload must be an object.", false);
        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "The active game bridge is unavailable.", true);
        int inputId;
        try { inputId = bridge.GetInputId(); }
        catch { return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "The active game input identity is unavailable.", true); }

        string donorId = ReadString(request.Payload, "sourceTowerId", "").Trim();
        string recipientId = ReadString(request.Payload, "targetTowerId", "").Trim();
        if (donorId.Length == 0 || donorId.Length > 128 || recipientId.Length == 0 || recipientId.Length > 128)
            return ErrorResult(request, "INVALID_TOWER_ID", "sourceTowerId and targetTowerId must be non-empty strings of at most 128 characters.", false);
        if (donorId == recipientId)
            return ErrorResult(request, "INVALID_MERGE", "A Beast Handler cannot merge into itself.", false);
        if (!TryReadBeastPath(request.Payload, out int path))
            return ErrorResult(request, "INVALID_PATH", "path must be an integer from 0 to 2.", false);

        var donor = FindBeastTower(inGame, donorId);
        var recipient = FindBeastTower(inGame, recipientId);
        if (donor == null || donor.Def == null)
            return ErrorResult(request, "DONOR_NOT_FOUND", $"Donor tower '{donorId}' was not found.", false);
        if (recipient == null || recipient.Def == null)
            return ErrorResult(request, "RECIPIENT_NOT_FOUND", $"Recipient tower '{recipientId}' was not found.", false);
        if (donor.owner != inputId || recipient.owner != inputId)
            return ErrorResult(request, "TOWER_NOT_OWNED", "Both Beast Handlers must be owned by the active player.", false);
        if (!TryGetBeastLeash(donor, out var donorLeash) || donorLeash == null
            || !TryGetBeastLeash(recipient, out var recipientLeash) || recipientLeash == null)
            return ErrorResult(request, "NOT_BEAST_HANDLER", "Both towers must be Beast Handlers.", false);

        if (!TryGetBeastMergeInput(donor, donorLeash, path, out var behavior, out var data, out var inputError))
            return ErrorResult(request, inputError ?? "BEAST_MERGE_INPUT_UNAVAILABLE", "The native Beast Handler merge input is unavailable.", false);
        if (behavior == null || data == null)
            return ErrorResult(request, "BEAST_MERGE_INPUT_UNAVAILABLE", "The native Beast Handler merge input is unavailable.", false);
        bool candidateOverflow;
        if (!NativeCandidateContains(data, recipient.Id.ToString(), out candidateOverflow))
        {
            if (candidateOverflow)
                return ErrorResult(request, "NATIVE_CANDIDATES_TOO_LARGE", "The native Beast Handler candidate list exceeded the validation bound.", true);
            return ErrorResult(request, "INVALID_MERGE_TARGET", "The native Beast Handler candidate list does not allow this recipient for the requested donor path.", false);
        }

        string beastId = BeastIdForPath(path);
        bool nativeCanMerge;
        try
        {
            if (donor.beastHandlerToSimulation == null)
                return ErrorResult(request, "BEAST_MERGE_INPUT_UNAVAILABLE", "Native Beast Handler bridge state is unavailable.", false);
            nativeCanMerge = donor.beastHandlerToSimulation.CanBeastHandlerMergeBeast(beastId);
        }
        catch
        {
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "Native Beast Handler merge state is unavailable.", true);
        }
        if (!nativeCanMerge)
            return ErrorResult(request, "MERGE_NOT_ALLOWED", "The native Beast Handler does not allow a merge for this path.", false);

        var donorBefore = ReadBeastMergeState(donorLeash, beastId);
        var recipientBefore = ReadBeastMergeState(recipientLeash, beastId);
        var customInput = new CustomInputData();
        customInput.objectIdValue = recipient.Id;
        customInput.towerBehaviorObjectId = behavior.Id;
        customInput.buttonId = BeastMergeButtonForPath(donor, path);

        bool previousImmediateMode = bridge.IsImmediateMode;
        try
        {
            bridge.IsImmediateMode = true;
            // ApplyCustomInputData is the same UnityToSimulation action used by the
            // vanilla BeastHandlerMergeInput CursorUp path; it preserves all native
            // ownership, range, tier/capacity, and contribution-chain validation.
            donor.ApplyCustomInputData(inputId, customInput);
        }
        catch (Exception ex)
        {
            BTD_Mod_Helper.ModHelper.Warning<AgentBridgeMod>($"Beast Handler merge failed: {ex}");
            return ErrorResult(request, "BEAST_MERGE_FAILED", "The native Beast Handler merge action failed.", false);
        }
        finally { bridge.IsImmediateMode = previousImmediateMode; }

        var donorAfter = ReadBeastMergeState(donorLeash, beastId);
        var recipientAfter = ReadBeastMergeState(recipientLeash, beastId);
        if (!BeastMergeReadbackChanged(donorBefore, recipientBefore, donorAfter, recipientAfter))
        {
            return ErrorResult(request, "MERGE_NOT_APPLIED", "The native merge action produced no observable Beast Handler state change.", false,
                new { SourceTowerId = donorId, TargetTowerId = recipientId, Path = path, BeastId = beastId, Before = new { Donor = donorBefore, Recipient = recipientBefore }, After = new { Donor = donorAfter, Recipient = recipientAfter } });
        }

        NativeBeastHandlerLeash? donorAfterRecipient = null;
        bool donorRecipientResolved = false;
        try
        {
            donorAfterRecipient = donorLeash.GetBeastHandlerContributingTowards(beastId);
            donorRecipientResolved = donorAfterRecipient != null
                && donorAfterRecipient.Pointer == recipientLeash.Pointer;
        }
        catch { }
        if (!donorRecipientResolved)
        {
            return ErrorResult(request, "MERGE_READBACK_FAILED", "The native merge action changed state but did not resolve the donor to the requested recipient.", false,
                new
                {
                    SourceTowerId = donorId,
                    TargetTowerId = recipientId,
                    Path = path,
                    BeastId = beastId,
                    RecipientResolved = false,
                    Before = new { Donor = donorBefore, Recipient = recipientBefore },
                    After = new { Donor = donorAfter, Recipient = recipientAfter }
                });
        }

        return SuccessResult(request, new
        {
            Source = "active-simulation",
            ObservedAtUtc = DateTime.UtcNow,
            SourceTowerId = donorId,
            TargetTowerId = recipientId,
            DonorTowerId = donorId,
            RecipientTowerId = recipientId,
            Path = path,
            BeastId = beastId,
            Before = new { Donor = donorBefore, Recipient = recipientBefore },
            After = new { Donor = donorAfter, Recipient = recipientAfter },
            NativeRecipientTowerId = recipientId,
            NativeDonorRecipientTowerId = recipientId
        });
    }
}
