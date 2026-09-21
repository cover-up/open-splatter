using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static CoverUp.Splatter.SplatRules;

namespace CoverUp.Splatter
{
    /// <summary>
    /// Paints a <see cref="SplatComposition"/> into a RenderTexture on the GPU, one item per
    /// call, so a painting can build progressively. Each item is a handful of small draws into
    /// scratch textures (max-blended primitives, a rounding pass) and composite steps onto the
    /// canvas: chains first (thinned where they cross paint already down), then the body, then
    /// the spray (dots landing on paint survive with a probability), then the splat's own throws.
    /// </summary>
    public sealed class SplatCanvas : IDisposable
    {
        public RenderTexture Texture { get; private set; }
        public SplatComposition Composition { get; private set; }
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;
        public int ItemsPainted { get; private set; }
        public int ItemCount => Composition?.Items.Count ?? 0;
        public bool Done => Composition == null || ItemsPainted >= Composition.Items.Count;
        public bool Idle => Composition == null;
        /// <summary>Set once a composition has been painted to the end (the followers read the art then).</summary>
        public bool Complete { get; private set; }

        static Material mat;
        static readonly int PrimsId = Shader.PropertyToID("_Prims"), TargetId = Shader.PropertyToID("_Target"), CentreId = Shader.PropertyToID("_Centre"),
            BelowId = Shader.PropertyToID("_Below"), BelowRectId = Shader.PropertyToID("_BelowRect"), SurviveId = Shader.PropertyToID("_Survive"),
            PSurviveId = Shader.PropertyToID("_PSurvive"), SeedId = Shader.PropertyToID("_Seed"), RectId = Shader.PropertyToID("_Rect"),
            PolarId = Shader.PropertyToID("_Polar"), PolarMMId = Shader.PropertyToID("_PolarMM"), PolK0 = Shader.PropertyToID("_PolK0"),
            PolK1 = Shader.PropertyToID("_PolK1"), PolA0 = Shader.PropertyToID("_PolA0"), PolA1 = Shader.PropertyToID("_PolA1"),
            PolP0 = Shader.PropertyToID("_PolP0"), PolP1 = Shader.PropertyToID("_PolP1"), CutId = Shader.PropertyToID("_Cut"),
            HasCutId = Shader.PropertyToID("_HasCut"), SigmaId = Shader.PropertyToID("_Sigma"), MaskId = Shader.PropertyToID("_Mask"),
            AlphaId = Shader.PropertyToID("_Alpha"), AlphaTexelId = Shader.PropertyToID("_AlphaTexel"), SrcId = Shader.PropertyToID("_Src"),
            SrcTexelId = Shader.PropertyToID("_SrcTexel"), ModeId = Shader.PropertyToID("_Mode"), PChainId = Shader.PropertyToID("_PChain"),
            Col0 = Shader.PropertyToID("_Col0"), Col1 = Shader.PropertyToID("_Col1"), Col2 = Shader.PropertyToID("_Col2"), Col3 = Shader.PropertyToID("_Col3"),
            GainId = Shader.PropertyToID("_Gain"),
            NearId = Shader.PropertyToID("_Near"), FarId = Shader.PropertyToID("_Far"), StrengthId = Shader.PropertyToID("_Strength"),
            FarWeightId = Shader.PropertyToID("_FarWeight"), DirId = Shader.PropertyToID("_Dir"), HoleId = Shader.PropertyToID("_Hole");
        static Material glowMat;
        /// <summary>Half float: room for paint above 1.0 (bloom), and no banding in the softer shades.</summary>
        const RenderTextureFormat CanvasFormat = RenderTextureFormat.ARGBHalf;
        // pass indices follow the order in SplatPaint.shader
        const int PassPrims = 0, PassPolar = 1, PassRound = 2, PassAccum = 3, PassComposite = 4, PassUnpremultiply = 5;
        /// <summary>A transparent canvas starts clear and ends straight-alpha (see
        /// <see cref="Unpremultiply"/>); an opaque one is black.</summary>
        public bool Transparent { get; }
        /// <summary>A multiplier on the paint's colour as it is written into the half-float canvas.
        /// Above 1 the paint exceeds white, which a bloom post-process thresholded at 1 picks up
        /// while ordinary UI does not; the cost is a small hue drift in colours with a strong
        /// second channel (pinks, sky blues) as their top channel clips at the display. 1 is
        /// plain paint.</summary>
        public static float DefaultGain = 1f;
        public float Gain = DefaultGain;
        /// <summary>The outer glow: an opaque canvas presents a display texture that is the sharp
        /// paint plus a blurred copy of it, added only where the canvas is still black, so every
        /// splat wears a halo of its own colour in the gaps while the paint itself stays exactly
        /// as painted. Strength scales the halo; the radius is in the rules' frame px (about one
        /// core radius by default), with a fainter tail at four times it. 0 presents the sharp
        /// paint as is.</summary>
        public static float DefaultGlow = 0f, DefaultGlowRadius = 40f;
        public float Glow = DefaultGlow, GlowRadius = DefaultGlowRadius;
        /// <summary>The sharp canvas changed since the last <see cref="Present"/>.</summary>
        public bool Dirty { get; private set; }
        /// <summary>Force the next <see cref="Present"/> (a glow setting changed).</summary>
        public void Touch() { Dirty = true; }
        RenderTexture sharp;      // the exactly painted canvas; the same texture for a transparent canvas, else the display is derived from it

        GraphicsBuffer prims; Prim[] primScratch = new Prim[1024];
        readonly List<RenderTexture> pool = new List<RenderTexture>(8);

        public SplatCanvas(int width, int height, bool transparent = false)
        {
            Transparent = transparent;
            if (transparent)
            {
                Texture = NewRt(width, height, CanvasFormat, "SplatCanvas");
                sharp = Texture;
            }
            else
            {
                // the display (what the RawImage shows) is derived from the sharp paint by Present();
                // no alpha needed on an opaque canvas, so the packed float format halves its memory
                sharp = NewRt(width, height, CanvasFormat, "SplatCanvasSharp");
                var displayFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : CanvasFormat;
                Texture = NewRt(width, height, displayFormat, "SplatCanvas");
            }
            Clear();
        }

        static RenderTexture NewRt(int width, int height, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            rt.Create();
            return rt;
        }

        static Material GlowMat()
        {
            if (glowMat == null)
            {
                var sh = Shader.Find("OpenSplatter/Glow");
                if (sh == null) { Debug.LogError("SplatCanvas: shader OpenSplatter/Glow is missing from the build"); return null; }
                glowMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return glowMat;
        }

        /// <summary>
        /// Opaque canvases: derive the display texture from the sharp paint, with the outer glow
        /// added in the black gaps. A few small blits (the paint blurred at an eighth and a
        /// thirty-second of its size, then one full-size composite); the owner calls it once per
        /// frame that painted, on <see cref="Dirty"/>.
        /// </summary>
        public void Present()
        {
            Dirty = false;
            if (Transparent || Texture == sharp) return;
            var prev = RenderTexture.active;
            var m = Glow > 0f ? GlowMat() : null;
            if (m == null) { Graphics.Blit(sharp, Texture); RenderTexture.active = prev; return; }
            try
            {
                int w = Width, h = Height;
                float scale = Composition != null && Composition.Scale > 0f ? Composition.Scale : 1f;
                float sigma = Mathf.Clamp(GlowRadius * scale / 8f / 2.5f, 0.6f, 5f);   // the near halo blurs at 1/8 size
                var d2 = Temp(w / 2, h / 2); var d4 = Temp(w / 4, h / 4); var d8 = Temp(w / 8, h / 8);
                Graphics.Blit(sharp, d2); Graphics.Blit(d2, d4); Graphics.Blit(d4, d8);
                var t8 = Temp(w / 8, h / 8); var near = Temp(w / 8, h / 8);
                Gaussian(m, d8, t8, 1f, 0f, sigma); Gaussian(m, t8, near, 0f, 1f, sigma);
                var d16 = Temp(w / 16, h / 16); var d32 = Temp(w / 32, h / 32);
                Graphics.Blit(near, d16); Graphics.Blit(d16, d32);
                var t32 = Temp(w / 32, h / 32); var far = Temp(w / 32, h / 32);
                Gaussian(m, d32, t32, 1f, 0f, sigma); Gaussian(m, t32, far, 0f, 1f, sigma);
                m.SetTexture(NearId, near); m.SetTexture(FarId, far);
                m.SetFloat(StrengthId, Glow); m.SetFloat(FarWeightId, 0.5f);
                Graphics.Blit(sharp, Texture, m, 1);
            }
            finally
            {
                RenderTexture.active = prev;
                ReleasePool();
            }
        }

        RenderTexture Temp(int w, int h)
        {
            var rt = RenderTexture.GetTemporary(Mathf.Max(1, w), Mathf.Max(1, h), 0, CanvasFormat, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Bilinear; rt.wrapMode = TextureWrapMode.Clamp;
            pool.Add(rt);
            return rt;
        }

        static void Gaussian(Material m, RenderTexture src, RenderTexture dst, float dx, float dy, float sigma)
        {
            m.SetVector(DirId, new Vector4(dx, dy, 0f, 0f)); m.SetFloat(SigmaId, sigma);
            Graphics.Blit(src, dst, m, 0);
        }

        static Material Mat()
        {
            if (mat == null)
            {
                var sh = Shader.Find("OpenSplatter/Paint");
                if (sh == null) { Debug.LogError("SplatCanvas: shader OpenSplatter/Paint is missing from the build"); return null; }
                mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return mat;
        }

        /// <summary>Opaque black, or clear for a transparent canvas.</summary>
        public void Clear()
        {
            var bg = Transparent ? Color.clear : Color.black;
            var prev = RenderTexture.active;
            RenderTexture.active = Texture; GL.Clear(false, true, bg);
            if (sharp != Texture) { RenderTexture.active = sharp; GL.Clear(false, true, bg); }
            RenderTexture.active = prev;
        }

        /// <summary>
        /// One splat on its own transparent canvas, sized to the recipe. The recipe's centre
        /// lands at <paramref name="centre"/> in canvas px. The caller owns the canvas.
        /// </summary>
        public static SplatCanvas RenderSingle(SplatRecipe r, out Vector2 centre, float sprayOnPaint = 0.5f)
        {
            float x0 = r.BodyX0, y0 = r.BodyY0, x1 = r.BodyX1, y1 = r.BodyY1;
            foreach (var p in new[] { r.Chains, r.Spray })
                if (!p.Empty) { x0 = Mathf.Min(x0, p.X0); y0 = Mathf.Min(y0, p.Y0); x1 = Mathf.Max(x1, p.X1); y1 = Mathf.Max(y1, p.Y1); }
            const int pad = 4;
            int w = Mathf.Clamp(Mathf.CeilToInt(x1 - x0) + 2 * pad, 8, 2048), h = Mathf.Clamp(Mathf.CeilToInt(y1 - y0) + 2 * pad, 8, 2048);
            centre = new Vector2(pad - x0, pad - y0);
            var canvas = new SplatCanvas(w, h, transparent: true);
            var comp = new SplatComposition { Width = w, Height = h, Scale = r.Scale, PSpray = sprayOnPaint, PChain = 1f };
            comp.Items.Add(new SplatComposition.Item { Kind = SplatComposition.ItemKind.Splat, Recipe = r, X = centre.x, Y = centre.y });
            canvas.Begin(comp);
            canvas.PaintAll();
            canvas.Unpremultiply();
            return canvas;
        }

        /// <summary>Transparent canvas only: convert the premultiplied paint to straight alpha, so a
        /// plain RawImage with a CanvasGroup shows it without dark fringes.</summary>
        public void Unpremultiply()
        {
            if (!Transparent) return;
            var m = Mat(); if (m == null) return;
            var prev = RenderTexture.active;
            var copy = RenderTexture.GetTemporary(Width, Height, 0, Texture.format, RenderTextureReadWrite.Linear);
            Graphics.CopyTexture(Texture, copy);
            m.SetTexture(SrcId, copy);
            m.SetVector(RectId, new Vector4(0, 0, Width, Height)); m.SetVector(TargetId, new Vector4(0, 0, Width, Height));
            RenderTexture.active = Texture;
            m.SetPass(PassUnpremultiply);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(copy);
        }

        /// <summary>Start painting a composition into a cleared canvas.</summary>
        public void Begin(SplatComposition composition)
        {
            Composition = composition; ItemsPainted = 0; Complete = false;
            Clear();
        }

        /// <summary>Paint the next item. Returns false when the composition is finished.</summary>
        public bool PaintNext()
        {
            if (Composition == null || ItemsPainted >= Composition.Items.Count) { Complete = Composition != null; return false; }
            var m = Mat(); if (m == null) return false;
            var prevActive = RenderTexture.active;
            try
            {
                var item = Composition.Items[ItemsPainted];
                if (item.Kind == SplatComposition.ItemKind.Splat) PaintSplat(m, item);
                else PaintPieces(m, item);
            }
            finally
            {
                RenderTexture.active = prevActive;
                ReleasePool();
            }
            ItemsPainted++;
            if (ItemsPainted >= Composition.Items.Count) Complete = true;
            return ItemsPainted < Composition.Items.Count;
        }

        public void PaintAll() { while (PaintNext()) { } }

        /// <summary>A living painting: after the composition's item list has been trimmed, the
        /// items already on this canvas are all of it (the popped ones stay visible until the
        /// canvas is replaced); further appended items paint from here.</summary>
        public void MarkAllPainted() { if (Composition != null) ItemsPainted = Composition.Items.Count; }

        // --- one splat ---------------------------------------------------------------------------
        void PaintSplat(Material m, SplatComposition.Item item)
        {
            var R = item.Recipe;
            float cx = item.X, cy = item.Y;
            m.SetVector(CentreId, new Vector4(cx, cy, 0, 0));
            m.SetFloat(HoleId, item.Hole ? 1f : 0f);   // a click's hole erases (black, alpha 0) with the same shape rules
            SetColour(m, R);
            float sigma = Mathf.Max(0.35f, 0.45f * R.Scale);

            // chains: rounded, laid first and thinned where they cross paint already down
            if (!R.Chains.Empty)
            {
                var rect = RectOf(R.Chains, cx, cy);
                if (rect.width > 1 && rect.height > 1)
                {
                    var raw = Scratch(rect); DrawPrims(m, raw, rect, R.Chains, null, 0f, 0f);
                    var rounded = Scratch(rect); Round(m, raw, null, rounded, rect, sigma, Vector4.zero);
                    Composite(m, rounded, rect, 1, Composition.PChain);
                }
            }
            // body: rounded(core + stubs) max web max drops
            {
                float coreR = R.Core.Base + R.Core.Span;
                var rectCore = Union(RectOf(R.Stubs, cx, cy), CircleRect(cx, cy, coreR * Mathf.Max(1f, R.Core.Aspect) + 4f));
                var rawCore = Scratch(rectCore); DrawPolar(m, rawCore, rectCore, R.Core); if (!R.Stubs.Empty) DrawPrims(m, rawCore, rectCore, R.Stubs, null, 0f, 0f);
                var roundCore = Scratch(rectCore); Round(m, rawCore, null, roundCore, rectCore, sigma, Vector4.zero);

                float sheetR = R.Sheet.Base + R.Sheet.Span;
                var rectWeb = Union(CircleRect(cx, cy, sheetR + 4f), RectOf(R.Holes, cx, cy));
                var sheet = Scratch(rectWeb); DrawPolar(m, sheet, rectWeb, R.Sheet);
                var holes = Scratch(rectWeb); if (!R.Holes.Empty) DrawPrims(m, holes, rectWeb, R.Holes, null, 0f, 0f);
                var web = Scratch(rectWeb); Round(m, sheet, holes, web, rectWeb, sigma, new Vector4(cx, cy, R.Rc, R.WebFrom));

                var rectBody = Union(Union(rectCore, rectWeb), RectOf(R.Drops, cx, cy));
                var body = Scratch(rectBody);
                Accum(m, roundCore, rectCore, body, rectBody);
                Accum(m, web, rectWeb, body, rectBody);
                if (!R.Drops.Empty) DrawPrims(m, body, rectBody, R.Drops, null, 0f, 0f);
                Composite(m, body, rectBody, 0, 0f);
            }
            // spray: dots that land on paint already down survive with a probability
            if (!R.Spray.Empty)
            {
                var rect = RectOf(R.Spray, cx, cy);
                if (rect.width > 1 && rect.height > 1)
                {
                    var below = Below(rect);
                    var spray = Scratch(rect); DrawPrims(m, spray, rect, R.Spray, below, Composition.PSpray, R.Seed * 0.001f);
                    Composite(m, spray, rect, 0, 0f);
                }
            }
        }

        // --- a pieces item (throws of one core, or one spatter drop): flat colour per piece ------
        void PaintPieces(Material m, SplatComposition.Item item)
        {
            var part = item.Pieces;
            if (part == null || part.Empty) return;
            var rect = RectOf(part, item.X, item.Y);
            if (rect.width <= 1 || rect.height <= 1) return;
            m.SetVector(CentreId, new Vector4(item.X, item.Y, 0, 0));
            m.SetFloat(HoleId, 0f);
            m.SetVector(Col0, new Vector4(item.Hue, item.SMul, item.VMul, 1f));
            m.SetVector(Col1, new Vector4(1, 1, 1, 0)); m.SetVector(Col2, Vector4.one); m.SetVector(Col3, Vector4.one);
            float sigma = Mathf.Max(0.35f, 0.45f * Composition.Scale);
            var raw = Scratch(rect); DrawPrims(m, raw, rect, part, null, 0f, 0f);
            var rounded = Scratch(rect); Round(m, raw, null, rounded, rect, sigma, Vector4.zero);
            Composite(m, rounded, rect, 2, Composition.PChain);
        }

        void SetColour(Material m, SplatRecipe R)
        {
            m.SetVector(Col0, new Vector4(R.Hue, R.SMul, R.VMul, R.ShadeMul));
            m.SetVector(Col1, new Vector4(R.Rc, R.ColCell, R.ColHueCell, (R.NoiseSeed & 0xFFFF) * 0.37f));
            m.SetVector(Col2, new Vector4(R.ColVTop, R.ColSpread, R.ColSTop, R.ColTint));
            m.SetVector(Col3, new Vector4(R.ColHueSpread, 0, 0, 0));
        }

        // --- rects (canvas px, y up, integer, clamped to the canvas) -----------------------------
        RectInt RectOf(Part p, float cx, float cy) => Clamp(new RectInt(Mathf.FloorToInt(cx + p.X0) - 2, Mathf.FloorToInt(cy + p.Y0) - 2,
            Mathf.CeilToInt(p.X1 - p.X0) + 5, Mathf.CeilToInt(p.Y1 - p.Y0) + 5));
        RectInt CircleRect(float cx, float cy, float r) => Clamp(new RectInt(Mathf.FloorToInt(cx - r) - 2, Mathf.FloorToInt(cy - r) - 2, Mathf.CeilToInt(2 * r) + 5, Mathf.CeilToInt(2 * r) + 5));
        RectInt Clamp(RectInt r)
        {
            int x0 = Mathf.Clamp(r.xMin, 0, Width), y0 = Mathf.Clamp(r.yMin, 0, Height), x1 = Mathf.Clamp(r.xMax, 0, Width), y1 = Mathf.Clamp(r.yMax, 0, Height);
            return new RectInt(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }
        static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width <= 0 || a.height <= 0) return b; if (b.width <= 0 || b.height <= 0) return a;
            int x0 = Mathf.Min(a.xMin, b.xMin), y0 = Mathf.Min(a.yMin, b.yMin), x1 = Mathf.Max(a.xMax, b.xMax), y1 = Mathf.Max(a.yMax, b.yMax);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }
        static Vector4 V(RectInt r) => new Vector4(r.xMin, r.yMin, r.width, r.height);

        // --- scratch textures ----------------------------------------------------------------------
        RenderTexture Scratch(RectInt rect)
        {
            var rt = RenderTexture.GetTemporary(Mathf.Max(1, rect.width), Mathf.Max(1, rect.height), 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Bilinear; rt.wrapMode = TextureWrapMode.Clamp;
            pool.Add(rt);
            RenderTexture.active = rt; GL.Clear(false, true, Color.clear);
            return rt;
        }
        /// <summary>A copy of the (sharp) canvas region already down.</summary>
        RenderTexture Below(RectInt rect)
        {
            var rt = RenderTexture.GetTemporary(Mathf.Max(1, rect.width), Mathf.Max(1, rect.height), 0, sharp.format, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Point; rt.wrapMode = TextureWrapMode.Clamp;
            pool.Add(rt);
            if (rect.width > 0 && rect.height > 0)   // a rect clamped away at the canvas edge is empty
                Graphics.CopyTexture(sharp, 0, 0, rect.xMin, rect.yMin, rect.width, rect.height, rt, 0, 0, 0, 0);
            return rt;
        }
        static bool Empty(RectInt r) => r.width <= 0 || r.height <= 0;
        void ReleasePool() { foreach (var rt in pool) RenderTexture.ReleaseTemporary(rt); pool.Clear(); }

        // --- draws ----------------------------------------------------------------------------------
        void DrawPrims(Material m, RenderTexture target, RectInt rect, Part part, RenderTexture below, float pSurvive, float seed)
        {
            int n = part.Prims.Count;
            if (n == 0 || Empty(rect)) return;
            if (prims == null || prims.count < n)
            {
                prims?.Dispose();
                prims = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(n, 1024)), Prim.Stride);
            }
            if (primScratch.Length < n) primScratch = new Prim[Mathf.NextPowerOfTwo(n)];
            part.Prims.CopyTo(primScratch, 0);
            prims.SetData(primScratch, 0, 0, n);
            m.SetBuffer(PrimsId, prims);
            m.SetVector(TargetId, V(rect));
            if (below != null)
            {
                m.SetTexture(BelowId, below); m.SetVector(BelowRectId, V(rect));
                m.SetFloat(SurviveId, 1f); m.SetFloat(PSurviveId, pSurvive); m.SetFloat(SeedId, seed);
            }
            else m.SetFloat(SurviveId, 0f);
            RenderTexture.active = target;
            m.SetPass(PassPrims);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, n);
        }

        void DrawPolar(Material m, RenderTexture target, RectInt rect, Polar p)
        {
            if (Empty(rect)) return;
            m.SetVector(TargetId, V(rect)); m.SetVector(RectId, V(rect));
            m.SetVector(PolarId, new Vector4(p.Base, p.Span, p.Aspect, p.Axis));
            m.SetVector(PolarMMId, new Vector4(p.Min, p.Max, 0, 0));
            m.SetVector(PolK0, new Vector4(p.K[0], p.K[1], p.K[2], p.K[3])); m.SetVector(PolK1, new Vector4(p.K[4], p.K[5], p.K[6], p.K[7]));
            m.SetVector(PolA0, new Vector4(p.Amp[0], p.Amp[1], p.Amp[2], p.Amp[3])); m.SetVector(PolA1, new Vector4(p.Amp[4], p.Amp[5], p.Amp[6], p.Amp[7]));
            m.SetVector(PolP0, new Vector4(p.Ph[0], p.Ph[1], p.Ph[2], p.Ph[3])); m.SetVector(PolP1, new Vector4(p.Ph[4], p.Ph[5], p.Ph[6], p.Ph[7]));
            RenderTexture.active = target;
            m.SetPass(PassPolar);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
        }

        void Round(Material m, RenderTexture src, RenderTexture cut, RenderTexture dst, RectInt rect, float sigma, Vector4 mask)
        {
            if (Empty(rect)) return;
            m.SetVector(TargetId, V(rect));
            m.SetTexture(SrcId, src); m.SetVector(SrcTexelId, new Vector4(1f / src.width, 1f / src.height, src.width, src.height));
            m.SetFloat(SigmaId, sigma); m.SetVector(MaskId, mask);
            if (cut != null) { m.SetTexture(CutId, cut); m.SetFloat(HasCutId, 1f); } else m.SetFloat(HasCutId, 0f);
            RenderTexture.active = dst;
            m.SetPass(PassRound);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
        }

        void Accum(Material m, RenderTexture src, RectInt srcRect, RenderTexture dst, RectInt dstRect)
        {
            if (Empty(srcRect) || Empty(dstRect)) return;
            m.SetTexture(AlphaId, src); m.SetVector(RectId, V(srcRect)); m.SetVector(TargetId, V(dstRect));
            RenderTexture.active = dst;
            m.SetPass(PassAccum);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
        }

        void Composite(Material m, RenderTexture alpha, RectInt rect, int mode, float pChain)
        {
            if (Empty(rect)) return;
            var below = Below(rect);
            m.SetTexture(BelowId, below); m.SetTexture(AlphaId, alpha);
            m.SetVector(AlphaTexelId, new Vector4(1f / alpha.width, 1f / alpha.height, alpha.width, alpha.height));
            m.SetVector(RectId, V(rect)); m.SetVector(TargetId, new Vector4(0, 0, Width, Height));
            m.SetFloat(ModeId, mode); m.SetFloat(PChainId, pChain); m.SetFloat(GainId, Gain);
            RenderTexture.active = sharp;
            m.SetPass(PassComposite);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
            Dirty = true;                                           // the owner presents the display once per frame
        }

        public void Dispose()
        {
            ReleasePool();
            prims?.Dispose(); prims = null;
            if (sharp != null && sharp != Texture)
            {
                sharp.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(sharp); else UnityEngine.Object.DestroyImmediate(sharp);
            }
            sharp = null;
            if (Texture != null)
            {
                Texture.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(Texture); else UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
        }
    }
}
