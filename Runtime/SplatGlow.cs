using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoverUp.Splatter
{
    /// <summary>
    /// An outer glow for an opaque canvas: the paint plus a blurred copy of it, added only where
    /// the canvas is still bare, so every splat wears a halo of its own colour in the gaps while
    /// the paint itself stays exactly as painted. It owns the output texture; show that in place
    /// of the canvas's. <see cref="Render(SplatCanvas)"/> once a frame costs nothing while the
    /// paint and the settings are unchanged.
    /// </summary>
    public sealed class SplatGlow : IDisposable
    {
        /// <summary>The paint with its glow, the size of the last paint rendered.</summary>
        public RenderTexture Texture { get; private set; }
        /// <summary>How bright the halo is. 0 presents the paint as is.</summary>
        public float Strength = 0.5f;
        /// <summary>The halo's radius in canvas px, with a fainter tail at four times it. 40 is
        /// about one core radius in a painting 1672 px wide; scale it with the painting.</summary>
        public float Radius = 40f;
        /// <summary>The far tail's share, relative to the near halo.</summary>
        public float FarWeight = 0.5f;

        static Material mat;
        static readonly int NearId = Shader.PropertyToID("_Near"), FarId = Shader.PropertyToID("_Far"),
            StrengthId = Shader.PropertyToID("_Strength"), FarWeightId = Shader.PropertyToID("_FarWeight"),
            DirId = Shader.PropertyToID("_Dir"), SigmaId = Shader.PropertyToID("_Sigma");
        // pass indices follow the order in SplatGlow.shader
        const int PassGaussian = 0, PassCompose = 1;
        readonly List<RenderTexture> pool = new List<RenderTexture>(10);
        int renderedVersion = -1; float renderedStrength, renderedRadius, renderedFarWeight;

        public SplatGlow(int width, int height) { EnsureTexture(width, height); }

        /// <summary>Render the canvas with the glow, unless nothing changed since the last time.
        /// Returns whether it rendered.</summary>
        public bool Render(SplatCanvas canvas)
        {
            if (canvas == null || canvas.Texture == null) return false;
            bool unchanged = Texture != null && Texture.width == canvas.Width && Texture.height == canvas.Height
                && canvas.Version == renderedVersion && Strength == renderedStrength && Radius == renderedRadius && FarWeight == renderedFarWeight;
            if (unchanged) return false;
            Render(canvas.Texture);
            renderedVersion = canvas.Version;
            return true;
        }

        /// <summary>Render any painted texture with the glow, every time it is called.</summary>
        public void Render(Texture paint)
        {
            int w = paint.width, h = paint.height;
            EnsureTexture(w, h);
            renderedStrength = Strength; renderedRadius = Radius; renderedFarWeight = FarWeight;
            var prev = RenderTexture.active;
            var m = Strength > 0f ? Mat() : null;
            if (m == null) { Graphics.Blit(paint, Texture); RenderTexture.active = prev; return; }
            try
            {
                // the near halo is the paint blurred at an eighth of its size, the far tail is the
                // near halo blurred again at a thirty-second; a gaussian's visible radius is about
                // 2.5 sigma, so the sigma follows the radius at the near halo's size
                float sigma = Mathf.Clamp(Radius / 8f / 2.5f, 0.6f, 5f);
                var d2 = Temp(w / 2, h / 2); var d4 = Temp(w / 4, h / 4); var d8 = Temp(w / 8, h / 8);
                Graphics.Blit(paint, d2); Graphics.Blit(d2, d4); Graphics.Blit(d4, d8);
                var t8 = Temp(w / 8, h / 8); var near = Temp(w / 8, h / 8);
                Gaussian(m, d8, t8, 1f, 0f, sigma); Gaussian(m, t8, near, 0f, 1f, sigma);
                var d16 = Temp(w / 16, h / 16); var d32 = Temp(w / 32, h / 32);
                Graphics.Blit(near, d16); Graphics.Blit(d16, d32);
                var t32 = Temp(w / 32, h / 32); var far = Temp(w / 32, h / 32);
                Gaussian(m, d32, t32, 1f, 0f, sigma); Gaussian(m, t32, far, 0f, 1f, sigma);
                m.SetTexture(NearId, near); m.SetTexture(FarId, far);
                m.SetFloat(StrengthId, Strength); m.SetFloat(FarWeightId, FarWeight);
                Graphics.Blit(paint, Texture, m, PassCompose);
            }
            finally
            {
                RenderTexture.active = prev;
                foreach (var rt in pool) RenderTexture.ReleaseTemporary(rt);
                pool.Clear();
            }
        }

        RenderTexture Temp(int w, int h)
        {
            var rt = RenderTexture.GetTemporary(Mathf.Max(1, w), Mathf.Max(1, h), 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Bilinear; rt.wrapMode = TextureWrapMode.Clamp;
            pool.Add(rt);
            return rt;
        }

        static void Gaussian(Material m, RenderTexture src, RenderTexture dst, float dx, float dy, float sigma)
        {
            m.SetVector(DirId, new Vector4(dx, dy, 0f, 0f)); m.SetFloat(SigmaId, sigma);
            Graphics.Blit(src, dst, m, PassGaussian);
        }

        static Material Mat()
        {
            if (mat == null)
            {
                var sh = Shader.Find("OpenSplatter/Glow");
                if (sh == null) { Debug.LogError("SplatGlow: shader OpenSplatter/Glow is missing from the build"); return null; }
                mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            return mat;
        }

        /// <summary>The output needs no alpha (the glow is for an opaque canvas), so the packed
        /// float format halves its memory where the platform has it.</summary>
        void EnsureTexture(int width, int height)
        {
            if (Texture != null && Texture.width == width && Texture.height == height) return;
            Release();
            var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float) ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGBHalf;
            Texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { name = "SplatGlow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
            Texture.Create();
            renderedVersion = -1;
        }

        void Release()
        {
            if (Texture == null) return;
            Texture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(Texture); else UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }

        public void Dispose() { Release(); }
    }
}
