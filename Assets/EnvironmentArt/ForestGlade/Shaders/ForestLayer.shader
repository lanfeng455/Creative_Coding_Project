Shader "WordDefense/Forest Background Layer"
{
    Properties
    {
        [PerRendererData] _MainTex ("Layer", 2D) = "white" {}
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
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float4 _Scroll;
                float4 _ClipRect;
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
            half4 Frag(Varyings input):SV_Target
            {
                clip(input.worldXY - _ClipRect.xy);
                clip(_ClipRect.zw - input.worldXY);
                // Clamp avoids exposing empty strips at the outside of the stage.
                float2 uv = clamp(input.uv + _Scroll.xy, 0.0003, 0.9997);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv) * input.color;
            }
            ENDHLSL
        }
    }
}
