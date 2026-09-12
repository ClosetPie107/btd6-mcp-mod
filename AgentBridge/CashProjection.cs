using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Bloons.Behaviors;
using Il2CppAssets.Scripts.Models.SimulationBehaviors;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static double? ProjectionMoney(double? value) => value.HasValue ? Math.Round(value.Value, 6) : null;

    private static BridgeResultV1 HandleGetProjectedCash(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        var bridge = inGame?.bridge;
        var gameModel = inGame?.GetGameModel();
        var simulation = bridge?.Simulation;
        if (inGame == null || !inGame.IsInGame() || bridge == null || gameModel == null || simulation == null)
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cash projections require the active match's economic model.", false);
        if (bridge.AreRoundsActive())
            return ErrorResult(request, "ROUND_ACTIVE", "Project between rounds; an active or paused wave has already earned part of its income.", false);
        var roundSet = gameModel.roundSet;
        var incomeSet = gameModel.incomeSet;
        if (roundSet?.rounds == null || incomeSet?.thresholds == null)
            return ErrorResult(request, "GAME_MODEL_UNAVAILABLE", "The active round set or income set is unavailable.", false);

        int currentRound = bridge.GetCurrentRound() + 1;
        int fromRound = ReadInt(request.Payload, "fromRound", currentRound);
        int targetRound = ReadInt(request.Payload, "targetRound", -1);
        if (fromRound < 1 || targetRound < fromRound || targetRound > 200)
            return ErrorResult(request, "INVALID_ROUND_RANGE", "Require 1 <= fromRound <= targetRound <= 200.", false);
        double startingCash = inGame.GetCash();
        bool suppliedCash = request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("currentCash", out _);
        if (suppliedCash)
        {
            var cash = request.Payload.GetProperty("currentCash");
            if (cash.ValueKind != JsonValueKind.Number || !cash.TryGetDouble(out startingCash) || !double.IsFinite(startingCash) || startingCash < 0)
                return ErrorResult(request, "INVALID_ARGUMENT", "currentCash must be a finite nonnegative number.", false);
        }
        if (fromRound != currentRound && !suppliedCash)
            return ErrorResult(request, "INVALID_ARGUMENT", "A hypothetical fromRound requires an explicit currentCash for the start of that round.", false);

        var unsupported = new List<string>();
        void Unsupported(string reason)
        {
            if (unsupported.Count < 32 && !unsupported.Contains(reason)) unsupported.Add(reason);
        }
        bool commonRulesSupported = true;
        var cashManager = simulation.GetCashManager(bridge.GetInputId());
        if (cashManager == null || cashManager.doubleCash)
        {
            Unsupported("Double Cash or an unavailable player cash manager: payout-type multipliers have not been verified.");
            commonRulesSupported = false;
        }
        if (simulation.GetCashModifierFromSimBehaviors() != 1f)
        {
            Unsupported("A dynamic simulation cash modifier is active; its future application is not modeled.");
            commonRulesSupported = false;
        }
        var simulationBehaviors = simulation.GetBehaviors();
        for (int i = 0; i < simulationBehaviors.Count; i++)
        {
            string name = simulationBehaviors[i].GetIl2CppType().Name;
            if (name is "SharedTowerGrid" or "BonusCashPerRound" or "SetMaxHealthOfBloonBehavior") continue;
            Unsupported($"Unmodeled simulation behavior: {name}.");
            commonRulesSupported = false;
        }
        var roundRewards = new List<BonusCashPerRoundModel>();
        if (gameModel.behaviors != null)
        {
            foreach (var behavior in gameModel.behaviors)
            {
                if (behavior == null) continue;
                var bonus = behavior.TryCast<BonusCashPerRoundModel>();
                if (bonus != null) roundRewards.Add(bonus);
                else if (behavior.GetIl2CppType().Name != "SetMaxHealthOfBloonBehaviorModel")
                {
                    Unsupported($"Unmodeled game behavior: {behavior.GetIl2CppType().Name}.");
                    commonRulesSupported = false;
                }
            }
        }

        // Cash is attached to each popped layer, not to HP. HalfCash and Deflation
        // already modify these active models: never apply their mode multipliers again.
        var cashCache = new Dictionary<string, double?>();
        var visiting = new HashSet<string>();
        double? BloonCash(string id)
        {
            if (cashCache.TryGetValue(id, out var cached)) return cached;
            if (cashCache.Count >= 2048 || visiting.Count >= 64 || !visiting.Add(id))
            {
                Unsupported("Bloon cash graph is cyclic or exceeds the bounded traversal limit.");
                return null;
            }
            var bloon = gameModel.GetBloon(id);
            double? total = 0;
            if (bloon?.behaviors == null)
            {
                Unsupported($"Missing cash model for bloon {id}.");
                total = null;
            }
            else
            {
                foreach (var behavior in bloon.behaviors)
                {
                    if (behavior == null) continue;
                    var distribution = behavior.TryCast<DistributeCashModel>();
                    if (distribution != null)
                    {
                        if (!float.IsFinite(distribution.cash) || distribution.cash < 0 || distribution.multiplier != 1f
                            || distribution.additive != 0f || !float.IsFinite(distribution.additionalCash) || distribution.additionalCash < 0)
                        {
                            Unsupported("Nonstandard per-bloon cash adjustments require a verified payout formula.");
                            total = null;
                        }
                        // Super ceramics preserve their original cash yield through additionalCash
                        // after their child branches are reduced at the round-81 transition.
                        else if (!distribution.giveNoCash) total += distribution.cash + distribution.additionalCash;
                    }
                    else
                    {
                        var children = behavior.TryCast<SpawnChildrenModel>();
                        if (children?.children != null)
                            foreach (var child in children.children) total += BloonCash(child);
                        else if (behavior.GetIl2CppType().Name.Contains("Cash", StringComparison.OrdinalIgnoreCase))
                        {
                            Unsupported($"Unmodeled bloon cash behavior: {behavior.GetIl2CppType().Name}.");
                            total = null;
                        }
                    }
                }
            }
            visiting.Remove(id);
            cashCache[id] = total;
            return total;
        }

        var towerEstimates = new List<TowerIncomeEstimateV1>();
        var towerAssumptions = new List<string>
        {
            "Uses current effective tower models held constant; no future placements, upgrades, sales, or income-buff changes.",
            "Ability income, attack-dependent bonus cash, and bank withdrawals are excluded; bank storage is not spendable cash."
        };
        double towerPerRound = 0;
        var towers = inGame.GetAllTowerToSim();
        foreach (var tower in towers)
        {
            if (tower == null) continue;
            var model = tower.GetSimTower()?.towerModel;
            var income = ExtractTowerIncomeStats(model);
            if (income == null) continue;
            // Reuse the same income extraction as inspect_tower; no independent farm formula.
            var type = income.GetType();
            string kind = type.GetProperty("IncomeType")?.GetValue(income)?.ToString() ?? "unknown";
            bool included = kind is "passive_per_round" or "ground_drops";
            double? amount = null;
            string? reason = null;
            if (included)
            {
                var raw = type.GetProperty("CashPerRound")?.GetValue(income);
                if (raw is float cash && float.IsFinite(cash) && cash >= 0) amount = cash;
                else { included = false; reason = "Income amount is unavailable or invalid."; }
            }
            else reason = kind switch
            {
                "bank" => "Deposits and interest remain in bank storage until a withdrawal; withdrawals and overflow are not projected.",
                "ability_activated" => "Future ability activation times are not specified.",
                "support_buff" => "Income-support effects are not independently added; current recipient models provide the estimate.",
                _ => "This income behavior is not modeled."
            };
            if (kind == "ground_drops" && !towerAssumptions.Contains("All produced cash drops are collected, regardless of the current automatic-collection setting."))
                towerAssumptions.Add("All produced cash drops are collected, regardless of the current automatic-collection setting.");
            if (included) towerPerRound += amount!.Value;
            towerEstimates.Add(new TowerIncomeEstimateV1
            {
                TowerId = tower.Id.ToString(), TowerType = model?.baseId ?? "Unknown", Kind = kind,
                Included = included, PerRoundEstimate = amount, Reason = reason
            });
        }

        var breakdown = new List<RoundIncomeBreakdownV1>();
        double? popTotal = 0, rewardTotal = 0, baselineCash = startingCash, estimatedCash = startingCash;
        double? baselineAtTarget = startingCash, estimatedAtTarget = startingCash;
        int groupCount = 0;
        for (int round = fromRound; round <= targetRound; round++)
        {
            int index = round - 1;
            if (round == targetRound) { baselineAtTarget = baselineCash; estimatedAtTarget = estimatedCash; }
            double multiplier = incomeSet.IncomeMultiplierForRound(index);
            double? popCash = 0;
            double? reward = commonRulesSupported ? 0 : null;
            if (!double.IsFinite(multiplier) || multiplier < 0)
                return ErrorResult(request, "GAME_MODEL_UNAVAILABLE", "The active income set returned an invalid multiplier.", false);
            // Apopalypse uses generated, overlapping waves, not the fixed round set.
            // Its ordinary round-end bonus is suppressed despite a bonus model existing.
            if (gameModel.isApopalypse || index >= roundSet.rounds.Length)
            {
                Unsupported("Generated/Apopalypse waves are not fixed round-set data; future pop cash is unavailable without reproducing the generator.");
                popCash = null;
            }
            else if (!commonRulesSupported) popCash = null;
            else
            {
                var groups = roundSet.rounds[index]?.groups;
                if (groups == null) { Unsupported($"Round {round} has no available group model."); popCash = null; }
                else foreach (var group in groups)
                {
                    if (group == null || group.count < 0 || ++groupCount > 10000)
                    {
                        Unsupported("Round groups are invalid or exceed the bounded projection limit.");
                        popCash = null;
                        break;
                    }
                    popCash += BloonCash(group.bloon) * group.count * multiplier;
                }
            }
            if (commonRulesSupported && !gameModel.isApopalypse)
                foreach (var bonus in roundRewards) reward += bonus.GetCashForRound(index);
            double? baseline = popCash + reward;
            baselineCash += baseline;
            estimatedCash += baseline + towerPerRound;
            popTotal += popCash;
            rewardTotal += reward;
            breakdown.Add(new RoundIncomeBreakdownV1
            {
                Round = round, IncomeMultiplier = Math.Round(multiplier, 8), PopCash = ProjectionMoney(popCash),
                RoundRewards = ProjectionMoney(reward), BaselineIncome = ProjectionMoney(baseline),
                TowerIncomeEstimate = ProjectionMoney(towerPerRound)!.Value, EstimatedCashAtEnd = ProjectionMoney(estimatedCash)
            });
        }
        bool baselineSupported = unsupported.Count == 0;
        var assumptions = new List<string>
        {
            "All scheduled bloons and their descendants are popped without leaks; regrow farming and attack-dependent cash bonuses are excluded.",
            "Uses the active modified game model and zero-based native income thresholds; no per-round flooring of fractional cash.",
            "Current economic rules remain unchanged throughout the projection."
        };
        if (fromRound != currentRound) assumptions.Add("Hypothetical starting round and cash supplied by the caller; intervening income is not inferred.");
        return SuccessResult(request, new ProjectedCashResultV1
        {
            FromRound = fromRound, TargetRound = targetRound, StartingCash = startingCash,
            Confidence = !baselineSupported ? "unsupported" : towers.Count > 0 ? "estimated" : "supported",
            Assumptions = assumptions, UnsupportedReasons = unsupported,
            BaselineIncome = new BaselineIncomeV1
            {
                Confidence = baselineSupported ? "supported" : "unsupported", PopCash = ProjectionMoney(popTotal),
                RoundRewards = ProjectionMoney(rewardTotal), Total = ProjectionMoney(popTotal + rewardTotal),
                CashAtStartOfTargetRound = ProjectionMoney(baselineAtTarget), CashAtEndOfTargetRound = ProjectionMoney(baselineCash)
            },
            TowerIncome = new TowerIncomeProjectionV1
            {
                Confidence = towers.Count > 0 ? "estimated" : "supported", EstimatedTotal = ProjectionMoney(towerPerRound * (targetRound - fromRound + 1))!.Value,
                Assumptions = towerAssumptions, Towers = towerEstimates
            },
            EstimatedCashAtStartOfTargetRound = ProjectionMoney(estimatedAtTarget), EstimatedCashAtEndOfTargetRound = ProjectionMoney(estimatedCash),
            Breakdown = breakdown,
            IncomeThresholds = incomeSet.thresholds.Select(value => new IncomeThresholdV1 { LastRound = value.threshold, Multiplier = Math.Round(value.multiplier, 8) }).ToList(),
            FinalIncomeMultiplier = Math.Round(incomeSet.finalMultiplier, 8)
        });
    }
}
