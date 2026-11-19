Shader "WordDefense/Forest Background Layer"
{
    Properties
    {
        [PerRendererData] _MainTex ("Layer", 2D) = "white" {}
        [PerRendererData] _BossRedTex ("Blood sky layer", 2D) = "white" {}
        [PerRendererData] _BossReveal ("Blood sky coverage", Range(0,1)) = 0
        [PerRendererData] _BossDim ("Retreat dimming", Range(0,1)) = 0
        [PerRendererData] _DaylightReturn ("Restore daylight", Range(0,1)) = 0
        _Color ("Tint", Color) = (1,1,1,1)
        _Scroll ("UV Offset", Vector) = (0,0,0,0)
        _ClipRect ("World Clip Rectangle", Vector) = (-100,-100,100,100)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="False" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_BossRedTex);
            SAMPLER(sampler_BossRedTex);
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _Scroll;
                float4 _ClipRect;
                float _BossReveal, _BossDim, _DaylightReturn;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float2 uv:TEXCOORD0; half4 color:COLOR; };
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; float2 worldXY:TEXCOORD1; half4 color:COLOR; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.worldXY = positionWS.xy;
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }
            float NoiseHash(float2 cell)
            {
                return frac(sin(dot(cell, float2(37.13, 91.7))) * 4375.241);
            }

            float SoftNoise(float2 uv)
            {
                float2 cell = floor(uv);
                float2 w = frac(uv);
                w = w * w * (3.0 - 2.0 * w);
                return lerp(lerp(NoiseHash(cell), NoiseHash(cell + float2(1, 0)), w.x),
                            lerp(NoiseHash(cell + float2(0, 1)), NoiseHash(cell + 1), w.x), w.y);
            }

            half4 Frag(Varyings input):SV_Target
            {
                clip(input.worldXY - _ClipRect.xy);
                clip(_ClipRect.zw - input.worldXY);
                // Clamp avoids exposing empty strips at the outside of the stage.
                float2 uv = clamp(input.uv + _Scroll.xy, 0.0003, 0.9997);
                half4 tint = input.color;
                tint.rgb *= 1.0 - _BossDim;
                if (_BossReveal <= 0.0) return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * tint;
                half4 red = SAMPLE_TEXTURE2D(_BossRedTex, sampler_BossRedTex, uv);
                if (_BossReveal >= 1.0 && _DaylightReturn <= 0.0) return red * tint;

                float wave = 1.0;
                if (_BossReveal < 1.0)
                {
                    // The reference painting measures its horizon from the top of the canvas.
                    float2 stageUV = float2(input.uv.x, 1.0 - input.uv.y);
                    float distanceToHorizon = length((stageUV - float2(.42, .62)) * float2(1.0, .48));
                    float grain = .09 * SoftNoise(stageUV * float2(8, 4))
                                + .035 * SoftNoise(stageUV * float2(19, 8));
                    float front = .96 * _BossReveal;
                    wave = 1.0 - smoothstep(front - .22, front + .02, distanceToHorizon + grain);
                }
                float coverage = wave * (1.0 - _DaylightReturn);
                half4 normal = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                // Blend coverage-weighted color so transparent leaf edges keep their silhouette.
                half alpha = lerp(normal.a, red.a, coverage);
                float3 color = lerp(normal.rgb * normal.a, red.rgb * red.a, coverage) / max(alpha, .0001);
                return half4(color, alpha) * tint;
            }
            ENDHLSL
        }
    }
}
