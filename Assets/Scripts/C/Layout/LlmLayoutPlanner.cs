// LlmLayoutPlanner.cs —— Prototype C：布局意图（阶段 4）
// 输入：物件定义 + 缩放后的真实包围盒 + 房间描述 + 同分区占用；
// 输出：LayoutIntent（锚点 + 横向偏移 + 朝向），交给 LayoutSolver 校验后落位。
// 横向卷轴视角：意图只在 X 上有意义；Z 由分带的固定 depth 决定，解析时忽略。
// 任何失败（没有 Key / 网络 / JSON 不合规 / 锚点不存在）都返回 null，
// 调用方退回纯规则求解——布局调用绝不阻断生成。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PrototypeC
{
    public class LlmLayoutPlanner
    {
        /// <summary>Schema 不合法时的总尝试次数（含首次）。</summary>
        public int maxAttempts = 2;

        private readonly ILlmChatClient _client;

        /// <summary>最近一次原始响应，便于排错。</summary>
        public string LastRaw { get; private set; } = string.Empty;

        /// <summary>最近一次成功解析的意图。</summary>
        public LayoutIntent LastIntent { get; private set; }

        public LlmLayoutPlanner(ILlmChatClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public static LlmLayoutPlanner CreateDefault()
        {
            var client = new LlmChatClient(LlmConfig.ApiKey, LlmConfig.BaseUrl, LlmConfig.Model)
            {
                disableThinking = LlmConfig.DisableThinking,
                maxTokens = 400,
            };

            return new LlmLayoutPlanner(client);
        }

        /// <summary>
        /// 让 LLM 给一个摆放意图。size 是「未旋转」的实际包围盒（米）。
        /// 返回 null 表示这次没拿到可用意图，调用方应传 null 给 PlaceInstance 走规则。
        /// </summary>
        public async Task<LayoutIntent> PlanAsync(ObjectSpec spec, Vector3 size, RoomLayout layout,
            IReadOnlyList<PlacedItem> occupants, int maxOccupants = 12, CancellationToken ct = default)
        {
            if (spec == null || layout == null) return null;

            if (_client is LlmChatClient http && string.IsNullOrEmpty(http.apiKey))
            {
                Debug.LogWarning("[PrototypeC] 布局意图：未配置 LLM API Key，改用规则求解");
                return null;
            }

            RoomBand band = RoomBands.FromPlacementKind(spec.placementKind);
            string user = LlmLayoutPrompt.BuildUserPrompt(spec, size, band, layout, occupants, maxOccupants);

            string lastError = "未知错误";

            for (int attempt = 1; attempt <= Mathf.Max(1, maxAttempts); attempt++)
            {
                string raw;
                try
                {
                    raw = await _client.CompleteAsync(LlmLayoutPrompt.SystemPrompt, user, ct);
                }
                catch (OperationCanceledException)
                {
                    Debug.LogWarning("[PrototypeC] 布局意图请求已取消，改用规则求解");
                    return null;
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    if (attempt < maxAttempts) continue;
                    break;
                }

                LastRaw = raw;

                LayoutIntent intent = Parse(raw, spec, band, layout, out string error);
                if (intent != null)
                {
                    LastIntent = intent;
                    Debug.Log($"[PrototypeC] 布局意图：{spec.objectName}（{spec.NormalizedId}）｜" +
                              $"锚点 {intent.anchor} + X {intent.offsetX:0.00}｜" +
                              $"facing {Facings.ToText(intent.facing)}｜yaw {intent.yawDegrees:0}｜" +
                              $"置信度 {intent.confidence:0.00}｜{intent.hint}");
                    return intent;
                }

                lastError = error;
                if (attempt < maxAttempts)
                    Debug.LogWarning($"[PrototypeC] 布局意图未通过校验（{error}），重试第 {attempt + 1} 次");
            }

            Debug.LogWarning($"[PrototypeC] 布局意图获取失败，改用规则求解：{lastError}" +
                             (string.IsNullOrEmpty(LastRaw) ? string.Empty : $"\n原始输出：{LastRaw}"));
            return null;
        }

        /// <summary>严格解析 + 与 RoomLayout 对齐校验；任何一处不合规都返回 null。</summary>
        private static LayoutIntent Parse(string raw, ObjectSpec spec, RoomBand band, RoomLayout layout,
            out string error)
        {
            error = null;

            if (!FlatJson.TryExtractObject(raw, out string json, out error)) return null;

            // 模型偶尔把数字写成裸字面量：这里把非字符串标量补上引号，再交给严格解析器
            string normalized = QuoteScalarValues(json);
            if (!FlatJson.TryParseFlatObject(normalized, out Dictionary<string, string> values, out error)) return null;

            values.TryGetValue("anchor", out string anchorId);
            if (string.IsNullOrWhiteSpace(anchorId))
            {
                error = "缺少 anchor";
                return null;
            }

            RoomAnchorPoint anchor = FindAnchor(layout, anchorId.Trim(), band);
            if (anchor == null)
            {
                error = $"锚点 {anchorId} 不存在或分区不匹配（{RoomBands.ToPlacementKind(band)}）";
                return null;
            }

            float offsetX = ReadFloat(values, "offsetX", 0f);

            // 挂墙：alongWall 是绝对 X；换算成相对该锚点的偏移，避免与 offsetX 重复累加
            if (band == RoomBand.Wall)
            {
                if (values.TryGetValue("alongWall", out string alongText)
                    && TryParseFloat(alongText, out float alongWall))
                {
                    offsetX = alongWall - anchor.x;
                }
            }

            // 横向卷轴视角：只保留 X 偏移；模型若仍输出 offsetZ，这里直接忽略（不读、不存、不参与计算）
            float maxX = Mathf.Max(0f, anchor.maxOffsetX);
            offsetX = Mathf.Clamp(offsetX, -maxX, maxX);

            // facing 只是「建议」：最终朝向按落点重新推导（右半区背靠右墙、左半区背靠左墙），
            // 求解器会在两者不一致时记日志并采用落点结论。
            string facing = string.Empty;
            if (values.TryGetValue("facing", out string facingText) && !string.IsNullOrWhiteSpace(facingText))
            {
                if (Facings.TryNormalize(facingText, out string normalizedFacing))
                {
                    facing = normalizedFacing;
                }
                else
                {
                    Debug.LogWarning($"[PrototypeC] 布局意图：无法识别的 facing「{facingText}」，改用落点推导");
                }
            }

            return new LayoutIntent
            {
                band = RoomBands.ToPlacementKind(band),
                anchor = anchor.id,
                offsetX = offsetX,
                yawDegrees = ReadFloat(values, "yawDegrees", spec != null ? spec.yawDegrees : 90f),
                facing = facing,
                alongWall = 0f,
                confidence = Mathf.Clamp01(ReadFloat(values, "confidence", 0f)),
                hint = values.TryGetValue("hint", out string hint) ? hint : string.Empty,
                source = "llm",
            };
        }

        private static RoomAnchorPoint FindAnchor(RoomLayout layout, string id, RoomBand band)
        {
            if (layout == null || layout.anchors == null) return null;

            foreach (RoomAnchorPoint candidate in layout.anchors)
            {
                if (candidate == null || candidate.band != band) continue;
                if (string.Equals(candidate.id, id, StringComparison.OrdinalIgnoreCase)) return candidate;
            }

            return null;
        }

        private static float ReadFloat(Dictionary<string, string> values, string key, float fallback)
        {
            return values.TryGetValue(key, out string text) && TryParseFloat(text, out float value) ? value : fallback;
        }

        private static bool TryParseFloat(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// 把 json 里「非字符串的标量值」补上引号（例如 "offsetX": 0.75 → "offsetX": "0.75"），
        /// 其余结构原样保留；字符串内部（含转义）不受影响。
        /// </summary>
        private static string QuoteScalarValues(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            var sb = new StringBuilder(json.Length + 32);
            bool inString = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    sb.Append(c);

                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[i + 1]);
                        i++;
                        continue;
                    }

                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                if (c != ':')
                {
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);

                int j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) sb.Append(json[j++]);

                if (j >= json.Length) break;

                char valueStart = json[j];
                if (valueStart == '"' || valueStart == '{' || valueStart == '[')
                {
                    i = j - 1;
                    continue;
                }

                int start = j;
                while (j < json.Length && json[j] != ',' && json[j] != '}') j++;

                string token = json.Substring(start, j - start).Trim();
                sb.Append('"').Append(token).Append('"');
                i = j - 1;
            }

            return sb.ToString();
        }
    }
}
