using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace CoverUp.Splatter.Samples
{
    /// <summary>
    /// A full-screen living backdrop. Drop it on any GameObject: it builds a screen-space canvas
    /// with a RawImage, generates a composition on a worker thread, paints it one splat at a
    /// time, then keeps it alive: every <see cref="pushEvery"/> seconds one more splat lands, and
    /// half that later the oldest dissolves through a crossfade between two canvases. Click
    /// anywhere to punch a hole in the paint.
    /// </summary>
    public sealed class SplatterBackdrop : MonoBehaviour
    {
        public enum Preset { Rainbow, Cool, Muted }

        [Tooltip("0 picks a random seed each run.")]
        public int seed;
        public Preset palette = Preset.Rainbow;
        [Tooltip("Canvas pixels per screen pixel; above 1 the edges stay crisp when the image is scaled down.")]
        [Range(1f, 2f)] public float supersample = 1.25f;
        [Tooltip("Seconds between the splats of the first paint. 0 paints everything at once.")]
        public float paintStep = 0.04f;
        [Tooltip("Seconds between arrivals once the painting is up. 0 keeps it still.")]
        public float pushEvery = 3.5f;
        [Tooltip("Seconds the dissolve of the oldest splat takes.")]
        public float fadeTime = 1f;
        [Tooltip("The paint's colour multiplier; above 1 it exceeds white, for a bloom to pick up.")]
        public float gain = 1f;
        [Tooltip("The outer glow's strength in the black gaps; 0 is off.")]
        [Range(0f, 2f)] public float glow = 0f;

        private RawImage shownImage, fadeImage;
        private SplatCanvas shown, hidden;
        private SplatComposition live;
        private SplatRng pushRng;
        private Task<SplatComposition> generating;
        private Task<SplatComposition.PushGroup> pushing;
        private float paintDue, nextPush, nextPop, fadeStart;
        private bool fading;
        private int width, height;

        private void Start()
        {
            var canvasGo = new GameObject("SplatterCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            shownImage = NewLayer(canvasGo.transform, "Shown");
            fadeImage = NewLayer(canvasGo.transform, "Fade");
            fadeImage.color = new Color(1f, 1f, 1f, 0f);
            fadeImage.enabled = false;

            width = Mathf.RoundToInt(Screen.width * supersample);
            height = Mathf.RoundToInt(Screen.height * supersample);
            shown = NewCanvas(); hidden = NewCanvas();
            shownImage.texture = shown.Texture;
            fadeImage.texture = hidden.Texture;

            int s = seed != 0 ? seed : Random.Range(1, int.MaxValue);
            SplatPalette p = palette == Preset.Cool ? SplatPalette.Cool : palette == Preset.Muted ? SplatPalette.Muted : SplatPalette.Rainbow;
            int w = width, h = height;
            generating = Task.Run(() => SplatComposition.Generate(s, p, w, h));
        }

        private SplatCanvas NewCanvas() => new SplatCanvas(width, height) { Gain = gain, Glow = glow };

        private static RawImage NewLayer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            var r = go.GetComponent<RectTransform>();
            r.SetParent(parent, false);
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            var img = go.GetComponent<RawImage>();
            img.raycastTarget = false;
            return img;
        }

        private void Update()
        {
            float t = Time.time;

            if (generating != null && generating.IsCompleted)
            {
                live = generating.Result; generating = null;
                pushRng = new SplatRng(unchecked(live.Seed * 7919 + 17));
                shown.Begin(live);
                if (paintStep <= 0f) shown.PaintAll();
                paintDue = t;
                nextPush = t + pushEvery; nextPop = t + pushEvery * 1.5f;
            }
            if (live == null) return;

            // the first paint, one splat per step
            if (!shown.Done && !fading && paintStep > 0f)
            {
                while (!shown.Done && t >= paintDue) { shown.PaintNext(); paintDue += paintStep; }
            }
            else if (!shown.Done) shown.PaintNext();

            if (pushEvery > 0f && shown.Done)
            {
                // an arrival: prepared on the worker, landed when ready
                if (pushing == null && t >= nextPush)
                {
                    var comp = live; var rng = pushRng;
                    pushing = Task.Run(() => comp.PreparePush(rng));
                    nextPush = t + pushEvery;
                }
                if (pushing != null && pushing.IsCompleted)
                {
                    live.ApplyPush(pushing.Result); pushing = null;
                    shown.PaintNext();
                    if (fading) while (!hidden.Done) hidden.PaintNext();
                }
                // a departure: the oldest goes, the rest is rebuilt unseen and crossfaded in
                if (!fading && pushing == null && t >= nextPop)
                {
                    nextPop = t + pushEvery;
                    if (live.PopOldest() > 0)
                    {
                        shown.MarkAllPainted();
                        hidden.Begin(live);
                        hidden.PaintAll();
                        hidden.Present();
                        fadeImage.color = new Color(1f, 1f, 1f, 0f);
                        fadeImage.enabled = true;
                        fading = true; fadeStart = t;
                    }
                }
            }

            if (fading)
            {
                float k = (t - fadeStart) / Mathf.Max(0.01f, fadeTime);
                if (k >= 1f)
                {
                    (shown, hidden) = (hidden, shown);
                    shownImage.texture = shown.Texture;
                    fadeImage.texture = hidden.Texture;
                    hidden.Begin(null);
                    fadeImage.enabled = false;
                    fading = false;
                }
                else fadeImage.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, k));
            }

            // a click erases a small splat's worth of paint
            if (Input.GetMouseButtonDown(0) && shown.Done)
            {
                Vector2 m = Input.mousePosition;
                float px = m.x / Screen.width * width, py = m.y / Screen.height * height;
                var hole = SplatRules.Generate(Random.Range(1, int.MaxValue), Random.Range(9f, 16f), width / (float)Screen.width, 0f, vMul: 0f);
                live.AddSplat(hole, px, py, hole: true);
                shown.PaintNext();
            }
        }

        private void LateUpdate()
        {
            if (shown != null && shown.Dirty) shown.Present();
        }

        private void OnDestroy()
        {
            shown?.Dispose(); hidden?.Dispose();
        }
    }
}
