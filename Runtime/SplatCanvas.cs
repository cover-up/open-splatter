using System;
using System.Collections.Generic;
using UnityEngine;
using static CoverUp.Splatter.SplatRules;

namespace CoverUp.Splatter
{
    /// <summary>
    /// Paints splats into a RenderTexture on the GPU. <see cref="Paint"/> puts one recipe down
    /// at a point; <see cref="Begin"/> and <see cref="PaintNext"/> walk a
    /// <see cref="SplatComposition"/> one item per call, so a painting can build progressively.
    /// Each splat is a handful of small draws into scratch textures (max-blended primitives, a
    /// rounding pass) and composite steps onto the canvas: chains first (thinned where they
    /// cross paint already down), then the body, then the spray (dots landing on paint survive
    /// with a probability). For a halo around the paint see <see cref="SplatGlow"/>.
    /// </summary>
    public sealed class SplatCanvas : IDisposable
    {
        public RenderTexture Texture { get; private set; }
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;
        /// <summary>A transparent canvas starts clear and ends straight-alpha (see
        /// <see cref="Unpremultiply"/>); an opaque one is black.</summary>
        public bool Transparent { get; }
        /// <summary>Counts up on every paint and clear, so a follower such as
        /// <see cref="SplatGlow"/> knows when to rerun.</summary>
        public int Version { get; private set; }

        /// <summary>A multiplier on the paint's colour as it is written into the half-float canvas.
        /// Above 1 the paint exceeds white, which a bloom post-process thresholded at 1 picks up
        /// while ordinary UI does not; the cost is a small hue drift in colours with a strong
        /// second channel (pinks, sky blues) as their top channel clips at the display. 1 is
        /// plain paint.</summary>
        public static float DefaultGain = 1f;
        public float Gain = DefaultGain;
        /// <summary>The odds that a spray dot landing on paint already down survives, and the
        /// opacity left to a chain or throw lying over paint. A composition draws its own from
        /// the rules and <see cref="Begin"/> takes them over; a splat painted on its own uses
        /// what is set here. The defaults are the measured values.</summary>
        public float SprayOnPaint = 0.45f, ChainOnPaint = 0.75f;

        /// <summary>The composition <see cref="PaintNext"/> is walking, if any.</summary>
        public SplatComposition Composition { get; private set; }
        public int ItemsPainted { get; private set; }
        public int ItemCount => Composition?.Items.Count ?? 0;
        public bool Done => Composition == null || ItemsPainted >= Composition.Items.Count;

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
            GainId = Shader.PropertyToID("_Gain"), HoleId = Shader.PropertyToID("_Hole");
        /// <summary>Half float: room for paint above 1.0 (bloom), and no banding in the softer shades.</summary>
        const RenderTextureFormat CanvasFormat = RenderTextureFormat.ARGBHalf;
        // pass indices follow the order in SplatPaint.shader
        const int PassPrims = 0, PassPolar = 1, PassRound = 2, PassAccum = 3, PassComposite = 4, PassUnpremultiply = 5;

        GraphicsBuffer prims; Prim[] primScratch = new Prim[1024];
        readonly List<RenderTexture> pool = new List<RenderTexture>(8);

        public SplatCanvas(int width, int height, bool transparent = false)
        {
            Transparent = transparent;
            Texture = new RenderTexture(width, height, 0, CanvasFormat, RenderTextureReadWrite.Linear)
            { name = "SplatCanvas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            Texture.Create();
            Clear();
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
            var prev = RenderTexture.active;
            RenderTexture.active = Texture; GL.Clear(false, true, Transparent ? Color.clear : Color.black);
            RenderTexture.active = prev;
            Version++;
        }

        // --- one splat at a time -------------------------------------------------------------------

        /// <summary>Paint one recipe with its centre at (<paramref name="x"/>, <paramref name="y"/>)
        /// canvas px, y up. A <paramref name="hole"/> erases the recipe's shape to the background
        /// instead of painting it.</summary>
        public void Paint(SplatRecipe recipe, float x, float y, bool hole = false)
        {
            var m = Mat(); if (m == null || recipe == null) return;
            var prev = RenderTexture.active;
            try { PaintSplat(m, recipe, x, y, hole); }
            finally { RenderTexture.active = prev; ReleasePool(); }
        }

        /// <summary>Paint a <see cref="Part"/> (a composition's throws or spatter, or any shape
        /// built from discs and capsules) in one flat colour, its prims offset by
        /// (<paramref name="x"/>, <paramref name="y"/>); each prim's own value multiplier still
        /// applies. <paramref name="scale"/> is display px per frame px and sets how much the
        /// edges are rounded.</summary>
        public void PaintPieces(Part pieces, float x, float y, float hue, float scale = 1f, float sMul = 1f, float vMul = 1f)
        {
            var m = Mat(); if (m == null || pieces == null || pieces.Empty) return;
            var prev = RenderTexture.active;
            try { PaintFlat(m, pieces, x, y, hue, sMul, vMul, scale); }
            finally { RenderTexture.active = prev; ReleasePool(); }
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
            var canvas = new SplatCanvas(w, h, transparent: true) { SprayOnPaint = sprayOnPaint, ChainOnPaint = 1f };
            canvas.Paint(r, centre.x, centre.y);
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
            Version++;
        }

        // --- a composition, one item per call ------------------------------------------------------

        /// <summary>Start painting a composition into a cleared canvas; its spray and chain odds
        /// become the canvas's. Null forgets the current one.</summary>
        public void Begin(SplatComposition composition)
        {
            Composition = composition; ItemsPainted = 0;
            if (composition != null) { SprayOnPaint = composition.PSpray; ChainOnPaint = composition.PChain; }
            Clear();
        }

        /// <summary>Paint the next item. Returns false when the composition is finished.</summary>
        public bool PaintNext()
        {
            if (Composition == null || ItemsPainted >= Composition.Items.Count) return false;
            var item = Composition.Items[ItemsPainted];
            if (item.Kind == SplatComposition.ItemKind.Splat) Paint(item.Recipe, item.X, item.Y, item.Hole);
            else PaintPieces(item.Pieces, item.X, item.Y, item.Hue, Composition.Scale, item.SMul, item.VMul);
            ItemsPainted++;
            return ItemsPainted < Composition.Items.Count;
        }

        public void PaintAll() { while (PaintNext()) { } }

        /// <summary>A living painting: after the composition's item list has been trimmed, the
        /// items already on this canvas are all of it (the popped ones stay visible until the
        /// canvas is replaced); further appended items paint from here.</summary>
        public void MarkAllPainted() { if (Composition != null) ItemsPainted = Composition.Items.Count; }

        // --- one splat: chains, body, spray ---------------------------------------------------------
        void PaintSplat(Material m, SplatRecipe R, float cx, float cy, bool hole)
        {
            m.SetVector(CentreId, new Vector4(cx, cy, 0, 0));
            m.SetFloat(HoleId, hole ? 1f : 0f);   // a hole erases (black, alpha 0) with the same shape rules
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
                    Composite(m, rounded, rect, 1, ChainOnPaint);
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
                    var spray = Scratch(rect); DrawPrims(m, spray, rect, R.Spray, below, SprayOnPaint, R.Seed * 0.001f);
                    Composite(m, spray, rect, 0, 0f);
                }
            }
        }

        // --- a flat-coloured part: throws of one core, a spatter drop, or a caller's own shape ------
        void PaintFlat(Material m, Part part, float x, float y, float hue, float sMul, float vMul, float scale)
        {
            var rect = RectOf(part, x, y);
            if (rect.width <= 1 || rect.height <= 1) return;
            m.SetVector(CentreId, new Vector4(x, y, 0, 0));
            m.SetFloat(HoleId, 0f);
            m.SetVector(Col0, new Vector4(hue, sMul, vMul, 1f));
            m.SetVector(Col1, new Vector4(1, 1, 1, 0)); m.SetVector(Col2, Vector4.one); m.SetVector(Col3, Vector4.one);
            float sigma = Mathf.Max(0.35f, 0.45f * scale);
            var raw = Scratch(rect); DrawPrims(m, raw, rect, part, null, 0f, 0f);
            var rounded = Scratch(rect); Round(m, raw, null, rounded, rect, sigma, Vector4.zero);
            Composite(m, rounded, rect, 2, ChainOnPaint);
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
        /// <summary>A copy of the canvas region already down.</summary>
        RenderTexture Below(RectInt rect)
        {
            var rt = RenderTexture.GetTemporary(Mathf.Max(1, rect.width), Mathf.Max(1, rect.height), 0, Texture.format, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Point; rt.wrapMode = TextureWrapMode.Clamp;
            pool.Add(rt);
            if (rect.width > 0 && rect.height > 0)   // a rect clamped away at the canvas edge is empty
                Graphics.CopyTexture(Texture, 0, 0, rect.xMin, rect.yMin, rect.width, rect.height, rt, 0, 0, 0, 0);
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
            RenderTexture.active = Texture;
            m.SetPass(PassComposite);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 6, 1);
            Version++;
        }

        public void Dispose()
        {
            ReleasePool();
            prims?.Dispose(); prims = null;
            if (Texture != null)
            {
                Texture.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(Texture); else UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
            }
        }
    }
}
