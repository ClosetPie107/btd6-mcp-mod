import * as z from 'zod/v4';

const FLOAT32_MAX = 3.4028234663852886e38;
const point = z.object({ x: z.number().finite(), y: z.number().finite() });
const abilityCoordinate = z.number().finite().min(-FLOAT32_MAX).max(FLOAT32_MAX);
const abilityPoint = z.object({ x: abilityCoordinate, y: abilityCoordinate }).strict();
const abilityTowerId = z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Tower ID must not be blank');
const abilityId = z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Ability ID must not be blank');
/** Matches native CheckpointLabelValidation: UTF-16 length 1-128 and no C0/C1 controls. */
export const checkpointLabelSchema = z.string()
    .min(1)
    .max(128)
    .refine(value => !/[\u0000-\u001F\u007F-\u009F]/.test(value), 'Checkpoint labels cannot contain control characters.');

export const abilityTargetSchema = z.discriminatedUnion('kind', [
    z.object({ kind: z.literal('point'), x: abilityCoordinate, y: abilityCoordinate }).strict(),
    z.object({ kind: z.literal('tower'), towerId: abilityTowerId }).strict(),
    z.object({
        kind: z.literal('tower_position'),
        towerId: abilityTowerId,
        x: abilityCoordinate,
        y: abilityCoordinate
    }).strict(),
    z.object({
        kind: z.literal('points'),
        points: z.array(abilityPoint).min(1).max(32)
    }).strict()
]);
export type AbilityTarget = z.infer<typeof abilityTargetSchema>;

export const abilityTargetingSchema = z.object({
    kind: z.enum(['none', 'point', 'tower', 'tower_position', 'points', 'unsupported']),
    inputClass: z.string().nullable(),
    supported: z.boolean(),
    pointCount: z.number().int().min(1).max(32).nullable().optional()
}).strict();
export type AbilityTargeting = z.infer<typeof abilityTargetingSchema>;

function rejectAmbiguousAbilitySelector(value: { abilityIndex?: number; abilityId?: string }, context: z.RefinementCtx): void {
    if (value.abilityIndex !== undefined && value.abilityId !== undefined) {
        context.addIssue({
            code: 'custom',
            message: 'Provide only one of abilityIndex or abilityId.'
        });
    }
}

export const activateAbilityInputSchema = z.object({
    abilityIndex: z.number().int().nonnegative().optional().describe('Zero-based index into the available abilities list'),
    abilityId: abilityId.optional().describe('Exact ability ID or unambiguous name/displayName to activate'),
    target: abilityTargetSchema.optional().describe('Optional typed target for the ability')
}).strict().superRefine(rejectAmbiguousAbilitySelector);

export const scheduleAbilityInputSchema = z.object({
    round: z.number().finite().int().positive().optional().describe('Target round to trigger on (e.g. 40). Defaults to current round.'),
    delaySeconds: z.number().finite().nonnegative().optional().describe('Simulation seconds into the target round to trigger (e.g. 5.5).'),
    delayFromNow: z.number().finite().nonnegative().optional().describe('Alternative: seconds from current moment to trigger on current round.'),
    abilityId: abilityId.optional().describe('Exact ability ID or unambiguous name. Defaults to first ability if omitted.'),
    abilityIndex: z.number().int().nonnegative().optional().describe('Zero-based index in the abilities list.'),
    target: abilityTargetSchema.optional().describe('Optional typed target for the ability'),
    autoRetry: z.boolean().optional().default(true).describe('Retry each frame if the ability is still on cooldown when the timer fires.')
}).strict().superRefine(rejectAmbiguousAbilitySelector);
export type ActivateAbilityInput = z.input<typeof activateAbilityInputSchema>;
export type ScheduleAbilityInput = z.input<typeof scheduleAbilityInputSchema>;

export const upgradeTowerInputSchema = z.object({
    towerId: abilityTowerId,
    upgradeSequence: z.array(z.number().int().min(0).max(2)).min(1).max(15),
    whenAffordable: z.boolean().optional().describe('Buy affordable steps, then wait for cash. Without timing, waits across rounds; with timing, expires at the end of the target round.'),
    round: z.number().int().positive().optional().describe('Target round; timing defaults to its start.'),
    delaySeconds: z.number().finite().nonnegative().optional().describe('Native simulation seconds into the target round, not wall time.'),
    delayFromNow: z.number().finite().nonnegative().optional().describe('Native simulation seconds from now; requires an active round.'),
    idempotencyKey: z.string().min(1).max(128).optional().describe('Reuse the same key only for the identical scheduled request after an uncertain response.')
}).strict().superRefine((value, context) => {
    if (value.delayFromNow !== undefined && (value.round !== undefined || value.delaySeconds !== undefined)) {
        context.addIssue({ code: 'custom', message: 'delayFromNow cannot be combined with round or delaySeconds.' });
    }
});
export type UpgradeTowerInput = z.input<typeof upgradeTowerInputSchema>;
export const geraldoTargetSchema = z.discriminatedUnion('kind', [
    z.object({
        kind: z.literal('point'),
        x: abilityCoordinate,
        y: abilityCoordinate
    }).strict(),
    z.object({
        kind: z.literal('tower'),
        towerId: abilityTowerId
    }).strict()
]);
export type GeraldoTarget = z.infer<typeof geraldoTargetSchema>;

export const geraldoItemCategorySchema = z.enum(['tower_placement', 'tower_target', 'geraldo_range', 'track', 'none']);
export type GeraldoItemCategory = z.infer<typeof geraldoItemCategorySchema>;
export const geraldoTargetKindSchema = z.enum(['point', 'tower', 'none']);
export type GeraldoTargetKind = z.infer<typeof geraldoTargetKindSchema>;

const geraldoItemIdSchema = z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Item ID must not be blank');
const geraldoFailureSchema = z.object({
    code: z.string(),
    message: z.string()
}).strict();
const geraldoPurchaseStatusSchema = z.enum(['pending', 'waiting', 'completed', 'failed', 'expired', 'cancelled', 'verification_pending']);

export const inspectGeraldoResultSchema = z.object({
    source: z.literal('active-simulation'),
    heroTowerId: abilityTowerId,
    heroLevel: z.number().int().nonnegative(),
    cash: z.number().finite(),
    items: z.array(z.object({
        itemId: geraldoItemIdSchema,
        name: z.string(),
        description: z.string().nullable(),
        category: geraldoItemCategorySchema,
        targetKind: geraldoTargetKindSchema,
        cost: z.number().finite().nonnegative().nullable(),
        stock: z.number().int().nonnegative(),
        maxStock: z.number().int().nonnegative(),
        unlockLevel: z.number().int().nonnegative(),
        available: z.boolean(),
        unavailableReason: z.string().nullable(),
        roundsToReplenish: z.number().int().nonnegative(),
        replenishingFromRound: z.number().int().positive().nullable(),
        canBeActivatedBetweenRounds: z.boolean()
    }).strict()).max(16)
}).strict();
export type InspectGeraldoResult = z.infer<typeof inspectGeraldoResultSchema>;

export const canUseGeraldoItemInputSchema = z.object({
    itemId: geraldoItemIdSchema,
    target: geraldoTargetSchema.optional()
}).strict();
export type CanUseGeraldoItemInput = z.input<typeof canUseGeraldoItemInputSchema>;

export const canUseGeraldoItemResultSchema = z.object({
    valid: z.boolean(),
    code: z.string().nullable(),
    message: z.string().nullable(),
    itemId: geraldoItemIdSchema,
    category: geraldoItemCategorySchema,
    target: geraldoTargetSchema.nullable(),
    cost: z.number().finite().nonnegative().nullable(),
    cash: z.number().finite(),
    stock: z.number().int().nonnegative()
}).strict();
export type CanUseGeraldoItemResult = z.infer<typeof canUseGeraldoItemResultSchema>;

export const useGeraldoItemInputSchema = z.object({
    itemId: geraldoItemIdSchema,
    target: geraldoTargetSchema.optional(),
    whenAffordable: z.boolean().optional(),
    round: z.number().int().positive().optional(),
    delaySeconds: z.number().finite().nonnegative().optional(),
    delayFromNow: z.number().finite().nonnegative().optional(),
    idempotencyKey: z.string().min(1).max(128).optional()
}).strict().superRefine((value, context) => {
    if (value.delayFromNow !== undefined && (value.round !== undefined || value.delaySeconds !== undefined)) {
        context.addIssue({ code: 'custom', message: 'delayFromNow cannot be combined with round or delaySeconds.' });
    }
});
export type UseGeraldoItemInput = z.input<typeof useGeraldoItemInputSchema>;

export const useGeraldoItemResultSchema = z.object({
    status: geraldoPurchaseStatusSchema,
    purchased: z.boolean(),
    scheduleId: z.string().nullable(),
    itemId: geraldoItemIdSchema,
    target: geraldoTargetSchema.nullable(),
    cost: z.number().finite().nonnegative().nullable(),
    cashBefore: z.number().finite().nullable(),
    cashAfter: z.number().finite().nullable(),
    stockBefore: z.number().int().nonnegative().nullable(),
    stockAfter: z.number().int().nonnegative().nullable(),
    createdTowerIds: z.array(z.string()).max(128),
    affectedTowerId: z.string().nullable(),
    failure: geraldoFailureSchema.nullable(),
    waitingFor: z.string().nullable(),
    executionRound: z.number().int().positive().nullable(),
    executionSeconds: z.number().finite().nonnegative().nullable()
}).strict();
export type UseGeraldoItemResult = z.infer<typeof useGeraldoItemResultSchema>;

export const scheduledGeraldoPurchaseSchema = z.object({
    scheduleId: z.string(),
    itemId: geraldoItemIdSchema,
    target: geraldoTargetSchema.nullable(),
    whenAffordable: z.boolean(),
    targetRound: z.number().int().positive().nullable(),
    delaySeconds: z.number().finite().nonnegative().nullable(),
    status: geraldoPurchaseStatusSchema,
    waitingFor: z.string().nullable(),
    failure: geraldoFailureSchema.nullable(),
    result: useGeraldoItemResultSchema.nullable()
}).strict();
export type ScheduledGeraldoPurchase = z.infer<typeof scheduledGeraldoPurchaseSchema>;

export const cancelScheduledGeraldoPurchaseInputSchema = z.object({
    scheduleId: z.string().min(1).max(128).optional()
}).strict();
export type CancelScheduledGeraldoPurchaseInput = z.input<typeof cancelScheduledGeraldoPurchaseInputSchema>;
export const cancelScheduledGeraldoPurchaseResultSchema = z.object({
    cancelled: z.boolean(),
    count: z.number().int().nonnegative()
}).strict();
export type CancelScheduledGeraldoPurchaseResult = z.infer<typeof cancelScheduledGeraldoPurchaseResultSchema>;
const corvusSpellIdSchema = z.string().min(1).max(128)
    .refine(value => value.trim().length > 0, 'Spell ID must not be blank');
const corvusIdempotencyKeySchema = z.string().min(1).max(128)
    .refine(value => value.trim().length > 0, 'Idempotency key must not be blank');
const corvusTimingInput = {
    round: z.number().int().positive().optional(),
    delaySeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).optional(),
    delayFromNow: z.number().finite().nonnegative().max(FLOAT32_MAX).optional(),
    whenReady: z.boolean().optional().default(false),
    idempotencyKey: corvusIdempotencyKeySchema.optional()
};
const corvusTimingRefinement = (value: {
    delayFromNow?: number;
    round?: number;
    delaySeconds?: number;
}, context: z.RefinementCtx): void => {
    if (value.delayFromNow !== undefined && (value.round !== undefined || value.delaySeconds !== undefined)) {
        context.addIssue({ code: 'custom', message: 'delayFromNow cannot be combined with round or delaySeconds.' });
    }
};

export const castCorvusSpellInputSchema = z.object({
    spellId: corvusSpellIdSchema,
    ...corvusTimingInput
}).strict().superRefine(corvusTimingRefinement);
export type CastCorvusSpellInput = z.input<typeof castCorvusSpellInputSchema>;

export const setCorvusSpellInputSchema = z.object({
    spellId: corvusSpellIdSchema,
    enabled: z.boolean(),
    ...corvusTimingInput
}).strict().superRefine(corvusTimingRefinement);
export type SetCorvusSpellInput = z.input<typeof setCorvusSpellInputSchema>;

const corvusSpellKindSchema = z.enum(['continuous', 'cast']);
const corvusFailureSchema = z.object({
    code: z.string(),
    message: z.string()
}).strict();
const corvusActionStatusSchema = z.enum([
    'pending', 'waiting', 'completed', 'failed', 'expired', 'cancelled', 'verification_pending'
]);

export const corvusSpellInfoSchema = z.object({
    spellId: corvusSpellIdSchema,
    name: z.string(),
    kind: corvusSpellKindSchema,
    unlockLevel: z.number().int().positive(),
    unlocked: z.boolean(),
    initialManaCost: z.number().int().nonnegative(),
    ongoingManaCost: z.number().int().nonnegative().nullable(),
    manaDrainIntervalSeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).nullable(),
    durationSeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).nullable(),
    cooldownSeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).nullable(),
    active: z.boolean(),
    canCast: z.boolean(),
    cooldownPercent: z.number().finite().min(0).max(1),
    activeDurationRemainingPercent: z.number().finite().min(0).max(1),
    unavailableReason: z.string().nullable()
}).strict();
export type CorvusSpellInfo = z.infer<typeof corvusSpellInfoSchema>;

export const inspectCorvusResultSchema = z.object({
    source: z.literal('active-simulation'),
    heroTowerId: abilityTowerId,
    heroLevel: z.number().int().positive(),
    mana: z.number().int().nonnegative(),
    maxMana: z.number().int().nonnegative(),
    isRecovering: z.boolean(),
    isStunned: z.boolean(),
    isManaDraining: z.boolean(),
    spells: z.array(corvusSpellInfoSchema).max(16)
}).strict();
export type InspectCorvusResult = z.infer<typeof inspectCorvusResultSchema>;

export const corvusActionResultSchema = z.object({
    status: corvusActionStatusSchema,
    executed: z.boolean(),
    changed: z.boolean(),
    scheduleId: z.string().nullable(),
    heroTowerId: z.string().nullable(),
    spellId: corvusSpellIdSchema,
    enabled: z.boolean().nullable(),
    manaBefore: z.number().int().nonnegative().nullable(),
    manaAfter: z.number().int().nonnegative().nullable(),
    spell: corvusSpellInfoSchema.nullable(),
    failure: corvusFailureSchema.nullable(),
    waitingFor: z.string().nullable(),
    executionRound: z.number().int().positive().nullable(),
    executionSeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).nullable()
}).strict();
export type CorvusActionResult = z.infer<typeof corvusActionResultSchema>;

export const scheduledCorvusActionSchema = z.object({
    scheduleId: z.string(),
    heroTowerId: z.string(),
    spellId: corvusSpellIdSchema,
    enabled: z.boolean().nullable(),
    whenReady: z.boolean(),
    targetRound: z.number().int().positive().nullable(),
    delaySeconds: z.number().finite().nonnegative().max(FLOAT32_MAX).nullable(),
    status: corvusActionStatusSchema,
    waitingFor: z.string().nullable(),
    failure: corvusFailureSchema.nullable(),
    result: corvusActionResultSchema.nullable()
}).strict();
export type ScheduledCorvusAction = z.infer<typeof scheduledCorvusActionSchema>;

export const castCorvusSpellResultSchema = corvusActionResultSchema;
export type CastCorvusSpellResult = CorvusActionResult;
export const setCorvusSpellResultSchema = corvusActionResultSchema;
export type SetCorvusSpellResult = CorvusActionResult;

export const cancelScheduledCorvusActionInputSchema = z.object({
    scheduleId: z.string().min(1).max(128)
        .refine(value => value.trim().length > 0, 'Schedule ID must not be blank').optional()
}).strict();
export type CancelScheduledCorvusActionInput = z.input<typeof cancelScheduledCorvusActionInputSchema>;

export const cancelScheduledCorvusActionResultSchema = z.object({
    cancelled: z.boolean(),
    count: z.number().int().nonnegative()
}).strict();
export type CancelScheduledCorvusActionResult = z.infer<typeof cancelScheduledCorvusActionResultSchema>;

export const exportCheckpointsInputSchema = z.object({
    includeCurrent: z.boolean().optional().default(true),
    includeSaved: z.boolean().optional().default(true)
}).strict().refine(value => value.includeCurrent || value.includeSaved, 'Select current state, saved checkpoints, or both.');

export const importCheckpointsInputSchema = z.object({
    exportId: z.string().min(1).max(128).regex(/^[A-Za-z0-9_-]+$/)
}).strict();

const roundBloonPropertiesSchema = z.object({
    baseId: z.string(), camo: z.boolean(), fortified: z.boolean(),
    regrow: z.boolean(), lead: z.boolean(), moab: z.boolean()
});
const roundThreatSchema = roundBloonPropertiesSchema.extend({
    trackProgress: z.number().finite().min(0).max(1).nullable(),
    pathIndex: z.number().int().nullable(),
    health: z.number().int().nonnegative().nullable(),
    x: z.number().finite().nullable(),
    y: z.number().finite().nullable()
});
export const postSpawnPressureSchema = z.object({
    available: z.boolean(),
    basis: z.literal('sampled_after_native_spawn_schedule_end'),
    sampleCount: z.number().int().nonnegative(),
    observedDurationSeconds: z.number().finite().nonnegative(),
    moabPresentDurationSeconds: z.number().finite().nonnegative(),
    ceramicPresentDurationSeconds: z.number().finite().nonnegative(),
    peakMoabCount: z.number().int().nonnegative(),
    peakCeramicCount: z.number().int().nonnegative(),
    peakNearExitMoabCount: z.number().int().nonnegative(),
    peakNearExitCeramicCount: z.number().int().nonnegative(),
    maxMoabProgress: z.number().finite().min(0).max(1).nullable(),
    maxCeramicProgress: z.number().finite().min(0).max(1).nullable()
}).strict();

export const roundInsightsSchema = z.object({
    matchId: z.string().nullable(),
    matchGeneration: z.number().int().nonnegative(),
    round: z.number().int().positive(),
    status: z.enum(['active', 'completed', 'victory', 'defeat']),
    observedAtUtc: z.string(),
    nativeElapsedSeconds: z.number().finite().nonnegative().nullable(),
    lastSpawnDurationSeconds: z.number().finite().nonnegative().nullable(),
    remainingScheduledSpawns: z.number().int().nonnegative().nullable(),
    activeBloonCount: z.number().int().nonnegative().nullable(),
    remainingScheduledSpawnsKnown: z.boolean(),
    estimatedRemainingScheduledSpawns: z.number().int().nonnegative().nullable(),
    spawningComplete: z.boolean().nullable(),
    omittedScheduleGroups: z.number().int().nonnegative(),
    activeMoabCount: z.number().int().nonnegative().nullable(),
    maxTrackProgress: z.number().finite().min(0).max(1).nullable(),
    peakBloonCount: z.number().int().nonnegative(),
    peakMoabCount: z.number().int().nonnegative(),
    peakNearExitCount: z.number().int().nonnegative(),
    nearExitSampledDurationSeconds: z.number().finite().nonnegative(),
    postSpawnPressure: postSpawnPressureSchema,
    samples: z.number().int().nonnegative(),
    coverage: z.object({
        requestedIntervalSeconds: z.number().finite().positive(),
        sampleCount: z.number().int().nonnegative(),
        missingSampleCount: z.number().int().nonnegative(),
        firstSampleElapsedSeconds: z.number().finite().nonnegative().nullable(),
        lastSampleElapsedSeconds: z.number().finite().nonnegative().nullable(),
        nativeClockAvailable: z.boolean(),
        samplingBasis: z.string(),
        completeness: z.enum(['none', 'sampled', 'partial']),
        missing: z.array(z.string()).max(32),
        leakCapture: z.string(),
        expectedDamageBasis: z.string(),
        nearExitThreshold: z.number().finite().min(0).max(1).optional(),
        progressBasis: z.string().optional(),
        progressBoundary: z.string().optional(),
        remainingSpawnsBasis: z.string().optional()
    }),
    worstMoment: z.object({
        elapsedSeconds: z.number().finite().nonnegative().nullable(),
        maxTrackProgress: z.number().finite().min(0).max(1).nullable(),
        bloonCount: z.number().int().nonnegative(),
        moabCount: z.number().int().nonnegative(),
        nearExitCount: z.number().int().nonnegative(),
        leadCount: z.number().int().nonnegative(),
        camoCount: z.number().int().nonnegative(),
        pathIndex: z.number().int().nullable()
    }).nullable(),
    pressureByLane: z.array(z.object({
        pathIndex: z.number().int(),
        peakBloonCount: z.number().int().nonnegative(),
        peakMoabCount: z.number().int().nonnegative(),
        peakNearExitCount: z.number().int().nonnegative(),
        maxTrackProgress: z.number().finite().min(0).max(1).nullable(),
        nearExitSampledDurationSeconds: z.number().finite().nonnegative(),
        samples: z.number().int().nonnegative()
    })).max(64),
    composition: z.array(roundBloonPropertiesSchema.extend({
        peakActiveCount: z.number().int().nonnegative(),
        activeCount: z.number().int().nonnegative(),
        samplesObserved: z.number().int().nonnegative()
    })).max(20),
    omittedCompositionGroups: z.number().int().nonnegative(),
    topThreats: z.array(roundThreatSchema).max(5),
    activeThreats: z.array(roundThreatSchema).max(5),
    leaks: z.object({
        total: z.number().int().nonnegative(),
        retained: z.number().int().min(0).max(15),
        omitted: z.number().int().nonnegative(),
        entries: z.array(roundBloonPropertiesSchema.extend({
            pathIndex: z.number().int().nullable(),
            trackProgress: z.number().finite().min(0).max(1).nullable(),
            x: z.number().finite().nullable(),
            y: z.number().finite().nullable(),
            expectedDamage: z.number().finite().nonnegative().nullable(),
            actualHealthLoss: z.number().finite().nonnegative().nullable(),
            elapsedSeconds: z.number().finite().nonnegative().nullable()
        })).max(15)
    }),
    actions: z.array(z.object({
        scheduleId: z.string(), actionKind: z.string(),
        targetRound: z.number().int().positive(), outcome: z.string(),
        error: z.string().nullable(),
        elapsedSeconds: z.number().finite().nonnegative().nullable()
    })).max(32)
});
export type RoundInsights = z.infer<typeof roundInsightsSchema>;

export const uiActionSchema = z.enum(['confirm', 'cancel', 'next', 'select_tower', 'unlock', 'back', 'freeplay', 'home', 'restart', 'collect', 'play']);
export const uiBlockerSchema = z.object({
    id: z.string().min(1).max(128),
    kind: z.enum(['popup', 'tutorial', 'level_up', 'tower_unlock', 'rewards', 'transition', 'menu', 'startup', 'unknown']),
    screen: z.string().max(256),
    state: z.enum(['waiting', 'acting', 'blocked', 'failed']),
    reason: z.string().max(4096).nullable(),
    title: z.string().max(4096).nullable(),
    body: z.string().max(4096).nullable(),
    actions: z.array(uiActionSchema).max(6),
    choices: z.array(z.string().max(256)).max(64),
    selectedChoice: z.string().max(256).nullable()
});
export const uiStateSchema = z.object({
    autoHandlingEnabled: z.boolean(),
    ready: z.boolean(),
    blocker: uiBlockerSchema.nullable()
}).refine(value => value.ready === (value.blocker === null), 'UI readiness must agree with its blocker');
export type UiState = z.infer<typeof uiStateSchema>;
export type UiAction = z.infer<typeof uiActionSchema>;

export const progressSchema = z.object({
    observedAtUtc: z.iso.datetime({ offset: true }),
    matchId: z.string().nullable(),
    activeGame: z.boolean(),
    ui: uiStateSchema,
    gameStatus: z.enum(['in_game', 'round_in_progress', 'paused', 'victory', 'defeat']).nullable(),
    round: z.number().int().positive().nullable(),
    roundActive: z.boolean().nullable(),
    canStartRound: z.boolean().nullable(),
    cash: z.number().finite().nullable(),
    lives: z.number().finite().nullable()
});
export type RoundProgress = z.infer<typeof progressSchema>;

export const configurationSchema = z.object({
    checkpointMode: z.enum(['manual', 'every_round', 'selected_rounds', 'assisted']),
    checkpointRounds: z.array(z.number().int().min(1).max(10000)).max(100),
    maxRoundCheckpoints: z.number().int().min(1).max(100),
    workBudgetMs: z.number().min(0.25).max(8),
    maxPlacementChecksPerFrame: z.number().int().min(1).max(64)
}).strict();
export const configurationInputSchema = configurationSchema.partial();
export type BridgeConfiguration = z.infer<typeof configurationSchema>;

const timingSchema = z.object({
    count: z.number().int().nonnegative(),
    p50Ms: z.number().nonnegative(), p95Ms: z.number().nonnegative(),
    p99Ms: z.number().nonnegative(), maxMs: z.number().nonnegative()
});
export const performanceSchema = z.object({
    capturedAtUtc: z.iso.datetime({ offset: true }),
    frames: timingSchema,
    operations: z.array(z.object({ name: z.string(), timing: timingSchema })).max(64),
    workerOperations: z.array(z.object({ name: z.string(), timing: timingSchema })).max(8),
    recentSpikes: z.array(z.object({
        frame: z.number().int().nonnegative(), observedAtUtc: z.iso.datetime({ offset: true }),
        intervalMs: z.number().nonnegative(),
        previousUpdateOperations: z.array(z.object({ operation: z.string(), milliseconds: z.number().nonnegative() })).max(32)
    })).max(64),
    configuration: configurationSchema,
    pendingCommands: z.number().int().nonnegative(),
    mailboxError: z.string().nullable(),
    notes: z.string()
});
export type BridgePerformance = z.infer<typeof performanceSchema>;

const MAX_GRAPH_PATHS = 512;
const MAX_GRAPH_POINTS = 32768;
const MAX_GRAPH_SPLIT_POINTS = MAX_GRAPH_POINTS * 2;

export const trackNodeSchema = z.object({
    id: z.string(),
    kind: z.enum(['entry', 'exit', 'junction', 'merge']),
    position: point,
    compass: z.string()
});
export type TrackNode = z.infer<typeof trackNodeSchema>;

export const trackEdgeSchema = z.object({
    id: z.string(),
    from: z.string(),
    to: z.string(),
    length: z.number().nonnegative(),
    routes: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS),
    waypoints: z.array(point).max(MAX_GRAPH_POINTS).optional()
});
export type TrackEdge = z.infer<typeof trackEdgeSchema>;

export const trackRouteSchema = z.object({
    routeId: z.number().int().nonnegative(),
    label: z.string(),
    edgeSequence: z.array(z.string()).max(MAX_GRAPH_POINTS),
    totalLength: z.number().nonnegative(),
    entryNode: z.string(),
    exitNode: z.string()
});
export type TrackRoute = z.infer<typeof trackRouteSchema>;

export const routingPolicySchema = z.object({
    type: z.string(),
    description: z.string(),
    activeRoutes: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS),
    selectionOrder: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS),
    bossRoute: z.number().int().nullable().optional(),
    routeForRound: z.number().int().nullable().optional()
});
export type RoutingPolicy = z.infer<typeof routingPolicySchema>;

export const edgeTrafficSchema = z.object({
    regularShare: z.number().nonnegative(),
    bossShare: z.number().nonnegative()
});
export type EdgeTraffic = z.infer<typeof edgeTrafficSchema>;

const routeTrafficSchema = z.record(z.string(), z.number().nonnegative())
    .refine(value => Object.keys(value).length <= MAX_GRAPH_PATHS, 'Route traffic exceeds the 512-path producer bound');
const edgeTrafficMapSchema = z.record(z.string(), edgeTrafficSchema)
    .refine(value => Object.keys(value).length <= MAX_GRAPH_POINTS, 'Edge traffic exceeds the topology producer bound');
export const trackTrafficSchema = z.object({
    regularRouteShare: routeTrafficSchema,
    bossRouteShare: routeTrafficSchema,
    edgeTraffic: edgeTrafficMapSchema
});
export type TrackTraffic = z.infer<typeof trackTrafficSchema>;

export const trackGraphSchema = z.object({
    nodes: z.array(trackNodeSchema).max(MAX_GRAPH_POINTS),
    edges: z.array(trackEdgeSchema).max(MAX_GRAPH_POINTS),
    routes: z.array(trackRouteSchema).max(MAX_GRAPH_PATHS),
    routingPolicy: routingPolicySchema,
    traffic: trackTrafficSchema
}).refine(graph => graph.edges.reduce((points, edge) => points + (edge.waypoints?.length ?? 0), 0) <= MAX_GRAPH_SPLIT_POINTS,
    'Track graph waypoints exceed the 65536-point topology producer bound');
export type TrackGraph = z.infer<typeof trackGraphSchema>;


// Action Response Schemas for Runtime Protocol Validation
export const canPlaceTowerResultSchema = z.object({
    canPlace: z.boolean(),
    affordable: z.boolean(),
    valid: z.boolean().optional(),
    cost: z.number().finite(),
    cash: z.number().finite(),
    reason: z.string().nullable()
});


export const nextUpgradeSchema = z.object({
    path: z.number().int().min(0).max(2),
    name: z.string().nullable(),
    cost: z.number().finite().nonnegative().nullable(),
    available: z.boolean()
});
export const supportRecipientSchema = z.object({
    towerId: z.string(),
    towerType: z.string(),
    position: point,
    distance: z.number().finite().nonnegative(),
    rangeMargin: z.number().finite()
});
export const supportCoverageSchema = z.object({
    towerId: z.string(),
    towerType: z.string(),
    supportRange: z.number().finite().nonnegative(),
    coverageBasis: z.literal('model_range_center_distance'),
    eligibilityVerified: z.boolean(),
    recipients: z.array(supportRecipientSchema).max(4096),
    strategicWarning: z.string().nullable()
});
export const placeTowerResultSchema = z.object({
    placed: z.boolean(),
    tower: z.object({
        id: z.string(),
        towerType: z.string(),
        name: z.string(),
        position: point,
        range: z.number().nonnegative(),
        targetPriority: z.string(),
        tiers: z.array(z.number().int()).length(3),
        upgradeCosts: z.array(z.number().finite().nonnegative().nullable()).length(3),
        nextUpgrades: z.array(nextUpgradeSchema).length(3),
        crosspathSlotsRemaining: z.number().int().min(0).max(2).nullable(),
        sellValue: z.number().nonnegative(),
        damageDealt: z.number().nonnegative(),
        pops: z.number().nonnegative(),
        cashEarned: z.number().nonnegative().optional(),
        isHero: z.boolean(),
        isSubmerged: z.boolean().nullable().optional(),
        targetPosition: point.nullable().optional(),
        bank: z.object({
            cash: z.number(),
            capacity: z.number(),
            interest: z.number(),
            isFull: z.boolean()
        }).nullable().optional()
    }),
    supportCoverage: supportCoverageSchema.nullable().optional(),
    cash: z.number().finite()
});

const upgradeFailureSchema = z.object({
    index: z.number().int().nonnegative(),
    path: z.number().int().min(0).max(2),
    code: z.string(),
    message: z.string()
});
const upgradeExecutionSchema = z.object({
    index: z.number().int().nonnegative(),
    path: z.number().int().min(0).max(2),
    tier: z.number().int().positive(),
    upgradeId: z.string(),
    cost: z.number().finite().nonnegative(),
    executionRound: z.number().int().positive(),
    executionSeconds: z.number().finite().nonnegative().nullable(),
    latenessSeconds: z.number().finite().nonnegative().nullable()
});
const upgradeStatusSchema = z.enum(['scheduled', 'pending', 'executing', 'completed', 'failed', 'expired', 'cancelled']);
export const upgradeTowerResultSchema = z.object({
    completed: z.boolean(),
    appliedPaths: z.array(z.number().int().min(0).max(2)).max(15),
    appliedSteps: z.array(upgradeExecutionSchema).max(15),
    tower: placeTowerResultSchema.shape.tower.nullable(),
    cash: z.number().finite().nullable(),
    failure: upgradeFailureSchema.nullable(),
    status: upgradeStatusSchema,
    scheduleId: z.string().nullable(),
    nextIndex: z.number().int().min(0).max(15),
    nextUpgradeCost: z.number().finite().nonnegative().nullable(),
    waitingFor: z.enum(['time', 'cash']).nullable()
});
export const scheduledUpgradeSchema = upgradeTowerResultSchema.omit({ completed: true }).extend({
    scheduleId: z.string(),
    towerId: z.string(),
    upgradeSequence: z.array(z.number().int().min(0).max(2)).min(1).max(15),
    whenAffordable: z.boolean(),
    targetRound: z.number().int().positive().nullable(),
    delaySeconds: z.number().finite().nonnegative().nullable(),
    triggered: z.boolean(),
    createdAtUtc: z.string(),
    completedAtUtc: z.string().nullable()
});
export type ScheduledUpgrade = z.infer<typeof scheduledUpgradeSchema>;
export const cancelScheduledUpgradeResultSchema = z.object({
    cancelled: z.boolean(),
    count: z.number().int().nonnegative(),
    scheduleId: z.string().nullable().optional()
});

export const sellTowerResultSchema = z.object({
    sold: z.boolean(),
    towerId: z.string(),
    cashReceived: z.number().finite(),
    cash: z.number().finite()
});

export const inspectObstaclesResultSchema = z.object({
    source: z.literal('active-simulation'),
    observedAtUtc: z.iso.datetime({ offset: true }),
    matchGeneration: z.number().int(),
    obstacles: z.array(z.object({
        id: z.string(), nativeType: z.string(), name: z.string().nullable(),
        objectName: z.string().nullable(), textKey: z.string().nullable(),
        position: z.object({ x: z.number().finite().nullable(), y: z.number().finite().nullable(), z: z.number().finite().nullable() }).nullable(),
        areaType: z.string().nullable(), destroyArea: z.boolean().nullable(),
        price: z.number().finite().nonnegative().nullable(), basePrice: z.number().finite().nonnegative().nullable(),
        priceSource: z.string().nullable(), present: z.boolean().nullable(), isActive: z.boolean().nullable(),
        modelActive: z.boolean().nullable(), removalAvailable: z.boolean().nullable(), status: z.string()
    })).max(1024)
});
export const removeObstacleResultSchema = z.object({
    obstacleId: z.string(), nativeType: z.string(), price: z.number().finite().nonnegative().nullable(),
    priceSource: z.string().nullable(), submitted: z.boolean(), removed: z.boolean(),
    verification: z.enum(['removed', 'pending', 'unavailable']), verificationComplete: z.boolean(),
    cashBefore: z.number().finite().nullable(), cashAfter: z.number().finite().nullable(),
    cashDelta: z.number().finite().nullable(), charged: z.boolean().nullable(), matchGeneration: z.number().int()
});

export const inspectTowerMicroResultSchema = z.object({
    source: z.literal('active-simulation'), towerId: z.string(), towerType: z.string().nullable(),
    targetPriority: z.string(), targetTypeSwitchingLocked: z.boolean().nullable(),
    supportedTargetTypes: z.array(z.string()).max(64),
    targetTypeOptions: z.array(z.object({
        index: z.number().int().nonnegative(), id: z.string(), isActionable: z.boolean(),
        actionOnCreate: z.boolean(), intId: z.number().int(), continueToNextPriority: z.boolean(), active: z.boolean()
    })).max(64),
    arms: z.array(z.object({
        index: z.number().int().nonnegative(), name: z.string(), kind: z.string(),
        targetPriority: z.string(), targetTypeId: z.string(), supportedTargetTypes: z.array(z.string()).max(64)
    })).max(64),
    pathModes: z.array(z.object({
        name: z.string(), kind: z.string(), type: z.string().nullable(), targetTypeId: z.string(), active: z.boolean()
    })).max(64),
    coordinateTargets: z.array(z.object({
        index: z.number().int().nonnegative(), name: z.string(), kind: z.string(),
        targetTypeId: z.string(), position: point.nullable(), patrolPoints: z.array(point).max(64).nullable()
    })).max(64)
});

const beastMergeStateSchema = z.object({
    power: z.number().int().nullable(), powerPercent: z.number().finite().nullable(),
    currentContributions: z.number().int().nullable(), lostThroughContribution: z.boolean().nullable()
});
export const inspectBeastMergesResultSchema = z.object({
    source: z.literal('active-simulation'), observedAtUtc: z.iso.datetime({ offset: true }),
    towerId: z.string(), owner: z.number().int(), tiers: z.array(z.number().int()).length(3).nullable(),
    paths: z.array(z.object({
        path: z.number().int().min(0).max(2), beastId: z.string(),
        ...beastMergeStateSchema.shape,
        currentBeastTowerId: z.string().nullable(), recipientTowerId: z.string().nullable(),
        donorTowerIds: z.array(z.string()).max(64), nativeCanMerge: z.boolean().nullable(),
        validTowerExistsForMerge: z.boolean().nullable(),
        nativeInputAvailable: z.boolean().nullable(), nativeInputClass: z.string().nullable(),
        validRecipientTowerIds: z.array(z.string()).max(64).nullable()
    })).length(3)
});
export const mergeBeastResultSchema = z.object({
    source: z.literal('active-simulation'), observedAtUtc: z.iso.datetime({ offset: true }),
    sourceTowerId: z.string(), targetTowerId: z.string(), donorTowerId: z.string(), recipientTowerId: z.string(),
    path: z.number().int().min(0).max(2), beastId: z.string(),
    before: z.object({ donor: beastMergeStateSchema, recipient: beastMergeStateSchema }),
    after: z.object({ donor: beastMergeStateSchema, recipient: beastMergeStateSchema }),
    nativeRecipientTowerId: z.string().nullable(), nativeDonorRecipientTowerId: z.string().nullable()
});

export const setTargetPriorityResultSchema = z.object({
    towerId: z.string(),
    targetPriority: z.string(),
    targetIndex: z.number().int().nonnegative().nullable().optional(),
    micro: inspectTowerMicroResultSchema
});

export const setTowerTargetPositionResultSchema = z.object({
    towerId: z.string(),
    targetPosition: point,
    targetIndex: z.number().int().nonnegative().nullable().optional(),
    micro: inspectTowerMicroResultSchema
});

export const toggleSubmergeResultSchema = z.object({
    towerId: z.string(),
    isSubmerged: z.boolean(),
    previousSubmergedState: z.boolean()
});

export const collectBankResultSchema = z.object({
    collected: z.boolean(),
    towerId: z.string(),
    amountCollected: z.number().finite(),
    cash: z.number().finite()
});

export const setAutoCollectResultSchema = z.object({
    autoCollectDrops: z.boolean()
});

export const collectDropsResultSchema = z.object({
    collected: z.boolean(),
    count: z.number().int().nonnegative(),
    cashCollected: z.number().finite(),
    currentCash: z.number().finite()
});

export const setGameSpeedResultSchema = z.object({
    fastForward: z.boolean()
});

export const activateAbilityResultSchema = z.object({
    activated: z.boolean(),
    abilityId: z.string(),
    name: z.string(),
    target: abilityTargetSchema.nullable()
});

export const startRoundResultSchema = z.object({
    started: z.boolean(),
    round: z.number().int().positive()
});
export const roundInfoGroupSchema = z.object({
    bloon: z.string(),
    baseType: z.string(),
    count: z.number().int().nonnegative(),
    startSeconds: z.number().finite().nonnegative(),
    endSeconds: z.number().finite().nonnegative(),
    isCamo: z.boolean(),
    isLead: z.boolean(),
    requiresLeadPopping: z.boolean(),
    isFortified: z.boolean(),
    isRegrow: z.boolean(),
    isMoab: z.boolean()
}).passthrough();
export type RoundInfoGroup = z.infer<typeof roundInfoGroupSchema>;

export const roundInfoResultSchema = z.object({
    round: z.number().int().positive(),
    activePaths: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS),
    routingPolicy: routingPolicySchema.nullable(),
    traffic: trackTrafficSchema.nullable(),
    totalBloons: z.number().int().nonnegative(),
    durationSeconds: z.number().finite().nonnegative(),
    threatIntel: z.object({
        hasCamo: z.boolean(),
        hasLead: z.boolean(),
        requiresLeadPopping: z.boolean(),
        hasPurple: z.boolean(),
        hasCeramic: z.boolean(),
        hasFortified: z.boolean(),
        hasRegrow: z.boolean(),
        hasMoab: z.boolean(),
        hasBfb: z.boolean(),
        hasZomg: z.boolean(),
        hasDdt: z.boolean(),
        hasBad: z.boolean()
    }),
    groups: z.array(roundInfoGroupSchema)
}).passthrough();
export type RoundInfoResult = z.infer<typeof roundInfoResultSchema>;

export const camoCapabilitySchema = z.object({
    canDetectCamo: z.boolean().nullable(),
    canCollideCamo: z.boolean().nullable(),
    needsDetection: z.boolean().nullable(),
    canDamageCamo: z.boolean().nullable(),
    detectionSource: z.string(),
    collisionSource: z.string(),
    damageSource: z.string(),
    evidence: z.array(z.string()).max(64),
    reason: z.string().nullable()
});
export const poppingCapabilitiesSchema = z.object({
    camo: camoCapabilitySchema,
    canPopLead: z.boolean(),
    canPopPurple: z.boolean(),
    canPopBlack: z.boolean(),
    canPopWhite: z.boolean(),
    canPopFrozen: z.boolean(),
    canDamageDdt: z.boolean().nullable(),
    immuneBloons: z.array(z.string()).max(16),
    ddtBlockers: z.array(z.string()).max(64)
});

const trackCoverageIntervalSchema = z.object({
    startProgress: z.number().finite().min(0).max(1),
    endProgress: z.number().finite().min(0).max(1),
    length: z.number().finite().nonnegative()
}).refine(interval => interval.endProgress >= interval.startProgress,
    'Track coverage interval must run forward along its path');
const trackCoverageSchema = z.object({
    rangeRadius: z.number().finite().positive(),
    uniqueTrackLength: z.number().finite().nonnegative(),
    angularCoverageDegrees: z.number().finite().min(0).max(360),
    detailsOmitted: z.boolean(),
    pathCount: z.number().int().nonnegative(),
    intervalCount: z.number().int().nonnegative(),
    paths: z.array(z.object({
        pathIndex: z.number().int().nonnegative(),
        inRangeLength: z.number().finite().nonnegative(),
        intervals: z.array(trackCoverageIntervalSchema).max(512)
    })).max(128)
});

export const placementSpotSchema = z.object({
    x: z.number().finite(),
    y: z.number().finite(),
    distanceToTrack: z.number().finite().nonnegative(),
    distanceToSupport: z.number().finite().nonnegative().nullable().optional(),
    coveredTowerIds: z.array(z.string()).max(20).optional(),
    uncoveredTowerIds: z.array(z.string()).max(20).optional(),
    recipientCount: z.number().int().nonnegative().optional(),
    supportRange: z.number().finite().positive().nullable().optional(),
    trackCoverage: trackCoverageSchema.optional()
});
export const findPlacementSpotsResultSchema = z.object({
    towerType: z.string(),
    totalFound: z.number().int().nonnegative(),
    returned: z.number().int().nonnegative(),
    targetSupportTower: z.object({
        id: z.string(),
        towerType: z.string(),
        name: z.string(),
        position: z.object({ x: z.number().finite(), y: z.number().finite() }),
        range: z.number().finite().positive()
    }).nullable().optional(),
    coverageTargets: z.array(z.object({
        id: z.string(),
        towerType: z.string(),
        position: point
    })).max(20).optional(),
    coverageBasis: z.literal('candidate_model_range_center_distance').nullable().optional(),
    eligibilityVerified: z.boolean().optional(),
    spots: z.array(placementSpotSchema).max(50),
    observedAtUtc: z.iso.datetime({ offset: true }),
    geometryRevision: z.string(),
    search: z.object({
        rankingStrategy: z.enum(['balanced', 'distanceToSupport', 'distanceToTrack', 'trackCoverage', 'supportCoverage']),
        coarseStep: z.number().finite().positive(),
        refinementStep: z.number().finite().positive().nullable(),
        nativeChecks: z.number().int().min(0).max(4096),
        refinementChecks: z.number().int().min(0).max(4096),
        maxNativeChecks: z.literal(4096),
        relevantPathIndices: z.array(z.number().int().nonnegative()),
        coverageBasis: z.literal('active_path_polylines'),
        notes: z.array(z.string())
    }).optional(),
    note: z.string()
}).passthrough()
    .refine(result => result.returned === result.spots.length, 'returned must equal the number of spots')
    .refine(result => result.returned <= result.totalFound, 'returned cannot exceed totalFound');
export type FindPlacementSpotsResult = z.infer<typeof findPlacementSpotsResultSchema>;

const projectionAmountSchema = z.number().finite().nonnegative().nullable();
const projectionConfidenceSchema = z.enum(['supported', 'estimated', 'unsupported']);
export const projectedCashBreakdownSchema = z.object({
    round: z.number().int().positive(),
    incomeMultiplier: z.number().finite().nonnegative(),
    popCash: projectionAmountSchema,
    roundRewards: projectionAmountSchema,
    towerIncomeEstimate: z.number().finite().nonnegative(),
    baselineIncome: projectionAmountSchema,
    estimatedCashAtEnd: projectionAmountSchema
}).passthrough();
export const projectedCashResultSchema = z.object({
    fromRound: z.number().int().positive(),
    targetRound: z.number().int().positive(),
    startingCash: z.number().finite().nonnegative(),
    confidence: projectionConfidenceSchema,
    assumptions: z.array(z.string()).max(32),
    unsupportedReasons: z.array(z.string()).max(32),
    baselineIncome: z.object({
        confidence: z.enum(['supported', 'unsupported']),
        popCash: projectionAmountSchema,
        roundRewards: projectionAmountSchema,
        total: projectionAmountSchema,
        cashAtStartOfTargetRound: projectionAmountSchema,
        cashAtEndOfTargetRound: projectionAmountSchema
    }).passthrough(),
    towerIncome: z.object({
        confidence: z.enum(['supported', 'estimated']),
        estimatedTotal: z.number().finite().nonnegative(),
        assumptions: z.array(z.string()).max(32),
        towers: z.array(z.object({
            towerId: z.string(),
            towerType: z.string(),
            kind: z.string(),
            perRoundEstimate: projectionAmountSchema,
            included: z.boolean(),
            reason: z.string().nullable()
        }).passthrough())
    }).passthrough(),
    estimatedCashAtStartOfTargetRound: projectionAmountSchema,
    estimatedCashAtEndOfTargetRound: projectionAmountSchema,
    breakdown: z.array(projectedCashBreakdownSchema).max(200),
    incomeThresholds: z.array(z.object({
        lastRound: z.number().int().nonnegative(),
        multiplier: z.number().finite().nonnegative()
    })).max(200),
    finalIncomeMultiplier: z.number().finite().nonnegative()
}).passthrough();

export const scheduleAbilityResultSchema = z.object({
    scheduled: z.boolean(),
    scheduleId: z.string(),
    targetRound: z.number().int().positive(),
    delaySeconds: z.number().nonnegative(),
    currentRound: z.number().int().positive().optional(),
    currentRoundElapsedSeconds: z.number().nonnegative().optional(),
    abilityId: z.string().nullable().optional(),
    abilityIndex: z.number().int().nonnegative().optional(),
    target: abilityTargetSchema.nullable(),
    autoRetry: z.boolean().optional()
});

export const cancelScheduledAbilityResultSchema = z.object({
    cancelled: z.boolean(),
    scheduleId: z.string().optional(),
    count: z.number().int().nonnegative().optional()
});

export const startMatchResultSchema = z.object({
    started: z.boolean(),
    matchId: z.string().nullable().optional(),
    mapId: z.string(),
    mapName: z.string(),
    difficulty: z.string(),
    mode: z.string(),
    hero: z.string().nullable().optional(),
    checkpointPolicy: z.enum(['assisted', 'none']),
    bossType: z.string().nullable().optional(),
    elite: z.boolean().nullable().optional(),
    ranked: z.boolean().nullable().optional()
});

export const restartMatchResultSchema = z.object({
    restarted: z.boolean(),
    matchId: z.string().nullable().optional()
});

export const quitMatchResultSchema = z.object({
    quit: z.boolean(),
    onMainMenu: z.boolean().optional()
});

export const ensureMainMenuResultSchema = z.object({
    onMainMenu: z.boolean(),
    inGame: z.boolean()
});

export const respondUiResultSchema = z.object({
    accepted: z.boolean(),
    blockerId: z.string().min(1).max(128),
    ui: uiStateSchema
});

export const selectHeroResultSchema = z.object({
    selectedHero: z.string()
});

export const setRoundResultSchema = z.object({
    round: z.number().int().positive(),
    currentRound: z.number().int().positive()
});

export const advanceRoundResultSchema = z.object({
    advanced: z.boolean(),
    completedRound: z.number().int().nonnegative(),
    newRound: z.number().int().positive(),
    cash: z.number().finite()
});

export const sandboxSpawnRoundResultSchema = z.object({
    spawned: z.boolean()
});

export const sandboxClearBloonsResultSchema = z.object({
    cleared: z.boolean()
});

export const pauseMatchResultSchema = z.object({
    paused: z.boolean(),
    round: z.number().int().positive().nullable().optional(),
    roundElapsedSeconds: z.number().nonnegative().nullable().optional()
});

export const resumeMatchResultSchema = z.object({
    resumed: z.boolean(),
    fastForward: z.boolean().optional(),
    round: z.number().int().positive().nullable().optional(),
    roundElapsedSeconds: z.number().nonnegative().nullable().optional()
});

export const saveCheckpointResultSchema = z.object({
    saved: z.boolean(),
    round: z.number().int().positive(),
    label: checkpointLabelSchema.nullable().optional(),
    timestampUtc: z.string()
});

export const restoreCheckpointResultSchema = z.object({
    restored: z.boolean(),
    round: z.number().int().positive(),
    cash: z.number().finite(),
    health: z.number().finite(),
    selectedHero: z.string().nullable().optional()
});

export const checkpointMetadataSchema = z.object({
    checkpointId: z.string().min(1),
    label: checkpointLabelSchema.nullable(),
    round: z.number().int().positive(),
    timestampUtc: z.string(),
    source: z.enum(['current', 'custom', 'round']),
    fidelity: z.literal('round_boundary')
});

export const exportCheckpointsResultSchema = z.object({
    exported: z.literal(true),
    exportId: z.string().min(1),
    path: z.string().min(1),
    checkpoints: z.array(checkpointMetadataSchema).min(1).max(128),
    sha256: z.string().regex(/^[a-f0-9]{64}$/i),
    bytes: z.number().int().positive()
});

export const importCheckpointsResultSchema = z.object({
    imported: z.literal(true),
    exportId: z.string().min(1),
    checkpoints: z.array(checkpointMetadataSchema).min(1).max(128)
});

export const listCheckpointsResultSchema = z.object({
    currentRound: z.number().int().positive().nullable().optional(),
    availableRoundCheckpoints: z.array(z.number().int().positive()).max(256),
    customCheckpoints: z.array(checkpointLabelSchema).max(256),
    importedCheckpoints: z.array(checkpointMetadataSchema.extend({ exportId: z.string() })).max(128).optional(),
    count: z.number().int().nonnegative()
});

export const deleteCheckpointResultSchema = z.object({
    deleted: z.boolean(),
    label: checkpointLabelSchema.nullable().optional(),
    round: z.number().int().positive().nullable().optional(),
    remainingCount: z.number().int().nonnegative().optional()
});

const rangeOverlayPointSchema = z.object({
    x: z.number().finite(),
    y: z.number().finite()
}).strict();
const rangeOverlayTriangleSchema = z.array(rangeOverlayPointSchema).length(3);
const rangeOverlayAttackSchema = z.object({
    name: z.string(),
    range: z.number().finite().nonnegative(),
    attackThroughWalls: z.boolean()
}).strict();
export const rangeOverlaySchema = z.object({
    towerId: z.string(),
    status: z.enum(['native', 'global', 'unsupported']),
    reason: z.string().nullable(),
    range: z.number().finite().nonnegative(),
    ignoresBlockers: z.boolean(),
    visibleTriangles: z.array(rangeOverlayTriangleSchema).max(4096),
    blockedTriangles: z.array(rangeOverlayTriangleSchema).max(4096),
    attacks: z.array(rangeOverlayAttackSchema).max(512)
}).strict().superRefine((range, context) => {
    if (range.status === 'native' && range.reason !== null) {
        context.addIssue({ code: 'custom', path: ['reason'], message: 'Native range overlays must have a null reason.' });
    }
    if (range.status !== 'native' && (range.visibleTriangles.length !== 0 || range.blockedTriangles.length !== 0)) {
        context.addIssue({ code: 'custom', path: ['visibleTriangles'], message: 'Global and unsupported range overlays must not include triangles.' });
    }
});
export type RangeOverlay = z.infer<typeof rangeOverlaySchema>;
export const rangeTowerIdsSchema = z.array(z.string().min(1).max(128).refine(value => value.trim().length > 0, 'Tower ID must not be blank')).max(20).optional();

export const mapLayoutSchema = z.object({
    schemaVersion: z.literal(1),
    mapId: z.string(), revision: z.string(), observedAtUtc: z.iso.datetime({ offset: true }),
    bounds: z.object({ minX: z.number().finite(), maxX: z.number().finite(), minY: z.number().finite(), maxY: z.number().finite() })
        .refine(b => b.minX < b.maxX && b.minY < b.maxY, 'Map bounds must have positive dimensions'),
    coordinateSystem: z.string(),
    paths: z.array(z.object({ pathIndex: z.number().int().nonnegative(), active: z.boolean(), hidden: z.boolean(), points: z.array(point).max(32768) })).max(512),
    areas: z.array(z.object({
        id: z.string(), type: z.string(), blocksPlacement: z.boolean(), blocksLineOfSight: z.boolean(),
        height: z.number().finite(), points: z.array(point).max(32768), holes: z.array(z.array(point).max(32768)).max(1024)
    })).max(2048),
    blockers: z.array(z.object({ x: z.number().finite(), y: z.number().finite(), z: z.number().finite(), radius: z.number().nonnegative() })).max(8192),
    placedTowers: z.array(z.object({ id: z.string(), type: z.string(), isHero: z.boolean(), x: z.number().finite(), y: z.number().finite(), range: z.number().nonnegative() })).max(4096),
    rangeOverlays: z.array(rangeOverlaySchema).max(20),
    trackGraph: trackGraphSchema.nullable().optional(),
    spawner: z.object({
        splitterType: z.string(),
        cycleLength: z.number().int().nonnegative(),
        isAlternating: z.boolean(),
        targetRound: z.number().int().positive(),
        activePathsForRound: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS),
        cycleSchedule: z.array(z.object({
            cycleRound: z.number().int().positive(),
            activePaths: z.array(z.number().int().nonnegative()).max(MAX_GRAPH_PATHS)
        })).max(32).optional()
    }).optional(),
    warnings: z.array(z.string()).max(32)
}).refine(m => m.paths.reduce((n, p) => n + p.points.length, 0) + m.areas.reduce((n, a) => n + a.points.length + a.holes.reduce((h, p) => h + p.length, 0), 0) <= 32768, 'Map geometry exceeds point budget');
export type MapLayout = z.infer<typeof mapLayoutSchema>;

export const paragonSacrificedTowerSchema = z.object({
    id: z.string().min(1).max(128),
    name: z.string(),
    isTier5: z.boolean(),
    excludedFromMoneyContribution: z.boolean(),
    damageDealt: z.number().nonnegative(),
    cashEarned: z.number().finite().nonnegative(),
    worth: z.number().finite().nonnegative()
}).passthrough();

export const projectParagonDegreeResultSchema = z.object({
    paragonType: z.string().min(1),
    paragonPrice: z.number().int().positive(),
    formulaConfidence: z.string().min(1),
    purchaseReadiness: z.string().min(1),
    warnings: z.array(z.string()).max(16),
    projectedDegree: z.number().int().min(1).max(100),
    totalPower: z.number().finite().nonnegative(),
    nextDegreeAtPower: z.number().int().nonnegative().nullable().optional(),
    powerNeededForNextDegree: z.number().finite().nonnegative().nullable().optional(),
    eligibleTowerCount: z.number().int().nonnegative(),
    contributions: z.object({
        popsAndCash: z.object({
            totalDamage: z.number().nonnegative(),
            totalCashEarned: z.number().finite().nonnegative(),
            combinedScore: z.number().finite().nonnegative(),
            powerEarned: z.number().finite().nonnegative(),
            maxPower: z.number().int().nonnegative(),
            percentOfMax: z.number().finite().nonnegative()
        }).passthrough(),
        sacrificedMoney: z.object({
            rawCashSpent: z.number().finite().nonnegative(),
            pricePerPower: z.number().finite().positive(),
            powerBeforeCategoryCap: z.number().finite().nonnegative(),
            powerEarned: z.number().finite().nonnegative(),
            maxPower: z.number().int().nonnegative()
        }).passthrough(),
        moneySpentCombined: z.object({
            sacrificedPower: z.number().finite().nonnegative(),
            sliderPower: z.number().finite().nonnegative(),
            powerEarned: z.number().finite().nonnegative(),
            maxPower: z.number().int().nonnegative(),
            percentOfMax: z.number().finite().nonnegative()
        }).passthrough(),
        sacrificedNonT5Tiers: z.object({
            rawTiers: z.number().int().nonnegative(),
            powerEarned: z.number().finite().nonnegative(),
            maxPower: z.number().int().nonnegative(),
            percentOfMax: z.number().finite().nonnegative()
        }).passthrough(),
        additionalTier5s: z.object({
            totalT5Count: z.number().int().nonnegative(),
            extraT5Count: z.number().int().nonnegative(),
            freeTier5Count: z.number().int().nonnegative(),
            powerEarned: z.number().finite().nonnegative(),
            maxPower: z.number().int().nonnegative(),
            tiers5Towers: z.array(z.string()).optional()
        }).passthrough(),
        geraldoTotems: z.object({
            count: z.number().int().nonnegative(),
            powerPerTotem: z.number().int().nonnegative(),
            powerEarned: z.number().int().nonnegative()
        }).passthrough(),
        cashSlider: z.object({
            cashInvested: z.number().int().nonnegative(),
            maxCash: z.number().int().nonnegative(),
            powerPerCash: z.number().finite().positive(),
            markup: z.number().finite().positive(),
            powerEarned: z.number().int().nonnegative()
        }).passthrough()
    }).passthrough(),
    milestoneProjections: z.record(z.string(), z.object({
        reached: z.boolean(),
        targetPower: z.number().int().nonnegative(),
        powerDeficit: z.number().finite().nonnegative(),
        cashSliderNeeded: z.number().int().nonnegative().nullable(),
        cashSliderReachable: z.boolean(),
        popsNeeded: z.number().int().nonnegative(),
        popsReachable: z.boolean()
    }).passthrough()).optional(),
    sliderSensitivity: z.array(z.object({
        sliderCash: z.number().int().nonnegative(),
        sliderPower: z.number().int().nonnegative(),
        degree: z.number().int().min(1).max(100),
        totalPower: z.number().finite().nonnegative()
    })).optional(),
    sacrificedTowers: z.array(paragonSacrificedTowerSchema).optional(),
    observedAtUtc: z.iso.datetime({ offset: true })
}).passthrough();
export type ProjectParagonDegreeResult = z.infer<typeof projectParagonDegreeResultSchema>;

export const templeSacrificeTowerSchema = z.object({
    id: z.string(),
    name: z.string(),
    baseId: z.string(),
    tiers: z.array(z.number().int()),
    worth: z.number().finite().nullable(),
    valuationAvailable: z.boolean(),
    position: z.object({ x: z.number().finite(), y: z.number().finite() }),
    distanceToTemple: z.number().finite().nonnegative()
}).passthrough();

export const templeCategorySchema = z.object({
    totalWorth: z.number().finite(),
    valuationComplete: z.boolean(),
    threshold50kMet: z.boolean(),
    surplus: z.number().finite().nullable(),
    deficit: z.number().finite().nullable(),
    towers: z.array(templeSacrificeTowerSchema)
}).passthrough();

export const inspectTempleSacrificesResultSchema = z.object({
    superMonkey: z.object({
        id: z.string(),
        towerType: z.string(),
        name: z.string(),
        tiers: z.array(z.number().int()),
        currentUpgrade: z.string(),
        targetUpgrade: z.string(),
        position: z.object({ x: z.number().finite(), y: z.number().finite() }),
        range: z.number().finite().nonnegative(),
        rangeSource: z.string()
    }).passthrough(),
    sacrificeCategories: z.object({
        primary: templeCategorySchema,
        military: templeCategorySchema,
        magic: templeCategorySchema,
        support: templeCategorySchema
    }).passthrough(),
    templeProjection: z.object({
        targetUpgrade: z.string(),
        valuationComplete: z.boolean(),
        willQualifyForMaxTier: z.boolean(),
        top3Categories: z.array(z.string()),
        excludedCategoryForTier4: z.string().nullable(),
        excludedCategoryTie: z.array(z.string()),
        blockerReason: z.string().nullable()
    }).passthrough(),
    vtsgReadiness: z.object({
        antiBloonStatus: z.string(),
        legendOfTheNightStatus: z.string(),
        positioningReady: z.boolean(),
        canFormVtsg: z.boolean().nullable(),
        unknownPrerequisites: z.array(z.string()),
        warning: z.string().nullable()
    }).passthrough(),
    towersConsumedCount: z.number().int().nonnegative(),
    safeSurroundingTowersCount: z.number().int().nonnegative(),
    observedAtUtc: z.iso.datetime({ offset: true })
}).passthrough();
export type InspectTempleSacrificesResult = z.infer<typeof inspectTempleSacrificesResultSchema>;

export const monkeyopolisFarmSchema = z.object({
    id: z.string(),
    name: z.string(),
    tiers: z.array(z.number().int()),
    worth: z.number().finite().nullable(),
    valuationAvailable: z.boolean(),
    incomePerRound: z.number().finite().nullable(),
    incomeAvailable: z.boolean(),
    incomeKind: z.string(),
    incomeReason: z.string().nullable(),
    position: z.object({ x: z.number().finite(), y: z.number().finite() }),
    distanceToVillage: z.number().finite().nonnegative()
}).passthrough();

export const monkeyopolisTier5FarmWarningSchema = z.object({
    id: z.string(),
    name: z.string(),
    tiers: z.array(z.number().int()),
    worth: z.number().finite().nullable(),
    valuationAvailable: z.boolean(),
    position: z.object({ x: z.number().finite(), y: z.number().finite() }),
    distanceToVillage: z.number().finite().nonnegative(),
    eligibility: z.literal('ineligible_tier5'),
    warning: z.string()
}).passthrough();

export const inspectMonkeyopolisSacrificesResultSchema = z.object({
    village: z.object({
        id: z.string(),
        towerType: z.string(),
        name: z.string(),
        tiers: z.array(z.number().int()),
        position: z.object({ x: z.number().finite(), y: z.number().finite() }),
        range: z.number().finite().nonnegative(),
        rangeSource: z.string(),
        upgradeCost: z.number().finite().nullable(),
        nativeUpgradeBlocked: z.boolean().nullable(),
        nativeBlockReason: z.string().nullable()
    }).passthrough(),
    farmSacrifices: z.object({
        count: z.number().int().nonnegative(),
        totalWorth: z.number().finite().nullable(),
        valuationComplete: z.boolean(),
        hasTier5FarmWarning: z.boolean(),
        tier5FarmWarnings: z.array(monkeyopolisTier5FarmWarningSchema),
        farms: z.array(monkeyopolisFarmSchema)
    }).passthrough(),
    economicProjection: z.object({
        status: z.string(),
        reason: z.string().nullable(),
        preUpgradeIncomePerRound: z.number().finite().nullable(),
        preUpgradeIncomeConfidence: z.string(),
        preUpgradeIncomeUnsupportedReasons: z.array(z.string()),
        assumptions: z.array(z.string()),
        projectedMonkeyopolisIncomePerRound: z.number().finite().nullable(),
        netIncomeDeltaPerRound: z.number().finite().nullable(),
        baseIncomePerRound: z.number().int().nonnegative().nullable(),
        valueRequiredForIncomeIncrement: z.number().int().positive().nullable(),
        cashPerIncomeIncrement: z.number().int().nonnegative().nullable(),
        incomeIncrements: z.number().int().nonnegative().nullable(),
        cratesPerRound: z.number().int().positive().nullable(),
        cashPerCrate: z.number().finite().nullable(),
        paybackPeriodRounds: z.number().finite().nullable(),
        formula: z.string()
    }).passthrough(),
    spaceReclamation: z.object({
        vacatedFootprintCount: z.number().int().nonnegative(),
        freedCenterCoordinates: z.array(z.object({
            x: z.number().finite(),
            y: z.number().finite(),
            radius: z.number().finite().nonnegative()
        }))
    }).passthrough(),
    readyToUpgrade: z.boolean(),
    recommendation: z.string().nullable(),
    observedAtUtc: z.iso.datetime({ offset: true })
}).passthrough();
export type InspectMonkeyopolisSacrificesResult = z.infer<typeof inspectMonkeyopolisSacrificesResultSchema>;

// Boss data keeps unknown native state nullable rather than interpreting it as safe.
const bossNumber = z.number().finite().nullable();
const bossInteger = z.number().int().nullable();
const bossText = z.string().nullable();
const bossStrings = z.array(z.string()).max(256);
export const bossCoverageSchema = z.object({
    status: z.string(), unknownBehaviorTypes: bossStrings, notes: bossStrings
});
const bossMechanicsSchema = z.object({
    healthSkulls: z.array(z.object({
        percentage: z.number().finite(), actionId: bossText, repeatFirst: z.boolean(), preventFallthrough: z.boolean()
    })).max(256),
    repeatingHealthTriggers: z.array(z.object({
        percentage: z.number().finite(), actionId: bossText, repeatFirst: z.boolean(), preventFallthrough: z.boolean()
    })).max(256),
    lych: z.object({
        etherealKillTrigger: bossInteger, etherealHealthPercentages: z.array(z.number().finite()).max(256),
        drainInterval: bossNumber, regrowInterval: bossNumber, tombstoneInterval: bossNumber,
        tombstoneHealthOverride: bossNumber, tombstoneMoabHealthOverride: bossNumber,
        tombstoneBfbHealthOverride: bossNumber, tombstoneZomgHealthOverride: bossNumber,
        tombstoneSpawnSpeedModifier: bossNumber
    }).nullable(),
    phayze: z.object({
        powerLevels: z.array(z.number().finite()).max(256), shieldSpeedBoost: bossNumber,
        enterCamoAnimation: bossText, enterCamoImmunityAnimation: bossText,
        exitCamoImmunityAnimation: bossText, exitCamoAnimation: bossText
    }).nullable(),
    dread: z.object({
        baseArmour: bossInteger, armourMultiplier: bossNumber, damageReduction: bossInteger,
        rockBloonBaseHealth: bossInteger, rockBloonHealthMultiplier: bossNumber, rockBloonAmount: bossInteger,
        rockBloonSpawnDelay: bossNumber, modelBloonProperties: bossText
    }).nullable(),
    recognizedMechanics: bossStrings
});
export const bossCatalogResultSchema = z.object({
    schemaVersion: z.literal(1), source: z.literal('native_game_data'), version: z.string(),
    bossTypeFilter: bossText,
    entries: z.array(z.object({
        bossType: z.string(), locsKey: z.string(), displayName: bossText, displayNameKey: bossText,
        description: z.object({
            available: z.boolean(), text: bossText, key: bossText,
            provenance: z.string(), discoveredKeys: bossStrings
        }),
        baselineVariants: z.array(z.object({
            id: z.string(), baseId: z.string(), variantName: bossText, variantPrefix: bossText,
            tier: bossInteger, tierSource: bossText, isBoss: z.boolean(), isBossSegment: z.boolean(),
            layerNumber: z.number().int(), maxHealth: bossInteger, armourMultiplier: bossNumber,
            bloonProperties: bossText, tags: bossStrings, mechanics: bossMechanicsSchema
        })).max(256),
        coverage: bossCoverageSchema
    })).max(128),
    coverage: bossCoverageSchema
});
export const bossRuntimeSchema = z.object({
    id: z.string(), bloonId: z.string(), baseId: z.string(), bossType: bossText,
    isBoss: z.boolean(), isBossSegment: z.boolean(), isElite: z.boolean().nullable(), tier: bossInteger,
    baseModelMaxHealth: bossInteger, effectiveModelMaxHealth: bossInteger, effectiveLiveHealth: bossInteger,
    healthScaling: z.string(),
    armour: z.object({ hasArmour: z.boolean().nullable(), current: bossInteger, currentProportion: bossNumber, modelMultiplier: bossNumber }),
    immunity: z.object({
        modelBloonProperties: bossText, runtimeTowerSet: bossText, modelInvulnerable: z.boolean().nullable(),
        runtimeInvulnerable: z.boolean().nullable(), untargetable: z.boolean().nullable()
    }),
    position: z.object({ x: bossNumber, y: bossNumber, z: bossNumber, progress: bossNumber, distanceTraveled: bossNumber }),
    skulls: z.object({
        current: bossInteger, damageUntilNext: bossInteger, thresholds: z.array(z.number().finite()).max(256), healthPercent: bossNumber
    }),
    specialState: z.object({
        lychEthereal: z.boolean().nullable(), phayzeCamoImmunityPhase: z.boolean().nullable(),
        invulnerabilityOverride: z.boolean().nullable(), coverage: z.string()
    })
});
export const inspectBossResultSchema = z.object({
    schemaVersion: z.literal(1), source: z.literal('native_simulation'), activeGame: z.boolean(),
    requestedBloonId: bossText,
    encounter: z.object({
        currentTier: bossInteger, isElite: z.boolean().nullable(), nextSpawnRound: bossInteger,
        defeatByRound: bossInteger, status: z.string()
    }),
    bosses: z.array(bossRuntimeSchema).max(128), coverage: bossCoverageSchema
});
export const bossSummarySchema = z.object({
    id: z.string(), bloonId: z.string(), baseId: z.string(), bossType: bossText,
    isBoss: z.boolean(), isBossSegment: z.boolean(), isElite: z.boolean().nullable(), tier: bossInteger,
    health: bossInteger, armour: bossInteger, modelBloonProperties: bossText, runtimeTowerSetImmunity: bossText,
    invulnerable: z.boolean().nullable(), untargetable: z.boolean().nullable(),
    currentSkull: bossInteger, damageUntilNextSkull: bossInteger, progress: bossNumber, coverage: bossCoverageSchema
});
export type BossCatalogResult = z.infer<typeof bossCatalogResultSchema>;
export type InspectBossResult = z.infer<typeof inspectBossResultSchema>;
export type BossSummary = z.infer<typeof bossSummarySchema>;

const damageConditionStrings = z.array(z.string()).max(64);
export const damageModifierSchema = z.object({
    modifierType: z.string(), applicability: z.string(), conditionEvaluated: z.boolean(), condition: z.string(),
    tag: bossText, tags: damageConditionStrings, mustIncludeAllTags: z.boolean().nullable(), ignoreTag: z.boolean().nullable(),
    bloonId: bossText, bloonIds: damageConditionStrings, bloonState: bossText, bloonStates: damageConditionStrings,
    bloonTypes: damageConditionStrings,
    mustIncludeAllStates: z.boolean().nullable(), mustBeModified: z.boolean().nullable(),
    includeChildren: z.boolean().nullable(), applyOverMaxDamage: z.boolean().nullable(),
    bonusDamage: bossNumber, multiplier: bossNumber,
    cashThreshold: bossNumber, stackId: bossText, lifeThreshold: bossNumber,
    percentPerLifeBelowThreshold: bossNumber, multiplierPerLifeBelowThreshold: bossNumber,
    percentPerShield: bossNumber, multiplierPerShield: bossNumber, modifier: bossText, modifiers: damageConditionStrings,
    damagePerRound: bossNumber, roundCap: bossInteger, rbeThreshold: bossInteger, maxDamageMultiplier: bossNumber,
    extraRbePerBossSkull: bossInteger, active: z.boolean().nullable(), damage: bossInteger, maxDamageBoost: bossInteger,
    level7Tags: damageConditionStrings, level11ExcludeTags: damageConditionStrings, level19BloonTags: damageConditionStrings,
    level7NonMoabBonus: bossNumber, level7MoabBonus: bossNumber, level11NonMoabBonus: bossNumber,
    level11MoabBonus: bossNumber, level19NonMoabBonus: bossNumber, level19MoabBonus: bossNumber,
    supported: z.boolean(), coverageNote: z.string()
});
export const damageSourceSchema = z.object({
    sourceKind: z.string(), projectileId: z.string(), path: z.string(), damage: bossNumber, maxDamage: bossNumber,
    conditions: damageConditionStrings,
    damageType: z.string(), blockedBy: damageConditionStrings, damageModifiers: z.array(damageModifierSchema).max(64),
    attachedBloonBehaviors: z.array(z.object({
        behaviorType: z.string(), source: z.string(), mutationId: bossText, damage: bossNumber,
        intervalSeconds: bossNumber, durationSeconds: bossNumber, isFireBased: z.boolean().nullable(),
        supported: z.boolean(), coverageNote: z.string()
    })).max(64),
    supported: z.boolean(), unsupportedCoverage: damageConditionStrings, coverageNote: z.string()
});
export const damageCoverageSchema = z.object({
    maxDepth: z.number().int().nonnegative(), maxSources: z.number().int().nonnegative(), complete: z.boolean(),
    visitedNodes: z.number().int().nonnegative(), supportedSourceTypes: damageConditionStrings, coverageNote: z.string(),
    sourceKinds: damageConditionStrings, unsupportedTypes: damageConditionStrings, notes: damageConditionStrings
});
export const paragonBossDamageSchema = z.object({
    isParagon: z.boolean(), source: z.string(), valueKind: z.string(), configuredBonusPercent: bossNumber,
    configuredEveryDegrees: bossInteger, currentDegree: bossInteger, currentDegreeBonusPercent: bossNumber,
    actualTargetDamageBonusPercent: bossNumber, confidence: z.string(), coverageNote: z.string()
});
export type DamageModifierStats = z.infer<typeof damageModifierSchema>;
export type DamageSourceStats = z.infer<typeof damageSourceSchema>;
export type DamageCoverage = z.infer<typeof damageCoverageSchema>;
export type ParagonBossDamage = z.infer<typeof paragonBossDamageSchema>;


