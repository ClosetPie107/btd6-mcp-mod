using System;
using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppNinjaKiwi.Localization;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static float? GetAvailableUpgradeCost(TowerToSimulation tower, int path, GameModel? gameModel)
    {
        var def = tower.Def;
        if (tower.hero != null || tower.IsNotUpgradeable || def?.tiers == null || def.tiers.Length <= path || def.upgrades == null)
            return null;
        int tier = def.tiers[path];
        var bridge = InGame.instance?.bridge;
        // The bridge takes the target tier and applies hero/knowledge overrides;
        // raw inventory limits alone would incorrectly reject Silas's extra Ice.
        if (tier >= 5 || bridge == null || bridge.IsUpgradeLocked(tower.Id, path, tier + 1)
            || tower.IsUpgradeBlocked(path, tier + 1, out _))
            return null;
        float? baseCost = null;
        foreach (var next in def.upgrades)
        {
            var upgrade = gameModel?.GetUpgrade(next.upgrade);
            if (upgrade?.path != path) continue;
            baseCost = upgrade.cost;
            break;
        }
        if (baseCost == null) return null;
        // overrideBaseCost is the input price, not an optional zero placeholder.
        // Native pricing then applies the placed tower's current modifiers.
        float cost = tower.GetUpgradeCost(path, tier + 1, baseCost.Value, false);
        // Native unavailable-path sentinels overflow the game's integer rounding.
        // Zero remains a valid free upgrade; never replace it with a catalog price.
        if (!float.IsFinite(cost) || cost < 0 || cost >= int.MaxValue)
            return null;
        int rounded = Il2CppAssets.Scripts.Simulation.SMath.Math.RoundToNearestInt(cost, 5);
        return rounded >= 0 ? rounded : null;
    }

    private static string? GetNextUpgradeName(TowerToSimulation tower, int path)
    {
        var def = tower.Def;
        if (def?.tiers == null || def.tiers.Length <= path)
            return null;
        var upgrade = def.GetUpgrade(path, def.tiers[path] + 1);
        if (upgrade == null)
            return null;
        string key = string.IsNullOrEmpty(upgrade.localizedNameOverride) ? upgrade.name : upgrade.localizedNameOverride;
        var localization = LocalizationManager.Instance;
        return localization != null && localization.TryGetTextEnglish(key, out string localized) && !string.IsNullOrWhiteSpace(localized)
            ? localized
            : key;
    }

    private static int? GetCrosspathSlotsRemaining(int[] tiers)
    {
        int mainPath = Array.FindIndex(tiers, tier => tier >= 3);
        if (mainPath < 0)
            return null;
        int crosspathTier = 0;
        for (int path = 0; path < tiers.Length; path++)
            if (path != mainPath)
                crosspathTier = Math.Max(crosspathTier, tiers[path]);
        return Math.Max(0, 2 - crosspathTier);
    }

    private static TowerInfoV1 ConvertTower(TowerToSimulation tower, GameModel? gameModel)
    {
        var def = tower.Def;
        bool isHero = tower.hero != null;

        var upgradeCosts = new float?[3];
        var nextUpgrades = new NextUpgradeInfoV1[3];
        for (int path = 0; path < 3; path++)
        {
            float? cost = null;
            if (!isHero && def?.tiers != null)
            {
                try { cost = GetAvailableUpgradeCost(tower, path, gameModel); }
                catch { }
            }
            upgradeCosts[path] = cost;
            nextUpgrades[path] = new NextUpgradeInfoV1
            {
                Path = path,
                Name = isHero ? null : GetNextUpgradeName(tower, path),
                Cost = cost,
                Available = cost.HasValue
            };
        }

        string targetPriority = "First";
        try
        {
            targetPriority = tower.TargetType?.id ?? "First";
        }
        catch { }

        bool? isSubmerged = null;
        Position2DV1? targetPosition = null;
        BankInfoV1? bankInfo = null;
        try
        {
            var simTower = tower.GetSimTower();
            if (simTower?.Behaviors?.list != null)
            {
                var bList = simTower.Behaviors.list;
                for (int i = 0; i < bList.Count; i++)
                {
                    var b = bList[i];
                    if (b == null) continue;

                    var sub = b.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Submerge>();
                    if (sub != null)
                    {
                        isSubmerged = sub.isSubmerged;
                    }

                    var bank = b.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Bank>();
                    if (bank != null)
                    {
                        bankInfo = new BankInfoV1
                        {
                            Cash = (float)Math.Round(bank.Cash, 1),
                            Capacity = bank.bankModel != null ? bank.bankModel.capacity : 0f,
                            Interest = bank.bankModel != null ? bank.bankModel.interest : 0f,
                            IsFull = bank.IsAtMaxCapacity
                        };
                    }

                    var atk = b.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Attack>();
                    if (atk?.attackBehaviors != null)
                    {
                        var atkList = atk.attackBehaviors;
                        for (int j = 0; j < atkList.Count; j++)
                        {
                            var tsp = atkList[j]?.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Behaviors.TargetSelectedPoint>();
                            if (tsp != null && tsp.hasValidPoint)
                            {
                                targetPosition = new Position2DV1
                                {
                                    X = (float)Math.Round(tsp.targetPoint.x, 1),
                                    Y = (float)Math.Round(tsp.targetPoint.y, 1)
                                };
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        float cashEarned = 0f;
        try
        {
            cashEarned = tower.cashEarned;
        }
        catch { }

        var position = tower.simPosition;
        return new TowerInfoV1
        {
            Id = tower.Id.ToString(),
            TowerType = def?.baseId ?? "",
            Name = def?.name ?? "",
            Position = new Position2DV1
            {
                X = position.x,
                Y = position.y
            },
            Range = def?.range ?? 0f,
            TargetPriority = targetPriority,
            Tiers = def?.tiers != null ? def.tiers.ToArray() : [0, 0, 0],
            UpgradeCosts = upgradeCosts,
            NextUpgrades = nextUpgrades,
            CrosspathSlotsRemaining = !isHero && def?.tiers != null ? GetCrosspathSlotsRemaining(def.tiers.ToArray()) : null,
            SellValue = tower.sellFor,
            DamageDealt = tower.damageDealt,
            Pops = tower.pops,
            CashEarned = (float)Math.Round(cashEarned, 1),
            Bank = bankInfo,
            IsHero = isHero,
            IsSubmerged = isSubmerged,
            TargetPosition = targetPosition
        };
    }

    private static BridgeResultV1 HandleTowerCatalog(BridgeRequestV1 request)
    {
        var gameModel = InGame.instance?.GetGameModel() ?? Game.instance?.model;
        if (gameModel?.towerSet == null)
            return ErrorResult(request, "GAME_MODEL_UNAVAILABLE", "The BTD6 game model is not available.", true);

        var towers = new List<object>();
        foreach (var detail in gameModel.towerSet.OrderBy(detail => detail.towerId, StringComparer.Ordinal))
        {
            if (detail == null || string.IsNullOrEmpty(detail.towerId))
                continue;

            var tower = gameModel.GetTower(detail.towerId, 0, 0, 0);
            if (tower == null)
                continue;

            var upgrades = new List<object>();
            for (int path = 0; path < 3; path++)
            {
                for (int tier = 1; tier <= 5; tier++)
                {
                    var upgrade = tower.GetUpgrade(path, tier);
                    if (upgrade == null)
                        continue;

                    upgrades.Add(new
                    {
                        UpgradeId = upgrade.name,
                        Path = upgrade.path,
                        Tier = upgrade.tier,
                        Cost = upgrade.cost,
                        Name = string.IsNullOrEmpty(upgrade.localizedNameOverride) ? upgrade.name : upgrade.localizedNameOverride
                    });
                }
            }

            towers.Add(new
            {
                TowerType = tower.baseId,
                Name = tower.name,
                BaseCost = tower.cost,
                BaseRange = tower.range,
                IsWaterBased = tower.IsWaterBased(),
                IsAmphibious = tower.IsAmphibiousBased(),
                Upgrades = upgrades
            });
        }

        return SuccessResult(request, new
        {
            Source = "live-game-model",
            Btd6Version = Application.version,
            Towers = towers
        });
    }
}
