// PromptStyle.cs —— Prototype C：可配置的 Prompt 风格层
// 共享模板整段作为固定前缀，[OBJECT DESCRIPTION] / [SIZE DESCRIPTION] 由「填充者」替换。
// 风格指纹（id + 版本 + 模板内容 + 贴图风格）会进入缓存指纹；换风格后旧缓存自然失效。

using System;
using System.Text;
using UnityEngine;

namespace PrototypeC
{
    [Serializable]
    public class PromptStyle
    {
        /// <summary>
        /// 共享 Prompt 模板（语义压缩版，保留全部硬性约束）。
        /// 模板本体 636 字符；Object + Size Reference 合计 ≤ 202 字符时可保证 prompt ≤ 800。
        /// 占位符由 objectPlaceholder / sizePlaceholder 指定。
        /// </summary>
        public const string DefaultTemplate =
            "A stylized 3D game asset for a cozy, unfinished apartment interior.\n" +
            "\n" +
            "Visual Style: charming, whimsical indie look; clean stylized silhouettes; " +
            "soft muted colors, simple materials; slightly exaggerated, believable proportions; " +
            "fitting a small dinosaur's living space.\n" +
            "\n" +
            "Model Requirements: only the requested object, no environment, floor, walls, background or props; " +
            "whole object visible; clean coherent mesh, moderate detail, no thin/fragile geometry; " +
            "appropriate proportions, recognizable construction; " +
            "centered on geometric center, main axis on Y; textured, game-ready.\n" +
            "\n" +
            "Object: [OBJECT DESCRIPTION]\n" +
            "Size Reference: [SIZE DESCRIPTION]";

        [Tooltip("风格标识，用于缓存与追溯")]
        public string styleId = "cozy-unfinished-apartment";

        [Tooltip("风格模板版本；改模板时同步 +1")]
        public int styleVersion = 2;

        [TextArea(10, 30)]
        [Tooltip("共享 Prompt 模板（固定前缀 + 两个占位符）")]
        public string template = DefaultTemplate;

        public string objectPlaceholder = "[OBJECT DESCRIPTION]";
        public string sizePlaceholder = "[SIZE DESCRIPTION]";

        [TextArea(2, 5)]
        [Tooltip("refine 阶段使用的贴图提示词（texture_prompt），保证贴图风格一致")]
        public string texturePrompt =
            "Soft, muted, slightly whimsical stylized colors; clean game-ready surface detail; " +
            "consistent with a cozy, unfinished small apartment for a small dinosaur";

        [Tooltip("Meshy 官方限制：prompt 最多 800 字符")]
        public int maxPromptLength = PromptComposer.MeshyPromptLimit;

        /// <summary>
        /// 超长兜底：按顺序从 prompt 中移除这些短语（当前模板为整段行，故按短语匹配）。
        /// 仅在 Object + Size Reference 合计超过 ~202 字符时才会触发；
        /// 顺序从「最不影响几何与贴图」到「较关键」，Y 轴/居中/只生成单个物体等不在此列。
        /// </summary>
        public string[] droppableLines =
        {
            "slightly exaggerated, believable proportions; ",
            "whole object visible; ",
            "moderate detail, ",
            "appropriate proportions, recognizable construction; ",
        };

        /// <summary>风格指纹：id、版本、模板内容、贴图提示词任一变化都会让旧缓存失效。</summary>
        public string Fingerprint()
        {
            string raw = string.Join("|", styleId, styleVersion, template, objectPlaceholder, sizePlaceholder, texturePrompt)
                         + "|" + string.Join("|", droppableLines ?? Array.Empty<string>());
            return StableHash(raw);
        }

        /// <summary>稳定的 64 位 FNV-1a 十六进制指纹（不依赖 GetHashCode 的进程随机化）。</summary>
        public static string StableHash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            foreach (char c in value ?? string.Empty)
            {
                hash ^= c;
                hash *= prime;
            }

            var sb = new StringBuilder(16);
            sb.Append(hash.ToString("x16"));
            return sb.ToString();
        }
    }
}
