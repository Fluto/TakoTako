
using System;

namespace TakoTako
{
    // public interface ISongSearchInterop
    // {
    //     /// <summary>
    //     /// Filter the current song list on screen
    //     /// </summary>
    //     /// <param name="input">The input string</param>
    //     public void Filter(string input);
    //
    //     /// <summary>
    //     /// When the song search UI is enabled
    //     /// </summary>
    //     public void OnShowSongSearchUI();
    //
    //     /// <summary>
    //     /// When the song search UI is disabled
    //     /// </summary>
    //     public void OnHideSongSearchUI();
    //
    //     /// <summary>
    //     /// This is called to pass through a setter to determine the state of the song results
    //     /// </summary>
    //     public void SetResultsCallback(Action<SearchResults> resultsCallback);
    // }

    public struct SearchResults
    {
        public int ResultsCount;
    }
}
