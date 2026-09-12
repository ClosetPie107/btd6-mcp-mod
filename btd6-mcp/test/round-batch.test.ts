import assert from 'node:assert/strict';
import test from 'node:test';
import {
    runRoundBatch,
    roundBatchAggregateError,
    BridgeClientError,
    type BridgeClient,
    type StartRoundResult
} from '../src/bridge-client.js';

function roundResult(round: number, roundCompleted: number | null, statusReason: string): StartRoundResult {
    return {
        started: true,
        completed: statusReason === 'round_cleared',
        timedOut: statusReason === 'timeout',
        statusReason: statusReason as StartRoundResult['statusReason'],
        ui: { ready: true, autoHandlingEnabled: true, blocker: null },
        round,
        roundCompleted,
        gameStatus: statusReason === 'defeat' ? 'defeat' : 'in_game',
        cash: 100,
        lives: statusReason === 'defeat' ? 0 : 1
    };
}

test('round batch stops before a strategic boundary without starting it', async () => {
    let starts = 0;
    const client = {
        roundProgress: async () => ({ round: 40 }),
        startRound: async () => { starts++; return roundResult(41, 40, 'round_cleared'); }
    } as unknown as Pick<BridgeClient, 'roundProgress' | 'startRound'>;
    const result = await runRoundBatch(client, {
        count: 5, stopBeforeRounds: [40], timeoutSecondsPerRound: 120, fastForward: true
    });
    assert.equal(starts, 0);
    assert.equal(result.stopReason, 'strategic_boundary');
    assert.equal(result.nextPlayableRound, 40);
    assert.deepEqual(result.outcomes, []);
});

test('round batch journals each start and stops on the first non-clear result', async () => {
    const calls: Array<{ fastForward?: boolean; timeoutSeconds?: number }> = [];
    const results = [
        roundResult(39, 38, 'round_cleared'),
        roundResult(40, 39, 'round_cleared'),
        roundResult(40, null, 'defeat')
    ];
    const client = {
        roundProgress: async () => ({ round: 38 }),
        startRound: async (options: { fastForward?: boolean; timeoutSeconds?: number }) => {
            calls.push(options);
            const result = results.shift();
            assert(result);
            return result;
        }
    } as unknown as Pick<BridgeClient, 'roundProgress' | 'startRound'>;
    const result = await runRoundBatch(client, {
        count: 10, stopBeforeRounds: [], timeoutSecondsPerRound: 45, fastForward: true
    });
    assert.equal(result.stopReason, 'defeat');
    assert.equal(result.nextPlayableRound, 40);
    assert.equal(result.outcomes.length, 3);
    assert.deepEqual(calls.map(call => call.fastForward), [true, undefined, undefined]);
    assert.deepEqual(calls.map(call => call.timeoutSeconds), [45, 45, 45]);
});

test('round batch keeps completed prefix when a later start is preflight-rejected', async () => {
    const completed = roundResult(2, 1, 'round_cleared');
    const rejection = new BridgeClientError(
        'UI_BLOCKED',
        'UI must be handled before starting the next round.',
        true,
        'request-2',
        'rejected'
    );
    let calls = 0;
    const client = {
        roundProgress: async () => ({ round: 1 }),
        startRound: async () => {
            calls++;
            if (calls === 1) return completed;
            throw rejection;
        }
    } as unknown as Pick<BridgeClient, 'roundProgress' | 'startRound'>;

    const result = await runRoundBatch(client, {
        count: 3, stopBeforeRounds: [], timeoutSecondsPerRound: 45, fastForward: true
    });

    assert.equal(calls, 2);
    assert.equal(result.attemptedCount, 2);
    assert.deepEqual(result.outcomes, [completed]);
    assert.equal(result.nextPlayableRound, 2);
    assert.equal(result.failure?.iteration, 2);
    assert.equal(result.failure?.submissionState, 'rejected');
    const aggregate = roundBatchAggregateError(result);
    assert(aggregate);
    assert.equal(aggregate.code, 'BATCH_PARTIALLY_COMPLETED');
    assert.equal(aggregate.retryable, false);
    assert.equal(aggregate.submissionState, 'submitted_outcome_unknown');
    assert.equal(aggregate.details?.completedOutcomeCount, 1);
    const failedIterationError = aggregate.details?.failedIterationError;
    assert(failedIterationError && typeof failedIterationError === 'object' && 'retryable' in failedIterationError);
    assert.equal(failedIterationError.retryable, true);
    assert.equal(result.failure?.retryable, true);
});

test('round batch preserves an uncertain later start without replaying the completed prefix', async () => {
    const completed = roundResult(2, 1, 'round_cleared');
    const uncertain = new BridgeClientError(
        'SUBMISSION_OUTCOME_UNKNOWN',
        'The start outcome could not be established.',
        false,
        'request-2',
        'submitted_outcome_unknown'
    );
    let calls = 0;
    const client = {
        roundProgress: async () => ({ round: 1 }),
        startRound: async () => {
            calls++;
            if (calls === 1) return completed;
            throw uncertain;
        }
    } as unknown as Pick<BridgeClient, 'roundProgress' | 'startRound'>;

    const result = await runRoundBatch(client, {
        count: 3, stopBeforeRounds: [], timeoutSecondsPerRound: 45, fastForward: true
    });

    assert.equal(calls, 2);
    assert.equal(result.attemptedCount, 2);
    assert.deepEqual(result.outcomes, [completed]);
    assert.equal(result.nextPlayableRound, null);
    assert.equal(result.failure?.iteration, 2);
    assert.equal(result.failure?.submissionState, 'submitted_outcome_unknown');
    assert.equal(result.failure?.retryable, false);
});
