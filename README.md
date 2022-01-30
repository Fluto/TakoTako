# Taiko no Tatsujin: The Drum Master - Mods

Hey this modifies version 1.1.0.0 of Taiko no Tatsujin: The Drum Master
Currently has the features:
- Fixes sign in screen
- Skips splash screen
- Disable fullscreen on application focus
- Allows custom tracks to be loaded into the game

----
## Installation

1. Download ![BepInEx](https://github.com/BepInEx/BepInEx/releases) `BepInEx_x64_XXXXX.zip`, as of writing the latest version is 5.4.18. This is a mod to patch Unity Games
2. Find your game's installation directiory, yours might be under `C:\Program Files\ModifiableWindowsApps\Taiko no Tatsujin\` 
3. Paste all of the files from the .zip from step 1 into this folder
(It will look something like this, my directory is different because I installed it to a different folder)\
![](https://github.com/Fluto/Taiko-no-Tatsujin-The-Drum-Master-Patch/blob/main/3.png)
4. Run Taiko no Tatusjin The Drum Master once, then close it
5. ![Download my patch](https://github.com/Fluto/TaikoMods/releases)
6. Look in your game's folder again, new files will have been generated under `.\BepInEx\plugins`
7. Paste the .DLL from step 4 into this folder\
![](https://github.com/Fluto/Taiko-no-Tatsujin-The-Drum-Master-Patch/blob/main/4.png)
8. And you're done!


## Configuration

After installing the mod, and running the game it will generate files in `.\BepInEx\config`. Open `com.fluto.taikomods.cfg` to configure this mod
Here you can enable each individual feature or redirect where custom songs will be loaded from


## Custom Songs

With this feature you can inject custom songs into the game!
To begin place custom songs in `SongDirectory` specified in your configuration file, by default this is `%userprofile%/Documents/TaikoTheDrumMasterMods/customSongs`
Each song must have it's own directory with a unique name. The folder must have this structure
```
CustomSongs
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
```
This format will be updated in the future to remove redundantancy 
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

  // Song Info
  int previewPos;
  int fumenOffsetPos;
  
  // Text Info
  TextEntry songName (Song Title - e.g. A Cruel Angel's Thesis)
  {
    string text;
    int font; (0 == Japanese, 1 == English, 2 == Traditional Chinese, 3 == Simplified Chinese, 4 == Korean)
  }
  
  TextEntry songSubtitle (Origin of the song - e.g. From \" Neon Genesis EVANGELION \")
  {
    string text;
    int font; (0 == Japanese, 1 == English, 2 == Traditional Chinese, 3 == Simplified Chinese, 4 == Korean)
  }
  
  TextEntry songDetail (Extra details for the track, sometimes used to say it's Japanese name - e.g. 残酷な天使のテーゼ)
  {
    string text;
    int font; (0 == Japanese, 1 == English, 2 == Traditional Chinese, 3 == Simplified Chinese, 4 == Korean)
  }
}
```
