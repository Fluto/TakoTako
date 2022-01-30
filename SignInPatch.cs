using System.Reflection;
using HarmonyLib;
using Microsoft.Xbox;
using TaikoMods;
using UnityEngine;

namespace FlutoTaikoMods;

/// <summary>
/// This patch will address the issue where signing with GDK is done correctly
/// </summary>
[HarmonyPatch(typeof(GdkHelpers))]
[HarmonyPatch("SignIn")]
public static class SignInPatch
{
    
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(GdkHelpers __instance)
    {
        // Only apply this patch if we're on version 1.0.0
        if (Application.version != "1.0.0")
            return false;

        Plugin.Log.LogInfo("Patching sign in to force the user to be prompted to sign in");
        var methodInfo = typeof(GdkHelpers).GetMethod("SignInImpl", BindingFlags.NonPublic | BindingFlags.Instance);
        if (methodInfo == null)
        {
            Plugin.Log.LogError("Failed to patch");
            return true;
        }

        methodInfo.Invoke(__instance, new object[] {true});
        return false;
    }
}