using System;
using System.Collections.Generic;

namespace AgentBridge;

internal sealed class BossCatalogResultV1
{
    public int SchemaVersion { get; init; } = 1;
    public string Source { get; init; } = "native_game_data";
    public string Version { get; init; } = "unknown";
    public string? BossTypeFilter { get; init; }
    public List<BossCatalogEntryV1> Entries { get; init; } = [];
    public BossCoverageV1 Coverage { get; init; } = new();
}

internal sealed class BossCatalogEntryV1
{
    public string BossType { get; init; } = "";
    public string LocsKey { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? DisplayNameKey { get; init; }
    public BossDescriptionV1 Description { get; init; } = new();
    public List<BossModelVariantV1> BaselineVariants { get; init; } = [];
    public BossCoverageV1 Coverage { get; init; } = new();
}

internal sealed class BossDescriptionV1
{
    public bool Available { get; init; }
    public string? Text { get; init; }
    public string? Key { get; init; }
    public string Provenance { get; init; } = "unavailable";
    public List<string> DiscoveredKeys { get; init; } = [];
}

internal sealed class BossModelVariantV1
{
    public string Id { get; init; } = "";
    public string BaseId { get; init; } = "";
    public string? VariantName { get; init; }
    public string? VariantPrefix { get; init; }
    public int? Tier { get; init; }
    public string? TierSource { get; init; }
    public bool IsBoss { get; init; }
    public bool IsBossSegment { get; init; }
    public int LayerNumber { get; init; }
    public int? MaxHealth { get; init; }
    public float? ArmourMultiplier { get; init; }
    public string? BloonProperties { get; init; }
    public List<string> Tags { get; init; } = [];
    public BossMechanicsV1 Mechanics { get; init; } = new();
}

internal sealed class BossMechanicsV1
{
    public List<HealthSkullModelV1> HealthSkulls { get; init; } = [];
    public List<HealthSkullModelV1> RepeatingHealthTriggers { get; init; } = [];
    public LychMechanicsV1? Lych { get; init; }
    public PhayzeMechanicsV1? Phayze { get; init; }
    public DreadMechanicsV1? Dread { get; init; }
    public List<string> RecognizedMechanics { get; init; } = [];
}

internal sealed class HealthSkullModelV1
{
    public float Percentage { get; init; }
    public string? ActionId { get; init; }
    public bool RepeatFirst { get; init; }
    public bool PreventFallthrough { get; init; }
}

internal sealed class LychMechanicsV1
{
    public int? EtherealKillTrigger { get; init; }
    public List<float> EtherealHealthPercentages { get; init; } = [];
    public float? DrainInterval { get; init; }
    public float? RegrowInterval { get; init; }
    public float? TombstoneInterval { get; init; }
    public float? TombstoneHealthOverride { get; init; }
    public float? TombstoneMoabHealthOverride { get; init; }
    public float? TombstoneBfbHealthOverride { get; init; }
    public float? TombstoneZomgHealthOverride { get; init; }
    public float? TombstoneSpawnSpeedModifier { get; init; }
}

internal sealed class PhayzeMechanicsV1
{
    public List<float> PowerLevels { get; init; } = [];
    public float? ShieldSpeedBoost { get; init; }
    public string? EnterCamoAnimation { get; init; }
    public string? EnterCamoImmunityAnimation { get; init; }
    public string? ExitCamoImmunityAnimation { get; init; }
    public string? ExitCamoAnimation { get; init; }
}

internal sealed class DreadMechanicsV1
{
    public int? BaseArmour { get; init; }
    public float? ArmourMultiplier { get; init; }
    public int? DamageReduction { get; init; }
    public int? RockBloonBaseHealth { get; init; }
    public float? RockBloonHealthMultiplier { get; init; }
    public int? RockBloonAmount { get; init; }
    public float? RockBloonSpawnDelay { get; init; }
    public string? ModelBloonProperties { get; init; }
}

internal sealed class BossCoverageV1
{
    public string Status { get; init; } = "partial";
    public List<string> UnknownBehaviorTypes { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

internal sealed class BossInspectResultV1
{
    public int SchemaVersion { get; init; } = 1;
    public string Source { get; init; } = "native_simulation";
    public bool ActiveGame { get; init; }
    public string? RequestedBloonId { get; init; }
    public BossEncounterV1 Encounter { get; init; } = new();
    public List<BossRuntimeV1> Bosses { get; init; } = [];
    public BossCoverageV1 Coverage { get; init; } = new();
}

internal sealed class BossRuntimeV1
{
    public string Id { get; init; } = "";
    public string BloonId { get; init; } = "";
    public string BaseId { get; init; } = "";
    public string? BossType { get; init; }
    public bool IsBoss { get; init; }
    public bool IsBossSegment { get; init; }
    public bool? IsElite { get; init; }
    public int? Tier { get; init; }
    public int? BaseModelMaxHealth { get; init; }
    public int? EffectiveModelMaxHealth { get; init; }
    public int? EffectiveLiveHealth { get; init; }
    public string HealthScaling { get; init; } = "unknown";
    public BossArmourV1 Armour { get; init; } = new();
    public BossImmunityV1 Immunity { get; init; } = new();
    public BossPositionV1 Position { get; init; } = new();
    public BossSkullRuntimeV1 Skulls { get; init; } = new();
    public BossSpecialStateV1 SpecialState { get; init; } = new();
}

internal sealed class BossArmourV1
{
    public bool? HasArmour { get; init; }
    public int? Current { get; init; }
    public float? CurrentProportion { get; init; }
    public float? ModelMultiplier { get; init; }
}

internal sealed class BossImmunityV1
{
    public string? ModelBloonProperties { get; init; }
    public string? RuntimeTowerSet { get; init; }
    public bool? ModelInvulnerable { get; init; }
    public bool? RuntimeInvulnerable { get; init; }
    public bool? Untargetable { get; init; }
}

internal sealed class BossPositionV1
{
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public float? Progress { get; init; }
    public float? DistanceTraveled { get; init; }
}

internal sealed class BossSkullRuntimeV1
{
    public int? Current { get; init; }
    public int? DamageUntilNext { get; init; }
    public List<float> Thresholds { get; init; } = [];
    public float? HealthPercent { get; init; }
}

internal sealed class BossSpecialStateV1
{
    public bool? LychEthereal { get; init; }
    public bool? PhayzeCamoImmunityPhase { get; init; }
    public bool? InvulnerabilityOverride { get; init; }
    public string Coverage { get; init; } = "unknown";
}

internal sealed class BossEncounterV1
{
    public int? CurrentTier { get; init; }
    public bool? IsElite { get; init; }
    public int? NextSpawnRound { get; init; }
    public int? DefeatByRound { get; init; }
    public string Status { get; init; } = "unavailable";
}

internal sealed class BossSummaryV1
{
    public string Id { get; init; } = "";
    public string BloonId { get; init; } = "";
    public string BaseId { get; init; } = "";
    public string? BossType { get; init; }
    public bool IsBoss { get; init; }
    public bool IsBossSegment { get; init; }
    public bool? IsElite { get; init; }
    public int? Tier { get; init; }
    public int? Health { get; init; }
    public int? Armour { get; init; }
    public string? ModelBloonProperties { get; init; }
    public string? RuntimeTowerSetImmunity { get; init; }
    public bool? Invulnerable { get; init; }
    public bool? Untargetable { get; init; }
    public int? CurrentSkull { get; init; }
    public int? DamageUntilNextSkull { get; init; }
    public float? Progress { get; init; }
    public BossCoverageV1 Coverage { get; init; } = new();
}
