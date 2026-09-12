import assert from 'node:assert/strict';
import { test } from 'node:test';
import { trackEdgeSchema, trackGraphSchema, bossSummarySchema } from '../src/bridge-data.js';

const edge = { id: 'e0', from: 'entry', to: 'exit', length: 1, routes: [0] };
const graph = {
    nodes: [], edges: [], routes: [],
    routingPolicy: { type: 'parallel', description: '', activeRoutes: [], selectionOrder: [] },
    traffic: { regularRouteShare: {}, bossRouteShare: {}, edgeTraffic: {} }
};

test('an edge can retain a complete producer-sized path, but not exceed it', () => {
    const waypoints = Array.from({ length: 32768 }, (_, x) => ({ x, y: 0 }));
    assert.equal(trackEdgeSchema.parse({ ...edge, waypoints }).waypoints?.length, 32768);
    assert.equal(trackEdgeSchema.safeParse({ ...edge, waypoints: [...waypoints, { x: 32768, y: 0 }] }).success, false);
});

test('all 512 producer paths can be represented as routes', () => {
    const routes = Array.from({ length: 512 }, (_, routeId) => ({
        routeId, label: String(routeId), edgeSequence: [], totalLength: 1, entryNode: 'entry', exitNode: 'exit'
    }));
    assert.equal(trackGraphSchema.parse({ ...graph, routes }).routes.length, 512);
    assert.equal(trackGraphSchema.safeParse({ ...graph, routes: [...routes, { ...routes[0], routeId: 512 }] }).success, false);
});

test('split endpoints have a bounded aggregate waypoint budget', () => {
    const waypoints = Array.from({ length: 16384 }, (_, x) => ({ x, y: 0 }));
    const edges = Array.from({ length: 4 }, (_, i) => ({ ...edge, id: `e${i}`, waypoints }));
    assert.equal(trackGraphSchema.parse({ ...graph, edges }).edges.length, 4);
    const extra = { ...edge, id: 'extra', waypoints: [{ x: 0, y: 0 }, { x: 1, y: 0 }] };
    assert.equal(trackGraphSchema.safeParse({ ...graph, edges: [...edges, extra] }).success, false);
});

test('unknown boss defenses remain unknown, not safe defaults', () => {
    const unknown = {
        id: '42', bloonId: 'FutureBoss1', baseId: 'FutureBoss', bossType: null,
        isBoss: true, isBossSegment: false, isElite: null, tier: null,
        health: 100, armour: null, modelBloonProperties: null, runtimeTowerSetImmunity: null,
        invulnerable: null, untargetable: null, currentSkull: null, damageUntilNextSkull: null, progress: 0.1,
        coverage: { status: 'partial', unknownBehaviorTypes: ['NewBossDefenseModel'], notes: [] }
    };
    const parsed = bossSummarySchema.parse(unknown);
    assert.equal(parsed.runtimeTowerSetImmunity, null);
    assert.equal(parsed.invulnerable, null);
    assert.deepEqual(parsed.coverage.unknownBehaviorTypes, ['NewBossDefenseModel']);
    assert.equal(bossSummarySchema.safeParse({ ...unknown, health: Infinity }).success, false);
});
