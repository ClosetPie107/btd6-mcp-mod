using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2Cpp;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static object BuildTowerStats(TowerModel tower)
    {
        var attacks = new List<object>();
        var camoCapabilities = new List<CamoCapabilityInfoV1>();
        var camoEvidence = new List<string>();
        string detectionSource;
        bool? towerCanDetectCamo = GetModelCamoDetection(tower, camoEvidence, out detectionSource);
        bool towerCanPopLead = false;
        bool towerCanPopPurple = false;
        bool towerCanPopBlack = false;
        bool towerCanPopWhite = false;
        bool towerCanPopFrozen = false;
        bool? towerCanDamageDdt = false;
        bool hasAnyDamagingWeapon = false;

        if (tower.behaviors != null)
        {
            foreach (var behavior in tower.behaviors)
            {
                var attack = behavior?.TryCast<AttackModel>();
                if (attack == null)
                    continue;

                var weapons = new List<object>();
                if (attack.weapons != null)
                {
                    foreach (var weapon in attack.weapons)
                    {
                        var projectile = weapon?.projectile;
                        var damageTraversal = ExtractDamageSourceTraversal(projectile);
                        var (damage, effProj) = ResolveDamageAndProjectile(projectile, damageTraversal);
                        var targetProj = effProj ?? projectile;
                        var immune = damage?.immuneBloonProperties ?? BloonProperties.None;
                        var camo = BuildCamoCapability(
                            tower,
                            attack,
                            projectile,
                            damageTraversal,
                            null,
                            null);
                        camoCapabilities.Add(camo);

                        bool? weaponCanDamageDdt = DetermineCanDamageDdt(
                            tower,
                            attack,
                            damageTraversal,
                            null,
                            null,
                            null);
                        if (damage != null && damage.damage > 0f)
                        {
                            hasAnyDamagingWeapon = true;
                            if ((immune & BloonProperties.Lead) == 0) towerCanPopLead = true;
                            if ((immune & BloonProperties.Purple) == 0) towerCanPopPurple = true;
                            if ((immune & BloonProperties.Black) == 0) towerCanPopBlack = true;
                            if ((immune & BloonProperties.White) == 0) towerCanPopWhite = true;
                            if ((immune & BloonProperties.Frozen) == 0) towerCanPopFrozen = true;

                            if (weaponCanDamageDdt == true)
                                towerCanDamageDdt = true;
                            else if (weaponCanDamageDdt == null && towerCanDamageDdt != true)
                                towerCanDamageDdt = null;
                        }

                        weapons.Add(new
                        {
                            Name = weapon?.name ?? "",
                            DamageType = GetDamageTypeLabel(immune),
                            CooldownSeconds = weapon?.rate ?? 0f,
                            ProjectileId = targetProj?.id ?? "",
                            Pierce = targetProj?.pierce ?? 0f,
                            MaxPierce = targetProj?.maxPierce ?? 0f,
                            Damage = damage?.damage ?? 0f,
                            MaxDamage = damage?.maxDamage ?? 0f,
                            Camo = camo,
                            CanDamageDdt = weaponCanDamageDdt,
                            BlockedBy = GetBlockedBloonTypes(immune),
                            DamageModifiers = ExtractDamageModifiers(targetProj),
                            DamageSources = damageTraversal.Sources,
                            DamageCoverage = BuildDamageCoverage(damageTraversal),
                            AppliedDebuffs = ExtractAppliedDebuffs(targetProj),
                            ProjectileBehaviors = targetProj?.behaviors?
                                .Where(model => model != null)
                                .Select(NativeTypeName)
                                .Distinct()
                                .ToArray() ?? []
                        });
                    }
                }

                attacks.Add(new
                {
                    Name = attack.name,
                    Range = attack.range,
                    AttackThroughWalls = attack.attackThroughWalls,
                    FireWithoutTarget = attack.fireWithoutTarget,
                    TargetProvider = attack.targetProvider?.name ?? "",
                    Weapons = weapons
                });
            }
        }

        var towerCamo = SummarizeCamoCapabilities(camoCapabilities, towerCanDetectCamo, detectionSource, camoEvidence);
        bool? effectiveCanDamageDdt = towerCanDamageDdt;
        var towerImmuneBloons = new List<string>();
        if (hasAnyDamagingWeapon)
        {
            if (!towerCanPopLead) towerImmuneBloons.Add("Lead");
            if (!towerCanPopPurple) towerImmuneBloons.Add("Purple");
            if (!towerCanPopBlack) towerImmuneBloons.Add("Black");
            if (!towerCanPopWhite) towerImmuneBloons.Add("White");
            if (!towerCanPopFrozen) towerImmuneBloons.Add("Frozen");
        }

        return new
        {
            TowerType = tower.baseId,
            Name = tower.name,
            Tiers = tower.tiers?.ToArray() ?? [0, 0, 0],
            BaseCost = tower.cost,
            Range = tower.range,
            IsGlobalRange = tower.isGlobalRange,
            IsWaterBased = tower.IsWaterBased(),
            IsAmphibious = tower.IsAmphibiousBased(),
            Attacks = attacks,
            PoppingCapabilities = new
            {
                Camo = towerCamo,
                CanPopLead = towerCanPopLead,
                CanPopPurple = towerCanPopPurple,
                CanPopBlack = towerCanPopBlack,
                CanPopWhite = towerCanPopWhite,
                CanPopFrozen = towerCanPopFrozen,
                CanDamageDdt = effectiveCanDamageDdt,
                ImmuneBloons = towerImmuneBloons,
                DdtBlockers = BuildDdtBlockers(effectiveCanDamageDdt, towerCamo.CanDamageCamo, towerCanPopLead, towerCanPopBlack, hasAnyDamagingWeapon)
            },
            AppliedDebuffs = ExtractTowerDebuffs(tower),
            Income = ExtractTowerIncomeStats(tower),
            ParagonBossDamage = BuildParagonBossDamageMetadata(tower, null)
        };
    }
}
