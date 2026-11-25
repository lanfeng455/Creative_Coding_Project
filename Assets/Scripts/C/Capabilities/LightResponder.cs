// LightResponder.cs —— Prototype C：INPUT 能力 respond_to_light（受光照反馈）
// 独立组件：每帧查询 LightEmitter 注册表——被照亮 → 物件 rim 发光 + 环境提亮；
// 离开光照 → 平滑恢复。多个光敏物件各自独立响应。
// 实现约束：
//   - 用 MaterialPropertyBlock（MPB）写 shader 参数，不创建 / 销毁材质实例，不改 sharedMaterial；
//     离开光照后 Clear 掉 MPB 覆盖，物件外观完全回到原样；
//   - 只认 toon shader 的 _RimStrength / _AmbientBoost 两个属性；材质不带这两个属性的 renderer 跳过
//     （不会有任何外观副作用）；
//   - 不加 Collider / Rigidbody，不影响物理与布局。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class LightResponder : MonoBehaviour
    {
        /// <summary>场上所有光敏物件（外部系统观察用）。</summary>
        public static readonly List<LightResponder> All = new List<LightResponder>();

        [Header("受光反馈（Inspector 可实时调）")]
        [Tooltip("受光时的 Rim 发光上限（叠加在该 renderer 材质原值上）")]
        [Range(0f, 1f)] public float rimBoost = 0.8f;

        [Tooltip("受光时的环境提亮上限（叠加在材质原值上）")]
        [Range(0f, 1f)] public float ambientBoost = 0.35f;

        [Tooltip("进入 / 离开光照的过渡时间（秒）")]
        public float fadeSeconds = 0.5f;

        /// <summary>当前受光强度（0~1，0 = 无光照）。恐龙等外部系统可读取。</summary>
        public float LitIntensity => _t;

        private struct RimRecord
        {
            public Renderer renderer;
            public float baseRim;
            public float baseAmbient;
        }

        private readonly List<RimRecord> _records = new List<RimRecord>();
        private static readonly MaterialPropertyBlock Mpb = new MaterialPropertyBlock();
        private float _t;
        private bool _dirty;
        private bool _collected;

        private void OnEnable() => All.Add(this);
        private void OnDisable()
        {
            All.Remove(this);
            ClearOverrides();
        }

        private void Update()
        {
            bool lit = LightEmitter.TryGetLightAt(transform.position, out float strength);

            float target = lit ? Mathf.Clamp01(strength) : 0f;
            _t = Mathf.MoveTowards(_t, target, Time.deltaTime / Mathf.Max(0.01f, fadeSeconds));

            if (_t <= 0.001f)
            {
                if (_dirty) ClearOverrides();
                return;
            }

            if (!_collected) CollectRecords();
            ApplyOverrides();
        }

        /// <summary>收集带 toon rim 属性的 renderer 与其材质原值（懒收集，只做一次）。</summary>
        private void CollectRecords()
        {
            _collected = true;
            _records.Clear();

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;

                Material material = renderer.sharedMaterial;
                if (material == null || !material.HasProperty("_RimStrength")) continue;

                _records.Add(new RimRecord
                {
                    renderer = renderer,
                    baseRim = material.GetFloat("_RimStrength"),
                    baseAmbient = material.HasProperty("_AmbientBoost") ? material.GetFloat("_AmbientBoost") : 0f,
                });
            }

            if (_records.Count == 0)
                Debug.Log($"[PrototypeC] 光敏反馈：{name} 没有 _RimStrength 材质（非 toon），仅提供状态查询不做外观变化");
        }

        private void ApplyOverrides()
        {
            if (_records.Count == 0) return;

            for (int i = 0; i < _records.Count; i++)
            {
                Renderer renderer = _records[i].renderer;
                if (renderer == null) continue;

                renderer.GetPropertyBlock(Mpb);
                Mpb.SetFloat("_RimStrength", _records[i].baseRim + rimBoost * _t);
                Mpb.SetFloat("_AmbientBoost", _records[i].baseAmbient + ambientBoost * _t);
                renderer.SetPropertyBlock(Mpb);
            }

            _dirty = true;
        }

        private void ClearOverrides()
        {
            if (!_dirty) return;

            for (int i = 0; i < _records.Count; i++)
            {
                Renderer renderer = _records[i].renderer;
                if (renderer == null) continue;

                renderer.GetPropertyBlock(Mpb);
                Mpb.Clear();
                renderer.SetPropertyBlock(Mpb);
            }

            _dirty = false;
        }
    }
}
