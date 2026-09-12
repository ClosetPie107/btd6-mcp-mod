using System;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using SimulationPickup = Il2CppAssets.Scripts.Simulation.Towers.Projectiles.Behaviors.Pickup;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static BridgeResultV1 HandleCollectBank(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot collect from bank: no active match.", false);

        string towerId = ReadString(request.Payload, "towerId", "");
        if (string.IsNullOrEmpty(towerId))
            return ErrorResult(request, "INVALID_TOWER_ID", "towerId must be provided.", false);

        var tower = inGame.GetAllTowerToSim()?.FirstOrDefault(t => t != null && t.Id.ToString() == towerId);
        if (tower == null)
            return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' was not found.", false);

        float bankCash = 0f;
        try
        {
            bankCash = tower.GetBankAmount();
        }
        catch { }

        bool isBank = false;
        var simTower = tower.GetSimTower();
        if (simTower?.Behaviors?.list != null)
        {
            for (int i = 0; i < simTower.Behaviors.list.Count; i++)
            {
                if (simTower.Behaviors.list[i]?.TryCast<Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Bank>() != null)
                {
                    isBank = true;
                    break;
                }
            }
        }

        if (!isBank && bankCash <= 0f)
            return ErrorResult(request, "NOT_A_BANK", $"Tower '{towerId}' ({tower.Def?.name ?? "unknown"}) is not a Monkey Bank.", false);

        try
        {
            tower.CollectFromBank(0);
            return SuccessResult(request, new
            {
                Collected = true,
                TowerId = towerId,
                AmountCollected = (float)Math.Round(bankCash, 1),
                Cash = (float)Math.Round(inGame.GetCash(), 1)
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "COLLECT_BANK_FAILED", $"Failed to collect from bank '{towerId}': {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSetAutoCollect(BridgeRequestV1 request)
    {
        bool enabled = true;
        if (request.Payload.ValueKind == JsonValueKind.Object && request.Payload.TryGetProperty("enabled", out var enabledProp))
        {
            if (enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False)
                enabled = enabledProp.GetBoolean();
        }

        autoCollectDrops = enabled;
        return SuccessResult(request, new
        {
            AutoCollectDrops = autoCollectDrops
        });
    }

    private static BridgeResultV1 HandleCollectDrops(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot collect drops: no active match.", false);

        var (count, cash) = CollectActiveDrops();
        float currentCash = 0f;
        try { currentCash = (float)inGame.GetCash(); } catch { }

        return SuccessResult(request, new
        {
            Collected = true,
            Count = count,
            CashCollected = (float)Math.Round(cash, 1),
            CurrentCash = (float)Math.Round(currentCash, 1)
        });
    }

    private static (int count, float cash) CollectActiveDrops()
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame()) return (0, 0f);
        var bridge = inGame.bridge;
        if (bridge == null) return (0, 0f);

        int count = 0;
        float totalCash = 0f;

        try
        {
            activePickupBuffer.Clear();
            activePickupIdentities.Clear();

            var simulation = bridge.Simulation;
            if (simulation?.factory == null) return (0, 0f);

            // Pickup has no subclasses in BTD6 56.3. Its direct factory enumerator
            // works under IL2CPP; the Get<T>()/IEnumerable path crashes on map entry.
            var pickupEnumerable = simulation.factory.GetUncast<SimulationPickup>();
            if (pickupEnumerable == null) return (0, 0f);

            // Snapshot owning projectiles before mutating any pickup. Factory enumeration
            // must be disposed before Pickup can destroy or remove its projectile.
            var pickupEnumerator = pickupEnumerable.GetEnumerator();
            try
            {
                while (pickupEnumerator.MoveNext())
                {
                    var pickup = pickupEnumerator.Current;
                    if (pickup == null || pickup.isDestroyed) continue;

                    var projectile = pickup.projectile;
                    if (projectile == null || projectile.isDestroyed) continue;

                    if (activePickupIdentities.Add(projectile.Pointer))
                    {
                        activePickupBuffer.Add(projectile);
                    }
                }
            }
            finally
            {
                pickupEnumerator.Dispose();
            }

            for (int i = 0; i < activePickupBuffer.Count; i++)
            {
                var projectile = activePickupBuffer[i];
                if (projectile == null || projectile.isDestroyed) continue;

                try
                {
                    float pickedUp = projectile.Pickup(projectile.EmittedBy);
                    if (pickedUp > 0f)
                    {
                        count++;
                        totalCash += pickedUp;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"[AgentBridge] CollectActiveDrops error: {ex.Message}");
        }
        finally
        {
            activePickupBuffer.Clear();
            activePickupIdentities.Clear();
        }

        return (count, totalCash);
    }
}
