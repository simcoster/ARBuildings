// Semi-transparent overlay for Streetscape Geometry, so you can see what ARCore is
// actually serving. Deliberately simple: no geometry shader (those are slow or broken on
// some tile-based mobile GPUs), so this is safe to leave on while walking around.
//
// Faces are shaded by their normal so adjacent surfaces are distinguishable, and the mesh
// stays translucent enough to check alignment against the real building behind it.
Shader "AR/StreetscapeDebug"
{
    Properties
    {
        _Color("Tint", Color) = (0.2, 0.9, 1.0, 0.25)
        _FaceShading("Face Shading", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent"
               "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "StreetscapeDebug"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off        // streetscape meshes are not reliably wound outward

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings   { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _FaceShading;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);

                // Cheap directional shading purely so faces read as separate surfaces.
                float facing = saturate(dot(n, normalize(float3(0.3, 1.0, 0.2))) * 0.5 + 0.5);
                float shade = lerp(1.0, facing, _FaceShading);

                return half4(_Color.rgb * shade, _Color.a);
            }
            ENDHLSL
        }
    }
}
