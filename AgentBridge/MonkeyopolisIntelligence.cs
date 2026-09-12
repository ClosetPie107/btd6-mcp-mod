using System;
using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Towers.Behaviors;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static bool TryGetFarmModelIncome(TowerToSimulation farm, out float income, out string kind, out string reason)
    {
        income = 0f;
        kind = "unavailable";
        reason = "Native tower income model is unavailable.";
        try
        {
            var info = ExtractTowerIncomeStats(farm.GetSimTower()?.towerModel);
            if (info == null)
                return false;

            var infoType = info.GetType();
            kind = infoType.GetProperty("IncomeType")?.GetValue(info)?.ToString() ?? "unknown";
            if (kind is not ("ground_drops" or "passive_per_round"))
            {
                reason = kind == "bank"
                    ? "Bank income is stored and depends on collection timing; it is not a fixed per-round payout."
                    : $"Income behavior '{kind}' is not a fixed per-round payout.";
                return false;
            }

            var raw = infoType.GetProperty("CashPerRound")?.GetValue(info);
            if (raw is float value && float.IsFinite(value) && value >= 0f)
            {
                income = value;
                reason = "Native tower model base payout; active income buffs and collection timing are not applied.";
                return true;
            }

            reason = "Native tower model did not expose a finite per-round payout.";
            return false;
        }
        catch
        {
            kind = "unavailable";
            reason = "Native tower income model could not be read.";
            return false;
        }
    }

    private static BridgeResultV1 HandleInspectMonkeyopolisSacrifices(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Monkeyopolis sacrifice inspection requires an active match.", false);

        string towerId = ReadString(request.Payload, "towerId", "");
        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_ARGUMENT", "towerId must be provided.", false);

        var allTowers = inGame.GetAllTowerToSim();
        var villageTower = allTowers?.FirstOrDefault(t => t != null && t.Id.ToString() == towerId);
        if (villageTower == null || villageTower.Def == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);

        string baseId = villageTower.Def.baseId ?? "";
        if (!string.Equals(baseId, "MonkeyVillage", StringComparison.OrdinalIgnoreCase))
            return ErrorResult(request, "INVALID_TOWER_TYPE", $"Target tower must be a Monkey Village, but was '{baseId}'.", false);

        int botTier = villageTower.Def.tiers != null && villageTower.Def.tiers.Length > 2 ? villageTower.Def.tiers[2] : 0;
        if (botTier != 4)
            return ErrorResult(request, "INVALID_TOWER_UPGRADE", $"Village must have exactly Tier 4 Monkey City (xx4) to inspect Monkeyopolis sacrifices, but bottom tier was {botTier}.", false);

        var vPos = villageTower.simPosition;
        float vx = vPos.x, vy = vPos.y;
        float range = villageTower.Def.range;
        if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(range) || range < 0f)
            return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", "Monkey City position or effective range is unavailable.", true);
        var absorbedFarms = new List<object>();
        var vacatedCoordinates = new List<object>();
        var tier5FarmWarnings = new List<object>();
        var incomeReasons = new List<string>();
        float totalFarmWorth = 0f;
        bool totalWorthAvailable = true;
        float preUpgradeIncome = 0f;
        bool preUpgradeIncomeAvailable = true;

        if (allTowers != null)
        {
            foreach (var candidate in allTowers)
            {
                if (candidate == null || candidate.Def == null)
                    continue;
                if (!string.Equals(candidate.Def.baseId ?? "", "BananaFarm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate.owner != villageTower.owner || candidate.Def.isSubTower || candidate.Def.isPowerTower)
                    continue;

                var cPos = candidate.simPosition;
                if (!float.IsFinite(cPos.x) || !float.IsFinite(cPos.y))
                    return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Farm '{candidate.Id}' has an unavailable position; sacrifice eligibility cannot be determined.", true);
                float dist = Vector2.Distance(new Vector2(cPos.x, cPos.y), new Vector2(vx, vy));
                if (!float.IsFinite(dist))
                    return ErrorResult(request, "NATIVE_STATE_UNAVAILABLE", $"Farm '{candidate.Id}' has an unavailable distance; sacrifice eligibility cannot be determined.", true);
                if (dist > range)
                    continue;

                int[] tiers = candidate.Def.tiers != null ? candidate.Def.tiers.ToArray() : [0, 0, 0];
                bool isTier5 = candidate.Def.tier >= 5 || tiers.Any(t => t >= 5);
                bool worthAvailable = TryGetNativeTowerWorth(candidate, out float worth);
                if (isTier5)
                {
                    // Tier 5 farms are reported separately from the eligible farm list.
                    tier5FarmWarnings.Add(new
                    {
                        Id = candidate.Id.ToString(),
                        Name = candidate.Def.name ?? "",
                        Tiers = tiers,
                        Worth = worthAvailable ? MathF.Round(worth, 1) : (float?)null,
                        ValuationAvailable = worthAvailable,
                        Position = new { X = MathF.Round(cPos.x, 1), Y = MathF.Round(cPos.y, 1) },
                        DistanceToVillage = MathF.Round(dist, 1),
                        Eligibility = "ineligible_tier5",
                        Warning = "Tier 5 Banana Farms are excluded by the native Monkeyopolis filter and remain in place."
                    });
                    continue;
                }

                totalWorthAvailable &= worthAvailable;
                totalFarmWorth += worth;
                if (!float.IsFinite(totalFarmWorth))
                    totalWorthAvailable = false;
                bool incomeAvailable = TryGetFarmModelIncome(candidate, out float farmIncome, out string incomeKind, out string incomeReason);
                preUpgradeIncomeAvailable &= incomeAvailable;
                if (!incomeAvailable && !incomeReasons.Contains(incomeReason))
                    incomeReasons.Add(incomeReason);
                if (incomeAvailable)
                    preUpgradeIncome += farmIncome;
                if (!float.IsFinite(preUpgradeIncome))
                {
                    preUpgradeIncomeAvailable = false;
                    incomeReasons.Add("The combined farm payout exceeds the supported numeric range.");
                }

                absorbedFarms.Add(new
                {
                    Id = candidate.Id.ToString(),
                    Name = candidate.Def.name ?? "",
                    Tiers = tiers,
                    Worth = worthAvailable ? MathF.Round(worth, 1) : (float?)null,
                    ValuationAvailable = worthAvailable,
                    IncomePerRound = incomeAvailable ? MathF.Round(farmIncome, 1) : (float?)null,
                    IncomeAvailable = incomeAvailable,
                    IncomeKind = incomeKind,
                    IncomeReason = incomeAvailable ? null : incomeReason,
                    Position = new { X = MathF.Round(cPos.x, 1), Y = MathF.Round(cPos.y, 1) },
                    DistanceToVillage = MathF.Round(dist, 1)
                });

                vacatedCoordinates.Add(new
                {
                    X = cPos.x,
                    Y = cPos.y,
                    Radius = candidate.Def.radius
                });
            }
        }

        int farmCount = absorbedFarms.Count;
        var gameModel = inGame.GetGameModel();
        var monkeyopolisTower = gameModel?.GetTower("MonkeyVillage", 0, 0, 5);
        var monkeyopolisModel = monkeyopolisTower?.GetBehavior<MonkeyopolisModel>();
        int? valueRequiredForIncomeIncrement = monkeyopolisModel?.valueRequiredForCrate;
        int? cashPerIncomeIncrement = monkeyopolisModel?.cashFromCrate;
        int? baseIncomePerRound = monkeyopolisModel?.baseIncome;
        int? cratesPerRound = monkeyopolisModel?.cratesPerRound;

        bool? nativeUpgradeBlocked = null;
        string? nativeBlockReason = null;
        try
        {
            nativeUpgradeBlocked = villageTower.IsUpgradeBlocked(2, 5, out string blockReason);
            nativeBlockReason = string.IsNullOrWhiteSpace(blockReason) ? null : blockReason;
        }
        catch
        {
            nativeBlockReason = "Native upgrade eligibility could not be queried.";
        }

        float? upgradeCost = GetAvailableUpgradeCost(villageTower, 2, gameModel);
        int? incomeIncrements = null;
        float? projectedIncome = null;
        float? cashPerCrate = null;
        string projectionStatus = "unavailable";
        string? projectionReason = null;
        if (farmCount == 0)
        {
            projectionReason = "No same-owner Banana Farm below Tier 5 is in range.";
        }
        else if (!totalWorthAvailable)
        {
            projectionReason = "One or more eligible farms have no native worth value.";
        }
        else if (monkeyopolisModel == null || valueRequiredForIncomeIncrement is not > 0 || cashPerIncomeIncrement is null || baseIncomePerRound is null || cratesPerRound is not > 0)
        {
            projectionReason = "Monkeyopolis native economic model fields are unavailable.";
        }
        else if (cashPerIncomeIncrement < 0 || baseIncomePerRound < 0)
        {
            projectionReason = "Monkeyopolis native economic model fields are outside supported non-negative bounds.";
        }
        else if (TryCalculateMonkeyopolisIncome(
            totalFarmWorth,
            valueRequiredForIncomeIncrement.Value,
            cashPerIncomeIncrement.Value,
            baseIncomePerRound.Value,
            cratesPerRound.Value,
            out int increments,
            out float income,
            out float perCrate))
        {
            incomeIncrements = increments;
            projectedIncome = income;
            cashPerCrate = perCrate;
            projectionStatus = "native_model_formula";
        }
        else
        {
            projectionReason = "Native model inputs produced an out-of-range or non-finite income value.";
        }

        float? preIncome = preUpgradeIncomeAvailable ? MathF.Round(preUpgradeIncome, 1) : null;
        float? netDelta = preIncome.HasValue && projectedIncome.HasValue
            ? MathF.Round(projectedIncome.Value - preIncome.Value, 1)
            : null;
        float? paybackPeriod = netDelta is > 0f && upgradeCost.HasValue
            ? MathF.Round(upgradeCost.Value / netDelta.Value, 1)
            : null;
        bool readyToUpgrade = farmCount > 0 && nativeUpgradeBlocked == false;
        string? recommendation;
        if (farmCount == 0)
            recommendation = "Monkeyopolis is not eligible: the native filter requires at least one same-owner Banana Farm below Tier 5 in range.";
        else if (nativeUpgradeBlocked == true)
            recommendation = nativeBlockReason ?? "Native upgrade eligibility currently blocks Monkeyopolis.";
        else if (!projectedIncome.HasValue)
            recommendation = "Native sacrifice eligibility is present, but the income projection is unavailable.";
        else if (netDelta.HasValue && netDelta.Value <= 0f)
            recommendation = "Projected Monkeyopolis income does not exceed the modeled standalone farm payout.";
        else if (!netDelta.HasValue || !paybackPeriod.HasValue)
            recommendation = "Native Monkeyopolis income is projected; pre-upgrade farm income or upgrade cost is unavailable for payback comparison.";
        else
            recommendation = $"Projected native-model increase is ${Math.Round(netDelta.Value, 0)}/round with payback in {paybackPeriod.Value} rounds.";

        return SuccessResult(request, new
        {
            Village = new
            {
                Id = villageTower.Id.ToString(),
                TowerType = baseId,
                Name = villageTower.Def.name ?? "",
                Tiers = villageTower.Def.tiers != null ? villageTower.Def.tiers.ToArray() : [0, 0, 4],
                Position = new { X = MathF.Round(vx, 1), Y = MathF.Round(vy, 1) },
                Range = MathF.Round(range, 1),
                RangeSource = "placed_tower_model",
                UpgradeCost = upgradeCost,
                NativeUpgradeBlocked = nativeUpgradeBlocked,
                NativeBlockReason = nativeBlockReason
            },
            FarmSacrifices = new
            {
                Count = farmCount,
                TotalWorth = totalWorthAvailable ? MathF.Round(totalFarmWorth, 1) : (float?)null,
                ValuationComplete = totalWorthAvailable,
                HasTier5FarmWarning = tier5FarmWarnings.Count > 0,
                Tier5FarmWarnings = tier5FarmWarnings.ToArray(),
                Farms = absorbedFarms.ToArray()
            },
            EconomicProjection = new
            {
                Status = projectionStatus,
                Reason = projectionReason,
                PreUpgradeIncomePerRound = preIncome,
                PreUpgradeIncomeConfidence = preUpgradeIncomeAvailable ? "native_model_base" : "unavailable",
                Assumptions = new[] { "Income comparison uses standalone model payouts, excluding active income buffs, bank balances, collection timing, and mode-specific income restrictions. Payback covers only the incremental upgrade cost, not the historical farm purchases." },
                PreUpgradeIncomeUnsupportedReasons = incomeReasons.ToArray(),
                ProjectedMonkeyopolisIncomePerRound = projectedIncome,
                NetIncomeDeltaPerRound = netDelta,
                BaseIncomePerRound = baseIncomePerRound,
                ValueRequiredForIncomeIncrement = valueRequiredForIncomeIncrement,
                CashPerIncomeIncrement = cashPerIncomeIncrement,
                IncomeIncrements = incomeIncrements,
                CratesPerRound = cratesPerRound,
                CashPerCrate = cashPerCrate,
                PaybackPeriodRounds = paybackPeriod,
                Formula = "baseIncome + max(1, floor(totalEligibleFarmWorth / valueRequiredForCrate)) * cashFromCrate; divided across cratesPerRound"
            },
            SpaceReclamation = new
            {
                VacatedFootprintCount = vacatedCoordinates.Count,
                FreedCenterCoordinates = vacatedCoordinates.ToArray()
            },
            ReadyToUpgrade = readyToUpgrade,
            Recommendation = recommendation,
            ObservedAtUtc = DateTime.UtcNow
        });
    }
}
