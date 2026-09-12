using System;
using System.Collections.Generic;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2Cpp;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Behaviors;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using SimulationAttack = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Attack;
using SimulationTower = Il2CppAssets.Scripts.Simulation.Towers.Tower;
using SimulationTargetSupplier = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Behaviors.TargetSupplier;
using SimulationPathSupplier = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Behaviors.PathSupplier;
using SimulationHeliMovement = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.HeliMovement;

namespace AgentBridge;
public sealed partial class AgentBridgeMod
{
    private const int MicroDiscoveryLimit = 64;

    private sealed class MicroTargetSlot
    {
        public int Index;
        public string Name = "";
        public string Kind = "";
        public string TargetTypeId = "";
        public SimulationTargetSupplier Supplier = null!;
        public TargetLeftHand? Left;
        public TargetRightHand? Right;
        public TargetSelectedPoint? SelectedPoint;
        public CenterElipsePattern? Center;
        public LockInPlaceSetting? Lock;
        public PatrolPointsSetting? Patrol;
        public string? ReadbackError;
    }

    private static BridgeResultV1 HandleInspectTowerMicro(BridgeRequestV1 request)
    {
        if (!TryGetMicroTower(request, out var tower, out var error))
            return error!;

        return SuccessResult(request, BuildTowerMicroInspection(tower!));
    }

    private static BridgeResultV1 HandleSetTargetPriorityMicro(BridgeRequestV1 request)
    {
        if (!TryGetMicroTower(request, out var tower, out var error))
            return error!;

        string towerId = ReadString(request.Payload, "towerId", "");
        string requestedPriority = ReadString(request.Payload, "priority", "").Trim();
        if (requestedPriority.Length == 0)
            return ErrorResult(request, "INVALID_TARGET_PRIORITY", "priority must be provided.", false);

        if (!TryReadOptionalMicroIndex(request.Payload, "targetIndex", out int targetIndex, out bool hasTargetIndex))
            return ErrorResult(request, "INVALID_TARGET_INDEX", "targetIndex must be a non-negative integer when provided.", false);

        var simTower = tower!.GetSimTower();
        if (simTower == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation tower unavailable.", true);

        bool? switchingLocked = SafeTargetSwitchingLocked(tower);
        if (!switchingLocked.HasValue)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Native target switching state is unavailable.", true);
        if (switchingLocked.Value)
            return ErrorResult(request, "TARGET_SWITCHING_LOCKED", $"Tower '{towerId}' cannot change target priority under the native game rules.", false);

        var supported = GetNativeTargetTypes(tower);
        string? priority = ResolveNativePriority(requestedPriority, supported, tower.Def?.baseId == "SpikeFactory");
        if (priority == null)
            return ErrorResult(request, "UNSUPPORTED_TARGET_PRIORITY", $"Target priority '{requestedPriority}' is not supported by tower '{towerId}'.", false);

        var armSlots = GetMicroArmSlots(simTower);
        if (armSlots.Count > 0 && !hasTargetIndex)
            return ErrorResult(request, "AMBIGUOUS_TARGET", "This tower has independent arms; provide targetIndex from inspect_tower_micro.", false);
        if (hasTargetIndex)
        {
            if (targetIndex < 0 || targetIndex >= armSlots.Count)
                return ErrorResult(request, "INVALID_TARGET_INDEX", $"targetIndex {targetIndex} does not identify an independent target arm.", false);

            var slot = armSlots[targetIndex];
            string current = GetSlotTargetTypeId(slot);
            string? otherPriority = GetOtherArmPriority(slot);
            if (priority != current && string.Equals(priority, otherPriority, StringComparison.OrdinalIgnoreCase))
                return ErrorResult(request, "UNSUPPORTED_TARGET_PRIORITY", "Native Robo arms must use different priorities; this priority belongs to the other arm.", false);
            int attempts = Math.Max(1, supported.Count + 1);
            try
            {
                for (int i = 0; i < attempts && !string.Equals(current, priority, StringComparison.OrdinalIgnoreCase); i++)
                {
                    if (slot.Left != null)
                        slot.Left.SetNextTargetType(false);
                    else if (slot.Right != null)
                        slot.Right.SetNextTargetType(false);
                    else
                        return ErrorResult(request, "UNSUPPORTED_TARGET_ARM", "The selected target arm has no native target-priority action.", false);
                    current = GetSlotTargetTypeId(slot);
                }
            }
            catch (Exception ex)
            {
                return ErrorResult(request, "SET_TARGET_PRIORITY_FAILED", $"Failed to set target priority for tower '{towerId}' arm {targetIndex}: {ex.Message}", false);
            }

            if (!string.Equals(current, priority, StringComparison.OrdinalIgnoreCase))
                return ErrorResult(request, "TARGET_PRIORITY_NOT_APPLIED", $"Native readback did not reach target priority '{priority}' for arm {targetIndex}.", false);

            return SuccessResult(request, new
            {
                TowerId = towerId,
                TargetPriority = current,
                TargetIndex = targetIndex,
                Micro = BuildTowerMicroInspection(tower)
            });
        }

        var bridge = InGame.instance?.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        string currentTowerPriority = GetTowerTargetTypeId(tower);
        int maxAttempts = Math.Max(1, supported.Count + 1);
        try
        {
            bool previousImmediate = bridge.IsImmediateMode;
            bridge.IsImmediateMode = true;
            try
            {
                for (int i = 0; i < maxAttempts && !string.Equals(currentTowerPriority, priority, StringComparison.OrdinalIgnoreCase); i++)
                {
                    bridge.SetNextTowerTargetType(bridge.GetInputId(), tower.Id, false);
                    currentTowerPriority = GetTowerTargetTypeId(tower);
                }
            }
            finally
            {
                bridge.IsImmediateMode = previousImmediate;
            }
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SET_TARGET_PRIORITY_FAILED", $"Failed to set target priority for tower '{towerId}': {ex.Message}", false);
        }

        if (!string.Equals(currentTowerPriority, priority, StringComparison.OrdinalIgnoreCase))
            return ErrorResult(request, "TARGET_PRIORITY_NOT_APPLIED", $"Native readback did not reach target priority '{priority}'.", false);

        return SuccessResult(request, new
        {
            TowerId = towerId,
            TargetPriority = currentTowerPriority,
            Micro = BuildTowerMicroInspection(tower)
        });
    }

    private static BridgeResultV1 HandleSetTowerTargetPositionMicro(BridgeRequestV1 request)
    {
        if (!TryGetMicroTower(request, out var tower, out var error))
            return error!;

        string towerId = ReadString(request.Payload, "towerId", "");
        float x = ReadFloat(request.Payload, "x", float.NaN);
        float y = ReadFloat(request.Payload, "y", float.NaN);
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
            return ErrorResult(request, "INVALID_TARGET_POSITION", "x and y must be finite numbers.", false);

        if (!TryReadOptionalMicroIndex(request.Payload, "targetIndex", out int targetIndex, out bool hasTargetIndex))
            return ErrorResult(request, "INVALID_TARGET_INDEX", "targetIndex must be a non-negative integer when provided.", false);

        var simTower = tower!.GetSimTower();
        if (simTower == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation tower unavailable.", true);

        var coordinateSlots = GetMicroCoordinateSlots(simTower, tower);
        if (coordinateSlots.Count == 0)
            return ErrorResult(request, "UNSUPPORTED_TARGET_TOWER", $"Tower '{towerId}' has no native coordinate target action.", false);
        if (hasTargetIndex && (targetIndex < 0 || targetIndex >= coordinateSlots.Count))
            return ErrorResult(request, "INVALID_TARGET_INDEX", $"targetIndex {targetIndex} does not identify a coordinate target on tower '{towerId}'.", false);
        if (!hasTargetIndex && coordinateSlots.Count != 1)
            return ErrorResult(request, "AMBIGUOUS_TARGET", $"Tower '{towerId}' has {coordinateSlots.Count} coordinate targets; targetIndex is required.", false);

        var slot = hasTargetIndex ? coordinateSlots[targetIndex] : coordinateSlots[0];
        var requestedPoint = new Il2CppAssets.Scripts.Simulation.SMath.Vector2(x, y);
        Il2CppStructArray<Il2CppAssets.Scripts.Simulation.SMath.Vector2>? requestedPatrolPoints = null;
        var bridge = InGame.instance!.bridge;
        if (bridge == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
        bool previousImmediate = bridge.IsImmediateMode;
        try
        {
            // TargetSelectedPoint exposes the native attack-range legality check.
            // Run it before ApplyTargetTypeData so an illegal Engineer target
            // cannot partially mutate simulation state.
            if (slot.SelectedPoint != null && !slot.SelectedPoint.IsPointInRangeOfAttack(requestedPoint))
                return ErrorResult(request, "TARGET_POSITION_OUT_OF_RANGE", $"Native target legality rejected ({x}, {y}) for tower '{towerId}'.", false);

            bridge.IsImmediateMode = true;
            if (slot.Patrol != null)
            {
                if (!TryBuildPatrolPoints(request.Payload, slot.Patrol, x, y, out requestedPatrolPoints, out string? pointsError))
                    return ErrorResult(request, "INVALID_TARGET_POINTS", pointsError ?? "Invalid patrol points.", false);
                bridge.ApplyTargetTypeData(bridge.GetInputId(), tower.Id, tower.TargetType, requestedPatrolPoints!);
            }
            else if (slot.Center != null || slot.Lock != null)
            {
                bridge.ApplyTargetTypeData(bridge.GetInputId(), tower.Id, tower.TargetType, requestedPoint);
            }
            else
            {
                slot.SelectedPoint!.ApplyTargetTypeData(requestedPoint);
            }
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SET_TARGET_POSITION_FAILED", $"Failed to apply target position for tower '{towerId}': {ex.Message}", false);
        }
        finally { bridge.IsImmediateMode = previousImmediate; }

        var readback = ReadMicroSlotPosition(slot);
        bool readbackMatches;
        if (slot.Patrol != null)
        {
            readbackMatches = requestedPatrolPoints != null && PatrolPointsMatch(slot.Patrol, requestedPatrolPoints);
        }
        else
        {
            readbackMatches = readback != null
                && Math.Abs(readback.X - x) < 0.01f
                && Math.Abs(readback.Y - y) < 0.01f;
        }
        if (!readbackMatches)
            return ErrorResult(request, "TARGET_POSITION_NOT_APPLIED", $"Native readback did not retain requested target position for tower '{towerId}'.", false,
                new { Requested = new Position2DV1 { X = x, Y = y }, Readback = readback, slot.ReadbackError, Micro = BuildTowerMicroInspection(tower) });
        if (slot.Patrol != null)
        {
            int pointIndex = request.Payload.TryGetProperty("pointIndex", out var selectedIndex) ? selectedIndex.GetInt32() : 0;
            var point = slot.Patrol.patrolPoints[pointIndex];
            readback = new Position2DV1 { X = point.x, Y = point.y };
        }

        return SuccessResult(request, new
        {
            TowerId = towerId,
            TargetPosition = readback,
            TargetIndex = hasTargetIndex ? targetIndex : (int?)null,
            Micro = BuildTowerMicroInspection(tower)
        });
    }
    private static bool TryGetMicroTower(BridgeRequestV1 request, out TowerToSimulation? tower, out BridgeResultV1? error)
    {
        tower = null;
        error = null;
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
        {
            error = ErrorResult(request, "NO_ACTIVE_GAME", "Tower micro requires an active match.", false);
            return false;
        }

        string towerId = ReadString(request.Payload, "towerId", "");
        if (towerId.Length == 0)
        {
            error = ErrorResult(request, "INVALID_TOWER_ID", "towerId must be provided.", false);
            return false;
        }

        try
        {
            var allTowers = inGame.GetAllTowerToSim();
            if (allTowers != null)
            {
                foreach (var candidate in allTowers)
                {
                    if (candidate != null && candidate.Id.ToString() == towerId)
                    {
                        tower = candidate;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ErrorResult(request, "SIMULATION_UNAVAILABLE", $"Could not enumerate simulation towers: {ex.Message}", true);
            return false;
        }

        if (tower == null)
        {
            error = ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' not found.", false);
            return false;
        }
        return true;
    }
    private static List<string> GetNativeTargetTypes(TowerToSimulation tower)
    {
        var result = new List<string>();
        try
        {
            var targetTypes = tower.Def?.targetTypes;
            if (targetTypes != null)
            {
                for (int i = 0; i < targetTypes.Length && i < MicroDiscoveryLimit; i++)
                {
                    string id = targetTypes[i]?.id ?? "";
                    if (id.Length > 0 && ContainsMicroTargetType(result, id) == false)
                        result.Add(id);
                }
            }
        }
        catch { }

        string current = GetTowerTargetTypeId(tower);
        if (current.Length > 0 && ContainsMicroTargetType(result, current) == false)
            result.Add(current);
        return result;
    }

    private static bool ContainsMicroTargetType(List<string> values, string candidate)
    {
        foreach (string value in values)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? ResolveNativePriority(string requested, List<string> supported, bool spikeFactory)
    {
        foreach (string candidate in supported)
            if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                return candidate;

        if (spikeFactory)
        {
            string? nativeId = requested.ToLowerInvariant() switch
            {
                "normal" => "Track",
                "close" => "CloseTrack",
                "far" => "FarTrack",
                "smart" => "SmartTrack",
                _ => null
            };
            // A display label never manufactures an upgrade-locked mode.
            if (nativeId != null)
                foreach (string candidate in supported)
                    if (string.Equals(candidate, nativeId, StringComparison.OrdinalIgnoreCase))
                        return candidate;
        }
        return null;
    }

    private static string GetTowerTargetTypeId(TowerToSimulation tower) => tower.TargetType?.id ?? "";
    private static string? GetOtherArmPriority(MicroTargetSlot slot) =>
        slot.Left != null ? slot.Left.rightHand?.targetType?.id : slot.Right?.leftHand?.targetType?.id;


    private static string GetSlotTargetTypeId(MicroTargetSlot slot)
    {
        try
        {
            var left = slot.Supplier.TryCast<TargetLeftHand>();
            if (left != null) return left.targetType?.id ?? "";
            var right = slot.Supplier.TryCast<TargetRightHand>();
            if (right != null) return right.targetType?.id ?? "";
            var path = slot.Supplier.TryCast<SimulationPathSupplier>();
            if (path != null) return path.targetType?.id ?? "";
        }
        catch { }
        return slot.TargetTypeId;
    }
    private static List<MicroTargetSlot> GetMicroArmSlots(SimulationTower simTower)
    {
        var result = new List<MicroTargetSlot>();
        foreach (var supplier in EnumerateMicroTargetSuppliers(simTower))
        {
            if (result.Count >= MicroDiscoveryLimit) break;
            var left = supplier.TryCast<TargetLeftHand>();
            var right = supplier.TryCast<TargetRightHand>();
            if (left == null && right == null) continue;
            result.Add(new MicroTargetSlot
            {
                Index = result.Count,
                Name = SafeSupplierName(supplier),
                Kind = left != null ? "robo_left_arm" : "robo_right_arm",
                TargetTypeId = GetSupplierTargetTypeId(supplier),
                Supplier = supplier,
                Left = left,
                Right = right
            });
        }
        return result;
    }

    private static List<MicroTargetSlot> GetMicroCoordinateSlots(SimulationTower simTower, TowerToSimulation tower)
    {
        var result = new List<MicroTargetSlot>();
        var currentTargetType = GetTowerTargetTypeId(tower);
        foreach (var supplier in EnumerateMicroTargetSuppliers(simTower))
        {
            if (result.Count >= MicroDiscoveryLimit) break;
            MicroTargetSlot? slot = null;
            string supplierTargetType = GetSupplierTargetTypeId(supplier);
            var patrol = supplier.TryCast<PatrolPointsSetting>();
            var locked = supplier.TryCast<LockInPlaceSetting>();
            var center = supplier.TryCast<CenterElipsePattern>();
            var selected = supplier.TryCast<TargetSelectedPoint>();
            if (patrol != null)
            {
                if (supplierTargetType.Length == 0 || string.Equals(supplierTargetType, currentTargetType, StringComparison.OrdinalIgnoreCase))
                    slot = new MicroTargetSlot { Name = SafeSupplierName(supplier), Kind = "heli_patrol", Patrol = patrol };
            }
            else if (locked != null)
            {
                if (supplierTargetType.Length == 0 || string.Equals(supplierTargetType, currentTargetType, StringComparison.OrdinalIgnoreCase))
                    slot = new MicroTargetSlot { Name = SafeSupplierName(supplier), Kind = "heli_lock", Lock = locked };
            }
            else if (center != null)
            {
                if (supplierTargetType.Length == 0 || string.Equals(supplierTargetType, currentTargetType, StringComparison.OrdinalIgnoreCase))
                    slot = new MicroTargetSlot { Name = SafeSupplierName(supplier), Kind = "ace_center", Center = center };
            }
            else if (selected != null)
            {
                slot = new MicroTargetSlot { Name = SafeSupplierName(supplier), Kind = "selected_point", SelectedPoint = selected };
            }

            if (slot == null) continue;
            slot.Index = result.Count;
            slot.Supplier = supplier;
            slot.TargetTypeId = supplierTargetType;
            result.Add(slot);
        }

        return result;
    }

    private static List<SimulationTargetSupplier> EnumerateMicroTargetSuppliers(SimulationTower simTower)
    {
        var result = new List<SimulationTargetSupplier>();
        var seen = new HashSet<IntPtr>();
        void AddSupplier(SimulationTargetSupplier? supplier)
        {
            if (supplier != null && result.Count < MicroDiscoveryLimit && seen.Add(supplier.Pointer))
                result.Add(supplier);
        }
        foreach (var behavior in EnumerateMicroTowerBehaviors(simTower))
        {
            var attack = behavior.TryCast<SimulationAttack>();
            if (attack?.attackBehaviors != null)
                for (int i = 0; i < attack.attackBehaviors.Count && i < MicroDiscoveryLimit; i++)
                    AddSupplier(attack.attackBehaviors[i]?.TryCast<SimulationTargetSupplier>());
            var ace = behavior.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.PathMovement>();
            if (ace?.pathSuppliers != null)
                for (int i = 0; i < ace.pathSuppliers.Count && i < MicroDiscoveryLimit; i++)
                    AddSupplier(ace.pathSuppliers[i]);
            var heli = behavior.TryCast<SimulationHeliMovement>();
            if (heli?.pathSuppliers != null)
                for (int i = 0; i < heli.pathSuppliers.Count && i < MicroDiscoveryLimit; i++)
                    AddSupplier(heli.pathSuppliers[i]);
        }
        return result;
    }

    private static IEnumerable<Il2CppAssets.Scripts.Simulation.Objects.RootBehavior> EnumerateMicroTowerBehaviors(SimulationTower simTower)
    {
        var pending = new List<Il2CppAssets.Scripts.Simulation.Objects.RootBehavior>();
        var seen = new HashSet<IntPtr>();
        void AddBehavior(Il2CppAssets.Scripts.Simulation.Objects.RootBehavior? behavior)
        {
            if (behavior != null && pending.Count < 256 && seen.Add(behavior.Pointer))
                pending.Add(behavior);
        }
        var roots = simTower.Behaviors?.list;
        if (roots == null) yield break;
        for (int i = 0; i < roots.Count && i < 256; i++)
            AddBehavior(roots[i]?.TryCast<Il2CppAssets.Scripts.Simulation.Objects.RootBehavior>());
        for (int i = 0; i < pending.Count; i++)
        {
            var behavior = pending[i];
            yield return behavior;
            // Ace/Heli movement and attacks can belong to the air unit rather
            // than the tower root. Native pointer identity prevents duplicates.
            var air = behavior.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.AirUnit>();
            if (air?.modelBehaviors != null)
                for (int j = 0; j < air.modelBehaviors.Count && j < 256; j++) AddBehavior(air.modelBehaviors[j]);
        }
    }

    private static string GetSupplierTargetTypeId(SimulationTargetSupplier supplier)
    {
        try
        {
            var path = supplier.TryCast<SimulationPathSupplier>();
            // PathSupplier inherits an attack targetType (often First); its
            // native name identifies the movement option selected by the tower.
            if (path != null) return path.GetName() ?? "";
            var left = supplier.TryCast<TargetLeftHand>();
            if (left != null) return left.targetType?.id ?? "";
            var right = supplier.TryCast<TargetRightHand>();
            if (right != null) return right.targetType?.id ?? "";
        }
        catch { }
        return "";
    }

    private static string SafeSupplierName(SimulationTargetSupplier supplier)
    {
        try { return supplier.GetName() ?? supplier.GetType().Name; }
        catch { return supplier.GetType().Name; }
    }

    private static Dictionary<string, object?> BuildTowerMicroInspection(TowerToSimulation tower)
    {
        var simTower = tower.GetSimTower();
        var supported = GetNativeTargetTypes(tower);
        string current = GetTowerTargetTypeId(tower);
        var options = new List<object>();
        try
        {
            var targetTypes = tower.Def?.targetTypes;
            if (targetTypes != null)
            {
                for (int i = 0; i < targetTypes.Length && i < MicroDiscoveryLimit; i++)
                {
                    var targetType = targetTypes[i];
                    if (targetType == null) continue;
                    options.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["id"] = targetType.id,
                        ["isActionable"] = targetType.isActionable,
                        ["actionOnCreate"] = targetType.actionOnCreate,
                        ["intId"] = targetType.intID,
                        ["continueToNextPriority"] = targetType.continueToNextPriority,
                        ["active"] = string.Equals(targetType.id, current, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        catch { }

        var arms = new List<object>();
        var coordinateTargets = new List<object>();
        var pathModes = new List<object>();
        if (simTower != null)
        {
            foreach (var arm in GetMicroArmSlots(simTower))
            {
                string? otherPriority = GetOtherArmPriority(arm);
                arms.Add(new Dictionary<string, object?>
                {
                    ["index"] = arm.Index,
                    ["name"] = arm.Name,
                    ["kind"] = arm.Kind,
                    ["targetPriority"] = GetSlotTargetTypeId(arm),
                    ["targetTypeId"] = arm.TargetTypeId,
                    ["supportedTargetTypes"] = supported.FindAll(id => !string.Equals(id, otherPriority, StringComparison.OrdinalIgnoreCase))
                });
            }

            int attackPathCount = 0;
            foreach (var supplier in EnumerateMicroTargetSuppliers(simTower))
            {
                if (supplier.TryCast<SimulationPathSupplier>() == null) continue;
                if (attackPathCount++ >= MicroDiscoveryLimit || pathModes.Count >= MicroDiscoveryLimit) break;
                pathModes.Add(new Dictionary<string, object?>
                {
                    ["name"] = SafeSupplierName(supplier),
                    ["kind"] = MicroPathKind(supplier),
                    ["type"] = supplier.GetIl2CppType().FullName,
                    ["targetTypeId"] = GetSupplierTargetTypeId(supplier),
                    ["active"] = string.Equals(GetSupplierTargetTypeId(supplier), current, StringComparison.OrdinalIgnoreCase)
                });
            }


            foreach (var slot in GetMicroCoordinateSlots(simTower, tower))
            {
                coordinateTargets.Add(new Dictionary<string, object?>
                {
                    ["index"] = slot.Index,
                    ["name"] = slot.Name,
                    ["kind"] = slot.Kind,
                    ["targetTypeId"] = slot.TargetTypeId,
                    ["position"] = ReadMicroSlotPosition(slot),
                    ["patrolPoints"] = ReadMicroPatrolPoints(slot)
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["source"] = "active-simulation",
            ["towerId"] = tower.Id.ToString(),
            ["towerType"] = tower.Def?.baseId,
            ["targetPriority"] = current,
            ["targetTypeSwitchingLocked"] = SafeTargetSwitchingLocked(tower),
            ["supportedTargetTypes"] = supported,
            ["targetTypeOptions"] = options,
            ["arms"] = arms,
            ["pathModes"] = pathModes,
            ["coordinateTargets"] = coordinateTargets
        };
    }

    private static bool? SafeTargetSwitchingLocked(TowerToSimulation tower)
    {
        try { return tower.IsTargetTypeSwitchingLocked; }
        catch { return null; }
    }

    private static string MicroPathKind(SimulationTargetSupplier supplier)
    {
        string type = supplier.GetIl2CppType().Name;
        string name = supplier.GetName() ?? "";
        if (name == "FigureInfinite") return "ace_infinite";
        if (name == "LockInPlace") return "heli_lock";
        if (name == "FollowTouch") return "heli_follow_touch";
        if (type.Contains("Circle", StringComparison.OrdinalIgnoreCase)) return "ace_circle";
        if (type.Contains("FigureEight", StringComparison.OrdinalIgnoreCase)) return "ace_figure_eight";
        if (type.Contains("Wingmonkey", StringComparison.OrdinalIgnoreCase)) return "ace_wingmonkey";
        if (type.Contains("ScreenCenter", StringComparison.OrdinalIgnoreCase)) return "ace_infinite";
        if (type.Contains("Center", StringComparison.OrdinalIgnoreCase) || type.Contains("Elipse", StringComparison.OrdinalIgnoreCase)) return "ace_center";
        if (type.Contains("Patrol", StringComparison.OrdinalIgnoreCase)) return "heli_patrol";
        if (type.Contains("Pursuit", StringComparison.OrdinalIgnoreCase)) return "heli_pursuit";
        return "native_path_supplier";
    }

    private static Position2DV1? ReadMicroSlotPosition(MicroTargetSlot slot)
    {
        try
        {
            if (slot.SelectedPoint != null)
            {
                if (!slot.SelectedPoint.hasValidPoint) return null;
                var point = slot.SelectedPoint.targetPoint;
                return new Position2DV1 { X = point.x, Y = point.y };
            }
            if (slot.Lock != null || slot.Center != null)
            {
                // IL2CPP boxes Nullable<Vector2> as Vector2 (or null), not as
                // Nullable<Vector2>. Read the live field using checked unboxing;
                // the UI input DTO may intentionally omit its previous position.
                var selected = slot.Center != null ? slot.Center.selectedPoint : slot.Lock!.lockedPosition;
                if (selected == null) return null;
                var point = selected.Unbox<Il2CppAssets.Scripts.Simulation.SMath.Vector2>();
                return new Position2DV1 { X = point.x, Y = point.y };
            }
            if (slot.Patrol != null && slot.Patrol.patrolPoints != null && slot.Patrol.patrolPoints.Length > 0)
            {
                var point = slot.Patrol.patrolPoints[0];
                return new Position2DV1 { X = point.x, Y = point.y };
            }
        }
        catch (Exception ex) { slot.ReadbackError = ex.ToString(); }
        return null;
    }

    private static List<object>? ReadMicroPatrolPoints(MicroTargetSlot slot)
    {
        if (slot.Patrol == null) return null;
        try
        {
            var points = new List<object>();
            if (slot.Patrol.patrolPoints == null) return points;
            int pointCount = 0;
            foreach (var point in slot.Patrol.patrolPoints)
            {
                if (pointCount++ >= MicroDiscoveryLimit) break;
                points.Add(new Dictionary<string, object?> { ["x"] = point.x, ["y"] = point.y });
            }
            return points;
        }
        catch { return null; }
    }

    private static bool TryBuildPatrolPoints(JsonElement payload, PatrolPointsSetting patrol, float x, float y, out Il2CppStructArray<Il2CppAssets.Scripts.Simulation.SMath.Vector2>? points, out string? error)
    {
        points = null;
        error = null;
        try
        {
            int nativeCount = PatrolPointsSetting.NUMPOINTS;
            if (nativeCount <= 0 || nativeCount > MicroDiscoveryLimit)
            {
                error = "Native patrol point count is unavailable or exceeds the bridge safety bound.";
                return false;
            }

            if (payload.TryGetProperty("points", out var inputPoints))
            {
                if (payload.TryGetProperty("pointIndex", out _))
                {
                    error = "Provide points or pointIndex, not both.";
                    return false;
                }
                if (inputPoints.ValueKind != JsonValueKind.Array || inputPoints.GetArrayLength() != nativeCount)
                {
                    error = $"points must contain exactly {nativeCount} native patrol points.";
                    return false;
                }
                points = new Il2CppStructArray<Il2CppAssets.Scripts.Simulation.SMath.Vector2>(nativeCount);
                int index = 0;
                foreach (var point in inputPoints.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Object)
                    {
                        error = "Each patrol point must be an object with finite x and y.";
                        return false;
                    }
                    float pointX = ReadFloat(point, "x", float.NaN);
                    float pointY = ReadFloat(point, "y", float.NaN);
                    if (float.IsNaN(pointX) || float.IsNaN(pointY) || float.IsInfinity(pointX) || float.IsInfinity(pointY))
                    {
                        error = "Each patrol point must contain finite x and y.";
                        return false;
                    }
                    points[index++] = new Il2CppAssets.Scripts.Simulation.SMath.Vector2(pointX, pointY);
                }
                return true;
            }

            var existing = patrol.patrolPoints;
            points = new Il2CppStructArray<Il2CppAssets.Scripts.Simulation.SMath.Vector2>(nativeCount);
            for (int i = 0; i < nativeCount; i++)
                points[i] = existing != null && i < existing.Length ? existing[i] : new Il2CppAssets.Scripts.Simulation.SMath.Vector2(x, y);

            int pointIndex = 0;
            if (payload.TryGetProperty("pointIndex", out var pointIndexElement) &&
                (pointIndexElement.ValueKind != JsonValueKind.Number || !pointIndexElement.TryGetInt32(out pointIndex) || pointIndex < 0 || pointIndex >= nativeCount))
            {
                error = "pointIndex must identify an existing native patrol point.";
                return false;
            }
            points[pointIndex] = new Il2CppAssets.Scripts.Simulation.SMath.Vector2(x, y);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool PatrolPointsMatch(PatrolPointsSetting patrol, Il2CppStructArray<Il2CppAssets.Scripts.Simulation.SMath.Vector2> expected)
    {
        try
        {
            var actual = patrol.patrolPoints;
            if (actual == null || actual.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (Math.Abs(actual[i].x - expected[i].x) >= 0.01f || Math.Abs(actual[i].y - expected[i].y) >= 0.01f)
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadOptionalMicroIndex(JsonElement payload, string name, out int value, out bool present)
    {
        value = 0;
        present = payload.TryGetProperty(name, out var element);
        if (!present) return true;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= 0;
    }
}
