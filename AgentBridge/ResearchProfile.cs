using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models.Profile;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Player;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using UnityEngine;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void WriteResearchDiagnostics()
    {
        var game = Game.instance;
        if (game == null)
        {
            ModHelper.Warning<AgentBridgeMod>("Diagnostics skipped: Game.instance is unavailable.");
            return;
        }

        var player = game.GetBtd6Player();
        var profile = game.GetPlayerProfile();
        var snapshot = new ResearchDiagnostics
        {
            CapturedAtUtc = DateTime.UtcNow,
            Btd6Version = Application.version,
            ModHelperVersion = typeof(ModHelper).Assembly.GetName().Version?.ToString() ?? "unknown",
            AccountFlagged = player?.IsFlagged,
            InGame = InGame.instance != null,
            TowerIds = game.model?.towerSet?.Select(tower => tower.towerId).Distinct().ToArray() ?? [],
            PlayerFields = InspectObject(player),
            ProfileFields = InspectObject(profile),
            TowerUnlockProgresses = profile?.towerUnlockProgresses.Entries()
                .ToDictionary(entry => entry.key, entry => entry.value.ValueLong) ?? [],
            SelectedTowerForUnlockProgression = profile?.selectedTowerForUnlockProgression,
            CurrentTowerGiftProgress = profile?.currentTowerGiftProgress?.ValueLong
        };

        Directory.CreateDirectory(ReportsDirectory);
        var path = Path.Combine(ReportsDirectory, "diagnostics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        ModHelper.Msg<AgentBridgeMod>($"Wrote read-only diagnostics to {path}");
    }

    private static void WriteBenchmarkPreflightReport()
    {
        var game = Game.instance;
        var player = game?.GetBtd6Player();
        if (game?.model == null || player == null)
        {
            ModHelper.Warning<AgentBridgeMod>("Benchmark preflight skipped: game profile is unavailable.");
            return;
        }

        var towerIds = game.model.towerSet.Select(tower => tower.towerId).Distinct().OrderBy(id => id).ToArray();
        var heroIds = game.model.heroSet.Select(hero => hero.towerId).Distinct().OrderBy(id => id).ToArray();
        var snapshot = new BenchmarkPreflight
        {
            CapturedAtUtc = DateTime.UtcNow,
            Btd6Version = Application.version,
            AccountFlagged = player.IsFlagged,
            InGame = InGame.instance != null,
            TowerAvailability = towerIds.ToDictionary(id => id, id => player.HasUnlockedTower(id, true)),
            HeroAvailability = heroIds.ToDictionary(id => id, id => player.HasUnlockedHero(id, true)),
            MapAvailability = BenchmarkMapIds.ToDictionary(id => id, player.IsMapUnlocked)
        };

        Directory.CreateDirectory(ReportsDirectory);
        var path = Path.Combine(ReportsDirectory, "benchmark-preflight.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        ModHelper.Msg<AgentBridgeMod>($"Wrote read-only benchmark preflight to {path}");
    }

    private static BridgeResultV1 HandleProvisionProfile(BridgeRequestV1 request)
    {
        try
        {
            ProvisionProfile();
            return SuccessResult(request, new { Provisioned = true });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "PROVISION_FAILED", ex.Message, false);
        }
    }

    private static void ProvisionProfile()
    {
        var game = Game.instance;
        var player = game?.GetBtd6Player();
        var profile = game?.GetPlayerProfile();
        if (game?.model == null || player == null || profile == null)
        {
            ModHelper.Warning<AgentBridgeMod>("Provisioning skipped: game profile is unavailable.");
            return;
        }

        var towerIds = game.model.towerSet.Select(tower => tower.towerId).Distinct().OrderBy(id => id).ToArray();
        var heroIds = game.model.heroSet.Select(hero => hero.towerId).Distinct().OrderBy(id => id).ToArray();
        var mapIds = profile.mapInfo.maps.Entries().Select(entry => entry.key).Distinct().OrderBy(id => id).ToArray();

        foreach (var towerId in towerIds.Where(id => !player.HasUnlockedTower(id, true)))
        {
            player.UnlockTower(towerId);
        }

        foreach (var heroId in heroIds.Where(id => !player.HasUnlockedHero(id, true)))
        {
            player.UnlockHero(heroId);
        }

        foreach (var mapId in mapIds.Where(id => !player.IsMapUnlocked(id)))
        {
            player.UnlockMap(mapId);
        }

        var modesToComplete = new List<(string Difficulty, string Mode)>();
        foreach (var difficulty in new[] { "Easy", "Medium", "Hard" })
        {
            var modes = Il2CppAssets.Scripts.Models.Difficulty.ModeType.GetModesForDifficulty(difficulty);
            if (modes == null) continue;
            foreach (var mode in modes)
                modesToComplete.Add((difficulty, mode));
        }

        foreach (var mapId in mapIds)
        {
            foreach (var (diff, m) in modesToComplete)
            {
                if (!player.HasCompletedMode(mapId, diff, m, false))
                {
                    player.CompleteMode(mapId, diff, m, true, false);
                }
            }
        }

        foreach (var towerId in towerIds)
        {
            var baseTower = game.model.GetTower(towerId, 0, 0, 0);
            if (baseTower == null)
            {
                continue;
            }

            var upgrades = Enumerable.Range(0, 3)
                .SelectMany(path => Enumerable.Range(1, 5).Select(tier => baseTower.GetUpgrade(path, tier)))
                .Where(upgrade => upgrade != null)
                .Distinct()
                .ToList();

            var paragon = game.model.GetParagonUpgradeForTowerId(towerId);
            if (paragon != null)
            {
                upgrades.Add(paragon);
            }

            var missingUpgradeXp = upgrades
                .Where(upgrade => !player.HasUpgrade(upgrade.name))
                .Sum(upgrade => (float)upgrade.xpCost);
            if (missingUpgradeXp > 0)
            {
                player.AddTowerXP(towerId, missingUpgradeXp);
            }

            foreach (var upgrade in upgrades.Where(upgrade => !player.HasUpgrade(upgrade.name)))
            {
                player.AcquireUpgrade(towerId, upgrade.name, upgrade.xpCost);
            }
        }

        var snapshot = new ProvisioningReport
        {
            CapturedAtUtc = DateTime.UtcNow,
            Btd6Version = Application.version,
            TowerAvailability = towerIds.ToDictionary(id => id, id => player.HasUnlockedTower(id, true)),
            HeroAvailability = heroIds.ToDictionary(id => id, id => player.HasUnlockedHero(id, true)),
            MapAvailability = mapIds.ToDictionary(id => id, id => player.IsMapUnlocked(id)),
            ChimpsAvailability = mapIds.ToDictionary(id => id, id => player.IsModeUnlocked(id, "Hard", Il2CppAssets.Scripts.Models.Difficulty.ModeType.CHIMPS, false)),
            UpgradeAvailability = towerIds.ToDictionary(
                id => id,
                id => CountMissingUpgrades(game, player, id))
        };

        Directory.CreateDirectory(ReportsDirectory);
        var path = Path.Combine(ReportsDirectory, "provisioning.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        ModHelper.Msg<AgentBridgeMod>($"Provisioned research profile and wrote verification to {path}");
    }

    private static int CountMissingUpgrades(Game game, Btd6Player player, string towerId)
    {
        var baseTower = game.model.GetTower(towerId, 0, 0, 0);
        if (baseTower == null)
            return 0;

        int missing = Enumerable.Range(0, 3)
            .SelectMany(path => Enumerable.Range(1, 5).Select(tier => baseTower.GetUpgrade(path, tier)))
            .Where(upgrade => upgrade != null && !player.HasUpgrade(upgrade.name))
            .Count();

        var paragon = game.model.GetParagonUpgradeForTowerId(towerId);
        if (paragon != null && !player.HasUpgrade(paragon.name))
        {
            missing++;
        }

        return missing;
    }

    private static Dictionary<string, object?> InspectObject(object? instance)
    {
        var fields = new Dictionary<string, object?>();
        if (instance == null)
            return fields;

        foreach (var member in instance.GetType().GetMembers(BindingFlags.Instance | BindingFlags.Public))
        {
            if (member is not FieldInfo and not PropertyInfo || !IsResearchRelevant(member.Name))
                continue;

            try
            {
                var value = member switch
                {
                    FieldInfo field => field.GetValue(instance),
                    PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0 => property.GetValue(instance),
                    _ => null
                };
                fields[member.Name] = Summarize(value);
            }
            catch (Exception exception)
            {
                fields[member.Name] = $"<unavailable: {exception.GetType().Name}>";
            }
        }

        return fields;
    }


    private static bool IsResearchRelevant(string name) => name.Contains("tower", StringComparison.OrdinalIgnoreCase)
        || name.Contains("upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Contains("hero", StringComparison.OrdinalIgnoreCase)
        || name.Contains("map", StringComparison.OrdinalIgnoreCase)
        || name.Contains("unlock", StringComparison.OrdinalIgnoreCase)
        || name.Contains("pop", StringComparison.OrdinalIgnoreCase)
        || name.Contains("knowledge", StringComparison.OrdinalIgnoreCase);

    private static object? Summarize(object? value)
    {
        if (value == null || value is string || value.GetType().IsPrimitive || value is decimal)
            return value;

        if (value is IDictionary dictionary)
            return dictionary.Cast<DictionaryEntry>().Take(200).ToDictionary(entry => entry.Key?.ToString() ?? "<null>", entry => SummarizeScalar(entry.Value));

        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Take(200).Select(SummarizeScalar).ToArray();

        return value.ToString();
    }

    private static object? SummarizeScalar(object? value) => value == null || value is string || value.GetType().IsPrimitive || value is decimal
        ? value
        : value.ToString();
    private sealed class ResearchDiagnostics
    {
        public DateTime CapturedAtUtc { get; init; }
        public string Btd6Version { get; init; } = "unknown";
        public string ModHelperVersion { get; init; } = "unknown";
        public bool? AccountFlagged { get; init; }
        public bool InGame { get; init; }
        public string[] TowerIds { get; init; } = [];
        public Dictionary<string, long> TowerUnlockProgresses { get; init; } = [];
        public string? SelectedTowerForUnlockProgression { get; init; }
        public long? CurrentTowerGiftProgress { get; init; }
        public Dictionary<string, object?> PlayerFields { get; init; } = [];
        public Dictionary<string, object?> ProfileFields { get; init; } = [];
    }

    private sealed class BenchmarkPreflight
    {
        public DateTime CapturedAtUtc { get; init; }
        public string Btd6Version { get; init; } = "";
        public bool AccountFlagged { get; init; }
        public bool InGame { get; init; }
        public Dictionary<string, bool> TowerAvailability { get; init; } = [];
        public Dictionary<string, bool> HeroAvailability { get; init; } = [];
        public Dictionary<string, bool> MapAvailability { get; init; } = [];
    }

    private sealed class ProvisioningReport
    {
        public DateTime CapturedAtUtc { get; init; }
        public string Btd6Version { get; init; } = "";
        public Dictionary<string, bool> TowerAvailability { get; init; } = [];
        public Dictionary<string, bool> HeroAvailability { get; init; } = [];
        public Dictionary<string, bool> MapAvailability { get; init; } = [];
        public Dictionary<string, bool> ChimpsAvailability { get; init; } = [];
        public Dictionary<string, int> UpgradeAvailability { get; init; } = [];
    }
}
