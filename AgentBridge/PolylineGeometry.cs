using System;
using System.Collections.Generic;

namespace AgentBridge;

// Pure managed geometry shared with the host regression executable.
public sealed partial class AgentBridgeMod
{
    private sealed record MapPoint(float X, float Y);
    private static float PointDist(MapPoint a, MapPoint b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static float RawPolylineLength(IReadOnlyList<MapPoint> points)
    {
        float length = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            float segmentLength = PointDist(points[i], points[i + 1]);
            if (!float.IsFinite(segmentLength)) return float.NaN;
            length += segmentLength;
            if (!float.IsFinite(length)) return float.NaN;
        }
        return length;
    }

    private static bool PointsWithinTolerance(MapPoint a, MapPoint b, float tolerance)
    {
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) ||
            !float.IsFinite(b.X) || !float.IsFinite(b.Y))
        {
            return false;
        }

        float distance = PointDist(a, b);
        return float.IsFinite(distance) && distance <= tolerance;
    }

    // Compare a fixed point with a point interpolated on one segment without
    // allocating a temporary MapPoint for every arc-length breakpoint.
    private static bool PointOnSegmentWithinTolerance(
        MapPoint fixedPoint,
        MapPoint segmentStart,
        MapPoint segmentEnd,
        float fraction,
        float tolerance)
    {
        if (fraction <= 0f) return PointsWithinTolerance(fixedPoint, segmentStart, tolerance);
        if (fraction >= 1f) return PointsWithinTolerance(fixedPoint, segmentEnd, tolerance);

        float x = segmentStart.X + (segmentEnd.X - segmentStart.X) * fraction;
        float y = segmentStart.Y + (segmentEnd.Y - segmentStart.Y) * fraction;
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;

        float dx = fixedPoint.X - x;
        float dy = fixedPoint.Y - y;
        float distanceSquared = dx * dx + dy * dy;
        float toleranceSquared = tolerance * tolerance;
        return float.IsFinite(distanceSquared) && distanceSquared <= toleranceSquared;
    }

    private static bool IsConstantPolylineWithinTolerance(
        MapPoint fixedPoint,
        IReadOnlyList<MapPoint> points,
        float tolerance)
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (!PointsWithinTolerance(fixedPoint, points[i], tolerance)) return false;
        }
        return true;
    }

    private static bool IsGeometryEquivalent(IReadOnlyList<MapPoint>? a, IReadOnlyList<MapPoint>? b, float tolerance = 1.0f)
    {
        if (a == null || b == null) return false;
        if (a.Count == 0 && b.Count == 0) return true;
        if (a.Count == 0 || b.Count == 0) return false;
        if (!float.IsFinite(tolerance) || tolerance < 0f) return false;
        if (!PointsWithinTolerance(a[0], b[0], tolerance)) return false;

        float totalA = RawPolylineLength(a);
        float totalB = RawPolylineLength(b);
        if (!float.IsFinite(totalA) || !float.IsFinite(totalB)) return false;
        if (MathF.Abs(totalA - totalB) > tolerance * 2f) return false;

        // A zero-length polyline is a single fixed location for purposes of
        // comparison. Its counterpart still needs every vertex checked.
        if (totalA == 0f)
        {
            return totalB == 0f
                ? PointsWithinTolerance(a[0], b[0], tolerance)
                : IsConstantPolylineWithinTolerance(a[0], b, tolerance);
        }
        if (totalB == 0f)
        {
            return IsConstantPolylineWithinTolerance(b[0], a, tolerance);
        }

        // The difference between two linear interpolants is itself linear
        // between the union of their normalized arc-length breakpoints. The
        // distance to zero is convex, so checking those breakpoints checks
        // the complete polylines rather than a fixed set of waypoint indexes.
        int ia = 0;
        int ib = 0;
        float consumedA = 0f;
        float consumedB = 0f;

        while (ia < a.Count - 1 && ib < b.Count - 1)
        {
            while (ia < a.Count - 1 && PointDist(a[ia], a[ia + 1]) == 0f) ia++;
            while (ib < b.Count - 1 && PointDist(b[ib], b[ib + 1]) == 0f) ib++;
            if (ia >= a.Count - 1 || ib >= b.Count - 1) break;

            float segmentA = PointDist(a[ia], a[ia + 1]);
            float segmentB = PointDist(b[ib], b[ib + 1]);
            float endA = ia == a.Count - 2 ? totalA : consumedA + segmentA;
            float endB = ib == b.Count - 2 ? totalB : consumedB + segmentB;
            float fractionA = endA / totalA;
            float fractionB = endB / totalB;

            if (fractionA <= fractionB)
            {
                float targetB = fractionA * totalB;
                float fractionOnB = (targetB - consumedB) / segmentB;
                if (!PointOnSegmentWithinTolerance(a[ia + 1], b[ib], b[ib + 1], fractionOnB, tolerance))
                    return false;
            }
            else
            {
                float targetA = fractionB * totalA;
                float fractionOnA = (targetA - consumedA) / segmentA;
                if (!PointOnSegmentWithinTolerance(b[ib + 1], a[ia], a[ia + 1], fractionOnA, tolerance))
                    return false;
            }

            if (fractionA <= fractionB)
            {
                consumedA = endA;
                ia++;
            }
            if (fractionB <= fractionA)
            {
                consumedB = endB;
                ib++;
            }
        }

        // Explicit endpoint comparison also covers harmless floating-point
        // differences in the final cumulative-length breakpoint.
        return PointsWithinTolerance(a[^1], b[^1], tolerance);
    }
}
