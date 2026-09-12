import { appendFileSync, mkdirSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { BridgeClient, BridgeClientError } from './bridge-client.js';
import { readResearchHistory, type ResearchHistory, type ResearchHistoryOptions } from './research-history.js';

export { readResearchHistory };
export type { ResearchHistory, ResearchHistoryOptions } from './research-history.js';

const reads: Record<string, true> = {
    status: true, observe: true, roundProgress: true, performance: true, canPlaceTower: true,
    towerCatalog: true, towerStats: true, inspectTower: true, getMapLayout: true,
    bossCatalog: true, inspectBoss: true,
    inspectObstacles: true, inspectTowerMicro: true, inspectBeastMerges: true,
    findPlacementSpots: true, getRoundInfo: true, getProjectedCash: true, listCheckpoints: true,
    inspectGeraldo: true, canUseGeraldoItem: true, inspectCorvus: true,
    projectParagonDegree: true, inspectTempleSacrifices: true, inspectMonkeyopolisSacrifices: true
};
const boundaries: Record<string, true> = {
    startMatch: true, restartMatch: true, quitMatch: true, ensureMainMenu: true, restoreCheckpoint: true
};
type Data = Record<string, unknown>;
const object = (value: unknown): Data => value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Data : {};

// Keep gameplay facts, not full simulation snapshots or rendered images.
function compact(value: unknown): Data {
    const data = object(value);
    const result: Data = {};
    for (const [key, item] of Object.entries(data)) {
        if (['observation', 'towers', 'abilities', 'bloons'].includes(key)) continue;
        if (key === 'tower' && item !== null) {
            const tower = object(item);
            result.tower = Object.fromEntries(['id', 'towerType', 'name', 'position', 'tiers', 'targetPriority']
                .filter(field => tower[field] !== undefined).map(field => [field, tower[field]]));
        } else {
            result[key] = item;
        }
    }
    return result;
}

export interface ActionJournalOptions {
    /** Override BTD6_AGENT_BRIDGE_RETAIN_READS for isolated tests or an embedding host. */
    retainReads?: boolean;
    /** Inline JSON payload limit. Larger payloads are immutable artifact references. */
    maxInlineBytes?: number;
    /** Injectable timestamp source for deterministic journal tests. */
    clock?: () => Date;
    /** Injectable session id for deterministic tests; normal sessions use a UUID. */
    sessionId?: string;
}

export interface JournalArtifactReference {
    artifactId: string;
    file: string;
    bytes: number;
    sha256: string;
    encoding: 'json';
}

export interface ReadEvidenceEntry {
    schemaVersion: 1;
    evidenceId: string;
    sessionId: string;
    timestamp: string;
    startedAt: string;
    event: 'read_requested' | 'read_returned' | 'read_error';
    source: 'bridge' | 'mcp';
    action: string;
    context: Data;
    args?: unknown;
    response?: unknown;
    error?: unknown;
}

const defaultSessionId = (): string => `${new Date().toISOString().replace(/[:.]/g, '-')}-${randomUUID()}`;

/** Append-only journal files per adapter session. Never writes MCP stdout. */
export class ActionJournal {
    readonly sessionId: string;
    readonly basePath: string;
    readonly directory: string;
    readonly sessionDirectory: string;
    private readonly artifactDirectory: string;
    readonly retainReads: boolean;
    private readonly maxInlineBytes: number;
    private readonly clock: () => Date;
    private sequence = 0;
    private disabled = false;
    private evidenceDisabled = false;
    private context: Data = {};
    private towers = new Map<string, Data>();

    constructor(directory = fileURLToPath(new URL('../logs/', import.meta.url)), options: ActionJournalOptions = {}) {
        const sessionId = options.sessionId ?? defaultSessionId();
        if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(sessionId)) throw new Error('Unsafe action journal session id.');
        const maxInlineBytes = options.maxInlineBytes ?? 16 * 1024;
        if (!Number.isFinite(maxInlineBytes) || maxInlineBytes <= 0) throw new Error('maxInlineBytes must be finite and positive.');
        this.sessionId = sessionId;
        this.directory = directory;
        this.basePath = join(directory, this.sessionId);
        this.sessionDirectory = join(directory, this.sessionId);
        this.artifactDirectory = join(this.sessionDirectory, 'artifacts');
        this.retainReads = options.retainReads ?? process.env.BTD6_AGENT_BRIDGE_RETAIN_READS === '1';
        this.maxInlineBytes = Math.floor(maxInlineBytes);
        this.clock = options.clock ?? (() => new Date());
        try {
            mkdirSync(directory, { recursive: true, mode: 0o700 });
            if (this.retainReads) mkdirSync(this.sessionDirectory, { recursive: true, mode: 0o700 });
            this.record({ event: 'session_started', pid: process.pid, readEvidenceEnabled: this.retainReads });
            if (!this.disabled) console.error(`BTD6 action journal: ${this.basePath}.log`);
        } catch (error) {
            this.disable(error);
        }
    }

    private timestamp(): string {
        return this.clock().toISOString();
    }

    private disable(error: unknown): void {
        this.disabled = true;
        console.error(`BTD6 action journal disabled; gameplay outcomes are unchanged: ${String(error)}`);
    }

    private disableEvidence(error: unknown): void {
        this.evidenceDisabled = true;
        console.error(`BTD6 read evidence disabled; gameplay outcomes are unchanged: ${String(error)}`);
    }

    private record(data: Data): void {
        if (this.disabled) return;
        try {
            const entry = { schemaVersion: 1, sessionId: this.sessionId, timestamp: this.timestamp(), ...data };
            appendFileSync(`${this.basePath}.jsonl`, `${JSON.stringify(entry)}\n`, { mode: 0o600 });
            const label = data.action ? `#${data.actionId} ${data.action}` : 'session';
            const context = object(data.lastObserved);
            const round = context.round === undefined ? '' : ` | Last observed round ${context.round}`;
            const facts = data.result ?? data.error ?? data.input ?? {};
            const tower = data.towerBefore ? ` | Tower ${JSON.stringify(data.towerBefore)}` : '';
            appendFileSync(`${this.basePath}.log`,
                `${entry.timestamp} | ${label} | ${data.event}${round}${tower} | ${JSON.stringify(facts)}\n`, { mode: 0o600 });
        } catch (error) {
            // A journal failure must never turn a completed purchase into a retryable error.
            this.disable(error);
        }
    }

    private artifact(value: unknown): unknown {
        const encoded = JSON.stringify(value);
        if (encoded === undefined || Buffer.byteLength(encoded, 'utf8') <= this.maxInlineBytes) return value;
        mkdirSync(this.artifactDirectory, { recursive: true, mode: 0o700 });
        const artifactId = randomUUID();
        const target = join(this.artifactDirectory, `${artifactId}.json`);
        const temporary = join(this.artifactDirectory, `.${artifactId}.${randomUUID()}.tmp`);
        try {
            writeFileSync(temporary, encoded, { encoding: 'utf8', mode: 0o600, flag: 'wx' });
            renameSync(temporary, target);
        } catch (error) {
            try { unlinkSync(temporary); } catch { /* Preserve the original logging failure. */ }
            throw error;
        }
        return {
            artifactId,
            file: `artifacts/${artifactId}.json`,
            bytes: Buffer.byteLength(encoded, 'utf8'),
            sha256: createHash('sha256').update(encoded).digest('hex'),
            encoding: 'json'
        } satisfies JournalArtifactReference;
    }

    private structuredError(error: unknown): Data {
        if (error instanceof BridgeClientError) {
            return {
                code: error.code,
                message: error.message,
                requestId: error.requestId,
                retryable: error.retryable,
                submissionState: error.submissionState,
                details: error.details
            };
        }
        return { message: String(error), submissionState: 'submitted_outcome_unknown' };
    }

    private beginEvidence(source: 'bridge' | 'mcp', action: string, args: unknown, context: Data): { evidenceId: string; startedAt: string } | undefined {
        if (!this.retainReads || this.evidenceDisabled) return undefined;
        const evidenceId = randomUUID();
        try {
            const startedAt = this.timestamp();
            const entry: ReadEvidenceEntry = {
                schemaVersion: 1,
                evidenceId,
                sessionId: this.sessionId,
                timestamp: startedAt,
                startedAt,
                event: 'read_requested',
                source,
                action,
                context: { ...context },
                args: this.artifact(args)
            };
            appendFileSync(`${this.basePath}.evidence.jsonl`, `${JSON.stringify(entry)}\n`, { mode: 0o600 });
            return { evidenceId, startedAt };
        } catch (writeError) {
            this.disableEvidence(writeError);
            return undefined;
        }
    }

    private finishEvidence(source: 'bridge' | 'mcp', action: string, context: Data,
        handle: { evidenceId: string; startedAt: string } | undefined,
        succeeded: boolean, response: unknown, error: unknown): void {
        if (!handle || !this.retainReads || this.evidenceDisabled) return;
        try {
            const entry: ReadEvidenceEntry = {
                schemaVersion: 1,
                evidenceId: handle.evidenceId,
                sessionId: this.sessionId,
                timestamp: this.timestamp(),
                startedAt: handle.startedAt,
                event: succeeded ? 'read_returned' : 'read_error',
                source,
                action,
                context: { ...context },
                ...(succeeded ? { response: this.artifact(response) } : { error: this.artifact(error) })
            };
            appendFileSync(`${this.basePath}.evidence.jsonl`, `${JSON.stringify(entry)}\n`, { mode: 0o600 });
        } catch (writeError) {
            this.disableEvidence(writeError);
        }
    }

    private observe(value: unknown): void {
        const result = object(value);
        const data = Object.keys(object(result.observation)).length ? object(result.observation) : result;
        if (data.activeGame === false) {
            this.context = {};
            this.towers.clear();
        }
        for (const key of ['matchId', 'round', 'map', 'difficulty', 'mode', 'selectedHero', 'moduleVersionId']) {
            if (data[key] !== undefined) this.context[key] = data[key];
        }
        if (Array.isArray(data.towers)) {
            this.towers.clear();
            for (const tower of data.towers) this.rememberTower(tower);
        }
        this.rememberTower(result.tower);
    }

    private rememberTower(value: unknown): void {
        const tower = object(value);
        if (typeof tower.id === 'string') this.towers.set(tower.id, object(compact({ tower }).tower));
    }

    async run<T>(method: string, args: unknown[], invoke: () => Promise<T>): Promise<T> {
        const logged = !reads[method];
        const actionId = logged ? ++this.sequence : undefined;
        const input = args[0] ?? {};
        const towerId = object(input).towerId;
        const before = typeof towerId === 'string' ? this.towers.get(towerId) : undefined;
        const started = Date.now();
        const retainReadEvidence = !logged && this.retainReads;
        const readContext = retainReadEvidence ? { ...this.context } : {};
        const readEvidence = retainReadEvidence ? this.beginEvidence('bridge', method, args, readContext) : undefined;
        if (logged) this.record({ event: 'action_requested', actionId, action: method,
            lastObserved: { ...this.context }, input, towerBefore: before });
        try {
            const result = await invoke();
            if (boundaries[method]) {
                this.context = {};
                this.towers.clear();
            }
            if (logged || ['status', 'observe', 'roundProgress', 'inspectTower'].includes(method)) this.observe(result);
            if (method === 'sellTower' && object(result).sold === true && typeof towerId === 'string') this.towers.delete(towerId);
            if (method === 'upgradeTower' && object(result).completed === false && typeof towerId === 'string') this.towers.delete(towerId);
            if (logged) this.record({ event: 'action_returned', actionId, action: method,
                durationMs: Date.now() - started, result: compact(result) });
            else if (retainReadEvidence) this.finishEvidence('bridge', method, readContext, readEvidence, true, result, undefined);
            return result;
        } catch (error) {
            if (logged) this.record({ event: 'action_error', actionId, action: method,
                durationMs: Date.now() - started, error: this.structuredError(error) });
            else if (retainReadEvidence) this.finishEvidence('bridge', method, readContext, readEvidence, false, undefined, this.structuredError(error));
            throw error;
        }
    }

    /** Record the actual MCP read-tool input/output without adding it to the action log. */
    async runReadTool<T>(tool: string, input: unknown, invoke: () => Promise<T>): Promise<T> {
        if (!this.retainReads) return invoke();
        const readContext = { ...this.context };
        const readEvidence = this.beginEvidence('mcp', tool, [input], readContext);
        try {
            const result = await invoke();
            this.finishEvidence('mcp', tool, readContext, readEvidence, true, result, undefined);
            return result;
        } catch (error) {
            this.finishEvidence('mcp', tool, readContext, readEvidence, false, undefined, this.structuredError(error));
            throw error;
        }
    }

    /** Read this journal's bounded factual branch history, including after adapter restart. */
    getResearchHistory(options: ResearchHistoryOptions = {}, sessionId = this.sessionId): Promise<ResearchHistory> {
        return readResearchHistory(this.directory, sessionId, options);
    }
}

/** Wrap only outer adapter calls; internal round polling stays on the original client. */
export function withActionJournal(client: BridgeClient, journal = new ActionJournal()): BridgeClient {
    const methods = new Map<PropertyKey, unknown>();
    return new Proxy(client, {
        get(target, property) {
            const value = Reflect.get(target, property, target);
            if (typeof value !== 'function' || property === 'constructor') return value;
            if (!methods.has(property)) methods.set(property, (...args: unknown[]) =>
                journal.run(String(property), args, () => value.apply(target, args)));
            return methods.get(property);
        }
    });
}
