// ObjectBehaviorAssembler.cs —— Prototype C：按 behavior × placement 查表装配
// 取代原来 ObjectModelService.ApplyBehavior 里的一串 if：
//   1) behavior/placement 字符串 → 枚举（旧数据按 Ignore+Ground 解释）；
//   2) 查 9 格表拿到装配预制件，实例化到模型根节点下；
//   3) 由预制件上的 ObjectBehaviorProfile 按模型包围盒生成碰撞体 / 刚体 / 晃动反馈。
// 任何一格缺预制件都不会静默降级：用内置默认装配兜底，并在 Hierarchy 里留下 *_FALLBACK 子节点、日志点名缺哪一格。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>一次装配的结果：给占用表（是否动态）与调用方（是否用了兜底）用。</summary>
    public struct BehaviorAssemblyResult
    {
        public bool ok;
        public ObjectBehavior behavior;
        public ObjectPlacement placement;
        public bool usedFallback;
        public bool pushable;
        public bool needsSupportFloor;
        public string cellLabel;
        public string note;

        public string Describe() =>
            $"{cellLabel}｜{(usedFallback ? "内置兜底" : "预制件装配")}" +
            $"｜{(pushable ? "可推动" : "不可推动")}" +
            (string.IsNullOrEmpty(note) ? string.Empty : "｜" + note);
    }

    public static class ObjectBehaviorAssembler
    {
        /// <summary>同一格的缺项只报一次，避免每个物件刷一条同样的错误。</summary>
        private static readonly HashSet<string> ReportedMissingCells = new HashSet<string>();

        public static BehaviorAssemblyResult Assemble(GameObject modelRoot, ObjectSpec spec,
            BehaviorAssemblyTable table, BehaviorFallbackSettings fallbackSettings,
            ObjectPlacement? placementFallback = null)
        {
            var result = new BehaviorAssemblyResult();

            if (spec == null || modelRoot == null)
            {
                result.note = "缺少模型或物件定义，未装配";
                return result;
            }

            bool legal = BehaviorCells.TryResolve(spec.behavior, spec.placementKind,
                out ObjectBehavior behavior, out ObjectPlacement placement, out string note, placementFallback);

            result.behavior = behavior;
            result.placement = placement;
            result.cellLabel = BehaviorCells.Label(behavior, placement);
            result.note = note;

            if (!legal)
            {
                Debug.LogError($"[PrototypeC] 行为装配：{spec.objectName}（{spec.NormalizedId}）拿到非法组合" +
                               $"{spec.behavior}+{spec.placementKind}，已按 {result.cellLabel} 兜底。解析阶段本应拒绝该组合");
            }

            Bounds localBounds;
            if (!GeneratedObjectPhysics.TryGetLocalBounds(modelRoot, true, out localBounds))
            {
                localBounds = new Bounds(Vector3.zero, new Vector3(0.5f, 0.5f, 0.5f));
                result.note = Append(result.note, "量不到模型包围盒，碰撞体按 0.5 m 方块兜底");
                Debug.LogWarning($"[PrototypeC] 行为装配：{spec.objectName} 量不到包围盒，按 0.5 m 方块兜底");
            }

            ObjectBehaviorProfile profile = ResolveProfile(modelRoot, behavior, placement, table,
                fallbackSettings, ref result);

            if (profile == null)
            {
                result.note = Append(result.note, "装配配置缺失，未加碰撞体/刚体");
                return result;
            }

            profile.Assemble(modelRoot, localBounds, behavior, placement);

            result.ok = true;
            result.pushable = profile.pushable && profile.bodyMode == BehaviorBodyMode.Dynamic;
            result.needsSupportFloor = result.pushable && profile.needsSupportFloor;

            Debug.Log($"[PrototypeC] 行为装配：{spec.objectName}（{spec.NormalizedId}）｜{result.Describe()}" +
                      $"｜碰撞体 {profile.colliderMode}/{profile.colliderSizing}｜刚体 {profile.bodyMode}");
            return result;
        }

        /// <summary>优先用表里的预制件；缺项 / 预制件没有配置组件时走内置默认装配。</summary>
        private static ObjectBehaviorProfile ResolveProfile(GameObject modelRoot, ObjectBehavior behavior,
            ObjectPlacement placement, BehaviorAssemblyTable table, BehaviorFallbackSettings fallbackSettings,
            ref BehaviorAssemblyResult result)
        {
            string label = BehaviorCells.Label(behavior, placement);

            if (table != null)
            {
                GameObject prefab = table.FindPrefab(behavior, placement);
                if (prefab != null)
                {
                    var instance = Object.Instantiate(prefab, modelRoot.transform, false);
                    instance.name = "Behavior_" + label;

                    var profile = instance.GetComponent<ObjectBehaviorProfile>();
                    if (profile != null) return profile;

                    Debug.LogError($"[PrototypeC] 行为装配：{label} 的预制件 {prefab.name} 上没有 ObjectBehaviorProfile，" +
                                   "已改用内置默认装配");
                    Object.Destroy(instance);
                }
            }

            // 缺项兜底：明确可见（Hierarchy 里有 *_FALLBACK 子节点）+ 明确报出缺的是哪一格
            result.usedFallback = true;
            result.note = Append(result.note, $"缺 {label} 的装配预制件，已用内置默认兜底");

            if (ReportedMissingCells.Add(label))
            {
                Debug.LogError($"[PrototypeC] 行为装配缺项：{label} 没有配置预制件，已用内置默认装配兜底。" +
                               "运行菜单 Tools/Prototype C/生成 9 个行为装配预制件 后回填到 ObjectModelService.behaviorAssemblies");
            }

            var host = new GameObject("Behavior_" + label + "_FALLBACK");
            host.transform.SetParent(modelRoot.transform, false);
            var fallbackProfile = host.AddComponent<ObjectBehaviorProfile>();
            BehaviorDefaults.Apply(fallbackProfile, behavior, placement, fallbackSettings);
            return fallbackProfile;
        }

        private static string Append(string note, string extra) =>
            string.IsNullOrEmpty(note) ? extra : note + "；" + extra;
    }
}
