Shader "WordDefense/Cyber Control"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
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
            struct Attributes { float4 positionOS:POSITION; half4 color:COLOR; float3 uv:TEXCOORD0; };
            struct Varyings { float4 positionCS:SV_POSITION; half4 color:COLOR; float3 uv:TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                half4 color = input.color;
                // Only dust uses soft circular quads. Lines and digits remain crisp.
                color.a *= lerp(1.0, 1.0 - smoothstep(0.08, 0.5, length(input.uv.xy - 0.5)), input.uv.z);
                return color;
            }
            ENDHLSL
        }
    }
}
