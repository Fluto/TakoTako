using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using TakoTako.Common;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace TakoTako;

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

    private static CustomMusicSaveDataBody GetCustomSaveData()
    {
        if (_customSaveData != null)
            return _customSaveData;

        var savePath = SaveFilePath;
        CustomMusicSaveDataBody data;
        try
        {
            if (!File.Exists(savePath))
            {
                data = new CustomMusicSaveDataBody();
                SaveCustomData();
            }
            else
            {
                using var fileStream = File.OpenRead(savePath);
                data = (CustomMusicSaveDataBody) JsonConvert.DeserializeObject<CustomMusicSaveDataBodySerializable>(File.ReadAllText(savePath));
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

        data = new CustomMusicSaveDataBody();
        SaveCustomData();
        return data;
    }

    private static int saveMutex = 0;

    private static void SaveCustomData()
    {
        if (!Plugin.Instance.ConfigSaveEnabled.Value)
            return;

        if (_customSaveData == null)
            return;

        saveMutex++;
        if (saveMutex > 1)
            return;

        SaveData();

        async void SaveData()
        {
            while (saveMutex > 0)
            {
                saveMutex = 0;
                Log.LogInfo("Saving custom data");
                try
                {
                    var data = GetCustomSaveData();
                    var savePath = SaveFilePath;
                    var json = JsonConvert.SerializeObject(data);

                    using Stream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    using StreamWriter streamWriter = new StreamWriter(fs);
                    await streamWriter.WriteAsync(json);
                }
                catch (Exception e)
                {
                    Log.LogError($"Could not save custom data \n {e}");
                }
            }
        }
    }

    #endregion

    #region Load Custom Songs

    private static ConcurrentBag<SongInstance> customSongsList;
    private static readonly ConcurrentDictionary<string, SongInstance> idToSong = new ConcurrentDictionary<string, SongInstance>();
    private static readonly ConcurrentDictionary<int, SongInstance> uniqueIdToSong = new ConcurrentDictionary<int, SongInstance>();

    public static ConcurrentBag<SongInstance> GetCustomSongs()
    {
        if (customSongsList != null)
            return customSongsList;

        customSongsList = new ConcurrentBag<SongInstance>();
        if (!Directory.Exists(MusicTrackDirectory))
        {
            Log.LogError($"Cannot find {MusicTrackDirectory}");
            return customSongsList;
        }

        try
        {
            // add songs
            var songPaths = Directory.GetFiles(MusicTrackDirectory, "song*.bin", SearchOption.AllDirectories).Select(Path.GetDirectoryName).Distinct().ToList();
            Parallel.ForEach(songPaths, musicDirectory =>
            {
                try
                {
                    var directory = musicDirectory;

                    var isGenerated = musicDirectory.EndsWith("[GENERATED]");
                    if (isGenerated)
                        return;

                    SubmitDirectory(directory, false);
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                }
            });

            var tjaPaths = Directory.GetFiles(MusicTrackDirectory, "*.tja", SearchOption.AllDirectories).Select(Path.GetDirectoryName).Distinct().ToList();
            // convert / add TJA songs
            Parallel.ForEach(tjaPaths, new ParallelOptions() {MaxDegreeOfParallelism = 4}, musicDirectory =>
            {
                try
                {
                    if (IsTjaConverted(musicDirectory, out var conversionStatus) && conversionStatus != null)
                    {
                        foreach (var item in conversionStatus.Items.Where(item => item.Successful))
                            SubmitDirectory(Path.Combine(musicDirectory, item.FolderName), true);
                        return;
                    }

                    conversionStatus ??= new ConversionStatus();

                    if (conversionStatus.Items.Count > 0 && conversionStatus.Items.Any(x => !x.Successful && x.Attempts > ConversionStatus.ConversionItem.MaxAttempts))
                    {
                        Log.LogWarning($"Ignoring {musicDirectory}");
                        return;
                    }

                    try
                    {
                        var pathName = Path.GetFileName(musicDirectory);
                        var pluginDirectory = @$"{Environment.CurrentDirectory}\BepInEx\plugins\{PluginInfo.PLUGIN_GUID}";
                        var tjaConvertPath = @$"{pluginDirectory}\TJAConvert.exe";
                        var tja2BinConvertPath = @$"{pluginDirectory}\tja2bin.exe";
                        if (!File.Exists(tjaConvertPath) || !File.Exists(tja2BinConvertPath))
                            throw new Exception("Cannot find .exes in plugin folder");

                        Log.LogInfo($"Converting {pathName}");
                        var info = new ProcessStartInfo()
                        {
                            FileName = tjaConvertPath,
                            Arguments = $"\"{musicDirectory}\"",
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            WorkingDirectory = pluginDirectory,
                            StandardOutputEncoding = Encoding.Unicode,
                        };

                        var process = new Process();
                        process.StartInfo = info;
                        process.Start();
                        var result = process.StandardOutput.ReadToEnd();
                        var resultLines = result.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var line in resultLines)
                        {
                            var match = ConversionStatus.ConversionResultRegex.Match(line);
                            if (!match.Success)
                                continue;

                            var resultInt = int.Parse(match.Groups["ID"].Value);
                            var folderPath = match.Groups["PATH"].Value;

                            folderPath = Path.GetFullPath(folderPath).Replace(Path.GetFullPath(musicDirectory), ".");

                            var existingEntry = conversionStatus.Items.FirstOrDefault(x => x.FolderName == folderPath);
                            var asciiFolderPath = Regex.Replace(folderPath, @"[^\u0000-\u007F]+", string.Empty);
                            if (resultInt >= 0)
                                Log.LogInfo($"Converted {asciiFolderPath} successfully");
                            else
                                Log.LogError($"Could not convert {asciiFolderPath}");

                            if (existingEntry == null)
                            {
                                conversionStatus.Items.Add(new ConversionStatus.ConversionItem()
                                {
                                    Attempts = 1,
                                    FolderName = folderPath,
                                    Successful = resultInt >= 0,
                                });
                            }
                            else
                            {
                                existingEntry.Attempts++;
                                existingEntry.Successful = resultInt >= 0;
                            }
                        }

                        File.WriteAllText(Path.Combine(musicDirectory, "conversion.json"), JsonConvert.SerializeObject(conversionStatus), Encoding.Unicode);
                    }
                    catch (Exception e)
                    {
                        Log.LogError(e);
                        return;
                    }

                    if (!IsTjaConverted(musicDirectory, out conversionStatus))
                        return;

                    // if the files are converted, let's gzip those .bins to save space... because they can add up
                    foreach (var item in conversionStatus.Items)
                    {
                        var directory = Path.Combine(musicDirectory, item.FolderName);
                        var dataPath = Path.Combine(directory, "data.json");
                        if (!File.Exists(dataPath))
                        {
                            Log.LogError($"Cannot find {dataPath}");
                            return;
                        }

                        var song = JsonConvert.DeserializeObject<SongInstance>(File.ReadAllText(dataPath));
                        if (song == null)
                        {
                            Log.LogError($"Cannot read {dataPath}");
                            return;
                        }

                        foreach (var filePath in Directory.EnumerateFiles(directory, "*.bin"))
                        {
                            using MemoryStream compressedMemoryStream = new MemoryStream();
                            using (FileStream originalFileStream = File.Open(filePath, FileMode.Open))
                            {
                                using var compressor = new GZipStream(compressedMemoryStream, CompressionMode.Compress);
                                originalFileStream.CopyTo(compressor);
                            }

                            File.WriteAllBytes(filePath, compressedMemoryStream.ToArray());
                        }

                        song.AreFilesGZipped = true;
                        File.WriteAllText(dataPath, JsonConvert.SerializeObject(song));

                        SubmitDirectory(directory, true);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                }
            });

            if (customSongsList.Count == 0)
                Log.LogInfo($"No tracks found");
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }

        return customSongsList;

        void SubmitDirectory(string directory, bool isTjaSong)
        {
            var dataPath = Path.Combine(directory, "data.json");
            if (!File.Exists(dataPath))
            {
                Log.LogError($"Cannot find {dataPath}");
                return;
            }

            var song = JsonConvert.DeserializeObject<SongInstance>(File.ReadAllText(dataPath));
            if (song == null)
            {
                Log.LogError($"Cannot read {dataPath}");
                return;
            }

            if (Plugin.Instance.ConfigApplyGenreOverride.Value)
            {
                // if this directory has a genre then override it
                var fullPath = Path.GetFullPath(directory);
                fullPath = fullPath.Replace(Path.GetFullPath(Plugin.Instance.ConfigSongDirectory.Value), "");
                var directories = fullPath.Split('\\');
                if (directories.Any(x => x.Equals("01 Pop", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 0;
                if (directories.Any(x => x.Equals("02 Anime", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 1;
                if (directories.Any(x => x.Equals("03 Vocaloid", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 2;
                if (directories.Any(x => x.Equals("04 Children and Folk", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 4;
                if (directories.Any(x => x.Equals("05 Variety", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 3;
                if (directories.Any(x => x.Equals("06 Classical", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 5;
                if (directories.Any(x => x.Equals("07 Game Music", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 6;
                if (directories.Any(x => x.Equals("08 Live Festival Mode", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 3;
                if (directories.Any(x => x.Equals("08 Namco Original", StringComparison.InvariantCultureIgnoreCase)))
                    song.genreNo = 7;
            }

            var instanceId = Guid.NewGuid().ToString();
            song.SongName = song.id;
            song.FolderPath = directory;
            song.id = instanceId;

            if (uniqueIdToSong.ContainsKey(song.uniqueId) || (song.uniqueId >= 0 && song.uniqueId <= SaveDataMax))
            {
                var uniqueIdTest = unchecked(song.id.GetHashCode() + song.previewPos + song.fumenOffsetPos);
                while (uniqueIdToSong.ContainsKey(uniqueIdTest) || (uniqueIdTest >= 0 && uniqueIdTest <= SaveDataMax))
                    uniqueIdTest = unchecked((uniqueIdTest + 1) * (uniqueIdTest + 1));

                song.uniqueId = uniqueIdTest;
            }

            customSongsList.Add(song);
            idToSong[song.id] = song;
            uniqueIdToSong[song.uniqueId] = song;
            Log.LogInfo($"Added {(isTjaSong ? "TJA" : "")} Song {song.songName.text}");
        }
    }

    private static bool IsTjaConverted(string directory, out ConversionStatus conversionStatus)
    {
        conversionStatus = null;
        if (!Directory.Exists(directory))
            return false;

        var conversionFile = Path.Combine(directory, "conversion.json");
        if (!File.Exists(conversionFile))
            return false;

        var json = File.ReadAllText(conversionFile, Encoding.Unicode);
        try
        {
            conversionStatus = JsonConvert.DeserializeObject<ConversionStatus>(json);
            if (conversionStatus == null)
                return false;

            return conversionStatus.Items.Count != 0 && conversionStatus.Items.All(x => x.Successful);
        }
        catch
        {
            return false;
        }
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
        try
        {
            // This is where the metadata for tracks are read in our attempt to allow custom tracks will be to add additional metadata to the list that is created
            Log.LogInfo("Injecting custom songs");

            var customSongs = GetCustomSongs();
            if (customSongs.Count == 0)
                return;

            // now that we have loaded this json, inject it into the existing `musicInfoAccessers`
            var musicInfoAccessors = __instance.musicInfoAccessers;

            #region Logic from the original constructor

            foreach (var song in customSongs)
            {
                if (song == null)
                    continue;

                musicInfoAccessors.Add(new MusicDataInterface.MusicInfoAccesser(
                    song.uniqueId,
                    song.id,
                    $"song_{song.id}",
                    song.order,
                    song.genreNo,
                    !Plugin.Instance.ConfigDisableCustomDLCSongs.Value,
                    false,
                    0, false,
                    0,
                    new[]
                    {
                        song.branchEasy,
                        song.branchNormal,
                        song.branchHard,
                        song.branchMania,
                        song.branchUra
                    }, new[]
                    {
                        song.starEasy,
                        song.starNormal,
                        song.starHard,
                        song.starMania,
                        song.starUra
                    }, new[]
                    {
                        song.shinutiEasy,
                        song.shinutiNormal,
                        song.shinutiHard,
                        song.shinutiMania,
                        song.shinutiUra
                    }, new[]
                    {
                        song.shinutiEasyDuet,
                        song.shinutiNormalDuet,
                        song.shinutiHardDuet,
                        song.shinutiManiaDuet,
                        song.shinutiUraDuet
                    }, new[]
                    {
                        song.scoreEasy,
                        song.scoreNormal,
                        song.scoreHard,
                        song.scoreMania,
                        song.scoreUra
                    }));
            }

            #endregion

            // sort this
            musicInfoAccessors.Sort((a, b) => a.Order - b.Order);
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
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
        if (musicInfoAccessors == null)
            return;

        foreach (var customTrack in customSongs)
        {
            if (customTrack == null)
                continue;

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

        var customLanguage = Plugin.Instance.ConfigOverrideDefaultSongLanguage.Value;
        var languageValue = language;
        if (customLanguage is "Japanese" or "English" or "French" or "Italian" or "German" or "Spanish" or "ChineseTraditional" or "ChineseSimplified" or "Korean")
            languageValue = customLanguage;

        // now that we have loaded this json, inject it into the existing `songInfoAccessers`
        var musicInfoAccessors = __instance.wordListInfoAccessers;

        // override the existing songs if we're using a custom language
        if (languageValue != language)
        {
            var wordListInfoRead = (ReadData<WordListInfo>) typeof(WordDataInterface).GetField("wordListInfoRead", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
            var dictionary = wordListInfoRead.InfomationDatas.ToList();

            for (int i = 0; i < musicInfoAccessors.Count; i++)
            {
                const string songDetailPrefix = "song_detail_";
                var entry = musicInfoAccessors[i];
                var index = entry.Key.IndexOf(songDetailPrefix, StringComparison.Ordinal);
                if (index < 0)
                    continue;

                var songTitle = entry.Key.Substring(songDetailPrefix.Length);
                if (string.IsNullOrWhiteSpace(songTitle))
                    continue;

                var songKey = $"song_{songTitle}";
                var subtitleKey = $"song_sub_{songTitle}";
                var detailKey = $"song_detail_{songTitle}";

                var songEntry = dictionary.Find(x => x.key == songKey);
                var subtitleEntry = dictionary.Find(x => x.key == subtitleKey);
                var detailEntry = dictionary.Find(x => x.key == detailKey);

                if (songEntry == null || subtitleEntry == null || detailEntry == null)
                    continue;

                musicInfoAccessors.RemoveAll(x => x.Key == songKey || x.Key == subtitleKey || x.Key == detailKey);

                var songValues = GetValuesWordList(songEntry);
                var subtitleValues = GetValuesWordList(songEntry);
                var detailValues = GetValuesWordList(songEntry);

                musicInfoAccessors.Insert(0, new WordDataInterface.WordListInfoAccesser(songKey, songValues.text, songValues.font));
                musicInfoAccessors.Insert(0, new WordDataInterface.WordListInfoAccesser(subtitleKey, subtitleValues.text, subtitleValues.font));
                musicInfoAccessors.Insert(0, new WordDataInterface.WordListInfoAccesser(detailKey, detailValues.text, detailValues.font));
            }
        }

        foreach (var customTrack in customSongs)
        {
            Add($"song_{customTrack.id}", customTrack.songName);
            Add($"song_sub_{customTrack.id}", customTrack.songSubtitle);
            Add($"song_detail_{customTrack.id}", customTrack.songDetail);

            void Add(string key, TextEntry textEntry)
            {
                var (text, font) = GetValuesTextEntry(textEntry);
                musicInfoAccessors.Add(new WordDataInterface.WordListInfoAccesser(key, text, font));
            }
        }

        (string text, int font) GetValuesWordList(WordListInfo wordListInfo)
        {
            string text;
            int font;
            switch (languageValue)
            {
                case "Japanese":
                    text = wordListInfo.jpText;
                    font = wordListInfo.jpFontType;
                    break;
                case "English":
                    text = wordListInfo.enText;
                    font = wordListInfo.enFontType;
                    break;
                case "French":
                    text = wordListInfo.frText;
                    font = wordListInfo.frFontType;
                    break;
                case "Italian":
                    text = wordListInfo.itText;
                    font = wordListInfo.itFontType;
                    break;
                case "German":
                    text = wordListInfo.deText;
                    font = wordListInfo.deFontType;
                    break;
                case "Spanish":
                    text = wordListInfo.esText;
                    font = wordListInfo.esFontType;
                    break;
                case "Chinese":
                case "ChineseT":
                case "ChineseTraditional":
                    text = wordListInfo.tcText;
                    font = wordListInfo.tcFontType;
                    break;
                case "ChineseSimplified":
                case "ChineseS":
                    text = wordListInfo.scText;
                    font = wordListInfo.scFontType;
                    break;
                case "Korean":
                    text = wordListInfo.krText;
                    font = wordListInfo.krFontType;
                    break;
                default:
                    text = wordListInfo.enText;
                    font = wordListInfo.enFontType;
                    break;
            }

            return (text, font);
        }

        (string text, int font) GetValuesTextEntry(TextEntry textEntry)
        {
            string text;
            int font;
            switch (languageValue)
            {
                case "Japanese":
                    text = textEntry.jpText;
                    font = textEntry.jpFont;
                    break;
                case "English":
                    text = textEntry.enText;
                    font = textEntry.enFont;
                    break;
                case "French":
                    text = textEntry.frText;
                    font = textEntry.frFont;
                    break;
                case "Italian":
                    text = textEntry.itText;
                    font = textEntry.itFont;
                    break;
                case "German":
                    text = textEntry.deText;
                    font = textEntry.deFont;
                    break;
                case "Spanish":
                    text = textEntry.esText;
                    font = textEntry.esFont;
                    break;
                case "Chinese":
                case "ChineseT":
                case "ChineseTraditional":
                    text = textEntry.tcText;
                    font = textEntry.tcFont;
                    break;
                case "ChineseSimplified":
                case "ChineseS":
                    text = textEntry.scText;
                    font = textEntry.scFont;
                    break;
                case "Korean":
                    text = textEntry.krText;
                    font = textEntry.krFont;
                    break;
                default:
                    text = textEntry.enText;
                    font = textEntry.enFont;
                    break;
            }

            if (!string.IsNullOrEmpty(text)) return (text, font);
            text = textEntry.text;
            font = textEntry.font;

            return (text, font);
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
                    if (uniqueIdToSong.ContainsKey(musicInfoAccess[j].UniqueId))
                    {
                        customData.CustomTrackToMusicInfoEx.TryGetValue(musicInfoAccess[j].UniqueId, out var objectData);
                        data = objectData;
                    }
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
            if (uniqueIdToSong.ContainsKey(unsortedSong.UniqueId))
            {
                customSaveData.CustomTrackToMusicInfoEx.TryGetValue(unsortedSong.UniqueId, out var data);
                saveCustomData |= data.favorite != unsortedSong.Favorite;
                data.favorite = unsortedSong.Favorite;
                customSaveData.CustomTrackToMusicInfoEx[unsortedSong.UniqueId] = data;
            }
            else
            {
                dst[unsortedSong.UniqueId].favorite = unsortedSong.Favorite;
                playDataMgr.SetMusicInfoEx(0, unsortedSong.UniqueId, ref dst[unsortedSong.UniqueId], num >= __instance.UnsortedSongList.Count);
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
        var SetSaveDataEnsoMode = (object x) => (string) SetSaveDataEnsoModeMethodInfo.Invoke(__instance, new object[] {x});

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

        if (uniqueIdToSong.ContainsKey(songUniqueId))
        {
            customSaveData.CustomTrackToMusicInfoEx.TryGetValue(songUniqueId, out var data);
            data.isNew = false;
            customSaveData.CustomTrackToMusicInfoEx[songUniqueId] = data;
            SaveCustomData();
        }
        else
        {
            playDataManager.GetMusicInfoExAll(0, out var dst3);
            dst3[songUniqueId].isNew = false;
            playDataManager.SetMusicInfoEx(0, songUniqueId, ref dst3[songUniqueId]);
        }

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
                if (uniqueIdToSong.ContainsKey(id))
                {
                    GetCustomSaveData().CustomTrackToMusicInfoEx.TryGetValue(id, out var data);
                    __instance.is_favorite = data.favorite;
                }
                else
                {
                    __instance.is_favorite = dst3[id].favorite;
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
        if (!uniqueIdToSong.ContainsKey(uniqueId))
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
        if (!uniqueIdToSong.ContainsKey(uniqueId))
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

    [HarmonyPatch(typeof(SongSelectManager), "Start")]
    [HarmonyPostfix]
    public static void Start_Postfix(SongSelectManager __instance)
    {
        Plugin.Instance.StartCustomCoroutine(SetSelectedSongAsync());

        IEnumerator SetSelectedSongAsync()
        {
            while (__instance.SongList.Count == 0 || (bool) typeof(SongSelectManager).GetField("isAsyncLoading", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance))
                yield return null;

            // if the song id is < 0 then fix the selected song index
            var ensoMode = (EnsoMode) typeof(SongSelectManager).GetField("ensoMode", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
            var lastPlaySongId = ensoMode.songUniqueId;

            var songIndex = __instance.SongList.IndexOf(__instance.SongList.FirstOrDefault(song => song.UniqueId == lastPlaySongId));
            if (songIndex < 0)
                yield break;

            typeof(SongSelectManager).GetProperty("SelectedSongIndex").SetValue(__instance, songIndex);
            __instance.SortSongList(ensoMode.songSortCourse, ensoMode.songSortType, ensoMode.songFilterType, ensoMode.songFilterTypeFavorite);
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

    /// <summary>
    /// Allow for a song id less than 0
    /// </summary>
    [HarmonyPatch(typeof(EnsoDataManager), "DecideSetting")]
    [HarmonyPrefix]
    public static bool DecideSetting_Prefix(EnsoDataManager __instance)
    {
        var ensoSettings = (EnsoData.Settings) typeof(EnsoDataManager).GetField("ensoSettings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
        // if (ensoSettings.musicuid.Length <= 0)
        // {
        // 	MusicDataInterface.MusicInfoAccesser infoByUniqueId = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.GetInfoByUniqueId(ensoSettings.musicUniqueId);
        // 	if (infoByUniqueId != null)
        // 	{
        // 		ensoSettings.musicuid = infoByUniqueId.Id;
        // 	}
        // }
        // else if (ensoSettings.musicUniqueId <= DataConst.InvalidId)
        // {
        // 	MusicDataInterface.MusicInfoAccesser infoById = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.GetInfoById(ensoSettings.musicuid);
        // 	if (infoById != null)
        // 	{
        // 		ensoSettings.musicUniqueId = infoById.UniqueId;
        // 	}
        // }
        if (ensoSettings.musicuid.Length <= 0 /* || ensoSettings.musicUniqueId <= DataConst.InvalidId*/)
        {
            List<MusicDataInterface.MusicInfoAccesser> musicInfoAccessers = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.musicInfoAccessers;
            for (int i = 0; i < musicInfoAccessers.Count; i++)
            {
                if (!musicInfoAccessers[i].Debug)
                {
                    ensoSettings.musicuid = musicInfoAccessers[i].Id;
                    ensoSettings.musicUniqueId = musicInfoAccessers[i].UniqueId;
                }
            }
        }

        MusicDataInterface.MusicInfoAccesser infoByUniqueId2 = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.GetInfoByUniqueId(ensoSettings.musicUniqueId);
        if (infoByUniqueId2 != null)
        {
            ensoSettings.songFilePath = infoByUniqueId2.SongFileName;
        }

        __instance.DecidePartsSetting();
        if (ensoSettings.ensoType == EnsoData.EnsoType.Normal)
        {
            int num = 0;
            int dlcType = 2;
            if (ensoSettings.rankMatchType == EnsoData.RankMatchType.None)
            {
                num = ((ensoSettings.playerNum != 1) ? 1 : 0);
            }
            else if (ensoSettings.rankMatchType == EnsoData.RankMatchType.RankMatch)
            {
                num = 2;
                ensoSettings.isRandomSelect = false;
                ensoSettings.isDailyBonus = false;
            }
            else
            {
                num = 3;
                ensoSettings.isRandomSelect = false;
                ensoSettings.isDailyBonus = false;
            }

            TaikoSingletonMonoBehaviour<CommonObjects>.Instance.CosmosLib._kpiListCommon._musicKpiInfo.SetMusicSortSettings(num, dlcType, ensoSettings.isRandomSelect, ensoSettings.isDailyBonus);
        }
        else
        {
            ensoSettings.isRandomSelect = false;
            ensoSettings.isDailyBonus = false;
        }

        typeof(EnsoDataManager).GetField("ensoSettings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, ensoSettings);
        return false;
    }

    #endregion

    #region Read Fumen

    private static readonly Regex fumenFilePathRegex = new Regex("(?<songID>.*?)_(?<difficulty>[ehmnx])(_(?<songIndex>[12]))?.bin");

    private static readonly Dictionary<object, IntPtr> playerToFumenData = new Dictionary<object, IntPtr>();

    /// <summary>
    /// Read unencrypted Fumen files, save them to <see cref="playerToFumenData"/>
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
        var songId = match.Groups["songID"].Value;
        var difficulty = match.Groups["difficulty"].Value;
        var songIndex = match.Groups["songIndex"].Value;

        if (!idToSong.TryGetValue(songId, out var songInstance))
        {
            Log.LogError($"Cannot find song with id: {songId}");
            return true;
        }

        var path = songInstance.FolderPath;
        var songName = songInstance.SongName;

        var files = Directory.GetFiles(path, "*.bin");
        if (files.Length == 0)
        {
            Log.LogError($"Cannot find fumen at {path}");
            return true;
        }

        var newPath = GetPathOfBestFumen();
        if (!File.Exists(newPath))
        {
            Log.LogError($"Cannot find fumen for {newPath}");
            return true;
        }

        type.GetMethod("Dispose").Invoke(__instance, new object[] { });
        type.GetField("fumenPath").SetValue(__instance, newPath);

        byte[] array = File.ReadAllBytes(newPath);
        if (songInstance.AreFilesGZipped)
        {
            using var memoryStream = new MemoryStream(array);
            using var destination = new MemoryStream();
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            gzipStream.CopyTo(destination);
            array = destination.ToArray();
        }

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

        string GetPathOfBestFumen()
        {
            var baseSongPath = Path.Combine(path, $"{songName}");
            var withDifficulty = baseSongPath + $"_{difficulty}";
            var withSongIndex = withDifficulty + (string.IsNullOrWhiteSpace(songIndex) ? "" : $"_{songIndex}");

            var testPath = withSongIndex + ".bin";
            if (File.Exists(testPath))
                return testPath;

            testPath = withDifficulty + ".bin";
            if (File.Exists(testPath))
                return testPath;

            // add every difficulty below this one
            Difficulty difficultyEnum = (Difficulty) Enum.Parse(typeof(Difficulty), difficulty);
            int difficultyInt = (int) difficultyEnum;

            var checkDifficulties = new List<Difficulty>();

            for (int i = 1; i < (int) Difficulty.Count; i++)
            {
                AddIfInRange(difficultyInt - i);
                AddIfInRange(difficultyInt + i);

                void AddIfInRange(int checkDifficulty)
                {
                    if (checkDifficulty is >= 0 and < (int) Difficulty.Count)
                        checkDifficulties.Add((Difficulty) checkDifficulty);
                }
            }

            foreach (var testDifficulty in checkDifficulties)
            {
                withDifficulty = baseSongPath + $"_{testDifficulty.ToString()}";
                testPath = withDifficulty + ".bin";
                if (File.Exists(testPath))
                    return testPath;
                testPath = withDifficulty + "_1.bin";
                if (File.Exists(testPath))
                    return testPath;
                testPath = withDifficulty + "_2.bin";
                if (File.Exists(testPath))
                    return testPath;
            }

            // uh... can't find it?
            return string.Empty;
        }
    }

    private enum Difficulty
    {
        e,
        h,
        m,
        n,
        x,
        Count,
    }

    private static Difficulty[] AllDifficulties = (Difficulty[]) Enum.GetValues(typeof(Difficulty));

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

            var songName = match.Groups["songName"].Value;
            if (!idToSong.TryGetValue(songName, out var songInstance))
            {
                Log.LogError($"Cannot find song : {songName}");
                yield break;
            }

            var newPath = Path.Combine(songInstance.FolderPath, $"{sheetName.Replace(songName, songInstance.SongName)}.bin");

            var bytes = File.ReadAllBytes(newPath);
            if (songInstance.AreFilesGZipped)
            {
                using var memoryStream = new MemoryStream(bytes);
                using var destination = new MemoryStream();
                using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
                gzipStream.CopyTo(destination);
                bytes = destination.ToArray();
            }

            var cueSheet = CriAtom.AddCueSheetAsync(sheetName, bytes, null);
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

        var songName = match.Groups["songName"].Value;

        if (!idToSong.TryGetValue(songName, out var songInstance))
        {
            Log.LogError($"Cannot find song : {songName}");
            return true;
        }

        var newPath = Path.Combine(songInstance.FolderPath, $"{sheetName.Replace(songName, songInstance.SongName)}.bin");

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

        var bytes = File.ReadAllBytes(newPath);
        if (songInstance.AreFilesGZipped)
        {
            using var memoryStream = new MemoryStream(bytes);
            using var destination = new MemoryStream();
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            gzipStream.CopyTo(destination);
            bytes = destination.ToArray();
        }

        var cueSheet = CriAtom.AddCueSheetAsync(sheetName, bytes, null);
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
    public class CustomMusicSaveDataBody
    {
        public Dictionary<int, MusicInfoEx> CustomTrackToMusicInfoEx = new();
        public Dictionary<int, EnsoRecordInfo[]> CustomTrackToEnsoRecordInfo = new();
    }

    /// <summary>
    /// This acts as a wrapper for the taiko save data formatting to decrease file size
    /// </summary>
    [Serializable]
    public class CustomMusicSaveDataBodySerializable
    {
        [JsonProperty("m")] public Dictionary<int, MusicInfoExSerializable> CustomTrackToMusicInfoEx = new();
        [JsonProperty("r")] public Dictionary<int, EnsoRecordInfoSerializable[]> CustomTrackToEnsoRecordInfo = new();


        public static explicit operator CustomMusicSaveDataBodySerializable(CustomMusicSaveDataBody m)
        {
            var result = new CustomMusicSaveDataBodySerializable();
            foreach (var musicInfoEx in m.CustomTrackToMusicInfoEx)
                result.CustomTrackToMusicInfoEx[musicInfoEx.Key] = musicInfoEx.Value;
            foreach (var ensoRecord in m.CustomTrackToEnsoRecordInfo)
            {
                var array = new EnsoRecordInfoSerializable[ensoRecord.Value.Length];
                for (var i = 0; i < ensoRecord.Value.Length; i++)
                    array[i] = ensoRecord.Value[i];
                result.CustomTrackToEnsoRecordInfo[ensoRecord.Key] = array;
            }

            return result;
        }

        public static explicit operator CustomMusicSaveDataBody(CustomMusicSaveDataBodySerializable m)
        {
            var result = new CustomMusicSaveDataBody();
            foreach (var musicInfoEx in m.CustomTrackToMusicInfoEx)
                result.CustomTrackToMusicInfoEx[musicInfoEx.Key] = musicInfoEx.Value;
            foreach (var ensoRecord in m.CustomTrackToEnsoRecordInfo)
            {
                var array = new EnsoRecordInfo[ensoRecord.Value.Length];
                for (var i = 0; i < ensoRecord.Value.Length; i++)
                    array[i] = ensoRecord.Value[i];
                result.CustomTrackToEnsoRecordInfo[ensoRecord.Key] = array;
            }

            return result;
        }


        [Serializable]
        public class MusicInfoExSerializable
        {
            [JsonProperty("f", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool favorite;

            [JsonProperty("favorite")]
            private bool favorite_v0
            {
                set => favorite = value;
            }

            [JsonProperty("n", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool isNew;

            [JsonProperty("isNew")]
            private bool isNew_v0
            {
                set => favorite = value;
            }

            public static implicit operator MusicInfoEx(MusicInfoExSerializable m) => new()
            {
                favorite = m.favorite,
                isNew = m.isNew,
            };

            public static implicit operator MusicInfoExSerializable(MusicInfoEx m) => new()
            {
                favorite = m.favorite,
                isNew = m.isNew,
            };
        }

        [Serializable]
        public class EnsoRecordInfoSerializable
        {
            [JsonProperty("h", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public HiScoreRecordInfoSerializable normalHiScore;

            [JsonProperty("normalHiScore", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            private HiScoreRecordInfoSerializable normalHiScore_v0
            {
                set => normalHiScore = value;
            }

            [JsonProperty("c", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public DataConst.CrownType crown;

            [JsonProperty("crown", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            private DataConst.CrownType crown_v0
            {
                set => crown = value;
            }

            [JsonProperty("p", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int playCount;

            [JsonProperty("playCount", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int playCount_v0
            {
                set => playCount = value;
            }

            [JsonProperty("l", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool cleared;

            [JsonProperty("cleared", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            private bool cleared_v0
            {
                set => cleared = value;
            }

            [JsonProperty("g", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool allGood;

            [JsonProperty("allGood", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
            private bool allGood_v0
            {
                set => allGood = value;
            }

            public static implicit operator EnsoRecordInfo(EnsoRecordInfoSerializable e) => new()
            {
                normalHiScore = e.normalHiScore,
                crown = e.crown,
                playCount = e.playCount,
                cleared = e.cleared,
                allGood = e.allGood,
            };

            public static implicit operator EnsoRecordInfoSerializable(EnsoRecordInfo e) => new()
            {
                normalHiScore = e.normalHiScore,
                crown = e.crown,
                playCount = e.playCount,
                cleared = e.cleared,
                allGood = e.allGood,
            };

            [Serializable]
            public struct HiScoreRecordInfoSerializable
            {
                [JsonProperty("s", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public int score;

                [JsonProperty("score", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public int score_v0
                {
                    set => score = value;
                }

                [JsonProperty("e", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short excellent;

                [JsonProperty("excellent", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short excellent_v0
                {
                    set => excellent = value;
                }

                [JsonProperty("g", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short good;

                [JsonProperty("good", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short good_v0
                {
                    set => good = value;
                }

                [JsonProperty("b", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short bad;

                [JsonProperty("bad", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short bad_v0
                {
                    set => bad = value;
                }

                [JsonProperty("c", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short combo;

                [JsonProperty("combo", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short combo_v0
                {
                    set => combo = value;
                }

                [JsonProperty("r", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short renda;

                [JsonProperty("renda", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
                public short renda_v0
                {
                    set => renda = value;
                }

                public static implicit operator HiScoreRecordInfo(HiScoreRecordInfoSerializable h) => new()
                {
                    score = h.score,
                    excellent = h.excellent,
                    good = h.good,
                    bad = h.bad,
                    combo = h.combo,
                    renda = h.renda,
                };

                public static implicit operator HiScoreRecordInfoSerializable(HiScoreRecordInfo h) => new()
                {
                    score = h.score,
                    excellent = h.excellent,
                    good = h.good,
                    bad = h.bad,
                    combo = h.combo,
                    renda = h.renda,
                };
            }
        }
    }

    #endregion

    private class ConversionStatus
    {
        public static Regex ConversionResultRegex = new("(?<ID>-?\\d*)\\:(?<PATH>.*?)$");

        [JsonProperty("i")] public List<ConversionItem> Items = new();

        public override string ToString()
        {
            return $"{nameof(Items)}: {string.Join(",", Items)}";
        }

        public class ConversionItem
        {
            [JsonIgnore] public const int CurrentVersion = 1;
            [JsonIgnore] public const int MaxAttempts = 3;

            [JsonProperty("f")] public string FolderName;
            [JsonProperty("a")] public int Attempts;
            [JsonProperty("s")] public bool Successful;
            [JsonProperty("v")] public int Version = CurrentVersion;

            public override string ToString()
            {
                return $"{nameof(FolderName)}: {FolderName}, {nameof(Attempts)}: {Attempts}, {nameof(Successful)}: {Successful}, {nameof(Version)}: {Version}";
            }
        }
    }

    public class SongInstance : CustomSong
    {
        public string FolderPath;
        public string SongName;
    }
}
