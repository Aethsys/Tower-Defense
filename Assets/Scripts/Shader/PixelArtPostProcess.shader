// ─────────────────────────────────────────────────────────────────────────────
//  Hidden/PixelArtPostProcess  —  versión optimizada
//
//  PASE ÚNICO: snap de UV (= resolución limitada) + cuantización de color,
//  todo en un solo Graphics.Blit sin RenderTexture intermedia.
// ─────────────────────────────────────────────────────────────────────────────
Shader "Hidden/PixelArtPostProcess"
{
    Properties
    {
        _MainTex    ("Source",            2D)     = "white" {}
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

            //  _RenderRes   = (ancho, alto, 1/ancho, 1/alto)
            //                 .xy → resolución low-res para snap de UV
            //                 .zw → recíprocos PRECALCULADOS en C# → sin división en shader
            //
            //  _QuantParams = (steps-1,  1/(steps-1),  0,  0)
            //                 .x → STEPS_M1   multiplicador para cuantizar
            //                 .y → STEPS_RCP  divisor final (multiplicamos por él = sin división)
            float4 _RenderRes;
            float4 _QuantParams;

            #define RES_XY    _RenderRes.xy     // (w, h) de la resolución low-res
            #define RES_ZW    _RenderRes.zw     // (1/w, 1/h) — evita dividir en UV snap
            #define STEPS_M1  _QuantParams.x    // colorsPerChannel - 1
            #define STEPS_RCP _QuantParams.y    // 1 / (colorsPerChannel - 1)

            fixed4 frag(v2f_img i) : SV_Target
            {
                // ── ① Snap de UV a la cuadrícula low-res (Pixelación) ────────
                float2 lowResPx  = min(floor(i.uv * RES_XY), RES_XY - 1.0);
                float2 snappedUV = (lowResPx + 0.5) * RES_ZW;

                // ── ② Muestreo de la imagen a resolución reducida ─────────────
                half3 col = (half3)tex2Dlod(_MainTex, float4(snappedUV, 0.0, 0.0)).rgb;

                // ── ③ Limitación / cuantización de color ──────────────────────
                col = floor(col * (half)STEPS_M1 + 0.5h) * (half)STEPS_RCP;

                return fixed4(col, 1.0h);
            }
            ENDCG
        }
    }

    FallBack Off
}
