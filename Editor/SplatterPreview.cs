using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoverUp.Splatter.Editor
{
    /// <summary>
    /// Renders splats to PNG files without entering play mode: a whole composition in each
    /// palette, the rainbow one again with the outer glow, a strip of single splats, and the same
    /// composition after a few pushes and pops.
    /// Menu item, or headless:
    ///   Unity -batchmode -quit -projectPath ... -executeMethod CoverUp.Splatter.Editor.SplatterPreview.RenderHeadless -splatOut DIR [-splatSeed N]
    /// (no -nographics: the painter needs a GPU).
    /// </summary>
    public static class SplatterPreview
    {
        [MenuItem("Window/Open Splatter/Render Preview")]
        public static void RenderFromMenu()
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "OpenSplatter");
            Render(dir, 1);
            EditorUtility.RevealInFinder(dir);
        }

        public static void RenderHeadless()
        {
            string dir = "Temp/OpenSplatter"; int seed = 1;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-splatOut") dir = args[i + 1];
                if (args[i] == "-splatSeed") int.TryParse(args[i + 1], out seed);
            }
            Render(dir, seed);
        }

        public static void Render(string dir, int seed)
        {
            Directory.CreateDirectory(dir);
            foreach (var palette in new[] { SplatPalette.Rainbow, SplatPalette.Cool, SplatPalette.Muted })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var comp = SplatComposition.Generate(seed, palette, 1920, 1080);
                long genMs = sw.ElapsedMilliseconds;
                using (var canvas = new SplatCanvas(1920, 1080))
                {
                    canvas.Begin(comp);
                    canvas.PaintAll();
                    long paintMs = sw.ElapsedMilliseconds - genMs;
                    Save(canvas.Texture, Path.Combine(dir, $"composition-{palette.Name.ToLowerInvariant()}-{seed:000}.png"));
                    Debug.Log($"[OpenSplatter] {palette.Name} seed {seed}: {comp.Items.Count} items ({comp.CoreCount} cores, {comp.BedCount} bed, {comp.ThrowCount} throws), generate {genMs} ms, paint {paintMs} ms");
                    if (palette.Bank == null) continue;
                    using (var glow = new SplatGlow(1920, 1080) { Strength = 0.5f, Radius = 40f * comp.Scale })
                    {
                        glow.Render(canvas);
                        Save(glow.Texture, Path.Combine(dir, $"glow-{seed:000}.png"));
                    }
                }
            }

            // a living painting: three pushes and two pops on the rainbow composition
            {
                var comp = SplatComposition.Generate(seed, SplatPalette.Rainbow, 1920, 1080);
                using (var canvas = new SplatCanvas(1920, 1080))
                {
                    var rng = new SplatRng(seed * 7919 + 17);
                    canvas.Begin(comp); canvas.PaintAll();
                    for (int i = 0; i < 3; i++) { comp.ApplyPush(comp.PreparePush(rng)); canvas.PaintAll(); }
                    comp.PopOldest(); comp.PopOldest();
                    canvas.Begin(comp); canvas.PaintAll();
                    Save(canvas.Texture, Path.Combine(dir, $"living-{seed:000}.png"));
                }
            }

            // six single splats on 1000 px tiles at 1.6 display px per frame px, cores of 30 to 62 frame px
            const int tile = 1000; const float scale = 1.6f;
            using (var strip = new SplatCanvas(tile * 3, tile * 2) { SprayOnPaint = 1f, ChainOnPaint = 1f })
            {
                float[] hues = { 190, 108, 20, 340, 290, 200 };
                for (int i = 0; i < 6; i++)
                {
                    int sd = seed * 100 + i + 1;
                    float rc = 30f + (float)new System.Random(sd + 99991).NextDouble() * 32f;
                    strip.Paint(SplatRules.Generate(sd, rc, scale, hues[i]), (i % 3) * tile + tile / 2f, (1 - i / 3) * tile + tile / 2f);
                }
                Save(strip.Texture, Path.Combine(dir, $"splats-{seed:000}.png"));
            }
            Debug.Log("[OpenSplatter] wrote " + dir);
        }

        /// <summary>The canvas holds linear colour; a PNG wants sRGB, so encode on the way out.</summary>
        static void Save(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active;
            var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(rt, tmp);
            RenderTexture.active = tmp;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].r)), Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].g)), Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].b)), 1f);
            var outTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            outTex.SetPixels(px); outTex.Apply(false);
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(outTex);
        }
    }
}
