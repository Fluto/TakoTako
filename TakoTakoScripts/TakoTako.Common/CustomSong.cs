using System;
using System.Runtime.Serialization;
using Newtonsoft.Json;

// ReSharper disable InconsistentNaming

namespace TakoTako.Common
{
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

        // Used for UID
        [DataMember] public int tjaFileHash;

        // Preview Details
        [DataMember] public int previewPos;
        [DataMember] public int fumenOffsetPos;

        [DataMember] public bool areFilesGZipped;

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
        /// The text to display by default, if any override exist, the game will use that text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string text;

        /// <summary>
        /// font for the default text, if any override exist, the game will use that text
        /// 0 == Japanese
        /// 1 == English
        /// 2 == Traditional Chinese
        /// 3 == Simplified Chinese
        /// 4 == Korean
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int font;

        /// <summary>
        /// 日本語 Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string jpText;

        /// <summary>
        /// 日本語 Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int jpFont;

        /// <summary>
        /// English Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string enText;

        /// <summary>
        /// English Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int enFont;

        /// <summary>
        /// Français Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string frText;

        /// <summary>
        /// Français Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int frFont;

        /// <summary>
        /// Italiano Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string itText;

        /// <summary>
        /// Italiano Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int itFont;

        /// <summary>
        /// Deutsch Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string deText;

        /// <summary>
        /// Deutsch Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int deFont;

        /// <summary>
        /// Español Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string esText;

        /// <summary>
        /// Español Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int esFont;

        /// <summary>
        /// 繁體中文 Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string tcText;

        /// <summary>
        /// 繁體中文 Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int tcFont;

        /// <summary>
        /// 简体中文 Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string scText;

        /// <summary>
        /// 简体中文 Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int scFont;

        /// <summary>
        /// 영어 Text
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string krText;

        /// <summary>
        /// 영어 Font
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int krFont;
    }
}