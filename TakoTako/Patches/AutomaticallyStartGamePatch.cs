using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Il2CppMicrosoft.Xbox;

namespace TakoTako.Patches;

[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class AutomaticallyStartGamePatch
{
    /// <summary>
    /// Simply load the next scene, I don't think this scene does anything specific?
    /// </summary>
    [HarmonyPatch(typeof(GameTitleManager), nameof(GameTitleManager.updateTitleMain))]
    [HarmonyPrefix]
    private static bool updateTitleMain_Prefix(GameTitleManager __instance)
    {
        if (GdkHelpers.Helpers.IsInvited() && GameTitleManager.m_loadState == GameTitleManager.LoadState.LoadCompleted)
        {
            // ignored
        }
        else
        {
            TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.CommonSePlay("don", false);
            __instance.switchNextEULAState();
            return false;
        }

        return true;
    }
}
