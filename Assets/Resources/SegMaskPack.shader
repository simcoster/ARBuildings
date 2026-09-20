Shader "Hidden/SegMaskPack"
{
    Properties
    {
        _Matte("Matte", 2D) = "black" {}
        _ScalarFloor("Floor", Float) = 8
        _PackedMetres("Metres", Float) = 2
        _MaxDist("Max Dist", Float) = 12
        _Inset("Inset XY", Vector) = (0,0,0,0)
        _MatteSize("Matte Size", Vector) = (1024,1024,0,0)
        _MaskSize("Mask Size", Vector) = (1024,1024,0,0)
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Matte;
            float _ScalarFloor;
            float _PackedMetres;
            float _MaxDist;
            float4 _Inset;
            float4 _MatteSize;
            float4 _MaskSize;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 Ramp(float t)
            {
                t = saturate(t);
                float r = saturate(1.5 - abs(4 * t - 3));
                float g = saturate(1.5 - abs(4 * t - 2));
                float b = saturate(1.5 - abs(4 * t - 1));
                return float3(r, g, b);
            }

            float4 frag(v2f i) : SV_Target
            {
                float maskW = max(_MaskSize.x, 1);
                float maskH = max(_MaskSize.y, 1);
                float mx = i.uv.x * maskW;
                float my = i.uv.y * maskH;
                float x0 = _Inset.x;
                float y0 = _Inset.y;
                float mw = _MatteSize.x;
                float mh = _MatteSize.y;
                if (mx < x0 || my < y0 || mx >= x0 + mw || my >= y0 + mh)
                    return float4(0, 0, 0, 0);

                float2 mu = float2((mx - x0 + 0.5) / mw, (my - y0 + 0.5) / mh);
                float v = tex2D(_Matte, mu).r;
                float code = saturate(v) * 255.0;
                if (code < _ScalarFloor)
                    return float4(0, 0, 0, 0);

                float3 rgb = Ramp(saturate(v));
                float scale = _MaxDist > 0 ? _MaxDist : 16;
                float a = 0;
                if (_PackedMetres > 0)
                    a = saturate(_PackedMetres / scale);
                a = max(a, 1.0 / 255.0);
                return float4(rgb, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
