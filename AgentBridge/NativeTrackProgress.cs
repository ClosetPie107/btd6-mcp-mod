using System;
using Il2CppAssets.Scripts.Simulation.Bloons;

namespace AgentBridge;

/// <summary>
/// Reads the game's own playable-track normalization.  The native
/// Bloon.PercThroughMap implementation accounts for the active path's spawn and
/// leak boundaries (including routed paths) rather than treating the path's
/// geometric length as the playable interval.
/// </summary>
internal static class NativeTrackProgress
{
    internal const double NearExitThreshold = 0.85d;
    internal const string ProgressBasis = "native_bloon_PercThroughMap";
    internal const string ProgressBoundary = "native_path_MaxDistUntilSpawn_to_MaxDistUntilLeak";

    /// <summary>
    /// Returns normalized playable-track progress, or false when the native
    /// object/path/progress is unavailable.  Values beyond the native leak
    /// boundary are bounded for consumers that require a [0, 1] progress value;
    /// no geometric or observed-leak fallback is used.
    /// </summary>
    internal static bool TryGetProgress(Bloon? simBloon, out double progress)
    {
        progress = 0d;
        if (simBloon == null) return false;

        float nativeProgress;
        try
        {
            // PercThroughMap dereferences the native path; keep unavailable
            // paths explicitly unknown instead of entering native code.
            if (simBloon.path == null) return false;
            nativeProgress = simBloon.PercThroughMap();
        }
        catch
        {
            return false;
        }

        if (!float.IsFinite(nativeProgress)) return false;
        progress = Math.Clamp((double)nativeProgress, 0d, 1d);
        return true;
    }
}
