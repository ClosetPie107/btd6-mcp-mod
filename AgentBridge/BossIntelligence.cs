using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Data;
using Il2CppAssets.Scripts.Data.Boss;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppAssets.Scripts.Models.Bloons.Behaviors;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Simulation.Bloons.Behaviors;
using Il2CppAssets.Scripts.Simulation.Track;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppNinjaKiwi.Localization;
using Il2CppAssets.Scripts.Unity;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int MaxBossCatalogEntries = 64;
    private const int MaxBossModelVariants = 256;
    private const int MaxBossLocalizationKeys = 48;
    private static GameModel? GetNativeGameModel()
    {
        try
        {
            var model = Game.instance?.model;
            if (model != null)
                return model;
        }
        catch { }
        try { return InGame.instance?.GetGameModel(); } catch { return null; }
    }
    private const int MaxBossRuntimeEntries = 64;
    private const int MaxBossCoverageNames = 32;

    private static BridgeResultV1 HandleBossCatalog(BridgeRequestV1 request)
    {
        string filter = ReadString(request.Payload, "bossType", "").Trim();
        try
        {
            var gameData = GameData.Instance;
            var bossItems = gameData?.bosses?.BossList?.items;
            if (bossItems == null)
                return ErrorResult(request, "GAME_DATA_UNAVAILABLE", "Native GameData boss catalog is unavailable.", true);

            var gameModel = GetNativeGameModel();
            var entries = new List<BossCatalogEntryV1>();
            bool filterMatched = string.IsNullOrEmpty(filter);
            var coverageNotes = new List<string>();
            int count = 0;
            foreach (var data in bossItems)
            {
                if (data == null || count >= MaxBossCatalogEntries)
                    continue;
                string bossType = data.id.ToString();
                if (!string.IsNullOrEmpty(filter) && !string.Equals(filter, bossType, StringComparison.OrdinalIgnoreCase))
                    continue;
                filterMatched = true;
                entries.Add(BuildBossCatalogEntry(data, gameModel));
                count++;
            }

            if (!string.IsNullOrEmpty(filter) && !filterMatched)
                return ErrorResult(request, "UNKNOWN_BOSS", $"Unknown native boss type '{filter}'.", false);
            if (count >= MaxBossCatalogEntries)
                coverageNotes.Add($"Native boss catalog was bounded at {MaxBossCatalogEntries} entries.");
            if (gameModel == null)
                coverageNotes.Add("The active native GameModel was unavailable; model variants and mechanics are omitted.");

            return SuccessResult(request, new BossCatalogResultV1
            {
                Version = Application.version,
                BossTypeFilter = string.IsNullOrEmpty(filter) ? null : filter,
                Entries = entries,
                Coverage = new BossCoverageV1
                {
                    Status = gameModel == null ? "partial" : "supported",
                    Notes = coverageNotes
                }
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "BOSS_CATALOG_FAILED", $"Native boss catalog could not be read: {ex.Message}", true);
        }
    }

    private static BossCatalogEntryV1 BuildBossCatalogEntry(BossData data, GameModel? gameModel)
    {
        string bossType = data.id.ToString();
        string locsKey = "";
        try { locsKey = data.LocsKey ?? ""; } catch { }
        var description = ReadBossDescription(locsKey, out string? displayName, out string? displayNameKey);
        var variants = new List<BossModelVariantV1>();
        var coverage = new BossCoverageV1Builder();
        if (gameModel?.bloons != null)
        {
            int modelCount = 0;
            foreach (var model in gameModel.bloons)
            {
                if (model == null || modelCount >= MaxBossModelVariants)
                    continue;
                bool isBoss = false;
                bool isSegment = false;
                try { isBoss = model.isBoss; } catch { }
                try { isSegment = model.isBossSegment; } catch { }
                if ((!isBoss && !isSegment) || !MatchesBoss(model, bossType))
                    continue;
                variants.Add(BuildBossModelVariant(model, coverage));
                modelCount++;
            }
            if (modelCount >= MaxBossModelVariants)
                coverage.Notes.Add($"Native boss model variants were bounded at {MaxBossModelVariants} entries.");
        }
        else
        {
            coverage.Notes.Add("Native GameModel unavailable for this catalog read.");
        }

        return new BossCatalogEntryV1
        {
            BossType = bossType,
            LocsKey = locsKey,
            DisplayName = displayName,
            DisplayNameKey = displayNameKey,
            Description = description,
            BaselineVariants = variants,
            Coverage = coverage.Build()
        };
    }

    private static BossDescriptionV1 ReadBossDescription(string locsKey, out string? displayName, out string? displayNameKey)
    {
        displayName = null;
        displayNameKey = null;
        if (string.IsNullOrWhiteSpace(locsKey))
            return new BossDescriptionV1 { Provenance = "unavailable_no_locs_key" };

        try
        {
            var localization = LocalizationManager.Instance;
            if (localization == null)
                return new BossDescriptionV1 { Provenance = "unavailable_localization_manager" };

            var keys = new List<string>();
            AddLocalizationKeys(localization, locsKey, keys);
            if (keys.Count == 0)
                AddLocalizationKeys(localization, locsKey.TrimEnd('.') + ".", keys);

            string? descriptionKey = null;
            string? descriptionText = null;
            foreach (string key in keys)
            {
                if (key.Contains("description", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("desc", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("hint", StringComparison.OrdinalIgnoreCase))
                {
                    if (localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value))
                    {
                        descriptionKey = key;
                        descriptionText = value;
                        break;
                    }
                }
            }

            foreach (string key in keys)
            {
                if (key.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, locsKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value))
                    {
                        displayNameKey = key;
                        displayName = value;
                        break;
                    }
                }
            }

            if (displayName == null && localization.TryGetTextEnglish(locsKey, out string directName) && !string.IsNullOrWhiteSpace(directName))
            {
                displayNameKey = locsKey;
                displayName = directName;
            }

            return new BossDescriptionV1
            {
                Available = descriptionText != null,
                Text = descriptionText,
                Key = descriptionKey,
                Provenance = descriptionText == null ? "unavailable_native_key_missing" : "native_localization_english",
                DiscoveredKeys = keys
            };
        }
        catch
        {
            return new BossDescriptionV1 { Provenance = "unavailable_localization_read_failed" };
        }
    }

    private static void AddLocalizationKeys(LocalizationManager localization, string prefix, List<string> output)
    {
        if (output.Count >= MaxBossLocalizationKeys)
            return;
        try
        {
            var keys = localization.GetKeysWithPrefix(prefix);
            if (keys == null)
                return;
            var iterator = keys.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
            while (iterator.MoveNext())
            {
                string key = iterator.Current?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(key) || output.Contains(key, StringComparer.OrdinalIgnoreCase))
                    continue;
                output.Add(key);
                if (output.Count >= MaxBossLocalizationKeys)
                    break;
            }
        }
        catch { }
    }

    private static BossModelVariantV1 BuildBossModelVariant(BloonModel model, BossCoverageV1Builder coverage)
    {
        string id = SafeString(() => model.id) ?? SafeString(() => model.name) ?? "";
        string baseId = SafeString(() => model.baseId) ?? "";
        return new BossModelVariantV1
        {
            Id = id,
            BaseId = baseId,
            VariantName = SafeString(() => model.variantName),
            VariantPrefix = SafeString(() => model.variantPrefix),
            Tier = ParseNativeTier(id),
            TierSource = ParseNativeTier(id).HasValue ? "native_model_id_suffix" : null,
            IsBoss = SafeBool(() => model.isBoss),
            IsBossSegment = SafeBool(() => model.isBossSegment),
            LayerNumber = SafeInt(() => model.layerNumber),
            MaxHealth = SafeIntNullable(() => model.maxHealth),
            BloonProperties = SafeEnumStringNullable(() => model.bloonProperties),
            Tags = ReadStrings(model.tags, 32),
            Mechanics = BuildBossMechanics(model, coverage)
        };
    }

    private static BossMechanicsV1 BuildBossMechanics(BloonModel model, BossCoverageV1Builder coverage)
    {
        coverage.Observed = true;
        var skulls = new List<HealthSkullModelV1>();
        var repeating = new List<HealthSkullModelV1>();
        var recognized = new List<string>();
        LychMechanicsV1? lych = null;
        PhayzeMechanicsV1? phayze = null;
        DreadMechanicsV1? dread = null;
        if (model.behaviors != null)
        {
            foreach (var behavior in model.behaviors)
            {
                if (behavior == null)
                    continue;
                try
                {
                    var trigger = behavior.TryCast<HealthPercentTriggerModel>();
                    if (trigger != null)
                    {
                        var percentages = trigger.percentageValues;
                        var actions = trigger.actionIds;
                        for (int i = 0; percentages != null && i < percentages.Length && i < 32; i++)
                        {
                            float percentage = percentages[i];
                            if (!float.IsFinite(percentage))
                                continue;
                            (trigger.repeatFirst ? repeating : skulls).Add(new HealthSkullModelV1
                            {
                                Percentage = percentage,
                                ActionId = actions != null && i < actions.Length ? actions[i] : null,
                                RepeatFirst = trigger.repeatFirst,
                                PreventFallthrough = trigger.preventFallthrough
                            });
                        }
                        recognized.Add(trigger.repeatFirst ? "repeating_health_trigger" : "health_skull_trigger");
                        continue;
                    }
                    var lychModel = behavior.TryCast<LychBossSuperScriptModel>();
                    if (lychModel != null)
                    {
                        lych = new LychMechanicsV1
                        {
                            EtherealKillTrigger = SafeIntNullable(() => lychModel.totalKills),
                            EtherealHealthPercentages = ReadFiniteFloats(lychModel.etherealHealthPercentageValues, 32),
                            DrainInterval = SafeFloatNullable(() => lychModel.drainInterval),
                            RegrowInterval = SafeFloatNullable(() => lychModel.regrowInterval),
                            TombstoneInterval = SafeFloatNullable(() => lychModel.tombstoneInterval),
                            TombstoneHealthOverride = SafeFloatNullable(() => lychModel.tombstoneHealthOverride),
                            TombstoneMoabHealthOverride = SafeFloatNullable(() => lychModel.tombstoneMoabHealthOverride),
                            TombstoneBfbHealthOverride = SafeFloatNullable(() => lychModel.tombstoneBfbHealthOverride),
                            TombstoneZomgHealthOverride = SafeFloatNullable(() => lychModel.tombstoneZomgHealthOverride),
                            TombstoneSpawnSpeedModifier = SafeFloatNullable(() => lychModel.tombstoneSpawnSpeedModifier)
                        };
                        recognized.Add("lych_super_script");
                        continue;
                    }
                    var phayzeModel = behavior.TryCast<PhayzeBehaviorModel>();
                    if (phayzeModel != null)
                    {
                        phayze = new PhayzeMechanicsV1
                        {
                            PowerLevels = ReadFiniteFloats(phayzeModel.powerLevels, 32),
                            ShieldSpeedBoost = SafeFloatNullable(() => phayzeModel.shieldSpeedBoost),
                            EnterCamoAnimation = SafeString(() => phayzeModel.enterCamoAnimationName),
                            EnterCamoImmunityAnimation = SafeString(() => phayzeModel.enterCamoImmunityAnimationName),
                            ExitCamoImmunityAnimation = SafeString(() => phayzeModel.exitCamoImmunityAnimationName),
                            ExitCamoAnimation = SafeString(() => phayzeModel.exitCamoAnimationName)
                        };
                        recognized.Add("phayze_behavior");
                        continue;
                    }
                    var dreadModel = behavior.TryCast<DreadbloonRushBehaviorModel>();
                    if (dreadModel != null)
                    {
                        dread = new DreadMechanicsV1
                        {
                            BaseArmour = SafeIntNullable(() => dreadModel.baseArmour),
                            ArmourMultiplier = SafeFloatNullable(() => dreadModel.armourMultiplier),
                            DamageReduction = SafeIntNullable(() => dreadModel.damageReduction),
                            RockBloonBaseHealth = SafeIntNullable(() => dreadModel.rockBloonBaseHealth),
                            RockBloonHealthMultiplier = SafeFloatNullable(() => dreadModel.rockBloonHealthMultiplier),
                            RockBloonAmount = SafeIntNullable(() => dreadModel.rockBloonAmount),
                            RockBloonSpawnDelay = SafeFloatNullable(() => dreadModel.rockBloonSpawnDelay),
                            ModelBloonProperties = SafeEnumStringNullable(() => model.bloonProperties)
                        };
                        recognized.Add("dreadbloon_rush_behavior");
                        continue;
                    }
                    string behaviorName = behavior.GetIl2CppType().Name;
                    if (IsPotentialBossMechanic(behaviorName))
                        coverage.AddUnknown(behaviorName);
                }
                catch { }
            }
        }
        return new BossMechanicsV1
        {
            HealthSkulls = skulls,
            RepeatingHealthTriggers = repeating,
            Lych = lych,
            Phayze = phayze,
            Dread = dread,
            RecognizedMechanics = recognized.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static BridgeResultV1 HandleInspectBoss(BridgeRequestV1 request)
    {
        string requested = ReadString(request.Payload, "bloonId", "").Trim();
        var inGame = InGame.instance;
        bool active = false;
        try { active = inGame != null && inGame.IsInGame(); } catch { }
        try
        {
            if (!active || inGame?.bridge == null)
            {
                if (!string.IsNullOrEmpty(requested))
                    return ErrorResult(request, "UNKNOWN_BLOON_ID", $"Runtime boss ID '{requested}' is unavailable without an active native simulation.", false);
                return SuccessResult(request, new BossInspectResultV1
                {
                    ActiveGame = false,
                    RequestedBloonId = null,
                    Encounter = new BossEncounterV1 { Status = "unavailable_no_active_game" },
                    Coverage = new BossCoverageV1 { Status = "partial", Notes = ["No active native simulation; an empty boss list is valid."] }
                });
            }
            var nativeTypes = ReadNativeBossTypes();
            var coverage = new BossCoverageV1Builder();
            var bosses = ReadRuntimeBosses(inGame.bridge, requested, nativeTypes, coverage);
            if (!string.IsNullOrEmpty(requested) && bosses.Count == 0)
                return ErrorResult(request, "UNKNOWN_BLOON_ID", $"Runtime boss ID '{requested}' was not found in the active native simulation.", false);
            return SuccessResult(request, new BossInspectResultV1
            {
                ActiveGame = true,
                RequestedBloonId = string.IsNullOrEmpty(requested) ? null : requested,
                Encounter = ReadBossEncounter(inGame.bridge),
                Bosses = bosses,
                Coverage = coverage.Build()
            });

        }
        catch (Exception ex)
        {
            return ErrorResult(request, "BOSS_INSPECTION_FAILED", $"Native boss state could not be read: {ex.Message}", true);
        }
    }

    private static List<BossSummaryV1> BuildBossSummaries()
    {
        var result = new List<BossSummaryV1>();
        try
        {
            var inGame = InGame.instance;
            if (inGame == null || !inGame.IsInGame() || inGame.bridge == null)
                return result;
            var coverage = new BossCoverageV1Builder();
            var bosses = ReadRuntimeBosses(inGame.bridge, "", ReadNativeBossTypes(), coverage);
            foreach (var boss in bosses)
            {
                result.Add(new BossSummaryV1
                {
                    Id = boss.Id,
                    BloonId = boss.BloonId,
                    BaseId = boss.BaseId,
                    BossType = boss.BossType,
                    IsBoss = boss.IsBoss,
                    IsBossSegment = boss.IsBossSegment,
                    IsElite = boss.IsElite,
                    Tier = boss.Tier,
                    Health = boss.EffectiveLiveHealth,
                    Armour = boss.Armour.Current,
                    ModelBloonProperties = boss.Immunity.ModelBloonProperties,
                    RuntimeTowerSetImmunity = boss.Immunity.RuntimeTowerSet,
                    Invulnerable = boss.Immunity.RuntimeInvulnerable,
                    Untargetable = boss.Immunity.Untargetable,
                    CurrentSkull = boss.Skulls.Current,
                    DamageUntilNextSkull = boss.Skulls.DamageUntilNext,
                    Progress = boss.Position.Progress,
                    Coverage = coverage.Build()
                });
            }
        }
        catch { }
        return result;
    }

    private static List<BossRuntimeV1> ReadRuntimeBosses(UnityToSimulation bridge, string requested, List<string> nativeTypes, BossCoverageV1Builder coverage)
    {
        var result = new List<BossRuntimeV1>();
        var allBloons = bridge.GetAllBloons();
        if (allBloons == null)
            return result;
        var bloonEnum = allBloons.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
        while (bloonEnum.MoveNext() && result.Count < MaxBossRuntimeEntries)
        {
            var wrapper = bloonEnum.Current?.TryCast<BloonToSimulation>();
            if (wrapper == null || wrapper.Def == null)
                continue;
            var def = wrapper.Def;
            bool isBoss = SafeBool(() => def.isBoss);
            bool isSegment = SafeBool(() => def.isBossSegment);
            if (!isBoss && !isSegment)
                continue;
            string runtimeId = SafeObjectId(wrapper.id);
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, runtimeId, StringComparison.Ordinal))
                continue;
            result.Add(BuildRuntimeBoss(wrapper, nativeTypes, coverage));
        }
        if (result.Count >= MaxBossRuntimeEntries)
            coverage.Notes.Add($"Active boss list was bounded at {MaxBossRuntimeEntries} entries.");
        return result;
    }

    private static BossRuntimeV1 BuildRuntimeBoss(BloonToSimulation wrapper, List<string> nativeTypes, BossCoverageV1Builder coverage)
    {
        BloonModel def = wrapper.Def;
        Bloon? sim = null;
        try { sim = wrapper.GetSimBloon(); } catch { }
        string bloonId = SafeString(() => def.id) ?? SafeString(() => def.name) ?? "";
        string baseId = SafeString(() => def.baseId) ?? "";
        int? liveHealth = null;
        int? effectiveMax = null;
        int? armour = null;
        bool? hasArmour = null;
        float? armourProportion = null;
        float? distance = null;
        float? progress = null;
        float? x = null, y = null, z = null;
        string? runtimeTowerSet = null;
        bool? runtimeInvulnerable = null;
        bool? untargetable = null;
        int? currentSkull = null;
        int? damageUntilNext = null;
        float? healthPercent = null;
        bool? lychEthereal = null;
        bool? phayzePhase = null;
        bool? invulnerabilityOverride = null;
        if (sim?.bloonModel != null)
            BuildBossMechanics(sim.bloonModel, coverage);
        else
            BuildBossMechanics(def, coverage);
        var thresholds = new List<float>();
        if (sim != null)
        {
            liveHealth = SafeIntNullable(() => sim.Health);
            effectiveMax = SafeIntNullable(() => sim.bloonModel.maxHealth);
            armour = SafeIntNullable(() => sim.CurrentArmourAmount);
            hasArmour = SafeBoolNullable(() => sim.HasArmour);
            armourProportion = SafeFloatNullable(() => sim.CurrentArmourProportion);
            distance = SafeFloatNullable(() => sim.DistanceTraveled);
            progress = SafeFloatNullable(() => sim.PercThroughMap());
            runtimeTowerSet = SafeEnumStringNullable(() => sim.TowerSetImmunity);
            runtimeInvulnerable = SafeBoolNullable(() => sim.IsInvulnerable);
            untargetable = SafeBoolNullable(() => sim.NonTargetable);
            var position = SafeVector3(() => wrapper.position);
            x = position?.x; y = position?.y; z = position?.z;
            ReadRuntimeBehaviors(sim, ref currentSkull, ref damageUntilNext, thresholds, ref lychEthereal, ref phayzePhase, ref invulnerabilityOverride);
        }
        int? baseMax = SafeIntNullable(() => def.maxHealth);
        if (effectiveMax.HasValue && effectiveMax > 0 && liveHealth.HasValue)
        {
            float p = (float)liveHealth.Value / effectiveMax.Value;
            if (float.IsFinite(p)) healthPercent = Math.Clamp(p, 0f, 1f);
        }
        string? bossType = ResolveBossType(bloonId, baseId, nativeTypes);
        bool? elite = DetectElite(bloonId, baseId);
        return new BossRuntimeV1
        {
            Id = SafeObjectId(wrapper.id),
            BloonId = bloonId,
            BaseId = baseId,
            BossType = bossType,
            IsBoss = SafeBool(() => def.isBoss),
            IsBossSegment = SafeBool(() => def.isBossSegment),
            IsElite = elite,
            Tier = ParseNativeTier(bloonId),
            BaseModelMaxHealth = baseMax,
            EffectiveModelMaxHealth = effectiveMax,
            EffectiveLiveHealth = liveHealth,
            HealthScaling = effectiveMax.HasValue ? (baseMax.HasValue && baseMax.Value != effectiveMax.Value ? "native_simulation_model_modified" : "native_simulation_model") : "unknown",
            Armour = new BossArmourV1
            {
                HasArmour = hasArmour,
                Current = armour,
                CurrentProportion = armourProportion,
                ModelMultiplier = SafeFloatNullable(() => def.armourMultiplier)
            },
            Immunity = new BossImmunityV1
            {
                ModelBloonProperties = SafeEnumStringNullable(() => def.bloonProperties),
                RuntimeTowerSet = runtimeTowerSet,
                ModelInvulnerable = SafeBoolNullable(() => def.isInvulnerable),
                RuntimeInvulnerable = runtimeInvulnerable,
                Untargetable = untargetable
            },
            Position = new BossPositionV1 { X = x, Y = y, Z = z, Progress = progress, DistanceTraveled = distance },
            Skulls = new BossSkullRuntimeV1 { Current = currentSkull, DamageUntilNext = damageUntilNext, Thresholds = thresholds, HealthPercent = healthPercent },
            SpecialState = new BossSpecialStateV1
            {
                LychEthereal = lychEthereal,
                PhayzeCamoImmunityPhase = phayzePhase,
                InvulnerabilityOverride = invulnerabilityOverride,
                Coverage = lychEthereal.HasValue || phayzePhase.HasValue || invulnerabilityOverride.HasValue ? "native_behavior" : "unknown"
            }
        };
    }

    private static void ReadRuntimeBehaviors(Bloon sim, ref int? currentSkull, ref int? damageUntilNext, List<float> thresholds,
        ref bool? lychEthereal, ref bool? phayzePhase, ref bool? invulnerabilityOverride)
    {
        try
        {
            var behaviorList = sim.bloonBehaviors?.list;
            if (behaviorList == null)
                return;
            // Game-thread-only read: indexing does not acquire a LockList enumeration lease.
            for (int i = 0; i < behaviorList.Count; i++)
            {
                var behavior = behaviorList[i];
                if (behavior == null)
                    continue;
                var health = behavior.TryCast<HealthPercentTrigger>();
                if (health?.modl != null && !health.modl.repeatFirst && !currentSkull.HasValue)
                {
                    currentSkull = health.GetCurrentSkull();
                    damageUntilNext = health.GetDamageUntilNextSkull();
                    if (health.modl?.percentageValues != null)
                        thresholds.AddRange(ReadFiniteFloats(health.modl.percentageValues, 32));
                    continue;
                }
                var lych = behavior.TryCast<LychBossSuperScript>();
                if (lych != null)
                {
                    currentSkull = SafeIntNullable(() => lych.CurrentSkull);
                    lychEthereal = lych.etherealActive;
                    invulnerabilityOverride = lych.invulnerableOverride;
                    if (lych.lychBossSuperScriptModel?.etherealHealthPercentageValues != null)
                        thresholds.AddRange(ReadFiniteFloats(lych.lychBossSuperScriptModel.etherealHealthPercentageValues, 32));
                    continue;
                }
                var phayze = behavior.TryCast<PhayzeBehavior>();
                if (phayze != null)
                    phayzePhase = phayze.isInCamoImmunityPhase;
            }
        }
        catch { }
    }

    private static BossEncounterV1 ReadBossEncounter(UnityToSimulation bridge)
    {
        try
        {
            var manager = bridge.Simulation?.Map?.spawner?.bossBloonManager;
            if (manager == null)
                return new BossEncounterV1 { Status = "unavailable" };
            int tier = manager.CurrentBossTier;
            var next = manager.GetNextBossSpawnRound();
            int deadline = manager.GetBossMustBeDefeatedByRound();
            Bloon? current = manager.CurrentBoss;
            bool? elite = null;
            if (current?.bloonModel != null)
                elite = DetectElite(SafeString(() => current.bloonModel.id) ?? "", SafeString(() => current.bloonModel.baseId) ?? "");
            return new BossEncounterV1
            {
                CurrentTier = current == null || tier < 1 ? null : tier,
                IsElite = elite,
                NextSpawnRound = next.HasValue && next.Value > 0 ? next.Value + 1 : null,
                DefeatByRound = deadline > 0 ? deadline + 1 : null,
                Status = current == null ? "idle" : "active"
            };
        }
        catch
        {
            return new BossEncounterV1 { Status = "unavailable_native_read_failed" };
        }
    }

    private static List<string> ReadNativeBossTypes()
    {
        var result = new List<string>();
        try
        {
            var items = GameData.Instance?.bosses?.BossList?.items;
            if (items == null)
                return result;
            foreach (var item in items)
            {
                if (item == null || result.Count >= MaxBossCatalogEntries)
                    continue;
                result.Add(item.id.ToString());
            }
        }
        catch { }
        return result;
    }

    private static bool MatchesBoss(BloonModel model, string bossType)
    {
        string id = SafeString(() => model.id) ?? "";
        string baseId = SafeString(() => model.baseId) ?? "";
        return id.StartsWith(bossType, StringComparison.OrdinalIgnoreCase) || baseId.StartsWith(bossType, StringComparison.OrdinalIgnoreCase) ||
               id.Contains(bossType, StringComparison.OrdinalIgnoreCase) || baseId.Contains(bossType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveBossType(string id, string baseId, List<string> nativeTypes)
    {
        foreach (string type in nativeTypes)
        {
            if (id.StartsWith(type, StringComparison.OrdinalIgnoreCase) || baseId.StartsWith(type, StringComparison.OrdinalIgnoreCase) ||
                id.Contains(type, StringComparison.OrdinalIgnoreCase) || baseId.Contains(type, StringComparison.OrdinalIgnoreCase))
                return type;
        }
        return null;
    }

    private static bool? DetectElite(string id, string baseId)
    {
        if (id.Contains("elite", StringComparison.OrdinalIgnoreCase) || baseId.Contains("elite", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Length > 0 || baseId.Length > 0) return false;
        return null;
    }

    private static int? ParseNativeTier(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int end = id.Length - 1;
        while (end >= 0 && char.IsDigit(id[end])) end--;
        if (end == id.Length - 1) return null;
        string suffix = id.Substring(end + 1);
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int tier) && tier > 0 && tier <= 100 ? tier : null;
    }

    private static List<string> ReadStrings(Il2CppStringArray? values, int max)
    {
        var result = new List<string>();
        if (values == null) return result;
        for (int i = 0; i < values.Length && i < max; i++)
        {
            string? value = values[i];
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return result;
    }

    private static List<float> ReadFiniteFloats(Il2CppStructArray<float>? values, int max)
    {
        var result = new List<float>();
        if (values == null) return result;
        for (int i = 0; i < values.Length && i < max; i++)
        {
            float value = values[i];
            if (float.IsFinite(value)) result.Add(value);
        }
        return result;
    }

    // A future combat behavior need not contain "Boss" or a known boss name.
    // Exclude only plainly cosmetic types; unfamiliar gameplay behavior stays visible.
    private static bool IsPotentialBossMechanic(string name) =>
        !name.StartsWith("CreateSound", StringComparison.Ordinal) &&
        !name.StartsWith("CreateEffect", StringComparison.Ordinal) &&
        !name.EndsWith("DisplayModel", StringComparison.Ordinal) &&
        name != "PlayAnimationActionModel";

    private static string SafeObjectId(Il2CppAssets.Scripts.ObjectId id)
    {
        try { return id.ToString(); } catch { return ""; }
    }

    private static string? SafeString(Func<string> read)
    {
        try { var value = read(); return string.IsNullOrWhiteSpace(value) ? null : value; } catch { return null; }
    }

    private static bool SafeBool(Func<bool> read) { try { return read(); } catch { return false; } }
    private static bool? SafeBoolNullable(Func<bool> read) { try { return read(); } catch { return null; } }
    private static int SafeInt(Func<int> read) { try { return read(); } catch { return 0; } }
    private static int? SafeIntNullable(Func<int> read) { try { return read(); } catch { return null; } }
    private static float? SafeFloatNullable(Func<float> read)
    {
        try { float value = read(); return float.IsFinite(value) ? value : null; } catch { return null; }
    }
    private static string SafeEnumString(Func<object> read) { try { return read()?.ToString() ?? ""; } catch { return ""; } }
    private static string? SafeEnumStringNullable(Func<object> read) { try { return read()?.ToString(); } catch { return null; } }
    private static Vector3? SafeVector3(Func<Vector3> read)
    {
        try
        {
            Vector3 value = read();
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) ? value : null;
        }
        catch { return null; }
    }

    private sealed class BossCoverageV1Builder
    {
        public readonly List<string> Unknown = new();
        public readonly List<string> Notes = new();
        public bool Observed;
        public void AddUnknown(string value)
        {
            if (Unknown.Count < MaxBossCoverageNames && !Unknown.Contains(value, StringComparer.Ordinal)) Unknown.Add(value);
        }
        public BossCoverageV1 Build() => new()
        {
            Status = Observed && Unknown.Count == 0 ? "supported" : "partial",
            UnknownBehaviorTypes = Unknown.ToList(),
            Notes = Notes.ToList()
        };
    }
}
