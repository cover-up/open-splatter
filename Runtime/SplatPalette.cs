namespace CoverUp.Splatter
{
    /// <summary>
    /// A colour theme: which hues a painting draws and from where, the saturation and value
    /// multipliers on every splat, and the chance of a white one. <see cref="Rainbow"/> draws
    /// from a fixed bank of ten neon hues with front-layer weights; <see cref="Cool"/> and
    /// <see cref="Muted"/> spread a handful of hues evenly over a range. Make your own by setting
    /// the fields, or by starting from a preset. A <see cref="SplatComposition"/> draws its whole
    /// hue set from the palette; a splat on its own takes one colour from <see cref="Pick"/>.
    /// </summary>
    public sealed class SplatPalette
    {
        public string Name = "Custom";
        /// <summary>How many hues a composition draws when there is no <see cref="Bank"/>.</summary>
        public int CountLo = 5, CountHi = 8;
        /// <summary>The hue range those are spread over, degrees.</summary>
        public float HueLo = 0f, HueHi = 360f;
        /// <summary>Saturation and value multipliers on every splat.</summary>
        public float SMul = 1f, VMul = 1f;
        /// <summary>The chance that a composition has one white splat among its mid-sized cores.</summary>
        public float WhiteChance = 0f;
        /// <summary>Fixed hues instead of a range: (hue, presence out of five, share of the front
        /// layer). In a composition a hue with a presence of two or less drops out half the time,
        /// and every hue jitters by a few degrees, so no two seeds share a palette exactly.</summary>
        public (float hue, int presence, float weight)[] Bank;

        public static SplatPalette Rainbow => new SplatPalette { Name = "Rainbow", Bank = NeonBank };
        public static SplatPalette Cool => new SplatPalette { Name = "Cool", CountLo = 4, CountHi = 6, HueLo = 85, HueHi = 335, SMul = 0.98f, VMul = 0.95f, WhiteChance = 1f };
        public static SplatPalette Muted => new SplatPalette { Name = "Muted", CountLo = 4, CountHi = 5, SMul = 0.85f, VMul = 0.62f };

        /// <summary>The presets, fresh copies, for a menu or a dropdown to offer.</summary>
        public static SplatPalette[] Presets => new[] { Rainbow, Cool, Muted };

        /// <summary>A preset by name, ignoring case, or null when nothing matches.</summary>
        public static SplatPalette ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Presets)
                if (string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>
        /// One colour for a splat on its own: a bank hue by its share of the front layer, or one
        /// spread over the range, jittered a few degrees as a composition's hues are, with the
        /// palette's saturation and value. White comes up as often as one white core does among
        /// a whole painting's sixteen; it has no saturation and full value, so the shading rules
        /// still apply.
        /// </summary>
        public (float hue, float sMul, float vMul, bool white) Pick(SplatRng rng)
        {
            if (rng.Chance(WhiteChance / CoresPerPainting)) return (rng.Uniform(0f, 360f), 0f, 1f, true);
            float hue;
            if (Bank != null && Bank.Length > 0)
            {
                var weights = new float[Bank.Length];
                for (int i = 0; i < Bank.Length; i++) weights[i] = Bank[i].weight;
                hue = Bank[rng.Choice(weights)].hue + rng.Uniform(-5f, 5f);
            }
            else hue = HueLo + rng.Uniform(0f, HueHi - HueLo);
            return ((hue % 360f + 360f) % 360f, SMul, VMul, false);
        }

        /// <summary>The measured core count of a painting: <see cref="WhiteChance"/> is per painting, so one pick gets its share.</summary>
        const float CoresPerPainting = 16f;

        // The neon set measured on the rainbow references, with how many of the five frames
        // carried each hue and the share of the front layer it took.
        static readonly (float hue, int presence, float weight)[] NeonBank =
        {
            (20, 5, 0.15f), (60, 3, 0.07f), (78, 2, 0.04f), (108, 4, 0.13f), (138, 2, 0.03f),
            (190, 4, 0.12f), (208, 5, 0.04f), (250, 4, 0.06f), (278, 2, 0.03f), (325, 5, 0.33f),
        };
    }
}
