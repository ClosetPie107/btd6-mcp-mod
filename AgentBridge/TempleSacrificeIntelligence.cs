using System;
using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.TowerSets;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static bool TryGetNativeTowerWorth(TowerToSimulation tower, out float worth)
    {
        worth = 0f;
        try
        {
            float nativeWorth = tower.worth;
            if (!float.IsFinite(nativeWorth) || nativeWorth < 0f)
                return false;
            worth = nativeWorth;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BridgeResultV1 HandleInspectTempleSacrifices(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Temple sacrifice inspection requires an active match.", false);

        string towerId = ReadString(request.Payload, "towerId", "");
        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_ARGUMENT", "towerId must be provided.", false);

        var allTowers = inGame.GetAllTowerToSim();
        var templeTower = allTowers?.FirstOrDefault(t => t != null && t.Id.ToString() == towerId);
        if (templeTower == null || templeTower.Def == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);

        string baseId = templeTower.Def.baseId ?? "";
        if (!string.Equals(baseId, "SuperMonkey", StringComparison.OrdinalIgnoreCase))
            return ErrorResult(request, "INVALID_TOWER_TYPE", $"Target tower must be a Super Monkey, but was '{baseId}'.", false);

        var templeTiers = templeTower.Def.tiers;
        if (templeTiers == null || templeTiers.Length != 3)
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "Temple upgrade tiers are unavailable.", true);
        int topTier = templeTiers[0];
        string currentUpgrade = topTier switch
        {
            3 => "SunAvatar",
            4 => "SunTemple",
            _ => ""
        };
        string targetUpgrade = topTier switch
        {
            3 => "SunTemple",
            4 => "TrueSunGod",
            _ => ""
        };
        if (targetUpgrade.Length == 0)
            return ErrorResult(request, "INVALID_TOWER_UPGRADE", $"Target Super Monkey must be Tier 3 Sun Avatar or Tier 4 Sun Temple; top tier was {topTier}.", false);

        var pos = templeTower.simPosition;
        float tx = pos.x, ty = pos.y;
        float range = templeTower.Def.range;
        if (!float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(range) || range < 0f)
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "Temple position or effective range is unavailable.", true);


        var primaryTowers = new List<object>();
        var militaryTowers = new List<object>();
        var magicTowers = new List<object>();
        var supportTowers = new List<object>();

        float primaryWorth = 0f;
        float militaryWorth = 0f;
        float magicWorth = 0f;
        float supportWorth = 0f;
        bool primaryValuationComplete = true;
        bool militaryValuationComplete = true;
        bool magicValuationComplete = true;
        bool supportValuationComplete = true;
        bool valuationComplete = true;
        TowerToSimulation? antiBloon = null;
        TowerToSimulation? legendOfTheNight = null;
        int consumedCount = 0;
        int safeCount = 0;


        if (allTowers != null)
        {
            foreach (var candidate in allTowers)
            {
                if (candidate == null || candidate.Def == null || candidate.Id == templeTower.Id)
                    continue;
                if (candidate.Def.isSubTower || candidate.hero != null || candidate.IsParagon || candidate.Def.isPowerTower ||
                    candidate.Def.towerSet is not (TowerSet.Primary or TowerSet.Military or TowerSet.Magic or TowerSet.Support))
                    continue;

                var cPos = candidate.simPosition;
                if (!float.IsFinite(cPos.x) || !float.IsFinite(cPos.y))
                    return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Tower '{candidate.Id}' has an unavailable position; sacrifice eligibility cannot be determined.", true);
                float dist = Vector2.Distance(new Vector2(cPos.x, cPos.y), new Vector2(tx, ty));
                if (!float.IsFinite(dist))
                    return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Tower '{candidate.Id}' has an unavailable distance; sacrifice eligibility cannot be determined.", true);
                string cBase = candidate.Def.baseId ?? "";

                if (string.Equals(cBase, "SuperMonkey", StringComparison.OrdinalIgnoreCase) && candidate.Def.tiers != null)
                {
                    if (candidate.Def.tiers.Length > 1 && candidate.Def.tiers[1] >= 5)
                        antiBloon = candidate;
                    if (candidate.Def.tiers.Length > 2 && candidate.Def.tiers[2] >= 5)
                        legendOfTheNight = candidate;
                }

                if (dist > range)
                {
                    safeCount++;
                    continue;
                }

                // Temples also sacrifice other players' towers in co-op; heroes,
                // paragons, powers and subtowers are not eligible.
                consumedCount++;
                bool worthAvailable = TryGetNativeTowerWorth(candidate, out float worth);
                valuationComplete &= worthAvailable;
                var tiers = candidate.Def.tiers != null ? candidate.Def.tiers.ToArray() : [0, 0, 0];
                var towerDto = new
                {
                    Id = candidate.Id.ToString(),
                    Name = candidate.Def.name ?? "",
                    BaseId = cBase,
                    Tiers = tiers,
                    Worth = worthAvailable ? MathF.Round(worth, 1) : (float?)null,
                    ValuationAvailable = worthAvailable,
                    Position = new { X = MathF.Round(cPos.x, 1), Y = MathF.Round(cPos.y, 1) },
                    DistanceToTemple = MathF.Round(dist, 1)
                };

                switch (candidate.Def.towerSet)
                {
                    case TowerSet.Primary:
                        primaryWorth += worth;
                        primaryValuationComplete &= worthAvailable;
                        primaryTowers.Add(towerDto);
                        break;
                    case TowerSet.Military:
                        militaryWorth += worth;
                        militaryValuationComplete &= worthAvailable;
                        militaryTowers.Add(towerDto);
                        break;
                    case TowerSet.Magic:
                        magicWorth += worth;
                        magicValuationComplete &= worthAvailable;
                        magicTowers.Add(towerDto);
                        break;
                    case TowerSet.Support:
                        supportWorth += worth;
                        supportValuationComplete &= worthAvailable;
                        supportTowers.Add(towerDto);
                        break;
                }
            }
        }

        const float MaxThreshold = 50000f;
        var categories = new[]
        {
            new { Name = "primary", Worth = primaryWorth, Towers = primaryTowers, ValuationComplete = primaryValuationComplete },
            new { Name = "military", Worth = militaryWorth, Towers = militaryTowers, ValuationComplete = militaryValuationComplete },
            new { Name = "magic", Worth = magicWorth, Towers = magicTowers, ValuationComplete = magicValuationComplete },
            new { Name = "support", Worth = supportWorth, Towers = supportTowers, ValuationComplete = supportValuationComplete }
        };

        var sortedCategories = categories.OrderByDescending(c => c.Worth).ToArray();
        float lowestWorth = sortedCategories[^1].Worth;
        var excludedTies = valuationComplete
            ? sortedCategories.Where(c => MathF.Abs(c.Worth - lowestWorth) <= 0.001f).Select(c => c.Name).ToArray()
            : Array.Empty<string>();
        string? excludedForTier4 = excludedTies.Length == 1 ? excludedTies[0] : null;

        bool isTier4 = targetUpgrade == "SunTemple";
        var activeCategories = isTier4 ? sortedCategories.Take(3).ToArray() : sortedCategories;
        bool willQualifyForMax = valuationComplete && activeCategories.All(c => c.Worth >= MaxThreshold);
        string? blockerReason = null;
        if (!valuationComplete)
            blockerReason = "One or more eligible towers have unavailable native state; threshold qualification is unavailable.";
        else if (!willQualifyForMax)
            blockerReason = $"Sacrifice threshold of $50,000 not met for: {string.Join(", ", activeCategories.Where(c => c.Worth < MaxThreshold).Select(c => $"{c.Name}: ${Math.Round(MaxThreshold - c.Worth, 0)} short"))}.";
        string antiBloonStatus = "not_placed";
        if (antiBloon != null)
        {
            float d = Vector2.Distance(new Vector2(antiBloon.simPosition.x, antiBloon.simPosition.y), new Vector2(tx, ty));
            antiBloonStatus = !float.IsFinite(d) ? "unavailable" : d <= range ? "inside_range_hazard" : "outside_range_ready";
        }

        string lotnStatus = "not_placed";
        if (legendOfTheNight != null)
        {
            float d = Vector2.Distance(new Vector2(legendOfTheNight.simPosition.x, legendOfTheNight.simPosition.y), new Vector2(tx, ty));
            lotnStatus = !float.IsFinite(d) ? "unavailable" : d <= range ? "inside_range_hazard" : "outside_range_ready";
        }

        var vtsgWarnings = new List<string>();
        if (antiBloonStatus == "inside_range_hazard")
            vtsgWarnings.Add("The Anti-Bloon is inside temple range and will be sacrificed, so it cannot contribute to VTSG formation.");
        if (lotnStatus == "inside_range_hazard")
            vtsgWarnings.Add("Legend of the Night is inside temple range and will be sacrificed, so it cannot contribute to VTSG formation.");
        bool positioningReady = antiBloonStatus == "outside_range_ready" && lotnStatus == "outside_range_ready";


        return SuccessResult(request, new
        {
            SuperMonkey = new
            {
                Id = templeTower.Id.ToString(),
                TowerType = baseId,
                Name = templeTower.Def.name ?? "",
                Tiers = templeTiers.ToArray(),
                CurrentUpgrade = currentUpgrade,
                TargetUpgrade = targetUpgrade,
                Position = new { X = MathF.Round(tx, 1), Y = MathF.Round(ty, 1) },
                Range = MathF.Round(range, 1),
                RangeSource = "placed_tower_model"
            },
            SacrificeCategories = new
            {
                Primary = new { TotalWorth = MathF.Round(primaryWorth, 1), ValuationComplete = primaryValuationComplete, Threshold50kMet = primaryValuationComplete && primaryWorth >= MaxThreshold, Surplus = primaryValuationComplete ? MathF.Round(Math.Max(0f, primaryWorth - MaxThreshold), 1) : (float?)null, Deficit = primaryValuationComplete ? MathF.Round(Math.Max(0f, MaxThreshold - primaryWorth), 1) : (float?)null, Towers = primaryTowers.ToArray() },
                Military = new { TotalWorth = MathF.Round(militaryWorth, 1), ValuationComplete = militaryValuationComplete, Threshold50kMet = militaryValuationComplete && militaryWorth >= MaxThreshold, Surplus = militaryValuationComplete ? MathF.Round(Math.Max(0f, militaryWorth - MaxThreshold), 1) : (float?)null, Deficit = militaryValuationComplete ? MathF.Round(Math.Max(0f, MaxThreshold - militaryWorth), 1) : (float?)null, Towers = militaryTowers.ToArray() },
                Magic = new { TotalWorth = MathF.Round(magicWorth, 1), ValuationComplete = magicValuationComplete, Threshold50kMet = magicValuationComplete && magicWorth >= MaxThreshold, Surplus = magicValuationComplete ? MathF.Round(Math.Max(0f, magicWorth - MaxThreshold), 1) : (float?)null, Deficit = magicValuationComplete ? MathF.Round(Math.Max(0f, MaxThreshold - magicWorth), 1) : (float?)null, Towers = magicTowers.ToArray() },
                Support = new { TotalWorth = MathF.Round(supportWorth, 1), ValuationComplete = supportValuationComplete, Threshold50kMet = supportValuationComplete && supportWorth >= MaxThreshold, Surplus = supportValuationComplete ? MathF.Round(Math.Max(0f, supportWorth - MaxThreshold), 1) : (float?)null, Deficit = supportValuationComplete ? MathF.Round(Math.Max(0f, MaxThreshold - supportWorth), 1) : (float?)null, Towers = supportTowers.ToArray() }
            },
            TempleProjection = new
            {
                TargetUpgrade = targetUpgrade,
                ValuationComplete = valuationComplete,
                WillQualifyForMaxTier = willQualifyForMax,
                Top3Categories = sortedCategories.Take(3).Select(c => c.Name).ToArray(),
                ExcludedCategoryForTier4 = isTier4 ? excludedForTier4 : null,
                ExcludedCategoryTie = isTier4 && excludedTies.Length > 1 ? excludedTies : Array.Empty<string>(),
                BlockerReason = blockerReason
            },
            VtsgReadiness = new
            {
                AntiBloonStatus = antiBloonStatus,
                LegendOfTheNightStatus = lotnStatus,
                PositioningReady = positioningReady,
                CanFormVtsg = (bool?)null,
                UnknownPrerequisites = new[]
                {
                    "Final VTSG formation also depends on native sacrifice thresholds, mode/Monkey Knowledge rules, and prior Temple state not exposed by this read-only inspection."
                },
                Warning = vtsgWarnings.Count > 0 ? string.Join(" ", vtsgWarnings) : null
            },
            TowersConsumedCount = consumedCount,
            SafeSurroundingTowersCount = safeCount,
            ObservedAtUtc = DateTime.UtcNow
        });
    }
}
