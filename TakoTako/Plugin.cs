using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
#if TAIKO_IL2CPP
using BepInEx.IL2CPP.Utils;
using BepInEx.IL2CPP;
#endif

namespace TakoTako
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
#if TAIKO_MONO
    public class Plugin : BaseUnityPlugin
#elif TAIKO_IL2CPP
    public class Plugin : BasePlugin
#endif
    {
        public ConfigEntry<bool> ConfigSkipSplashScreen;
        public ConfigEntry<bool> ConfigDisableScreenChangeOnFocus;
        public ConfigEntry<bool> ConfigFixSignInScreen;
        public ConfigEntry<bool> ConfigEnableCustomSongs;
        public ConfigEntry<bool> ConfigEnableTaikoDrumSupport;
        public ConfigEntry<bool> ConfigTaikoDrumUseNintendoLayout;
        public ConfigEntry<bool> ConfigSkipDLCCheck;

        public ConfigEntry<string> ConfigSongDirectory;
        public ConfigEntry<bool> ConfigSaveEnabled;
        public ConfigEntry<string> ConfigSaveDirectory;
        public ConfigEntry<string> ConfigOverrideDefaultSongLanguage;
        public ConfigEntry<bool> ConfigApplyGenreOverride;

        public static Plugin Instance;
        private Harmony _harmony;
        public new static ManualLogSource Log;

        // private ModMonoBehaviourHelper _modMonoBehaviourHelper;

#if TAIKO_MONO
        private void Awake()
#elif TAIKO_IL2CPP
        public override void Load()
#endif
        {
            Instance = this;

#if TAIKO_MONO
            Log = Logger;
#elif TAIKO_IL2CPP
            Log = base.Log;
#endif

            Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            SetupConfig();
            SetupHarmony();
        }

        private void SetupConfig()
        {
            var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ConfigEnableCustomSongs = Config.Bind("CustomSongs",
                "EnableCustomSongs",
                true,
                "When true this will load custom mods");

            ConfigSongDirectory = Config.Bind("CustomSongs",
                "SongDirectory",
                $"{userFolder}/Documents/{typeof(Plugin).Namespace}/customSongs",
                "The directory where custom tracks are stored");

            ConfigSaveEnabled = Config.Bind("CustomSongs",
                "SaveEnabled",
                true,
                "Should there be local saves? Disable this if you want to wipe modded saves with every load");

            ConfigSaveDirectory = Config.Bind("CustomSongs",
                "SaveDirectory",
                $"{userFolder}/Documents/{typeof(Plugin).Namespace}/saves",
                "The directory where saves are stored");

            ConfigOverrideDefaultSongLanguage = Config.Bind("CustomSongs",
                "ConfigOverrideDefaultSongLanguage",
                string.Empty,
                "Set this value to {Japanese, English, French, Italian, German, Spanish, ChineseTraditional, ChineseSimplified, Korean} " +
                "to override all music tracks to a certain language, regardless of your applications language");

            ConfigApplyGenreOverride = Config.Bind("CustomSongs",
                "ConfigApplyGenreOverride",
                true,
                "Set this value to {01 Pop, 02 Anime, 03 Vocaloid, 04 Children and Folk, 05 Variety, 06 Classical, 07 Game Music, 08 Live Festival Mode, 08 Namco Original} " +
                "to override all track's genre in a certain folder. This is useful for TJA files that do not have a genre");

            ConfigFixSignInScreen = Config.Bind("General",
                "FixSignInScreen",
                false,
                "When true this will apply the patch to fix signing into Xbox Live");

            ConfigSkipSplashScreen = Config.Bind("General",
                "SkipSplashScreen",
                true,
                "When true this will skip the intro");
            
            ConfigSkipDLCCheck = Config.Bind("General",
                "SkipDLCCheck",
                true,
                "When true this will skip slow DLC checks");

            ConfigDisableScreenChangeOnFocus = Config.Bind("General",
                "DisableScreenChangeOnFocus",
                false,
                "When focusing this wont do anything jank, I thnk");

            ConfigEnableTaikoDrumSupport = Config.Bind("Controller.TaikoDrum",
                "ConfigEnableTaikoDrumSupport",
                true,
                "This will enable support for Taiko drums, current tested with official Hori Drum");

            ConfigTaikoDrumUseNintendoLayout = Config.Bind("Controller.TaikoDrum",
                "ConfigTaikoDrumUseNintendoLayout",
                false,
                "This will use the Nintendo layout YX/BA for the Hori Taiko Drum");
        }

        private void SetupHarmony()
        {
            // Patch methods
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);

            if (ConfigSkipSplashScreen.Value)
                _harmony.PatchAll(typeof(SkipSplashScreen));

            if (ConfigFixSignInScreen.Value)
                _harmony.PatchAll(typeof(SignInPatch));

            if (ConfigDisableScreenChangeOnFocus.Value)
                _harmony.PatchAll(typeof(DisableScreenChangeOnFocus));

            if (ConfigEnableTaikoDrumSupport.Value)
                _harmony.PatchAll(typeof(TaikoDrumSupport));

            #if TAIKO_IL2CPP
            if (ConfigSkipDLCCheck.Value)
                _harmony.PatchAll(typeof(SkipDLCCheck));
            #endif
    
            if (ConfigEnableCustomSongs.Value)
            {
                _harmony.PatchAll(typeof(MusicPatch));
                MusicPatch.Setup(_harmony);
            }
        }

        public static MonoBehaviour GetMonoBehaviour() => TaikoSingletonMonoBehaviour<CommonObjects>.Instance;

        public void StartCustomCoroutine(IEnumerator enumerator)
        {
            #if TAIKO_MONO
            GetMonoBehaviour().StartCoroutine(enumerator);
            #elif TAIKO_IL2CPP
            GetMonoBehaviour().StartCoroutine(enumerator);
            #endif
        }
    }
}
