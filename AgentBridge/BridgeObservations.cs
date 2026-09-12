using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader.Utils;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static BridgeStatusV1 BuildStatus()
    {
        string? selectedHero = null;
        try
        {
            selectedHero = Game.instance?.GetPlayerProfile()?.primaryHero;
        }
        catch { }

        return new()
        {
            Btd6Version = Application.version,
            GameAvailable = Game.instance != null,
            ActiveGame = IsActiveGame(),
            OnMainMenu = IsOnMainMenu(),
            SelectedHero = selectedHero,
            AutoCollectDrops = autoCollectDrops,
            Ui = uiState
        };
    }

    private static BridgeObservationV1 BuildObservation()
    {
        UpdateUiState(false);
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
        {
            return new BridgeObservationV1
            {
                ObservedAtUtc = DateTime.UtcNow,
                ActiveGame = false,
                Ui = uiState
            };
        }

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        var currentGame = InGameData.CurrentGame;

        var towers = new List<TowerInfoV1>();
        HeroInfoV1? heroInfo = null;

        if (bridge != null)
        {
            List<TowerToSimulation>? towerSims = null;
            try
            {
                towerSims = inGame.GetAllTowerToSim();
            }
            catch (Exception ex)
            {
                ModHelper.Warning<AgentBridgeMod>($"Failed to enumerate towers: {ex.Message}");
            }

            if (towerSims != null)
            {
                foreach (var tower in towerSims)
                {
                    if (tower == null || tower.Def == null)
                        continue;

                    var isHero = tower.hero != null;
                    if (isHero && heroInfo == null && tower.hero != null)
                    {
                        try
                        {
                            var hero = tower.hero;
                            heroInfo = new HeroInfoV1
                            {
                                HeroId = tower.Def?.baseId ?? "Unknown",
                                Level = hero.level,
                                Xp = hero.relativeXp,
                                XpToNextLevel = hero.relativeXpForNextLevel,
                                CostToLevelUp = hero.costToLevelUp
                            };
                        }
                        catch (Exception ex)
                        {
                            ModHelper.Warning<AgentBridgeMod>($"Failed to extract hero data: {ex.Message}");
                        }
                    }

                    towers.Add(ConvertTower(tower, gameModel));
                }
            }
        }

        var abilities = new List<AbilityInfoV1>();
        try
        {
            var abilitySims = inGame.GetAbilities();
            if (abilitySims != null)
            {
                foreach (var abilitySim in abilitySims)
                {
                    if (abilitySim == null)
                        continue;

                    abilities.Add(new AbilityInfoV1
                    {
                        AbilityId = abilitySim.ability?.Id.ToString() ?? abilitySim.TowerId.ToString(),
                        TowerId = abilitySim.TowerId.ToString(),
                        Name = abilitySim.model?.displayName ?? abilitySim.model?.name ?? "Unknown",
                        IsReady = abilitySim.IsReady,
                        CanUse = abilitySim.CanUseAbility(),
                        CooldownRemaining = abilitySim.CooldownRemaining,
                        CooldownTotal = abilitySim.CooldownTotal,
                        Targeting = DescribeAbilityTargeting(abilitySim)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"Failed to read abilities: {ex.Message}");
        }

        double? cash = null;
        double? lives = null;
        double? maxLives = null;
        try { cash = inGame.GetCash(); } catch { }
        try { lives = inGame.GetHealth(); } catch { }
        try { maxLives = inGame.GetMaxHealth(); } catch { }

        int? round = null;
        int? endRound = null;
        bool? roundActive = null;
        bool? canStartRound = null;
        bool? autoPlay = null;

        if (bridge != null)
        {
            try { round = bridge.GetCurrentRound() + 1; } catch { }
            try { endRound = bridge.GetEndRound() + 1; } catch { }
            try { roundActive = bridge.AreRoundsActive(); } catch { }
            try { canStartRound = bridge.CanSendNextRound(); } catch { }
            try { autoPlay = bridge.Simulation != null && bridge.Simulation.autoPlay; } catch { }
        }

        bool? inBetweenRounds = null;
        bool? fastForward = null;
        try { inBetweenRounds = TimeManager.inBetweenRounds; } catch { }
        try { fastForward = TimeManager.FastForwardActive; } catch { }

        bool matchLost = false;
        bool matchWon = false;
        try { matchLost = inGame.MatchLost; } catch { }
        try { matchWon = inGame.WaitingForVictoryScreen; } catch { }

        bool isPaused = isMatchPaused || TimeManager.gamePaused || (UnityEngine.Time.timeScale == 0f && IsActiveGame());

        string gameStatus = matchLost ? "defeat"
            : matchWon ? "victory"
            : isPaused ? "paused"
            : (roundActive == true) ? "round_in_progress"
            : "in_game";

        double? maxTrackProgress = null;
        int? activeBloonCount = null;
        int? activeMoabCount = null;
        var trackProgressByLane = new List<TrackLaneProgressV1>();
        var activeThreats = new List<ActiveThreatV1>();

        if (bridge != null && roundActive == true)
        {
            try
            {
                var bloons = bridge.GetAllBloons();
                if (bloons != null)
                {
                    int totalBloons = 0;
                    int totalMoabs = 0;
                    double? globalMaxProgress = null;
                    var laneMap = new Dictionary<int, TrackLaneProgressV1>();
                    var leadingThreats = new List<(BloonToSimulation Bloon, double? Progress, int Lane, double Score)>(8);

                    var sim = bridge.Simulation;
                    var paths = sim?.Map?.pathManager?.paths;
                    var pathIndices = new Dictionary<IntPtr, int>();
                    if (paths != null)
                        for (int p = 0; p < paths.Count; p++)
                            if (paths[p] != null) pathIndices[paths[p].Pointer] = p;

                    var bloonEnum = bloons.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                    while (bloonEnum.MoveNext())
                    {
                        var b = bloonEnum.Current?.TryCast<BloonToSimulation>();
                        if (b == null || b.Def == null) continue;
                        totalBloons++;
                        var def = b.Def;
                        bool isMoab = def.isMoab;
                        if (isMoab) totalMoabs++;

                        var simBloon = b.GetSimBloon();
                        double? progress = NativeTrackProgress.TryGetProgress(simBloon, out double normalizedProgress)
                            ? normalizedProgress : null;
                        int pathIndex = -1;
                        if (simBloon?.path != null && pathIndices.TryGetValue(simBloon.path.Pointer, out int mappedPath))
                            pathIndex = mappedPath;

                        if (progress.HasValue && (!globalMaxProgress.HasValue || progress.Value > globalMaxProgress.Value))
                        {
                            globalMaxProgress = progress;
                        }

                        if (!laneMap.TryGetValue(pathIndex, out var lane))
                        {
                            lane = new TrackLaneProgressV1
                            {
                                PathIndex = pathIndex,
                                MaxProgress = progress.HasValue ? Math.Round(progress.Value, 3) : null,
                                BloonCount = 1,
                                LeadCount = (def.bloonProperties & Il2Cpp.BloonProperties.Lead) != 0 ? 1 : 0,
                                CamoCount = def.isCamo ? 1 : 0,
                                MoabCount = isMoab ? 1 : 0
                            };
                            laneMap[pathIndex] = lane;
                        }
                        else
                        {
                            lane.BloonCount++;
                            if (progress.HasValue && (!lane.MaxProgress.HasValue || progress.Value > lane.MaxProgress.Value))
                                lane.MaxProgress = Math.Round(progress.Value, 3);
                            if ((def.bloonProperties & Il2Cpp.BloonProperties.Lead) != 0) lane.LeadCount++;
                            if (def.isCamo) lane.CamoCount++;
                            if (isMoab) lane.MoabCount++;
                        }

                        double? roundedProgress = progress.HasValue ? Math.Round(progress.Value, 3) : null;
                        double score = (roundedProgress >= .7 ? 1000 : 0) + (isMoab ? 500 : 0) + (roundedProgress ?? 0) * 100;
                        if (simBloon != null && (leadingThreats.Count < 8 || score > leadingThreats[^1].Score))
                        {
                            int insert = 0;
                            while (insert < leadingThreats.Count && score <= leadingThreats[insert].Score) insert++;
                            if (leadingThreats.Count == 8) leadingThreats.RemoveAt(7);
                            leadingThreats.Insert(insert, (b, roundedProgress, pathIndex, score));
                        }
                    }

                    if (totalBloons > 0)
                    {
                        activeBloonCount = totalBloons;
                        activeMoabCount = totalMoabs;
                        maxTrackProgress = globalMaxProgress.HasValue ? Math.Round(globalMaxProgress.Value, 3) : null;
                        trackProgressByLane = laneMap.Values.OrderBy(l => l.PathIndex).ToList();

                        foreach (var threat in leadingThreats)
                        {
                            var b = threat.Bloon;
                            var def = b.Def;
                            var position = b.GetSimBloon().Position.data;
                            activeThreats.Add(new ActiveThreatV1
                            {
                                Type = def.baseId ?? def.name ?? "Unknown",
                                IsFortified = def.isFortified,
                                IsCamo = def.isCamo,
                                IsMoab = def.isMoab,
                                TrackProgress = threat.Progress,
                                PathIndex = threat.Lane,
                                Health = b.GetHealth(),
                                X = (float)Math.Round(position.x, 1),
                                Y = (float)Math.Round(position.y, 1)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModHelper.Warning<AgentBridgeMod>($"Failed to collect bloon telemetry: {ex.Message}");
            }
        }

        var roundReport = SnapshotLastRoundReport();
        if (matchLost && roundReport?.Status == "defeat")
        {
            activeBloonCount = roundReport.ActiveBloonCount;
            activeMoabCount = roundReport.ActiveMoabCount;
            activeThreats = roundReport.ActiveThreats
                .Where(threat => threat.PathIndex.HasValue &&
                    threat.Health.HasValue && threat.X.HasValue && threat.Y.HasValue)
                .Select(threat => new ActiveThreatV1
                {
                    Type = threat.BaseId,
                    IsFortified = threat.Fortified,
                    IsCamo = threat.Camo,
                    IsMoab = threat.Moab,
                    TrackProgress = threat.TrackProgress,
                    PathIndex = threat.PathIndex!.Value,
                    Health = threat.Health!.Value,
                    X = threat.X!.Value,
                    Y = threat.Y!.Value
                }).ToList();
            maxTrackProgress = roundReport.ActiveThreats.Select(threat => threat.TrackProgress).Max();
        }

        var observationTime = DateTime.UtcNow;
        return new BridgeObservationV1
        {
            ObservedAtUtc = observationTime,
            ActiveGame = true,
            Ui = uiState,
            GameStatus = gameStatus,
            IsPaused = isPaused,
            Round = round,
            EndRound = endRound,
            RoundActive = roundActive,
            RoundElapsedSeconds = roundActive == true && hasRoundElapsedTime ? currentRoundElapsedSeconds : null,
            InBetweenRounds = inBetweenRounds,
            CanStartRound = canStartRound,
            FastForward = fastForward,
            AutoPlay = autoPlay,
            Cash = cash,
            Lives = lives,
            MaxLives = maxLives,
            TowerCount = towers.Count,
            MapId = currentGame?.selectedMap ?? gameModel?.map?.mapName,
            MapName = ResolveMapName(currentGame?.selectedMap ?? gameModel?.map?.mapName ?? ""),
            Mode = currentGame?.selectedMode == Il2CppAssets.Scripts.Models.Difficulty.ModeType.CHIMPS ? "CHIMPS" : currentGame?.selectedMode,
            Difficulty = currentGame?.selectedDifficulty,
            Towers = towers,
            Bosses = BuildBossSummaries(),
            Hero = heroInfo,
            Abilities = abilities,
            ScheduledAbilities = scheduledAbilities.Where(s => !s.Triggered ||
                (s.TriggeredAtUtc is { } completed && observationTime - completed < ScheduledAbilityRetention))
                .Select(s => s.Snapshot()).ToList(),
            ScheduledUpgrades = SnapshotScheduledUpgrades(),
            ScheduledGeraldoPurchases = SnapshotScheduledGeraldoPurchases(),
            ScheduledCorvusActions = SnapshotScheduledCorvusActions(),
            RoundInsights = SnapshotRoundInsights(),
            RoundReport = roundReport,
            ThreatSnapshotSource = matchLost && roundReport?.Status == "defeat" ? "defeat_report" : "live",
            AvailableCheckpoints = roundJsonCheckpoints.Keys.OrderBy(r => r).ToArray(),
            MaxTrackProgress = maxTrackProgress,
            ActiveBloonCount = activeBloonCount,
            ActiveMoabCount = activeMoabCount,
            TrackProgressByLane = trackProgressByLane,
            ActiveThreats = activeThreats
        };
    }

    private static bool IsActiveGame() => InGame.instance != null && InGame.instance.IsInGame();

    private static BridgeResultV1 SuccessResult(BridgeRequestV1 request, object result) => new()
    {
        RequestId = request.RequestId,
        Ok = true,
        Result = result
    };

    private static BridgeResultV1 ErrorResult(BridgeRequestV1 request, string code, string message, bool retryable, object? details = null) => new()
    {
        RequestId = request.RequestId,
        Ok = false,
        Error = new BridgeErrorV1 { Code = code, Message = message, Retryable = retryable, Details = details }
    };


    private static void PublishStateSnapshot()
    {
        System.Threading.Interlocked.Exchange(ref pendingState, new BridgeStateV1
        {
            Status = BuildStatus(),
            Progress = BuildRoundProgress()
        });
    }

    private static void WriteJsonAtomically(string path, object payload)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        string json = JsonSerializer.Serialize(payload, ProtocolJsonOptions);
        serializationTimes.Add(ElapsedMs(start));
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            atomicWriteTimes.Add(ElapsedMs(start));
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsSafeRequestId(string requestId) => requestId.Length is > 0 and <= 128
        && requestId.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.' or '_' or '-');

    private static string IpcDirectory => Path.Combine(MelonEnvironment.UserDataDirectory, "AgentBridge", "ipc");
    private static string ReportsDirectory => Path.Combine(MelonEnvironment.UserDataDirectory, "AgentBridge", "reports");
    private static string IpcInboxDirectory => Path.Combine(IpcDirectory, "inbox");
    private static string IpcProcessingDirectory => Path.Combine(IpcDirectory, "processing");
    private static string IpcOutboxDirectory => Path.Combine(IpcDirectory, "outbox");
    private static string IpcStatePath => Path.Combine(IpcDirectory, "state.json");
}
