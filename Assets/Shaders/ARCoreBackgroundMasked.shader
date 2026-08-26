Shader "Unlit/ARCoreBackgroundMasked"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _EnvironmentDepth("Texture", 2D) = "black" {}
        _SemanticMask("Semantic Mask", 2D) = "black" {}
        _MaxOcclusionDistance("Max Occlusion Distance", Float) = 12
        _SegEnabled("Seg Enabled", Float) = 0
        _SegDebug("Seg Debug", Float) = 0
    }

    SubShader
    {
        Name "ARCore Background Masked (Before Opaques) for GLES3"
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "ForceNoShadowCasting" = "True"
        }

        Pass
        {
            Name "AR Camera Background (ARCore Masked)"
            Cull Off
            ZTest Always
            ZWrite On
            Lighting Off
            LOD 100
            Tags { "LightMode" = "Always" }

            GLSLPROGRAM
            #pragma only_renderers gles3
            #pragma multi_compile_local __ ARCORE_ENVIRONMENT_DEPTH_ENABLED
            #pragma multi_compile_local __ ARCORE_IMAGE_STABILIZATION_ENABLED
            #include "UnityCG.glslinc"

#ifdef SHADER_API_GLES3
#extension GL_OES_EGL_image_external_essl3 : require
#endif

#ifndef ARCORE_IMAGE_STABILIZATION_ENABLED
#define ARCORE_TEXCOORD_TYPE vec2
#else
#define ARCORE_TEXCOORD_TYPE vec3
#endif

            uniform mat4 _UnityDisplayTransform;

#ifdef VERTEX
            varying ARCORE_TEXCOORD_TYPE textureCoord;
            void main()
            {
#ifdef SHADER_API_GLES3
                gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
#ifdef ARCORE_IMAGE_STABILIZATION_ENABLED
                textureCoord = gl_MultiTexCoord0.xyz;
#else
                textureCoord = (vec4(gl_MultiTexCoord0.x, gl_MultiTexCoord0.y, 1.0f, 0.0f) * _UnityDisplayTransform).xy;
#endif
#endif
            }
#endif

#ifdef FRAGMENT
            varying ARCORE_TEXCOORD_TYPE textureCoord;
            uniform samplerExternalOES _MainTex;
            uniform float _UnityCameraForwardScale;
            uniform sampler2D _SemanticMask;
            uniform float _MaxOcclusionDistance;
            uniform float _SegEnabled;
            uniform float _SegDebug;

#ifdef ARCORE_ENVIRONMENT_DEPTH_ENABLED
            uniform sampler2D _EnvironmentDepth;
#endif

#if defined(SHADER_API_GLES3) && !defined(UNITY_COLORSPACE_GAMMA)
            vec3 GammaToLinearSpace(vec3 sRGB)
            {
                return sRGB * (sRGB * (sRGB * 0.305306011F + 0.682171111F) + 0.012522878F);
            }
#endif

            float ConvertDistanceToDepth(float d)
            {
                d = _UnityCameraForwardScale > 0.0 ? _UnityCameraForwardScale * d : d;
                float zBufferParamsW = 1.0 / _ProjectionParams.y;
                float zBufferParamsY = _ProjectionParams.z * zBufferParamsW;
                float zBufferParamsX = 1.0 - zBufferParamsY;
                float zBufferParamsZ = zBufferParamsX * _ProjectionParams.w;
                return (d < _ProjectionParams.y) ? 1.0f : ((1.0 / zBufferParamsZ) * ((1.0 / d) - zBufferParamsW));
            }

            bool DepthMayOcclude(float distance)
            {
                if (distance <= 0.001) return false;
                if (_MaxOcclusionDistance > 0.0 && distance >= _MaxOcclusionDistance) return false;
                return true;
            }

            void main()
            {
#ifdef SHADER_API_GLES3
#ifdef ARCORE_IMAGE_STABILIZATION_ENABLED
                vec2 tc = textureCoord.xy / textureCoord.z;
#else
                vec2 tc = textureCoord;
#endif
                vec3 result = texture(_MainTex, tc).xyz;
                float depth = 1.0;
                vec4 seg = texture(_SemanticMask, tc);

#ifdef ARCORE_ENVIRONMENT_DEPTH_ENABLED
                float distance = texture(_EnvironmentDepth, tc).x;
                if (_SegEnabled > 0.5)
                {
                    if (seg.a > 0.5 && DepthMayOcclude(distance))
                        depth = ConvertDistanceToDepth(distance);
                }
                else if (DepthMayOcclude(distance))
                {
                    depth = ConvertDistanceToDepth(distance);
                }
#endif
                // Paint every THING the model labelled. Independent of depth so a
                // `seg on` toggle is visible even if occlusion is off.
                if (_SegEnabled > 0.5 && dot(seg.rgb, vec3(1.0)) > 0.05)
                    result = mix(result, seg.rgb, 0.55);

#ifndef UNITY_COLORSPACE_GAMMA
                result = GammaToLinearSpace(result);
#endif
                gl_FragColor = vec4(result, 1.0);
                gl_FragDepth = depth;
#endif
            }
#endif
            ENDGLSL
        }
    }

    SubShader
    {
        Name "ARCore Background Masked (Before Opaques) for Vulkan"
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "ForceNoShadowCasting" = "True"
        }

        Pass
        {
            Name "AR Camera Background (ARCore Masked)"
            Cull Off
            ZTest Always
            ZWrite On
            Lighting Off
            LOD 100
            Tags { "LightMode" = "Always" }

            HLSLPROGRAM
            #pragma only_renderers vulkan
            #pragma multi_compile_local __ ARCORE_ENVIRONMENT_DEPTH_ENABLED
            #pragma multi_compile_local __ ARCORE_IMAGE_STABILIZATION_ENABLED
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag

#ifndef ARCORE_IMAGE_STABILIZATION_ENABLED
#define ARCORE_TEXCOORD_TYPE float2
#else
#define ARCORE_TEXCOORD_TYPE float3
#endif

            float4x4 _UnityDisplayTransform;

            struct vertexInput
            {
                float4 vertex : POSITION;
                float3 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                ARCORE_TEXCOORD_TYPE textureCoord : TEXCOORD0;
            };

            v2f vert(vertexInput i)
            {
                v2f o;
                o.position = UnityObjectToClipPos(i.vertex.xyz);
#ifdef ARCORE_IMAGE_STABILIZATION_ENABLED
                o.textureCoord = i.uv.xyz;
#else
                o.textureCoord = mul(float4(i.uv.x, i.uv.y, 1.0f, 0.0f), _UnityDisplayTransform).xy;
#endif
                return o;
            }

            sampler2D _MainTex;
            float _UnityCameraForwardScale;
            sampler2D _SemanticMask;
            float _MaxOcclusionDistance;
            float _SegEnabled;
            float _SegDebug;

#ifdef ARCORE_ENVIRONMENT_DEPTH_ENABLED
            sampler2D _EnvironmentDepth;
#endif

#ifndef UNITY_COLORSPACE_GAMMA
            float3 GammaToLinearSpace(float3 sRGB)
            {
                return sRGB * (sRGB * (sRGB * 0.305306011F + 0.682171111F) + 0.012522878F);
            }
#endif

            float ConvertDistanceToDepth(float d)
            {
                d = _UnityCameraForwardScale > 0.0 ? _UnityCameraForwardScale * d : d;
                float zBufferParamsW = 1.0 / _ProjectionParams.y;
                float zBufferParamsY = _ProjectionParams.z * zBufferParamsW;
                float zBufferParamsX = 1.0 - zBufferParamsY;
                float zBufferParamsZ = zBufferParamsX * _ProjectionParams.w;
                return (d < _ProjectionParams.y) ? 1.0f : ((1.0 / zBufferParamsZ) * ((1.0 / d) - zBufferParamsW));
            }

            bool DepthMayOcclude(float distance)
            {
                if (distance <= 0.001) return false;
                if (_MaxOcclusionDistance > 0.0 && distance >= _MaxOcclusionDistance) return false;
                return true;
            }

            struct fragOutput
            {
                float4 color : SV_Target;
                float depth : SV_Depth;
            };

            fragOutput frag(v2f i)
            {
#ifdef ARCORE_IMAGE_STABILIZATION_ENABLED
                float2 tc = i.textureCoord.xy / i.textureCoord.z;
#else
                float2 tc = i.textureCoord;
#endif
                float3 result = tex2D(_MainTex, tc).xyz;
                float depth = 1.0;
                float4 seg = tex2D(_SemanticMask, tc);

#ifdef ARCORE_ENVIRONMENT_DEPTH_ENABLED
                float distance = tex2D(_EnvironmentDepth, tc).x;
                if (_SegEnabled > 0.5)
                {
                    if (seg.a > 0.5 && DepthMayOcclude(distance))
                        depth = ConvertDistanceToDepth(distance);
                }
                else if (DepthMayOcclude(distance))
                {
                    depth = ConvertDistanceToDepth(distance);
                }
#endif
                if (_SegEnabled > 0.5 && dot(seg.rgb, float3(1, 1, 1)) > 0.05)
                    result = lerp(result, seg.rgb, 0.55);

#ifndef UNITY_COLORSPACE_GAMMA
                result = GammaToLinearSpace(result);
#endif
                fragOutput o;
                o.color = float4(result, 1.0);
                o.depth = 1.0 - depth;
                return o;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
