import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readdir, readFile, rename, rm, unlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { setTimeout as sleep } from 'node:timers/promises';
import { BridgeClient, BridgeClientError } from '../src/bridge-client.js';

interface Command {
    requestId: string;
    kind: string;
    payload: Record<string, unknown>;
}

const progress = {
    observedAtUtc: '2026-01-01T00:00:00.000Z', matchId: 'match-1', activeGame: true,
    ui: { autoHandlingEnabled: true, ready: true, blocker: null },
    gameStatus: 'in_game', round: 1, roundActive: false, canStartRound: true, cash: 650, lives: 200
};
const observation = {
    ...progress, mapId: 'Logs', mapName: 'Logs', mode: 'Standard', towerCount: 0,
    towers: [], abilities: [], scheduledAbilities: [], hero: null
};
const capableStatus = {
    protocolVersion: 1, bridgeVersion: '0.2.0', moduleVersionId: '00000000-0000-0000-0000-000000000001',
    btd6Version: '56.3', gameAvailable: true, activeGame: true, ui: progress.ui,
    capabilities: { status: true, observation: true, playerActions: true, researchControls: true,
        scheduledUpgrades: true, roundInsights: true, checkpointTransfer: true, corvusSpells: true }
};
const success = (command: Command, result: unknown) => JSON.stringify({
    protocolVersion: 1, requestId: command.requestId, ok: true, result, error: null
});
const failure = (command: Command, code: string, retryable: boolean) => JSON.stringify({
    protocolVersion: 1, requestId: command.requestId, ok: false, result: null,
    error: { code, message: code, retryable }
});

async function withMailbox(
    respond: (command: Command) => string | undefined,
    run: (client: BridgeClient, commands: Command[]) => Promise<void>
): Promise<void> {
    const root = await mkdtemp(join(tmpdir(), 'btd6-mcp-test-'));
    const inbox = join(root, 'inbox');
    const outbox = join(root, 'outbox');
    await mkdir(inbox);
    await mkdir(outbox);
    const commands: Command[] = [];
    let stopped = false;
    const worker = (async () => {
        while (!stopped) {
            for (const name of await readdir(inbox)) {
                if (!name.endsWith('.json')) continue;
                const path = join(inbox, name);
                const command = JSON.parse(await readFile(path, 'utf8')) as Command;
                await unlink(path);
                commands.push(command);
                const response = respond(command);
                if (response !== undefined) {
                    const resultPath = join(outbox, name);
                    await writeFile(`${resultPath}.tmp`, response);
                    await rename(`${resultPath}.tmp`, resultPath);
                }
            }
            await sleep(2);
        }
    })();
    try {
        await Promise.race([run(new BridgeClient(root), commands), worker]);
    } finally {
        stopped = true;
        try { await worker; } finally { await rm(root, { recursive: true, force: true }); }
    }
}

function unknownOutcome(error: unknown, command: Command | undefined): boolean {
    assert(command, 'Expected a submitted command');
    assert(error instanceof BridgeClientError);
    assert.equal(error.requestId, command.requestId);
    assert.equal(error.submissionState, 'submitted_outcome_unknown');
    assert.equal(error.retryable, false);
    return true;
}

for (const [name, reply] of [
    ['malformed JSON', () => '{not-json'],
    ['wrong request ID', (command: Command) => success({ ...command, requestId: 'wrong-id' }, {})],
    ['null action payload', (command: Command) => success(command, null)]
] as const) {
    test(`${name} retains the submitted mutation identity`, async () => {
        await withMailbox(reply, async (client, commands) => {
            await assert.rejects(client.sellTower({ towerId: 'tower-1' }), error => {
                assert(error instanceof BridgeClientError);
                assert.equal(error.code, 'MALFORMED_BRIDGE_RESPONSE');
                return unknownOutcome(error, commands[0]);
            });
        });
    });
}

test('nested observation validation retains its request identity', async () => {
    await withMailbox(command => success(command, { ...observation, towers: [{}] }), async (client, commands) => {
        await assert.rejects(client.observe(), error => unknownOutcome(error, commands[0]));
    });
});

test('placement preflight rejection preserves the actual quote without implying a purchase', async () => {
    await withMailbox(command => JSON.stringify({
        protocolVersion: 1, requestId: command.requestId, ok: false, result: null,
        error: { code: 'INSUFFICIENT_CASH', message: 'Cannot afford hero', retryable: false, details: { cost: 970, cash: 847 } }
    }), async client => {
        await assert.rejects(client.placeTower({ towerType: 'AdmiralBrickell', x: -75, y: 32 }), error => {
            assert(error instanceof BridgeClientError);
            assert.equal(error.submissionState, 'rejected');
            assert.deepEqual(error.details, { cost: 970, cash: 847 });
            return true;
        });
    });
});

const upgradeTowerFixture = {
    id: 'tower-1', towerType: 'MonkeySub', name: 'MonkeySub-100', position: { x: -75, y: 32 },
    range: 40, targetPriority: 'First', tiers: [1, 0, 0], upgradeCosts: [540, 485, 485],
    nextUpgrades: [{ path: 0, name: 'Sharp Shots', cost: 540, available: true }, { path: 1, name: 'Quick Shots', cost: 485, available: true }, { path: 2, name: 'Long Range Darts', cost: 485, available: true }],
    crosspathSlotsRemaining: null, sellValue: 0, damageDealt: 0, pops: 0, isHero: false
};

test('an uncertain sequence outcome is never replayed or presented as a confirmed prefix', async () => {
    await withMailbox(command => command.kind === 'status' ? success(command, capableStatus) : '{malformed',
        async (client, commands) => {
            await assert.rejects(client.upgradeTower({ towerId: 'tower-1', upgradeSequence: [0, 2, 2] }),
                error => unknownOutcome(error, commands.find(command => command.kind === 'upgrade_tower')));
            assert.equal(commands.filter(command => command.kind === 'upgrade_tower').length, 1);
        });
});

test('an old bridge cannot silently ignore scheduled actions or checkpoint transfer', async () => {
    await withMailbox(command => success(command, { ...capableStatus, capabilities: {
        status: true, observation: true, playerActions: true, researchControls: true
    } }), async (client, commands) => {
        for (const operation of [
            () => client.upgradeTower({ towerId: 'tower-1', upgradeSequence: [0], whenAffordable: true }),
            () => client.exportCheckpoints(),
            () => client.importCheckpoints({ exportId: 'saved-export' }),
            () => client.useGeraldoItem({ itemId: 'ShootyTurret', target: { kind: 'point', x: 0, y: 70 }, whenAffordable: true }),
            () => client.castCorvusSpell({ spellId: 'Vision' })
        ]) {
            await assert.rejects(operation(), error => error instanceof BridgeClientError
                && error.code === 'UNSUPPORTED_CAPABILITY' && error.submissionState === 'not_submitted');
        }
        assert(commands.every(command => command.kind === 'status'), 'Unsupported mutations must not be submitted');
    });
});

test('an invalid later batch path cannot purchase an earlier upgrade', async () => {
    await withMailbox(() => { throw new Error('Invalid batch submitted'); }, async (client, commands) => {
        await assert.rejects(client.upgradeTower({ towerId: 'tower-1', upgradeSequence: [0, 9] }), error =>
            error instanceof BridgeClientError && error.submissionState === 'not_submitted');
        assert.equal(commands.length, 0);
    });
});

test('observations reject negative unavailable-upgrade sentinels', async () => {
    const tower = { ...upgradeTowerFixture, upgradeCosts: [540, -2147483600, 1080] };
    await withMailbox(command => success(command, { ...observation, towerCount: 1, towers: [tower] }), async client => {
        await assert.rejects(client.observe(), error =>
            error instanceof BridgeClientError && error.code === 'MALFORMED_BRIDGE_RESPONSE');
    });
});

test('bridge restart is uncertain but a known preflight rejection remains retryable', async () => {
    await withMailbox(command => failure(command, command.kind === 'sell_tower' ? 'BRIDGE_RESTARTED' : 'DEADLINE_EXCEEDED', true), async (client, commands) => {
        await assert.rejects(client.sellTower({ towerId: 'tower-1' }), error => unknownOutcome(error, commands[0]));
        await assert.rejects(client.startMatch(), error => {
            assert(error instanceof BridgeClientError);
            assert.equal(error.submissionState, 'rejected');
            assert.equal(error.retryable, true);
            assert(commands[1], 'Expected a second submitted command');
            assert.equal(error.requestId, commands[1].requestId);
            return true;
        });
    });
});

test('invalid start options publish neither speed changes nor a round start', async () => {
    await withMailbox(() => { throw new Error('An invalid operation was submitted'); }, async (client, commands) => {
        await assert.rejects(client.startRound({ timeoutSeconds: 0, fastForward: true }), error => {
            assert(error instanceof BridgeClientError);
            assert.equal(error.code, 'INVALID_ARGUMENT');
            assert.equal(error.submissionState, 'not_submitted');
            return true;
        });
        await assert.rejects(client.startRound({ timeoutSeconds: Number.MAX_VALUE, fastForward: true }), error => {
            assert(error instanceof BridgeClientError);
            assert.equal(error.code, 'INVALID_ARGUMENT');
            assert.equal(error.submissionState, 'not_submitted');
            return true;
        });
        assert.equal(commands.length, 0);
    });
});

test('an idle upcoming round waits for the deadline without claiming clearance', async () => {
    await withMailbox(command => success(command, command.kind === 'observe' ? observation : progress), async client => {
        const start = Date.now();
        const result = await client.waitForRoundEnd({ timeoutSeconds: 0.12, pollIntervalMs: 25 });
        assert(Date.now() - start >= 120);
        assert.equal(result.completed, false);
        assert.equal(result.roundCompleted, null);
        assert.equal(result.statusReason, 'timeout');
        assert.equal(result.timedOut, true);
    });
});

test('round advancement proves clearance and preserves full output', async () => {
    await withMailbox(command => success(command, { ...(command.kind === 'observe' ? observation : progress), round: 2 }), async client => {
        const result = await client.waitForRoundEnd({ initialRound: 1, initialMatchId: 'match-1', outputProfile: 'full' });
        assert.equal(result.completed, true);
        assert.equal(result.statusReason, 'round_cleared');
        assert.equal(result.roundCompleted, 1);
        assert.equal(result.observation?.round, 2);
    });
});

for (const [gameStatus, activeGame, expectedReason, completed] of [
    ['defeat', true, 'defeat', false],
    [null, false, 'match_ended', false],
    ['victory', true, 'victory', true]
] as const) {
    test(`${expectedReason} is distinct from clearing an ordinary round`, async () => {
        await withMailbox(command => success(command, {
            ...(command.kind === 'observe' ? observation : progress), gameStatus, activeGame
        }), async client => {
            const result = await client.waitForRoundEnd({ initialRound: 1 });
            assert.equal(result.statusReason, expectedReason);
            assert.equal(result.completed, completed);
            assert.equal(result.roundCompleted, completed ? 1 : null);
        });
    });
}

test('a progress request lost at the deadline returns a timeout after final capture', async () => {
    let firstProgress = true;
    await withMailbox(command => {
        if (command.kind === 'round_progress' && firstProgress) {
            firstProgress = false;
            return undefined;
        }
        return success(command, command.kind === 'observe' ? observation : progress);
    }, async client => {
        const result = await client.waitForRoundEnd({ timeoutSeconds: 0.06, pollIntervalMs: 25 });
        assert.equal(result.completed, false);
        assert.equal(result.roundCompleted, null);
        assert.equal(result.statusReason, 'timeout');
    });
});

test('a failed completion read cannot make an accepted start safe to retry', async () => {
    let started = false;
    await withMailbox(command => {
        if (command.kind === 'start_round') {
            started = true;
            return success(command, { started: true, round: 1 });
        }
        if (started) return failure(command, 'DEADLINE_EXCEEDED', true);
        return success(command, progress);
    }, async (client, commands) => {
        await assert.rejects(client.startRound(), error => unknownOutcome(error, commands.at(-1)!));
    });
});

const blockedUi = {
    autoHandlingEnabled: true, ready: false,
    blocker: { id: 'ui-test-1', kind: 'popup', screen: 'Popup', state: 'blocked',
        reason: null, title: 'Decision', body: 'Choose explicitly', actions: ['confirm', 'cancel'], choices: [], selectedChoice: null }
};

test('unknown UI returns a blocker rather than claiming an idle or cleared round', async () => {
    await withMailbox(command => success(command, {
        ...(command.kind === 'observe' ? observation : progress), round: 2, ui: blockedUi
    }), async client => {
        const result = await client.waitForRoundEnd({ initialRound: 1 });
        assert.equal(result.statusReason, 'ui_blocked');
        assert.equal(result.completed, false);
        assert.equal(result.roundCompleted, null);
        assert.equal(result.ui.blocker?.id, 'ui-test-1');
        assert.deepEqual(result.ui.blocker?.actions, ['confirm', 'cancel']);
    });
});

test('a known interruption can complete without ending the round wait early', async () => {
    let polls = 0;
    await withMailbox(command => {
        if (command.kind === 'round_progress' && polls++ === 0)
            return success(command, { ...progress, round: 2, ui: {
                ...blockedUi, blocker: { ...blockedUi.blocker, kind: 'tutorial', state: 'acting', actions: [] }
            } });
        return success(command, { ...(command.kind === 'observe' ? observation : progress), round: 2 });
    }, async client => {
        const result = await client.waitForRoundEnd({ initialRound: 1, pollIntervalMs: 25 });
        assert.equal(result.statusReason, 'round_cleared');
        assert.equal(result.roundCompleted, 1);
        assert.equal(result.ui.ready, true);
    });
});

test('a UI blocker prevents both speed mutation and round submission', async () => {
    await withMailbox(command => {
        assert.equal(command.kind, 'round_progress');
        return success(command, { ...progress, ui: blockedUi });
    }, async client => {
        const result = await client.startRound({ fastForward: true });
        assert.equal(result.started, false);
        assert.equal(result.statusReason, 'ui_blocked');
        assert.equal(result.ui?.blocker?.id, 'ui-test-1');
    });
});

test('malformed UI data cannot be treated as an unobstructed game', async () => {
    await withMailbox(command => success(command, { ...progress, ui: { ...blockedUi, ready: true } }), async (client, commands) => {
        await assert.rejects(client.roundProgress(), error => unknownOutcome(error, commands[0]));
    });
});

test('malformed activation target cannot report successful placement', async () => {
    await withMailbox(command => success(command, {
        activated: true, abilityId: '42', name: 'Mega Mine',
        target: { kind: 'point', x: 12 }
    }), async (client, commands) => {
        await assert.rejects(client.activateAbility({
            abilityId: '42', target: { kind: 'point', x: 12, y: 4 }
        }), error => unknownOutcome(error, commands[0]));
    });
});

test('native target rejection is safe but a failed activation remains uncertain', async () => {
    await withMailbox(command => failure(command,
        command.payload.abilityId === 'missing' ? 'ABILITY_TARGET_REQUIRED' : 'ABILITY_ACTIVATION_FAILED', true),
    async (client, commands) => {
        await assert.rejects(client.activateAbility({ abilityId: 'missing' }), error =>
            error instanceof BridgeClientError && error.submissionState === 'rejected');
        await assert.rejects(client.activateAbility({ abilityId: 'failing', target: { kind: 'point', x: 12, y: 4 } }),
            error => unknownOutcome(error, commands[1]));
    });
});

test('invalid ability targets and ambiguous selectors are rejected before mailbox submission', async () => {
    await withMailbox(() => {
        throw new Error('Invalid ability input was submitted');
    }, async (client, commands) => {
        await assert.rejects(
            client.activateAbility({ target: null } as never),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        await assert.rejects(
            client.activateAbility({ target: { kind: 'point', x: Number.MAX_VALUE, y: 0 } } as never),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        await assert.rejects(
            client.scheduleAbility({ abilityIndex: 0, abilityId: 'both' }),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        await assert.rejects(
            client.scheduleAbility({
                target: { kind: 'points', points: [] }
            }),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        assert.equal(commands.length, 0);
    });
});
test('observation rejects malformed ability targeting metadata', async () => {
    await withMailbox(command => success(command, {
        ...observation,
        abilities: [{
            abilityId: 'Ability.Native',
            towerId: 'tower-1',
            name: 'Native ability',
            isReady: true,
            canUse: true,
            cooldownRemaining: 0,
            cooldownTotal: 10,
            targeting: { kind: 'invalid', inputClass: null, supported: false }
        }]
    }), async (client, commands) => {
        await assert.rejects(client.observe(), error => unknownOutcome(error, commands[0]));
    });
});

test('malformed Geraldo schedules cannot silently disappear from observation', async () => {
    await withMailbox(command => success(command, {
        ...observation,
        scheduledGeraldoPurchases: [{
            scheduleId: 'geraldo-1', itemId: 'ShootyTurret',
            target: { kind: 'tower', towerId: '' },
            whenAffordable: true, targetRound: null, delaySeconds: null,
            status: 'waiting', waitingFor: 'cash', failure: null, result: null
        }]
    }), async (client, commands) => {
        await assert.rejects(client.observe(), error => unknownOutcome(error, commands[0]));
    });
});

test('ambiguous Geraldo timing and mixed target shapes never submit a purchase', async () => {
    await withMailbox(() => { throw new Error('Invalid Geraldo purchase was submitted'); }, async (client, commands) => {
        await assert.rejects(client.useGeraldoItem({
            itemId: 'ShootyTurret', target: { kind: 'point', x: 0, y: 70 }, round: 2, delayFromNow: 1
        }), error => error instanceof BridgeClientError && error.code === 'INVALID_ARGUMENT'
            && error.submissionState === 'not_submitted');
        await assert.rejects(client.useGeraldoItem({
            itemId: 'JarOfPickles', target: { kind: 'tower', towerId: 'tower-1', x: 0, y: 70 }
        } as never), error => error instanceof BridgeClientError && error.code === 'INVALID_ARGUMENT'
            && error.submissionState === 'not_submitted');
        assert.equal(commands.length, 0);
    });
});
test('invalid Corvus identifiers and ambiguous timing never submit an action', async () => {
    await withMailbox(() => { throw new Error('Invalid Corvus action was submitted'); }, async (client, commands) => {
        for (const operation of [
            () => client.castCorvusSpell({ spellId: '   ' }),
            () => client.castCorvusSpell({ spellId: 'Vision', round: 2, delayFromNow: 1 }),
            () => client.setCorvusSpell({ spellId: 'Aggression', enabled: true, delaySeconds: Number.MAX_VALUE }),
            () => client.setCorvusSpell({ spellId: 'Aggression', enabled: true, x: 0 } as never)
        ]) {
            await assert.rejects(operation(), error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT' && error.submissionState === 'not_submitted');
        }
        assert.equal(commands.length, 0);
    });
});

test('malformed Corvus schedules cannot silently disappear from observation', async () => {
    await withMailbox(command => success(command, {
        ...observation,
        scheduledCorvusActions: [{
            scheduleId: 'corvus-1', heroTowerId: 'hero-1', spellId: 'Vision',
            enabled: null, whenReady: true, targetRound: null, delaySeconds: null,
            status: 'waiting', waitingFor: 'mana', failure: null, result: null,
            unexpected: true
        }]
    }), async (client, commands) => {
        await assert.rejects(client.observe(), error => unknownOutcome(error, commands[0]));
    });
});

test('checkpoint labels match native limits and invalid selectors never submit', async () => {
    await withMailbox(command => success(command, {
        saved: true, round: 1, label: null, timestampUtc: '2026-01-01T00:00:00.000Z'
    }), async (client, commands) => {
        for (const label of ['', 'x'.repeat(129), 'before\u0001bfb', 'before\u009fbfb', null]) {
            await assert.rejects(
                client.saveCheckpoint({ label } as never),
                error => error instanceof BridgeClientError
                    && error.code === 'INVALID_ARGUMENT'
                    && error.submissionState === 'not_submitted'
            );
        }
        await assert.rejects(
            client.restoreCheckpoint({ label: 'restore\u0001' }),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        await assert.rejects(
            client.deleteCheckpoint({ label: 'delete\u009f' }),
            error => error instanceof BridgeClientError
                && error.code === 'INVALID_ARGUMENT'
                && error.submissionState === 'not_submitted'
        );
        assert.equal(commands.length, 0);
        await client.saveCheckpoint();
        assert.equal(commands.length, 1);
        assert.equal(commands[0]?.payload.label, undefined);
        await client.saveCheckpoint({ label: 'x'.repeat(128) });
        assert.equal(commands.length, 2);
        assert.equal(commands[1]?.payload.label, 'x'.repeat(128));
    });
});

test('checkpoint restore waits for and returns refreshed between-round state', async () => {
    await withMailbox(command => {
        if (command.kind === 'restore_checkpoint') {
            return success(command, { restored: true, round: 1, cash: 650, health: 200 });
        }
        if (command.kind === 'round_progress') return success(command, progress);
        if (command.kind === 'observe') return success(command, observation);
        throw new Error(`Unexpected command ${command.kind}`);
    }, async (client, commands) => {
        const result = await client.restoreCheckpoint({ round: 1 });
        assert.equal(result.restored, true);
        assert.equal(result.stateReady, true);
        assert.equal(result.schedulesCleared, true);
        assert.equal(result.observation?.round, 1);
        assert.deepEqual(commands.map(command => command.kind), [
            'restore_checkpoint', 'round_progress', 'observe'
        ]);
    });
});
