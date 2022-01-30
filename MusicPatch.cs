using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace TaikoMods;

/// <summary>
/// This will allow custom songs to be read in
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class MusicPatch
{
    public static int SaveDataMax => DataConst.MusicMax;

    public static string MusicTrackDirectory => Plugin.Instance.ConfigSongDirectory.Value;
    public static string SaveFilePath => $"{Plugin.Instance.ConfigSaveDirectory.Value}/save.json";
    private const string SongDataFileName = "data.json";

    public static ManualLogSource Log => Plugin.Log;

    public static void Setup(Harmony harmony)
    {
        CreateDirectoryIfNotExist(Path.GetDirectoryName(SaveFilePath));
        CreateDirectoryIfNotExist(MusicTrackDirectory);

        PatchManual(harmony);

        void CreateDirectoryIfNotExist(string path)
        {
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path))
            {
                Log.LogInfo($"Creating path at {path}");
                Directory.CreateDirectory(path);
            }
        }
    }

    private static void PatchManual(Harmony harmony)
    {
        var original = typeof(FumenLoader).GetNestedType("PlayerData", BindingFlags.NonPublic).GetMethod("Read");
        var prefix = typeof(MusicPatch).GetMethod(nameof(Read_Prefix), BindingFlags.Static | BindingFlags.NonPublic);

        harmony.Patch(original, new HarmonyMethod(prefix));
    }

    #region Custom Save Data

    private static CustomMusicSaveDataBody _customSaveData;
    private static readonly DataContractJsonSerializer saveDataSerializer = new(typeof(CustomMusicSaveDataBody));

    private static CustomMusicSaveDataBody GetCustomSaveData()
    {
        if (_customSaveData != null)
            return _customSaveData;

        Log.LogInfo("Loading custom save data");
        var savePath = SaveFilePath;
        try
        {
            CustomMusicSaveDataBody data;
            if (!File.Exists(savePath))
            {
                data = new CustomMusicSaveDataBody();
                using var fileStream = File.OpenWrite(savePath);
                saveDataSerializer.WriteObject(fileStream, data);
            }
            else
            {
                using var fileStream = File.OpenRead(savePath);
                data = (CustomMusicSaveDataBody) saveDataSerializer.ReadObject(fileStream);

                data.CustomTrackToEnsoRecordInfo ??= new Dictionary<int, EnsoRecordInfo[]>();
                data.CustomTrackToMusicInfoEx ??= new Dictionary<int, MusicInfoEx>();
            }

            _customSaveData = data;
            return data;
        }
        catch (Exception e)
        {
            Log.LogError($"Could not load custom data, creating a fresh one\n {e}");
        }

        try
        {
            var data = new CustomMusicSaveDataBody();
            using var fileStream = File.OpenWrite(savePath);
            saveDataSerializer.WriteObject(fileStream, data);
        }
        catch (Exception e)
        {
            Log.LogError($"Cannot save data at path {savePath}\n {e}");
        }

        return new CustomMusicSaveDataBody();
    }

    private static void SaveCustomData()
    {
        if (_customSaveData == null)
            return;

        Log.LogInfo("Saving custom save data");
        try
        {
            var data = GetCustomSaveData();
            var savePath = SaveFilePath;
            using var fileStream = File.OpenWrite(savePath);
            saveDataSerializer.WriteObject(fileStream, data);
        }
        catch (Exception e)
        {
            Log.LogError($"Could not save custom data \n {e}");
        }
    }

    #endregion

    #region Load Custom Songs

    private static readonly DataContractJsonSerializer customSongSerializer = new(typeof(CustomSong));
    private static List<CustomSong> customSongsList;
    private static readonly Dictionary<string, CustomSong> idToSong = new Dictionary<string, CustomSong>();
    private static readonly Dictionary<int, CustomSong> uniqueIdToSong = new Dictionary<int, CustomSong>();

    public static List<CustomSong> GetCustomSongs()
    {
        if (customSongsList != null)
            return customSongsList;

        if (!Directory.Exists(MusicTrackDirectory))
        {
            Log.LogError($"Cannot find {MusicTrackDirectory}");
            customSongsList = new List<CustomSong>();
            return customSongsList;
        }

        customSongsList = new List<CustomSong>();

        foreach (var musicDirectory in Directory.GetDirectories(MusicTrackDirectory))
        {
            var files = Directory.GetFiles(musicDirectory);
            var customSongPath = files.FirstOrDefault(x => Path.GetFileName(x) == SongDataFileName);
            if (string.IsNullOrWhiteSpace(customSongPath))
                continue;

            using var fileStream = File.OpenRead(customSongPath);
            var song = (CustomSong) customSongSerializer.ReadObject(fileStream);
            if (song == null)
            {
                Log.LogError($"Cannot read {customSongPath}");
                continue;
            }

            if (idToSong.ContainsKey(song.id))
            {
                Log.LogError($"Cannot load song {song.songName.text} with ID {song.uniqueId} as it clashes with another, skipping it...");
                continue;
            }
            
            if (uniqueIdToSong.ContainsKey(song.uniqueId))
            {
                var uniqueIdTest = song.id.GetHashCode();
                while (uniqueIdToSong.ContainsKey(uniqueIdTest))
                    uniqueIdTest++;

                Log.LogWarning($"Found song {song.songName.text} with an existing ID {song.uniqueId}, changing it to {uniqueIdTest}");
                song.uniqueId = uniqueIdTest;
            }

            customSongsList.Add(song);
            idToSong[song.id] = song;
            uniqueIdToSong[song.uniqueId] = song;            
            Log.LogInfo($"Added Song {song.songName.text}:{song.id}:{song.uniqueId}");
        }

        if (customSongsList.Count == 0)
            Log.LogInfo($"No tracks found");

        return customSongsList;
    }

    #endregion

    #region Loading and Initializing Data

    /// <summary>
    /// This will handle loading the meta data of tracks
    /// </summary>
    [HarmonyPatch(typeof(MusicDataInterface))]
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPatch(new[] {typeof(string)})]
    [HarmonyPostfix]
    private static void MusicDataInterface_Postfix(MusicDataInterface __instance, string path)
    {
        // This is where the metadata for tracks are read in our attempt to allow custom tracks will be to add additional metadata to the list that is created
        Log.LogInfo("Injecting custom songs");

        var customSongs = GetCustomSongs();

        if (customSongs.Count == 0)
            return;
        // now that we have loaded this json, inject it into the existing `musicInfoAccessers`
        var musicInfoAccessors = __instance.musicInfoAccessers;

        #region Logic from the original constructor

        for (int i = 0; i < customSongs.Count; i++)
        {
            musicInfoAccessors.Add(new MusicDataInterface.MusicInfoAccesser(customSongs[i].uniqueId, customSongs[i].id, $"song_{customSongs[i].id}", customSongs[i].order, customSongs[i].genreNo,
                true, false, 0, false, 0, new bool[5]
                {
                    customSongs[i].branchEasy,
                    customSongs[i].branchNormal,
                    customSongs[i].branchHard,
                    customSongs[i].branchMania,
                    customSongs[i].branchUra
                }, new int[5]
                {
                    customSongs[i].starEasy,
                    customSongs[i].starNormal,
                    customSongs[i].starHard,
                    customSongs[i].starMania,
                    customSongs[i].starUra
                }, new int[5]
                {
                    customSongs[i].shinutiEasy,
                    customSongs[i].shinutiNormal,
                    customSongs[i].shinutiHard,
                    customSongs[i].shinutiMania,
                    customSongs[i].shinutiUra
                }, new int[5]
                {
                    customSongs[i].shinutiEasyDuet,
                    customSongs[i].shinutiNormalDuet,
                    customSongs[i].shinutiHardDuet,
                    customSongs[i].shinutiManiaDuet, 
                    customSongs[i].shinutiUraDuet
                }, new int[5]
                {
                    customSongs[i].scoreEasy,
                    customSongs[i].scoreNormal,
                    customSongs[i].scoreHard,
                    customSongs[i].scoreMania,
                    customSongs[i].scoreUra
                }));
        }

        #endregion

        // sort this 
        musicInfoAccessors.Sort((MusicDataInterface.MusicInfoAccesser a, MusicDataInterface.MusicInfoAccesser b) => a.Order - b.Order);
    }


    /// <summary>
    /// This will handle loading the preview data of tracks
    /// </summary>
    [HarmonyPatch(typeof(SongDataInterface))]
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPatch(new[] {typeof(string)})]
    [HarmonyPostfix]
    private static void SongDataInterface_Postfix(SongDataInterface __instance, string path)
    {
        // This is where the metadata for tracks are read in our attempt to allow custom tracks will be to add additional metadata to the list that is created
        Log.LogInfo("Injecting custom song preview data");
        var customSongs = GetCustomSongs();

        if (customSongs.Count == 0)
            return;

        // now that we have loaded this json, inject it into the existing `songInfoAccessers`
        var musicInfoAccessors = __instance.songInfoAccessers;

        foreach (var customTrack in customSongs)
        {
            musicInfoAccessors.Add(new SongDataInterface.SongInfoAccesser(customTrack.id, customTrack.previewPos, customTrack.fumenOffsetPos));
        }
    }


    /// <summary>
    /// This will handle loading the localisation of tracks
    /// </summary>
    [HarmonyPatch(typeof(WordDataInterface))]
    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPatch(new[] {typeof(string), typeof(string)})]
    [HarmonyPostfix]
    private static void WordDataInterface_Postfix(WordDataInterface __instance, string path, string language)
    {
        // This is where the metadata for tracks are read in our attempt to allow custom tracks will be to add additional metadata to the list that is created
        var customSongs = GetCustomSongs();

        if (customSongs.Count == 0)
            return;

        // now that we have loaded this json, inject it into the existing `songInfoAccessers`
        var musicInfoAccessors = __instance.wordListInfoAccessers;

        foreach (var customTrack in customSongs)
        {
            musicInfoAccessors.Add(new WordDataInterface.WordListInfoAccesser($"song_{customTrack.id}", customTrack.songName.text, customTrack.songName.font));
            musicInfoAccessors.Add(new WordDataInterface.WordListInfoAccesser($"song_sub_{customTrack.id}", customTrack.songSubtitle.text, customTrack.songSubtitle.font));
            musicInfoAccessors.Add(new WordDataInterface.WordListInfoAccesser($"song_detail_{customTrack.id}", customTrack.songDetail.text, customTrack.songDetail.font));
        }
    }

    #endregion

    #region Loading / Save Custom Save Data

    /// <summary>
    /// When loading, make sure to ignore custom tracks, as their IDs will be different
    /// </summary>
    [HarmonyPatch(typeof(SongSelectManager), "LoadSongList")]
    [HarmonyPrefix]
    private static bool LoadSongList_Prefix(SongSelectManager __instance)
    {
        #region Edited Code

        Log.LogInfo("Loading custom save");
        var customData = GetCustomSaveData();

        #endregion

        #region Setup instanced variables / methods

        var playDataMgr = (PlayDataManager) typeof(SongSelectManager).GetField("playDataMgr", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
        var musicInfoAccess = (MusicDataInterface.MusicInfoAccesser[]) typeof(SongSelectManager).GetField("musicInfoAccess", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
        var enableKakuninSong = (bool) (typeof(SongSelectManager).GetField("enableKakuninSong", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) ?? false);

        var getLocalizedTextMethodInfo = typeof(SongSelectManager).GetMethod("GetLocalizedText", BindingFlags.NonPublic | BindingFlags.Instance);
        var getLocalizedText = (string x) => (string) getLocalizedTextMethodInfo?.Invoke(__instance, new object[] {x, string.Empty});

        var updateSortCategoryInfoMethodInfo = typeof(SongSelectManager).GetMethod("UpdateSortCategoryInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        var updateSortCategoryInfo = (DataConst.SongSortType x) => updateSortCategoryInfoMethodInfo?.Invoke(__instance, new object[] {x});

        #endregion

        if (playDataMgr == null)
        {
            Log.LogError("Could not find playDataMgr");
            return true;
        }

        __instance.UnsortedSongList.Clear();
        playDataMgr.GetMusicInfoExAll(0, out var dst);
        playDataMgr.GetPlayerInfo(0, out var _);
        _ = TaikoSingletonMonoBehaviour<MultiplayManager>.Instance.newFriends.Count;
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < musicInfoAccess.Length; j++)
            {
                bool flag = false;
                playDataMgr.GetUnlockInfo(0, DataConst.ItemType.Music, musicInfoAccess[j].UniqueId, out var dst3);
                if (!dst3.isUnlock && musicInfoAccess[j].Price != 0)
                {
                    flag = true;
                }

                if (!enableKakuninSong && musicInfoAccess[j].IsKakuninSong())
                {
                    flag = true;
                }

                if (flag || musicInfoAccess[j].GenreNo != i)
                {
                    continue;
                }

                SongSelectManager.Song song2 = new SongSelectManager.Song();
                song2.PreviewIndex = j;
                song2.Id = musicInfoAccess[j].Id;
                song2.TitleKey = "song_" + musicInfoAccess[j].Id;
                song2.SubKey = "song_sub_" + musicInfoAccess[j].Id;
                song2.RubyKey = "song_detail_" + musicInfoAccess[j].Id;
                song2.UniqueId = musicInfoAccess[j].UniqueId;
                song2.SongGenre = musicInfoAccess[j].GenreNo;
                song2.ListGenre = i;
                song2.Order = musicInfoAccess[j].Order;
                song2.TitleText = getLocalizedText("song_" + song2.Id);
                song2.SubText = getLocalizedText("song_sub_" + song2.Id);
                song2.DetailText = getLocalizedText("song_detail_" + song2.Id);
                song2.Stars = musicInfoAccess[j].Stars;
                song2.Branches = musicInfoAccess[j].Branches;
                song2.HighScores = new SongSelectManager.Score[5];
                song2.HighScores2P = new SongSelectManager.Score[5];
                song2.DLC = musicInfoAccess[j].IsDLC;
                song2.Price = musicInfoAccess[j].Price;
                song2.IsCap = musicInfoAccess[j].IsCap;
                if (TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.SongData.GetInfo(song2.Id) != null)
                {
                    song2.AudioStartMS = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.SongData.GetInfo(song2.Id).PreviewPos;
                }
                else
                {
                    song2.AudioStartMS = 0;
                }

                if (dst != null)
                {
                    #region Edited Code

                    MusicInfoEx data;

                    if (musicInfoAccess[j].UniqueId >= SaveDataMax)
                        customData.CustomTrackToMusicInfoEx.TryGetValue(musicInfoAccess[j].UniqueId, out data);
                    else
                        data = dst[musicInfoAccess[j].UniqueId];
                    song2.Favorite = data.favorite;
                    song2.NotPlayed = new bool[5];
                    song2.NotCleared = new bool[5];
                    song2.NotFullCombo = new bool[5];
                    song2.NotDondaFullCombo = new bool[5];
                    song2.NotPlayed2P = new bool[5];
                    song2.NotCleared2P = new bool[5];
                    song2.NotFullCombo2P = new bool[5];
                    song2.NotDondaFullCombo2P = new bool[5];
                    bool isNew = data.isNew;

                    #endregion

                    for (int k = 0; k < 5; k++)
                    {
                        playDataMgr.GetPlayerRecordInfo(0, musicInfoAccess[j].UniqueId, (EnsoData.EnsoLevelType) k, out var dst4);
                        song2.NotPlayed[k] = dst4.playCount <= 0;
                        song2.NotCleared[k] = dst4.crown < DataConst.CrownType.Silver;
                        song2.NotFullCombo[k] = dst4.crown < DataConst.CrownType.Gold;
                        song2.NotDondaFullCombo[k] = dst4.crown < DataConst.CrownType.Rainbow;
                        song2.HighScores[k].hiScoreRecordInfos = dst4.normalHiScore;
                        song2.HighScores[k].crown = dst4.crown;
                        playDataMgr.GetPlayerRecordInfo(1, musicInfoAccess[j].UniqueId, (EnsoData.EnsoLevelType) k, out var dst5);
                        song2.NotPlayed2P[k] = dst5.playCount <= 0;
                        song2.NotCleared2P[k] = dst4.crown < DataConst.CrownType.Silver;
                        song2.NotFullCombo2P[k] = dst5.crown < DataConst.CrownType.Gold;
                        song2.NotDondaFullCombo2P[k] = dst5.crown < DataConst.CrownType.Rainbow;
                        song2.HighScores2P[k].hiScoreRecordInfos = dst5.normalHiScore;
                        song2.HighScores2P[k].crown = dst5.crown;
                    }

                    song2.NewSong = isNew && (song2.DLC || song2.Price > 0);
                }

                __instance.UnsortedSongList.Add(song2);
            }
        }

        var unsortedSongList = (from song in __instance.UnsortedSongList
            orderby song.SongGenre, song.Order
            select song).ToList();
        typeof(SongSelectManager).GetProperty(nameof(SongSelectManager.UnsortedSongList), BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance)?.SetValue(__instance, unsortedSongList);

        var songList = new List<SongSelectManager.Song>(__instance.UnsortedSongList);
        typeof(SongSelectManager).GetProperty(nameof(SongSelectManager.SongList), BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance)?.SetValue(__instance, songList);

        updateSortCategoryInfo(DataConst.SongSortType.Genre);
        return false;
    }

    /// <summary>
    /// When saving favourite tracks, save the custom ones too
    /// </summary>
    [HarmonyPatch(typeof(SongSelectManager), "SaveFavotiteSongs")]
    [HarmonyPrefix]
    private static bool SaveFavotiteSongs_Prefix(SongSelectManager __instance)
    {
        var playDataMgr = (PlayDataManager) typeof(SongSelectManager).GetField("playDataMgr", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);

        playDataMgr.GetMusicInfoExAll(0, out var dst);
        var customSaveData = GetCustomSaveData();

        bool saveCustomData = false;
        int num = 0;
        foreach (SongSelectManager.Song unsortedSong in __instance.UnsortedSongList)
        {
            num++;
            if (unsortedSong.UniqueId < SaveDataMax)
            {
                dst[unsortedSong.UniqueId].favorite = unsortedSong.Favorite;
                playDataMgr.SetMusicInfoEx(0, unsortedSong.UniqueId, ref dst[unsortedSong.UniqueId], num >= __instance.UnsortedSongList.Count);
            }
            else
            {
                customSaveData.CustomTrackToMusicInfoEx.TryGetValue(unsortedSong.UniqueId, out var data);
                saveCustomData |= data.favorite != unsortedSong.Favorite;
                data.favorite = unsortedSong.Favorite;
                customSaveData.CustomTrackToMusicInfoEx[unsortedSong.UniqueId] = data;
            }
        }

        if (saveCustomData)
            SaveCustomData();

        return false;
    }

    /// <summary>
    /// When loading the song, mark the custom song as not new
    /// </summary>
    [HarmonyPatch(typeof(CourseSelect), "EnsoConfigSubmit")]
    [HarmonyPrefix]
    private static bool EnsoConfigSubmit_Prefix(CourseSelect __instance)
    {
        var songInfoType = typeof(CourseSelect).GetNestedType("SongInfo", BindingFlags.NonPublic);
        var scoreType = typeof(CourseSelect).GetNestedType("Score", BindingFlags.NonPublic);
        var playerTypeEnumType = typeof(CourseSelect).GetNestedType("PlayerType", BindingFlags.NonPublic);

        var settings = (EnsoData.Settings) typeof(CourseSelect).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var playDataManager = (PlayDataManager) typeof(CourseSelect).GetField("playDataManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var ensoDataManager = (EnsoDataManager) typeof(CourseSelect).GetField("ensoDataManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);

        var selectedSongInfo = typeof(CourseSelect).GetField("selectedSongInfo", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var ensoMode = (EnsoMode) typeof(CourseSelect).GetField("ensoMode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var ensoMode2P = (EnsoMode) typeof(CourseSelect).GetField("ensoMode2P", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var selectedCourse = (int) typeof(CourseSelect).GetField("selectedCourse", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var selectedCourse2P = (int) typeof(CourseSelect).GetField("selectedCourse2P", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var status = (SongSelectStatus) typeof(CourseSelect).GetField("status", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);

        var SetSaveDataEnsoModeMethodInfo = typeof(CourseSelect).GetMethod("SetSaveDataEnsoMode", BindingFlags.NonPublic | BindingFlags.Instance);
        var SetSaveDataEnsoMode = (object x) => (string) SetSaveDataEnsoModeMethodInfo?.Invoke(__instance, new object[] {x});

        var songUniqueId = (int) songInfoType.GetField("UniqueId").GetValue(selectedSongInfo);

        void SetSettings() => typeof(CourseSelect).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, settings);

        settings.ensoType = EnsoData.EnsoType.Normal;
        settings.rankMatchType = EnsoData.RankMatchType.None;
        settings.musicuid = (string) songInfoType.GetField("Id").GetValue(selectedSongInfo);
        settings.musicUniqueId = songUniqueId;
        settings.genre = (EnsoData.SongGenre) songInfoType.GetField("SongGenre").GetValue(selectedSongInfo);
        settings.playerNum = 1;
        settings.ensoPlayerSettings[0].neiroId = ensoMode.neiro;
        settings.ensoPlayerSettings[0].courseType = (EnsoData.EnsoLevelType) selectedCourse;
        settings.ensoPlayerSettings[0].speed = ensoMode.speed;
        settings.ensoPlayerSettings[0].dron = ensoMode.dron;
        settings.ensoPlayerSettings[0].reverse = ensoMode.reverse;
        settings.ensoPlayerSettings[0].randomlv = ensoMode.randomlv;
        settings.ensoPlayerSettings[0].special = ensoMode.special;

        var array = (Array) songInfoType.GetField("HighScores").GetValue(selectedSongInfo);
        settings.ensoPlayerSettings[0].hiScore = ((HiScoreRecordInfo) scoreType.GetField("hiScoreRecordInfos").GetValue(array.GetValue(selectedCourse))).score;

        SetSettings();
        if (status.Is2PActive)
        {
            settings.ensoPlayerSettings[1].neiroId = ensoMode2P.neiro;
            settings.ensoPlayerSettings[1].courseType = (EnsoData.EnsoLevelType) selectedCourse2P;
            settings.ensoPlayerSettings[1].speed = ensoMode2P.speed;
            settings.ensoPlayerSettings[1].dron = ensoMode2P.dron;
            settings.ensoPlayerSettings[1].reverse = ensoMode2P.reverse;
            settings.ensoPlayerSettings[1].randomlv = ensoMode2P.randomlv;
            settings.ensoPlayerSettings[1].special = ensoMode2P.special;
            TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData.GetPlayerRecordInfo(1, songUniqueId, (EnsoData.EnsoLevelType) selectedCourse2P, out var dst);
            settings.ensoPlayerSettings[1].hiScore = dst.normalHiScore.score;
            settings.playerNum = 2;
        }

        settings.debugSettings.isTestMenu = false;
        settings.rankMatchType = EnsoData.RankMatchType.None;
        settings.isRandomSelect = (bool) songInfoType.GetField("IsRandomSelect").GetValue(selectedSongInfo);
        settings.isDailyBonus = (bool) songInfoType.GetField("IsDailyBonus").GetValue(selectedSongInfo);
        ensoMode.songUniqueId = settings.musicUniqueId;
        ensoMode.level = (EnsoData.EnsoLevelType) selectedCourse;
        
        SetSettings();
        typeof(CourseSelect).GetField("ensoMode", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, ensoMode);
        SetSaveDataEnsoMode(Enum.Parse(playerTypeEnumType, "Player1"));
        ensoMode2P.songUniqueId = settings.musicUniqueId;
        ensoMode2P.level = (EnsoData.EnsoLevelType) selectedCourse2P;
        typeof(CourseSelect).GetField("ensoMode2P", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, ensoMode2P);
        SetSaveDataEnsoMode(Enum.Parse(playerTypeEnumType, "Player2"));
        playDataManager.GetSystemOption(out var dst2);
        int deviceTypeIndex = EnsoDataManager.GetDeviceTypeIndex(settings.ensoPlayerSettings[0].inputDevice);
        settings.noteDispOffset = dst2.onpuDispLevels[deviceTypeIndex];
        settings.noteDelay = dst2.onpuHitLevels[deviceTypeIndex];
        settings.songVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.InGameSong);
        settings.seVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Se);
        settings.voiceVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Voice);
        settings.bgmVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Bgm);
        settings.neiroVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.InGameNeiro);
        settings.effectLevel = (EnsoData.EffectLevel) dst2.qualityLevel;
        SetSettings();
        ensoDataManager.SetSettings(ref settings);
        ensoDataManager.DecideSetting();
        if (status.Is2PActive)
        {
            dst2.controlType[1] = dst2.controlType[0];
            playDataManager.SetSystemOption(ref dst2);
        }

        var customSaveData = GetCustomSaveData();

        if (songUniqueId < SaveDataMax)
        {
            playDataManager.GetMusicInfoExAll(0, out var dst3);
            dst3[songUniqueId].isNew = false;
            playDataManager.SetMusicInfoEx(0, songUniqueId, ref dst3[songUniqueId]);
        }
        else
        {
            customSaveData.CustomTrackToMusicInfoEx.TryGetValue(songUniqueId, out var data);
            data.isNew = false;
            customSaveData.CustomTrackToMusicInfoEx[songUniqueId] = data;
            SaveCustomData();
        }

        UnityEngine.Debug.Log($"p1 is {ensoMode}");
        return false;
    }

    /// <summary>
    /// When loading the song obtain isfavourite correctly
    /// </summary>
    [HarmonyPatch(typeof(KpiListCommon.MusicKpiInfo), "GetEnsoSettings")]
    [HarmonyPrefix]
    private static bool GetEnsoSettings_Prefix(KpiListCommon.MusicKpiInfo __instance)
    {
        TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.EnsoData.CopySettings(out var dst);
        __instance.music_id = dst.musicuid;
        __instance.genre = (int) dst.genre;
        __instance.course_type = (int) dst.ensoPlayerSettings[0].courseType;
        __instance.neiro_id = dst.ensoPlayerSettings[0].neiroId;
        __instance.speed = (int) dst.ensoPlayerSettings[0].speed;
        __instance.dron = (int) dst.ensoPlayerSettings[0].dron;
        __instance.reverse = (int) dst.ensoPlayerSettings[0].reverse;
        __instance.randomlv = (int) dst.ensoPlayerSettings[0].randomlv;
        __instance.special = (int) dst.ensoPlayerSettings[0].special;
        PlayDataManager playData = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData;
        playData.GetEnsoMode(out var dst2);
        __instance.sort_course = (int) dst2.songSortCourse;
        __instance.sort_type = (int) dst2.songSortType;
        __instance.sort_filter = (int) dst2.songFilterType;
        __instance.sort_favorite = (int) dst2.songFilterTypeFavorite;
        MusicDataInterface.MusicInfoAccesser[] array = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.musicInfoAccessers.ToArray();
        playData.GetMusicInfoExAll(0, out var dst3);

        #region edited code

        for (int i = 0; i < array.Length; i++)
        {
            var id = array[i].UniqueId;
            if (id == dst.musicUniqueId && dst3 != null)
            {
                if (id < SaveDataMax)
                {
                    __instance.is_favorite = dst3[id].favorite;
                }
                else
                {
                    GetCustomSaveData().CustomTrackToMusicInfoEx.TryGetValue(id, out var data);
                    __instance.is_favorite = data.favorite;
                }
            }
        }

        #endregion

        playData.GetPlayerInfo(0, out var dst4);
        __instance.current_coins_num = dst4.donCoin;
        __instance.total_coins_num = dst4.getCoinsInTotal;
        playData.GetRankMatchSeasonRecordInfo(0, 0, out var dst5);
        __instance.rank_point = dst5.rankPointMax;

        return false;
    }

    /// <summary>
    /// Load scores from custom save data
    /// </summary>
    [HarmonyPatch(typeof(PlayDataManager), "GetPlayerRecordInfo")]
    [HarmonyPrefix]
    public static bool GetPlayerRecordInfo_Prefix(int playerId, int uniqueId, EnsoData.EnsoLevelType levelType, out EnsoRecordInfo dst, PlayDataManager __instance)
    {
        if (uniqueId < SaveDataMax)
        {
            dst = new EnsoRecordInfo();
            return true;
        }

        int num = (int) levelType;
        if (num is < 0 or >= 5)
            num = 0;

        // load our custom save, this will combine the scores of player1 and player2
        var saveData = GetCustomSaveData().CustomTrackToEnsoRecordInfo;
        if (!saveData.TryGetValue(uniqueId, out var ensoData))
        {
            ensoData = new EnsoRecordInfo[(int) EnsoData.EnsoLevelType.Num];
            saveData[uniqueId] = ensoData;
        }

        dst = ensoData[num];
        return false;
    }

    /// <summary>
    /// Save scores to custom save data
    /// </summary>
    [HarmonyPatch(typeof(PlayDataManager), "UpdatePlayerScoreRecordInfo",
        new Type[] {typeof(int), typeof(int), typeof(int), typeof(EnsoData.EnsoLevelType), typeof(bool), typeof(DataConst.SpecialTypes), typeof(HiScoreRecordInfo), typeof(DataConst.ResultType), typeof(bool), typeof(DataConst.CrownType)})]
    [HarmonyPrefix]
    public static bool UpdatePlayerScoreRecordInfo(PlayDataManager __instance, int playerId, int charaIndex, int uniqueId, EnsoData.EnsoLevelType levelType, bool isSinuchi, DataConst.SpecialTypes spTypes, HiScoreRecordInfo record,
        DataConst.ResultType resultType, bool savemode, DataConst.CrownType crownType)
    {
        if (uniqueId < SaveDataMax)
            return true;

        int num = (int) levelType;
        if (num is < 0 or >= 5)
            num = 0;

        var saveData = GetCustomSaveData().CustomTrackToEnsoRecordInfo;
        if (!saveData.TryGetValue(uniqueId, out var ensoData))
        {
            ensoData = new EnsoRecordInfo[(int) EnsoData.EnsoLevelType.Num];
            saveData[uniqueId] = ensoData;
        }

        EnsoRecordInfo ensoRecordInfo = ensoData[(int) levelType];
#pragma warning disable Harmony003
        if (ensoRecordInfo.normalHiScore.score <= record.score)
        {
            ensoRecordInfo.normalHiScore.score = record.score;
            ensoRecordInfo.normalHiScore.combo = record.combo;
            ensoRecordInfo.normalHiScore.excellent = record.excellent;
            ensoRecordInfo.normalHiScore.good = record.good;
            ensoRecordInfo.normalHiScore.bad = record.bad;
            ensoRecordInfo.normalHiScore.renda = record.renda;
        }
#pragma warning restore Harmony003

        if (crownType != DataConst.CrownType.Off)
        {
            if (IsValueInRange((int) crownType, 0, 5) && ensoRecordInfo.crown <= crownType)
            {
                ensoRecordInfo.crown = crownType;
                ensoRecordInfo.cleared = crownType >= DataConst.CrownType.Silver;
            }
        }

        ensoData[(int) levelType] = ensoRecordInfo;

        if (savemode && playerId == 0)
            SaveCustomData();

        return false;

        bool IsValueInRange(int myValue, int minValue, int maxValue)
        {
            if (myValue >= minValue && myValue < maxValue)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Allow for a song id > 400
    /// </summary>
    [HarmonyPatch(typeof(EnsoMode), "IsValid")]
    [HarmonyPrefix]
    public static bool IsValid_Prefix(ref bool __result, EnsoMode __instance)
    {
#pragma warning disable Harmony003
        __result = Validate();
        return false;
        bool Validate()
        {
            // commented out this code
            // if (songUniqueId < DataConst.InvalidId || songUniqueId > DataConst.MusicMax)
            // {
            //     return false;
            // }
            if (!Enum.IsDefined(typeof(EnsoData.SongGenre), __instance.listGenre))
            {
                return false;
            }
            if (__instance.neiro < 0 || __instance.neiro > DataConst.NeiroMax)
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(EnsoData.EnsoLevelType), __instance.level))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SpeedTypes), __instance.speed))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.OptionOnOff), __instance.dron))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.OptionOnOff), __instance.reverse))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.RandomLevel), __instance.randomlv))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SpecialTypes), __instance.special))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SongSortType), __instance.songSortType))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SongSortCourse), __instance.songSortCourse))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SongFilterType), __instance.songFilterType))
            {
                return false;
            }
            if (!Enum.IsDefined(typeof(DataConst.SongFilterTypeFavorite), __instance.songFilterTypeFavorite))
            {
                return false;
            }
            return true;
        }
#pragma warning restore Harmony003
    }
    
    #endregion

    #region Read Fumen

    private static readonly Regex fumenFilePathRegex = new Regex("(?<songID>.*?)_(?<difficulty>[ehmnx])(?<songIndex>_[12])?.bin");

    private static readonly Dictionary<object, IntPtr> playerToFumenData = new Dictionary<object, IntPtr>();

    /// <summary>
    /// Read unencrypted Fumen files, save them to <see cref="playerToFumenData"/>
    /// todo dispose old fumens?
    /// </summary>
    /// <returns></returns>
    private static unsafe bool Read_Prefix(string filePath, ref bool __result, object __instance)
    {
        var type = typeof(FumenLoader).GetNestedType("PlayerData", BindingFlags.NonPublic);

        if (File.Exists(filePath))
            return true;

        // if the file doesn't exist, perhaps it's a custom song?
        var fileName = Path.GetFileName(filePath);
        var match = fumenFilePathRegex.Match(fileName);
        if (!match.Success)
        {
            Log.LogError($"Cannot interpret {fileName}");
            return true;
        }

        // get song id
        var songId = match.Groups["songID"];
        var difficulty = match.Groups["difficulty"];
        var songIndex = match.Groups["songIndex"];

        var newPath = Path.Combine(MusicTrackDirectory, $"{songId}\\{fileName}");
        Log.LogInfo($"Redirecting file from {filePath} to {newPath}");

        type.GetMethod("Dispose").Invoke(__instance, new object[] { });
        type.GetField("fumenPath").SetValue(__instance, newPath);

        byte[] array = File.ReadAllBytes(newPath);
        var fumenSize = array.Length;
        type.GetField("fumenSize").SetValue(__instance, fumenSize);

        var fumenData = UnsafeUtility.Malloc(fumenSize, 16, Allocator.Persistent);
        type.GetField("fumenData").SetValue(__instance, (IntPtr) fumenData);

        Marshal.Copy(array, 0, (IntPtr) fumenData, fumenSize);

        type.GetField("isReadEnd").SetValue(__instance, true);
        type.GetField("isReadSucceed").SetValue(__instance, true);
        __result = true;

        playerToFumenData[__instance] = (IntPtr) fumenData;
        return false;
    }

    /// <summary>
    /// When asking to get a Fumen, used the ones we stored above
    /// </summary>
    [HarmonyPatch(typeof(FumenLoader), "GetFumenData")]
    [HarmonyPrefix]
    public static unsafe bool GetFumenData_Prefix(int player, ref void* __result, FumenLoader __instance)
    {
        var settings = (EnsoData.Settings) typeof(FumenLoader).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        var playerData = (Array) typeof(FumenLoader).GetField("playerData", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);

        if (player >= 0 && player < settings.playerNum)
        {
            if (playerToFumenData.TryGetValue(playerData.GetValue(player), out var data))
            {
                __result = (void*) data;
                return false;
            }
        }

        // try loading the actual data
        return true;
    }

    #endregion

    #region Read Song

    private static readonly Regex musicFilePathRegex = new Regex("^song_(?<songName>.*?)$");

    /// <summary>
    /// Read an unencrypted song "asynchronously" (it does it instantly, we should have fast enough PCs right?)
    /// </summary>
    /// <param name="__instance"></param>
    [HarmonyPatch(typeof(CriPlayer), "LoadAsync")]
    [HarmonyPostfix]
    public static void LoadAsync_Postfix(CriPlayer __instance)
    {
        // Run this on the next frame
        Plugin.Instance.StartCustomCoroutine(LoadAsync());

        IEnumerator LoadAsync()
        {
            yield return null;
            var sheetName = __instance.CueSheetName;
            var path = Application.streamingAssetsPath + "/sound/" + sheetName + ".bin";

            if (File.Exists(path))
                yield break;

            typeof(CriPlayer).GetField("isLoadingAsync", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, true);
            typeof(CriPlayer).GetField("isCancelLoadingAsync", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, false);
            typeof(CriPlayer).GetProperty("IsPrepared").SetValue(__instance, false);
            typeof(CriPlayer).GetProperty("IsLoadSucceed").SetValue(__instance, false);
            typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Loading);
            typeof(CriPlayer).GetProperty("LoadTime").SetValue(__instance, -1f);
            typeof(CriPlayer).GetField("loadStartTime", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, Time.time);

            var match = musicFilePathRegex.Match(sheetName);
            if (!match.Success)
            {
                Log.LogError($"Cannot interpret {sheetName}");
                yield break;
            }

            var songName = match.Groups["songName"];
            var newPath = Path.Combine(MusicTrackDirectory, $"{songName}\\{sheetName}.bin");
            Log.LogInfo($"Redirecting file from {path} to {newPath}");

            var cueSheet = CriAtom.AddCueSheetAsync(sheetName, File.ReadAllBytes(newPath), null);
            typeof(CriPlayer).GetProperty("CueSheet").SetValue(__instance, cueSheet);

            if (cueSheet != null)
            {
                typeof(CriPlayer).GetField("isLoadingAsync", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, false);
                typeof(CriPlayer).GetProperty("IsLoadSucceed").SetValue(__instance, true);

                typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Finished);
                typeof(CriPlayer).GetProperty("LoadTime").SetValue(__instance, 0);
                yield break;
            }

            Log.LogError($"Could not load music");
            typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Finished);
            typeof(CriPlayer).GetField("isLoadingAsync", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, false);
        }
    }

    /// <summary>
    /// Read an unencrypted song
    /// </summary>
    [HarmonyPatch(typeof(CriPlayer), "Load")]
    [HarmonyPrefix]
    private static bool Load_Prefix(ref bool __result, CriPlayer __instance)
    {
        var sheetName = __instance.CueSheetName;
        var path = Application.streamingAssetsPath + "/sound/" + sheetName + ".bin";

        if (File.Exists(path))
            return true;

        var match = musicFilePathRegex.Match(sheetName);
        if (!match.Success)
        {
            Log.LogError($"Cannot interpret {sheetName}");
            return true;
        }

        var songName = match.Groups["songName"];

        var newPath = Path.Combine(MusicTrackDirectory, $"{songName}\\{sheetName}.bin");
        Log.LogInfo($"Redirecting file from {path} to {newPath}");

        // load custom song
        typeof(CriPlayer).GetProperty("IsPrepared").SetValue(__instance, false);
        typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Loading);
        typeof(CriPlayer).GetProperty("IsLoadSucceed").SetValue(__instance, false);
        typeof(CriPlayer).GetProperty("LoadTime").SetValue(__instance, -1f);
        typeof(CriPlayer).GetField("loadStartTime", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, Time.time);

        if (sheetName == "")
        {
            typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Finished);
            __result = false;
            return false;
        }

        //
        var cueSheet = CriAtom.AddCueSheetAsync(sheetName, File.ReadAllBytes(newPath), null);
        typeof(CriPlayer).GetProperty("CueSheet").SetValue(__instance, cueSheet);

        if (cueSheet != null)
        {
            __result = true;
            return false;
        }

        typeof(CriPlayer).GetProperty("LoadingState").SetValue(__instance, CriPlayer.LoadingStates.Finished);
        __result = false;
        return false;
    }

    #endregion

    #region Data Structures

    [Serializable]
    [DataContract(Name = "CustomSaveData")]
    public class CustomMusicSaveDataBody
    {
        [DataMember] public Dictionary<int, MusicInfoEx> CustomTrackToMusicInfoEx = new();

        [DataMember] public Dictionary<int, EnsoRecordInfo[]> CustomTrackToEnsoRecordInfo = new();
    }

    // ReSharper disable once ClassNeverInstantiated.Global
    [DataContract(Name = "CustomSong")]
    [Serializable]
    public class CustomSong
    {
        // Song Details
        [DataMember] public int uniqueId;
        [DataMember] public string id;
        [DataMember] public int order;
        [DataMember] public int genreNo;
        [DataMember] public bool branchEasy;
        [DataMember] public bool branchNormal;
        [DataMember] public bool branchHard;
        [DataMember] public bool branchMania;
        [DataMember] public bool branchUra;
        [DataMember] public int starEasy;
        [DataMember] public int starNormal;
        [DataMember] public int starHard;
        [DataMember] public int starMania;
        [DataMember] public int starUra;
        [DataMember] public int shinutiEasy;
        [DataMember] public int shinutiNormal;
        [DataMember] public int shinutiHard;
        [DataMember] public int shinutiMania;
        [DataMember] public int shinutiUra;
        [DataMember] public int shinutiEasyDuet;
        [DataMember] public int shinutiNormalDuet;
        [DataMember] public int shinutiHardDuet;
        [DataMember] public int shinutiManiaDuet;
        [DataMember] public int shinutiUraDuet;
        [DataMember] public int scoreEasy;
        [DataMember] public int scoreNormal;
        [DataMember] public int scoreHard;
        [DataMember] public int scoreMania;
        [DataMember] public int scoreUra;

        // Preview Details
        [DataMember] public int previewPos;
        [DataMember] public int fumenOffsetPos;

        // LocalisationDetails
        /// <summary>
        /// Song Title
        /// <example>
        /// A Cruel Angel's Thesis
        /// </example>
        /// </summary>
        [DataMember] public TextEntry songName;

        /// <summary>
        /// Origin of the song
        /// <example>
        /// From \" Neon Genesis EVANGELION \"
        /// </example>
        /// </summary>
        [DataMember] public TextEntry songSubtitle;

        /// <summary>
        /// Extra details for the track, sometimes used to say it's Japanese name
        /// <example>
        /// 残酷な天使のテーゼ
        /// </example>
        /// </summary>
        [DataMember] public TextEntry songDetail;
    }

    [Serializable]
    public class TextEntry
    {
        /// <summary>
        /// The text to display
        /// </summary>
        public string text;

        /// <summary>
        /// 0 == Japanese
        /// 1 == English
        /// 2 == Traditional Chinese
        /// 3 == Simplified Chinese
        /// 4 == Korean
        /// </summary>
        public int font;
    }

    #endregion
}