using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FlutoTaikoMods;
using HarmonyLib;
using HarmonyLib.Tools;

namespace TaikoMods
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public ConfigEntry<bool> ConfigSkipSplashScreen;
        public ConfigEntry<bool> ConfigDisableScreenChangeOnFocus;
        public ConfigEntry<bool> ConfigFixSignInScreen;
        public ConfigEntry<bool> ConfigRandomSongSelectSkip;
        public ConfigEntry<bool> ConfigEnableCustomSongs;
        public ConfigEntry<string> ConfigSongDirectory;
        public ConfigEntry<string> ConfigSaveDirectory;
        
        public static Plugin Instance;
        private Harmony _harmony;
        public static ManualLogSource Log;
        
        private void Awake()
        {
            Instance = this;
            Log = Logger;
            
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            
            SetupConfig();
            
            SetupHarmony();
        }

        private void SetupConfig()
        {
            var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            ConfigFixSignInScreen = Config.Bind("General",
                "FixSignInScreen",
                true,
                "When true this will apply the patch to fix signing into Xbox Live");
            
            ConfigSkipSplashScreen = Config.Bind("General",
                "SkipSplashScreen",
                true,
                "When true this will skip the intro");
            
            ConfigDisableScreenChangeOnFocus = Config.Bind("General",
                "DisableScreenChangeOnFocus",
                false,
                "When focusing this wont do anything jank, I thnk");
            
            this.ConfigRandomSongSelectSkip = Config.Bind("General",
                "RandomSongSelectSkip",
                true,
                "When true, the game will not proceed to the song screen when selecting a random song, instead letting you re-roll");
            
            ConfigEnableCustomSongs = Config.Bind("CustomSongs",
                "EnableCustomSongs",
                true,
                "When true this will load custom mods");
            
            ConfigSongDirectory = Config.Bind("CustomSongs",
                "SongDirectory",
                $"{userFolder}/Documents/TaikoTheDrumMasterMods/customSongs",
                "The directory where custom tracks are stored");

            ConfigSaveDirectory = Config.Bind("CustomSongs",
                "SaveDirectory",
                $"{userFolder}/Documents/TaikoTheDrumMasterMods/saves",
                "The directory where saves are stored");
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
            
            if (ConfigRandomSongSelectSkip.Value)
                _harmony.PatchAll(typeof(RandomSongSelectPatch));
            
            if (ConfigEnableCustomSongs.Value)
            {
                _harmony.PatchAll(typeof(MusicPatch));
                MusicPatch.Setup(_harmony);
            }
        }

        public void StartCustomCoroutine(IEnumerator enumerator)
        {
            StartCoroutine(enumerator);
        }
    }
}