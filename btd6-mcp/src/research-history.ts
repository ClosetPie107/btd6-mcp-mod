import { createReadStream } from 'node:fs';
import { access } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { join } from 'node:path';

export interface ResearchHistoryOptions {
    offset?: number;
    limit?: number;
    actionLimit?: number;
    maxBranches?: number;
}

export interface ResearchHistoryChange {
    actionId: number;
    action: string;
    kind: 'purchase' | 'target' | 'timing' | 'other';
    timestamp: string;
    input: unknown;
    result?: unknown;
    error?: unknown;
}

export interface ResearchHistoryBoundary {
    actionId?: number;
    action?: string;
    kind: 'session_start' | 'checkpoint_saved' | 'checkpoint_restored' | 'match_boundary';
    timestamp?: string;
    input?: unknown;
    result?: unknown;
    error?: unknown;
}

export interface ResearchHistoryOutcome {
    actionId: number;
    action: string;
    timestamp: string;
    round: number | null;
    status: string;
    completed: boolean | null;
    cash: number | null;
    lives: number | null;
    triggeringLeak: unknown | null;
    remainingWorkload: Record<string, unknown> | null;
}
export interface ResearchHistoryUnresolvedAction {
    actionId: number;
    action: string;
    timestamp: string;
    input: unknown;
    lastObserved?: unknown;
    towerBefore?: unknown;
}


export interface ResearchHistoryBranch {
    branchId: number;
    attempt: number;
    matchGroup: number;
    restoredFrom: ResearchHistoryBoundary | null;
    boundaries: ResearchHistoryBoundary[];
    omittedBoundaries: number;
    changes: ResearchHistoryChange[];
    omittedChanges: number;
    outcomes: ResearchHistoryOutcome[];
    omittedOutcomes: number;
    unresolvedActions: ResearchHistoryUnresolvedAction[];
    omittedUnresolvedActions: number;
}

export interface RepeatedResearchFailure {
    key: string;
    count: number;
    branches: number[];
    actionIds: number[];
}

export interface ResearchHistory {
    schemaVersion: 1;
    sessionId: string;
    readEvidenceEnabled: boolean | null;
    offset: number;
    limit: number;
    totalBranches: number;
    nextOffset: number | null;
    truncated: boolean;
    incomplete: boolean;
    malformedLines: number;
    branches: ResearchHistoryBranch[];
    repeatedFailures: RepeatedResearchFailure[];
    repeatedFailuresOmitted: number;
}


type Data = Record<string, unknown>;
const object = (value: unknown): Data => value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Data : {};
const isData = (value: unknown): value is Data => value !== null && typeof value === 'object' && !Array.isArray(value);
const safeSessionId = (sessionId: string): boolean => /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(sessionId);

// History is intentionally compact. It is a factual view, not a model-authored explanation.
function compact(value: unknown, depth = 0): unknown {
    if (value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') return value;
    if (depth >= 3) return '[omitted]';
    if (Array.isArray(value)) return value.slice(0, 16).map(item => compact(item, depth + 1));
    if (!isData(value)) return String(value);
    const result: Data = {};
    for (const [key, item] of Object.entries(value).slice(0, 48)) {
        if (['observation', 'towers', 'abilities', 'bloons', 'pressureByLane', 'topThreats'].includes(key)) continue;
        result[key] = compact(item, depth + 1);
    }
    return result;
}

function actionKind(action: string): ResearchHistoryChange['kind'] {
    if (['placeTower', 'upgradeTower', 'sellTower', 'removeObstacle', 'useGeraldoItem', 'mergeBeast', 'collectBank'].includes(action)) return 'purchase';
    if (['setTargetPriority', 'setTowerTargetPosition', 'setCorvusSpellTarget', 'setCorvusSpell'].includes(action)) return 'target';
    if (['setGameSpeed', 'setRound', 'pauseMatch', 'resumeMatch', 'scheduleAbility', 'scheduleUpgrade',
        'scheduleGeraldoPurchase', 'scheduleCorvusAction', 'cancelScheduledAbility', 'cancelScheduledUpgrade',
        'cancelScheduledGeraldoPurchase', 'cancelScheduledCorvusAction'].includes(action)) return 'timing';
    return 'other';
}

function numberOrNull(value: unknown): number | null {
    return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function roundOutcome(action: string, record: Data): ResearchHistoryOutcome | null {
    if (!['startRound', 'waitForRoundEnd', 'advanceRound'].includes(action)) return null;
    const result = object(record.result);
    const report = object(result.roundReport);
    const statusReason = typeof result.statusReason === 'string' ? result.statusReason : undefined;
    const reportStatus = typeof report.status === 'string' ? report.status : undefined;
    if (action !== 'advanceRound' && statusReason === undefined && reportStatus === undefined) return null;
    const status = statusReason ?? reportStatus ?? (result.advanced === true ? 'round_cleared' : 'not_advanced');
    const completed = typeof result.completed === 'boolean' ? result.completed
        : status === 'round_cleared' || status === 'victory' || status === 'completed' || result.advanced === true ? true
            : status === 'defeat' || status === 'timeout' || status === 'ui_blocked' ? false : null;
    const leaks = object(report.leaks);
    const leakEntries = Array.isArray(leaks.entries) ? leaks.entries : [];
    const lives = numberOrNull(result.lives ?? object(result.summary).lives);
    let lethalLeak: unknown;
    if (status === 'defeat' && lives !== null && lives <= 0 && leaks.omitted === 0 && leaks.total === leakEntries.length) {
        for (let index = leakEntries.length - 1; index >= 0; index--) {
            if ((numberOrNull(object(leakEntries[index]).actualHealthLoss) ?? 0) > 0) {
                lethalLeak = leakEntries[index];
                break;
            }
        }
    }
    const workload: Data = {};
    for (const key of ['remainingScheduledSpawns', 'estimatedRemainingScheduledSpawns', 'spawningComplete', 'activeBloonCount', 'activeMoabCount', 'omittedScheduleGroups']) {
        if (result[key] !== undefined) workload[key] = compact(result[key]);
        else if (report[key] !== undefined) workload[key] = compact(report[key]);
    }
    return {
        actionId: typeof record.actionId === 'number' ? record.actionId : 0,
        action,
        timestamp: typeof record.timestamp === 'string' ? record.timestamp : '',
        round: numberOrNull(result.roundCompleted ?? result.completedRound ?? result.round ?? report.round),
        status,
        completed,
        cash: numberOrNull(result.cash ?? object(result.summary).cash),
        lives,
        triggeringLeak: lethalLeak === undefined ? null : compact(lethalLeak),
        remainingWorkload: Object.keys(workload).length ? workload : null
    };
}

function failureKey(action: string, record: Data, outcome: ResearchHistoryOutcome | null): string | null {
    const result = object(record.result);
    const error = object(record.error);
    const failure = object(result.failure);
    const status = outcome?.status ?? '';
    const actionStatus = typeof result.status === 'string' ? result.status : '';
    const explicitFailure = record.event === 'action_error'
        || Object.keys(failure).length > 0
        || ['defeat', 'timeout', 'ui_blocked', 'failed', 'expired', 'cancelled'].includes(status)
        || ['failed', 'expired', 'cancelled'].includes(actionStatus);
    if (!explicitFailure) return null;
    const round = outcome?.round ?? numberOrNull(object(record.lastObserved).round);
    const code = typeof failure.code === 'string' ? failure.code : typeof error.code === 'string' ? error.code : status || actionStatus || 'failed';
    return `${action}:${round ?? 'unknown'}:${code}`;
}

type MutableBranch = ResearchHistoryBranch;

function newBranch(branchId: number, attempt: number, matchGroup: number, restoredFrom: ResearchHistoryBoundary | null): MutableBranch {
    return {
        branchId,
        attempt,
        matchGroup,
        restoredFrom,
        boundaries: restoredFrom ? [restoredFrom] : [{ kind: 'session_start' }],
        omittedBoundaries: 0,
        changes: [],
        omittedChanges: 0,
        outcomes: [],
        omittedOutcomes: 0,
        unresolvedActions: [],
        omittedUnresolvedActions: 0
    };
}

function appendBoundary(branch: MutableBranch, boundary: ResearchHistoryBoundary, limit: number): void {
    if (branch.boundaries.length >= limit) {
        branch.boundaries.shift();
        branch.omittedBoundaries++;
    }
    branch.boundaries.push(boundary);
}

function toBoundary(record: Data, kind: ResearchHistoryBoundary['kind']): ResearchHistoryBoundary {
    return {
        actionId: typeof record.actionId === 'number' ? record.actionId : undefined,
        action: typeof record.action === 'string' ? record.action : undefined,
        kind,
        timestamp: typeof record.timestamp === 'string' ? record.timestamp : undefined,
        input: compact(record.input),
        result: record.result === undefined ? undefined : compact(record.result),
        error: record.error === undefined ? undefined : compact(record.error)
    };
}
function appendUnresolvedAction(branch: MutableBranch, record: Data, limit: number): void {
    if (branch.unresolvedActions.length >= limit) {
        branch.unresolvedActions.shift();
        branch.omittedUnresolvedActions++;
    }
    const unresolved: ResearchHistoryUnresolvedAction = {
        actionId: typeof record.actionId === 'number' ? record.actionId : 0,
        action: typeof record.action === 'string' ? record.action : '',
        timestamp: typeof record.timestamp === 'string' ? record.timestamp : '',
        input: record.input === undefined ? undefined : compact(record.input),
        ...(record.lastObserved === undefined ? {} : { lastObserved: compact(record.lastObserved) }),
        ...(record.towerBefore === undefined ? {} : { towerBefore: compact(record.towerBefore) })
    };
    branch.unresolvedActions.push(unresolved);
}

function restoreSucceeded(record: Data): boolean {
    return object(record.result).restored === true;
}

function matchBoundarySucceeded(action: string, record: Data): boolean {
    const result = object(record.result);
    if (action === 'startMatch') return result.started === true;
    if (action === 'restartMatch') return result.restarted === true;
    if (action === 'quitMatch') return result.quit === true;
    if (action === 'respondUi') return result.accepted === true && ['restart', 'home'].includes(String(object(record.input).action));
    return false;
}

function addAction(branch: MutableBranch, record: Data, actionLimit: number, boundaryLimit: number): string | null {
    const action = typeof record.action === 'string' ? record.action : '';
    if (!action || typeof record.actionId !== 'number') return null;
    const outcome = roundOutcome(action, record);
    if (outcome) {
        if (branch.outcomes.length >= actionLimit) {
            branch.outcomes.shift();
            branch.omittedOutcomes++;
        }
        branch.outcomes.push(outcome);
    }
    const key = failureKey(action, record, outcome);
    if (action === 'saveCheckpoint') appendBoundary(branch, toBoundary(record, 'checkpoint_saved'), boundaryLimit);
    const isOutcomeAction = ['startRound', 'waitForRoundEnd', 'advanceRound'].includes(action);
    if (!isOutcomeAction && (actionKind(action) !== 'other' || action === 'setRound')) {
        if (branch.changes.length >= actionLimit) {
            branch.changes.shift();
            branch.omittedChanges++;
        }
        branch.changes.push({
            actionId: record.actionId,
            action,
            kind: actionKind(action),
            timestamp: typeof record.timestamp === 'string' ? record.timestamp : '',
            input: compact(record.input),
            ...(record.result === undefined ? {} : { result: compact(record.result) }),
            ...(record.error === undefined ? {} : { error: compact(record.error) })
        });
    }
    return key;
}
/** Read a bounded, factual branch view from one explicitly selected journal session. */
export async function readResearchHistory(
    directory: string,
    sessionId: string,
    options: ResearchHistoryOptions = {}
): Promise<ResearchHistory> {
    if (!safeSessionId(sessionId)) throw new Error('Unsafe research history session id.');
    const rawOffset = options.offset ?? 0;
    const rawLimit = options.limit ?? 20;
    const rawActionLimit = options.actionLimit ?? 100;
    const rawMaxBranches = options.maxBranches ?? 256;
    const offset = Number.isFinite(rawOffset) ? Math.max(0, Math.floor(rawOffset)) : 0;
    const limit = Number.isFinite(rawLimit) ? Math.min(100, Math.max(1, Math.floor(rawLimit))) : 20;
    const actionLimit = Number.isFinite(rawActionLimit) ? Math.min(1000, Math.max(1, Math.floor(rawActionLimit))) : 100;
    const boundaryLimit = Math.min(actionLimit, 100);
    const maxBranchRequest = Number.isFinite(rawMaxBranches) ? Math.floor(rawMaxBranches) : 256;
    const maxBranches = Math.min(2048, Math.max(limit + offset + 1, maxBranchRequest));
    const path = join(directory, `${sessionId}.jsonl`);
    await access(path);
    const input = createReadStream(path, { encoding: 'utf8' });
    const lines = createInterface({ input, crlfDelay: Infinity });
    const branches: MutableBranch[] = [];
    const failures = new Map<string, { key: string; count: number; branches: Set<number>; actionIds: number[] }>();
    const pending = new Map<number, { record: Data; branch: MutableBranch }>();
    let branch = newBranch(0, 0, 0, null);
    branches.push(branch);
    let truncated = false;
    let malformedLines = 0;
    let events = 0;
    let omittedFailures = 0;
    let readEvidenceEnabled: boolean | null = null;
    let omittedPending = 0;
    try {
        for await (const line of lines) {
            if (!line.trim()) continue;
            let parsed: unknown;
            try {
                parsed = JSON.parse(line);
            } catch {
                malformedLines++;
                truncated = true;
                continue;
            }
            if (!isData(parsed)) {
                malformedLines++;
                continue;
            }
            events++;
            if (events > 100_000) {
                truncated = true;
                break;
            }
            const event = parsed.event;
            const actionId = parsed.actionId;
            if (event === 'session_started') {
                branch.boundaries[0] = { kind: 'session_start', timestamp: typeof parsed.timestamp === 'string' ? parsed.timestamp : undefined };
                readEvidenceEnabled = typeof parsed.readEvidenceEnabled === 'boolean' ? parsed.readEvidenceEnabled : null;
                continue;
            }
            if (event === 'action_requested' && typeof actionId === 'number') {
                const previous = pending.get(actionId);
                if (previous) {
                    previous.branch.omittedUnresolvedActions++;
                    omittedPending++;
                }
                pending.set(actionId, { record: parsed, branch });
                if (pending.size > 10_000) {
                    const first = pending.keys().next().value;
                    if (typeof first === 'number') {
                        const dropped = pending.get(first);
                        if (dropped) {
                            dropped.branch.omittedUnresolvedActions++;
                            omittedPending++;
                        }
                        pending.delete(first);
                    }
                    truncated = true;
                }
                continue;
            }
            if ((event !== 'action_returned' && event !== 'action_error') || typeof actionId !== 'number') continue;
            const pendingAction = pending.get(actionId);
            pending.delete(actionId);
            if (!pendingAction) continue;
            const requested = pendingAction.record;
            const record: Data = { ...requested, ...parsed, event };
            const key = addAction(branch, record, actionLimit, boundaryLimit);
            if (key) {
                const internalKey = `${branch.matchGroup}:${key}`;
                const failure = failures.get(internalKey);
                if (failure) {
                    failure.count++;
                    failure.branches.add(branch.branchId);
                    if (failure.actionIds.length < 32) failure.actionIds.push(actionId);
                } else if (failures.size < 4096) {
                    failures.set(internalKey, { key, count: 1, branches: new Set([branch.branchId]), actionIds: [actionId] });
                } else {
                    omittedFailures++;
                }
            }
            const action = typeof parsed.action === 'string' ? parsed.action : '';
            if (action === 'restoreCheckpoint') {
                const boundary = toBoundary(record, 'checkpoint_restored');
                appendBoundary(branch, boundary, boundaryLimit);
                if (restoreSucceeded(record)) {
                    if (branches.length >= maxBranches) {
                        truncated = true;
                        break;
                    }
                    const nextId = branches.length;
                    branch = newBranch(nextId, nextId, branch.matchGroup, boundary);
                    branches.push(branch);
                }
                continue;
            }
            if (['startMatch', 'restartMatch', 'quitMatch'].includes(action)
                || (action === 'respondUi' && ['restart', 'home'].includes(String(object(record.input).action)))) {
                const boundary = toBoundary(record, 'match_boundary');
                appendBoundary(branch, boundary, boundaryLimit);
                if (matchBoundarySucceeded(action, record)) {
                    const nextMatchGroup = branch.matchGroup + 1;
                    if (action === 'startMatch' && branch.branchId === 0 && branch.changes.length === 0 && branch.outcomes.length === 0) {
                        branch.matchGroup = nextMatchGroup;
                    } else {
                        if (branches.length >= maxBranches) {
                            truncated = true;
                            break;
                        }
                        const nextId = branches.length;
                        branch = newBranch(nextId, nextId, nextMatchGroup, null);
                        appendBoundary(branch, boundary, boundaryLimit);
                        branches.push(branch);
                    }
                }
            }
        }
    } finally {
        lines.close();
        input.destroy();
    }
    for (const pendingAction of pending.values()) {
        appendUnresolvedAction(pendingAction.branch, pendingAction.record, actionLimit);
    }
    const unresolvedCount = pending.size + omittedPending;
    const orderedBranches = [...branches].reverse();
    const selected = orderedBranches.slice(offset, offset + limit).map(item => ({
        branchId: item.branchId,
        attempt: item.attempt,
        matchGroup: item.matchGroup,
        restoredFrom: item.restoredFrom,
        boundaries: item.boundaries,
        omittedBoundaries: item.omittedBoundaries,
        changes: item.changes,
        omittedChanges: item.omittedChanges,
        outcomes: item.outcomes,
        omittedOutcomes: item.omittedOutcomes,
        unresolvedActions: item.unresolvedActions,
        omittedUnresolvedActions: item.omittedUnresolvedActions
    }));
    const repeated = [...failures.values()]
        .filter(value => value.count > 1 && value.branches.size > 1)
        .map(value => ({ key: value.key, count: value.count, branches: [...value.branches].sort((a, b) => a - b), actionIds: value.actionIds }));
    const repeatedFailures = repeated.slice(0, 128);
    return {
        schemaVersion: 1,
        sessionId,
        readEvidenceEnabled,
        offset,
        limit,
        totalBranches: branches.length,
        nextOffset: offset + selected.length < branches.length ? offset + selected.length : null,
        truncated,
        incomplete: unresolvedCount > 0,
        malformedLines,
        branches: selected,
        repeatedFailures,
        repeatedFailuresOmitted: omittedFailures + Math.max(0, repeated.length - repeatedFailures.length)
    };
}
