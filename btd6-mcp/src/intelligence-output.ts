import type {
    TowerCatalog,
    TowerInspectionResult,
    ProjectedCashResult,
    EffectiveAttackStats,
    EffectiveTowerStats,
    EffectiveWeaponStats,
    TowerAttackStats,
    TowerCatalogEntry,
    TowerStats,
    TowerWeaponStats
} from './bridge-client.js';
import type { FindPlacementSpotsResult, RoundInfoResult } from './bridge-data.js';
import { summarizeTower } from './play-output.js';

type OutputProfile = 'summary' | 'full';

type TowerCatalogFilters = { towerType?: string };


/**
 * Format the native tower catalog as a compact index. A requested towerType is
 * matched case-insensitively against the native ID and includes that tower's
 * upgrade records so the result remains actionable for purchases.
 */
export function formatTowerCatalogOutput(
    result: TowerCatalog,
    profile: OutputProfile,
    filters: TowerCatalogFilters = {}
) {
    const hasFilter = filters.towerType !== undefined;
    const normalizedTowerType = filters.towerType?.toLowerCase();
    if (profile === 'full' && !hasFilter) return result;

    const towers = hasFilter
        ? result.towers.filter(tower => tower.towerType.toLowerCase() === normalizedTowerType)
        : result.towers;
    const noMatch = hasFilter && towers.length === 0;
    const availableTowerTypes = noMatch
        ? result.towers.map(tower => tower.towerType)
        : undefined;

    if (profile === 'full') {
        return {
            ...result,
            towers,
            filters: { towerType: filters.towerType },
            matched: towers.length > 0,
            returned: towers.length,
            ...(noMatch ? { availableTowerTypes } : {})
        };
    }

    const summaryTowers = towers.map(tower => formatCatalogEntry(tower, hasFilter));
    return {
        source: result.source,
        btd6Version: result.btd6Version,
        profile: 'summary' as const,
        ...(hasFilter ? { matched: towers.length > 0 } : {}),
        returned: summaryTowers.length,
        towers: summaryTowers,
        ...(hasFilter
            ? {
                filters: { towerType: filters.towerType },
                ...(noMatch ? { availableTowerTypes } : {})
            }
            : {})
    };
}

/**
 * Format model stats without recursively carrying the large projectile/source
 * trees. Retained numeric values are not rounded or combined into inferred DPS.
 */
export function formatTowerStatsOutput(result: TowerStats, profile: OutputProfile) {
    if (profile === 'full') return result;
    return {
        profile: 'summary' as const,
        towerType: result.towerType,
        name: result.name,
        tiers: result.tiers,
        baseCost: result.baseCost,
        range: result.range,
        isGlobalRange: result.isGlobalRange,
        isWaterBased: result.isWaterBased,
        isAmphibious: result.isAmphibious,
        poppingCapabilities: result.poppingCapabilities,
        attacks: result.attacks.map(formatTowerAttack),
        ...(result.paragonBossDamage !== undefined ? { paragonBossDamage: result.paragonBossDamage } : {}),
        ...(result.appliedDebuffs !== undefined ? { appliedDebuffs: result.appliedDebuffs } : {}),
        income: result.income,
        omittedFields: ['damageSources', 'projectileBehaviors']
    };
}

/**
 * Format a placed-tower inspection while retaining its native identity,
 * effective capabilities, dynamic buffs/debuffs, and income state. Effective
 * weapon values are projections of native values, not target-specific DPS.
 */
export function formatTowerInspectionOutput(result: TowerInspectionResult, profile: OutputProfile) {
    if (profile === 'full') return result;
    const effectiveStats = formatEffectiveTowerStats(result.effectiveStats);
    return {
        source: result.source,
        observedAtUtc: result.observedAtUtc,
        profile: 'summary' as const,
        tower: summarizeTower(result.tower),
        ...(result.effectiveStats.available ? {} : {
            modelStats: formatTowerStatsOutput(result.modelStats, 'summary')
        }),
        effectiveStats,
        omittedFields: [
            'damageSources',
            'projectileBehaviors',
            ...(result.effectiveStats.available ? ['modelStats'] : [])
        ]
    };
}

/**
 * Format round routing and threat intelligence without returning every spawn
 * group. Group count remains explicit so omitted schedule detail cannot be
 * mistaken for an empty wave.
 */
export function formatRoundInfoOutput(result: RoundInfoResult, profile: OutputProfile) {
    if (profile === 'full') return result;
    return {
        profile: 'summary' as const,
        round: result.round,
        activePaths: result.activePaths,
        routingPolicy: result.routingPolicy,
        traffic: result.traffic,
        totalBloons: result.totalBloons,
        durationSeconds: result.durationSeconds,
        threatIntel: result.threatIntel,
        omittedGroups: result.groups.length
    };
}

/**
 * Format economic projections without the potentially large per-round
 * breakdown. Totals, confidence, unsupported reasons, assumptions, income
 * sources, and threshold data remain available for conservative budgeting.
 */
export function formatProjectedCashOutput(result: ProjectedCashResult, profile: OutputProfile) {
    if (profile === 'full') return result;
    return {
        profile: 'summary' as const,
        fromRound: result.fromRound,
        targetRound: result.targetRound,
        startingCash: result.startingCash,
        confidence: result.confidence,
        assumptions: result.assumptions,
        unsupportedReasons: result.unsupportedReasons,
        baselineIncome: result.baselineIncome,
        towerIncome: result.towerIncome,
        estimatedCashAtStartOfTargetRound: result.estimatedCashAtStartOfTargetRound,
        estimatedCashAtEndOfTargetRound: result.estimatedCashAtEndOfTargetRound,
        incomeThresholds: result.incomeThresholds,
        finalIncomeMultiplier: result.finalIncomeMultiplier,
        omittedBreakdownRounds: result.breakdown.length
    };
}

function formatCatalogEntry(entry: TowerCatalogEntry, includeUpgrades: boolean) {
    return {
        towerType: entry.towerType,
        name: entry.name,
        baseCost: entry.baseCost,
        baseRange: entry.baseRange,
        isWaterBased: entry.isWaterBased,
        isAmphibious: entry.isAmphibious,
        ...(includeUpgrades ? { upgrades: entry.upgrades } : {})
    };
}

function formatTowerAttack(attack: TowerAttackStats) {
    return {
        name: attack.name,
        range: attack.range,
        attackThroughWalls: attack.attackThroughWalls,
        fireWithoutTarget: attack.fireWithoutTarget,
        targetProvider: attack.targetProvider,
        weapons: attack.weapons.map(formatTowerWeapon)
    };
}

function formatTowerWeapon(weapon: TowerWeaponStats) {
    return {
        name: weapon.name,
        damageType: weapon.damageType,
        cooldownSeconds: weapon.cooldownSeconds,
        projectileId: weapon.projectileId,
        pierce: weapon.pierce,
        maxPierce: weapon.maxPierce,
        damage: weapon.damage,
        maxDamage: weapon.maxDamage,
        camo: weapon.camo,
        canDamageDdt: weapon.canDamageDdt,
        ...(weapon.blockedBy !== undefined ? { blockedBy: weapon.blockedBy } : {}),
        ...(weapon.damageModifiers !== undefined ? { damageModifiers: weapon.damageModifiers } : {}),
        ...(weapon.damageCoverage !== undefined ? { damageCoverage: weapon.damageCoverage } : {}),
        ...(weapon.appliedDebuffs !== undefined ? { appliedDebuffs: weapon.appliedDebuffs } : {})
    };
}

function formatEffectiveTowerStats(stats: EffectiveTowerStats) {
    if (!stats.available) return stats;

    return {
        available: true,
        ...(stats.range !== undefined ? { range: stats.range } : {}),
        ...(stats.baseRange !== undefined ? { baseRange: stats.baseRange } : {}),
        ...(stats.totalRateModifier !== undefined ? { totalRateModifier: stats.totalRateModifier } : {}),
        ...(stats.poppingCapabilities !== undefined ? { poppingCapabilities: stats.poppingCapabilities } : {}),
        ...(stats.attacks !== undefined ? { attacks: stats.attacks.map(formatEffectiveAttack) } : {}),
        ...(stats.paragonBossDamage !== undefined ? { paragonBossDamage: stats.paragonBossDamage } : {}),
        ...(stats.activeBuffs !== undefined ? { activeBuffs: stats.activeBuffs } : {}),
        ...(stats.appliedDebuffs !== undefined ? { appliedDebuffs: stats.appliedDebuffs } : {}),
        ...(stats.cashEarned !== undefined ? { cashEarned: stats.cashEarned } : {}),
        ...(stats.bank !== undefined ? { bank: stats.bank } : {}),
        ...(stats.income !== undefined ? { income: stats.income } : {}),
        ...(stats.passiveCashPerRound !== undefined ? { passiveCashPerRound: stats.passiveCashPerRound } : {})
    };
}

function formatEffectiveAttack(attack: EffectiveAttackStats) {
    return {
        range: attack.range,
        onlyTargetsMoab: attack.onlyTargetsMoab,
        cannotTargetMoab: attack.cannotTargetMoab,
        cannotTargetCamo: attack.cannotTargetCamo,
        weapons: attack.weapons.map(formatEffectiveWeapon)
    };
}

function formatEffectiveWeapon(weapon: EffectiveWeaponStats) {
    return {
        name: weapon.name,
        damageType: weapon.damageType,
        effectiveCooldownSeconds: weapon.effectiveCooldownSeconds,
        effectiveDamage: weapon.effectiveDamage,
        effectivePierce: weapon.effectivePierce,
        camo: weapon.camo,
        canDamageDdt: weapon.canDamageDdt,
        blockedBy: weapon.blockedBy,
        damageModifiers: weapon.damageModifiers,
        ...(weapon.damageCoverage !== undefined ? { damageCoverage: weapon.damageCoverage } : {}),
        ...(weapon.appliedDebuffs !== undefined ? { appliedDebuffs: weapon.appliedDebuffs } : {})
    };
}

export function formatPlacementSpotsOutput(result: FindPlacementSpotsResult, profile: OutputProfile) {
    if (profile === 'full') return result;
    return {
        towerType: result.towerType,
        totalFound: result.totalFound,
        returned: result.returned,
        targetSupportTower: result.targetSupportTower,
        coverageTargets: result.coverageTargets,
        coverageBasis: result.coverageBasis,
        eligibilityVerified: result.eligibilityVerified,
        spots: result.spots.map(spot => ({
            x: spot.x,
            y: spot.y,
            distanceToTrack: spot.distanceToTrack,
            distanceToSupport: spot.distanceToSupport,
            coveredTowerIds: spot.coveredTowerIds,
            uncoveredTowerIds: spot.uncoveredTowerIds,
            recipientCount: spot.recipientCount,
            supportRange: spot.supportRange,
            ...(spot.trackCoverage ? {
                trackCoverage: {
                    rangeRadius: spot.trackCoverage.rangeRadius,
                    uniqueTrackLength: spot.trackCoverage.uniqueTrackLength,
                    angularCoverageDegrees: spot.trackCoverage.angularCoverageDegrees,
                    pathCount: spot.trackCoverage.pathCount,
                    intervalCount: spot.trackCoverage.intervalCount,
                    detailsOmitted: true
                }
            } : {})
        })),
        geometryRevision: result.geometryRevision,
        search: result.search ? {
            rankingStrategy: result.search.rankingStrategy,
            coarseStep: result.search.coarseStep,
            refinementStep: result.search.refinementStep,
            nativeChecks: result.search.nativeChecks,
            refinementChecks: result.search.refinementChecks,
            maxNativeChecks: result.search.maxNativeChecks,
            relevantPathIndices: result.search.relevantPathIndices,
            coverageBasis: result.search.coverageBasis,
            notes: result.search.notes
        } : undefined,
        note: result.note
    };
}
