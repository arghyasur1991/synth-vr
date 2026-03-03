// Passthrough Relighting with depth write for MR occlusion.
// Based on Meta's HighlightsAndShadows shader with two changes:
//   1. ZWrite On  (writes depth so virtual objects are occluded behind room walls)
//   2. Queue = Geometry-1  (renders before virtual objects for correct depth testing)
// Shadow/highlight alpha still works: Quest compositor uses alpha to darken passthrough.

Shader "Sentience/PTRLWithDepth"
{
    Properties
    {
        _ShadowIntensity ("Shadow Intensity", Range (0, 1)) = 0.7
        _HighLightAttenuation ("Highlight Attenuation", Range (0, 1)) = 0.5
        _HighlightOpacity("Highlight Opacity", Range (0, 1)) = 0.25
        _EnvironmentDepthBias("Environment Depth Bias", Range (0, 1)) = 0.06
    }

    SubShader
    {
        PackageRequirements
        {
            "com.unity.render-pipelines.universal"
        }
        Tags
        {
            "RenderPipeline"="UniversalPipeline" "Queue"="Geometry-1"
        }
        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Blend One OneMinusSrcAlpha
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex ShadowReceiverVertex
            #pragma fragment ShadowReceiverFragment

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            float _HighLightAttenuation;
            float _ShadowIntensity;
            float _HighlightOpacity;
            float _EnvironmentDepthBias;

            struct Attributes {
                float4 positionOS : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float3 normalWS : NORMAL;
                float3 positionWS : TEXCOORD0;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowReceiverVertex(Attributes input) {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                const VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalize(mul(unity_ObjectToWorld, float4(input.normal, 0.0)).xyz);
                return output;
            }

            half4 ShadowReceiverFragment(const Varyings input) : SV_Target {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = input.normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                half3 color = half3(0, 0, 0);
                half mainLightShadowAttenuation;

                VertexPositionInputs vertexInput = (VertexPositionInputs)0;
                vertexInput.positionWS = input.positionWS;
                const float4 shadowCoord = GetShadowCoord(vertexInput);
                mainLightShadowAttenuation = MainLightRealtimeShadow(shadowCoord);
                half alpha = (1 - mainLightShadowAttenuation) * _ShadowIntensity;
                half finalAlpha = alpha;

#if defined(_ADDITIONAL_LIGHTS)
                float lightAlpha = 0;
#if USE_CLUSTER_LIGHT_LOOP
                UNITY_LOOP for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1,1,1,1));
                    float ndtol = saturate(dot(light.direction, input.normalWS));
                    lightAlpha = light.distanceAttenuation * ndtol * _HighLightAttenuation * light.shadowAttenuation * _HighlightOpacity;
                    color += light.color * lightAlpha * (1-alpha);
                }
#endif
                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, float4(0, 0, 0, 0));
                    float ndtol = saturate(dot(light.direction, input.normalWS));
                    lightAlpha = light.distanceAttenuation * ndtol * _HighLightAttenuation * light.shadowAttenuation * _HighlightOpacity;
                    color += light.color * lightAlpha * (1-alpha);
                LIGHT_LOOP_END
#endif
                float occlusionValue = META_DEPTH_GET_OCCLUSION_VALUE_WORLDPOS(input.positionWS, _EnvironmentDepthBias);
                return half4(color * occlusionValue, finalAlpha * occlusionValue);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
