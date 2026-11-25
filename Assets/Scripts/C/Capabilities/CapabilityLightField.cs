// CapabilityLightField.cs —— Prototype C：能力光场
// 背景：工程的 toon shader 只采样主光与 SH，不采样 additional light；
//       而 Renderer2DData 又不会为 3D 光源生成阴影/光照通道。于是 LightEmitter 挂的 Point Light
//       对 toon 物件完全无效（台灯照不亮自己和周围）。
// 做法：把所有启用的 LightEmitter 每帧打包成最多 8 盏全局灯光（世界坐标 / 范围 / 颜色×强度），
//       通过 Shader.SetGlobalVectorArray 喂给 PrototypeC/Toon，在片元里做距离衰减 + 半朗伯。
// 注意：这是渲染层的近似光照，不参与阴影；能力判定（LightResponder）仍走 LightEmitter 自己的范围查询。

using UnityEngine;

namespace PrototypeC
{
    public static class CapabilityLightField
    {
        /// <summary>shader 里固定数组长度；超出部分按 LightEmitter.All 的顺序截断。</summary>
        public const int MaxLights = 8;

        private static readonly Vector4[] Positions = new Vector4[MaxLights];
        private static readonly Vector4[] Colors = new Vector4[MaxLights];

        private static int _publishedFrame = -1;
        private static bool _dirty = true;

        /// <summary>
        /// 每帧最多发布一次。由任一启用的 LightEmitter 在 Update 里调用，
        /// 这样不需要额外的常驻驱动对象，也不会因为多盏灯而重复写全局参数。
        /// </summary>
        public static void PublishIfNeeded()
        {
            if (!_dirty && _publishedFrame == Time.frameCount) return;
            _publishedFrame = Time.frameCount;
            _dirty = false;

            int count = 0;
            var emitters = LightEmitter.All;
            for (int i = 0; i < emitters.Count && count < MaxLights; i++)
            {
                LightEmitter emitter = emitters[i];
                if (emitter == null || !emitter.isActiveAndEnabled || !emitter.emitEnabled) continue;

                Vector3 position = emitter.transform.position;
                Positions[count] = new Vector4(
                    position.x, position.y, position.z,
                    Mathf.Max(0.01f, emitter.range));

                float strength = Mathf.Clamp01(emitter.intensity)
                                 * Mathf.Clamp(emitter.lightIntensity / 1.6f, 0f, 2f);
                Color color = emitter.lightColor;
                Colors[count] = new Vector4(
                    color.r * strength,
                    color.g * strength,
                    color.b * strength,
                    strength);

                count++;
            }

            for (int i = count; i < MaxLights; i++)
            {
                Positions[i] = Vector4.zero;
                Colors[i] = Vector4.zero;
            }

            Shader.SetGlobalVectorArray("_CapLightPos", Positions);
            Shader.SetGlobalVectorArray("_CapLightColor", Colors);
            Shader.SetGlobalFloat("_CapLightCount", count);
        }

        /// <summary>所有光源都被关闭/销毁时清场，避免 shader 残留上一帧的光。</summary>
        public static void Invalidate()
        {
            _dirty = true;
            _publishedFrame = -1;
            for (int i = 0; i < MaxLights; i++)
            {
                Positions[i] = Vector4.zero;
                Colors[i] = Vector4.zero;
            }

            Shader.SetGlobalFloat("_CapLightCount", 0);
        }
    }
}
