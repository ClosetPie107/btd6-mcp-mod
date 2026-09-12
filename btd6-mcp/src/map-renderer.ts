import sharp from 'sharp';
import type { MapLayout } from './bridge-data.js';

const WIDTH = 960;
const HEIGHT = 800;
const MARGIN = 58;
const PLOT_HEIGHT = 620;
const colors: Record<string, string> = {
    land: '#eee9d5', water: '#277da8', shallowWater: '#69c7df', waterMermonkey: '#397e91',
    track: '#826044', unplaceable: '#474c55', removable: '#807384', ice: '#c0ecf5'
};
const terrainOrder: Record<string, number> = { land: 0, water: 1, shallowWater: 1, waterMermonkey: 1, ice: 1, track: 2, unplaceable: 3, removable: 3 };
let backgroundCache: { key: string; png: Promise<Buffer> } | undefined;

function escapeXml(value: string): string {
    return value.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c]!);
}

export async function renderMap(layout: MapLayout, options: { placementCandidates?: Array<{ x: number; y: number }> } = {}): Promise<{ png: Buffer; summary: object }> {
    const b = layout.bounds;
    const scale = Math.min((WIDTH - MARGIN * 2) / (b.maxX - b.minX), PLOT_HEIGHT / (b.maxY - b.minY));
    const plotWidth = (b.maxX - b.minX) * scale;
    const plotHeight = (b.maxY - b.minY) * scale;
    const left = (WIDTH - plotWidth) / 2;
    const top = MARGIN + (PLOT_HEIGHT - plotHeight) / 2;
    const px = (x: number) => left + (x - b.minX) * scale;
    // Keep simulation coordinates unchanged while matching the game's top-left, Y-down map image.
    const py = (y: number) => top + (y - b.minY) * scale;
    const edgeColors = ['#00e5ff', '#ffc400', '#ff2a85', '#00e676', '#ff9100', '#d500f9', '#76ff03', '#ff1744'];
    const arrowDefs = edgeColors.map((c, i) =>
        `<marker id="arrow-${i}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="${c}"/></marker>`
    ).join('');
    const svgStart = `<svg xmlns="http://www.w3.org/2000/svg" width="${WIDTH}" height="${HEIGHT}" viewBox="0 0 ${WIDTH} ${HEIGHT}"><defs><clipPath id="map"><rect x="${left}" y="${top}" width="${plotWidth}" height="${plotHeight}"/></clipPath><pattern id="los-blocked-hatch" patternUnits="userSpaceOnUse" width="8" height="8"><rect width="8" height="8" fill="#3b424b"/><path d="M-2,2L2,-2M0,8L8,0M6,10L10,6" stroke="#d49b29" stroke-width="2"/></pattern><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#fff"/></marker>${arrowDefs}</defs>`;
    const key = `${layout.mapId}:${layout.revision}`;
    if (backgroundCache?.key !== key) {
        const shapes = [svgStart, '<rect width="100%" height="100%" fill="#161a20"/>',
            `<text x="${MARGIN}" y="31" fill="white" font-family="sans-serif" font-size="22" font-weight="bold">${escapeXml(layout.mapId)} — tactical map</text>`,
            `<g clip-path="url(#map)"><rect x="${left}" y="${top}" width="${plotWidth}" height="${plotHeight}" fill="#292d35"/>`];
        // Base land can follow water/track in the game list. Draw it first at each elevation.
        for (const area of [...layout.areas].sort((a, b) => a.height - b.height || (terrainOrder[a.type] ?? 3) - (terrainOrder[b.type] ?? 3))) {
            const rings = [area.points, ...area.holes].filter(r => r.length >= 3);
            const d = rings.map(r => r.map((p, i) => `${i ? 'L' : 'M'}${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' ') + ' Z').join(' ');
            shapes.push(`<path d="${d}" fill="${colors[area.type] ?? '#a28fa8'}" fill-rule="evenodd" stroke="${area.blocksLineOfSight ? '#ee4559' : '#20242b'}" stroke-width="${area.blocksLineOfSight ? 2 : 0.6}"/>`);
        }
        for (const blocker of layout.blockers) {
            shapes.push(`<circle cx="${px(blocker.x)}" cy="${py(blocker.y)}" r="${blocker.radius * scale}" fill="#ee4559" fill-opacity="0.12" stroke="#d72946" stroke-width="0.7"/>`);
        }
        for (let x = Math.ceil(b.minX / 50) * 50; x <= b.maxX; x += 50) {
            shapes.push(`<path d="M${px(x)} ${top}v${plotHeight}" stroke="#101820" stroke-opacity="0.3" stroke-dasharray="3 5"/>`);
        }
        for (let y = Math.ceil(b.minY / 50) * 50; y <= b.maxY; y += 50) {
            shapes.push(`<path d="M${left} ${py(y)}h${plotWidth}" stroke="#101820" stroke-opacity="0.3" stroke-dasharray="3 5"/>`);
        }
        for (const path of layout.paths) {
            if (path.points.length < 2) continue;
            const d = path.points.map((p, i) => `${i ? 'L' : 'M'}${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' ');
            shapes.push(`<path d="${d}" fill="none" stroke="#17212b" stroke-width="4" opacity="${path.active && !path.hidden ? 1 : 0.3}"/>`);
            shapes.push(`<path d="${d}" fill="none" stroke="#fff" stroke-width="1.5" stroke-dasharray="${path.active && !path.hidden ? 'none' : '5 4'}" opacity="${path.active && !path.hidden ? 1 : 0.3}"/>`);
        }
        shapes.push('</g>');
        for (let x = Math.ceil(b.minX / 50) * 50; x <= b.maxX; x += 50) {
            shapes.push(`<text x="${px(x)}" y="${top + plotHeight + 20}" fill="#ddd" text-anchor="middle" font-family="sans-serif" font-size="13">${x}</text>`);
        }
        for (let y = Math.ceil(b.minY / 50) * 50; y <= b.maxY; y += 50) {
            shapes.push(`<text x="${left - 10}" y="${py(y) + 4}" fill="#ddd" text-anchor="end" font-family="sans-serif" font-size="13">${y}</text>`);
        }
        const legend = [['Land', colors.land], ['Water', colors.water], ['Track', colors.track], ['Blocked', colors.unplaceable], ['Removable', colors.removable], ['LOS blocker', '#ee4559']];
        legend.forEach(([label, color], i) => shapes.push(`<rect x="${MARGIN + i * 140}" y="720" width="14" height="14" fill="${color}"/><text x="${MARGIN + i * 140 + 21}" y="732" fill="white" font-family="sans-serif" font-size="13">${label}</text>`));
        shapes.push('<rect x="58" y="744" width="14" height="14" fill="#ffe26a" fill-opacity="0.7"/><text x="79" y="756" fill="white" font-family="sans-serif" font-size="13">Native visible LOS</text>');
        shapes.push('<rect x="218" y="744" width="14" height="14" fill="url(#los-blocked-hatch)"/><text x="239" y="756" fill="white" font-family="sans-serif" font-size="13">Native blocked LOS</text>');
        shapes.push('<line x1="408" y1="751" x2="430" y2="751" stroke="#e9bd00" stroke-width="2" stroke-dasharray="7 4"/><text x="437" y="756" fill="white" font-family="sans-serif" font-size="13">Native maximum range</text>');
        shapes.push('<text x="58" y="774" fill="#ddd" font-family="sans-serif" font-size="13">X → right · Y ↓ down · T = tower · H = hero · P = candidate · Nodes: ● IN ■ OUT ◆ J/M</text>');
        shapes.push('<text x="58" y="792" fill="#bbb" font-family="sans-serif" font-size="12">Native selected-tower range/LOS only; not damage, projectile, attack-primary, or subtower coverage.</text></svg>');
        backgroundCache = { key, png: sharp(Buffer.from(shapes.join(''))).png().toBuffer() };
    }
    const background = backgroundCache.png;
    const overlay = [svgStart];
    const rangeExceptionCaption = layout.rangeOverlays
        .filter(range => range.status !== 'native')
        .map(range => `${range.towerId}: ${range.status}${range.reason ? ` — ${range.reason}` : ''}`)
        .join(' · ');
    if (rangeExceptionCaption) {
        const caption = rangeExceptionCaption.length > 180 ? `${rangeExceptionCaption.slice(0, 177)}...` : rangeExceptionCaption;
        overlay.push(`<text x="${MARGIN}" y="50" fill="#ffd166" font-family="sans-serif" font-size="11">Range exception: ${escapeXml(caption)}</text>`);
    }
    overlay.push('<g clip-path="url(#map)">');
    const towers = layout.placedTowers.map((tower, i) => ({ marker: `${tower.isHero ? 'H' : 'T'}${i + 1}`, ...tower }));
    // Range geometry is native world-space triangulation. Never infer LOS from a
    // circle or decode UVs here; global/unsupported entries intentionally have
    // no fabricated geometry.
    const visibleTriangles = layout.rangeOverlays.flatMap(range => range.visibleTriangles);
    if (visibleTriangles.length > 0) {
        const d = visibleTriangles.map(triangle => `M${triangle.map(p => `${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' L')} Z`).join(' ');
        overlay.push(`<path d="${d}" fill="#ffe26a" fill-opacity="0.3"/>`);
    }
    const blockedTriangles = layout.rangeOverlays.flatMap(range => range.blockedTriangles);
    if (blockedTriangles.length > 0) {
        const d = blockedTriangles.map(triangle => `M${triangle.map(p => `${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' L')} Z`).join(' ');
        overlay.push(`<path d="${d}" fill="url(#los-blocked-hatch)" fill-opacity="0.82"/>`);
    }
    for (const range of layout.rangeOverlays) {
        const tower = towers.find(candidate => candidate.id === range.towerId);
        if (range.status === 'native' && tower) {
            overlay.push(`<circle cx="${px(tower.x)}" cy="${py(tower.y)}" r="${range.range * scale}" fill="none" stroke="#e9bd00" stroke-width="2" stroke-dasharray="7 4"/>`);
        }
    }
    for (const tower of towers) {
        overlay.push(`<circle cx="${px(tower.x)}" cy="${py(tower.y)}" r="12" fill="${tower.isHero ? '#ce59db' : '#f5df56'}" stroke="#101820" stroke-width="2"/><text x="${px(tower.x)}" y="${py(tower.y) + 4}" fill="#101820" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold">${tower.marker}</text>`);
    }
    const candidates = (options.placementCandidates ?? []).map((p, i) => ({ marker: `P${i + 1}`, ...p }));
    for (const candidate of candidates) {
        overlay.push(`<circle cx="${px(candidate.x)}" cy="${py(candidate.y)}" r="9" fill="#00e5ff" fill-opacity="0.7" stroke="#161a20" stroke-width="1.5"/><text x="${px(candidate.x)}" y="${py(candidate.y) + 3}" fill="#161a20" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="bold">${candidate.marker}</text>`);
    }

    if (layout.trackGraph?.edges && layout.trackGraph.edges.length > 0) {
        // Draw each topological edge with distinct vibrant color and flow arrows
        layout.trackGraph.edges.forEach((edge, idx) => {
            const color = edgeColors[idx % edgeColors.length]!;
            const pts = edge.waypoints && edge.waypoints.length >= 2 ? edge.waypoints : [];
            if (pts.length >= 2) {
                const d = pts.map((p, i) => `${i ? 'L' : 'M'}${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' ');
                overlay.push(`<path d="${d}" fill="none" stroke="${color}" stroke-width="3.5" opacity="0.9"/>`);

                let distance = 0;
                for (let i = 1; i < pts.length; i++) {
                    const a = pts[i - 1]!, c = pts[i]!;
                    const length = Math.hypot(c.x - a.x, c.y - a.y);
                    distance += length;
                    if (distance < 55 || length === 0) continue;
                    distance = 0;
                    const x = (a.x + c.x) / 2, y = (a.y + c.y) / 2;
                    overlay.push(`<path d="M${px(x - (c.x - a.x) / length * 3.5)} ${py(y - (c.y - a.y) / length * 3.5)}L${px(x + (c.x - a.x) / length * 3.5)} ${py(y + (c.y - a.y) / length * 3.5)}" stroke="${color}" stroke-width="2.5" marker-end="url(#arrow-${idx % edgeColors.length})"/>`);
                }

                // Midpoint edge ID badge
                const midIdx = Math.floor(pts.length / 2);
                const midPt = pts[midIdx]!;
                const bx = px(midPt.x), by = py(midPt.y);
                overlay.push(`<rect x="${bx - 14}" y="${by - 9}" width="28" height="18" rx="4" fill="#161a20" stroke="${color}" stroke-width="2"/>` +
                    `<text x="${bx}" y="${by + 4}" fill="${color}" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold">${edge.id}</text>`);
            }
        });

        // Draw graph nodes
        for (const node of layout.trackGraph.nodes) {
            const nx = px(node.position.x), ny = py(node.position.y);
            if (node.kind === 'entry') {
                overlay.push(`<circle cx="${nx}" cy="${ny}" r="11" fill="#00e676" stroke="#161a20" stroke-width="2.5"/>` +
                    `<text x="${nx}" y="${ny + 4}" fill="#161a20" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="bold">IN</text>` +
                    `<text x="${nx}" y="${ny - 14}" fill="white" stroke="#161a20" stroke-width="2" paint-order="stroke" text-anchor="middle" font-family="sans-serif" font-size="10" font-weight="bold">${node.id}</text>`);
            } else if (node.kind === 'exit') {
                overlay.push(`<rect x="${nx - 11}" y="${ny - 11}" width="22" height="22" rx="3" fill="#ff1744" stroke="#161a20" stroke-width="2.5"/>` +
                    `<text x="${nx}" y="${ny + 4}" fill="white" text-anchor="middle" font-family="sans-serif" font-size="8" font-weight="bold">OUT</text>` +
                    `<text x="${nx}" y="${ny - 14}" fill="white" stroke="#161a20" stroke-width="2" paint-order="stroke" text-anchor="middle" font-family="sans-serif" font-size="10" font-weight="bold">${node.id}</text>`);
            } else if (node.kind === 'junction') {
                overlay.push(`<polygon points="${nx},${ny - 12} ${nx + 12},${ny} ${nx},${ny + 12} ${nx - 12},${ny}" fill="#ffd700" stroke="#161a20" stroke-width="2.5"/>` +
                    `<text x="${nx}" y="${ny + 3}" fill="#161a20" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="bold">J</text>` +
                    `<text x="${nx}" y="${ny - 15}" fill="#ffd700" stroke="#161a20" stroke-width="2" paint-order="stroke" text-anchor="middle" font-family="sans-serif" font-size="10" font-weight="bold">${node.id}</text>`);
            } else if (node.kind === 'merge') {
                overlay.push(`<polygon points="${nx},${ny - 12} ${nx + 12},${ny} ${nx},${ny + 12} ${nx - 12},${ny}" fill="#d500f9" stroke="#161a20" stroke-width="2.5"/>` +
                    `<text x="${nx}" y="${ny + 3}" fill="white" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="bold">M</text>` +
                    `<text x="${nx}" y="${ny - 15}" fill="#d500f9" stroke="#161a20" stroke-width="2" paint-order="stroke" text-anchor="middle" font-family="sans-serif" font-size="10" font-weight="bold">${node.id}</text>`);
            }
        }
    } else {
        // Fallback: draw raw polylines
        for (const path of layout.paths) {
            if (path.points.length < 2) continue;
            const d = path.points.map((p, i) => `${i ? 'L' : 'M'}${px(p.x).toFixed(2)},${py(p.y).toFixed(2)}`).join(' ');
            overlay.push(`<path d="${d}" fill="none" stroke="#1ce4af" stroke-width="3.5" opacity="0.85"/>`);
        }
    }

    overlay.push('</g></svg>');
    const png = await sharp(await background).composite([{ input: Buffer.from(overlay.join('')) }]).png().toBuffer();
    return { png, summary: {
        mapId: layout.mapId, revision: layout.revision, observedAtUtc: layout.observedAtUtc,
        bounds: layout.bounds, coordinateSystem: layout.coordinateSystem,
        trackGraph: layout.trackGraph ?? null,
        paths: layout.paths.map(p => ({
            pathIndex: p.pathIndex,
            active: p.active,
            hidden: p.hidden,
            isActiveForRound: layout.spawner?.activePathsForRound ? layout.spawner.activePathsForRound.includes(p.pathIndex) : (p.active && !p.hidden),
            start: p.points[0] ?? null,
            end: p.points.at(-1) ?? null
        })),
        spawner: layout.spawner,
        towers, candidates, warnings: layout.warnings,
        rangeOverlays: layout.rangeOverlays.map(({ visibleTriangles, blockedTriangles, ...metadata }) => ({
            ...metadata,
            visibleTriangleCount: visibleTriangles.length,
            blockedTriangleCount: blockedTriangles.length
        })),
        rangeOverlayNote: 'Native local range/LOS and direct attack wall flags only; projectile and subtower coverage are not shown. Global and unsupported entries have no ring. Overlapping overlays do not describe combined coverage.',
        candidateNote: 'Candidate coordinates were supplied by the caller; this render does not validate placement.'
    } };
}

