import { readFile } from 'node:fs/promises';
import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import * as z from 'zod/v4';
import { BridgeClient, BridgeClientError, roundBatchAggregateError, runRoundBatch, type StartRoundResult } from './bridge-client.js';
import {
    configurationInputSchema,
    uiActionSchema,
    activateAbilityInputSchema,
    scheduleAbilityInputSchema,
    upgradeTowerInputSchema,
    exportCheckpointsInputSchema,
    importCheckpointsInputSchema,
    checkpointLabelSchema,
    rangeTowerIdsSchema,
    canUseGeraldoItemInputSchema,
    useGeraldoItemInputSchema,
    cancelScheduledGeraldoPurchaseInputSchema,
    castCorvusSpellInputSchema,
    setCorvusSpellInputSchema,
    cancelScheduledCorvusActionInputSchema
} from './bridge-data.js';
import { renderMap } from './map-renderer.js';
import { ActionJournal, withActionJournal } from './action-journal.js';
import { formatObservationOutput, formatRoundOutput, summarizeTower } from './play-output.js';
import {
    formatTowerCatalogOutput, formatTowerStatsOutput, formatTowerInspectionOutput,
    formatRoundInfoOutput, formatProjectedCashOutput, formatPlacementSpotsOutput
} from './intelligence-output.js';

const toolInput = z.object({}).strict();
const playingGuideUri = 'btd6://guides/playing';
const playingGuidePath = new URL('../PLAYING.md', import.meta.url);
const outputProfileSchema = z.enum(['summary', 'full']).default('summary')
    .describe('summary (default): concise decision-ready state; full: complete diagnostic payload.');

function formatToolResult(result: unknown) {
    return {
        content: [{ type: 'text' as const, text: JSON.stringify(result) }]
    };
}

function formatToolError(error: unknown, details?: Record<string, unknown>) {
    const payload = error instanceof BridgeClientError
        ? {
            error: {
                code: error.code,
                message: error.message,
                retryable: error.retryable,
                requestId: error.requestId,
                submissionState: error.submissionState,
                details: error.details
            },
            ...details
        }
        : {
            error: {
                code: 'ADAPTER_FAILURE',
                message: error instanceof Error ? error.message : String(error),
                retryable: false,
                submissionState: 'submitted_outcome_unknown'
            },
            ...details
        };
    return {
        content: [{ type: 'text' as const, text: JSON.stringify(payload) }],
        isError: true
    };
}
async function formatDecisionRound(bridge: BridgeClient, result: StartRoundResult) {
    if (result.statusReason !== 'defeat' && result.statusReason !== 'timeout') {
        return formatRoundOutput(result, 'summary');
    }
    try {
        return formatRoundOutput(result, 'summary', await bridge.listCheckpoints());
    } catch {
        // Recovery inventory is supplementary; never turn a known round outcome into
        // an uncertain mutation merely because listing checkpoints failed afterward.
        return formatRoundOutput(result, 'summary');
    }
}


function createServer(): McpServer {
    const journal = new ActionJournal(process.env.BTD6_AGENT_BRIDGE_LOG_ROOT || undefined);
    const bridge = withActionJournal(new BridgeClient(), journal);
    const retainRead = <Input, Result>(tool: string, handler: (input: Input) => Promise<Result>) =>
        (input: Input): Promise<Result> => journal.runReadTool(tool, input, () => handler(input));
    const server = new McpServer({ name: 'btd6-mcp', version: '0.1.0' }, {
        instructions: `Before playing BTD6, read the gameplay guide at ${playingGuideUri} using resources/read. It covers ordinary versus research-assisted runs, safe game actions, output profiles, checkpoints, and result reporting. Read it once per session, or again if its guidance is lost from context; it is available without a running game.`
    });

    server.registerResource(
        'playing-guide',
        playingGuideUri,
        {
            title: 'BTD6 Gameplay Guide',
            description: 'Read before gameplay: MCP workflow, ordinary versus research-assisted runs, safety, planning, checkpoints, and reporting.',
            mimeType: 'text/markdown'
        },
        async uri => ({
            contents: [{
                uri: uri.href,
                mimeType: 'text/markdown',
                text: await readFile(playingGuidePath, 'utf8')
            }]
        })
    );

    server.registerTool('btd6_status', {
        description: 'Read AgentBridge connection status, BTD6 version, match activity, and declared read-only capabilities.',
        inputSchema: toolInput
    }, retainRead('btd6_status', async () => {
        try {
            const status = await bridge.status();
            return formatToolResult(status);
        } catch (error) {
            return formatToolError(error);
        }
    }));


    server.registerTool('btd6_observe', {
        description: 'Read current match state, resources, actionable tower roster, hero, abilities, bosses and pending/failed schedules. Summary is self-contained and omits lifetime statistics and retained round reports; use outputProfile=full for diagnostics.',
        inputSchema: z.object({ outputProfile: outputProfileSchema }).strict()
    }, retainRead('btd6_observe', async ({ outputProfile }) => {
        try {
            const observation = await bridge.observe();
            if (!observation.activeGame) {
                return formatToolError(
                    new BridgeClientError(
                        'NO_ACTIVE_GAME',
                        'BTD6 is at the main menu; start a match before requesting an in-game observation.',
                        true,
                        undefined,
                        'not_submitted'
                    ),
                    { observation: formatObservationOutput(observation, outputProfile) }
                );
            }
            return formatToolResult(formatObservationOutput(observation, outputProfile));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    for (const [name, description, read] of [
        ['btd6_round_progress', 'Read cheap round lifecycle, match ID, cash and lives without scanning towers or bloons.', () => bridge.roundProgress()],
        ['btd6_bridge_performance', 'Read bounded bridge operation timings, frame-interval percentiles, recent spikes and effective research configuration. Frame intervals include non-bridge game/OS work.', () => bridge.performance()]
    ] as const) {
        server.registerTool(name, { description, inputSchema: toolInput }, async () => {
            try {
                return formatToolResult(await read());
            } catch (error) {
                return formatToolError(error);
            }
        });
    }

    server.registerTool('btd6_configure_bridge', {
        description: 'Explicit research control: configure automatic checkpoints (default manual), retained round saves, cooperative command budget and placement checks per frame. Selected checkpoint rounds refer to the next playable round. Settings last until game process exit; reducing retention evicts oldest round saves. Named saves are separate.',
        inputSchema: configurationInputSchema
    }, async options => {
        try {
            return formatToolResult(await bridge.configure(options));
        } catch (error) {
            return formatToolError(error);
        }
    });

    server.registerTool(
        'btd6_start_match',
        {
            description: 'Start a regular match or unranked BossChallenge on a selected map from an unobstructed main menu. The result reports distinct mapId and mapName fields. Assisted checkpointing is enabled by default: the bridge retains a rolling latest checkpoint plus anchors after every fifth completed round. Set checkpointPolicy=none for an unassisted run. Map accepts the in-game display name or native ID. BossChallenge requires bossType from boss_catalog and supports elite.',
            inputSchema: z.object({
                map: z.string().optional().default('Logs').describe('In-game map name or native map ID, case-insensitive; spaces, hyphens, and underscores are optional.'),
                difficulty: z.enum(['Easy', 'Medium', 'Hard']).optional().default('Easy').describe('Difficulty category. Modes exclusive to another category select their native category; CHIMPS and Impoppable resolve to Hard. The result reports the resolved category.'),
                mode: z.string().optional().default('Standard').describe('Regular native mode, Sandbox, or BossChallenge.'),
                bossType: z.string().trim().min(1).max(128).optional().describe('Required for BossChallenge only; installed boss ID from boss_catalog.'),
                elite: z.boolean().optional().describe('BossChallenge only: Elite rather than Normal. Defaults to false.'),
                hero: z.string().optional().describe('Hero to play with (e.g. Quincy, ObynGreenfoot, Gwendolin, Sauda, Geraldo, Corvus, etc.)'),
                autoHandleUi: z.boolean().optional().default(true).describe('Automatically acknowledge recognized informational gameplay interruptions for this match. Unknown dialogs and tower choices remain explicit.'),
                checkpointPolicy: z.enum(['assisted', 'none']).optional().default('assisted').describe('assisted retains the rolling latest checkpoint plus anchors after rounds 5, 10, 15, and so on; none creates no automatic checkpoints.')
            })
        },
        async ({ map, difficulty, mode, hero, autoHandleUi, checkpointPolicy, bossType, elite }) => {
            try {
                const result = await bridge.startMatch({ map, difficulty, mode, hero, autoHandleUi, checkpointPolicy, bossType, elite });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_restart_match',
        {
            description: 'Restart the current active match.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.restartMatch();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_quit_match',
        {
            description: 'Quit the current match and return to the main menu.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.quitMatch();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_ensure_main_menu',
        {
            description: 'Return BTD6 to the Main Menu. Fails with UI_BLOCKED when a dialog or menu requires an explicit response; inspect status.ui.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.ensureMainMenu();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool('btd6_can_place_tower', {
        description: 'Check whether a tower of specified type can be placed at the given simulation coordinates (checks map boundaries, track collisions, terrain restrictions, and cash affordability).',
        inputSchema: z.object({
            towerType: z.string().describe('The tower type name, e.g. "DartMonkey", "TackShooter", "Quincy", etc.'),
            x: z.number().describe('Simulation X coordinate (typically -150 to 150)'),
            y: z.number().describe('Simulation Y coordinate (typically -115 to 115)')
        })
    }, retainRead('btd6_can_place_tower', async ({ towerType, x, y }) => {
        try {
            const result = await bridge.canPlaceTower({ towerType, x, y });
            return formatToolResult(result);
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool(
        'btd6_place_tower',
        {
            description: 'Place a tower or hero at the specified simulation coordinates. Deducts cash and creates the tower in the game simulation.',
            inputSchema: z.object({
                towerType: z.string().describe('The tower type name, e.g. "DartMonkey", "TackShooter", "Quincy", etc.'),
                x: z.number().describe('Simulation X coordinate (typically -150 to 150)'),
                y: z.number().describe('Simulation Y coordinate (typically -115 to 115)')
            })
        },
        async ({ towerType, x, y }) => {
            try {
                const result = await bridge.placeTower({ towerType, x, y });
                return formatToolResult({ ...result, tower: summarizeTower(result.tower) });
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_upgrade_tower',
        {
            description: 'Buy 1–15 ordered upgrade tiers (0=top, 1=middle, 2=bottom), immediately by default. Optional round/delaySeconds or delayFromNow schedule native simulation timing; whenAffordable waits for cash. Timed schedules expire at target-round end; affordability-only schedules persist across rounds. Purchases are not atomic: inspect appliedPaths, status, and failure. Cancellation never rolls back purchases.',
            inputSchema: upgradeTowerInputSchema
        },
        async options => {
            try {
                const result = await bridge.upgradeTower(options);
                return formatToolResult({ ...result, tower: result.tower === null ? null : summarizeTower(result.tower) });
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_cancel_scheduled_upgrade',
        {
            description: 'Cancel remaining purchases for one scheduled upgrade, or all pending upgrades when scheduleId is omitted or "all". Already applied upgrades remain.',
            inputSchema: z.object({ scheduleId: z.string().min(1).max(128).optional() }).strict()
        },
        async options => {
            try { return formatToolResult(await bridge.cancelScheduledUpgrade(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool('btd6_inspect_geraldo', {
        description: 'Inspect Geraldo’s native shop inventory, exact item IDs, prices, stock and unlock state. Items use tower placement (turret, idol, Quincy figure, genie, totem), eligible tower targets (pickles, camo potion, sharpening stone, Cape for Dart Monkeys, Gerry’s Fire, fertilizer for Banana Farms), or track points (nails, glue, blade trap). Rabbit and Rejuvenation take no target: rabbit placement is resolved automatically at Geraldo. Follow each item’s targetKind.',
        inputSchema: toolInput,
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_geraldo', async () => {
        try { return formatToolResult(await bridge.inspectGeraldo()); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool('btd6_can_use_geraldo_item', {
        description: 'Check native Geraldo item eligibility and price without spending or reserving stock. Use an exact item ID from inspect_geraldo. Supply its advertised point or tower target; omit target for rabbit and Rejuvenation. Fertilizer requires an eligible Banana Farm. Rabbit placement is validated automatically at Geraldo.',
        inputSchema: canUseGeraldoItemInputSchema,
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_can_use_geraldo_item', async options => {
        try { return formatToolResult(await bridge.canUseGeraldoItem(options)); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool(
        'btd6_use_geraldo_item',
        {
            description: 'Buy and apply one native Geraldo item from inspect_geraldo. Immediate by default, including mid-round; schedule with round/delaySeconds or delayFromNow. whenAffordable waits for cash, not stock. Timed schedules expire at target-round end; affordability-only schedules persist across rounds. Nothing is reserved. Follow targetKind: point for tower placement/track items, tower ID for buffs (fertilizer requires an eligible Banana Farm), no target for rabbit/Rejuvenation. Rabbit uses Geraldo’s current position when executed. Inspect failed or verification_pending outcomes before retrying.',
            inputSchema: useGeraldoItemInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.useGeraldoItem(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_cancel_scheduled_geraldo_purchase',
        {
            description: 'Cancel a pending scheduled Geraldo purchase by scheduleId, or all pending Geraldo purchases when scheduleId is omitted or "all". Already completed purchases remain; cancellation never rolls back purchases.',
            inputSchema: cancelScheduledGeraldoPurchaseInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.cancelScheduledGeraldoPurchase(options)); }
            catch (error) { return formatToolError(error); }
        }
    );
    server.registerTool('btd6_inspect_corvus', {
        description: 'Inspect Corvus’s native spellbook and live mana/readiness state. Use the exact spellId values returned here; Corvus spells have no point or tower targets. Read-only.',
        inputSchema: toolInput,
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_corvus', async () => {
        try { return formatToolResult(await bridge.inspectCorvus()); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool(
        'btd6_cast_corvus_spell',
        {
            description: 'Cast one unlocked native Corvus cast spell by exact spellId. Immediate by default; schedule with round/delaySeconds or delayFromNow. whenReady waits for transient readiness (mana, active spell, cooldown, recovery, stun or native restriction), not future unlocks. Native verification_pending results and uncertain transport errors require inspection before retrying; never replay them automatically.',
            inputSchema: castCorvusSpellInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.castCorvusSpell(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_set_corvus_spell',
        {
            description: 'Set one native Corvus continuous spell to the desired enabled state by exact spellId. Already-desired state completes without another activation or initial mana charge; disabling does not require activation mana. Enabling does not guarantee sustained uptime or auto-reenable after depletion. Immediate by default; schedule with round/delaySeconds or delayFromNow. whenReady waits only for transient readiness. Inspect native verification_pending results or uncertain transport errors before retrying; never replay automatically.',
            inputSchema: setCorvusSpellInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.setCorvusSpell(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_cancel_scheduled_corvus_action',
        {
            description: 'Cancel one pending scheduled Corvus action by scheduleId, or all pending Corvus actions when scheduleId is omitted or "all". This does not disable active spells. Completed, failed, expired, cancelled and verification_pending outcomes are never replayed or rolled back.',
            inputSchema: cancelScheduledCorvusActionInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.cancelScheduledCorvusAction(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_sell_tower',
        {
            description: 'Sell a placed tower for cash.',
            inputSchema: z.object({
                towerId: z.string().describe('The unique ID of the placed tower to sell')
            })
        },
        async ({ towerId }) => {
            try {
                const result = await bridge.sellTower({ towerId });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool('btd6_inspect_obstacles', {
        description: 'Inspect native paid removable obstacles in the active map: runtime IDs, positions, current prices, activity and availability. Re-read after removal or match replacement; null prices/state are unknown. Does not operate map gimmicks.',
        inputSchema: toolInput,
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_obstacles', async () => {
        try { return formatToolResult(await bridge.inspectObstacles()); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool(
        'btd6_remove_obstacle',
        {
            description: 'Purchase removal of a current obstacle by runtime ID from inspect_obstacles. Uses native affordability, mode checks and cash accounting. Calls the purchase once; if verification is pending, inspect again rather than resubmitting the purchase.',
            inputSchema: z.object({ obstacleId: z.string().trim().min(1).max(128) })
        },
        async options => {
            try { return formatToolResult(await bridge.removeObstacle(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool('btd6_inspect_tower_micro', {
        description: 'Read a placed tower’s available native targeting IDs, flight/movement modes, independent arms and coordinate targets. Use returned indices with set_target_priority or set_tower_target_position; upgrade/MK availability is native.',
        inputSchema: z.object({ towerId: z.string().trim().min(1).max(128) }),
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_tower_micro', async options => {
        try { return formatToolResult(await bridge.inspectTowerMicro(options)); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool('btd6_inspect_beast_merges', {
        description: 'Inspect a Beast Handler’s three paths: native power, donor/recipient relationships and legal merge recipient IDs. Inspect the intended donor; validRecipientTowerIds are keepers it can currently join. Null state is unknown.',
        inputSchema: z.object({ towerId: z.string().trim().min(1).max(128) }),
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_beast_merges', async options => {
        try { return formatToolResult(await bridge.inspectBeastMerges(options)); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool(
        'btd6_merge_beast',
        {
            description: 'Merge a donor Beast Handler’s selected path into a keeper through the native merge input. sourceTowerId is the donor; targetTowerId is the keeper. Native recipient, ownership, path and merge rules apply; towers are not sold or deleted. Inspect the donor first.',
            inputSchema: z.object({
                sourceTowerId: z.string().trim().min(1).max(128),
                targetTowerId: z.string().trim().min(1).max(128),
                path: z.number().int().min(0).max(2).describe('0 top/water, 1 middle/land, 2 bottom/air.')
            })
        },
        async options => {
            try { return formatToolResult(await bridge.mergeBeast(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_set_target_priority',
        {
            description: 'Select a native targeting mode advertised by inspect_tower_micro: ordinary priorities, Ace flight patterns, Heli movement modes, or Spike Factory priorities. Optional targetIndex selects an independent attack/arm where advertised. Upgrade-locked or unsupported options are rejected.',
            inputSchema: z.object({
                towerId: z.string().describe('The unique ID of the placed tower'),
                priority: z.string().trim().min(1).max(128).describe('Native option ID/name advertised by inspect_tower_micro.'),
                targetIndex: z.number().int().nonnegative().optional().describe('Independent attack/arm index from inspect_tower_micro; omit for whole-tower targeting.')
            })
        },
        async ({ towerId, priority, targetIndex }) => {
            try {
                const result = await bridge.setTargetPriority({ towerId, priority, targetIndex });
                const { micro, ...acknowledgement } = result;
                return formatToolResult({
                    ...acknowledgement,
                    activeTargetTypes: micro.targetTypeOptions.filter(option => option.active).map(option => option.id),
                    arms: micro.arms.map(({ index, targetPriority, targetTypeId }) => ({ index, targetPriority, targetTypeId })),
                    pathModes: micro.pathModes.filter(mode => mode.active)
                });
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_start_round',
        {
            description: 'Start the next Bloons round. By default, waits for completion. Successful rounds return a compact decision-ready result; defeat and timeout add telemetry and exact recovery selectors.',
            inputSchema: z.object({
                waitForCompletion: z.boolean().optional().default(true).describe('If true (default), waits until the round completes (or game victory/defeat occurs) and returns the post-round result. If false, returns immediately after issuing the start command.'),
                timeoutSeconds: z.number().optional().default(120).describe('Maximum seconds to wait for round completion when waitForCompletion is true. Defaults to 120 seconds.'),
                fastForward: z.boolean().optional().describe('Optionally set fast-forward before starting the round.')
            })
        },
        async ({ waitForCompletion, timeoutSeconds, fastForward }) => {
            try {
                const result = await bridge.startRound({ waitForCompletion, timeoutSeconds, fastForward, outputProfile: 'full' });
                return formatToolResult(await formatDecisionRound(bridge, result));
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_run_rounds',
        {
            description: 'Play 1-20 consecutive rounds with one call. Stops before requested strategic rounds and always stops on defeat, victory, timeout, match replacement, UI blockage, or an action error. Every underlying round remains separately journaled. If a later action errors, the response retains completed prefix outcomes and reports the failed iteration certainty; do not replay the completed prefix.',
            inputSchema: z.object({
                count: z.number().int().min(1).max(20).default(5).describe('Maximum rounds to play.'),
                stopBeforeRounds: z.array(z.number().int().positive()).max(20).default([])
                    .describe('Do not start a round in this list; return control immediately before it.'),
                timeoutSecondsPerRound: z.number().finite().positive().max(600).default(120)
                    .describe('Completion timeout applied independently to each round.'),
                fastForward: z.boolean().optional().default(true)
            }).strict()
        },
        async ({ count, stopBeforeRounds, timeoutSecondsPerRound, fastForward }) => {
            try {
                const batch = await runRoundBatch(bridge, {
                    count, stopBeforeRounds, timeoutSecondsPerRound, fastForward
                });
                const outcomes = [];
                for (const result of batch.outcomes) {
                    outcomes.push(await formatDecisionRound(bridge, result));
                }
                const output = {
                    requestedCount: batch.requestedCount,
                    attemptedCount: batch.attemptedCount,
                    completedRounds: batch.outcomes.flatMap(result =>
                        result.statusReason === 'round_cleared' && result.roundCompleted != null
                            ? [result.roundCompleted] : []),
                    stopReason: batch.stopReason,
                    nextPlayableRound: batch.nextPlayableRound,
                    failedIteration: batch.failure?.iteration ?? null,
                    failure: batch.failure,
                    outcomes
                };
                const aggregateError = roundBatchAggregateError(batch);
                if (aggregateError) return formatToolError(aggregateError, output);
                return formatToolResult(output);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_set_round',
        {
            description: 'Set the current round number in the match (particularly useful in Sandbox mode or research benchmarking).',
            inputSchema: z.object({
                round: z.number().int().min(1).describe('The target round number to set (must be >= 1)')
            })
        },
        async ({ round }) => {
            try {
                const result = await bridge.setRound({ round });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_advance_round',
        {
            description: 'Advance the match by one round, triggering end-of-round simulation events (end-of-round cash rewards, passive tower income, monkey bank compound interest, ability cooldown steps) without needing to play through bloon spawns.',
            inputSchema: z.object({})
        },
        async () => {
            try {
                const result = await bridge.advanceRound();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_sandbox_spawn_round',
        {
            description: 'In Sandbox mode, spawn the full natural wave of bloons for the specified round (or currently selected round). Allows live DPS and defense testing.',
            inputSchema: z.object({
                round: z.number().int().min(1).optional().describe('Optional round number whose bloons to spawn. If omitted, uses current round.')
            })
        },
        async ({ round }) => {
            try {
                const result = await bridge.sandboxSpawnRound({ round });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_sandbox_clear_bloons',
        {
            description: 'In Sandbox mode, immediately destroy/clear all active bloons on the track.',
            inputSchema: z.object({})
        },
        async () => {
            try {
                const result = await bridge.sandboxClearBloons();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_wait_for_round_end',
        {
            description: 'Wait until the current round completes. Returns compact decision-ready state; defeat and timeout retain diagnostic telemetry. Request btd6_observe separately for complete state.',
            inputSchema: z.object({
                timeoutSeconds: z.number().optional().default(120).describe('Maximum seconds to wait for round completion. Defaults to 120 seconds.')
            })
        },
        async ({ timeoutSeconds }) => {
            try {
                const result = await bridge.waitForRoundEnd({ timeoutSeconds, outputProfile: 'full' });
                return formatToolResult(formatRoundOutput(result, 'summary'));
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_set_game_speed',
        {
            description: 'Toggle fast forward mode on or off.',
            inputSchema: z.object({
                fastForward: z.boolean().optional().default(true).describe('True for fast forward speed (3x), false for normal speed (1x)')
            })
        },
        async ({ fastForward }) => {
            try {
                const result = await bridge.setGameSpeed({ fastForward });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_activate_ability',
        {
            description: 'Activate an ability by exact ID, unambiguous name, or abilityIndex. Supply the typed target advertised by observe.abilities[].targeting; omit it only for untargeted abilities.',
            inputSchema: activateAbilityInputSchema
        },
        async ({ abilityIndex, abilityId, target }) => {
            try {
                const result = await bridge.activateAbility({ abilityIndex, abilityId, target });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_respond_ui',
        {
            description: 'Respond to the exact current UI blocker from status.ui or a ui_blocked round result through native game controls. Use only advertised actions: next for level-up/hero-unlock screens and victory statistics; freeplay/home for victory choices; restart/home for defeat; collect/play/back for collection events; confirm/cancel/back for popups. Popup back is available only when the game permits native dismissal; it does not accept a purchase. No screen coordinates are required. Decisions and collection remain explicit. An action is issued once; accepted does not mean its transition completed—observe ui.ready or the next blocker.',
            inputSchema: z.object({
                blockerId: z.string().min(1).max(128),
                action: uiActionSchema,
                value: z.string().max(256).optional().describe('Tower ID from blocker.choices, required for select_tower.')
            })
        },
        async ({ blockerId, action, value }) => {
            try {
                const result = await bridge.respondUi({ blockerId, action, value });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_select_hero',
        {
            description: 'Select the active primary hero for matches (e.g. Quincy, ObynGreenfoot, Gwendolin, Sauda, Geraldo, Corvus, etc.).',
            inputSchema: z.object({
                hero: z.string().describe('Hero name to select')
            })
        },
        async ({ hero }) => {
            try {
                const result = await bridge.selectHero({ hero });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_pause_match',
        {
            description: 'Pause the current active match. Freezes the simulation clock, bloon movement, and projectiles without opening modal menus.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.pauseMatch();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_resume_match',
        {
            description: 'Resume the current paused match.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.resumeMatch();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_schedule_ability',
        {
            description: 'Schedule an ability using native simulation-round timing. Accepts the same typed target as activate_ability and pins the selected ability ID. Cooldown retries retain and revalidate that target.',
            inputSchema: scheduleAbilityInputSchema
        },
        async ({ round, delaySeconds, delayFromNow, abilityId, abilityIndex, target, autoRetry }) => {
            try {
                const result = await bridge.scheduleAbility({
                    round,
                    delaySeconds,
                    delayFromNow,
                    abilityId,
                    abilityIndex,
                    target,
                    autoRetry
                });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_cancel_scheduled_ability',
        {
            description: 'Cancel a pending scheduled ability by scheduleId, or cancel all scheduled abilities if scheduleId is omitted or "all".',
            inputSchema: z.object({
                scheduleId: z.string().optional().describe('Schedule ID to cancel (e.g. "sched_1_abc123"). If omitted or "all", cancels all pending schedules.')
            })
        },
        async ({ scheduleId }) => {
            try {
                const result = await bridge.cancelScheduledAbility({ scheduleId });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_save_checkpoint',
        {
            description: 'Create a named or round-based save checkpoint of the current match state (towers, upgrades, cash, health, round). Can be restored at any time, even after defeat. Labels are non-empty, at most 128 UTF-16 code units, and cannot contain control characters; omit label for a round-keyed save.',
            inputSchema: z.object({
                label: checkpointLabelSchema.optional().describe('Optional custom name for this checkpoint, e.g. "before_bfb" or "pre_farm"; omit for a round-keyed checkpoint.')
            }).strict()
        },
        async ({ label }) => {
            try {
                const result = await bridge.saveCheckpoint(label === undefined ? {} : { label });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_export_checkpoints',
        {
            description: 'Explicitly export current round-boundary state and/or retained checkpoints to a durable versioned disk bundle. Does not advance gameplay or evict checkpoints. Returns an exportId for later import; no automatic disk persistence.',
            inputSchema: exportCheckpointsInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.exportCheckpoints(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_import_checkpoints',
        {
            description: 'Validate and import a previously exported checkpoint bundle by exportId. Does not start, restore, or otherwise mutate the simulation. Rejects incompatible or corrupt bundles and collisions without partial import. Restore explicitly using checkpointId afterward.',
            inputSchema: importCheckpointsInputSchema
        },
        async options => {
            try { return formatToolResult(await bridge.importCheckpoints(options)); }
            catch (error) { return formatToolError(error); }
        }
    );

    server.registerTool(
        'btd6_restore_checkpoint',
        {
            description: 'Restore by imported checkpointId, local label, or round. Labels are non-empty, at most 128 UTF-16 code units, and cannot contain control characters. Waits for the restored between-round state and returns its compact tower/ability state. Restore clears all scheduled actions and round telemetry.',
            inputSchema: z.object({
                checkpointId: z.string().min(1).max(128).optional(),
                round: z.number().int().positive().optional(),
                label: checkpointLabelSchema.optional()
            }).strict().refine(value => [value.checkpointId, value.round, value.label].filter(v => v !== undefined).length <= 1, 'Provide at most one checkpoint selector.')
        },
        async options => {
            try {
                const { observation, ...result } = await bridge.restoreCheckpoint(options);
                return formatToolResult({
                    ...result,
                    ...(observation ? { state: formatObservationOutput(observation, 'summary') } : {})
                });
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool('btd6_list_checkpoints', {
        description: 'List all available round checkpoints and custom labeled checkpoints saved in the active match.',
        inputSchema: toolInput
    }, retainRead('btd6_list_checkpoints', async () => {
        try {
            const result = await bridge.listCheckpoints();
            return formatToolResult(result);
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool(
        'btd6_delete_checkpoint',
        {
            description: 'Delete an in-memory checkpoint by checkpointId, label, or round to manage capacity. Labels are non-empty, at most 128 UTF-16 code units, and cannot contain control characters. Does not delete exported disk bundles.',
            inputSchema: z.object({
                checkpointId: z.string().min(1).max(128).optional(),
                label: checkpointLabelSchema.optional(),
                round: z.number().int().positive().optional()
            }).strict().refine(value => [value.checkpointId, value.label, value.round].filter(v => v !== undefined).length === 1, 'Provide exactly one checkpoint selector.')
        },
        async options => {
            try {
                const result = await bridge.deleteCheckpoint(options);
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool('btd6_round_info', {
        description: 'Get round totals, threats, active paths, routing and traffic. Summary omits spawn groups; use outputProfile=full for group timing needed by ability schedules. durationSeconds is the last scheduled spawn, not round-clear time. requiresLeadPopping includes DDTs; hasLead identifies literal Lead bloons.',
        inputSchema: z.object({
            round: z.number().int().positive().optional().describe('Round number to inspect (1-100+). Defaults to the current round.'),
            outputProfile: outputProfileSchema
        })
    }, retainRead('btd6_round_info', async ({ round, outputProfile }) => {
        try {
            const result = await bridge.getRoundInfo({ round });
            return formatToolResult(formatRoundInfoOutput(result, outputProfile));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_find_placement_spots', {
        description: 'Find native-valid tower centers. summary output omits verbose track intervals; request full only when exact interval geometry is needed. supportCoverage ranks candidates by how many requested placed towers fit within the candidate model range. Coverage is geometric center distance, not proof of buff eligibility.',
        inputSchema: z.object({
            towerType: z.string().optional().default('DartMonkey').describe('Native tower ID or configured model name (e.g. Sauda, TackShooter, MonkeyVillage-020).'),
            limit: z.number().int().min(1).max(50).optional().default(20),
            near: z.object({
                x: z.number().finite(),
                y: z.number().finite(),
                radius: z.number().positive().max(500).optional().default(40)
            }).optional(),
            minDistanceToTrack: z.number().nonnegative().optional().default(0),
            maxDistanceToTrack: z.number().nonnegative().optional().default(100),
            withinRangeOfTowerId: z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Tower ID must not be blank').optional().describe('Restrict the candidate center to the placed tower model range.'),
            coverTowerIds: z.array(z.string().min(1).max(128)).min(1).max(20).optional().describe('Placed tower IDs the candidate support tower should geometrically cover. Required for supportCoverage ranking.'),
            rankingStrategy: z.enum(['balanced', 'distanceToSupport', 'distanceToTrack', 'trackCoverage', 'supportCoverage']).optional().default('balanced'),
            outputProfile: outputProfileSchema
        })
    }, retainRead('btd6_find_placement_spots', async ({ outputProfile, ...options }) => {
        try {
            const result = await bridge.findPlacementSpots(options);
            return formatToolResult(formatPlacementSpotsOutput(result, outputProfile));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_inspect_support_coverage', {
        description: 'Inspect the placed towers geometrically inside a Monkey Village or Alchemist model range. eligibilityVerified is false; use inspect_tower on recipients to verify native active buffs when eligibility matters.',
        inputSchema: z.object({
            towerId: z.string().min(1).max(128)
        }).strict()
    }, retainRead('btd6_inspect_support_coverage', async ({ towerId }) => {
        try {
            return formatToolResult(await bridge.inspectSupportCoverage({ towerId }));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_get_map_layout', {
        description: 'Get a tactical PNG plus exact simulation coordinates (+X right, +Y down), terrain, trackGraph, routing, traffic, blockers, numbered towers, and optional native range/line-of-sight overlays. All image layers use the same game-oriented transform; use returned coordinates directly for placement. Geometry is cached by live revision. Use format=geometry for structured data without an image. Native overlays describe sampled range/LOS only, not complete damage or projectile coverage.',
        inputSchema: z.object({
            format: z.enum(['image', 'geometry']).default('image'),
            round: z.number().int().positive().optional().describe('Optional round number to check which paths/lanes are active for that specific round. Defaults to current/upcoming round.'),
            rangeTowerIds: rangeTowerIdsSchema.describe('Optional placed tower IDs (at most 20) to resolve native range/LOS triangles and per-attack wall metadata. Global-range and unsupported towers are reported without fabricated circles.'),
            placementCandidates: z.array(z.object({ x: z.number().finite(), y: z.number().finite() }).strict()).max(50).optional().describe('Optional coordinates from find_placement_spots, labeled P1, P2, etc. Rendering does not revalidate them.')
        }).strict()
    }, retainRead('btd6_get_map_layout', async ({ format, round, rangeTowerIds, placementCandidates }) => {
        try {
            const result = await bridge.getMapLayout({ round, rangeTowerIds });
            if (format === 'geometry') {
                return formatToolResult(result);
            }
            const { png, summary } = await renderMap(result, { placementCandidates });
            return {
                content: [
                    { type: 'image' as const, data: png.toString('base64'), mimeType: 'image/png' },
                    { type: 'text' as const, text: JSON.stringify(summary) }
                ]
            };
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool(
        'btd6_set_tower_target_position',
        {
            description: 'Set a native selected-point target using simulation coordinates: Ace centered path, Heli lock/patrol, Engineer trap, Mortar, or another advertised selected-point attack. Inspect inspect_tower_micro first for valid mode and targetIndex. Does not silently switch modes.',
            inputSchema: z.object({
                towerId: z.string().describe('The unique ID of the placed tower to re-target'),
                x: z.number().finite().describe('Target X in simulation coordinates; use native map bounds.'),
                y: z.number().finite().describe('Target Y in simulation coordinates; use native map bounds.'),
                targetIndex: z.number().int().nonnegative().optional().describe('Point or independent attack index advertised by inspect_tower_micro.'),
                pointIndex: z.number().int().nonnegative().optional().describe('Patrol waypoint to replace with x,y; defaults to the first waypoint.'),
                points: z.array(z.object({ x: z.number().finite(), y: z.number().finite() })).min(1).max(64).optional().describe('Replace the complete native patrol waypoint array. Its length must equal the native waypoint count.')
            })
        },
        async ({ towerId, x, y, targetIndex, pointIndex, points }) => {
            try {
                const result = await bridge.setTowerTargetPosition({ towerId, x, y, targetIndex, pointIndex, points });
                const { micro, ...acknowledgement } = result;
                return formatToolResult({
                    ...acknowledgement,
                    coordinateTargets: micro.coordinateTargets
                });
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_toggle_submerge',
        {
            description: 'Toggle or explicitly set the submerge state for a Monkey Sub (3xx+ upgrade: Submerge and Support / Reactor / Energizer). When submerged, attacks are converted to decamo/submerge pulses; when surfaced, fires darts.',
            inputSchema: z.object({
                towerId: z.string().describe('The unique ID of the Monkey Sub tower'),
                submerged: z.boolean().optional().describe('Optional explicit desired submerged state (true = submerge, false = surface). If omitted, toggles current state.')
            })
        },
        async ({ towerId, submerged }) => {
            try {
                const result = await bridge.toggleSubmerge({ towerId, submerged });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_collect_bank',
        {
            description: 'Collect accumulated cash from a Monkey Bank (middle path Banana Farm, 030+). Immediately transfers all stored bank funds into the player wallet.',
            inputSchema: z.object({
                towerId: z.string().describe('Placed Monkey Bank tower ID')
            })
        },
        async ({ towerId }) => {
            try {
                const result = await bridge.collectBank({ towerId });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_set_auto_collect',
        {
            description: 'Toggle automatic collection of ground pickups (Banana Farm bananas/crates, supply drops). Enabled by default. When disabled, drops remain on the ground until collected manually or expired.',
            inputSchema: z.object({
                enabled: z.boolean().describe('Whether auto drop collection should be enabled')
            })
        },
        async ({ enabled }) => {
            try {
                const result = await bridge.setAutoCollect({ enabled });
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool(
        'btd6_collect_drops',
        {
            description: 'Manually sweep and collect all active dropped items (Banana Farm bananas/crates, supply drops) currently on the map. Returns count and cash collected.',
            inputSchema: toolInput
        },
        async () => {
            try {
                const result = await bridge.collectDrops();
                return formatToolResult(result);
            } catch (error) {
                return formatToolError(error);
            }
        }
    );

    server.registerTool('btd6_tower_catalog', {
        description: 'Read a live tower index with base costs, ranges and placement classes. Filter by exact native towerType (case-insensitive) to include its upgrades; outputProfile=full includes all upgrade details for matching towers.',
        inputSchema: z.object({
            towerType: z.string().trim().min(1).max(128).optional().describe('Exact native tower ID; includes upgrade details for this tower.'),
            outputProfile: outputProfileSchema
        }).strict()
    }, retainRead('btd6_tower_catalog', async ({ towerType, outputProfile }) => {
        try {
            const result = await bridge.towerCatalog();
            return formatToolResult(formatTowerCatalogOutput(result, outputProfile, { towerType }));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_boss_catalog', {
        description: 'Read the native boss roster, in-game English descriptions and baseline model variants. Descriptions and mechanics come from the installed game, not a fixed wiki roster. Baseline health is not an event-scaled encounter prediction; unsupported mechanics are explicit.',
        inputSchema: z.object({ bossType: z.string().trim().min(1).max(128).optional().describe('Optional native boss ID, for example Dreadbloon. Omit to list all bosses.') }),
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_boss_catalog', async options => {
        try { return formatToolResult(await bridge.bossCatalog(options)); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool('btd6_inspect_boss', {
        description: 'Inspect active bosses and segments: live HP, armour, tower-category immunity, invulnerability, skull progress, position and recognized special phases. Works between rounds. Select by runtime id, not model name. Segment HP may be a sentinel: do not sum it into encounter HP. Unknown mechanics and unavailable values are explicit; this does not predict final tower damage.',
        inputSchema: z.object({ bloonId: z.string().trim().min(1).max(128).optional().describe('Optional active boss/segment ID from observe.bosses or this tool. Omit for all active bosses.') }),
        annotations: { readOnlyHint: true, destructiveHint: false }
    }, retainRead('btd6_inspect_boss', async options => {
        try { return formatToolResult(await bridge.inspectBoss(options)); }
        catch (error) { return formatToolError(error); }
    }));

    server.registerTool('btd6_tower_stats', {
        description: 'Read live-model configuration stats: damage, cooldowns, pierce, debuffs, income and poppingCapabilities. camo distinguishes target detection from projectile collision, needsDetection and actual camo damage eligibility; null is unknown, not false. A targetless spike pile can damage camo without detection. DDT capability also requires compatible damage immunities, not just camo. Summary retains these distinctions; full adds nested damage sources/projectile behaviors. Components are not final DPS. Tiers are [top, middle, bottom].',
        inputSchema: z.object({
            towerType: z.string().min(1).describe('Internal tower ID or display name, for example DartMonkey or MonkeySub'),
            tiers: z.array(z.number().int().min(0).max(5)).length(3).optional(),
            outputProfile: outputProfileSchema
        })
    }, retainRead('btd6_tower_stats', async ({ towerType, tiers, outputProfile }) => {
        try {
            const result = await bridge.towerStats({ towerType, tiers: tiers as [number, number, number] | undefined });
            return formatToolResult(outputProfile === 'full'
                ? result
                : { ...result, stats: formatTowerStatsOutput(result.stats, outputProfile) });
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_inspect_tower', {
        description: 'Inspect a placed tower: effective poppingCapabilities, attacks, active buffs, debuffs and income. camo separates detection from collision and reports whether detection is needed, whether damage is possible, and native versus buffed evidence. Do not buy Radar Scanner merely because a camo bloon leaked: detection and damage throughput are different. Null capabilities are unknown. Summary omits duplicate model stats and nested damage sources; full retains diagnostics. Paragon degree fractions are not final boss damage.',
        inputSchema: z.object({
            towerId: z.string().min(1).describe('Placed tower ID returned by btd6_observe or btd6_place_tower'),
            outputProfile: outputProfileSchema
        })
    }, retainRead('btd6_inspect_tower', async ({ towerId, outputProfile }) => {
        try {
            const result = await bridge.inspectTower({ towerId });
            return formatToolResult(formatTowerInspectionOutput(result, outputProfile));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_get_projected_cash', {
        description: 'Project between-round cash using the active match model: recursive bloon cash, native income-scaling thresholds, and native round rewards. Returns separate baselineIncome and explicitly estimated towerIncome, confidence, assumptions, and unsupportedReasons. HalfCash/Deflation use their modified cash models, not extra multipliers. Unknown economic rules or generated waves return null for unavailable cash, never standard-mode fallback. Excludes future spending, bank withdrawals, and ability/attack-dependent bonus cash; ground-drop estimates assume collection. Read baselineIncome.confidence before budgeting; use round_info separately for threats.',
        inputSchema: z.object({
            targetRound: z.number().int().min(1).max(200).describe('Inclusive target round; generated waves beyond the active fixed round set are explicitly unsupported'),
            fromRound: z.number().int().min(1).max(200).optional().describe('Starting round, defaulting to the active match round; a different round requires explicit currentCash'),
            currentCash: z.number().finite().nonnegative().optional().describe('Cash available at the start of fromRound; defaults to current spendable cash only for the current round'),
            outputProfile: outputProfileSchema.describe('summary (default): totals, confidence and assumptions; full: also per-round income breakdown.')
        })
    }, retainRead('btd6_get_projected_cash', async ({ targetRound, fromRound, currentCash, outputProfile }) => {
        try {
            const result = await bridge.getProjectedCash({ targetRound, fromRound, currentCash });
            return formatToolResult(formatProjectedCashOutput(result, outputProfile));
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_project_paragon_degree', {
        description: 'Project a Paragon degree from the active game model and placed-tower state using the documented v39+ cash formula and native paragon price. The result exposes native contribution parameters, a theoretical cash-slider cap, tower eligibility, milestone reachability, and bounded slider sensitivity; player cash, UI state, and purchase readiness are not evaluated.',
        inputSchema: z.object({
            paragonType: z.string().min(1).max(64).refine(value => value.trim().length > 0, 'Paragon type must not be blank').describe('Native base tower ID for the paragon family, optionally with a "-Paragon" suffix (for example, "DartMonkey" or "DartMonkey-Paragon")'),
            additionalCashSlider: z.number().finite().int().nonnegative().optional().default(0).describe('Whole amount of additional cash to project through the native paragon creation slider; the active model cap is reported in the result'),
            excludeTowerIds: z.array(z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Tower ID must not be blank')).optional().describe('Optional list of placed tower IDs to exclude from the native contribution calculation')
        })
    }, retainRead('btd6_project_paragon_degree', async ({ paragonType, additionalCashSlider, excludeTowerIds }) => {
        try {
            const result = await bridge.projectParagonDegree({ paragonType, additionalCashSlider, excludeTowerIds });
            return formatToolResult(result);
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_inspect_temple_sacrifices', {
        description: 'Inspect prospective Sun Temple or True Sun God sacrifices from current tower centers, range, and native worth. Includes towers belonging to other players in co-op, excludes heroes/paragons/powers/subtowers, reports category $50,000 thresholds and unresolved Tier 4 ties. VTSG positioning is not proof of formation: prior sacrifices, Monkey Knowledge, and mode prerequisites remain explicitly unknown.',
        inputSchema: z.object({
            towerId: z.string().describe('The unique ID of the placed Super Monkey or Sun Temple to inspect sacrifices for')
        })
    }, retainRead('btd6_inspect_temple_sacrifices', async ({ towerId }) => {
        try {
            const result = await bridge.inspectTempleSacrifices({ towerId });
            return formatToolResult(result);
        } catch (error) {
            return formatToolError(error);
        }
    }));

    server.registerTool('btd6_inspect_monkeyopolis_sacrifices', {
        description: 'Inspect native Monkeyopolis eligibility for a Monkey City (0-0-4). Reports same-owner Banana Farms below Tier 5 in range, leaves Tier 5 farms explicitly ineligible, uses native tower-worth and Monkeyopolis model fields for supported income calculations, and marks standalone-farm income or payback as unavailable when existing income analysis cannot produce a fixed payout.',
        inputSchema: z.object({
            towerId: z.string().describe('The unique ID of the placed Monkey City (0-0-4 Village) to inspect for Monkeyopolis upgrade')
        })
    }, retainRead('btd6_inspect_monkeyopolis_sacrifices', async ({ towerId }) => {
        try {
            const result = await bridge.inspectMonkeyopolisSacrifices({ towerId });
            return formatToolResult(result);
        } catch (error) {
            return formatToolError(error);
        }
    }));

    return server;
}


void serveStdio(createServer);
console.error('btd6-mcp 0.1.0 serving stdio MCP; stdout is reserved for MCP messages.');
