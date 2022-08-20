using HarmonyLib;
#if TAIKO_MONO
using Microsoft.Xbox;
#endif
#if TAIKO_IL2CPP
using Il2CppMicrosoft.Xbox;
#endif

namespace TakoTako.Patches;

/// <summary>
/// This patch will address the issue where signing with GDK is done correctly
/// </summary>
public static class SignInPatch
{
    [HarmonyPatch(typeof(GdkHelpers))]
    [HarmonyPatch("SignIn")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool SignIn_Prefix(GdkHelpers __instance)
    {
        Plugin.Log.LogInfo("Patching sign in to force the user to be prompted to sign in");
        __instance.SignInImpl(true);
        return false;
    }
}
