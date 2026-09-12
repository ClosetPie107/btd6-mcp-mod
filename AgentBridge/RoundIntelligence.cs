using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Il2Cpp;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static BridgeResultV1 HandleRoundInfo(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot get round info: no active match.", false);

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        int targetRound = bridge.GetCurrentRound() + 1;
        if (request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("round", out var roundProp) &&
            roundProp.ValueKind == JsonValueKind.Number &&
            roundProp.TryGetInt32(out var r))
        {
            targetRound = r;
        }

        var roundSet = gameModel.roundSet;
        if (roundSet?.rounds == null || targetRound < 1 || targetRound > roundSet.rounds.Length)
        {
            return ErrorResult(request, "ROUND_OUT_OF_RANGE",
                $"Round {targetRound} is out of range (1 - {roundSet?.rounds?.Length ?? 0}).", false);
        }

        var roundModel = roundSet.rounds[targetRound - 1];
        var groups = new List<object>();
        bool hasCamo = false;
        bool hasLead = false;
        bool hasFortified = false;
        bool hasMoab = false;
        bool hasBfb = false;
        bool hasZomg = false;
        bool hasDdt = false;
        bool hasBad = false;
        bool hasCeramic = false;
        bool hasPurple = false;
        bool hasRegrow = false;
        bool requiresLeadPopping = false;
        int totalBloons = 0;
        float maxEndTime = 0f;

        if (roundModel.groups != null)
        {
            foreach (var group in roundModel.groups)
            {
                if (group == null) continue;
                var bloonModel = gameModel.GetBloon(group.bloon);
                bool isCamo = (bloonModel != null && bloonModel.isCamo) || group.bloon.Contains("Camo", StringComparison.OrdinalIgnoreCase);
                bool isFortified = (bloonModel != null && bloonModel.isFortified) || group.bloon.Contains("Fortified", StringComparison.OrdinalIgnoreCase);
                bool isGrow = (bloonModel != null && bloonModel.isGrow) || group.bloon.Contains("Regrow", StringComparison.OrdinalIgnoreCase);
                bool isMoab = bloonModel != null && bloonModel.isMoab;
                string baseId = bloonModel?.baseId ?? group.bloon;

                bool isLead = baseId.Contains("Lead", StringComparison.OrdinalIgnoreCase) ||
                              (bloonModel?.tags != null && bloonModel.tags.Any(t => t.Equals("Lead", StringComparison.OrdinalIgnoreCase)));
                bool isCeramic = baseId.Contains("Ceramic", StringComparison.OrdinalIgnoreCase);
                bool isPurple = baseId.Contains("Purple", StringComparison.OrdinalIgnoreCase) ||
                               (bloonModel?.tags != null && bloonModel.tags.Any(t => t.Equals("Purple", StringComparison.OrdinalIgnoreCase)));
                bool isBfb = baseId.Contains("Bfb", StringComparison.OrdinalIgnoreCase);
                bool isZomg = baseId.Contains("Zomg", StringComparison.OrdinalIgnoreCase);
                bool isDdt = baseId.Contains("Ddt", StringComparison.OrdinalIgnoreCase);
                bool isBad = baseId.Contains("Bad", StringComparison.OrdinalIgnoreCase);
                // BloonProperties.Lead is the native damage-immunity/property bit. It
                // covers DDTs without changing the existing Lead identity semantics.
                bool requiresLead = bloonModel != null && (bloonModel.bloonProperties & BloonProperties.Lead) != 0;

                if (isCamo) hasCamo = true;
                if (isLead) hasLead = true;
                if (isFortified) hasFortified = true;
                if (isGrow) hasRegrow = true;
                if (isMoab) hasMoab = true;
                if (isBfb) hasBfb = true;
                if (isZomg) hasZomg = true;
                if (isDdt) hasDdt = true;
                if (isBad) hasBad = true;
                if (isCeramic) hasCeramic = true;
                if (isPurple) hasPurple = true;
                if (requiresLead) requiresLeadPopping = true;

                totalBloons += group.count;
                if (group.end > maxEndTime) maxEndTime = group.end;

                groups.Add(new
                {
                    Bloon = group.bloon,
                    BaseType = baseId,
                    Count = group.count,
                    // RoundModel stores schedule values as 60 Hz simulation frames.
                    StartSeconds = Math.Round(group.start / 60d, 2),
                    EndSeconds = Math.Round(group.end / 60d, 2),
                    IsCamo = isCamo,
                    IsLead = isLead,
                    RequiresLeadPopping = requiresLead,
                    IsFortified = isFortified,
                    IsRegrow = isGrow,
                    IsMoab = isMoab
                });
            }
        }

        var activePaths = new List<int>();
        try
        {
            var map = bridge.Simulation?.Map;
            var spawner = map?.spawner;
            var paths = map?.pathManager?.paths;
            if (spawner != null && paths != null)
            {
                var pathIndices = new Dictionary<IntPtr, int>();
                for (int i = 0; i < paths.Count; i++)
                {
                    var p = paths[i];
                    if (p != null) pathIndices[p.Pointer] = i;
                }

                var spawnPaths = spawner.GetSpawnPathsForRound(targetRound);
                if (spawnPaths != null)
                {
                    foreach (var sp in spawnPaths)
                    {
                        if (sp != null && pathIndices.TryGetValue(sp.Pointer, out int idx) && !activePaths.Contains(idx))
                        {
                            activePaths.Add(idx);
                        }
                    }
                }
            }
        }
        catch { }

        if (activePaths.Count == 0)
        {
            try
            {
                var paths = bridge.Simulation?.Map?.pathManager?.paths;
                if (paths != null)
                {
                    for (int i = 0; i < paths.Count; i++)
                    {
                        if (paths[i]?.isActive == true && paths[i]?.isHidden == false)
                            activePaths.Add(i);
                    }
                }
            }
            catch { }
            if (activePaths.Count == 0) activePaths.Add(0);
        }

        RoutingPolicy? routingPolicy = null;
        TrackTraffic? traffic = null;
        try
        {
            var map = bridge.Simulation?.Map;
            if (map != null)
            {
                var graph = BuildTrackGraph(map, GetMapGeometry(map), targetRound);
                routingPolicy = graph.RoutingPolicy;
                traffic = graph.Traffic;
            }
        }
        catch { }

        return SuccessResult(request, new
        {
            Round = targetRound,
            ActivePaths = activePaths.ToArray(),
            RoutingPolicy = routingPolicy,
            Traffic = traffic,
            TotalBloons = totalBloons,
            // This is the last scheduled spawn window end, not guaranteed round-clear time.
            DurationSeconds = Math.Round(maxEndTime / 60d, 2),
            ThreatIntel = new
            {
                HasCamo = hasCamo,
                HasLead = hasLead,
                RequiresLeadPopping = requiresLeadPopping,
                HasPurple = hasPurple,
                HasCeramic = hasCeramic,
                HasFortified = hasFortified,
                HasRegrow = hasRegrow,
                HasMoab = hasMoab,
                HasBfb = hasBfb,
                HasZomg = hasZomg,
                HasDdt = hasDdt,
                HasBad = hasBad
            },
            Groups = groups
        });
    }
}
