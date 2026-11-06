Shader "Focus/FlowFire/UpperScreen"
{
    Properties
    {
        [PerRendererData] _MainTex ("UI Texture", 2D) = "white" {}
        _Color ("UI Tint", Color) = (1,1,1,1)
        _FlowTime ("Flow Time", Float) = 0
        _Reveal ("Top Down Reveal", Range(0,1)) = 1
        _Opacity ("Opacity", Range(0,1)) = 0.96
        _AspectRatio ("Region Aspect", Float) = 3.5
        _FrontSoftness ("Reveal Edge Softness", Range(0.005,0.15)) = 0.04
        _SplitStart ("Split Line Start (Local)", Vector) = (0,0,0,0)
        _SplitEnd ("Split Line End (Local)", Vector) = (0,0,0,0)
        _SplitFeather ("Split Line Feather (px)", Float) = 6
        _SplitSpan ("Split Reveal Span (px)", Float) = 1000
        _SplitEnabled ("Split Line Enabled", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            float4 _Color;
            float4 _ClipRect;
            float _FlowTime, _Reveal, _Opacity, _AspectRatio, _FrontSoftness;
            float4 _SplitStart, _SplitEnd;
            float _SplitFeather, _SplitSpan, _SplitEnabled;

            struct Attributes
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 localPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.localPosition = input.vertex.xy;
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            float4 FlowRotation(float halfAngle)
            {
                return float4(float3(0.577350269, 0.577350269, 0.577350269) *
                    sin(halfAngle), cos(halfAngle));
            }

            float3 RotateGradient(float4 rotation, float3 gradient)
            {
                float3 t = 2.0 * cross(rotation.xyz, gradient);
                return gradient + rotation.w * t + cross(rotation.xyz, t);
            }

            float3 RandomGradient(float3 cell, float4 rotation)
            {
                float j = 4096.0 * sin(dot(cell, float3(17.0, 59.4, 15.0)));
                float3 gradient;
                gradient.z = frac(512.0 * j);
                j *= 0.125;
                gradient.x = frac(512.0 * j);
                j *= 0.125;
                gradient.y = frac(512.0 * j);
                return RotateGradient(rotation, gradient - 0.5);
            }

            float4 DerivativeNoise(float3 p, float4 rotation)
            {
                const float F3 = 0.3333333;
                const float G3 = 0.1666667;
                float3 s = floor(p + (p.x + p.y + p.z) * F3);
                float3 x = p - s + (s.x + s.y + s.z) * G3;
                float3 e = step(float3(0.0, 0.0, 0.0), x - x.yzx);
                float3 i1 = e * (1.0 - e.zxy);
                float3 i2 = 1.0 - e.zxy * (1.0 - e);
                float3 x1 = x - i1 + G3;
                float3 x2 = x - i2 + 2.0 * G3;
                float3 x3 = x - 1.0 + 3.0 * G3;

                float4 w = float4(dot(x,x), dot(x1,x1), dot(x2,x2), dot(x3,x3));
                w = max(0.6 - w, 0.0);
                float4 w2 = w * w;
                float4 w4 = w2 * w2;

                float3 g0 = RandomGradient(s, rotation);
                float3 g1 = RandomGradient(s + i1, rotation);
                float3 g2 = RandomGradient(s + i2, rotation);
                float3 g3 = RandomGradient(s + 1.0, rotation);
                float4 d = float4(dot(g0,x), dot(g1,x1), dot(g2,x2), dot(g3,x3));

                float4 weighted = w * w2 * d;
                float3 derivative = -8.0 * (weighted.x*x + weighted.y*x1 +
                    weighted.z*x2 + weighted.w*x3);
                derivative += w4.x*g0 + w4.y*g1 + w4.z*g2 + w4.w*g3;
                derivative *= 52.0;
                float value = dot(d * w4, float4(1.0,1.0,1.0,1.0)) * 52.0;
                return float4(derivative, value);
            }

            float4 FlowNoise(float3 domain, float2 uv, float time)
            {
                float4 noise = 0.0;
                float frequency = 1.0;
                float amplitude = 1.0;
                const float lacunarity = 2.13;
                [unroll]
                for (int octave = 0; octave < 5; octave++)
                {
                    float angle = time * 0.4 + uv.y * 0.5;
                    float4 rotation = FlowRotation(angle * 2.0 * frequency);
                    float4 dn = DerivativeNoise(domain, rotation);
                    domain -= 0.01 * dn.xyz;
                    noise += abs(amplitude * dn);
                    domain *= lacunarity;
                    frequency *= lacunarity;
                    amplitude /= lacunarity;
                }
                return noise;
            }

            float3 FlameColour(float density)
            {
                float hue = (density - 2.25/6.0) * (1.25/6.0);
                float saturation = density * 1.25 + 0.2;
                float value = density * 0.95;
                float3 cycle = frac((hue * 6.0 + float3(0.0,4.0,2.0)) / 6.0) * 6.0;
                float3 rgb = saturate(abs(cycle - 3.0) - 1.0);
                rgb = rgb * rgb * (3.0 - 2.0 * rgb);
                return max(0.0, value * lerp(float3(1.0,1.0,1.0), rgb, saturation));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                if (_Reveal <= 0.0) return 0.0;
                float2 uv = float2(input.uv.x * _AspectRatio, 1.0 - input.uv.y);
                // Rotate the turbulence flow so it streams from the upper right
                // toward the lower left, matching the 45-degree beam clash.
                float2 flowDir = float2(-0.7071068, 0.7071068);
                float2 crossDir = float2(0.7071068, 0.7071068);
                float2 flowUv = float2(dot(uv, crossDir), dot(uv, flowDir));
                float t = _FlowTime * 0.8;
                float3 domain = float3(flowUv * 1.15 - float2(0.0, t), t * 0.5);
                float4 d = FlowNoise(domain, flowUv, _FlowTime);
                float density = length(d.xyz) * 0.15 + 0.2 - d.w * 0.2;
                float3 rgb = FlameColour(density);

                float revealed = 0.0;
                float clipMask = 1.0;
                if (_SplitEnabled > 0.5 && _SplitSpan > 0.0001)
                {
                    // Local UI space: 1 unit = 1 reference pixel on both axes,
                    // so the wavefront stays physically parallel to the 45-degree line.
                    float2 start = _SplitStart.xy;
                    float2 split = _SplitEnd.xy - start;
                    float2 dir = split / max(0.0001, length(split));
                    float2 normal = float2(-dir.y, dir.x); // toward the upper-right flame side
                    float2 p = input.localPosition - start;

                    // Normalize wave to [0..1] across the flame image, then drive the
                    // front from *outside* the image (1.2) all the way past the split
                    // line (-0.05). This matches the old top-down behaviour where the
                    // first visible flame was well off-screen.
                    float wave = dot(p, normal) / _SplitSpan;
                    float along = dot(p, dir) / _SplitSpan;
                    wave += 0.02 * sin(along * 5.0 + _FlowTime * 1.3)
                          + 0.01 * sin(along * 11.0 - _FlowTime * 0.9);
                    float revealFront = lerp(1.2, -0.05, _Reveal);
                    revealed = smoothstep(revealFront - _FrontSoftness,
                        revealFront + _FrontSoftness, wave);

                    // Hard clip: keep only the flame side of the split line.
                    clipMask = smoothstep(-_SplitFeather, _SplitFeather, dot(p, normal));
                }
                else
                {
                    float front = lerp(-0.09, 1.09, _Reveal);
                    front += 0.023 * sin(uv.x * 9.0 + _FlowTime * 1.3)
                        + 0.015 * sin(uv.x * 17.0 - _FlowTime * 0.9);
                    revealed = 1.0 - smoothstep(front - _FrontSoftness,
                        front + _FrontSoftness, uv.y);
                }

                float brightness = max(rgb.r, max(rgb.g, rgb.b));
                float coverage = smoothstep(0.015, 0.12, brightness);
                float alpha = coverage * revealed * clipMask * _Opacity * input.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(input.localPosition, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(alpha - 0.001);
                #endif
                return float4(rgb * input.color.rgb, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
