// LightEmitter.cs —— Prototype C：OUTPUT 能力 emit_light（持续光照源）
// 独立组件：手动摆放的物件直接挂本组件即可；生成物件由 CapabilityAssembler 按能力 ID 挂上。
// 交互模式与 AirflowEmitter 一致：只注册进静态注册表 + 提供范围查询，由 LightResponder 主动查询。
// 可见光：默认在根节点加一个 Unity Point Light（装饰用）。toon 物件本身由 CapabilityLightField
// 全局光场照亮（shader 不采样 additional light），这个 Point Light 只对标准材质有效；
// 游戏逻辑上的「照到谁」始终走注册表范围检测。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class LightEmitter : MonoBehaviour
    {
        /// <summary>场上所有启用的光照源（调试 / 外部系统观察用）。</summary>
        public static readonly List<LightEmitter> All = new List<LightEmitter>();

        [Header("光照参数（Inspector 可实时调）")]
        [Tooltip("光照有效范围（米；X-Y 平面圆形半径，忽略 Z 错位）")]
        public float range = 2.2f;

        [Tooltip("光强（0~1，决定光敏物件的反馈幅度）")]
        [Range(0f, 1f)] public float intensity = 0.7f;

        [Tooltip("是否启用该光照源（可在 Play 中随时开关做对照测试）")]
        public bool emitEnabled = true;

        [Header("可见光（纯装饰，不影响范围判定）")]
        [Tooltip("是否在根节点添加真实的 Point Light（toon 物件不受真实光照影响，仅供其他材质与氛围用）")]
        public bool addVisibleLight = true;

        [Tooltip("Point Light 颜色")]
        public Color lightColor = new Color(1f, 0.9f, 0.65f, 1f);

        [Tooltip("Point Light 强度")]
        [Range(0f, 8f)] public float lightIntensity = 1.6f;

        private Light _light;

        private void OnEnable()
        {
            All.Add(this);
            EnsureLight();
        }

        private void OnDisable()
        {
            All.Remove(this);
            CapabilityLightField.Invalidate();
        }

        private void EnsureLight()
        {
            if (!addVisibleLight) return;
            if (_light == null) _light = GetComponent<Light>();
            if (_light == null) _light = gameObject.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.shadows = LightShadows.None;
            _light.renderMode = LightRenderMode.ForcePixel;
        }

        private void Update()
        {
            // 每帧只发布一次：任一启用的光源负责把本帧的光场写给 toon shader
            CapabilityLightField.PublishIfNeeded();

            // 参数全部可在 Inspector 实时调：每帧同步给可见光（开销可忽略）
            if (_light == null) return;
            _light.enabled = emitEnabled;
            _light.range = Mathf.Max(0.1f, range);
            _light.color = lightColor;
            _light.intensity = lightIntensity;
        }

        /// <summary>某世界坐标位置是否处于本光照范围内（X-Y 平面距离）。</summary>
        public bool IsInside(Vector3 worldPos)
        {
            if (!emitEnabled || range <= 0.01f) return false;

            float dx = worldPos.x - transform.position.x;
            float dy = worldPos.y - transform.position.y;
            return dx * dx + dy * dy <= range * range;
        }

        /// <summary>
        /// 全局查询：该位置受到的光照强度（0~1，多光源重叠取最强）。返回 false = 无光照。
        /// </summary>
        public static bool TryGetLightAt(Vector3 worldPos, out float intensity01)
        {
            intensity01 = 0f;
            bool any = false;

            for (int i = 0; i < All.Count; i++)
            {
                LightEmitter emitter = All[i];
                if (emitter == null || !emitter.IsInside(worldPos)) continue;

                float value = Mathf.Clamp01(emitter.intensity);
                if (!any || value > intensity01) intensity01 = value;
                any = true;
            }

            return any;
        }

#if UNITY_EDITOR
        // Scene 视图辅助：选中光源物件时画出光照范围（X-Y 平面圆）
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.9f);

            Vector3 center = transform.position;
            const int segments = 40;
            Vector3 prev = center + new Vector3(0f, range, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 point = center + new Vector3(Mathf.Cos(a) * range, Mathf.Sin(a) * range, 0f);
                Gizmos.DrawLine(prev, point);
                prev = point;
            }
        }
#endif
    }
}
