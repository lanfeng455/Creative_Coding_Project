Shader "Enemies/Riftbark"
{
    Properties
    {
        _BaseMap ("Bark", 2D) = "white" {}
        _ReflectMap ("Reflection", 2D) = "black" {}
        _GlossMap ("Gloss", 2D) = "black" {}
        _Split ("Chest opening", Range(0,1)) = 0
        _Windup ("Throw windup", Range(0,1)) = 0
        _Materialize ("Summon height", Range(0,1)) = 1
        _Glow ("Inner light", Range(0,2)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_ReflectMap); TEXTURE2D(_GlossMap);
            CBUFFER_START(UnityPerMaterial)
                float _Split, _Windup, _Glow, _Materialize;
            CBUFFER_END
            struct Input { float3 pos:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; half4 color:COLOR; };
            struct Output { float4 clipPos:SV_POSITION; float3 local:TEXCOORD0; float3 normal:TEXCOORD1; float2 uv:TEXCOORD2; half3 tint:COLOR; };
            Output Vert(Input v)
            {
                Output o;
                float3 p = v.pos;
                float arm = smoothstep(.18, .34, -p.x) * (1 - smoothstep(.50, .73, p.y));
                float s, c;
                sincos(-.8 * arm * _Windup, s, c);
                float2 pivot = float2(-.27, .58);
                p.xy = pivot + mul(float2x2(c,-s,s,c), p.xy - pivot);
                float chest = saturate(1 - abs(p.y - .405) * 5) * saturate(1 - abs(p.x) * 3.7) * smoothstep(.06,.21,-p.z);
                p.x += _Split * chest * .078 * clamp(p.x * 65, -1, 1);
                o.local = p;
                o.clipPos = TransformObjectToHClip(p);
                v.normal.xy = mul(float2x2(c,-s,s,c), v.normal.xy);
                o.normal = TransformObjectToWorldNormal(v.normal);
                o.uv = v.uv; o.tint = v.color.rgb;
                return o;
            }
            half4 Frag(Output i):SV_Target
            {
                half summonLight = 0;
                if (_Materialize < 1)
                {
                    float grain = .018 * sin(i.local.x * 46 + i.local.z * 19)
                                + .012 * sin(i.local.z * 69 - i.local.y * 43);
                    float depth = lerp(-.035, 1.06, _Materialize) - i.local.y + grain;
                    clip(depth);
                    summonLight = 1 - smoothstep(.004, .043, depth);
                }
                if (_Split > 0 && i.local.z < -.10)
                {
                    float jag = .008 * sin(i.local.y * 105);
                    float incision = abs(i.local.x + jag) / .090 + abs(i.local.y - .405) / .125;
                    clip(incision - _Split);
                }
                float3 n = normalize(i.normal);
                half3 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * i.tint;
                half3 reflectance = SAMPLE_TEXTURE2D(_ReflectMap, sampler_BaseMap, i.uv).rgb;
                half gloss = SAMPLE_TEXTURE2D(_GlossMap, sampler_BaseMap, i.uv).r;
                half key = .82 + .32 * saturate(dot(n, normalize(float3(-.45,.72,-.65))));
                half edge = pow(1 - saturate(-n.z), 2.7);
                half spec = pow(saturate(dot(n,normalize(float3(-.30,.42,-1)))),lerp(6,44,gloss));
                half3 lit = baseColor * (key + _Glow) + reflectance * spec * .23 + half3(.115,.045,.105)*edge;
                return half4(lit + half3(.85,.26,1.0) * summonLight, 1);
            }
            ENDHLSL
        }
    }
}
