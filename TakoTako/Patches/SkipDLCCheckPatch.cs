using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace TakoTako.Patches;

#if TAIKO_IL2CPP
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class SkipDLCCheckPatch
{
    private static SongSelectManager songSelectManager;
    
    [HarmonyPatch(typeof(SongSelectManager), "Start")]
    [HarmonyPostfix]
    public static void Start_Postfix(SongSelectManager __instance)
    {
        songSelectManager = __instance;
    }

    [HarmonyPatch(typeof(DLCLicenseCheckWindow), nameof(DLCLicenseCheckWindow.OpenWindow))]
    [HarmonyPostfix]
    private static void OpenWindow_Postfix(DLCLicenseCheckWindow __instance)
    {
        songSelectManager.isDLCLicenseChecking = false;
        songSelectManager.isDLCPackInfoUpdateChecked = true;
        __instance.CloseWindowImmediately();
    }
}
#endif
