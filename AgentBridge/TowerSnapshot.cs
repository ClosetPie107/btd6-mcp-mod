using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2Cpp;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static BridgeResultV1 HandleTowerStats(BridgeRequestV1 request)
    {
        var gameModel = InGame.instance?.GetGameModel() ?? Game.instance?.model;
        if (gameModel == null)
            return ErrorResult(request, "GAME_MODEL_UNAVAILABLE", "The BTD6 game model is not available.", true);

        string towerType = ReadString(request.Payload, "towerType", "");
        if (string.IsNullOrEmpty(towerType))
            return ErrorResult(request, "INVALID_TOWER_TYPE", "towerType must be provided.", false);

        int[] tiers = ReadTiers(request.Payload);
        var tower = gameModel.GetTower(towerType, tiers[0], tiers[1], tiers[2])
            ?? gameModel.GetTowerWithName(towerType)
            ?? gameModel.GetTowerFromId(towerType);
        if (tower == null)
            return ErrorResult(request, "UNKNOWN_TOWER_TYPE", $"Unknown tower type '{towerType}'.", false);

        return SuccessResult(request, new
        {
            Source = "live-game-model",
            Btd6Version = Application.version,
            Stats = BuildTowerStats(tower)
        });
    }

    private static BridgeResultV1 HandleInspectTower(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot inspect a tower without an active match.", false);

        string towerId = ReadString(request.Payload, "towerId", "");
        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_TOWER_ID", "towerId must be provided.", false);

        TowerToSimulation? tower = inGame.GetAllTowerToSim()?.FirstOrDefault(candidate => candidate != null && candidate.Id.ToString() == towerId);
        if (tower == null || tower.Def == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);

        return SuccessResult(request, new
        {
            Source = "active-simulation",
            ObservedAtUtc = DateTime.UtcNow,
            Tower = ConvertTower(tower, inGame.GetGameModel()),
            ModelStats = BuildTowerStats(tower.Def),
            EffectiveStats = BuildEffectiveTowerStats(tower.GetSimTower())
        });
    }

    private static int[] ReadTiers(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("tiers", out var tiers)
            || tiers.ValueKind != JsonValueKind.Array
            || tiers.GetArrayLength() != 3)
            return [0, 0, 0];

        var result = new int[3];
        for (int path = 0; path < result.Length; path++)
        {
            if (!tiers[path].TryGetInt32(out int tier) || tier is < 0 or > 5)
                return [0, 0, 0];
            result[path] = tier;
        }
        return result;
    }

    private static string GetDamageTypeLabel(BloonProperties immune)
    {
        if (immune == BloonProperties.None)
            return "Normal";
        if (immune == (BloonProperties.Lead | BloonProperties.Frozen))
            return "Sharp";
        if (immune == BloonProperties.Black)
            return "Explosion";
        if (immune == (BloonProperties.Lead | BloonProperties.Purple))
            return "Energy";
        if (immune == BloonProperties.Purple)
            return "Plasma";
        if (immune == (BloonProperties.Lead | BloonProperties.White))
            return "Cold";
        if (immune == BloonProperties.Lead)
            return "Anti-Lead-Restricted";
        return "Composite";
    }

    private static List<string> GetBlockedBloonTypes(BloonProperties immune)
    {
        var list = new List<string>();
        if ((immune & BloonProperties.Lead) != 0) list.Add("Lead");
        if ((immune & BloonProperties.Purple) != 0) list.Add("Purple");
        if ((immune & BloonProperties.Black) != 0) list.Add("Black");
        if ((immune & BloonProperties.White) != 0) list.Add("White");
        if ((immune & BloonProperties.Frozen) != 0) list.Add("Frozen");
        return list;
    }
}
