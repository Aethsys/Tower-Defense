// ─────────────────────────────────────────────────────────────────────────────
//  Hidden/PixelArtPostProcess
//
//  SINGLE PASS: UV snap (pixelation) + optional cell filtering + ordered
//  (Bayer 4x4) dithering + color quantization in a single Graphics.Blit
//  without an intermediate RenderTexture.
// ─────────────────────────────────────────────────────────────────────────────
Shader "Hidden/PixelArtPostProcess"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert_img
            #pragma fragment frag
            #pragma target   3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            // _RenderRes    = (width, height, 1/width, 1/height)
            // _QuantParams  = (steps - 1, 1 / (steps - 1), 0, 0)
            // _EffectParams = (dither strength, cell filter amount [0..1], 0, 0)
            CBUFFER_START(PixelArtProperties)
            float4 _RenderRes;
            float4 _QuantParams;
            float4 _EffectParams;
            CBUFFER_END

            #define RES_XY     _RenderRes.xy
            #define RES_ZW     _RenderRes.zw
            #define STEPS_M1   _QuantParams.x
            #define STEPS_RCP  _QuantParams.y
            #define DITHER_AMT _EffectParams.x
            #define FILTER_AMT _EffectParams.y

            // Bayer 4x4 normalized to (-0.5, 0.5)
            static const float BAYER4[16] =
            {
                -0.46875,  0.03125, -0.34375,  0.15625,
                 0.28125, -0.21875,  0.40625, -0.09375,
                -0.28125,  0.21875, -0.40625,  0.09375,
                 0.46875, -0.03125,  0.34375, -0.15625
            };

            fixed4 frag(v2f_img i) : SV_Target
            {
                // 1. Snap UV to the low-res grid (Pixelation)
                float2 lowResPx  = min(floor(i.uv * RES_XY), RES_XY - 1.0);
                float2 snappedUV = (lowResPx + 0.5) * RES_ZW;

                // 2. Sample source at the cell center. With the filter enabled, four bilinear taps
                //    spread inside the cell approximate its average, which removes shimmering/aliasing.
                half3 col;
                if (FILTER_AMT > 0.001)
                {
                    float2 o = RES_ZW * (0.25 * FILTER_AMT);
                    col = (half3)tex2Dlod(_MainTex, float4(snappedUV + float2(-o.x, -o.y), 0.0, 0.0)).rgb;
                    col += (half3)tex2Dlod(_MainTex, float4(snappedUV + float2( o.x, -o.y), 0.0, 0.0)).rgb;
                    col += (half3)tex2Dlod(_MainTex, float4(snappedUV + float2(-o.x,  o.y), 0.0, 0.0)).rgb;
                    col += (half3)tex2Dlod(_MainTex, float4(snappedUV + float2( o.x,  o.y), 0.0, 0.0)).rgb;
                    col *= 0.25h;
                }
                else
                {
                    col = (half3)tex2Dlod(_MainTex, float4(snappedUV, 0.0, 0.0)).rgb;
                }

                // 3. Ordered dithering: offsets by up to half a quantization step before rounding
                if (DITHER_AMT > 0.001)
                {
                    uint2 p = (uint2)lowResPx & 3u;
                    col += (half)(BAYER4[p.y * 4u + p.x] * DITHER_AMT * STEPS_RCP);
                }

                // 4. Color quantization
                col = floor(saturate(col) * (half)STEPS_M1 + 0.5h) * (half)STEPS_RCP;

                return fixed4(col, 1.0h);
            }
            ENDCG
        }
    }

    FallBack Off
}
