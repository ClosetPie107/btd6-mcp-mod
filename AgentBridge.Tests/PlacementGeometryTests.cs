using System;
using System.Linq;

namespace AgentBridge;

internal static class PlacementGeometryTests
{
    public static void Run()
    {
        AssertClip("secant", -2f, 0f, 2f, 0f, 0f, 0f, 1f, .25f, .75f);
        AssertClip("vertical pass", 0f, -3f, 0f, 3f, 0f, 0f, 1f, 1f / 3f, 2f / 3f);
        AssertClip("tangent", -2f, 1f, 2f, 1f, 0f, 0f, 1f, .5f, .5f);
        if (PlacementGeometryMath.TryClipLineSegmentToCircle(-2f, 2f, 2f, 2f, 0f, 0f, 1f, out _, out _))
            throw new InvalidOperationException("FAIL disjoint segment");
        AssertBendDiscovery();
        AssertDiverseShortlist();
        Console.WriteLine("PASS placement line-circle regressions");
    }

    private static void AssertBendDiscovery()
    {
        // Party Parade's right upper inside corner. The global 15-unit grid
        // misses this narrow pocket; its legal candidate must cover both legs.
        var candidates = PlacementSearchGeometry.BendCandidates(
            100.6f, 40f, 100.6f, -42f, 46.3f, -42f, 23f).ToArray();
        var inside = candidates.Where(p =>
            p.X >= 82f && p.X <= 84.1f && p.Y >= -25.5f && p.Y <= -23f).ToArray();
        if (!inside.Any(p => CoveredLength(100.6f, 40f, 100.6f, -42f, p.X, p.Y, 23f) +
            CoveredLength(100.6f, -42f, 46.3f, -42f, p.X, p.Y, 23f) > 60f))
            throw new InvalidOperationException("FAIL bend discovery misses dual-leg coverage pocket");
        var reversed = PlacementSearchGeometry.BendCandidates(
            46.3f, -42f, 100.6f, -42f, 100.6f, 40f, 23f).ToHashSet();
        if (!candidates.All(reversed.Contains))
            throw new InvalidOperationException("FAIL bend discovery depends on travel direction");
        if (PlacementSearchGeometry.BendCandidates(0, 0, 0, 0, 1, 1, 23).Any() ||
            PlacementSearchGeometry.BendCandidates(-10, 0, 0, 0, 10, 0, 23).Any())
            throw new InvalidOperationException("FAIL degenerate or straight bend candidates");
        Console.WriteLine("PASS bend discovery finds >60 track units instead of 33.62");
    }

    private static float CoveredLength(float ax, float ay, float bx, float by, float x, float y, float range)
    {
        if (!PlacementGeometryMath.TryClipLineSegmentToCircle(ax, ay, bx, by, x, y, range, out float start, out float end))
            return 0;
        return MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) * (end - start);
    }

    private static void AssertDiverseShortlist()
    {
        var ranked = new[]
        {
            new PlacementCandidate(-30, -80, 15, 0, 34, 94),
            new PlacementCandidate(-30, -79, 15, 0, 33, 94),
            new PlacementCandidate(84, -25, 17, 0, 32, 90),
            new PlacementCandidate(-30, -65, 15, 0, 31, 90)
        };
        var selected = PlacementSearchGeometry.SelectSeparated(ranked, 3, 15);
        if (!selected.SequenceEqual(new[] { ranked[0], ranked[2], ranked[3] }))
            throw new InvalidOperationException("FAIL shortlist hides distinct alternatives or suppresses separation boundary");
        if (PlacementSearchGeometry.SelectSeparated(ranked.Take(2), 8, 15).Count != 1)
            throw new InvalidOperationException("FAIL shortlist pads with near-duplicates");
        Console.WriteLine("PASS diverse placement shortlist preserves best and distinct alternatives");
    }

    private static void AssertClip(string name, float ax, float ay, float bx, float by,
        float cx, float cy, float radius, float expectedStart, float expectedEnd)
    {
        if (!PlacementGeometryMath.TryClipLineSegmentToCircle(ax, ay, bx, by, cx, cy, radius, out float start, out float end) ||
            MathF.Abs(start - expectedStart) > 1e-4f || MathF.Abs(end - expectedEnd) > 1e-4f)
            throw new InvalidOperationException($"FAIL {name}: [{start}, {end}]");
    }
}
