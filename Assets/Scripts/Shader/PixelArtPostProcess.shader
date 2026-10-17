// ─────────────────────────────────────────────────────────────────────────────
//  Hidden/PixelArtPostProcess
//
//  SINGLE PASS: UV snap (pixelation) + color quantization in a single
//  Graphics.Blit without an intermediate RenderTexture.
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

            // _RenderRes   = (width, height, 1/width, 1/height)
            // _QuantParams = (steps - 1, 1 / (steps - 1), 0, 0)
            float4 _RenderRes;
            float4 _QuantParams;

            #define RES_XY    _RenderRes.xy
            #define RES_ZW    _RenderRes.zw
            #define STEPS_M1  _QuantParams.x
            #define STEPS_RCP _QuantParams.y

            fixed4 frag(v2f_img i) : SV_Target
            {
                // 1. Snap UV to the low-res grid (Pixelation)
                float2 lowResPx  = min(floor(i.uv * RES_XY), RES_XY - 1.0);
                float2 snappedUV = (lowResPx + 0.5) * RES_ZW;

                // 2. Sample source texture at the snapped texel center
                half3 col = (half3)tex2Dlod(_MainTex, float4(snappedUV, 0.0, 0.0)).rgb;

                // 3. Color quantization
                col = floor(col * (half)STEPS_M1 + 0.5h) * (half)STEPS_RCP;

                return fixed4(col, 1.0h);
            }
            ENDCG
        }
    }

    FallBack Off
}
