// Blit passes around the splat painter (Runtime/SplatCanvas.cs): a separable gaussian, the
// outer-glow composite, and two small helpers (premultiply into a padded frame, a weighted sum)
// that are handy for building a glow around any sprite. All standard Graphics.Blit passes
// (_MainTex in, full-target quad).
Shader "OpenSplatter/Glow"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex; float4 _MainTex_TexelSize;
        ENDHLSL

        // ---- pass 0: separable gaussian, 17 taps a texel apart along _Dir ----------------------
        Pass
        {
            Name "Gaussian"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 _Dir; float _Sigma;   // _Dir.xy = (1,0) or (0,1); _Sigma in texels
            float4 frag(v2f_img i) : SV_Target
            {
                float s2 = 2.0 * max(_Sigma, 0.1) * max(_Sigma, 0.1); float4 sum = 0; float wsum = 0;
                [unroll] for (int k = -8; k <= 8; k++)
                {
                    float w = exp(-(k * k) / s2);
                    sum += w * tex2D(_MainTex, i.uv + _Dir.xy * _MainTex_TexelSize.xy * k); wsum += w;
                }
                return sum / wsum;
            }
            ENDHLSL
        }

        // ---- pass 1: the outer glow: display = sharp + strength * (near + farW * far) where the
        //      sharp canvas is still black and not erased, so the paint and the bed stay exactly as
        //      painted and a click's hole stays a hole ----
        Pass
        {
            Name "GlowCompose"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            sampler2D _Near, _Far; float _Strength, _FarWeight;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 s = tex2D(_MainTex, i.uv);
                float m = max(s.r, max(s.g, s.b));
                float painted = smoothstep(0.004, 0.04, m);            // linear; the painter's own floor is 0.01
                float3 g = tex2D(_Near, i.uv).rgb + _FarWeight * tex2D(_Far, i.uv).rgb;
                return float4(s.rgb + _Strength * g * (1.0 - painted) * s.a, s.a);   // s.a is 0 where a click erased the paint
            }
            ENDHLSL
        }

        // ---- pass 2: premultiply a sprite into a padded frame (uv outside the sprite is empty) ----
        Pass
        {
            Name "PremultiplyPad"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float _Pad;   // fraction of the frame on each side that is padding
            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = (i.uv - _Pad) / max(1.0 - 2.0 * _Pad, 1e-4);
                if (any(uv < 0.0) || any(uv > 1.0)) return 0;
                float4 c = tex2D(_MainTex, uv);
                return float4(c.rgb * c.a, c.a);
            }
            ENDHLSL
        }

        // ---- pass 3: weighted sum of two textures (the halo's near and far blurs) ----------------
        Pass
        {
            Name "Add"
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            sampler2D _Far; float _NearWeight, _FarWeight;
            float4 frag(v2f_img i) : SV_Target { return _NearWeight * tex2D(_MainTex, i.uv) + _FarWeight * tex2D(_Far, i.uv); }
            ENDHLSL
        }
    }
    Fallback Off
}
