Shader "Hidden/SegDisBlit"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Mean("Mean", Float) = 127.5
        _Scale("Scale", Float) = 255
        _Normalize("Normalize", Float) = 1
        _CropScale("Crop Scale", Vector) = (1,1,0,0)
        _CropOffset("Crop Offset", Vector) = (0,0,0,0)
        _Luma("Luma", Float) = 0
    }

    SubShader
    {
        Name "Seg DIS blit GLES3 OES"
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            GLSLPROGRAM
            #pragma only_renderers gles3
            #include "UnityCG.glslinc"
#ifdef SHADER_API_GLES3
            #extension GL_OES_EGL_image_external_essl3 : require
#endif
            uniform mat4 _UnityDisplayTransform;
            uniform vec4 _CropScale;
            uniform vec4 _CropOffset;
            uniform float _Mean;
            uniform float _Scale;
            uniform float _Normalize;
            uniform float _Luma;

#ifdef VERTEX
            varying vec2 vUv;
            void main()
            {
                gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
                vUv = gl_MultiTexCoord0.xy;
            }
#endif
#ifdef FRAGMENT
            varying vec2 vUv;
#ifdef SHADER_API_GLES3
            uniform samplerExternalOES _MainTex;
#endif
            void main()
            {
#ifdef SHADER_API_GLES3
                vec2 tc = (vec4(vUv.x, vUv.y, 1.0, 0.0) * _UnityDisplayTransform).xy;
                tc = tc * _CropScale.xy + _CropOffset.xy;
                vec3 rgb = texture(_MainTex, tc).xyz;
                if (_Normalize > 0.5)
                {
                    float s = _Scale == 0.0 ? 1.0 : _Scale;
                    rgb = (rgb * 255.0 - vec3(_Mean)) / s;
                }
                if (_Luma > 0.5)
                {
                    float y = dot(rgb, vec3(0.299, 0.587, 0.114));
                    gl_FragColor = vec4(y, y, y, 1.0);
                }
                else
                    gl_FragColor = vec4(rgb, 1.0);
#endif
            }
#endif
            ENDGLSL
        }
    }

    SubShader
    {
        Name "Seg DIS blit 2D"
        Tags { "Queue" = "Overlay" "RenderType" = "Opaque" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _CropScale;
            float4 _CropOffset;
            float _Mean;
            float _Scale;
            float _Normalize;
            float _Luma;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv * _CropScale.xy + _CropOffset.xy;
                float3 rgb = tex2D(_MainTex, uv).rgb;
                if (_Normalize > 0.5)
                {
                    float s = _Scale == 0 ? 1 : _Scale;
                    rgb = (rgb * 255.0 - _Mean) / s;
                }
                if (_Luma > 0.5)
                {
                    float y = dot(rgb, float3(0.299, 0.587, 0.114));
                    return float4(y, y, y, 1);
                }
                return float4(rgb, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
