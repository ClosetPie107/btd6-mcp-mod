import { randomUUID } from 'node:crypto';
import { access, readFile, rename, unlink, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
import type * as z from 'zod/v4';
import {
    progressSchema, configurationSchema, performanceSchema, mapLayoutSchema,
    canPlaceTowerResultSchema, placeTowerResultSchema, upgradeTowerResultSchema,
    sellTowerResultSchema, setTargetPriorityResultSchema, setTowerTargetPositionResultSchema,
    inspectObstaclesResultSchema, removeObstacleResultSchema, inspectTowerMicroResultSchema,
    inspectBeastMergesResultSchema, mergeBeastResultSchema,
    toggleSubmergeResultSchema, collectBankResultSchema, setAutoCollectResultSchema,
    collectDropsResultSchema, setGameSpeedResultSchema, activateAbilityResultSchema,
    scheduleAbilityResultSchema, cancelScheduledAbilityResultSchema, startMatchResultSchema,
    restartMatchResultSchema, quitMatchResultSchema, ensureMainMenuResultSchema,
    respondUiResultSchema, selectHeroResultSchema, setRoundResultSchema,
    advanceRoundResultSchema, sandboxSpawnRoundResultSchema, sandboxClearBloonsResultSchema,
    pauseMatchResultSchema, resumeMatchResultSchema, saveCheckpointResultSchema,
    checkpointLabelSchema, restoreCheckpointResultSchema, listCheckpointsResultSchema, deleteCheckpointResultSchema,
    startRoundResultSchema, roundInfoResultSchema, findPlacementSpotsResultSchema,
    projectedCashResultSchema, uiStateSchema, abilityTargetSchema, abilityTargetingSchema, supportCoverageSchema,
    activateAbilityInputSchema, scheduleAbilityInputSchema, type UiState, type UiAction,
    type RoundProgress, type BridgeConfiguration, type BridgePerformance, type MapLayout,
    type RoundInfoResult, type FindPlacementSpotsResult, type AbilityTarget,
    type AbilityTargeting, type ActivateAbilityInput, type ScheduleAbilityInput,
    upgradeTowerInputSchema, type UpgradeTowerInput, scheduledUpgradeSchema, type ScheduledUpgrade,
    cancelScheduledUpgradeResultSchema, exportCheckpointsInputSchema, importCheckpointsInputSchema,
    exportCheckpointsResultSchema, importCheckpointsResultSchema,
    roundInsightsSchema, type RoundInsights,
    projectParagonDegreeResultSchema, type ProjectParagonDegreeResult,
    inspectTempleSacrificesResultSchema, type InspectTempleSacrificesResult,
    inspectMonkeyopolisSacrificesResultSchema, type InspectMonkeyopolisSacrificesResult,
    bossCatalogResultSchema, inspectBossResultSchema, bossSummarySchema,
    type BossCatalogResult, type InspectBossResult, type BossSummary,
    damageModifierSchema, damageSourceSchema, damageCoverageSchema, paragonBossDamageSchema,
    camoCapabilitySchema, poppingCapabilitiesSchema,
    type DamageModifierStats, type DamageSourceStats, type DamageCoverage, type ParagonBossDamage,
    canUseGeraldoItemInputSchema, useGeraldoItemInputSchema,
    inspectGeraldoResultSchema, canUseGeraldoItemResultSchema, useGeraldoItemResultSchema,
    cancelScheduledGeraldoPurchaseInputSchema, cancelScheduledGeraldoPurchaseResultSchema,
    scheduledGeraldoPurchaseSchema,
    type InspectGeraldoResult, type CanUseGeraldoItemResult,
    type UseGeraldoItemInput, type UseGeraldoItemResult, type ScheduledGeraldoPurchase,
    type CancelScheduledGeraldoPurchaseResult,
    castCorvusSpellInputSchema, castCorvusSpellResultSchema,
    setCorvusSpellInputSchema, setCorvusSpellResultSchema,
    inspectCorvusResultSchema, cancelScheduledCorvusActionInputSchema,
    cancelScheduledCorvusActionResultSchema, scheduledCorvusActionSchema,
    type CastCorvusSpellInput, type CastCorvusSpellResult,
    type SetCorvusSpellInput, type SetCorvusSpellResult, type InspectCorvusResult,
    type ScheduledCorvusAction, type CancelScheduledCorvusActionResult
} from './bridge-data.js';

export type RoundStatusReason = 'round_cleared' | 'victory' | 'defeat' | 'match_ended' | 'timeout' | 'ui_blocked';
const uiNeedsDecision = (ui: UiState) => ui.blocker?.state === 'blocked' || ui.blocker?.state === 'failed';
export type RoundReport = Omit<RoundInsights, 'pressureByLane' | 'topThreats'> & {
    pressureByLane?: RoundInsights['pressureByLane'];
    topThreats?: RoundInsights['topThreats'];
    omittedActions?: number;
};
export const PROTOCOL_VERSION = 1;
const DEFAULT_TIMEOUT_MS = 5_000;
const RESULT_POLL_INTERVAL_MS = 50;
const DEFAULT_IPC_ROOT = resolve(
    dirname(fileURLToPath(import.meta.url)),
    '../..',
    'loader-payload',
    'UserData',
    'AgentBridge',
    'ipc'
);

type JsonObject = Record<string, unknown>;

export interface BridgeStatus {
    protocolVersion: number;
    bridgeVersion: string;
    moduleVersionId: string;
    btd6Version: string;
    gameAvailable: boolean;
    activeGame: boolean;
    onMainMenu?: boolean;
    selectedHero?: string | null;
    autoCollectDrops?: boolean;
    ui: UiState;
    capabilities: {
        status: boolean;
        observation: boolean;
        playerActions: boolean;
        researchControls: boolean;
        scheduledUpgrades?: boolean;
        roundInsights?: boolean;
        checkpointTransfer?: boolean;
        geraldoShop?: boolean;
        corvusSpells?: boolean;
    };
}

export interface BankInfo {
    cash: number;
    capacity: number;
    interest: number;
    isFull: boolean;
}

export interface TowerInfo {
    id: string;
    towerType: string;
    name: string;
    position: { x: number; y: number };
    range: number;
    targetPriority: string;
    tiers: [number, number, number];
    upgradeCosts: [number | null, number | null, number | null];
    nextUpgrades: Array<{ path: number; name: string | null; cost: number | null; available: boolean }>;
    crosspathSlotsRemaining: number | null;
    sellValue: number;
    damageDealt: number;
    pops: number;
    cashEarned?: number;
    bank?: BankInfo | null;
    isHero: boolean;
    isSubmerged?: boolean | null;
    targetPosition?: { x: number; y: number } | null;
}

export type PlacedTower = z.infer<typeof placeTowerResultSchema>['tower'];


export interface AppliedDebuff {
    category: string;
    name: string;
    damageBonus?: number;
    slowPercentage?: number;
    durationSeconds?: number;
    intervalSeconds?: number;
    damage?: number;
    isFireBased?: boolean;
    canFreezeMoabs?: boolean;
    chance?: number;
    affectMoab?: boolean;
    strips?: string[];
    targets?: string;
    description: string;
}

export interface TowerWeaponStats {
    camo: CamoCapability;
    canDamageDdt: boolean | null;
    name: string;
    damageType?: string;
    cooldownSeconds: number;
    projectileId: string;
    pierce: number;
    maxPierce: number;
    damage: number;
    maxDamage: number;
    blockedBy?: string[];
    damageModifiers?: DamageModifierStats[];
    damageSources?: DamageSourceStats[];
    damageCoverage?: DamageCoverage;
    appliedDebuffs?: AppliedDebuff[];
    projectileBehaviors: string[];
}

export interface TowerAttackStats {
    name: string;
    range: number;
    attackThroughWalls: boolean;
    fireWithoutTarget: boolean;
    targetProvider: string;
    weapons: TowerWeaponStats[];
}

export interface TowerIncomeStats {
    incomeType: string;
    cashPerRound: number;
    countPerRound?: number;
    cashPerItem?: number;
    capacity?: number;
    interest?: number;
    autoCollect?: boolean;
    incomeModifier?: number;
    baseCash?: number;
    maxCashGeneration?: number;
    targetTowers?: string[];
    cooldownSeconds?: number;
    description: string;
}

export interface TowerStats {
    towerType: string;
    name: string;
    tiers: number[];
    baseCost: number;
    range: number;
    isGlobalRange: boolean;
    isWaterBased: boolean;
    isAmphibious: boolean;
    poppingCapabilities: PoppingCapabilities;
    attacks: TowerAttackStats[];
    paragonBossDamage?: ParagonBossDamage;
    appliedDebuffs?: AppliedDebuff[];
    income?: TowerIncomeStats | null;
}

export interface EffectiveWeaponStats {
    name: string;
    damageType: string;
    baseCooldownSeconds: number;
    effectiveCooldownSeconds: number;
    baseDamage: number;
    effectiveDamage: number;
    basePierce: number;
    effectivePierce: number;
    camo: CamoCapability;
    canDamageDdt: boolean | null;
    blockedBy: string[];
    damageModifiers: DamageModifierStats[];
    damageSources?: DamageSourceStats[];
    damageCoverage?: DamageCoverage;
    appliedDebuffs?: AppliedDebuff[];
    rateFrames: number;
    isReloadReady: boolean;
    isInThrow: boolean;
}

export interface EffectiveAttackStats {
    range: number;
    onlyTargetsMoab: boolean;
    cannotTargetMoab: boolean;
    cannotTargetCamo: boolean;
    weapons: EffectiveWeaponStats[];
}

export interface BuffTimeout {
    removeAtFrame: number;
    usesRoundTime: boolean;
    onlyTimeoutWhenActive: boolean;
    roundsRemaining: number;
}

export interface BuffEffect {
    stat: string;
    operation: string;
    amount?: number;
    multiplier?: number;
    tag?: string;
    description?: string;
    mutationId?: string;
    condition?: string;
}

export interface ActiveBuffInfo {
    indicator: string;
    displayName: string;
    sourceTowerType: string;
    canCurrentlyBuff: boolean;
    canEventuallyBuff: boolean;
    availableCount: number;
    unavailableCount: number;
    mutatorId: string;
    stackCount: number;
    timeout: BuffTimeout | null;
    effects: BuffEffect[];
}

export type CamoCapability = z.infer<typeof camoCapabilitySchema>;
export type PoppingCapabilities = z.infer<typeof poppingCapabilitiesSchema>;

export interface EffectiveTowerStats {
    available: boolean;
    reason?: string;
    range?: number;
    baseRange?: number;
    totalRateModifier?: number;
    poppingCapabilities?: PoppingCapabilities;
    attacks?: EffectiveAttackStats[];
    paragonBossDamage?: ParagonBossDamage;
    activeBuffs?: ActiveBuffInfo[];
    appliedDebuffs?: AppliedDebuff[];
    cashEarned?: number;
    bank?: BankInfo | null;
    income?: TowerIncomeStats | null;
    passiveCashPerRound?: number | null;
}

export interface TowerCatalogEntry {
    towerType: string;
    name: string;
    baseCost: number;
    baseRange: number;
    isWaterBased: boolean;
    isAmphibious: boolean;
    upgrades: Array<{ upgradeId: string; path: number; tier: number; cost: number; name: string }>;
}

export interface TowerCatalog {
    source: 'live-game-model';
    btd6Version: string;
    towers: TowerCatalogEntry[];
}

export interface HeroInfo {
    heroId: string;
    level: number;
    xp: number;
    xpToNextLevel: number;
    costToLevelUp: number;
}

export interface AbilityInfo {
    abilityId: string;
    towerId: string;
    name: string;
    isReady: boolean;
    canUse: boolean;
    cooldownRemaining: number;
    cooldownTotal: number;
    targeting: AbilityTargeting;
}

export interface ScheduledAbilityInfo {
    scheduleId: string;
    abilityId?: string | null;
    abilityIndex: number;
    targetRound: number;
    delaySeconds: number;
    autoRetry: boolean;
    scheduledAtUtc: string;
    triggered?: boolean;
    triggeredAtUtc?: string | null;
    error?: string | null;
    target: AbilityTarget | null;
}

export interface ActiveThreat {
    type: string;
    isFortified: boolean;
    isCamo: boolean;
    isMoab: boolean;
    trackProgress: number | null;
    pathIndex: number;
    health: number;
    x: number;
    y: number;
}

export interface TrackLaneProgress {
    pathIndex: number;
    maxProgress: number | null;
    bloonCount: number;
    leadCount: number;
    camoCount: number;
    moabCount: number;
}

export type ProjectedCashResult = z.infer<typeof projectedCashResultSchema>;

export interface BridgeObservation {
    observedAtUtc: string;
    activeGame: boolean;
    ui: UiState;
    gameStatus: string | null;
    isPaused?: boolean | null;
    round: number | null;
    endRound: number | null;
    roundActive: boolean | null;
    roundElapsedSeconds?: number | null;
    inBetweenRounds: boolean | null;
    canStartRound: boolean | null;
    fastForward: boolean | null;
    autoPlay: boolean | null;
    cash: number | null;
    lives: number | null;
    maxLives: number | null;
    towerCount: number;
    mapId: string | null;
    mapName: string | null;
    mode: string | null;
    difficulty: string | null;
    towers: TowerInfo[];
    bosses?: BossSummary[];
    hero: HeroInfo | null;
    abilities: AbilityInfo[];
    scheduledAbilities?: ScheduledAbilityInfo[];
    scheduledUpgrades?: ScheduledUpgrade[];
    scheduledGeraldoPurchases?: ScheduledGeraldoPurchase[];
    scheduledCorvusActions?: ScheduledCorvusAction[];
    roundInsights?: RoundInsights | null;
    roundReport?: RoundInsights | null;
    threatSnapshotSource?: 'live' | 'defeat_report' | null;
    availableCheckpoints?: number[];
    maxTrackProgress?: number | null;
    activeBloonCount?: number | null;
    activeMoabCount?: number | null;
    trackProgressByLane?: TrackLaneProgress[];
    activeThreats?: ActiveThreat[];
}

export interface WaitForRoundEndResult {
    completed: boolean;
    timedOut: boolean;
    statusReason: RoundStatusReason;
    ui: UiState;
    round: number;
    roundCompleted: number | null;
    gameStatus: string;
    cash: number | null;
    lives: number | null;
    summary: {
        round: number;
        roundCompleted: number | null;
        cash: number | null;
        lives: number | null;
        towerCount: number;
        totalPops: number;
        totalDamage: number;
    };
    observation?: BridgeObservation;
    roundReport?: RoundReport;
}

export interface StartRoundResult extends Partial<WaitForRoundEndResult> {
    started: boolean;
    round: number;
}

export interface TowerInspectionResult {
    source: 'active-simulation';
    observedAtUtc: string;
    tower: TowerInfo;
    modelStats: TowerStats;
    effectiveStats: EffectiveTowerStats;
}

interface BridgeResult {
    protocolVersion: number;
    requestId: string;
    ok: boolean;
    result: unknown;
    error: {
        code: string;
        message: string;
        retryable: boolean;
        details?: Record<string, unknown> | null;
    } | null;
}

export type SubmissionState = 'not_submitted' | 'rejected' | 'submitted_outcome_unknown';

export class BridgeClientError extends Error {
    constructor(
        public readonly code: string,
        message: string,
        public readonly retryable: boolean,
        public readonly requestId?: string,
        public readonly submissionState?: SubmissionState,
        public readonly details?: Record<string, unknown>
    ) {
        super(message);
        this.name = 'BridgeClientError';
    }
}

type ResponseValidator<T> = z.ZodType<T> | ((value: unknown) => T);

const PRE_EXECUTION_ERROR_CODES: Record<string, true> = {
    UNSUPPORTED_PROTOCOL: true,
    INVALID_REQUEST_ID: true,
    DEADLINE_EXCEEDED: true,
    UNKNOWN_COMMAND: true,
    GAME_UNAVAILABLE: true,
    ALREADY_IN_GAME: true,
    NOT_ON_MAIN_MENU: true,
    NO_ACTIVE_GAME: true,
    SIMULATION_UNAVAILABLE: true,
    INVALID_ARGUMENT: true,
    INVALID_DIFFICULTY: true,
    INVALID_MODE: true,
    UNKNOWN_MAP: true,
    MAP_LOCKED: true,
    MODE_LOCKED: true,
    SELLING_DISABLED: true,
    INSUFFICIENT_CASH: true,
    LOCATION_BLOCKED: true,
    PRICE_UNAVAILABLE: true,
    UPGRADE_UNAVAILABLE: true,
    UI_BLOCKED: true,
    STALE_UI_BLOCKER: true,
    INVALID_UI_ACTION: true,
    UI_NOT_READY: true,
    INVALID_HERO: true,
    INVALID_TOWER_TYPE: true,
    UNKNOWN_TOWER_TYPE: true,
    INVALID_TOWER_ID: true,
    TOWER_NOT_FOUND: true,
    CORVUS_NOT_FOUND: true,
    UNKNOWN_CORVUS_SPELL: true,
    CORVUS_SPELL_KIND_MISMATCH: true,
    CORVUS_SPELL_LOCKED: true,
    INVALID_PATH: true,
    TARGET_ROUND_PASSED: true,
    DELAY_PASSED: true,
    ROUND_TIME_UNAVAILABLE: true,
    SCHEDULE_NOT_FOUND: true,
    NO_ABILITIES: true,
    ABILITY_NOT_FOUND: true,
    AMBIGUOUS_ABILITY: true,
    ABILITY_NOT_READY: true,
    ABILITY_TARGET_REQUIRED: true,
    ABILITY_TARGET_KIND_MISMATCH: true,
    UNEXPECTED_ABILITY_TARGET: true,
    UNSUPPORTED_ABILITY_INPUT: true,
    INVALID_ABILITY_TARGET: true,
    ABILITY_INPUT_UNAVAILABLE: true,
    ROUND_ACTIVE: true,
    CHECKPOINT_NOT_FOUND: true,
    CORRUPT_CHECKPOINT: true,
    UPGRADE_ALREADY_SCHEDULED: true,
    SCHEDULE_QUEUE_FULL: true,
    IDEMPOTENCY_KEY_REUSED: true,
    IDEMPOTENCY_CAPACITY_EXCEEDED: true,
    CHECKPOINT_AMBIGUOUS: true,
    CHECKPOINT_SELECTOR_MISMATCH: true,
    CHECKPOINT_CAPACITY_EXCEEDED: true,
    CHECKPOINT_HASH_MISMATCH: true,
    IMPORT_COLLISION: true,
    IMPORT_CONFLICT: true,
    INCOMPATIBLE_MATCH: true,
    INCOMPATIBLE_CHECKPOINT: true,
    INCOMPATIBLE_GAME_VERSION: true,
    INCOMPATIBLE_NATIVE_SCHEMA: true,
    UNSUPPORTED_NATIVE_TYPE: true,
    UNSUPPORTED_FIDELITY: true,
    UNSUPPORTED_EXPORT_VERSION: true,
    EXPORT_NOT_FOUND: true,
    EXPORT_ID_MISMATCH: true,
    CORRUPT_EXPORT: true,
    ROUND_OUT_OF_RANGE: true,
    INVALID_TARGET_ROUND: true,
    INVALID_ROUND_RANGE: true,
    SANDBOX_UNAVAILABLE: true,
    NOT_A_BANK: true,
    NOT_SUBMERGE_TOWER: true,
    UNSUPPORTED_TARGET_TOWER: true,
    SIMULATION_TOWER_UNAVAILABLE: true,
    INVALID_ROUND: true
};


export class BridgeClient {
    readonly ipcRoot: string;

    constructor(ipcRoot = process.env.BTD6_AGENT_BRIDGE_IPC_ROOT ?? DEFAULT_IPC_ROOT) {
        this.ipcRoot = ipcRoot;
    }

    private async requireCapability(capability: 'scheduledUpgrades' | 'checkpointTransfer' | 'geraldoShop' | 'corvusSpells'): Promise<void> {
        const status = await this.status();
        if (status.capabilities[capability] !== true) {
            throw new BridgeClientError('UNSUPPORTED_CAPABILITY', `The loaded bridge does not support ${capability}; restart with an updated bridge before using this feature.`, false, undefined, 'not_submitted');
        }
    }
    async status(): Promise<BridgeStatus> {
        return this.request('status', {}, DEFAULT_TIMEOUT_MS, validateStatus);
    }

    async observe(): Promise<BridgeObservation> {
        return this.request('observe', {}, DEFAULT_TIMEOUT_MS, validateObservation);
    }

    async roundProgress(): Promise<RoundProgress> {
        return this.request('round_progress', {}, DEFAULT_TIMEOUT_MS, progressSchema);
    }

    async performance(): Promise<BridgePerformance> {
        return this.request('bridge_performance', {}, DEFAULT_TIMEOUT_MS, performanceSchema);
    }

    async configure(options: Partial<BridgeConfiguration>): Promise<BridgeConfiguration> {
        return this.request('configure_bridge', options, DEFAULT_TIMEOUT_MS, configurationSchema);
    }

    async startMatch(options: {
        map?: string; difficulty?: string; mode?: string; hero?: string; autoHandleUi?: boolean;
        checkpointPolicy?: 'assisted' | 'none'; bossType?: string; elite?: boolean
    } = {}) {
        const payload: Record<string, unknown> = {
            map: options.map ?? 'Logs',
            difficulty: options.difficulty ?? 'Easy',
            mode: options.mode ?? 'Standard',
            autoHandleUi: options.autoHandleUi ?? true,
            checkpointPolicy: options.checkpointPolicy ?? 'assisted'
        };
        if (options.hero) payload.hero = options.hero;
        if (options.bossType !== undefined) payload.bossType = options.bossType;
        if (options.elite !== undefined) payload.elite = options.elite;
        return this.request('start_match', payload, 15_000, startMatchResultSchema);
    }

    async selectHero(options: { hero: string }) {
        return this.request('select_hero', options, 10_000, selectHeroResultSchema);
    }

    async restartMatch() {
        return this.request('restart_match', {}, 10_000, restartMatchResultSchema);
    }

    async quitMatch() {
        return this.request('quit_match', {}, 10_000, quitMatchResultSchema);
    }

    async ensureMainMenu() {
        return this.request('ensure_main_menu', {}, 10_000, ensureMainMenuResultSchema);
    }

    async canPlaceTower(options: { towerType: string; x: number; y: number }) {
        return this.request('can_place_tower', options, 10_000, canPlaceTowerResultSchema);
    }

    async placeTower(options: { towerType: string; x: number; y: number }) {
        return this.request('place_tower', options, 10_000, placeTowerResultSchema);
    }

    async upgradeTower(options: UpgradeTowerInput) {
        const parsed = upgradeTowerInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('scheduledUpgrades');
        return this.request('upgrade_tower', parsed.data, 10_000, upgradeTowerResultSchema);
    }
    async inspectGeraldo(): Promise<InspectGeraldoResult> {
        await this.requireCapability('geraldoShop');
        return this.request('inspect_geraldo', {}, 10_000, inspectGeraldoResultSchema);
    }

    async canUseGeraldoItem(options: z.input<typeof canUseGeraldoItemInputSchema>): Promise<CanUseGeraldoItemResult> {
        const parsed = canUseGeraldoItemInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('geraldoShop');
        return this.request('can_use_geraldo_item', parsed.data, 10_000, canUseGeraldoItemResultSchema);
    }

    async useGeraldoItem(options: UseGeraldoItemInput): Promise<UseGeraldoItemResult> {
        const parsed = useGeraldoItemInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('geraldoShop');
        return this.request('use_geraldo_item', parsed.data, 10_000, useGeraldoItemResultSchema);
    }

    async cancelScheduledGeraldoPurchase(
        options: z.input<typeof cancelScheduledGeraldoPurchaseInputSchema> = {}
    ): Promise<CancelScheduledGeraldoPurchaseResult> {
        const parsed = cancelScheduledGeraldoPurchaseInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('geraldoShop');
        return this.request('cancel_scheduled_geraldo_purchase', parsed.data, 10_000, cancelScheduledGeraldoPurchaseResultSchema);
    }
    async inspectCorvus(): Promise<InspectCorvusResult> {
        await this.requireCapability('corvusSpells');
        return this.request('inspect_corvus', {}, 10_000, inspectCorvusResultSchema);
    }

    async castCorvusSpell(options: CastCorvusSpellInput): Promise<CastCorvusSpellResult> {
        const parsed = castCorvusSpellInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('corvusSpells');
        return this.request('cast_corvus_spell', parsed.data, 10_000, castCorvusSpellResultSchema);
    }

    async setCorvusSpell(options: SetCorvusSpellInput): Promise<SetCorvusSpellResult> {
        const parsed = setCorvusSpellInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('corvusSpells');
        return this.request('set_corvus_spell', parsed.data, 10_000, setCorvusSpellResultSchema);
    }

    async cancelScheduledCorvusAction(
        options: z.input<typeof cancelScheduledCorvusActionInputSchema> = {}
    ): Promise<CancelScheduledCorvusActionResult> {
        const parsed = cancelScheduledCorvusActionInputSchema.safeParse(options);
        if (!parsed.success) {
            throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        }
        await this.requireCapability('corvusSpells');
        return this.request('cancel_scheduled_corvus_action', parsed.data, 10_000, cancelScheduledCorvusActionResultSchema);
    }


    async cancelScheduledUpgrade(options: { scheduleId?: string } = {}) {
        if (options.scheduleId !== undefined && (typeof options.scheduleId !== 'string' || !options.scheduleId.length || options.scheduleId.length > 128)) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'Provide a scheduleId, "all", or omit it.', false, undefined, 'not_submitted');
        }
        await this.requireCapability('scheduledUpgrades');
        return this.request('cancel_scheduled_upgrade', options, 10_000, cancelScheduledUpgradeResultSchema);
    }

    async sellTower(options: { towerId: string }) {
        return this.request('sell_tower', options, 10_000, sellTowerResultSchema);
    }

    async inspectObstacles() {
        return this.request('inspect_obstacles', {}, 10_000, inspectObstaclesResultSchema);
    }

    async removeObstacle(options: { obstacleId: string }) {
        return this.request('remove_obstacle', options, 10_000, removeObstacleResultSchema);
    }

    async inspectTowerMicro(options: { towerId: string }) {
        return this.request('inspect_tower_micro', options, 10_000, inspectTowerMicroResultSchema);
    }

    async inspectBeastMerges(options: { towerId: string }) {
        return this.request('inspect_beast_merges', options, 10_000, inspectBeastMergesResultSchema);
    }

    async mergeBeast(options: { sourceTowerId: string; targetTowerId: string; path: number }) {
        return this.request('merge_beast', options, 10_000, mergeBeastResultSchema);
    }

    async setTargetPriority(options: { towerId: string; priority: string; targetIndex?: number }) {
        return this.request('set_target_priority', options, 10_000, setTargetPriorityResultSchema);
    }

    async startRound(options: {
        waitForCompletion?: boolean;
        timeoutSeconds?: number;
        fastForward?: boolean;
        outputProfile?: 'summary' | 'full';
    } = {}): Promise<StartRoundResult> {
        const waitForCompletion = options.waitForCompletion ?? true;
        if ((options.waitForCompletion !== undefined && typeof options.waitForCompletion !== 'boolean')
            || (options.timeoutSeconds !== undefined &&
                (typeof options.timeoutSeconds !== 'number' || !Number.isFinite(options.timeoutSeconds * 1000) || options.timeoutSeconds <= 0))
            || (options.fastForward !== undefined && typeof options.fastForward !== 'boolean')
            || (options.outputProfile !== undefined && options.outputProfile !== 'summary' && options.outputProfile !== 'full')) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'startRound options require a positive finite timeoutSeconds, a boolean fastForward, and outputProfile summary or full.',
                false,
                undefined,
                'not_submitted'
            );
        }

        let before = await this.roundProgress();
        const uiDeadline = Date.now() + (options.timeoutSeconds ?? 120) * 1000;
        while (before.activeGame && !before.ui.ready) {
            if (uiNeedsDecision(before.ui) || Date.now() >= uiDeadline) {
                return { started: false, round: before.round ?? 1, completed: false,
                    roundCompleted: null, statusReason: 'ui_blocked', ui: before.ui,
                    cash: before.cash, lives: before.lives };
            }
            await sleep(100);
            before = await this.roundProgress();
        }
        if (options.fastForward !== undefined) {
            await this.setGameSpeed({ fastForward: options.fastForward });
        }
        const res = await this.request('start_round', {}, 10_000, startRoundResultSchema);

        if (!waitForCompletion) {
            return res;
        }

        let waitResult;
        try {
            waitResult = await this.waitForRoundEnd({
                timeoutSeconds: options.timeoutSeconds,
                initialRound: res.round,
                initialMatchId: before?.matchId ?? undefined,
                outputProfile: options.outputProfile
            });
        } catch (error) {
            const requestId = error instanceof BridgeClientError ? error.requestId : undefined;
            const code = error instanceof BridgeClientError ? error.code : 'WAIT_FAILED';
            const message = error instanceof Error ? error.message : String(error);
            throw new BridgeClientError(
                code,
                `Round ${res.round} was accepted, but the subsequent completion read failed: ${message}`,
                false,
                requestId,
                'submitted_outcome_unknown'
            );
        }

        return {
            started: res.started,
            round: waitResult.round,
            roundCompleted: waitResult.roundCompleted,
            completed: waitResult.completed,
            timedOut: waitResult.timedOut,
            statusReason: waitResult.statusReason,
            gameStatus: waitResult.gameStatus,
            cash: waitResult.cash,
            lives: waitResult.lives,
            summary: waitResult.summary,
            ui: waitResult.ui,
            ...(waitResult.roundReport ? { roundReport: waitResult.roundReport } : {}),
            ...(waitResult.observation ? { observation: waitResult.observation } : {})
        };
    }

    async setRound(options: { round: number }) {
        return this.request('set_round', { round: options.round }, 10_000, setRoundResultSchema);
    }

    async advanceRound() {
        return this.request('advance_round', {}, 10_000, advanceRoundResultSchema);
    }

    async sandboxSpawnRound(options?: { round?: number }) {
        return this.request('sandbox_spawn_round', options ?? {}, 10_000, sandboxSpawnRoundResultSchema);
    }

    async sandboxClearBloons() {
        return this.request('sandbox_clear_bloons', {}, 10_000, sandboxClearBloonsResultSchema);
    }

    async waitForRoundEnd(options: {
        timeoutSeconds?: number;
        pollIntervalMs?: number;
        initialRound?: number | null;
        initialMatchId?: string;
        outputProfile?: 'summary' | 'full';
    } = {}): Promise<WaitForRoundEndResult> {
        const timeoutMs = (options.timeoutSeconds ?? 120) * 1000;
        const pollInterval = options.pollIntervalMs ?? 350;
        if (!Number.isFinite(timeoutMs) || timeoutMs <= 0 || !Number.isFinite(pollInterval) || pollInterval < 25) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'Waiting requires a positive finite timeout and poll interval of at least 25ms.', false, undefined, 'not_submitted');
        }
        const deadline = Date.now() + timeoutMs;
        let initialRound = options.initialRound;
        let initialMatchId = options.initialMatchId;
        let seenActive = false;
        let completed = false;
        let roundCompleted: number | null = null;
        let statusReason: RoundStatusReason = 'timeout';
        let blockingUi: UiState | undefined;

        while (Date.now() < deadline) {
            let progress: RoundProgress;
            try {
                progress = await this.request(
                    'round_progress',
                    {},
                    Math.min(DEFAULT_TIMEOUT_MS, Math.max(1, deadline - Date.now())),
                    progressSchema
                );
            } catch (error) {
                if (Date.now() >= deadline && error instanceof BridgeClientError
                    && (error.code === 'BRIDGE_UNAVAILABLE'
                        || error.code === 'DEADLINE_EXCEEDED'
                        || error.code === 'SUBMISSION_OUTCOME_UNKNOWN')) {
                    break;
                }
                throw error;
            }
            if (initialMatchId !== undefined && progress.activeGame && progress.matchId !== initialMatchId) {
                throw new BridgeClientError('MATCH_CHANGED', 'Match was restarted or replaced while waiting for the round.', false, undefined, 'submitted_outcome_unknown');
            }
            initialMatchId ??= progress.matchId ?? undefined;
            initialRound ??= progress.round;
            seenActive ||= progress.roundActive === true;

            if (!progress.activeGame) {
                statusReason = 'match_ended';
                break;
            }
            if (progress.gameStatus === 'victory') {
                statusReason = 'victory';
                completed = true;
                roundCompleted = initialRound ?? progress.round;
                break;
            }
            if (progress.gameStatus === 'defeat') {
                statusReason = 'defeat';
                completed = false;
                roundCompleted = null;
                break;
            }
            if(!progress.ui.ready) {
                if(uiNeedsDecision(progress.ui)) {
                    statusReason='ui_blocked';
                    blockingUi=progress.ui;
                    break;
                }
                await sleep(Math.min(pollInterval,Math.max(0,deadline-Date.now())));
                continue;
            }

            const advanced = initialRound != null && progress.round != null && progress.round > initialRound;
            const finished = progress.roundActive === false && (seenActive || advanced);
            if (finished && (progress.gameStatus === 'in_game' || progress.gameStatus === 'paused')) {
                statusReason = 'round_cleared';
                completed = true;
                roundCompleted = initialRound;
                break;
            }
            await sleep(Math.min(pollInterval, Math.max(0, deadline - Date.now())));
        }

        // Exactly one full capture, including on timeout. Polling never walks towers/bloons.
        const observation = await this.observe();
        const finalProgress = await this.roundProgress();
        if (initialMatchId !== undefined && finalProgress.activeGame && finalProgress.matchId !== initialMatchId) {
            throw new BridgeClientError('MATCH_CHANGED', 'Match changed during final observation capture.', false, undefined, 'submitted_outcome_unknown');
        }

        const totalPops = observation.towers.reduce((sum, t) => sum + (t.pops ?? 0), 0);
        const totalDamage = observation.towers.reduce((sum, t) => sum + (t.damageDealt ?? 0), 0);
        const summary = {
            round: observation.round ?? (initialRound ?? 0),
            roundCompleted,
            cash: observation.cash,
            lives: observation.lives,
            towerCount: observation.towerCount,
            totalPops,
            totalDamage
        };

        const result: {
            completed: boolean;
            timedOut: boolean;
            statusReason: RoundStatusReason;
            ui: UiState;
            round: number;
            roundCompleted: number | null;
            gameStatus: string;
            cash: number | null;
            lives: number | null;
            summary: typeof summary;
            observation?: BridgeObservation;
            roundReport?: RoundReport;
        } = {
            completed,
            timedOut: statusReason === 'timeout',
            statusReason,
            ui: blockingUi ?? finalProgress.ui,
            round: observation.round ?? (initialRound ?? 0),
            roundCompleted,
            gameStatus: observation.gameStatus ?? (completed ? 'in_game' : statusReason),
            cash: observation.cash,
            lives: observation.lives,
            summary
        };

        if (options.outputProfile === 'full') {
            result.observation = observation;
        }
        const report = observation.roundReport;
        if (report && report.round === initialRound
            && (initialMatchId === undefined || report.matchId === initialMatchId)
            && ((statusReason === 'defeat' && report.status === 'defeat')
                || (statusReason === 'round_cleared' && report.status === 'completed')
                || (statusReason === 'victory' && report.status === 'victory'))) {
            if (options.outputProfile === 'full') {
                result.roundReport = report;
            } else {
                const { pressureByLane: _lanes, topThreats: _worstThreats, ...compact } = report;
                result.roundReport = {
                    ...compact,
                    composition: report.composition.slice(0, 5),
                    omittedCompositionGroups: report.omittedCompositionGroups + Math.max(0, report.composition.length - 5),
                    leaks: {
                        ...report.leaks,
                        retained: Math.min(5, report.leaks.retained),
                        omitted: report.leaks.omitted + Math.max(0, report.leaks.retained - 5),
                        entries: report.leaks.entries.slice(-5)
                    },
                    actions: report.actions.slice(-8),
                    omittedActions: Math.max(0, report.actions.length - 8)
                };
            }
        }

        return result;
    }

    async setGameSpeed(options: { fastForward?: boolean } = {}) {
        return this.request('set_game_speed', options, 10_000, setGameSpeedResultSchema);
    }

    async activateAbility(options: ActivateAbilityInput = {}) {
        if (!activateAbilityInputSchema.safeParse(options).success) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'Ability activation requires at most one selector and a valid target.',
                false,
                undefined,
                'not_submitted'
            );
        }
        return this.request('activate_ability', options, 10_000, activateAbilityResultSchema);
    }

    async respondUi(options: { blockerId: string; action: UiAction; value?: string }) {
        return this.request('respond_ui', options, 10_000, respondUiResultSchema);
    }

    async pauseMatch() {
        return this.request('pause_match', {}, 10_000, pauseMatchResultSchema);
    }

    async resumeMatch() {
        return this.request('resume_match', {}, 10_000, resumeMatchResultSchema);
    }

    async scheduleAbility(options: ScheduleAbilityInput = {}) {
        if (!scheduleAbilityInputSchema.safeParse(options).success) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'Ability scheduling requires valid timing, at most one selector, and a valid target.',
                false,
                undefined,
                'not_submitted'
            );
        }
        return this.request('schedule_ability', options, 10_000, scheduleAbilityResultSchema);
    }

    async cancelScheduledAbility(options: {
        scheduleId?: string;
    } = {}) {
        return this.request('cancel_scheduled_ability', options, 10_000, cancelScheduledAbilityResultSchema);
    }

    async saveCheckpoint(options: { label?: string } = {}) {
        if (!isObject(options)
            || (options.label !== undefined && !checkpointLabelSchema.safeParse(options.label).success)) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'label must be omitted or a non-empty string of at most 128 characters without control characters.',
                false,
                undefined,
                'not_submitted'
            );
        }
        return this.request('save_checkpoint', options, 10_000, saveCheckpointResultSchema);
    }

    async restoreCheckpoint(options: { checkpointId?: string; round?: number; label?: string } = {}) {
        if (!isObject(options)
            || [options.checkpointId, options.round, options.label].filter(value => value !== undefined).length > 1
            || (options.checkpointId !== undefined && (typeof options.checkpointId !== 'string' || options.checkpointId.length < 1 || options.checkpointId.length > 128))
            || (options.round !== undefined && (!Number.isInteger(options.round) || options.round <= 0))
            || (options.label !== undefined && !checkpointLabelSchema.safeParse(options.label).success)) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'Provide at most one valid checkpointId, label, or positive round.', false, undefined, 'not_submitted');
        }
        if (options.checkpointId !== undefined) await this.requireCapability('checkpointTransfer');
        const restored = await this.request('restore_checkpoint', options, 15_000, restoreCheckpointResultSchema);

        const deadline = Date.now() + 15_000;
        let progress: RoundProgress | undefined;
        let observation: BridgeObservation | undefined;
        let stateError: string | undefined;
        try {
            do {
                progress = await this.roundProgress();
                const settled = progress.activeGame
                    && progress.round === restored.round
                    && progress.roundActive === false
                    && progress.ui.ready;
                if (settled || uiNeedsDecision(progress.ui) || !progress.activeGame) break;
                await sleep(100);
            } while (Date.now() < deadline);
            observation = await this.observe();
        } catch (error) {
            stateError = error instanceof Error ? error.message : String(error);
        }
        const stateReady = observation?.activeGame === true
            && observation.round === restored.round
            && observation.roundActive === false
            && observation.ui.ready;
        return {
            ...restored,
            stateReady,
            schedulesCleared: true,
            ...(observation ? { observation } : {}),
            ...(stateError ? { stateError } : {})
        };
    }

    async exportCheckpoints(options: z.input<typeof exportCheckpointsInputSchema> = {}) {
        const parsed = exportCheckpointsInputSchema.safeParse(options);
        if (!parsed.success) throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        await this.requireCapability('checkpointTransfer');
        return this.request('export_checkpoints', parsed.data, 30_000, exportCheckpointsResultSchema);
    }

    async importCheckpoints(options: z.input<typeof importCheckpointsInputSchema>) {
        const parsed = importCheckpointsInputSchema.safeParse(options);
        if (!parsed.success) throw new BridgeClientError('INVALID_ARGUMENT', parsed.error.message, false, undefined, 'not_submitted');
        await this.requireCapability('checkpointTransfer');
        return this.request('import_checkpoints', parsed.data, 30_000, importCheckpointsResultSchema);
    }

    async listCheckpoints() {
        return this.request('list_checkpoints', {}, 10_000, listCheckpointsResultSchema);
    }

    async deleteCheckpoint(options: { checkpointId?: string; label?: string; round?: number } = {}) {
        if (!isObject(options)
            || [options.checkpointId, options.label, options.round].filter(value => value !== undefined).length !== 1
            || (options.checkpointId !== undefined && (typeof options.checkpointId !== 'string' || !options.checkpointId.length || options.checkpointId.length > 128))
            || (options.label !== undefined && !checkpointLabelSchema.safeParse(options.label).success)
            || (options.round !== undefined && (!Number.isInteger(options.round) || options.round <= 0))) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'Provide exactly one valid checkpointId, label, or positive round.', false, undefined, 'not_submitted');
        }
        if (options.checkpointId !== undefined) await this.requireCapability('checkpointTransfer');
        return this.request('delete_checkpoint', options, 10_000, deleteCheckpointResultSchema);
    }

    async getRoundInfo(options: { round?: number } = {}): Promise<RoundInfoResult> {
        return this.request('round_info', options, 10_000, roundInfoResultSchema);
    }

    async inspectSupportCoverage(options: { towerId: string }) {
        return this.request('inspect_support_coverage', options, 10_000, supportCoverageSchema);
    }

    async findPlacementSpots(options: {
        towerType?: string;
        near?: { x: number; y: number; radius?: number };
        minDistanceToTrack?: number;
        maxDistanceToTrack?: number;
        limit?: number;
        withinRangeOfTowerId?: string;
        coverTowerIds?: string[];
        rankingStrategy?: 'distanceToSupport' | 'distanceToTrack' | 'balanced' | 'trackCoverage' | 'supportCoverage';
    } = {}): Promise<FindPlacementSpotsResult> {
        const supportId = options.withinRangeOfTowerId;
        if (supportId !== undefined &&
            (typeof supportId !== 'string' || supportId.length < 1 || supportId.length > 128 || supportId.trim().length === 0)) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'withinRangeOfTowerId must be a non-empty tower ID of at most 128 characters.',
                false,
                undefined,
                'not_submitted'
            );
        }
        const coverTowerIds = options.coverTowerIds;
        if (coverTowerIds !== undefined && (!Array.isArray(coverTowerIds) || coverTowerIds.length < 1 || coverTowerIds.length > 20 ||
            new Set(coverTowerIds).size !== coverTowerIds.length ||
            coverTowerIds.some(id => typeof id !== 'string' || id.trim().length < 1 || id.length > 128))) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'coverTowerIds must contain 1-20 unique non-empty tower IDs.', false, undefined, 'not_submitted');
        }
        const rankingStrategy = options.rankingStrategy;
        if (rankingStrategy !== undefined &&
            !['balanced', 'distanceToSupport', 'distanceToTrack', 'trackCoverage', 'supportCoverage'].includes(rankingStrategy)) {
            throw new BridgeClientError(
                'INVALID_ARGUMENT',
                'rankingStrategy must be balanced, distanceToSupport, distanceToTrack, trackCoverage, or supportCoverage.',
                false,
                undefined,
                'not_submitted'
            );
        }
        if (rankingStrategy === 'supportCoverage' && coverTowerIds === undefined) {
            throw new BridgeClientError('INVALID_ARGUMENT', 'supportCoverage requires coverTowerIds.', false, undefined, 'not_submitted');
        }
        return this.request('find_placement_spots', options, 10_000, findPlacementSpotsResultSchema);
    }

    async getMapLayout(options: { round?: number; rangeTowerIds?: string[] } = {}): Promise<MapLayout> {
        return this.request('get_map_layout', options, 10_000, mapLayoutSchema);
    }

    async setTowerTargetPosition(options: { towerId: string; x: number; y: number; targetIndex?: number; pointIndex?: number; points?: { x: number; y: number }[] }) {
        return this.request('set_tower_target_position', options, 10_000, setTowerTargetPositionResultSchema);
    }

    async toggleSubmerge(options: { towerId: string; submerged?: boolean }) {
        return this.request('toggle_submerge', options, 10_000, toggleSubmergeResultSchema);
    }

    async collectBank(options: { towerId: string }) {
        return this.request('collect_bank', options, 10_000, collectBankResultSchema);
    }

    async setAutoCollect(options: { enabled: boolean }) {
        return this.request('set_auto_collect', options, 10_000, setAutoCollectResultSchema);
    }

    async collectDrops() {
        return this.request('collect_drops', {}, 10_000, collectDropsResultSchema);
    }

    async getProjectedCash(options: { targetRound: number; fromRound?: number; currentCash?: number }): Promise<ProjectedCashResult> {
        return this.request('get_projected_cash', options, 10_000, projectedCashResultSchema);
    }

    async projectParagonDegree(options: {
        paragonType: string;
        additionalCashSlider?: number;
        excludeTowerIds?: string[];
    }): Promise<ProjectParagonDegreeResult> {
        return this.request('project_paragon_degree', options, 10_000, projectParagonDegreeResultSchema);
    }

    async inspectTempleSacrifices(options: { towerId: string }): Promise<InspectTempleSacrificesResult> {
        return this.request('inspect_temple_sacrifices', options, 10_000, inspectTempleSacrificesResultSchema);
    }

    async inspectMonkeyopolisSacrifices(options: { towerId: string }): Promise<InspectMonkeyopolisSacrificesResult> {
        return this.request('inspect_monkeyopolis_sacrifices', options, 10_000, inspectMonkeyopolisSacrificesResultSchema);
    }

    async bossCatalog(options: { bossType?: string } = {}): Promise<BossCatalogResult> {
        if (options.bossType !== undefined && (typeof options.bossType !== 'string' ||
            !options.bossType.trim() || options.bossType.length > 128))
            throw new BridgeClientError('INVALID_ARGUMENT', 'bossType must be a non-empty string of at most 128 characters.', false, undefined, 'not_submitted');
        return this.request('boss_catalog', options, 15_000, bossCatalogResultSchema);
    }

    async inspectBoss(options: { bloonId?: string } = {}): Promise<InspectBossResult> {
        if (options.bloonId !== undefined && (typeof options.bloonId !== 'string' ||
            !options.bloonId.trim() || options.bloonId.length > 128))
            throw new BridgeClientError('INVALID_ARGUMENT', 'bloonId must be a non-empty string of at most 128 characters.', false, undefined, 'not_submitted');
        return this.request('inspect_boss', options, 10_000, inspectBossResultSchema);
    }


    async towerCatalog(): Promise<TowerCatalog> {
        return this.request('tower_catalog', {}, 15_000, validateTowerCatalog);
    }

    async towerStats(options: { towerType: string; tiers?: [number, number, number] }): Promise<{ source: 'live-game-model'; btd6Version: string; stats: TowerStats }> {
        return this.request('tower_stats', options, 10_000, validateTowerStatsResult);
    }

    async inspectTower(options: { towerId: string }): Promise<TowerInspectionResult> {
        return this.request('inspect_tower', options, 10_000, validateTowerInspection);
    }
    private async request<T>(
        kind: string,
        payload: Record<string, unknown>,
        timeoutMs: number,
        validator: ResponseValidator<T>
    ): Promise<T> {
        const inbox = resolve(this.ipcRoot, 'inbox');
        const outbox = resolve(this.ipcRoot, 'outbox');
        await assertAccessible(inbox);
        await assertAccessible(outbox);

        const requestId = `mcp-${randomUUID()}`;
        const now = new Date();
        const deadline = new Date(now.getTime() + timeoutMs);
        const commandPath = resolve(inbox, `${requestId}.json`);
        const temporaryPath = resolve(inbox, `.${requestId}.${randomUUID()}.tmp`);
        const resultPath = resolve(outbox, `${requestId}.json`);
        const command = {
            protocolVersion: PROTOCOL_VERSION,
            requestId,
            kind,
            issuedAtUtc: now.toISOString(),
            deadlineAtUtc: deadline.toISOString(),
            payload
        };

        try {
            await writeFile(temporaryPath, JSON.stringify(command), { encoding: 'utf8', mode: 0o600 });
            await rename(temporaryPath, commandPath);
        } catch (error) {
            await unlink(temporaryPath).catch(() => {});
            throw new BridgeClientError(
                'IPC_WRITE_FAILED',
                `Could not submit AgentBridge command '${kind}': ${error instanceof Error ? error.message : String(error)}`,
                true,
                requestId,
                'not_submitted'
            );
        }

        let result: BridgeResult;
        try {
            result = await waitForResult(resultPath, deadline.getTime(), requestId);
        } catch (error) {
            if (error instanceof BridgeClientError) {
                throw new BridgeClientError(
                    error.code,
                    error.message,
                    false,
                    requestId,
                    'submitted_outcome_unknown'
                );
            }
            throw new BridgeClientError(
                'MALFORMED_BRIDGE_RESPONSE',
                `Cannot read AgentBridge response: ${error instanceof Error ? error.message : String(error)}`,
                false,
                requestId,
                'submitted_outcome_unknown'
            );
        }

        if (result.protocolVersion !== PROTOCOL_VERSION) {
            throw new BridgeClientError(
                'INCOMPATIBLE_PROTOCOL',
                `AgentBridge returned protocol v${result.protocolVersion}; this adapter requires v${PROTOCOL_VERSION}.`,
                false,
                requestId,
                'submitted_outcome_unknown'
            );
        }
        if (result.requestId !== requestId) {
            throw new BridgeClientError(
                'MALFORMED_BRIDGE_RESPONSE',
                'AgentBridge returned a mismatched request ID.',
                false,
                requestId,
                'submitted_outcome_unknown'
            );
        }
        if (!result.ok) {
            const error = result.error;
            if (!isObject(error)
                || typeof error.code !== 'string'
                || typeof error.message !== 'string'
                || typeof error.retryable !== 'boolean'
                || (error.details != null && !isObject(error.details))) {
                throw new BridgeClientError(
                    'MALFORMED_BRIDGE_RESPONSE',
                    'AgentBridge returned a rejected command without a structured error.',
                    false,
                    requestId,
                    'submitted_outcome_unknown'
                );
            }
            const submissionState = PRE_EXECUTION_ERROR_CODES[error.code] === true
                ? 'rejected'
                : 'submitted_outcome_unknown';
            throw new BridgeClientError(
                error.code,
                error.message,
                submissionState === 'rejected' ? error.retryable : false,
                requestId,
                submissionState,
                isObject(error.details) ? error.details : undefined
            );
        }

        try {
            if (typeof validator === 'function') {
                return validator(result.result);
            }
            const parsed = validator.safeParse(result.result);
            if (!parsed.success) {
                throw new Error(`Invalid bridge payload: ${parsed.error.message}`);
            }
            return parsed.data;
        } catch (error) {
            if (error instanceof BridgeClientError) {
                throw new BridgeClientError(
                    error.code,
                    error.message,
                    false,
                    requestId,
                    'submitted_outcome_unknown'
                );
            }
            throw new BridgeClientError(
                'MALFORMED_BRIDGE_RESPONSE',
                error instanceof Error ? error.message : String(error),
                false,
                requestId,
                'submitted_outcome_unknown'
            );
        }
    }
}
export interface RunRoundBatchOptions {
    count: number;
    stopBeforeRounds: number[];
    timeoutSecondsPerRound: number;
    fastForward?: boolean;
}

export interface RunRoundBatchFailure {
    /** One-based batch iteration whose startRound call failed. */
    iteration: number;
    code: string;
    message: string;
    retryable: boolean;
    submissionState: SubmissionState;
    requestId?: string;
    details?: Record<string, unknown>;
}

export interface RunRoundBatchResult {
    requestedCount: number;
    /** Number of startRound calls made, including a failed call. */
    attemptedCount: number;
    stopReason: string;
    nextPlayableRound: number | null;
    outcomes: StartRoundResult[];
    failure: RunRoundBatchFailure | null;
}
/** Convert a partially completed batch into a conservative aggregate error. */
export function roundBatchAggregateError(batch: RunRoundBatchResult): BridgeClientError | null {
    if (!batch.failure) return null;
    const failure = batch.failure;
    return new BridgeClientError(
        'BATCH_PARTIALLY_COMPLETED',
        `Round batch stopped at iteration ${failure.iteration} after ${batch.outcomes.length} completed outcome(s); inspect the returned prefix before continuing.`,
        false,
        undefined,
        'submitted_outcome_unknown',
        {
            completedOutcomeCount: batch.outcomes.length,
            failedIteration: failure.iteration,
            failedIterationError: failure
        }
    );
}

export async function runRoundBatch(
    client: Pick<BridgeClient, 'roundProgress' | 'startRound'>,
    options: RunRoundBatchOptions
): Promise<RunRoundBatchResult> {
    const initial = await client.roundProgress();
    const outcomes: StartRoundResult[] = [];
    let nextPlayableRound = initial.round;
    let stopReason = 'count_reached';
    let failure: RunRoundBatchFailure | null = null;
    for (let index = 0; index < options.count; index++) {
        if (nextPlayableRound !== null && options.stopBeforeRounds.includes(nextPlayableRound)) {
            stopReason = 'strategic_boundary';
            break;
        }
        try {
            const result = await client.startRound({
                waitForCompletion: true,
                timeoutSeconds: options.timeoutSecondsPerRound,
                fastForward: index === 0 ? options.fastForward : undefined,
                outputProfile: 'full'
            });
            outcomes.push(result);
            nextPlayableRound = result.round;
            if (result.statusReason !== 'round_cleared') {
                stopReason = result.statusReason ?? 'round_not_cleared';
                break;
            }
        } catch (error) {
            const submissionState = error instanceof BridgeClientError
                ? error.submissionState ?? 'submitted_outcome_unknown'
                : 'submitted_outcome_unknown';
            failure = {
                iteration: index + 1,
                code: error instanceof BridgeClientError ? error.code : 'ADAPTER_FAILURE',
                message: error instanceof Error ? error.message : String(error),
                retryable: error instanceof BridgeClientError && submissionState !== 'submitted_outcome_unknown'
                    ? error.retryable : false,
                submissionState,
                ...(error instanceof BridgeClientError && error.requestId !== undefined ? { requestId: error.requestId } : {}),
                ...(error instanceof BridgeClientError && error.details !== undefined ? { details: error.details } : {})
            };
            stopReason = 'action_error';
            if (submissionState === 'submitted_outcome_unknown') nextPlayableRound = null;
            break;
        }
    }
    return {
        requestedCount: options.count,
        attemptedCount: outcomes.length + (failure ? 1 : 0),
        stopReason,
        nextPlayableRound,
        outcomes,
        failure
    };
}


async function assertAccessible(path: string): Promise<void> {
    try {
        await access(path);
    } catch {
        throw new BridgeClientError(
            'BRIDGE_UNAVAILABLE',
            `AgentBridge mailbox is unavailable at ${path}. Launch BTD6 in research mode and wait for AgentBridge to load.`,
            true,
            undefined,
            'not_submitted'
        );
    }
}

async function waitForResult(path: string, deadlineMs: number, requestId: string): Promise<BridgeResult> {
    while (Date.now() <= deadlineMs) {
        try {
            const raw = await readFile(path, 'utf8');
            await unlink(path).catch(() => {});
            return parseResult(raw);
        } catch (error) {
            if (!(error instanceof Error) || !('code' in error) || error.code !== 'ENOENT') {
                if (error instanceof BridgeClientError) {
                    throw error;
                }
                throw new BridgeClientError(
                    'MALFORMED_BRIDGE_RESPONSE',
                    `Cannot parse AgentBridge response: ${error instanceof Error ? error.message : String(error)}`,
                    false
                );
            }
        }
        await new Promise<void>(resolve => setTimeout(resolve, RESULT_POLL_INTERVAL_MS));
    }

    throw new BridgeClientError(
        'SUBMISSION_OUTCOME_UNKNOWN',
        `AgentBridge did not answer before the command deadline (request ID: ${requestId}). The command was submitted but its outcome is unknown. Verify game state before repeating any mutations.`,
        false,
        requestId,
        'submitted_outcome_unknown'
    );
}

function parseResult(raw: string): BridgeResult {
    let value: unknown;
    try {
        value = JSON.parse(raw);
    } catch {
        throw new BridgeClientError('MALFORMED_BRIDGE_RESPONSE', 'AgentBridge response is not valid JSON.', false);
    }
    if (!isObject(value)
        || typeof value.protocolVersion !== 'number'
        || typeof value.requestId !== 'string'
        || typeof value.ok !== 'boolean'
        || !('result' in value)
        || !('error' in value)) {
        throw new BridgeClientError('MALFORMED_BRIDGE_RESPONSE', 'AgentBridge response does not match the v1 result envelope.', false);
    }

    return value as unknown as BridgeResult;
}
function malformedResponse(_value: unknown, message: string): BridgeClientError {
    return new BridgeClientError('MALFORMED_BRIDGE_RESPONSE', message, false);
}

function validateStatus(value: unknown): BridgeStatus {
    if (!isObject(value)
        || typeof value.protocolVersion !== 'number'
        || typeof value.bridgeVersion !== 'string'
        || typeof value.moduleVersionId !== 'string'
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.moduleVersionId)
        || typeof value.btd6Version !== 'string'
        || typeof value.gameAvailable !== 'boolean'
        || typeof value.activeGame !== 'boolean'
        || !isObject(value.capabilities)
        || typeof value.capabilities.status !== 'boolean'
        || typeof value.capabilities.observation !== 'boolean'
        || typeof value.capabilities.playerActions !== 'boolean'
        || typeof value.capabilities.researchControls !== 'boolean') {
        throw malformedResponse(value, 'AgentBridge status payload does not match protocol v1.');
    }
    if (value.protocolVersion !== PROTOCOL_VERSION) {
        throw new BridgeClientError(
            'INCOMPATIBLE_PROTOCOL',
            `AgentBridge status reports protocol v${value.protocolVersion}.`,
            false
        );
    }
    uiStateSchema.parse(value.ui);
    for (const name of ['scheduledUpgrades', 'roundInsights', 'checkpointTransfer', 'geraldoShop', 'corvusSpells']) {
        if (value.capabilities[name] !== undefined && typeof value.capabilities[name] !== 'boolean') {
            throw malformedResponse(value, `Invalid capability ${name}.`);
        }
    }
    return value as unknown as BridgeStatus;
}

function validateTowerInfo(value: unknown): TowerInfo {
    if (!isObject(value)
        || typeof value.id !== 'string'
        || typeof value.towerType !== 'string'
        || typeof value.name !== 'string'
        || !isObject(value.position)
        || typeof (value.position as JsonObject).x !== 'number'
        || typeof (value.position as JsonObject).y !== 'number'
        || typeof value.range !== 'number'
        || typeof value.targetPriority !== 'string'
        || !Array.isArray(value.tiers)
        || value.tiers.length !== 3
        || !Array.isArray(value.upgradeCosts)
        || value.upgradeCosts.length !== 3
        || value.upgradeCosts.some(cost => cost !== null && (typeof cost !== 'number' || !Number.isFinite(cost) || cost < 0))
        || !Array.isArray(value.nextUpgrades)
        || value.nextUpgrades.length !== 3
        || value.nextUpgrades.some(upgrade => !isObject(upgrade)
            || typeof upgrade.path !== 'number'
            || !isNullableString(upgrade.name)
            || !isNullableNumber(upgrade.cost)
            || typeof upgrade.available !== 'boolean')
        || !isNullableNumber(value.crosspathSlotsRemaining)
        || typeof value.sellValue !== 'number'
        || typeof value.damageDealt !== 'number'
        || typeof value.pops !== 'number'
        || (value.cashEarned !== undefined && typeof value.cashEarned !== 'number')
        || (value.bank !== undefined && value.bank !== null && (!isObject(value.bank)
            || typeof (value.bank as JsonObject).cash !== 'number'
            || typeof (value.bank as JsonObject).capacity !== 'number'
            || typeof (value.bank as JsonObject).interest !== 'number'
            || typeof (value.bank as JsonObject).isFull !== 'boolean'))
        || typeof value.isHero !== 'boolean') {
        throw malformedResponse(value, 'TowerInfo payload does not match protocol schema.');
    }
    return value as unknown as TowerInfo;
}

function validateHeroInfo(value: unknown): HeroInfo | null {
    if (value === null || value === undefined) return null;
    if (!isObject(value)
        || typeof value.heroId !== 'string'
        || typeof value.level !== 'number'
        || typeof value.xp !== 'number'
        || typeof value.xpToNextLevel !== 'number'
        || typeof value.costToLevelUp !== 'number') {
        throw malformedResponse(value, 'HeroInfo payload does not match protocol schema.');
    }
    return value as unknown as HeroInfo;
}

function validateAbilityInfo(value: unknown): AbilityInfo {
    if (!isObject(value)
        || typeof value.abilityId !== 'string'
        || typeof value.towerId !== 'string'
        || typeof value.name !== 'string'
        || typeof value.isReady !== 'boolean'
        || typeof value.canUse !== 'boolean'
        || typeof value.cooldownRemaining !== 'number'
        || typeof value.cooldownTotal !== 'number'
        || !abilityTargetingSchema.safeParse(value.targeting).success) {
        throw malformedResponse(value, 'AbilityInfo payload does not match protocol schema.');
    }
    return value as unknown as AbilityInfo;
}

function validateScheduledAbilityInfo(value: unknown): ScheduledAbilityInfo {
    if (!isObject(value)
        || typeof value.scheduleId !== 'string'
        || typeof value.targetRound !== 'number'
        || typeof value.delaySeconds !== 'number'
        || typeof value.abilityIndex !== 'number'
        || !Number.isInteger(value.abilityIndex)
        || value.abilityIndex < 0
        || !('target' in value)
        || !abilityTargetSchema.nullable().safeParse(value.target).success) {
        throw malformedResponse(value, 'ScheduledAbilityInfo payload does not match protocol schema.');
    }
    return value as unknown as ScheduledAbilityInfo;
}

function validateObservation(value: unknown): BridgeObservation {
    if (!isObject(value)
        || typeof value.observedAtUtc !== 'string'
        || typeof value.activeGame !== 'boolean'
        || !isNullableNumber(value.round)
        || !isNullableBoolean(value.roundActive)
        || !isNullableNumber(value.cash)
        || !isNullableNumber(value.lives)
        || typeof value.towerCount !== 'number'
        || !isNullableString(value.mapId)
        || !isNullableString(value.mapName)
        || !isNullableString(value.mode)) {
        throw malformedResponse(value, 'AgentBridge observation payload does not match protocol v1.');
    }

    if (value.activeGame) {
        if (!Array.isArray(value.towers) || !Array.isArray(value.abilities) || !Array.isArray(value.scheduledAbilities)) {
            throw malformedResponse(value, 'Active game observation missing required tower/ability collections.');
        }
    }

    const towers = Array.isArray(value.towers) ? value.towers.map(item => validateTowerInfo(item)) : [];
    const hero = validateHeroInfo(value.hero);
    const abilities = Array.isArray(value.abilities) ? value.abilities.map(item => validateAbilityInfo(item)) : [];
    const scheduledAbilities = Array.isArray(value.scheduledAbilities)
        ? value.scheduledAbilities.map(item => validateScheduledAbilityInfo(item))
        : [];
    const scheduledGeraldoPurchases = value.scheduledGeraldoPurchases === undefined
        ? []
        : scheduledGeraldoPurchaseSchema.array().max(96).parse(value.scheduledGeraldoPurchases);
    const scheduledCorvusActions = value.scheduledCorvusActions === undefined
        ? []
        : scheduledCorvusActionSchema.array().max(96).parse(value.scheduledCorvusActions);

    return {
        observedAtUtc: value.observedAtUtc,
        activeGame: value.activeGame,
        ui: uiStateSchema.parse(value.ui),
        gameStatus: isNullableString(value.gameStatus) ? value.gameStatus : null,
        isPaused: isNullableBoolean(value.isPaused) ? value.isPaused : null,
        round: value.round,
        endRound: isNullableNumber(value.endRound) ? value.endRound : null,
        roundActive: value.roundActive,
        roundElapsedSeconds: isNullableNumber(value.roundElapsedSeconds) ? value.roundElapsedSeconds : null,
        inBetweenRounds: isNullableBoolean(value.inBetweenRounds) ? value.inBetweenRounds : null,
        canStartRound: isNullableBoolean(value.canStartRound) ? value.canStartRound : null,
        fastForward: isNullableBoolean(value.fastForward) ? value.fastForward : null,
        autoPlay: isNullableBoolean(value.autoPlay) ? value.autoPlay : null,
        cash: value.cash,
        lives: value.lives,
        maxLives: isNullableNumber(value.maxLives) ? value.maxLives : null,
        towerCount: value.towerCount,
        mapId: value.mapId,
        mapName: value.mapName,
        mode: value.mode,
        difficulty: isNullableString(value.difficulty) ? value.difficulty : null,
        towers,
        bosses: value.bosses === undefined ? [] : bossSummarySchema.array().max(128).parse(value.bosses),
        hero,
        scheduledGeraldoPurchases,
        scheduledCorvusActions,
        abilities,
        scheduledAbilities,
        scheduledUpgrades: value.scheduledUpgrades === undefined ? [] : scheduledUpgradeSchema.array().max(96).parse(value.scheduledUpgrades),
        roundInsights: value.roundInsights === undefined ? null : roundInsightsSchema.nullable().parse(value.roundInsights),
        roundReport: value.roundReport === undefined ? null : roundInsightsSchema.nullable().parse(value.roundReport),
        threatSnapshotSource: value.threatSnapshotSource === 'live' || value.threatSnapshotSource === 'defeat_report' ? value.threatSnapshotSource : null,
        availableCheckpoints: Array.isArray(value.availableCheckpoints) ? value.availableCheckpoints.filter((x): x is number => typeof x === 'number') : [],
        maxTrackProgress: isNullableNumber(value.maxTrackProgress) ? value.maxTrackProgress : null,
        activeBloonCount: isNullableNumber(value.activeBloonCount) ? value.activeBloonCount : null,
        activeMoabCount: isNullableNumber(value.activeMoabCount) ? value.activeMoabCount : null,
        trackProgressByLane: Array.isArray(value.trackProgressByLane) ? value.trackProgressByLane as TrackLaneProgress[] : [],
        activeThreats: Array.isArray(value.activeThreats) ? value.activeThreats as ActiveThreat[] : []
    };
}

function validateTowerCatalog(value: unknown): TowerCatalog {
    if (!isObject(value)
        || value.source !== 'live-game-model'
        || typeof value.btd6Version !== 'string'
        || !Array.isArray(value.towers)) {
        throw malformedResponse(value, 'Tower catalog payload does not match protocol schema.');
    }

    for (const tower of value.towers) {
        if (!isObject(tower)
            || typeof tower.towerType !== 'string'
            || typeof tower.name !== 'string'
            || typeof tower.baseCost !== 'number'
            || typeof tower.baseRange !== 'number'
            || typeof tower.isWaterBased !== 'boolean'
            || typeof tower.isAmphibious !== 'boolean'
            || !Array.isArray(tower.upgrades)) {
            throw malformedResponse(value, 'Tower catalog contains an invalid tower entry.');
        }
    }
    return value as unknown as TowerCatalog;
}

function validateDamageIntelligence(value: JsonObject): void {
    if (value.paragonBossDamage !== undefined) paragonBossDamageSchema.parse(value.paragonBossDamage);
    poppingCapabilitiesSchema.parse(value.poppingCapabilities);
    if (!Array.isArray(value.attacks)) return;
    for (const attack of value.attacks) {
        if (!isObject(attack) || !Array.isArray(attack.weapons))
            throw malformedResponse(value, 'Damage intelligence requires attack weapon collections.');
        for (const weapon of attack.weapons) {
            if (!isObject(weapon)) throw malformedResponse(value, 'Invalid damage source weapon.');
            camoCapabilitySchema.parse(weapon.camo);
            if (weapon.canDamageDdt !== null && typeof weapon.canDamageDdt !== 'boolean')
                throw malformedResponse(value, 'Weapon DDT capability must be boolean or explicitly unknown.');
            if (weapon.damageSources !== undefined) damageSourceSchema.array().max(64).parse(weapon.damageSources);
            if (weapon.damageCoverage !== undefined) damageCoverageSchema.parse(weapon.damageCoverage);
            if (weapon.damageModifiers !== undefined) damageModifierSchema.array().max(64).parse(weapon.damageModifiers);
        }
    }
}

function validateTowerStats(value: unknown): TowerStats {
    if (!isObject(value)
        || typeof value.towerType !== 'string'
        || typeof value.name !== 'string'
        || !Array.isArray(value.tiers)
        || value.tiers.length !== 3
        || typeof value.baseCost !== 'number'
        || typeof value.range !== 'number'
        || typeof value.isGlobalRange !== 'boolean'
        || typeof value.isWaterBased !== 'boolean'
        || typeof value.isAmphibious !== 'boolean'
        || !Array.isArray(value.attacks)) {
        throw malformedResponse(value, 'Tower stats payload does not match protocol schema.');
    }
    if (value.appliedDebuffs !== undefined) {
        if (!Array.isArray(value.appliedDebuffs)) {
            throw malformedResponse(value, 'appliedDebuffs must be an array.');
        }
        for (const debuff of value.appliedDebuffs) {
            if (!isObject(debuff)
                || typeof debuff.category !== 'string'
                || typeof debuff.name !== 'string'
                || typeof debuff.description !== 'string') {
                throw malformedResponse(value, 'Applied debuff payload does not match protocol schema.');
            }
        }
    }
    validateDamageIntelligence(value);
    return value as unknown as TowerStats;
}

function validateTowerStatsResult(value: unknown): { source: 'live-game-model'; btd6Version: string; stats: TowerStats } {
    if (!isObject(value) || value.source !== 'live-game-model' || typeof value.btd6Version !== 'string') {
        throw malformedResponse(value, 'Static tower stats response does not match protocol schema.');
    }
    return { source: value.source, btd6Version: value.btd6Version, stats: validateTowerStats(value.stats) };
}

function validateEffectiveTowerStats(value: unknown): EffectiveTowerStats {
    if (!isObject(value) || typeof value.available !== 'boolean') {
        throw malformedResponse(value, 'Effective tower stats payload does not match protocol schema.');
    }
    if (!value.available) {
        if (value.reason !== undefined && typeof value.reason !== 'string') {
            throw malformedResponse(value, 'Unavailable effective tower stats contain an invalid reason.');
        }
        return value as unknown as EffectiveTowerStats;
    }
    if (typeof value.totalRateModifier !== 'number'
        || !Array.isArray(value.attacks)
        || !Array.isArray(value.activeBuffs)) {
        throw malformedResponse(value, 'Available effective tower stats are incomplete.');
    }
    if (value.range !== undefined && typeof value.range !== 'number') {
        throw malformedResponse(value, 'Effective tower stats range must be a number.');
    }
    if (value.baseRange !== undefined && typeof value.baseRange !== 'number') {
        throw malformedResponse(value, 'Effective tower stats baseRange must be a number.');
    }
    for (const buff of value.activeBuffs) {
        if (!isObject(buff)
            || typeof buff.indicator !== 'string'
            || typeof buff.displayName !== 'string'
            || typeof buff.sourceTowerType !== 'string'
            || typeof buff.canCurrentlyBuff !== 'boolean'
            || typeof buff.canEventuallyBuff !== 'boolean'
            || typeof buff.availableCount !== 'number'
            || typeof buff.unavailableCount !== 'number'
            || typeof buff.mutatorId !== 'string'
            || typeof buff.stackCount !== 'number'
            || (buff.timeout !== null && (!isObject(buff.timeout)
                || typeof buff.timeout.removeAtFrame !== 'number'
                || typeof buff.timeout.usesRoundTime !== 'boolean'
                || typeof buff.timeout.onlyTimeoutWhenActive !== 'boolean'
                || typeof buff.timeout.roundsRemaining !== 'number'))
            || !Array.isArray(buff.effects)) {
            throw malformedResponse(value, 'Active buff payload does not match protocol schema.');
        }
        for (const effect of buff.effects) {
            if (!isObject(effect)
                || typeof effect.stat !== 'string'
                || typeof effect.operation !== 'string'
                || (effect.amount !== undefined && typeof effect.amount !== 'number')
                || (effect.multiplier !== undefined && typeof effect.multiplier !== 'number')
                || (effect.tag !== undefined && typeof effect.tag !== 'string')
                || (effect.description !== undefined && typeof effect.description !== 'string')
                || (effect.mutationId !== undefined && typeof effect.mutationId !== 'string')
                || (effect.condition !== undefined && typeof effect.condition !== 'string')) {
                throw malformedResponse(value, 'Buff effect payload does not match protocol schema.');
            }
        }
    }
    if (value.appliedDebuffs !== undefined) {
        if (!Array.isArray(value.appliedDebuffs)) {
            throw malformedResponse(value, 'appliedDebuffs must be an array.');
        }
        for (const debuff of value.appliedDebuffs) {
            if (!isObject(debuff)
                || typeof debuff.category !== 'string'
                || typeof debuff.name !== 'string'
                || typeof debuff.description !== 'string') {
                throw malformedResponse(value, 'Applied debuff payload does not match protocol schema.');
            }
        }
    }
    validateDamageIntelligence(value);
    return value as unknown as EffectiveTowerStats;
}

function validateTowerInspection(value: unknown): {
    source: 'active-simulation';
    observedAtUtc: string;
    tower: TowerInfo;
    modelStats: TowerStats;
    effectiveStats: EffectiveTowerStats;
} {
    if (!isObject(value) || value.source !== 'active-simulation' || typeof value.observedAtUtc !== 'string') {
        throw malformedResponse(value, 'Tower inspection response does not match protocol schema.');
    }
    return {
        source: value.source,
        observedAtUtc: value.observedAtUtc,
        tower: validateTowerInfo(value.tower),
        modelStats: validateTowerStats(value.modelStats),
        effectiveStats: validateEffectiveTowerStats(value.effectiveStats)
    };
}

function isObject(value: unknown): value is JsonObject {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNullableString(value: unknown): value is string | null {
    return value === null || typeof value === 'string';
}

function isNullableNumber(value: unknown): value is number | null {
    return value === null || typeof value === 'number';
}

function isNullableBoolean(value: unknown): value is boolean | null {
    return value === null || typeof value === 'boolean';
}

