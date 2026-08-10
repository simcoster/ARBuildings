Shader "AR/GhostWireframe"
{
    Properties
    {
        _WireColour("Wire Colour",  Color) = (0.35, 0.75, 1.0, 1.0)
        _FillColour("Fill Colour",  Color) = (0.35, 0.75, 1.0, 0.06)
        _WireWidth ("Wire Width",   Range(0.5, 4)) = 1.4
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+10"
               "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha One          // additive — ghost reads as light, not paint
            ZWrite Off
            ZTest Always                // draw over the solid model so the cage stays visible
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct V2G { float4 positionCS : SV_POSITION; };
            struct G2F { float4 positionCS : SV_POSITION; float3 bary : TEXCOORD0; };

            float4 _WireColour, _FillColour;
            float  _WireWidth;

            V2G vert(Attributes IN)
            {
                V2G o;
                o.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle V2G i[3], inout TriangleStream<G2F> stream)
            {
                G2F o;
                float3 bary[3] = { float3(1,0,0), float3(0,1,0), float3(0,0,1) };
                [unroll] for (int k = 0; k < 3; k++)
                {
                    o.positionCS = i[k].positionCS;
                    o.bary = bary[k];
                    stream.Append(o);
                }
            }

            half4 frag(G2F i) : SV_Target
            {
                // Screen-space-consistent line width via derivatives
                float3 d = fwidth(i.bary);
                float3 a = smoothstep(float3(0,0,0), d * _WireWidth, i.bary);
                float  wire = 1.0 - min(min(a.x, a.y), a.z);

                half4 col = lerp(_FillColour, _WireColour, wire);
                col.a = max(_FillColour.a, wire * _WireColour.a);
                return col;
            }
            ENDHLSL
        }
    }
}