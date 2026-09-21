using System;
using System.Collections.Generic;

namespace CoverUp.Splatter
{
    /// <summary>
    /// The rule-based paint splat. Every rule was measured on a set of reference paintings and
    /// carries the measured value with a range around it. Every quantity is in units of the
    /// solid core radius Rc unless it says px (then it is a pixel of the 1672-wide measurement
    /// frame, scaled by the caller's scale). Each rule carries the measured value and a range; a seed
    /// draws its own value from the range once (the splat's "character"), and every element
    /// then draws around it. So two seeds differ in kind, and no seed is the measurement copied.
    ///
    /// This class is pure C#: a seed in, a <see cref="SplatRecipe"/> of primitives and colour
    /// parameters out. It never touches the Unity API, so it runs on a worker thread and is
    /// deterministic per seed. <see cref="SplatCanvas"/> draws the recipe on the GPU.
    /// </summary>
    public static class SplatRules
    {
        public const float Tau = (float)(2 * Math.PI);
        /// <summary>The frame every measurement is in: the reference paintings were 1672 px wide.</summary>
        public const float FrameW = 1672f, FrameH = 941f;

        // --- the rules ------------------------------------------------------------------------
        // (measured, lo, hi): a seed draws its own value uniformly from (lo, hi). Integer rules
        // are rounded. The comments carry what was measured; do not "tidy" them away.
        public sealed class Rule
        {
            public readonly string Name; public readonly float Measured, Lo, Hi; public readonly bool Int;
            public Rule(string name, float measured, float lo, float hi, bool isInt = false) { Name = name; Measured = measured; Lo = lo; Hi = hi; Int = isInt; }
        }

        public static readonly Rule
            // core outline
            CoreRLo = new Rule("core_r_lo", 0.64f, 0.60f, 0.68f),        // lowest outline radius, Rc (the core dissolves into the web from 0.75 Rc)
            CoreRHi = new Rule("core_r_hi", 0.90f, 0.84f, 0.96f),        // highest outline radius, Rc (the web supplies everything beyond)
            CoreAspect = new Rule("core_aspect", 1.25f, 1.10f, 1.35f),   // bounding-box aspect of the core
            StubsN = new Rule("stubs_n", 12, 8, 18, true),               // crown fingers on the rim (the web carries most of the crown now)
            // chains: every stroke out of the core. A wide root dragged out of the core narrowing
            // to a line, a comet drop whose tail widens from the line into a round head; LONG
            // chains go on from the head with a thin line to a small end drop. Measured on 94
            // mains: 2.5 chains past 2.2 Rc per splat (median 2, p90 5), one typically very long
            // (longest per splat median 4.8 Rc, p90 9); chains past ~3.5 Rc are a thin line with a
            // teardrop head at the FAR end, far heads 12-24 px in radius whatever the source size.
            ChainsN = new Rule("chains_n", 3, 2, 5, true),
            ChainReachMed = new Rule("chain_reach_med", 2.0f, 1.6f, 2.4f),   // Rc; lognormal sigma 0.7 (28 % past 3 Rc, 16 % past 4), clipped 0.8-7
            ChainThrowFrom = new Rule("chain_throw_from", 3.5f, 3.0f, 4.0f), // Rc; longer chains take the throw form: line all the way, head at the end
            ChainThrowHead = new Rule("chain_throw_head", 15, 13, 17),       // px; head radius of a throw-form chain, lognormal sigma 0.25, clip 10-24
            ChainLongFrom = new Rule("chain_long_from", 2.2f, 2.0f, 2.5f),   // Rc; chains reaching past this continue beyond their head
            ChainRootW = new Rule("chain_root_w", 0.24f, 0.18f, 0.30f),      // Rc, full width where it leaves the core
            ChainLineK = new Rule("chain_line_k", 0.30f, 0.22f, 0.40f),      // line width = this x the head width (floor 2 px, ceiling 6 px)
            ChainHeadW = new Rule("chain_head_w", 13, 10, 16),               // px, full width of a mid-way chain head (yellow: 6-25 px); fixed, not scaled by the core
            ChainDropTail = new Rule("chain_drop_tail", 0.4f, 0.3f, 0.5f),   // Rc, the head's tail widening from the line (long chains)
            ChainEndW = new Rule("chain_end_w", 4.5f, 3.0f, 6.0f),           // px, median end-drop width; lognormal sigma 0.55, clip 1.5-15
            ChainBend = new Rule("chain_bend", 4.0f, 2.0f, 8.0f),            // degrees of bend over the chain
            ChainJitter = new Rule("chain_jitter", 0.4f, 0.3f, 0.5f),        // chains sit on evenly spaced slots, jittered by this fraction of a slot
            // detached drops
            DropsN = new Rule("drops_n", 117, 90, 140, true),
            DropDiamMed = new Rule("drop_diam_med", 0.10f, 0.085f, 0.12f),   // Rc; lognormal sigma 0.5, cap 0.45
            DropDiamCap = new Rule("drop_diam_cap", 0.45f, 0.35f, 0.50f),
            DropDistMed = new Rule("drop_dist_med", 2.4f, 2.1f, 2.7f),       // Rc; first drop of a line, lognormal sigma 0.25
            DropDistMax = new Rule("drop_dist_max", 4.4f, 4.0f, 4.8f),
            LineSpacingMed = new Rule("line_spacing_med", 0.40f, 0.32f, 0.50f), // Rc between consecutive drops on a line
            LineLenMean = new Rule("line_len_mean", 1.3f, 1.0f, 1.7f),       // drops per line = 1 + exponential(mean); p50 2, max 6
            // direction: two sectors + floor
            SectorMainW = new Rule("sector_main_w", 60, 45, 75),             // degrees
            SectorSecondW = new Rule("sector_second_w", 75, 60, 90),
            SectorExtra = new Rule("sector_extra", 0.30f, 0.15f, 0.45f),     // extra lines added inside the sectors, on top of an even spread
            // drop shape
            TeardropAspect = new Rule("teardrop_aspect", 2.3f, 2.0f, 2.6f),  // for 0.09-0.3 Rc
            BigAspect = new Rule("big_aspect", 1.6f, 1.4f, 1.8f),            // above 0.3 Rc
            // trails
            TrailFillNear = new Rule("trail_fill_near", 0.42f, 0.35f, 0.50f), // painted fraction of the path inside 2.6 Rc
            TrailFillFar = new Rule("trail_fill_far", 0.15f, 0.10f, 0.20f),   // beyond 3.5 Rc
            PieceMed = new Rule("piece_med", 0.07f, 0.05f, 0.10f),           // Rc; painted piece length, lognormal sigma 0.8
            GapMed = new Rule("gap_med", 0.20f, 0.15f, 0.26f),               // Rc; gap length, lognormal sigma 0.7
            TrailWK = new Rule("trail_w_k", 0.31f, 0.25f, 0.40f),            // hair width = this x the width of the drop it feeds (measured 0.31, corr 0.63)
            TrailWMin = new Rule("trail_w_min", 1.2f, 1.0f, 1.6f),           // px, full width floor
            TrailWMax = new Rule("trail_w_max", 6.0f, 5.0f, 7.0f),           // px, full width ceiling
            FunnelLen = new Rule("funnel_len", 0.08f, 0.06f, 0.12f),         // Rc; the funnel that opens onto the drop the trail feeds
            FunnelK = new Rule("funnel_k", 0.55f, 0.45f, 0.70f),             // funnel mouth = this x the drop's radius
            NeckLen = new Rule("neck_len", 0.18f, 0.12f, 0.28f),             // Rc; the neck DRAGGED out of the parent, concave, always attached
            NeckK = new Rule("neck_k", 0.65f, 0.5f, 0.85f),                  // neck root width = this x the parent's local radius
            CoreRoot = new Rule("core_root", 0.12f, 0.09f, 0.16f),           // Rc; local radius the core offers a neck (like a crown stub)
            CoreNeckP = new Rule("core_neck_p", 0.35f, 0.25f, 0.50f),        // share of lines whose first drop drags a neck out of the core
            TrailMinDiam = new Rule("trail_min_diam", 0.12f, 0.10f, 0.15f),  // Rc; drops below this are too small to leave a trail
            // children
            ChildMinDiam = new Rule("child_min_diam", 0.20f, 0.16f, 0.24f),  // a drop above this throws children
            ChildN = new Rule("child_n", 2, 1, 3, true),
            ChildSize = new Rule("child_size", 0.42f, 0.33f, 0.50f),         // child diameter / parent diameter
            ChildDist = new Rule("child_dist", 1.0f, 0.5f, 1.5f),            // Rc further along the parent's direction
            ChildCone = new Rule("child_cone", 22, 15, 30),                  // degrees
            // colour (measured on 66 rainbow mains, own-colour INTERIOR paint per annulus): the
            // bright paint stays at V 0.96 / S 0.99 everywhere; what grows with radius is a shallow
            // ladder of shades below it, pale tints, and a symmetric hue spread (no drift); patches
            // are flat regions of 0.12-0.3 Rc. The ranges sit below the raw measurement: the
            // references carried upscaling noise, and the perceived contrast is what matters.
            ColVTop = new Rule("col_v_top", 1.0f, 0.98f, 1.0f),              // V of the bright paint
            ColSpread = new Rule("col_spread", 1.0f, 0.75f, 1.25f),          // scale of the shade-spread profile
            ColCell = new Rule("col_cell", 0.25f, 0.18f, 0.32f),             // Rc; coarse cell of the shade noise
            ColSTop = new Rule("col_s_top", 0.99f, 0.97f, 1.0f),             // S of the bright paint
            ColTint = new Rule("col_tint", 1.0f, 0.6f, 1.4f),                // scale of the pale-tint profile
            ColHueSpread = new Rule("col_hue_spread", 1.0f, 0.6f, 1.4f),     // scale of the hue-spread profile
            ColHueCell = new Rule("col_hue_cell", 0.35f, 0.25f, 0.5f),       // Rc; cell of the hue patches
            // the web: the dissolving sheet from the core edge out to ~2.6 Rc (measured on 94
            // mains: own-paint share per 0.3 Rc band from 0.8 Rc = 0.87, 0.70, 0.52, 0.41, 0.34,
            // 0.25 on the isolated ones; wall thickness ~5 px at the core edge, ~3 px beyond;
            // black between strands OPEN; cell spacing 0.15 -> 0.24 Rc outward). A sheet with a
            // lumpy hole at every seed of a jittered set (hole = half the spacing minus the wall),
            // with doors cut through the walls between neighbours by a smooth patch field
            // wherever it exceeds the keep profile, so the sheet dissolves outward.
            WebFrom = new Rule("web_from", 0.75f, 0.70f, 0.80f),             // Rc; where the web starts
            WebReach = new Rule("web_reach", 2.6f, 2.3f, 2.9f),              // Rc; outer limit of the sheet
            WebSpaceIn = new Rule("web_space_in", 0.18f, 0.15f, 0.21f),      // Rc; cell spacing at 1.0 Rc
            WebSpaceOut = new Rule("web_space_out", 0.28f, 0.24f, 0.32f),    // Rc; cell spacing at 2.4 Rc
            WebStrandW = new Rule("web_strand_w", 3.6f, 3.0f, 4.2f),         // px; median wall width at 1.0 Rc, x0.6 from 1.6 Rc on; lognormal sigma 0.35
            WebKeep = new Rule("web_keep", 0.9f, 0.82f, 0.98f),              // scale on the keep profile [0.85, 0.66, 0.56, 0.50, 0.38] at q = 0.9, 1.3, 1.7, 2.1, 2.6
            WebPatch = new Rule("web_patch", 0.35f, 0.25f, 0.45f),           // Rc; size of the smooth field that decides which walls go: bays, not salt and pepper
            // spray: tiny droplets around the splat (measured 300-450 per splat, density peaking
            // at 1.5-2.5 Rc). No mist: the references' sub-pixel specks were upscaling artefacts.
            SprayN = new Rule("spray_n", 380, 300, 460, true),
            SprayPeak = new Rule("spray_peak", 2.1f, 1.8f, 2.4f),            // Rc; radial density peak
            SpraySpread = new Rule("spray_spread", 0.7f, 0.55f, 0.9f),       // Rc; lognormal-ish width of the radial band
            SprayDiam = new Rule("spray_diam", 2.6f, 2.2f, 3.2f);            // px; lognormal sigma 0.4, clip 1.2-9

        /// <summary>The web's keep profile: share of walls kept at q = 0.9, 1.3, 1.7, 2.1, 2.6 Rc,
        /// times <see cref="WebKeep"/>; every wall beyond the reach goes.</summary>
        static readonly float[] KeepQ = { 0.9f, 1.3f, 1.7f, 2.1f, 2.6f };
        static readonly float[] KeepP = { 0.85f, 0.66f, 0.56f, 0.50f, 0.38f };

        // --- a splat's character: one draw per rule ----------------------------------------------
        /// <summary>A seed's draw of every rule: its character. Two seeds differ in kind through
        /// these, and every element of the splat then draws around them.</summary>
        public sealed class Character
        {
            readonly Dictionary<string, float> v = new Dictionary<string, float>(80);
            public float this[Rule r] => v[r.Name];
            public int I(Rule r) => (int)Math.Round(v[r.Name]);
            public Character(SplatRng rng, IEnumerable<Rule> rules, SplatRuleOverrides overrides = null)
            {
                foreach (var r in rules)
                {
                    float lo = r.Lo, hi = r.Hi;
                    if (overrides != null && overrides.TryGet(r, out var range)) { lo = range.lo; hi = range.hi; }
                    float x = rng.Uniform(lo, hi);
                    v[r.Name] = r.Int ? (float)Math.Round(x) : x;
                }
            }
            public IEnumerable<KeyValuePair<string, float>> All => v;
        }

        /// <summary>Every rule of the splat, in table order, for tooling that lists or tweaks them.</summary>
        public static IReadOnlyList<Rule> All => AllRules;

        static readonly Rule[] AllRules =
        {
            CoreRLo, CoreRHi, CoreAspect, StubsN, ChainsN, ChainReachMed, ChainThrowFrom, ChainThrowHead, ChainLongFrom,
            ChainRootW, ChainLineK, ChainHeadW, ChainDropTail, ChainEndW, ChainBend, ChainJitter, DropsN, DropDiamMed,
            DropDiamCap, DropDistMed, DropDistMax, LineSpacingMed, LineLenMean, SectorMainW, SectorSecondW, SectorExtra,
            TeardropAspect, BigAspect, TrailFillNear, TrailFillFar, PieceMed, GapMed, TrailWK, TrailWMin, TrailWMax,
            FunnelLen, FunnelK, NeckLen, NeckK, CoreRoot, CoreNeckP, TrailMinDiam, ChildMinDiam, ChildN, ChildSize,
            ChildDist, ChildCone, ColVTop, ColSpread, ColCell, ColSTop, ColTint, ColHueSpread, ColHueCell, WebFrom,
            WebReach, WebSpaceIn, WebSpaceOut, WebStrandW, WebKeep, WebPatch, SprayN, SprayPeak, SpraySpread, SprayDiam,
        };

        // --- primitives -------------------------------------------------------------------------
        /// <summary>One raster primitive, in display px relative to the splat's centre. Kind 0 is a
        /// lumpy disc (three wobble harmonics on its radius), kind 1 a capsule with a wavering
        /// width. Laid out as five float4s for the GPU buffer (see SplatPaint.shader).</summary>
        public struct Prim
        {
            public float Kind, X0, Y0, Ang;        // a
            public float X1, Y1, Rx, Ry;           // b: disc rx, ry | capsule hw0, hw1
            public float A2, A3, A5, MinHw;        // c: disc wobble amplitudes | capsule min half-width
            public float P2, P3, P5, Tip;          // d: disc wobble phases | capsule tip radius
            public float WaveF, WavePh, Wob, V;    // e: capsule waver | V = per-piece value (throws), else 0
            public const int Stride = 20 * 4;

            public float Extent => Kind == 0 ? Math.Max(Rx, Ry) * 1.3f + 2f : Math.Max(Math.Max(Rx, Ry), Tip) + 2f;
            public void Bounds(ref float x0, ref float y0, ref float x1, ref float y1)
            {
                float e = Extent;
                if (Kind == 0) { x0 = Math.Min(x0, X0 - e); y0 = Math.Min(y0, Y0 - e); x1 = Math.Max(x1, X0 + e); y1 = Math.Max(y1, Y0 + e); }
                else
                {
                    x0 = Math.Min(x0, Math.Min(X0, X1) - e); y0 = Math.Min(y0, Math.Min(Y0, Y1) - e);
                    x1 = Math.Max(x1, Math.Max(X0, X1) + e); y1 = Math.Max(y1, Math.Max(Y0, Y1) + e);
                }
            }
        }

        /// <summary>A polar shape: r(theta) = Base + Span x normalised(sum of Amp[k] cos(K[k] theta + Ph[k]))
        /// in a frame elongated by Aspect along Axis. The core outline and the web's sheet.</summary>
        public sealed class Polar
        {
            public float Base, Span, Aspect, Axis, Min, Max;
            public readonly float[] K = new float[8], Amp = new float[8], Ph = new float[8];
            public float Raw(float theta)
            {
                float s = 0f;
                for (int i = 0; i < 8; i++) s += Amp[i] * (float)Math.Cos(K[i] * theta + Ph[i]);
                return s;
            }
            /// <summary>Radius along the (frame) angle theta.</summary>
            public float R(float theta) => Base + Span * (Raw(theta) - Min) / (Max - Min + 1e-6f);
            public void Normalise()
            {
                Min = float.MaxValue; Max = float.MinValue;
                for (int i = 0; i < 720; i++) { float r = Raw(i * Tau / 720f); if (r < Min) Min = r; if (r > Max) Max = r; }
            }
        }

        /// <summary>A prim list with its bounds, in display px relative to the splat centre.</summary>
        public sealed class Part
        {
            public readonly List<Prim> Prims = new List<Prim>(512);
            public float Wobble, MinHw;
            public float X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue;
            public bool Empty => Prims.Count == 0;
            public Part(float wobble, float minHw) { Wobble = wobble; MinHw = minHw; }
            public void Disc(SplatRng rng, float cx, float cy, float rx, float ry, float ang = 0f, float v = 0f)
            {
                var p = new Prim { Kind = 0, X0 = cx, Y0 = cy, Rx = rx, Ry = ry, Ang = ang, V = v,
                    A2 = Wobble * 1.0f * rng.Uniform(0.3f, 1f), A3 = Wobble * 0.8f * rng.Uniform(0.3f, 1f), A5 = Wobble * 0.5f * rng.Uniform(0.3f, 1f),
                    P2 = rng.Uniform(0, Tau), P3 = rng.Uniform(0, Tau), P5 = rng.Uniform(0, Tau) };
                p.Bounds(ref X0, ref Y0, ref X1, ref Y1); Prims.Add(p);
            }
            public void Disc(SplatRng rng, float cx, float cy, float r) => Disc(rng, cx, cy, r, r, 0f);
            public void Capsule(SplatRng rng, float xa, float ya, float xb, float yb, float hw0, float hw1, float tip = 0f, float v = 0f)
            {
                float L = Hypot(xb - xa, yb - ya);
                if (L < 1e-3f) { Disc(rng, xa, ya, Math.Max(hw0, tip)); return; }
                var p = new Prim { Kind = 1, X0 = xa, Y0 = ya, X1 = xb, Y1 = yb, Rx = hw0, Ry = hw1, Tip = tip, MinHw = MinHw, V = v,
                    WaveF = rng.Uniform(1.5f, 3.0f), WavePh = rng.Uniform(0, Tau), Wob = Wobble };
                p.Bounds(ref X0, ref Y0, ref X1, ref Y1); Prims.Add(p);
            }
        }

        // --- the recipe -------------------------------------------------------------------------
        public sealed class SplatRecipe
        {
            public int Seed;
            public float Rc;                 // display px
            public float Scale;              // display px per measurement-frame px
            public float Hue, SMul, VMul, ShadeMul;
            public bool White;
            public Character Ch;
            public Polar Core, Sheet;
            /// <summary>Solid parts: the core outline is <see cref="Core"/>; stubs are the crown
            /// fingers. Together they are rounded (blur + threshold) as one solid.</summary>
            public Part Stubs, Chains, Holes, Drops, Spray;
            public float WebFrom;            // Rc; the web is masked inside this
            public float ColCell, ColHueCell, ColVTop, ColSpread, ColSTop, ColTint, ColHueSpread;  // colour field (px / units)
            public int NoiseSeed;
            public int DropsPlaced, ChainsCount;
            public readonly List<float> ChainReach = new List<float>(8);
            /// <summary>Bounds of everything but the chains, relative to the centre (px).</summary>
            public float BodyX0, BodyY0, BodyX1, BodyY1;
        }

        static float Hypot(float a, float b) => (float)Math.Sqrt(a * a + b * b);
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        static float Interp(float x, float[] xs, float[] ys)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) { float t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]); return ys[i - 1] + (ys[i] - ys[i - 1]) * t; }
            return ys[ys.Length - 1];
        }
        static float WrapPi(float a) { a = (float)Math.IEEERemainder(a, Tau); return a; }
        static float AngDiff(float a, float b) => Math.Abs(WrapPi(a - b));

        /// <summary>
        /// One splat coloured by a palette: hue, saturation, value and whiteness come from
        /// <see cref="SplatPalette.Pick"/> on a random stream of their own, so the shape is exactly
        /// what the hue overload makes for the seed, only coloured.
        /// </summary>
        public static SplatRecipe Generate(int seed, float rcFramePx, float scale, SplatPalette palette, float outward = float.NaN, SplatRuleOverrides overrides = null)
        {
            var (hue, sMul, vMul, white) = (palette ?? SplatPalette.Rainbow).Pick(new SplatRng(seed ^ ColourStream));
            return Generate(seed, rcFramePx, scale, hue, outward, sMul, vMul, 1f, white, overrides);
        }
        /// <summary>Sets the colour's random stream apart from the shape's.</summary>
        const int ColourStream = 0x2F1E9A57;

        /// <summary>
        /// One splat. <paramref name="rcFramePx"/> is the core radius in measurement-frame px,
        /// <paramref name="scale"/> display px per frame px. <paramref name="outward"/> (radians,
        /// y up) is the direction from the frame centre through this splat in a composite: chains
        /// lean that way and run longer (measured: 1.03 outward, 1.04 sideways, 0.39 inward chains
        /// per splat; outward connected reach p90 4.3 Rc against 2.7 inward). NaN = no lean.
        /// <paramref name="shadeMul"/> scales the shade spread: the bed splats are darker and far
        /// more mottled.
        /// </summary>
        public static SplatRecipe Generate(int seed, float rcFramePx, float scale, float hue, float outward = float.NaN,
            float sMul = 1f, float vMul = 1f, float shadeMul = 1f, bool white = false, SplatRuleOverrides overrides = null)
        {
            var rng = new SplatRng(seed);
            var ch = new Character(rng, AllRules, overrides);
            float Rc = rcFramePx * scale;
            float Px(float framePx) => framePx * scale;
            var R = new SplatRecipe { Seed = seed, Rc = Rc, Scale = scale, Hue = hue, SMul = sMul, VMul = vMul, ShadeMul = shadeMul, White = white, Ch = ch, NoiseSeed = rng.Next() };

            // --- 1. core: a lobed outline around an elongated disc
            float asp = ch[CoreAspect]; float ax = rng.Uniform(0, Tau);
            var core = new Polar { Aspect = asp, Axis = ax };
            float[] coreK = { 2, 3, 5, 7, 11, 17, 23, 31 }; float[] coreA = { 0.45f, 0.6f, 0.45f, 0.4f, 0.35f, 0.3f, 0.25f, 0.2f };
            for (int i = 0; i < 8; i++) { core.K[i] = coreK[i]; core.Amp[i] = coreA[i]; core.Ph[i] = rng.Uniform(0, Tau); }
            core.Normalise();
            core.Base = Rc * ch[CoreRLo]; core.Span = Rc * (ch[CoreRHi] - ch[CoreRLo]);
            R.Core = core;

            // a point on the (elongated) outline along canvas angle theta, at frac of the radius
            (float x, float y, float r) OutlinePoint(float theta, float frac)
            {
                float c = (float)Math.Cos(theta), s = (float)Math.Sin(theta);
                float cn = (c * (float)Math.Cos(ax) + s * (float)Math.Sin(ax)) / asp;
                float sn = -c * (float)Math.Sin(ax) + s * (float)Math.Cos(ax);
                float m = Hypot(cn, sn);
                float r = core.R((float)Math.Atan2(sn, cn)) / m * frac;
                return (c * r, s * r, r);
            }

            var stubs = new Part(0.06f, Px(1f));
            var chains = new Part(0.06f, Px(1f));
            var side = new Part(0.06f, 0f);          // side droplets go in with the drops
            var drops = new Part(0.06f, 0f);
            var trails = new Part(0.05f, Px(0.5f));  // hairs go in with the drops (their own min width)

            // --- direction model: two sectors + a floor
            float mainC = rng.Uniform(0, Tau);
            float secondC = mainC + rng.Sign() * Deg(rng.Uniform(90, 180));
            float mainW = Deg(ch[SectorMainW]), secondW = Deg(ch[SectorSecondW]);
            List<float> EvenDirections(int n, float jitter)
            {
                // n directions on evenly spaced slots, each jittered by a fraction of a slot
                float b = rng.Uniform(0, Tau); var outl = new List<float>(n);
                foreach (int k in rng.Permutation(n)) outl.Add(b + (k + rng.Uniform(-jitter, jitter)) * Tau / n);
                return outl;
            }
            List<float> SectorDirections(int n)
            {
                var outl = new List<float>(n);
                for (int i = 0; i < n; i++)
                    outl.Add(rng.Chance(0.5f) ? mainC + rng.Uniform(-mainW / 2, mainW / 2) : secondC + rng.Uniform(-secondW / 2, secondW / 2));
                return outl;
            }

            // --- chains: every stroke out of the core
            int chainsN = ch.I(ChainsN);
            List<float> slots = EvenDirections(chainsN, ch[ChainJitter]);
            bool lean = !float.IsNaN(outward);
            if (lean)
            {
                // measured: 1.7x / 0.85x / 0.6x a uniform spread for outward / sideways / inward
                slots = new List<float>(chainsN);
                for (int i = 0; i < chainsN; i++)
                {
                    int c = rng.Choice3(0.42f, 0.42f, 0.16f);
                    if (c == 0) slots.Add(outward + rng.Uniform(-Tau / 8, Tau / 8));
                    else if (c == 1) slots.Add(outward + rng.Sign() * rng.Uniform(Tau / 8, 3 * Tau / 8));
                    else slots.Add(outward + Tau / 2 + rng.Uniform(-Tau / 8, Tau / 8));
                }
            }
            foreach (float th in slots)
            {
                float mult = 1f, sig = 0.7f;
                if (lean)
                {
                    float dOut = AngDiff(th, outward);
                    if (dOut < Tau / 8) { mult = 1.2f; sig = 0.7f; } else if (dOut > 3 * Tau / 8) { mult = 0.85f; sig = 0.4f; } else { mult = 1.0f; sig = 0.5f; }
                }
                float reach = Rc * rng.LogNormal(ch[ChainReachMed] * mult, sig, 0.8f, 7.0f);
                R.ChainReach.Add(reach / Rc);
                bool isLong = reach > ch[ChainLongFrom] * Rc;
                bool isThrow = reach > ch[ChainThrowFrom] * Rc;
                float headHw = Px(ch[ChainHeadW]) / 2 * (float)Math.Pow(reach / (1.8f * Rc), 0.3) * rng.LogNormal(1f, 0.3f, 0.6f, 1.7f);
                headHw = Clamp(headHw, Px(3), Px(13));
                float wl = Clamp(ch[ChainLineK] * headHw, Px(1f), Px(3f));    // half-widths
                float rootHw = Rc * ch[ChainRootW] / 2 * rng.Uniform(0.7f, 1.3f) * (reach < 1.5f * Rc ? 0.7f : 1f);
                float bend = Deg(ch[ChainBend]) * rng.Sign() * rng.Uniform(0.6f, 1.4f);
                var (xa, ya, r0) = OutlinePoint(th, 0.8f);
                float posX = xa, posY = ya, heading = th;
                float span = reach - r0 * 0.8f;                       // px from the root to the far end
                if (span < Px(4)) continue;
                R.ChainsCount++;

                void Walk(float length, Func<float, float> wOfT, int steps)
                {
                    for (int k = 0; k < steps; k++)
                    {
                        float t0 = (float)k / steps, t1 = (float)(k + 1) / steps, dl = length / steps;
                        heading += bend * dl / span;
                        float nx = posX + (float)Math.Cos(heading) * dl, ny = posY + (float)Math.Sin(heading) * dl;
                        chains.Capsule(rng, posX, posY, nx, ny, wOfT(t0), wOfT(t1));
                        posX = nx; posY = ny;
                    }
                }

                if (isThrow)
                {
                    // throw form: the root narrows to a thin line that runs almost the whole way, then
                    // a comet tail widens over about a core radius into a big teardrop head at the end;
                    // half the time a small drop lies a little beyond the head
                    headHw = Px(rng.LogNormal(ch[ChainThrowHead], 0.25f, 10f, 24f));
                    float rootLen = span * rng.Uniform(0.15f, 0.25f);
                    float tail = Rc * rng.Uniform(0.8f, 1.4f);
                    float line1 = Math.Max(Px(4), span - rootLen - tail - headHw * 1.6f);
                    float hh = headHw;
                    Walk(rootLen, t => wl + (rootHw - wl) * (float)Math.Pow(1 - t, 2.2), 8);
                    Walk(line1, t => wl, 8);
                    Walk(tail, t => wl + (hh * 0.85f - wl) * (float)Math.Pow(t, 1.6), 6);
                    float hx = posX + (float)Math.Cos(heading) * headHw * 0.6f, hy = posY + (float)Math.Sin(heading) * headHw * 0.6f;
                    chains.Disc(rng, hx, hy, headHw * 1.2f, headHw, heading);
                    if (rng.Chance(0.5f))
                    {
                        float r2 = headHw * rng.Uniform(0.25f, 0.5f), d2 = headHw * rng.Uniform(1.8f, 3.2f);
                        float x2 = hx + (float)Math.Cos(heading) * d2, y2 = hy + (float)Math.Sin(heading) * d2;
                        if (rng.Chance(0.5f))
                            chains.Capsule(rng, hx + (float)Math.Cos(heading) * headHw * 0.6f, hy + (float)Math.Sin(heading) * headHw * 0.6f, x2, y2, wl * 0.6f, wl * 0.6f);
                        chains.Disc(rng, x2, y2, r2 * rng.Uniform(1.0f, 1.4f), r2, heading);
                    }
                }
                else if (isLong)
                {
                    float rootLen = span * rng.Uniform(0.22f, 0.32f);
                    float line1 = span * rng.Uniform(0.12f, 0.22f);
                    float tail = Rc * ch[ChainDropTail] * rng.Uniform(0.8f, 1.2f);
                    float hh = headHw;
                    Walk(rootLen, t => wl + (rootHw - wl) * (float)Math.Pow(1 - t, 2.2), 8);
                    Walk(line1, t => wl, 4);
                    Walk(tail, t => wl + (hh - wl) * (float)Math.Pow(t, 1.6), 6);
                    float hx = posX + (float)Math.Cos(heading) * headHw * 0.6f, hy = posY + (float)Math.Sin(heading) * headHw * 0.6f;
                    chains.Disc(rng, hx, hy, headHw * 1.15f, headHw, heading);
                    posX = hx + (float)Math.Cos(heading) * headHw; posY = hy + (float)Math.Sin(heading) * headHw;
                    float used = rootLen + line1 + tail + headHw * 1.6f;
                    float endHw = Px(rng.LogNormal(ch[ChainEndW], 0.55f, 1.5f, 15f)) / 2;
                    float line2 = Math.Max(Px(4), span - used - endHw * 2.5f);
                    float wl2 = wl * rng.Uniform(0.6f, 0.9f);
                    Walk(line2, t => wl2, 5);
                    Walk(endHw * 1.5f, t => wl2 + (endHw - wl2) * (float)Math.Pow(t, 1.4), 3);
                    chains.Disc(rng, posX + (float)Math.Cos(heading) * endHw * 0.8f, posY + (float)Math.Sin(heading) * endHw * 0.8f, endHw * 1.3f, endHw, heading);
                }
                else
                {
                    // short chain: the root narrows into a short line, then widens straight into its head
                    float rootLen = span * rng.Uniform(0.35f, 0.5f);
                    float tail = span * rng.Uniform(0.25f, 0.4f);
                    float line1 = Math.Max(0f, span - rootLen - tail - headHw * 1.2f);
                    float hh = headHw;
                    Walk(rootLen, t => wl + (rootHw - wl) * (float)Math.Pow(1 - t, 2.2), 8);
                    if (line1 > Px(1)) Walk(line1, t => wl, 3);
                    Walk(tail, t => wl + (hh - wl) * (float)Math.Pow(t, 1.6), 5);
                    float hx = posX + (float)Math.Cos(heading) * headHw * 0.6f, hy = posY + (float)Math.Sin(heading) * headHw * 0.6f;
                    chains.Disc(rng, hx, hy, headHw * 1.15f, headHw, heading);
                }
                // a few side droplets along the chain
                int nSide = rng.Integers(1, 5);
                for (int i = 0; i < nSide; i++)
                {
                    float f = rng.Uniform(0.3f, 1.0f);
                    float sx = xa + (float)Math.Cos(th) * span * f + (float)Math.Cos(th + Tau / 4) * Rc * rng.Uniform(-0.15f, 0.15f);
                    float sy = ya + (float)Math.Sin(th) * span * f + (float)Math.Sin(th + Tau / 4) * Rc * rng.Uniform(-0.15f, 0.15f);
                    side.Disc(rng, sx, sy, Px(rng.Uniform(1.0f, 2.5f)));
                }
            }

            // --- stubs: short pointed fingers around the rim, the crown between the chains
            int stubsN = ch.I(StubsN);
            for (int i = 0; i < stubsN; i++)
            {
                float th = rng.Uniform(0, Tau);
                var (xa, ya, _) = OutlinePoint(th, 0.85f);
                float L = Rc * rng.Uniform(0.08f, 0.28f), hw = Rc * rng.Uniform(0.03f, 0.07f);
                float th2 = th + rng.Normal(0, 0.1f);
                stubs.Capsule(rng, xa, ya, xa + (float)Math.Cos(th2) * L, ya + (float)Math.Sin(th2) * L, hw, hw * rng.Uniform(0.3f, 0.7f));
            }

            // --- 3. the web: a lobed sheet out to the reach with a lumpy hole at every seed and doors
            // cut through the walls where the patch field exceeds the keep profile
            float sIn = ch[WebSpaceIn], sOut = ch[WebSpaceOut], reachRc = ch[WebReach];
            R.WebFrom = ch[WebFrom];
            var sheet = new Polar { Aspect = 1f, Axis = 0f };
            float[] sheetK = { 3, 5, 7, 11, 17, 23, 0, 0 }; float[] sheetA = { 0.5f, 0.6f, 0.6f, 0.5f, 0.4f, 0.3f, 0f, 0f };
            for (int i = 0; i < 8; i++) { sheet.K[i] = sheetK[i]; sheet.Amp[i] = sheetA[i]; sheet.Ph[i] = rng.Uniform(0, Tau); }
            sheet.Normalise();
            sheet.Base = Rc * reachRc * 0.95f; sheet.Span = Rc * reachRc * 0.10f;
            R.Sheet = sheet;
            var holes = new Part(0.14f, 0f);
            List<(float x, float y)> seeds = WebSeeds(rng, Rc, ch[WebFrom] - 0.1f, reachRc * 1.05f + 0.5f, sIn, sOut);
            List<(int a, int b)> edges = SplatVoronoi.Edges(seeds);
            var nn = new float[seeds.Count]; for (int i = 0; i < nn.Length; i++) nn[i] = float.MaxValue;
            foreach (var (a, b) in edges)
            {
                float d = Hypot(seeds[a].x - seeds[b].x, seeds[a].y - seeds[b].y);
                if (d < nn[a]) nn[a] = d; if (d < nn[b]) nn[b] = d;
            }
            var holeR = new float[seeds.Count];
            for (int i = 0; i < seeds.Count; i++)
            {
                float qi = Hypot(seeds[i].x, seeds[i].y) / Rc;
                float w = Px(ch[WebStrandW] * Interp(qi, new[] { 1.0f, 1.6f }, new[] { 1.0f, 0.6f })) * rng.LogNormal(1f, 0.35f, 0.4f, 2.5f);
                float nnI = nn[i] < float.MaxValue ? nn[i] : Rc * sIn;
                float rI = Math.Max(Px(0.7f), 0.5f * nnI - w / 2);
                holeR[i] = rI;
                holes.Disc(rng, seeds[i].x, seeds[i].y, rI * rng.Uniform(0.9f, 1.25f), rI, rng.Uniform(0, Tau));
            }
            // the patch field at every wall's midpoint, rank-normalised over the walls (the Python
            // ranked the whole field; the walls are a fair sample of it)
            var field = new float[edges.Count]; var order = new int[edges.Count];
            int fSeed = rng.Next(); float patch = ch[WebPatch] * Rc;
            for (int i = 0; i < edges.Count; i++)
            {
                var (a, b) = edges[i];
                float mx = (seeds[a].x + seeds[b].x) / 2, my = (seeds[a].y + seeds[b].y) / 2;
                field[i] = SplatNoise.Value(mx, my, patch, fSeed) + 0.5f * SplatNoise.Value(mx, my, patch * 0.5f, fSeed + 7);
                order[i] = i;
            }
            Array.Sort((float[])field.Clone(), order);
            var rank = new float[edges.Count];
            for (int i = 0; i < order.Length; i++) rank[order[i]] = edges.Count > 1 ? (float)i / (edges.Count - 1) : 0.5f;
            float keepScale = ch[WebKeep];
            for (int i = 0; i < edges.Count; i++)
            {
                var (a, b) = edges[i];
                float mx = (seeds[a].x + seeds[b].x) / 2, my = (seeds[a].y + seeds[b].y) / 2;
                float qm = Hypot(mx, my) / Rc;
                float wf = Interp(qm, new[] { 1.0f, 2.1f }, new[] { 0.75f, 0.15f });   // the patch field rules near the core, chance at the rim
                float keep = Interp(qm, KeepQ, KeepP) * keepScale;
                if (qm > reachRc || wf * rank[i] + (1 - wf) * rng.Uniform(0, 1) > keep)
                {
                    float hw = 0.5f * (holeR[a] + holeR[b]) * rng.Uniform(0.9f, 1.15f);   // as wide as the holes it joins: a merged pocket
                    holes.Capsule(rng, seeds[a].x, seeds[a].y, seeds[b].x, seeds[b].y, hw, hw);
                }
            }

            // --- 4. drops on lines, with trails and children
            float DropShape(float diam)
            {
                if (diam < 0.09f * Rc) return rng.Uniform(1.0f, 1.15f);
                if (diam < 0.30f * Rc) return ch[TeardropAspect] * rng.Uniform(0.85f, 1.15f);
                return ch[BigAspect] * rng.Uniform(0.9f, 1.1f);
            }
            float TrailFill(float distRc)
            {
                float near = ch[TrailFillNear], far = ch[TrailFillFar];
                float f = distRc <= 2.6f ? near : (distRc >= 3.5f ? far : near + (far - near) * (distRc - 2.6f) / 0.9f);
                return f * rng.Uniform(0.8f, 1.2f);
            }
            void Trail(float xa, float ya, float xb, float yb, float distRc, float endHw, float startHw, bool neck)
            {
                // pieces and gaps from the parent to the drop's back; the painted share is the
                // measured fill for this distance. Everything scales with the DROP IT FEEDS: a hair
                // between, about a third of that drop's width, and a wide, short, concave mouth
                // onto each splat at both ends (a measured trait).
                float L = Hypot(xb - xa, yb - ya);
                if (L < Px(2)) return;
                float ex = (xb - xa) / L, ey = (yb - ya) / L;
                float fill = TrailFill(distRc);
                int n = L < 0.2f * Rc ? 1 : rng.Integers(2, 6);
                var pieces = new float[n]; float psum = 0f;
                for (int i = 0; i < n; i++) { pieces[i] = rng.LogNormal(ch[PieceMed], 0.8f, 0.02f, 0.5f); psum += pieces[i]; }
                for (int i = 0; i < n; i++) pieces[i] = pieces[i] / psum * fill * L;
                float[] gaps = rng.Dirichlet(n + 1, 0.8f);
                for (int i = 0; i <= n; i++) gaps[i] *= (1 - fill) * L;
                gaps[0] *= rng.Uniform(0f, 0.6f);      // the neck starts near the parent
                gaps[n] *= rng.Uniform(0f, 0.4f);      // and the last piece flares into the drop
                float gsum = 0f; for (int i = 0; i <= n; i++) gsum += gaps[i];
                float rest = (1 - fill) * L - gsum;
                if (n > 1) for (int i = 1; i < n; i++) gaps[i] += rest / (n - 1);
                else { gaps[0] += rest / 2; gaps[n] += rest / 2; }
                float dropW = 2 * endHw;
                float hwMid = Clamp(ch[TrailWK] * dropW / 2 * rng.Uniform(0.7f, 1.3f), Px(ch[TrailWMin]) / 2, Px(ch[TrailWMax]) / 2);
                float mouthB = Math.Max(hwMid, endHw * rng.Uniform(0.7f, 0.95f));
                float mouthA = startHw > 0 ? Math.Max(hwMid, startHw * rng.Uniform(0.7f, 0.95f))
                                           : Math.Max(hwMid, Math.Min(ch[CoreRoot] * Rc, mouthB * rng.Uniform(1.2f, 2.2f)));
                float flB = Math.Min(0.25f * L, Math.Max(Px(2.5f), dropW * rng.Uniform(0.6f, 1.0f)));
                float flA = neck ? Math.Min(0.3f * L, Math.Max(Px(2.5f), 2 * mouthA * rng.Uniform(0.8f, 1.4f))) : 0f;
                float HwAt(float t)
                {
                    float dFromA = t * L, dToB = (1 - t) * L, w = hwMid;
                    if (dToB < flB) w = Math.Max(w, hwMid + (mouthB - hwMid) * (float)Math.Pow(1 - dToB / flB, 2.0));
                    if (neck && dFromA < flA) w = Math.Max(w, hwMid + (mouthA - hwMid) * (float)Math.Pow(1 - dFromA / flA, 2.0));
                    return w;
                }
                float pos = gaps[0];
                for (int i = 0; i < n; i++)
                {
                    float p = pieces[i];
                    float t0 = pos / L, t1 = Math.Min(1f, (pos + p) / L);
                    trails.Capsule(rng, xa + ex * pos, ya + ey * pos, xa + ex * (pos + p), ya + ey * (pos + p), HwAt(t0), HwAt(t1));
                    if (p < Px(3)) trails.Disc(rng, xa + ex * (pos + p / 2), ya + ey * (pos + p / 2), Math.Max(HwAt(t0) * 1.2f, Px(0.8f)));
                    pos += p + gaps[i + 1];
                }
                void PaintMouth(float tFrom, float tTo, float backA, float fwdB)
                {
                    const int steps = 6;
                    for (int k = 0; k < steps; k++)
                    {
                        float t0 = tFrom + (tTo - tFrom) * k / steps, t1 = tFrom + (tTo - tFrom) * (k + 1) / steps;
                        float p0 = t0 * L - (k == 0 ? backA : 0f), p1 = t1 * L + (k == steps - 1 ? fwdB : 0f);
                        trails.Capsule(rng, xa + ex * p0, ya + ey * p0, xa + ex * p1, ya + ey * p1, HwAt(t0), HwAt(t1));
                    }
                }
                if (neck) PaintMouth(0f, flA / L, (startHw > 0 ? startHw : mouthA) * 0.4f, 0f);
                PaintMouth(1f - flB / L, 1f, 0f, endHw * 0.35f);
            }
            int placed = 0;
            void PlaceDrop(float x, float y, float diam, float direction, float distRc, bool hasParent, float parentX, float parentY, float parentHw, bool neck, int gen)
            {
                float aspect = DropShape(diam);
                float ry = diam / 2 / (float)Math.Sqrt(aspect), rx = ry * aspect;
                drops.Disc(rng, x, y, rx, ry, direction);
                if (diam >= ch[TrailMinDiam] * Rc && hasParent)
                    Trail(parentX, parentY, x - (float)Math.Cos(direction) * rx, y - (float)Math.Sin(direction) * ry, distRc, ry, parentHw, neck);
                placed++;
                // children: a big drop throws on, in its own direction
                if (diam >= ch[ChildMinDiam] * Rc)
                {
                    int cn = ch.I(ChildN);
                    for (int i = 0; i < cn; i++)
                    {
                        float d2 = direction + Deg(rng.Normal(0, ch[ChildCone] / 2));
                        float cd = diam * ch[ChildSize] * rng.Uniform(0.8f, 1.2f);
                        float dd = Rc * ch[ChildDist] * rng.Uniform(0.7f, 1.3f);
                        float x2 = x + (float)Math.Cos(d2) * dd, y2 = y + (float)Math.Sin(d2) * dd;
                        float dist2 = Hypot(x2, y2) / Rc;
                        if (dist2 <= ch[DropDistMax])
                            PlaceDrop(x2, y2, cd, d2, dist2, true, x + (float)Math.Cos(direction) * rx, y + (float)Math.Sin(direction) * ry, ry, true, gen + 1);
                    }
                }
            }
            int nTarget = ch.I(DropsN);
            int nLines = Math.Max(8, (int)Math.Round(nTarget / (1.0f + ch[LineLenMean])));
            var lineDirs = EvenDirections(nLines, 0.45f);
            lineDirs.AddRange(SectorDirections((int)Math.Round(nLines * ch[SectorExtra])));
            rng.Shuffle(lineDirs);
            int guard = 0;
            while (placed < nTarget && guard < 400)
            {
                float th = lineDirs[guard % lineDirs.Count] + (guard >= lineDirs.Count ? rng.Normal(0, Deg(3)) : 0f);
                guard++;
                int nLine = Math.Min(6, 1 + (int)rng.Exponential(ch[LineLenMean]));
                float d = rng.LogNormal(ch[DropDistMed], 0.25f, 1.5f, ch[DropDistMax]);
                var (xa, ya, _) = OutlinePoint(th, 1.0f);
                float parentX = xa, parentY = ya, parentHw = 0f;
                bool neck = rng.Chance(ch[CoreNeckP]);          // only some lines drag a neck out of the core
                for (int i = 0; i < nLine; i++)
                {
                    if (d > ch[DropDistMax]) break;
                    float diam = Rc * rng.LogNormal(ch[DropDiamMed], 0.5f, 0.05f, ch[DropDiamCap]);
                    float thI = th + rng.Normal(0, Deg(2.5f));
                    float x = (float)Math.Cos(thI) * d * Rc, y = (float)Math.Sin(thI) * d * Rc;
                    PlaceDrop(x, y, diam, thI, d, true, parentX, parentY, parentHw, neck, 0);
                    neck = false;                                // later drops on the line: hair + own tail only
                    float aspect = DropShape(diam);
                    parentX = x + (float)Math.Cos(thI) * diam / 2 * (float)Math.Sqrt(aspect) * 0.9f;
                    parentY = y + (float)Math.Sin(thI) * diam / 2 * (float)Math.Sqrt(aspect) * 0.9f;
                    parentHw = diam / 2 / (float)Math.Sqrt(aspect);
                    d += rng.LogNormal(ch[LineSpacingMed], 0.55f, 0.15f, 1.2f);
                }
            }
            R.DropsPlaced = placed;

            // --- spray: hundreds of tiny droplets, evenly spread in angle, density peaking around
            // spray_peak; a little elongated radially when bigger
            var spray = new Part(0.10f, 0f);
            int nSpray = ch.I(SprayN);
            foreach (float a in EvenDirections(nSpray, 0.5f))
            {
                float r = rng.LogNormal(ch[SprayPeak], ch[SpraySpread] * 0.6f, 1.0f, 4.6f);
                float diam = Px(rng.LogNormal(ch[SprayDiam], 0.4f, 1.2f, 9.0f));
                float aspS = 1.0f + (diam / Px(4)) * rng.Uniform(0f, 0.5f);
                spray.Disc(rng, (float)Math.Cos(a) * r * Rc, (float)Math.Sin(a) * r * Rc, diam / 2 * aspS, diam / 2 / aspS, a);
            }

            // side droplets and hairs join the drops part (all drawn plain, no rounding)
            foreach (var p in side.Prims) { var q = p; drops.Prims.Add(q); q.Bounds(ref drops.X0, ref drops.Y0, ref drops.X1, ref drops.Y1); }
            foreach (var p in trails.Prims) { var q = p; drops.Prims.Add(q); q.Bounds(ref drops.X0, ref drops.Y0, ref drops.X1, ref drops.Y1); }

            R.Stubs = stubs; R.Chains = chains; R.Holes = holes; R.Drops = drops; R.Spray = spray;
            // colour field parameters (px)
            R.ColCell = ch[ColCell] * Rc; R.ColHueCell = ch[ColHueCell] * Rc;
            R.ColVTop = ch[ColVTop]; R.ColSpread = ch[ColSpread]; R.ColSTop = ch[ColSTop]; R.ColTint = ch[ColTint]; R.ColHueSpread = ch[ColHueSpread];
            // body bounds: the sheet's reach, the drops, the spray
            float reachPx = Rc * reachRc * 1.05f + Px(2);
            R.BodyX0 = Math.Min(-reachPx, Math.Min(drops.X0, spray.X0)); R.BodyY0 = Math.Min(-reachPx, Math.Min(drops.Y0, spray.Y0));
            R.BodyX1 = Math.Max(reachPx, Math.Max(drops.X1, spray.X1)); R.BodyY1 = Math.Max(reachPx, Math.Max(drops.Y1, spray.Y1));
            return R;
        }

        static float Deg(float d) => d * Tau / 360f;

        /// <summary>Dart-thrown seeds in the annulus qLo..qHi (Rc) with a minimum spacing that
        /// grows from sIn at 1.0 Rc to sOut at 2.4 Rc (fractions of Rc); grid-hashed rejection.</summary>
        static List<(float x, float y)> WebSeeds(SplatRng rng, float Rc, float qLo, float qHi, float sIn, float sOut)
        {
            var pts = new List<(float x, float y)>(512);
            var grid = new Dictionary<(int, int), List<(float x, float y)>>(1024);
            float cell = Rc * sIn * 0.8f;
            float area = (float)Math.PI * (qHi * qHi - qLo * qLo) * Rc * Rc;
            int tries = (int)(area / ((Rc * sIn) * (Rc * sIn)) * 14);
            for (int n = 0; n < tries; n++)
            {
                float r = (float)Math.Sqrt(rng.Uniform(qLo * qLo, qHi * qHi)); float a = rng.Uniform(0, Tau);
                float x = (float)Math.Cos(a) * r * Rc, y = (float)Math.Sin(a) * r * Rc;
                float s = Rc * (sIn + (sOut - sIn) * Clamp((r - 1.0f) / 1.4f, 0f, 1f)) * 0.85f;
                int gx = (int)Math.Floor(x / cell), gy = (int)Math.Floor(y / cell); int rad = (int)Math.Ceiling(s / cell); bool ok = true;
                for (int i = gx - rad; i <= gx + rad && ok; i++)
                    for (int j = gy - rad; j <= gy + rad && ok; j++)
                        if (grid.TryGetValue((i, j), out var bucket))
                            foreach (var (px, py) in bucket)
                                if ((px - x) * (px - x) + (py - y) * (py - y) < s * s) { ok = false; break; }
                if (ok)
                {
                    pts.Add((x, y));
                    if (!grid.TryGetValue((gx, gy), out var b)) grid[(gx, gy)] = b = new List<(float x, float y)>(4);
                    b.Add((x, y));
                }
            }
            return pts;
        }
    }

    /// <summary>Deterministic random draws for the splat rules, on System.Random.</summary>
    public sealed class SplatRng
    {
        readonly Random r;
        public SplatRng(int seed) { r = new Random(seed); }
        public int Next() => r.Next();
        public float Uniform(float lo, float hi) => lo + (float)r.NextDouble() * (hi - lo);
        public bool Chance(float p) => r.NextDouble() < p;
        public float Sign() => r.NextDouble() < 0.5 ? -1f : 1f;
        /// <summary>Integer in [lo, hi).</summary>
        public int Integers(int lo, int hi) => r.Next(lo, hi);
        public float Normal(float mu, float sigma)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return mu + sigma * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
        public float LogNormal(float median, float sigma, float lo, float hi)
        {
            float v = (float)Math.Exp(Math.Log(median) + sigma * Normal(0, 1));
            return v < lo ? lo : (v > hi ? hi : v);
        }
        public float Exponential(float mean) => -mean * (float)Math.Log(1.0 - r.NextDouble());
        public int Choice3(float p0, float p1, float p2) { double u = r.NextDouble() * (p0 + p1 + p2); return u < p0 ? 0 : (u < p0 + p1 ? 1 : 2); }
        public int Choice(float[] weights)
        {
            double tot = 0; foreach (float w in weights) tot += w;
            double u = r.NextDouble() * tot;
            for (int i = 0; i < weights.Length; i++) { u -= weights[i]; if (u < 0) return i; }
            return weights.Length - 1;
        }
        public int[] Permutation(int n)
        {
            var p = new int[n]; for (int i = 0; i < n; i++) p[i] = i;
            for (int i = n - 1; i > 0; i--) { int j = r.Next(i + 1); (p[i], p[j]) = (p[j], p[i]); }
            return p;
        }
        public void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        }
        /// <summary>Symmetric Dirichlet(alpha) of n parts, via gamma draws (Marsaglia-Tsang, with
        /// the alpha-below-one boost).</summary>
        public float[] Dirichlet(int n, float alpha)
        {
            var g = new float[n]; float sum = 0f;
            for (int i = 0; i < n; i++) { g[i] = Gamma(alpha); sum += g[i]; }
            if (sum <= 0f) { for (int i = 0; i < n; i++) g[i] = 1f / n; return g; }
            for (int i = 0; i < n; i++) g[i] /= sum;
            return g;
        }
        float Gamma(float a)
        {
            if (a < 1f) return Gamma(a + 1f) * (float)Math.Pow(r.NextDouble(), 1.0 / a);
            double d = a - 1.0 / 3.0, c = 1.0 / Math.Sqrt(9.0 * d);
            for (int guard = 0; guard < 100; guard++)
            {
                double x = Normal(0, 1), v = 1.0 + c * x;
                if (v <= 0) continue;
                v = v * v * v; double u = r.NextDouble();
                if (u < 1.0 - 0.0331 * x * x * x * x) return (float)(d * v);
                if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return (float)(d * v);
            }
            return (float)d;
        }
    }

    /// <summary>Lattice value noise with smooth interpolation, for the CPU side of the rules
    /// (the shader has its own copy of the same function for the colour field).</summary>
    public static class SplatNoise
    {
        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }
        public static float Value(float x, float y, float cell, int seed)
        {
            float fx = x / cell, fy = y / cell;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
        }
    }

    /// <summary>Voronoi neighbour pairs of a point set = the Delaunay edges, by Bowyer-Watson.
    /// Hull edges of the triangulation are kept too (the web only needs neighbour pairs).</summary>
    public static class SplatVoronoi
    {
        struct Tri { public int A, B, C; public double Cx, Cy, R2; }

        static bool Circum(double ax, double ay, double bx, double by, double cx, double cy, out double ux, out double uy, out double r2)
        {
            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            ux = uy = r2 = 0;
            if (Math.Abs(d) < 1e-9) return false;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            r2 = (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy);
            return true;
        }

        public static List<(int a, int b)> Edges(List<(float x, float y)> pts)
        {
            var outE = new List<(int, int)>(pts.Count * 3);
            int n = pts.Count;
            if (n < 4) return outE;
            var P = new List<(double x, double y)>(n + 3);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts) { P.Add((p.x, p.y)); minX = Math.Min(minX, p.x); minY = Math.Min(minY, p.y); maxX = Math.Max(maxX, p.x); maxY = Math.Max(maxY, p.y); }
            double mx = (minX + maxX) / 2, my = (minY + maxY) / 2, d = Math.Max(maxX - minX, maxY - minY) * 20 + 10;
            P.Add((mx - d, my - d * 0.6)); P.Add((mx + d, my - d * 0.6)); P.Add((mx, my + d));
            var tris = new List<Tri>(n * 2 + 8);
            {
                Circum(P[n].x, P[n].y, P[n + 1].x, P[n + 1].y, P[n + 2].x, P[n + 2].y, out double ux, out double uy, out double r2);
                tris.Add(new Tri { A = n, B = n + 1, C = n + 2, Cx = ux, Cy = uy, R2 = r2 });
            }
            var bad = new List<int>(64);
            var edgeCount = new Dictionary<(int, int), int>(128);
            var keep = new List<Tri>(n * 2 + 8);
            for (int pi = 0; pi < n; pi++)
            {
                double x = P[pi].x, y = P[pi].y;
                bad.Clear(); edgeCount.Clear();
                for (int t = 0; t < tris.Count; t++)
                {
                    var tr = tris[t];
                    if ((tr.Cx - x) * (tr.Cx - x) + (tr.Cy - y) * (tr.Cy - y) < tr.R2) bad.Add(t);
                }
                foreach (int t in bad)
                {
                    var tr = tris[t];
                    Count(edgeCount, tr.A, tr.B); Count(edgeCount, tr.B, tr.C); Count(edgeCount, tr.C, tr.A);
                }
                keep.Clear();
                int bi = 0;
                for (int t = 0; t < tris.Count; t++)
                {
                    if (bi < bad.Count && bad[bi] == t) { bi++; continue; }
                    keep.Add(tris[t]);
                }
                tris.Clear(); tris.AddRange(keep);
                foreach (var kv in edgeCount)
                {
                    if (kv.Value != 1) continue;
                    var (i, j) = kv.Key;
                    if (Circum(P[i].x, P[i].y, P[j].x, P[j].y, x, y, out double ux, out double uy, out double r2))
                        tris.Add(new Tri { A = i, B = j, C = pi, Cx = ux, Cy = uy, R2 = r2 });
                }
            }
            var seen = new HashSet<(int, int)>();
            foreach (var tr in tris)
            {
                if (tr.A >= n || tr.B >= n || tr.C >= n) continue;
                AddEdge(outE, seen, tr.A, tr.B); AddEdge(outE, seen, tr.B, tr.C); AddEdge(outE, seen, tr.C, tr.A);
            }
            return outE;
        }

        static void Count(Dictionary<(int, int), int> d, int a, int b)
        {
            var k = a < b ? (a, b) : (b, a);
            d.TryGetValue(k, out int c); d[k] = c + 1;
        }
        static void AddEdge(List<(int, int)> outE, HashSet<(int, int)> seen, int a, int b)
        {
            var k = a < b ? (a, b) : (b, a);
            if (seen.Add(k)) outE.Add(k);
        }
    }

    /// <summary>
    /// A caller's ranges for any of the rules, by rule: every rule not named keeps its measured
    /// range. Pass one to <see cref="SplatRules.Generate"/> or <see cref="SplatComposition.Generate"/>
    /// to change the character of every splat drawn with it, such as more drops, longer chains,
    /// or a tighter web, without touching the table.
    /// </summary>
    public sealed class SplatRuleOverrides
    {
        readonly Dictionary<string, (float lo, float hi)> ranges = new Dictionary<string, (float, float)>();

        public SplatRuleOverrides Set(SplatRules.Rule rule, float lo, float hi) { ranges[rule.Name] = (lo, hi); return this; }
        /// <summary>Pin a rule to one value.</summary>
        public SplatRuleOverrides Set(SplatRules.Rule rule, float value) => Set(rule, value, value);
        /// <summary>Scale a rule's measured range by a factor, keeping its shape.</summary>
        public SplatRuleOverrides Scale(SplatRules.Rule rule, float factor) => Set(rule, rule.Lo * factor, rule.Hi * factor);
        public bool TryGet(SplatRules.Rule rule, out (float lo, float hi) range) => ranges.TryGetValue(rule.Name, out range);
        public int Count => ranges.Count;
    }
}
