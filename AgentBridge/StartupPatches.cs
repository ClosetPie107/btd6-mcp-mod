using BTD_Mod_Helper;
using HarmonyLib;
using Il2CppAssets.Scripts.Unity.Scenes;
using Il2CppAssets.Scripts.Unity.UI_New.Main;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;

namespace AgentBridge;

[HarmonyPatch(typeof(PopupScreen), nameof(PopupScreen.ShowModderPopup))]
internal static class ShowModderPopupPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task<ModdingPopupChoice> __result)
    {
        ModHelper.Msg<AgentBridgeMod>("Auto-dismissing modder warning popup with Continue.");
        __result = Il2CppSystem.Threading.Tasks.Task.FromResult(ModdingPopupChoice.Continue);
        return false;
    }
}

[HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.Update))]
internal static class TitleScreenAutoStartPatch
{
    private static bool _clicked;

    internal static void Reset() => _clicked = false;

    [HarmonyPostfix]
    internal static void Postfix(TitleScreen __instance)
    {
        if (_clicked || __instance == null || __instance.playButtonClicked)
            return;

        if (__instance.startButton != null && __instance.startButton.isActiveAndEnabled &&
            __instance.startButton.IsInteractable() && PopupScreen.instance?.IsPopupActiveOrLoading() != true)
        {
            _clicked = true;
            ModHelper.Msg<AgentBridgeMod>("Title screen ready: auto-clicking Start button.");
            __instance.OnPlayButtonClicked();
        }
    }
}

[HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.Awake))]
internal static class TitleScreenAwakePatch
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        TitleScreenAutoStartPatch.Reset();
    }
}
