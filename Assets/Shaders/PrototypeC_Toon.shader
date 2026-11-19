// PrototypeC_Toon.shader —— Prototype C：可复用卡通化 Shader（URP）
// 表现：albedo 贴图保留；主光 NdotL 做色阶量化（有限色盘）+ 弱平滑过渡；
// 前景/背景两个风格档由 _StyleBlend 切换（0=前景，1=背景，背景压低对比/饱和/亮度）。
// _Saturation / _RimStrength 只作用于前景档（背景走 _BackdropSaturation 等），
// 用于让 AI 生成物从木屋背景里跳出来：饱和度 1.18、边缘光 0.22。
// 批量指定走 ToonMaterials.Apply()（见 Assets/Scripts/C/Rendering/ToonMaterials.cs）。
Shader "PrototypeC/Toon"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        [Header(Toon)]
        _Bands ("Light Bands", Range(1, 5)) = 3
        _RampSoftness ("Ramp Softness", Range(0.001, 0.5)) = 0.06
        _ShadeTint ("Shade Tint", Color) = (0.62, 0.68, 0.88, 1)
        _AmbientBoost ("Ambient Boost", Range(0, 1)) = 0.18
        _Contrast ("Contrast", Range(0.5, 2)) = 1.1
        _Saturation ("Saturation", Range(0, 1.6)) = 1.18
        _RimStrength ("Rim Light", Range(0, 1)) = 0.22

        [Header(Style)]
        _StyleBlend ("Foreground 0 / Backdrop 1", Range(0, 1)) = 0
        _BackdropDim ("Backdrop Dim", Range(0, 1)) = 0.3
        _BackdropSaturation ("Backdrop Saturation", Range(0, 1)) = 0.65
        _BackdropContrast ("Backdrop Contrast", Range(0.5, 1.5)) = 0.9

        [Header(Warm Neutralize)]
        _NeutralStrength ("Neutralize (desaturate)", Range(0, 1)) = 0
        _NeutralMaxSat ("Target Max Saturation", Range(0, 0.6)) = 0.22
        _NeutralLighten ("Lighten Toward White", Range(0, 1)) = 0.3
        _WarmHueMin ("Warm Hue Min", Range(0, 1)) = 0.0694
        _WarmHueMax ("Warm Hue Max", Range(0, 1)) = 0.1944
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct ToonAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
        };

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _ShadeTint;
            half _Bands;
            half _RampSoftness;
            half _AmbientBoost;
            half _Contrast;
            half _Saturation;
            half _RimStrength;
            half _StyleBlend;
            half _BackdropDim;
            half _BackdropSaturation;
            half _BackdropContrast;
            half _Cutoff;
            half _NeutralStrength;
            half _NeutralMaxSat;
            half _NeutralLighten;
            half _WarmHueMin;
            half _WarmHueMax;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ToonVert
            #pragma fragment ToonFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"

            struct ToonVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            ToonVaryings ToonVert(ToonAttributes input)
            {
                ToonVaryings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS);

                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = nrm.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            // 把 [0,1] 的亮度量化成 bands 档；softness 控制档位边界的过渡宽度
            half QuantizeStep(half value, half bands, half softness)
            {
                half scaled = saturate(value) * bands;
                half index = floor(scaled);
                half fraction = scaled - index;
                half blend = smoothstep(0.5 - softness, 0.5 + softness, fraction);
                return saturate((index + blend) / bands);
            }

            // 黄/褐/土色中性化：只对暖色像素「降饱和 + 提亮」，向浅木/米白/灰白收敛。
            // 色相完全不动，所以不会把木色染成绿；区间外的红/粉/绿/蓝一个像素都不碰。
            half3 RgbToHsv(half3 c)
            {
                half4 K = half4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                half4 p = lerp(half4(c.bg, K.wz), half4(c.gb, K.xy), step(c.b, c.g));
                half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));
                half d = q.x - min(q.w, q.y);
                half e = 1.0e-10;
                return half3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            half3 HsvToRgb(half3 c)
            {
                half4 K = half4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                half3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            half3 NeutralizeWarm(half3 rgb)
            {
                if (_NeutralStrength <= 0.0001 && _NeutralLighten <= 0.0001) return rgb;

                // 硬边界：只允许 25°~70°（黄/褐/土黄）。红色 0°~20°、粉色/品红 300°~360°、绿色 90°~160° 都不在区间内
                half lo = max(_WarmHueMin, 0.0694);
                half hi = min(_WarmHueMax, 0.1944);
                if (hi <= lo) return rgb;

                half3 hsv = RgbToHsv(saturate(rgb));

                half inWarm = step(lo, hsv.x) * (1.0 - step(hi, hsv.x));
                inWarm *= step(0.08, hsv.y); // 本来就接近灰白的像素不用管
                if (inWarm <= 0.0001) return rgb;

                // 只降不升：把饱和度压向目标上限（目标值不超过当前值）
                half targetSat = min(hsv.y, _NeutralMaxSat);
                hsv.y = lerp(hsv.y, targetSat, inWarm * _NeutralStrength);

                // 提亮：按「到白色的剩余距离」比例抬，保留原有的明暗关系与纹理层次
                hsv.z = saturate(hsv.z + (1.0 - hsv.z) * _NeutralLighten * inWarm);

                return HsvToRgb(hsv);
            }

            half4 ToonFrag(ToonVaryings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                albedo.rgb = NeutralizeWarm(albedo.rgb);

                #ifdef _ALPHATEST_ON
                    clip(albedo.a - _Cutoff);
                #endif

                half3 normalWS = normalize(input.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndl = saturate(dot(normalWS, mainLight.direction));
                half litMask = ndl * mainLight.shadowAttenuation;
                half ramp = QuantizeStep(litMask, max(1.0, _Bands), _RampSoftness);

                half3 ambient = SampleSH(normalWS) + _AmbientBoost;
                half3 shadeColor = albedo.rgb * _ShadeTint.rgb * ambient;
                half3 litColor = albedo.rgb * (mainLight.color * mainLight.distanceAttenuation + ambient);
                half3 color = lerp(shadeColor, litColor, ramp);

                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                half rim = pow(1.0 - saturate(dot(normalWS, viewDirWS)), 4.0);
                color += rim * _RimStrength * mainLight.color;

                // 前景 / 背景两个风格档
                half contrast = lerp(_Contrast, _BackdropContrast, _StyleBlend);
                half saturation = lerp(_Saturation, _BackdropSaturation, _StyleBlend);
                half dim = lerp(1.0, 1.0 - _BackdropDim, _StyleBlend);

                half luma = dot(color, half3(0.299, 0.587, 0.114));
                color = lerp(luma.xxx, color, saturation);
                color = (color - 0.5) * contrast + 0.5;
                color = max(color * dim, 0);

                return half4(color, albedo.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            float4 GetShadowPositionHClip(ToonAttributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            ShadowVaryings ShadowVert(ToonAttributes input)
            {
                ShadowVaryings output;
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_fragment _ _ALPHATEST_ON

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            DepthVaryings DepthVert(ToonAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(alpha - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
