// LlmParseTestRunner.cs —— Prototype C：LLM 物件解析的测试入口（编辑器菜单）
// 两种模式：
//   1) Offline Self-Test (Mock)：用脚本化 mock 响应验证「通道 → JSON 提取 → Schema 校验 → ObjectSpec」全链路，
//      不访问网络，也不代表真实 LLM 的输出质量。
//   2) Live Cases (Needs Key)：用真实 API 跑 15 组固定输入，逐条记录原始输出 / 校验结果 / 最终 ObjectSpec。
// 报告写到 <工程根>/Logs/PrototypeC_LlmParse_<时间戳>.md（Logs 已被 .gitignore 忽略）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PrototypeC.EditorTools
{
    public static class LlmParseTestRunner
    {
        private static readonly string[] Cases =
        {
            "CHAIR", "TABLE", "LAMP", "TREE", "FLOWER", "FLOWER POT", "BOOKSHELF",
            "BAT", "ROCK", "CHAIRS", "LOVE", "HAPPINESS", "DOG", "CAT", "QZXVBNMPL",
        };

        [MenuItem("Tools/Prototype C/LLM Parse/Run Offline Self-Test (Mock)")]
        public static async void RunMock()
        {
            var client = BuildScriptedClient();
            var parser = new LlmObjectParser(client) { maxAttempts = 1 };

            var rows = new List<CaseRow>();
            foreach (string input in Cases)
            {
                ObjectParseResult result = await parser.ParseAsync(input, CancellationToken.None);
                rows.Add(new CaseRow(input, result));
            }

            // 校验器的负面用例：这些必须被拒绝
            foreach (KeyValuePair<string, string> bad in BadPayloads)
            {
                ObjectParseResult result = ObjectDefinitionValidator.Validate(bad.Value);
                rows.Add(new CaseRow("[校验器] " + bad.Key, result));
            }

            string path = WriteReport("mock", "脚本化 mock（离线自测，非真实 LLM 输出）", rows);
            Debug.Log($"[PrototypeC] 离线自测完成：{path}");
            EditorUtility.DisplayDialog("Prototype C", "离线自测完成（mock，非真实 LLM）：\n" + path, "OK");
        }

        [MenuItem("Tools/Prototype C/LLM Parse/Run Live Cases (Needs Key)")]
        public static async void RunLive()
        {
            if (!LlmConfig.HasApiKey)
            {
                EditorUtility.DisplayDialog(
                    "Prototype C",
                    "未配置 LLM API Key，无法跑真实用例。\n\n" +
                    $"任选一种方式：\n1) 环境变量 {LlmConfig.ApiKeyEnvironmentVariable}\n" +
                    $"2) {LlmConfig.ProjectRootConfigPath}（apiKey / baseUrl / model，已加入 .gitignore）\n" +
                    $"3) {LlmConfig.PersistentConfigPath}",
                    "OK");
                return;
            }

            LlmObjectParser parser = LlmObjectParser.CreateDefault();
            var rows = new List<CaseRow>();
            int ok = 0;

            foreach (string input in Cases)
            {
                Debug.Log($"[PrototypeC] 真实用例：{input} …");
                try
                {
                    ObjectParseResult result = await parser.ParseAsync(input, CancellationToken.None);
                    rows.Add(new CaseRow(input, result));
                    if (result.IsSuccess) ok++;
                    Debug.Log($"[PrototypeC] {input} → {result.status}｜{result.Describe()}\n原始输出：{result.rawText}");
                }
                catch (Exception e)
                {
                    var failed = new ObjectParseResult
                    {
                        status = ObjectParseStatus.RequestFailed,
                        error = e.Message,
                    };
                    rows.Add(new CaseRow(input, failed));
                    Debug.LogError($"[PrototypeC] {input} 请求异常：{e.Message}");
                }
            }

            string channel = $"{LlmConfig.Model} @ {LlmConfig.BaseUrl}（Key 来源：{LlmConfig.Source}）";
            string path = WriteReport("live", channel, rows);
            Debug.Log($"[PrototypeC] 真实用例完成 {ok}/{rows.Count}：{path}");
            EditorUtility.DisplayDialog("Prototype C", $"真实用例完成：成功 {ok}/{rows.Count}\n报告：{path}", "OK");
        }

        // ---------- mock ----------

        private static ILlmChatClient BuildScriptedClient()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHAIR"] = "```json\n{\"objectName\":\"chair\",\"objectDescription\":\"A simple wooden chair with four legs, a flat seat and a low backrest.\",\"sizeDescription\":\"about 0.5 m wide, 0.5 m deep, 0.9 m tall\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":\"Medium\"}\n```",
                ["TABLE"] = "Here is the json you asked for:\n{\"objectName\":\"table\",\"objectDescription\":\"A small wooden dining table with a rectangular top and four straight legs.\",\"sizeDescription\":\"about 1.2 m wide, 0.7 m deep, 0.75 m tall\",\"behavior\":\"Touch\",\"placement\":\"Ground\",\"size\":\"Medium\"}\nHope this helps.",
                ["LAMP"] = "{\"objectName\":\"lamp\",\"objectDescription\":\"A small table lamp with a round base and a conical fabric shade.\",\"sizeDescription\":\"about 0.3 m wide, 0.3 m deep, 0.45 m tall\",\"behavior\":\"Touch\",\"placement\":\"Ground\",\"size\":\"Small\"}",
                ["TREE"] = "{\"objectName\":\"tree\",\"objectDescription\":\"A potted indoor tree with a slim trunk and a rounded cluster of leaves.\",\"sizeDescription\":\"about 0.6 m wide, 0.6 m deep, 1.6 m tall\",\"behavior\":\"Ignore\",\"placement\":\"Ground\",\"size\":\"Large\"}",
                ["FLOWER"] = "{\"objectName\":\"flower\",\"objectDescription\":\"A single flower with a thin green stem and a few rounded petals.\",\"sizeDescription\":\"about 0.15 m wide, 0.15 m deep, 0.3 m tall\",\"behavior\":\"Ignore\",\"placement\":\"Ground\",\"size\":\"Small\"}",
                ["FLOWER POT"] = "{\"objectName\":\"flower pot\",\"objectDescription\":\"A tapered ceramic pot filled with soil and a small flowering plant.\",\"sizeDescription\":\"about 0.25 m wide, 0.25 m deep, 0.35 m tall\",\"behavior\":\"Touch\",\"placement\":\"Ground\",\"size\":\"Small\"}",
                ["BOOKSHELF"] = "{\"objectName\":\"bookshelf\",\"objectDescription\":\"A tall wooden bookshelf with four open shelves and a solid back panel.\",\"sizeDescription\":\"about 0.9 m wide, 0.35 m deep, 1.8 m tall\",\"behavior\":\"Touch\",\"placement\":\"Ground\",\"size\":\"Large\"}",
                ["BAT"] = "{\"objectName\":\"baseball bat\",\"objectDescription\":\"A smooth wooden baseball bat with a thick barrel and a narrow handle.\",\"sizeDescription\":\"about 0.07 m wide, 0.07 m deep, 0.85 m long\",\"behavior\":\"Play\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
                ["ROCK"] = "{\"objectName\":\"rock\",\"objectDescription\":\"A rounded grey stone with a few flat facets and a rough surface.\",\"sizeDescription\":\"about 0.4 m wide, 0.35 m deep, 0.3 m tall\",\"behavior\":\"Ignore\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
                ["CHAIRS"] = "{\"objectName\":\"chair\",\"objectDescription\":\"A simple wooden chair with four legs, a flat seat and a low backrest.\",\"sizeDescription\":\"about 0.5 m wide, 0.5 m deep, 0.9 m tall\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
                ["LOVE"] = "{\"unrecognized\":true,\"reason\":\"abstract concept, not a physical object\"}",
                ["HAPPINESS"] = "{\"unrecognized\":true,\"reason\":\"abstract emotion, not a physical object\"}",
                ["DOG"] = "{\"unrecognized\":true,\"reason\":\"animal, not allowed\"}",
                ["CAT"] = "{\"unrecognized\":true,\"reason\":\"animal, not allowed\"}",
                ["QZXVBNMPL"] = "{\"unrecognized\":true,\"reason\":\"nonsense input\"}",
            };

            return new ScriptedChatClient(map);
        }

        private static readonly Dictionary<string, string> BadPayloads = new Dictionary<string, string>
        {
            ["非法枚举"] = "{\"objectName\":\"chair\",\"objectDescription\":\"a chair\",\"sizeDescription\":\"0.5 m wide\",\"behavior\":\"Sit\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
            ["多余字段"] = "{\"objectName\":\"chair\",\"objectDescription\":\"a chair\",\"sizeDescription\":\"0.5 m wide\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":\"Medium\",\"yawDegrees\":\"90\"}",
            ["缺少字段"] = "{\"objectName\":\"chair\",\"objectDescription\":\"a chair\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
            ["非法组合 Rest+Wall"] = "{\"objectName\":\"painting\",\"objectDescription\":\"a small framed painting\",\"sizeDescription\":\"0.4 m wide\",\"behavior\":\"Rest\",\"placement\":\"Wall\",\"size\":\"Small\"}",
            ["描述超限"] = "{\"objectName\":\"chair\",\"objectDescription\":\"" + new string('a', 150) + "\",\"sizeDescription\":\"" + new string('b', 60) + "\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":\"Medium\"}",
            ["非字符串值"] = "{\"objectName\":\"chair\",\"objectDescription\":\"a chair\",\"sizeDescription\":\"0.5 m wide\",\"behavior\":\"Rest\",\"placement\":\"Ground\",\"size\":3}",
            ["非 JSON"] = "Sorry, I cannot help with that.",
            ["unrecognized=false"] = "{\"unrecognized\":false,\"reason\":\"wrong flag\"}",
        };

        private class ScriptedChatClient : ILlmChatClient
        {
            private readonly Dictionary<string, string> _responses;

            public ScriptedChatClient(Dictionary<string, string> responses)
            {
                _responses = responses;
            }

            public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct)
            {
                string input = userContent ?? string.Empty;
                int newline = input.LastIndexOf('\n');
                if (newline >= 0) input = input.Substring(newline + 1).Trim();

                if (!_responses.TryGetValue(input, out string reply))
                {
                    reply = "{\"unrecognized\":true,\"reason\":\"no scripted response for " + input + "\"}";
                }

                return Task.FromResult(reply);
            }
        }

        // ---------- 报告 ----------

        private class CaseRow
        {
            public readonly string input;
            public readonly ObjectParseResult result;

            public CaseRow(string input, ObjectParseResult result)
            {
                this.input = input;
                this.result = result;
            }
        }

        private static string WriteReport(string mode, string channel, List<CaseRow> rows)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"PrototypeC_LlmParse_{mode}_{DateTime.Now:yyyyMMdd_HHmmss}.md");

            var sb = new StringBuilder();
            sb.AppendLine("# Prototype C — LLM 物件解析测试报告");
            sb.AppendLine();
            sb.AppendLine($"- 模式：{mode}");
            sb.AppendLine($"- 通道：{channel}");
            sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("| # | 输入 | 状态 | 校验结果 | objectName | 缓存标识 | behavior | placement | size | 描述字符 | 备注 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

            int ok = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                CaseRow row = rows[i];
                ObjectDefinition def = row.result.definition;
                if (row.result.IsSuccess) ok++;

                ObjectSpec spec = BuildSpec(row.result);
                int chars = def != null ? def.objectDescription.Length + def.sizeDescription.Length : 0;
                sb.AppendLine(
                    $"| {i + 1} | {Cell(row.input)} | {row.result.status} | {row.result.Describe()} | " +
                    $"{Cell(def?.objectName)} | {Cell(spec?.NormalizedId)} | {def?.behavior.ToString() ?? ""} | {def?.placement.ToString() ?? ""} | " +
                    $"{def?.size.ToString() ?? ""} | {(def != null ? chars.ToString() : "")} | {Cell(row.result.error)} |");
            }

            sb.AppendLine();
            sb.AppendLine($"成功 {ok}/{rows.Count}");

            sb.AppendLine();
            sb.AppendLine("## 最终 ObjectSpec（交给 ObjectModelService 的实际输入）");
            sb.AppendLine();
            for (int i = 0; i < rows.Count; i++)
            {
                CaseRow row = rows[i];
                sb.AppendLine($"### {i + 1}. {row.input}");
                ObjectSpec spec = BuildSpec(row.result);
                if (spec == null)
                {
                    sb.AppendLine($"- 未构建（{row.result.status}）");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"- objectName: {Cell(spec.objectName)}");
                sb.AppendLine($"- 缓存标识: {spec.NormalizedId}");
                sb.AppendLine($"- objectDescription: {Cell(spec.objectDescription)}");
                sb.AppendLine($"- sizeDescription: {Cell(spec.sizeDescription)}");
                sb.AppendLine($"- behavior / placement / size: {spec.behavior} / {spec.placementKind} / {spec.sizeClass}");
                sb.AppendLine($"- prompt: {spec.prompt.Length} 字符，trimmed={spec.promptTrimmed}");
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("## 原始输出");
            sb.AppendLine();
            for (int i = 0; i < rows.Count; i++)
            {
                sb.AppendLine($"### {i + 1}. {rows[i].input}");
                sb.AppendLine("```");
                sb.AppendLine(string.IsNullOrEmpty(rows[i].result.rawText) ? "(空)" : rows[i].result.rawText);
                sb.AppendLine("```");
            }

            File.WriteAllText(path, sb.ToString());
            return path;
        }

        /// <summary>把成功的解析结果走一遍真实构建路径；失败返回 null。Unity 管理字段用默认值占位。</summary>
        private static ObjectSpec BuildSpec(ObjectParseResult result)
        {
            if (result == null || !result.IsSuccess || result.definition == null) return null;

            return ObjectSpecFactory.Create(
                result.definition,
                new PromptStyle(),
                new GenerationParams(),
                90f,
                new Vector3(0.55f, 0.95f, 0.55f),
                Vector3.zero);
        }

        private static string Cell(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("|", "\\|").Replace("\n", " ");
        }
    }
}
