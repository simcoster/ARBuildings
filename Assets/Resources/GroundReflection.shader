Shader "AR/GroundReflection"
{
    // The reflection is a SCREEN-SPACE lookup, not a texture on the quad: the mirror camera
    // renders the model from the reflected viewpoint into _ReflectionTex, and every pixel of
    // the ground reads the same screen position it occupies itself. That is what makes the
    // reflection line up with the model above it without any UV work on the quad.
    Properties
    {
        _ReflectionTex ("Reflection", 2D) = "black" {}

        // Both behind material properties on purpose. A semi-gloss tile floor wants a
        // different number from polished stone, and finding it is a job of pushing a value
        // twenty times over adb — which is free, where a shader edit costs a 15-minute build.
        _Strength ("Strength", Range(0,1)) = 0.25
        _FadeDistance ("Fade distance (m)", Float) = 6.0
        _Tint ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        // Transparent and depth-write off: this draws ON TOP of the camera feed, and must
        // never occlude the model it is reflecting.
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "GroundReflection"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_ReflectionTex);
            SAMPLER(sampler_ReflectionTex);

            CBUFFER_START(UnityPerMaterial)
                float _Strength;
                float _FadeDistance;
                float4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos  : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv         : TEXCOORD2;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs p = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = p.positionCS;
                OUT.positionWS = p.positionWS;
                OUT.screenPos = ComputeScreenPos(p.positionCS);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);

                half4 reflection = SAMPLE_TEXTURE2D(_ReflectionTex, sampler_ReflectionTex, uv);

                // The mirror camera clears to transparent black, so alpha is already "is
                // there any model here" — no masking needed beyond it.
                half mask = reflection.a;

                // Fade with distance from the quad's centre, in the quad's own UV space.
                // A real floor reflection is strongest at the object's feet and gone a few
                // metres out; without this the rectangle's hard edge gives the trick away.
                float2 fromCentre = (IN.uv - 0.5) * 2.0;
                half radial = saturate(1.0 - dot(fromCentre, fromCentre));

                half alpha = mask * radial * _Strength;

                return half4(reflection.rgb * _Tint.rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
