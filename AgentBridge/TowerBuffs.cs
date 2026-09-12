using System;
using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2Cpp;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors;
using SimulationAttack = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Attack;
using SimulationWeapon = Il2CppAssets.Scripts.Simulation.Towers.Weapons.Weapon;
using SimulationBehaviorMutator = Il2CppAssets.Scripts.Simulation.Objects.BehaviorMutator;
using SimulationDamageMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.DamageTowerMutator.Mutator;
using SimulationPierceMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.PierceTowerMutator.Mutator;
using SimulationRangeMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.RangeTowerMutator.Mutator;
using SimulationReloadMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.ReloadTimeTowerMutator.Mutator;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static object BuildEffectiveTowerStats(Il2CppAssets.Scripts.Simulation.Towers.Tower? simTower)
    {
        if (simTower == null)
        {
            return new
            {
                Available = false,
                Reason = "Simulation tower is unavailable."
            };
        }

        // Buff mutators can alter detection, projectile filters, and damage models.
        // Refresh before reading any effective capability or weapon fields.
        try { simTower.UpdateBuffs(); } catch { }

        var baseTowerModel = simTower.rootModel?.TryCast<TowerModel>();
        var attacks = new List<object>();
        var camoCapabilities = new List<CamoCapabilityInfoV1>();
        var camoEvidence = new List<string>();
        string detectionSource;
        bool? modelDetection = GetModelCamoDetection(baseTowerModel, camoEvidence, out detectionSource);
        bool? towerCanDetectCamo = simTower.canTargetCamo;
        AddCamoEvidence(camoEvidence, $"Live Tower.canTargetCamo={towerCanDetectCamo.Value} after native buff refresh.");
        if (towerCanDetectCamo != modelDetection)
            detectionSource = "live-simulation";

        var behaviors = simTower.Behaviors?.list;
        bool towerCanPopLead = false;
        bool towerCanPopPurple = false;
        bool towerCanPopBlack = false;
        bool towerCanPopWhite = false;
        bool towerCanPopFrozen = false;
        bool? towerCanDamageDdt = false;
        bool hasAnyDamagingWeapon = false;

        if (behaviors != null)
        {
            foreach (var behavior in behaviors)
            {
                var attack = behavior?.TryCast<SimulationAttack>();
                if (attack == null)
                    continue;

                var weapons = new List<object>();
                if (attack.attackBehaviors != null)
                {
                    foreach (var attackBehavior in attack.attackBehaviors)
                    {
                        var weapon = attackBehavior?.TryCast<SimulationWeapon>();
                        if (weapon == null)
                            continue;

                        float effectiveCooldownSeconds = weapon.GetRate(true);
                        var wepModel = weapon.weaponModel;
                        var projectile = wepModel?.projectile;
                        var damageTraversal = ExtractDamageSourceTraversal(projectile);
                        var (damageModel, effProj) = ResolveDamageAndProjectile(projectile, damageTraversal);
                        var targetProj = effProj ?? projectile;

                        var immune = damageModel?.immuneBloonProperties ?? BloonProperties.None;
                        string damageType = GetDamageTypeLabel(immune);

                        var baseWep = FindBaseWeapon(baseTowerModel, wepModel?.name ?? "");
                        var baseProj = baseWep?.projectile;
                        var (baseDamageModel, effBaseProj) = ResolveDamageAndProjectile(baseProj);
                        var targetBaseProj = effBaseProj ?? baseProj;

                        float effectiveDamage = damageModel?.damage ?? 0f;
                        float baseDamage = baseDamageModel?.damage ?? effectiveDamage;
                        float effectivePierce = targetProj?.pierce ?? 0f;
                        float basePierce = targetBaseProj?.pierce ?? effectivePierce;
                        float baseCooldownSeconds = baseWep?.rate ?? (wepModel?.rate ?? 0f);
                        var camo = BuildCamoCapability(
                            baseTowerModel,
                            attack.attackModel,
                            projectile,
                            damageTraversal,
                            towerCanDetectCamo,
                            attack.cantTargetCamo,
                            baseProj);
                        camoCapabilities.Add(camo);

                        bool? weaponCanDamageDdt = DetermineCanDamageDdt(
                            baseTowerModel,
                            attack.attackModel,
                            damageTraversal,
                            towerCanDetectCamo,
                            attack.cantTargetCamo,
                            attack.cantTargetMoab);

                        if (damageModel != null && effectiveDamage > 0f)
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
                            Name = wepModel?.name ?? "",
                            DamageType = damageType,
                            BaseCooldownSeconds = baseCooldownSeconds,
                            EffectiveCooldownSeconds = effectiveCooldownSeconds,
                            BaseDamage = baseDamage,
                            EffectiveDamage = effectiveDamage,
                            BasePierce = basePierce,
                            EffectivePierce = effectivePierce,
                            Camo = camo,
                            CanDamageDdt = weaponCanDamageDdt,
                            BlockedBy = GetBlockedBloonTypes(immune),
                            DamageModifiers = ExtractDamageModifiers(targetProj),
                            DamageSources = damageTraversal.Sources,
                            DamageCoverage = BuildDamageCoverage(damageTraversal),
                            AppliedDebuffs = ExtractAppliedDebuffs(targetProj),
                            RateFrames = weapon.RateFrames,
                            IsReloadReady = weapon.IsReloadReady,
                            IsInThrow = weapon.IsInThrow
                        });
                    }
                }

                attacks.Add(new
                {
                    Name = attack.attackModel?.name ?? "",
                    Range = attack.range,
                    OnlyTargetsMoab = attack.onlyTargetsMoab,
                    CannotTargetMoab = attack.cantTargetMoab,
                    CannotTargetCamo = attack.cantTargetCamo,
                    Weapons = weapons
                });
            }
        }

        var towerImmuneBloons = new List<string>();
        if (hasAnyDamagingWeapon)
        {
            if (!towerCanPopLead) towerImmuneBloons.Add("Lead");
            if (!towerCanPopPurple) towerImmuneBloons.Add("Purple");
            if (!towerCanPopBlack) towerImmuneBloons.Add("Black");
            if (!towerCanPopWhite) towerImmuneBloons.Add("White");
            if (!towerCanPopFrozen) towerImmuneBloons.Add("Frozen");
        }

        var camoSummary = SummarizeCamoCapabilities(camoCapabilities, towerCanDetectCamo, detectionSource, camoEvidence);
        var poppingCapabilities = new
        {
            Camo = camoSummary,
            CanPopLead = towerCanPopLead,
            CanPopPurple = towerCanPopPurple,
            CanPopBlack = towerCanPopBlack,
            CanPopWhite = towerCanPopWhite,
            CanPopFrozen = towerCanPopFrozen,
            CanDamageDdt = towerCanDamageDdt,
            ImmuneBloons = towerImmuneBloons,
            DdtBlockers = BuildDdtBlockers(towerCanDamageDdt, camoSummary.CanDamageCamo, towerCanPopLead, towerCanPopBlack, hasAnyDamagingWeapon)
        };

        var activeBuffs = new List<object>();
        var seenMutatorIds = new HashSet<string>();

        if (simTower.activeBuffs != null)
        {
            foreach (var buff in simTower.activeBuffs)
            {
                var timedMutator = buff.timedMutator;
                var mutator = timedMutator?.mutator;
                string mutatorId = mutator?.id ?? "";
                if (!string.IsNullOrEmpty(mutatorId))
                    seenMutatorIds.Add(mutatorId);

                activeBuffs.Add(new
                {
                    Indicator = buff.buffIndicator?.name ?? "",
                    DisplayName = buff.buffIndicator?.buffName ?? "",
                    SourceTowerType = buff.tower?.towerModel?.baseId ?? "",
                    CanCurrentlyBuff = buff.canCurrentlyBuff,
                    CanEventuallyBuff = buff.canEventuallyBuff,
                    AvailableCount = buff.availableBuffCount,
                    UnavailableCount = buff.unavailableBuffCount,
                    MutatorId = mutatorId,
                    StackCount = mutator?.StackCount() ?? 0,
                    Timeout = timedMutator == null ? null : new
                    {
                        RemoveAtFrame = timedMutator.removeAt,
                        UsesRoundTime = timedMutator.useRoundTime,
                        OnlyTimeoutWhenActive = timedMutator.onlyTimeoutWhenActive,
                        RoundsRemaining = timedMutator.roundsRemaining
                    },
                    Effects = DescribeBuffEffects(mutator, baseTowerModel)
                });
            }
        }

        object? effectiveBank = null;
        float? passiveCashPerRound = null;
        if (behaviors != null)
        {
            foreach (var b in behaviors)
            {
                var bank = b?.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Bank>();
                if (bank != null)
                {
                    effectiveBank = new
                    {
                        Cash = (float)Math.Round(bank.Cash, 1),
                        Capacity = bank.bankModel != null ? bank.bankModel.capacity : 0f,
                        Interest = bank.bankModel != null ? bank.bankModel.interest : 0f,
                        IsFull = bank.IsAtMaxCapacity
                    };
                }

                var prc = b?.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.PerRoundCashBonusTower>();
                if (prc?.perRoundCashBonusTowerModel != null)
                    passiveCashPerRound = prc.perRoundCashBonusTowerModel.cashPerRound;
            }
        }

        return new
        {
            Available = true,
            Range = simTower.towerModel?.range ?? 0f,
            BaseRange = baseTowerModel?.range ?? (simTower.towerModel?.range ?? 0f),
            TotalRateModifier = simTower.GetTotalRateModifier(),
            PoppingCapabilities = poppingCapabilities,
            Attacks = attacks,
            ActiveBuffs = activeBuffs,
            AppliedDebuffs = ExtractTowerDebuffs(simTower.towerModel ?? baseTowerModel),
            CashEarned = (float)Math.Round(simTower.cashEarned, 1),
            Bank = effectiveBank,
            Income = (simTower.towerModel ?? baseTowerModel) != null ? ExtractTowerIncomeStats(simTower.towerModel ?? baseTowerModel) : null,
            PassiveCashPerRound = passiveCashPerRound,
            ParagonBossDamage = BuildParagonBossDamageMetadata(simTower.towerModel ?? baseTowerModel, simTower)
        };
    }

    private static List<object> DescribeBuffEffects(SimulationBehaviorMutator? mutator, TowerModel? rootModel)
    {
        var effects = new List<object>();
        if (mutator == null)
            return effects;

        // 1. Dynamic Simulation: Attempt to run mutator.Mutate on a scratch clone of rootModel
        if (rootModel != null)
        {
            try
            {
                var scratch = rootModel.Clone()?.TryCast<TowerModel>();
                if (scratch != null)
                {
                    bool mutated = mutator.Mutate(rootModel, scratch);
                    if (mutated)
                    {
                        // Range delta
                        if (Math.Abs(scratch.range - rootModel.range) > 0.001f)
                        {
                            effects.Add(new
                            {
                                Stat = "range",
                                Operation = "add",
                                Amount = (float)Math.Round(scratch.range - rootModel.range, 2),
                                MutationId = mutator.id ?? "",
                                Condition = ""
                            });
                        }

                        // Projectile damage / pierce delta
                        var baseProj = GetFirstProjectile(rootModel);
                        var scratchProj = GetFirstProjectile(scratch);
                        var (baseDamage, _) = ResolveDamageAndProjectile(baseProj);
                        var (scratchDamage, _) = ResolveDamageAndProjectile(scratchProj);

                        if (baseDamage != null && scratchDamage != null && Math.Abs(scratchDamage.damage - baseDamage.damage) > 0.001f)
                        {
                            effects.Add(new
                            {
                                Stat = "damage",
                                Operation = "add",
                                Amount = (float)Math.Round(scratchDamage.damage - baseDamage.damage, 2),
                                MutationId = mutator.id ?? "",
                                Condition = ""
                            });
                        }

                        if (baseProj != null && scratchProj != null && Math.Abs(scratchProj.pierce - baseProj.pierce) > 0.001f)
                        {
                            effects.Add(new
                            {
                                Stat = "pierce",
                                Operation = "add",
                                Amount = (float)Math.Round(scratchProj.pierce - baseProj.pierce, 2),
                                MutationId = mutator.id ?? "",
                                Condition = ""
                            });
                        }

                        // Cooldown delta
                        var baseWep = GetFirstWeapon(rootModel);
                        var scratchWep = GetFirstWeapon(scratch);
                        if (baseWep != null && scratchWep != null && baseWep.rate > 0f && Math.Abs(scratchWep.rate - baseWep.rate) > 0.001f)
                        {
                            effects.Add(new
                            {
                                Stat = "cooldown",
                                Operation = "multiply",
                                Multiplier = (float)Math.Round(scratchWep.rate / baseWep.rate, 4),
                                MutationId = mutator.id ?? "",
                                Condition = ""
                            });
                        }

                        // Added damage modifiers (tag bonuses e.g. Ceramic / MOAB)
                        if (scratchProj?.behaviors != null)
                        {
                            foreach (var b in scratchProj.behaviors)
                            {
                                var tagMod = b?.TryCast<DamageModifierForTagModel>();
                                if (tagMod != null && !string.IsNullOrEmpty(tagMod.tag))
                                {
                                    bool existsInBase = baseProj?.behaviors?.Any(bb => bb?.TryCast<DamageModifierForTagModel>()?.tag == tagMod.tag) ?? false;
                                    if (!existsInBase)
                                    {
                                        effects.Add(new
                                        {
                                            Stat = "tag_damage",
                                            Operation = "add",
                                            Amount = tagMod.damageAddative,
                                            Multiplier = tagMod.damageMultiplier,
                                            Tag = tagMod.tag,
                                            MutationId = mutator.id ?? "",
                                            Condition = ""
                                        });
                                    }
                                }
                            }
                        }

                        // Immunity clearance (e.g. Lead popping, MIB normal damage)
                        if (baseDamage != null && scratchDamage != null)
                        {
                            bool baseHadLead = (baseDamage.immuneBloonProperties & BloonProperties.Lead) != 0;
                            bool scratchHadLead = (scratchDamage.immuneBloonProperties & BloonProperties.Lead) != 0;
                            if (baseHadLead && !scratchHadLead)
                            {
                                effects.Add(new
                                {
                                    Stat = "lead",
                                    Operation = "grant",
                                    Description = "Grants Lead popping capability",
                                    MutationId = mutator.id ?? "",
                                    Condition = ""
                                });
                            }

                            if (baseDamage.immuneBloonProperties != BloonProperties.None && scratchDamage.immuneBloonProperties == BloonProperties.None)
                            {
                                effects.Add(new
                                {
                                    Stat = "all_damage_types",
                                    Operation = "grant",
                                    Description = "Bypasses all bloon damage immunities",
                                    MutationId = mutator.id ?? "",
                                    Condition = ""
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to direct mutator inspection below
            }
        }

        // 2. Fallback to direct model inspection for standard mutator instances if dynamic diff didn't capture effects
        if (effects.Count == 0)
        {
            var damage = mutator.TryCast<SimulationDamageMutator>();
            if (damage?.damageModel != null)
            {
                effects.Add(new
                {
                    Stat = "damage",
                    Operation = "add",
                    Amount = damage.damageModel.damage,
                    MutationId = damage.damageModel.mutationId,
                    Condition = damage.damageModel.conditionalId?.name ?? ""
                });
            }

            var pierce = mutator.TryCast<SimulationPierceMutator>();
            if (pierce?.pierceModel != null)
            {
                effects.Add(new
                {
                    Stat = "pierce",
                    Operation = "add",
                    Amount = pierce.pierceModel.pierce,
                    MutationId = pierce.pierceModel.mutationId,
                    Condition = pierce.pierceModel.conditionalId?.name ?? ""
                });
            }

            var range = mutator.TryCast<SimulationRangeMutator>();
            if (range?.rangeTowerModel != null)
            {
                effects.Add(new
                {
                    Stat = "range",
                    Operation = "add",
                    Amount = range.rangeTowerModel.rangeIncrease,
                    MutationId = range.rangeTowerModel.mutationId,
                    Condition = range.rangeTowerModel.conditionalId?.name ?? ""
                });
            }

            var reload = mutator.TryCast<SimulationReloadMutator>();
            if (reload?.reloadTimeModel != null)
            {
                effects.Add(new
                {
                    Stat = "cooldown",
                    Operation = "multiply",
                    Multiplier = reload.reloadTimeModel.multiplier,
                    MutationId = reload.reloadTimeModel.mutationId,
                    Condition = reload.reloadTimeModel.conditionalId?.name ?? ""
                });
            }
        }

        return effects;
    }
}
