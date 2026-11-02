// EdgeGlow.shader —— 屏幕边缘局部高亮
// 给 UI Image 用的 Unlit Shader
// 通过 _EdgeAxis 选择沿 X 或 Y 方向做钟形衰减
// 4 条边缘都用同一个 shader，只是 _EdgeAxis / _HitPos 参数不同

Shader "UI/EdgeGlow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _BaseColor ("Base Color", Color) = (1,1,1,0.08)
        _GlowColor ("Glow Color", Color) = (1, 0.85, 0.2, 1)
        _EdgeAxis ("Edge Axis (0=X, 1=Y)", Float) = 0
        _HitPos ("Hit Pos (0~1)", Float) = 0.5
        _Intensity ("Intensity (0~1)", Float) = 0
        _Spread ("Spread", Float) = 0.12
    }

    SubShader
    {
        Tags
        {
            "Queue"             = "Transparent"
            "IgnoreProjector"   = "True"
            "RenderType"        = "Transparent"
            "PreviewType"       = "Plane"
            "CanUseSpriteAtlas" = "True"
        }
        LOD 100

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex      : SV_POSITION;
                fixed4 color       : COLOR;
                float2 uv          : TEXCOORD0;
                float4 worldPos    : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4    _Color;
            fixed4    _BaseColor;
            fixed4    _GlowColor;
            float     _EdgeAxis;
            float     _HitPos;
            float     _Intensity;
            float     _Spread;
            float4    _MainTex_TexelSize;
            float4    _ClipRect;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPos = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1) 决定沿哪个 UV 轴做钟形
                float axisVal = _EdgeAxis < 0.5 ? i.uv.x : i.uv.y;

                // 2) 距离命中点的绝对距离
                float dist = abs(axisVal - _HitPos);

                // 3) 高斯钟形衰减  exp(-d² / (2·σ²))
                float sigma = max(_Spread, 0.001);
                float glow = exp(-dist * dist / (2.0 * sigma * sigma));
                glow = saturate(glow * _Intensity);

                // 4) 颜色混合
                fixed4 col = lerp(_BaseColor, _GlowColor, glow);

                // 5) Canvas Tint + Sprite 纹理
                col *= i.color;
                col *= tex2D(_MainTex, i.uv);

                // 6) Canvas 裁剪（Unity UI 必备）
                col.a *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);

                return col;
            }
            ENDCG
        }
    }

    Fallback "UI/Default"
}
