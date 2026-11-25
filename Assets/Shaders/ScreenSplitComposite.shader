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

            // 上层手绘化：抬阴影 / 纸张颗粒 / 墨线
            float _UShadowLift;
            float _UPaperGrain;
            float _UPaperScale;
            float _UInkStrength;

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

            float Luma(float3 c)
            {
                return dot(c, float3(0.2126, 0.7152, 0.0722));
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float3 ApplyLook(float3 c,
                             float temperature, float tint,
                             float3 lift, float3 gamma, float3 gain,
                             float3 shadowTint, float3 highlightTint,
                             float saturation, float contrast,
                             float shadowLift)
            {
                // 色温 / 色调
                float t = temperature * 0.0012;
                float g = tint * 0.0012;
                c *= float3(1.0 + t, 1.0 + g, 1.0 - t);

                // Lift / Gamma / Gain
                c = max(0.0, c * gain + lift);
                c = pow(c, 1.0 / max(gamma, 1e-3));

                // 抬暗部：削弱硬阴影，让上层更像纸张上的淡彩
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                c += shadowLift * (1.0 - smoothstep(0.0, 0.55, luma));

                // 分离色调
                luma = dot(c, float3(0.2126, 0.7152, 0.0722));
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
                                         _USaturation, _UContrast, _UShadowLift) + bloom * _UBloom;

                // 纸张颗粒：静态、屏幕分辨率归一化，避免闪烁
                float2 grainUV = uv * (_ScreenParams.xy / 1080.0) * max(_UPaperScale, 0.01);
                float grain = ValueNoise(grainUV) * 0.65 + ValueNoise(grainUV * 2.37 + 11.0) * 0.35;
                upper *= 1.0 + (grain - 0.5) * _UPaperGrain * 2.0;

                // 墨线：对颜色亮度做 Sobel，只在上层轻轻压暗边缘
                float2 texel = 1.0 / max(_ScreenParams.xy, 1.0);
                float l00 = Luma(SampleScene(uv + texel * float2(-1.0, -1.0)));
                float l10 = Luma(SampleScene(uv + texel * float2( 0.0, -1.0)));
                float l20 = Luma(SampleScene(uv + texel * float2( 1.0, -1.0)));
                float l01 = Luma(SampleScene(uv + texel * float2(-1.0,  0.0)));
                float l21 = Luma(SampleScene(uv + texel * float2( 1.0,  0.0)));
                float l02 = Luma(SampleScene(uv + texel * float2(-1.0,  1.0)));
                float l12 = Luma(SampleScene(uv + texel * float2( 0.0,  1.0)));
                float l22 = Luma(SampleScene(uv + texel * float2( 1.0,  1.0)));

                float gx = -l00 - 2.0 * l01 - l02 + l20 + 2.0 * l21 + l22;
                float gy = -l00 - 2.0 * l10 - l20 + l02 + 2.0 * l12 + l22;
                float edge = length(float2(gx, gy));
                float ink = saturate(edge * _UInkStrength * 6.0);
                upper *= 1.0 - ink * 0.5;

                float3 lower = ApplyLook(baseColor, _LTemperature, _LTint,
                                         _LLift.rgb, _LGamma.rgb, _LGain.rgb,
                                         _LShadowTint.rgb, _LHighlightTint.rgb,
                                         _LSaturation, _LContrast, 0.0) + bloom * _LBloom;

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
