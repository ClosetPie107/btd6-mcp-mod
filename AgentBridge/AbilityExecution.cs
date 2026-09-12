using System;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Abilities;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NativeAbilities = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Abilities.Behaviors;
using SimVector2 = Il2CppAssets.Scripts.Simulation.SMath.Vector2;
using UnityVector2 = UnityEngine.Vector2;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static bool TryReadAbilitySelector(JsonElement payload, out string? id, out int index)
    {
        id = null;
        index = -1;
        if (payload.ValueKind != JsonValueKind.Object || payload.TryGetProperty("index", out _)) return false;
        bool hasId = payload.TryGetProperty("abilityId", out var idValue);
        bool hasIndex = payload.TryGetProperty("abilityIndex", out var indexValue);
        if (hasId && hasIndex) return false;
        if (hasId)
        {
            if (idValue.ValueKind != JsonValueKind.String) return false;
            id = idValue.GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        }
        return !hasIndex || (indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out index) && index >= 0);
    }

    private static string AbilityId(AbilityToSimulation ability) => ability.ability.Id.ToString();

    private static AbilityToSimulation? ResolveAbility(InGame inGame, string? id, int index, out string? error, out int resolvedIndex)
    {
        error = null;
        resolvedIndex = -1;
        var abilities = inGame.GetAbilities();
        if (abilities == null || abilities.Count == 0)
        {
            error = "NO_ABILITIES";
            return null;
        }
        if (index >= 0)
        {
            if (index < abilities.Count && abilities[index]?.ability != null)
            {
                resolvedIndex = index;
                return abilities[index];
            }
            error = "ABILITY_NOT_FOUND";
            return null;
        }
        if (id == null)
        {
            if (abilities[0]?.ability != null)
            {
                resolvedIndex = 0;
                return abilities[0];
            }
            error = "ABILITY_NOT_FOUND";
            return null;
        }
        // Exact simulation identity wins over a coincidentally matching name.
        for (int i = 0; i < abilities.Count; i++)
            if (abilities[i]?.ability != null && AbilityId(abilities[i]) == id)
            {
                resolvedIndex = i;
                return abilities[i];
            }
        AbilityToSimulation? named = null;
        for (int i = 0; i < abilities.Count; i++)
        {
            var ability = abilities[i];
            if (ability?.ability == null) continue;
            if (!string.Equals(ability.model?.name, id, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ability.model?.displayName, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (named != null)
            {
                error = "AMBIGUOUS_ABILITY";
                return null;
            }
            named = ability;
            resolvedIndex = i;
        }
        if (named == null) error = "ABILITY_NOT_FOUND";
        return named;
    }

    private static AbilityTargetingV1 DescribeAbilityTargeting(AbilityToSimulation ability)
    {
        try
        {
            string? inputClass = ability.GetCustomInputClass();
            if (string.IsNullOrEmpty(inputClass)) inputClass = ability.GetCustomInputClass(false);
            if (string.IsNullOrEmpty(inputClass)) return new AbilityTargetingV1 { Kind = "none", Supported = true };
            string shortName = inputClass[(inputClass.LastIndexOf('.') + 1)..];
            string kind = shortName switch
            {
                "SubTowerDeployInput" or "DeployInput" or "RepositionTowerInput" or "SelectPointInput" => "point",
                "OverclockInput" or "BloodSacrificeInput" or "DoorGunnerInput" or "TechBotInput" or "TechBotUnlinkInput" => "tower",
                "RedeployInput" => "tower_position",
                "ParagonCarpetBombSelectMultiPoint" => "points",
                _ => "unsupported"
            };
            return new AbilityTargetingV1
            {
                Kind = kind,
                InputClass = inputClass,
                Supported = kind != "unsupported",
                PointCount = kind == "points" ? ability.GetCustomInputData()?.TryCast<SelectTargetCIData>()?.numberOfPoints : null
            };
        }
        catch
        {
            // Failure to inspect a native input must never look like an untargeted ability.
            return new AbilityTargetingV1();
        }
    }

    private static bool IsPointWithinMap(UnityToSimulation bridge, float x, float y)
    {
        var map = bridge.Simulation?.Map;
        if (map == null) return false;
        var point = map.GetPointWithinMap(new SimVector2(x, y));
        return Math.Abs(point.x - x) < 0.01f && Math.Abs(point.y - y) < 0.01f;
    }

    private static T? AbilityBehavior<T>(AbilityToSimulation ability) where T : AbilityBehavior
    {
        var behaviors = ability.ability.createdBehaviors;
        if (behaviors != null)
            foreach (var behavior in behaviors)
            {
                var typed = behavior?.TryCast<T>();
                if (typed != null) return typed;
            }
        return null;
    }

    private static Il2CppSystem.Collections.Generic.List<ObjectId>? ValidAbilityTowerIds(Il2CppSystem.Object? data)
    {
        if (data == null) return null;
        return data.TryCast<OverclockCIData>()?.validTowerIds
            ?? data.TryCast<BloodSacrificeCIData>()?.validTowerIds
            ?? data.TryCast<RedeployCIData>()?.validTowerIds
            ?? data.TryCast<DoorGunnerCIData>()?.validTowerIds
            ?? data.TryCast<TechBotCIData>()?.validTowerIds;
    }

    private static string? ValidateAbilityTarget(UnityToSimulation bridge, AbilityToSimulation ability,
        AbilityTargetV1? target, out CustomInputData? nativeTarget, bool prepareNativeTarget = true)
    {
        nativeTarget = null;
        var targeting = DescribeAbilityTargeting(ability);
        if (!targeting.Supported) return "UNSUPPORTED_ABILITY_INPUT";
        if (targeting.Kind == "none") return target == null ? null : "UNEXPECTED_ABILITY_TARGET";
        if (target == null) return "ABILITY_TARGET_REQUIRED";
        if (target.Kind != targeting.Kind) return "ABILITY_TARGET_KIND_MISMATCH";

        var data = ability.GetCustomInputData();
        var native = prepareNativeTarget ? new CustomInputData() : null;
        TowerToSimulation? targetTower = null;
        if (target.TowerId != null)
        {
            var validIds = ValidAbilityTowerIds(data);
            if (validIds == null) return "UNSUPPORTED_ABILITY_INPUT";
            foreach (var validId in validIds)
            {
                if (validId.ToString() != target.TowerId) continue;
                targetTower = bridge.GetTower(validId);
                if (targetTower?.GetSimTower() != null && native != null) native.objectIdValue = validId;
                break;
            }
            if (targetTower == null || targetTower.GetSimTower() == null) return "INVALID_ABILITY_TARGET";
        }
        if (target.Kind == "point" || target.Kind == "tower_position")
        {
            float x = target.X!.Value, y = target.Y!.Value;
            if (!IsPointWithinMap(bridge, x, y)) return "INVALID_ABILITY_TARGET";
            var point = new UnityVector2(x, y);
            bool valid;
            if (targetTower != null)
            {
                // Native placement excludes the relocated tower's current footprint.
                valid = bridge.CanPlaceTowerAt(point, targetTower.Def, bridge.GetInputId(), targetTower.Id);
            }
            else if (data?.TryCast<TowerModel>() is { } placementModel)
            {
                // PlaceProjectileAt supplies its own mock model, including terrain and radius.
                valid = bridge.CanPlaceTowerAt(point, placementModel, bridge.GetInputId(), ObjectId.Invalid);
            }
            else if (data?.TryCast<RepositionTowerCIData>() is { } reposition)
            {
                var movingTower = bridge.GetTower(reposition.towerId);
                if (movingTower == null) return "INVALID_ABILITY_TARGET";
                var position = movingTower.simPosition;
                double dx = (double)x - position.x, dy = (double)y - position.y;
                valid = (reposition.restrictPlacementRadius <= 0 || dx * dx + dy * dy <= (double)reposition.restrictPlacementRadius * reposition.restrictPlacementRadius)
                    && bridge.CanPlaceTowerAt(point, movingTower.Def, bridge.GetInputId(), movingTower.Id);
            }
            else if (data?.TryCast<DeployCIData>() != null || data?.TryCast<SelectTargetCIData>() != null)
            {
                if (data?.TryCast<DeployCIData>() is { } deployment)
                {
                    // DeployInput adds range/context restrictions; the placement
                    // system separately checks its supplied model's terrain/footprint.
                    var model = deployment.towerModelToDeploy;
                    if (model == null) return "UNSUPPORTED_ABILITY_INPUT";
                    if (!bridge.CanPlaceTowerAt(point, model, bridge.GetInputId(), deployment.towerId))
                        return "INVALID_ABILITY_TARGET";
                }
                InputManager? manager = null;
                foreach (var context in InGame.instance.playerContexts)
                    if (context.inputId == bridge.GetInputId()) { manager = context.inputManager; break; }
                if (manager == null) return "ABILITY_INPUT_UNAVAILABLE";
                // Initialize the actual handler, but never enter input mode, select a
                // tower, move the cursor, or invoke its placement/confirmation callbacks.
                var input = InGame.GetCustomInputAbility(manager, ability, !string.IsNullOrEmpty(ability.GetCustomInputClass()));
                if (input == null) return "UNSUPPORTED_ABILITY_INPUT";
                valid = input.IsPositionValid(point, true);
            }
            else return "UNSUPPORTED_ABILITY_INPUT";
            if (!valid) return "INVALID_ABILITY_TARGET";
            if (native != null) native.vector2Value = new SimVector2(x, y);
        }
        else if (target.Kind == "points")
        {
            var carpet = AbilityBehavior<NativeAbilities.CarpetBombAbility>(ability);
            var selection = data?.TryCast<SelectTargetCIData>();
            if (carpet == null || selection == null) return "UNSUPPORTED_ABILITY_INPUT";
            var points = target.Points!;
            if (points.Length != selection.numberOfPoints) return "INVALID_ABILITY_TARGET";
            double length = 0;
            for (int i = 0; i < points.Length; i++)
            {
                if (!IsPointWithinMap(bridge, points[i].X, points[i].Y)) return "INVALID_ABILITY_TARGET";
                if (i > 0)
                {
                    double dx = (double)points[i].X - points[i - 1].X, dy = (double)points[i].Y - points[i - 1].Y;
                    length += Math.Sqrt(dx * dx + dy * dy);
                }
            }
            if (length < carpet.carpetBombAbilityModel.minPathDistance || length == 0) return "INVALID_ABILITY_TARGET";
            if (native != null)
            {
                var nativePoints = new Il2CppStructArray<SimVector2>(points.Length);
                for (int i = 0; i < points.Length; i++) nativePoints[i] = new SimVector2(points[i].X, points[i].Y);
                native.vector2ValueArr = nativePoints;
            }
        }
        nativeTarget = native;
        return null;
    }

    private static (bool ok, string abilityId, string name, string? error) TryActivateAbilityInternal(
        InGame inGame, UnityToSimulation bridge, string? abilityId, int abilityIndex, AbilityTargetV1? target)
    {
        var ability = ResolveAbility(inGame, abilityId, abilityIndex, out string? error, out _);
        if (ability == null) return (false, "", "", error);
        string finalId = AbilityId(ability);
        string name = ability.model?.displayName ?? ability.model?.name ?? "Unknown";
        try
        {
            // Cooldown retries do not repeatedly allocate native input/eligibility
            // data. Revalidate the retained target once activation becomes ready.
            if (!ability.IsReady) return (false, finalId, name, "ABILITY_NOT_READY");
            error = ValidateAbilityTarget(bridge, ability, target, out var nativeTarget);
            if (error != null) return (false, finalId, name, error);
            if (!ability.CanUseAbility()) return (false, finalId, name, "ABILITY_NOT_READY");
            bool previous = bridge.IsImmediateMode;
            try
            {
                // Commit before checking another due schedule in this same frame.
                // Otherwise two entries can both observe the pre-activation cooldown.
                bridge.IsImmediateMode = true;
                if (nativeTarget == null) ability.Activate(bridge.GetInputId());
                else ability.ApplyCustomInputData(bridge.GetInputId(), nativeTarget);
            }
            finally { bridge.IsImmediateMode = previous; }
            return (true, finalId, name, null);
        }
        catch (Exception ex)
        {
            BTD_Mod_Helper.ModHelper.Warning<AgentBridgeMod>($"Ability {finalId} target execution failed: {ex}");
            return (false, finalId, name, "ABILITY_ACTIVATION_FAILED");
        }
    }
}
