using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace TaikoMods;

[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class SkipSplashScreen
{
    /// <summary>
    /// Simply load the next scene, I don't think this scene does anything specific?
    /// </summary>
    [HarmonyPatch(typeof(BootManager), "Start")]
    [HarmonyPostfix]
    private static void BootManager_Postfix(BootManager __instance)
    {
        TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySceneManager.ChangeScene("Title", false);
        __instance.gameObject.SetActive(false);
    }
}