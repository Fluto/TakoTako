using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;
using SonicAudioLib.Archives;
using SonicAudioLib.CriMw;
using TakoTako.Common;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;
using SimpleHelpers;

namespace TJAConvert
{
    public static class Program
    {
        public const int PaddedSongTime = 2 * 1000; // in ms
        public const float TjaOffsetForPaddingSong = -1.0f; // in ms

        public static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine("Pass in a tja directory");
                return;
            }

            var directory = args[0];
            if (!Directory.Exists(directory))
            {
                Console.WriteLine("This is not a valid tja directory");
                return;
            }


            var result = await Run(directory);
            Console.OutputEncoding = Encoding.Unicode;
            Console.WriteLine(result);
        }

        /// <returns>0 if pass, -1 if failed unexpectedly, -2 if invalid tja, -3 Timeout</returns>
        public static async Task<string> Run(string directory)
        {
            StringBuilder result = new StringBuilder();
            foreach (var tjaPath in Directory.EnumerateFiles(directory, "*.tja"))
            {
                var fileName = Path.GetFileNameWithoutExtension(tjaPath);
                var truncatedName = fileName.Substring(0, Math.Min(fileName.Length, 10));
                if (truncatedName.Length != fileName.Length)
                    fileName = truncatedName + "...";

                string realDirectory;
                int namingAttempts = 0;
                do
                {
                    if (namingAttempts == 0)
                        realDirectory = Path.Combine(directory, $"{fileName} [GENERATED]");
                    else
                        realDirectory = Path.Combine(directory, $"{fileName} {namingAttempts}[GENERATED]");
                    namingAttempts++;
                } while (File.Exists(realDirectory));

                int intResult = -1;
                try
                {
                    intResult = await RunConversion();
                }
                catch
                {
                }

                result.AppendLine($"{intResult}:{realDirectory}");

                async Task<int> RunConversion()
                {
                    TJAMetadata metadata;
                    try
                    {
                        metadata = new TJAMetadata(tjaPath);
                    }
                    catch
                    {
                        Console.WriteLine("TJA Metadata is invalid, or points to invalid paths.");
                        return -2;
                    }

                    var originalAudioPath = $"{directory}/{metadata.AudioPath}";

                    if (!File.Exists(originalAudioPath))
                    {
                        Console.WriteLine("Audio path does not exist. Check WAVE field in TJA.");
                        return -2;
                    }
                        

                    var newDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    var tempOutDirectory = Path.Combine(newDirectory, Guid.NewGuid().ToString());
                    if (Directory.Exists(tempOutDirectory))
                        Directory.Delete(tempOutDirectory, true);
                    Directory.CreateDirectory(tempOutDirectory);

                    var originalTjaData = File.ReadAllBytes(tjaPath);
                    var tjaHash = (int) (MurmurHash2.Hash(originalTjaData) & 0xFFFF_FFF);

                    var passed = await TJAToFumens(metadata, tjaPath, tjaHash, tempOutDirectory);
                    if (passed >= 0) passed = CreateMusicFile(metadata, tjaHash, tempOutDirectory) ? 0 : -1;

                    var copyFilePath = Path.Combine(newDirectory, Path.GetFileName(originalAudioPath));
                    File.Copy(originalAudioPath, copyFilePath);

                    int millisecondsAddedSilence = metadata.Offset > TjaOffsetForPaddingSong ? PaddedSongTime : 0;

                    var audioExtension = Path.GetExtension(copyFilePath).TrimStart('.');
                    switch (audioExtension.ToLowerInvariant())
                    {
                        case "wav":
                            if (passed >= 0) passed = WavToACB(copyFilePath, tempOutDirectory, tjaHash, millisecondsAddedSilence: millisecondsAddedSilence) ? 0 : -1;
                            break;
                        case "ogg":
                            if (passed >= 0) passed = OGGToACB(copyFilePath, tempOutDirectory, tjaHash, millisecondsAddedSilence) ? 0 : -1;
                            break;
                        default:
                            Console.WriteLine($"Do not support {audioExtension} audio files");
                            passed = -4;
                            break;
                    }

                    if (passed >= 0)
                    {
                        if (Directory.Exists(realDirectory))
                            Directory.Delete(realDirectory, true);
                        Directory.CreateDirectory(realDirectory);
                        foreach (var filePath in Directory.EnumerateFiles(tempOutDirectory))
                        {
                            var extension = Path.GetExtension(filePath).Trim('.');
                            if (extension != "bin" && extension != "json")
                                continue;
                            var copyFileName = Path.GetFileName(filePath);
                            File.Copy(filePath, Path.Combine(realDirectory, copyFileName));
                        }
                    }

                    if (Directory.Exists(tempOutDirectory))
                        Directory.Delete(tempOutDirectory, true);

                    return passed;
                }
            }

            return result.ToString();
        }

        private static void Pack(string path)
        {
            const int bufferSize = 4096;
            string acbPath = path + ".acb";

            if (!File.Exists(acbPath))
                throw new FileNotFoundException("Unable to locate the corresponding ACB file. Please ensure that it's in the same directory.");

            CriTable acbFile = new CriTable();
            acbFile.Load(acbPath, bufferSize);

            CriAfs2Archive afs2Archive = new CriAfs2Archive();

            CriCpkArchive cpkArchive = new CriCpkArchive();
            CriCpkArchive extCpkArchive = new CriCpkArchive();
            cpkArchive.Mode = extCpkArchive.Mode = CriCpkMode.Id;

            using (CriTableReader reader = CriTableReader.Create((byte[]) acbFile.Rows[0]["WaveformTable"]))
            {
                while (reader.Read())
                {
                    ushort id = reader.ContainsField("MemoryAwbId") ? reader.GetUInt16("MemoryAwbId") : reader.GetUInt16("Id");

                    string inputName = id.ToString("D5");

                    inputName += ".hca";
                    inputName = Path.Combine(path, inputName);

                    if (!File.Exists(inputName))
                        throw new FileNotFoundException($"Unable to locate {inputName}");

                    CriAfs2Entry entry = new CriAfs2Entry
                    {
                        FilePath = new FileInfo(inputName),
                        Id = id
                    };
                    afs2Archive.Add(entry);
                }
            }

            acbFile.Rows[0]["AwbFile"] = null;
            acbFile.Rows[0]["StreamAwbAfs2Header"] = null;

            if (afs2Archive.Count > 0 || cpkArchive.Count > 0)
                acbFile.Rows[0]["AwbFile"] = afs2Archive.Save();

            acbFile.WriterSettings = CriTableWriterSettings.Adx2Settings;
            acbFile.Save(acbPath, bufferSize);
        }

        private static bool CreateMusicFile(TJAMetadata metadata, int tjaHash, string outputPath)
        {
            try
            {
                var addedTime = metadata.Offset > TjaOffsetForPaddingSong ? PaddedSongTime : 0;
                var musicInfo = new CustomSong
                {
                    id = tjaHash.ToString(),
                    order = 0,
                    genreNo = (int) metadata.Genre,
                    branchEasy = false,
                    branchNormal = false,
                    branchHard = false,
                    branchMania = false,
                    branchUra = false,
                    previewPos = (int) (metadata.PreviewTime * 1000) + addedTime,
                    fumenOffsetPos = (int) (metadata.Offset * 10) + (addedTime),
                    tjaFileHash = tjaHash,
                    songName = new TextEntry()
                    {
                        text = metadata.Title,
                        font = GetFontForText(metadata.Title),
                        jpText = metadata.TitleJA,
                        jpFont = string.IsNullOrWhiteSpace(metadata.TitleJA) ? 0 : GetFontForText(metadata.TitleJA),
                        enText = metadata.TitleEN,
                        enFont = string.IsNullOrWhiteSpace(metadata.TitleEN) ? 0 : GetFontForText(metadata.TitleEN),
                        scText = metadata.TitleCN,
                        scFont = string.IsNullOrWhiteSpace(metadata.TitleCN) ? 0 : GetFontForText(metadata.TitleCN),
                        tcText = metadata.TitleTW,
                        tcFont = string.IsNullOrWhiteSpace(metadata.TitleTW) ? 0 : GetFontForText(metadata.TitleTW),
                        krText = metadata.TitleKO,
                        krFont = string.IsNullOrWhiteSpace(metadata.TitleKO) ? 0 : GetFontForText(metadata.TitleKO),
                    },
                    songSubtitle = new TextEntry()
                    {
                        text = metadata.Subtitle,
                        font = GetFontForText(metadata.Subtitle),
                        jpText = metadata.SubtitleJA,
                        jpFont = string.IsNullOrWhiteSpace(metadata.SubtitleJA) ? 0 : GetFontForText(metadata.SubtitleJA),
                        enText = metadata.SubtitleEN,
                        enFont = string.IsNullOrWhiteSpace(metadata.SubtitleEN) ? 0 : GetFontForText(metadata.SubtitleEN),
                        scText = metadata.SubtitleCN,
                        scFont = string.IsNullOrWhiteSpace(metadata.SubtitleCN) ? 0 : GetFontForText(metadata.SubtitleCN),
                        tcText = metadata.SubtitleTW,
                        tcFont = string.IsNullOrWhiteSpace(metadata.SubtitleTW) ? 0 : GetFontForText(metadata.SubtitleTW),
                        krText = metadata.SubtitleKO,
                        krFont = string.IsNullOrWhiteSpace(metadata.SubtitleKO) ? 0 : GetFontForText(metadata.SubtitleKO),
                    },
                    songDetail = new TextEntry()
                    {
                        text = metadata.Detail,
                        font = GetFontForText(metadata.Detail),
                        jpText = metadata.DetailJA,
                        jpFont = string.IsNullOrWhiteSpace(metadata.DetailJA) ? 0 : GetFontForText(metadata.DetailJA),
                        enText = metadata.DetailEN,
                        enFont = string.IsNullOrWhiteSpace(metadata.DetailEN) ? 0 : GetFontForText(metadata.DetailEN),
                        scText = metadata.DetailCN,
                        scFont = string.IsNullOrWhiteSpace(metadata.DetailCN) ? 0 : GetFontForText(metadata.DetailCN),
                        tcText = metadata.DetailTW,
                        tcFont = string.IsNullOrWhiteSpace(metadata.DetailTW) ? 0 : GetFontForText(metadata.DetailTW),
                        krText = metadata.DetailKO,
                        krFont = string.IsNullOrWhiteSpace(metadata.DetailKO) ? 0 : GetFontForText(metadata.DetailKO),
                    },
                };

                foreach (var course in metadata.Courses)
                {
                    var isDouble = course.PlayStyle == TJAMetadata.PlayStyle.Double;
                    var shinuti = EstimateScoreBasedOnNotes(course);

                    //todo figure out the best score?
                    switch (course.CourseType)
                    {
                        case CourseType.Easy:
                            musicInfo.starEasy = course.Level;
                            musicInfo.scoreEasy = 1000000;
                            musicInfo.branchEasy = musicInfo.branchEasy || course.IsBranching;
                            if (isDouble)
                                musicInfo.shinutiEasyDuet = shinuti;
                            else
                                musicInfo.shinutiEasy = shinuti;
                            break;
                        case CourseType.Normal:
                            musicInfo.starNormal = course.Level;
                            musicInfo.scoreNormal = 1000000;
                            musicInfo.branchNormal = musicInfo.branchNormal || course.IsBranching;
                            if (isDouble)
                                musicInfo.shinutiNormalDuet = shinuti;
                            else
                                musicInfo.shinutiNormal = shinuti;
                            break;
                        case CourseType.Hard:
                            musicInfo.starHard = course.Level;
                            musicInfo.scoreHard = 1000000;
                            musicInfo.branchHard = musicInfo.branchHard || course.IsBranching;
                            if (isDouble)
                                musicInfo.shinutiHardDuet = shinuti;
                            else
                                musicInfo.shinutiHard = shinuti;
                            break;
                        case CourseType.Oni:
                            musicInfo.starMania = course.Level;
                            musicInfo.scoreMania = 1000000;
                            musicInfo.branchMania = musicInfo.branchMania || course.IsBranching;
                            if (isDouble)
                                musicInfo.shinutiManiaDuet = shinuti;
                            else
                                musicInfo.shinutiMania = shinuti;
                            break;
                        case CourseType.UraOni:
                            musicInfo.starUra = course.Level;
                            musicInfo.scoreUra = 1000000;
                            musicInfo.branchUra = musicInfo.branchUra || course.IsBranching;
                            if (isDouble)
                                musicInfo.shinutiUraDuet = shinuti;
                            else
                                musicInfo.shinutiUra = shinuti;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }


                // make sure each course as a score
                if (musicInfo.shinutiEasy == 0)
                    musicInfo.shinutiEasy = musicInfo.shinutiEasyDuet != 0 ? musicInfo.shinutiEasyDuet : 7352;
                if (musicInfo.shinutiNormal == 0)
                    musicInfo.shinutiNormal = musicInfo.shinutiNormalDuet != 0 ? musicInfo.shinutiNormalDuet : 4830;
                if (musicInfo.shinutiHard == 0)
                    musicInfo.shinutiHard = musicInfo.shinutiHardDuet != 0 ? musicInfo.shinutiHardDuet : 3144;
                if (musicInfo.shinutiMania == 0)
                    musicInfo.shinutiMania = musicInfo.shinutiManiaDuet != 0 ? musicInfo.shinutiManiaDuet : 2169;
                if (musicInfo.shinutiUra == 0)
                    musicInfo.shinutiUra = musicInfo.shinutiUraDuet != 0 ? musicInfo.shinutiUraDuet : 1420;

                if (musicInfo.shinutiEasyDuet == 0)
                    musicInfo.shinutiEasyDuet = musicInfo.shinutiEasy;
                if (musicInfo.shinutiNormalDuet == 0)
                    musicInfo.shinutiNormalDuet = musicInfo.shinutiNormal;
                if (musicInfo.shinutiHardDuet == 0)
                    musicInfo.shinutiHardDuet = musicInfo.shinutiHard;
                if (musicInfo.shinutiManiaDuet == 0)
                    musicInfo.shinutiManiaDuet = musicInfo.shinutiMania;
                if (musicInfo.shinutiUraDuet == 0)
                    musicInfo.shinutiUraDuet = musicInfo.shinutiUra;

                int EstimateScoreBasedOnNotes(TJAMetadata.Course course)
                {
                    return Math.Max(1, 1000000 / course.EstimatedNotes);
                }

                var json = JsonConvert.SerializeObject(musicInfo, Formatting.Indented);
                File.WriteAllText($"{outputPath}/data.json", json);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
        }

        private static async Task<int> TJAToFumens(TJAMetadata metadata, string tjaPath, int tjaHash, string outputPath)
        {
            var fileName = Path.GetFileName(tjaPath);
            var newPath = Path.Combine(outputPath, fileName);

            // copy the file here in case we need to make any edits to it
            if (File.Exists(newPath))
                File.Delete(newPath);

            File.Copy(tjaPath, newPath);

            int passed = 0;
            // If this TJA has Ura Oni
            if (metadata.Courses.Any(x => x.CourseType == CourseType.UraOni))
            {
                // tja2bin doesn't support Ura Oni, so rip it out and change the course type to oni, then rename the final file
                passed = await ConvertUraOni(metadata, newPath, tjaHash);
                if (passed < 0)
                    return passed;
                // for every .bin in this directory, we can now add the prefix _x

                foreach (var filePath in Directory.EnumerateFiles(outputPath, "*.bin"))
                {
                    var binFileName = Path.GetFileName(filePath);
                    var binDirectory = Path.GetDirectoryName(filePath);

                    binFileName = binFileName
                        .Replace("_m_1.bin", "_x_1_.bin")
                        .Replace("_m_2.bin", "_x_2_.bin")
                        .Replace("_m.bin", "_x.bin");

                    File.Move(filePath, $"{binDirectory}/{binFileName}");
                }

                try
                {
                    metadata = new TJAMetadata(newPath);
                }
                catch
                {
                    Console.WriteLine("TJA Metadata during Fumens convesion process was invalid.");
                    return -2;
                }
            }

            // If this TJA has doubles, then rip them out out
            if (metadata.Courses.Any(x => x.PlayStyle == TJAMetadata.PlayStyle.Double))
            {
                // will need to create additional files to splice them out

                passed = await SpliceDoubles(metadata, newPath, tjaHash);
                if (passed < 0)
                    return passed;

                try
                {
                    metadata = new TJAMetadata(newPath);
                }
                catch
                {
                    Console.WriteLine("TJA Metadata while reading courses was invalid.");
                    return -2;
                }
            }

            if (metadata.Courses.All(x => x.PlayStyle != TJAMetadata.PlayStyle.Double))
                passed = await Convert(newPath, outputPath, tjaHash);

            if (passed < 0)
                return passed;

            var fumenFiles = Directory.EnumerateFiles(outputPath, "*.bin").ToList().Where(x => !x.StartsWith("song_")).ToList();
            if (fumenFiles.Count == 0)
            {
                Console.WriteLine($"Failed to create Fumens for {fileName}");
                return -1;
            }

            return passed;
        }

        private static async Task<int> ConvertUraOni(TJAMetadata metadata, string newPath, int tjaHash)
        {
            var directory = Path.GetDirectoryName(newPath);
            var fileName = Path.GetFileNameWithoutExtension(newPath);
            var encoding = FileEncoding.DetectFileEncoding(newPath);
            var lines = File.ReadAllLines(newPath, encoding).ToList();

            int courseStartIndex = lines.FindLastIndex(x =>
            {
                var match = TJAMetadata.TJAKeyValueRegex.Match(x);
                if (!match.Success)
                    return false;

                var type = match.Groups["KEY"].Value;
                return TJAMetadata.MainMetadataKeys.Contains(type.ToUpperInvariant());
            });

            var metaDataLines = lines.Take(courseStartIndex + 1).ToList();
            var courses = metadata.Courses.Where(x => x.CourseType == CourseType.UraOni).Reverse();

            foreach (var course in courses)
            {
                if (course.PlayStyle == TJAMetadata.PlayStyle.Single)
                {
                    var file = new List<string>(metaDataLines);
                    file.Add("");
                    file.AddRange(course.MetadataToTJA(courseTypeOverride: CourseType.Oni));
                    file.Add("");
                    file.AddRange(lines.GetRange(course.SongDataIndexStart, course.SongDataIndexEnd - course.SongDataIndexStart + 1));

                    var path = $"{directory}/{fileName}.tja";
                    File.WriteAllLines(path, file, encoding);

                    var passed = await Convert(path, directory, tjaHash);
                    if (passed < 0)
                        return passed;

                    lines.RemoveRange(course.CourseDataIndexStart, course.SongDataIndexEnd - course.CourseDataIndexStart + 1);
                }
                else
                {
                    var passed = await SplitP1P2(lines, course, directory, fileName, tjaHash, CourseType.Oni);
                    if (passed < 0)
                        return passed;
                }
            }

            File.WriteAllLines(newPath, lines, encoding);

            return 0;
        }

        /// <summary>
        /// This aims to separate P1 and P2 tracks for TJA2BIN to read
        /// </summary>
        private static async Task<int> SpliceDoubles(TJAMetadata metadata, string newPath, int tjaHash)
        {
            var directory = Path.GetDirectoryName(newPath);
            var fileName = Path.GetFileNameWithoutExtension(newPath);
            var encoding = FileEncoding.DetectFileEncoding(newPath);
            var lines = File.ReadAllLines(newPath, encoding).ToList();

            // first thing to do is inject missing metadata
            for (int i = metadata.Courses.Count - 1; i >= 0; i--)
            {
                var course = metadata.Courses[i];
                lines.RemoveRange(course.CourseDataIndexStart, course.CourseDataIndexEnd - course.CourseDataIndexStart);
                lines.Insert(course.CourseDataIndexStart, "");
                var courseData = course.MetadataToTJA();
                lines.InsertRange(course.CourseDataIndexStart + 1, courseData);
                lines.Insert(course.CourseDataIndexStart + 1 + courseData.Count, "");
            }

            File.WriteAllLines(newPath, lines, encoding);

            try
            {
                metadata = new TJAMetadata(newPath);
            }
            catch
            {
                Console.WriteLine("TJA Metadata while splicing doubles was invalid.");
                return -2;
            }

            var doubleCourses = metadata.Courses.Where(x => x.PlayStyle == TJAMetadata.PlayStyle.Double).Reverse();

            // remove doubles section
            foreach (var course in doubleCourses)
            {
                var passed = await SplitP1P2(lines, course, directory, fileName, tjaHash);
                if (passed < 0)
                    return passed;
            }

            File.WriteAllLines(newPath, lines, encoding);
            return 0;
        }

        private static async Task<int> SplitP1P2(List<string> lines, TJAMetadata.Course course, string directory, string fileName, int tjaHash, CourseType? courseTypeOverride = null)
        {
            // metadata end
            int courseStartIndex = lines.FindLastIndex(x =>
            {
                var match = TJAMetadata.TJAKeyValueRegex.Match(x);
                if (!match.Success)
                    return false;

                var type = match.Groups["KEY"].Value;
                return TJAMetadata.MainMetadataKeys.Contains(type.ToUpperInvariant());
            });
            var metaDataLines = lines.Take(courseStartIndex + 1).ToList();

            var startSongP1Index = lines.FindIndex(course.CourseDataIndexEnd, x => x.StartsWith("#START P1", StringComparison.InvariantCultureIgnoreCase));
            if (startSongP1Index < 0)
                return -1;
            var endP1Index = lines.FindIndex(startSongP1Index, x => x.StartsWith("#END", StringComparison.InvariantCultureIgnoreCase));
            if (endP1Index < 0)
                return -1;

            var startSongP2Index = lines.FindIndex(endP1Index, x => x.StartsWith("#START P2", StringComparison.InvariantCultureIgnoreCase));
            if (startSongP2Index < 0)
                return -1;
            var endP2Index = lines.FindIndex(startSongP2Index, x => x.StartsWith("#END", StringComparison.InvariantCultureIgnoreCase));
            if (endP2Index < 0)
                return -1;

            // otherwise create new files
            var p1File = new List<string>(metaDataLines);
            p1File.AddRange(course.MetadataToTJA(TJAMetadata.PlayStyle.Single, courseTypeOverride));
            p1File.AddRange(lines.GetRange(startSongP1Index, endP1Index - startSongP1Index + 1));
            RemoveP1P2(p1File);

            var path = $"{directory}/{fileName}_1.tja";
            File.WriteAllLines(path, p1File);

            var passed = await Convert(path, directory, tjaHash);
            if (passed < 0)
                return passed;

            var p2File = new List<string>(metaDataLines);
            p2File.AddRange(course.MetadataToTJA(TJAMetadata.PlayStyle.Single, courseTypeOverride));
            p2File.AddRange(lines.GetRange(startSongP2Index, endP2Index - startSongP2Index + 1));
            RemoveP1P2(p2File);

            path = $"{directory}/{fileName}_2.tja";
            File.WriteAllLines(path, p2File);

            passed = await Convert(path, directory, tjaHash);
            if (passed < 0)
                return passed;

            lines.RemoveRange(course.CourseDataIndexStart, course.SongDataIndexEnd - course.CourseDataIndexStart + 1);
            return 0;
        }

        private static void RemoveP1P2(List<string> playerLines)
        {
            for (var i = 0; i < playerLines.Count; i++)
            {
                var line = playerLines[i];
                if (line.StartsWith("#START", StringComparison.InvariantCultureIgnoreCase))
                    playerLines[i] = "#START";
            }
        }

        private static async Task<int> Convert(string tjaPath, string outputPath, int tjaHash)
        {
            var fileName = tjaHash.ToString();

            TJAMetadata metadata;
            try
            {
                metadata = new TJAMetadata(tjaPath);
            }
            catch
            {
                Console.WriteLine("TJA Metadata read during conversion was invalid");
                return -2;
            }

            var newPath = $"{outputPath}\\{fileName}";
            if (metadata.Courses.Count == 1)
            {
                var coursePostfix = metadata.Courses[0].CourseType.ToShort();
                if (fileName.EndsWith("_1"))
                    newPath = $"{outputPath}\\{fileName.Substring(0, fileName.Length - 2)}_{coursePostfix}_1.tja";
                else if (fileName.EndsWith("_2"))
                    newPath = $"{outputPath}\\{fileName.Substring(0, fileName.Length - 2)}_{coursePostfix}_2.tja";
                else
                    newPath = $"{outputPath}\\{fileName}_{coursePostfix}.tja";
            }

            var encoding = FileEncoding.DetectFileEncoding(tjaPath);
            var lines = ApplyGeneralFixes(File.ReadAllLines(tjaPath, encoding).ToList());
            File.WriteAllLines(newPath, lines, encoding);

            var currentDirectory = Environment.CurrentDirectory;
            var exePath = $"{currentDirectory}/tja2bin.exe";
            if (!File.Exists(exePath))
            {
                Console.WriteLine($"Cannot find tja2bin at {exePath}");
                return -1;
            }

            var timeStamp = Guid.NewGuid().ToString();
            bool isUsingTempFilePath = false;
            try
            {
                int attempts = 30;
                string result = string.Empty;

                do
                {
                    ProcessStartInfo info = new ProcessStartInfo()
                    {
                        FileName = exePath,
                        Arguments = $"\"{newPath}\"",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                    };

                    var process = new Process();
                    process.StartInfo = info;

                    CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationTokenSource.Token);
                    var runTask = RunProcess();

                    var taskResult = await Task.WhenAny(delayTask, runTask);
                    if (taskResult == delayTask)
                    {
                        // tja2bin can sometimes have memory leak
                        if (!process.HasExited)
                        {
                            process.Kill();
                            return -3;
                        }
                    }

                    attempts--;

                    // todo: Not sure how to solve this, so ignore it for now
                    if (result.Contains("branches must have same measure count"))
                    {
                        Console.WriteLine("TJA is invalid, branches do not have same measure count.");
                        return -2;
                    }

                    //Provide useful output when the metadata is bad
                    if (result.Contains("invalid Metadata"))
                    {
                        Console.WriteLine(result);
                        return -2;
                    }
                    //output any lines where there are warnings to aid with troubleshooting.
                    if (result.Contains("warning"))
                    {
                        Console.WriteLine(Regex.Match(result, "warning:.*"));
                    }


                    async Task RunProcess()
                    {
                        process.Start();
                        result = await process.StandardOutput.ReadToEndAsync();
                    }
                } while (FailedAndCanRetry(result) && attempts > 0);

                if (isUsingTempFilePath)
                {
                    foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(newPath)))
                    {
                        var tempFileName = Path.GetFileName(file);
                        var newName = $"{outputPath}\\{tempFileName}".Replace(timeStamp.ToString(), fileName);
                        if (File.Exists(newName))
                            File.Delete(newName);
                        File.Move(file, newName);
                    }

                    Directory.Delete(Path.GetDirectoryName(newPath));
                    if (File.Exists($"{outputPath}\\{fileName}.tja"))
                        File.Delete($"{outputPath}\\{fileName}.tja");
                }
                else
                {
                    File.Delete(newPath);
                }

                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return -1;
            }

            List<string> ApplyGeneralFixes(List<string> lines)
            {
                var noScoreInitRegex = new Regex("SCOREINIT:\\s*$", RegexOptions.CultureInvariant);
                var noScoreDiffRegex = new Regex("SCOREDIFF:\\s*$", RegexOptions.CultureInvariant);
                lines = lines
                    // get rid of comments
                    .Select(x =>
                    {
                        x = x.Trim();
                        var commentIndex = x.IndexOf("//", StringComparison.Ordinal);
                        if (commentIndex >= 0)
                            x = x.Substring(0, commentIndex);

                        // remove unwanted characters in tracks
                        if (x.Trim().EndsWith(",") && !x.Trim().Contains(":"))
                            x = Regex.Replace(x, "[^\\d,ABF]", "");

                        // if there's no scores, give them an arbitrary score
                        if (noScoreInitRegex.IsMatch(x))
                            return "SCOREINIT:440";
                        if (noScoreDiffRegex.IsMatch(x))
                            return "SCOREDIFF:113";

                        // I saw a few typos in a bunch of tjas, so I added a fix for it here, lol
                        return x.Replace("##", "#").Replace("#SCROOL", "#SCROLL").Replace("#SCROLLL", "#SCROLL").Replace("#SRCOLL", "#SCROLL").Replace("#MEAUSRE", "#MEASURE").Trim();
                    })
                    // remove lyrics, sections and delays as they are not supported by tja2bin
                    .Where(x => !(
                        x.StartsWith("#LYRIC", StringComparison.InvariantCultureIgnoreCase)
                        || x.StartsWith("#DELAY", StringComparison.InvariantCultureIgnoreCase)
                        || x.StartsWith("#MAKER", StringComparison.InvariantCultureIgnoreCase)
                        || x.StartsWith("#SECTION", StringComparison.InvariantCultureIgnoreCase)))
                    .ToList();
                return lines;
            }

            bool FailedAndCanRetry(string result)
            {
                metadata = new TJAMetadata(newPath);
                if (result.Contains("too many balloon notes"))
                {
                    // one of the fumens does not have the correct amount of balloon amounts
                    var currentLines = File.ReadLines(newPath).ToList();
                    var problematicCourse = GetCourseWithProblems();

                    // if there are two 9s close to each other, remove the second one
                    for (int i = problematicCourse.SongDataIndexStart; i < problematicCourse.SongDataIndexEnd; i++)
                    {
                        // if we find a 9 in a song, make sure there isn't another within this line or the next
                        var line = currentLines[i].Trim();
                        if (line.EndsWith(",", StringComparison.InvariantCultureIgnoreCase))
                        {
                            var index = line.IndexOf("9", StringComparison.Ordinal);
                            if (index < 0) continue;

                            var nextIndex = line.IndexOf("9", index + 1, StringComparison.Ordinal);
                            if (nextIndex > 0)
                            {
                                TryReplace(line, i, nextIndex);
                            }
                            else
                            {
                                // check the next line
                                for (int j = 1; j <= 1; j++)
                                {
                                    var lineAhead = currentLines[i + j];
                                    nextIndex = lineAhead.IndexOf("9", 0, StringComparison.Ordinal);
                                    if (nextIndex < 0)
                                        continue;

                                    TryReplace(lineAhead, i + j, nextIndex);
                                }
                            }

                            void TryReplace(string currentLine, int linesIndex, int searchStartIndex)
                            {
                                currentLine = currentLine.Remove(searchStartIndex, 1);
                                currentLine = currentLine.Insert(searchStartIndex, "0");
                                currentLines[linesIndex] = currentLine;
                            }
                        }
                    }

                    var balloonLine = currentLines.FindIndex(problematicCourse.CourseDataIndexStart, x => x.StartsWith("balloon:", StringComparison.InvariantCultureIgnoreCase));
                    if (balloonLine < 0)
                    {
                        // dunno stop here
                        return false;
                    }

                    // check to see if the balloon count matches up with the amount of 7
                    var balloonMatches = Regex.Matches(currentLines[balloonLine], "(\\d+)");

                    List<int> currentNumbers = new List<int>();
                    foreach (Match match in balloonMatches)
                        currentNumbers.Add(int.Parse(match.Value));

                    int balloons8 = 0;
                    int balloons79 = 0;
                    // bug perhaps 7|9 instead of 8?
                    Regex balloonRegex = new Regex("^.*(7|8|9).*,.*$");
                    for (int i = problematicCourse.SongDataIndexStart; i < problematicCourse.SongDataIndexEnd; i++)
                    {
                        var line = currentLines[i];
                        if (balloonRegex.IsMatch(line))
                        {
                            balloons8 += line.Count(x => x is '8');
                            balloons79 += line.Count(x => x is '7' or '9');
                        }
                    }

                    var balloons = Math.Max(balloons8, balloons79);

                    if (balloons > currentNumbers.Count)
                    {
                        // since we're patching this, do whatever we want
                        var finalBalloonText = "BALLOON:";
                        if (balloons >= currentNumbers.Count)
                        {
                            for (int i = currentNumbers.Count; i < balloons; i++)
                                currentNumbers.Add(4);
                        }

                        finalBalloonText += string.Join(",", currentNumbers);
                        currentLines[balloonLine] = finalBalloonText;
                    }

                    File.WriteAllLines(newPath, currentLines);
                    return true;
                }

                if (result.Contains("need a #BRANCHEND"))
                {
                    var currentLines = File.ReadLines(newPath).ToList();
                    var problematicCourse = GetCourseWithProblems();

                    currentLines.Insert(problematicCourse.SongDataIndexEnd, "#BRANCHEND");
                    File.WriteAllLines(newPath, currentLines);
                    return true;
                }

                if (result.Contains("invalid #BRANCHSTART"))
                {
                    var currentLines = File.ReadLines(newPath).ToList();
                    for (var i = 0; i < currentLines.Count; i++)
                    {
                        var line = currentLines[i];
                        if (!line.StartsWith("#BRANCHSTART p,", StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        var arguments = line.Substring("#BRANCHSTART ".Length).Split(',');
                        // This invalid branch start error needs to be manually resolved
                        if (arguments.Length != 3)
                            return false;

                        float number1;
                        float number2;
                        if (!float.TryParse(arguments[1], out var test))
                            return false;

                        number1 = test;
                        if (!float.TryParse(arguments[2], out test))
                            return false;

                        number2 = test;
                        currentLines[i] = $"#BRANCHSTART p,{(int) Math.Ceiling(number1)},{(int) Math.Ceiling(number2)}";
                    }

                    File.WriteAllLines(newPath, currentLines);
                    return true;
                }

                if (result.Contains("#E must be after the #N branch") || result.Contains("#M must be after the #E branch"))
                {
                    var currentLines = File.ReadLines(newPath).ToList();

                    string currentBranch = "";
                    int startOfBranch = -1;
                    int endOfBranch = -1;
                    var branches = new List<(string branch, int start, int end)>();

                    for (int i = 0; i < currentLines.Count; i++)
                    {
                        var line = currentLines[i];
                        bool isBranchStart = line.StartsWith("#BRANCHSTART", StringComparison.InvariantCultureIgnoreCase);

                        if (line.StartsWith("#BRANCHEND", StringComparison.InvariantCultureIgnoreCase) || isBranchStart)
                        {
                            if (!string.IsNullOrWhiteSpace(currentBranch))
                                EndBranch();

                            currentBranch = "";
                            // Order is N E M
                            if (branches.Count > 1)
                            {
                                // try to sort the order of branches
                                var branchesSorted = new List<(string branch, int start, int end)>(branches);
                                branchesSorted.Sort((x, y) => GetSortOrder(x.branch) - GetSortOrder(y.branch));
                                var branchesSortedValue = new List<List<string>>();

                                foreach (var branch in branchesSorted)
                                {
                                    var text = currentLines.GetRange(branch.start, branch.end - branch.start);
                                    // does this text actually have any music? found a case where a branch was empty /shrug
                                    if (!text.Any(x => x.Contains(",")))
                                        continue;

                                    branchesSortedValue.Add(text);
                                }

                                for (int j = 0; j < branches.Count; j++)
                                {
                                    var branch = branchesSorted[branchesSorted.Count - 1 - j];
                                    currentLines.RemoveRange(branch.start, branch.end - branch.start);
                                    if (j < 3)
                                        currentLines.InsertRange(branch.start, branchesSortedValue[j]);
                                }
                            }

                            branches.Clear();
                        }

                        else if (line.Trim().Equals("#M", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(currentBranch))
                                EndBranch();

                            currentBranch = "m";
                            startOfBranch = i;
                        }
                        else if (line.Trim().Equals("#N", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(currentBranch))
                                EndBranch();

                            currentBranch = "n";
                            startOfBranch = i;
                        }
                        else if (line.Trim().Equals("#E", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(currentBranch))
                                EndBranch();

                            currentBranch = "e";
                            startOfBranch = i;
                        }

                        int GetSortOrder(string branch)
                        {
                            switch (branch)
                            {
                                case "n":
                                    return 1;
                                case "e":
                                    return 2;
                                case "m":
                                    return 3;
                            }

                            return 0;
                        }

                        void EndBranch()
                        {
                            endOfBranch = i;
                            if (!string.IsNullOrEmpty(currentBranch))
                                branches.Add((currentBranch, startOfBranch, endOfBranch));
                        }
                    }

                    File.WriteAllLines(newPath, currentLines);
                    return true;
                }

                if (result.Contains("unexpected EOF") && !newPath.Contains(timeStamp.ToString()))
                {
                    // perhaps the files name is weird? let's rename it, and move this to a new folder
                    var newDirectory = Path.GetTempPath() + "\\" + timeStamp;
                    var fileName = timeStamp + ".tja";
                    var tempFilePath = Path.Combine(newDirectory, fileName);
                    Directory.CreateDirectory(newDirectory);
                    File.Copy(newPath, tempFilePath);
                    newPath = tempFilePath;
                    isUsingTempFilePath = true;
                    return true;
                }

                if (result.Contains("missing score information"))
                {
                    // Missing score! let's just to replace the existing one with whatever
                    for (int i = metadata.Courses.Count - 1; i >= 0; i--)
                    {
                        var course = metadata.Courses[i];

                        if (!string.IsNullOrWhiteSpace(course.Metadata.ScoreDiff) && !string.IsNullOrWhiteSpace(course.Metadata.ScoreInit))
                            continue;

                        course.Metadata.ScoreInit = "980";
                        course.Metadata.ScoreDiff = "320";

                        lines.RemoveRange(course.CourseDataIndexStart, course.CourseDataIndexEnd - course.CourseDataIndexStart);
                        lines.Insert(course.CourseDataIndexStart, "");
                        var newCourseData = course.MetadataToTJA();
                        lines.InsertRange(course.CourseDataIndexStart + 1, newCourseData);
                        lines.Insert(course.CourseDataIndexStart + 1 + newCourseData.Count, "");
                        File.WriteAllLines(newPath, lines);
                    }

                    return true;
                }

                return false;

                TJAMetadata.Course GetCourseWithProblems()
                {
                    var courses = new List<TJAMetadata.Course>(metadata.Courses);
                    // step 1. find the troublesome course
                    var resultLines = result.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in resultLines)
                    {
                        if (line.Contains("_x.bin"))
                            courses.RemoveAll(x => x.CourseType == CourseType.UraOni);
                        if (line.Contains("_m.bin"))
                            courses.RemoveAll(x => x.CourseType == CourseType.Oni);
                        if (line.Contains("_h.bin"))
                            courses.RemoveAll(x => x.CourseType == CourseType.Hard);
                        if (line.Contains("_n.bin"))
                            courses.RemoveAll(x => x.CourseType == CourseType.Normal);
                        if (line.Contains("_e.bin"))
                            courses.RemoveAll(x => x.CourseType == CourseType.Easy);
                    }

                    if (courses.Count == 0)
                    {
                        // dunno stop here
                        return null;
                    }

                    return courses[0];
                }
            }
        }

        private static bool OGGToACB(string oggPath, string outDirectory, int tjaHash, int millisecondsAddedSilence = 0)
        {
            try
            {
                var directory = Path.GetDirectoryName(oggPath);
                var acbPath = $"{directory}/{Guid.NewGuid().ToString()}";
                Directory.CreateDirectory(acbPath);

                using MemoryStream stream = new MemoryStream(Files.TemplateACBData);
                using var decompressor = new GZipStream(stream, CompressionMode.Decompress);
                using (FileStream compressedFileStream = File.Create($"{acbPath}.acb"))
                    decompressor.CopyTo(compressedFileStream);

                var hca = OggToHca(oggPath, millisecondsAddedSilence);
                if (hca == null)
                    return false;

                File.WriteAllBytes($"{acbPath}/00000.hca", hca);
                Pack(acbPath);
                if (File.Exists($"{outDirectory}/song_{tjaHash}.bin"))
                    File.Delete($"{outDirectory}/song_{tjaHash}.bin");

                File.Move($"{acbPath}.acb", $"{outDirectory}/song_{tjaHash}.bin");
                Directory.Delete(acbPath, true);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
        }

        private static bool WavToACB(string wavPath, string outDirectory, int tjaHash, bool deleteWav = false, int millisecondsAddedSilence = 0)
        {
            try
            {
                var directory = Path.GetDirectoryName(wavPath);
                var acbPath = $"{directory}/{Guid.NewGuid().ToString()}";
                Directory.CreateDirectory(acbPath);

                using MemoryStream stream = new MemoryStream(Files.TemplateACBData);
                using var decompressor = new GZipStream(stream, CompressionMode.Decompress);
                using (FileStream compressedFileStream = File.Create($"{acbPath}.acb"))
                    decompressor.CopyTo(compressedFileStream);

                var hca = WavToHca(wavPath, millisecondsAddedSilence);
                File.WriteAllBytes($"{acbPath}/00000.hca", hca);
                Pack(acbPath);
                if (File.Exists($"{outDirectory}/song_{tjaHash}.bin"))
                    File.Delete($"{outDirectory}/song_{tjaHash}.bin");

                File.Move($"{acbPath}.acb", $"{outDirectory}/song_{tjaHash}.bin");

                if (deleteWav)
                    File.Delete(wavPath);
                Directory.Delete(acbPath, true);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
        }

        private static byte[] WavToHca(string path, int millisecondSilence = 0)
        {
            var wavReader = new WaveReader();
            var hcaWriter = new HcaWriter();

            if (millisecondSilence > 0)
            {
                WaveFileReader reader = new WaveFileReader(path);
                var memoryStream = new MemoryStream();

                var trimmed = new OffsetSampleProvider(reader.ToSampleProvider())
                {
                    DelayBy = TimeSpan.FromMilliseconds(millisecondSilence)
                };
                WaveFileWriter.WriteWavFileToStream(memoryStream, trimmed.ToWaveProvider16());

                var audioData = wavReader.Read(memoryStream.ToArray());
                return hcaWriter.GetFile(audioData);
            }
            else
            {
                var audioData = wavReader.Read(File.ReadAllBytes(path));
                return hcaWriter.GetFile(audioData);
            }
        }

        private static byte[] OggToHca(string inPath, int millisecondSilence = 0)
        {
            try
            {
                using FileStream fileIn = new FileStream(inPath, FileMode.Open);
                var vorbis = new VorbisWaveReader(fileIn);
                var wavProvider = new SampleToWaveProvider16(vorbis);
                var memoryStream = new MemoryStream();

                if (millisecondSilence > 0)
                {
                    var trimmed = new OffsetSampleProvider(wavProvider.ToSampleProvider())
                    {
                        DelayBy = TimeSpan.FromMilliseconds(millisecondSilence)
                    };
                    WaveFileWriter.WriteWavFileToStream(memoryStream, trimmed.ToWaveProvider16());
                }
                else
                {
                    WaveFileWriter.WriteWavFileToStream(memoryStream, wavProvider);
                }

                var hcaWriter = new HcaWriter();
                var waveReader = new WaveReader();
                var audioData = waveReader.Read(memoryStream.ToArray());
                return hcaWriter.GetFile(audioData);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return null;
            }
        }

        private static bool OggToWav(string inPath, string outPath)
        {
            try
            {
                using FileStream fileIn = new FileStream(inPath, FileMode.Open);
                var vorbis = new VorbisWaveReader(fileIn);
                WaveFileWriter.CreateWaveFile16(outPath, vorbis);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
        }

        private static IEnumerable<char> GetCharsInRange(string text, int min, int max)
        {
            return text.Where(e => e >= min && e <= max);
        }

        private static int GetFontForText(string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
                return 1;

            var hiragana = GetCharsInRange(keyword, 0x3040, 0x309F).Any();
            var katakana = GetCharsInRange(keyword, 0x30A0, 0x30FF).Any();
            var kanji = GetCharsInRange(keyword, 0x4E00, 0x9FBF).Any();

            if (hiragana || katakana || kanji)
                return 0;

            var hangulJamo = GetCharsInRange(keyword, 0x1100, 0x11FF).Any();
            var hangulSyllables = GetCharsInRange(keyword, 0xAC00, 0xD7A3).Any();
            var hangulCompatibility = GetCharsInRange(keyword, 0x3130, 0x318F).Any();
            var hangulExtendedA = GetCharsInRange(keyword, 0xA960, 0xA97F).Any();
            var hangulExtendedB = GetCharsInRange(keyword, 0xD7B0, 0xD7FF).Any();

            if (hangulJamo
                || hangulSyllables
                || hangulCompatibility
                || hangulExtendedA
                || hangulExtendedB)
                return 4;

            var ascii = GetCharsInRange(keyword, 0x0041, 0x005A).Any();
            var ascii2 = GetCharsInRange(keyword, 0x0061, 0x007A).Any();
            if (ascii || ascii2)
                return 1;

            // don't know how to distinguish between simplified and traditional chinese... sorry :(
            return 3;
        }
    }
}
