#if TAIKO_IL2CPP
using HarmonyLib;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;

namespace TakoTako;

public static class Il2cppRedirection
{
    [CallerCount(4)]
    [CachedScanResults(MetadataInitFlagRva = 0, MetadataInitTokenRva = 0, RefRangeEnd = 504456, RefRangeStart = 504452, XrefRangeEnd = 504452, XrefRangeStart = 504452)]
    public static unsafe void GetMusicInfoExAllIl2cpp(this PlayDataManager instance, int playerId, out Il2CppStructArray<MusicInfoEx> dst)
    {
        IL2CPP.Il2CppObjectBaseToPtrNotNull(instance);
        System.IntPtr* numPtr = stackalloc System.IntPtr[2];
        numPtr[0] = (System.IntPtr) (&playerId);
        var outPtr = IntPtr.Zero;
        numPtr[1] = (System.IntPtr) (&outPtr);
        System.IntPtr exc = System.IntPtr.Zero;
        var fieldInfo = AccessTools.Field(typeof(PlayDataManager), "NativeMethodInfoPtr_GetMusicInfoExAll_Public_Void_Int32_byref_Il2CppStructArray_1_MusicInfoEx_0") ??
            AccessTools.Field(typeof(PlayDataManager), "NativeMethodInfoPtr_GetMusicInfoExAll_Public_Void_Int32_byref_ArrayOf_MusicInfoEx_0");
        var field = (System.IntPtr)fieldInfo.GetValue(null);
        IL2CPP.il2cpp_runtime_invoke(field, IL2CPP.Il2CppObjectBaseToPtrNotNull(instance), (void**) numPtr, ref exc);
        dst = new Il2CppStructArray<MusicInfoEx>(outPtr);
        Il2CppException.RaiseExceptionIfNecessary(exc);
    }
    
    public static void GetPlayerInfoRemake(this PlayDataManager instance, int playerId, out PlayerInfo dst) => dst = instance.saveData[playerId].dataBody.playerInfo;
    
    public static bool SetPlayerInfoRemake(this PlayDataManager instance,  int playerId, ref PlayerInfo src, bool immediate = false)
    {
        return SetPlayerInfoRemake(instance, playerId, ref src, true, !immediate);
    }

    public static bool SetPlayerInfoRemake(this PlayDataManager instance,  int playerId, ref PlayerInfo src, bool savemode, bool async = true)
    {
        src.checkNull();
        if (!src.IsValid())
            return false;
        instance.saveData[playerId].dataBody.playerInfo = src;
        if (savemode && playerId == 0)
        {
            if (async)
                instance.SaveObjectAsync();
            else
                instance.SaveObject();
        }
        instance.isSaveDataChanged = true;
        return true;
    }
    
    public static void GetRankMatchSeasonRecordInfoRemake(
        this PlayDataManager instance,
        int playerId,
        int seasonId,
        out RankMatchSeasonRecordInfo dst)
    {
        if (instance.saveData[playerId].dataBody.rankMatchSeasonRecordInfo == null)
            instance.saveData[playerId].ResetRankMatchSeasonRecordInfo();
        dst = instance.saveData[playerId].dataBody.rankMatchSeasonRecordInfo[seasonId];
    }
    
    public static void CopySettingsRemake(this EnsoDataManager ensoDataManager, out EnsoData.Settings dst) => ensoDataManager.ensoSettings.CopyRemake(out dst);

    public static void GetSystemOptionRemake(this PlayDataManager instance, out SystemOption dst, bool forceReload = false)
    {
        dst = instance.systemOption;
    }
    
    public static void SetSettingsRemake(this EnsoDataManager instance, ref EnsoData.Settings src)
    {
        src.CopyRemake(out var value);
        instance.ensoSettings = value;
    }
    
    public static void CopyRemake(this EnsoData.Settings settings, out EnsoData.Settings dst)
    {
        dst = settings;
        for (int i = 0; i < settings.ensoPlayerSettings.Length; i++)
            dst.ensoPlayerSettings = settings.ensoPlayerSettings;
        
        for (int i = 0; i < settings.partsSettings.Length; i++)
            dst.partsSettings = settings.partsSettings;


        DebugCopyRemake(settings.debugSettings, out var value);
        settings.debugSettings = value;
    }
    public static void DebugCopyRemake(this EnsoData.DebugSettings settings, out EnsoData.DebugSettings dst)
    {
        dst = settings;
        
        for (int i = 0; i < settings.debugPlayerSettings.Length; i++)
            dst.debugPlayerSettings = settings.debugPlayerSettings;
    }
}
#endif
