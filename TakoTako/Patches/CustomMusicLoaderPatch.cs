using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Logging;
using Blittables;
using HarmonyLib;
#if TAIKO_IL2CPP
using UnhollowerBaseLib;
using BepInEx.IL2CPP.Utils.Collections;
using Object = Il2CppSystem.Object;
#endif
using Newtonsoft.Json;
using SongSelect;
using SongSelectRanking;
using TakoTako.Common;
using UnityEngine;

namespace TakoTako.Patches;

/// <summary>
/// This will allow custom songs to be read in
/// </summary>
[HarmonyPatch]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class CustomMusicLoaderPatch
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
                data.CustomTrackToEnsoRecordInfo ??= new System.Collections.Generic.Dictionary<int, EnsoRecordInfo[]>();
                data.CustomTrackToMusicInfoEx ??= new System.Collections.Generic.Dictionary<int, MusicInfoEx>();
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
                    var json = JsonConvert.SerializeObject((CustomMusicSaveDataBodySerializable) data);

                    using Stream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    using var streamWriter = new StreamWriter(fs);
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
                        foreach (var item in conversionStatus.Items.Where(item => item.Successful && item.Version == ConversionStatus.ConversionItem.CurrentVersion))
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

                            var resultCode = int.Parse(match.Groups["ID"].Value);
                            var folderPath = match.Groups["PATH"].Value;

                            folderPath = Path.GetFullPath(folderPath).Replace(Path.GetFullPath(musicDirectory), ".");

                            var existingEntry = conversionStatus.Items.FirstOrDefault(x => x.FolderName == folderPath);
                            var asciiFolderPath = Regex.Replace(folderPath, @"[^\u0000-\u007F]+", string.Empty);
                            if (resultCode >= 0)
                                Log.LogInfo($"Converted {asciiFolderPath} successfully");
                            else
                                Log.LogError($"Could not convert {asciiFolderPath}");

                            if (existingEntry == null)
                            {
                                conversionStatus.Items.Add(new ConversionStatus.ConversionItem()
                                {
                                    Attempts = 1,
                                    FolderName = folderPath,
                                    Successful = resultCode >= 0,
                                    ResultCode = resultCode,
                                    Version = ConversionStatus.ConversionItem.CurrentVersion,
                                });
                            }
                            else
                            {
                                existingEntry.Attempts++;
                                existingEntry.Successful = resultCode >= 0;
                                existingEntry.ResultCode = resultCode;
                                existingEntry.Version = ConversionStatus.ConversionItem.CurrentVersion;
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
                        var dataPath = Path.Combine(directory, SongDataFileName);
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

                        song.areFilesGZipped = true;
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

            song.SongName = song.id;
            song.FolderPath = directory;

            // Clip off the last bit of the hash to make sure that the number is positive. This will lead to more collisions, but we should be fine.
            if (isTjaSong)
            {
                // For TJAs, we need to hash the TJA file.
                song.UniqueId = song.tjaFileHash;

                if (song.UniqueId == 0)
                    throw new Exception("Converted TJA had no hash.");
            }
            else
            {
                // For official songs, we can just use the hash of the song internal name.
                song.UniqueId = (int) (MurmurHash2.Hash(song.id) & 0xFFFF_FFF);
            }

            if (song.UniqueId <= SaveDataMax)
                song.UniqueId += SaveDataMax;

            if (uniqueIdToSong.ContainsKey(song.UniqueId))
            {
                throw new Exception($"Song \"{song.id}\" has collision with \"{uniqueIdToSong[song.UniqueId].id}\", bailing out...");
            }

            song.id += $"_custom_{song.UniqueId}";
            customSongsList.Add(song);
            idToSong[song.id] = song;
            uniqueIdToSong[song.UniqueId] = song;
            Log.LogInfo($"Added{(isTjaSong ? " TJA" : "")} Song {song.songName.text}({song.UniqueId})");
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

            return conversionStatus.Items.Count != 0 && conversionStatus.Items.All(x => x.Successful && x.Version == ConversionStatus.ConversionItem.CurrentVersion);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Read in custom tracks

    [HarmonyPatch(typeof(DataManager), nameof(DataManager.Awake))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    private static void DataManager_PostFix(DataManager __instance)
    {
        if (__instance.MusicData != null)
        {
            MusicDataInterface_Postfix(__instance.MusicData);
            SongDataInterface_Postfix(__instance.SongData);
        }
    }

    [HarmonyPatch(typeof(DataManager), nameof(DataManager.ExchangeWordData))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    private static void ExchangeWordData_PostFix(DataManager __instance, string language)
    {
        if (__instance.MusicData != null)
        {
            WordDataInterface_Postfix(__instance.WordData, language);
        }
    }

    /// <summary>
    /// This will handle loading the meta data of tracks
    /// </summary>
    private static void MusicDataInterface_Postfix(MusicDataInterface __instance)
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

                var musicInfo = new MusicDataInterface.MusicInfoAccesser(
                    song.UniqueId, // From SongInstance, as we always recalculate it now
                    song.id,
                    $"song_{song.id}",
                    song.order,
                    song.genreNo,
                    true, // We always want to mark songs as DLC, otherwise ranked games will be broken as you are gonna match songs that other people don't have
                    false,
                    0,
                    true, // can we capture footage
                    2, // Always mark custom songs as "both players need to have this song to play it"
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
                    }
#if TAIKO_IL2CPP
                    , 0, // no idea what this is, going to mark them as default for now :)
                    string.Empty, // no idea what this is, going to mark them as default for now :)
                    string.Empty, // no idea what this is, going to mark them as default for now :)
                    false, // no idea what this is, going to mark them as default for now :),
                    new[] // no idea what this is, setting it to shinuti score
                    {
                        song.shinutiEasy,
                        song.shinutiNormal,
                        song.shinutiHard,
                        song.shinutiMania,
                        song.shinutiUra
                    }, new[] // no idea what this is, setting it to shinuti duet score
                    {
                        song.shinutiEasyDuet,
                        song.shinutiNormalDuet,
                        song.shinutiHardDuet,
                        song.shinutiManiaDuet,
                        song.shinutiUraDuet
                    }
#endif
                );
                musicInfoAccessors.Add(musicInfo);
            }

            #endregion

            BubbleSort(musicInfoAccessors, (a, b) => a.Order - b.Order);
            __instance.musicInfoAccessers = musicInfoAccessors;
        }
        catch (Exception e)
        {
            Log.LogError(e);
        }
    }

#if TAIKO_IL2CPP
    // this is to work around sorting unmanaged lists
    public static void BubbleSort<T>(Il2CppSystem.Collections.Generic.List<T> data, Func<T, T, int> compare) where T : Object
    {
        var tempList = new System.Collections.Generic.List<T>(data.ToArray());

        BubbleSort(tempList, compare);
        data.Clear();
        foreach (var temp in tempList)
            data.Add(temp);
    }
#endif
    // this is to work around sorting unmanaged lists
    public static void BubbleSort<T>(System.Collections.Generic.List<T> data, Func<T, T, int> compare)
    {
        int i, j;
        int N = data.Count;

        for (j = N - 1; j > 0; j--)
        {
            for (i = 0; i < j; i++)
            {
                if (compare(data[i], data[i + 1]) > 0)
                    (data[i + 1], data[i]) = (data[i], data[i + 1]);
            }
        }
    }

    /// <summary>
    /// This will handle loading the preview data of tracks
    /// </summary>
    private static void SongDataInterface_Postfix(SongDataInterface __instance)
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

        __instance.songInfoAccessers = musicInfoAccessors;
    }

    /// <summary>
    /// This will handle loading the localisation of tracks
    /// </summary>
    private static void WordDataInterface_Postfix(WordDataInterface __instance, string language)
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
            var wordListInfoRead = (ReadData<WordListInfo>) AccessTools.Property(typeof(WordDataInterface), nameof(WordDataInterface.wordListInfoRead)).GetValue(__instance);
            var dictionary = wordListInfoRead.InfomationDatas.ToList();

            for (int i = 0; i < musicInfoAccessors.Count; i++)
            {
                const string songDetailPrefix = "song_detail_";
#if TAIKO_IL2CPP
                var entry = musicInfoAccessors._items[i];
#elif TAIKO_MONO
                var entry = musicInfoAccessors[i];
#endif
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

                for (int j = musicInfoAccessors.Count - 1; j >= 0; j--)
                {
#if TAIKO_IL2CPP
                    var info = musicInfoAccessors._items[i];
#elif TAIKO_MONO
                    var info = musicInfoAccessors[i];
#endif
                    if (info.Key == songKey || info.Key == subtitleKey || info.Key == detailKey)
                        musicInfoAccessors.RemoveAt(i);
                }

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
                var (text, font) = GetValuesTextEntry(textEntry, languageValue);
                musicInfoAccessors.Add(new WordDataInterface.WordListInfoAccesser(key, text, font));
            }
        }

        __instance.wordListInfoAccessers = musicInfoAccessors;

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

        (string text, int font) GetValuesTextEntry(TextEntry textEntry, string selectedLanguage)
        {
            string text;
            int font;
            switch (selectedLanguage)
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

            // if this text is default, and we're not English / Japanese default to one of them
            if (string.IsNullOrEmpty(text) && selectedLanguage != "Japanese" && selectedLanguage != "English")
            {
                string fallbackLanguage;
                switch (selectedLanguage)
                {
                    case "Chinese":
                    case "ChineseT":
                    case "ChineseTraditional":
                    case "ChineseSimplified":
                    case "ChineseS":
                    case "Korean":
                        fallbackLanguage = "Japanese";
                        break;
                    default:
                        fallbackLanguage = "English";
                        break;
                }

                return GetValuesTextEntry(textEntry, fallbackLanguage);
            }

            if (!string.IsNullOrEmpty(text))
                return (text, font);

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
    [HarmonyPatch(typeof(SongSelectManager), nameof(SongSelectManager.LoadSongList))]
    [HarmonyPrefix]
    private static bool LoadSongList_Prefix(SongSelectManager __instance)
    {
        #region Edited Code

        Log.LogInfo("Loading custom save");
        var customData = GetCustomSaveData();

        #endregion

        #region Setup instanced variables / methods

        var playDataMgr = __instance.playDataMgr;
        var musicInfoAccess = __instance.musicInfoAccess;
        var enableKakuninSong = __instance.enableKakuninSong;
        var getLocalizedText = (string x) => __instance.GetLocalizedText(x);
        var updateSortCategoryInfo = __instance.UpdateSortCategoryInfo;

        #endregion

        if (playDataMgr == null)
        {
            Log.LogError("Could not find playDataMgr");
            return true;
        }

        var unsortedSongList = __instance.UnsortedSongList;
        unsortedSongList.Clear();
#if TAIKO_IL2CPP
        playDataMgr.GetMusicInfoExAllIl2cpp(0, out var dst);
#elif TAIKO_MONO
        playDataMgr.GetMusicInfoExAll(0, out var dst);
#endif
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
                song2.IsCap = true; // should DVR Capture be enabled?
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
                        GetPlayerRecordInfo(playDataMgr, 0, musicInfoAccess[j].UniqueId, (EnsoData.EnsoLevelType) k, out var dst4);
                        song2.NotPlayed[k] = dst4.playCount <= 0;
                        song2.NotCleared[k] = dst4.crown < DataConst.CrownType.Silver;
                        song2.NotFullCombo[k] = dst4.crown < DataConst.CrownType.Gold;
                        song2.NotDondaFullCombo[k] = dst4.crown < DataConst.CrownType.Rainbow;
#if TAIKO_IL2CPP
                        var highScore1 = song2.HighScores[k];
                        highScore1.hiScoreRecordInfos = dst4.normalHiScore;
                        highScore1.crown = dst4.crown;
                        song2.HighScores[k] = highScore1;
#elif TAIKO_MONO
                        song2.HighScores[k].hiScoreRecordInfos = dst4.normalHiScore;
                        song2.HighScores[k].crown = dst4.crown;
#endif

                        GetPlayerRecordInfo(playDataMgr, 1, musicInfoAccess[j].UniqueId, (EnsoData.EnsoLevelType) k, out var dst5);
                        song2.NotPlayed2P[k] = dst5.playCount <= 0;
                        song2.NotCleared2P[k] = dst4.crown < DataConst.CrownType.Silver;
                        song2.NotFullCombo2P[k] = dst5.crown < DataConst.CrownType.Gold;
                        song2.NotDondaFullCombo2P[k] = dst5.crown < DataConst.CrownType.Rainbow;

#if TAIKO_IL2CPP
                        var highScore2 = song2.HighScores2P[k];
                        highScore2.hiScoreRecordInfos = dst5.normalHiScore;
                        highScore2.crown = dst5.crown;
                        song2.HighScores2P[k] = highScore2;
#elif TAIKO_MONO
                        song2.HighScores2P[k].hiScoreRecordInfos = dst5.normalHiScore;
                        song2.HighScores2P[k].crown = dst5.crown;
#endif
                    }

                    song2.NewSong = isNew && (song2.DLC || song2.Price > 0);
                }

                unsortedSongList.Add(song2);
            }
        }

        BubbleSort(unsortedSongList, (a, b) =>
        {
            var value = a.SongGenre.CompareTo(b.SongGenre);
            if (value != 0)
                return value;

            return a.Order - b.Order;
        });

        __instance.SongList.Clear();
        foreach (var song in unsortedSongList)
            __instance.SongList.Add(song);

        __instance.UnsortedSongList = unsortedSongList;

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
#if TAIKO_IL2CPP
        __instance.playDataMgr.GetMusicInfoExAllIl2cpp(0, out var dst);
#elif TAIKO_MONO
        __instance.playDataMgr.GetMusicInfoExAll(0, out var dst);
#endif
        var customSaveData = GetCustomSaveData();

        bool saveCustomData = false;
        int num = 0;
        foreach (var unsortedSong in __instance.UnsortedSongList)
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
                var entry = dst[unsortedSong.UniqueId];
                entry.favorite = unsortedSong.Favorite;
                dst[unsortedSong.UniqueId] = entry;

                __instance.playDataMgr.SetMusicInfoEx(0, unsortedSong.UniqueId, ref entry, num >= __instance.UnsortedSongList.Count);
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
        var settings = __instance.settings;
        var playDataManager = __instance.playDataManager;
        var ensoDataManager = __instance.ensoDataManager;

        var selectedSongInfo = __instance.selectedSongInfo;
        var ensoMode = __instance.ensoMode;
        var ensoMode2P = __instance.ensoMode2P;
        var selectedCourse = __instance.selectedCourse;
        var selectedCourse2P = __instance.selectedCourse2P;
        var status = __instance.status;

        var songUniqueId = selectedSongInfo.UniqueId;

        settings.ensoType = EnsoData.EnsoType.Normal;
        settings.rankMatchType = EnsoData.RankMatchType.None;
        settings.musicuid = selectedSongInfo.Id;
        settings.musicUniqueId = songUniqueId;
        settings.genre = (EnsoData.SongGenre) selectedSongInfo.SongGenre;
        settings.playerNum = 1;
        var player1Entry = settings.ensoPlayerSettings[0];
        player1Entry.neiroId = ensoMode.neiro;
        player1Entry.courseType = (EnsoData.EnsoLevelType) selectedCourse;
        player1Entry.speed = ensoMode.speed;
        player1Entry.dron = ensoMode.dron;
        player1Entry.reverse = ensoMode.reverse;
        player1Entry.randomlv = ensoMode.randomlv;
        player1Entry.special = ensoMode.special;

        var array = selectedSongInfo.HighScores;
        player1Entry.hiScore = array[selectedCourse].hiScoreRecordInfos.score;
        settings.ensoPlayerSettings[0] = player1Entry;

        __instance.settings = settings;
        if (status.Is2PActive)
        {
            var player2Entry = settings.ensoPlayerSettings[1];
            player2Entry.neiroId = ensoMode2P.neiro;
            player2Entry.courseType = (EnsoData.EnsoLevelType) selectedCourse2P;
            player2Entry.speed = ensoMode2P.speed;
            player2Entry.dron = ensoMode2P.dron;
            player2Entry.reverse = ensoMode2P.reverse;
            player2Entry.randomlv = ensoMode2P.randomlv;
            player2Entry.special = ensoMode2P.special;
            GetPlayerRecordInfo(TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData, 1, songUniqueId, (EnsoData.EnsoLevelType) selectedCourse2P, out var dst);
            player2Entry.hiScore = dst.normalHiScore.score;
            settings.playerNum = 2;
            settings.ensoPlayerSettings[1] = player2Entry;
        }

        settings.debugSettings.isTestMenu = false;
        settings.rankMatchType = EnsoData.RankMatchType.None;
        settings.isRandomSelect = selectedSongInfo.IsRandomSelect;
        settings.isDailyBonus = selectedSongInfo.IsDailyBonus;
        ensoMode.songUniqueId = settings.musicUniqueId;
        ensoMode.level = (EnsoData.EnsoLevelType) selectedCourse;

        __instance.settings = settings;
        __instance.ensoMode = ensoMode;
        __instance.SetSaveDataEnsoMode(CourseSelect.PlayerType.Player1);
        ensoMode2P.songUniqueId = settings.musicUniqueId;
        ensoMode2P.level = (EnsoData.EnsoLevelType) selectedCourse2P;
        __instance.ensoMode2P = ensoMode2P;
        __instance.SetSaveDataEnsoMode(CourseSelect.PlayerType.Player2);

#if TAIKO_IL2CPP
        playDataManager.GetSystemOptionRemake(out var dst2);
#elif TAIKO_MONO
        playDataManager.GetSystemOption(out var dst2);
#endif

        int deviceTypeIndex = EnsoDataManager.GetDeviceTypeIndex(settings.ensoPlayerSettings[0].inputDevice);
        settings.noteDispOffset = dst2.onpuDispLevels[deviceTypeIndex];
        settings.noteDelay = dst2.onpuHitLevels[deviceTypeIndex];
        settings.songVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.InGameSong);
        settings.seVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Se);
        settings.voiceVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Voice);
        settings.bgmVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.Bgm);
        settings.neiroVolume = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MySoundManager.GetVolume(SoundManager.SoundType.InGameNeiro);
        settings.effectLevel = (EnsoData.EffectLevel) dst2.qualityLevel;
        __instance.settings = settings;
#if TAIKO_IL2CPP
        ensoDataManager.SetSettingsRemake(ref settings);
#elif TAIKO_MONO
        ensoDataManager.SetSettings(ref settings);
#endif
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
#if TAIKO_IL2CPP
            playDataManager.GetMusicInfoExAllIl2cpp(0, out var dst3);
#elif TAIKO_MONO
            playDataManager.GetMusicInfoExAll(0, out var dst3);
#endif
            var entry = dst3[songUniqueId];
            entry.isNew = false;
            dst3[songUniqueId] = entry;

            playDataManager.SetMusicInfoEx(0, songUniqueId, ref entry);
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
#if TAIKO_IL2CPP
        TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.EnsoData.CopySettingsRemake(out var dst);
#elif TAIKO_MONO
        TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.EnsoData.CopySettings(out var dst);
#endif
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
#if TAIKO_IL2CPP
        playData.GetMusicInfoExAllIl2cpp(0, out var dst3);
#elif TAIKO_MONO
        playData.GetMusicInfoExAll(0, out var dst3);
#endif

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

#if TAIKO_IL2CPP
        playData.GetPlayerInfoRemake(0, out var dst4);
#elif TAIKO_MONO
        playData.GetPlayerInfo(0, out var dst4);
#endif
        __instance.current_coins_num = dst4.donCoin;
        __instance.total_coins_num = dst4.getCoinsInTotal;
#if TAIKO_IL2CPP
        playData.GetRankMatchSeasonRecordInfoRemake(0, 0, out var dst5);
#elif TAIKO_MONO
        playData.GetRankMatchSeasonRecordInfo(0, 0, out var dst5);
#endif
        __instance.rank_point = dst5.rankPointMax;

        return false;
    }

    [HarmonyPatch(typeof(PlayDataManager), nameof(PlayDataManager.IsValueInRange))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    private static void IsValueInRange(int myValue, int minValue, int maxValue, ref bool __result)
    {
        // if the max value is the same as music max, hopefully we're validating song ids
        // in which case return true if this is one of our songs
        if (maxValue != DataConst.MusicMax) return;

        if (uniqueIdToSong.ContainsKey(myValue))
            __result = true;
    }

    #region Methods with GetPlayerRecordInfo

    // this doesn't patch well, so I have to redo each method that uses it
    // /// <summary>
    // /// Load scores from custom save data
    // /// </summary>
    // [HarmonyPatch(typeof(PlayDataManager), nameof(PlayDataManager.GetPlayerRecordInfo))]
    // [HarmonyPrefix]
    public static void GetPlayerRecordInfo(PlayDataManager __instance,
        int playerId,
        int uniqueId,
        EnsoData.EnsoLevelType levelType,
        out EnsoRecordInfo dst)
    {
        if (!uniqueIdToSong.ContainsKey(uniqueId))
        {
            __instance.GetPlayerRecordInfo(playerId, uniqueId, levelType, out dst);
            return;
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
    }

    [HarmonyPatch(typeof(CourseSelect), nameof(CourseSelect.SetInfo))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void SetInfo_Postfix(
        CourseSelect __instance,
        MusicDataInterface.MusicInfoAccesser song,
        bool isRandomSelect,
        bool isDailyBonus)
    {
        for (int levelType = 0; levelType < __instance.selectedSongInfo.HighScores.Length; ++levelType)
        {
            EnsoRecordInfo dst;
            GetPlayerRecordInfo(__instance.playDataManager, 0, song.UniqueId, (EnsoData.EnsoLevelType) levelType, out dst);
            var highScore = __instance.selectedSongInfo.HighScores[levelType];
            highScore.hiScoreRecordInfos = dst.normalHiScore;
            highScore.crown = dst.crown;
            __instance.selectedSongInfo.HighScores[levelType] = highScore;
        }
    }

    [HarmonyPatch(typeof(CourseSelect), nameof(CourseSelect.UpdateDiffCourseAnim))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void UpdateDiffCourseAnim_Postfix(CourseSelect __instance)
    {
        int num = __instance.selectedSongInfo.Stars[4] == 0 ? 4 : 5;
        for (int levelType = 0; levelType < num; ++levelType)
        {
            Animator iconCrown2 = __instance.diffCourseAnims[levelType].IconCrowns[1];
            if (__instance.status.Is2PActive)
            {
                GetPlayerRecordInfo(TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData, 1, __instance.selectedSongInfo.UniqueId, (EnsoData.EnsoLevelType) levelType, out var dst);
                switch (dst.crown)
                {
                    case DataConst.CrownType.Silver:
                        iconCrown2.Play("Silver");
                        break;
                    case DataConst.CrownType.Gold:
                        iconCrown2.Play("Gold");
                        break;
                    case DataConst.CrownType.Rainbow:
                        iconCrown2.Play("Rainbow");
                        break;
                    default:
                        iconCrown2.Play("None");
                        break;
                }
            }
        }
    }

    [HarmonyPatch(typeof(EnsoGameManager), nameof(EnsoGameManager.SetResults))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void SetResults_Postfix(EnsoGameManager __instance)
    {
        TaikoCoreFrameResults frameResults = __instance.ensoParam.GetFrameResults();
        for (int index = 0; index < __instance.settings.playerNum; ++index)
        {
            EnsoData.PlayerResult playerResult = __instance.ensoParam.GetPlayerResult(index);
            var eachPlayer = frameResults.GetEachPlayer(index);
            ref EachPlayer local = ref eachPlayer;
            if (__instance.ensoParam.IsOnlineRankedMatch && index == 1)
                __instance.SetEnsoInfoOnlineRecieve(ref local);
            playerResult.level = __instance.settings.ensoPlayerSettings[index].courseType;
            bool flag = frameResults.isAllOnpuEndPlayer[index];
            playerResult.resultType = (double) local.tamashii >= (double) local.constTamashiiNorm ? DataConst.ResultType.NormaClear : DataConst.ResultType.NormaFailer;
            if (((playerResult.resultType != DataConst.ResultType.NormaClear ? 0 : (local.countFuka == 0U ? 1 : 0)) & (flag ? 1 : 0)) != 0)
                playerResult.resultType = DataConst.ResultType.Fullcombo;
            playerResult.combomax = (int) local.maxCombo;
            playerResult.rendatotal = (int) local.countRenda;
            playerResult.hits = (int) local.countRyo + (int) local.countKa;
            playerResult.score = (int) local.score;
            playerResult.tamashii = local.tamashii;
            playerResult.isAllOnpuEnd = flag;
            GetPlayerRecordInfo(__instance.playDataMgr, index, __instance.settings.musicUniqueId, __instance.settings.ensoPlayerSettings[index].courseType, out var dst1);
            playerResult.isHiScore = (int) local.score > dst1.normalHiScore.score;
            playerResult.crown = DataConst.CrownType.None;
            for (int crown = (int) playerResult.crown; (DataConst.CrownType) crown > dst1.crown; --crown)
                playerResult.isNewCrown[crown] = true;

            if (index == 0)
                TaikoSingletonMonoBehaviour<CommonObjects>.Instance.CosmosLib._kpiListCommon._musicKpiInfo.SetEnsoResult1p(playerResult);
            else
                TaikoSingletonMonoBehaviour<CommonObjects>.Instance.CosmosLib._kpiListCommon._musicKpiInfo.SetEnsoResult2p(playerResult);
        }
    }

    [HarmonyPatch(typeof(SongSelectRankingBestScoreDisplay), nameof(SongSelectRankingBestScoreDisplay.SetMyInfo))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void SetMyInfo_Postfix(SongSelectRankingBestScoreDisplay __instance, int musicUniqueId, EnsoData.EnsoLevelType ensoLevel)
    {
        GetPlayerRecordInfo(TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData, 0, musicUniqueId, ensoLevel, out var dst);
        __instance.UpdateScoreDisplay(dst.normalHiScore);
    }

    [HarmonyPatch(typeof(CourseSelectScoreDisplay), nameof(CourseSelectScoreDisplay.UpdateDisplay))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void UpdateDisplay_Postfix(CourseSelectScoreDisplay __instance, int musicUniqueId, EnsoData.EnsoLevelType levelType)
    {
        GetPlayerRecordInfo(TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData, __instance.playerType == DataConst.PlayerType.Player_1 ? 0 : 1, musicUniqueId, levelType, out var dst);
        var normalHiScore = dst.normalHiScore;
        for (int index = 0; index < 6; ++index)
        {
            int num = 0;
            switch (index)
            {
                case 0:
                    num = normalHiScore.score;
                    break;
                case 1:
                    num = (int) normalHiScore.excellent;
                    break;
                case 2:
                    num = (int) normalHiScore.good;
                    break;
                case 3:
                    num = (int) normalHiScore.bad;
                    break;
                case 4:
                    num = (int) normalHiScore.combo;
                    break;
                case 5:
                    num = (int) normalHiScore.renda;
                    break;
            }

            __instance.numDisplays[index].NumberPlayer.SetValue(num);
        }
    }

    [HarmonyPatch(typeof(SongSelectScoreDisplay), nameof(SongSelectScoreDisplay.UpdateCrownNumDisplay))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void UpdateCrownNumDisplay_Postfix(SongSelectScoreDisplay __instance, int playerId)
    {
        PlayDataManager playData = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData;
        var musicInfoAccessers = TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.MusicData.musicInfoAccessers;
        int[,] numArray = new int[3, 5];
        foreach (MusicDataInterface.MusicInfoAccesser musicInfoAccesser in musicInfoAccessers)
        {
            int num = musicInfoAccesser.Stars[4] > 0 ? 5 : 4;
            for (int levelType = 0; levelType < num; ++levelType)
            {
                GetPlayerRecordInfo(playData, playerId, musicInfoAccesser.UniqueId, (EnsoData.EnsoLevelType) levelType, out var dst);
                switch (dst.crown)
                {
                    case DataConst.CrownType.Silver:
                        ++numArray[0, levelType];
                        break;
                    case DataConst.CrownType.Gold:
                        ++numArray[1, levelType];
                        break;
                    case DataConst.CrownType.Rainbow:
                        ++numArray[2, levelType];
                        break;
                }
            }
        }

        for (int index = 0; index < 5; ++index)
        {
            __instance.crownNums[index].CrownNumbers[0].SetNum(numArray[0, index]);
            __instance.crownNums[index].CrownNumbers[1].SetNum(numArray[1, index]);
            __instance.crownNums[index].CrownNumbers[2].SetNum(numArray[2, index]);
        }
    }

    [HarmonyPatch(typeof(SongSelectScoreDisplay), nameof(SongSelectScoreDisplay.UpdateScoreDisplay))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void UpdateScoreDisplay_Postfix(SongSelectScoreDisplay __instance, int playerId, int musicUniqueId, bool enableUra)
    {
        var num = enableUra ? 5 : 4;

        for (int levelType = 0; levelType < num; ++levelType)
        {
            GetPlayerRecordInfo(TaikoSingletonMonoBehaviour<CommonObjects>.Instance.MyDataManager.PlayData, playerId, musicUniqueId, (EnsoData.EnsoLevelType) levelType, out var dst);
            __instance.bestScores[levelType].RootObject.SetValue(dst.normalHiScore.score);
        }
    }

    #endregion

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

    [HarmonyPatch(typeof(SongSelectManager), nameof(SongSelectManager.Start))]
    [HarmonyPostfix]
    [HarmonyWrapSafe]
    public static void Start_Postfix(SongSelectManager __instance)
    {
        if (__instance.SongList == null)
            return;

        Plugin.Instance.StartCustomCoroutine(SetSelectedSongAsync());

        IEnumerator SetSelectedSongAsync()
        {
            yield return null;
            while (__instance.SongList.Count == 0 || __instance.isAsyncLoading)
                yield return null;

            // if the song id is < 0 then fix the selected song index
            var lastPlaySongId = GetCustomSaveData().LastSongID;
            if (lastPlaySongId == 0)
                yield break;

            var songIndex = -1;

            for (int i = 0; i < __instance.SongList.Count; i++)
            {
#if TAIKO_IL2CPP
                var song = (SongSelectManager.Song) __instance.SongList[(Index) i];
#elif TAIKO_MONO
                var song = __instance.SongList[i];
#endif
                if (song.UniqueId != lastPlaySongId)
                    continue;

                songIndex = i;
            }

            if (songIndex < 0)
                yield break;

            __instance.SelectedSongIndex = songIndex;
            __instance.songPlayer.Stop(true);
            __instance.songPlayer.Dispose();
            __instance.isSongLoadRequested = true;
            __instance.UpdateScoreDisplay();
            __instance.UpdateKanbanSurface();
            __instance.UpdateSortBarSurface();
            __instance.UpdateScoreDisplay();
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

    private static readonly Regex fumenFilePathRegex = new Regex("(?<songID>.*?_custom_\\d*?)_(?<difficulty>[ehmnx])(_(?<songIndex>[12]))?.bin");
    private static readonly System.Collections.Generic.Dictionary<string, byte[]> pathToData = new System.Collections.Generic.Dictionary<string, byte[]>();

    [HarmonyPatch(typeof(Cryptgraphy), nameof(Cryptgraphy.ReadAllAesAndGZipBytes))]
    [HarmonyPrefix]
    private static bool ReadAllAesAndGZipBytes_Prefix(string path, Cryptgraphy.AesKeyType type, 
        #if TAIKO_IL2CPP
        ref Il2CppStructArray<byte> __result
        #elif TAIKO_MONO
        ref byte[] __result
        #endif
        )
    {
        if (pathToData.TryGetValue(path, out var data))
        {
            __result = data;
            return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(FumenLoader.PlayerData), nameof(FumenLoader.PlayerData.Read))]
    [HarmonyPrefix]
    private static void Read_Prefix(FumenLoader.PlayerData __instance, ref string filePath)
    {
        GetCustomSaveData().LastSongID = 0;
        if (File.Exists(filePath))
            return;

        // if the file doesn't exist, perhaps it's a custom song?
        var fileName = Path.GetFileName(filePath);
        var match = fumenFilePathRegex.Match(fileName);
        if (!match.Success)
        {
            Log.LogError($"Cannot interpret {fileName}");
            return;
        }

        // get song id
        var songId = match.Groups["songID"].Value;
        var difficulty = match.Groups["difficulty"].Value;
        var songIndex = match.Groups["songIndex"].Value;

        if (!idToSong.TryGetValue(songId, out var songInstance))
        {
            Log.LogError($"Cannot find song with id: {songId}");
            return;
        }

        GetCustomSaveData().LastSongID = songInstance.UniqueId;
        SaveCustomData();
        var path = songInstance.FolderPath;
        var songName = songInstance.SongName;

        var files = Directory.GetFiles(path, "*.bin");
        if (files.Length == 0)
        {
            Log.LogError($"Cannot find fumen at {path}");
            return;
        }

        var customPath = GetPathOfBestFumen();
        if (!File.Exists(customPath))
        {
            Log.LogError($"Cannot find fumen for {customPath}");
            return;
        }

        byte[] array = File.ReadAllBytes(customPath);
        if (songInstance.areFilesGZipped)
        {
            using var memoryStream = new MemoryStream(array);
            using var destination = new MemoryStream();
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            gzipStream.CopyTo(destination);
            array = destination.ToArray();

            pathToData[customPath] = array;
        }
        else
        {
            pathToData[customPath] = array;
        }

        filePath = customPath;

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

            var checkDifficulties = new System.Collections.Generic.List<Difficulty>();

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

    #endregion

    #region Read Song

    private static readonly Regex musicFilePathRegex = new Regex("^song_(?<songName>.*?_custom_\\d*?)$");

    /// <summary>
    /// Read an unencrypted song "asynchronously" (it does it instantly, we should have fast enough PCs right?)
    /// </summary>
    /// <param name="__instance"></param>
    [HarmonyPatch(typeof(CriPlayer), nameof(CriPlayer.LoadAsync))]
    [HarmonyPrefix]
    [HarmonyWrapSafe]
    public static bool LoadAsync_Postfix(CriPlayer __instance, 
#if TAIKO_IL2CPP
        ref Il2CppSystem.Collections.IEnumerator __result
#elif TAIKO_MONO
        ref IEnumerator __result
#endif
        )
    {
        var sheetName = __instance.CueSheetName;
        var path = UnityEngine.Application.streamingAssetsPath + "/sound/" + sheetName + ".bin";

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

        __instance.isLoadingAsync = true;
        __instance.isCancelLoadingAsync = false;
        __instance.IsPrepared = false;
        __instance.IsLoadSucceed = false;
        __instance.LoadingState = CriPlayer.LoadingStates.Loading;
        __instance.LoadTime = -1f;
        __instance.loadStartTime = UnityEngine.Time.time;
        
        // Run this on the next frame
#if TAIKO_IL2CPP
        __result = LoadAsync().WrapToIl2Cpp();
#elif TAIKO_MONO
        __result = LoadAsync();
#endif
        return false;

        IEnumerator LoadAsync()
        {
            yield return null;
            
            var newPath = Path.Combine(songInstance.FolderPath, $"{sheetName.Replace(songName, songInstance.SongName)}.bin");
            var task = Task.Run(async () =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(newPath);
                    if (songInstance.areFilesGZipped)
                    {
                        using var memoryStream = new MemoryStream(bytes);
                        using var destination = new MemoryStream();
                        using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
                        await gzipStream.CopyToAsync(destination);
                        bytes = destination.ToArray();
                    }

                    return bytes;
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    return null;
                }
            });

            do
            {
                yield return null;
            } while (!task.IsCompleted);

            var bytes = task.Result;
            var cueSheet = CriAtom.AddCueSheetAsync(sheetName, bytes, null);

            __instance.CueSheet = cueSheet;

            if (cueSheet != null)
            {
                while (cueSheet.IsLoading)
                    yield return null;

                __instance.isLoadingAsync = false;
                __instance.IsLoadSucceed = true;
                __instance.LoadingState = CriPlayer.LoadingStates.Finished;
                __instance.LoadTime = 0;
                
                yield break;
            }

            Log.LogError($"Could not load music");
            __instance.LoadingState = CriPlayer.LoadingStates.Finished;
            __instance.isLoadingAsync = false;
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
        __instance.IsPrepared = false;
        __instance.LoadingState = CriPlayer.LoadingStates.Loading;
        __instance.IsLoadSucceed = false;
        __instance.LoadTime = -1f;
        __instance.loadStartTime = Time.time;

        if (sheetName == "")
        {
            __instance.LoadingState = CriPlayer.LoadingStates.Finished;
            __result = false;
            return false;
        }

        var bytes = File.ReadAllBytes(newPath);
        if (songInstance.areFilesGZipped)
        {
            using var memoryStream = new MemoryStream(bytes);
            using var destination = new MemoryStream();
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            gzipStream.CopyTo(destination);
            bytes = destination.ToArray();
        }

        var cueSheet = CriAtom.AddCueSheetAsync(sheetName, bytes, null);
        __instance.CueSheet = cueSheet;

        if (cueSheet != null)
        {
            __result = true;
            return false;
        }

        __instance.LoadingState = CriPlayer.LoadingStates.Finished;
        __result = false;
        return false;
    }

    #endregion

    #region Data Structures

    [Serializable]
    public class CustomMusicSaveDataBody
    {
        public int LastSongID;
        public System.Collections.Generic.Dictionary<int, MusicInfoEx> CustomTrackToMusicInfoEx = new();
        public System.Collections.Generic.Dictionary<int, EnsoRecordInfo[]> CustomTrackToEnsoRecordInfo = new();
    }

    /// <summary>
    /// This acts as a wrapper for the taiko save data formatting to decrease file size
    /// </summary>
    [Serializable]
    public class CustomMusicSaveDataBodySerializable
    {
        [JsonProperty("l")] public int LastSongID;

        [JsonProperty("m")] public System.Collections.Generic.Dictionary<int, MusicInfoExSerializable> CustomTrackToMusicInfoEx = new();

        [JsonProperty("CustomTrackToMusicInfoEx")]
        public System.Collections.Generic.Dictionary<int, MusicInfoExSerializable> CustomTrackToMusicInfoEx_v0
        {
            set => CustomTrackToMusicInfoEx = value;
        }

        [JsonProperty("r")] public System.Collections.Generic.Dictionary<int, EnsoRecordInfoSerializable[]> CustomTrackToEnsoRecordInfo = new();

        [JsonProperty("CustomTrackToEnsoRecordInfo")]
        public System.Collections.Generic.Dictionary<int, EnsoRecordInfoSerializable[]> CustomTrackToEnsoRecordInfo_v0
        {
            set => CustomTrackToEnsoRecordInfo = value;
        }

        public static explicit operator CustomMusicSaveDataBodySerializable(CustomMusicSaveDataBody m)
        {
            var result = new CustomMusicSaveDataBodySerializable();
            result.LastSongID = m.LastSongID;

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
            result.LastSongID = m.LastSongID;

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
                set => isNew = value;
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

        [JsonProperty("i")] public System.Collections.Generic.List<ConversionItem> Items = new();

        public override string ToString()
        {
            return $"{nameof(Items)}: {string.Join(",", Items)}";
        }

        public class ConversionItem
        {
            [JsonIgnore] public const int CurrentVersion = 2;
            [JsonIgnore] public const int MaxAttempts = 3;

            [JsonProperty("f")] public string FolderName;
            [JsonProperty("a")] public int Attempts;
            [JsonProperty("s")] public bool Successful;
            [JsonProperty("v")] public int Version;
            [JsonProperty("e")] public int ResultCode;

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
        public int UniqueId;
    }
}
