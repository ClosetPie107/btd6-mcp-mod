using System;
using System.Linq;
using System.Collections.Generic;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2Cpp;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Simulation.Bloons;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

/// <summary>
/// Round-local telemetry.  This intentionally keeps native objects out of the retained
/// state: native objects are read on the game thread and only scalar/string copies leave
/// the sampling path.
/// </summary>
public sealed partial class AgentBridgeMod
{
    private const int RoundTelemetryMaxCompositionKeys = 64;
    private const int RoundTelemetryMaxComposition = 20;
    private const int RoundTelemetryMaxThreats = 5;
    private const int RoundTelemetryMaxLeaks = 15;
    private const int RoundTelemetryMaxActions = 32;
    private const int RoundTelemetryMaxLanes = 64;
    private const int RoundTelemetrySampleFrames = 6; // 0.1 native simulation seconds.

    private static TelemetryRound? telemetryRound;
    private static RoundInsightsV1? lastRoundReport;
    private static long telemetryGeneration = long.MinValue;

    private static readonly Dictionary<IntPtr, int> telemetryPathIndices = new();

    private sealed class TelemetryRound
    {
        public int Round;
        public string Status = "active";
        public bool Frozen;
        public bool SawActiveRound;
        public bool LastActiveCountsKnown;
        public int LastActiveBloonCount;
        public int LastActiveMoabCount;
        public double? LastSampleNativeSeconds;
        public DateTime StartedAtUtc;
        public DateTime LastObservedAtUtc;

        public int Samples;
        public int MissingSamples;
        public int LastNativeTick = -1;
        public double? FirstRoundElapsedSeconds;
        public double? LastRoundElapsedSeconds;
        public double? LastSpawnDurationSeconds;
        public bool ScheduleKnown;
        public bool ScheduleComplete;
        public int ScheduleGroupCount;
        public int OmittedScheduleGroups;
        public readonly ScheduleGroupWork[] ScheduleGroups = new ScheduleGroupWork[128];
        public int? EstimatedRemainingScheduledSpawns;
        public bool? SpawningComplete;
        public bool NativeClockEverKnown;
        public bool PathGeometryEverKnown;
        public bool NativeProgressEverKnown;

        public bool HasProgress;
        public double MaxTrackProgress;

        public int PeakBloonCount;
        public int PeakMoabCount;
        public int PeakNearExitCount;
        public double NearExitSampledDurationSeconds;
        public int PostSpawnSamples;
        public double PostSpawnObservedSeconds;
        public double PostSpawnMoabPresentSeconds;
        public double PostSpawnCeramicPresentSeconds;
        public int PostSpawnPeakMoabCount;
        public int PostSpawnPeakCeramicCount;
        public int PostSpawnPeakNearExitMoabCount;
        public int PostSpawnPeakNearExitCeramicCount;
        public bool PostSpawnHasMoabProgress;
        public double PostSpawnMaxMoabProgress;
        public bool PostSpawnHasCeramicProgress;
        public double PostSpawnMaxCeramicProgress;
        public double WorstScore = double.MinValue;
        public RoundWorstMomentWork WorstMoment;

        public int LaneCount;
        public readonly LaneWork[] Lanes = CreateLanes();
        public readonly CompositionWork[] Compositions = CreateCompositions();
        public int CompositionCount;
        public int OmittedCompositionKeys;
        public readonly ThreatWork[] CurrentThreats = new ThreatWork[RoundTelemetryMaxThreats];
        public int CurrentThreatCount;
        public readonly ThreatWork[] WorstThreats = new ThreatWork[RoundTelemetryMaxThreats];
        public int WorstThreatCount;

        public int LeakHead;
        public int LeakCount;
        public int LeakTotal;
        public int LeakOmitted;
        public readonly LeakWork[] Leaks = new LeakWork[RoundTelemetryMaxLeaks];
        public readonly Dictionary<IntPtr, int> PendingLeaks = new();

        public TelemetryRound()
        {
            for (int i = 0; i < Leaks.Length; i++) Leaks[i] = new LeakWork();
            for (int i = 0; i < Actions.Length; i++) Actions[i] = new ActionWork();
        }

        public int ActionCount;
        public readonly ActionWork[] Actions = new ActionWork[RoundTelemetryMaxActions];

        private static LaneWork[] CreateLanes()
        {
            var result = new LaneWork[RoundTelemetryMaxLanes];
            for (int i = 0; i < result.Length; i++) result[i] = new LaneWork();
            return result;
        }

        private static CompositionWork[] CreateCompositions()
        {
            var result = new CompositionWork[RoundTelemetryMaxCompositionKeys];
            for (int i = 0; i < result.Length; i++) result[i] = new CompositionWork();
            return result;
        }
    }

    private sealed class LaneWork
    {
        public int PathIndex;
        public int CurrentBloonCount;
        public int CurrentMoabCount;
        public int CurrentNearExitCount;
        public int PeakBloonCount;
        public int PeakMoabCount;
        public int PeakNearExitCount;
        public double MaxProgress;
        public bool HasProgress;
        public double NearExitSeconds;
        public int Samples;

        public void Reset(int pathIndex)
        {
            PathIndex = pathIndex;
            CurrentBloonCount = 0;
            CurrentMoabCount = 0;
            CurrentNearExitCount = 0;
            PeakBloonCount = 0;
            PeakMoabCount = 0;
            PeakNearExitCount = 0;
            MaxProgress = 0;
            HasProgress = false;
            NearExitSeconds = 0;
            Samples = 0;
        }

        public void BeginSample()
        {
            CurrentBloonCount = 0;
            CurrentMoabCount = 0;
            CurrentNearExitCount = 0;
        }
    }

    private sealed class CompositionWork
    {
        public string BaseId = "";
        public bool Camo;
        public bool Fortified;
        public bool Regrow;
        public bool Lead;
        public bool Moab;
        public int CurrentCount;
        public int PeakCount;
        public int Samples;

        public void Reset(string baseId, bool camo, bool fortified, bool regrow, bool lead, bool moab)
        {
            BaseId = baseId;
            Camo = camo;
            Fortified = fortified;
            Regrow = regrow;
            Lead = lead;
            Moab = moab;
            CurrentCount = 0;
            PeakCount = 0;
            Samples = 0;
        }
    }

    private struct ThreatWork
    {
        public string BaseId;
        public bool Camo;
        public bool Fortified;
        public bool Regrow;
        public bool Lead;
        public bool Moab;
        public bool HasProgress;
        public double Progress;
        public int PathIndex;
        public int? Health;
        public float? X;
        public float? Y;
        public double Score;
    }
    private struct RoundWorstMomentWork
    {
        public double? ElapsedSeconds;
        public bool HasProgress;
        public double MaxProgress;
        public int BloonCount;
        public int MoabCount;
        public int NearExitCount;
        public int LeadCount;
        public int CamoCount;
        public int? PathIndex;
    }
    private struct ScheduleGroupWork
    {
        public int Count;
        public float StartFrames;
        public float EndFrames;
    }


    private sealed class LeakWork
    {
        public IntPtr Pointer;
        public string BaseId = "";
        public bool Camo;
        public bool Fortified;
        public bool Regrow;
        public bool Lead;
        public bool Moab;
        public int? PathIndex;
        public double? TrackProgress;
        public float? X;
        public float? Y;
        public double? ExpectedDamage;
        public double? HealthBefore;
        public double? ActualHealthLoss;
        public double? ElapsedSeconds;
        public bool Posted;

        public void Clear()
        {
            Pointer = IntPtr.Zero;
            BaseId = "";
            Camo = false;
            Fortified = false;
            Regrow = false;
            Lead = false;
            Moab = false;
            PathIndex = null;
            TrackProgress = null;
            X = null;
            Y = null;
            ExpectedDamage = null;
            HealthBefore = null;
            ActualHealthLoss = null;
            ElapsedSeconds = null;
            Posted = false;
        }
    }

    private sealed class ActionWork
    {
        public string ScheduleId = "";
        public string ActionKind = "";
        public int TargetRound;
        public string Outcome = "";
        public string? Error;
        public double? ElapsedSeconds;

        public void Set(string scheduleId, string actionKind, int targetRound, string outcome, string? error, double? elapsedSeconds)
        {
            ScheduleId = scheduleId;
            ActionKind = actionKind;
            TargetRound = targetRound;
            Outcome = outcome;
            Error = error;
            ElapsedSeconds = elapsedSeconds;
        }
    }

    private static void ResetRoundTelemetry()
    {
        telemetryRound = null;
        lastRoundReport = null;
        telemetryPathIndices.Clear();
        telemetryGeneration = matchGeneration;
    }

    /// <summary>
    /// Called from AgentBridgeMod.OnUpdate before UpdateSimulationTiming.  All native reads
    /// happen synchronously on the Unity/game thread.  The native simulation tick, rather
    /// than wall time or frame count, controls the 0.1-second sampling cadence.
    /// </summary>
    private static void ProcessRoundTelemetry()
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame()) return;

        var bridge = inGame.bridge;
        if (bridge == null || bridge.Simulation == null) return;

        if (telemetryGeneration != matchGeneration)
            ResetRoundTelemetry();

        int round;
        bool roundsActive;
        try
        {
            round = bridge.GetCurrentRound() + 1;
            roundsActive = bridge.AreRoundsActive();
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"Round telemetry could not read round identity: {ex.Message}");
            return;
        }

        if (round < 1) return;

        if (telemetryRound == null)
        {
            // A match can be observed in the between-round state immediately after a
            // restore or before round one starts.  Do not manufacture a completed report.
            if (!roundsActive) return;
            BeginTelemetryRound(bridge, round);
        }
        else if (telemetryRound.Round != round)
        {
            if (!telemetryRound.Frozen && telemetryRound.SawActiveRound)
            {
                if (!roundsActive && TryReadNativeClock(bridge, telemetryRound.Round, out int finalTick, out var finalElapsed, out bool finalClock))
                    CaptureRoundSample(bridge, telemetryRound, finalTick, finalElapsed, finalClock, true);
                else
                {
                    telemetryRound.LastActiveCountsKnown = false;
                    telemetryRound.MissingSamples++;
                }
                FreezeTelemetryRound(IsDefeat(inGame) ? "defeat" : IsVictory(inGame) ? "victory" : "completed");
            }
            if (!roundsActive) return;
            BeginTelemetryRound(bridge, round);
        }

        var current = telemetryRound;
        if (current == null || current.Frozen) return;
        current.SawActiveRound |= roundsActive;

        if (!TryReadNativeClock(bridge, round, out int nativeTick, out var roundElapsedSeconds, out bool clockKnown))
        {
            current.MissingSamples++;
            return;
        }

        current.NativeClockEverKnown |= clockKnown;
        if (roundsActive)
        {
            CaptureRoundSample(bridge, current, nativeTick, roundElapsedSeconds, clockKnown, false);
            return;
        }

        // The final inactive tick is important: successful rounds commonly expose an
        // empty active-bloon collection here, whereas defeat is frozen by the Lose prefix.
        if (current.SawActiveRound)
        {
            CaptureRoundSample(bridge, current, nativeTick, roundElapsedSeconds, clockKnown, true);
            FreezeTelemetryRound(IsDefeat(inGame) ? "defeat" : IsVictory(inGame) ? "victory" : "completed");
        }
    }

    private static void BeginTelemetryRound(UnityToSimulation bridge, int round)
    {
        telemetryRound = new TelemetryRound
        {
            Round = round,
            StartedAtUtc = DateTime.UtcNow,
            LastObservedAtUtc = DateTime.UtcNow
        };
        PopulateRoundSchedule(bridge, telemetryRound);
    }

    private static bool IsDefeat(InGame inGame)
    {
        try { return inGame.MatchLost; } catch { return false; }
    }

    private static bool IsVictory(InGame inGame)
    {
        try { return inGame.WaitingForVictoryScreen; } catch { return false; }
    }

    private static bool TryReadNativeClock(UnityToSimulation bridge, int round, out int nativeTick, out double? roundElapsedSeconds, out bool clockKnown)
    {
        nativeTick = -1;
        roundElapsedSeconds = null;
        clockKnown = false;
        try
        {
            nativeTick = bridge.ElapsedTime;
            int? startTick = null;

            // Main's clock is already native and may have been established on a previous
            // frame.  Reading the spawner directly also works on the first frame, before
            // UpdateSimulationTiming has populated currentRoundStartTick.
            if (clockRound == round && currentRoundStartTick.HasValue)
                startTick = currentRoundStartTick.Value;

            var spawner = bridge.Simulation?.Map?.spawner;
            if (spawner != null && spawner.roundData.TryGetValue(round - 1, out var nativeRound) && nativeRound != null)
                startTick = nativeRound.roundStartTime;

            if (startTick.HasValue && startTick.Value <= nativeTick)
            {
                roundElapsedSeconds = Math.Max(0d, (nativeTick - startTick.Value) / 60d);
                clockKnown = true;
            }
            return nativeTick >= 0;
        }
        catch
        {
            nativeTick = -1;
            return false;
        }
    }

    private static void PopulateRoundSchedule(UnityToSimulation bridge, TelemetryRound round)
    {
        try
        {
            var model = InGame.instance?.GetGameModel();
            var rounds = model?.roundSet?.rounds;
            if (rounds == null || round.Round < 1 || round.Round > rounds.Length) return;
            var nativeRound = rounds[round.Round - 1];
            if (nativeRound?.groups == null) return;

            float lastEnd = 0f;
            int groupIndex = 0;
            foreach (var group in nativeRound.groups)
            {
                if (group == null) continue;
                if (group.end > lastEnd) lastEnd = group.end;
                if (groupIndex < round.ScheduleGroups.Length)
                {
                    round.ScheduleGroups[groupIndex++] = new ScheduleGroupWork
                    {
                        Count = Math.Max(0, group.count),
                        StartFrames = Math.Max(0f, group.start),
                        EndFrames = Math.Max(0f, group.end)
                    };
                }
                else round.OmittedScheduleGroups++;
            }
            round.ScheduleGroupCount = groupIndex;
            round.LastSpawnDurationSeconds = Math.Round(lastEnd / 60d, 3);
            round.ScheduleKnown = true;
            round.ScheduleComplete = round.OmittedScheduleGroups == 0;
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"Round telemetry could not read native spawn schedule: {ex.Message}");
        }
    }

    private static void UpdateScheduleEstimate(TelemetryRound round, double? elapsedSeconds)
    {
        if (!round.ScheduleKnown || !round.ScheduleComplete || !elapsedSeconds.HasValue)
        {
            round.EstimatedRemainingScheduledSpawns = null;
            round.SpawningComplete = null;
            return;
        }

        double elapsedFrames = Math.Max(0d, elapsedSeconds.Value) * 60d;
        bool complete = true;
        int remaining = 0;
        for (int i = 0; i < round.ScheduleGroupCount; i++)
        {
            var group = round.ScheduleGroups[i];
            if (group.EndFrames > elapsedFrames) complete = false;
            // Approximate an in-progress group as uniformly distributed over its window.
            // This estimates future scheduled emissions, not live bloons or exact native spawns.
            if (group.StartFrames > elapsedFrames)
                remaining = SaturatingAdd(remaining, group.Count);
            else if (group.EndFrames > elapsedFrames && group.EndFrames > group.StartFrames)
                remaining = SaturatingAdd(remaining, (int)Math.Ceiling(group.Count *
                    Math.Clamp((group.EndFrames - elapsedFrames) / (group.EndFrames - group.StartFrames), 0d, 1d)));
        }
        round.EstimatedRemainingScheduledSpawns = remaining;
        round.SpawningComplete = complete;
    }

    private static int SaturatingAdd(int left, int right)
    {
        if (right <= 0 || left == int.MaxValue) return left;
        return left > int.MaxValue - right ? int.MaxValue : left + right;
    }

    private static void CaptureRoundSample(UnityToSimulation bridge, TelemetryRound round, int nativeTick, double? roundElapsedSeconds, bool clockKnown, bool force)
    {
        if (nativeTick < 0 || (!force && round.LastNativeTick >= 0 && nativeTick - round.LastNativeTick < RoundTelemetrySampleFrames))
            return;

        if (!force && round.LastNativeTick == nativeTick && round.Samples > 0)
            return;

        round.LastNativeTick = nativeTick;
        round.LastObservedAtUtc = DateTime.UtcNow;
        BeginSample(round);
        UpdateScheduleEstimate(round, roundElapsedSeconds);

        bool extractionComplete = false;
        int totalBloons = 0;
        int totalMoabs = 0;
        int totalCeramics = 0;
        int totalNearExit = 0;
        int totalNearExitMoabs = 0;
        int totalNearExitCeramics = 0;
        int totalLead = 0;
        int totalCamo = 0;
        double globalMaxProgress = 0;
        double maxMoabProgress = 0;
        double maxCeramicProgress = 0;
        bool globalHasProgress = false;
        bool hasMoabProgress = false;
        bool hasCeramicProgress = false;
        try
        {
            BuildPathIndexMap(bridge);
            var bloons = bridge.GetAllBloons();
            if (bloons == null)
            {
                round.MissingSamples++;
                return;
            }

            var bloonEnum = bloons.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
            while (bloonEnum.MoveNext())
            {
                var bloon = bloonEnum.Current?.TryCast<BloonToSimulation>();
                if (bloon == null || bloon.Def == null) continue;
                if (bloon.Pointer != IntPtr.Zero && round.PendingLeaks.ContainsKey(bloon.Pointer)) continue;

                var def = bloon.Def;
                string baseId = def.baseId ?? def.name ?? "Unknown";
                bool isLead = (def.bloonProperties & BloonProperties.Lead) != 0;
                bool isMoab = def.isMoab;
                bool isCeramic = baseId.Contains("Ceramic", StringComparison.OrdinalIgnoreCase);
                bool isCamo = def.isCamo;
                bool isFortified = def.isFortified;
                bool isRegrow = def.isGrow;
                totalBloons++;
                if (isMoab) totalMoabs++;
                if (isCeramic) totalCeramics++;
                if (isLead) totalLead++;
                if (isCamo) totalCamo++;

                int pathIndex = -1;
                double progress = 0;
                bool hasProgress = false;
                int? health = null;
                float? x = null;
                float? y = null;
                try
                {
                    var simBloon = bloon.GetSimBloon();
                    if (simBloon != null)
                    {
                        health = bloon.GetHealth();
                        var path = simBloon.path;
                        if (NativeTrackProgress.TryGetProgress(simBloon, out progress))
                            hasProgress = true;
                        if (path != null && telemetryPathIndices.TryGetValue(path.Pointer, out int mappedPath))
                            pathIndex = mappedPath;

                        var position = simBloon.Position.data;
                        x = position.x;
                        y = position.y;
                    }
                }
                catch
                {
                    // The bloon remains a valid count/composition observation; location,
                    // health, and progress stay explicitly unavailable for this sample.
                }
                bool nearExit = hasProgress && progress > NativeTrackProgress.NearExitThreshold;
                if (nearExit)
                {
                    totalNearExit++;
                    if (isMoab) totalNearExitMoabs++;
                    if (isCeramic) totalNearExitCeramics++;
                }
                if (hasProgress && (!globalHasProgress || progress > globalMaxProgress))
                {
                    globalMaxProgress = progress;
                    globalHasProgress = true;
                }
                if (isMoab && hasProgress && (!hasMoabProgress || progress > maxMoabProgress))
                {
                    maxMoabProgress = progress;
                    hasMoabProgress = true;
                }
                if (isCeramic && hasProgress && (!hasCeramicProgress || progress > maxCeramicProgress))
                {
                    maxCeramicProgress = progress;
                    hasCeramicProgress = true;
                }

                var lane = GetLane(round, pathIndex);
                lane.CurrentBloonCount++;
                if (isMoab) lane.CurrentMoabCount++;
                if (nearExit) lane.CurrentNearExitCount++;
                if (hasProgress && (!lane.HasProgress || progress > lane.MaxProgress))
                {
                    lane.MaxProgress = progress;
                    lane.HasProgress = true;
                }

                var composition = GetComposition(round, baseId, isCamo, isFortified, isRegrow, isLead, isMoab);
                if (composition != null) composition.CurrentCount++;

                double score = (nearExit ? 10000d : 0d) + (isMoab ? 1000d : 0d) + progress * 100d;
                AddThreat(round.CurrentThreats, ref round.CurrentThreatCount, new ThreatWork
                {
                    BaseId = baseId,
                    Camo = isCamo,
                    Fortified = isFortified,
                    Regrow = isRegrow,
                    Lead = isLead,
                    Moab = isMoab,
                    HasProgress = hasProgress,
                    Progress = progress,
                    PathIndex = pathIndex,
                    Health = health,
                    X = x,
                    Y = y,
                    Score = score
                });

            }
            extractionComplete = true;

        }
        catch (Exception ex)
        {
            round.MissingSamples++;
            ModHelper.Warning<AgentBridgeMod>($"Round telemetry sample failed: {ex.Message}");
        }

        if (!extractionComplete) return;

        UpdateSampleAggregates(round, nativeTick, totalBloons, totalMoabs, totalCeramics,
            totalNearExit, totalNearExitMoabs, totalNearExitCeramics, totalLead, totalCamo,
            globalMaxProgress, globalHasProgress, maxMoabProgress, hasMoabProgress,
            maxCeramicProgress, hasCeramicProgress, roundElapsedSeconds, clockKnown);
    }

    private static void BuildPathIndexMap(UnityToSimulation bridge)
    {
        telemetryPathIndices.Clear();
        var paths = bridge.Simulation?.Map?.pathManager?.paths;
        if (paths == null) return;
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            if (path != null) telemetryPathIndices[path.Pointer] = i;
        }
        if (telemetryPathIndices.Count > 0 && telemetryRound != null)
            telemetryRound.PathGeometryEverKnown = true;
    }

    private static void BeginSample(TelemetryRound round)
    {
        round.CurrentThreatCount = 0;
        round.LastActiveCountsKnown = false;
        for (int i = 0; i < round.LaneCount; i++) round.Lanes[i].BeginSample();
        for (int i = 0; i < round.CompositionCount; i++) round.Compositions[i].CurrentCount = 0;
    }

    private static LaneWork GetLane(TelemetryRound round, int pathIndex)
    {
        for (int i = 0; i < round.LaneCount; i++)
            if (round.Lanes[i].PathIndex == pathIndex) return round.Lanes[i];

        if (round.LaneCount < round.Lanes.Length)
        {
            var lane = round.Lanes[round.LaneCount++];
            lane.Reset(pathIndex);
            return lane;
        }

        // A path index beyond the bounded lane table is folded into the final unknown
        // lane; counts remain recoverable without unbounded allocations.
        return round.Lanes[^1];
    }

    private static CompositionWork? GetComposition(TelemetryRound round, string baseId, bool camo, bool fortified, bool regrow, bool lead, bool moab)
    {
        for (int i = 0; i < round.CompositionCount; i++)
        {
            var existing = round.Compositions[i];
            if (existing.BaseId == baseId && existing.Camo == camo && existing.Fortified == fortified &&
                existing.Regrow == regrow && existing.Lead == lead && existing.Moab == moab)
                return existing;
        }

        if (round.CompositionCount >= round.Compositions.Length)
        {
            round.OmittedCompositionKeys++;
            return null;
        }

        var result = round.Compositions[round.CompositionCount++];
        result.Reset(baseId, camo, fortified, regrow, lead, moab);
        return result;
    }

    private static void AddThreat(ThreatWork[] threats, ref int count, ThreatWork value)
    {
        int insert = 0;
        while (insert < count && value.Score <= threats[insert].Score) insert++;
        if (insert >= threats.Length) return;
        if (count < threats.Length) count++;
        for (int i = count - 1; i > insert; i--) threats[i] = threats[i - 1];
        threats[insert] = value;
    }

    private static void UpdateSampleAggregates(TelemetryRound round, int nativeTick,
        int totalBloons, int totalMoabs, int totalCeramics,
        int totalNearExit, int totalNearExitMoabs, int totalNearExitCeramics,
        int totalLead, int totalCamo, double globalMaxProgress, bool globalHasProgress,
        double maxMoabProgress, bool hasMoabProgress, double maxCeramicProgress,
        bool hasCeramicProgress, double? elapsedSeconds, bool clockKnown)
    {
        double? previousTime = round.LastSampleNativeSeconds;
        double sampleTime = nativeTick / 60d;
        double delta = previousTime.HasValue ? sampleTime - previousTime.Value : 0d;
        if (delta < 0) delta = 0;
        round.LastSampleNativeSeconds = sampleTime;

        round.Samples++;
        if (!round.FirstRoundElapsedSeconds.HasValue && elapsedSeconds.HasValue)
            round.FirstRoundElapsedSeconds = elapsedSeconds;
        if (elapsedSeconds.HasValue) round.LastRoundElapsedSeconds = elapsedSeconds;
        round.NativeClockEverKnown |= clockKnown;
        round.LastActiveCountsKnown = true;
        round.LastActiveBloonCount = totalBloons;
        round.LastActiveMoabCount = totalMoabs;

        if (totalBloons > round.PeakBloonCount) round.PeakBloonCount = totalBloons;
        if (totalMoabs > round.PeakMoabCount) round.PeakMoabCount = totalMoabs;
        if (totalNearExit > round.PeakNearExitCount) round.PeakNearExitCount = totalNearExit;
        round.NativeProgressEverKnown |= globalHasProgress;
        if (globalHasProgress && (globalMaxProgress > round.MaxTrackProgress || !round.HasProgress))
        {
            round.MaxTrackProgress = globalMaxProgress;
            round.HasProgress = true;
        }
        if (totalNearExit > 0) round.NearExitSampledDurationSeconds += delta;
        if (round.SpawningComplete == true)
        {
            round.PostSpawnSamples++;
            round.PostSpawnObservedSeconds += delta;
            if (totalMoabs > 0) round.PostSpawnMoabPresentSeconds += delta;
            if (totalCeramics > 0) round.PostSpawnCeramicPresentSeconds += delta;
            if (totalMoabs > round.PostSpawnPeakMoabCount) round.PostSpawnPeakMoabCount = totalMoabs;
            if (totalCeramics > round.PostSpawnPeakCeramicCount) round.PostSpawnPeakCeramicCount = totalCeramics;
            if (totalNearExitMoabs > round.PostSpawnPeakNearExitMoabCount)
                round.PostSpawnPeakNearExitMoabCount = totalNearExitMoabs;
            if (totalNearExitCeramics > round.PostSpawnPeakNearExitCeramicCount)
                round.PostSpawnPeakNearExitCeramicCount = totalNearExitCeramics;
            if (hasMoabProgress && (!round.PostSpawnHasMoabProgress || maxMoabProgress > round.PostSpawnMaxMoabProgress))
            {
                round.PostSpawnMaxMoabProgress = maxMoabProgress;
                round.PostSpawnHasMoabProgress = true;
            }
            if (hasCeramicProgress && (!round.PostSpawnHasCeramicProgress || maxCeramicProgress > round.PostSpawnMaxCeramicProgress))
            {
                round.PostSpawnMaxCeramicProgress = maxCeramicProgress;
                round.PostSpawnHasCeramicProgress = true;
            }
        }

        for (int i = 0; i < round.LaneCount; i++)
        {
            var lane = round.Lanes[i];
            lane.Samples++;
            if (lane.CurrentBloonCount > lane.PeakBloonCount) lane.PeakBloonCount = lane.CurrentBloonCount;
            if (lane.CurrentMoabCount > lane.PeakMoabCount) lane.PeakMoabCount = lane.CurrentMoabCount;
            if (lane.CurrentNearExitCount > lane.PeakNearExitCount) lane.PeakNearExitCount = lane.CurrentNearExitCount;
            if (lane.CurrentNearExitCount > 0) lane.NearExitSeconds += delta;
        }

        for (int i = 0; i < round.CompositionCount; i++)
        {
            var composition = round.Compositions[i];
            if (composition.CurrentCount > 0) composition.Samples++;
            if (composition.CurrentCount > composition.PeakCount) composition.PeakCount = composition.CurrentCount;
        }

        double pressureScore = totalBloons + totalMoabs * 2d + totalNearExit * 4d;
        if (pressureScore >= round.WorstScore)
        {
            round.WorstScore = pressureScore;
            round.WorstMoment = new RoundWorstMomentWork
            {
                ElapsedSeconds = elapsedSeconds,
                HasProgress = globalHasProgress,
                MaxProgress = globalMaxProgress,
                BloonCount = totalBloons,
                MoabCount = totalMoabs,
                NearExitCount = totalNearExit,
                LeadCount = totalLead,
                CamoCount = totalCamo,
                PathIndex = round.CurrentThreatCount > 0 ? round.CurrentThreats[0].PathIndex : null
            };
            round.WorstThreatCount = round.CurrentThreatCount;
            for (int i = 0; i < round.CurrentThreatCount; i++) round.WorstThreats[i] = round.CurrentThreats[i];
        }
    }


    private static void FreezeTelemetryRound(string status)
    {
        var current = telemetryRound;
        if (current == null || current.Frozen) return;
        current.Status = status;
        current.Frozen = true;
        lastRoundReport = BuildRoundSnapshot(current);
    }

    internal static void CaptureDefeatTelemetry(UnityToSimulation? simulation)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame() || inGame.bridge?.Simulation == null) return;
        if (simulation != null && inGame.bridge.Pointer != simulation.Pointer) return;
        if (telemetryGeneration != matchGeneration) ResetRoundTelemetry();
        int round;
        try { round = inGame.bridge.GetCurrentRound() + 1; } catch { return; }
        if (round < 1) return;
        bool roundsActive;
        try { roundsActive = inGame.bridge.AreRoundsActive(); } catch { return; }
        if (telemetryRound == null && !roundsActive) return;
        if (telemetryRound == null) BeginTelemetryRound(inGame.bridge, round);
        var current = telemetryRound;
        if (current == null || current.Frozen) return;
        current.SawActiveRound = true;

        if (TryReadNativeClock(inGame.bridge, round, out int nativeTick, out var elapsed, out bool known))
            CaptureRoundSample(inGame.bridge, current, nativeTick, elapsed, known, true);
        FreezeTelemetryRound("defeat");
    }

    /// <summary>
    /// Main calls this after a scheduled upgrade/ability has been attempted.  It is
    /// intentionally outcome-oriented; no native object or exception is retained.
    private static void AppendRoundActionOutcome(string scheduleId, string actionKind, int targetRound, string outcome, string? error = null, double? elapsedSeconds = null)
    {
        if (scheduleId == null || actionKind == null || outcome == null)
            return;

        var current = telemetryRound;
        if (current != null && current.Round == targetRound && current.ActionCount < current.Actions.Length)
        {
            var action = current.Actions[current.ActionCount++];
            action.Set(scheduleId.Length > 128 ? scheduleId[..128] : scheduleId,
                actionKind.Length > 64 ? actionKind[..64] : actionKind,
                targetRound,
                outcome.Length > 64 ? outcome[..64] : outcome,
                error == null ? null : error.Length > 256 ? error[..256] : error,
                elapsedSeconds ?? current.LastRoundElapsedSeconds);
            if (current.Frozen) lastRoundReport = BuildRoundSnapshot(current);
            return;
        }

        // RoundTelemetry runs before the scheduled-action processors.  At a native
        // round transition it may have already frozen the target round and started
        // the next one before an action notices that its target expired.  Keep that
        // terminal outcome on the frozen target report rather than silently dropping
        // it or attaching it to the new round.
        var frozen = lastRoundReport;
        if (frozen == null || frozen.Round != targetRound || frozen.Actions.Count >= RoundTelemetryMaxActions)
            return;
        string boundedScheduleId = scheduleId.Length > 128 ? scheduleId[..128] : scheduleId;
        if (frozen.Actions.Any(action => action.ScheduleId == boundedScheduleId))
            return;
        // Previously published reports may still be serializing on the mailbox
        // worker. Replace the snapshot and action list instead of mutating them.
        var actions = new List<RoundActionOutcomeV1>(frozen.Actions.Count + 1);
        actions.AddRange(frozen.Actions);
        actions.Add(new RoundActionOutcomeV1
        {
            ScheduleId = boundedScheduleId,
            ActionKind = actionKind.Length > 64 ? actionKind[..64] : actionKind,
            TargetRound = targetRound,
            Outcome = outcome.Length > 64 ? outcome[..64] : outcome,
            Error = error == null ? null : error.Length > 256 ? error[..256] : error,
            ElapsedSeconds = elapsedSeconds ?? frozen.NativeElapsedSeconds
        });
        lastRoundReport = frozen with { Actions = actions };
    }

    private static RoundInsightsV1? SnapshotRoundInsights()
    {
        return telemetryRound == null ? null : BuildRoundSnapshot(telemetryRound);
    }

    private static RoundInsightsV1? SnapshotLastRoundReport()
    {
        return lastRoundReport;
    }

    private static RoundInsightsV1 BuildRoundSnapshot(TelemetryRound round)
    {
        var pressureByLane = new List<RoundLanePressureV1>(round.LaneCount);
        for (int i = 0; i < round.LaneCount; i++)
        {
            var lane = round.Lanes[i];
            pressureByLane.Add(new RoundLanePressureV1
            {
                PathIndex = lane.PathIndex,
                PeakBloonCount = lane.PeakBloonCount,
                PeakMoabCount = lane.PeakMoabCount,
                PeakNearExitCount = lane.PeakNearExitCount,
                MaxTrackProgress = lane.HasProgress ? Math.Round(lane.MaxProgress, 4) : null,
                NearExitSampledDurationSeconds = Math.Round(lane.NearExitSeconds, 3),
                Samples = lane.Samples
            });
        }
        pressureByLane.Sort((a, b) => a.PathIndex.CompareTo(b.PathIndex));

        var composition = new List<RoundCompositionGroupV1>(Math.Min(round.CompositionCount, RoundTelemetryMaxComposition));
        for (int i = 0; i < round.CompositionCount; i++)
        {
            var item = round.Compositions[i];
            InsertComposition(composition, new RoundCompositionGroupV1
            {
                BaseId = item.BaseId,
                Camo = item.Camo,
                Fortified = item.Fortified,
                Regrow = item.Regrow,
                Lead = item.Lead,
                Moab = item.Moab,
                ActiveCount = item.CurrentCount,
                PeakActiveCount = item.PeakCount,
                SamplesObserved = item.Samples
            });
        }

        var topThreats = CopyThreats(round.WorstThreats, round.WorstThreatCount);
        var remainingThreats = CopyThreats(round.CurrentThreats, round.CurrentThreatCount);
        var leaks = new List<RoundLeakV1>(round.LeakCount);
        for (int i = 0; i < round.LeakCount; i++)
        {
            int index = (round.LeakHead - round.LeakCount + i + round.Leaks.Length) % round.Leaks.Length;
            var leak = round.Leaks[index];
            leaks.Add(new RoundLeakV1
            {
                BaseId = leak.BaseId,
                Camo = leak.Camo,
                Fortified = leak.Fortified,
                Regrow = leak.Regrow,
                Lead = leak.Lead,
                Moab = leak.Moab,
                PathIndex = leak.PathIndex,
                TrackProgress = leak.TrackProgress,
                X = leak.X,
                Y = leak.Y,
                ExpectedDamage = leak.ExpectedDamage,
                ActualHealthLoss = leak.ActualHealthLoss,
                ElapsedSeconds = leak.ElapsedSeconds
            });
        }

        var actions = new List<RoundActionOutcomeV1>(round.ActionCount);
        for (int i = 0; i < round.ActionCount; i++)
        {
            var action = round.Actions[i];
            actions.Add(new RoundActionOutcomeV1
            {
                ScheduleId = action.ScheduleId,
                ActionKind = action.ActionKind,
                TargetRound = action.TargetRound,
                Outcome = action.Outcome,
                Error = action.Error,
                ElapsedSeconds = action.ElapsedSeconds
            });
        }

        var missing = new List<string>(6);
        if (!round.NativeClockEverKnown) missing.Add("native_round_clock");
        if (!round.ScheduleKnown || !round.ScheduleComplete) missing.Add("native_spawn_schedule");
        if (!round.EstimatedRemainingScheduledSpawns.HasValue) missing.Add("remaining_spawn_estimate");
        if (!round.PathGeometryEverKnown) missing.Add("native_path_geometry");
        if (!round.NativeProgressEverKnown) missing.Add("native_path_progress");
        if (round.MissingSamples > 0) missing.Add("sample_reads");
        return new RoundInsightsV1
        {
            MatchId = matchId,
            MatchGeneration = matchGeneration,
            Round = round.Round,
            Status = round.Status,
            ObservedAtUtc = round.LastObservedAtUtc == default ? DateTime.UtcNow : round.LastObservedAtUtc,
            NativeElapsedSeconds = round.LastRoundElapsedSeconds.HasValue ? Math.Round(round.LastRoundElapsedSeconds.Value, 3) : null,
            OmittedScheduleGroups = round.OmittedScheduleGroups,
            LastSpawnDurationSeconds = round.LastSpawnDurationSeconds,
            RemainingScheduledSpawns = round.SpawningComplete == true || round.Status is "completed" or "victory" ? 0 : null,
            RemainingScheduledSpawnsKnown = round.SpawningComplete == true || round.Status is "completed" or "victory",
            EstimatedRemainingScheduledSpawns = round.EstimatedRemainingScheduledSpawns,
            SpawningComplete = round.SpawningComplete,
            ActiveBloonCount = round.LastActiveCountsKnown ? round.LastActiveBloonCount : null,
            ActiveMoabCount = round.LastActiveCountsKnown ? round.LastActiveMoabCount : null,
            MaxTrackProgress = round.HasProgress ? Math.Round(round.MaxTrackProgress, 4) : null,
            PeakBloonCount = round.PeakBloonCount,
            PeakMoabCount = round.PeakMoabCount,
            PeakNearExitCount = round.PeakNearExitCount,
            NearExitSampledDurationSeconds = Math.Round(round.NearExitSampledDurationSeconds, 3),
            PostSpawnPressure = new PostSpawnPressureV1
            {
                Available = round.PostSpawnSamples > 0,
                Basis = "sampled_after_native_spawn_schedule_end",
                SampleCount = round.PostSpawnSamples,
                ObservedDurationSeconds = Math.Round(round.PostSpawnObservedSeconds, 3),
                MoabPresentDurationSeconds = Math.Round(round.PostSpawnMoabPresentSeconds, 3),
                CeramicPresentDurationSeconds = Math.Round(round.PostSpawnCeramicPresentSeconds, 3),
                PeakMoabCount = round.PostSpawnPeakMoabCount,
                PeakCeramicCount = round.PostSpawnPeakCeramicCount,
                PeakNearExitMoabCount = round.PostSpawnPeakNearExitMoabCount,
                PeakNearExitCeramicCount = round.PostSpawnPeakNearExitCeramicCount,
                MaxMoabProgress = round.PostSpawnHasMoabProgress ? Math.Round(round.PostSpawnMaxMoabProgress, 4) : null,
                MaxCeramicProgress = round.PostSpawnHasCeramicProgress ? Math.Round(round.PostSpawnMaxCeramicProgress, 4) : null
            },
            Samples = round.Samples,
            Coverage = new RoundTelemetryCoverageV1
            {
                RequestedIntervalSeconds = RoundTelemetrySampleFrames / 60d,
                NearExitThreshold = NativeTrackProgress.NearExitThreshold,
                ProgressBasis = NativeTrackProgress.ProgressBasis,
                ProgressBoundary = NativeTrackProgress.ProgressBoundary,
                SampleCount = round.Samples,
                MissingSampleCount = round.MissingSamples,
                FirstSampleElapsedSeconds = round.FirstRoundElapsedSeconds,
                LastSampleElapsedSeconds = round.LastRoundElapsedSeconds,
                NativeClockAvailable = round.NativeClockEverKnown,
                SamplingBasis = "native_simulation_elapsed",
                Completeness = round.Samples == 0 ? "none" : round.MissingSamples == 0 ? "sampled" : "partial",
                Missing = missing.ToArray(),
                RemainingSpawnsBasis = round.ScheduleKnown && round.ScheduleComplete
                    ? "estimated_uniform_group_windows"
                    : "unavailable_schedule_or_clock",
                LeakCapture = "native_pre_post_hooks",
                ExpectedDamageBasis = "Bloon.GetModifiedTotalLeakDamage; actualHealthLoss is the native health delta, including overkill"
            },
            WorstMoment = round.Samples == 0 ? null : new RoundWorstMomentV1
            {
                ElapsedSeconds = round.WorstMoment.ElapsedSeconds,
                MaxTrackProgress = round.WorstMoment.HasProgress ? Math.Round(round.WorstMoment.MaxProgress, 4) : null,
                BloonCount = round.WorstMoment.BloonCount,
                MoabCount = round.WorstMoment.MoabCount,
                NearExitCount = round.WorstMoment.NearExitCount,
                LeadCount = round.WorstMoment.LeadCount,
                CamoCount = round.WorstMoment.CamoCount,
                PathIndex = round.WorstMoment.PathIndex
            },
            PressureByLane = pressureByLane,
            Composition = composition,
            OmittedCompositionGroups = Math.Max(round.OmittedCompositionKeys, round.CompositionCount > RoundTelemetryMaxComposition ? round.CompositionCount - RoundTelemetryMaxComposition : 0),
            TopThreats = topThreats,
            ActiveThreats = remainingThreats,
            Leaks = new RoundLeakSummaryV1
            {
                Total = round.LeakTotal,
                Retained = round.LeakCount,
                Omitted = round.LeakOmitted,
                Entries = leaks
            },
            Actions = actions
        };
    }

    private static void InsertComposition(List<RoundCompositionGroupV1> items, RoundCompositionGroupV1 value)
    {
        int insert = 0;
        while (insert < items.Count && value.PeakActiveCount <= items[insert].PeakActiveCount) insert++;
        if (insert >= RoundTelemetryMaxComposition) return;
        if (items.Count < RoundTelemetryMaxComposition) items.Add(value);
        for (int i = items.Count - 1; i > insert; i--) items[i] = items[i - 1];
        items[insert] = value;
    }

    private static List<RoundThreatV1> CopyThreats(ThreatWork[] source, int count)
    {
        var result = new List<RoundThreatV1>(Math.Min(count, RoundTelemetryMaxThreats));
        for (int i = 0; i < count && i < source.Length; i++)
        {
            var item = source[i];
            result.Add(new RoundThreatV1
            {
                BaseId = item.BaseId,
                Camo = item.Camo,
                Fortified = item.Fortified,
                Regrow = item.Regrow,
                Lead = item.Lead,
                Moab = item.Moab,
                TrackProgress = item.HasProgress ? Math.Round(item.Progress, 4) : null,
                PathIndex = item.PathIndex,
                Health = item.Health,
                X = item.X,
                Y = item.Y
            });
        }
        return result;
    }

    private static void CaptureLeakBefore(Bloon bloon)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame() || inGame.bridge?.Simulation == null) return;
        if (telemetryGeneration != matchGeneration) ResetRoundTelemetry();

        int round;
        try { round = inGame.bridge.GetCurrentRound() + 1; } catch { return; }
        if (round < 1) return;
        bool roundsActive;
        try { roundsActive = inGame.bridge.AreRoundsActive(); } catch { return; }
        if (telemetryRound == null && !roundsActive) return;
        if (telemetryRound == null) BeginTelemetryRound(inGame.bridge, round);
        var current = telemetryRound;
        if (current == null || current.Frozen) return;
        current.SawActiveRound = true;

        if (current.LeakCount == current.Leaks.Length)
            current.LeakOmitted++;
        int slot = current.LeakHead;
        current.LeakHead = (current.LeakHead + 1) % current.Leaks.Length;
        if (current.LeakCount < current.Leaks.Length) current.LeakCount++;
        current.LeakTotal++;

        var leak = current.Leaks[slot];
        leak.Clear();
        leak.Pointer = bloon.Pointer;
        var model = bloon.bloonModel;
        if (model != null)
        {
            leak.BaseId = model.baseId ?? model.name ?? "Unknown";
            leak.Camo = model.isCamo;
            leak.Fortified = model.isFortified;
            leak.Regrow = model.isGrow;
            leak.Lead = (model.bloonProperties & BloonProperties.Lead) != 0;
            leak.Moab = model.isMoab;
            float damage = bloon.GetModifiedTotalLeakDamage();
            leak.ExpectedDamage = float.IsFinite(damage) && damage >= 0 ? damage : null;
        }

        BuildPathIndexMap(inGame.bridge);
        try
        {
            if (inGame.bridge.GetAllBloons() != null)
            {
                var sim = bloon.GetBloonToSim();
                var simBloon = sim?.GetSimBloon();
                if (simBloon != null)
                {
                    if (NativeTrackProgress.TryGetProgress(simBloon, out double progress))
                    {
                        leak.TrackProgress = progress;
                        current.NativeProgressEverKnown = true;
                    }
                    var path = simBloon.path;
                    if (path != null)
                        leak.PathIndex = telemetryPathIndices.TryGetValue(path.Pointer, out int pathIndex) ? pathIndex : null;
                    var position = simBloon.Position.data;
                    leak.X = position.x;
                    leak.Y = position.y;
                }
            }
        }
        catch { }

        if (TryReadNativeClock(inGame.bridge, round, out _, out var elapsed, out _))
            leak.ElapsedSeconds = elapsed;
        try { leak.HealthBefore = inGame.GetHealth(); } catch { }
        if (leak.Pointer != IntPtr.Zero) current.PendingLeaks[leak.Pointer] = slot;
    }
    private static void CaptureLeakAfter(Bloon bloon)
    {
        var current = telemetryRound;
        if (current == null || bloon == null || bloon.Pointer == IntPtr.Zero) return;
        if (!current.PendingLeaks.TryGetValue(bloon.Pointer, out int slot)) return;
        current.PendingLeaks.Remove(bloon.Pointer);
        var leak = current.Leaks[slot];
        leak.Posted = true;
        try
        {
            var inGame = InGame.instance;
            if (inGame != null && leak.HealthBefore.HasValue)
            {
                double after = inGame.GetHealth();
                leak.ActualHealthLoss = Math.Max(0d, leak.HealthBefore.Value - after);
            }
        }
        catch { }
        if (current.Frozen) lastRoundReport = BuildRoundSnapshot(current);
    }

    public override bool PreBloonLeaked(Bloon bloon)
    {
        try { if (bloon != null) CaptureLeakBefore(bloon); }
        catch (Exception ex) { ModHelper.Warning<AgentBridgeMod>($"Round telemetry pre-leak capture failed: {ex.Message}"); }
        return true;
    }

    public override void PostBloonLeaked(Bloon bloon)
    {
        try { if (bloon != null) CaptureLeakAfter(bloon); }
        catch (Exception ex) { ModHelper.Warning<AgentBridgeMod>($"Round telemetry post-leak capture failed: {ex.Message}"); }
    }
}

/// <summary>
/// ModHelper invokes OnDefeat from a postfix on UnityToSimulation.Lose in this game
/// version.  The prefix below runs before Lose performs terminal cleanup and therefore
/// captures active bloons for a defeat report without changing the return value or flow.
/// </summary>
[HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.Lose))]
internal static class RoundTelemetryLosePatch
{
    [HarmonyPrefix]
    private static void Prefix(UnityToSimulation __instance)
    {
        try { AgentBridgeMod.CaptureDefeatTelemetry(__instance); }
        catch (Exception ex) { ModHelper.Warning<AgentBridgeMod>($"Round telemetry defeat capture failed: {ex.Message}"); }
    }
}

internal sealed record RoundInsightsV1
{
    public string? MatchId { get; init; }
    public long MatchGeneration { get; init; }
    public int Round { get; init; }
    public string Status { get; init; } = "active";
    public DateTime ObservedAtUtc { get; init; }
    public double? NativeElapsedSeconds { get; init; }
    public int OmittedScheduleGroups { get; init; }
    public double? LastSpawnDurationSeconds { get; init; }
    public int? RemainingScheduledSpawns { get; init; }
    public bool RemainingScheduledSpawnsKnown { get; init; }
    public int? EstimatedRemainingScheduledSpawns { get; init; }
    public bool? SpawningComplete { get; init; }
    public int? ActiveBloonCount { get; init; }
    public int? ActiveMoabCount { get; init; }
    public double? MaxTrackProgress { get; init; }
    public int PeakBloonCount { get; init; }
    public int PeakMoabCount { get; init; }
    public int PeakNearExitCount { get; init; }
    public double NearExitSampledDurationSeconds { get; init; }
    public PostSpawnPressureV1 PostSpawnPressure { get; init; } = new();
    public int Samples { get; init; }
    public RoundTelemetryCoverageV1 Coverage { get; init; } = new();
    public RoundWorstMomentV1? WorstMoment { get; init; }
    public List<RoundLanePressureV1> PressureByLane { get; init; } = [];
    public List<RoundCompositionGroupV1> Composition { get; init; } = [];
    public int OmittedCompositionGroups { get; init; }
    public List<RoundThreatV1> TopThreats { get; init; } = [];
    public List<RoundThreatV1> ActiveThreats { get; init; } = [];
    public RoundLeakSummaryV1 Leaks { get; init; } = new();
    public List<RoundActionOutcomeV1> Actions { get; init; } = [];
}

internal sealed class PostSpawnPressureV1
{
    public bool Available { get; init; }
    public string Basis { get; init; } = "sampled_after_native_spawn_schedule_end";
    public int SampleCount { get; init; }
    public double ObservedDurationSeconds { get; init; }
    public double MoabPresentDurationSeconds { get; init; }
    public double CeramicPresentDurationSeconds { get; init; }
    public int PeakMoabCount { get; init; }
    public int PeakCeramicCount { get; init; }
    public int PeakNearExitMoabCount { get; init; }
    public int PeakNearExitCeramicCount { get; init; }
    public double? MaxMoabProgress { get; init; }
    public double? MaxCeramicProgress { get; init; }
}

internal sealed class RoundTelemetryCoverageV1
{
    public double RequestedIntervalSeconds { get; init; }
    public double NearExitThreshold { get; init; }
    public string ProgressBasis { get; init; } = "native_bloon_PercThroughMap";
    public string ProgressBoundary { get; init; } = "native_path_MaxDistUntilSpawn_to_MaxDistUntilLeak";
    public int SampleCount { get; init; }
    public int MissingSampleCount { get; init; }
    public double? FirstSampleElapsedSeconds { get; init; }
    public double? LastSampleElapsedSeconds { get; init; }
    public bool NativeClockAvailable { get; init; }
    public string SamplingBasis { get; init; } = "native_simulation_elapsed";
    public string Completeness { get; init; } = "sampled";
    public string[] Missing { get; init; } = [];
    public string RemainingSpawnsBasis { get; init; } = "unavailable_schedule_or_clock";
    public string LeakCapture { get; init; } = "native_pre_post_hooks";
    public string ExpectedDamageBasis { get; init; } = "BloonModel.leakDamage; modifiers are not folded into this field";
}

internal sealed class RoundWorstMomentV1
{
    public double? ElapsedSeconds { get; init; }
    public double? MaxTrackProgress { get; init; }
    public int BloonCount { get; init; }
    public int MoabCount { get; init; }
    public int NearExitCount { get; init; }
    public int LeadCount { get; init; }
    public int CamoCount { get; init; }
    public int? PathIndex { get; init; }
}

internal sealed class RoundLanePressureV1
{
    public int PathIndex { get; init; }
    public int PeakBloonCount { get; init; }
    public int PeakMoabCount { get; init; }
    public int PeakNearExitCount { get; init; }
    public double? MaxTrackProgress { get; init; }
    public double NearExitSampledDurationSeconds { get; init; }
    public int Samples { get; init; }
}

internal sealed class RoundCompositionGroupV1
{
    public string BaseId { get; init; } = "";
    public bool Camo { get; init; }
    public bool Fortified { get; init; }
    public bool Regrow { get; init; }
    public bool Lead { get; init; }
    public bool Moab { get; init; }
    public int ActiveCount { get; init; }
    public int PeakActiveCount { get; init; }
    public int SamplesObserved { get; init; }
}

internal sealed class RoundThreatV1
{
    public string BaseId { get; init; } = "";
    public bool Camo { get; init; }
    public bool Fortified { get; init; }
    public bool Regrow { get; init; }
    public bool Lead { get; init; }
    public bool Moab { get; init; }
    public double? TrackProgress { get; init; }
    public int? PathIndex { get; init; }
    public int? Health { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
}

internal sealed class RoundLeakSummaryV1
{
    public int Total { get; init; }
    public int Retained { get; init; }
    public int Omitted { get; init; }
    public List<RoundLeakV1> Entries { get; init; } = [];
}

internal sealed class RoundLeakV1
{
    public string BaseId { get; init; } = "";
    public bool Camo { get; init; }
    public bool Fortified { get; init; }
    public bool Regrow { get; init; }
    public bool Lead { get; init; }
    public bool Moab { get; init; }
    public int? PathIndex { get; init; }
    public double? TrackProgress { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public double? ExpectedDamage { get; init; }
    public double? ActualHealthLoss { get; init; }
    public double? ElapsedSeconds { get; init; }
}

internal sealed class RoundActionOutcomeV1
{
    public string ScheduleId { get; init; } = "";
    public string ActionKind { get; init; } = "";
    public int TargetRound { get; init; }
    public string Outcome { get; init; } = "";
    public string? Error { get; init; }
    public double? ElapsedSeconds { get; init; }
}
