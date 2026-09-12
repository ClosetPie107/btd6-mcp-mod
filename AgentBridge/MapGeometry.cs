using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2Cpp;
using Il2CppAssets.Scripts.Models.Map;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Simulation.Behaviors;
using Il2CppAssets.Scripts.Simulation.Display;
using Il2CppAssets.Scripts.Simulation.SMath;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using SimMap = Il2CppAssets.Scripts.Simulation.Track.Map;
using NativeMesh = Il2CppAssets.Scripts.Simulation.Display.Mesh;
using NativeRangeMesh = Il2CppAssets.Scripts.Simulation.Behaviors.RangeMesh;
using Math = System.Math;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private sealed record MapBounds(float MinX, float MaxX, float MinY, float MaxY);
    // Unconstrained placement keeps the legacy conservative envelope. Focused
    // near/aura queries use native bounds but remain capped to a finite scan.
    private const float PlacementMinX = -135f;
    private const float PlacementMaxX = 135f;
    private const float PlacementMinY = -95f;
    private const float PlacementMaxY = 95f;
    private const int MaxPlacementSamples = 4096;

    private sealed record MapPath(int PathIndex, bool Active, bool Hidden, MapPoint[] Points);
    private readonly record struct CoverageInterval(float StartProgress, float EndProgress, float Length);
    private readonly record struct CoverageLineKey(long DirectionX, long DirectionY, long Offset);
    private readonly record struct CoverageSpan(double Start, double End);
    private readonly record struct CoverageSegment(
        MapPoint A,
        MapPoint B,
        float StartDistance,
        float Length,
        float DirectionX,
        float DirectionY,
        CoverageLineKey LineKey);
    private sealed record CoveragePathData(int PathIndex, MapPoint[] Points, CoverageSegment[] Segments, float TotalLength);
    private sealed record CoveragePathSummary(int PathIndex, float InRangeLength, CoverageInterval[] Intervals);
    private readonly record struct TrackCoverageSummary(float UniqueTrackLength, float AngularCoverageDegrees, CoveragePathSummary[] Paths);
    private sealed class CoverageUnionScratch
    {
        private readonly List<List<CoverageSpan>> listPool = new();
        private int usedLists;
        public readonly Dictionary<CoverageLineKey, List<CoverageSpan>> Lines = new();
        public void Clear()
        {
            foreach (var spans in Lines.Values) spans.Clear();
            Lines.Clear();
            usedLists = 0;
        }
        public List<CoverageSpan> GetSpans(CoverageLineKey key)
        {
            if (Lines.TryGetValue(key, out var existing)) return existing;
            var spans = usedLists < listPool.Count ? listPool[usedLists] : new List<CoverageSpan>();
            usedLists++;
            Lines.Add(key, spans);
            return spans;
        }
    }
    private readonly record struct AngularArc(float Start, float End);
    private sealed record MapArea(string Id, string Type, bool BlocksPlacement, bool BlocksLineOfSight, float Height, MapPoint[] Points, MapPoint[][] Holes);
    private sealed record MapBlocker(float X, float Y, float Z, float Radius);
    private sealed record MapTower(string Id, string Type, bool IsHero, float X, float Y, float Range);
    private sealed record MapRangeAttack(string Name, float Range, bool AttackThroughWalls);
    private sealed record MapRangeOverlay(
        string TowerId,
        string Status,
        string? Reason,
        float Range,
        bool IgnoresBlockers,
        MapPoint[][] VisibleTriangles,
        MapPoint[][] BlockedTriangles,
        MapRangeAttack[] Attacks);
    private sealed record MapGeometry(string Revision, MapBounds Bounds, MapPath[] Paths, MapArea[] Areas, MapBlocker[] Blockers);
    private static MapGeometry? cachedGeometry;
    private static void ResetMapGeometryCache() => cachedGeometry = null;


    // Hash live values rather than just object identity: moving areas, sold obstacles and
    // changing paths can mutate their existing IL2CPP objects. The scan allocates no point DTOs.
    private sealed class GeometryReader
    {
        public ulong Hash = 14695981039346656037UL;
        public int PointCount;
        public readonly bool Capture;
        public GeometryReader(bool capture) { Capture = capture; }
        public void Add(int value) { unchecked { Hash = (Hash ^ (uint)value) * 1099511628211UL; } }
        public void Add(float value)
        {
            if (!float.IsFinite(value)) throw new InvalidOperationException("Non-finite map geometry.");
            Add(BitConverter.SingleToInt32Bits(value));
        }
        public void Add(string value) { Add(value.Length); foreach (char c in value) Add((int)c); }
        public MapPoint[] Polygon(Polygon? polygon)
        {
            var points = polygon?.points;
            int count = points?.Length ?? 0;
            Add(count);
            PointCount += count;
            if (PointCount > 32768) throw new InvalidOperationException("Map geometry exceeds 32768 points.");
            var result = Capture ? new MapPoint[count] : Array.Empty<MapPoint>();
            for (int i = 0; i < count; i++)
            {
                var p = points![i];
                Add(p.x); Add(p.y);
                if (Capture) result[i] = new MapPoint(p.x, p.y);
            }
            return result;
        }
    }

    private static MapGeometry ReadGeometry(SimMap map, bool capture)
    {
        var reader = new GeometryReader(capture);
        reader.Add(matchGeneration.GetHashCode());
        reader.Add(map.Pointer.GetHashCode());
        var lower = map.GetPointWithinMap(new Vector2(-100000f, -100000f));
        var upper = map.GetPointWithinMap(new Vector2(100000f, 100000f));
        var bounds = new MapBounds(lower.x, upper.x, lower.y, upper.y);
        reader.Add(lower.x); reader.Add(lower.y); reader.Add(upper.x); reader.Add(upper.y);
        var paths = map.pathManager?.paths;
        int pathCount = paths?.Count ?? 0;
        if (pathCount > 512) throw new InvalidOperationException("Map exceeds 512 paths.");
        reader.Add(pathCount);
        var pathDtos = capture ? new List<MapPath>(pathCount) : null;
        for (int i = 0; i < pathCount; i++)
        {
            var path = paths![i];
            var points = path?.def?.points;
            int count = points?.Length ?? 0;
            reader.Add(count);
            reader.Add(path?.isActive == true ? 1 : 0);
            reader.Add(path?.isHidden == true ? 1 : 0);
            reader.PointCount += count;
            if (reader.PointCount > 32768) throw new InvalidOperationException("Map geometry exceeds 32768 points.");
            var dto = capture ? new MapPoint[count] : Array.Empty<MapPoint>();
            for (int j = 0; j < count; j++)
            {
                var p = points![j].point;
                reader.Add(p.x); reader.Add(p.y);
                if (capture) dto[j] = new MapPoint(p.x, p.y);
            }
            pathDtos?.Add(new MapPath(i, path?.isActive == true, path?.isHidden == true, dto));
        }
        var areas = map.GetAreas();
        int areaCount = areas?.Count ?? 0;
        if (areaCount > 2048) throw new InvalidOperationException("Map exceeds 2048 areas.");
        reader.Add(areaCount);
        var areaDtos = capture ? new List<MapArea>(areaCount) : null;
        for (int i = 0; i < areaCount; i++)
        {
            var area = areas![i];
            var model = area?.areaModel;
            bool active = area?.isActive == true && model != null && !model.isDisabled;
            reader.Add(active ? 1 : 0);
            if (!active) continue;
            string id = model!.name ?? i.ToString();
            reader.Add(id); reader.Add((int)model.type); reader.Add(model.height);
            reader.Add(model.isBlocker ? 1 : 0); reader.Add(model.lockedArea ? 1 : 0);
            var points = reader.Polygon(model.polygon);
            var holes = model.holes;
            int holeCount = holes?.Length ?? 0;
            if (holeCount > 1024) throw new InvalidOperationException("Area exceeds 1024 holes.");
            reader.Add(holeCount);
            var holeDtos = capture ? new MapPoint[holeCount][] : Array.Empty<MapPoint[]>();
            for (int j = 0; j < holeCount; j++)
            {
                var hole = reader.Polygon(holes![j]);
                if (capture) holeDtos[j] = hole;
            }
            areaDtos?.Add(new MapArea(id, model.type.ToString(), model.lockedArea || model.type is AreaType.unplaceable or AreaType.removable,
                model.isBlocker, model.height, points, holeDtos));
        }
        var blockers = map.blockers;
        int blockerCount = blockers?.Count ?? 0;
        if (blockerCount > 8192) throw new InvalidOperationException("Map exceeds 8192 blockers.");
        reader.Add(blockerCount);
        var blockerDtos = capture ? new List<MapBlocker>(blockerCount) : null;
        for (int i = 0; i < blockerCount; i++)
        {
            var blocker = blockers![i];
            if (blocker == null) { reader.Add(-1); continue; }
            var p = blocker.Position.data;
            float radius = blocker.Radius;
            reader.Add(p.x); reader.Add(p.y); reader.Add(p.z); reader.Add(radius);
            blockerDtos?.Add(new MapBlocker(p.x, p.y, p.z, radius));
        }
        return new MapGeometry(reader.Hash.ToString("x16"), bounds, pathDtos?.ToArray() ?? Array.Empty<MapPath>(),
            areaDtos?.ToArray() ?? Array.Empty<MapArea>(), blockerDtos?.ToArray() ?? Array.Empty<MapBlocker>());
    }

    private static MapGeometry GetMapGeometry(SimMap map)
    {
        var fingerprint = ReadGeometry(map, false);
        if (cachedGeometry?.Revision != fingerprint.Revision) cachedGeometry = ReadGeometry(map, true);
        return cachedGeometry!;
    }

    private static bool TryReadRangeTowerIds(JsonElement payload, out string[] ids, out string error)
    {
        ids = Array.Empty<string>();
        error = "";
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("rangeTowerIds", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Array)
        {
            error = "rangeTowerIds must be an array of tower ID strings.";
            return false;
        }
        int count = value.GetArrayLength();
        if (count > 20)
        {
            error = "rangeTowerIds may contain at most 20 tower IDs.";
            return false;
        }
        var result = new string[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = "rangeTowerIds may contain only tower ID strings.";
                return false;
            }
            string id = item.GetString() ?? "";
            if (id.Length > 128)
            {
                error = "rangeTowerIds entries may be at most 128 characters.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "rangeTowerIds may not contain empty tower IDs.";
                return false;
            }
            if (!seen.Add(id))
            {
                error = $"rangeTowerIds contains duplicate tower ID '{id}'.";
                return false;
            }
            result[index++] = id;
        }
        ids = result;
        return true;
    }

    private static MapRangeOverlay UnsupportedRangeOverlay(
        string towerId,
        float range,
        bool ignoresBlockers,
        MapRangeAttack[] attacks,
        string reason) =>
        new(towerId, "unsupported", reason, range, ignoresBlockers,
            Array.Empty<MapPoint[]>(), Array.Empty<MapPoint[]>(), attacks);

    private static MapRangeAttack[] CaptureDirectAttacks(TowerModel? model)
    {
        if (model?.behaviors == null) return Array.Empty<MapRangeAttack>();
        var attacks = new List<MapRangeAttack>();
        foreach (var behavior in model.behaviors)
        {
            var attack = behavior?.TryCast<AttackModel>();
            if (attack == null) continue;
            if (attacks.Count >= 256)
                throw new InvalidOperationException("Tower has too many direct attacks.");
            if (!float.IsFinite(attack.range) || attack.range < 0f)
                throw new InvalidOperationException("Tower attack has an invalid range.");
            attacks.Add(new MapRangeAttack(attack.name ?? "", attack.range, attack.attackThroughWalls));
        }
        return attacks.ToArray();
    }

    private static bool IsUvClass(Il2CppAssets.Scripts.Simulation.SMath.Vector2 uv, float expected) =>
        uv.x == expected && uv.y == expected;

    private static bool IsKnownBorder(
        Il2CppAssets.Scripts.Simulation.SMath.Vector2 a,
        Il2CppAssets.Scripts.Simulation.SMath.Vector2 b,
        Il2CppAssets.Scripts.Simulation.SMath.Vector2 c) =>
        (IsUvClass(a, 0.4f) || IsUvClass(a, 1f)) &&
        (IsUvClass(b, 0.4f) || IsUvClass(b, 1f)) &&
        (IsUvClass(c, 0.4f) || IsUvClass(c, 1f));


    private static (MapPoint[][] Visible, MapPoint[][] Blocked) ExtractRangeTriangles(
        NativeMesh mesh, Vector3 meshPosition)
    {
        if (!mesh.isValid)
            throw new InvalidOperationException("Native range mesh is invalid.");
        var vertices = mesh.verticies;
        var uvs = mesh.uvs;
        var indices = mesh.triangles;
        if (vertices == null || uvs == null || indices == null ||
            vertices.Count != uvs.Count || indices.Count % 3 != 0 ||
            vertices.Count > 65536 || indices.Count > 65536 * 3)
            throw new InvalidOperationException("Native range mesh has invalid buffers.");
        // The standalone generator leaves mesh.position unset. Its vertices are
        // local to the supplied tower position, unlike GetRangeMeshes output.

        var visible = new List<MapPoint[]>(Math.Min(4096, indices.Count / 3));
        var blocked = new List<MapPoint[]>(Math.Min(4096, indices.Count / 3));
        for (int i = 0; i < indices.Count; i += 3)
        {
            int ia = indices[i], ib = indices[i + 1], ic = indices[i + 2];
            if (ia < 0 || ib < 0 || ic < 0 ||
                ia >= vertices.Count || ib >= vertices.Count || ic >= vertices.Count)
                throw new InvalidOperationException("Native range mesh has an out-of-range index.");
            var a = vertices[ia];
            var b = vertices[ib];
            var c = vertices[ic];
            var ua = uvs[ia];
            var ub = uvs[ib];
            var uc = uvs[ic];
            if (!float.IsFinite(a.x) || !float.IsFinite(a.y) || !float.IsFinite(a.z) ||
                !float.IsFinite(b.x) || !float.IsFinite(b.y) || !float.IsFinite(b.z) ||
                !float.IsFinite(c.x) || !float.IsFinite(c.y) || !float.IsFinite(c.z) ||
                !float.IsFinite(ua.x) || !float.IsFinite(ua.y) ||
                !float.IsFinite(ub.x) || !float.IsFinite(ub.y) ||
                !float.IsFinite(uc.x) || !float.IsFinite(uc.y))
                throw new InvalidOperationException("Native range mesh has non-finite data.");

            // Degenerate triangles are harmless native strip padding; do not reject a
            // valid mesh because of them, regardless of their UV marker. Compute this
            // from native coordinates before allocating any managed DTO points.
            float area2 = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (area2 == 0f) continue;
            bool isVisible = IsUvClass(ua, 0.6f) && IsUvClass(ub, 0.6f) && IsUvClass(uc, 0.6f);
            bool isBlocked = IsUvClass(ua, 0f) && IsUvClass(ub, 0f) && IsUvClass(uc, 0f);
            if (!isVisible && !isBlocked)
            {
                if (IsKnownBorder(ua, ub, uc)) continue;
                throw new InvalidOperationException("Native range mesh has an unexpected interior UV class.");
            }

            var pointA = new MapPoint(meshPosition.x + a.x, meshPosition.y + a.y);
            var pointB = new MapPoint(meshPosition.x + b.x, meshPosition.y + b.y);
            var pointC = new MapPoint(meshPosition.x + c.x, meshPosition.y + c.y);
            if (!float.IsFinite(pointA.X) || !float.IsFinite(pointA.Y) ||
                !float.IsFinite(pointB.X) || !float.IsFinite(pointB.Y) ||
                !float.IsFinite(pointC.X) || !float.IsFinite(pointC.Y))
                throw new InvalidOperationException("Native range mesh produced non-finite coordinates.");
            var triangle = new[] { pointA, pointB, pointC };
            var destination = isVisible ? visible : blocked;
            if (destination.Count >= 4096)
                throw new InvalidOperationException("Native range mesh exceeds 4096 triangles.");
            destination.Add(triangle);
        }
        return (visible.ToArray(), blocked.ToArray());
    }

    private static MapRangeOverlay BuildRangeOverlay(
        Il2CppAssets.Scripts.Unity.Bridge.TowerToSimulation tower,
        Simulation simulation)
    {
        string towerId = tower.Id.ToString();
        var simulationTower = tower.GetSimTower();
        var model = simulationTower?.towerModel ?? tower.Def;
        float modelRange = model?.range ?? 0f;
        bool validRange = model != null && float.IsFinite(modelRange) && modelRange >= 0f;
        float range = validRange ? modelRange : 0f;
        bool ignoresBlockers = model?.ignoreBlockers ?? false;
        MapRangeAttack[] attacks;
        try
        {
            attacks = CaptureDirectAttacks(model);
        }
        catch (Exception)
        {
            return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, Array.Empty<MapRangeAttack>(),
                "Direct attack metadata is unavailable.");
        }
        if (!validRange)
            return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, attacks,
                "Placed tower model or range is unavailable.");
        if (model!.isGlobalRange)
            return new MapRangeOverlay(towerId, "global",
                "Global range does not imply ignoring line of sight.", range, ignoresBlockers,
                Array.Empty<MapPoint[]>(), Array.Empty<MapPoint[]>(), attacks);
        var position = tower.simPosition;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, attacks,
                "Tower position is non-finite.");
        NativeMesh? mesh = null;
        try
        {
            // GetMeshStatically returns one owned local mesh. Unlike GetRangeMeshes,
            // it cannot include global/additional meshes from unrelated behaviors.
            mesh = NativeRangeMesh.GetMeshStatically(simulation, position, range, ignoresBlockers);
            if (mesh == null)
                return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, attacks,
                    "Native range mesh is unavailable.");
            var triangles = ExtractRangeTriangles(mesh, position);
            if (range > 0f && triangles.Visible.Length == 0 && triangles.Blocked.Length == 0)
                return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, attacks,
                    "Native range mesh has no interior triangles.");
            return new MapRangeOverlay(towerId, "native", null, range, ignoresBlockers,
                triangles.Visible, triangles.Blocked, attacks);
        }
        catch (Exception)
        {
            return UnsupportedRangeOverlay(towerId, range, ignoresBlockers, attacks,
                "Native range mesh extraction failed.");
        }
        finally
        {
            if (mesh != null)
                mesh.Release();
        }
    }

    private static BridgeResultV1 HandleGetMapLayout(BridgeRequestV1 request)
    {
        if (!TryReadRangeTowerIds(request.Payload, out var selectedRangeIds, out var rangeIdsError))
            return ErrorResult(request, "INVALID_ARGUMENT", rangeIdsError, false);
        var inGame = InGame.instance;
        var map = inGame?.bridge?.Simulation?.Map;
        if (inGame == null || !inGame.IsInGame() || map == null)
            return ErrorResult(request, "NO_ACTIVE_GAME", "Map geometry requires an active match.", false);
        var towers = inGame.GetAllTowerToSim();
        var placed = new List<MapTower>();
        var towersById = selectedRangeIds.Length == 0
            ? null
            : new Dictionary<string, Il2CppAssets.Scripts.Unity.Bridge.TowerToSimulation>(StringComparer.Ordinal);
        foreach (var tower in towers)
        {
            if (tower == null) continue;
            var p = tower.simPosition;
            placed.Add(new MapTower(tower.Id.ToString(), tower.Def?.name ?? "Unknown", tower.hero != null, p.x, p.y, tower.Def?.range ?? 0));
            towersById?.TryAdd(tower.Id.ToString(), tower);
        }
        if (selectedRangeIds.Length > 0)
        {
            foreach (string towerId in selectedRangeIds)
            {
                if (towersById == null || !towersById.TryGetValue(towerId, out _))
                    return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);
            }
        }
        var geometry = GetMapGeometry(map);
        MapRangeOverlay[] rangeOverlays = Array.Empty<MapRangeOverlay>();
        if (selectedRangeIds.Length > 0)
        {
            var simulation = inGame.bridge!.Simulation;
            rangeOverlays = new MapRangeOverlay[selectedRangeIds.Length];
            for (int i = 0; i < selectedRangeIds.Length; i++)
                rangeOverlays[i] = BuildRangeOverlay(towersById![selectedRangeIds[i]], simulation);
        }
        int targetRound = inGame.bridge != null ? inGame.bridge.GetCurrentRound() + 1 : 1;
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("round", out var roundProp) &&
            roundProp.ValueKind == JsonValueKind.Number &&
            roundProp.TryGetInt32(out var r) && r > 0)
        {
            targetRound = r;
        }

        var spawner = map.spawner;
        int cycleLength = 1;
        string splitterType = "Default";
        bool isAlternating = false;
        var activePathsForRound = new List<int>();
        var cycleSchedule = new List<object>();

        try
        {
            var allPaths = map.pathManager?.paths;
            var pathIndices = new Dictionary<IntPtr, int>();
            if (allPaths != null)
            {
                for (int i = 0; i < allPaths.Count; i++)
                {
                    var p = allPaths[i];
                    if (p != null) pathIndices[p.Pointer] = i;
                }
            }

            if (spawner != null)
            {
                cycleLength = Math.Max(1, spawner.GetNumberOfUniqueRounds());
                var junction = spawner.spawnJunction;
                if (junction != null)
                {
                    splitterType = junction.def?.GetIl2CppType()?.Name ?? junction.GetIl2CppType()?.Name ?? "Splitter";
                }
                isAlternating = cycleLength > 1 || splitterType.Contains("Alternate", StringComparison.OrdinalIgnoreCase);

                var spawnPaths = spawner.GetSpawnPathsForRound(targetRound);
                if (spawnPaths != null)
                {
                    foreach (var sp in spawnPaths)
                    {
                        if (sp != null && pathIndices.TryGetValue(sp.Pointer, out int idx) && !activePathsForRound.Contains(idx))
                            activePathsForRound.Add(idx);
                    }
                }

                int scheduleRounds = Math.Min(cycleLength, 32);
                for (int cycleRound = 1; cycleRound <= scheduleRounds; cycleRound++)
                {
                    var cPaths = spawner.GetSpawnPathsForRound(cycleRound);
                    var roundPathIndices = new List<int>();
                    if (cPaths != null)
                    {
                        foreach (var sp in cPaths)
                        {
                            if (sp != null && pathIndices.TryGetValue(sp.Pointer, out int idx) && !roundPathIndices.Contains(idx))
                                roundPathIndices.Add(idx);
                        }
                    }
                    cycleSchedule.Add(new
                    {
                        CycleRound = cycleRound,
                        ActivePaths = roundPathIndices.ToArray()
                    });
                }
            }

            if (activePathsForRound.Count == 0 && allPaths != null)
            {
                for (int i = 0; i < allPaths.Count; i++)
                {
                    if (allPaths[i]?.isActive == true && allPaths[i]?.isHidden == false)
                        activePathsForRound.Add(i);
                }
                if (activePathsForRound.Count == 0) activePathsForRound.Add(0);
            }
        }
        catch { }

        TrackGraph? trackGraph = null;
        try
        {
            trackGraph = BuildTrackGraph(map, geometry, targetRound);
        }
        catch { }

        return SuccessResult(request, new
        {
            SchemaVersion = 1,
            MapId = InGameData.CurrentGame?.selectedMap ?? map.mapModel.mapName,
            geometry.Revision,
            ObservedAtUtc = DateTime.UtcNow,
            geometry.Bounds,
            CoordinateSystem = "BTD6 simulation coordinates: +x right, +y down; rendered image Y matches the top-left game map. Bounds are game-clamped map bounds.",
            geometry.Paths,
            geometry.Areas,
            geometry.Blockers,
            PlacedTowers = placed,
            RangeOverlays = rangeOverlays,
            TrackGraph = trackGraph,
            Warnings = new[]
            {
                "Terrain and blocker geometry are not a tower-specific placement guarantee; validate using can_place_tower or find_placement_spots.",
                "Range overlays use native local line-of-sight meshes for selected tower IDs; direct attack metadata is listed, but additional/projectile/subtower coverage is not shown.",
                "Global-range towers are reported without triangles because global range does not imply ignoring line of sight. Unsupported extraction never fabricates a circle.",
                "Polylines show path model waypoints; area polygons show track surfaces. Inactive/hidden paths are identified separately."
            },
            Spawner = new
            {
                SplitterType = splitterType,
                CycleLength = cycleLength,
                IsAlternating = isAlternating,
                TargetRound = targetRound,
                ActivePathsForRound = activePathsForRound.ToArray(),
                CycleSchedule = cycleSchedule.ToArray()
            }
        });
    }


    private static float TrackDistance(MapPath[] paths, float x, float y)
    {
        float minSquared = float.PositiveInfinity;
        foreach (var path in paths)
        {
            if (!path.Active || path.Hidden) continue;
            for (int i = 0; i < path.Points.Length; i++)
            {
                var a = path.Points[i];
                var b = path.Points[Math.Min(i + 1, path.Points.Length - 1)];
                float dx = b.X - a.X, dy = b.Y - a.Y;
                float length = dx * dx + dy * dy;
                float t = length <= 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
                float ex = x - a.X - t * dx, ey = y - a.Y - t * dy;
                minSquared = Math.Min(minSquared, ex * ex + ey * ey);
            }
        }
        return float.IsPositiveInfinity(minSquared) ? 999f : MathF.Sqrt(minSquared);
    }

    private static ulong MixPathHash(ulong hash, int value)
    {
        unchecked { return (hash ^ (uint)value) * 1099511628211UL; }
    }

    private static ulong PathGeometryHash(MapPoint[] points)
    {
        ulong forward = 14695981039346656037UL;
        ulong reverse = forward;
        forward = MixPathHash(forward, points.Length);
        reverse = MixPathHash(reverse, points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            forward = MixPathHash(forward, BitConverter.SingleToInt32Bits(points[i].X));
            forward = MixPathHash(forward, BitConverter.SingleToInt32Bits(points[i].Y));
            int reverseIndex = points.Length - i - 1;
            reverse = MixPathHash(reverse, BitConverter.SingleToInt32Bits(points[reverseIndex].X));
            reverse = MixPathHash(reverse, BitConverter.SingleToInt32Bits(points[reverseIndex].Y));
        }
        return Math.Min(forward, reverse);
    }

    private static bool SamePathGeometry(MapPoint[] left, MapPoint[] right)
    {
        if (left.Length != right.Length) return false;
        bool forward = true, reverse = true;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].X != right[i].X || left[i].Y != right[i].Y) forward = false;
            int reverseIndex = right.Length - i - 1;
            if (left[i].X != right[reverseIndex].X || left[i].Y != right[reverseIndex].Y) reverse = false;
            if (!forward && !reverse) return false;
        }
        return true;
    }

    private static long QuantizeLineValue(double value, double scale) =>
        (long)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static CoverageLineKey BuildCoverageLineKey(
        MapPoint point,
        float dx,
        float dy,
        float length,
        out float directionX,
        out float directionY)
    {
        directionX = dx / length;
        directionY = dy / length;
        if (directionX < 0f || (directionX == 0f && directionY < 0f))
        {
            directionX = -directionX;
            directionY = -directionY;
        }
        double offset = (double)directionX * point.Y - (double)directionY * point.X;
        return new CoverageLineKey(
            QuantizeLineValue(directionX, 1_000_000d),
            QuantizeLineValue(directionY, 1_000_000d),
            QuantizeLineValue(offset, 10_000d));
    }

    private static CoveragePathData[] BuildCoveragePaths(MapPath[] paths, out int[] relevantPathIndices)
    {
        var unique = new List<CoveragePathData>();
        var pathIndices = new List<int>();
        var buckets = new Dictionary<ulong, List<CoveragePathData>>();
        foreach (var path in paths)
        {
            if (!path.Active || path.Hidden || path.Points.Length < 2) continue;
            ulong hash = PathGeometryHash(path.Points);
            bool duplicate = false;
            if (buckets.TryGetValue(hash, out var existing))
            {
                foreach (var prior in existing)
                {
                    if (SamePathGeometry(prior.Points, path.Points))
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            if (duplicate) continue;

            var segments = new List<CoverageSegment>(path.Points.Length - 1);
            float totalLength = 0f;
            for (int i = 0; i < path.Points.Length - 1; i++)
            {
                var a = path.Points[i];
                var b = path.Points[i + 1];
                float dx = b.X - a.X;
                float dy = b.Y - a.Y;
                float length = MathF.Sqrt(dx * dx + dy * dy);
                if (!float.IsFinite(length) || !float.IsFinite(totalLength))
                {
                    segments.Clear();
                    totalLength = 0f;
                    break;
                }
                if (length <= 0f) continue;
                CoverageLineKey lineKey = BuildCoverageLineKey(a, dx, dy, length, out float directionX, out float directionY);
                segments.Add(new CoverageSegment(a, b, totalLength, length, directionX, directionY, lineKey));
                totalLength += length;
            }
            if (segments.Count == 0 || !float.IsFinite(totalLength) || totalLength <= 0f) continue;
            var coveragePath = new CoveragePathData(path.PathIndex, path.Points, segments.ToArray(), totalLength);
            unique.Add(coveragePath);
            pathIndices.Add(path.PathIndex);
            if (existing == null)
            {
                existing = new List<CoveragePathData>();
                buckets.Add(hash, existing);
            }
            existing.Add(coveragePath);
        }
        relevantPathIndices = pathIndices.ToArray();
        return unique.ToArray();
    }

    private static void AddAngularArc(
        List<AngularArc> arcs,
        float ax,
        float ay,
        float bx,
        float by,
        float centerX,
        float centerY)
    {
        double ux = (double)ax - centerX;
        double uy = (double)ay - centerY;
        double vx = (double)bx - centerX;
        double vy = (double)by - centerY;
        double firstSquared = ux * ux + uy * uy;
        double secondSquared = vx * vx + vy * vy;
        if (!double.IsFinite(firstSquared) || !double.IsFinite(secondSquared) ||
            firstSquared <= 1e-12d || secondSquared <= 1e-12d)
            return;

        // A segment through the center has only two ray directions, not a
        // positive-width angular arc. Do not let atan2's branch cut turn that
        // into a misleading near-180-degree contribution.
        double cross = ux * vy - uy * vx;
        double dot = ux * vx + uy * vy;
        double scale = Math.Sqrt(firstSquared * secondSquared);
        if (Math.Abs(cross) <= 1e-10d * Math.Max(1d, scale) && dot <= 0d) return;

        const double TwoPi = Math.PI * 2d;
        double first = Math.Atan2(uy, ux);
        double second = Math.Atan2(vy, vx);
        double delta = second - first;
        while (delta > Math.PI) delta -= TwoPi;
        while (delta < -Math.PI) delta += TwoPi;
        double span = Math.Abs(delta);
        if (!double.IsFinite(span) || span <= 1e-9d) return;
        double start = delta >= 0d ? first : second;
        start %= TwoPi;
        if (start < 0d) start += TwoPi;
        if (start + span <= TwoPi)
        {
            arcs.Add(new AngularArc((float)start, (float)(start + span)));
        }
        else
        {
            arcs.Add(new AngularArc((float)start, (float)TwoPi));
            arcs.Add(new AngularArc(0f, (float)(start + span - TwoPi)));
        }
    }

    private static float UnionAngularCoverageDegrees(List<AngularArc> arcs)
    {
        if (arcs.Count == 0) return 0f;
        arcs.Sort((left, right) => left.Start.CompareTo(right.Start));
        float covered = 0f;
        float start = arcs[0].Start;
        float end = arcs[0].End;
        for (int i = 1; i < arcs.Count; i++)
        {
            var arc = arcs[i];
            if (arc.Start <= end + 1e-6f)
            {
                if (arc.End > end) end = arc.End;
                continue;
            }
            covered += end - start;
            start = arc.Start;
            end = arc.End;
        }
        covered += end - start;
        return Math.Clamp(covered * (180f / MathF.PI), 0f, 360f);
    }

    private static float UnionCoverageLength(CoverageUnionScratch scratch)
    {
        double total = 0d;
        foreach (var spans in scratch.Lines.Values)
        {
            if (spans.Count == 0) continue;
            spans.Sort((left, right) => left.Start.CompareTo(right.Start));
            double start = spans[0].Start;
            double end = spans[0].End;
            for (int i = 1; i < spans.Count; i++)
            {
                var span = spans[i];
                if (span.Start <= end + 1e-4d)
                {
                    if (span.End > end) end = span.End;
                    continue;
                }
                total += end - start;
                start = span.Start;
                end = span.End;
            }
            total += end - start;
        }
        return double.IsFinite(total) && total >= 0d && total <= float.MaxValue ? (float)total : 0f;
    }

    private static TrackCoverageSummary EvaluateTrackCoverage(
        CoveragePathData[] paths,
        float x,
        float y,
        float range,
        List<AngularArc> angularScratch,
        CoverageUnionScratch unionScratch,
        bool includeDetails)
    {
        angularScratch.Clear();
        unionScratch.Clear();
        var pathSummaries = includeDetails ? new List<CoveragePathSummary>() : null;
        foreach (var path in paths)
        {
            bool hasInterval = false;
            float mergedStart = 0f;
            float mergedEnd = 0f;
            var intervals = includeDetails ? new List<CoverageInterval>() : null;
            foreach (var segment in path.Segments)
            {
                if (!PlacementGeometryMath.TryClipLineSegmentToCircle(
                        segment.A.X, segment.A.Y, segment.B.X, segment.B.Y,
                        x, y, range, out float startT, out float endT))
                    continue;
                float startDistance = segment.StartDistance + startT * segment.Length;
                float endDistance = segment.StartDistance + endT * segment.Length;
                if (!float.IsFinite(startDistance) || !float.IsFinite(endDistance) ||
                    endDistance - startDistance <= 1e-5f)
                    continue;

                float clippedAx = segment.A.X + (segment.B.X - segment.A.X) * startT;
                float clippedAy = segment.A.Y + (segment.B.Y - segment.A.Y) * startT;
                float clippedBx = segment.A.X + (segment.B.X - segment.A.X) * endT;
                float clippedBy = segment.A.Y + (segment.B.Y - segment.A.Y) * endT;
                AddAngularArc(angularScratch, clippedAx, clippedAy, clippedBx, clippedBy, x, y);

                double projectedStart = (double)segment.DirectionX * clippedAx + (double)segment.DirectionY * clippedAy;
                double projectedEnd = (double)segment.DirectionX * clippedBx + (double)segment.DirectionY * clippedBy;
                if (double.IsFinite(projectedStart) && double.IsFinite(projectedEnd))
                {
                    if (projectedStart > projectedEnd) (projectedStart, projectedEnd) = (projectedEnd, projectedStart);
                    var spans = unionScratch.GetSpans(segment.LineKey);
                    spans.Add(new CoverageSpan(projectedStart, projectedEnd));
                }

                if (!hasInterval)
                {
                    mergedStart = startDistance;
                    mergedEnd = endDistance;
                    hasInterval = true;
                }
                else if (startDistance <= mergedEnd + 1e-4f)
                {
                    if (endDistance > mergedEnd) mergedEnd = endDistance;
                }
                else
                {
                    float intervalLength = mergedEnd - mergedStart;
                    intervals?.Add(new CoverageInterval(
                        mergedStart / path.TotalLength,
                        mergedEnd / path.TotalLength,
                        intervalLength));
                    mergedStart = startDistance;
                    mergedEnd = endDistance;
                }
            }
            if (hasInterval)
            {
                float intervalLength = mergedEnd - mergedStart;
                intervals?.Add(new CoverageInterval(
                    mergedStart / path.TotalLength,
                    mergedEnd / path.TotalLength,
                    intervalLength));
            }
            if (intervals is { Count: > 0 })
                pathSummaries!.Add(new CoveragePathSummary(path.PathIndex, intervals.Sum(item => item.Length), intervals.ToArray()));
        }

        return new TrackCoverageSummary(
            UnionCoverageLength(unionScratch),
            UnionAngularCoverageDegrees(angularScratch),
            pathSummaries?.ToArray() ?? Array.Empty<CoveragePathSummary>());
    }

    private static bool TryReadPlacementFloat(JsonElement value, string propertyName, out float result)
    {
        result = 0f;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out double parsed) ||
            !double.IsFinite(parsed) ||
            parsed < -float.MaxValue || parsed > float.MaxValue)
            return false;
        result = (float)parsed;
        return float.IsFinite(result);
    }

    private static long PlacementSampleCount(float min, float max, float step)
    {
        if (min > max) return 0;
        double intervals = ((double)max - min) / step;
        if (!double.IsFinite(intervals) || intervals >= MaxPlacementSamples)
            return MaxPlacementSamples + 1L;
        return (long)Math.Ceiling(intervals) + 1;
    }

    private static IEnumerator<BridgeResultV1?> RunPlacementSearch(BridgeRequestV1 request)
    {
        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "Placement search payload must be an object.", false);
            yield break;
        }

        var inGame = InGame.instance;
        var bridge = inGame?.bridge;
        var map = bridge?.Simulation?.Map;
        if (inGame == null || !inGame.IsInGame() || map == null)
        {
            yield return ErrorResult(request, "NO_ACTIVE_GAME", "Placement search requires an active match.", false);
            yield break;
        }
        long generation = matchGeneration;
        var simulation = bridge!.Simulation.Pointer;
        var gameModel = inGame.GetGameModel();
        if (gameModel == null)
        {
            yield return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Game model unavailable for placement search.", true);
            yield break;
        }

        string towerType = ReadString(request.Payload, "towerType", "DartMonkey");
        var tm = gameModel.GetTower(towerType, 0, 0, 0) ?? gameModel.GetTowerWithName(towerType) ?? gameModel.GetTowerFromId(towerType);
        if (tm == null)
        {
            yield return ErrorResult(request, "UNKNOWN_TOWER_TYPE", $"Unknown tower: {towerType}", false);
            yield break;
        }
        int limit = ReadInt(request.Payload, "limit", 20);
        float minDistance = ReadFloat(request.Payload, "minDistanceToTrack", 0);
        float maxDistance = ReadFloat(request.Payload, "maxDistanceToTrack", 100);
        if (limit < 1 || limit > 50 || !float.IsFinite(minDistance) || !float.IsFinite(maxDistance) || minDistance < 0 || maxDistance < minDistance)
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "Invalid placement limit or track-distance bounds.", false);
            yield break;
        }

        bool hasRankingStrategy = request.Payload.TryGetProperty("rankingStrategy", out var rankingProperty);
        string rankingStrategy = ReadString(request.Payload, "rankingStrategy", "balanced");
        if (hasRankingStrategy && rankingProperty.ValueKind != JsonValueKind.String)
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "rankingStrategy must be balanced, distanceToSupport, distanceToTrack, trackCoverage, or supportCoverage.", false);
            yield break;
        }
        if (!string.Equals(rankingStrategy, "balanced", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rankingStrategy, "distanceToSupport", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rankingStrategy, "distanceToTrack", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rankingStrategy, "trackCoverage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rankingStrategy, "supportCoverage", StringComparison.OrdinalIgnoreCase))
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "rankingStrategy must be balanced, distanceToSupport, distanceToTrack, trackCoverage, or supportCoverage.", false);
            yield break;
        }
        bool trackCoverageRanking = string.Equals(rankingStrategy, "trackCoverage", StringComparison.OrdinalIgnoreCase);
        bool supportCoverageRanking = string.Equals(rankingStrategy, "supportCoverage", StringComparison.OrdinalIgnoreCase);
        var coverageTargets = new List<(string Id, string TowerType, float X, float Y)>();
        if (request.Payload.TryGetProperty("coverTowerIds", out var coverIdsProperty))
        {
            if (coverIdsProperty.ValueKind != JsonValueKind.Array || coverIdsProperty.GetArrayLength() is < 1 or > 20)
            {
                yield return ErrorResult(request, "INVALID_ARGUMENT", "coverTowerIds must contain 1-20 placed tower IDs.", false);
                yield break;
            }
            var requestedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var idProperty in coverIdsProperty.EnumerateArray())
            {
                string? id = idProperty.ValueKind == JsonValueKind.String ? idProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !requestedIds.Add(id))
                {
                    yield return ErrorResult(request, "INVALID_ARGUMENT", "coverTowerIds must contain unique non-empty tower IDs of at most 128 characters.", false);
                    yield break;
                }
            }
            var allTowers = inGame.GetAllTowerToSim();
            if (allTowers != null)
                foreach (var tower in allTowers)
                    if (tower != null && requestedIds.Contains(tower.Id.ToString()))
                    {
                        var position = tower.simPosition;
                        coverageTargets.Add((tower.Id.ToString(), tower.Def?.baseId ?? "", position.x, position.y));
                    }
            var missingIds = requestedIds.Except(coverageTargets.Select(target => target.Id)).ToArray();
            if (missingIds.Length > 0)
            {
                yield return ErrorResult(request, "TOWER_NOT_FOUND", $"Coverage target tower(s) not found: {string.Join(", ", missingIds)}.", false);
                yield break;
            }
        }
        if (supportCoverageRanking && coverageTargets.Count == 0)
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "supportCoverage ranking requires coverTowerIds.", false);
            yield break;
        }
        if (supportCoverageRanking && tm.baseId is not ("MonkeyVillage" or "Alchemist"))
        {
            yield return ErrorResult(request, "UNSUPPORTED_TOWER", "supportCoverage ranking currently supports MonkeyVillage and Alchemist candidates.", false);
            yield break;
        }
        if ((trackCoverageRanking || supportCoverageRanking) && (!float.IsFinite(tm.range) || tm.range <= 0f))
        {
            yield return ErrorResult(request, "INVALID_TOWER_RANGE", $"Tower '{towerType}' has no finite positive model range for {rankingStrategy} ({tm.range}).", false);
            yield break;
        }

        var geometry = GetMapGeometry(map);
        var bounds = geometry.Bounds;
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MaxX) ||
            !float.IsFinite(bounds.MinY) || !float.IsFinite(bounds.MaxY) ||
            bounds.MinX > bounds.MaxX || bounds.MinY > bounds.MaxY)
        {
            yield return ErrorResult(request, "MAP_BOUNDS_INVALID", "Native map bounds are invalid for placement search.", true);
            yield break;
        }

        float minX = Math.Max(PlacementMinX, bounds.MinX), maxX = Math.Min(PlacementMaxX, bounds.MaxX);
        float minY = Math.Max(PlacementMinY, bounds.MinY), maxY = Math.Min(PlacementMaxY, bounds.MaxY), step = 15f;
        bool hasNear = request.Payload.TryGetProperty("near", out var near);
        float nearX = 0f, nearY = 0f;
        if (hasNear)
        {
            if (near.ValueKind != JsonValueKind.Object ||
                !TryReadPlacementFloat(near, "x", out float x) ||
                !TryReadPlacementFloat(near, "y", out float y) ||
                (near.TryGetProperty("radius", out _) && !TryReadPlacementFloat(near, "radius", out float _)))
            {
                yield return ErrorResult(request, "INVALID_ARGUMENT", "near requires finite numeric x and y, with an optional finite radius.", false);
                yield break;
            }
            float radius = ReadFloat(near, "radius", 40);
            if (!float.IsFinite(radius) || radius <= 0 || radius > 500)
            {
                yield return ErrorResult(request, "INVALID_ARGUMENT", "near requires a radius in (0, 500].", false);
                yield break;
            }
            minX = Math.Max(bounds.MinX, x - radius); maxX = Math.Min(bounds.MaxX, x + radius);
            minY = Math.Max(bounds.MinY, y - radius); maxY = Math.Min(bounds.MaxY, y + radius); step = 8f;
            nearX = x;
            nearY = y;
        }

        bool hasSupportId = request.Payload.TryGetProperty("withinRangeOfTowerId", out var supportIdProperty);
        string withinRangeTowerId = ReadString(request.Payload, "withinRangeOfTowerId", "");
        if (hasSupportId &&
            (supportIdProperty.ValueKind != JsonValueKind.String ||
             string.IsNullOrWhiteSpace(withinRangeTowerId) ||
             withinRangeTowerId.Length > 128))
        {
            yield return ErrorResult(request, "INVALID_ARGUMENT", "withinRangeOfTowerId must be a non-empty tower ID of at most 128 characters.", false);
            yield break;
        }

        TowerToSimulation? targetSupportTower = null;
        TowerModel? supportModel = null;
        float supportX = 0f, supportY = 0f, supportRange = 0f;
        if (!string.IsNullOrEmpty(withinRangeTowerId))
        {
            bool towerEnumerationFailed = false;
            try
            {
                var allTowers = inGame.GetAllTowerToSim();
                if (allTowers != null)
                    foreach (var tower in allTowers)
                    {
                        if (tower != null && tower.Id.ToString() == withinRangeTowerId)
                        {
                            targetSupportTower = tower;
                            break;
                        }
                    }
            }
            catch
            {
                towerEnumerationFailed = true;
            }
            if (towerEnumerationFailed)
            {
                yield return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Could not enumerate placed towers for the requested support range.", true);
                yield break;
            }
            if (targetSupportTower == null)
            {
                yield return ErrorResult(request, "TOWER_NOT_FOUND", $"Target support tower with ID '{withinRangeTowerId}' was not found.", false);
                yield break;
            }

            try { supportModel = targetSupportTower.GetSimTower()?.towerModel ?? targetSupportTower.Def; }
            catch { supportModel = targetSupportTower.Def; }
            var sPos = targetSupportTower.simPosition;
            supportX = sPos.x;
            supportY = sPos.y;
            supportRange = supportModel?.range ?? 0f;
            if (!float.IsFinite(supportX) || !float.IsFinite(supportY))
            {
                yield return ErrorResult(request, "INVALID_TOWER_POSITION", $"Target support tower '{withinRangeTowerId}' has a non-finite position.", false);
                yield break;
            }
            if (!float.IsFinite(supportRange) || supportRange <= 0f)
            {
                yield return ErrorResult(request, "INVALID_TOWER_RANGE", $"Target support tower '{withinRangeTowerId}' has no valid finite positive range ({supportRange}).", false);
                yield break;
            }

            if (!hasNear)
            {
                minX = bounds.MinX;
                maxX = bounds.MaxX;
                minY = bounds.MinY;
                maxY = bounds.MaxY;
            }

            minX = Math.Max(minX, (float)Math.Max((double)bounds.MinX, (double)supportX - supportRange));
            maxX = Math.Min(maxX, (float)Math.Min((double)bounds.MaxX, (double)supportX + supportRange));
            minY = Math.Max(minY, (float)Math.Max((double)bounds.MinY, (double)supportY - supportRange));
            maxY = Math.Min(maxY, (float)Math.Min((double)bounds.MaxY, (double)supportY + supportRange));
            step = 4f;
        }

        CoveragePathData[] coveragePaths = Array.Empty<CoveragePathData>();
        int[] relevantPathIndices = Array.Empty<int>();
        if (trackCoverageRanking)
            coveragePaths = BuildCoveragePaths(geometry.Paths, out relevantPathIndices);

        float refinementStep = trackCoverageRanking ? Math.Max(step * .5f, .5f) : 0f;
        long xSamples = PlacementSampleCount(minX, maxX, step);
        long ySamples = PlacementSampleCount(minY, maxY, step);
        long estimatedSamples = xSamples * ySamples;
        if (estimatedSamples > MaxPlacementSamples)
        {
            yield return ErrorResult(request, "PLACEMENT_SEARCH_TOO_LARGE", $"Placement search is bounded to {MaxPlacementSamples} samples; narrow the near or support-range filters.", false);
            yield break;
        }

        var candidates = new List<PlacementCandidate>((int)Math.Min(estimatedSamples, MaxPlacementSamples));
        var angularScratch = trackCoverageRanking ? new List<AngularArc>(256) : null;
        var unionScratch = trackCoverageRanking ? new CoverageUnionScratch() : null;
        var sampledCoordinates = new HashSet<(int X, int Y)>();
        int inputId = bridge.GetInputId();
        int nativeValidationChecks = 0;
        int refinementValidationChecks = 0;

        bool TryEvaluateCandidate(float x, float y, bool refinement)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || x < minX || x > maxX || y < minY || y > maxY)
                return false;
            var key = (BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y));
            if (!sampledCoordinates.Add(key) || nativeValidationChecks >= MaxPlacementSamples)
                return false;

            double distanceToSupport = 0;
            bool inSupportRange = true;
            if (targetSupportTower != null)
            {
                double dx = (double)x - supportX;
                double dy = (double)y - supportY;
                double distanceSquared = dx * dx + dy * dy;
                double rangeSquared = (double)supportRange * supportRange;
                inSupportRange = distanceSquared <= rangeSquared;
                if (inSupportRange) distanceToSupport = Math.Sqrt(distanceSquared);
            }
            if (!inSupportRange) return false;

            float distance = TrackDistance(geometry.Paths, x, y);
            if (distance < minDistance || distance > maxDistance) return false;
            nativeValidationChecks++;
            if (refinement) refinementValidationChecks++;
            if (!bridge.CanPlaceTowerAt(new UnityEngine.Vector2(x, y), tm, inputId, ObjectId.Invalid))
                return false;

            float uniqueTrackLength = 0f;
            float angularCoverageDegrees = 0f;
            if (trackCoverageRanking)
            {
                var coverage = EvaluateTrackCoverage(coveragePaths, x, y, tm.range, angularScratch!, unionScratch!, false);
                uniqueTrackLength = coverage.UniqueTrackLength;
                angularCoverageDegrees = coverage.AngularCoverageDegrees;
            }
            candidates.Add(new PlacementCandidate(x, y, distance, (float)distanceToSupport, uniqueTrackLength, angularCoverageDegrees));
            return true;
        }

        yield return null;
        if (hasNear)
        {
            TryEvaluateCandidate(nearX, nearY, false);
            yield return null;
        }
        for (long xIndex = 0; xIndex < xSamples; xIndex++)
        {
            double rawX = (double)minX + xIndex * (double)step;
            if (rawX > maxX) break;
            float x = (float)rawX;
            if (!float.IsFinite(x)) break;
            if (x < minX) x = minX;
            for (long yIndex = 0; yIndex < ySamples; yIndex++)
            {
                double rawY = (double)minY + yIndex * (double)step;
                if (rawY > maxY) break;
                float y = (float)rawY;
                if (!float.IsFinite(y)) break;
                if (y < minY) y = minY;
                if (matchGeneration != generation || InGame.instance?.bridge?.Simulation?.Pointer != simulation)
                {
                    yield return ErrorResult(request, "MATCH_CHANGED", "Match changed during placement search.", true);
                    yield break;
                }
                TryEvaluateCandidate(x, y, false);
                yield return null;
            }
        }

        // Seed corners independently of coarse-grid winners. A narrow legal pocket
        // can have no valid coarse neighbor and would otherwise never be refined.
        if (trackCoverageRanking)
        {
            int bendSamples = 0;
            foreach (var path in coveragePaths)
            {
                for (int pointIndex = 1; pointIndex + 1 < path.Points.Length && bendSamples < 1024; pointIndex++)
                {
                    var a = path.Points[pointIndex - 1];
                    var b = path.Points[pointIndex];
                    var c = path.Points[pointIndex + 1];
                    foreach (var point in PlacementSearchGeometry.BendCandidates(a.X, a.Y, b.X, b.Y, c.X, c.Y, tm.range))
                    {
                        if (bendSamples >= 1024 || nativeValidationChecks >= MaxPlacementSamples) break;
                        if (point.X < minX || point.X > maxX || point.Y < minY || point.Y > maxY) continue;
                        if (matchGeneration != generation || InGame.instance?.bridge?.Simulation?.Pointer != simulation)
                        {
                            yield return ErrorResult(request, "MATCH_CHANGED", "Match changed during placement search.", true);
                            yield break;
                        }
                        bendSamples++;
                        TryEvaluateCandidate(point.X, point.Y, false);
                        yield return null;
                    }
                }
                if (bendSamples >= 1024 || nativeValidationChecks >= MaxPlacementSamples) break;
            }
        }

        // Re-rank after each pass so a discovered improvement becomes the next
        // local center. Preserve distinct neighborhoods throughout refinement.
        for (int pass = 0; trackCoverageRanking && pass < 3 &&
            nativeValidationChecks < MaxPlacementSamples && candidates.Count > 0; pass++)
        {
            float passStep = Math.Max(refinementStep / (1 << pass), .5f);
            var seeds = PlacementSearchGeometry.SelectSeparated(
                candidates.OrderByDescending(item => item.UniqueTrackLength)
                    .ThenByDescending(item => item.AngularCoverageDegrees)
                    .ThenBy(item => item.DistanceToTrack),
                16, Math.Max(step * 1.5f, tm.range * .5f));
            foreach (var seed in seeds)
            {
                for (int xOffset = -1; xOffset <= 1 && nativeValidationChecks < MaxPlacementSamples; xOffset++)
                {
                    for (int yOffset = -1; yOffset <= 1 && nativeValidationChecks < MaxPlacementSamples; yOffset++)
                    {
                        if (xOffset == 0 && yOffset == 0) continue;
                        if (matchGeneration != generation || InGame.instance?.bridge?.Simulation?.Pointer != simulation)
                        {
                            yield return ErrorResult(request, "MATCH_CHANGED", "Match changed during placement search.", true);
                            yield break;
                        }
                        TryEvaluateCandidate(seed.X + xOffset * passStep, seed.Y + yOffset * passStep, true);
                        yield return null;
                    }
                }
            }
        }

        if (matchGeneration != generation || InGame.instance?.bridge?.Simulation?.Pointer != simulation)
        {
            yield return ErrorResult(request, "MATCH_CHANGED", "Match changed during placement search.", true);
            yield break;
        }

        if (targetSupportTower != null)
        {
            bool supportStillPlaced = false;
            bool towerVerificationFailed = false;
            try
            {
                var currentTowers = inGame.GetAllTowerToSim();
                if (currentTowers != null)
                    foreach (var tower in currentTowers)
                        if (tower != null && tower.Id.ToString() == withinRangeTowerId)
                        {
                            supportStillPlaced = true;
                            break;
                        }
            }
            catch
            {
                towerVerificationFailed = true;
            }
            if (towerVerificationFailed)
            {
                yield return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Could not verify the support tower after placement search.", true);
                yield break;
            }
            if (!supportStillPlaced)
            {
                yield return ErrorResult(request, "TOWER_NOT_FOUND", $"Target support tower with ID '{withinRangeTowerId}' is no longer placed.", true);
                yield break;
            }
        }

        int CoveredTargetCount(PlacementCandidate candidate) => coverageTargets.Count(target =>
        {
            double dx = candidate.X - target.X;
            double dy = candidate.Y - target.Y;
            return dx * dx + dy * dy <= (double)tm.range * tm.range;
        });
        double CoveredTargetDistance(PlacementCandidate candidate) => coverageTargets.Sum(target =>
        {
            double dx = candidate.X - target.X;
            double dy = candidate.Y - target.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        });

        IEnumerable<PlacementCandidate> sortedCandidates = candidates;
        if (supportCoverageRanking)
        {
            sortedCandidates = candidates
                .OrderByDescending(CoveredTargetCount)
                .ThenBy(CoveredTargetDistance)
                .ThenBy(c => c.DistanceToTrack);
        }
        else if (trackCoverageRanking)
        {
            sortedCandidates = candidates
                .OrderByDescending(c => c.UniqueTrackLength)
                .ThenByDescending(c => c.AngularCoverageDegrees)
                .ThenBy(c => c.DistanceToTrack);
        }
        else if (targetSupportTower != null)
        {
            if (string.Equals(rankingStrategy, "distanceToSupport", StringComparison.OrdinalIgnoreCase))
                sortedCandidates = candidates.OrderBy(c => c.DistanceToSupport);
            else if (string.Equals(rankingStrategy, "distanceToTrack", StringComparison.OrdinalIgnoreCase))
                sortedCandidates = candidates.OrderBy(c => c.DistanceToTrack);
            else
            {
                float trackScale = Math.Max(maxDistance, 1e-6f);
                sortedCandidates = candidates.OrderBy(c =>
                    0.5f * (c.DistanceToSupport / supportRange) +
                    0.5f * (c.DistanceToTrack / trackScale));
            }
        }
        else
        {
            sortedCandidates = candidates.OrderBy(c => c.DistanceToTrack);
        }
        var returnedCandidates = trackCoverageRanking
            ? PlacementSearchGeometry.SelectSeparated(sortedCandidates, limit, Math.Max(step, tm.range * .5f)).ToArray()
            : sortedCandidates.Take(limit).ToArray();

        object? targetSupportDto = targetSupportTower == null ? null : new
        {
            Id = targetSupportTower.Id.ToString(),
            TowerType = supportModel?.baseId ?? targetSupportTower.Def?.baseId ?? "",
            Name = supportModel?.name ?? targetSupportTower.Def?.name ?? "",
            Position = new { X = supportX, Y = supportY },
            Range = supportRange
        };
        var searchDto = new
        {
            RankingStrategy = rankingStrategy,
            CoarseStep = step,
            RefinementStep = refinementStep,
            NativeChecks = nativeValidationChecks,
            RefinementChecks = refinementValidationChecks,
            MaxNativeChecks = MaxPlacementSamples,
            RelevantPathIndices = relevantPathIndices,
            CoverageBasis = "active_path_polylines",
            Notes = new[]
            {
                "trackCoverage scores unique geometric length of active, non-hidden path polylines inside the requested tower model range.",
                "Exact duplicate or reversed path polylines are aggregated once under the first path index; collinear shared segments (using a bounded line-key tolerance) are unioned for uniqueTrackLength, while other overlaps are not. Per-path interval details retain route identity and may overlap; uniqueTrackLength is the deduplicated score.",
                "Angular coverage is the union of geometric viewing arcs in degrees and is a radial-attack heuristic, not native line of sight or projectile behavior.",
                "Search includes the requested near center, coarse samples, up to 1024 in-bounds bend-offset samples, and three refinement passes around up to 16 spatially separated seeds. refinementStep is the initial half-step; later passes halve it to a minimum of 0.5.",
                "trackCoverage results suppress neighbors within max(coarseStep, range / 2) of a better returned candidate; fewer than limit may be returned. This is a bounded heuristic, not an exhaustive optimum.",
                "Native CanPlaceTowerAt remains authoritative for footprint, terrain, and existing-tower validation; all discovery and refinement share the 4096 native-check budget.",
                "Path interval arrays are bounded to 128 paths and 512 intervals per returned spot; detailsOmitted, pathCount, and intervalCount distinguish omission from zero coverage."
            }
        };

        var returnedSpots = returnedCandidates.Select(c =>
        {
            object? trackCoverage = null;
            if (trackCoverageRanking)
            {
                var coverage = EvaluateTrackCoverage(coveragePaths, c.X, c.Y, tm.range, angularScratch!, unionScratch!, true);
                const int maxCoverageDetailPaths = 128;
                const int maxCoverageDetailIntervals = 512;
                int pathCount = coverage.Paths.Length;
                int intervalCount = coverage.Paths.Sum(path => path.Intervals.Length);
                bool includePathDetails = pathCount <= maxCoverageDetailPaths && intervalCount <= maxCoverageDetailIntervals;
                object[] pathDetails = includePathDetails
                    ? coverage.Paths.Select(path => (object)new
                    {
                        PathIndex = path.PathIndex,
                        InRangeLength = MathF.Round(path.InRangeLength, 2),
                        Intervals = path.Intervals.Select(interval => new
                        {
                            StartProgress = MathF.Round(interval.StartProgress, 4),
                            EndProgress = MathF.Round(interval.EndProgress, 4),
                            Length = MathF.Round(interval.Length, 2)
                        }).ToArray()
                    }).ToArray()
                    : Array.Empty<object>();
                trackCoverage = new
                {
                    RangeRadius = tm.range,
                    UniqueTrackLength = MathF.Round(coverage.UniqueTrackLength, 2),
                    AngularCoverageDegrees = MathF.Round(coverage.AngularCoverageDegrees, 1),
                    DetailsOmitted = !includePathDetails,
                    PathCount = pathCount,
                    IntervalCount = intervalCount,
                    Paths = pathDetails
                };
            }
            var coveredTowerIds = coverageTargets.Where(target =>
            {
                double dx = c.X - target.X;
                double dy = c.Y - target.Y;
                return dx * dx + dy * dy <= (double)tm.range * tm.range;
            }).Select(target => target.Id).ToArray();
            var uncoveredTowerIds = coverageTargets.Select(target => target.Id).Except(coveredTowerIds).ToArray();
            var baseSpot = new Dictionary<string, object?>
            {
                ["x"] = c.X,
                ["y"] = c.Y,
                ["distanceToTrack"] = MathF.Round(c.DistanceToTrack, 1),
                ["distanceToSupport"] = targetSupportTower != null ? (float?)MathF.Round(c.DistanceToSupport, 1) : null,
                ["coveredTowerIds"] = coveredTowerIds,
                ["uncoveredTowerIds"] = uncoveredTowerIds,
                ["recipientCount"] = coveredTowerIds.Length,
                ["supportRange"] = coverageTargets.Count > 0 ? tm.range : null
            };
            if (trackCoverageRanking)
                baseSpot["trackCoverage"] = trackCoverage;
            return (object)baseSpot;
        }).ToArray();

        var response = new Dictionary<string, object?>
        {
            ["towerType"] = tm.name,
            ["totalFound"] = candidates.Count,
            ["returned"] = returnedSpots.Length,
            ["targetSupportTower"] = targetSupportDto,
            ["spots"] = returnedSpots,
            ["coverageTargets"] = coverageTargets.Select(target => new
            {
                target.Id,
                target.TowerType,
                Position = new { target.X, target.Y }
            }).ToArray(),
            ["coverageBasis"] = coverageTargets.Count > 0 ? "candidate_model_range_center_distance" : null,
            ["eligibilityVerified"] = false,
            ["observedAtUtc"] = DateTime.UtcNow,
            ["geometryRevision"] = geometry.Revision,
            ["note"] = supportCoverageRanking
                ? "Candidates are ranked by requested tower centers inside the candidate tower model range. This geometric support plan does not prove native buff eligibility; inspect recipients after placement."
                : trackCoverageRanking
                    ? "Coordinates are tower centers validated by native CanPlaceTowerAt. TrackCoverage is geometric active-path length inside the requested tower model range with disjoint normalized path intervals and radial angular coverage; it does not model native line of sight, path width, projectile footprint, attack targeting, or subtower behavior. Placement is revalidated by the game when acted upon."
                    : targetSupportTower == null
                        ? "Coordinates are tower centers; native validation includes the requested tower footprint. Candidates were validated over multiple frames and placement is revalidated by the game when acted upon."
                        : "Coordinates are tower centers; native validation includes the requested tower footprint. DistanceToSupport is center-to-center Euclidean distance against the support tower's current model range; this does not prove support-recipient eligibility, line of sight, sacrifice inclusion, or other tower-specific aura rules. Candidates were validated over multiple frames and placement is revalidated by the game when acted upon."
        };
        if (trackCoverageRanking) response["search"] = searchDto;
        yield return SuccessResult(request, response);
    }
}
