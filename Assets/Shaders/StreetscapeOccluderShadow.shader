// Streetscape Geometry stand-in for the real world (Step 10 + 11.4).
//
// Three passes:
//   1. Depth-only occluder  — real buildings correctly hide the model behind them.
//   2. Shadow receiver      — the model's shadow lands on real terrain and facades.
//   3. ShadowCaster         — and, more importantly, real neighbours shade the model.
//
// The reciprocal case matters more than the first: if the building next door puts the
// site in shade at 4pm but the model renders in full sun, that reads as fake immediately.
//
// CUTOUT: all three passes discard fragments inside a world-space box supplied by
// StreetscapeShadowSetup, so the volume the replacement building occupies stops occluding,
// shadowing and receiving. This replaces picking a whole mesh to ghost — which only works
// when ARCore actually serves a Building mesh for the site, and at some sites it serves
// none at all. Cutting by volume works regardless of how the geometry happens to be split.
Shader "AR/StreetscapeOccluderShadow"
{
    Properties
    {
        _ShadowStrength("Shadow Strength", Range(0,1)) = 0.7
        _ShadowTint("Shadow Tint", Color) = (0.02, 0.03, 0.05, 1)
    }

    SubShader
    {
        // Before the opaque model (2000) so the depth prepass below can reject it.
        Tags { "RenderType"="Opaque" "Queue"="Geometry-100"
               "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Set globally, so every streetscape instance shares one box with no per-material
        // plumbing. Declared outside UnityPerMaterial: these are not material properties and
        // must not go in the SRP Batcher's per-material buffer.
        float4x4 _OccluderCutoutWorldToLocal;
        float    _OccluderCutoutOn;

        // The matrix maps the box to a unit cube centred on the origin, so the test is a
        // simple bounds check and rotation comes along for free.
        bool InsideCutout(float3 positionWS)
        {
            if (_OccluderCutoutOn < 0.5) return false;

            float3 p = mul(_OccluderCutoutWorldToLocal, float4(positionWS, 1.0)).xyz;
            return all(abs(p) <= 0.5);
        }
        ENDHLSL

        // ---------------------------------------------------------- 1. occluder
        Pass
        {
            Name "Occluder"
            Tags { "LightMode"="SRPDefaultUnlit" }

            ZWrite On
            ZTest LEqual
            ColorMask 0        // depth only — never paint over the camera feed
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Inside the replacement building's volume the real geometry must not write
                // depth, or it carves holes out of the model standing in the same place.
                if (InsideCutout(IN.positionWS)) discard;
                return 0;
            }
            ENDHLSL
        }

        // --------------------------------------------------- 2. shadow receiver
        Pass
        {
            Name "ShadowReceive"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual       // co-planar with the occluder pass above
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float  _ShadowStrength;
                float4 _ShadowTint;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Cut here too: a shadow drawn on a surface we have just made invisible is
                // a shadow floating in mid-air.
                if (InsideCutout(IN.positionWS)) discard;

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                half   atten       = mainLight.shadowAttenuation;

                return half4(_ShadowTint.rgb, (1.0 - atten) * _ShadowStrength);
            }
            ENDHLSL
        }

        // ----------------------------------------------------- 3. shadow caster
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Streetscape meshes arrive without normals — StreetscapeShadowSetup calls
            // RecalculateNormals() so the bias below has something to work with.
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            float3 _LightDirection;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);

                float4 positionCS =
                    TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionCS = positionCS;
                OUT.positionWS = positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // The demolished building must not go on casting a shadow the replacement
                // should be casting instead.
                if (InsideCutout(IN.positionWS)) discard;
                return 0;
            }
            ENDHLSL
        }
    }
}
