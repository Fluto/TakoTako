# TakoTako

This is a mod for Taiko no Tatsujin: The Drum Master\
Currently has the features:
- Auto sign-in into xbox
- Skip splash screen
- Skip DLC check screen
- Allows custom official tracks or TJAs to be loaded into the game
- Override songs names to a certain language than the default one
- Allows use of the Hori Taiko Drum

---
## Supported Versions
<details>
<summary>Supported Versions</summary>
<p>
- <b>Mono</b>: 1.1.0.0 - 1.2.2.0
</p>
<p>
- <b>Il2CPP</b>: 1.3.0.0 - 1.6.00
</p>
</details>

----
## Installation

1. Install the game via the xbox app to a directory of your choice
2. Download [BepInEx](https://builds.bepinex.dev/projects/bepinex_be). If you're using a newer version of Taiko 1.3.0.0 or higher (you probably are) download the IL2CPP version `BepInEx_UnityIL2CPP_x64_XXXXX.zip`.If you're using a downgraded version of Taiko (1.2.2.0 on lower) download the "Mono" build, called something like `BepInEx_UnityMono_x64_XXXXX.zip`.
3. Go to where you installed your game, for example `C:\XboxGames\T Tablet\Content`
4. Paste all of the files from the .zip from step 5 into this folder
(It will look something like this)\
![](/readme-image-0.png)
5. We now need to give special permissions to the `BepInEx` folder. To do this, right click it, click on `Properties`, go to the `Security` tab, Click on the `Advanced` button, Click Change at the top, Under `Enter the object name to select` field type in your username and click `Check Names`. If the text doesn't become underscored that means you have entered the incorrect username. Then press `Ok` on that window to dismiss it. Going back to the `Advanced Security Settings Window` tick `Replace owner on subcontainers and objects` then finally press Apply.
![](/readme-image-1.png)
6. Run Taiko no Tatusjin The Drum Master once, then close it. This will generate some files
7. Look in your game's folder again, new files will have been generated under `.\BepInEx\plugins`
8. [Download my patch](https://github.com/Fluto/TaikoMods/releases). Once again download the IL2CPP version if you're on a newer version of Taiko, otherwise grab the Mono version.
9. Extract the `com.fluto.takotako` folder from the download in step 10 and paste it into the `.\BepInEx\plugins` folder\
![](/readme-image-2.png)
10. And you're done!


## Configuration

After installing the mod, and running the game it will generate files in `.\BepInEx\config`. Open `com.fluto.takotako.cfg` to configure this mod
Here you can enable each individual feature or redirect where custom songs will be loaded from


## Custom Songs

With this feature you can inject custom songs into the game!
To begin place custom songs in `SongDirectory` specified in your configuration file, by default this is `%userprofile%/Documents/TakoTako/customSongs`
Each song must have it's own directory with a unique name. 
These songs can be nested within folders.

The folder must have this structure:
```
Offical Songs
-- [MUSIC_ID]
---- data.json (this contains the metadata for the track)
---- song_[MUSIC_ID].bin (this is a raw .acb music file, this is a CRIWARE format)
---- [MUSIC_ID]_e.bin (all of these items below are unencrypted Fumens, which formats how the song is played)
---- [MUSIC_ID]_e_1.bin
---- [MUSIC_ID]_e_2.bin
---- [MUSIC_ID]_h.bin
---- [MUSIC_ID]_h_1.bin
---- [MUSIC_ID]_h_2.bin
---- [MUSIC_ID]_m.bin
---- [MUSIC_ID]_m_1.bin
---- [MUSIC_ID]_m_2.bin
---- [MUSIC_ID]_n.bin
---- [MUSIC_ID]_n_1.bin
---- [MUSIC_ID]_n_2.bin
---- [MUSIC_ID]_x.bin
---- [MUSIC_ID]_x_1.bin
---- [MUSIC_ID]_x_2.bin

TJA
-- [MUSIC_ID]
---- [MUSIC_ID].tja
---- song_[MUSIC_ID].ogg or .wav

Genre override
e.g. this will override the songs to pop
-- 01 Pop
---- [MUSIC_ID]
------ [MUSIC_ID].tja
------ song_[MUSIC_ID].ogg or .wav
```

```
data.json Format
{
  // Music Info
  int uniqueId; (This has to be a unique int, the mod will handle clashes, but it's best to generate a random int)
  string id; (This is the MUSIC_ID, this also has to be unique, because it's the same as the folder structure this file is in)
  int order; (default sorting order)
  int genreNo; (Genre enum [Pops 0, Anime 1, Vocalo 2, Variety 3, Children 4, Classic 5, Game 6, Namco 7])
  bool branchEasy; (does this difficulty have a branch?, this will need to align with the fumen files)
  bool branchNormal; (does this difficulty have a branch?, this will need to align with the fumen files)
  bool branchHard; (does this difficulty have a branch?, this will need to align with the fumen files)
  bool branchMania; (does this difficulty have a branch?, this will need to align with the fumen files)
  bool branchUra; (does this difficulty have a branch?, this will need to align with the fumen files)
  int starEasy; (star difficulty)
  int starNormal; (star difficulty)
  int starHard; (star difficulty)
  int starMania; (star difficulty)
  int starUra; (star difficulty, set to 0 for unused)
  int shinutiEasy; 
  int shinutiNormal;
  int shinutiHard;
  int shinutiMania;
  int shinutiUra;
  int shinutiEasyDuet;
  int shinutiNormalDuet;
  int shinutiHardDuet;
  int shinutiManiaDuet;
  int shinutiUraDuet;
  int scoreEasy; 
  int scoreNormal;
  int scoreHard;
  int scoreMania;
  int scoreUra;

  bool areFilesGZipped; (are the music and fumen files gzipped? this saves file space and is done automatically when converting TJAs)

  // Song Info
  int previewPos;
  int fumenOffsetPos;
  
  // Text Info
  TextEntry songName; (Song Title - e.g. A Cruel Angel's Thesis)
  
  TextEntry songSubtitle; (Origin of the song - e.g. From \" Neon Genesis EVANGELION \")
  
  TextEntry songDetail; (Extra details for the track, sometimes used to say it's Japanese name - e.g. 残酷な天使のテーゼ)
}

TextEntry {
  
    string text;
    int font; (0 == Japanese, 1 == English, 2 == Traditional Chinese, 3 == Simplified Chinese, 4 == Korean)

    // Langauge overrides
    string jpText; (langauge override for 日本語 text)
    int jpFont; (langauge override for 日本語 text)
    string enText; (langauge override for English text)
    int enFont; (langauge override for English text)
    string frText; (langauge override for Français text)
    int frFont; (langauge override for Français text)
    string itText; (langauge override for Italiano text)
    int itFont; (langauge override for Italiano text)
    string deText; (langauge override for Deutsch text)
    int deFont; (langauge override for Deutsch text)
    string esText; (langauge override for Español text)
    int esFont; (langauge override for Español text)
    string tcText; (langauge override for 繁體中文 text)
    int tcFont; (langauge override for 繁體中文 text)
    string scText; (langauge override for 简体中文 text)
    int scFont; (langauge override for 简体中文 text)
    string krText; (langauge override for 영어 text)
    int krFont; (langauge override for 영어 text)
}
```

---
## Contributers
(to add!)

---
## Credits 
- [SonicAudioTools](https://github.com/blueskythlikesclouds/SonicAudioTools)
- [VGAudio](https://github.com/Thealexbarney/VGAudio)
- Pulsar#5356 for the TJA2BIN.exe
