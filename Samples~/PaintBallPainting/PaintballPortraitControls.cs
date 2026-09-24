using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CoverUp.Splatter.Samples
{
    /// <summary>
    /// Play-mode panel for a <see cref="PaintballPortrait"/>. Put it on the same GameObject.
    /// Every setting is editable here, and Clear and paint drops the canvas and starts the
    /// volley again from what's in the panel. The list shows the portraits that ship with a build.
    /// Load image copies a file into the project in the editor, and reads a file from disk in a build.
    /// Press I to hide or show the panel. Press Enter to clear the canvas and paint again.
    /// Press P to pause or continue the volley. Escape quits. F11 toggles fullscreen.
    /// Enter paints even while a field is still selected. I and P do not fire while a field or a button has focus.
    /// </summary>
    public sealed class PaintballPortraitControls : MonoBehaviour
    {
        [Tooltip("The portrait these controls edit. Empty uses a PaintballPortrait on this GameObject.")]
        public PaintballPortrait portrait;

        InputField seedField, everyField, hitField, stepField, burstField, gainField, superField;
        Toggle originalToggle, sequentialToggle;
        Text pictureLabel, pauseLabel;
        RectTransform listContent;
        GameObject controlsRoot;
        GameObject tipRoot;
        Text tipLabel;
        int tipShowFrame = -1, tipHideFrame = -1;
        int fitFrame = -1, fitTries;
        static Font builtin;

        void Start()
        {
            if (!Screen.fullScreen) fitFrame = Time.frameCount + 1;
            if (portrait == null) portrait = GetComponent<PaintballPortrait>();
            if (portrait == null)
            {
                Debug.LogError("PaintballPortraitControls: no PaintballPortrait on this GameObject.");
                return;
            }
            EnsureEventSystem();
            Build();
        }

        void OnDestroy()
        {
            if (controlsRoot != null) Destroy(controlsRoot);
        }

        void Update()
        {
            if (fitFrame == Time.frameCount) FitWindow();
            if (PressedQuit()) QuitGame();
            if (PressedFullscreen()) ToggleFullscreen();
            if (controlsRoot == null) return;
            bool hidden = !controlsRoot.activeSelf;
            if (PressedHide() && (hidden || !UiHasFocus()))
            {
                controlsRoot.SetActive(hidden);
                HideTip();
            }
            if (PressedPaint() && (hidden || !UiHasFocus() || FieldSelected()))
                PaintAgain();
            if (PressedPause() && (hidden || !UiHasFocus()))
                TogglePause();
        }

        void ToggleFullscreen()
        {
            if (Screen.fullScreen) FitWindow();
            else
            {
                var display = Screen.mainWindowDisplayInfo;
                Screen.SetResolution(Mathf.Max(1, display.width), Mathf.Max(1, display.height), FullScreenMode.FullScreenWindow);
            }
        }

        void FitWindow()
        {
            var display = Screen.mainWindowDisplayInfo;
            if ((display.width < 2 || display.height < 2) && fitTries < 8)
            {
                fitTries++;
                fitFrame = Time.frameCount + 1;
                return;
            }
            fitTries = 0;
            Screen.SetResolution(Mathf.Max(1, display.width / 2), Mathf.Max(1, display.height / 2), FullScreenMode.Windowed);
        }

        static bool PressedHide()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.iKey.wasPressedThisFrame) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.I)) return true;
#endif
            return false;
        }

        static bool PressedPaint()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) return true;
#endif
            return false;
        }

        static bool PressedPause()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.pKey.wasPressedThisFrame) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.P)) return true;
#endif
            return false;
        }

        static bool PressedQuit()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) return true;
#endif
            return false;
        }

        static bool PressedFullscreen()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.f11Key.wasPressedThisFrame) return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F11)) return true;
#endif
            return false;
        }

        void LateUpdate()
        {
            if (tipRoot == null || !tipRoot.activeSelf) return;
            if (tipHideFrame == Time.frameCount && tipShowFrame != Time.frameCount)
                tipRoot.SetActive(false);
        }

        void ShowTip(string message, Vector2 screen)
        {
            tipShowFrame = Time.frameCount;
            tipLabel.text = message;
            tipRoot.SetActive(true);
            tipRoot.transform.SetAsLastSibling();
            Canvas.ForceUpdateCanvases();
            var rt = tipRoot.GetComponent<RectTransform>();
            float w = rt.rect.width, h = rt.rect.height;
            var pos = screen + new Vector2(16f, -12f);
            if (pos.x + w > Screen.width - 8f) pos.x = Mathf.Max(8f, screen.x - w - 16f);
            if (pos.y - h < 8f) pos.y = h + 8f;
            rt.position = pos;
        }

        void HideTip() => tipHideFrame = Time.frameCount;

        static bool FieldSelected()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponentInParent<InputField>() != null;
        }

        static bool UiHasFocus()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            return selected.GetComponentInParent<InputField>() != null
                || selected.GetComponentInParent<Button>() != null
                || selected.GetComponentInParent<Toggle>() != null;
        }

        /// <summary>
        /// A canvas built in play mode does not get an event system, so nothing on it takes a
        /// click. Projects on the Input System package also need that module's default actions;
        /// adding the component at runtime does not assign them.
        /// </summary>
        static void EnsureEventSystem()
        {
            var system = EventSystem.current;
            if (system == null)
            {
                var go = new GameObject("EventSystem");
                system = go.AddComponent<EventSystem>();
            }
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var legacy = system.GetComponent<StandaloneInputModule>();
            if (legacy != null) Object.Destroy(legacy);
            var module = system.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            if (module == null)
                module = system.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            if (module.actionsAsset == null) module.AssignDefaultActions();
#else
            if (system.GetComponent<StandaloneInputModule>() == null)
                system.gameObject.AddComponent<StandaloneInputModule>();
#endif
        }

        void PaintAgain()
        {
            ApplyFields();
            portrait.Restart();
            RefreshPauseLabel();
        }

        void TogglePause()
        {
            portrait.SetPaused(!portrait.Paused);
            RefreshPauseLabel();
        }

        void RefreshPauseLabel()
        {
            if (pauseLabel != null) pauseLabel.text = portrait.Paused ? "Resume" : "Pause";
        }

        void ApplyFields()
        {
            portrait.seed = ReadInt(seedField, portrait.seed);
            portrait.sampleEvery = Mathf.Max(1, ReadInt(everyField, portrait.sampleEvery));
            portrait.hit = Mathf.Clamp(ReadFloat(hitField, portrait.hit), 0.15f, 1f);
            portrait.paintStep = Mathf.Max(0f, ReadFloat(stepField, portrait.paintStep));
            portrait.shotsPerFrame = Mathf.Clamp(ReadInt(burstField, portrait.shotsPerFrame), 1, 32);
            portrait.gain = Mathf.Max(0f, ReadFloat(gainField, portrait.gain));
            portrait.supersample = Mathf.Clamp(ReadFloat(superField, portrait.supersample), 1f, 2f);
            portrait.showOriginal = originalToggle.isOn;
            portrait.sequential = sequentialToggle.isOn;
            seedField.text = portrait.seed.ToString();
            everyField.text = portrait.sampleEvery.ToString();
            hitField.text = portrait.hit.ToString("0.##");
            stepField.text = portrait.paintStep.ToString("0.##");
            burstField.text = portrait.shotsPerFrame.ToString();
            gainField.text = portrait.gain.ToString("0.##");
            superField.text = portrait.supersample.ToString("0.##");
        }

        void UsePicture(Texture2D tex)
        {
            if (tex == null) return;
            ApplyFields();
            portrait.picture = tex;
            pictureLabel.text = tex.name;
            portrait.Restart();
            RefreshPauseLabel();
            RefreshList();
        }

        void LoadExternal()
        {
#if UNITY_EDITOR
            UsePicture(PaintballPortraitLibrary.ImportExternal());
#else
            UsePicture(PaintballPortraitLibrary.LoadFromDisk());
#endif
        }

        void RefreshList()
        {
            if (listContent == null) return;
            for (int i = listContent.childCount - 1; i >= 0; i--)
                Destroy(listContent.GetChild(i).gameObject);
#if UNITY_EDITOR
            string current = portrait.picture != null ? UnityEditor.AssetDatabase.GetAssetPath(portrait.picture) : null;
            var entries = PaintballPortraitLibrary.List(portrait.picture);
            if (entries.Count == 0)
            {
                var empty = Text(listContent, "No pictures yet", 14, FontStyle.Italic);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 22;
                return;
            }
            foreach (var entry in entries)
            {
                string path = entry.Path;
                bool on = path == current;
                Choice(listContent, entry.Label, on, () =>
                {
                    var tex = PaintballPortraitLibrary.Prepare(path);
                    UsePicture(tex);
                });
            }
#else
            var shown = new List<Texture2D>();
            AddPicture(shown, portrait.picture);
            foreach (var tex in PaintballPortraitLibrary.Bundled()) AddPicture(shown, tex);
            foreach (var tex in PaintballPortraitLibrary.Loaded) AddPicture(shown, tex);
            shown.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
            if (shown.Count == 0)
            {
                var empty = Text(listContent, "No pictures yet", 14, FontStyle.Italic);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 22;
                return;
            }
            foreach (var tex in shown)
            {
                var pick = tex;
                Choice(listContent, pick.name, pick == portrait.picture, () => UsePicture(pick));
            }
#endif
        }

        static void AddPicture(List<Texture2D> shown, Texture2D tex)
        {
            if (tex == null) return;
            for (int i = 0; i < shown.Count; i++)
                if (shown[i] == tex) return;
            shown.Add(tex);
        }

        void Build()
        {
            controlsRoot = new GameObject("PaintballControls", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            controlsRoot.transform.SetParent(null, false);
            var canvas = controlsRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var panel = panelGo.GetComponent<RectTransform>();
            panel.SetParent(controlsRoot.transform, false);
            panel.anchorMin = panel.anchorMax = new Vector2(0f, 1f);
            panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = new Vector2(16f, -16f);
            panelGo.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
            var layout = panelGo.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.spacing = 6;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = panelGo.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            Title(panel, "Paintball");
            pictureLabel = Note(panel, portrait.picture != null ? portrait.picture.name : "Stand-in",
                "The picture the guns are copying. Choose one in the list below, or load a file. With nothing assigned, a stand-in face is used. If Read/Write is off, the stand-in is used instead.");
            seedField = Field(panel, "Seed", portrait.seed.ToString(), InputField.ContentType.IntegerNumber,
                "0 picks a new paintball shape each run. Any other number repeats the same shapes. The colour always comes from the pixel, not from the seed.");
            everyField = Field(panel, "Every", portrait.sampleEvery.ToString(), InputField.ContentType.IntegerNumber,
                "Take one pixel every this many, across and down. 1 fires at every pixel. A large picture with a small step is a long volley, so raise it.");
            hitField = Field(panel, "Hit", portrait.hit.ToString("0.##"), InputField.ContentType.DecimalNumber,
                "Core radius as a fraction of the gap between samples. Near 0.45 the hits meet. Lower leaves more bare canvas. Higher piles paint on top of paint.");
            stepField = Field(panel, "Step", portrait.paintStep.ToString("0.##"), InputField.ContentType.DecimalNumber,
                "Seconds to wait between paintballs. 0 does not wait. Per frame is still the cap on how many can land in one frame.");
            burstField = Field(panel, "Per frame", portrait.shotsPerFrame.ToString(), InputField.ContentType.IntegerNumber,
                "How many paintballs may land in one frame, from 1 to 32. Lower it if the frame rate drops.");
            gainField = Field(panel, "Gain", portrait.gain.ToString("0.##"), InputField.ContentType.DecimalNumber,
                "Multiplies the paint colour. 1 keeps the pixel's colour. Above 1 the paint can pass white.");
            superField = Field(panel, "Supersample", portrait.supersample.ToString("0.##"), InputField.ContentType.DecimalNumber,
                "Canvas pixels per screen pixel, from 1 to 2. Above 1 the edges stay sharper when the view shows the picture smaller.");
            originalToggle = Tick(panel, "Original", portrait.showOriginal,
                "Show the source beside the canvas. Off, the canvas fills the screen, still at the picture's own ratio.");
            sequentialToggle = Tick(panel, "Sequential", portrait.sequential,
                "On, the guns fire left to right, one row at a time from the bottom. Off, the same shots land in a random order. A fixed Seed repeats that order.");
            Title(panel, "Portraits");
            listContent = Scroll(panel, 160,
                "Pictures that ship with the game, from Assets/Resources/PaintballPortraits, plus any file loaded from disk this session. In the editor the list also includes other png or jpeg files at least 256 pixels on the long side. Click one to paint with it.");
            Button(panel, "Load image", LoadExternal,
                "Open a png or jpeg from disk. In the editor, a file already in the project is used in place, and any other file is copied into Assets/Resources/PaintballPortraits. Read/Write is turned on, Non-Power of Two is set to None, and Max Size is raised so the ratio stays the file's ratio. In a build, the file is read in and added to this list.");
            pauseLabel = Button(panel, "Pause", TogglePause,
                "Hold the volley on the next paintball. Resume continues from there. P does the same. Clear and paint starts a new volley, and that one runs.");
            Button(panel, "Clear and paint", PaintAgain,
                "Drop the canvas and shoot the volley again with the settings in this panel. Enter does the same.");
            Button(panel, "Export", Export,
                "Save the canvas, as it looks right now, to a PNG. A volley that is still landing is saved as far as it has got. This opens a window so you can choose the file.");
            Button(panel, "Quit", QuitGame,
                "Close the game. Escape does the same. In the editor this stops play mode.");
            var hint = Text(panel, "I hides the panel. P pauses. Enter paints again. Esc quits. F11 is fullscreen.", 12, FontStyle.Italic);
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.horizontalOverflow = HorizontalWrapMode.Wrap;
            hint.verticalOverflow = VerticalWrapMode.Overflow;
            var hintLe = hint.gameObject.AddComponent<LayoutElement>();
            hintLe.preferredWidth = 280;
            hintLe.minHeight = 16;
            hintLe.preferredHeight = -1;
            BuildTip();
            RefreshList();
        }

        void BuildTip()
        {
            tipRoot = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            tipRoot.transform.SetParent(controlsRoot.transform, false);
            var rt = tipRoot.GetComponent<RectTransform>();
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(280f, 32f);
            tipRoot.GetComponent<Image>().color = new Color(0.14f, 0.11f, 0.09f, 0.96f);
            tipRoot.GetComponent<Image>().raycastTarget = false;
            var layout = tipRoot.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 8, 8);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = tipRoot.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            tipLabel = Text(tipRoot.transform, "", 13, FontStyle.Normal);
            tipLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            tipLabel.verticalOverflow = VerticalWrapMode.Overflow;
            tipLabel.gameObject.AddComponent<LayoutElement>().preferredWidth = 260;
            tipRoot.SetActive(false);
        }

        void Hover(GameObject go, string message)
        {
            var tip = go.AddComponent<HoverTip>();
            tip.Owner = this;
            tip.Message = message;
        }

        void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void Export()
        {
            string name = portrait.picture != null ? portrait.picture.name : "paintball";
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.SaveFilePanel("Export painting", "", name, "png");
            if (string.IsNullOrEmpty(path)) return;
#else
            string path = PaintballPortraitLibrary.SavePngPath(name);
            if (string.IsNullOrEmpty(path)) return;
#endif
            if (portrait.ExportPng(path)) Debug.Log("[OpenSplatter] Exported " + path);
            else Debug.LogError("PaintballPortrait: nothing on the canvas to export.");
        }

        void Title(RectTransform panel, string text)
        {
            var label = Text(panel, text, 16, FontStyle.Bold);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 22;
        }

        Text Note(RectTransform panel, string text, string tip)
        {
            var row = Row(panel, tip);
            var rowLe = row.GetComponent<LayoutElement>();
            rowLe.minHeight = 26;
            rowLe.preferredHeight = -1;
            Label(row, "Picture");
            var value = Text(row, text, 14, FontStyle.Normal);
            value.horizontalOverflow = HorizontalWrapMode.Wrap;
            value.verticalOverflow = VerticalWrapMode.Overflow;
            var le = value.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 140;
            le.flexibleWidth = 1;
            return value;
        }

        RectTransform Scroll(RectTransform panel, float height, string tip)
        {
            var go = new GameObject("Scroll", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            go.transform.SetParent(panel, false);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.06f);
            Hover(go, tip);
            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = height;
            le.preferredWidth = 280;

            var viewGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            var view = viewGo.GetComponent<RectTransform>();
            view.SetParent(go.transform, false);
            view.anchorMin = Vector2.zero;
            view.anchorMax = Vector2.one;
            view.offsetMin = new Vector2(4f, 4f);
            view.offsetMax = new Vector2(-4f, -4f);

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var content = contentGo.GetComponent<RectTransform>();
            content.SetParent(view, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 4;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = contentGo.GetComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = go.GetComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24;
            return content;
        }

        void Choice(RectTransform parent, string name, bool on, UnityEngine.Events.UnityAction click)
        {
            var color = on ? new Color(0.85f, 0.4f, 0.26f, 1f) : new Color(1f, 1f, 1f, 0.12f);
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            go.GetComponent<LayoutElement>().preferredHeight = 24;
            var label = Text(go.transform, name, 14, FontStyle.Normal);
            label.alignment = TextAnchor.MiddleLeft;
            var tr = label.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(8f, 0f);
            tr.offsetMax = new Vector2(-8f, 0f);
            go.GetComponent<Button>().onClick.AddListener(click);
            Hover(go, "Paint with this picture. If its importer would squash the ratio or refuse to be read, those settings are corrected first.");
        }

        InputField Field(RectTransform panel, string name, string value, InputField.ContentType type, string tip)
        {
            var row = Row(panel, tip);
            Label(row, name);
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(row, false);
            go.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 140;
            le.preferredHeight = 24;
            le.flexibleWidth = 1;

            var text = Text(go.transform, value, 14, FontStyle.Normal);
            var tr = text.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(6f, 2f);
            tr.offsetMax = new Vector2(-6f, -2f);

            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.text = value;
            field.contentType = type;
            field.lineType = InputField.LineType.SingleLine;
            Hover(go, tip);
            return field;
        }

        Toggle Tick(RectTransform panel, string name, bool on, string tip)
        {
            var row = Row(panel, tip);
            Label(row, name);
            var go = new GameObject(name, typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var le = go.GetComponent<LayoutElement>();
            le.preferredWidth = 22;
            le.preferredHeight = 22;

            var box = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var boxRt = box.GetComponent<RectTransform>();
            boxRt.SetParent(go.transform, false);
            boxRt.anchorMin = boxRt.anchorMax = new Vector2(0f, 0.5f);
            boxRt.pivot = new Vector2(0f, 0.5f);
            boxRt.sizeDelta = new Vector2(18f, 18f);
            box.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.2f);
            Hover(box, tip);

            var mark = new GameObject("Mark", typeof(RectTransform), typeof(Image));
            var markRt = mark.GetComponent<RectTransform>();
            markRt.SetParent(boxRt, false);
            markRt.anchorMin = new Vector2(0.18f, 0.18f);
            markRt.anchorMax = new Vector2(0.82f, 0.82f);
            markRt.offsetMin = markRt.offsetMax = Vector2.zero;
            mark.GetComponent<Image>().color = new Color(0.93f, 0.5f, 0.32f, 1f);
            Hover(mark, tip);

            var toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = box.GetComponent<Image>();
            toggle.graphic = mark.GetComponent<Image>();
            toggle.isOn = on;
            return toggle;
        }

        Text Button(RectTransform panel, string name, UnityEngine.Events.UnityAction click, string tip)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(panel, false);
            go.GetComponent<Image>().color = new Color(0.85f, 0.4f, 0.26f, 1f);
            go.GetComponent<LayoutElement>().preferredHeight = 30;
            var label = Text(go.transform, name, 15, FontStyle.Bold);
            label.alignment = TextAnchor.MiddleCenter;
            var tr = label.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
            go.GetComponent<Button>().onClick.AddListener(click);
            Hover(go, tip);
            return label;
        }

        RectTransform Row(RectTransform panel, string tip)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(panel, false);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0f);
            var h = go.GetComponent<HorizontalLayoutGroup>();
            h.spacing = 8;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = 26;
            le.preferredWidth = 280;
            Hover(go, tip);
            return rt;
        }

        void Label(RectTransform row, string text)
        {
            var label = Text(row, text, 14, FontStyle.Normal);
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = 108;
            le.minWidth = 108;
        }

        Text Text(Transform parent, string value, int size, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Builtin();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        static Font Builtin()
        {
            if (builtin == null) builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (builtin == null) builtin = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return builtin;
        }

        static int ReadInt(InputField field, int fallback) => int.TryParse(field.text, out int n) ? n : fallback;

        static float ReadFloat(InputField field, float fallback) => float.TryParse(field.text, out float n) ? n : fallback;

        sealed class HoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public PaintballPortraitControls Owner;
            public string Message;
            public void OnPointerEnter(PointerEventData eventData) => Owner.ShowTip(Message, eventData.position);
            public void OnPointerExit(PointerEventData eventData) => Owner.HideTip();
        }
    }
}
