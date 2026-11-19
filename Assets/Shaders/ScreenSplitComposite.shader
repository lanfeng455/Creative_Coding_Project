Shader "Hidden/ScreenSplitPost"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "ScreenSplitPost"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // 分屏
            float _SplitY;      // 分界线，0 = 屏幕底部，1 = 屏幕顶部
            float _Feather;     // 分界柔化

            // 上层（天空）
            float _UTemperature;
            float _UTint;
            float _USaturation;
            float _UContrast;
            float _UBloom;
            float4 _ULift;
            float4 _UGamma;
            float4 _UGain;
            float4 _UShadowTint;
            float4 _UHighlightTint;

            // 下层（房间）
            float _LTemperature;
            float _LTint;
            float _LSaturation;
            float _LContrast;
            float _LBloom;
            float4 _LLift;
            float4 _LGamma;
            float4 _LGain;
            float4 _LShadowTint;
            float4 _LHighlightTint;

            // 辉光（上下共用阈值与半径，强度各自设置）
            float _BloomThreshold;
            float _BloomRadius;

            static const float2 kBloomOffsets[12] =
            {
                float2( 1.0,  0.0), float2(-1.0,  0.0),
                float2( 0.0,  1.0), float2( 0.0, -1.0),
                float2( 0.7071,  0.7071), float2(-0.7071,  0.7071),
                float2( 0.7071, -0.7071), float2(-0.7071, -0.7071),
                float2( 0.5000,  0.8660), float2(-0.5000,  0.8660),
                float2( 0.5000, -0.8660), float2(-0.5000, -0.8660)
            };

            float3 SampleScene(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, saturate(uv), 0.0).rgb;
            }

            float3 ApplyLook(float3 c,
                             float temperature, float tint,
                             float3 lift, float3 gamma, float3 gain,
                             float3 shadowTint, float3 highlightTint,
                             float saturation, float contrast)
            {
                // 色温 / 色调
                float t = temperature * 0.0012;
                float g = tint * 0.0012;
                c *= float3(1.0 + t, 1.0 + g, 1.0 - t);

                // Lift / Gamma / Gain
                c = max(0.0, c * gain + lift);
                c = pow(c, 1.0 / max(gamma, 1e-3));

                // 分离色调
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                float3 shT = shadowTint * 2.0 - 1.0;
                float3 hiT = highlightTint * 2.0 - 1.0;
                float wSh = pow(saturate(1.0 - luma), 2.0);
                float wHi = pow(saturate(luma), 2.0);
                c += (shT * wSh + hiT * wHi) * 0.25;

                // 饱和度 / 对比度
                luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                c = lerp(luma.xxx, c, saturation);
                c = (c - 0.5) * contrast + 0.5;

                return max(0.0, c);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float3 baseColor = SampleScene(uv);

                float3 bloom = 0.0;
                [unroll]
                for (int i = 0; i < 12; i++)
                    bloom += max(0.0, SampleScene(uv + kBloomOffsets[i] * _BloomRadius) - _BloomThreshold);
                bloom /= 12.0;

                float3 upper = ApplyLook(baseColor, _UTemperature, _UTint,
                                         _ULift.rgb, _UGamma.rgb, _UGain.rgb,
                                         _UShadowTint.rgb, _UHighlightTint.rgb,
                                         _USaturation, _UContrast) + bloom * _UBloom;

                float3 lower = ApplyLook(baseColor, _LTemperature, _LTint,
                                         _LLift.rgb, _LGamma.rgb, _LGain.rgb,
                                         _LShadowTint.rgb, _LHighlightTint.rgb,
                                         _LSaturation, _LContrast) + bloom * _LBloom;

                // 1 = 上层，0 = 下层
                float screenY = 1.0 - input.positionCS.y / max(_ScreenParams.y, 1.0);
                float upperWeight = smoothstep(_SplitY - _Feather, _SplitY + _Feather, screenY);

                return half4(lerp(lower, upper, upperWeight), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
