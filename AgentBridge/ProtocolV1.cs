using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgentBridge;

internal sealed class BridgeRequestV1
{
    public int ProtocolVersion { get; init; }
    public string RequestId { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime IssuedAtUtc { get; init; }
    public DateTime DeadlineAtUtc { get; init; }
    public JsonElement Payload { get; init; }
}

internal sealed class BridgeResultV1
{
    public int ProtocolVersion { get; init; } = AgentBridgeMod.ProtocolVersion;
    public string RequestId { get; init; } = "";
    public bool Ok { get; init; }
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
    public object? Result { get; init; }
    public BridgeErrorV1? Error { get; init; }
}

internal sealed class BridgeErrorV1
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public bool Retryable { get; init; }
    public object? Details { get; init; }
}

internal sealed class BridgeStatusV1
{
    private static readonly string LoadedModuleVersionId = typeof(BridgeStatusV1).Module.ModuleVersionId.ToString("D");
    public int ProtocolVersion { get; init; } = AgentBridgeMod.ProtocolVersion;
    public string BridgeVersion { get; init; } = ModHelperData.Version;
    public string ModuleVersionId { get; init; } = LoadedModuleVersionId;
    public string Btd6Version { get; init; } = "unknown";
    public bool GameAvailable { get; init; }
    public bool ActiveGame { get; init; }
    public bool OnMainMenu { get; init; }
    public string? SelectedHero { get; init; }
    public bool AutoCollectDrops { get; init; } = true;
    public UiStateV1 Ui { get; init; } = new();
    public BridgeCapabilitiesV1 Capabilities { get; init; } = new();
}

internal sealed class BridgeCapabilitiesV1
{
    public bool Status { get; init; } = true;
    public bool Observation { get; init; } = true;
    public bool PlayerActions { get; init; } = true;
    public bool ResearchControls { get; init; } = true;
    public bool RoundProgress { get; init; } = true;
    public bool Performance { get; init; } = true;
    public bool ScheduledUpgrades { get; init; } = true;
    public bool GeraldoShop { get; init; } = true;
    public bool CorvusSpells { get; init; } = true;
    public bool RoundInsights { get; init; } = true;
    public bool CheckpointTransfer { get; init; } = true;
}

internal sealed class Position2DV1
{
    public float X { get; init; }
    public float Y { get; init; }
}

internal sealed class NextUpgradeInfoV1
{
    public int Path { get; init; }
    public string? Name { get; init; }
    public float? Cost { get; init; }
    public bool Available { get; init; }
}

internal sealed class SupportRecipientV1
{
    public string TowerId { get; init; } = "";
    public string TowerType { get; init; } = "";
    public Position2DV1 Position { get; init; } = new();
    public float Distance { get; init; }
    public float RangeMargin { get; init; }
}

internal sealed class SupportCoverageV1
{
    public string TowerId { get; init; } = "";
    public string TowerType { get; init; } = "";
    public float SupportRange { get; init; }
    public string CoverageBasis { get; init; } = "model_range_center_distance";
    public bool EligibilityVerified { get; init; }
    public List<SupportRecipientV1> Recipients { get; init; } = [];
    public string? StrategicWarning { get; init; }
}

internal sealed class TowerInfoV1
{
    public string Id { get; init; } = "";
    public string TowerType { get; init; } = "";
    public string Name { get; init; } = "";
    public Position2DV1 Position { get; init; } = new();
    public float Range { get; init; }
    public string TargetPriority { get; init; } = "First";
    public int[] Tiers { get; init; } = [0, 0, 0];
    public float?[] UpgradeCosts { get; init; } = [null, null, null];
    public NextUpgradeInfoV1[] NextUpgrades { get; init; } = [];
    public int? CrosspathSlotsRemaining { get; init; }
    public float SellValue { get; init; }
    public long DamageDealt { get; init; }
    public long Pops { get; init; }
    public float CashEarned { get; init; }
    public BankInfoV1? Bank { get; init; }
    public bool IsHero { get; init; }
    public bool? IsSubmerged { get; init; }
    public Position2DV1? TargetPosition { get; init; }
}

internal sealed class BankInfoV1
{
    public float Cash { get; init; }
    public float Capacity { get; init; }
    public float Interest { get; init; }
    public bool IsFull { get; init; }
}

internal sealed class HeroInfoV1
{
    public string HeroId { get; init; } = "";
    public int Level { get; init; }
    public float Xp { get; init; }
    public float XpToNextLevel { get; init; }
    public float CostToLevelUp { get; init; }
}

internal sealed class AbilityInfoV1
{
    public string AbilityId { get; init; } = "";
    public string TowerId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsReady { get; init; }
    public bool CanUse { get; init; }
    public float CooldownRemaining { get; init; }
    public float CooldownTotal { get; init; }
    public AbilityTargetingV1 Targeting { get; init; } = new();
}

internal sealed class ScheduledAbilityInfoV1
{
    public string ScheduleId { get; init; } = "";
    public string? AbilityId { get; init; }
    public int AbilityIndex { get; init; }
    public AbilityTargetV1? Target { get; init; }
    public int TargetRound { get; init; }
    public float DelaySeconds { get; init; }
    public bool AutoRetry { get; init; } = true;
    public DateTime ScheduledAtUtc { get; init; }
    public bool Triggered { get; set; }
    public DateTime? TriggeredAtUtc { get; set; }
    public string? Error { get; set; }
    public ScheduledAbilityInfoV1 Snapshot() => (ScheduledAbilityInfoV1)MemberwiseClone();
}

internal sealed class ActiveThreatV1
{
    public string Type { get; init; } = "";
    public bool IsFortified { get; init; }
    public bool IsCamo { get; init; }
    public bool IsMoab { get; init; }
    public double? TrackProgress { get; init; }
    public int PathIndex { get; init; }
    public int Health { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
}

internal sealed class TrackLaneProgressV1
{
    public int PathIndex { get; set; }
    public double? MaxProgress { get; set; }
    public int BloonCount { get; set; }
    public int LeadCount { get; set; }
    public int CamoCount { get; set; }
    public int MoabCount { get; set; }
}

internal sealed class RoundIncomeBreakdownV1
{
    public int Round { get; init; }
    public double IncomeMultiplier { get; init; }
    public double? PopCash { get; init; }
    public double? RoundRewards { get; init; }
    public double TowerIncomeEstimate { get; init; }
    public double? BaselineIncome { get; init; }
    public double? EstimatedCashAtEnd { get; init; }
}

internal sealed class BaselineIncomeV1
{
    public string Confidence { get; init; } = "unsupported";
    public double? PopCash { get; init; }
    public double? RoundRewards { get; init; }
    public double? Total { get; init; }
    public double? CashAtStartOfTargetRound { get; init; }
    public double? CashAtEndOfTargetRound { get; init; }
}

internal sealed class TowerIncomeEstimateV1
{
    public string TowerId { get; init; } = "";
    public string TowerType { get; init; } = "";
    public string Kind { get; init; } = "";
    public double? PerRoundEstimate { get; init; }
    public bool Included { get; init; }
    public string? Reason { get; init; }
}

internal sealed class TowerIncomeProjectionV1
{
    public string Confidence { get; init; } = "estimated";
    public double EstimatedTotal { get; init; }
    public List<string> Assumptions { get; init; } = [];
    public List<TowerIncomeEstimateV1> Towers { get; init; } = [];
}

internal sealed class IncomeThresholdV1
{
    public int LastRound { get; init; }
    public double Multiplier { get; init; }
}

internal sealed class ProjectedCashResultV1
{
    public int FromRound { get; init; }
    public int TargetRound { get; init; }
    public double StartingCash { get; init; }
    public string Confidence { get; init; } = "unsupported";
    public List<string> Assumptions { get; init; } = [];
    public List<string> UnsupportedReasons { get; init; } = [];
    public BaselineIncomeV1 BaselineIncome { get; init; } = new();
    public TowerIncomeProjectionV1 TowerIncome { get; init; } = new();
    public double? EstimatedCashAtStartOfTargetRound { get; init; }
    public double? EstimatedCashAtEndOfTargetRound { get; init; }
    public List<RoundIncomeBreakdownV1> Breakdown { get; init; } = [];
    public List<IncomeThresholdV1> IncomeThresholds { get; init; } = [];
    public double FinalIncomeMultiplier { get; init; }
}

internal sealed class BridgeObservationV1
{
    public DateTime ObservedAtUtc { get; init; }
    public bool ActiveGame { get; init; }
    public UiStateV1 Ui { get; init; } = new();
    public string? GameStatus { get; init; }
    public bool? IsPaused { get; init; }
    public int? Round { get; init; }
    public int? EndRound { get; init; }
    public bool? RoundActive { get; init; }
    public float? RoundElapsedSeconds { get; init; }
    public bool? InBetweenRounds { get; init; }
    public bool? CanStartRound { get; init; }
    public bool? FastForward { get; init; }
    public bool? AutoPlay { get; init; }
    public double? Cash { get; init; }
    public double? Lives { get; init; }
    public double? MaxLives { get; init; }
    public int TowerCount { get; init; }
    public string? MapId { get; init; }
    public string? MapName { get; init; }
    public string? Mode { get; init; }
    public string? Difficulty { get; init; }
    public List<TowerInfoV1> Towers { get; init; } = [];
    public List<BossSummaryV1> Bosses { get; init; } = [];
    public HeroInfoV1? Hero { get; init; }
    public List<AbilityInfoV1> Abilities { get; init; } = [];
    public List<ScheduledAbilityInfoV1> ScheduledAbilities { get; init; } = [];
    public List<ScheduledUpgradeInfoV1> ScheduledUpgrades { get; init; } = [];
    public List<ScheduledGeraldoPurchaseInfoV1> ScheduledGeraldoPurchases { get; init; } = [];
    public List<ScheduledCorvusActionInfoV1> ScheduledCorvusActions { get; init; } = [];
    public RoundInsightsV1? RoundInsights { get; init; }
    public RoundInsightsV1? RoundReport { get; init; }
    public string? ThreatSnapshotSource { get; init; }
    public int[] AvailableCheckpoints { get; init; } = [];
    public double? MaxTrackProgress { get; init; }
    public int? ActiveBloonCount { get; init; }
    public int? ActiveMoabCount { get; init; }
    public List<TrackLaneProgressV1> TrackProgressByLane { get; init; } = [];
    public List<ActiveThreatV1> ActiveThreats { get; init; } = [];
}

internal sealed class BridgeStateV1
{
    public BridgeStatusV1 Status { get; init; } = new();
    public BridgeProgressV1 Progress { get; init; } = new();
}

internal sealed class BridgeProgressV1
{
    public DateTime ObservedAtUtc { get; init; }
    public string? MatchId { get; init; }
    public bool ActiveGame { get; init; }
    public UiStateV1 Ui { get; init; } = new();
    public string? GameStatus { get; init; }
    public int? Round { get; init; }
    public bool? RoundActive { get; init; }
    public bool? CanStartRound { get; init; }
    public double? Cash { get; init; }
    public double? Lives { get; init; }
}
internal sealed class CorvusSpellInfoV1
{
    public string SpellId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "cast";
    public int UnlockLevel { get; init; }
    public bool Unlocked { get; init; }
    public int InitialManaCost { get; init; }
    public int? OngoingManaCost { get; init; }
    public float? ManaDrainIntervalSeconds { get; init; }
    public float? DurationSeconds { get; init; }
    public float? CooldownSeconds { get; init; }
    public bool Active { get; init; }
    public bool CanCast { get; init; }
    public float CooldownPercent { get; init; }
    public float ActiveDurationRemainingPercent { get; init; }
    public string? UnavailableReason { get; init; }
}

internal sealed class InspectCorvusResultV1
{
    public string Source { get; init; } = "active-simulation";
    public string HeroTowerId { get; init; } = "";
    public int HeroLevel { get; init; }
    public int Mana { get; init; }
    public int MaxMana { get; init; }
    public bool IsRecovering { get; init; }
    public bool IsStunned { get; init; }
    public bool IsManaDraining { get; init; }
    public List<CorvusSpellInfoV1> Spells { get; init; } = [];
}

internal sealed class CorvusFailureV1
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class CorvusActionResultV1
{
    public string Status { get; init; } = "pending";
    public bool Executed { get; init; }
    public bool Changed { get; init; }
    public string? ScheduleId { get; init; }
    public string? HeroTowerId { get; init; }
    public string SpellId { get; init; } = "";
    public bool? Enabled { get; init; }
    public int? ManaBefore { get; init; }
    public int? ManaAfter { get; init; }
    public CorvusSpellInfoV1? Spell { get; init; }
    public CorvusFailureV1? Failure { get; init; }
    public string? WaitingFor { get; init; }
    public int? ExecutionRound { get; init; }
    public float? ExecutionSeconds { get; init; }
}

internal sealed class ScheduledCorvusActionInfoV1
{
    public string ScheduleId { get; init; } = "";
    public string HeroTowerId { get; init; } = "";
    public string SpellId { get; init; } = "";
    public bool? Enabled { get; init; }
    public bool WhenReady { get; init; }
    public int? TargetRound { get; init; }
    public float? DelaySeconds { get; init; }
    public string Status { get; init; } = "pending";
    public string? WaitingFor { get; init; }
    public CorvusFailureV1? Failure { get; init; }
    public CorvusActionResultV1? Result { get; init; }
}

internal sealed class CancelScheduledCorvusActionResultV1
{
    public bool Cancelled { get; init; }
    public int Count { get; init; }
}

