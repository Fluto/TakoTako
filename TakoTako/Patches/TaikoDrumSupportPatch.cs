using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
#if TAIKO_IL2CPP
using Array = Il2CppSystem.Array;
#endif

namespace TakoTako.Patches;

[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("Member Access", "Publicizer001:Accessing a member that was not originally public")]
public class TaikoDrumSupportPatch
{
    private const float analogThreshold = 0.333f;

    [HarmonyPatch(typeof(ControllerManager), "GetAxis")]
    [HarmonyPostfix]
    private static void GetAxis_Postfix(ControllerManager __instance, ref float __result, ControllerManager.ControllerPlayerNo controllerPlayerNo, ControllerManager.Axes axis)
    {
        int controllerIndex = __instance.GetContollersIndex(controllerPlayerNo);
        if (controllerIndex <= 0 || !__instance.Controllers[controllerIndex].joystickName.Contains("Taiko"))
            return;

        var prefix = $"J{controllerIndex - 1}";
        switch (axis)
        {
            case ControllerManager.Axes.L_Horizontal:
            {
                __result = Input.GetAxis($"{prefix}H3");
                break;
            }
            case ControllerManager.Axes.L_Vertical:
            {
                __result = Input.GetAxis($"{prefix}V3");
                break;
            }
            case ControllerManager.Axes.D_Right:
            {
                var axisValue = Input.GetAxis($"{prefix}H3");
                if (axisValue > 0)
                    __result = axisValue;
                break;
            }
            case ControllerManager.Axes.D_Left:
            {
                var axisValue = Input.GetAxis($"{prefix}H3");
                if (axisValue < 0)
                    __result = -axisValue;
                break;
            }
            case ControllerManager.Axes.D_Up:
            {
                var axisValue = Input.GetAxis($"{prefix}V3");
                if (axisValue > 0)
                    __result = axisValue;
                break;
            }
            case ControllerManager.Axes.D_Down:
            {
                var axisValue = Input.GetAxis($"{prefix}V3");
                if (axisValue < 0)
                    __result = -axisValue;
                break;
            }
        }
    }

    private static unsafe 
#if TAIKO_IL2CPP
        bool[] 
#elif TAIKO_MONO
        bool[,] 
#endif
        GetPreviousButtons(ControllerManager instance)
    {
#if TAIKO_IL2CPP
        var fieldPtr = (IntPtr)AccessTools.Field(typeof(ControllerManager), "NativeFieldInfoPtr_prevButtons").GetValue(null);
        System.IntPtr nativeObject = *(System.IntPtr*) (IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase) instance) + (int) IL2CPP.il2cpp_field_get_offset(fieldPtr));
        var array = nativeObject == System.IntPtr.Zero ? (Il2CppStructArray<bool>) null : new Il2CppStructArray<bool>(nativeObject);
        return array;
        // return instance.prevButtons.Cast<ReferenceObject<bool[,]>>();
#elif TAIKO_MONO
       return instance.prevButtons;
#endif
    }
    

    [HarmonyPatch(typeof(ControllerManager), "GetButtonDown")]
    [HarmonyPostfix]
    private static void GetButtonDown_Postfix(ControllerManager __instance, ref bool __result, ControllerManager.ControllerPlayerNo controllerPlayerNo, ControllerManager.Buttons btn)
    {
        int controllerIndex = __instance.GetContollersIndex(controllerPlayerNo);
        if (controllerIndex <= 0 || !__instance.Controllers[controllerIndex].joystickName.Contains("Taiko"))
            return;

        var prefix = $"J{controllerIndex}";
        #if TAIKO_IL2CPP
        var previousButtons = GetPreviousButtons(__instance);
        int playerIndex = (int) (controllerPlayerNo - 1);
        int btnIndex = (int) btn;
        var previous = previousButtons[playerIndex * 31 + btnIndex];
        #elif TAIKO_MONO
        var previous = GetPreviousButtons(__instance)[(int) (controllerPlayerNo - 1), (int) btn];
        #endif

        __result = btn switch
        {
            ControllerManager.Buttons.D_Right => Input.GetAxis($"{prefix}H3") > analogThreshold && !previous,
            ControllerManager.Buttons.D_Left => Input.GetAxis($"{prefix}H3") < -analogThreshold && !previous,
            ControllerManager.Buttons.D_Up => Input.GetAxis($"{prefix}V3") > analogThreshold && !previous,
            ControllerManager.Buttons.D_Down => Input.GetAxis($"{prefix}V3") < -analogThreshold && !previous || Input.GetButtonDown($"{prefix}B10"),
            _ => RunMethodWithButton(btn, controllerIndex, Input.GetButtonDown)
        };
    }

    [HarmonyPatch(typeof(ControllerManager), "GetButton")]
    [HarmonyPostfix]
    private static void GetButton_Postfix(ControllerManager __instance, ref bool __result, ControllerManager.ControllerPlayerNo controllerPlayerNo, ControllerManager.Buttons btn)
    {
        int controllerIndex = __instance.GetContollersIndex(controllerPlayerNo);
        if (controllerIndex <= 0 || !__instance.Controllers[controllerIndex].joystickName.Contains("Taiko"))
            return;

        var prefix = $"J{controllerIndex}";

        __result = btn switch
        {
            ControllerManager.Buttons.D_Right => Input.GetAxis($"{prefix}H3") > analogThreshold,
            ControllerManager.Buttons.D_Left => Input.GetAxis($"{prefix}H3") < -analogThreshold,
            ControllerManager.Buttons.D_Up => Input.GetAxis($"{prefix}V3") > analogThreshold,
            ControllerManager.Buttons.D_Down => Input.GetAxis($"{prefix}V3") < -analogThreshold || Input.GetButton($"{prefix}B10"),
            _ => RunMethodWithButton(btn, controllerIndex, Input.GetButton)
        };
    }

    [HarmonyPatch(typeof(ControllerManager), "GetButtonUp")]
    [HarmonyPostfix]
    private static void GetButtonUp_Postfix(ControllerManager __instance, ref bool __result, ControllerManager.ControllerPlayerNo controllerPlayerNo, ControllerManager.Buttons btn)
    {
        int controllerIndex = __instance.GetContollersIndex(controllerPlayerNo);
        if (controllerIndex <= 0 || !__instance.Controllers[controllerIndex].joystickName.Contains("Taiko"))
            return;

        var prefix = $"J{controllerIndex}";
        
#if TAIKO_IL2CPP
        var previousButtons = GetPreviousButtons(__instance);
        int playerIndex = (int) (controllerPlayerNo - 1);
        int btnIndex = (int) btn;
        var previous = previousButtons[playerIndex * 31 + btnIndex];
#elif TAIKO_MONO
        var previous = GetPreviousButtons(__instance)[(int) (controllerPlayerNo - 1), (int) btn];
#endif

        __result = btn switch
        {
            ControllerManager.Buttons.D_Right => Input.GetAxis($"{prefix}H3") < analogThreshold && previous,
            ControllerManager.Buttons.D_Left => Input.GetAxis($"{prefix}H3") > -analogThreshold && previous,
            ControllerManager.Buttons.D_Up => Input.GetAxis($"{prefix}V3") < analogThreshold && previous,
            ControllerManager.Buttons.D_Down => (Input.GetAxis($"{prefix}V3") > -analogThreshold && previous) || Input.GetButtonUp($"{prefix}B10"),
            _ => RunMethodWithButton(btn, controllerIndex, Input.GetButtonUp)
        };
    }

    private static bool RunMethodWithButton(ControllerManager.Buttons button, int controllerIndex, Func<string, bool> function)
    {
        var nintendoLayout = Plugin.Instance.ConfigTaikoDrumUseNintendoLayout.Value;
        var prefix = $"J{controllerIndex}";

        return button switch
        {
            ControllerManager.Buttons.Menu1 => function($"{prefix}B13"),
            ControllerManager.Buttons.Menu2 => function($"{prefix}B12"),
            ControllerManager.Buttons.A => (nintendoLayout ? function($"{prefix}B2") : function($"{prefix}B1")) || function($"{prefix}B11"),
            ControllerManager.Buttons.B => (nintendoLayout ? function($"{prefix}B1") : function($"{prefix}B2")),
            ControllerManager.Buttons.X => nintendoLayout ? function($"{prefix}B3") : function($"{prefix}B0"),
            ControllerManager.Buttons.Y => nintendoLayout ? function($"{prefix}B0") : function($"{prefix}B3"),
            ControllerManager.Buttons.L1 => function($"{prefix}B4") || function($"{prefix}B6"),
            ControllerManager.Buttons.L2 => function($"{prefix}B6"),
            ControllerManager.Buttons.R1 => function($"{prefix}B5") || function($"{prefix}B7"),
            ControllerManager.Buttons.R2 => function($"{prefix}B7"),
            ControllerManager.Buttons.L3 => function($"{prefix}B10"),
            ControllerManager.Buttons.R3 => function($"{prefix}B11"),
            ControllerManager.Buttons.Start => function($"{prefix}B9"),
            ControllerManager.Buttons.Back => function($"{prefix}B8"),
            _ => false
        };
    }
}
