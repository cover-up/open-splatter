// The two blit passes behind SplatGlow (Runtime/SplatGlow.cs): a separable gaussian and the
// outer-glow composite. Standard Graphics.Blit passes (_MainTex in, full-target quad).
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

        // ---- pass 1: the outer glow: out = paint + strength * (near + farW * far) where the
        //      canvas is still black and not erased, so the paint stays exactly as painted and
        //      a hole stays a hole ----
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
                return float4(s.rgb + _Strength * g * (1.0 - painted) * s.a, s.a);   // s.a is 0 where a hole erased the paint
            }
            ENDHLSL
        }
    }
    Fallback Off
}
