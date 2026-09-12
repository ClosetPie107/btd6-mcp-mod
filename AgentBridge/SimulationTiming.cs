using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void UpdateSimulationTiming()
    {
        var inGame = InGame.instance;
        if (inGame != null && inGame.IsInGame())
        {
            var bridge = inGame.bridge;
            if (bridge != null)
            {
                bool matchLost = false;
                bool matchWon = false;
                try { matchLost = inGame.MatchLost; } catch { }
                try { matchWon = inGame.WaitingForVictoryScreen; } catch { }

                if ((matchLost || matchWon) && isMatchPaused)
                {
                    isMatchPaused = false;
                    try { TimeManager.gamePaused = false; } catch { }
                    try { TimeManager.ResetTimeScale(); } catch { }
                    if (UnityEngine.Time.timeScale == 0f) UnityEngine.Time.timeScale = 1f;
                }

                bool roundsActive = false;
                try { roundsActive = bridge.AreRoundsActive(); } catch { }

                int round = -1;
                try { round = bridge.GetCurrentRound() + 1; } catch { }

                if (round == 1 && !roundsActive && ShouldCheckpoint(1) && !roundJsonCheckpoints.ContainsKey(1))
                {
                    SaveRoundCheckpoint(1, 0);
                }

                if (!roundsActive && lastRoundsActive && !matchLost)
                {
                    int completedRound = lastObservedRound;
                    int nextRound = completedRound + 1;
                    if (completedRound > 0 && ShouldCheckpoint(nextRound))
                        SaveRoundCheckpoint(nextRound, completedRound);
                }
                if (roundsActive) lastObservedRound = round;

                if (!roundsActive || clockRound != round)
                {
                    ResetRoundClock();
                    clockRound = roundsActive ? round : -1;
                }
                var spawner = bridge.Simulation?.Map?.spawner;
                if (roundsActive && spawner != null &&
                    spawner.roundData.TryGetValue(round - 1, out var nativeRound) && nativeRound != null)
                    currentRoundStartTick = nativeRound.roundStartTime;

                // Native spawn records disappear after the last spawn, not after
                // the last bloon. Retain the observed anchor for the entire round.
                // Never synthesize an anchor when attaching after the record is gone.
                if (currentRoundStartTick > bridge.ElapsedTime) currentRoundStartTick = null;
                currentRoundElapsedSeconds = 0f;
                hasRoundElapsedTime = !roundsActive;
                if (roundsActive && currentRoundStartTick.HasValue)
                {
                    currentRoundElapsedSeconds = (bridge.ElapsedTime - currentRoundStartTick.Value) / 60f;
                    hasRoundElapsedTime = true;
                }

                lastRoundsActive = roundsActive;

                if (roundsActive && !matchLost && !matchWon && hasRoundElapsedTime && !isMatchPaused && UnityEngine.Time.timeScale > 0f)
                {
                    ProcessScheduledAbilities(inGame, bridge, round, currentRoundElapsedSeconds);
                }
            }
        }
        else
        {
            ResetRoundClock();
            lastObservedRound = -1;
            lastRoundsActive = false;
            isMatchPaused = false;
            if (scheduledAbilities.Count > 0)
            {
                scheduledAbilities.Clear();
            }
            ResetScheduledGeraldoPurchases();
            ResetScheduledCorvusActions();
        }
    }

    private static void ResetRoundClock()
    {
        clockRound = -1;
        currentRoundStartTick = null;
        currentRoundElapsedSeconds = 0f;
        hasRoundElapsedTime = false;
    }

    private static void ResetManagedMatchState()
    {
        matchGeneration++;
        matchId = null;
        ResetMapGeometryCache();
        isMatchPaused = false;
        ResetRoundClock();
        lastObservedRound = -1;
        lastRoundsActive = false;
        scheduledAbilities.Clear();
        ResetScheduledUpgrades();
        ResetScheduledGeraldoPurchases();
        ResetScheduledCorvusActions();
        ResetRoundTelemetry();
    }

    private static void ResetNativeMatchTime()
    {
        try { TimeManager.gamePaused = false; } catch { }
        try { TimeManager.ResetTimeScale(); } catch { }
        try { if (UnityEngine.Time.timeScale == 0f) UnityEngine.Time.timeScale = 1f; } catch { }
    }
    internal static void HandleNativeRestartCommitted()
    {
        // This is called from the committed UnityToSimulation.Restart postfix.
        // Keeping managed reset work here makes UI defeat restarts and restart_match share
        // one post-native lifecycle, while a cancelled restart confirmation leaves
        // checkpoints and pending schedules untouched.
        ResetAllCheckpoints();
        ResetManagedMatchState();
        ResetNativeMatchTime();
    }
}
