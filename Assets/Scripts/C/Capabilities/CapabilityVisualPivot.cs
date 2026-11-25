// CapabilityVisualPivot.cs —— Prototype C：能力视觉枢轴
// 生成物件的根节点承担物理（Collider / Rigidbody）与布局（占用登记、台阶闸）职责，
// 直接旋转根节点会带动碰撞体、与 FreezeRotation 约束打架。
// 本工具在根节点「有物理组件」时，把它的全部直接子节点收进一个专用的视觉子节点：
//   - 子节点被摆成「世界直立」（抵消根节点 yaw=±90°），对它做 Z 轴小角度旋转＝世界屏幕面内倾斜；
//   - 根节点没有物理组件（手动摆放的纯视觉测试物件）时直接返回根节点，行为与旧版一致。
// AirflowResponder（旋转摆动）与 SoundResponder（位置轻抖）共用同一个枢轴，各写各的通道互不干扰。

using UnityEngine;

namespace PrototypeC
{
    public static class CapabilityVisualPivot
    {
        public const string PivotName = "Capability_VisualPivot";

        /// <summary>
        /// 取（必要时创建）物件的可旋转视觉枢轴。
        /// 只在根节点带 Rigidbody / Collider 时才创建子节点；重复调用返回同一个枢轴。
        /// </summary>
        public static Transform Ensure(GameObject root)
        {
            if (root == null) return null;

            bool physical = root.GetComponent<Rigidbody>() != null || root.GetComponent<Collider>() != null;
            if (!physical) return root.transform;

            Transform existing = root.transform.Find(PivotName);
            if (existing != null) return existing;

            var pivotGo = new GameObject(PivotName);
            Transform pivot = pivotGo.transform;
            pivot.SetParent(root.transform, false);
            // 世界直立：pivot 世界旋转 = root.rotation * Inverse(root.rotation) = identity
            pivot.localRotation = Quaternion.Inverse(root.transform.rotation);

            // 根节点现有的直接子节点（模型节点、Behavior_* 装配节点等）整体挪进枢轴，世界位置不变
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child == pivot) continue;
                child.SetParent(pivot, true);
            }

            Debug.Log($"[PrototypeC] 视觉枢轴：{root.name} 的渲染子节点已收进 {PivotName}（根节点保持物理与布局职责）");
            return pivot;
        }
    }
}
