// CapabilityAssembler.cs —— Prototype C：物件能力装配器（v0.1）
// 接入位置：ObjectModelService.FinishPlacement 中，ApplyBehaviorAssembly 之后、RegisterOccupancy /
// NotifyItemSettled 之前——保证物件登记与恐龙收到广播时能力已就绪。
// 能力来源（两路合并、自动去重）：
//   ① LLM 解析出的 spec.capabilities（Schema 里可选的 capabilities 字段，已按目录规范化）；
//   ② ObjectModelService.capabilityAssignments 分配表（Inspector 手动配置 / 测试用）。
// 职责边界：只把「能力 ID → 能力组件」挂到物件上；
//   - 不碰碰撞体 / 刚体 / WallTouchWobble / GeneratedObjectPushRelay（那些归行为装配器）；
//   - receive_force 由现有推动机制（Dynamic 刚体 + PushRelay）提供，这里只认 ID 不挂组件；
//   - 不在物件根节点加 Collider；
//   - 未知 ID 只告警跳过，绝不影响物件生成与落位。
// 第一版参数全部使用组件默认值（Inspector 可再调），LLM 不生成能力参数。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>一条分配记录：哪个生成物件（规范化 id）额外拥有哪些能力 ID。</summary>
    [Serializable]
    public class CapabilityAssignment
    {
        [Tooltip("物件规范化 id（与模型缓存 key 一致，例如 fan / potted_plant）；大小写与分隔符不影响匹配")]
        public string objectId = "";

        [Tooltip("能力 ID 列表（emit_airflow / emit_light / emit_sound / respond_to_* / receive_force）")]
        public string[] capabilityIds = Array.Empty<string>();
    }

    public static class CapabilityAssembler
    {
        /// <summary>
        /// 能力装配入口：合并 spec.capabilities 与分配表里命中的 ID，逐个挂对应能力组件。
        /// 任何未知 ID / 组件已存在都只记日志跳过，绝不影响落位与后续登记。
        /// </summary>
        public static void Assemble(GameObject instance, ObjectSpec spec, List<CapabilityAssignment> assignments)
        {
            if (instance == null || spec == null) return;

            List<string> ids = CollectCapabilityIds(spec, assignments);
            if (ids.Count == 0) return;

            foreach (string id in ids) AttachCapability(instance, spec, id);
        }

        /// <summary>合并两路能力来源并去重（保持出现顺序）。</summary>
        private static List<string> CollectCapabilityIds(ObjectSpec spec, List<CapabilityAssignment> assignments)
        {
            var ids = new List<string>();

            void AddNormalized(string raw)
            {
                if (!ObjectCapabilityIds.TryNormalize(raw, out string id)) return;
                if (!ids.Contains(id)) ids.Add(id);
            }

            // ① LLM 给的（Schema 可选 capabilities 字段，validator 已规范化过，这里再防御一次）
            if (spec.capabilities != null)
            {
                foreach (string raw in spec.capabilities) AddNormalized(raw);
            }

            // ② Inspector 分配表（命中当前物件 id 的）
            if (assignments != null)
            {
                foreach (CapabilityAssignment assignment in assignments)
                {
                    if (assignment == null || string.IsNullOrWhiteSpace(assignment.objectId)) continue;
                    if (!string.Equals(assignment.objectId.Trim(), spec.NormalizedId, StringComparison.OrdinalIgnoreCase)) continue;

                    if (assignment.capabilityIds != null)
                    {
                        foreach (string raw in assignment.capabilityIds) AddNormalized(raw);
                    }
                }
            }

            return ids;
        }

        private static void AttachCapability(GameObject instance, ObjectSpec spec, string capabilityId)
        {
            switch (capabilityId)
            {
                case ObjectCapabilityIds.EmitAirflow:
                    Attach<AirflowEmitter>(instance, spec, capabilityId, "emit_airflow");
                    return;

                case ObjectCapabilityIds.RespondToAirflow:
                    Attach<AirflowResponder>(instance, spec, capabilityId, "respond_to_airflow");
                    return;

                case ObjectCapabilityIds.EmitLight:
                    Attach<LightEmitter>(instance, spec, capabilityId, "emit_light");
                    return;

                case ObjectCapabilityIds.RespondToLight:
                    Attach<LightResponder>(instance, spec, capabilityId, "respond_to_light");
                    return;

                case ObjectCapabilityIds.EmitSound:
                    Attach<SoundEmitter>(instance, spec, capabilityId, "emit_sound");
                    return;

                case ObjectCapabilityIds.RespondToSound:
                    Attach<SoundResponder>(instance, spec, capabilityId, "respond_to_sound");
                    return;

                case ObjectCapabilityIds.ReceiveForce:
                    Debug.Log($"[PrototypeC] 能力装配：{spec.objectName}（{spec.NormalizedId}）→ receive_force：" +
                              "由现有推动机制（Dynamic 刚体 + GeneratedObjectPushRelay）提供，无需额外组件");
                    return;

                default:
                    Debug.LogWarning($"[PrototypeC] 能力装配：未知能力 ID「{capabilityId}」（{spec.objectName}），已跳过。" +
                                     "已注册 ID 见 ObjectCapabilityIds");
                    return;
            }
        }

        private static void Attach<T>(GameObject instance, ObjectSpec spec, string capabilityId, string label)
            where T : Component
        {
            if (instance.GetComponent<T>() != null)
            {
                Debug.Log($"[PrototypeC] 能力装配：{spec.objectName}（{spec.NormalizedId}）已有 {typeof(T).Name}，跳过 {label}");
                return;
            }

            instance.AddComponent<T>();
            Debug.Log($"[PrototypeC] 能力装配：{spec.objectName}（{spec.NormalizedId}）→ {label}（{typeof(T).Name}）");
        }
    }
}
