// AirflowEmitter.cs —— Prototype C：OUTPUT 能力 emit_airflow（持续气流源）
// 独立组件：手动摆放的物件直接挂本组件即可产生气流；生成物件由 CapabilityAssembler 按分配表挂上。
// 交互模式：本组件只注册进静态注册表、提供范围查询，不主动查找目标——
// 由 AirflowResponder 每帧主动查询（单向依赖，风扇不认识植物，植物也不认识风扇）。
// 风向：默认根据模型朝向自动推导（正面 local +Z 在世界 X 上的投影；生成物 yaw=±90° 时即朝左/朝右），
//       模型正对相机无法判断、或用负 scale 翻身的模型，可切 Manual 手动指定。
// 不依赖物件名称、不依赖恐龙；不添加任何 Collider / Rigidbody（那些归行为装配器管）。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class AirflowEmitter : MonoBehaviour
    {
        /// <summary>风向决定方式：按朝向自动 / 手动指定。</summary>
        public enum DirectionMode
        {
            FromFacing,
            Manual,
        }

        /// <summary>场上所有启用的气流源（调试 / 外部系统观察用）。</summary>
        public static readonly List<AirflowEmitter> All = new List<AirflowEmitter>();

        [Header("风向")]
        [Tooltip("FromFacing = 按模型正面朝向自动定风向（推荐）；Manual = 用下面的手动方向")]
        public DirectionMode directionMode = DirectionMode.FromFacing;

        [Tooltip("Manual 模式下的风向：1 = +X（右），-1 = -X（左）；FromFacing 判断不出朝向时也回退到这个值")]
        [Range(-1, 1)] public int manualDirectionX = 1;

        [Header("气流参数（Inspector 可实时调）")]
        [Tooltip("气流有效范围（米；X-Y 平面圆形半径，忽略 Z 错位）")]
        public float range = 2.5f;

        [Tooltip("气流强度（0~1，决定植物摆动幅度）")]
        [Range(0f, 1f)] public float strength = 0.6f;

        [Tooltip("是否启用该气流源（可在 Play 中随时开关做对照测试）")]
        public bool emitEnabled = true;

        /// <summary>当前生效的风向（+1 右 / -1 左）。</summary>
        public int FlowDirectionX
        {
            get
            {
                int manual = manualDirectionX >= 0 ? 1 : -1;
                if (directionMode == DirectionMode.Manual) return manual;

                // 正面 local +Z 投影到世界 X：yaw=+90°（朝右）→ forward.x>0；yaw=-90°（朝左）→ forward.x<0
                float fx = transform.forward.x;
                if (Mathf.Abs(fx) < 0.05f) return manual;   // 正对/背对相机：判断不出，回退手动值
                return fx > 0f ? 1 : -1;
            }
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        /// <summary>某世界坐标位置是否处于本气流范围内：距离达标 + 位于下风向（正上方留 5% 容差）。</summary>
        public bool IsInside(Vector3 worldPos)
        {
            if (!emitEnabled || range <= 0.01f) return false;

            float dx = worldPos.x - transform.position.x;
            float dy = worldPos.y - transform.position.y;
            if (dx * dx + dy * dy > range * range) return false;

            int dir = FlowDirectionX;
            return dx * dir >= -0.05f * range;
        }

        /// <summary>
        /// 全局查询：该位置受到的气流影响。返回 false = 无气流；
        /// 返回 true 时 influenceX = 方向（±1）× 强度（0~1）。
        /// 多个气流源重叠时取绝对值最强的一个（第一版不做向量合成）。
        /// </summary>
        public static bool TryGetInfluence(Vector3 worldPos, out float influenceX)
        {
            influenceX = 0f;
            bool any = false;

            for (int i = 0; i < All.Count; i++)
            {
                AirflowEmitter emitter = All[i];
                if (emitter == null || !emitter.IsInside(worldPos)) continue;

                float value = emitter.FlowDirectionX * Mathf.Clamp01(emitter.strength);
                if (!any || Mathf.Abs(value) > Mathf.Abs(influenceX)) influenceX = value;
                any = true;
            }

            return any;
        }

#if UNITY_EDITOR
        // Scene 视图辅助：选中风扇时画出气流范围（X-Y 平面圆 + 方向箭头），方便手动摆放测试
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.9f);

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

            int dir = FlowDirectionX;
            Vector3 arrowStart = center + new Vector3(dir * range * 0.35f, 0f, 0f);
            Vector3 arrowEnd = center + new Vector3(dir * range * 0.8f, 0f, 0f);
            Gizmos.DrawLine(arrowStart, arrowEnd);
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(-dir * 0.25f, 0.12f, 0f));
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(-dir * 0.25f, -0.12f, 0f));
        }
#endif
    }
}
