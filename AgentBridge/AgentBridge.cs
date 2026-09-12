using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2Cpp;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Models.Bloons;
using Il2CppNinjaKiwi.Localization;
using Il2CppAssets.Scripts.Models.Map;
using Il2CppAssets.Scripts.Models.Profile;
using Il2CppAssets.Scripts.Models.Rounds;
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Attack;
using Il2CppAssets.Scripts.Models.Towers.Projectiles;
using Il2CppAssets.Scripts.Models.Towers.Projectiles.Behaviors;
using Il2CppAssets.Scripts.Models.Towers.Filters;
using Il2CppAssets.Scripts.Models.Towers.Weapons;
using Il2CppAssets.Scripts.Models.Bloons.Behaviors;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities;
using Il2CppAssets.Scripts.Models.Towers.Behaviors.Abilities.Behaviors;
using SimulationAttack = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.Attack.Attack;
using SimulationWeapon = Il2CppAssets.Scripts.Simulation.Towers.Weapons.Weapon;
using SimulationBehaviorMutator = Il2CppAssets.Scripts.Simulation.Objects.BehaviorMutator;
using SimulationDamageMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.DamageTowerMutator.Mutator;
using SimulationPierceMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.PierceTowerMutator.Mutator;
using SimulationRangeMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.RangeTowerMutator.Mutator;
using SimulationReloadMutator = Il2CppAssets.Scripts.Simulation.Towers.Mutators.ReloadTimeTowerMutator.Mutator;
using SimulationPickup = Il2CppAssets.Scripts.Simulation.Towers.Projectiles.Behaviors.Pickup;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Unity.Player;
using Il2CppAssets.Scripts.Unity.Scenes;
using Il2CppAssets.Scripts.Unity.UI_New.GameOver;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.LevelUp;
using Il2CppAssets.Scripts.Unity.UI_New.Main;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;
using Il2CppAssets.Scripts.Unity.UI_New.Rewards;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using AgentBridge;

[assembly: MelonInfo(typeof(AgentBridge.AgentBridgeMod), ModHelperData.Name, ModHelperData.Version, ModHelperData.RepoOwner)]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6-Epic")]

namespace AgentBridge;

public sealed partial class AgentBridgeMod : BloonsTD6Mod
{
    public const int ProtocolVersion = 1;
    private const int MaxCommandBytes = 64 * 1024;
    private static readonly TimeSpan StatePublishInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PopupCheckInterval = TimeSpan.FromMilliseconds(200);
    private static DateTime nextStatePublishUtc;
    private static DateTime nextPopupCheckUtc;

    private static readonly List<ScheduledAbilityInfoV1> scheduledAbilities = new();
    private static int scheduleCounter = 0;
    private static readonly TimeSpan ScheduledAbilityRetention = TimeSpan.FromSeconds(60);
    private static float currentRoundElapsedSeconds = 0f;
    private static bool hasRoundElapsedTime;
    private static int clockRound = -1;
    private static int? currentRoundStartTick;
    private static int lastObservedRound = -1;
    private static bool lastRoundsActive = false;
    private static bool isMatchPaused = false;
    private static bool autoCollectDrops = true;
    private static readonly TimeSpan DropCollectionInterval = TimeSpan.FromMilliseconds(100);
    private static DateTime nextDropCollectionUtc;
    private static bool recordFirstUpdateAfterMainMenu;

    private const int MaxCustomCheckpoints = 50;
    private static readonly Dictionary<int, string> roundJsonCheckpoints = new();
    private static readonly Dictionary<string, string> customJsonCheckpoints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> customCheckpointTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, DateTime> roundCheckpointTimestamps = new();
    private static readonly List<Il2CppAssets.Scripts.Simulation.Towers.Projectiles.Projectile> activePickupBuffer = new();
    private static readonly HashSet<IntPtr> activePickupIdentities = new();

    private static readonly ModSettingButton WriteDiagnostics = new(WriteResearchDiagnostics)
    {
        displayName = "Write research diagnostics",
        description = "Writes a read-only account and progression snapshot to AgentBridge/reports/diagnostics.json.",
        buttonText = "Write diagnostics"
    };

    private static readonly ModSettingButton WriteBenchmarkPreflight = new(WriteBenchmarkPreflightReport)
    {
        displayName = "Write benchmark preflight",
        description = "Checks the fixed three-map CHIMPS research suite without modifying the profile.",
        buttonText = "Write preflight"
    };

    private static readonly ModSettingButton ProvisionResearchProfile = new(ProvisionProfile)
    {
        displayName = "Provision research profile",
        description = "Unlocks all current towers, heroes, maps, upgrades, and CHIMPS mode for this research profile.",
        buttonText = "Provision profile"
    };

    public override void OnApplicationStart()
    {
        EnsureIpcDirectories();
        StartMailboxWorker();
        PublishStateSnapshot();
        ModHelper.Msg<AgentBridgeMod>($"AgentBridge {ModHelperData.Version} loaded with protocol v{ProtocolVersion}; checkpoint policy: manual.");
    }

    public override void OnUpdate()
    {
        if (recordFirstUpdateAfterMainMenu)
        {
            recordFirstUpdateAfterMainMenu = false;
            RecordUiTransition("first_update_after_main_menu");
        }
        RecordFrame();
        var now = DateTime.UtcNow;
        if (now >= nextPopupCheckUtc)
        {
            nextPopupCheckUtc = now + PopupCheckInterval;
            using (Measure("ui_interruptions")) UpdateUiState(true);
        }
        using (Measure("round_telemetry")) ProcessRoundTelemetry();
        using (Measure("simulation_timing")) UpdateSimulationTiming();
        using (Measure("scheduled_upgrades")) ProcessScheduledUpgrades();
        using (Measure("scheduled_geraldo")) ProcessScheduledGeraldoPurchases();
        using (Measure("scheduled_corvus")) ProcessScheduledCorvusActions();
        using (Measure("mailbox_pump")) PumpMailbox();
        if (now >= nextStatePublishUtc)
        {
            nextStatePublishUtc = now + StatePublishInterval;
            using (Measure("state_capture")) PublishStateSnapshot();
        }
        if (autoCollectDrops && now >= nextDropCollectionUtc && IsActiveGame() && !TimeManager.gamePaused)
        {
            nextDropCollectionUtc = now + DropCollectionInterval;
            using (Measure("drop_collection")) CollectActiveDrops();
        }
    }

    public override void OnMainMenu()
    {
        RecordUiTransition("on_main_menu_entered");
        try
        {
            ResetAllCheckpoints();
            ResetManagedMatchState();
        }
        finally
        {
            RecordUiTransition("on_main_menu_returned");
            recordFirstUpdateAfterMainMenu = true;
        }
    }

    private static readonly string[] BenchmarkMapIds = ["Logs", "DarkCastle", "FloodedValley"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions ProtocolJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
