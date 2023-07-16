using System.Linq;
using TMPro;
#if TAIKO_IL2CPP
using Il2CppInterop.Runtime;
using Il2CppSystem.Collections.Generic;
using Il2CppSystem;
using Il2CppInterop.Runtime.Injection;
#else
using System;
#endif
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// declare these as they are in managed spaced
using IntPtr = System.IntPtr;
using Object = UnityEngine.Object;

namespace TakoTako
{
    public class SongSearchUI : MonoBehaviour
    {
#if TAIKO_IL2CPP
        // Used by IL2CPP when creating new instances of this class
        public SongSearchUI(IntPtr ptr) : base(ptr) { }

        // Used by managed code when creating new instances of this class
        public SongSearchUI() : base(ClassInjector.DerivedConstructorPointer<SongSearchUI>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }
#endif

        // il2cpp is annoying these have to be retrieved manually
        private RectTransform mainTransform;

        private TMP_Text songSearchText;
        private TMP_Text resultsText;
        private TMP_Text resultsValueText;
        private TMP_InputField songSearchInput;

        private TMP_Text backButton;
        private Image backButtonImage;
        private Image escImage;

        private CanvasGroup canvasGroup;

        private const string TitleTextFont = "Swis721BdRnd_OutlinePlayerName";
        private const string SimpleTextFont = "DomBold_all plane";

        private Material titleTextMaterial;
        private TMP_FontAsset titleTextFont;

        private Material simpleTextMaterial;
        private TMP_FontAsset simpleTextFont;

        private bool uiIsActive = false;

        private GameObject guideGameObject;
        private bool isSetup = false;

        public void Setup()
        {
            if (isSetup)
                return;
#if TAIKO_IL2CPP
            mainTransform = FindComponentsInChildren<RectTransform>(gameObject).FirstOrDefault(x => x.gameObject.name == "MainTransform");
            songSearchText = FindComponentsInChildren<TMP_Text>(gameObject).FirstOrDefault(x => x.gameObject.name == "SongSearchText");
            resultsText = FindComponentsInChildren<TMP_Text>(gameObject).FirstOrDefault(x => x.gameObject.name == "ResultsText");
            resultsValueText = FindComponentsInChildren<TMP_Text>(gameObject).FirstOrDefault(x => x.gameObject.name == "ResultsValueText");
            songSearchInput = FindComponentsInChildren<TMP_InputField>(gameObject).FirstOrDefault(x => x.gameObject.name == "SongSearchInput");
            backButton = FindComponentsInChildren<TMP_Text>(gameObject).FirstOrDefault(x => x.gameObject.name == "BackButton");
            backButtonImage = FindComponentsInChildren<Image>(gameObject).FirstOrDefault(x => x.gameObject.name == "BackButtonImage");
            escImage = FindComponentsInChildren<Image>(gameObject).FirstOrDefault(x => x.gameObject.name == "EscImage");
            canvasGroup = FindComponent<CanvasGroup>(gameObject);
#else
            mainTransform = gameObject.GetComponentsInChildren<RectTransform>().FirstOrDefault(x => x.gameObject.name == "MainTransform");
            songSearchText = gameObject.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.gameObject.name == "SongSearchText");
            resultsText = gameObject.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.gameObject.name == "ResultsText");
            resultsValueText = gameObject.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.gameObject.name == "ResultsValueText");
            songSearchInput = gameObject.GetComponentsInChildren<TMP_InputField>().FirstOrDefault(x => x.gameObject.name == "SongSearchInput");
            backButton = gameObject.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.gameObject.name == "BackButton");
            backButtonImage = gameObject.GetComponentsInChildren<Image>().FirstOrDefault(x => x.gameObject.name == "BackButtonImage");
            escImage = gameObject.GetComponentsInChildren<Image>().FirstOrDefault(x => x.gameObject.name == "EscImage");
            canvasGroup = GetComponent<CanvasGroup>();
#endif

            isSetup = true;
            UnityEngine.Debug.Log($"Injected {nameof(SongSearchUI)}");
            ToggleCanvas(false);
            SetupComponents();
        }

        private void Update()
        {
            if (!isSetup)
                return;

            if (Input.GetKeyDown(KeyCode.F))
                ShowSongSearch();

            var stopSearch = Input.GetKeyDown(KeyCode.Escape);
            var acceptSearch = Input.GetKeyDown(KeyCode.Return);

            if (stopSearch)
                HideSongSearch(false);
            else if (acceptSearch)
                HideSongSearch(true);
        }

        private void ToggleCanvas(bool show)
        {
            canvasGroup.alpha = show ? 1 : 0;
            canvasGroup.interactable = show;
            canvasGroup.blocksRaycasts = show;
        }

        private void ShowSongSearch()
        {
            if (uiIsActive)
                return;

            uiIsActive = true;
            ToggleCanvas(true);
            guideGameObject.SetActive(false);
            OnShowSongSearchUI?.Invoke();

            ExecuteEvents.Execute(songSearchInput.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        }

        private void HideSongSearch(bool acceptResults)
        {
            if (!uiIsActive)
                return;

            if (!acceptResults)
                OnTextChange(string.Empty);

            uiIsActive = false;
            ToggleCanvas(false);
            guideGameObject.SetActive(true);
            OnHideSongSearchUI?.Invoke();
        }

        public void AfterSetup()
        {
            SetResultsCallback?.Invoke((System.Action<SearchResults>)OnResults);
        }


        /// <summary>
        /// Filter the current song list on screen
        /// </summary>
        /// <param name="input">The input string</param>
        public System.Action<string> Filter;

        /// <summary>
        /// When the song search UI is enabled
        /// </summary>
        public System.Action OnShowSongSearchUI;

        /// <summary>
        /// When the song search UI is disabled
        /// </summary>
        public System.Action OnHideSongSearchUI;

        /// <summary>
        /// This is called to pass through a setter to determine the state of the song results
        /// </summary>
        public System.Action<System.Action<SearchResults>> SetResultsCallback;

        // we'll need to find the font that's used across the game
        private void SetupComponents()
        {
            // this is really not optimal, but we gotta to what we gotta do

            #region Text

            var allTMPText = FindObjectsOfType<TMP_Text>().ToList();
            // find fonts' materials
            var result = allTMPText.FirstOrDefault(x =>
            {
                try
                {
                    return x != null && x.fontMaterial != null && x.font != null && x.fontMaterial.name.Contains(TitleTextFont);
                }
                catch
                {
                    return false;
                }
            });
            if (result != null)
            {
                titleTextMaterial = result.fontMaterial;
                titleTextFont = result.font;
            }

            result = allTMPText.FirstOrDefault(x =>
            {
                try
                {
                    return x != null && x.fontMaterial != null && x.font != null && x.fontMaterial.name.Contains(SimpleTextFont);
                }
                catch
                {
                    return false;
                }
            });
            if (result != null)
            {
                simpleTextMaterial = result.fontMaterial;
                simpleTextFont = result.font;
            }

            if (titleTextMaterial == null)
            {
                UnityEngine.Debug.LogError($"Could not find Font Material {TitleTextFont}");
            }
            else
            {
                songSearchText.fontSharedMaterial = titleTextMaterial;
                resultsText.fontSharedMaterial = titleTextMaterial;
                resultsValueText.fontSharedMaterial = titleTextMaterial;
                backButton.fontSharedMaterial = titleTextMaterial;

                songSearchText.font = titleTextFont;
                resultsText.font = titleTextFont;
                resultsValueText.font = titleTextFont;
                backButton.font = titleTextFont;
            }

            if (simpleTextMaterial == null)
            {
                UnityEngine.Debug.LogError($"Could not find Font Material {SimpleTextFont}");
            }
            else
            {
                songSearchInput.textComponent.fontSharedMaterial = simpleTextMaterial;
                songSearchInput.textComponent.font = simpleTextFont;

                var placeholder = songSearchInput.placeholder.GetComponent<TMP_Text>();
                placeholder.fontSharedMaterial = simpleTextMaterial;
                placeholder.font = simpleTextFont;
            }

#if TAIKO_IL2CPP
            songSearchInput.onValueChanged.AddListener(new System.Action<string>(OnTextChange));
#endif
            resultsValueText.text = 0.ToString();

            #endregion

            var images = FindObjectsOfType<Image>();

            var imageResult = images.FirstOrDefault(x => x != null && x.sprite != null && x.sprite.name.Contains("mark_cancel"));
            if (imageResult != null)
                backButtonImage.sprite = imageResult.sprite;
            imageResult = images.FirstOrDefault(x => x != null && x.sprite != null && x.sprite.name.Contains("g_key_Esc"));
            if (imageResult != null)
                escImage.sprite = imageResult.sprite;

            //find the guide canvas
            var canvases = FindObjectsOfType<CanvasScaler>();
            var inputCanvas = canvases.FirstOrDefault(x => x.name.Contains("input_guide_canvas"));
            if (inputCanvas == null)
            {
                UnityEngine.Debug.LogError("Cannot find input_guide_canvas");
                return;
            }

            guideGameObject = inputCanvas.transform.GetChild(0).GetChild(0).GetChild(0).gameObject;
        }

        private void OnTextChange(string text)
        {
            Filter?.Invoke(text);
        }

        private void OnResults(SearchResults searchResults)
        {
            resultsValueText.text = searchResults.ResultsCount.ToString("N0");
        }

#if TAIKO_IL2CPP

        public static T FindComponent<T>(GameObject gameObject) where T : Component
        {
            return gameObject.GetComponent(Il2CppType.Of<T>()).Cast<T>();
        }

        public static System.Collections.Generic.IEnumerable<T> FindComponentsInChildren<T>(GameObject gameObject) where T : Component
        {
            return gameObject.GetComponentsInChildren(Il2CppType.Of<T>(), true).Select(i => i.Cast<T>());
        }

        public new static System.Collections.Generic.List<T> FindObjectsOfType<T>() where T : Component
        {
            return Object.FindObjectsOfType(Il2CppType.Of<T>(), true).Select(i => i.Cast<T>()).ToList();
        }

        // public static T FindObjectOfType<T>() where T : Component
        // {
        //     return Object.FindObjectOfType(Il2CppType.Of<T>()).Cast<T>();
        // }
#endif
    }
}
