using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static float? GetPlacementCost(UnityToSimulation bridge, TowerModel towerModel)
    {
        try
        {
            var simulation = bridge.GetSim();
            var inventory = simulation?.GetTowerInventory(bridge.GetInputId());
            if (simulation == null || inventory == null) return null;
            float cost = inventory.GetTowerCost(towerModel, simulation, bridge.GetInputId());
            return float.IsFinite(cost) && cost >= 0 ? cost : null;
        }
        catch { return null; }
    }
    private static bool IsRangeSupportTower(TowerToSimulation tower) =>
        tower.Def?.baseId is "MonkeyVillage" or "Alchemist";

    private static SupportCoverageV1 BuildSupportCoverage(TowerToSimulation supportTower)
    {
        var position = supportTower.simPosition;
        float range = supportTower.Def?.range ?? 0f;
        var recipients = new List<SupportRecipientV1>();
        var allTowers = InGame.instance?.GetAllTowerToSim();
        if (allTowers != null)
            foreach (var tower in allTowers)
            {
                if (tower == null || tower.Id == supportTower.Id)
                    continue;
                var target = tower.simPosition;
                double dx = target.x - position.x;
                double dy = target.y - position.y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                if (distance > range)
                    continue;
                recipients.Add(new SupportRecipientV1
                {
                    TowerId = tower.Id.ToString(),
                    TowerType = tower.Def?.baseId ?? "",
                    Position = new Position2DV1 { X = target.x, Y = target.y },
                    Distance = MathF.Round(distance, 1),
                    RangeMargin = MathF.Round(range - distance, 1)
                });
            }
        return new SupportCoverageV1
        {
            TowerId = supportTower.Id.ToString(),
            TowerType = supportTower.Def?.baseId ?? "",
            SupportRange = range,
            EligibilityVerified = false,
            Recipients = recipients.OrderBy(recipient => recipient.Distance).ToList(),
            StrategicWarning = recipients.Count == 0
                ? "No existing tower center is inside this support tower's current model range."
                : null
        };
    }

    private static BridgeResultV1 HandleInspectSupportCoverage(BridgeRequestV1 request)
    {
        string towerId = ReadString(request.Payload, "towerId", "");
        var tower = InGame.instance?.GetAllTowerToSim()?.FirstOrDefault(candidate => candidate != null && candidate.Id.ToString() == towerId);
        if (tower == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);
        if (!IsRangeSupportTower(tower))
            return ErrorResult(request, "UNSUPPORTED_TOWER", $"Tower '{towerId}' is not a supported range-aura tower.", false);
        return SuccessResult(request, BuildSupportCoverage(tower));
    }


    private static BridgeResultV1 HandleCanPlaceTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot check placement: no active match.", false);

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        string towerType = ReadString(request.Payload, "towerType", "DartMonkey");
        float x = ReadFloat(request.Payload, "x", 0f);
        float y = ReadFloat(request.Payload, "y", 0f);

        var tm = gameModel.GetTower(towerType, 0, 0, 0)
              ?? gameModel.GetTowerWithName(towerType)
              ?? gameModel.GetTowerFromId(towerType);

        if (tm == null)
        {
            return SuccessResult(request, new
            {
                CanPlace = false,
                Affordable = false,
                Valid = false,
                Cost = 0f,
                Cash = inGame.GetCash(),
                Reason = $"Unknown tower type '{towerType}'."
            });
        }

        bool canPlace = bridge.CanPlaceTowerAt(new UnityEngine.Vector2(x, y), tm, bridge.GetInputId(), ObjectId.Invalid);
        float? quote = GetPlacementCost(bridge, tm);
        if (quote == null)
            return ErrorResult(request, "PRICE_UNAVAILABLE", "The native tower purchase price is unavailable; no purchase was submitted.", false);
        float cost = quote.Value;

        double currentCash = inGame.GetCash();
        bool affordable = currentCash >= cost;

        string? reason = null;
        if (!canPlace) reason = "Location blocked (terrain, track collision, or existing tower)";
        else if (!affordable) reason = $"Insufficient cash (costs ${cost:F0}, have ${currentCash:F0})";

        return SuccessResult(request, new
        {
            CanPlace = canPlace,
            Affordable = affordable,
            Valid = canPlace && affordable,
            Cost = cost,
            Cash = currentCash,
            Reason = reason
        });
    }

    private static BridgeResultV1 HandlePlaceTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot place tower: no active match.", false);

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        string towerType = ReadString(request.Payload, "towerType", "DartMonkey");
        float x = ReadFloat(request.Payload, "x", 0f);
        float y = ReadFloat(request.Payload, "y", 0f);
        var tm = gameModel.GetTower(towerType, 0, 0, 0)
              ?? gameModel.GetTowerWithName(towerType)
              ?? gameModel.GetTowerFromId(towerType);
        if (tm == null)
            return ErrorResult(request, "UNKNOWN_TOWER_TYPE", $"Unknown tower type '{towerType}'.", false);

        float? quote = GetPlacementCost(bridge, tm);
        if (quote == null)
            return ErrorResult(request, "PRICE_UNAVAILABLE", "The native tower purchase price is unavailable; no purchase was submitted.", false);
        double cash = inGame.GetCash();
        if (cash < quote.Value)
            return ErrorResult(request, "INSUFFICIENT_CASH", $"Cannot afford {towerType}: costs ${quote.Value:F0}, have ${cash:F0}. No purchase was submitted.", false,
                new { Cost = quote.Value, Cash = cash });
        if (!bridge.CanPlaceTowerAt(new UnityEngine.Vector2(x, y), tm, bridge.GetInputId(), ObjectId.Invalid))
            return ErrorResult(request, "LOCATION_BLOCKED", $"Cannot place {towerType} at ({x:F1}, {y:F1}): the native placement check rejected this location. No purchase was submitted.", false,
                new { Cost = quote.Value, Cash = cash });

        var existingIds = new HashSet<string>();
        try
        {
            var existing = inGame.GetAllTowerToSim();
            if (existing != null)
                foreach (var tower in existing)
                    if (tower != null)
                        existingIds.Add(tower.Id.ToString());
        }
        catch { }

        bool success = false;
        bool prevImmediate = bridge.IsImmediateMode;
        try
        {
            bridge.IsImmediateMode = true;
            bridge.CreateTowerAt(
                bridge.GetInputId(),
                new UnityEngine.Vector2(x, y),
                tm,
                ObjectId.Invalid,
                false,
                (Il2CppSystem.Action<bool>)((bool result) => success = result),
                false,
                false,
                false,
                -1,
                true,
                -1
            );
        }
        finally
        {
            bridge.IsImmediateMode = prevImmediate;
        }
        if (!success)
            return ErrorResult(request, "PLACEMENT_FAILED", $"Could not place {towerType} at ({x:F1}, {y:F1}): location blocked or insufficient cash.", false);

        TowerToSimulation? newSimTower = null;
        try
        {
            var current = inGame.GetAllTowerToSim();
            if (current != null)
                foreach (var tower in current)
                    if (tower != null && !existingIds.Contains(tower.Id.ToString()))
                    {
                        newSimTower = tower;
                        break;
                    }
        }
        catch { }
        if (newSimTower == null)
            return ErrorResult(request, "PLACEMENT_VERIFICATION_FAILED", $"Placed {towerType}, but could not identify its simulation tower.", true);

        TowerInfoV1 info = ConvertTower(newSimTower, gameModel);
        SupportCoverageV1? supportCoverage = IsRangeSupportTower(newSimTower)
            ? BuildSupportCoverage(newSimTower)
            : null;
        return SuccessResult(request, new
        {
            Placed = true,
            Tower = info,
            Cash = inGame.GetCash(),
            SupportCoverage = supportCoverage
        });
    }


    private static BridgeResultV1 HandleSellTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot sell tower: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        if (inGame.GetGameModel()?.towerSellEnabled != true)
            return ErrorResult(request, "SELLING_DISABLED", "Selling is disabled by the active game rules.", false);
        string towerId = ReadString(request.Payload, "towerId", "");
        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_TOWER_ID", "towerId must be provided.", false);

        TowerToSimulation? targetTower = null;
        try
        {
            var allTowers = inGame.GetAllTowerToSim();
            if (allTowers != null)
            {
                foreach (var t in allTowers)
                {
                    if (t != null && t.Id.ToString() == towerId)
                    {
                        targetTower = t;
                        break;
                    }
                }
            }
        }
        catch { }

        if (targetTower == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' not found.", false);
        if (targetTower.IsSellingBlocked)
            return ErrorResult(request, "SELLING_DISABLED", $"Selling tower '{towerId}' is blocked by the native game rules.", false);

        double cashBefore = inGame.GetCash();
        bool prevImmediate = bridge.IsImmediateMode;
        try
        {
            bridge.IsImmediateMode = true;
            bridge.SellTower(bridge.GetInputId(), targetTower.Id);
        }
        finally
        {
            bridge.IsImmediateMode = prevImmediate;
        }
        if (inGame.GetAllTowerToSim().Any(t => t != null && t.Id.ToString() == towerId))
            return ErrorResult(request, "SELL_FAILED", $"Tower '{towerId}' remains in the simulation after the sell request.", false);

        return SuccessResult(request, new
        {
            Sold = true,
            TowerId = towerId,
            CashReceived = inGame.GetCash() - cashBefore,
            Cash = inGame.GetCash()
        });
    }



    private static BridgeResultV1 HandleToggleSubmerge(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot toggle submerge: no active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        string towerId = ReadString(request.Payload, "towerId", "");
        bool? desiredState = null;
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("submerged", out var subProp))
        {
            if (subProp.ValueKind == JsonValueKind.True || subProp.ValueKind == JsonValueKind.False)
                desiredState = subProp.GetBoolean();
        }

        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_TOWER_ID", "towerId must be provided.", false);

        TowerToSimulation? targetTower = null;
        try
        {
            var allTowers = inGame.GetAllTowerToSim();
            if (allTowers != null)
            {
                foreach (var t in allTowers)
                {
                    if (t != null && t.Id.ToString() == towerId)
                    {
                        targetTower = t;
                        break;
                    }
                }
            }
        }
        catch { }

        if (targetTower == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' not found.", false);

        var simTower = targetTower.GetSimTower();
        if (simTower == null)
            return ErrorResult(request, "SIMULATION_TOWER_UNAVAILABLE", $"Simulation tower for ID '{towerId}' unavailable.", false);

        Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Submerge? submerge = null;
        if (simTower.Behaviors?.list != null)
        {
            var bList = simTower.Behaviors.list;
            for (int i = 0; i < bList.Count; i++)
            {
                var sub = bList[i]?.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Submerge>();
                if (sub != null)
                {
                    submerge = sub;
                    break;
                }
            }
        }

        if (submerge == null)
            return ErrorResult(request, "NOT_SUBMERGE_TOWER", $"Tower '{towerId}' does not have submerge capability (requires Monkey Sub 3xx+).", false);

        bool previousSubmergedState = submerge.isSubmerged;
        bool targetSubmerged = desiredState ?? !previousSubmergedState;

        if (targetSubmerged != previousSubmergedState)
        {
            if (targetSubmerged)
            {
                submerge.SwitchToSubmergeAttacks();
                try { submerge.CreateSubmergeEffect(); } catch { }
                try { submerge.PlaySubmergeSound(); } catch { }
            }
            else
            {
                submerge.SwitchToSurfaceAttacks();
                try { submerge.CreateUnsubmergeEffect(); } catch { }
                try { submerge.PlayEmergeSound(); } catch { }
            }
        }

        if (submerge.isSubmerged != targetSubmerged)
            return ErrorResult(request, "SUBMERGE_STATE_NOT_APPLIED", $"Tower '{towerId}' did not reach the requested submerge state.", false);

        return SuccessResult(request, new
        {
            TowerId = towerId,
            PreviousSubmergedState = previousSubmergedState,
            IsSubmerged = submerge.isSubmerged
        });
    }
}
