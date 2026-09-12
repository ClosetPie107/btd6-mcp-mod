using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Extensions;
using HarmonyLib;
using Il2CppAssets.Scripts.Unity.CollectionEvent;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Unity.UI_New.GameOver;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.LevelUp;
using Il2CppAssets.Scripts.Unity.UI_New.Main.HeroSelect;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;
using Il2CppAssets.Scripts.Unity.UI_New.Rewards;
using UnityEngine;
using UnityEngine.UI;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    private static readonly UiInterruptionTracker uiTracker = new();
    private static UiStateV1 uiState = new();
    private static bool gameplayUiAuthorized;
    private static bool uiMatchStarting;
    private static string? reportedUiFailure;
    private sealed record TutorialMarker(string Event, Il2CppSystem.Action Callback);
    private static readonly Dictionary<IntPtr, TutorialMarker> tutorialMarkers = new();
    // BTD6 56.3 event IDs verified against the live localization table. Never match
    // translated text, arbitrary ft_* events (including login), or generic OK dialogs.
    private static readonly HashSet<string> InformationalEvents = new(StringComparer.Ordinal)
    {
        "ft_camobloons", "ft_leadbloons", "ft_regrowbloons", "ft_moabclassbloons",
        "ft_ceramicbloons", "ft_purplebloons", "ft_blackbloons", "ft_whitebloons",
        "ft_fortifiedbloons", "ft_zomg", "ft_bad", "ft_camoreminder", "ft_trackremovable",
        "ft_activatedabilities", "ft_autostart", "ft_bananafarm", "ft_monkeyknowledge",
        "ft_powers", "ft_sandboxmode", "ft_ultimatepower", "ft_upgradesreminder",
        "ft_golden", "ft_monkeyteams", "ft_paragon"
    };

    private sealed record UiCandidate(string Key, string Kind, string Screen, bool Ready,
        bool Automatic, // Owned by automatic policy; readiness is represented separately.
        bool AwaitingReadiness, string[] Actions, Action<string, string?>? Invoke,
        string? Title = null, string? Body = null, string[]? Choices = null, string? SelectedChoice = null);

    private static double UiNow => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private static string? UiText(string? value) => value == null ? null : value[..Math.Min(value.Length, 4096)];
    private static bool ButtonReady(Button? button) => button != null && button.isActiveAndEnabled && button.IsInteractable();
    private static Button? PopupButton(GameObject? obj) => obj != null && obj.activeInHierarchy ? obj.GetComponentInChildren<Button>() : null;
    private static bool MenuReady(GameMenu menu) => !menu.isStillLoading && !menu.isAnimatingWithCallback &&
        MenuManager.instance != null && !MenuManager.instance.IsTransitioning && !MenuManager.instance.IsClosingOrOpeningMenu;

    // Click-to-continue rewards can keep their parent menu transition open until
    // acknowledged. Gate their native click on the screen, not that transition.
    private static bool ClickScreenReady(GameMenu menu) => menu.gameObject.activeInHierarchy && !menu.isStillLoading;

    private static void AuthorizeGameplayUi(bool enabled)
    {
        gameplayUiAuthorized = enabled;
        uiMatchStarting = enabled;
        tutorialMarkers.Clear();
        uiTracker.Observe(null, false, false, false, UiNow);
    }

    internal static void TagInformationalEvent(string eventName, ref Il2CppSystem.Action? onEndCallback)
    {
        // Authorization starts before InGame.IsInGame() during LoadGame. The
        // allowlist remains the ownership boundary for callbacks seen in that gap.
        if (!gameplayUiAuthorized || !InformationalEvents.Contains(eventName) || tutorialMarkers.Count >= 32)
            return;
        var original = onEndCallback;
        IntPtr identity = IntPtr.Zero;
        Il2CppSystem.Action wrapped = (Action)(() =>
        {
            tutorialMarkers.Remove(identity);
            original?.Invoke();
        });
        identity = wrapped.Pointer;
        tutorialMarkers[identity] = new TutorialMarker(eventName, wrapped);
        onEndCallback = wrapped;
    }

    private static string? GetTutorialOrigin(Popup popup)
    {
        // These are the installed 56.3 ShowEventPopup callback closures. Match the
        // exact tagged completion delegate, not just the closure's type. A changed
        // game implementation fails closed to an ordinary, explicitly handled popup.
        var target = popup.confirmCallback?.Target;
        var direct = target?.TryCast<InGame.__c__DisplayClass374_0>();
        var indirect = target?.TryCast<InGame.__c__DisplayClass374_1>();
        var completion = direct?.onEndCallback ?? indirect?.field_Public___c__DisplayClass374_0_0?.onEndCallback;
        return completion != null && tutorialMarkers.TryGetValue(completion.Pointer, out var marker) ? marker.Event : null;
    }

    private static UiCandidate? InspectUi()
    {
        bool inGame = IsActiveGame();
        var popups = PopupScreen.instance;
        if (popups != null && popups.TryGetActivePopup(out var popup) && popup != null)
        {
            string? tutorial = GetTutorialOrigin(popup);
            bool automatic = tutorial != null && gameplayUiAuthorized;
            bool ok = ButtonReady(PopupButton(popup.okObj));
            bool cancel = ButtonReady(PopupButton(popup.cancelObj));
            bool dismiss = popup.canHide;
            var actions = new List<string>(3);
            if (ok) actions.Add("confirm");
            if (cancel) actions.Add("cancel");
            if (dismiss) actions.Add("back");
            string? title = UiText(popup.title?.text);
            string? body = UiText(popup.body?.text);
            string key = $"popup:{popup.Pointer}:{popup.confirmCallback?.Pointer}:{popup.cancelCallback?.Pointer}:{title}:{body}";
            // The tagged callback establishes automatic ownership. InGame.IsInGame()
            // can lag the popup during LoadGame, so gate readiness with it without
            // reclassifying the known candidate as a manual blocker.
            bool automaticReady = automatic && inGame && ok;
            return new(key, tutorial == null ? "popup" : "tutorial", popup.GetIl2CppType().Name,
                automatic ? automaticReady : ok || cancel || dismiss, automatic,
                tutorial != null, actions.ToArray(), (action, _) =>
                {
                    if (action == "confirm")
                    {
                        if (popup.AsyncConfirmCallback != null) popup.OKClickedAsync(); else popup.OKClicked();
                    }
                    else if (action == "cancel")
                    {
                        if (popup.AsyncCancelCallback != null) popup.CancelClickedAsync(false); else popup.CancelClicked();
                    }
                    else if (action == "back" && !popup.TryHidePop())
                        throw new InvalidOperationException("The popup refused its native dismiss action.");
                }, title, body);
        }
        if (popups != null && popups.IsPopupActiveOrLoading())
            return new("popup-loading", "transition", "PopupScreen", false, false, true, Array.Empty<string>(), null);

        var manager = MenuManager.instance;
        var menu = manager?.GetCurrentMenu();
        if (menu == null)
            return manager?.IsTransitioning == true
                ? new("menu-loading", "transition", "MenuManager", false, false, true, Array.Empty<string>(), null)
                : null;
        string screen = menu.GetIl2CppType().Name;
        bool ready = MenuReady(menu);
        bool owned = gameplayUiAuthorized;
        var money = menu.TryCast<LevelUpMonkeyMoneyScreen>();
        if (money != null)
        {
            bool canNext = ClickScreenReady(money);
            return new($"level-money:{menu.Pointer}", "level_up", screen,
                owned ? inGame && canNext : canNext, owned, true,
                canNext ? new[] { "next" } : Array.Empty<string>(), (_, _) => money.OnClick(),
                "Monkey Money", UiText(money.monkeyMoney?.text));
        }
        var knowledge = menu.TryCast<LevelUpKnowledgeScreen>();
        if (knowledge != null)
        {
            bool canNext = ClickScreenReady(knowledge);
            return new($"level-knowledge:{menu.Pointer}", "level_up", screen,
                owned ? inGame && canNext : canNext, owned, true,
                canNext ? new[] { "next" } : Array.Empty<string>(), (_, _) => knowledge.OnClick(),
                "Monkey Knowledge", knowledge.knowledgeCount.ToString());
        }
        var heroSplash = menu.TryCast<HeroPurchaseSplash>();
        if (heroSplash != null)
        {
            bool canNext = ClickScreenReady(heroSplash);
            return new($"hero-splash:{menu.Pointer}", "menu", screen, canNext, false, true,
                canNext ? new[] { "next" } : Array.Empty<string>(), (_, _) => heroSplash.MenuClicked(),
                UiText(heroSplash.heroName?.text), UiText(heroSplash.heroShortDescription?.text));
        }
        var victory = menu.TryCast<VictoryScreen>();
        if (victory != null)
        {
            var next = PopupButton(victory.nextButtonContainer?.gameObject);
            bool showingSummary = next != null && next.gameObject.activeInHierarchy;
            var actions = new List<string>(3);
            if (ButtonReady(next)) actions.Add("next");
            if (ButtonReady(victory.freePlayBtn)) actions.Add("freeplay");
            if (ButtonReady(victory.homeBtn)) actions.Add("home");
            return new($"victory:{menu.Pointer}:{showingSummary}", "menu", screen, actions.Count > 0, false, true,
                actions.ToArray(), (action, _) =>
                {
                    if (action == "next") next!.onClick.Invoke();
                    else if (action == "freeplay") victory.freePlayBtn!.onClick.Invoke();
                    else victory.homeBtn!.onClick.Invoke();
                });
        }
        var defeat = menu.TryCast<DefeatScreen>();
        if (defeat != null)
        {
            var actions = new List<string>(2);
            if (ButtonReady(defeat.restartButton)) actions.Add("restart");
            if (ButtonReady(defeat.homeBtn)) actions.Add("home");
            return new($"defeat:{menu.Pointer}", "menu", screen, actions.Count > 0, false, true,
                actions.ToArray(), (action, _) =>
                {
                    if (action == "restart") defeat.RestartClick();
                    else defeat.homeBtn!.onClick.Invoke();
                });
        }
        var collection = menu.TryCast<CollectionEventUI>();
        if (collection != null)
        {
            var actions = new List<string>(3);
            if (ButtonReady(collection.collectButton)) actions.Add("collect");
            if (ButtonReady(collection.playButton)) actions.Add("play");
            if (ready) actions.Add("back");
            string progress = collection.collectProgressText?.text ?? "";
            string goal = collection.collectGoalText?.text ?? "";
            return new($"collection:{menu.Pointer}:{progress}:{goal}", "menu", screen, actions.Count > 0, false, true,
                actions.ToArray(), (action, _) =>
                {
                    if (action == "collect") collection.OnCollect();
                    else if (action == "play") collection.OnPlay();
                    else manager!.CloseCurrentMenu();
                });
        }
        var level = menu.TryCast<LevelUpScreen>();
        if (level != null)
            return new($"level:{menu.Pointer}", "level_up", screen, owned ? inGame && ready : ready, owned, true,
                ready ? new[] { "next" } : Array.Empty<string>(), (_, _) => level.OpenNextUnlockScreen());
        var unlock = menu.TryCast<TowerUnlockScreen>();
        if (unlock != null)
        {
            var choices = new List<string>();
            if (ready && unlock.towerButtons != null)
                foreach (var button in unlock.towerButtons)
                    if (button != null && ButtonReady(button.button) && choices.Count < 64) choices.Add(button.towerId);
            string selected = unlock.selectedButton?.towerId ?? "";
            bool actionable = ready && !unlock.towerUnlocked;
            var actions = new List<string>();
            bool hasSelection = choices.Contains(selected);
            if (actionable && choices.Count > (hasSelection ? 1 : 0)) actions.Add("select_tower");
            if (actionable && hasSelection) actions.Add("unlock");
            return new($"unlock:{menu.Pointer}:{selected}:{unlock.towerUnlocked}", "tower_unlock", screen,
                actionable && actions.Count > 0, false, unlock.towerUnlocked || !ready, actions.ToArray(), (action, value) =>
                {
                    if (action == "select_tower")
                    {
                        var choice = unlock.towerButtons!.First(button => button != null && button.towerId == value && ButtonReady(button.button));
                        choice.button.onClick.Invoke();
                    }
                    else unlock.OnUnlockTower();
                }, Choices: choices.ToArray(), SelectedChoice: selected.Length > 0 ? selected : null);
        }
        var rewards = menu.TryCast<RewardsScreen>();
        if (rewards != null)
        {
            bool summary = rewards.summaryScreen != null && rewards.summaryScreen.activeInHierarchy;
            Button? button = summary ? rewards.summaryOkButton : rewards.nextBtn;
            string panel = rewards.activePanel?.GetIl2CppType().Name ?? "";
            bool informational = summary || panel is "GenericRewardPanel" or "PowerRewardPanel" or "InstaMonkeyRewardPanel";
            bool canNext = ready && !rewards.IsCurrentlyAnimatingAllLoot &&
                (summary || rewards.activePanel?.interactable == true) && ButtonReady(button);
            bool automatic = owned && informational;
            return new($"rewards:{menu.Pointer}:{rewards.lootIndex}:{summary}:{panel}", "rewards", screen,
                automatic ? inGame && canNext : canNext, automatic, automatic,
                canNext ? new[] { "next" } : Array.Empty<string>(), (_, _) => button!.onClick.Invoke());
        }
        if (manager!.IsTransitioning || manager.IsClosingOrOpeningMenu || menu.isStillLoading || menu.isAnimatingWithCallback)
            return new($"transition:{menu.Pointer}", "transition", screen, false, false, true, Array.Empty<string>(), null);
        if (screen is "MainMenu" or "MapSelectScreen" or "InGame") return null;
        if (screen == "TitleScreen") return new($"title:{menu.Pointer}", "startup", screen, false, false, true, Array.Empty<string>(), null);
        return new($"menu:{menu.Pointer}", "menu", screen, ready, false, false,
            ready ? new[] { "back" } : Array.Empty<string>(), (_, _) => manager.CloseCurrentMenu());
    }

    private static void RecordUiTransition(string eventName, string? blockerId = null, string? action = null, string? error = null)
    {
        string? screen = null;
        bool? activeGame = null;
        bool? transitioning = null;
        bool? openingOrClosing = null;
        bool? stillLoading = null;
        bool? animating = null;
        string? contextError = null;
        try
        {
            var manager = MenuManager.instance;
            var menu = manager?.GetCurrentMenu();
            screen = menu?.GetIl2CppType().Name;
            activeGame = InGame.instance?.IsInGame() == true;
            transitioning = manager?.IsTransitioning;
            openingOrClosing = manager?.IsClosingOrOpeningMenu;
            stillLoading = menu?.isStillLoading;
            animating = menu?.isAnimatingWithCallback;
        }
        catch (Exception ex)
        {
            contextError = $"{ex.GetType().Name}: {ex.Message}";
        }
        QueueTransitionJournal(new TransitionJournalEntry
        {
            AtUtc = DateTime.UtcNow,
            Event = eventName,
            BlockerId = blockerId,
            Action = action,
            Screen = screen,
            ActiveGame = activeGame,
            OnMainMenu = screen == "MainMenu" && activeGame == false,
            MenuTransitioning = transitioning,
            MenuOpeningOrClosing = openingOrClosing,
            MenuStillLoading = stillLoading,
            MenuAnimatingWithCallback = animating,
            Error = error,
            ContextError = contextError
        });
    }

    private static void InvokeUiAction(UiCandidate candidate, string blockerId, string action, string? value)
    {
        RecordUiTransition("ui_action_requested", blockerId, action);
        try
        {
            candidate.Invoke!(action, value);
            RecordUiTransition("ui_action_returned", blockerId, action);
        }
        catch (Exception ex)
        {
            RecordUiTransition("ui_action_failed", blockerId, action, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private static UiCandidate? UpdateUiState(bool allowAutomatic)
    {
        try
        {
            if (IsActiveGame()) uiMatchStarting = false;
            else if (!uiMatchStarting && MenuManager.instance?.GetCurrentMenu()?.GetIl2CppType().Name == "MainMenu")
            {
                gameplayUiAuthorized = false;
                tutorialMarkers.Clear();
            }
            var candidate = InspectUi();
            uiTracker.Observe(candidate?.Key, candidate?.Ready == true, candidate?.Automatic == true,
                candidate?.AwaitingReadiness == true, UiNow);
            if (candidate != null && allowAutomatic && candidate.Automatic && uiTracker.CanAct)
            {
                if (uiTracker.Begin(uiTracker.Id, UiNow))
                {
                    ModHelper.Msg<AgentBridgeMod>($"Acknowledging {candidate.Kind} ({uiTracker.Id}) through its normal UI action.");
                    try { InvokeUiAction(candidate, uiTracker.Id, candidate.Actions[0], null); }
                    catch (Exception ex) { uiTracker.Fail($"UI action failed: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            uiState = new UiStateV1
            {
                AutoHandlingEnabled = gameplayUiAuthorized,
                Ready = candidate == null,
                Blocker = candidate == null ? null : new UiBlockerV1
                {
                    Id = uiTracker.Id, Kind = candidate.Kind, Screen = candidate.Screen,
                    State = uiTracker.State, Reason = UiText(uiTracker.Error),
                    Title = candidate.Title, Body = candidate.Body,
                    Actions = uiTracker.CanAct ? candidate.Actions : Array.Empty<string>(),
                    Choices = candidate.Choices ?? Array.Empty<string>(), SelectedChoice = candidate.SelectedChoice
                }
            };
            if (uiTracker.Error != null && reportedUiFailure != uiTracker.Id)
            {
                reportedUiFailure = uiTracker.Id;
                ModHelper.Warning<AgentBridgeMod>($"UI interruption {uiTracker.Id}: {uiTracker.Error}");
            }
            return candidate;
        }
        catch (Exception ex)
        {
            uiTracker.Observe("ui-inspection-error", false, false, false, UiNow);
            uiTracker.Fail($"UI inspection failed: {ex.GetType().Name}: {ex.Message}");
            uiState = new UiStateV1 { AutoHandlingEnabled = gameplayUiAuthorized, Ready = false,
                Blocker = new UiBlockerV1 { Id = uiTracker.Id, Kind = "unknown", State = "failed", Reason = UiText(uiTracker.Error) } };
            return null;
        }
    }

    private static BridgeResultV1 HandleRespondUi(BridgeRequestV1 request)
    {
        string id = ReadString(request.Payload, "blockerId", "");
        string action = ReadString(request.Payload, "action", "");
        string? value = ReadString(request.Payload, "value", "");
        var target = UpdateUiState(false);
        if (target == null || id != uiTracker.Id)
            return ErrorResult(request, "STALE_UI_BLOCKER", "The requested blocker is no longer current. Observe UI before acting.", false);
        if (!target.Actions.Contains(action) || (action == "select_tower" &&
            (target.Choices?.Contains(value) != true || value == target.SelectedChoice)))
            return ErrorResult(request, "INVALID_UI_ACTION", "The action or selection is not available for this blocker.", false);
        if (!uiTracker.Begin(id, UiNow, action is "cancel" or "back"))
            return ErrorResult(request, "UI_NOT_READY", "The blocker is transitioning, failed, or has already received this action. Observe rather than retrying.", false);
        try { InvokeUiAction(target, id, action, value); }
        catch (Exception ex)
        {
            uiTracker.Fail($"UI action failed: {ex.GetType().Name}: {ex.Message}");
            UpdateUiState(false);
            return ErrorResult(request, "UI_ACTION_FAILED", uiTracker.Error ?? "UI action failed.", false);
        }
        UpdateUiState(false);
        return SuccessResult(request, new { Accepted = true, BlockerId = id, Ui = uiState });
    }
}

[HarmonyPatch(typeof(InGame), nameof(InGame.ShowEventPopup))]
internal static class InformationalGameplayEventPatch
{
    [HarmonyPrefix]
    internal static void Prefix(string eventName, ref Il2CppSystem.Action? onEndCallback)
    {
        AgentBridgeMod.TagInformationalEvent(eventName, ref onEndCallback);
    }
}
