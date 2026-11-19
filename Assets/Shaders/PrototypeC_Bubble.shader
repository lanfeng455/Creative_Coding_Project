// PrototypeC_Bubble.shader —— 泡泡表面显示「摄像头里的人」（球面经纬铺开 + 压暗）
// 用法：把 MediaPipe 已经在用的 WebCamTexture 直接传进 _MainTex（不需要额外的捕获相机/RT）。
//   1. UV：按对象空间方向算经纬（equirect）—— u = atan2(x, -z) 绕球一圈，v = asin(y) 极到极，
//      整张摄像头画面绕球面铺开，正面只看到前半球（卷轴视角，预期结果），不再做屏幕投影特写；
//   2. 亮度：画面乘 _Exposure（默认 0.55，明显压暗），边缘只留一圈很窄的高光（_RimStrength / _FresnelPower），
//      最终颜色 clamp 到 1，避免 HDR 输出超过 1 把 Bloom 撑爆；
//   3. 只渲染朝向相机的那半球（Cull Back），避免背面的经纬接缝透出来。
// Unlit + 透明，不受光照影响。

Shader "PrototypeC/Bubble"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("摄像头画面", 2D) = "white" {}
        _UvScale ("经纬铺开（x=经度绕圈, y=纬度跨度）", Vector) = (1, 1, 0, 0)
        _Flip ("画面翻转（x=水平镜像, y=垂直）", Vector) = (0, 0, 0, 0)
        _Exposure ("画面亮度（默认已压暗）", Range(0.05, 1.5)) = 0.55
        _Alpha ("整体不透明度", Range(0, 1)) = 0.9
        _Distortion ("球面扰动强度", Range(0, 0.15)) = 0.04
        _FresnelPower ("边缘高光宽度（越大越窄）", Range(0.5, 12)) = 6
        _RimStrength ("边缘高光强度", Range(0, 1)) = 0.3
        _RimColor ("边缘颜色", Color) = (0.62, 0.95, 1, 1)
        _Tint ("整体染色", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Sphere" }

        Pass
        {
            Cull Back
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float2 _UvScale;
            float2 _Flip;
            float _Exposure;
            float _Alpha;
            float _Distortion;
            float _FresnelPower;
            float _RimStrength;
            float4 _RimColor;
            float4 _Tint;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objDir : TEXCOORD0;       // 对象空间采样方向（球面经纬）
                float3 worldNormal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);

                float3 worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldNormal = worldNormal;

                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.viewDir = normalize(UnityWorldSpaceViewDir(worldPos));

                // 眼球方向转到对象空间，叠加一点折射扰动（球面中心处几乎为 0，边缘略偏）
                float3 objEye = normalize(mul((float3x3)unity_WorldToObject, _WorldSpaceCameraPos - worldPos));
                o.objDir = normalize(v.normal + objEye * _Distortion);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.objDir);

                // 经纬 UV：正面（朝相机 = -Z）落在 u = 0.5，绕一圈后回到背面
                float u = atan2(dir.x, -dir.z) * 0.159154943 + 0.5;           // 1/(2*PI)
                float v = asin(clamp(dir.y, -1.0, 1.0)) * 0.318309886 + 0.5;  // 1/PI

                float2 uv = float2((u - 0.5) * _UvScale.x + 0.5,
                                   (v - 0.5) * _UvScale.y + 0.5);
                // 镜像/翻转：前置摄像头水平镜像，上下颠倒的平台翻 Y
                uv = lerp(uv, 1.0 - uv, _Flip);

                float3 feed = tex2D(_MainTex, saturate(uv)).rgb * _Tint.rgb;

                // fresnel：正对相机的地方完全透明，只有很靠边缘的一圈提亮
                float facing = saturate(dot(normalize(i.worldNormal), normalize(i.viewDir)));
                float fresnel = pow(1.0 - facing, _FresnelPower);

                // 压暗：画面乘曝光，边缘只加很弱的一点光；clamp 到 1，HDR 下也不会被 Bloom 撑爆
                float3 col = feed * _Exposure + _RimColor.rgb * (fresnel * _RimStrength);
                col = min(col, 1.0);

                // 主体接近不透明才能看清人脸，边缘略微透一点
                float a = saturate(_Alpha * (0.9 + 0.1 * fresnel));
                return float4(col, a);
            }
            ENDCG
        }
    }

    Fallback "Unlit/Transparent"
}
