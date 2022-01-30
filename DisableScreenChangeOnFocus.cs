using HarmonyLib;

namespace TaikoMods;

[HarmonyPatch]
public class DisableScreenChangeOnFocus
{
    [HarmonyPatch(typeof(FocusManager), "OnApplicationFocus")]
    [HarmonyPrefix]
    private static bool OnApplicationFocus_Prefix(bool focusStatus)
    {
        return false;
    }
    
    [HarmonyPatch(typeof(FocusManager), "UpdateFocusManager")]
    [HarmonyPrefix]
    private static bool UpdateFocusManager_Prefix()
    {
        return false;
    }
}