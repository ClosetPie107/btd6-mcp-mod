import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { ActionJournal, readResearchHistory } from '../src/action-journal.js';
import { BridgeClientError } from '../src/bridge-client.js';

const fixedClock = () => new Date('2026-09-09T00:00:00.000Z');

test('checkpoint branches retain history without reusing stale tower context or projected rounds', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));
    try {
        const journal = new ActionJournal(root, { retainReads: false, clock: fixedClock });
        await journal.run('observe', [], async () => ({ round: 15, towers: [{ id: '1', towerType: 'NinjaMonkey', tiers: [2, 0, 0] }] }));
        await journal.run('getRoundInfo', [{ round: 100 }], async () => ({ round: 100 }));
        await journal.run('upgradeTower', [{ towerId: '1', upgradeSequence: [0] }], async () => ({ completed: true, appliedPaths: [0], cash: 100, failure: null }));
        const error = new BridgeClientError('MALFORMED_BRIDGE_RESPONSE', 'Unknown purchase outcome', false, 'request-1', 'submitted_outcome_unknown');
        await assert.rejects(journal.run('sellTower', [{ towerId: '1' }], async () => { throw error; }), e => e === error);
        await journal.run('restoreCheckpoint', [{ label: 'opening' }], async () => ({ restored: true, round: 10 }));
        await journal.run('upgradeTower', [{ towerId: '1', upgradeSequence: [0] }], async () => ({ completed: false, appliedPaths: [], cash: null, failure: { code: 'UPGRADE_UNAVAILABLE' } }));
        const entries = (await readFile(`${journal.basePath}.jsonl`, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
        assert(!entries.some(e => ['observe', 'getRoundInfo'].includes(e.action)));
        const purchases = entries.filter(e => e.action === 'upgradeTower' && e.event === 'action_requested');
        assert.equal(purchases[0].lastObserved.round, 15);
        assert.equal(purchases[0].towerBefore.towerType, 'NinjaMonkey');
        assert.equal(purchases[1].lastObserved.round, 10);
        assert.equal(purchases[1].towerBefore, undefined);
        assert.equal(entries.find(e => e.event === 'action_error').error.submissionState, 'submitted_outcome_unknown');
        assert(!entries.some(e => e.action === 'sellTower' && e.event === 'action_returned'));
        assert.equal(entries.at(-1).result.completed, false);
        const text = await readFile(`${journal.basePath}.log`, 'utf8');
        assert.match(text, /NinjaMonkey/);
        assert.match(text, /restoreCheckpoint/);
        assert.match(text, /submitted_outcome_unknown/);
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('opt-in evidence retains exact bridge and MCP read payloads, including artifact spills', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));
    try {
        const journal = new ActionJournal(root, { retainReads: true, maxInlineBytes: 256, clock: fixedClock, sessionId: 'evidence-session' });
        const bridgeResponse = { activeGame: true, round: 7, payload: 'x'.repeat(1024) };
        await journal.run('status', [{ detail: 'full' }], async () => bridgeResponse);
        const mcpResponse = { content: [{ type: 'image', data: 'y'.repeat(1024), mimeType: 'image/png' }] };
        await journal.runReadTool('btd6_map_layout', { format: 'png', placementCandidates: true }, async () => mcpResponse);
        const entries = (await readFile(`${journal.basePath}.evidence.jsonl`, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
        assert.equal(entries.length, 4);
        const bridgeRequest = entries.find(entry => entry.source === 'bridge' && entry.event === 'read_requested');
        const bridgeReturn = entries.find(entry => entry.source === 'bridge' && entry.event === 'read_returned');
        const mcpRequest = entries.find(entry => entry.source === 'mcp' && entry.event === 'read_requested');
        const mcpReturn = entries.find(entry => entry.source === 'mcp' && entry.event === 'read_returned');
        assert.deepEqual(bridgeRequest.args, [{ detail: 'full' }]);
        assert.equal(bridgeReturn.response.encoding, 'json');
        assert.deepEqual(JSON.parse(await readFile(join(root, journal.sessionId, bridgeReturn.response.file), 'utf8')), bridgeResponse);
        assert.deepEqual(mcpRequest.args, [{ format: 'png', placementCandidates: true }]);
        assert.deepEqual(JSON.parse(await readFile(join(root, journal.sessionId, mcpReturn.response.file), 'utf8')), mcpResponse);
        assert.equal(bridgeRequest.evidenceId, bridgeReturn.evidenceId);
        assert.equal(mcpRequest.evidenceId, mcpReturn.evidenceId);
        assert.notEqual(bridgeRequest.evidenceId, mcpRequest.evidenceId);
        assert.equal(bridgeRequest.timestamp, '2026-09-09T00:00:00.000Z');
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('opt-in evidence retains structured bridge errors without changing the thrown error', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));
    try {
        const journal = new ActionJournal(root, { retainReads: true, clock: fixedClock });
        const error = new BridgeClientError('READ_FAILED', 'Read failed', true, 'request-2', 'not_submitted', { reason: 'test' });
        await assert.rejects(journal.run('status', [], async () => { throw error; }), thrown => thrown === error);
        const entries = (await readFile(`${journal.basePath}.evidence.jsonl`, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
        const errorEntry = entries.find(entry => entry.event === 'read_error');
        assert.deepEqual(errorEntry.error, {
            code: 'READ_FAILED', message: 'Read failed', requestId: 'request-2', retryable: true,
            submissionState: 'not_submitted', details: { reason: 'test' }
        });
        assert.equal(entries.find(entry => entry.event === 'read_requested').evidenceId, errorEntry.evidenceId);
    } finally { await rm(root, { recursive: true, force: true }); }
});
test('research history counts restore branches and repeated factual failures', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));

    try {
        const journal = new ActionJournal(root, { retainReads: false, clock: fixedClock, sessionId: 'history-session' });
        await journal.run('saveCheckpoint', [{ label: 'tactical' }], async () => ({ saved: true, checkpointId: 'cp-1', round: 20, cash: 500 }));
        await journal.run('upgradeTower', [{ towerId: '1', upgradeSequence: [0] }], async () => ({ completed: false, status: 'failed', failure: { code: 'UPGRADE_UNAVAILABLE' } }));
        await journal.run('restoreCheckpoint', [{ label: 'tactical' }], async () => ({ restored: true, round: 20, cash: 500 }));
        await journal.run('setTargetPriority', [{ towerId: '1', priority: 'last' }], async () => ({ changed: true, cash: 500 }));
        await journal.run('upgradeTower', [{ towerId: '1', upgradeSequence: [0] }], async () => ({ completed: false, status: 'failed', failure: { code: 'UPGRADE_UNAVAILABLE' } }));
        const history = await journal.getResearchHistory();
        assert.equal(history.totalBranches, 2);
        const newest = history.branches[0];
        assert(newest);
        assert.equal(newest.branchId, 1);
        assert.equal(newest.changes.find(change => change.action === 'setTargetPriority')?.kind, 'target');
        assert.equal(newest.restoredFrom?.kind, 'checkpoint_restored');
        assert.equal(history.repeatedFailures[0]?.count, 2);
        assert.deepEqual(history.repeatedFailures[0]?.branches, [0, 1]);
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('history bounds changes without treating queued actions as failures or merging different matches', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-history-bounds-'));
    try {
        const journal = new ActionJournal(root, { retainReads: false });
        await journal.run('startMatch', [{ map: 'Streambed' }], async () => ({ started: true }));
        await journal.run('upgradeTower', [{ towerId: 'one', upgradeSequence: [0], whenAffordable: true }],
            async () => ({ completed: false, status: 'scheduled', failure: null }));
        await journal.run('placeTower', [{ towerType: 'DartMonkey' }], async () => ({ placed: true }));
        await journal.run('startRound', [], async () => ({
            statusReason: 'defeat', round: 40, completed: false, lives: 0,
            roundReport: { leaks: { total: 2, omitted: 0, entries: [
                { baseId: 'Red', actualHealthLoss: 1 }, { baseId: 'Green', actualHealthLoss: 3 }
            ] } }
        }));
        await journal.run('respondUi', [{ blockerId: 'defeat', action: 'restart' }], async () => ({ accepted: true }));
        await journal.run('startRound', [], async () => ({
            statusReason: 'defeat', round: 40, completed: false, lives: 0,
            roundReport: { leaks: { total: 2, omitted: 1, entries: [{ baseId: 'Red', actualHealthLoss: 1 }] } }
        }));
        const history = await journal.getResearchHistory({ actionLimit: 1 });
        assert.equal(history.readEvidenceEnabled, false);
        assert.equal(history.totalBranches, 2);
        const [newest, previous] = history.branches;
        assert(newest && previous);
        assert.equal(previous.omittedChanges, 1);
        assert.equal(previous.changes[0]?.action, 'placeTower');
        assert.equal(previous.outcomes[0]?.status, 'defeat');
        assert.deepEqual(previous.outcomes[0]?.triggeringLeak, { baseId: 'Green', actualHealthLoss: 3 });
        assert.equal(newest.outcomes[0]?.triggeringLeak, null, 'Incomplete leak evidence must not invent the lethal bloon');
        assert.notEqual(newest.matchGroup, previous.matchGroup);
        assert.deepEqual(history.repeatedFailures, []);
    } finally { await rm(root, { recursive: true, force: true }); }
});
test('read evidence write failure is diagnostic-only', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));
    try {
        const journal = new ActionJournal(root, { retainReads: true, clock: fixedClock });
        await mkdir(`${journal.basePath}.evidence.jsonl`);
        const result = await journal.runReadTool('btd6_round_progress', {}, async () => ({ round: 8 }));
        assert.equal(result.round, 8);
    } finally { await rm(root, { recursive: true, force: true }); }
});



test('journal write failure cannot change a completed action or cause its retry', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-journal-'));
    try {
        const journal = new ActionJournal(root);
        await rm(`${journal.basePath}.jsonl`);
        await mkdir(`${journal.basePath}.jsonl`);
        let purchases = 0;
        const result = await journal.run('placeTower', [{ towerType: 'DartMonkey' }], async () => {
            purchases++;
            return { placed: true };
        });
        assert.equal(result.placed, true);
        assert.equal(purchases, 1);
    } finally { await rm(root, { recursive: true, force: true }); }
});

test('history exposes bounded unresolved requests without inventing outcomes', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-history-incomplete-'));
    const path = join(root, 'incomplete.jsonl');
    try {
        await writeFile(path, [
            { schemaVersion: 1, sessionId: 'incomplete', timestamp: fixedClock().toISOString(), event: 'session_started', readEvidenceEnabled: false },
            { schemaVersion: 1, sessionId: 'incomplete', timestamp: fixedClock().toISOString(), event: 'action_requested', actionId: 1, action: 'upgradeTower', input: { towerId: 'one' } },
            { schemaVersion: 1, sessionId: 'incomplete', timestamp: fixedClock().toISOString(), event: 'action_requested', actionId: 2, action: 'placeTower', input: { towerType: 'DartMonkey' } }
        ].map(line => JSON.stringify(line)).join('\n') + '\n');

        const history = await readResearchHistory(root, 'incomplete', { actionLimit: 1 });
        const branch = history.branches[0];
        assert(branch);
        assert.equal(history.incomplete, true);
        assert.equal(branch.unresolvedActions.length, 1);
        assert.equal(branch.unresolvedActions[0]?.actionId, 2);
        assert.equal(branch.omittedUnresolvedActions, 1);
        assert.deepEqual(branch.outcomes, []);
        assert.deepEqual(branch.changes, []);
        assert.deepEqual(history.repeatedFailures, []);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});

test('history marks unresolved requests on omitted branches in the aggregate result', async () => {
    const root = await mkdtemp(join(tmpdir(), 'btd6-history-incomplete-'));
    const path = join(root, 'paged.jsonl');
    try {
        await writeFile(path, [
            { schemaVersion: 1, sessionId: 'paged', timestamp: fixedClock().toISOString(), event: 'session_started', readEvidenceEnabled: false },
            { schemaVersion: 1, sessionId: 'paged', timestamp: fixedClock().toISOString(), event: 'action_requested', actionId: 1, action: 'upgradeTower', input: { towerId: 'one' } },
            { schemaVersion: 1, sessionId: 'paged', timestamp: fixedClock().toISOString(), event: 'action_requested', actionId: 2, action: 'restoreCheckpoint', input: { label: 'opening' } },
            { schemaVersion: 1, sessionId: 'paged', timestamp: fixedClock().toISOString(), event: 'action_returned', actionId: 2, action: 'restoreCheckpoint', result: { restored: true, round: 10 } }
        ].map(line => JSON.stringify(line)).join('\n') + '\n');

        const newest = await readResearchHistory(root, 'paged', { offset: 0, limit: 1 });
        assert.equal(newest.totalBranches, 2);
        assert.equal(newest.branches[0]?.unresolvedActions.length, 0);
        assert.equal(newest.incomplete, true);
        const oldest = await readResearchHistory(root, 'paged', { offset: 1, limit: 1 });
        assert.equal(oldest.branches[0]?.unresolvedActions[0]?.actionId, 1);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});
