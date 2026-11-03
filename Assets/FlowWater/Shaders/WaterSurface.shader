Shader "Hidden/Focus/FlowWater/ReflectiveSurface"
{
    Properties
    {
        _WaterTime ("Water Time", Float) = 0
        _AspectRatio ("Aspect", Float) = 3.5
        _DeepColor ("Deep Water", Color) = (0.025,0.18,0.23,1)
        _WaterColor ("Water Body", Color) = (0.12,0.48,0.56,1)
        _ReflectionColor ("Reflection", Color) = (0.70,0.89,0.88,1)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "WaterSurfaceNoise.cginc"
            float _WaterTime, _AspectRatio;
            float4 _DeepColor, _WaterColor, _ReflectionColor;

            float3 SkyReflection(float3 direction)
            {
                float sky = smoothstep(-0.1,0.65,direction.y);
                float horizon = pow(saturate(1.0-abs(direction.y)*1.7),8.0);
                return lerp(_WaterColor.rgb * 0.65, _ReflectionColor.rgb, sky)
                    + _ReflectionColor.rgb * horizon * 0.30;
            }

            float4 Frag(v2f_img input) : SV_Target
            {
                float2 screen = (input.uv * 2.0 - 1.0) * float2(_AspectRatio,1.0);
                float3 origin = float3(0.0,5.0,-7.5);
                float3 forward = normalize(float3(0.0,0.3,0.0)-origin);
                float3 right = normalize(cross(forward,float3(0,1,0)));
                float3 up = cross(right,forward);
                float3 ray = normalize(right*screen.x + up*screen.y + forward*4.2);
                float planeDistance = (1.0-origin.y) / min(ray.y,-0.001);
                float3 samplePosition = origin + planeDistance*ray;
                float3 lastPoint = samplePosition;
                float lastHeight = WaterHeight(samplePosition,_WaterTime);
                float height = lastHeight;
                bool found = false;
                const float traceStep = 0.3;
                [loop]
                for(int index=0; index<20; index++)
                {
                    lastPoint = samplePosition;
                    lastHeight = height;
                    samplePosition += traceStep * ray;
                    height = WaterHeight(samplePosition,_WaterTime);
                    if(samplePosition.y < height) { found=true; break; }
                }
                if(!found) return float4(_DeepColor.rgb,1);
                float previousGap = max(0.0001,lastPoint.y-lastHeight);
                float currentGap = max(0.0,height-samplePosition.y);
                samplePosition = lerp(lastPoint,samplePosition,saturate(previousGap /
                    max(0.0001,previousGap+currentGap)));

                float h = WaterHeight(samplePosition,_WaterTime);
                float hx = WaterHeight(samplePosition+float3(0.01,0,0),_WaterTime);
                float hz = WaterHeight(samplePosition+float3(0,0,0.01),_WaterTime);
                float3 normal = normalize(float3((h-hx)/0.01,1.0,(h-hz)/0.01));
                float facing = saturate(dot(-ray,normal));
                float fresnel = 0.99 * pow(1.0-facing,5.0) + 0.01;
                float3 reflected = reflect(ray,normal);
                float3 light = normalize(float3(-0.6,1.0,-0.4));
                float sunlight = pow(saturate(dot(reflected,light)),80.0);
                float diffuse = saturate(dot(normal,light));
                float3 baseColor = lerp(_DeepColor.rgb,_WaterColor.rgb,
                    0.35 + diffuse*0.5);
                float3 color = baseColor + SkyReflection(reflected) * fresnel
                    + _ReflectionColor.rgb * sunlight * 0.55;
                return float4(saturate(color),1);
            }
            ENDCG
        }
    }
    Fallback Off
}
