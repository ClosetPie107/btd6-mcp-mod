using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppNinjaKiwi.Localization;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static string NormalizeMapSelector(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ResolveMapId(IEnumerable<string> mapIds, string requested)
    {
        var selector = NormalizeMapSelector(requested);
        // Monkey Meadow predates the public map naming scheme and retains
        // "Tutorial" as its native profile/setup ID.
        if (selector == "monkeymeadow")
            selector = "tutorial";

        var exact = mapIds.FirstOrDefault(id => string.Equals(id, selector, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        return mapIds.FirstOrDefault(id => NormalizeMapSelector(id) == selector);
    }

    private static string ResolveMapName(string mapId)
    {
        try
        {
            var localization = LocalizationManager.Instance;
            if (localization != null)
                foreach (var key in new[] { mapId, $"MapName_{mapId}", $"{mapId}Name" })
                    if (localization.TryGetTextEnglish(key, out string value) && !string.IsNullOrWhiteSpace(value))
                        return value;
        }
        catch { }
        return string.Equals(mapId, "Tutorial", StringComparison.OrdinalIgnoreCase) ? "Monkey Meadow" : mapId;
    }

    private static BridgeResultV1 HandleStartMatch(BridgeRequestV1 request)
    {
        if (Game.instance == null || Il2CppAssets.Scripts.Unity.UI_New.UI.instance == null)
            return ErrorResult(request, "GAME_UNAVAILABLE", "BTD6 is not ready to load a match.", true);

        if (IsActiveGame())
            return ErrorResult(request, "ALREADY_IN_GAME", "A match is already in progress. Use restart_match or quit_match.", false);

        string map = "Logs";
        string difficulty = "Easy";
        string mode = "Standard";
        string? hero = null;

        string checkpointPolicy = "assisted";
        if (request.Payload.ValueKind == JsonValueKind.Object)
        {
            if (request.Payload.TryGetProperty("map", out var mapProp) && mapProp.ValueKind == JsonValueKind.String)
                map = mapProp.GetString() ?? "Logs";
            if (request.Payload.TryGetProperty("difficulty", out var diffProp) && diffProp.ValueKind == JsonValueKind.String)
                difficulty = diffProp.GetString() ?? "Easy";
            if (request.Payload.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
                mode = modeProp.GetString() ?? "Standard";
            if (request.Payload.TryGetProperty("hero", out var heroProp) && heroProp.ValueKind == JsonValueKind.String)
                hero = heroProp.GetString();
            if (request.Payload.TryGetProperty("checkpointPolicy", out var checkpointProp) && checkpointProp.ValueKind == JsonValueKind.String)
                checkpointPolicy = checkpointProp.GetString() ?? "assisted";
        }
        if (checkpointPolicy is not ("assisted" or "none"))
            return ErrorResult(request, "INVALID_ARGUMENT", "checkpointPolicy must be assisted or none.", false);

        bool bossChallenge = string.Equals(mode, "BossChallenge", StringComparison.OrdinalIgnoreCase);
        string? bossType = null;
        bool elite = false;
        if (request.Payload.TryGetProperty("bossType", out var bossProp))
        {
            if (bossProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(bossProp.GetString()))
                return ErrorResult(request, "INVALID_ARGUMENT", "bossType must be a non-empty native boss ID.", false);
            bossType = bossProp.GetString();
        }
        if (request.Payload.TryGetProperty("elite", out var eliteProp))
        {
            if (eliteProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ErrorResult(request, "INVALID_ARGUMENT", "elite must be a boolean.", false);
            elite = eliteProp.GetBoolean();
        }
        if (!bossChallenge && (bossType != null || request.Payload.TryGetProperty("elite", out _)))
            return ErrorResult(request, "INVALID_ARGUMENT", "bossType and elite require mode BossChallenge.", false);
        Il2CppAssets.Scripts.Data.Boss.BossData? selectedBoss = null;
        if (bossChallenge)
        {
            if (bossType == null)
                return ErrorResult(request, "INVALID_ARGUMENT", "BossChallenge requires bossType from boss_catalog.", false);
            var roster = Il2CppAssets.Scripts.Data.GameData.Instance?.bosses?.BossList?.items;
            if (roster == null)
                return ErrorResult(request, "GAME_UNAVAILABLE", "The native boss roster is unavailable.", true);
            foreach (var candidate in roster)
                if (candidate != null && string.Equals(candidate.id.ToString(), bossType, StringComparison.OrdinalIgnoreCase))
                {
                    selectedBoss = candidate;
                    break;
                }
            if (selectedBoss == null)
                return ErrorResult(request, "UNKNOWN_BOSS", $"Unknown installed boss '{bossType}'. Use boss_catalog.", false);
            bossType = selectedBoss.id.ToString();
            mode = "Standard";
        }

        if (string.Equals(mode, "CHIMPS", StringComparison.OrdinalIgnoreCase))
            mode = Il2CppAssets.Scripts.Models.Difficulty.ModeType.CHIMPS;
        // Difficulty and mode are separate game-owned modifiers. Resolve ordinary
        // mode/difficulty pairs from the same lists used by the native mode selector.
        var canonicalDifficulty = new[] { "Easy", "Medium", "Hard" }
            .FirstOrDefault(value => string.Equals(value, difficulty, StringComparison.OrdinalIgnoreCase));
        if (canonicalDifficulty == null)
            return ErrorResult(request, "INVALID_DIFFICULTY", "difficulty must be Easy, Medium, or Hard; Impoppable is a mode.", false);
        difficulty = canonicalDifficulty;
        string? resolvedMode = null;
        string? resolvedDifficulty = null;
        foreach (var candidateDifficulty in new[] { difficulty, "Easy", "Medium", "Hard" }.Distinct())
        {
            var modes = Il2CppAssets.Scripts.Models.Difficulty.ModeType.GetModesForDifficulty(candidateDifficulty);
            if (modes == null) continue;
            // Sandbox has its own selector button rather than a regular-mode entry.
            foreach (var candidateMode in modes.Append(Il2CppAssets.Scripts.Models.Difficulty.ModeType.Sandbox))
            {
                if (!string.Equals(candidateMode, mode, StringComparison.OrdinalIgnoreCase)) continue;
                resolvedMode = candidateMode;
                resolvedDifficulty = candidateDifficulty;
                break;
            }
            if (resolvedMode != null) break;
        }
        if (resolvedMode == null || resolvedDifficulty == null)
            return ErrorResult(request, "INVALID_MODE", $"Unknown regular game mode '{mode}'.", false);
        mode = resolvedMode;
        difficulty = resolvedDifficulty;

        UpdateUiState(false);
        if (!IsOnMainMenu())
            return ErrorResult(request, "UI_BLOCKED", "Start a match only from an unobstructed main menu or map selection. Inspect status.ui.", false);
        bool autoHandleUi = true;
        if (request.Payload.TryGetProperty("autoHandleUi", out var uiPolicy))
        {
            if (uiPolicy.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ErrorResult(request, "INVALID_ARGUMENT", "autoHandleUi must be a boolean.", false);
            autoHandleUi = uiPolicy.GetBoolean();
        }

        try
        {

            var profile = Game.instance?.GetPlayerProfile();
            var player = Game.instance?.GetBtd6Player();
            var mapItems = Il2CppAssets.Scripts.Data.GameData.Instance?.mapSet?.Maps?.items;
            if (profile == null || player == null || mapItems == null)
                return ErrorResult(request, "GAME_UNAVAILABLE", "The BTD6 research profile or map catalog is unavailable.", true);
            var requestedMap = map;
            map = ResolveMapId(mapItems.Select(details => details.id), requestedMap) ?? "";
            if (map.Length == 0)
                return ErrorResult(request, "UNKNOWN_MAP", $"Unknown installed map '{requestedMap}'. Use its in-game display name or native ID.", false);
            if (!player.IsMapUnlocked(map))
                return ErrorResult(request, "MAP_LOCKED", $"Map '{map}' is not unlocked for this research profile.", false);
            if (!player.IsModeUnlocked(map, difficulty, mode, false))
                return ErrorResult(request, "MODE_LOCKED", $"Mode '{mode}' on {difficulty} is not unlocked for map '{map}'. Run Provision research profile explicitly before starting it.", false);

            if (!string.IsNullOrEmpty(hero) && profile != null)
            {
                profile.primaryHero = hero;
            }

            InGameData.CreateNewInstance();
            var editable = InGameData.Editable;
            if (editable == null)
                return ErrorResult(request, "GAME_SETUP_UNAVAILABLE", "BTD6 did not provide mutable game setup data.", true);

            editable.selectedMap = map;
            editable.selectedDifficulty = difficulty;
            editable.selectedMode = mode;

            if (selectedBoss != null)
            {
                var challenge = Il2CppAssets.Scripts.Models.ServerEvents.DailyChallengeModel.CreateDefaultEditorModel();
                challenge.map = map;
                challenge.difficulty = difficulty;
                challenge.mode = mode;
                // Challenge rounds are public (one-based). Keep the full fifth-tier
                // defeat window instead of the regular difficulty's victory round.
                challenge.startRules.endRound = 140;
                challenge.disableSaves = true;
                challenge.noInstaReward = true;
                editable.SetupBossChallenge("", selectedBoss.id, elite, false, challenge,
                    Il2CppAssets.Scripts.Models.ServerEvents.LeaderboardScoringType.GameTime);
            }

            ResetAllCheckpoints();
            ResetManagedMatchState();
            ResetNativeMatchTime();
            configuration = configuration with
            {
                CheckpointMode = checkpointPolicy == "assisted" ? "assisted" : "manual",
                CheckpointRounds = Array.Empty<int>(),
                MaxRoundCheckpoints = checkpointPolicy == "assisted" ? 50 : configuration.MaxRoundCheckpoints
            };
            AuthorizeGameplayUi(autoHandleUi);
            Il2CppAssets.Scripts.Unity.UI_New.UI.instance.LoadGame();

            return SuccessResult(request, new
            {
                Started = true,
                MapId = map,
                MapName = ResolveMapName(map),
                Difficulty = difficulty,
                Mode = bossChallenge ? "BossChallenge" : mode == Il2CppAssets.Scripts.Models.Difficulty.ModeType.CHIMPS ? "CHIMPS" : mode,
                Hero = profile?.primaryHero ?? hero,
                CheckpointPolicy = checkpointPolicy,
                BossType = bossType,
                Elite = bossChallenge ? (bool?)elite : null,
                Ranked = bossChallenge ? (bool?)false : null
            });
        }
        catch (Exception ex)
        {
            AuthorizeGameplayUi(false);
            return ErrorResult(request, "START_MATCH_FAILED", $"Failed to start match: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleRestartMatch(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot restart: no active match.", false);

        var bridge = inGame.GetUnityToSimulation();
        if (bridge == null)
            return ErrorResult(request, "NO_SIMULATION", "Cannot restart: simulation is unavailable.", false);

        try
        {
            // InGame.Restart is the post-reset UI callback. Dispatch through the
            // simulation so native reset, Restart, and LateRestart run in order;
            // the UnityToSimulation.Restart postfix performs managed cleanup only
            // after that native restart commits.
            bridge.Restart();
            return SuccessResult(request, new { Restarted = true });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "RESTART_FAILED", $"Failed to restart match: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleQuitMatch(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot quit: no active match.", false);

        try
        {
            AuthorizeGameplayUi(false);
            ResetAllCheckpoints();
            ResetManagedMatchState();
            inGame.Quit();
            return SuccessResult(request, new { Quit = true });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "QUIT_FAILED", $"Failed to quit match: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleEnsureMainMenu(BridgeRequestV1 request)
    {
        try
        {
            UpdateUiState(false);
            if (!uiState.Ready)
                return ErrorResult(request, "UI_BLOCKED", "A UI interruption must be completed explicitly before returning to the main menu. Inspect status.ui.", false);
            AuthorizeGameplayUi(false);
            ResetManagedMatchState();

            var inGame = InGame.instance;
            if (inGame != null && inGame.IsInGame())
            {
                inGame.Quit();
            }
            else if (MenuManager.instance is { } manager &&
                manager.GetCurrentMenu()?.GetIl2CppType().Name != "MainMenu")
            {
                // GoToMainMenu closes the current menu; calling it on MainMenu
                // leaves a black screen. Quit already owns its return transition.
                manager.GoToMainMenu();
            }

            return SuccessResult(request, new
            {
                OnMainMenu = IsOnMainMenu(),
                InGame = IsActiveGame()
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "ENSURE_MAIN_MENU_FAILED", $"Failed to return to main menu: {ex.Message}", false);
        }
    }


    private static BridgeResultV1 HandleSelectHero(BridgeRequestV1 request)
    {
        string hero = ReadString(request.Payload, "hero", "");
        if (string.IsNullOrEmpty(hero))
            return ErrorResult(request, "INVALID_HERO", "hero name must be provided.", false);

        var profile = Game.instance?.GetPlayerProfile();
        if (profile == null)
            return ErrorResult(request, "PROFILE_UNAVAILABLE", "Player profile is unavailable.", true);

        profile.primaryHero = hero;
        return SuccessResult(request, new
        {
            SelectedHero = profile.primaryHero
        });
    }

    public static bool IsOnMainMenu()
    {
        try
        {
            UpdateUiState(false);
            if (IsActiveGame() || !uiState.Ready) return false;
            string? name = MenuManager.instance?.GetCurrentMenu()?.GetIl2CppType().Name;
            return name is "MainMenu" or "MapSelectScreen";
        }
        catch
        {
            return false;
        }
    }
}
[HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.Restart))]
internal static class MatchRestartLifecyclePatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try { AgentBridgeMod.HandleNativeRestartCommitted(); }
        catch (Exception ex) { BTD_Mod_Helper.ModHelper.Warning<AgentBridgeMod>($"Restart lifecycle cleanup failed: {ex.Message}"); }
    }
}
