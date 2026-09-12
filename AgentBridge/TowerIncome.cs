using System;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static object? ExtractTowerIncomeStats(TowerModel? tower)
    {
        if (tower?.behaviors == null) return null;

        // 1. PerRoundCashBonusTowerModel (Marketplace, Central Market, Merchantman, etc.)
        foreach (var b in tower.behaviors)
        {
            var prc = b?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.PerRoundCashBonusTowerModel>();
            if (prc != null)
            {
                return new
                {
                    IncomeType = "passive_per_round",
                    CashPerRound = prc.cashPerRound,
                    DistributeCash = prc.distributeCash,
                    Description = $"Generates ${prc.cashPerRound:0} cash directly at the end of each round."
                };
            }
        }

        // 2. BankModel (Monkey Bank, IMF Loan, Monkey-Nomics)
        foreach (var b in tower.behaviors)
        {
            var bank = b?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.BankModel>();
            if (bank != null)
            {
                return new
                {
                    IncomeType = "bank",
                    CashPerRound = 0f,
                    Capacity = bank.capacity,
                    Interest = bank.interest,
                    AutoCollect = bank.autoCollect,
                    Description = $"Monkey Bank stores income up to ${bank.capacity:0} with {bank.interest * 100:0}% compound interest per round. Requires collection."
                };
            }
        }

        // 3. AttackModel producing bananas/crates (Top path Banana Farm)
        foreach (var b in tower.behaviors)
        {
            var attack = b?.TryCast<AttackModel>();
            if (attack?.weapons == null) continue;
            foreach (var w in attack.weapons)
            {
                if (w?.behaviors == null) continue;
                foreach (var wb in w.behaviors)
                {
                    var eprm = wb?.TryCast<Il2CppAssets.Scripts.Models.Towers.Weapons.Behaviors.EmissionsPerRoundFilterModel>();
                    if (eprm != null && w.projectile?.behaviors != null)
                    {
                        foreach (var pb in w.projectile.behaviors)
                        {
                            var cm = pb?.TryCast<Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors.CashModel>();
                            if (cm != null)
                            {
                                float cashPerItem = cm.maximum > 0 ? cm.maximum : cm.minimum;
                                float total = eprm.count * cashPerItem;
                                return new
                                {
                                    IncomeType = "ground_drops",
                                    CashPerRound = total,
                                    CountPerRound = eprm.count,
                                    CashPerItem = cashPerItem,
                                    Description = $"Produces {eprm.count} bananas/crates per round worth ${cashPerItem:0} each (${total:0}/round)."
                                };
                            }
                        }
                    }
                }
            }
        }

        // 4. AbilityModel containing CashPerTowerInRangeModel (Druid 0-4-0 Jungle's Bounty, 0-5-0 Spirit of the Forest)
        foreach (var b in tower.behaviors)
        {
            var ability = b?.TryCast<AbilityModel>();
            if (ability?.behaviors == null) continue;
            foreach (var ab in ability.behaviors)
            {
                var cpt = ab?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities.Behaviors.CashPerTowerInRangeModel>();
                if (cpt != null)
                {
                    var targets = cpt.towerIds != null ? cpt.towerIds.ToArray() : Array.Empty<string>();
                    return new
                    {
                        IncomeType = "ability_activated",
                        CashPerRound = cpt.baseCash,
                        BaseCash = cpt.baseCash,
                        MaxCashGeneration = cpt.maxCashGeneration,
                        TargetTowers = targets,
                        CooldownSeconds = ability.cooldown,
                        Description = $"Generates ${cpt.baseCash:0} base cash when ability '{ability.displayName}' is activated, plus bonus cash for each Banana Farm in range (up to ${cpt.maxCashGeneration:0} max)."
                    };
                }
            }
        }

        // 5. MonkeyCityIncomeSupportModel (Monkey Village 0-0-4)
        foreach (var b in tower.behaviors)
        {
            var mc = b?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.MonkeyCityIncomeSupportModel>();
            if (mc != null)
            {
                return new
                {
                    IncomeType = "support_buff",
                    CashPerRound = 0f,
                    IncomeModifier = mc.incomeModifier,
                    Description = $"Increases cash generation of nearby Banana Farms by {mc.incomeModifier * 100:0}%."
                };
            }
        }

        return null;
    }
}
