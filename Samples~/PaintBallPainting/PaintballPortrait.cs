using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace CoverUp.Splatter.Samples
{
    /// <summary>
    /// The paintball portrait. Drop it on any GameObject and press play. It reads a picture into
    /// one flat array, then takes every <see cref="sampleEvery"/>th pixel across and down for a
    /// position and a colour, and fires a paintball splat there, the way an array of guns would
    /// throw paint at a canvas to rebuild the picture. Assign any readable texture; with none
    /// set, a small stand-in portrait is used so the run still shows a face. With
    /// <see cref="showOriginal"/> the source sits on the left and the canvas on the right;
    /// otherwise the canvas fills the screen. The hits land one burst at a time, in sample order
    /// or shuffled when <see cref="sequential"/> is off. <see cref="Paused"/> holds the volley
    /// where it is. <see cref="Restart"/> clears the canvas and paints again.
    /// <see cref="PaintballPortraitControls"/> is a panel for these settings.
    /// </summary>
    public sealed class PaintballPortrait : MonoBehaviour
    {
        [Tooltip("The picture to rebuild. Read/Write must be on. Empty uses the stand-in portrait.")]
        public Texture2D picture;
        [Tooltip("0 picks a random seed each run. The seed picks each paintball's shape; the colour comes from the pixel.")]
        public int seed;
        [Tooltip("Take one pixel every this many, across and down the picture. 1 fires every pixel.")]
        [Min(1)] public int sampleEvery = 8;
        [Tooltip("Core radius as a fraction of the gap between samples. Near 0.45 the hits meet.")]
        [Range(0.15f, 1f)] public float hit = 0.45f;
        [Tooltip("Seconds between paintballs. 0 fires a burst every frame.")]
        public float paintStep;
        [Tooltip("How many paintballs may land in one frame.")]
        [Range(1, 32)] public int shotsPerFrame = 4;
        [Tooltip("The paint's colour multiplier.")]
        public float gain = 1f;
        [Tooltip("Canvas pixels per screen pixel.")]
        [Range(1f, 2f)] public float supersample = 1.25f;
        [Tooltip("Show the source picture beside the canvas. Off, the canvas uses the whole screen.")]
        public bool showOriginal = true;
        [Tooltip("Fire in sample order, across each row from the bottom. Off, the same shots land in a random order.")]
        public bool sequential = true;

        struct Shot
        {
            public float X, Y, Hue, S, V;
            public int Seed;
        }

        RawImage hitsImage;
        SplatCanvas canvas;
        SplatRuleOverrides paintball;
        Shot[] shots;
        Texture2D standIn;
        GameObject overlay;
        int cursor;
        float paintDue;
        float rc;

        /// <summary>True while the volley is held. Shots already on the canvas stay.</summary>
        public bool Paused { get; private set; }

        /// <summary>Hold or continue the volley. Continuing starts the wait from now, so a long
        /// pause does not dump every shot that would have landed in that time.</summary>
        public void SetPaused(bool value)
        {
            if (Paused == value) return;
            Paused = value;
            if (!value) paintDue = Time.time;
        }

        void Start() => Begin();

        /// <summary>Drop what's on the canvas and paint again from the current settings.</summary>
        public void Restart()
        {
            Paused = false;
            shots = null;
            cursor = 0;
            canvas?.Dispose();
            canvas = null;
            if (overlay != null)
            {
                overlay.SetActive(false);
                Destroy(overlay);
                overlay = null;
            }
            Begin();
        }

        void Begin()
        {
            if (standIn != null) { Destroy(standIn); standIn = null; }
            Color32[] pixels = ReadPicture(out int srcW, out int srcH, out Texture display);
            int runSeed = seed != 0 ? seed : Random.Range(1, int.MaxValue);
            Layout(display, srcW, srcH, out int canvasW, out int canvasH);

            canvas = new SplatCanvas(canvasW, canvasH) { Gain = gain, SprayOnPaint = 0.55f, ChainOnPaint = 0.65f };
            hitsImage.texture = canvas.Texture;
            Prime(canvasW, canvasH);

            int stride = Mathf.Max(1, sampleEvery);
            float spacing = stride * Mathf.Max(canvasW / (float)srcW, canvasH / (float)srcH);
            rc = Mathf.Max(3f, spacing * hit);
            paintball = PaintballRules();
            shots = Sample(pixels, srcW, srcH, canvasW, canvasH, stride, runSeed);
            if (!sequential) Shuffle(shots, runSeed);
            paintDue = Time.time;
            Debug.Log($"[OpenSplatter] {shots.Length} paintballs from {srcW}x{srcH}, every {stride} pixel, core {rc:0.#} px.");
        }

        void Update()
        {
            if (canvas == null || shots == null || cursor >= shots.Length || Paused) return;
            int fired = 0;
            float t = Time.time;
            while (cursor < shots.Length && fired < shotsPerFrame && (paintStep <= 0f || t >= paintDue))
            {
                var s = shots[cursor++];
                var recipe = SplatRules.Generate(s.Seed, rc, 1f, s.Hue, sMul: s.S, vMul: s.V, shadeMul: 0.35f, overrides: paintball);
                canvas.Paint(recipe, s.X, s.Y);
                fired++;
                if (paintStep > 0f) paintDue += paintStep;
            }
        }

        void OnDestroy()
        {
            canvas?.Dispose();
            if (standIn != null) Destroy(standIn);
            if (overlay != null) Destroy(overlay);
        }

        /// <summary>Write the canvas as a PNG. The paint is stored linear, so the file is encoded
        /// in sRGB to match the screen. Returns false when there is nothing to save.</summary>
        public bool ExportPng(string path)
        {
            if (string.IsNullOrEmpty(path) || canvas == null || canvas.Texture == null) return false;
            var rt = canvas.Texture;
            var prev = RenderTexture.active;
            var copy = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(rt, copy);
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            RenderTexture.active = copy;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(copy);

            var pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                pixels[i] = new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g), Mathf.LinearToGammaSpace(c.b), c.a);
            }
            tex.SetPixels(pixels);
            try { File.WriteAllBytes(path, tex.EncodeToPNG()); }
            catch (IOException e)
            {
                Debug.LogError("PaintballPortrait: could not write " + path + ". " + e.Message);
                Destroy(tex);
                return false;
            }
            Destroy(tex);
            return true;
        }

        /// <summary>The picture as one row-major strip, index = x + y * width, y up, which is the order
        /// <see cref="Texture2D.GetPixels32"/> already uses.</summary>
        Color32[] ReadPicture(out int width, out int height, out Texture display)
        {
            if (picture != null && picture.isReadable)
            {
                width = picture.width; height = picture.height; display = picture;
                return picture.GetPixels32();
            }
            if (picture != null)
                Debug.LogError("PaintballPortrait: enable Read/Write on the picture's import settings. Using the stand-in.");
            standIn = StandIn(out Color32[] pixels);
            width = standIn.width; height = standIn.height; display = standIn;
            return pixels;
        }

        /// <summary>
        /// Every Nth pixel across and down. A stride of N along the strip would only thin the
        /// columns and leave every row, so the guns would stack in stripes. The index back into
        /// the flat array is <c>x + y * width</c>.
        /// </summary>
        static Shot[] Sample(Color32[] pixels, int srcW, int srcH, int canvasW, int canvasH, int stride, int runSeed)
        {
            float sx = canvasW / (float)srcW, sy = canvasH / (float)srcH;
            var list = new List<Shot>((srcW / stride + 1) * (srcH / stride + 1));
            for (int y = 0; y < srcH; y += stride)
            {
                int row = y * srcW;
                for (int x = 0; x < srcW; x += stride)
                {
                    int i = row + x;
                    Color32 sample = pixels[i];
                    if (sample.a < 16) continue;
                    Color.RGBToHSV((Color)sample, out float h, out float s, out float v);
                    list.Add(new Shot
                    {
                        X = (x + 0.5f) * sx,
                        Y = (y + 0.5f) * sy,
                        Hue = h * 360f,
                        S = s,
                        V = v,
                        Seed = unchecked(runSeed * 7919 + i * 13 + 1),
                    });
                }
            }
            return list.ToArray();
        }

        /// <summary>Fisher-Yates, seeded, so a fixed <see cref="seed"/> repeats the same volley.</summary>
        static void Shuffle(Shot[] shots, int runSeed)
        {
            var rng = new System.Random(runSeed);
            for (int i = shots.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shots[i], shots[j]) = (shots[j], shots[i]);
            }
        }

        /// <summary>A wet hit: a solid core, a short lace, a few drops and a little mist. The measured
        /// splat is a whole painting's worth of arms and spray, which would bury the picture.</summary>
        static SplatRuleOverrides PaintballRules() => new SplatRuleOverrides()
            .Set(SplatRules.ChainsN, 1f, 2f)
            .Set(SplatRules.ChainReachMed, 1.05f, 1.45f)
            .Set(SplatRules.DropsN, 6f, 12f)
            .Set(SplatRules.DropDistMed, 1.15f, 1.4f)
            .Set(SplatRules.DropDistMax, 1.7f, 2.1f)
            .Set(SplatRules.SprayN, 10f, 22f)
            .Set(SplatRules.SprayPeak, 1.15f, 1.4f)
            .Set(SplatRules.SpraySpread, 0.35f, 0.55f)
            .Set(SplatRules.StubsN, 5f, 9f)
            .Set(SplatRules.WebFrom, 0.86f, 0.92f)
            .Set(SplatRules.WebReach, 1.15f, 1.4f)
            .Set(SplatRules.WebSpaceIn, 0.42f, 0.52f)
            .Set(SplatRules.WebSpaceOut, 0.52f, 0.62f)
            .Set(SplatRules.ColVTop, 1f)
            .Set(SplatRules.ColSTop, 1f)
            .Set(SplatRules.ColSpread, 0.25f)
            .Set(SplatRules.ColTint, 0.2f)
            .Set(SplatRules.ColHueSpread, 0.15f);

        /// <summary>Linen, so the gaps between hits read as canvas and the dark paint has something to sit on.</summary>
        void Prime(int width, int height)
        {
            var ground = new SplatRules.Part(0.02f, 0f);
            float radius = 0.5f * Mathf.Sqrt(width * width + height * height) + 32f;
            ground.Disc(new SplatRng(1), 0f, 0f, radius, radius, 0f, 1f);
            canvas.PaintPieces(ground, width * 0.5f, height * 0.5f, 40f, 1f, 0.05f, 0.94f);
        }

        void Layout(Texture display, int srcW, int srcH, out int canvasW, out int canvasH)
        {
            overlay = new GameObject("PaintballCanvas", typeof(Canvas), typeof(CanvasScaler));
            var root = overlay;
            root.transform.SetParent(null, false);
            var paintCanvas = root.GetComponent<Canvas>();
            paintCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            paintCanvas.sortingOrder = 0;

            // One scale for both axes, so the canvas is the picture's own ratio.
            // Rounding width and height separately can drift that ratio by a pixel.
            float panelW = Screen.width * (showOriginal ? 0.5f : 1f), panelH = Screen.height;
            float scale = Mathf.Min(panelW / srcW, panelH / srcH) * supersample;
            canvasW = Mathf.Max(8, Mathf.RoundToInt(srcW * scale));
            canvasH = Mathf.Max(8, Mathf.RoundToInt(srcH * scale));

            float aspect = srcW / (float)srcH;
            if (showOriginal)
            {
                var source = NewFitted(root.transform, "Source", 0f, 0.5f, aspect);
                source.texture = display;
                hitsImage = NewFitted(root.transform, "Hits", 0.5f, 1f, aspect);
            }
            else hitsImage = NewFitted(root.transform, "Hits", 0f, 1f, aspect);
        }

        /// <summary>
        /// A half-screen slot, and inside it a RawImage fitted to <paramref name="aspect"/>.
        /// The fitter has to be the child: on the slot itself it would lock both pictures
        /// to the middle of the whole screen and stretch them to that one rect.
        /// </summary>
        static RawImage NewFitted(Transform parent, string name, float x0, float x1, float aspect)
        {
            var slotGo = new GameObject(name + "Slot", typeof(RectTransform));
            var slot = slotGo.GetComponent<RectTransform>();
            slot.SetParent(parent, false);
            slot.anchorMin = new Vector2(x0, 0f); slot.anchorMax = new Vector2(x1, 1f);
            slot.offsetMin = Vector2.zero; slot.offsetMax = Vector2.zero;

            var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            var r = go.GetComponent<RectTransform>();
            r.SetParent(slot, false);
            var img = go.GetComponent<RawImage>();
            img.raycastTarget = false;
            var fitter = go.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = aspect;
            return img;
        }

        /// <summary>A face in flat colour, so a run with no texture still has a picture to aim at.</summary>
        Texture2D StandIn(out Color32[] pixels)
        {
            const int w = 192, h = 256;
            pixels = new Color32[w * h];
            var sky = new Color32(168, 190, 204, 255);
            var land = new Color32(86, 104, 72, 255);
            var hair = new Color32(58, 40, 30, 255);
            var skin = new Color32(214, 176, 142, 255);
            var dress = new Color32(42, 58, 62, 255);
            var dark = new Color32(36, 28, 24, 255);
            Fill(pixels, w, h, 0, 0, w, 100, land);
            Fill(pixels, w, h, 0, 100, w, h, sky);
            Disc(pixels, w, h, 96, 58, 70, 44, dress);
            Disc(pixels, w, h, 96, 142, 52, 68, hair);
            Disc(pixels, w, h, 96, 136, 34, 46, skin);
            Disc(pixels, w, h, 82, 148, 7, 8, dark);
            Disc(pixels, w, h, 110, 148, 7, 8, dark);
            Fill(pixels, w, h, 86, 112, 108, 118, new Color32(176, 110, 104, 255));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "PaintballStandIn", filterMode = FilterMode.Point };
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        static void Fill(Color32[] pixels, int w, int h, int x0, int y0, int x1, int y1, Color32 c)
        {
            x0 = Mathf.Clamp(x0, 0, w); x1 = Mathf.Clamp(x1, 0, w);
            y0 = Mathf.Clamp(y0, 0, h); y1 = Mathf.Clamp(y1, 0, h);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    pixels[x + y * w] = c;
        }

        static void Disc(Color32[] pixels, int w, int h, int cx, int cy, int rx, int ry, Color32 c)
        {
            float rx2 = rx * rx, ry2 = ry * ry;
            for (int y = cy - ry; y <= cy + ry; y++)
                for (int x = cx - rx; x <= cx + rx; x++)
                {
                    if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                    float dx = x - cx, dy = y - cy;
                    if (dx * dx / rx2 + dy * dy / ry2 <= 1f) pixels[x + y * w] = c;
                }
        }
    }
}
