using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int FreeTier5Count = 3;
    private const int GeraldoTotemPower = 2_000;
    private const string ParagonPowerTotemId = "ParagonPowerTotem";
    private const string ParagonPowerTotemTowerId = "ParagonPowerTotemTower";

    private static BridgeResultV1 HandleProjectParagonDegree(BridgeRequestV1 request)
    {

        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Paragon projection requires an active match.", false);

        string paragonType = ReadString(request.Payload, "paragonType", "");
        if (string.IsNullOrWhiteSpace(paragonType))
            paragonType = ReadString(request.Payload, "towerType", "");

        paragonType = paragonType.Trim();
        if (string.IsNullOrEmpty(paragonType))
            return ErrorResult(request, "INVALID_ARGUMENT", "paragonType must be provided as a native base tower ID (for example, 'DartMonkey' or 'DartMonkey-Paragon').", false);

        const string paragonSuffix = "-Paragon";
        string baseId = paragonType.EndsWith(paragonSuffix, StringComparison.OrdinalIgnoreCase)
            ? paragonType[..^paragonSuffix.Length]
            : paragonType;
        if (string.IsNullOrEmpty(baseId))
            return ErrorResult(request, "INVALID_ARGUMENT", "paragonType must contain a native base tower ID before the '-Paragon' suffix.", false);

        double additionalCashSlider = 0d;
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("additionalCashSlider", out var sliderProp))
        {
            if (sliderProp.ValueKind != JsonValueKind.Number || !sliderProp.TryGetDouble(out additionalCashSlider) ||
                !double.IsFinite(additionalCashSlider) || additionalCashSlider < 0d ||
                Math.Truncate(additionalCashSlider) != additionalCashSlider)
            {
                return ErrorResult(request, "INVALID_ARGUMENT", "additionalCashSlider must be a finite non-negative whole amount of cash.", false);
            }
        }

        var excludedIds = new HashSet<string>(StringComparer.Ordinal);
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("excludeTowerIds", out var excProp))
        {
            if (excProp.ValueKind != JsonValueKind.Array)
                return ErrorResult(request, "INVALID_ARGUMENT", "excludeTowerIds must be an array of non-empty tower IDs.", false);

            foreach (var item in excProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return ErrorResult(request, "INVALID_ARGUMENT", "excludeTowerIds must contain only non-empty strings.", false);

                string id = (item.GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(id) || id.Length > 128)
                    return ErrorResult(request, "INVALID_ARGUMENT", "excludeTowerIds must contain non-empty IDs of at most 128 characters.", false);
                excludedIds.Add(id);
            }
        }

        var gameModel = inGame.GetGameModel();
        if (gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "The active game model is unavailable; paragon projection cannot validate the native paragon family.", true);

        var paragonUpgrade = gameModel.GetParagonUpgradeForTowerId(baseId);
        var paragonTowerModel = gameModel.GetParagonTower(baseId);
        if (paragonUpgrade == null || !paragonUpgrade.IsParagon || paragonTowerModel == null || !paragonTowerModel.isParagon)
        {
            return ErrorResult(request, "UNSUPPORTED_PARAGON_TYPE",
                $"'{paragonType}' is not a native paragon family in the active game model. Use the base tower ID for a tower with a native paragon upgrade.",
                false,
                new { BaseId = baseId });
        }

        var paragonData = gameModel.paragonDegreeDataModel;
        if (paragonData == null || paragonData.powerDegreeRequirements == null)
            return ErrorResult(request, "PARAGON_DATA_UNAVAILABLE", "The active game model did not provide native paragon degree data.", false);

        int degreeCount = paragonData.degreeCount;
        int[] reqs = paragonData.powerDegreeRequirements.ToArray();
        if (degreeCount != 100 || reqs.Length < degreeCount)
            return ErrorResult(request, "PARAGON_DATA_INVALID",
                $"Native paragon degree data has an invalid degree table (degreeCount={degreeCount}, requirements={reqs.Length}).", false);

        for (int i = 0; i < degreeCount; i++)
        {
            if (reqs[i] < 0 || (i > 0 && reqs[i] < reqs[i - 1]))
                return ErrorResult(request, "PARAGON_DATA_INVALID", "Native paragon degree requirements must be non-negative and monotonic.", false);
        }

        int maxPowerFromPops = paragonData.maxPowerFromPops;
        int maxPowerFromMoneySpent = paragonData.maxPowerFromMoneySpent;
        int maxPowerFromNonTier5Count = paragonData.maxPowerFromNonTier5Count;
        int maxPowerFromTier5Count = paragonData.maxPowerFromTier5Count;
        float popsDivider = paragonData.popsOverX;
        float tiersMultiplier = paragonData.nonTier5TowersMultByX;
        float t5Multiplier = paragonData.tier5TowersMultByX;
        float cashEarnedModifier = paragonData.cashEarnedContributionModifier;
        double cashPowerNumerator = paragonData.moneySpentOverX;
        double sliderMarkup = 1d + paragonData.paidContributionPenalty;
        if (maxPowerFromPops < 0 || maxPowerFromMoneySpent < 0 || maxPowerFromNonTier5Count < 0 ||
            maxPowerFromTier5Count < 0 || !float.IsFinite(popsDivider) || popsDivider <= 0f ||
            !float.IsFinite(tiersMultiplier) || tiersMultiplier < 0f ||
            !float.IsFinite(t5Multiplier) || t5Multiplier < 0f ||
            !float.IsFinite(cashEarnedModifier) || cashEarnedModifier < 0f ||
            !double.IsFinite(cashPowerNumerator) || cashPowerNumerator <= 0d ||
            !double.IsFinite(sliderMarkup) || sliderMarkup <= 0d)
        {
            return ErrorResult(request, "PARAGON_DATA_INVALID",
                "Native paragon contribution parameters are invalid.", false);
        }

        var allTowers = inGame.GetAllTowerToSim();
        if (allTowers == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "The active simulation did not provide placed towers for paragon projection.", true);

        var eligibleTowers = new List<(string Id, string Name, bool IsTier5, long Damage, float Cash, float Worth, int TierTotal, TowerToSimulation Tower)>();
        int totemCount = 0;
        long totalDamage = 0;
        foreach (var tower in allTowers)
        {
            if (tower == null || tower.Def == null) continue;

            string id = tower.Id.ToString();
            if (excludedIds.Contains(id)) continue;

            var def = tower.Def;
            string tBase = def.baseId ?? "";
            string tName = def.name ?? "";
            if (string.Equals(tBase, ParagonPowerTotemId, StringComparison.Ordinal) ||
                string.Equals(tBase, ParagonPowerTotemTowerId, StringComparison.Ordinal) ||
                string.Equals(tName, ParagonPowerTotemId, StringComparison.Ordinal) ||
                string.Equals(tName, ParagonPowerTotemTowerId, StringComparison.Ordinal))
            {
                totemCount++;
                continue;
            }

            // Native paragon sacrifice matching is by the tower model base ID.
            // Name-prefix matching would admit transformed or unrelated models.
            if (!string.Equals(tBase, baseId, StringComparison.Ordinal)) continue;
            if (tower.IsParagon || def.isParagon || def.isSubTower) continue;

            var sim = tower.GetSimTower();
            if (sim == null)
                return ErrorResult(request, "TOWER_STATE_UNAVAILABLE",
                    $"Native simulation state for eligible tower '{id}' is unavailable; refusing to undercount its contribution.", true);

            long damage = sim.damageDealt;
            float cash = sim.cashEarned;
            float worth = sim.worth;
            if (damage < 0 || !float.IsFinite(cash) || cash < 0f || !float.IsFinite(worth) || worth < 0f)
            {
                return ErrorResult(request, "TOWER_STATE_INVALID",
                    $"Native contribution state for eligible tower '{id}' is invalid.", false);
            }
            if (damage > long.MaxValue - totalDamage)
                return ErrorResult(request, "TOWER_STATE_INVALID", "The sum of eligible tower damage exceeds the native integer range.", false);
            totalDamage += damage;

            var tiers = def.tiers;
            if (tiers == null || tiers.Length < 3)
                return ErrorResult(request, "TOWER_STATE_INVALID",
                    $"Native upgrade tiers for eligible tower '{id}' are unavailable.", false);

            int tierTotal = 0;
            bool isT5 = def.tier >= 5;
            for (int path = 0; path < tiers.Length; path++)
            {
                int tier = tiers[path];
                if (tier < 0 || tier > 5)
                    return ErrorResult(request, "TOWER_STATE_INVALID",
                        $"Native upgrade tier {tier} for eligible tower '{id}' is outside the supported 0..5 range.", false);
                tierTotal += tier;
                isT5 |= tier >= 5;
            }

            eligibleTowers.Add((id, tName, isT5, damage, cash, worth, tierTotal, tower));
        }

        var priceCandidate = eligibleTowers.FirstOrDefault(tower => tower.IsTier5);
        if (priceCandidate.Tower == null)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE",
                "A native Tier 5 tower is required to resolve the active paragon price through the simulation pricing API.", false);

        int pricePath = -1;
        var priceTiers = priceCandidate.Tower.Def?.tiers;
        if (priceTiers != null)
        {
            for (int path = 0; path < priceTiers.Length; path++)
            {
                if (priceTiers[path] >= 5)
                {
                    pricePath = path;
                    break;
                }
            }
        }
        if (pricePath < 0 || !float.IsFinite(paragonUpgrade.cost) || paragonUpgrade.cost <= 0f)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE",
                "The native paragon upgrade price or the selected Tier 5 pricing path is unavailable.", false);

        float rawParagonPrice;
        try
        {
            rawParagonPrice = priceCandidate.Tower.GetUpgradeCost(pricePath, 6, paragonUpgrade.cost, true);
        }
        catch (Exception exception)
        {
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE",
                $"Native paragon price lookup failed: {exception.Message}", true);
        }
        if (!float.IsFinite(rawParagonPrice) || rawParagonPrice <= 0f || rawParagonPrice >= int.MaxValue)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE", "Native paragon price lookup returned an invalid value.", false);

        int paragonPrice = Il2CppAssets.Scripts.Simulation.SMath.Math.RoundToNearestInt(rawParagonPrice, 5);
        if (paragonPrice <= 0)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE", "Native paragon price lookup rounded to an invalid value.", false);

        double pricePerSacrificePower = cashPowerNumerator / paragonPrice;
        double sliderPowerPerCash = pricePerSacrificePower / sliderMarkup;
        double maxSliderCashRaw = (double)maxPowerFromMoneySpent * paragonPrice * sliderMarkup / cashPowerNumerator;
        if (!double.IsFinite(pricePerSacrificePower) || !double.IsFinite(sliderPowerPerCash) || sliderPowerPerCash <= 0d ||
            !double.IsFinite(maxSliderCashRaw) || maxSliderCashRaw < 0d || maxSliderCashRaw > int.MaxValue)
        {
            return ErrorResult(request, "PARAGON_DATA_INVALID", "Native paragon price-scaled cash parameters exceed the supported range.", false);
        }
        int maxSliderCash = (int)Math.Floor(maxSliderCashRaw);
        if (additionalCashSlider > maxSliderCash)
        {
            return ErrorResult(request, "CASH_SLIDER_OVER_CAP",
                $"additionalCashSlider cannot exceed the native cash-contribution cap of ${maxSliderCash}.",
                false,
                new { MaxCash = maxSliderCash, ParagonPrice = paragonPrice });
        }

        var moneyExcludedT5Ids = eligibleTowers
            .Where(tower => tower.IsTier5)
            .OrderBy(tower => tower.Worth)
            .ThenBy(tower => tower.Id, StringComparer.Ordinal)
            .Take(FreeTier5Count)
            .Select(tower => tower.Id)
            .ToHashSet(StringComparer.Ordinal);

        double totalCashEarned = 0d;
        double sacrificedMoneySpent = 0d;
        int nonT5Tiers = 0;
        int t5Count = 0;
        var sacrificedTowers = new List<object>();
        var t5TowerList = new List<string>();
        foreach (var tower in eligibleTowers)
        {
            totalCashEarned += tower.Cash;
            bool excludedFromMoney = moneyExcludedT5Ids.Contains(tower.Id);
            if (!excludedFromMoney) sacrificedMoneySpent += tower.Worth;
            if (tower.IsTier5)
            {
                t5Count++;
                t5TowerList.Add(tower.Name);
            }
            else
            {
                nonT5Tiers += tower.TierTotal;
            }

            sacrificedTowers.Add(new
            {
                Id = tower.Id,
                Name = tower.Name,
                IsTier5 = tower.IsTier5,
                ExcludedFromMoneyContribution = excludedFromMoney,
                DamageDealt = tower.Damage,
                CashEarned = MathF.Round(tower.Cash, 1),
                Worth = MathF.Round(tower.Worth, 1)
            });
        }

        if (!double.IsFinite(totalCashEarned) || !double.IsFinite(sacrificedMoneySpent))
            return ErrorResult(request, "TOWER_STATE_INVALID", "Native tower contribution totals are outside the supported numeric range.", false);

        double combinedPopScore = totalDamage + totalCashEarned * cashEarnedModifier;
        float popsPower = CappedParagonPower(combinedPopScore, popsDivider, maxPowerFromPops);
        double sacrificedMoneyRawPower = sacrificedMoneySpent * pricePerSacrificePower;
        float sacrificedMoneyPower = CappedParagonPower(sacrificedMoneyRawPower, 1d, maxPowerFromMoneySpent);
        float sliderPower = CappedParagonPower(Math.Ceiling(additionalCashSlider * sliderPowerPerCash), 1d, maxPowerFromMoneySpent);
        float moneyPower = ParagonCashPower(sacrificedMoneyRawPower, additionalCashSlider, sliderPowerPerCash, maxPowerFromMoneySpent);
        float tiersPower = CappedParagonPower(nonT5Tiers * (double)tiersMultiplier, 1d, maxPowerFromNonTier5Count);
        int extraT5Count = Math.Max(0, t5Count - FreeTier5Count);
        float t5Power = CappedParagonPower(extraT5Count * (double)t5Multiplier, 1d, maxPowerFromTier5Count);
        long totemPowerLong = (long)totemCount * GeraldoTotemPower;
        if (totemPowerLong > int.MaxValue)
            return ErrorResult(request, "PARAGON_POWER_OVERFLOW", "Projected native paragon power exceeds the supported integer range.", false);

        int totemPower = (int)totemPowerLong;
        float totalPower = popsPower + moneyPower + tiersPower + t5Power + totemPower;
        int degree = DegreeForPower(totalPower, reqs, degreeCount);
        int? nextDegreeAtPower = degree < degreeCount ? reqs[degree] : null;
        float? powerNeededForNext = nextDegreeAtPower.HasValue
            ? Math.Max(0, nextDegreeAtPower.Value - totalPower)
            : null;

        int[] milestones = [20, 40, 60, 76, 91, 100];
        var milestoneResults = new Dictionary<string, object>();
        foreach (int m in milestones)
        {
            int targetPower = reqs[m - 1];
            float deficit = Math.Max(0, targetPower - totalPower);
            int? cashSliderNeeded = deficit == 0 ? 0 : ParagonSliderCashNeeded(
                moneyPower + deficit, sacrificedMoneyRawPower, (int)additionalCashSlider,
                maxSliderCash, sliderPowerPerCash, maxPowerFromMoneySpent);
            long popsNeeded = deficit == 0 ? 0 : CeilingAdditionalCost(
                (double)popsPower + deficit, combinedPopScore, popsDivider);
            float remainingPopsPower = Math.Max(0, maxPowerFromPops - popsPower);
            milestoneResults[$"degree{m}"] = new
            {
                Reached = deficit == 0,
                TargetPower = targetPower,
                PowerDeficit = deficit,
                CashSliderNeeded = cashSliderNeeded,
                CashSliderReachable = cashSliderNeeded.HasValue,
                PopsNeeded = popsNeeded,
                PopsReachable = deficit == 0 || deficit <= remainingPopsPower
            };
        }

        var sliderTestAmounts = new SortedSet<int> { 0, maxSliderCash };
        foreach (int testCash in new[] { 25_000, 50_000, 100_000, 200_000 })
        {
            if (testCash <= maxSliderCash) sliderTestAmounts.Add(testCash);
        }

        var sensitivity = new List<object>();
        foreach (int testCash in sliderTestAmounts)
        {
            float testSliderPower = CappedParagonPower(Math.Ceiling(testCash * sliderPowerPerCash), 1d, maxPowerFromMoneySpent);
            float testMoneyPower = ParagonCashPower(sacrificedMoneyRawPower, testCash, sliderPowerPerCash, maxPowerFromMoneySpent);
            float testTotal = popsPower + testMoneyPower + tiersPower + t5Power + totemPower;
            int testDeg = DegreeForPower(testTotal, reqs, degreeCount);
            sensitivity.Add(new
            {
                SliderCash = testCash,
                SliderPower = testSliderPower,
                Degree = testDeg,
                TotalPower = testTotal
            });
        }

        return SuccessResult(request, new
        {
            ParagonType = baseId,
            ParagonPrice = paragonPrice,
            FormulaConfidence = "native_56_3_verified_rounding",
            PurchaseReadiness = "not_evaluated",
            Warnings = new[]
            {
                "Native 56.3 power rounding is modeled; player cash, UI state, and purchase locks are not evaluated."
            },
            ProjectedDegree = degree,
            TotalPower = totalPower,
            NextDegreeAtPower = nextDegreeAtPower,
            PowerNeededForNextDegree = powerNeededForNext,
            EligibleTowerCount = sacrificedTowers.Count,
            Contributions = new
            {
                PopsAndCash = new
                {
                    TotalDamage = totalDamage,
                    TotalCashEarned = Math.Round(totalCashEarned, 1),
                    CombinedScore = Math.Round(combinedPopScore, 1),
                    PowerEarned = popsPower,
                    MaxPower = maxPowerFromPops,
                    PercentOfMax = MathF.Round((float)popsPower / Math.Max(1, maxPowerFromPops) * 100f, 1)
                },
                SacrificedMoney = new
                {
                    RawCashSpent = Math.Round(sacrificedMoneySpent, 1),
                    PricePerPower = 1d / pricePerSacrificePower,
                    PowerBeforeCategoryCap = sacrificedMoneyRawPower,
                    PowerEarned = sacrificedMoneyPower,
                    MaxPower = maxPowerFromMoneySpent
                },
                MoneySpentCombined = new
                {
                    SacrificedPower = sacrificedMoneyRawPower,
                    SliderPower = sliderPower,
                    PowerEarned = moneyPower,
                    MaxPower = maxPowerFromMoneySpent,
                    PercentOfMax = MathF.Round((float)moneyPower / Math.Max(1, maxPowerFromMoneySpent) * 100f, 1)
                },
                SacrificedNonT5Tiers = new
                {
                    RawTiers = nonT5Tiers,
                    PowerEarned = tiersPower,
                    MaxPower = maxPowerFromNonTier5Count,
                    PercentOfMax = MathF.Round((float)tiersPower / Math.Max(1, maxPowerFromNonTier5Count) * 100f, 1)
                },
                AdditionalTier5s = new
                {
                    TotalT5Count = t5Count,
                    ExtraT5Count = extraT5Count,
                    FreeTier5Count,
                    PowerEarned = t5Power,
                    MaxPower = maxPowerFromTier5Count,
                    Tiers5Towers = t5TowerList.ToArray()
                },
                GeraldoTotems = new
                {
                    Count = totemCount,
                    PowerPerTotem = GeraldoTotemPower,
                    PowerEarned = totemPower
                },
                CashSlider = new
                {
                    CashInvested = additionalCashSlider,
                    MaxCash = maxSliderCash,
                    PowerPerCash = sliderPowerPerCash,
                    Markup = sliderMarkup,
                    PowerEarned = sliderPower
                }
            },
            MilestoneProjections = milestoneResults,
            SliderSensitivity = sensitivity.ToArray(),
            SacrificedTowers = sacrificedTowers.ToArray(),
            ObservedAtUtc = DateTime.UtcNow
        });
    }

}
