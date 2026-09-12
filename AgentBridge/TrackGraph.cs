using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppAssets.Scripts.Models.Map;
using Il2CppAssets.Scripts.Models.Map.Spawners;
using SimMap = Il2CppAssets.Scripts.Simulation.Track.Map;
using SimPath = Il2CppAssets.Scripts.Simulation.Track.Path;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private sealed record TrackNode(
        string Id,
        string Kind,
        MapPoint Position,
        string Compass
    );

    private sealed record TrackEdge(
        string Id,
        string From,
        string To,
        float Length,
        int[] Routes,
        MapPoint[] Waypoints
    );

    private sealed record TrackRoute(
        int RouteId,
        string Label,
        string[] EdgeSequence,
        float TotalLength,
        string EntryNode,
        string ExitNode
    );

    private sealed record RoutingPolicy(
        string Type,
        string Description,
        int[] ActiveRoutes,
        int[] SelectionOrder,
        int? BossRoute,
        int? RouteForRound
    );

    private sealed record EdgeTraffic(
        float RegularShare,
        float BossShare
    );

    private sealed record TrackTraffic(
        Dictionary<string, float> RegularRouteShare,
        Dictionary<string, float> BossRouteShare,
        Dictionary<string, EdgeTraffic> EdgeTraffic
    );

    private sealed record TrackGraph(
        TrackNode[] Nodes,
        TrackEdge[] Edges,
        TrackRoute[] Routes,
        RoutingPolicy RoutingPolicy,
        TrackTraffic Traffic
    );


    private static float PolylineLength(IReadOnlyList<MapPoint> pts, int startIdx = 0, int endIdx = -1)
    {
        if (pts.Count < 2) return 0f;
        int end = endIdx < 0 ? pts.Count - 1 : Math.Min(endIdx, pts.Count - 1);
        float len = 0f;
        for (int i = startIdx; i < end; i++)
        {
            len += PointDist(pts[i], pts[i + 1]);
        }
        return MathF.Round(len, 1);
    }

    private static string GetCompass(MapPoint pt, MapBounds bounds)
    {
        float cx = (bounds.MinX + bounds.MaxX) * 0.5f;
        float cy = (bounds.MinY + bounds.MaxY) * 0.5f;
        float w = bounds.MaxX - bounds.MinX;
        float h = bounds.MaxY - bounds.MinY;
        float dx = pt.X - cx;
        float dy = cy - pt.Y; // Simulation Y grows downward; compass north is upward.

        if (Math.Abs(dx) < w * 0.12f && Math.Abs(dy) < h * 0.12f)
            return "Center";

        double angle = Math.Atan2(dy, dx) * (180.0 / Math.PI);
        if (angle < 0) angle += 360.0;

        if (angle >= 337.5 || angle < 22.5) return "East";
        if (angle < 67.5) return "North-East";
        if (angle < 112.5) return "North";
        if (angle < 157.5) return "North-West";
        if (angle < 202.5) return "West";
        if (angle < 247.5) return "South-West";
        if (angle < 292.5) return "South";
        return "South-East";
    }

    private static string CompassSuffix(string compass) => compass switch
    {
        "North" => "n",
        "North-East" => "ne",
        "East" => "e",
        "South-East" => "se",
        "South" => "s",
        "South-West" => "sw",
        "West" => "w",
        "North-West" => "nw",
        _ => "c"
    };

    private sealed class CanonicalRoute
    {
        public int RouteId { get; init; }
        public int OriginalPathIndex { get; init; }
        public MapPoint[] Points { get; init; } = Array.Empty<MapPoint>();
        public bool IsBossRoute { get; set; }
    }

    private sealed record SplitMarker(int RouteIndex, int PointIndex, MapPoint Position, string Kind);

    private sealed class CachedTopology
    {
        public string MapId { get; init; } = "";
        public string Revision { get; init; } = "";
        public int PathCount { get; init; }
        public TrackNode[] Nodes { get; init; } = Array.Empty<TrackNode>();
        public TrackEdge[] Edges { get; init; } = Array.Empty<TrackEdge>();
        public TrackRoute[] Routes { get; init; } = Array.Empty<TrackRoute>();
        public List<CanonicalRoute> CanonicalRoutes { get; init; } = new();
        public Dictionary<int, int> PathToRoute { get; init; } = new();
    }

    private static CachedTopology? _cachedTopology;


    private static CachedTopology BuildTopology(SimMap map, MapGeometry geometry, string mapId)
    {
        var rawPaths = map.pathManager?.paths;
        int rawCount = rawPaths?.Count ?? 0;
        var validPaths = new List<(int Index, SimPath Path, MapPoint[] Points)>();

        for (int i = 0; i < rawCount; i++)
        {
            var p = rawPaths![i];
            if (p == null || !p.isActive || p.isHidden) continue;
            var pts = p.def?.points;
            if (pts == null || pts.Length < 2) continue;
            var dtoArray = new MapPoint[pts.Length];
            for (int j = 0; j < pts.Length; j++)
            {
                var vec = pts[j].point;
                dtoArray[j] = new MapPoint(vec.x, vec.y);
            }
            validPaths.Add((i, p, dtoArray));
        }

        if (validPaths.Count == 0)
        {
            return new CachedTopology
            {
                MapId = mapId,
                Revision = geometry.Revision,
                PathCount = rawCount
            };
        }

        // 1. Identify boss path
        string? bossPathName = null;
        var spawner = map.spawner;
        var junction = spawner?.spawnJunction;
        if (junction?.def is SplitterModel sm && !string.IsNullOrEmpty(sm.overriddenBossPath))
        {
            bossPathName = sm.overriddenBossPath;
        }

        // 2. Canonicalize routes (deduplicate identical paths)
        var canonical = new List<CanonicalRoute>();
        var pathToRoute = new Dictionary<int, int>();

        foreach (var (origIndex, simPath, pts) in validPaths)
        {
            int dupIndex = -1;
            for (int r = 0; r < canonical.Count; r++)
            {
                var cr = canonical[r];
                if (IsGeometryEquivalent(pts, cr.Points, 0.5f))
                {
                    dupIndex = r;
                    break;
                }
            }

            bool isBoss = !string.IsNullOrEmpty(bossPathName) &&
                string.Equals(simPath.def?.name, bossPathName, StringComparison.OrdinalIgnoreCase);

            if (dupIndex >= 0)
            {
                pathToRoute[origIndex] = dupIndex;
                if (isBoss) canonical[dupIndex].IsBossRoute = true;
            }
            else
            {
                int newId = canonical.Count;
                pathToRoute[origIndex] = newId;
                canonical.Add(new CanonicalRoute
                {
                    RouteId = newId,
                    OriginalPathIndex = origIndex,
                    Points = pts,
                    IsBossRoute = isBoss
                });
            }
        }

        // 3. Detect split and merge points between canonical route pairs
        var routeSplits = new List<SplitMarker>[canonical.Count];
        for (int i = 0; i < canonical.Count; i++) routeSplits[i] = new List<SplitMarker>();

        for (int a = 0; a < canonical.Count; a++)
        {
            for (int b = a + 1; b < canonical.Count; b++)
            {
                var ptsA = canonical[a].Points;
                var ptsB = canonical[b].Points;
                bool inShared = false;
                int startA = -1, startB = -1;

                for (int i = 0; i < ptsA.Length; i++)
                {
                    float bestDist = float.MaxValue;
                    int bestJ = -1;
                    for (int j = 0; j < ptsB.Length; j++)
                    {
                        float d = PointDist(ptsA[i], ptsB[j]);
                        if (d < bestDist) { bestDist = d; bestJ = j; }
                    }

                    bool match = bestDist < 2.5f;
                    if (match && !inShared)
                    {
                        inShared = true;
                        startA = i;
                        startB = bestJ;
                    }
                    else if (!match && inShared)
                    {
                        inShared = false;
                        int endA = i - 1;
                        int endB = bestJ;
                        float segLen = PolylineLength(ptsA, startA, endA);
                        if (segLen > 10f)
                        {
                            if (startA > 0 || startB > 0)
                            {
                                routeSplits[a].Add(new SplitMarker(a, startA, ptsA[startA], "merge"));
                                routeSplits[b].Add(new SplitMarker(b, startB, ptsB[startB], "merge"));
                            }
                            if (endA < ptsA.Length - 1 || endB < ptsB.Length - 1)
                            {
                                routeSplits[a].Add(new SplitMarker(a, endA, ptsA[endA], "junction"));
                                routeSplits[b].Add(new SplitMarker(b, endB, ptsB[endB], "junction"));
                            }
                        }
                    }
                }

                if (inShared)
                {
                    int endA = ptsA.Length - 1;
                    float segLen = PolylineLength(ptsA, startA, endA);
                    if (segLen > 10f && (startA > 0 || startB > 0))
                    {
                        routeSplits[a].Add(new SplitMarker(a, startA, ptsA[startA], "merge"));
                        routeSplits[b].Add(new SplitMarker(b, startB, ptsB[startB], "merge"));
                    }
                }
            }
        }

        // 4. Cluster and assign unique Node IDs
        var nodes = new List<TrackNode>();

        TrackNode GetOrCreateNode(MapPoint pt, string kind)
        {
            foreach (var n in nodes)
            {
                if (PointDist(n.Position, pt) < 6.0f)
                {
                    return n;
                }
            }

            string compass = GetCompass(pt, geometry.Bounds);
            string suffix = CompassSuffix(compass);
            string id = $"{kind}_{suffix}";
            int counter = 2;
            while (nodes.Any(n => n.Id == id))
            {
                id = $"{kind}_{suffix}_{counter++}";
            }

            var newNode = new TrackNode(id, kind, new MapPoint(MathF.Round(pt.X, 1), MathF.Round(pt.Y, 1)), compass);
            nodes.Add(newNode);
            return newNode;
        }

        // 5. Sequence nodes along each route
        var routeNodeSequences = new List<List<(int Index, TrackNode Node)>>();
        for (int r = 0; r < canonical.Count; r++)
        {
            var pts = canonical[r].Points;
            var entryNode = GetOrCreateNode(pts[0], "entry");
            var exitNode = GetOrCreateNode(pts[^1], "exit");

            var splits = routeSplits[r].OrderBy(s => s.PointIndex).ToList();
            var waystops = new List<(int Index, TrackNode Node)> { (0, entryNode) };

            foreach (var sp in splits)
            {
                if (sp.PointIndex > 5 && sp.PointIndex < pts.Length - 5)
                {
                    var n = GetOrCreateNode(sp.Position, sp.Kind);
                    if (waystops[^1].Node.Id != n.Id)
                    {
                        waystops.Add((sp.PointIndex, n));
                    }
                }
            }

            waystops.Add((pts.Length - 1, exitNode));
            routeNodeSequences.Add(waystops);
        }

        // 6. Build unique Edges and Route edge sequences
        var edges = new List<TrackEdge>();
        var routes = new List<TrackRoute>();

        for (int r = 0; r < canonical.Count; r++)
        {
            var stops = routeNodeSequences[r];
            var pts = canonical[r].Points;
            var edgeSeq = new List<string>();

            for (int s = 0; s < stops.Count - 1; s++)
            {
                var fromNode = stops[s].Node;
                var toNode = stops[s + 1].Node;
                int idxFrom = stops[s].Index;
                int idxTo = stops[s + 1].Index;

                int segCount = idxTo - idxFrom + 1;
                var segPts = new MapPoint[segCount];
                Array.Copy(pts, idxFrom, segPts, 0, segCount);
                float segLen = PolylineLength(segPts);

                var existingEdge = edges.FirstOrDefault(e => e.From == fromNode.Id && e.To == toNode.Id && IsGeometryEquivalent(e.Waypoints, segPts));
                if (existingEdge != null)
                {
                    if (!existingEdge.Routes.Contains(r))
                    {
                        var updatedRoutes = existingEdge.Routes.Append(r).OrderBy(x => x).ToArray();
                        int edgeIndex = edges.IndexOf(existingEdge);
                        edges[edgeIndex] = existingEdge with { Routes = updatedRoutes };
                    }
                    edgeSeq.Add(existingEdge.Id);
                }
                else
                {
                    string edgeId = $"e{edges.Count}";
                    var newEdge = new TrackEdge(
                        edgeId,
                        fromNode.Id,
                        toNode.Id,
                        segLen,
                        new[] { r },
                        segPts
                    );
                    edges.Add(newEdge);
                    edgeSeq.Add(edgeId);
                }
            }

            float totalLen = PolylineLength(pts);
            string label = canonical[r].IsBossRoute ? "Center / Boss" : (r == 1 && canonical.Count > 2 ? "Left" : $"Route {r + 1}");
            routes.Add(new TrackRoute(
                r,
                label,
                edgeSeq.ToArray(),
                totalLen,
                stops[0].Node.Id,
                stops[^1].Node.Id
            ));
        }

        return new CachedTopology
        {
            MapId = mapId,
            Revision = geometry.Revision,
            PathCount = rawCount,
            Nodes = nodes.ToArray(),
            Edges = edges.ToArray(),
            Routes = routes.ToArray(),
            CanonicalRoutes = canonical,
            PathToRoute = pathToRoute
        };
    }

    private static TrackGraph BuildTrackGraph(SimMap map, MapGeometry geometry, int targetRound)
    {
        string mapId = Il2CppAssets.Scripts.Unity.UI_New.InGame.InGameData.CurrentGame?.selectedMap ?? map.mapModel?.mapName ?? "unknown";
        int rawCount = map.pathManager?.paths?.Count ?? 0;

        if (_cachedTopology == null ||
            _cachedTopology.MapId != mapId ||
            _cachedTopology.Revision != geometry.Revision ||
            _cachedTopology.PathCount != rawCount)
        {
            _cachedTopology = BuildTopology(map, geometry, mapId);
        }

        var topology = _cachedTopology;
        if (topology.CanonicalRoutes.Count == 0)
        {
            var emptyPolicy = new RoutingPolicy("no_paths", "No active track paths available", Array.Empty<int>(), Array.Empty<int>(), null, null);
            var emptyTraffic = new TrackTraffic(new Dictionary<string, float>(), new Dictionary<string, float>(), new Dictionary<string, EdgeTraffic>());
            return new TrackGraph(Array.Empty<TrackNode>(), Array.Empty<TrackEdge>(), Array.Empty<TrackRoute>(), emptyPolicy, emptyTraffic);
        }

        // 7. Routing Policy
        string policyType = "single_route";
        string policyDesc = "Single active route.";
        int[] activeRoutes = topology.CanonicalRoutes.Select(c => c.RouteId).ToArray();
        int[] selectionOrder = topology.CanonicalRoutes.Select(c => c.RouteId).ToArray();
        int? bossRoute = topology.CanonicalRoutes.FirstOrDefault(c => c.IsBossRoute)?.RouteId;
        int? routeForRound = null;

        var spawner = map.spawner;
        var junction = spawner?.spawnJunction;
        string splitterName = junction?.def?.GetIl2CppType()?.Name ?? junction?.GetIl2CppType()?.Name ?? "";
        int uniqueRounds = spawner?.GetNumberOfUniqueRounds() ?? 1;

        if (topology.CanonicalRoutes.Count <= 1)
        {
            policyType = "single_route";
            policyDesc = "Single track path.";
            routeForRound = 0;
        }
        else if (splitterName.Contains("BloonTag", StringComparison.OrdinalIgnoreCase) ||
                 splitterName.Contains("AlternateBloon", StringComparison.OrdinalIgnoreCase))
        {
            policyType = "per_bloon_round_robin";
            policyDesc = "Regular bloons alternate round-robin across active routes on every spawn. Boss bloons take the dedicated boss route.";
        }
        else if (uniqueRounds > 1 || splitterName.Contains("AlternateRound", StringComparison.OrdinalIgnoreCase))
        {
            policyType = "per_round_alternation";
            policyDesc = "Bloons follow a specific route each round, alternating according to the round schedule.";
            var spawnPaths = spawner?.GetSpawnPathsForRound(targetRound);
            var rawPaths = map.pathManager?.paths;
            if (spawnPaths != null && spawnPaths.Length > 0 && rawPaths != null)
            {
                var activeIndices = new List<int>();
                foreach (var sp in spawnPaths)
                {
                    for (int i = 0; i < rawPaths.Count; i++)
                    {
                        if (rawPaths[i]?.Pointer == sp?.Pointer && topology.PathToRoute.TryGetValue(i, out int mappedRoute))
                        {
                            if (!activeIndices.Contains(mappedRoute)) activeIndices.Add(mappedRoute);
                        }
                    }
                }
                if (activeIndices.Count > 0)
                {
                    activeRoutes = activeIndices.ToArray();
                    routeForRound = activeIndices[0];
                }
            }
        }
        else
        {
            policyType = "simultaneous_all";
            policyDesc = "All routes are active simultaneously.";
        }

        var routingPolicy = new RoutingPolicy(
            policyType,
            policyDesc,
            activeRoutes,
            selectionOrder,
            bossRoute,
            routeForRound
        );

        // 8. Traffic Shares
        var regularRouteShare = new Dictionary<string, float>();
        var bossRouteShare = new Dictionary<string, float>();

        foreach (var r in topology.CanonicalRoutes)
        {
            string key = r.RouteId.ToString();
            if (policyType == "per_round_alternation")
            {
                regularRouteShare[key] = (routeForRound == r.RouteId) ? 1.0f : 0.0f;
            }
            else
            {
                regularRouteShare[key] = activeRoutes.Contains(r.RouteId) ? MathF.Round(1.0f / activeRoutes.Length, 3) : 0.0f;
            }

            bossRouteShare[key] = (bossRoute.HasValue && bossRoute.Value == r.RouteId) ? 1.0f : 0.0f;
        }

        var edgeTraffic = new Dictionary<string, EdgeTraffic>();
        foreach (var edge in topology.Edges)
        {
            float reg = 0f;
            float boss = 0f;
            foreach (int r in edge.Routes)
            {
                string rKey = r.ToString();
                if (regularRouteShare.TryGetValue(rKey, out float sReg)) reg += sReg;
                if (bossRouteShare.TryGetValue(rKey, out float sBoss)) boss += sBoss;
            }
            edgeTraffic[edge.Id] = new EdgeTraffic(MathF.Round(reg, 3), MathF.Round(boss, 3));
        }

        var traffic = new TrackTraffic(regularRouteShare, bossRouteShare, edgeTraffic);
        return new TrackGraph(topology.Nodes, topology.Edges, topology.Routes, routingPolicy, traffic);
    }
}
