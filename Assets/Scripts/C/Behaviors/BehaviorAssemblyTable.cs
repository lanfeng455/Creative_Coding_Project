// BehaviorAssemblyTable.cs —— Prototype C：behavior × placement → 装配预制件 的查表
// 合法组合共 9 格：Touch(Ground/Wall/Rooftop)、Rest(Ground)、Play(Ground/Wall)、Ignore(Ground/Wall/Rooftop)。
// 表只存「哪一格用哪个预制件」；每格的具体参数在预制件的 ObjectBehaviorProfile 上，所有模型共用。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>格子标识与合法组合判定的唯一来源（运行时查表、编辑器生成预制件都走这里）。</summary>
    public static class BehaviorCells
    {
        public static readonly ObjectBehavior[] Behaviors =
        {
            ObjectBehavior.Touch, ObjectBehavior.Rest, ObjectBehavior.Play, ObjectBehavior.Ignore,
        };

        public static readonly ObjectPlacement[] Placements =
        {
            ObjectPlacement.Ground, ObjectPlacement.Wall, ObjectPlacement.Rooftop,
        };

        /// <summary>与 LLM 契约完全一致的合法组合表：Rest 只允许 Ground，Play 不允许 Rooftop。</summary>
        public static bool IsLegal(ObjectBehavior behavior, ObjectPlacement placement)
        {
            if (!Enum.IsDefined(typeof(ObjectBehavior), behavior)) return false;
            if (!Enum.IsDefined(typeof(ObjectPlacement), placement)) return false;

            switch (behavior)
            {
                case ObjectBehavior.Rest:
                    return placement == ObjectPlacement.Ground;
                case ObjectBehavior.Play:
                    return placement != ObjectPlacement.Rooftop;
                default:
                    return true;
            }
        }

        public static string Label(ObjectBehavior behavior, ObjectPlacement placement) =>
            $"{behavior}+{placement}";

        /// <summary>九格合法组合，顺序固定（编辑器生成预制件、Inspector 列表都按它排）。</summary>
        public static List<(ObjectBehavior behavior, ObjectPlacement placement)> LegalCells()
        {
            var cells = new List<(ObjectBehavior, ObjectPlacement)>(9);
            foreach (ObjectBehavior behavior in Behaviors)
            {
                foreach (ObjectPlacement placement in Placements)
                {
                    if (IsLegal(behavior, placement)) cells.Add((behavior, placement));
                }
            }

            return cells;
        }

        /// <summary>
        /// ObjectSpec 里的字符串 → 枚举。旧数据（behavior=Decoration、placementKind 为空）按 Ignore + 实际落位解释；
        /// 无法识别或非法组合都会带出 note，由调用方明确报出来，绝不静默变形。
        /// </summary>
        public static bool TryResolve(string behaviorText, string placementText,
            out ObjectBehavior behavior, out ObjectPlacement placement, out string note,
            ObjectPlacement? placementFallback = null)
        {
            note = string.Empty;

            if (string.IsNullOrWhiteSpace(behaviorText))
            {
                behavior = ObjectBehavior.Ignore;
                note = "behavior 为空，按 Ignore 处理";
            }
            else if (!Enum.TryParse(behaviorText.Trim(), true, out behavior) ||
                     !Enum.IsDefined(typeof(ObjectBehavior), behavior))
            {
                behavior = ObjectBehavior.Ignore;
                note = $"behavior=\"{behaviorText}\" 不是合法枚举，按 Ignore 处理";
            }

            if (string.IsNullOrWhiteSpace(placementText))
            {
                placement = placementFallback ?? ObjectPlacement.Ground;
                note = Append(note, $"placement 为空，按实际落位 {placement} 处理");
            }
            else if (!Enum.TryParse(placementText.Trim(), true, out placement) ||
                     !Enum.IsDefined(typeof(ObjectPlacement), placement))
            {
                placement = placementFallback ?? ObjectPlacement.Ground;
                note = Append(note, $"placement=\"{placementText}\" 不是合法枚举，按实际落位 {placement} 处理");
            }

            if (!IsLegal(behavior, placement))
            {
                note = Append(note, $"非法组合 {Label(behavior, placement)}，装配按 Ignore+{placement} 兜底");
                behavior = ObjectBehavior.Ignore;
                return false;
            }

            return true;
        }

        private static string Append(string note, string extra) =>
            string.IsNullOrEmpty(note) ? extra : note + "；" + extra;
    }

    /// <summary>表里的一格：behavior + placement → 装配预制件。</summary>
    [Serializable]
    public class BehaviorProfileSlot
    {
        public ObjectBehavior behavior = ObjectBehavior.Ignore;
        public ObjectPlacement placement = ObjectPlacement.Ground;

        [Tooltip("这一格的装配预制件（挂 ObjectBehaviorProfile）；留空时用内置默认装配并报出缺的是哪一格")]
        public GameObject prefab;

        public string Label => BehaviorCells.Label(behavior, placement);
    }

    /// <summary>九格查表。挂在 ObjectModelService 上，所有模型共用同一份。</summary>
    [Serializable]
    public class BehaviorAssemblyTable
    {
        [Tooltip("按 behavior × placement 查装配预制件；九格齐全时不会有任何兜底日志")]
        public List<BehaviorProfileSlot> slots = new List<BehaviorProfileSlot>();

        /// <summary>新建一张表：九格都建好但预制件为空（等编辑器菜单生成后回填）。</summary>
        public static BehaviorAssemblyTable CreateDefault()
        {
            var table = new BehaviorAssemblyTable();
            foreach ((ObjectBehavior behavior, ObjectPlacement placement) in BehaviorCells.LegalCells())
            {
                table.slots.Add(new BehaviorProfileSlot { behavior = behavior, placement = placement });
            }

            return table;
        }

        /// <summary>补上缺失的格子（旧场景序列化出来的表可能只有部分格子）。</summary>
        public void EnsureComplete()
        {
            if (slots == null) slots = new List<BehaviorProfileSlot>();

            foreach ((ObjectBehavior behavior, ObjectPlacement placement) in BehaviorCells.LegalCells())
            {
                bool exists = false;
                for (int i = 0; i < slots.Count; i++)
                {
                    if (slots[i] != null && slots[i].behavior == behavior && slots[i].placement == placement)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists) slots.Add(new BehaviorProfileSlot { behavior = behavior, placement = placement });
            }
        }

        public GameObject FindPrefab(ObjectBehavior behavior, ObjectPlacement placement)
        {
            if (slots == null) return null;

            for (int i = 0; i < slots.Count; i++)
            {
                BehaviorProfileSlot slot = slots[i];
                if (slot == null) continue;
                if (slot.behavior == behavior && slot.placement == placement) return slot.prefab;
            }

            return null;
        }

        /// <summary>编辑器用：把一格的预制件写进表（没有这一格就补上）。</summary>
        public void AssignPrefab(ObjectBehavior behavior, ObjectPlacement placement, GameObject prefab)
        {
            EnsureComplete();

            for (int i = 0; i < slots.Count; i++)
            {
                BehaviorProfileSlot slot = slots[i];
                if (slot == null || slot.behavior != behavior || slot.placement != placement) continue;
                slot.prefab = prefab;
                return;
            }
        }
    }

    /// <summary>缺预制件时，用服务上原有的手感参数做兜底（只影响兜底路径，不会写进配置）。</summary>
    public struct BehaviorFallbackSettings
    {
        public float mass;
        public float linearDamping;
        public float angularDamping;
        public bool lockY;
        public bool freezeRotation;
        public float pushFactor;
    }

    /// <summary>九格内置默认：编辑器生成预制件、运行时缺项兜底都从这里取同一份。</summary>
    public static class BehaviorDefaults
    {
        public static void Apply(ObjectBehaviorProfile profile, ObjectBehavior behavior, ObjectPlacement placement,
            BehaviorFallbackSettings? fallback = null)
        {
            if (profile == null) return;

            profile.behavior = behavior;
            profile.placement = placement;

            // —— 共同基线：静态实体，推不动，没有反馈 ——
            profile.colliderMode = BehaviorColliderMode.Box;
            profile.colliderSizing = BehaviorColliderSizing.FitBounds;
            profile.colliderPadding = 1f;
            profile.colliderMaxThickness = 0.18f;
            profile.fixedSize = new Vector3(0.5f, 0.5f, 0.5f);

            profile.bodyMode = BehaviorBodyMode.Static;
            profile.mass = 2f;
            profile.linearDamping = 1.5f;
            profile.angularDamping = 4f;
            profile.useGravity = true;
            profile.lockY = false;
            profile.lockZ = false;
            profile.freezeRotation = false;
            profile.startAsleep = false;

            profile.pushable = false;
            profile.pushFactor = 0f;
            profile.needsSupportFloor = false;

            profile.wobbleOnTouch = false;
            profile.wobbleAmplitude = 0.05f;
            profile.wobbleDuration = 0.45f;
            profile.wobbleOscillations = 1.5f;
            profile.wobbleProbePadding = 0.08f;

            switch (behavior)
            {
                case ObjectBehavior.Touch when placement == ObjectPlacement.Ground:
                    MakePushable(profile, fallback, 0.8f, true);
                    break;

                case ObjectBehavior.Touch when placement == ObjectPlacement.Wall:
                    MakeWallMounted(profile);
                    profile.wobbleOnTouch = true;
                    profile.wobbleAmplitude = 0.05f;
                    break;

                case ObjectBehavior.Touch when placement == ObjectPlacement.Rooftop:
                    profile.wobbleOnTouch = true;
                    profile.wobbleAmplitude = 0.04f;
                    break;

                case ObjectBehavior.Play when placement == ObjectPlacement.Ground:
                    MakePushable(profile, fallback, 1.2f, true);
                    break;

                case ObjectBehavior.Play when placement == ObjectPlacement.Wall:
                    // 挂墙的可玩物件：关重力、锁 Y/Z、锁旋转 → 只能在墙面左右滑动，被推时明显区别于 Ignore
                    MakeWallMounted(profile);
                    profile.bodyMode = BehaviorBodyMode.Dynamic;
                    profile.useGravity = false;
                    profile.lockY = true;
                    profile.lockZ = true;
                    profile.freezeRotation = true;
                    profile.pushable = true;
                    profile.pushFactor = fallback?.pushFactor ?? 1f;
                    profile.startAsleep = true;
                    break;

                case ObjectBehavior.Ignore when placement == ObjectPlacement.Wall:
                    MakeWallMounted(profile);
                    break;

                default:
                    // Rest+Ground、Ignore+Ground、Ignore+Rooftop：静态实体
                    break;
            }

            if (fallback.HasValue && profile.bodyMode == BehaviorBodyMode.Dynamic)
            {
                BehaviorFallbackSettings settings = fallback.Value;
                profile.mass = Mathf.Max(0.1f, settings.mass);
                profile.linearDamping = Mathf.Max(0f, settings.linearDamping);
                profile.angularDamping = Mathf.Max(0f, settings.angularDamping);
                if (!profile.lockY) profile.lockY = settings.lockY;
                if (!profile.freezeRotation) profile.freezeRotation = settings.freezeRotation;
            }
        }

        private static void MakePushable(ObjectBehaviorProfile profile, BehaviorFallbackSettings? fallback,
            float defaultPushFactor, bool needsFloor)
        {
            // 地面可推物件（Touch 家具 / Play 玩具）：只允许 X 轴平推，锁 Y/Z/旋转防踢飞、防穿房、防翻倒
            profile.bodyMode = BehaviorBodyMode.Dynamic;
            profile.pushable = true;
            profile.pushFactor = fallback?.pushFactor ?? defaultPushFactor;
            profile.needsSupportFloor = needsFloor;
            profile.lockY = true;
            profile.lockZ = true;
            profile.freezeRotation = true;
        }

        private static void MakeWallMounted(ObjectBehaviorProfile profile)
        {
            profile.colliderSizing = BehaviorColliderSizing.FitBoundsThinDepth;
            profile.bodyMode = BehaviorBodyMode.Static;
        }
    }
}
