using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Simulation.Map;
using Il2CppAssets.Scripts.Simulation.Objects;
using Il2CppAssets.Scripts.Simulation.Track;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private const int MaxObstacleProcessEntries = 32768;
    private const int MaxObstacleResults = 1024;
    private sealed class ObstaclePosition
    {
        public float? X { get; init; }
        public float? Y { get; init; }
        public float? Z { get; init; }
    }

    private sealed class ObstacleSnapshot
    {
        public string Id { get; init; } = "";
        public string NativeType { get; init; } = "";
        public string? Name { get; init; }
        public string? ObjectName { get; init; }
        public string? TextKey { get; init; }
        public string? AreaType { get; init; }
        public bool? DestroyArea { get; init; }
        public ObstaclePosition? Position { get; init; }
        public double? Price { get; set; }
        public double? BasePrice { get; init; }
        public string? PriceSource { get; set; }
        public bool? Present { get; init; }
        public bool? RuntimeActive { get; init; }
        public bool? ModelActive { get; init; }
        public bool? RemovalAvailable { get; init; }
        public Removeable? NativeRemoveable { get; init; }
        public RegenRemovable? NativeRegenRemovable { get; init; }
    }

    private static BridgeResultV1 HandleInspectObstacles(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot inspect obstacles without an active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null || bridge.Simulation == null)
            return ErrorResult(request, "NO_SIMULATION", "Cannot inspect obstacles: the simulation bridge is unavailable.", true);

        var obstacles = ReadObstacleSnapshots(bridge, out bool complete);
        if (!complete)
            return ErrorResult(request, "OBSTACLE_SCAN_FAILED", "The native obstacle list could not be read completely.", true);

        QuoteRegenRemovablePrices(bridge, obstacles);
        return SuccessResult(request, new
        {
            Source = "active-simulation",
            ObservedAtUtc = DateTime.UtcNow,
            MatchGeneration = matchGeneration,
            Obstacles = obstacles
                .OrderBy(obstacle => obstacle.Id, StringComparer.Ordinal)
                .ThenBy(obstacle => obstacle.NativeType, StringComparer.Ordinal)
                .Select(ToObstacleResult)
                .ToArray()
        });
    }

    private static BridgeResultV1 HandleRemoveObstacle(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot remove an obstacle without an active match.", false);

        var bridge = inGame.bridge;
        if (bridge == null || bridge.Simulation == null)
            return ErrorResult(request, "NO_SIMULATION", "Cannot remove an obstacle: the simulation bridge is unavailable.", true);

        if (request.Payload.ValueKind != JsonValueKind.Object ||
            !request.Payload.TryGetProperty("obstacleId", out var idProperty) ||
            idProperty.ValueKind != JsonValueKind.String)
            return ErrorResult(request, "INVALID_ARGUMENT", "obstacleId must be a non-empty string.", false);

        string obstacleId = idProperty.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(obstacleId) || obstacleId.Length > 128)
            return ErrorResult(request, "INVALID_ARGUMENT", "obstacleId must contain 1-128 non-whitespace characters.", false);

        var obstacles = ReadObstacleSnapshots(bridge, out bool complete);
        if (!complete)
            return ErrorResult(request, "OBSTACLE_SCAN_FAILED", "The native obstacle list could not be read completely; no purchase was submitted.", true);

        var matches = obstacles
            .Where(obstacle => string.Equals(obstacle.Id, obstacleId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return ErrorResult(request, "OBSTACLE_NOT_FOUND", $"No obstacle with ID '{obstacleId}' was found; no purchase was submitted.", false);
        if (matches.Length != 1)
            return ErrorResult(request, "OBSTACLE_ID_AMBIGUOUS", $"Obstacle ID '{obstacleId}' identifies {matches.Length} native objects; no purchase was submitted.", false,
                new { ObstacleId = obstacleId, MatchCount = matches.Length });

        var target = matches[0];
        if (target.Present == false)
            return ErrorResult(request, "OBSTACLE_ALREADY_REMOVED", $"Obstacle '{obstacleId}' is already removed; no purchase was submitted.", false,
                new { ObstacleId = obstacleId, Price = target.Price, Submitted = false });
        if (target.RemovalAvailable != true)
            return ErrorResult(request, target.Present == true ? "OBSTACLE_NOT_AVAILABLE" : "OBSTACLE_STATE_UNKNOWN",
                $"Obstacle '{obstacleId}' is not currently removable; no purchase was submitted.", false,
                new { ObstacleId = obstacleId, Price = target.Price, Submitted = false, RemovalAvailable = target.RemovalAvailable });

        int inputId;
        try
        {
            inputId = bridge.GetInputId();
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "INPUT_ID_UNAVAILABLE", $"The native input ID was unavailable; no purchase was submitted: {ex.Message}", true);
        }

        double? cashBefore = TryGetCash(inGame);
        MapInteractable? interactable = null;
        try
        {
            if (target.NativeRemoveable != null)
                interactable = new RemovableToSimulation(bridge, target.NativeRemoveable);
            else if (target.NativeRegenRemovable != null)
                interactable = new RegenRemovableToSimulation(bridge, target.NativeRegenRemovable);
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "OBSTACLE_WRAPPER_FAILED", $"The native obstacle interaction wrapper could not be created; no purchase was submitted: {ex.Message}", false,
                new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, Submitted = false });
        }

        if (interactable == null)
            return ErrorResult(request, "OBSTACLE_TYPE_UNSUPPORTED", $"Obstacle '{obstacleId}' has no supported native interaction wrapper; no purchase was submitted.", false);

        try
        {
            // Regen-removable pricing is formulaic. Read it from the native UI wrapper immediately
            // before the native affordability check; never estimate or write cash locally.
            if (target.NativeRegenRemovable != null && interactable is RegenRemovableToSimulation regenInteractable)
            {
                float nativePrice = regenInteractable.GetRemovalCost();
                if (float.IsFinite(nativePrice) && nativePrice >= 0f)
                {
                    target.Price = nativePrice;
                    target.PriceSource = "native_regen_removable_wrapper";
                }
            }

            if (interactable.IsDisabled())
                return ErrorResult(request, "OBSTACLE_DISABLED", $"Obstacle '{obstacleId}' is disabled by the native game rules; no purchase was submitted.", false,
                    new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, Submitted = false });

            if (!interactable.IsConfirmable(inputId))
            {
                if (target.Price.HasValue && cashBefore.HasValue && cashBefore.Value < target.Price.Value)
                    return ErrorResult(request, "INSUFFICIENT_CASH", $"Cannot remove obstacle '{obstacleId}': costs ${target.Price.Value:F0}, have ${cashBefore.Value:F0}. No purchase was submitted.", false,
                        new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, Submitted = false });

                return ErrorResult(request, "OBSTACLE_NOT_CONFIRMABLE", $"The native game rejected obstacle '{obstacleId}' as not confirmable; no purchase was submitted.", false,
                    new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, Submitted = false });
            }

            long requestGeneration = matchGeneration;
            // This is the same game-owned callback used by the native map-interactable button.
            // Force the native action to run synchronously so a second request cannot queue a
            // duplicate purchase before the simulation processes the first one.
            bool previousImmediate = bridge.IsImmediateMode;
            try
            {
                bridge.IsImmediateMode = true;
                interactable.OnButtonConfirm(inputId);
            }
            finally
            {
                bridge.IsImmediateMode = previousImmediate;
            }

            if (requestGeneration != matchGeneration)
                return ErrorResult(request, "MATCH_CHANGED", "The active match changed while the native obstacle callback was running; charge/removal state is unknown.", false,
                    new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, CashAfter = TryGetCash(inGame), Submitted = true, ChargeStatus = "unknown" });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "OBSTACLE_REMOVAL_FAILED", $"The native obstacle callback failed; charge state is unknown and no retry was submitted: {ex.Message}", false,
                new { ObstacleId = obstacleId, Price = target.Price, CashBefore = cashBefore, CashAfter = TryGetCash(inGame), Submitted = true, ChargeStatus = "unknown" });
        }

        double? cashAfter = TryGetCash(inGame);
        var after = ReadObstacleSnapshots(bridge, out bool verified);
        bool removed = verified && IsRemovedAfterCallback(after, obstacleId);
        var cashDelta = cashBefore.HasValue && cashAfter.HasValue ? cashAfter.Value - cashBefore.Value : (double?)null;

        return SuccessResult(request, new
        {
            ObstacleId = obstacleId,
            NativeType = target.NativeType,
            Price = target.Price,
            PriceSource = target.PriceSource,
            Submitted = true,
            Removed = removed,
            Verification = !verified ? "unavailable" : removed ? "removed" : "pending",
            VerificationComplete = verified,
            CashBefore = cashBefore,
            CashAfter = cashAfter,
            CashDelta = cashDelta,
            Charged = cashDelta.HasValue ? cashDelta.Value < 0d : (bool?)null,
            MatchGeneration = matchGeneration
        });
    }

    private static List<ObstacleSnapshot> ReadObstacleSnapshots(UnityToSimulation bridge, out bool complete)
    {
        var result = new List<ObstacleSnapshot>();
        complete = false;
        try
        {
            var process = bridge.Simulation?.process;
            var processList = process?.list;
            if (processList == null || processList.Count > MaxObstacleProcessEntries)
                return result;

            for (int i = 0; i < processList.Count; i++)
            {
                var root = processList[i];
                if (root == null)
                    continue;

                var removable = root.TryCast<Removeable>();
                if (removable != null)
                {
                    var snapshot = ReadRemoveable(removable);
                    if (snapshot != null)
                        result.Add(snapshot);
                }
                else
                {
                    var regenRemovable = root.TryCast<RegenRemovable>();
                    if (regenRemovable != null)
                    {
                        var snapshot = ReadRegenRemovable(regenRemovable);
                        if (snapshot != null)
                            result.Add(snapshot);
                    }
                }

                if (result.Count > MaxObstacleResults)
                    return result;
            }

            complete = true;
        }
        catch
        {
            // A partial process-list scan must never be used to claim that an obstacle was removed.
            complete = false;
        }

        return result;
    }

    private static ObstacleSnapshot? ReadRemoveable(Removeable removable)
    {
        string? id = TryReadId(removable);
        if (string.IsNullOrEmpty(id))
            return null;

        Il2CppAssets.Scripts.Models.Map.RemoveableModel? model = null;
        try { model = removable.removeableModel; } catch { }

        bool? present = TryReadPresent(removable);
        bool? runtimeActive = null;
        try { runtimeActive = removable.isActive; } catch { }
        bool? modelActive = null;
        try { modelActive = model?.isActive; } catch { }

        var snapshot = new ObstacleSnapshot
        {
            Id = id,
            NativeType = "removeable",
            Name = TryReadString(() => model?.menuName),
            RemovalAvailable = DeriveRemovalAvailability(present, runtimeActive),
            TextKey = TryReadString(() => model?.textKey),
            AreaType = TryReadString(() => model?.defaultType.ToString()),
            DestroyArea = TryReadBool(() => model?.destroyArea),
            Position = TryReadPosition(() => model?.position),
            Price = TryReadNonNegativeDouble(() => model?.removealCost),
            PriceSource = "native_removeable_model.removealCost",
            Present = present,
            RuntimeActive = runtimeActive,
            ModelActive = modelActive,
            NativeRemoveable = removable
        };
        return snapshot;
    }

    private static ObstacleSnapshot? ReadRegenRemovable(RegenRemovable removable)
    {
        string? id = TryReadId(removable);
        if (string.IsNullOrEmpty(id))
            return null;

        Il2CppAssets.Scripts.Models.Map.RegenRemovableModel? model = null;
        try { model = removable.regenRemovableModel; } catch { }

        bool? present = TryReadPresent(removable);
        bool? runtimeActive = null;
        try { runtimeActive = removable.isActive; } catch { }
        bool? modelActive = null;
        try { modelActive = model?.isActive; } catch { }

        double? basePrice = null;
        try
        {
            float nativeBasePrice = model?.costFormula?.defaultCost ?? float.NaN;
            if (float.IsFinite(nativeBasePrice) && nativeBasePrice >= 0f)
                basePrice = nativeBasePrice;
        }
        catch { }

        return new ObstacleSnapshot
        {
            RemovalAvailable = DeriveRemovalAvailability(present, runtimeActive),
            NativeType = "regen_removable",
            Name = TryReadString(() => model?.popupTextLocKey),
            TextKey = TryReadString(() => model?.popupTextLocKey),
            Position = TryReadPosition(() => model?.position),
            BasePrice = basePrice,
            PriceSource = "native_regen_removable_wrapper",
            Present = present,
            RuntimeActive = runtimeActive,
            ModelActive = modelActive,
            NativeRegenRemovable = removable
        };
    }

    private static void QuoteRegenRemovablePrices(UnityToSimulation bridge, List<ObstacleSnapshot> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (obstacle.NativeRegenRemovable == null || obstacle.RemovalAvailable != true)
                continue;

            try
            {
                var interactable = new RegenRemovableToSimulation(bridge, obstacle.NativeRegenRemovable);
                float nativePrice = interactable.GetRemovalCost();
                if (float.IsFinite(nativePrice) && nativePrice >= 0f)
                {
                    obstacle.Price = nativePrice;
                    obstacle.PriceSource = "native_regen_removable_wrapper";
                }
            }
            catch
            {
                // Dynamic native prices remain null rather than being estimated from the model.
            }
        }
    }

    private static object ToObstacleResult(ObstacleSnapshot obstacle) => new
    {
        Id = obstacle.Id,
        NativeType = obstacle.NativeType,
        Name = obstacle.Name,
        ObjectName = obstacle.ObjectName,
        TextKey = obstacle.TextKey,
        Position = obstacle.Position,
        AreaType = obstacle.AreaType,
        DestroyArea = obstacle.DestroyArea,
        Price = obstacle.Price,
        BasePrice = obstacle.BasePrice,
        PriceSource = obstacle.PriceSource,
        Present = obstacle.Present,
        IsActive = obstacle.RuntimeActive,
        ModelActive = obstacle.ModelActive,
        RemovalAvailable = obstacle.RemovalAvailable,
        Status = GetObstacleStatus(obstacle)
    };

    private static string GetObstacleStatus(ObstacleSnapshot obstacle)
    {
        if (obstacle.Present == false)
            return "destroyed";
        if (obstacle.Present == true && obstacle.RuntimeActive == false)
            return "inactive";
        if (obstacle.RemovalAvailable == true)
            return "active";
        if (obstacle.Present == true)
            return "unavailable";
        return "unknown";
    }

    private static bool IsRemovedAfterCallback(List<ObstacleSnapshot> snapshots, string obstacleId)
    {
        var remaining = snapshots
            .Where(obstacle => string.Equals(obstacle.Id, obstacleId, StringComparison.Ordinal))
            .ToArray();
        if (remaining.Length == 0)
            return true;

        // Removeable objects may remain in the process list while inactive. Treat only a
        // destroyed or natively inactive object as removed; an active object means pending.
        return remaining.All(obstacle => obstacle.Present == false || obstacle.RuntimeActive == false);
    }

    private static string? TryReadId(RootObject root)
    {
        try
        {
            string id = root.Id.ToString();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryReadPresent(RootObject root)
    {
        try { return !root.IsDestroyed; }
        catch { return null; }
    }

    private static bool? DeriveRemovalAvailability(bool? present, bool? runtimeActive)
    {
        if (present == false || runtimeActive == false)
            return false;
        if (present == true && runtimeActive == true)
            return true;
        return null;
    }

    private static string? TryReadString(Func<string?> reader)
    {
        try { return reader(); }
        catch { return null; }
    }

    private static bool? TryReadBool(Func<bool?> reader)
    {
        try { return reader(); }
        catch { return null; }
    }

    private static double? TryReadNonNegativeDouble(Func<int?> reader)
    {
        try
        {
            int? value = reader();
            return value.HasValue && value.Value >= 0 ? value.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static ObstaclePosition? TryReadPosition(Func<Il2CppAssets.Scripts.Simulation.SMath.Vector3?> reader)
    {
        try
        {
            var value = reader();
            if (!value.HasValue)
                return null;
            var position = value.Value;
            return new ObstaclePosition
            {
                X = float.IsFinite(position.x) ? position.x : null,
                Y = float.IsFinite(position.y) ? position.y : null,
                Z = float.IsFinite(position.z) ? position.z : null
            };
        }
        catch
        {
            return null;
        }
    }

    private static double? TryGetCash(InGame inGame)
    {
        try { return inGame.GetCash(); }
        catch { return null; }
    }
}
