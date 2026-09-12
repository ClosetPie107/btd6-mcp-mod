import assert from 'node:assert/strict';
import { test } from 'node:test';
import type {
    BridgeObservation, ProjectedCashResult, TowerCatalog, WaitForRoundEndResult
} from '../src/bridge-client.js';
import type { FindPlacementSpotsResult, RoundInsights } from '../src/bridge-data.js';
import { formatPlacementSpotsOutput, formatProjectedCashOutput, formatTowerCatalogOutput } from '../src/intelligence-output.js';
import { formatObservationOutput, formatRoundOutput } from '../src/play-output.js';

const catalog: TowerCatalog = {
    source: 'live-game-model', btd6Version: '56.3',
    towers: [
        { towerType: 'MonkeySub', name: 'Monkey Sub', baseCost: 325, baseRange: 42,
            isWaterBased: true, isAmphibious: false,
            upgrades: [{ upgradeId: 'LongerRange', path: 0, tier: 1, cost: 140, name: 'Longer Range' }] },
        { towerType: 'DartMonkey', name: 'Dart Monkey', baseCost: 200, baseRange: 32,
            isWaterBased: false, isAmphibious: false, upgrades: [] }
    ]
};

test('catalog filter resolves native IDs without widening a miss to the whole roster', () => {
    const selected = formatTowerCatalogOutput(catalog, 'summary', { towerType: 'monkeysub' });
    assert.deepEqual(selected.towers.map(tower => tower.towerType), ['MonkeySub']);
    assert.deepEqual(selected.towers[0]?.upgrades, catalog.towers[0]?.upgrades);
    const missing = formatTowerCatalogOutput(catalog, 'full', { towerType: 'MonkeySbu' });
    assert.deepEqual(missing.towers, []);
    assert('matched' in missing && missing.matched === false);
    assert('availableTowerTypes' in missing);
    assert.deepEqual(missing.availableTowerTypes, ['MonkeySub', 'DartMonkey']);
    assert.deepEqual(catalog.towers.map(tower => tower.towerType), ['MonkeySub', 'DartMonkey']);
});

test('cash summary preserves unavailable budgets and excluded income rather than inventing spendable cash', () => {
    const projection: ProjectedCashResult = {
        fromRound: 140, targetRound: 141, startingCash: 1234.00000001,
        confidence: 'unsupported', assumptions: ['No future spending'], unsupportedReasons: ['Generated wave'],
        baselineIncome: { confidence: 'unsupported', popCash: null, roundRewards: null, total: null,
            cashAtStartOfTargetRound: null, cashAtEndOfTargetRound: null },
        towerIncome: { confidence: 'estimated', estimatedTotal: 0, assumptions: ['No bank withdrawals'],
            towers: [{ towerId: 'bank-1', towerType: 'BananaFarm', kind: 'bank', perRoundEstimate: null,
                included: false, reason: 'Storage is not spendable income' }] },
        estimatedCashAtStartOfTargetRound: null, estimatedCashAtEndOfTargetRound: null,
        breakdown: [{ round: 140, incomeMultiplier: 0.02, popCash: null, roundRewards: null,
            towerIncomeEstimate: 0, baselineIncome: null, estimatedCashAtEnd: null }],
        incomeThresholds: [{ lastRound: 150, multiplier: 0.02 }], finalIncomeMultiplier: 0.02
    };
    const summary = formatProjectedCashOutput(projection, 'summary');
    assert.equal(summary.startingCash, 1234.00000001);
    assert.equal(summary.estimatedCashAtEndOfTargetRound, null);
    assert.equal(summary.baselineIncome.confidence, 'unsupported');
    assert.deepEqual(summary.unsupportedReasons, ['Generated wave']);
    assert.equal(summary.towerIncome.towers[0]?.included, false);
    assert.equal(summary.towerIncome.towers[0]?.perRoundEstimate, null);
    assert.equal('breakdown' in summary, false);
    assert.deepEqual(formatProjectedCashOutput(projection, 'full'), projection);
});

const priorReport: RoundInsights = {
    matchId: 'match-1', matchGeneration: 1, round: 61, status: 'completed',
    observedAtUtc: '2026-09-08T12:00:00.000Z', nativeElapsedSeconds: 20, lastSpawnDurationSeconds: 18,
    remainingScheduledSpawns: 0, remainingScheduledSpawnsKnown: true, estimatedRemainingScheduledSpawns: 0,
    spawningComplete: true, omittedScheduleGroups: 0, activeBloonCount: 0, activeMoabCount: 0,
    maxTrackProgress: null, peakBloonCount: 25, peakMoabCount: 0, peakNearExitCount: 0,
    nearExitSampledDurationSeconds: 0, samples: 200,
    postSpawnPressure: {
        available: true, basis: 'sampled_after_native_spawn_schedule_end',
        sampleCount: 20, observedDurationSeconds: 2,
        moabPresentDurationSeconds: 1.7, ceramicPresentDurationSeconds: 0.4,
        peakMoabCount: 3, peakCeramicCount: 12,
        peakNearExitMoabCount: 1, peakNearExitCeramicCount: 8,
        maxMoabProgress: 0.91, maxCeramicProgress: 0.98
    },
    coverage: { requestedIntervalSeconds: 0.1, sampleCount: 200, missingSampleCount: 0,
        firstSampleElapsedSeconds: 0, lastSampleElapsedSeconds: 20, nativeClockAvailable: true,
        samplingBasis: 'native_simulation_elapsed', completeness: 'sampled', missing: [],
        leakCapture: 'native_pre_post_hooks', expectedDamageBasis: 'native_health_delta' },
    worstMoment: null, composition: [], omittedCompositionGroups: 0, activeThreats: [], topThreats: [], pressureByLane: [],
    leaks: { total: 0, retained: 0, omitted: 0, entries: [] }, actions: []
};

const activeObservation: BridgeObservation = {
    observedAtUtc: '2026-09-08T12:00:30.000Z', activeGame: true,
    ui: { ready: true, autoHandlingEnabled: true, blocker: null }, gameStatus: 'in_game',
    round: 62, endRound: 100, roundActive: true, inBetweenRounds: false, canStartRound: false,
    fastForward: true, autoPlay: false, cash: 200.0000001, lives: 1, maxLives: 1,
    towerCount: 0, mapId: 'Logs', mapName: 'Logs', mode: 'CHIMPS', difficulty: 'Hard', towers: [], hero: null, abilities: [],
    roundReport: priorReport, roundInsights: priorReport,
    activeThreats: [{ type: 'Ddt', isFortified: false, isCamo: true, isMoab: true,
        trackProgress: 0.9, pathIndex: 0, health: 300, x: 70.25, y: -12.5 }]
};

test('timeout cannot reuse an earlier clear as evidence of current safety', () => {
    const result: WaitForRoundEndResult = {
        completed: false, timedOut: true, statusReason: 'timeout', ui: activeObservation.ui,
        round: 62, roundCompleted: null, gameStatus: 'in_game', cash: activeObservation.cash, lives: 1,
        summary: { round: 62, roundCompleted: null, cash: activeObservation.cash, lives: 1,
            towerCount: 0, totalPops: 100, totalDamage: 100 },
        observation: activeObservation
    };
    const output = formatRoundOutput(result, 'summary');
    assert.equal(output.statusReason, 'timeout');
    assert.equal(output.completed, false);
    assert.equal('roundReport' in output, false);
    assert.equal('roundInsights' in output, false);
    assert('telemetryAvailable' in output && output.telemetryAvailable === false);
    assert('activeThreats' in output);
    assert.equal(output.activeThreats?.[0]?.x, 70.25);
    assert.equal('summary' in output, false);
});

test('observation summary keeps live targeting coordinates but does not repeat stale between-round threats', () => {
    const live = formatObservationOutput(activeObservation, 'summary');
    assert.equal(live.activeThreats?.[0]?.y, -12.5);
    assert.equal('roundReport' in live, false);
    assert.equal('roundInsights' in live, false);
    const idle = formatObservationOutput({ ...activeObservation, roundActive: false, inBetweenRounds: true }, 'summary');
    assert.equal('activeThreats' in idle, false);
    assert.deepEqual(formatObservationOutput(activeObservation, 'full'), activeObservation);
});

test('round output stays compact and labels automatic checkpoint round semantics', () => {
    const observation = {
        ...activeObservation,
        round: 63,
        roundActive: false,
        inBetweenRounds: true,
        availableCheckpoints: [6, 11, 63]
    };
    const result: WaitForRoundEndResult = {
        completed: true,
        timedOut: false,
        statusReason: 'round_cleared',
        ui: observation.ui,
        round: 63,
        roundCompleted: 62,
        gameStatus: 'in_game',
        cash: 500,
        lives: 1,
        summary: { round: 63, roundCompleted: 62, cash: 500, lives: 1, towerCount: 0, totalPops: 200, totalDamage: 200 },
        observation
    };
    const output = formatRoundOutput(result, 'full');
    assert.equal('observation' in output, false);
    assert.deepEqual(output.checkpoint, {
        nextPlayableRound: 63,
        lastCompletedRound: 62,
        periodicAnchor: false
    });
});

test('defeat output distinguishes post-spawn blimp pressure and supplies exact recovery selectors', () => {
    const report: RoundInsights = {
        ...priorReport,
        status: 'defeat',
        leaks: {
            total: 1, retained: 1, omitted: 0,
            entries: [{ baseId: 'Ceramic', camo: false, fortified: false, regrow: false,
                lead: false, moab: false, pathIndex: 0, trackProgress: 1, x: 0, y: 0,
                expectedDamage: 104, actualHealthLoss: 104, elapsedSeconds: 22 }]
        }
    };
    const observation = {
        ...activeObservation,
        gameStatus: 'defeat' as const,
        round: 61,
        roundActive: false,
        inBetweenRounds: false,
        roundReport: report,
        roundInsights: report
    };
    const result: WaitForRoundEndResult = {
        completed: false, timedOut: false, statusReason: 'defeat', ui: observation.ui,
        round: 61, roundCompleted: null, gameStatus: 'defeat', cash: 0, lives: 0,
        summary: { round: 61, roundCompleted: null, cash: 0, lives: 0,
            towerCount: 0, totalPops: 100, totalDamage: 100 },
        observation,
        roundReport: report
    };
    const output = formatRoundOutput(result, 'summary', {
        availableRoundCheckpoints: [6, 11, 56],
        customCheckpoints: ['before-60'],
        importedCheckpoints: [{ checkpointId: 'import-1', label: 'baseline', round: 51 }]
    });
    assert.deepEqual(output.roundReport?.postSpawnPressure, report.postSpawnPressure);
    assert.deepEqual(output.recovery?.latest?.selector, { round: 56 });
    assert.deepEqual(output.recovery?.earlierRoundAnchors.map(anchor => anchor.selector), [{ round: 11 }, { round: 6 }]);
    assert.deepEqual(output.recovery?.custom[0]?.selector, { label: 'before-60' });
    assert.deepEqual(output.recovery?.imported[0]?.selector, { checkpointId: 'import-1' });
});

test('placement summary drops interval arrays but keeps support coverage decisions', () => {
    const result: FindPlacementSpotsResult = {
        towerType: 'MonkeyVillage',
        totalFound: 1,
        returned: 1,
        coverageTargets: [{ id: 'tower-1', towerType: 'DartMonkey', position: { x: 1, y: 2 } }],
        coverageBasis: 'candidate_model_range_center_distance',
        eligibilityVerified: false,
        spots: [{
            x: 5,
            y: 6,
            distanceToTrack: 4,
            coveredTowerIds: ['tower-1'],
            uncoveredTowerIds: [],
            recipientCount: 1,
            supportRange: 40,
            trackCoverage: {
                rangeRadius: 40,
                uniqueTrackLength: 88,
                angularCoverageDegrees: 170,
                detailsOmitted: false,
                pathCount: 1,
                intervalCount: 1,
                paths: [{ pathIndex: 0, inRangeLength: 88, intervals: [{ startProgress: 0.1, endProgress: 0.4, length: 88 }] }]
            }
        }],
        observedAtUtc: '2026-09-08T12:00:00.000Z',
        geometryRevision: 'geometry-1',
        note: 'geometric'
    };
    const summary = formatPlacementSpotsOutput(result, 'summary');
    assert.equal(summary.spots[0]?.trackCoverage?.uniqueTrackLength, 88);
    assert.equal('paths' in (summary.spots[0]?.trackCoverage ?? {}), false);
    assert.deepEqual(summary.spots[0]?.coveredTowerIds, ['tower-1']);
    assert.deepEqual(formatPlacementSpotsOutput(result, 'full'), result);
});
