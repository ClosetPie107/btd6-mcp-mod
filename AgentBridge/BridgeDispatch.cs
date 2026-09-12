using System;
using System.IO;
using System.Text.Json;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static void EnsureIpcDirectories()
    {
        Directory.CreateDirectory(IpcInboxDirectory);
        Directory.CreateDirectory(IpcProcessingDirectory);
        Directory.CreateDirectory(IpcOutboxDirectory);
    }


    private static BridgeResultV1 HandleRequest(BridgeRequestV1 request)
    {
        if (request.ProtocolVersion != ProtocolVersion)
            return ErrorResult(request, "UNSUPPORTED_PROTOCOL", $"Protocol version {request.ProtocolVersion} is unsupported.", false);
        if (!IsSafeRequestId(request.RequestId))
            return ErrorResult(request, "INVALID_REQUEST_ID", "requestId must contain 1-128 letters, digits, '.', '_' or '-'.", false);
        if (request.DeadlineAtUtc == default || request.DeadlineAtUtc < DateTime.UtcNow)
            return ErrorResult(request, "DEADLINE_EXCEEDED", "The command deadline has expired.", true);

        return request.Kind switch
        {
            "status" => SuccessResult(request, BuildStatus()),
            "observe" => SuccessResult(request, BuildObservation()),
            "round_progress" => SuccessResult(request, BuildRoundProgress()),
            "bridge_performance" => SuccessResult(request, BuildPerformance()),
            "configure_bridge" => HandleConfigureBridge(request),
            "tower_catalog" => HandleTowerCatalog(request),
            "tower_stats" => HandleTowerStats(request),
            "inspect_tower" => HandleInspectTower(request),
            "inspect_support_coverage" => HandleInspectSupportCoverage(request),
            "inspect_geraldo" => HandleInspectGeraldo(request),
            "inspect_corvus" => HandleInspectCorvus(request),
            "can_use_geraldo_item" => HandleCanUseGeraldoItem(request),
            "use_geraldo_item" => HandleUseGeraldoItem(request),
            "cancel_scheduled_geraldo_purchase" => HandleCancelScheduledGeraldoPurchase(request),
            "cast_corvus_spell" => HandleCastCorvusSpell(request),
            "set_corvus_spell" => HandleSetCorvusSpell(request),
            "cancel_scheduled_corvus_action" => HandleCancelScheduledCorvusAction(request),
            "boss_catalog" => HandleBossCatalog(request),
            "inspect_boss" => HandleInspectBoss(request),
            "inspect_obstacles" => HandleInspectObstacles(request),
            "remove_obstacle" => HandleRemoveObstacle(request),
            "inspect_tower_micro" => HandleInspectTowerMicro(request),
            "inspect_beast_merges" => HandleInspectBeastMerges(request),
            "merge_beast" => HandleMergeBeast(request),
            "provision_profile" => HandleProvisionProfile(request),
            "start_match" => HandleStartMatch(request),
            "restart_match" => HandleRestartMatch(request),
            "quit_match" => HandleQuitMatch(request),
            "ensure_main_menu" => HandleEnsureMainMenu(request),
            "can_place_tower" => HandleCanPlaceTower(request),
            "place_tower" => HandlePlaceTower(request),
            "cancel_scheduled_upgrade" => HandleCancelScheduledUpgrade(request),
            "sell_tower" => HandleSellTower(request),
            "set_target_priority" => HandleSetTargetPriorityMicro(request),
            "set_tower_target_position" => HandleSetTowerTargetPositionMicro(request),
            "toggle_submerge" => HandleToggleSubmerge(request),
            "collect_bank" => HandleCollectBank(request),
            "collect_drops" => HandleCollectDrops(request),
            "set_auto_collect" => HandleSetAutoCollect(request),
            "get_projected_cash" => HandleGetProjectedCash(request),
            "start_round" => HandleStartRound(request),
            "set_round" => HandleSetRound(request),
            "advance_round" => HandleAdvanceRound(request),
            "sandbox_spawn_round" => HandleSandboxSpawnRound(request),
            "sandbox_clear_bloons" => HandleSandboxClearBloons(request),
            "set_game_speed" => HandleSetGameSpeed(request),
            "pause_match" => HandlePauseMatch(request),
            "resume_match" => HandleResumeMatch(request),
            "activate_ability" => HandleActivateAbility(request),
            "schedule_ability" => HandleScheduleAbility(request),
            "cancel_scheduled_ability" => HandleCancelScheduledAbility(request),
            "respond_ui" => HandleRespondUi(request),
            "select_hero" => HandleSelectHero(request),
            "save_checkpoint" => HandleSaveCheckpoint(request),
            "restore_checkpoint" => HandleRestoreCheckpoint(request),
            "list_checkpoints" => HandleListCheckpoints(request),
            "delete_checkpoint" => HandleDeleteCheckpoint(request),
            "round_info" => HandleRoundInfo(request),
            "get_map_layout" => HandleGetMapLayout(request),
            "project_paragon_degree" => HandleProjectParagonDegree(request),
            "inspect_temple_sacrifices" => HandleInspectTempleSacrifices(request),
            "inspect_monkeyopolis_sacrifices" => HandleInspectMonkeyopolisSacrifices(request),
            _ => ErrorResult(request, "UNKNOWN_COMMAND", $"Command '{request.Kind}' is not supported by protocol v{ProtocolVersion}.", false)
        };
    }
    private static float ReadFloat(JsonElement element, string propName, float fallback = 0f)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
                return (float)d;
        }
        return fallback;
    }

    private static string ReadString(JsonElement element, string propName, string fallback = "")
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? fallback;
        }
        return fallback;
    }

    private static int ReadInt(JsonElement element, string propName, int fallback = 0)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
                return i;
        }
        return fallback;
    }
}
