Shader "AR/ShadowCatcher"
{
    Properties
    {
        _ShadowStrength("Shadow Strength", Range(0,1)) = 0.7

        // Diagnostic: paints the catcher by the shadow term instead of shading with it.
        // "No shadow appears" has two opposite causes that look identical from a screenshot —
        // the shadow map never being sampled (atten stays 1, catcher invisible) or the model
        // simply not casting into it. Green = lit, red = shadowed, so one frame tells them
        // apart. Off by default; turn on with `catcher on` over RemoteControl.
        _Debug("Debug Attenuation", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-100"
               "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off        // a ground plane seen from the wrong side must still catch

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            float _ShadowStrength;
            float _Debug;

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
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                half   atten       = mainLight.shadowAttenuation;

                // Solid green where the shadow map says LIT, solid red where it says shadowed.
                // A uniformly green quad means the catcher works and nothing is casting into
                // it; no quad at all means the catcher is not drawing; red under the building
                // means it works and the shadow was simply too subtle to see.
                if (_Debug > 0.5)
                    return half4(1.0 - atten, atten, 0.0, 0.55);

                // Slightly blue rather than pure black: real outdoor shadow fill is
                // sky-coloured, and a pure-black shadow always reads too strong.
                return half4(0.02, 0.03, 0.05, (1.0 - atten) * _ShadowStrength);
            }
            ENDHLSL
        }
    }
}
