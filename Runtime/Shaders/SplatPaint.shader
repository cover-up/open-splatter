// The splat painter's passes (see Runtime/SplatCanvas.cs). Everything draws into single-part
// scratch textures (R = coverage, G = per-piece value) with MAX blending, then a rounding pass,
// then a composite pass lays the part onto the canvas with the splat's colour field. An opaque
// canvas is cleared to black with alpha 1; a transparent one is cleared to transparent and ends
// up premultiplied, which the last pass converts to straight alpha for a plain RawImage. "Paint
// already down" is judged by colour either way. Coordinates are canvas px, y up; the vertex
// shaders compute clip positions themselves, so no GL matrix state is involved.
Shader "OpenSplatter/Paint"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "UnityCG.cginc"
        #define TAU 6.28318530718
        #define PAINTED 0.01     // linear-space floor for "there is paint here" (about 0.08 in sRGB)

        struct Prim { float4 a, b, c, d, e; };
        StructuredBuffer<Prim> _Prims;
        float4 _Target;        // x, y: origin of the target texture in canvas px; z, w: its size
        float2 _Centre;        // the splat's centre in canvas px (prims are relative to it)

        float4 ClipFromCanvas(float2 canvasPx)
        {
            float2 t = (canvasPx - _Target.xy) / _Target.zw;
            float4 o = float4(t * 2.0 - 1.0, 0.0, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
            o.y = -o.y;
            #endif
            return o;
        }
        float2 Corner(uint vid)
        {
            // two triangles: 0 1 2, 2 1 3 over the corners (0,0) (1,0) (0,1) (1,1)
            uint c = vid < 3 ? vid : (vid == 3 ? 2 : (vid == 4 ? 1 : 3));
            return float2(c & 1, c >> 1);
        }
        float SS(float e0, float e1, float x) { float t = saturate((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }
        ENDHLSL

        // ---- pass 0: primitives (lumpy discs, wavering capsules) ----------------------------
        Pass
        {
            Name "Prims"
            BlendOp Max
            Blend One One
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _Below; float4 _BelowRect;   // spray survival: the canvas region already down
            float _Survive, _PSurvive, _Seed;

            struct v2f { float4 pos : SV_POSITION; float2 pc : TEXCOORD0; nointerpolation uint iid : TEXCOORD1; };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                v2f o; Prim p = _Prims[iid];
                bool disc = p.a.x < 0.5;
                float e = disc ? max(p.b.z, p.b.w) * 1.3 + 2.0 : max(max(p.b.z, p.b.w), p.d.w) + 2.0;
                float2 lo = disc ? p.a.yz - e : min(p.a.yz, p.b.xy) - e;
                float2 hi = disc ? p.a.yz + e : max(p.a.yz, p.b.xy) + e;
                if (_Survive > 0.5)
                {
                    float2 cpx = _Centre + p.a.yz;
                    float2 uv = (cpx - _BelowRect.xy) / _BelowRect.zw;
                    float3 below = tex2Dlod(_Below, float4(uv, 0, 0)).rgb;
                    float h = frac(sin(iid * 12.9898 + _Seed) * 43758.5453);
                    if (max(below.r, max(below.g, below.b)) > PAINTED && h > _PSurvive) { lo = hi = p.a.yz; }   // lands on paint, does not stick
                }
                float2 pc = lerp(lo, hi, Corner(vid));
                o.pc = pc; o.iid = iid;
                o.pos = ClipFromCanvas(_Centre + pc);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                Prim p = _Prims[i.iid];
                float cov;
                if (p.a.x < 0.5)
                {
                    float2 d = i.pc - p.a.yz; float c = cos(p.a.w), s = sin(p.a.w);
                    float u = d.x * c + d.y * s, v = -d.x * s + d.y * c;
                    float th = atan2(v, u);
                    float prof = 1.0 + p.c.x * cos(2 * th + p.d.x) + p.c.y * cos(3 * th + p.d.y) + p.c.z * cos(5 * th + p.d.z);
                    float rx = p.b.z, ry = p.b.w;
                    cov = saturate((prof - sqrt((u / rx) * (u / rx) + (v / ry) * (v / ry))) * min(rx, ry) + 0.5);
                }
                else
                {
                    float2 a = p.a.yz, b = p.b.xy, q = i.pc;
                    float2 ab = b - a; float L = max(length(ab), 1e-4); float2 e = ab / L;
                    float t = saturate(dot(q - a, e) / L);
                    float dist = length(q - a - t * L * e);
                    float width = p.b.z + (p.b.w - p.b.z) * t;
                    width *= 1.0 + p.e.z * cos(TAU * p.e.x * t + p.e.y);
                    width = max(width, p.c.w);
                    cov = saturate(width - dist + 0.5);
                    if (p.d.w > 0) cov = max(cov, saturate(p.d.w - length(q - b) + 0.5));
                }
                return float4(cov, cov > 0.002 ? p.e.w : 0.0, 0, 0);
            }
            ENDHLSL
        }

        // ---- pass 1: a polar shape (the core outline, the web's sheet) ----------------------
        Pass
        {
            Name "Polar"
            BlendOp Max
            Blend One One
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 _Rect;                       // quad in canvas px: x, y, w, h
            float4 _Polar;                      // base, span, aspect, axis
            float4 _PolarMM;                    // min, max of the raw harmonic sum
            float4 _PolK0, _PolK1, _PolA0, _PolA1, _PolP0, _PolP1;
            struct v2f { float4 pos : SV_POSITION; float2 pc : TEXCOORD0; };
            v2f vert(uint vid : SV_VertexID)
            {
                v2f o; float2 cpx = _Rect.xy + Corner(vid) * _Rect.zw;
                o.pc = cpx - _Centre; o.pos = ClipFromCanvas(cpx); return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float c = cos(_Polar.w), s = sin(_Polar.w);
                float along = (i.pc.x * c + i.pc.y * s) / _Polar.z, across = -i.pc.x * s + i.pc.y * c;
                float rn = sqrt(along * along + across * across);
                float th = atan2(across, along);
                float raw = _PolA0.x * cos(_PolK0.x * th + _PolP0.x) + _PolA0.y * cos(_PolK0.y * th + _PolP0.y)
                          + _PolA0.z * cos(_PolK0.z * th + _PolP0.z) + _PolA0.w * cos(_PolK0.w * th + _PolP0.w)
                          + _PolA1.x * cos(_PolK1.x * th + _PolP1.x) + _PolA1.y * cos(_PolK1.y * th + _PolP1.y)
                          + _PolA1.z * cos(_PolK1.z * th + _PolP1.z) + _PolA1.w * cos(_PolK1.w * th + _PolP1.w);
                float r = _Polar.x + _Polar.y * (raw - _PolarMM.x) / (_PolarMM.y - _PolarMM.x + 1e-6);
                float cov = saturate(r - rn + 0.5);
                return float4(cov, 0, 0, 0);
            }
            ENDHLSL
        }

        // ---- pass 2: rounding: gaussian blur of the coverage (optionally times 1 - a cut
        //      texture) then a smoothstep threshold; optional radial mask q > _MaskQ -----------
        Pass
        {
            Name "Round"
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _Src; float4 _SrcTexel;    // the raw coverage, and 1/size
            sampler2D _Cut; float _HasCut;
            float _Sigma;                        // px
            float4 _Mask;                        // cx, cy (canvas px), Rc, qMin (qMin <= 0: no mask)
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(uint vid : SV_VertexID)
            {
                v2f o; float2 c = Corner(vid);
                o.pos = float4(c * 2 - 1, 0, 1);
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y = -o.pos.y;
                #endif
                o.uv = c; return o;
            }
            float Tap(float2 uv)
            {
                float a = tex2D(_Src, uv).r;
                if (_HasCut > 0.5) a *= 1.0 - tex2D(_Cut, uv).r;
                return a;
            }
            float4 frag(v2f i) : SV_Target
            {
                float s2 = 2.0 * _Sigma * _Sigma; float sum = 0, wsum = 0;
                [unroll] for (int y = -2; y <= 2; y++)
                [unroll] for (int x = -2; x <= 2; x++)
                {
                    float w = exp(-(x * x + y * y) / s2);
                    sum += w * Tap(i.uv + float2(x, y) * _SrcTexel.xy); wsum += w;
                }
                float a = SS(0.38, 0.62, sum / wsum);
                if (_Mask.w > 0)
                {
                    float2 cpx = _Target.xy + i.uv * _Target.zw;
                    if (length(cpx - _Mask.xy) / _Mask.z < _Mask.w) a = 0;
                }
                return float4(a, tex2D(_Src, i.uv).g, 0, 0);
            }
            ENDHLSL
        }

        // ---- pass 3: accumulate one part texture into another by max (rect-mapped) -----------
        Pass
        {
            Name "Accum"
            BlendOp Max
            Blend One One
            ColorMask RG
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _Alpha; float4 _Rect;      // the source part's rect in canvas px
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(uint vid : SV_VertexID)
            {
                v2f o; float2 c = Corner(vid);
                o.uv = c; o.pos = ClipFromCanvas(_Rect.xy + c * _Rect.zw); return o;
            }
            float4 frag(v2f i) : SV_Target { return float4(tex2D(_Alpha, i.uv).rg, 0, 0); }
            ENDHLSL
        }

        // ---- pass 4: composite a part onto the canvas with the splat's colour field ----------
        Pass
        {
            Name "Composite"
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _Below;                    // the canvas region already down (same rect)
            sampler2D _Alpha; float4 _AlphaTexel; float4 _Rect;
            float _Mode;                         // 0 over, 1 thin (chains), 2 pieces (flat colour, thin)
            float _PChain;                       // fade where a thin stroke lies over paint
            float _Gain;                     // the paint's colour multiplier (above 1 exceeds white, for a bloom to pick up)
            float _Hole;                         // 1: erase instead of paint (a click's hole): black, and alpha 0 so the outer glow leaves it dark
            float4 _Col0;                        // hue (deg), sMul, vMul, shadeMul
            float4 _Col1;                        // Rc (px), colCell (px), colHueCell (px), noise seed
            float4 _Col2;                        // vTop, spread, sTop, tint
            float4 _Col3;                        // hueSpread, 0, 0, 0
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 cpx : TEXCOORD1; };
            v2f vert(uint vid : SV_VertexID)
            {
                v2f o; float2 c = Corner(vid); o.uv = c;
                o.cpx = _Rect.xy + c * _Rect.zw; o.pos = ClipFromCanvas(o.cpx); return o;
            }
            float Hash(float2 p, float seed) { return frac(sin(dot(p, float2(127.1, 311.7)) + seed * 0.618) * 43758.5453); }
            float VNoise(float2 p, float cell, float seed)
            {
                float2 f = p / cell; float2 i = floor(f); f = frac(f); f = f * f * (3 - 2 * f);
                float a = Hash(i, seed), b = Hash(i + float2(1, 0), seed), c = Hash(i + float2(0, 1), seed), d = Hash(i + float2(1, 1), seed);
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }
            // two octaves of value noise remapped toward a flat distribution (rank-normalised)
            float U(float2 p, float cell, float k2, float seed) { float s = VNoise(p, cell, seed) + k2 * VNoise(p, cell * 0.5, seed + 3.7); return SS(0.5 * (1 + k2) - 0.5, 0.5 * (1 + k2) + 0.5, s); }
            float3 Hsv(float h, float s, float v)
            {
                float h6 = fmod(fmod(h, 360.0) + 360.0, 360.0) / 60.0;
                float3 k = fmod(float3(5, 3, 1) + h6, 6.0);
                float3 f = v - v * s * saturate(min(k, min(4 - k, 1)));
                return f;
            }
            float Prof(float q, float y0, float y1, float y2, float y3, float y4, float y5, float y6, float y7, float y8)
            {
                // the measured radial profiles at q = 0.25, 0.65, 0.9, 1.125, 1.375, 1.75, 2.25, 2.75, 3.5
                if (q <= 0.25) return y0; if (q <= 0.65) return lerp(y0, y1, (q - 0.25) / 0.4);
                if (q <= 0.9) return lerp(y1, y2, (q - 0.65) / 0.25); if (q <= 1.125) return lerp(y2, y3, (q - 0.9) / 0.225);
                if (q <= 1.375) return lerp(y3, y4, (q - 1.125) / 0.25); if (q <= 1.75) return lerp(y4, y5, (q - 1.375) / 0.375);
                if (q <= 2.25) return lerp(y5, y6, (q - 1.75) / 0.5); if (q <= 2.75) return lerp(y6, y7, (q - 2.25) / 0.5);
                if (q <= 3.5) return lerp(y7, y8, (q - 2.75) / 0.75); return y8;
            }
            float3 Field(float2 cpx)
            {
                float2 d = cpx - _Centre; float q = length(d) / _Col1.x;
                float spread = Prof(q, 0.03, 0.03, 0.04, 0.05, 0.06, 0.07, 0.07, 0.07, 0.08) * _Col2.y * _Col0.w;
                float tint = Prof(q, 0.01, 0.01, 0.02, 0.02, 0.03, 0.03, 0.03, 0.03, 0.04) * _Col2.w;
                float hueq = Prof(q, 3, 4, 5, 7, 9, 11, 11, 11, 11) * _Col3.x;
                float u = U(d, _Col1.y, 0.6, _Col1.w);
                float val = _Col2.x - spread * pow(u, 2.4);
                float u2 = U(d, _Col1.y * 0.7, 0.5, _Col1.w + 11.0);
                float sat = _Col2.z - tint * u2 * u2 * u2;
                float u3 = U(d, _Col1.z, 0.5, _Col1.w + 23.0);
                float hue = _Col0.x + hueq * (u3 - 0.5) * 1.6;
                return Hsv(hue, saturate(sat * _Col0.y), saturate(val * _Col0.z));
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 below = tex2D(_Below, i.uv);
                float2 av = tex2D(_Alpha, i.uv).rg; float a = av.r;
                if (_Mode > 0.5)
                {
                    float2 ts = _AlphaTexel.xy;
                    float thin = min(a, min(min(tex2D(_Alpha, i.uv + float2(ts.x, 0)).r, tex2D(_Alpha, i.uv - float2(ts.x, 0)).r),
                                            min(tex2D(_Alpha, i.uv + float2(0, ts.y)).r, tex2D(_Alpha, i.uv - float2(0, ts.y)).r)));
                    a = max(below.r, max(below.g, below.b)) > PAINTED ? thin * _PChain : a;
                }
                if (_Hole > 0.5) return float4(below.rgb * (1 - a), below.a * (1 - a));   // erased: to black, and out of the glow
                float3 col = _Mode > 1.5 ? Hsv(_Col0.x, 0.95 * _Col0.y, av.g * _Col0.z) : Field(i.cpx);
                col = GammaToLinearSpace(col) * _Gain;   // the colour field is authored in sRGB terms; the canvas is linear
                return float4(col * a + below.rgb * (1 - a), a + below.a * (1 - a));   // premultiplied over; opaque canvas stays 1
            }
            ENDHLSL
        }

        // ---- pass 5: premultiplied to straight alpha (transparent canvases only) --------------
        Pass
        {
            Name "Unpremultiply"
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _Src; float4 _Rect;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(uint vid : SV_VertexID)
            {
                v2f o; float2 cpx = _Rect.xy + Corner(vid) * _Rect.zw;
                o.uv = cpx / _Target.zw; o.pos = ClipFromCanvas(cpx); return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_Src, i.uv);
                return float4(c.rgb / max(c.a, 1e-4), c.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
