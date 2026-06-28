Shader "Custom/VoxelAtlas"
{
    Properties
    {
        [MainTexture] _BaseColorMap ("Block Atlas (32px tiles, 16 cols)", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
        }

        // ── Main colour pass ──────────────────────────────────────────────────
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Cull  Back
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target   4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma instancing_options renderinglayer

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            TEXTURE2D(_BaseColorMap);
            SAMPLER(sampler_BaseColorMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float2 uv0        : TEXCOORD1;
                float2 uv1        : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 posWS   = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv0        = IN.uv0;
                OUT.uv1        = IN.uv1;
                return OUT;
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                const float ATLAS_COLS = 16.0;
                const float TILE       = 1.0 / ATLAS_COLS;

                // Half-texel inset so interpolation never bleeds into an
                // adjacent tile at the frac() repeat boundary.
                // Atlas is 512px wide (16 tiles x 32px) → 1 texel = 1/512.
                const float HALF_TEXEL = 0.5 / 512.0;

                float2 local = frac(IN.uv0) * TILE;
                local = clamp(local, HALF_TEXEL, TILE - HALF_TEXEL);

                float4 col = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, local + IN.uv1);

                float3 N     = normalize(IN.normalWS);
                float3 L     = normalize(float3(0.5, 1.0, 0.3));
                float  NdotL = saturate(dot(N, L));
                float  light = 0.3 + 0.7 * NdotL;

                return float4(col.rgb * light, col.a);
            }

            ENDHLSL
        }

        // ── Depth pre-pass ────────────────────────────────────────────────────
        Pass
        {
            Name "DepthForwardOnly"
            Tags { "LightMode" = "DepthForwardOnly" }

            Cull      Back
            ZWrite    On
            ColorMask 0

            HLSLPROGRAM

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target   4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma instancing_options renderinglayer

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            void Frag(Varyings IN) { }

            ENDHLSL
        }

        // ── Shadow caster ─────────────────────────────────────────────────────
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull      Back
            ZWrite    On
            ZTest     LEqual
            ColorMask 0

            HLSLPROGRAM

            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target   4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma instancing_options renderinglayer

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            void Frag(Varyings IN) { }

            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
