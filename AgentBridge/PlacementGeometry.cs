using System;
using System.Collections.Generic;

namespace AgentBridge;

// Pure managed math used by placement ranking and host-side regressions. It has no
// Unity/native dependencies and treats tangent contact as a zero-length interval.
internal static class PlacementGeometryMath
{
    public static bool TryClipLineSegmentToCircle(
        float ax,
        float ay,
        float bx,
        float by,
        float centerX,
        float centerY,
        float radius,
        out float startT,
        out float endT)
    {
        startT = 0f;
        endT = 0f;
        if (!float.IsFinite(ax) || !float.IsFinite(ay) ||
            !float.IsFinite(bx) || !float.IsFinite(by) ||
            !float.IsFinite(centerX) || !float.IsFinite(centerY) ||
            !float.IsFinite(radius) || radius < 0f)
            return false;

        double dx = (double)bx - ax;
        double dy = (double)by - ay;
        double fx = (double)ax - centerX;
        double fy = (double)ay - centerY;
        double a = dx * dx + dy * dy;
        double radiusSquared = (double)radius * radius;
        if (!double.IsFinite(a) || !double.IsFinite(radiusSquared)) return false;

        // A point segment is either wholly in or wholly out of the closed disk.
        if (a == 0d)
        {
            double distanceSquared = fx * fx + fy * fy;
            if (!double.IsFinite(distanceSquared) || distanceSquared > radiusSquared) return false;
            startT = 0f;
            endT = 1f;
            return true;
        }

        double b = 2d * (fx * dx + fy * dy);
        double c = fx * fx + fy * fy - radiusSquared;
        double discriminant = b * b - 4d * a * c;
        if (!double.IsFinite(b) || !double.IsFinite(c) || !double.IsFinite(discriminant)) return false;
        double tolerance = 1e-12d * Math.Max(1d, Math.Abs(b * b) + Math.Abs(4d * a * c));
        if (discriminant < -tolerance) return false;
        if (discriminant < 0d) discriminant = 0d;

        double root = Math.Sqrt(discriminant);
        double t0 = (-b - root) / (2d * a);
        double t1 = (-b + root) / (2d * a);
        if (t0 > t1) (t0, t1) = (t1, t0);
        t0 = Math.Max(0d, t0);
        t1 = Math.Min(1d, t1);
        if (!double.IsFinite(t0) || !double.IsFinite(t1) || t1 < t0) return false;
        startT = (float)t0;
        endT = (float)t1;
        return true;
    }
}

internal readonly record struct PlacementCandidate(
    float X,
    float Y,
    float DistanceToTrack,
    float DistanceToSupport,
    float UniqueTrackLength,
    float AngularCoverageDegrees);

internal static class PlacementSearchGeometry
{
    // Intersections of offset segment lines cover all four sides of a bend.
    // Clearance bands are range-relative; native validation decides footprint fit.
    public static IEnumerable<(float X, float Y)> BendCandidates(
        float ax, float ay, float bx, float by, float cx, float cy, float range)
    {
        double ux = (double)ax - bx, uy = (double)ay - by;
        double vx = (double)cx - bx, vy = (double)cy - by;
        double uLength = Math.Sqrt(ux * ux + uy * uy);
        double vLength = Math.Sqrt(vx * vx + vy * vy);
        if (!double.IsFinite(uLength) || !double.IsFinite(vLength) ||
            uLength == 0 || vLength == 0 || !float.IsFinite(range) || range <= 0)
            yield break;
        ux /= uLength;
        uy /= uLength;
        vx /= vLength;
        vy /= vLength;
        double determinant = ux * vy - uy * vx;
        // Nearly straight/reversing segments do not define a useful local corner.
        if (Math.Abs(determinant) < .1) yield break;
        for (int band = 0; band < 5; band++)
        {
            double clearance = range * (.5 + band * .125);
            for (int firstSign = -1; firstSign <= 1; firstSign += 2)
                for (int secondSign = -1; secondSign <= 1; secondSign += 2)
                {
                    double first = firstSign * clearance, second = secondSign * clearance;
                    double dx = (first * vx - ux * second) / determinant;
                    double dy = (first * vy - uy * second) / determinant;
                    if (dx * dx + dy * dy > 4d * range * range) continue;
                    float x = (float)(bx + dx), y = (float)(by + dy);
                    if (float.IsFinite(x) && float.IsFinite(y)) yield return (x, y);
                }
        }
    }

    // Input is already ranked. Keep the best representative of each neighborhood,
    // without filling a short result with suppressed near-duplicates.
    public static List<PlacementCandidate> SelectSeparated(
        IEnumerable<PlacementCandidate> ranked, int limit, float separation)
    {
        var selected = new List<PlacementCandidate>(limit);
        double separationSquared = (double)separation * separation;
        foreach (var candidate in ranked)
        {
            bool distinct = true;
            foreach (var existing in selected)
            {
                double dx = (double)candidate.X - existing.X;
                double dy = (double)candidate.Y - existing.Y;
                if (dx * dx + dy * dy < separationSquared)
                {
                    distinct = false;
                    break;
                }
            }
            if (!distinct) continue;
            selected.Add(candidate);
            if (selected.Count >= limit) break;
        }
        return selected;
    }
}
