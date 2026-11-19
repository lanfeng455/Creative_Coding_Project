// PromptComposer.cs —— Prototype C：Prompt 拼装层
// 开发者只需传入 objectDescription / sizeDescription，风格层负责其余部分。
// 将来接 LLM 时实现 IPromptFiller 即可，不需要修改拼装逻辑。
//
// 注意：Meshy 官方限制 prompt ≤ 800 字符（https://docs.meshy.ai/en/api/text-to-3d）。
// 共享模板已按语义压缩到 636 字符：Object + Size Reference 合计 ≤ 202 字符时不会触发裁剪。
// 更长的输入才会走兜底：先按 droppableLines 移除冗余短语，再整行移除 “- ” 列表项，最后才截断；
// 每次裁剪都写进报告与 metadata，便于追溯。

using System;
using System.Collections.Generic;

namespace PrototypeC
{
    /// <summary>「填充者」接口：现在由测试入口手动填，将来由 LLM 实现。</summary>
    public interface IPromptFiller
    {
        bool TryFill(string objectName, out string objectDescription, out string sizeDescription);
    }

    public struct PromptComposeReport
    {
        public string prompt;
        public bool trimmed;
        public string trimNote;
        public int limit;
    }

    public static class PromptComposer
    {
        /// <summary>Meshy Text-to-3D 官方上限。</summary>
        public const int MeshyPromptLimit = 800;

        public static string ComposePrompt(PromptStyle style, string objectDescription, string sizeDescription)
        {
            return Compose(style, objectDescription, sizeDescription).prompt;
        }

        public static PromptComposeReport Compose(PromptStyle style, string objectDescription, string sizeDescription)
        {
            style ??= new PromptStyle();

            string template = string.IsNullOrEmpty(style.template) ? PromptStyle.DefaultTemplate : style.template;
            string objectText = string.IsNullOrWhiteSpace(objectDescription) ? "unspecified object" : objectDescription.Trim();
            string sizeText = string.IsNullOrWhiteSpace(sizeDescription) ? "no specific size reference" : sizeDescription.Trim();

            string prompt = template
                .Replace("\r\n", "\n")
                .Replace(style.objectPlaceholder, objectText)
                .Replace(style.sizePlaceholder, sizeText)
                .Trim();

            int limit = Math.Clamp(style.maxPromptLength <= 0 ? MeshyPromptLimit : style.maxPromptLength, 200, MeshyPromptLimit);

            var report = new PromptComposeReport
            {
                prompt = prompt,
                limit = limit,
                trimmed = false,
                trimNote = string.Empty,
            };

            if (prompt.Length <= limit) return report;

            // 1) 先压缩空行，不改动任何文字内容
            string compact = prompt.Replace("\n\n", "\n");
            if (compact.Length <= limit)
            {
                report.prompt = compact;
                report.trimmed = true;
                report.trimNote = $"压缩空行以满足 {limit} 字符上限";
                return report;
            }

            var dropped = new List<string>();

            // 2) 仍超限：按 droppableLines 优先级移除冗余短语（当前模板是整段行，按短语匹配）
            if (style.droppableLines != null)
            {
                foreach (string phrase in style.droppableLines)
                {
                    if (compact.Length <= limit) break;
                    if (string.IsNullOrWhiteSpace(phrase)) continue;

                    int index = compact.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;

                    compact = compact.Remove(index, phrase.Length);
                    dropped.Add(phrase.Trim());
                }
            }

            // 3) 仍超限：整行移除 “- ” 列表项（兼容分条模板）
            var lines = new List<string>(compact.Split('\n'));
            for (int i = lines.Count - 1; i >= 0 && CurrentLength(lines) > limit; i--)
            {
                string content = lines[i].Trim();
                if (!content.StartsWith("- ", StringComparison.Ordinal)) continue;

                dropped.Add(content);
                lines.RemoveAt(i);
            }

            prompt = string.Join("\n", lines);

            if (dropped.Count > 0)
            {
                report.trimmed = true;
                report.trimNote = $"为满足 {limit} 字符上限，移除了 {dropped.Count} 条要求：" + string.Join("；", dropped);
            }

            // 4) 极端情况：截断到最后一段完整文字
            if (prompt.Length > limit)
            {
                int cut = prompt.LastIndexOf(' ', Math.Min(limit, prompt.Length - 1));
                if (cut <= 0) cut = limit;
                prompt = prompt.Substring(0, cut).TrimEnd();
                report.trimmed = true;
                report.trimNote = $"为满足 {limit} 字符上限，末尾内容被截断";
            }

            report.prompt = prompt;
            return report;
        }

        private static int CurrentLength(List<string> lines)
        {
            int total = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                total += lines[i].Length;
                if (i < lines.Count - 1) total += 1;
            }
            return total;
        }
    }
}
