// PrototypeCStage.cs —— Prototype C 阶段裁剪：只对 TemplateC_LanMay 生效
// 当前阶段只测「输入 → LLM → Meshy」，所以在这个场景里关掉 B1 的玩法噪音：
//   ① 天灾（红色威胁 / 火灾）    → B1EventScheduler
//   ② 恐龙盖房子（WOOD / STONE） → DinoAgentB1
//   ③ 拼出 CHAIR 生成椅子        → TypeWriterB1
// 其他场景（TemplateB1 / TemplateB2）不受影响：按场景名判定，而不是改共享默认值。
// C# 侧仍保留各自的 Inspector 开关（threatsEnabled / constructionEnabled / chairCraftingEnabled），
// 场景名命中时会在 Awake 里把它们置为关闭。

using UnityEngine;

namespace PrototypeC
{
    public static class PrototypeCStage
    {
        /// <summary>唯一需要裁剪的场景名。</summary>
        public const string TrimmedSceneName = "TemplateC_LanMay";

        /// <summary>该物体是否在裁剪场景里（编辑器与运行时都按当前场景名判断）。</summary>
        public static bool IsTrimmedScene(GameObject go)
        {
            return go != null && string.Equals(go.scene.name, TrimmedSceneName);
        }
    }
}
