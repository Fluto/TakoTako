using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TJAConvert;

internal class TJAMetadata
{
    public string Id;
    public string Title;
    public string TitleJA;
    public string TitleEN;
    public string TitleCN;
    public string TitleTW;
    public string TitleKO;

    public string Detail;
// dunno what to put in here
#pragma warning disable CS0649
    public string DetailJA;
    public string DetailEN;
    public string DetailCN;
    public string DetailTW;
    public string DetailKO;
#pragma warning restore CS0649

    public string Subtitle;
    public string SubtitleJA;
    public string SubtitleEN;
    public string SubtitleCN;
    public string SubtitleTW;
    public string SubtitleKO;

    public string AudioPath;
    public float Offset;
    public float PreviewTime;
    public SongGenre Genre;

    public List<Course> Courses = new();

    public TJAMetadata(string tjaPath)
    {
        Id = Path.GetFileNameWithoutExtension(tjaPath);

        var lines = File.ReadLines(tjaPath).ToList();

        Title = FindAndGetField("TITLE");
        TitleJA = FindAndGetField("TITLEJA");
        TitleEN = FindAndGetField("TITLEEN");
        TitleCN = FindAndGetField("TITLECN");
        TitleTW = FindAndGetField("TITLETW");
        TitleKO = FindAndGetField("TITLEKO");

        Detail = FindAndGetField("MAKER");

        ModifySubtitle("SUBTITLE", x => Subtitle = x);
        ModifySubtitle("SUBTITLEJA", x => SubtitleJA = x);
        ModifySubtitle("SUBTITLEEN", x => SubtitleEN = x);
        ModifySubtitle("SUBTITLECN", x => SubtitleCN = x);
        ModifySubtitle("SUBTITLETW", x => SubtitleTW = x);
        ModifySubtitle("SUBTITLEKO", x => SubtitleKO = x);

        void ModifySubtitle(string key, Action<string> setSubtitle)
        {
            var entry = FindAndGetField(key);
            if (string.IsNullOrEmpty(entry))
                return;

            var subtitle = FindAndGetField(key).TrimStart('-', '+');
            setSubtitle(subtitle);
        }

        AudioPath = FindAndGetField("WAVE");
        Offset = float.Parse(FindAndGetField("OFFSET"));
        PreviewTime = float.Parse(FindAndGetField("DEMOSTART"));

        var genreEntry = FindAndGetField("GENRE");
        Genre = GetGenre(genreEntry);

        // start finding courses
        Course currentCourse = new Course();
        // find the last metadata entry
        int courseStartIndex = lines.FindLastIndex(x =>
        {
            var match = TJAKeyValueRegex.Match(x);
            if (!match.Success)
                return false;

            var type = match.Groups["KEY"].Value;
            return MainMetadataKeys.Contains(type.ToUpperInvariant());
        });
        // find where the track starts
        int courseEndIndex = lines.FindIndex(courseStartIndex, x => x.StartsWith("#START", StringComparison.InvariantCultureIgnoreCase));

        do
        {
            bool newContent = false;
            for (int i = courseStartIndex + 1; i < courseEndIndex; i++)
            {
                var line = lines[i];
                var match = TJAKeyValueRegex.Match(line);
                if (!match.Success)
                    continue;

                newContent = true;
                var key = match.Groups["KEY"].Value.ToUpperInvariant().Trim();
                var value = match.Groups["VALUE"].Value.Trim();

                switch (key)
                {
                    case "COURSE":
                    {
                        currentCourse.CourseType = GetCourseType(value);
                        break;
                    }
                    case "LEVEL":
                    {
                        currentCourse.Level = int.Parse(value);
                        break;
                    }
                    case "STYLE":
                    {
                        currentCourse.PlayStyle = (PlayStyle) Enum.Parse(typeof(PlayStyle), value, true);
                        break;
                    }
                }

                if (key.Equals(nameof(Course.OtherMetadata.Balloon).ToUpperInvariant())) currentCourse.Metadata.Balloon = value;
                if (key.Equals(nameof(Course.OtherMetadata.ScoreInit).ToUpperInvariant())) currentCourse.Metadata.ScoreInit = value;
                if (key.Equals(nameof(Course.OtherMetadata.ScoreDiff).ToUpperInvariant())) currentCourse.Metadata.ScoreDiff = value;
                if (key.Equals(nameof(Course.OtherMetadata.BalloonNor).ToUpperInvariant())) currentCourse.Metadata.BalloonNor = value;
                if (key.Equals(nameof(Course.OtherMetadata.BalloonExp).ToUpperInvariant())) currentCourse.Metadata.BalloonExp = value;
                if (key.Equals(nameof(Course.OtherMetadata.BalloonMas).ToUpperInvariant())) currentCourse.Metadata.BalloonMas = value;
                if (key.Equals(nameof(Course.OtherMetadata.Exam1).ToUpperInvariant())) currentCourse.Metadata.Exam1 = value;
                if (key.Equals(nameof(Course.OtherMetadata.Exam2).ToUpperInvariant())) currentCourse.Metadata.Exam2 = value;
                if (key.Equals(nameof(Course.OtherMetadata.Exam3).ToUpperInvariant())) currentCourse.Metadata.Exam3 = value;
                if (key.Equals(nameof(Course.OtherMetadata.GaugeNcr).ToUpperInvariant())) currentCourse.Metadata.GaugeNcr = value;
                if (key.Equals(nameof(Course.OtherMetadata.Total).ToUpperInvariant())) currentCourse.Metadata.Total = value;
                if (key.Equals(nameof(Course.OtherMetadata.HiddenBranch).ToUpperInvariant())) currentCourse.Metadata.HiddenBranch = value;
            }

            currentCourse.CourseDataIndexStart = courseStartIndex + 1;
            currentCourse.CourseDataIndexEnd = courseEndIndex;
            currentCourse.SongDataIndexStart = courseEndIndex;

            // find the next end
            if (currentCourse.PlayStyle == PlayStyle.Double)
            {
                // go through p1 and p2
                var index = lines.FindIndex(courseEndIndex, x => x.StartsWith("#END", StringComparison.InvariantCultureIgnoreCase));
                courseStartIndex = lines.FindIndex(index + 1, x => x.StartsWith("#END", StringComparison.InvariantCultureIgnoreCase));
            }
            else
            {
                courseStartIndex = lines.FindIndex(courseEndIndex, x => x.StartsWith("#END", StringComparison.InvariantCultureIgnoreCase));
            }

            currentCourse.SongDataIndexEnd = courseStartIndex;

            // is this branching?
            for (int i = currentCourse.SongDataIndexStart; i < currentCourse.SongDataIndexEnd; i++)
            {
                var line = lines[i];
                if (line.Contains("#BRANCH"))
                {
                    currentCourse.IsBranching = true;
                    break;
                }
            }

            // calculate roughly the amount of song notes in this course
            int noteCount = 0;
            int branchNoteCount = 0;
            int branches = 0;
            bool inBranch = false;
            for (int i = currentCourse.SongDataIndexStart; i < currentCourse.SongDataIndexEnd; i++)
            {
                var line = lines[i].Trim();

                if (line.Equals("#N", StringComparison.InvariantCultureIgnoreCase)
                    || line.Equals("#E", StringComparison.InvariantCultureIgnoreCase)
                    || line.Equals("#M", StringComparison.InvariantCultureIgnoreCase))
                    branches++;

                var branchStart = line.StartsWith("#BRANCHSTART", StringComparison.InvariantCultureIgnoreCase);
                if (inBranch && (branchStart || line.StartsWith("#BRANCHEND", StringComparison.InvariantCultureIgnoreCase)))
                {
                    noteCount += branchNoteCount / Math.Max(1, branches);
                    inBranch = false;
                }

                if (!inBranch && branchStart)
                {
                    inBranch = true;
                    branchNoteCount = 0;
                    branches = 0;
                }

                if (!line.EndsWith(","))
                    continue;

                var notes = line.Count(x => x is '1' or '2' or '3' or '4');
                if (inBranch)
                    branchNoteCount += notes;
                else
                    noteCount += notes;
            }

            if (currentCourse.PlayStyle == PlayStyle.Double)
                currentCourse.EstimatedNotes = noteCount / 2;
            else
                currentCourse.EstimatedNotes = noteCount;

            if (newContent)
                Courses.Add(currentCourse);

            // duplicate the existing course
            currentCourse = new Course(currentCourse);
            // find the next start
            courseEndIndex = lines.FindIndex(courseStartIndex, x => x.StartsWith("#START", StringComparison.InvariantCultureIgnoreCase));
        } while (courseEndIndex > 0);

        string FindAndGetField(string fieldName)
        {
            var tileRegex = new Regex(string.Format(TJAFieldRegexTemplate, fieldName), RegexOptions.IgnoreCase);
            var index = lines.FindIndex(x => tileRegex.IsMatch(x));
            if (index < 0)
                return null;

            return tileRegex.Match(lines[index]).Groups["VALUE"].Value;
        }

        SongGenre GetGenre(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return SongGenre.Variety;

            switch (value.ToUpperInvariant())
            {
                case "アニメ":
                    return SongGenre.Anime;
                case "J-POP":
                    return SongGenre.Pop;
                case "どうよう":
                    return SongGenre.Children;
                case "バラエティ":
                    return SongGenre.Variety;
                case "ボーカロイド":
                case "VOCALOID":
                    return SongGenre.Vocaloid;
                case "クラシック":
                    return SongGenre.Classic;
                case "ゲームミュージック":
                    return SongGenre.Game;
                case "ナムコオリジナル":
                    return SongGenre.Namco;
            }

            return SongGenre.Variety;
        }

        CourseType GetCourseType(string value)
        {
            switch (value.ToUpperInvariant())
            {
                case "EASY":
                case "0":
                    return CourseType.Easy;
                case "NORMAL":
                case "1":
                    return CourseType.Normal;
                case "HARD":
                case "2":
                    return CourseType.Hard;
                case "ONI":
                case "3":
                    return CourseType.Oni;
                case "Edit":
                case "4":
                    return CourseType.UraOni;
            }

            return CourseType.UraOni;
        }
    }

    public const string TJAFieldRegexTemplate = "^{0}:\\s*(?<VALUE>.*?)\\s*$";
    public static Regex TJAKeyValueRegex = new("^(?<KEY>.*?):\\s*(?<VALUE>.*?)\\s*$", RegexOptions.IgnoreCase);

    public static HashSet<string> MainMetadataKeys = new()
    {
        "TITLE",
        "TITLEEN",
        "SUBTITLE",
        "SUBTITLEEN",
        "BPM",
        "WAVE",
        "OFFSET",
        "DEMOSTART",
        "GENRE",
        "SCOREMODE",
        "MAKER",
        "LYRICS",
        "SONGVOL",
        "SEVOL",
        "SIDE",
        "LIFE",
        "GAME",
        "HEADSCROLL",
        "BGIMAGE",
        "BGMOVIE",
        "MOVIEOFFSET",
        "TAIKOWEBSKIN",
    };


    public class Course
    {
        public CourseType CourseType = CourseType.Oni;
        public int Level = 5;
        public PlayStyle PlayStyle = PlayStyle.Single;
        public bool IsBranching = false;

        public OtherMetadata Metadata = new();

        public int CourseDataIndexStart;
        public int CourseDataIndexEnd;

        public int SongDataIndexStart;
        public int SongDataIndexEnd;

        public int EstimatedNotes = 0;

        public Course()
        {
        }

        public Course(Course course)
        {
            CourseType = course.CourseType;
            Level = course.Level;
            // PlayStyle = course.PlayStyle;
            Metadata = new OtherMetadata(course.Metadata);
        }

        public class OtherMetadata
        {
            public string Balloon;
            public string ScoreInit;
            public string ScoreDiff;
            public string BalloonNor;
            public string BalloonExp;
            public string BalloonMas;
            public string Exam1;
            public string Exam2;
            public string Exam3;
            public string GaugeNcr;
            public string Total;
            public string HiddenBranch;

            public OtherMetadata()
            {
            }

            public OtherMetadata(OtherMetadata metadata)
            {
                Balloon = metadata.Balloon;
                ScoreInit = metadata.ScoreInit;
                ScoreDiff = metadata.ScoreDiff;
                BalloonNor = metadata.BalloonNor;
                BalloonExp = metadata.BalloonExp;
                BalloonMas = metadata.BalloonMas;
                Exam1 = metadata.Exam1;
                Exam2 = metadata.Exam2;
                Exam3 = metadata.Exam3;
                GaugeNcr = metadata.GaugeNcr;
                Total = metadata.Total;
                HiddenBranch = metadata.HiddenBranch;
            }
        }

        public List<string> MetadataToTJA(PlayStyle? playStyleOverride = null, CourseType? courseTypeOverride = null)
        {
            List<string> result = new List<string>();
            result.Add($"COURSE:{(courseTypeOverride ?? CourseType).ToString()}");
            result.Add($"LEVEL:{Level.ToString()}");
            result.Add($"STYLE:{(playStyleOverride ?? PlayStyle).ToString()}");

            AddIfNotNull(nameof(OtherMetadata.Balloon), Metadata.Balloon);
            AddIfNotNull(nameof(OtherMetadata.ScoreInit), Metadata.ScoreInit);
            AddIfNotNull(nameof(OtherMetadata.ScoreDiff), Metadata.ScoreDiff);
            AddIfNotNull(nameof(OtherMetadata.BalloonNor), Metadata.BalloonNor);
            AddIfNotNull(nameof(OtherMetadata.BalloonExp), Metadata.BalloonExp);
            AddIfNotNull(nameof(OtherMetadata.BalloonMas), Metadata.BalloonMas);
            AddIfNotNull(nameof(OtherMetadata.Exam1), Metadata.Exam1);
            AddIfNotNull(nameof(OtherMetadata.Exam2), Metadata.Exam2);
            AddIfNotNull(nameof(OtherMetadata.Exam3), Metadata.Exam3);
            AddIfNotNull(nameof(OtherMetadata.GaugeNcr), Metadata.GaugeNcr);
            AddIfNotNull(nameof(OtherMetadata.Total), Metadata.Total);
            AddIfNotNull(nameof(OtherMetadata.HiddenBranch), Metadata.HiddenBranch);

            void AddIfNotNull(string name, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add($"{name.ToUpperInvariant()}:{value}");
            }

            return result;
        }
    }

    public enum PlayStyle
    {
        None = 0,
        Single = 1,
        Double = 2,
    }


    public enum SongGenre
    {
        Pop,
        Anime,
        Vocaloid,
        Variety,
        Children,
        Classic,
        Game,
        Namco,
    }
}

public enum CourseType
{
    Easy,
    Normal,
    Hard,
    Oni,
    UraOni
}

public static class CourseTypeExtensions
{
    public static string ToShort(this CourseType courseType)
    {
        return courseType switch
        {
            CourseType.Easy => "e",
            CourseType.Normal => "n",
            CourseType.Hard => "h",
            CourseType.Oni => "m",
            CourseType.UraOni => "x",
            _ => throw new ArgumentOutOfRangeException(nameof(courseType), courseType, null)
        };
    }
}
