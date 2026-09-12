using System;
using System.Collections.Generic;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Bloons.Behaviors;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities.Behaviors;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;
using Il2CppAssets.Scripts.Models.Towers.Projectiles;
using Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors;
using Il2CppAssets.Scripts.Models.Towers.Weapons;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static List<DamageModifierInfoV1> ExtractDamageModifiers(ProjectileModel? projectile)
    {
        return BuildDamageModifiers(projectile);
    }

    private static List<object> ExtractAppliedDebuffs(ProjectileModel? projectile, HashSet<string>? visited = null)
    {
        var debuffs = new List<object>();
        if (projectile?.behaviors == null) return debuffs;

        visited ??= new HashSet<string>();
        if (!string.IsNullOrEmpty(projectile.id) && !visited.Add(projectile.id))
            return debuffs;

        foreach (var behavior in projectile.behaviors)
        {
            if (behavior == null) continue;

            // 1. AddBonusDamagePerHitToBloonModel (Embrittlement, Super Brittle)
            var bonusDmg = behavior.TryCast<AddBonusDamagePerHitToBloonModel>();
            if (bonusDmg != null && bonusDmg.perHitDamageAddition > 0)
            {
                string name = !string.IsNullOrEmpty(bonusDmg.mutationId) ? bonusDmg.mutationId : "DamageAmplification";
                debuffs.Add(new
                {
                    Category = "damage_amplification",
                    Name = name,
                    DamageBonus = bonusDmg.perHitDamageAddition,
                    DurationSeconds = bonusDmg.lifespan,
                    Targets = "All",
                    Description = $"Affected bloons take +{bonusDmg.perHitDamageAddition:0.##} damage from all attacks"
                });
            }

            // 2. SlowMaimMoabModel (Cripple MOAB, Maim MOAB)
            var maimMoab = behavior.TryCast<SlowMaimMoabModel>();
            if (maimMoab != null)
            {
                if (maimMoab.bloonPerHitDamageAddition > 0)
                {
                    debuffs.Add(new
                    {
                        Category = "damage_amplification",
                        Name = "CrippleMOAB",
                        DamageBonus = maimMoab.bloonPerHitDamageAddition,
                        DurationSeconds = maimMoab.moabDuration,
                        Targets = "MOABs",
                        Description = $"Affected MOABs take +{maimMoab.bloonPerHitDamageAddition:0.##} damage from all attacks"
                    });
                }
                if (maimMoab.multiplier == 0f && maimMoab.moabDuration > 0)
                {
                    debuffs.Add(new
                    {
                        Category = "stun",
                        Name = "MaimMOABStun",
                        DurationSeconds = maimMoab.moabDuration,
                        Targets = "MOABs",
                        Description = $"Stuns MOAB-class bloons for up to {maimMoab.moabDuration:0.##}s (ZOMG: {maimMoab.zomgDuration:0.##}s, BAD: {maimMoab.badDuration:0.##}s)"
                    });
                }
            }

            // 3. AddBehaviorToBloonModel (Glue Storm, Burny Stuff, Corrosive, GrowBlock, etc.)
            var addBloonBeh = behavior.TryCast<AddBehaviorToBloonModel>();
            if (addBloonBeh?.behaviors != null)
            {
                foreach (var subBeh in addBloonBeh.behaviors)
                {
                    if (subBeh == null) continue;

                    var incDmg = subBeh.TryCast<IncreaseDamageFromAllTypesModel>();
                    if (incDmg != null && incDmg.amount > 0)
                    {
                        string name = !string.IsNullOrEmpty(addBloonBeh.mutationId) ? addBloonBeh.mutationId : "DamageAmplification";
                        debuffs.Add(new
                        {
                            Category = "damage_amplification",
                            Name = name,
                            DamageBonus = incDmg.amount,
                            DurationSeconds = addBloonBeh.lifespan,
                            Targets = "All",
                            Description = $"Affected bloons take +{incDmg.amount:0.##} damage from all attacks"
                        });
                    }

                    var dot = subBeh.TryCast<DamageOverTimeModel>();
                    if (dot != null && dot.damage > 0)
                    {
                        string name = !string.IsNullOrEmpty(addBloonBeh.mutationId) ? addBloonBeh.mutationId : (dot.isFireBased ? "Burn" : "DamageOverTime");
                        debuffs.Add(new
                        {
                            Category = "damage_over_time",
                            Name = name,
                            Damage = dot.damage,
                            IntervalSeconds = dot.interval,
                            DurationSeconds = addBloonBeh.lifespan,
                            IsFireBased = dot.isFireBased,
                            Description = $"Deals {dot.damage:0.##} damage every {dot.interval:0.##}s{(dot.isFireBased ? " (fire)" : "")}"
                        });
                    }

                    var growBlock = subBeh.TryCast<GrowBlockModel>();
                    if (growBlock != null)
                    {
                        debuffs.Add(new
                        {
                            Category = "crowd_control",
                            Name = "GrowBlock",
                            DurationSeconds = addBloonBeh.lifespan,
                            Description = "Prevents Regrow bloons from regenerating layers"
                        });
                    }

                    var freezeImmunity = subBeh.TryCast<FreezeImmunityRemovalModel>();
                    if (freezeImmunity != null)
                    {
                        debuffs.Add(new
                        {
                            Category = "vulnerability",
                            Name = "FreezeImmunityRemoval",
                            DurationSeconds = addBloonBeh.lifespan,
                            Description = "Allows sharp and standard attacks to damage frozen bloons"
                        });
                    }

                    var concoction = subBeh.TryCast<UnstableConcoctionSplashModel>();
                    if (concoction != null)
                    {
                        debuffs.Add(new
                        {
                            Category = "special",
                            Name = "UnstableConcoction",
                            Description = "Coats MOAB-class bloons to explode violently upon destruction, dealing % damage to nearby bloons"
                        });
                    }
                }
            }

            // 4. SlowModel (Glue, Freeze, Ice, Stun)
            var slow = behavior.TryCast<SlowModel>();
            if (slow != null)
            {
                string name = !string.IsNullOrEmpty(slow.mutationId) ? slow.mutationId : (slow.multiplier == 0f ? "Stun" : "Slow");
                if (slow.multiplier == 0f)
                {
                    debuffs.Add(new
                    {
                        Category = "stun",
                        Name = name,
                        DurationSeconds = slow.lifespan,
                        Description = $"Stuns/freezes bloons for {slow.lifespan:0.##}s"
                    });
                }
                else if (slow.multiplier < 1f)
                {
                    float slowPct = (float)Math.Round((1f - slow.multiplier) * 100f, 1);
                    debuffs.Add(new
                    {
                        Category = "slow",
                        Name = name,
                        SlowPercentage = slowPct,
                        DurationSeconds = slow.lifespan,
                        Description = $"Slows bloons by {slowPct:0.##}% for {slow.lifespan:0.##}s"
                    });
                }
            }

            // 5. FreezeModel (Ice Monkey freeze)
            var freeze = behavior.TryCast<FreezeModel>();
            if (freeze != null)
            {
                string name = !string.IsNullOrEmpty(freeze.mutationId) ? freeze.mutationId : "Freeze";
                debuffs.Add(new
                {
                    Category = "freeze",
                    Name = name,
                    DurationSeconds = freeze.lifespan,
                    Targets = freeze.canFreezeMoabs ? "All (including MOABs)" : "Regular Bloons",
                    Description = $"Freezes bloons in place for {freeze.lifespan:0.##}s{(freeze.canFreezeMoabs ? " (can freeze MOABs)" : "")}"
                });
            }

            // 6. RemoveBloonModifiersModel (Decamo, Defortify, Regrow strip)
            var strip = behavior.TryCast<RemoveBloonModifiersModel>();
            if (strip != null)
            {
                var stripped = new List<string>();
                if (strip.cleanseCamo) stripped.Add("Camo");
                if (strip.cleanseFortified) stripped.Add("Fortified");
                if (strip.cleanseRegen) stripped.Add("Regrow");
                if (strip.cleanseLead) stripped.Add("Lead");

                if (stripped.Count > 0)
                {
                    debuffs.Add(new
                    {
                        Category = "strip_property",
                        Name = "RemoveBloonModifiers",
                        Strips = stripped.ToArray(),
                        Description = $"Strips {string.Join(", ", stripped)} from affected bloons"
                    });
                }
            }

            // 7. WindModel & KnockbackModel
            var wind = behavior.TryCast<WindModel>();
            if (wind != null)
            {
                debuffs.Add(new
                {
                    Category = "crowd_control",
                    Name = "Wind",
                    Targets = wind.affectMoab ? "All (including MOABs)" : "Regular Bloons",
                    Description = $"Blows bloons back along track ({wind.distanceMin:0.##}-{wind.distanceMax:0.##} distance, {wind.chance * 100:0.#}% chance)"
                });
            }

            var knockback = behavior.TryCast<KnockbackModel>();
            if (knockback != null)
            {
                debuffs.Add(new
                {
                    Category = "crowd_control",
                    Name = "Knockback",
                    DurationSeconds = knockback.lifespan,
                    Description = "Knocks bloons backwards and slows forward movement"
                });
            }

            // 8. Child projectiles (e.g. Mortar explosion, Bomb explosion)
            var contact = behavior.TryCast<CreateProjectileOnContactModel>();
            if (contact?.projectile != null)
            {
                debuffs.AddRange(ExtractAppliedDebuffs(contact.projectile, visited));
            }

            var exhaust = behavior.TryCast<CreateProjectileOnExhaustFractionModel>();
            if (exhaust?.projectile != null)
            {
                debuffs.AddRange(ExtractAppliedDebuffs(exhaust.projectile, visited));
            }

            var expire = behavior.TryCast<CreateProjectileOnExpireModel>();
            if (expire?.projectile != null)
            {
                debuffs.AddRange(ExtractAppliedDebuffs(expire.projectile, visited));
            }
        }

        return debuffs;
    }

    private static List<object> ExtractTowerDebuffs(TowerModel? tower)
    {
        var allDebuffs = new List<object>();
        if (tower?.behaviors == null) return allDebuffs;

        var seenKeys = new HashSet<string>();

        void AddDebuffs(IEnumerable<object> debuffs)
        {
            foreach (var d in debuffs)
            {
                string key = JsonSerializer.Serialize(d, JsonOptions);
                if (seenKeys.Add(key))
                {
                    allDebuffs.Add(d);
                }
            }
        }

        foreach (var behavior in tower.behaviors)
        {
            if (behavior == null) continue;

            var attack = behavior.TryCast<AttackModel>();
            if (attack?.weapons != null)
            {
                foreach (var weapon in attack.weapons)
                {
                    if (weapon?.projectile != null)
                    {
                        var (_, effProj) = ResolveDamageAndProjectile(weapon.projectile);
                        var targetProj = effProj ?? weapon.projectile;
                        AddDebuffs(ExtractAppliedDebuffs(targetProj));
                    }
                }
            }

            var ability = behavior.TryCast<AbilityModel>();
            if (ability?.behaviors != null)
            {
                foreach (var abBeh in ability.behaviors)
                {
                    if (abBeh == null) continue;

                    var actAttack = abBeh.TryCast<ActivateAttackModel>();
                    if (actAttack?.attacks != null)
                    {
                        foreach (var atk in actAttack.attacks)
                        {
                            if (atk?.weapons == null) continue;
                            foreach (var weapon in atk.weapons)
                            {
                                if (weapon?.projectile != null)
                                {
                                    var (_, effProj) = ResolveDamageAndProjectile(weapon.projectile);
                                    var targetProj = effProj ?? weapon.projectile;
                                    AddDebuffs(ExtractAppliedDebuffs(targetProj));
                                }
                            }
                        }
                    }

                    var slowZone = abBeh.TryCast<ActivateSlowZoneOnAbilityModel>();
                    if (slowZone != null)
                    {
                        float slowPct = (float)Math.Round((1f - slowZone.speedScale) * 100f, 1);
                        string name = !string.IsNullOrEmpty(slowZone.mutationId) ? slowZone.mutationId : (!string.IsNullOrEmpty(ability.displayName) ? ability.displayName : "SlowZone");
                        AddDebuffs(new[]
                        {
                            new
                            {
                                Category = "slow",
                                Name = name,
                                SlowPercentage = slowPct,
                                DurationSeconds = slowZone.lifetime,
                                Targets = "Screen",
                                Description = $"Slows all bloons on screen by {slowPct:0.##}% for {slowZone.lifetime:0.##}s"
                            }
                        });
                    }
                }
            }
        }

        return allDebuffs;
    }

    private static WeaponModel? FindBaseWeapon(TowerModel? baseTower, string weaponName)
    {
        if (baseTower?.behaviors == null || string.IsNullOrEmpty(weaponName)) return null;
        foreach (var b in baseTower.behaviors)
        {
            var attack = b?.TryCast<AttackModel>();
            if (attack?.weapons == null) continue;
            foreach (var w in attack.weapons)
            {
                if (w != null && w.name == weaponName)
                    return w;
            }
        }
        return null;
    }

    private static (DamageModel? Damage, ProjectileModel? EffectiveProjectile) ResolveDamageAndProjectile(ProjectileModel? rootProjectile)
    {
        if (rootProjectile == null)
            return (null, null);

        return ResolveDamageAndProjectile(rootProjectile, ExtractDamageSourceTraversal(rootProjectile));
    }

    private static (DamageModel? Damage, ProjectileModel? EffectiveProjectile) ResolveDamageAndProjectile(
        ProjectileModel? rootProjectile,
        DamageTraversalResultV1 traversal)
    {
        if (rootProjectile == null)
            return (null, null);

        var source = FindPrimaryDamageSource(traversal);
        return source == null
            ? (null, rootProjectile)
            : (source.Damage, source.Projectile);
    }

    private static ProjectileModel? GetFirstProjectile(TowerModel? tower)
    {
        if (tower?.behaviors == null) return null;
        foreach (var b in tower.behaviors)
        {
            var attack = b?.TryCast<AttackModel>();
            if (attack?.weapons == null) continue;
            foreach (var w in attack.weapons)
            {
                if (w?.projectile != null)
                {
                    var (_, effectiveProj) = ResolveDamageAndProjectile(w.projectile);
                    return effectiveProj ?? w.projectile;
                }
            }
        }
        return null;
    }

    private static WeaponModel? GetFirstWeapon(TowerModel? tower)
    {
        if (tower?.behaviors == null) return null;
        foreach (var b in tower.behaviors)
        {
            var attack = b?.TryCast<AttackModel>();
            if (attack?.weapons == null) continue;
            foreach (var w in attack.weapons)
            {
                if (w != null)
                    return w;
            }
        }
        return null;
    }
}
