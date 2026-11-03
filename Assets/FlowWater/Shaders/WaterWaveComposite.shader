Shader "Focus/FlowWater/WaveSilhouette"
{
    Properties
    {
        [PerRendererData] _MainTex ("Water Surface", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Progress ("Sweep Progress", Range(0,1)) = 0
        _WaterTime ("Water Time", Float) = 0
        _AspectRatio ("Region Aspect", Float) = 3.5
        _Opacity ("Opacity", Range(0,1)) = 1
        _FoamColor ("Wave Edge", Color) = (0.70,0.88,0.86,1)
        _EdgeSoftness ("Edge Softness", Range(0.002,0.04)) = 0.004
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
        Tags {"Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True"}
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off Lighting Off ZWrite Off
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
            sampler2D _MainTex;
            float4 _Color, _ClipRect, _FoamColor;
            float _Progress, _WaterTime, _AspectRatio, _Opacity, _EdgeSoftness;
            struct Attributes
            {
                float4 vertex:POSITION;
                float4 color:COLOR;
                float2 uv:TEXCOORD0;
            };
            struct Varyings
            {
                float4 vertex:SV_POSITION;
                float4 color:COLOR;
                float2 uv:TEXCOORD0;
                float2 position:TEXCOORD1;
            };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.vertex=UnityObjectToClipPos(input.vertex);
                o.position=input.vertex.xy;
                o.uv=input.uv;
                o.color=input.color*_Color;
                return o;
            }
            float4 Frag(Varyings input):SV_Target
            {
                if(_Progress<=0.0 || _Opacity<=0.0) return 0.0;
                float2 p=float2(input.uv.x*_AspectRatio,input.uv.y);
                float front=lerp(-0.45,_AspectRatio+0.75,_Progress);
                float behind=front-p.x;
                // One continuous surface tapers into the advancing front.
                // The wave height belongs to the body; no separate curl geometry.
                float taper=smoothstep(-0.10,0.55,behind);
                float fill=0.50+0.82*smoothstep(0.35,1.0,_Progress);
                float ripple=0.026*sin(p.x*9.0-_WaterTime*1.3)
                    +0.012*sin(p.x*17.0+_WaterTime*0.8);
                float swell=0.045*exp(-pow((behind-0.48)/0.36,2.0));
                float surface=(fill+ripple+swell)*taper;
                float shape=min(surface-p.y,behind+0.10);
                float feather=max(_EdgeSoftness,fwidth(shape));
                float mask=smoothstep(-feather,feather,shape);
                float rim=1.0-smoothstep(0.004,0.015,abs(shape-0.007));
                float3 color=tex2D(_MainTex,input.uv).rgb;
                color=lerp(color,_FoamColor.rgb,rim*0.40);
                float alpha=mask*_Opacity*input.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                alpha*=UnityGet2DClipping(input.position,_ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(alpha-0.001);
                #endif
                return float4(color*input.color.rgb,alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
