import type {
    BridgeObservation, RoundReport, ScheduledAbilityInfo, PlacedTower,
    StartRoundResult, WaitForRoundEndResult
} from './bridge-client.js';
import type { ScheduledUpgrade, ScheduledGeraldoPurchase, ScheduledCorvusAction } from './bridge-data.js';

type OutputProfile = 'summary' | 'full';
type RoundResult = StartRoundResult | WaitForRoundEndResult;
export interface CheckpointRecoveryInventory {
    availableRoundCheckpoints: number[];
    customCheckpoints: string[];
    importedCheckpoints?: Array<{
        checkpointId: string;
        label: string | null;
        round: number;
    }>;
}

function summarizeRecovery(inventory: CheckpointRecoveryInventory) {
    const rounds = [...new Set(inventory.availableRoundCheckpoints)].sort((left, right) => left - right);
    const latestRound = rounds.at(-1);
    const earlierRounds = rounds.slice(Math.max(0, rounds.length - 6), -1).reverse();
    const custom = inventory.customCheckpoints.slice(0, 10);
    const imported = (inventory.importedCheckpoints ?? []).slice(0, 10);
    return {
        available: latestRound !== undefined || custom.length > 0 || imported.length > 0,
        latest: latestRound === undefined ? null : {
            selector: { round: latestRound },
            nextPlayableRound: latestRound,
            lastCompletedRound: latestRound - 1
        },
        earlierRoundAnchors: earlierRounds.map(round => ({ selector: { round }, nextPlayableRound: round })),
        custom: custom.map(label => ({ selector: { label } })),
        imported: imported.map(checkpoint => ({
            selector: { checkpointId: checkpoint.checkpointId },
            label: checkpoint.label,
            nextPlayableRound: checkpoint.round
        })),
        omittedCustom: Math.max(0, inventory.customCheckpoints.length - custom.length),
        omittedImported: Math.max(0, (inventory.importedCheckpoints?.length ?? 0) - imported.length)
    };
}


/** Keep purchase/targeting state; nested values can be shared because projections never mutate them. */
export function summarizeTower(tower: PlacedTower) {
    return {
        id: tower.id,
        towerType: tower.towerType,
        position: tower.position,
        range: tower.range,
        targetPriority: tower.targetPriority,
        tiers: tower.tiers,
        upgradeCosts: tower.upgradeCosts,
        nextUpgrades: tower.nextUpgrades,
        crosspathSlotsRemaining: tower.crosspathSlotsRemaining,
        sellValue: tower.sellValue,
        isHero: tower.isHero,
        bank: tower.bank,
        isSubmerged: tower.isSubmerged,
        targetPosition: tower.targetPosition
    };
}

function pendingOrFailedAbilities(schedules: ScheduledAbilityInfo[]) {
    return schedules.filter(schedule => schedule.triggered !== true || schedule.error != null)
        .map(({ scheduledAtUtc: _created, triggeredAtUtc: _triggered, ...schedule }) => schedule);
}

function pendingOrFailedUpgrades(schedules: ScheduledUpgrade[]) {
    return schedules.filter(schedule => schedule.status !== 'completed' && schedule.status !== 'cancelled')
        .map(schedule => ({
            scheduleId: schedule.scheduleId,
            towerId: schedule.towerId,
            upgradeSequence: schedule.upgradeSequence,
            whenAffordable: schedule.whenAffordable,
            targetRound: schedule.targetRound,
            delaySeconds: schedule.delaySeconds,
            status: schedule.status,
            failure: schedule.failure,
            appliedPaths: schedule.appliedPaths,
            nextIndex: schedule.nextIndex,
            nextUpgradeCost: schedule.nextUpgradeCost,
            waitingFor: schedule.waitingFor
        }));
}

function pendingOrFailedGeraldoPurchases(schedules: ScheduledGeraldoPurchase[]) {
    return schedules.filter(schedule => schedule.status !== 'completed' && schedule.status !== 'cancelled')
        .map(schedule => ({
            scheduleId: schedule.scheduleId,
            itemId: schedule.itemId,
            target: schedule.target,
            whenAffordable: schedule.whenAffordable,
            targetRound: schedule.targetRound,
            delaySeconds: schedule.delaySeconds,
            status: schedule.status,
            waitingFor: schedule.waitingFor,
            failure: schedule.failure,
            result: schedule.result
        }));
}
function pendingOrFailedCorvusActions(schedules: ScheduledCorvusAction[]) {
    return schedules.filter(schedule => schedule.status !== 'completed' && schedule.status !== 'cancelled');
}

function summarizeCoverage(coverage: RoundReport['coverage']) {
    return {
        completeness: coverage.completeness,
        nativeClockAvailable: coverage.nativeClockAvailable,
        missingSampleCount: coverage.missingSampleCount,
        ...(coverage.missing.length > 0 ? { missing: coverage.missing } : {}),
        nearExitThreshold: coverage.nearExitThreshold,
        progressBasis: coverage.progressBasis,
        progressBoundary: coverage.progressBoundary,
        leakCapture: coverage.leakCapture
    };
}

function summarizeLeaks(leaks: RoundReport['leaks']) {
    const entries = leaks.entries.slice(-5).map(({ x: _x, y: _y, ...entry }) => entry);
    return {
        total: leaks.total,
        retained: entries.length,
        omitted: leaks.omitted + Math.max(0, leaks.retained - entries.length),
        entries
    };
}

function summarizeRoundFeedback(report: RoundReport, includeTerminalState: boolean) {
    const actions = report.actions.filter(action => action.outcome !== 'completed' || action.error !== null);
    const retainedActions = actions.slice(-8);
    return {
        round: report.round,
        status: report.status,
        nativeElapsedSeconds: report.nativeElapsedSeconds,
        peakBloonCount: report.peakBloonCount,
        peakMoabCount: report.peakMoabCount,
        peakNearExitCount: report.peakNearExitCount,
        nearExitSampledDurationSeconds: report.nearExitSampledDurationSeconds,
        coverage: summarizeCoverage(report.coverage),
        ...(report.peakNearExitCount > 0 || includeTerminalState ? { worstMoment: report.worstMoment } : {}),
        ...(report.omittedScheduleGroups > 0 ? { omittedScheduleGroups: report.omittedScheduleGroups } : {}),
        ...(report.leaks.total > 0 || includeTerminalState ? { leaks: summarizeLeaks(report.leaks) } : {}),
        ...(report.actions.length > actions.length ? { completedActionCount: report.actions.length - actions.length } : {}),
        ...(retainedActions.length > 0 ? { actions: retainedActions } : {}),
        ...(actions.length > retainedActions.length || (report.omittedActions ?? 0) > 0
            ? { omittedActions: (report.omittedActions ?? 0) + actions.length - retainedActions.length } : {}),
        ...(includeTerminalState ? {
            postSpawnPressure: report.postSpawnPressure,
            lastSpawnDurationSeconds: report.lastSpawnDurationSeconds,
            remainingScheduledSpawns: report.remainingScheduledSpawns,
            remainingScheduledSpawnsKnown: report.remainingScheduledSpawnsKnown,
            estimatedRemainingScheduledSpawns: report.estimatedRemainingScheduledSpawns,
            spawningComplete: report.spawningComplete,
            activeBloonCount: report.activeBloonCount,
            activeMoabCount: report.activeMoabCount,
            maxTrackProgress: report.maxTrackProgress,
            activeThreats: report.activeThreats.map(({ x: _x, y: _y, ...threat }) => threat)
        } : {})
    };
}

/** Self-contained state, not a delta. UI, ability targeting and boss uncertainty remain intact. */
export function formatObservationOutput(observation: BridgeObservation, profile: OutputProfile) {
    if (profile === 'full') return observation;
    const {
        towers, scheduledAbilities, scheduledUpgrades, scheduledGeraldoPurchases, scheduledCorvusActions,
        roundReport: _retainedReport, roundInsights,
        activeThreats, trackProgressByLane, maxTrackProgress, activeBloonCount, activeMoabCount,
        threatSnapshotSource, ...state
    } = observation;
    const live = observation.roundActive === true;
    const currentInsights = live && roundInsights?.status === 'active' && roundInsights.round === observation.round
        ? roundInsights : undefined;
    return {
        ...state,
        towers: towers.map(summarizeTower),
        ...(scheduledAbilities !== undefined ? { scheduledAbilities: pendingOrFailedAbilities(scheduledAbilities) } : {}),
        ...(scheduledUpgrades !== undefined ? { scheduledUpgrades: pendingOrFailedUpgrades(scheduledUpgrades) } : {}),
        ...(scheduledGeraldoPurchases !== undefined ? { scheduledGeraldoPurchases: pendingOrFailedGeraldoPurchases(scheduledGeraldoPurchases) } : {}),
        ...(scheduledCorvusActions !== undefined ? { scheduledCorvusActions: pendingOrFailedCorvusActions(scheduledCorvusActions) } : {}),
        ...(currentInsights ? { roundInsights: summarizeRoundFeedback(currentInsights, true) } : {}),
        ...(live ? {
            activeBloonCount, activeMoabCount, maxTrackProgress, trackProgressByLane,
            // Live coordinates are actionable inputs for targeted abilities, not diagnostic clutter.
            activeThreats, threatSnapshotSource,
            ...(!currentInsights ? { telemetryAvailable: false } : {})
        } : {})
    };
}

function reportMatchesResult(report: RoundReport, result: RoundResult): boolean {
    if (report.round !== (result.roundCompleted ?? result.round)) return false;
    switch (result.statusReason) {
        case 'round_cleared': return report.status === 'completed';
        case 'victory': return report.status === 'victory';
        case 'defeat': return report.status === 'defeat';
        default: return false;
    }
}

function insightMatchesResult(insight: RoundReport, result: RoundResult): boolean {
    if (result.statusReason === 'timeout' || result.statusReason === 'ui_blocked') {
        return insight.round === result.round && insight.status === 'active';
    }
    return reportMatchesResult(insight, result);
}

/** Waiting captures once in BridgeClient; only the MCP boundary removes diagnostic detail. */
export function formatRoundOutput(
    result: RoundResult,
    _profile: OutputProfile,
    recoveryInventory?: CheckpointRecoveryInventory
) {
    const { observation, summary, roundReport, ...outcome } = result;
    const currentObservation = observation?.round === result.round && observation.gameStatus === result.gameStatus
        ? observation : undefined;
    const report = roundReport && reportMatchesResult(roundReport, result) ? roundReport : undefined;
    const insight = !report && currentObservation?.roundInsights
        && insightMatchesResult(currentObservation.roundInsights, result) ? currentObservation.roundInsights : undefined;
    const terminal = result.statusReason === 'defeat' || result.statusReason === 'timeout';
    const checkpoints = currentObservation?.availableCheckpoints ?? [];
    const latestCheckpoint = checkpoints.length ? Math.max(...checkpoints) : undefined;
    return {
        ...outcome,
        ...(summary ? { towerCount: summary.towerCount } : {}),
        ...(latestCheckpoint !== undefined ? {
            checkpoint: {
                nextPlayableRound: latestCheckpoint,
                lastCompletedRound: latestCheckpoint - 1,
                periodicAnchor: latestCheckpoint > 1 && (latestCheckpoint - 1) % 5 === 0
            }
        } : {}),
        ...(terminal && recoveryInventory ? { recovery: summarizeRecovery(recoveryInventory) } : {}),
        ...(report ? { roundReport: summarizeRoundFeedback(report, terminal) } : {}),
        ...(insight ? { roundInsights: summarizeRoundFeedback(insight, true) } : {}),
        ...(result.statusReason !== undefined && !report && !insight ? { telemetryAvailable: false } : {}),
        ...(currentObservation ? {
            ...(currentObservation.scheduledAbilities !== undefined
                ? { scheduledAbilities: pendingOrFailedAbilities(currentObservation.scheduledAbilities) } : {}),
            ...(currentObservation.scheduledUpgrades !== undefined
                ? { scheduledUpgrades: pendingOrFailedUpgrades(currentObservation.scheduledUpgrades) } : {}),
            ...(currentObservation.scheduledGeraldoPurchases !== undefined
                ? { scheduledGeraldoPurchases: pendingOrFailedGeraldoPurchases(currentObservation.scheduledGeraldoPurchases) } : {}),
            ...(currentObservation.scheduledCorvusActions !== undefined
                ? { scheduledCorvusActions: pendingOrFailedCorvusActions(currentObservation.scheduledCorvusActions) } : {}),
            ...(currentObservation.bosses !== undefined ? { bosses: currentObservation.bosses } : {}),
            // Defeat reports already carry pre-cleanup threats; a live timeout still needs target coordinates.
            ...(result.statusReason === 'timeout' ? {
                activeThreats: currentObservation.activeThreats,
                threatSnapshotSource: currentObservation.threatSnapshotSource
            } : {})
        } : {})
    };
}
