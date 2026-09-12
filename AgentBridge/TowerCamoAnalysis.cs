using System;
using System.Collections.Generic;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2Cpp;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;
using Il2CppAssets.Scripts.Models.Towers.Filters;
using Il2CppAssets.Scripts.Models.Towers.Projectiles;
using Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private sealed class CamoCapabilityInfoV1
    {
        public bool? CanDetectCamo { get; init; }
        public bool? CanCollideCamo { get; init; }
        public bool? NeedsDetection { get; init; }
        public bool? CanDamageCamo { get; init; }
        public string DetectionSource { get; init; } = "unknown";
        public string CollisionSource { get; init; } = "unknown";
        public string DamageSource { get; init; } = "unknown";
        public string[] Evidence { get; init; } = [];
        public string? Reason { get; init; }
    }

    private sealed class CamoCollisionAssessment
    {
        public bool? CanCollideCamo { get; set; }
        public string Source { get; set; } = "unknown";
        public List<string> Evidence { get; } = [];
        public string? Reason { get; set; }
    }

    private static string NativeTypeName(Model model)
    {
        try
        {
            return model.GetIl2CppType().Name;
        }
        catch
        {
            return "unknown";
        }
    }
    private static void AddCamoEvidence(List<string> evidence, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || evidence.Contains(text, StringComparer.Ordinal))
            return;
        if (evidence.Count < 12)
            evidence.Add(text);
    }

    private static bool? GetModelCamoDetection(TowerModel? tower, List<string> evidence, out string source)
    {
        source = "unknown";
        if (tower == null)
        {
            AddCamoEvidence(evidence, "TowerModel is unavailable; native Camo detection cannot be derived.");
            return null;
        }

        bool foundOverride = false;
        bool overrideDetection = false;
        bool sawActiveInvisibleFilter = false;
        bool sawInactiveInvisibleFilter = false;
        if (tower.behaviors != null)
        {
            foreach (var behavior in tower.behaviors)
            {
                var overrideCamo = behavior?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.OverrideCamoDetectionModel>();
                if (overrideCamo != null)
                {
                    foundOverride = true;
                    overrideDetection = overrideCamo.detectCamo;
                    AddCamoEvidence(evidence, $"Native OverrideCamoDetectionModel.detectCamo={overrideDetection} on '{tower.baseId}'.");
                }

                var attack = behavior?.TryCast<AttackModel>();
                if (attack?.behaviors == null)
                    continue;

                foreach (var attackBehavior in attack.behaviors)
                {
                    var attackFilter = attackBehavior?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack.Behaviors.AttackFilterModel>();
                    if (attackFilter?.filters == null)
                        continue;

                    for (int i = 0; i < attackFilter.filters.Length; i++)
                    {
                        var invisible = attackFilter.filters[i]?.TryCast<FilterInvisibleModel>();
                        if (invisible == null)
                            continue;

                        if (invisible.isActive)
                            sawActiveInvisibleFilter = true;
                        else
                            sawInactiveInvisibleFilter = true;
                        AddCamoEvidence(evidence, $"Native AttackFilterModel FilterInvisibleModel at filters[{i}]; isActive={invisible.isActive}.");
                    }
                }
            }
        }

        if (foundOverride)
        {
            source = "native-model";
            return overrideDetection;
        }

        if (sawInactiveInvisibleFilter)
        {
            source = "native-model-filter";
            AddCamoEvidence(evidence, "Inactive native FilterInvisibleModel indicates the attack path does not block Camo targeting.");
            return true;
        }

        if (sawActiveInvisibleFilter)
        {
            source = "native-model-filter";
            AddCamoEvidence(evidence, "Active native FilterInvisibleModel blocks Camo targeting.");
            return false;
        }

        // BTD6 56.3 Tower.CanTargetCamo (RVA 0xB1AF00) returns
        // !foundInvisibleFilter || foundInactiveInvisibleFilter on its ordinary path.
        source = "native-unfiltered-acquisition";
        AddCamoEvidence(evidence, "No native acquisition FilterInvisibleModel blocks Camo; the ordinary native targeting path is unrestricted.");
        return true;
    }

    private static bool? GetModelAttackCamoBlock(AttackModel? attack, List<string> evidence)
    {
        if (attack?.behaviors == null)
            return false;

        bool? blocked = false;
        foreach (var behavior in attack.behaviors)
        {
            var attackFilter = behavior?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack.Behaviors.AttackFilterModel>();
            if (attackFilter?.filters == null)
                continue;

            for (int i = 0; i < attackFilter.filters.Length; i++)
            {
                var filter = attackFilter.filters[i];
                if (filter == null)
                    continue;

                var invisible = filter.TryCast<FilterInvisibleModel>();
                if (invisible != null)
                {
                    AddCamoEvidence(evidence, $"Native AttackFilterModel FilterInvisibleModel at filters[{i}]; isActive={invisible.isActive}.");
                    if (invisible.isActive)
                        blocked = true;
                    continue;
                }

                if (IsPotentialCamoFilter(filter))
                {
                    blocked = null;
                    AddCamoEvidence(evidence, $"Native attack camo filter '{NativeTypeName(filter)}' has no safe field mapping.");
                }
            }
        }

        return blocked;
    }

    private static bool? DetermineNeedsCamoDetection(
        AttackModel? attack,
        List<string> evidence,
        out string? reason)
    {
        reason = null;
        bool? result = null;
        bool found = false;
        bool unresolved = false;
        void InspectSupplier(Model? model)
        {
            var supplier = model?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack.Behaviors.TargetSupplierModel>();
            if (supplier == null) return;
            string type = NativeTypeName(supplier);
            // These are native target-supplier roles, not tower IDs. A selected
            // point or track point is not a bloon and requires no camo acquisition.
            bool? needs = type switch
            {
                "TargetTrackModel" or "RandomTargetTrackModel" or "CloseTargetTrackModel"
                    or "FarTargetTrackModel" or "SmartTargetTrackModel" or "AutoTargetTrackModel"
                    or "NecromancerTargetTrackWithinRangeModel" or "TargetSelectedPointModel"
                    or "TargetSelectedPointOrDefaultModel" or "TargetSuppliedPositionModel"
                    or "TargetPointerModel" or "RandomTargetModel" or "UsePresetTargetModel"
                    or "FollowTouchSettingModel" or "LockInPlaceSettingModel" or "PatrolPointsSettingModel"
                    or "CirclePatternModel" or "FigureEightPatternModel" => false,
                "TargetFirstModel" or "TargetLastModel" or "TargetStrongModel" or "TargetCloseModel"
                    or "TargetFirstPrioCamoModel" or "TargetLastPrioCamoModel" or "TargetStrongPrioCamoModel"
                    or "TargetClosePrioCamoModel" or "TargetFirstSharedRangeModel" or "TargetLastSharedRangeModel"
                    or "TargetStrongSharedRangeModel" or "TargetCloseSharedRangeModel"
                    or "TargetCamoModel" or "TargetMoabModel" or "TargetTagModel"
                    or "TargetEliteTargettingModel" => true,
                _ => null
            };
            AddCamoEvidence(evidence, $"Native target supplier '{type}': {(needs == false ? "map-position targeting" : needs == true ? "bloon acquisition" : "unresolved target role")}.");
            if (needs == null || (found && result != needs)) unresolved = true;
            if (!found) result = needs;
            found = true;
        }
        InspectSupplier(attack?.targetProvider);
        if (attack?.behaviors != null)
            foreach (var behavior in attack.behaviors) InspectSupplier(behavior);
        if (attack?.fireWithoutTarget == true)
        {
            AddCamoEvidence(evidence, "Native AttackModel.fireWithoutTarget permits emission without bloon acquisition; projectile filters still govern camo collision.");
            return false;
        }
        if (found && !unresolved) return result;
        reason = found ? "Mixed or unsupported native target suppliers; acquisition requirement is unknown."
            : "No native target-supplier metadata is available; acquisition requirement is unknown.";
        AddCamoEvidence(evidence, reason);
        return null;
    }

    private static bool IsPotentialCamoFilter(FilterModel filter)
    {
        string typeName = NativeTypeName(filter);
        return typeName.Contains("Camo", StringComparison.Ordinal)
            || typeName.Contains("Invisible", StringComparison.Ordinal);
    }

    private static void ApplyCamoFilter(
        FilterModel? filter,
        CamoCollisionAssessment assessment,
        string path)
    {
        if (filter == null)
            return;

        var invisible = filter.TryCast<FilterInvisibleModel>();
        if (invisible != null)
        {
            AddCamoEvidence(assessment.Evidence, $"Native FilterInvisibleModel at {path}; isActive={invisible.isActive}.");
            if (invisible.isActive)
            {
                assessment.CanCollideCamo = false;
                assessment.Source = "native-projectile-filter";
                assessment.Reason = null;
            }
            return;
        }

        if (IsPotentialCamoFilter(filter) && assessment.CanCollideCamo != false)
        {
            assessment.CanCollideCamo = null;
            assessment.Source = "unsupported-projectile-filter";
            assessment.Reason = $"Native camo/invisible filter '{NativeTypeName(filter)}' has no safe field mapping.";
            AddCamoEvidence(assessment.Evidence, assessment.Reason);
        }
    }

    private static CamoCollisionAssessment AssessProjectileCamoCollision(
        ProjectileModel? projectile,
        bool? liveTowerDetection)
    {
        var assessment = new CamoCollisionAssessment
        {
            CanCollideCamo = projectile == null ? null : true,
            Source = projectile == null ? "unknown" : "native-projectile-collision"
        };

        if (projectile == null)
        {
            assessment.Reason = "ProjectileModel is unavailable; native collision cannot be classified.";
            AddCamoEvidence(assessment.Evidence, assessment.Reason);
            return assessment;
        }
        if (!projectile.canCollideWithBloons)
        {
            assessment.CanCollideCamo = false;
            assessment.Source = "native-projectile-collision-disabled";
            AddCamoEvidence(assessment.Evidence, "Native ProjectileModel.canCollideWithBloons=false.");
        }

        if (projectile.filters != null)
        {
            for (int i = 0; i < projectile.filters.Length; i++)
                ApplyCamoFilter(projectile.filters[i], assessment, $"projectile.filters[{i}]");
        }

        if (projectile.behaviors != null)
        {
            foreach (var behavior in projectile.behaviors)
            {
                if (behavior == null)
                    continue;

                var projectileFilter = behavior.TryCast<ProjectileFilterModel>();
                if (projectileFilter?.filters != null)
                {
                    for (int i = 0; i < projectileFilter.filters.Length; i++)
                        ApplyCamoFilter(projectileFilter.filters[i], assessment, $"ProjectileFilterModel.filters[{i}]");
                }

                var sync = behavior.TryCast<SyncCamoDetectionWithTowerModel>();
                if (sync != null)
                {
                    AddCamoEvidence(assessment.Evidence, "Native SyncCamoDetectionWithTowerModel makes projectile camo collision follow tower detection.");
                    if (liveTowerDetection.HasValue)
                    {
                        if (assessment.CanCollideCamo == true || !liveTowerDetection.Value)
                        {
                            assessment.CanCollideCamo = liveTowerDetection.Value;
                            assessment.Source = "native-sync-with-tower";
                            assessment.Reason = null;
                        }
                    }
                    else if (assessment.CanCollideCamo != false)
                    {
                        assessment.CanCollideCamo = null;
                        assessment.Source = "native-sync-with-tower";
                        assessment.Reason = "SyncCamoDetectionWithTowerModel requires live Tower.canTargetCamo.";
                        AddCamoEvidence(assessment.Evidence, assessment.Reason);
                    }
                }
            }
        }

        return assessment;
    }

    private static CamoCapabilityInfoV1 BuildCamoCapability(
        TowerModel? tower,
        AttackModel? attack,
        ProjectileModel? projectile,
        DamageTraversalResultV1 traversal,
        bool? liveTowerDetection,
        bool? attackCantTargetCamo,
        ProjectileModel? baseProjectile = null)
    {
        var evidence = new List<string>();
        string modelDetectionSource;
        bool? modelDetection = GetModelCamoDetection(tower, evidence, out modelDetectionSource);
        bool? modelAttackCamoBlock = GetModelAttackCamoBlock(attack, evidence);
        bool? canDetect = liveTowerDetection ?? modelDetection;
        string detectionSource = liveTowerDetection.HasValue
            ? liveTowerDetection.Value == modelDetection
                ? modelDetectionSource
                : "live-simulation"
            : modelDetectionSource;

        if (liveTowerDetection.HasValue)
            AddCamoEvidence(evidence, $"Live Tower.canTargetCamo={liveTowerDetection.Value} after native buff refresh.");

        bool? effectiveAttackCamoBlock = attackCantTargetCamo ?? modelAttackCamoBlock;
        if (effectiveAttackCamoBlock == true)
        {
            canDetect = false;
            detectionSource = "native-attack-filter";
            AddCamoEvidence(evidence, "Native attack camo filter blocks target acquisition for this attack.");
        }
        else if (effectiveAttackCamoBlock == null)
        {
            canDetect = null;
            detectionSource = "unsupported-attack-filter";
            AddCamoEvidence(evidence, "Attack camo-filter semantics are unresolved.");
        }

        bool? needsDetection = DetermineNeedsCamoDetection(attack, evidence, out string? needsReason);
        var rootCollision = AssessProjectileCamoCollision(projectile, liveTowerDetection ?? modelDetection);
        if (baseProjectile != null)
        {
            var baseCollision = AssessProjectileCamoCollision(baseProjectile, modelDetection);
            if (rootCollision.CanCollideCamo != baseCollision.CanCollideCamo)
            {
                rootCollision.Source = "buffed-projectile-model";
                AddCamoEvidence(evidence, "Effective projectile collision differs from the base projectile model after buffs.");
            }
        }

        foreach (var item in rootCollision.Evidence)
            AddCamoEvidence(evidence, item);

        bool? canCollide = false;
        string collisionSource = rootCollision.Source;
        bool sawPositiveDamage = false;
        bool sawUnknownDamage = traversal.Notes.Count > 0;
        bool sawDamageAllowed = false;
        string damageSource = "unknown";
        string? damageReason = null;

        foreach (var source in traversal.Records)
        {
            if (source.Damage == null || source.Damage.damage <= 0f)
                continue;

            sawPositiveDamage = true;
            var collision = AssessProjectileCamoCollision(source.Projectile, liveTowerDetection ?? modelDetection);
            foreach (var item in collision.Evidence)
                AddCamoEvidence(evidence, $"{source.Path}: {item}");

            if (collision.CanCollideCamo == true)
            {
                canCollide = true;
                collisionSource = collision.Source;

                bool? branchCanDamage = needsDetection switch
                {
                    false => true,
                    true => canDetect,
                    _ => canDetect == true ? true : null
                };

                if (branchCanDamage == true)
                {
                    sawDamageAllowed = true;
                    damageSource = collision.Source;
                    break;
                }

                if (branchCanDamage == null)
                    sawUnknownDamage = true;
                else
                    damageReason ??= canDetect == false
                        ? "Projectile collision is available but this attack cannot acquire Camo."
                        : "Projectile collision is available but target-acquisition semantics are unresolved.";
            }
            else if (collision.CanCollideCamo == null)
            {
                sawUnknownDamage = true;
                damageReason ??= collision.Reason;
                if (canCollide != true)
                {
                    canCollide = null;
                    collisionSource = collision.Source;
                }
            }
            else
            {
                damageReason ??= "Native projectile collision filters block Camo.";
            }
        }

        bool? canDamage;
        if (!sawPositiveDamage)
        {
            canDamage = sawUnknownDamage ? null : false;
            damageSource = sawUnknownDamage ? "incomplete-traversal" : "no-damage-model";
            damageReason = sawUnknownDamage ? "Damage traversal is incomplete; camo damage is unknown."
                : "No positive native DamageModel exists on this weapon's traversed projectile branches.";
            AddCamoEvidence(evidence, damageReason);
        }
        else if (sawDamageAllowed)
        {
            canDamage = true;
        }
        else if (sawUnknownDamage)
        {
            canDamage = null;
            damageSource = "unknown";
            AddCamoEvidence(evidence, damageReason);
        }
        else
        {
            canDamage = false;
            damageSource = "native-collision-filter";
            AddCamoEvidence(evidence, damageReason);
        }

        if (!sawPositiveDamage && rootCollision.CanCollideCamo == null)
            canCollide = null;

        string? reason = canDamage == null
            ? damageReason ?? rootCollision.Reason ?? needsReason ?? "Native camo capability is unresolved."
            : null;
        if (canDamage == false && reason == null && !string.IsNullOrWhiteSpace(needsReason))
            reason = needsReason;

        return new CamoCapabilityInfoV1
        {
            CanDetectCamo = canDetect,
            CanCollideCamo = canCollide,
            NeedsDetection = needsDetection,
            CanDamageCamo = canDamage,
            DetectionSource = detectionSource,
            CollisionSource = collisionSource,
            DamageSource = damageSource,
            Evidence = evidence.ToArray(),
            Reason = reason
        };
    }

    private static bool? GetModelMoabBlock(
        AttackModel? attack,
        ProjectileModel? projectile,
        List<string> evidence)
    {
        bool blocked = false;
        bool sawUnsupported = false;

        void InspectFilter(FilterModel? filter, string location)
        {
            if (filter == null)
                return;

            var moab = filter.TryCast<FilterMoabModel>();
            if (moab != null)
            {
                AddCamoEvidence(evidence, $"Native {location} FilterMoabModel.flip={moab.flip}.");
                blocked |= moab.flip;
                return;
            }

            string typeName = NativeTypeName(filter);
            if (typeName.Contains("Moab", StringComparison.OrdinalIgnoreCase))
            {
                sawUnsupported = true;
                AddCamoEvidence(evidence, $"Unsupported native MOAB filter '{typeName}' at {location}.");
            }
        }

        if (attack?.behaviors != null)
        {
            foreach (var behavior in attack.behaviors)
            {
                var attackFilter = behavior?.TryCast<Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack.Behaviors.AttackFilterModel>();
                if (attackFilter?.filters == null)
                    continue;
                for (int i = 0; i < attackFilter.filters.Length; i++)
                    InspectFilter(attackFilter.filters[i], $"attack.filters[{i}]");
            }
        }

        if (projectile != null)
        {
            if (projectile.filters != null)
            {
                for (int i = 0; i < projectile.filters.Length; i++)
                    InspectFilter(projectile.filters[i], $"projectile.filters[{i}]");
            }

            if (projectile.behaviors != null)
            {
                foreach (var behavior in projectile.behaviors)
                {
                    var projectileFilter = behavior?.TryCast<ProjectileFilterModel>();
                    if (projectileFilter?.filters == null)
                        continue;
                    for (int i = 0; i < projectileFilter.filters.Length; i++)
                        InspectFilter(projectileFilter.filters[i], $"projectile.behaviors.filters[{i}]");
                }
            }
        }

        if (sawUnsupported)
            return null;
        return blocked;
    }


    private static bool? DetermineCanDamageDdt(
        TowerModel? tower,
        AttackModel? attack,
        DamageTraversalResultV1 traversal,
        bool? liveTowerDetection,
        bool? attackCantTargetCamo,
        bool? attackCantTargetMoab)
    {
        var evidence = new List<string>();
        string modelDetectionSource;
        bool? modelDetection = GetModelCamoDetection(tower, evidence, out modelDetectionSource);
        bool? canDetect = liveTowerDetection ?? modelDetection;
        bool? modelAttackCamoBlock = GetModelAttackCamoBlock(attack, evidence);
        bool? effectiveAttackCamoBlock = attackCantTargetCamo ?? modelAttackCamoBlock;
        if (effectiveAttackCamoBlock == true)
            canDetect = false;
        else if (effectiveAttackCamoBlock == null)
            canDetect = null;

        bool sawPositiveDamage = false;
        bool sawUnknown = false;
        foreach (var source in traversal.Records)
        {
            if (source.Damage == null || source.Damage.damage <= 0f)
                continue;

            sawPositiveDamage = true;
            bool? sourceNeedsDetection = DetermineNeedsCamoDetection(attack, evidence, out _);
            var collision = AssessProjectileCamoCollision(source.Projectile, liveTowerDetection ?? modelDetection);
            bool? canDamageCamo = collision.CanCollideCamo switch
            {
                false => false,
                null => null,
                true => sourceNeedsDetection switch
                {
                    false => true,
                    true => canDetect,
                    _ => canDetect == true ? true : null
                }
            };

            // Immunity on this exact damage source is decisive, even if another
            // capability is unknown. Targetless attacks do not need MOAB acquisition.
            if ((source.Damage.immuneBloonProperties & (BloonProperties.Lead | BloonProperties.Black)) != 0)
                continue;
            bool? modelMoabBlock = GetModelMoabBlock(sourceNeedsDetection == false ? null : attack, source.Projectile, evidence);
            bool? moabBlock = modelMoabBlock == true || (sourceNeedsDetection != false && attackCantTargetMoab == true)
                ? true : modelMoabBlock;
            bool? canDamageDdt = canDamageCamo == false || moabBlock == true ? false
                : canDamageCamo == true && moabBlock == false && sourceNeedsDetection.HasValue ? true : null;

            if (canDamageDdt == true)
                return true;
            if (canDamageDdt == null)
                sawUnknown = true;
        }

        if (!sawPositiveDamage)
            return false;
        return sawUnknown ? null : false;
    }

    private static CamoCapabilityInfoV1 SummarizeCamoCapabilities(
        IReadOnlyList<CamoCapabilityInfoV1> capabilities,
        bool? towerDetection,
        string detectionSource,
        IEnumerable<string>? towerEvidence = null)
    {
        if (capabilities.Count == 0)
        {
            return new CamoCapabilityInfoV1
            {
                CanDetectCamo = towerDetection,
                CanCollideCamo = false,
                NeedsDetection = null,
                CanDamageCamo = false,
                DetectionSource = detectionSource,
                CollisionSource = "no-weapons",
                DamageSource = "no-damage-model",
                Evidence = towerEvidence?.ToArray() ?? [],
                Reason = "Tower has no weapon branches to assess."
            };
        }

        static bool? AllEqual(IEnumerable<bool?> values)
        {
            bool? first = null;
            bool found = false;
            foreach (var value in values)
            {
                if (!found)
                {
                    first = value;
                    found = true;
                    continue;
                }
                if (value != first)
                    return null;
            }
            return first;
        }

        bool? collision = capabilities.Any(value => value.CanCollideCamo == true)
            ? true
            : capabilities.Any(value => value.CanCollideCamo == null)
                ? null
                : false;
        bool? damage = capabilities.Any(value => value.CanDamageCamo == true)
            ? true
            : capabilities.Any(value => value.CanDamageCamo == null)
                ? null
                : false;
        var evidence = new List<string>();
        if (towerEvidence != null)
        {
            foreach (var item in towerEvidence)
                AddCamoEvidence(evidence, item);
        }
        foreach (var capability in capabilities)
        {
            foreach (var item in capability.Evidence)
                AddCamoEvidence(evidence, item);
        }

        string? reason = damage == null
            ? "At least one damaging projectile branch has unsupported camo semantics."
            : capabilities.Select(value => value.Reason).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new CamoCapabilityInfoV1
        {
            CanDetectCamo = towerDetection ?? AllEqual(capabilities.Select(value => value.CanDetectCamo)),
            CanCollideCamo = collision,
            NeedsDetection = AllEqual(capabilities.Select(value => value.NeedsDetection)),
            CanDamageCamo = damage,
            DetectionSource = detectionSource,
            CollisionSource = capabilities.Select(value => value.CollisionSource).Distinct(StringComparer.Ordinal).Count() == 1
                ? capabilities[0].CollisionSource
                : "mixed",
            DamageSource = capabilities.Select(value => value.DamageSource).Distinct(StringComparer.Ordinal).Count() == 1
                ? capabilities[0].DamageSource
                : "mixed",
            Evidence = evidence.ToArray(),
            Reason = reason
        };
    }

    private static List<string> BuildDdtBlockers(
        bool? canDamageDdt,
        bool? canDamageCamo,
        bool canPopLead,
        bool canPopBlack,
        bool hasAnyDamagingWeapon)
    {
        var blockers = new List<string>();
        if (canDamageDdt == true || !hasAnyDamagingWeapon)
            return blockers;
        if (canDamageCamo != true)
            blockers.Add(canDamageCamo == false ? "No weapon can damage Camo" : "Camo damage capability is unknown");
        if (!canPopLead)
            blockers.Add("Tower has no Lead-popping weapon");
        if (!canPopBlack)
            blockers.Add("Tower has no weapon that damages Black bloons (explosions blocked)");
        if (canDamageCamo == true && canPopLead && canPopBlack && canDamageDdt == false)
            blockers.Add("No single weapon satisfies Camo + Lead + Black + MOAB eligibility simultaneously");
        if (canDamageDdt == null)
            blockers.Add("DDT damage eligibility is unresolved");
        return blockers;
    }
}
