#if TAIKO_IL2CPP
using Il2CppSystem;
using Il2CppInterop.Runtime.Injection;
using IntPtr = System.IntPtr;
#else
using System;
using Object = UnityEngine.Object;
#endif
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using StringComparison = System.StringComparison;

namespace TakoTako
{
    public class SongSearchInjection : MonoBehaviour
    {
#if TAIKO_IL2CPP
        // Used by IL2CPP when creating new instances of this class
        public SongSearchInjection(IntPtr ptr) : base(ptr) { }

        // Used by managed code when creating new instances of this class
        public SongSearchInjection() : base(ClassInjector.DerivedConstructorPointer<SongSearchInjection>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }
#endif

        private const string SongSelectSceneName = "SongSelect";

        // private ISongSearchInterop songSearchInterop;

        private object onSongSearchInstantiate;

        public void SetOnCreate(object songSearch, object canvasPrefab)
        {
            UnityEngine.Debug.Log("setting up");
            onSongSearchInstantiate = songSearch;
#if TAIKO_IL2CPP
            SceneManager.activeSceneChanged += new System.Action<Scene, Scene>((x, y) => SceneManagerOnSceneChanged(x, y, (GameObject)canvasPrefab));
#else
            SceneManager.activeSceneChanged += ((x,y) => SceneManagerOnSceneChanged(x,y, (GameObject)canvasPrefab));
#endif
            if (songSearchUI != null)
                ((Action<object>)(songSearch))?.Invoke(songSearchUI);
        }

        private SongSearchUI songSearchUI;

        private void SceneManagerOnSceneChanged(Scene oldScene, Scene newScene, GameObject canvasPrefab)
        {
            if (!newScene.name.Equals(SongSelectSceneName, StringComparison.InvariantCultureIgnoreCase))
                return;

            UnityEngine.Debug.Log("instantiating");
            UnityEngine.Debug.Log(canvasPrefab);
// #if TAIKO_IL2CPP
//             if (!newScene.name.Contains(SongSelectSceneName, StringComparison.InvariantCultureIgnoreCase))
//                 return;
// #else
//             if (!newScene.name.Equals(SongSelectSceneName, StringComparison.InvariantCultureIgnoreCase))
//                 return;
// #endif

            // this will spawn the Search UI object when the song select scene is active
            songSearchUI = Instantiate((GameObject)canvasPrefab, null).GetComponent<SongSearchUI>();
            ((Action<object>)(onSongSearchInstantiate))?.Invoke(songSearchUI);
            UnityEngine.Debug.Log("instantiated");
        }
    }
}
