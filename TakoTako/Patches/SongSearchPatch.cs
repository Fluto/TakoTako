using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TakoTako.Patches;

[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class SongSearchPatch
{
    private static bool disableUIControls = false;
    private static string filterString = string.Empty;

    private static SongSearchInterop currentInterop;

    public static ManualLogSource Log => Plugin.Log;

    private static bool didSetup;

    private const string SongSelectSceneName = "SongSelect";
    
    [HarmonyPatch(typeof(CommonObjects), nameof(CommonObjects.Awake))]
    [HarmonyPostfix]
    private static void Awake_Postfix(SongSelectManager __instance)
    {
        if (didSetup)
            return;

        didSetup = true;

        var myLoadedAssetBundle = AssetBundle.LoadFromFile(Path.Combine(@$"{Environment.CurrentDirectory}\BepInEx\plugins\{PluginInfo.PLUGIN_GUID}", "content"));
        if (myLoadedAssetBundle == null)
        {
            Log.LogError("Failed to load AssetBundle!");
        }
        else
        {
            // var prefabSongSearchInjection = myLoadedAssetBundle.LoadAsset("SongSearchInjection");
            var canvasPrefab = Object.Instantiate(myLoadedAssetBundle.LoadAsset("SongSearchCanvas"));
            // UnityEngine.Object obj = Instantiate(assetBundle.LoadAllAssets(Il2CppType.Of<GameObject>())[0]);
            // GameObject instance = GameObject.Find(canvasPrefab.name);
            var canvasUI = canvasPrefab.Cast<GameObject>();
            Object.DontDestroyOnLoad(canvasUI);
            
            var songSearchUI = canvasUI.AddComponent<SongSearchUI>();
            songSearchUI.gameObject.SetActive(false);

            SceneManager.activeSceneChanged += (UnityAction<Scene, Scene>)SceneManagerOnSceneChanged;

            void SceneManagerOnSceneChanged(Scene scene, Scene scene1)
            {
                if (!scene1.name.Equals(SongSelectSceneName, StringComparison.InvariantCultureIgnoreCase))
                {
                    songSearchUI.gameObject.SetActive(false);
                    return;
                }

                Plugin.Instance.StartCustomCoroutine(SetupSearch());

                IEnumerator SetupSearch()
                {
                    // wait 2 frames for good luck
                    yield return null;
                    yield return null;
                    songSearchUI.gameObject.SetActive(true);
                    songSearchUI.Setup();
                    SetupSongInjection(songSearchUI);
                }
            }
        }

        static void SetupSongInjection(SongSearchUI songSearchUI)
        {
            var songSearchInterop = SongSearchInterop.Instance;
            songSearchUI.Filter += songSearchInterop.Filter;
            songSearchUI.OnShowSongSearchUI += songSearchInterop.OnShowSongSearchUI;
            songSearchUI.OnHideSongSearchUI += songSearchInterop.OnHideSongSearchUI;
            songSearchUI.SetResultsCallback += x => songSearchInterop.SetResultsCallback(x);
            songSearchUI.AfterSetup();
        }
    }

    [HarmonyPatch(typeof(SongSelectManager), "UpdateSongSelect")]
    [HarmonyPrefix]
    private static bool UpdateSongSelect_Prefix() => !disableUIControls;

    private static readonly System.Collections.Generic.List<SongSelectManager.Song> originalSongs = new System.Collections.Generic.List<SongSelectManager.Song>();

    /// <summary>
    /// The idea here is to call the sorting method, store the previous song list, modify it with our filter, and in the post method restore that song list
    /// </summary>
    [HarmonyPatch(typeof(SongSelectManager), nameof(SongSelectManager.SortSongList),
        typeof(DataConst.SongSortCourse),
        typeof(DataConst.SongSortType),
        typeof(DataConst.SongFilterType),
        typeof(DataConst.SongFilterTypeFavorite))]
    [HarmonyPrefix]
    private static void SortSongList_Prefix(
        SongSelectManager __instance,
#if TAIKO_IL2CPP
        out Il2CppSystem.Collections.Generic.List<SongSelectManager.Song> __state,
#elif TAIKO_MONO
        out List<SongSelectManager.Song> __state,
#endif
        DataConst.SongSortCourse sortDifficulty,
        DataConst.SongSortType sortType,
        DataConst.SongFilterType filterType,
        DataConst.SongFilterTypeFavorite filterTypeFavorite)
    {
        __state = new List<SongSelectManager.Song>();
        
        foreach (var song in __instance.UnsortedSongList)
        {
            if (originalSongs.FindIndex(x => x.Id == song.Id) < 0)
                originalSongs.Add(song);
        }

        foreach (var originalSong in originalSongs)
            __state.Add(originalSong);

        if (string.IsNullOrWhiteSpace(filterString))
        {
            SongSearchInterop.Instance.ResultsCallback?.Invoke(new SearchResults
            {
                ResultsCount = __state.Count
            });
            return;
        }

        var compareOptions = CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
        var compareInfo = CultureInfo.InvariantCulture.CompareInfo;

        for (int i = __state.Count - 1; i >= 0; i--)
        {
            var song = (SongSelectManager.Song)__state[(Index)i];
            if (compareInfo.IndexOf(song.TitleText ?? string.Empty, filterString, compareOptions) <= -1
                && compareInfo.IndexOf(song.SubText ?? string.Empty, filterString, compareOptions) <= -1
                && compareInfo.IndexOf(song.DetailText ?? string.Empty, filterString, compareOptions) <= -1)
            {
                __state.RemoveAt(i);
            }
        }
        

        SongSearchInterop.Instance.ResultsCallback?.Invoke(new SearchResults
        {
            ResultsCount = __state.Count
        });

        // fallback to a default song
        if (__state.Count == 0 && originalSongs.Count > 0)
        {
#if TAIKO_IL2CPP
            __state.Add((SongSelectManager.Song)originalSongs[0]);
#elif TAIKO_MONO
            __state.Add(cloned[0]);
#endif
        }
    }

    [HarmonyPatch(typeof(SongSelectManager), nameof(SongSelectManager.SortSongList),
        typeof(DataConst.SongSortCourse),
        typeof(DataConst.SongSortType),
        typeof(DataConst.SongFilterType),
        typeof(DataConst.SongFilterTypeFavorite))]
    [HarmonyPostfix]
    private static void SortSongList_Postfix(SongSelectManager __instance,
#if TAIKO_IL2CPP
        Il2CppSystem.Collections.Generic.List<SongSelectManager.Song> __state,
#elif TAIKO_MONO
        List<SongSelectManager.Song> __state,
#endif
        DataConst.SongSortCourse sortDifficulty,
        DataConst.SongSortType sortType,
        DataConst.SongFilterType filterType,
        DataConst.SongFilterTypeFavorite filterTypeFavorite)
    {
        __instance.UnsortedSongList = __state;
        UnityEngine.Debug.Log("Entries " + __state.Count);
    }

    public class SongSearchInterop
    {
        private static SongSearchInterop instance;
        public static SongSearchInterop Instance => instance ??= new SongSearchInterop();

        private static ManualLogSource Log => Plugin.Log;
        private SongSelectManager songSelectManager;
        public Action<SearchResults> ResultsCallback;

        private SongSearchInterop() { }

        public void Filter(string input)
        {
            filterString = input;

            if (songSelectManager == null)
                songSelectManager = Object.FindObjectOfType<SongSelectManager>();

            if (songSelectManager == null)
                return;

            songSelectManager.SortSongList(songSelectManager.CurrentSortDifficulty, songSelectManager.CurrentSortType, songSelectManager.CurrentFilterType,
                songSelectManager.CurrentFilterTypeFavorite);
        }

        public void OnShowSongSearchUI()
        {
            disableUIControls = true;
        }

        public void OnHideSongSearchUI()
        {
            disableUIControls = false;
        }

        public void SetResultsCallback(Action<SearchResults> resultsCallback)
        {
            ResultsCallback = resultsCallback;
        }
    }
}
