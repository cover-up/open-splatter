using System;
using System.Collections.Generic;
using static CoverUp.Splatter.SplatRules;

namespace CoverUp.Splatter
{
    /// <summary>
    /// A palette: how many hues a composition draws and from where, the saturation and value
    /// multipliers on every splat, and the chance of one white splat. <see cref="Rainbow"/>
    /// draws from a fixed bank of ten neon hues with front-layer weights; <see cref="Cool"/> and
    /// <see cref="Muted"/> spread a handful of hues evenly over a range. Make your own by
    /// setting the fields, or by starting from a preset.
    /// </summary>
    public sealed class SplatPalette
    {
        public string Name = "Custom";
        /// <summary>How many hues are drawn when there is no <see cref="Bank"/>.</summary>
        public int CountLo = 5, CountHi = 8;
        /// <summary>The hue range those are spread over, degrees.</summary>
        public float HueLo = 0f, HueHi = 360f;
        /// <summary>Saturation and value multipliers on every splat.</summary>
        public float SMul = 1f, VMul = 1f;
        /// <summary>The chance of one white splat among the mid-sized cores.</summary>
        public float WhiteChance = 0f;
        /// <summary>Fixed hues instead of a range: (hue, presence out of five, share of the front
        /// layer). A hue with a presence of two or less drops out half the time, and every hue
        /// jitters by a few degrees, so no two seeds share a palette exactly.</summary>
        public (float hue, int presence, float weight)[] Bank;

        public static SplatPalette Rainbow => new SplatPalette { Name = "Rainbow", Bank = NeonBank };
        public static SplatPalette Cool => new SplatPalette { Name = "Cool", CountLo = 4, CountHi = 6, HueLo = 85, HueHi = 335, SMul = 0.98f, VMul = 0.95f, WhiteChance = 1f };
        public static SplatPalette Muted => new SplatPalette { Name = "Muted", CountLo = 4, CountHi = 5, SMul = 0.85f, VMul = 0.62f };

        // The neon set measured on the rainbow references, with how many of the five frames
        // carried each hue and the share of the front layer it took.
        static readonly (float hue, int presence, float weight)[] NeonBank =
        {
            (20, 5, 0.15f), (60, 3, 0.07f), (78, 2, 0.04f), (108, 4, 0.13f), (138, 2, 0.03f),
            (190, 4, 0.12f), (208, 5, 0.04f), (250, 4, 0.06f), (278, 2, 0.03f), (325, 5, 0.33f),
        };
    }

    /// <summary>
    /// A whole painting: the composition rules, measured on reference paintings, produce an
    /// ordered list of paint items for <see cref="SplatCanvas"/>. Pure C#, thread-safe,
    /// deterministic per seed.
    ///
    /// Layering is one order for everything, as paint thrown at a canvas one colour at a time:
    /// each splat's chains, body and spray go down together in its slot, followed by its own
    /// throws and any throw head that lands inside the band as a small splat of its own; the halo
    /// spatter slots in by its hue. The bed (large, dark, violet) goes first, the warm colours
    /// last, as measured from which colours' drops lie on which.
    /// </summary>
    public sealed class SplatComposition
    {
        public int Seed; public SplatPalette Palette; public int Width, Height;
        public float Scale;                            // display px per measurement-frame px (sizes)
        public float SMul = 1f, VMul = 1f;             // the palette's saturation and value multipliers
        /// <summary>The rule ranges this composition and every splat it adds draw from; null is the table.</summary>
        public SplatRuleOverrides Overrides;
        public readonly List<float> Hues = new List<float>(10);
        public readonly List<float> Weights = new List<float>(10);
        public readonly List<Item> Items = new List<Item>(64);
        /// <summary>The cores on the canvas (bed and front), for a living painting's pushes.</summary>
        public readonly List<CoreInfo> Cores = new List<CoreInfo>(32);
        public int CoreCount, BedCount, ThrowCount;
        int nextGroup = 1;
        float pSprayDrawn, pChainDrawn;

        public sealed class CoreInfo { public float X, Y, Rc, Hue; public bool Bed, White; public int Group; }

        public enum ItemKind { Splat, Pieces }
        public sealed class Item
        {
            public ItemKind Kind;
            /// <summary>Items that arrived together (a core with its throws and heads, or a spatter
            /// drop) share a group; a living painting pushes and pops whole groups. The bed is
            /// group 0 and never popped.</summary>
            public int Group;
            public SplatRecipe Recipe;                  // Splat
            public float X, Y;                          // canvas px, y up
            public bool Bed, Mini;
            public bool Hole;                           // Splat: erased instead of painted (a hole to the background)
            public Part Pieces; public float Hue, SMul, VMul;   // Pieces: throws or a spatter drop, flat colour with a per-piece value
        }

        // --- composition rules: (measured, lo, hi), drawn once per composite
        static readonly Rule
            CoresN = new Rule("cores_n", 16, 13, 20, true),
            RcMed = new Rule("rc_med", 42.6f, 38, 48),                 // frame px; lognormal sigma 0.34, clip 24-101
            SepLo = new Rule("sep_lo", 1.0f, 0.95f, 1.1f), SepHi = new Rule("sep_hi", 1.3f, 1.2f, 1.4f),  // min centre distance / (Rc_i + Rc_j), drawn per core
            DominantW = new Rule("dominant_w", 0.45f, 0.30f, 0.60f),   // cool/muted only: share of the front given to one hue
            ThrowsN = new Rule("throws_n", 40, 30, 50, true),          // the references' halo carries about 25 visible radial tails per frame
            ThrowHeadR = new Rule("throw_head_r", 15, 13, 17),         // frame px; lognormal sigma 0.25, clip 12-24
            ThrowDist = new Rule("throw_dist", 4.5f, 3.8f, 5.2f),      // Rc of the source; lognormal sigma 0.45, clip 2-7.5
            ThrowSigmaDeg = new Rule("throw_sigma_deg", 21, 16, 26),   // spread around the outward direction from the frame centre
            ThrowLineHw = new Rule("throw_line_hw", 1.8f, 1.4f, 2.2f), // frame px half-width of the tail line
            // small frame-radial teardrops in the halo (measured: ~43 per image, head width 7-16 px,
            // length 19-57 px, 75 % within 25 deg of the radial from the frame centre)
            SpatterN = new Rule("spatter_n", 43, 30, 60, true),
            SpatterHeadR = new Rule("spatter_head_r", 5.5f, 4.5f, 6.5f),   // frame px; lognormal sigma 0.3, clip 3-9
            SpatterLen = new Rule("spatter_len", 2.5f, 2.0f, 3.0f),        // x head diameter; lognormal sigma 0.35, clip 1.6-5.5
            SpatterR = new Rule("spatter_r", 540, 480, 600),               // frame px from the centre along x; lognormal sigma 0.28, clip 280-780
            SpatterSigmaDeg = new Rule("spatter_sigma_deg", 18, 14, 22),
            // spray landing on paint: the references carry drops on other colours half as densely as
            // on black (2.0x for 1-12 px2, 2.5x for 13-60, 1.6x for 61-400); a dot whose centre lands
            // on paint already down is kept with this probability
            SprayOnPaint = new Rule("spray_on_paint", 0.45f, 0.35f, 0.55f),
            // a chain or throw lying over paint already down is thinned by a pixel and faded to this
            ChainOnPaint = new Rule("chain_on_paint", 0.75f, 0.65f, 0.85f),
            // draw order by colour family (other colours' drops lie on blue and purple 2x more often
            // than the reverse, cyan 1.4x, pink 0.8x, warm 0.4-0.5x): a 2:1 tendency, so the rank
            // gets this much normal noise
            LayerNoise = new Rule("layer_noise", 0.8f, 0.6f, 1.0f),
            // the bed: violet splats thrown first and mostly buried (the references' cool paint showing
            // through between other colours measures hue 240, V 0.72 with a tail to 0.29, S 0.98;
            // a reference's band is only 9 % black because the bed shows through every hole)
            BedN = new Rule("bed_n", 8, 7, 9, true),
            BedRc = new Rule("bed_rc", 95, 85, 105),                       // frame px; lognormal sigma 0.15, clip 70-130
            BedV = new Rule("bed_v", 0.72f, 0.64f, 0.80f),                 // value multiplier on bed splats
            BedShade = new Rule("bed_shade", 4.0f, 3.0f, 5.0f);            // shade-spread multiplier on bed splats
        const float SameHueDeg = 25f;
        const float BandX0 = 0.20f, BandX1 = 0.82f, BandY0 = 0.28f, BandY1 = 0.69f;

        static float LayerRank(float hue)
        {
            float h = ((hue % 360) + 360) % 360;
            if (h >= 195 && h < 290) return 0f;   // blue, violet, purple: the bed's family
            if (h >= 160 && h < 195) return 1f;   // cyan
            if (h >= 290) return 2f;              // pink
            return 3f;                            // red, orange, yellow, green: on top
        }
        static float CircDiff(float a, float b) { float d = Math.Abs((a - b) % 360f); return Math.Min(d, 360f - d); }
        static float Hypot(float a, float b) => (float)Math.Sqrt(a * a + b * b);
        const float Tau = SplatRules.Tau;

        sealed class Core { public float X, Y, Rc, Hue; public bool White, Bed; public int Seed; public readonly List<int> Throws = new List<int>(4); }
        sealed class Throw { public int Src; public float Hue, HeadR, Dist, Ang, X, Y, LineHw, Bend, TailLen, Aspect, V; public int Pieces, Children, Spray; public bool EndDrop, Connected; }
        sealed class Spatter { public float Hue, X, Y, HeadR, Length, Ang, V; }

        /// <summary>Compose a painting of <paramref name="width"/> x <paramref name="height"/> display px
        /// in <paramref name="palette"/> (null: <see cref="SplatPalette.Rainbow"/>), with the rules'
        /// measured ranges or the caller's <paramref name="overrides"/>.</summary>
        public static SplatComposition Generate(int seed, SplatPalette palette, int width, int height, SplatRuleOverrides overrides = null)
        {
            var rng = new SplatRng(seed);
            var sc = palette ?? SplatPalette.Rainbow;
            var o = overrides;
            var C = new SplatComposition { Seed = seed, Palette = sc, Width = width, Height = height, SMul = sc.SMul, VMul = sc.VMul, Overrides = overrides };
            float scale = Math.Min(width / FrameW, height / FrameH);
            C.Scale = scale;
            float fcx = width / 2f, fcy = height / 2f;

            // --- palette
            var hues = new List<float>(10); var weights = new List<float>(10);
            if (sc.Bank != null)
            {
                // the whole bank every time; its rarest hues each drop out half the time; front
                // weights fixed per hue so the band comes out in the measured proportions
                foreach (var (hue, count, frontW) in sc.Bank)
                {
                    if (count <= 2 && rng.Chance(0.5f)) continue;
                    hues.Add(((hue + rng.Uniform(-5, 5)) % 360 + 360) % 360); weights.Add(frontW * rng.Uniform(0.8f, 1.2f));
                }
            }
            else
            {
                int n = rng.Integers(sc.CountLo, sc.CountHi + 1);
                float span = sc.HueHi - sc.HueLo, step = span / n;
                float b0 = span >= 360 ? rng.Uniform(0, step) : 0f;
                for (int k = 0; k < n; k++) hues.Add(((sc.HueLo + b0 + (k + 0.5f) * step + rng.Uniform(-0.3f, 0.3f) * step) % 360 + 360) % 360);
                int dom = rng.Integers(0, n);
                float wDom = Draw(rng, o, DominantW);
                float[] rest = rng.Dirichlet(n - 1, 1.2f);
                int ri = 0;
                for (int k = 0; k < n; k++) weights.Add(k == dom ? wDom : rest[ri++] * (1 - wDom));
            }
            float wsum = 0f; foreach (float w in weights) wsum += w;
            for (int i = 0; i < weights.Count; i++) weights[i] /= wsum;
            C.Hues.AddRange(hues); C.Weights.AddRange(weights);

            // --- cores: sizes, positions (dart throwing with the separation rule), hues by quota
            int nCores = (int)Draw(rng, o, CoresN);
            float rcMed = Draw(rng, o, RcMed);
            var rcs = new List<float>(nCores);
            for (int i = 0; i < nCores; i++) rcs.Add(rng.LogNormal(rcMed, 0.34f, 24f, 101f));
            rcs.Sort((a, b) => b.CompareTo(a));
            var cores = new List<Core>(nCores + 12);
            foreach (float rc in rcs)
            {
                float need = rng.Uniform(Draw(rng, o, SepLo), Draw(rng, o, SepHi));
                float bx = 0, by = 0, bestRatio = -1f; bool found = false;
                for (int t = 0; t < 600 && !found; t++)
                {
                    float x = rng.Uniform(BandX0, BandX1) * width, y = rng.Uniform(BandY0, BandY1) * height;
                    float ratio = 9f;
                    foreach (var c in cores) ratio = Math.Min(ratio, Hypot(x - c.X, y - c.Y) / ((rc + c.Rc) * scale));
                    if (ratio >= need) { bx = x; by = y; found = true; }
                    else if (ratio > bestRatio) { bestRatio = ratio; bx = x; by = y; }
                }
                cores.Add(new Core { X = bx, Y = by, Rc = rc });
            }
            int whiteIdx = -1;
            if (rng.Chance(sc.WhiteChance))
            {
                var cands = new List<int>();
                for (int i = 0; i < cores.Count; i++) if (cores[i].Rc >= 28 && cores[i].Rc <= 55) cands.Add(i);
                if (cands.Count > 0) whiteIdx = cands[rng.Integers(0, cands.Count)];
            }
            AssignHues(rng, cores, hues, weights, whiteIdx, scale);
            C.CoreCount = cores.Count;

            // --- the bed: violet only (225-290), large, anywhere in the band, overlapping freely
            var cool = new List<float>();
            foreach (float h in hues) if (h >= 225 && h < 290) cool.Add(h);
            if (cool.Count == 0) foreach (float h in hues) if (h >= 195 && h < 290) cool.Add(h);
            if (cool.Count == 0) { hues.Sort((a, b) => CircDiff(a, 240).CompareTo(CircDiff(b, 240))); cool.Add(hues[0]); if (hues.Count > 1) cool.Add(hues[1]); }
            int nBed = (int)Draw(rng, o, BedN);
            float bedRcMed = Draw(rng, o, BedRc);
            var bed = new List<Core>(nBed);
            for (int k = 0; k < nBed; k++)
            {
                float rc = rng.LogNormal(bedRcMed, 0.15f, 70f, 130f);
                float bx = 0, by = 0, bestRatio = -1f; bool found = false;
                for (int t = 0; t < 300 && !found; t++)
                {
                    float x = rng.Uniform(BandX0, BandX1) * width, y = rng.Uniform(BandY0, BandY1) * height;
                    float ratio = 9f;
                    foreach (var b in bed) ratio = Math.Min(ratio, Hypot(x - b.X, y - b.Y) / ((rc + b.Rc) * scale));
                    if (ratio >= 0.8f) { bx = x; by = y; found = true; }
                    else if (ratio > bestRatio) { bestRatio = ratio; bx = x; by = y; }
                }
                float prev = bed.Count > 0 ? bed[bed.Count - 1].Hue : float.NaN;
                var choices = new List<float>(); foreach (float h in cool) if (h != prev) choices.Add(h);
                if (choices.Count == 0) choices.AddRange(cool);
                bed.Add(new Core { X = bx, Y = by, Rc = rc, Hue = choices[rng.Integers(0, choices.Count)], Bed = true });
            }
            cores.AddRange(bed);
            C.BedCount = bed.Count;
            foreach (var c in cores) c.Seed = rng.Integers(1, int.MaxValue);

            // --- throws: from the band's rim outward from the frame centre
            var throws = DrawThrows(rng, o, cores, (int)Draw(rng, o, ThrowsN), width, height, scale);
            C.ThrowCount = throws.Count;
            var spatter = DrawSpatter(rng, o, cores, hues, (int)Draw(rng, o, SpatterN), width, height, scale);

            // --- one layer order for everything
            float noise = Draw(rng, o, LayerNoise);
            var order = new List<(float rank, int kind, int idx)>(cores.Count + spatter.Count);
            for (int i = 0; i < cores.Count; i++)
                order.Add((cores[i].Bed ? -1e6f + i : LayerRank(cores[i].White ? 0 : cores[i].Hue) + (cores[i].White ? 0.5f : 0f) + rng.Normal(0, noise), 0, i));
            for (int i = 0; i < spatter.Count; i++) order.Add((LayerRank(spatter[i].Hue) + rng.Normal(0, noise), 1, i));
            order.Sort((a, b) => a.rank.CompareTo(b.rank));
            for (int j = 0; j < throws.Count; j++) cores[throws[j].Src].Throws.Add(j);
            float bedV = Draw(rng, o, BedV), bedShade = Draw(rng, o, BedShade);
            float pSpray = Draw(rng, o, SprayOnPaint), pChain = Draw(rng, o, ChainOnPaint);
            C.PSpray = pSpray; C.PChain = pChain; C.pSprayDrawn = pSpray; C.pChainDrawn = pChain;
            C.bedV = bedV; C.bedShade = bedShade;

            foreach (var (rank, kind, idx) in order)
            {
                if (kind == 1)
                {
                    var s = spatter[idx];
                    var part = new Part(0.03f, 0f);
                    PaintSpatter(rng, part, s, scale);
                    C.Items.Add(new Item { Kind = ItemKind.Pieces, Pieces = part, X = 0, Y = 0, Hue = s.Hue, SMul = sc.SMul, VMul = sc.VMul, Group = C.nextGroup++ });
                    continue;
                }
                var c = cores[idx];
                int group = c.Bed ? 0 : C.nextGroup++;
                C.Cores.Add(new CoreInfo { X = c.X, Y = c.Y, Rc = c.Rc, Hue = c.Hue, Bed = c.Bed, White = c.White, Group = group });
                var throwsOf = new List<Throw>(c.Throws.Count); foreach (int j in c.Throws) throwsOf.Add(throws[j]);
                C.EmitCore(rng, c, throwsOf, group, C.Items);
            }
            return C;
        }

        float bedV, bedShade;

        /// <summary>The core's splat, then its throws as one pieces item, then any throw head
        /// landing inside the band as a small splat of its own; all in one group.</summary>
        void EmitCore(SplatRng rng, Core c, List<Throw> throwsOf, int group, List<Item> into)
        {
            float fcx = Width / 2f, fcy = Height / 2f;
            float outward = (float)Math.Atan2(c.Y - fcy, c.X - fcx);
            float hue = c.White ? rng.Uniform(0, 360) : c.Hue;
            var recipe = SplatRules.Generate(c.Seed, c.Rc, Scale, hue, outward,
                c.White ? 0f : SMul, (c.White ? 1f : VMul) * (c.Bed ? bedV : 1f), c.Bed ? bedShade : 1f, c.White, overrides: Overrides);
            into.Add(new Item { Kind = ItemKind.Splat, Recipe = recipe, X = c.X, Y = c.Y, Bed = c.Bed, Group = group });
            if (throwsOf.Count == 0) return;
            var pieces = new Part(0.03f, 0f);
            foreach (var t in throwsOf) PaintThrow(rng, pieces, t, c, Scale);
            into.Add(new Item { Kind = ItemKind.Pieces, Pieces = pieces, X = 0, Y = 0, Hue = c.Hue, SMul = SMul, VMul = VMul, Group = group });
            foreach (var t in throwsOf)
            {
                if (t.X > BandX0 * Width && t.X < BandX1 * Width && t.Y > BandY0 * Height && t.Y < BandY1 * Height)
                {
                    var mini = SplatRules.Generate(rng.Integers(1, int.MaxValue), t.HeadR, Scale, t.Hue, (float)Math.Atan2(t.Y - fcy, t.X - fcx), SMul, VMul, overrides: Overrides);
                    into.Add(new Item { Kind = ItemKind.Splat, Recipe = mini, X = t.X, Y = t.Y, Mini = true, Group = group });
                }
            }
        }

        /// <summary>A group prepared off the main thread, applied on it.</summary>
        public sealed class PushGroup { public readonly List<Item> Items = new List<Item>(4); public CoreInfo Core; public int Group, Throws; }

        /// <summary>
        /// A living painting: one more core with its throws, placed and coloured by the same rules
        /// against the cores already on the canvas (separation, hue by the remaining quota, never
        /// the hue of a touching neighbour), as a new group to append with <see cref="ApplyPush"/>.
        /// Now and then a spatter drop instead. Reads <see cref="Cores"/> only, so it may run on a
        /// worker thread while the main thread paints, as long as no pop runs meanwhile.
        /// </summary>
        public PushGroup PreparePush(SplatRng rng)
        {
            var o = Overrides;
            var g = new PushGroup { Group = nextGroup++ };
            if (rng.Chance(0.25f))
            {
                var fronts = new List<Core>();
                foreach (var ci in Cores) if (!ci.Bed) fronts.Add(new Core { X = ci.X, Y = ci.Y, Rc = ci.Rc, Hue = ci.Hue, White = ci.White });
                var sp = DrawSpatter(rng, o, fronts, Hues, 1, Width, Height, Scale);
                if (sp.Count > 0)
                {
                    var part = new Part(0.03f, 0f); PaintSpatter(rng, part, sp[0], Scale);
                    g.Items.Add(new Item { Kind = ItemKind.Pieces, Pieces = part, Hue = sp[0].Hue, SMul = SMul, VMul = VMul, Group = g.Group });
                }
                return g;
            }
            // a front core, placed and sized by the same rules as the composition's
            float rc = rng.LogNormal(Draw(rng, o, RcMed), 0.34f, 24f, 101f);
            float need = rng.Uniform(Draw(rng, o, SepLo), Draw(rng, o, SepHi));
            float bx = 0, by = 0, bestRatio = -1f; bool found = false;
            for (int t = 0; t < 600 && !found; t++)
            {
                float x = rng.Uniform(BandX0, BandX1) * Width, y = rng.Uniform(BandY0, BandY1) * Height;
                float ratio = 9f;
                foreach (var ci in Cores) if (!ci.Bed) ratio = Math.Min(ratio, Hypot(x - ci.X, y - ci.Y) / ((rc + ci.Rc) * Scale));
                if (ratio >= need) { bx = x; by = y; found = true; }
                else if (ratio > bestRatio) { bestRatio = ratio; bx = x; by = y; }
            }
            // hue: the palette slot furthest below its share of the front area, never a touching neighbour's
            float total = 0f; var have = new float[Hues.Count];
            foreach (var ci in Cores)
            {
                if (ci.Bed || ci.White) continue;
                total += ci.Rc * ci.Rc;
                int k = 0; float bestD = 999f;
                for (int h = 0; h < Hues.Count; h++) { float d = CircDiff(Hues[h], ci.Hue); if (d < bestD) { bestD = d; k = h; } }
                have[k] += ci.Rc * ci.Rc;
            }
            total += rc * rc;
            int pick = 0; float bestScore = float.MinValue;
            for (int h = 0; h < Hues.Count; h++)
            {
                bool touching = false;
                foreach (var ci in Cores)
                    if (!ci.Bed && !ci.White && Hypot(bx - ci.X, by - ci.Y) < 1.3f * (rc + ci.Rc) * Scale && CircDiff(Hues[h], ci.Hue) < SameHueDeg) { touching = true; break; }
                if (touching) continue;
                float score = Weights[h] * total - have[h] + rng.Uniform(0, 0.05f) * total;
                if (score > bestScore) { bestScore = score; pick = h; }
            }
            var core = new Core { X = bx, Y = by, Rc = rc, Hue = Hues[pick], Seed = rng.Integers(1, int.MaxValue) };
            g.Core = new CoreInfo { X = bx, Y = by, Rc = rc, Hue = core.Hue, Group = g.Group };
            // its throws: the frame carries about 2.5 per core
            var throwsOf = DrawThrows(rng, o, new List<Core> { core }, rng.Integers(1, 4), Width, Height, Scale);
            EmitCore(rng, core, throwsOf, g.Group, g.Items);
            g.Throws = throwsOf.Count;
            return g;
        }

        /// <summary>Append a prepared group: it lands on top of everything painted so far.</summary>
        public void ApplyPush(PushGroup g)
        {
            Items.AddRange(g.Items);
            if (g.Core != null) { Cores.Add(g.Core); CoreCount++; ThrowCount += g.Throws; }
        }

        /// <summary>One more splat on its own group (an erased hole, or any extra splat): appended
        /// last, so a canvas painting in order reaches it, and popped in turn like any front group.
        /// Not a core: the placement and hue rules ignore it.</summary>
        public Item AddSplat(SplatRecipe recipe, float x, float y, bool hole = false)
        {
            var it = new Item { Kind = ItemKind.Splat, Recipe = recipe, X = x, Y = y, Hole = hole, Group = nextGroup++ };
            Items.Add(it);
            return it;
        }

        /// <summary>Remove the oldest front group (never the bed). Returns its id, or 0 if none.</summary>
        public int PopOldest()
        {
            int oldest = int.MaxValue;
            foreach (var it in Items) if (it.Group > 0 && it.Group < oldest) oldest = it.Group;
            if (oldest == int.MaxValue) return 0;
            Items.RemoveAll(it => it.Group == oldest);
            Cores.RemoveAll(ci => ci.Group == oldest);
            return oldest;
        }

        /// <summary>Drawn once per composite: spray survival on paint below, and the fade of thin strokes over it.</summary>
        public float PSpray, PChain;

        static float Draw(SplatRng rng, SplatRuleOverrides o, Rule r)
        {
            float lo = r.Lo, hi = r.Hi;
            if (o != null && o.TryGet(r, out var range)) { lo = range.lo; hi = range.hi; }
            float x = rng.Uniform(lo, hi);
            return r.Int ? (float)Math.Round(x) : x;
        }

        /// <summary>Hues by area quota: cores largest first, each takes the allowed hue with the
        /// largest remaining share of its quota, so every frame lands on the palette's proportions
        /// whatever the size draw; touching cores never share a hue.</summary>
        static void AssignHues(SplatRng rng, List<Core> cores, List<float> hues, List<float> weights, int whiteIdx, float scale)
        {
            float total = 0f;
            for (int i = 0; i < cores.Count; i++) if (i != whiteIdx) total += cores[i].Rc * cores[i].Rc;
            var quota = new float[hues.Count];
            for (int k = 0; k < hues.Count; k++) quota[k] = weights[k] * total;
            var order = new List<int>(cores.Count);
            for (int i = 0; i < cores.Count; i++) order.Add(i);
            order.Sort((a, b) => cores[b].Rc.CompareTo(cores[a].Rc));
            var assigned = new bool[cores.Count];
            foreach (int i in order)
            {
                var c = cores[i];
                if (i == whiteIdx) { c.White = true; assigned[i] = true; continue; }
                var allowed = new List<int>(hues.Count);
                for (int k = 0; k < hues.Count; k++)
                {
                    bool ok = true;
                    for (int j = 0; j < cores.Count && ok; j++)
                    {
                        if (j == i || !assigned[j] || cores[j].White) continue;
                        if (Hypot(c.X - cores[j].X, c.Y - cores[j].Y) < 1.3f * (c.Rc + cores[j].Rc) * scale && CircDiff(hues[k], cores[j].Hue) < SameHueDeg) ok = false;
                    }
                    if (ok) allowed.Add(k);
                }
                if (allowed.Count == 0) for (int k = 0; k < hues.Count; k++) allowed.Add(k);
                int best = allowed[0]; float bestScore = float.MinValue;
                foreach (int k in allowed)
                {
                    float score = quota[k] / Math.Max(weights[k], 1e-6f) + rng.Uniform(0, 0.05f) * total;
                    if (score > bestScore) { bestScore = score; best = k; }
                }
                c.Hue = hues[best]; assigned[i] = true;
                quota[best] -= c.Rc * c.Rc;
            }
        }

        static List<Throw> DrawThrows(SplatRng rng, SplatRuleOverrides o, List<Core> cores, int nT, int width, int height, float scale)
        {
            float fcx = width / 2f, fcy = height / 2f, halfW = width / 2f;
            var coloured = new List<Core>(); foreach (var c in cores) if (!c.White) coloured.Add(c);
            // sources: bigger and, above all, OUTER splats throw more (the references' radial tails
            // come from the band's rim)
            var w = new float[coloured.Count];
            for (int i = 0; i < w.Length; i++) { float d = Hypot(coloured[i].X - fcx, coloured[i].Y - fcy) / halfW; w[i] = coloured[i].Rc * (0.3f + d * d); }
            float sigma = Deg(Draw(rng, o, ThrowSigmaDeg));
            var throws = new List<Throw>(nT);
            int guard = 0;
            while (throws.Count < nT && guard++ < nT * 20)
            {
                var src = coloured[rng.Choice(w)];
                float headR = rng.LogNormal(Draw(rng, o, ThrowHeadR), 0.25f, 12f, 24f);
                float dist = src.Rc * scale * rng.LogNormal(Draw(rng, o, ThrowDist), 0.45f, 2.0f, 7.5f);
                float ang = (float)Math.Atan2(src.Y - fcy, src.X - fcx) + rng.Normal(0, sigma);
                float hx = src.X + (float)Math.Cos(ang) * dist, hy = src.Y + (float)Math.Sin(ang) * dist;
                if (!(hx > 0.04f * width && hx < 0.96f * width && hy > 0.06f * height && hy < 0.94f * height)) continue;
                throws.Add(new Throw
                {
                    Src = cores.IndexOf(src), Hue = src.Hue, HeadR = headR, Dist = dist, Ang = ang, X = hx, Y = hy,
                    LineHw = rng.LogNormal(Draw(rng, o, ThrowLineHw), 0.3f, 0.9f, 2.4f), Bend = rng.Normal(0, Deg(5)),
                    Pieces = rng.Chance(0.2f) ? rng.Integers(2, 4) : 1, EndDrop = rng.Chance(0.5f), Aspect = rng.Uniform(1.1f, 1.4f),
                    V = rng.Uniform(0.78f, 0.92f), Connected = rng.Chance(0.8f), TailLen = rng.LogNormal(2.5f, 0.35f, 1.5f, 5.0f),
                    Children = rng.Integers(2, 7), Spray = rng.Integers(8, 25),
                });
            }
            return throws;
        }

        /// <summary>One long throw: a comet (teardrop head with a tail tapering concavely into a
        /// thin line) on a line back to the source rim in 80 % of cases, an optional small end drop
        /// ahead, a small forward splash of children and a few spray dots. Prims are in canvas px
        /// (the pieces item sits at the origin).</summary>
        static void PaintThrow(SplatRng rng, Part cv, Throw t, Core src, float scale)
        {
            float ex = (float)Math.Cos(t.Ang), ey = (float)Math.Sin(t.Ang);
            float sx = src.X + ex * src.Rc * scale * 1.2f, sy = src.Y + ey * src.Rc * scale * 1.2f;
            float hx = t.X, hy = t.Y;
            float headR = t.HeadR * scale, hw = t.LineHw * scale;
            float L = Hypot(hx - sx, hy - sy);
            float nx = -ey, ny = ex;
            float bulge = (float)Math.Tan(t.Bend) * L * 0.5f;
            (float x, float y) Pt(float s) => (sx + (hx - sx) * s + nx * bulge * 4 * s * (1 - s), sy + (hy - sy) * s + ny * bulge * 4 * s * (1 - s));
            float tail = Math.Min(t.TailLen * headR, L * 0.8f);
            float sTail = 1 - tail / Math.Max(L, 1f);
            float WOf(float s)
            {
                if (s < sTail) return hw;
                float d = (s - sTail) / (1 - sTail);
                return hw + (headR * 0.85f - hw) * (float)Math.Pow(d, 1.6);
            }
            var gaps = new List<(float g0, float g1)>();
            float s0Line;
            if (t.Connected)
            {
                s0Line = 0f;
                if (t.Pieces > 1 && sTail > 0.3f)
                {
                    var cuts = new List<float>();
                    for (int i = 0; i < t.Pieces - 1; i++) cuts.Add(rng.Uniform(0.12f, sTail - 0.06f));
                    cuts.Sort();
                    foreach (float c in cuts) gaps.Add((c, Math.Min(c + rng.Uniform(0.04f, 0.10f), sTail)));
                }
            }
            else s0Line = sTail;
            const int steps = 32;
            for (int k = 0; k < steps; k++)
            {
                float s0 = (float)k / steps, s1 = (float)(k + 1) / steps;
                if (s1 <= s0Line) continue;
                bool inGap = false; float mid = (s0 + s1) / 2;
                foreach (var (g0, g1) in gaps) if (g0 <= mid && mid <= g1) { inGap = true; break; }
                if (inGap) continue;
                var (xa, ya) = Pt(Math.Max(s0, s0Line)); var (xb, yb) = Pt(s1);
                cv.Capsule(rng, xa, ya, xb, yb, WOf(Math.Max(s0, s0Line)), WOf(s1), 0f, t.V);
            }
            cv.Disc(rng, hx, hy, headR * t.Aspect, headR, t.Ang, t.V);
            if (t.EndDrop)
            {
                float r2 = headR * rng.Uniform(0.25f, 0.5f), d2 = headR * rng.Uniform(1.8f, 3.2f);
                float x2 = hx + ex * d2, y2 = hy + ey * d2;
                if (rng.Chance(0.5f)) cv.Capsule(rng, hx + ex * headR * 0.6f, hy + ey * headR * 0.6f, x2, y2, hw * 0.6f, hw * 0.6f, 0f, t.V);
                cv.Disc(rng, x2, y2, r2 * rng.Uniform(1.0f, 1.4f), r2, t.Ang, t.V);
            }
            for (int i = 0; i < t.Children; i++)
            {
                float a = t.Ang + rng.Normal(0, Deg(25)), d = headR * rng.Uniform(1.6f, 4.0f), r = headR * rng.Uniform(0.15f, 0.40f), asp = rng.Uniform(1.2f, 1.8f);
                cv.Disc(rng, hx + (float)Math.Cos(a) * d, hy + (float)Math.Sin(a) * d, r * asp, r, a, t.V);
            }
            for (int i = 0; i < t.Spray; i++)
            {
                float a = rng.Chance(0.6f) ? t.Ang + rng.Normal(0, Deg(45)) : rng.Uniform(0, Tau);
                float d = headR * rng.Uniform(1.0f, 3.5f), r = rng.Uniform(0.6f, 1.6f) * scale;
                cv.Disc(rng, hx + (float)Math.Cos(a) * d, hy + (float)Math.Sin(a) * d, r, r, 0f, t.V);
            }
        }

        static List<Spatter> DrawSpatter(SplatRng rng, SplatRuleOverrides o, List<Core> cores, List<float> hues, int n, int width, int height, float scale)
        {
            float fcx = width / 2f, fcy = height / 2f;
            float sigma = Deg(Draw(rng, o, SpatterSigmaDeg));
            float rMed = Draw(rng, o, SpatterR), headMed = Draw(rng, o, SpatterHeadR), lenMed = Draw(rng, o, SpatterLen);
            var coloured = new List<Core>(); foreach (var c in cores) if (!c.White) coloured.Add(c);
            var outl = new List<Spatter>(n);
            int guard = 0;
            while (outl.Count < n && guard++ < n * 20)
            {
                float th = rng.Uniform(0, Tau);
                float r = rng.LogNormal(rMed, 0.28f, 280f, 780f);
                float x = fcx + (float)Math.Cos(th) * r * (width / FrameW), y = fcy + (float)Math.Sin(th) * r * 0.5f * (height / FrameH);
                if (!(x > 0.02f * width && x < 0.98f * width && y > 0.04f * height && y < 0.96f * height)) continue;
                Core near = null; float best = float.MaxValue;
                foreach (var c in coloured) { float d = Hypot(c.X - x, c.Y - y); if (d < best) { best = d; near = c; } }
                float hue = near != null && rng.Chance(0.6f) ? near.Hue : hues[rng.Integers(0, hues.Count)];
                float headR = rng.LogNormal(headMed, 0.3f, 3f, 9f);
                outl.Add(new Spatter { Hue = hue, X = x, Y = y, HeadR = headR, Length = 2 * headR * rng.LogNormal(lenMed, 0.35f, 1.6f, 5.5f),
                    Ang = (float)Math.Atan2(y - fcy, x - fcx) + rng.Normal(0, sigma), V = rng.Uniform(0.78f, 0.92f) });
            }
            return outl;
        }

        /// <summary>A small teardrop: round head at the far end, tail tapering back toward the frame centre.</summary>
        static void PaintSpatter(SplatRng rng, Part cv, Spatter t, float scale)
        {
            float ex = (float)Math.Cos(t.Ang), ey = (float)Math.Sin(t.Ang);
            float hx = t.X, hy = t.Y, r = t.HeadR * scale;
            float tail = Math.Max(t.Length * scale - 2 * r, r);
            const int steps = 10;
            for (int k = 0; k < steps; k++)
            {
                float d0 = tail * k / steps, d1 = tail * (k + 1) / steps;
                float w0 = 0.6f * scale + (r * 0.85f - 0.6f * scale) * (float)Math.Pow(d0 / tail, 1.5);
                float w1 = 0.6f * scale + (r * 0.85f - 0.6f * scale) * (float)Math.Pow(d1 / tail, 1.5);
                cv.Capsule(rng, hx - ex * (tail - d0), hy - ey * (tail - d0), hx - ex * (tail - d1), hy - ey * (tail - d1), w0, w1, 0f, t.V);
            }
            cv.Disc(rng, hx, hy, r * 1.15f, r, t.Ang, t.V);
        }

        static float Deg(float d) => d * Tau / 360f;
    }
}
