using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppAssets.Scripts.Models.Bloons.Behaviors;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors;
using Il2CppAssets.Scripts.Models.Towers.Projectiles;
using Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using SimulationParagonTower = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.ParagonTower;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int MaxProjectileTraversalDepth = 8;
    private const int MaxProjectileTraversalSources = 64;
    private const int MaxProjectileTraversalNotes = 64;
    private const int MaxModifierConditionValues = 64;

    internal sealed class DamageModifierInfoV1
    {
        public string ModifierType { get; init; } = "unknown";
        public string Applicability { get; init; } = "unknown";
        public bool ConditionEvaluated { get; init; }
        public string Condition { get; init; } = "Native predicate is not evaluated without a target bloon.";
        public string? Tag { get; init; }
        public string[] Tags { get; init; } = [];
        public bool? MustIncludeAllTags { get; init; }
        public bool? IgnoreTag { get; init; }
        public string? BloonId { get; init; }
        public string[] BloonIds { get; init; } = [];
        public string[] BloonTypes { get; init; } = [];
        public string? BloonState { get; init; }
        public string[] BloonStates { get; init; } = [];
        public bool? MustIncludeAllStates { get; init; }
        public bool? MustBeModified { get; init; }
        public bool? IncludeChildren { get; init; }
        public bool? ApplyOverMaxDamage { get; init; }
        public float? BonusDamage { get; init; }
        public float? Multiplier { get; init; }
        public float? CashThreshold { get; init; }
        public string? StackId { get; init; }
        public float? LifeThreshold { get; init; }
        public float? PercentPerLifeBelowThreshold { get; init; }
        public float? MultiplierPerLifeBelowThreshold { get; init; }
        public float? PercentPerShield { get; init; }
        public float? MultiplierPerShield { get; init; }
        public string? Modifier { get; init; }
        public string[] Modifiers { get; init; } = [];
        public float? DamagePerRound { get; init; }
        public int? RoundCap { get; init; }
        public int? RbeThreshold { get; init; }
        public float? MaxDamageMultiplier { get; init; }
        public int? ExtraRbePerBossSkull { get; init; }
        public bool? Active { get; init; }
        public int? Damage { get; init; }
        public int? MaxDamageBoost { get; init; }
        public string[] Level7Tags { get; init; } = [];
        public string[] Level11ExcludeTags { get; init; } = [];
        public string[] Level19BloonTags { get; init; } = [];
        public float? Level7NonMoabBonus { get; init; }
        public float? Level7MoabBonus { get; init; }
        public float? Level11NonMoabBonus { get; init; }
        public float? Level11MoabBonus { get; init; }
        public float? Level19NonMoabBonus { get; init; }
        public float? Level19MoabBonus { get; init; }
        public bool Supported { get; init; }
        public string CoverageNote { get; init; } = "";
    }

    internal sealed class AttachedBloonBehaviorInfoV1
    {
        public string BehaviorType { get; init; } = "unknown";
        public string Source { get; init; } = "projectile";
        public string? MutationId { get; init; }
        public float? Damage { get; init; }
        public float? IntervalSeconds { get; init; }
        public float? DurationSeconds { get; init; }
        public bool? IsFireBased { get; init; }
        public bool Supported { get; init; } = true;
        public string CoverageNote { get; init; } = "";
    }

    internal sealed class DamageSourceInfoV1
    {
        public string SourceKind { get; init; } = "direct";
        public string ProjectileId { get; init; } = "";
        public string Path { get; init; } = "root";
        public float? Damage { get; init; }
        public float? MaxDamage { get; init; }
        public string DamageType { get; init; } = "Normal";
        public string[] Conditions { get; init; } = [];
        public string[] BlockedBy { get; init; } = [];
        public List<DamageModifierInfoV1> DamageModifiers { get; init; } = [];
        public List<AttachedBloonBehaviorInfoV1> AttachedBloonBehaviors { get; init; } = [];
        public bool Supported { get; init; } = true;
        public string[] UnsupportedCoverage { get; set; } = [];
        public string CoverageNote { get; init; } = "";
    }

    internal sealed class DamageCoverageInfoV1
    {
        public int MaxDepth { get; init; }
        public int MaxSources { get; init; }
        public int VisitedNodes { get; init; }
        public bool Complete { get; init; }
        public string[] SupportedSourceTypes { get; init; } =
        [
            "DamageModel",
            "CreateProjectileOnContactModel",
            "CreateProjectileOnExhaustFractionModel",
            "CreateProjectileOnExpireModel"
        ];
        public string[] SourceKinds { get; init; } = [];
        public string[] UnsupportedTypes { get; init; } = [];
        public string[] Notes { get; init; } = [];
        public string CoverageNote { get; init; } = "DamageModel and contact/exhaust/expire nested projectile branches are traversed with bounded depth and source count.";
    }

    private sealed class DamageTraversalResultV1
    {
        public List<DamageSourceInfoV1> Sources { get; } = [];
        public int VisitedNodeCount { get; set; }
        public List<DamageSourceRecordV1> Records { get; } = [];
        public List<string> SourceKinds { get; } = [];
        public List<string> UnsupportedTypes { get; } = [];
        public List<string> Notes { get; } = [];
    }

    internal sealed class ParagonBossDamageInfoV1
    {
        public bool IsParagon { get; init; }
        public string Source { get; init; } = "not-paragon";
        public string ValueKind { get; init; } = "none";
        public float? ConfiguredBonusPercent { get; init; }
        public int? ConfiguredEveryDegrees { get; init; }
        public int? CurrentDegree { get; init; }
        public float? CurrentDegreeBonusPercent { get; init; }
        public float? ActualTargetDamageBonusPercent { get; init; }
        public string Confidence { get; init; } = "unknown";
        public string CoverageNote { get; init; } = "";
    }

    private sealed class DamageSourceRecordV1
    {
        public DamageModel Damage { get; init; } = null!;
        public ProjectileModel Projectile { get; init; } = null!;
        public string SourceKind { get; init; } = "direct";
        public string Path { get; init; } = "root";
    }

    private static float? FiniteOrNull(float value)
    {
        return float.IsFinite(value) ? value : null;
    }

    private static int? FiniteIntOrNull(int value)
    {
        return value >= 0 ? value : null;
    }

    private static string[] ReadConditionValues<T>(IEnumerable<T>? values)
    {
        if (values == null)
            return [];

        return values
            .Select(value => value is null ? "" : value.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxModifierConditionValues)
            .ToArray();
    }

    private static DamageModifierInfoV1? DescribeDamageModifier(Model? behavior)
    {
        if (behavior == null)
            return null;

        var tag = behavior.TryCast<DamageModifierForTagModel>();
        if (tag != null)
        {
            string[] tags = ReadConditionValues(tag.tags);
            if (tags.Length == 0 && !string.IsNullOrWhiteSpace(tag.tag))
                tags = [tag.tag];

            return new DamageModifierInfoV1
            {
                ModifierType = "tag",
                Applicability = "bloon_tag",
                Tag = string.IsNullOrWhiteSpace(tag.tag) ? null : tag.tag,
                Tags = tags,
                MustIncludeAllTags = tag.mustIncludeAllTags,
                IgnoreTag = tag.ignoreTag,
                ApplyOverMaxDamage = tag.applyOverMaxDamage,
                BonusDamage = FiniteOrNull(tag.damageAddative),
                Multiplier = FiniteOrNull(tag.damageMultiplier),
                Supported = true,
                CoverageNote = "Native tag arrays and all/any/ignore/max-damage flags are reported; no target bloon was evaluated."
            };
        }

        var stateAndType = behavior.TryCast<DamageModifierForBloonStateAndTypeModel>();
        if (stateAndType != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "bloon_state_and_type",
                Applicability = "bloon_state_and_type",
                BloonStates = ReadConditionValues(stateAndType.bloonStates),
                BloonTypes = ReadConditionValues(stateAndType.bloonTypes),
                MustIncludeAllStates = stateAndType.mustIncludeAllStates,
                ApplyOverMaxDamage = stateAndType.applyOverMaxDamage,
                MustBeModified = stateAndType.mustBeModified,
                IncludeChildren = stateAndType.includeChildren,
                BonusDamage = FiniteOrNull(stateAndType.damageAdditive),
                Multiplier = FiniteOrNull(stateAndType.damageMultiplier),
                Supported = true,
                CoverageNote = "Native state/type conditions are reported without asserting that the predicate passes for a target."
            };
        }

        var state = behavior.TryCast<DamageModifierForBloonStateModel>();
        if (state != null)
        {
            string[] states = ReadConditionValues(state.bloonStates);
            if (states.Length == 0 && !string.IsNullOrWhiteSpace(state.bloonState))
                states = [state.bloonState];

            return new DamageModifierInfoV1
            {
                ModifierType = "bloon_state",
                Applicability = "bloon_state",
                BloonState = string.IsNullOrWhiteSpace(state.bloonState) ? null : state.bloonState,
                BloonStates = states,
                MustIncludeAllStates = state.mustIncludeAllStates,
                ApplyOverMaxDamage = state.applyOverMaxDamage,
                MustBeModified = state.mustBeModified,
                BonusDamage = FiniteOrNull(state.damageAdditive),
                Multiplier = FiniteOrNull(state.damageMultiplier),
                Supported = true,
                CoverageNote = "Native state arrays and all-state/max-damage/must-be-modified flags are reported without target evaluation."
            };
        }

        var bloonType = behavior.TryCast<DamageModifierForBloonTypeModel>();
        if (bloonType != null)
        {
            string[] ids = ReadConditionValues(bloonType.bloonIds);
            if (ids.Length == 0 && !string.IsNullOrWhiteSpace(bloonType.bloonId))
                ids = [bloonType.bloonId];

            return new DamageModifierInfoV1
            {
                ModifierType = "bloon_type",
                Applicability = "bloon_type",
                BloonId = string.IsNullOrWhiteSpace(bloonType.bloonId) ? null : bloonType.bloonId,
                BloonIds = ids,
                IncludeChildren = bloonType.includeChildren,
                BonusDamage = FiniteOrNull(bloonType.damageAdditive),
                Multiplier = FiniteOrNull(bloonType.damageMultiplier),
                Supported = true,
                CoverageNote = "Native bloon IDs and include-children behavior are reported; no target bloon was evaluated."
            };
        }

        var cash = behavior.TryCast<DamageModifierForCashAmountModel>();
        if (cash != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "cash_amount",
                Applicability = "cash_threshold",
                CashThreshold = FiniteOrNull(cash.cashThreshold),
                StackId = string.IsNullOrWhiteSpace(cash.stackId) ? null : cash.stackId,
                BonusDamage = FiniteOrNull(cash.damageAdditive),
                Multiplier = FiniteOrNull(cash.damageMultiplier),
                Supported = true,
                CoverageNote = "Native cash threshold and stack ID are reported; current cash is not a projectile-model property."
            };
        }

        var life = behavior.TryCast<DamageModifierForLifeBelowMaxModel>();
        if (life != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "life_below_max",
                Applicability = "life_threshold",
                LifeThreshold = FiniteOrNull(life.lifeThreshold),
                PercentPerLifeBelowThreshold = FiniteOrNull(life.percentPerLifeBelowThreshold),
                MultiplierPerLifeBelowThreshold = FiniteOrNull(life.multiplierPerLifeBelowThreshold),
                Supported = true,
                CoverageNote = "Native life threshold scaling is reported; current player life is not evaluated."
            };
        }

        var mana = behavior.TryCast<DamageModifierForManaShieldModel>();
        if (mana != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "mana_shield",
                Applicability = "mana_shield",
                PercentPerShield = FiniteOrNull(mana.percentPerShield),
                MultiplierPerShield = FiniteOrNull(mana.multiplierPerShield),
                Supported = true,
                CoverageNote = "Native mana-shield scaling is reported; current shield state is not evaluated."
            };
        }

        var modifiers = behavior.TryCast<DamageModifierForModifiersModel>();
        if (modifiers != null)
        {
            string[] names = ReadConditionValues(modifiers.modifiers);
            if (names.Length == 0 && !string.IsNullOrWhiteSpace(modifiers.modifier))
                names = [modifiers.modifier];

            return new DamageModifierInfoV1
            {
                ModifierType = "bloon_modifiers",
                Applicability = "bloon_modifier",
                Modifier = string.IsNullOrWhiteSpace(modifiers.modifier) ? null : modifiers.modifier,
                Modifiers = names,
                BonusDamage = FiniteOrNull(modifiers.damageAddative),
                Multiplier = FiniteOrNull(modifiers.damageMultiplier),
                Supported = true,
                CoverageNote = "Native bloon modifier names are reported; current target modifiers are not evaluated."
            };
        }

        var round = behavior.TryCast<DamageModifierForRoundModel>();
        if (round != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "round",
                Applicability = "round_threshold",
                DamagePerRound = FiniteOrNull(round.damagePerRound),
                RoundCap = FiniteIntOrNull(round.roundCap),
                Supported = true,
                CoverageNote = "Native per-round damage and cap are reported; no accumulated value is inferred."
            };
        }

        var primordial = behavior.TryCast<DamageModifierPrimordialWrathModel>();
        if (primordial != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "primordial_wrath",
                Applicability = "rbe_and_boss_skulls",
                RbeThreshold = FiniteIntOrNull(primordial.rbeThreshold),
                Multiplier = FiniteOrNull(primordial.damageMultiplier),
                MaxDamageMultiplier = FiniteOrNull(primordial.maxDamageMultiplier),
                ExtraRbePerBossSkull = FiniteIntOrNull(primordial.extraRbePerBossSkull),
                Active = primordial.active,
                Supported = true,
                CoverageNote = "Native RBE/skull fields are reported; current boss skull state is not projected."
            };
        }

        var unstable = behavior.TryCast<DamageModifierUnstableConcoctionModel>();
        if (unstable != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "unstable_concoction",
                Applicability = "coated_moab_destruction",
                Supported = true,
                CoverageNote = "Native unstable-concoction modifier is identified; runtime stored damage is not exposed by the model."
            };
        }

        var wrath = behavior.TryCast<DamageModifierWrathModel>();
        if (wrath != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "wrath",
                Applicability = "rbe_threshold",
                RbeThreshold = FiniteIntOrNull(wrath.rbeThreshold),
                Damage = wrath.damage >= 0 ? wrath.damage : null,
                MaxDamageBoost = wrath.maxDamageBoost >= 0 ? wrath.maxDamageBoost : null,
                Supported = true,
                CoverageNote = "Native RBE threshold and damage fields are reported without applying them to any branch."
            };
        }

        var sauda = behavior.TryCast<SaudaAfflictionDamageModifierModel>();
        if (sauda != null)
        {
            return new DamageModifierInfoV1
            {
                ModifierType = "sauda_affliction",
                Applicability = "sauda_affliction_tags",
                Level7Tags = ReadConditionValues(sauda.lv7TagsList),
                Level11ExcludeTags = ReadConditionValues(sauda.lv11ExcludeTagsList),
                Level19BloonTags = ReadConditionValues(sauda.lv19BloonTagsList),
                Level7NonMoabBonus = FiniteOrNull(sauda.lv7NonMoabBonus),
                Level7MoabBonus = FiniteOrNull(sauda.lv7MoabBonus),
                Level11NonMoabBonus = FiniteOrNull(sauda.lv11NonMoabBonus),
                Level11MoabBonus = FiniteOrNull(sauda.lv11MoabBonus),
                Level19NonMoabBonus = FiniteOrNull(sauda.lv19NonMoabBonus),
                Level19MoabBonus = FiniteOrNull(sauda.lv19MoabBonus),
                Supported = true,
                CoverageNote = "Native Sauda affliction tag lists and level bonuses are reported without target evaluation."
            };
        }

        var generic = behavior.TryCast<DamageModifierModel>();
        if (generic == null)
            return null;

        string typeName;
        try
        {
            typeName = behavior.GetIl2CppType().Name;
        }
        catch
        {
            typeName = "unknown";
        }

        return new DamageModifierInfoV1
        {
            ModifierType = "unsupported",
            Applicability = "unknown",
            Supported = false,
            CoverageNote = $"Native damage modifier '{typeName}' is present but has no safe field mapping."
        };
    }

    private static List<DamageModifierInfoV1> BuildDamageModifiers(ProjectileModel? projectile)
    {
        var list = new List<DamageModifierInfoV1>();
        if (projectile?.behaviors == null)
            return list;

        foreach (var behavior in projectile.behaviors)
        {
            var descriptor = DescribeDamageModifier(behavior);
            if (descriptor != null)
                list.Add(descriptor);
        }

        return list;
    }
    private static List<AttachedBloonBehaviorInfoV1> ExtractAttachedBloonBehaviors(ProjectileModel? projectile)
    {
        var list = new List<AttachedBloonBehaviorInfoV1>();
        if (projectile?.behaviors == null)
            return list;

        foreach (var behavior in projectile.behaviors)
        {
            if (behavior == null)
                continue;

            var addBehavior = behavior.TryCast<AddBehaviorToBloonModel>();
            if (addBehavior?.behaviors != null)
            {
                foreach (var child in addBehavior.behaviors)
                {
                    if (child == null)
                        continue;

                    string childType = child.GetIl2CppType().Name;
                    var dot = child.TryCast<DamageOverTimeModel>();
                    bool recognized = dot != null
                        || child.TryCast<IncreaseDamageFromAllTypesModel>() != null
                        || child.TryCast<GrowBlockModel>() != null
                        || child.TryCast<FreezeImmunityRemovalModel>() != null
                        || child.TryCast<UnstableConcoctionSplashModel>() != null;
                    bool suspicious = childType.Contains("Damage", StringComparison.Ordinal)
                        || childType.Contains("Percent", StringComparison.Ordinal);

                    var entry = new AttachedBloonBehaviorInfoV1
                    {
                        BehaviorType = childType,
                        Source = "AddBehaviorToBloonModel",
                        MutationId = string.IsNullOrWhiteSpace(addBehavior.mutationId) ? null : addBehavior.mutationId,
                        DurationSeconds = FiniteOrNull(addBehavior.lifespan),
                        Supported = recognized || !suspicious,
                        CoverageNote = recognized || !suspicious
                            ? "Attached bloon behavior is attributed to this projectile branch; target application count is not inferred."
                            : "Attached damage/percent-damage behavior type is present but has no safe field mapping."
                    };
                    if (dot != null)
                    {
                        entry = new AttachedBloonBehaviorInfoV1
                        {
                            BehaviorType = childType,
                            Source = "AddBehaviorToBloonModel",
                            MutationId = string.IsNullOrWhiteSpace(addBehavior.mutationId) ? null : addBehavior.mutationId,
                            Damage = FiniteOrNull(dot.damage),
                            IntervalSeconds = FiniteOrNull(dot.interval),
                            DurationSeconds = FiniteOrNull(addBehavior.lifespan),
                            IsFireBased = dot.isFireBased,
                            Supported = true,
                            CoverageNote = "Damage-over-time behavior is attached to this projectile branch; no DPS or target lifetime is inferred."
                        };
                    }
                    list.Add(entry);
                    if (list.Count >= MaxModifierConditionValues)
                        return list;
                }
            }

            var bonus = behavior.TryCast<AddBonusDamagePerHitToBloonModel>();
            if (bonus != null)
            {
                list.Add(new AttachedBloonBehaviorInfoV1
                {
                    BehaviorType = "AddBonusDamagePerHitToBloonModel",
                    Source = "projectile",
                    MutationId = string.IsNullOrWhiteSpace(bonus.mutationId) ? null : bonus.mutationId,
                    Damage = FiniteOrNull(bonus.perHitDamageAddition),
                    DurationSeconds = FiniteOrNull(bonus.lifespan),
                    Supported = true,
                    CoverageNote = "Per-hit bloon damage addition is attached to this projectile branch; it is not merged into branch damage."
                });
            }

            var directDot = behavior.TryCast<DamageOverTimeModel>();
            string behaviorType = behavior.GetIl2CppType().Name;
            if (directDot != null && addBehavior == null)
            {
                list.Add(new AttachedBloonBehaviorInfoV1
                {
                    BehaviorType = behaviorType,
                    Source = "projectile",
                    Damage = FiniteOrNull(directDot.damage),
                    IntervalSeconds = FiniteOrNull(directDot.interval),
                    IsFireBased = directDot.isFireBased,
                    Supported = true,
                    CoverageNote = "Direct damage-over-time behavior is attributed to this projectile branch; no DPS or target lifetime is inferred."
                });
            }
            else if (behaviorType.Contains("Damage", StringComparison.Ordinal) &&
                     behaviorType.Contains("Percent", StringComparison.Ordinal) &&
                     behavior.TryCast<DamageModifierModel>() == null &&
                     behavior.TryCast<DamageModel>() == null)
            {
                list.Add(new AttachedBloonBehaviorInfoV1
                {
                    BehaviorType = behaviorType,
                    Source = "projectile",
                    Supported = false,
                    CoverageNote = "Attached percent-damage behavior type is present but has no safe field mapping."
                });
            }
        }

        return list;
    }

    private static void AddUnsupportedCoverage(DamageTraversalResultV1 result, string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return;

        const string prefix = "Nested projectile behavior '";
        if (note.StartsWith(prefix, StringComparison.Ordinal))
        {
            int end = note.IndexOf('\'', prefix.Length);
            if (end > prefix.Length)
                result.UnsupportedTypes.Add(note[prefix.Length..end]);
        }

        if (result.Notes.Count < MaxProjectileTraversalNotes && !result.Notes.Contains(note, StringComparer.Ordinal))
            result.Notes.Add(note);
    }

    private static void WalkProjectileSources(
        ProjectileModel? projectile,
        string sourceKind,
        string path,
        int depth,
        HashSet<IntPtr> visited,
        DamageTraversalResultV1 result)
    {
        if (projectile == null)
            return;

        if (depth > MaxProjectileTraversalDepth)
        {
            AddUnsupportedCoverage(result, $"Projectile traversal depth exceeded {MaxProjectileTraversalDepth} at '{path}'.");
            return;
        }

        IntPtr identity = projectile.Pointer;
        if (!visited.Add(identity))
        {
            AddUnsupportedCoverage(result, $"Projectile cycle or repeated reference was skipped at '{path}'.");
            return;
        }

        result.VisitedNodeCount++;
        if (result.VisitedNodeCount > MaxProjectileTraversalSources)
        {
            AddUnsupportedCoverage(result, $"Projectile node limit {MaxProjectileTraversalSources} reached; additional projectile branches omitted.");
            visited.Remove(identity);
            return;
        }

        result.SourceKinds.Add(sourceKind);
        var modifiers = BuildDamageModifiers(projectile);
        var attached = ExtractAttachedBloonBehaviors(projectile);
        foreach (var modifier in modifiers.Where(modifier => !modifier.Supported))
            AddUnsupportedCoverage(result, modifier.CoverageNote);
        foreach (var behavior in attached.Where(behavior => !behavior.Supported))
            AddUnsupportedCoverage(result, behavior.CoverageNote);

        bool sourceSupported = modifiers.All(modifier => modifier.Supported) &&
            attached.All(behavior => behavior.Supported);
        bool sourceLimitReached = false;
        void AddSource(DamageModel? damage)
        {
            if (result.Sources.Count >= MaxProjectileTraversalSources)
            {
                AddUnsupportedCoverage(result, $"Projectile source limit {MaxProjectileTraversalSources} reached; additional damage/attached sources omitted.");
                sourceLimitReached = true;
                return;
            }

            result.Sources.Add(new DamageSourceInfoV1
            {
                SourceKind = sourceKind,
                ProjectileId = projectile.id ?? "",
                Path = path,
                Conditions = sourceKind == "direct"
                    ? []
                    : ["Native child-projectile branch; branch emission timing and count are not evaluated."],
                Damage = damage == null ? null : FiniteOrNull(damage.damage),
                MaxDamage = damage == null ? null : FiniteOrNull(damage.maxDamage),
                DamageType = damage == null ? "Unknown" : GetDamageTypeLabel(damage.immuneBloonProperties),
                BlockedBy = damage == null ? [] : GetBlockedBloonTypes(damage.immuneBloonProperties).ToArray(),
                DamageModifiers = modifiers,
                AttachedBloonBehaviors = attached,
                Supported = sourceSupported,
                CoverageNote = damage == null
                    ? "No native DamageModel was found on this visited projectile; attached behaviors and modifiers are reported without inventing direct damage."
                    : "This entry describes one native DamageModel behavior; sibling and nested branches are reported separately."
            });

            if (damage != null)
            {
                result.Records.Add(new DamageSourceRecordV1
                {
                    Damage = damage,
                    Projectile = projectile,
                    SourceKind = sourceKind,
                    Path = path
                });
            }
        }

        bool emittedDamage = false;
        int contactIndex = 0;
        int exhaustIndex = 0;
        int expireIndex = 0;
        var behaviors = projectile.behaviors;
        if (behaviors != null)
        {
            foreach (var behavior in behaviors)
            {
                if (behavior == null)
                    continue;

                var damage = behavior.TryCast<DamageModel>();
                if (damage != null)
                {
                    emittedDamage = true;
                    AddSource(damage);
                    if (sourceLimitReached)
                    {
                        visited.Remove(identity);
                        return;
                    }
                }

                var contact = behavior.TryCast<CreateProjectileOnContactModel>();
                if (contact?.projectile != null)
                {
                    string childPath = path + ".contact[" + contactIndex + "]";
                    contactIndex++;
                    WalkProjectileSources(contact.projectile, "contact", childPath, depth + 1, visited, result);
                }

                var exhaust = behavior.TryCast<CreateProjectileOnExhaustFractionModel>();
                if (exhaust?.projectile != null)
                {
                    string childPath = path + ".exhaust[" + exhaustIndex + "]";
                    exhaustIndex++;
                    WalkProjectileSources(exhaust.projectile, "exhaust", childPath, depth + 1, visited, result);
                }

                var expire = behavior.TryCast<CreateProjectileOnExpireModel>();
                if (expire?.projectile != null)
                {
                    string childPath = path + ".expire[" + expireIndex + "]";
                    expireIndex++;
                    WalkProjectileSources(expire.projectile, "expire", childPath, depth + 1, visited, result);
                }

                string behaviorType = behavior.GetIl2CppType().Name;
                if (behaviorType.Contains("CreateProjectile", StringComparison.Ordinal) &&
                    contact == null && exhaust == null && expire == null)
                {
                    AddUnsupportedCoverage(result, $"Nested projectile behavior '{behaviorType}' is not one of the supported contact/exhaust/expire forms.");
                }
            }
        }

        if (!emittedDamage)
        {
            AddSource(null);
        }

        visited.Remove(identity);
    }

    private static DamageTraversalResultV1 ExtractDamageSourceTraversal(ProjectileModel? projectile)
    {
        var result = new DamageTraversalResultV1();
        WalkProjectileSources(projectile, "direct", "root", 0, new HashSet<IntPtr>(), result);
        var uniqueSourceKinds = result.SourceKinds.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        result.SourceKinds.Clear();
        result.SourceKinds.AddRange(uniqueSourceKinds);
        var uniqueUnsupportedTypes = result.UnsupportedTypes.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        result.UnsupportedTypes.Clear();
        result.UnsupportedTypes.AddRange(uniqueUnsupportedTypes);
        string[] notes = result.Notes.Distinct(StringComparer.Ordinal).Take(MaxProjectileTraversalNotes).ToArray();
        foreach (var source in result.Sources)
            source.UnsupportedCoverage = notes;
        return result;
    }

    private static DamageCoverageInfoV1 BuildDamageCoverage(DamageTraversalResultV1 traversal)
    {
        return new DamageCoverageInfoV1
        {
            MaxDepth = MaxProjectileTraversalDepth,
            MaxSources = MaxProjectileTraversalSources,
            VisitedNodes = traversal.VisitedNodeCount,
            Complete = traversal.Notes.Count == 0,
            SourceKinds = traversal.SourceKinds.Distinct(StringComparer.Ordinal).ToArray(),
            UnsupportedTypes = traversal.UnsupportedTypes.Distinct(StringComparer.Ordinal).ToArray(),
            Notes = traversal.Notes.Distinct(StringComparer.Ordinal).Take(MaxProjectileTraversalNotes).ToArray()
        };
    }

    private static DamageSourceRecordV1? FindPrimaryDamageSource(ProjectileModel? rootProjectile)
    {
        return FindPrimaryDamageSource(ExtractDamageSourceTraversal(rootProjectile));
    }

    private static DamageSourceRecordV1? FindPrimaryDamageSource(DamageTraversalResultV1 traversal)
    {
        DamageSourceRecordV1? selected = traversal.Records
            .Where(source => source.Path == "root" && source.Damage.damage > 0f)
            .FirstOrDefault();
        selected ??= traversal.Records
            .Where(source => source.Path != "root" && source.Damage.damage > 0f)
            .FirstOrDefault();
        selected ??= traversal.Records.FirstOrDefault(source => source.Path == "root");
        return selected ?? traversal.Records.FirstOrDefault();
    }


    private static ParagonBossDamageInfoV1 BuildParagonBossDamageMetadata(TowerModel? tower, Il2CppAssets.Scripts.Simulation.Towers.Tower? simTower)
    {
        bool isParagon = tower?.isParagon == true;
        if (!isParagon)
        {
            return new ParagonBossDamageInfoV1
            {
                IsParagon = false,
                Source = "not-paragon",
                ValueKind = "none",
                Confidence = "not-applicable",
                CoverageNote = "Tower is not marked as a native Paragon tower."
            };
        }

        var degreeData = InGame.instance?.GetGameModel()?.paragonDegreeDataModel ?? Game.instance?.model?.paragonDegreeDataModel;
        float? configuredPercent = degreeData == null ? null : FiniteOrNull(degreeData.bonusBossDamagePercent);
        int? configuredEvery = degreeData == null || degreeData.bonusBossDamagePerDegrees <= 0
            ? null
            : degreeData.bonusBossDamagePerDegrees;

        int? currentDegree = null;
        float? activeBonus = null;
        string source = degreeData == null ? "native-paragon-state" : "native-paragon-state-and-degree-data";
        if (simTower != null)
        {
            try
            {
                SimulationParagonTower? paragon = null;
                var behaviors = simTower.modelBehaviors;
                for (int i = 0; behaviors != null && i < behaviors.Count && paragon == null; i++)
                    paragon = behaviors[i]?.TryCast<SimulationParagonTower>();
                if (paragon != null)
                {
                    int degree = paragon.GetCurrentDegree();
                    if (degree is >= 0 and <= 100)
                        currentDegree = degree;

                    var mutator = paragon.GetDegreeMutator(paragon.investmentInfo.totalInvestment, null!);
                    if (mutator != null)
                        activeBonus = FiniteOrNull(mutator.bonusBossDamagePercent);
                }
            }
            catch
            {
                source = degreeData == null ? "native-paragon-state-unavailable" : "native-paragon-state-and-degree-data-partial";
            }
        }

        return new ParagonBossDamageInfoV1
        {
            IsParagon = true,
            Source = source,
            ValueKind = activeBonus.HasValue ? "configured_scaling_and_active_degree_mutator" : "configured_scaling_only",
            ConfiguredBonusPercent = configuredPercent,
            ConfiguredEveryDegrees = configuredEvery,
            CurrentDegree = currentDegree,
            CurrentDegreeBonusPercent = activeBonus,
            ActualTargetDamageBonusPercent = null,
            Confidence = activeBonus.HasValue ? "native_mutator_field" : configuredPercent.HasValue ? "native_degree_data_only" : "unknown",
            CoverageNote = "Configured boss scaling and active mutator fields are reported separately. Actual final target damage is not calculated; no degree formula or bonus stacking is inferred."
        };
    }
}
