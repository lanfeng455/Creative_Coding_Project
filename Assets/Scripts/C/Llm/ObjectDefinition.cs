// ObjectDefinition.cs —— Prototype C：LLM 物件定义（6 必填字段 + 可选 capabilities）+ 严格 Schema 校验
// 校验点：必填字段集合一致、全部为字符串、枚举严格匹配、合法组合、两段描述合计 ≤ 202 字符；
//         capabilities 为唯一可选键，ID 未注册时只告警忽略、不让物件失败。
// 校验不通过一律不构造 ObjectSpec，避免把非法结果喂给 Meshy。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    public enum ObjectBehavior { Touch, Rest, Play, Ignore }
    public enum ObjectPlacement { Ground, Wall, Rooftop }
    public enum ObjectSize { Small, Medium, Large, SuperLarge }

    /// <summary>LLM 解析出的标准化物件定义：6 个必填字段 + 1 个可选能力字段（capabilities）。</summary>
    [Serializable]
    public class ObjectDefinition
    {
        public string objectName;
        public string objectDescription;
        public string sizeDescription;
        public ObjectBehavior behavior;
        public ObjectPlacement placement;
        public ObjectSize size;

        /// <summary>能力 ID（已按 ObjectCapabilityIds 规范化）；无能力时为空数组，不影响生成。</summary>
        public string[] capabilities = Array.Empty<string>();

        public string Describe()
        {
            string cap = capabilities != null && capabilities.Length > 0
                ? "｜cap=" + string.Join(",", capabilities)
                : string.Empty;
            return $"{objectName}｜behavior={behavior}｜placement={placement}｜size={size}{cap}";
        }
    }

    public enum ObjectParseStatus
    {
        Ok,
        Unrecognized,
        InvalidSchema,
        InvalidCombination,
        RequestFailed,
    }

    public class ObjectParseResult
    {
        public ObjectParseStatus status;
        public ObjectDefinition definition;
        public string error = string.Empty;
        public string rawText = string.Empty;

        /// <summary>true = 结果来自物件字典（直接命中或字典纠错），本次没有消耗 LLM 请求。</summary>
        public bool fromDictionary;

        public bool IsSuccess => status == ObjectParseStatus.Ok;

        public string Describe()
        {
            switch (status)
            {
                case ObjectParseStatus.Ok:
                    return definition != null ? definition.Describe() : "解析成功";
                case ObjectParseStatus.Unrecognized:
                    return "LLM 判定为不可识别物件" + (string.IsNullOrEmpty(error) ? "" : "：" + error);
                case ObjectParseStatus.InvalidSchema:
                    return "Schema 校验失败：" + error;
                case ObjectParseStatus.InvalidCombination:
                    return "枚举组合非法：" + error;
                default:
                    return "请求失败：" + error;
            }
        }
    }

    public static class ObjectDefinitionValidator
    {
        /// <summary>两段描述合计上限：超过会触发 800 字符裁剪，因此直接判为不合法。</summary>
        public const int MaxDescriptionChars = 202;

        private static readonly string[] RequiredKeys =
        {
            "objectName", "objectDescription", "sizeDescription", "behavior", "placement", "size",
        };

        /// <summary>可选键：物件能力 ID（逗号分隔字符串）。唯一允许的第七个键，缺省 / 留空都合法。</summary>
        public const string CapabilitiesKey = "capabilities";

        /// <summary>校验任意文本（可带 ```json 包裹或前后废话）。</summary>
        public static ObjectParseResult Validate(string rawText)
        {
            var result = new ObjectParseResult
            {
                rawText = rawText ?? string.Empty,
                status = ObjectParseStatus.InvalidSchema,
            };

            if (!FlatJson.TryExtractObject(rawText, out string json, out string extractError))
            {
                result.error = extractError;
                return result;
            }

            if (!FlatJson.TryParseFlatObject(json, out Dictionary<string, string> values, out string parseError))
            {
                result.error = parseError;
                return result;
            }

            // 不可识别的唯一合法形态：{"unrecognized":true,"reason":"..."}
            if (values.ContainsKey("unrecognized"))
            {
                if (!string.Equals(values["unrecognized"], "true", StringComparison.OrdinalIgnoreCase))
                {
                    result.error = "unrecognized 的值必须是 true";
                    return result;
                }

                if (values.Count == 1)
                {
                    result.status = ObjectParseStatus.Unrecognized;
                    return result;
                }

                if (values.Count == 2 && values.ContainsKey("reason"))
                {
                    result.status = ObjectParseStatus.Unrecognized;
                    result.error = values["reason"];
                    return result;
                }

                result.error = "unrecognized 形式只允许 unrecognized / reason 两个字段";
                return result;
            }

            var missing = new List<string>();
            foreach (string key in RequiredKeys)
            {
                if (!values.ContainsKey(key)) missing.Add(key);
            }

            var extra = new List<string>();
            foreach (string key in values.Keys)
            {
                if (Array.IndexOf(RequiredKeys, key) < 0) extra.Add(key);
            }

            // capabilities 是唯一允许的可选键：不计入「多余字段」
            extra.RemoveAll(key => string.Equals(key, CapabilitiesKey, StringComparison.Ordinal));

            if (missing.Count > 0 || extra.Count > 0
                || values.Count < RequiredKeys.Length || values.Count > RequiredKeys.Length + 1)
            {
                result.error = "字段集合不符" +
                               (missing.Count > 0 ? "；缺少 " + string.Join(", ", missing) : "") +
                               (extra.Count > 0 ? "；多余 " + string.Join(", ", extra) : "");
                return result;
            }

            foreach (string key in RequiredKeys)
            {
                if (string.IsNullOrWhiteSpace(values[key]))
                {
                    result.error = $"字段 {key} 为空";
                    return result;
                }
            }

            string objectDescription = values["objectDescription"].Trim();
            string sizeDescription = values["sizeDescription"].Trim();

            int descriptionChars = objectDescription.Length + sizeDescription.Length;
            if (descriptionChars > MaxDescriptionChars)
            {
                result.error = $"objectDescription + sizeDescription 共 {descriptionChars} 字符，超过上限 {MaxDescriptionChars}";
                return result;
            }

            if (!TryParseEnum(values["behavior"], out ObjectBehavior behavior))
            {
                result.error = "behavior 必须是 Touch / Rest / Play / Ignore 之一，实际为 " + values["behavior"];
                return result;
            }

            if (!TryParseEnum(values["placement"], out ObjectPlacement placement))
            {
                result.error = "placement 必须是 Ground / Wall / Rooftop 之一，实际为 " + values["placement"];
                return result;
            }

            if (!TryParseEnum(values["size"], out ObjectSize size))
            {
                result.error = "size 必须是 Small / Medium / Large / SuperLarge 之一，实际为 " + values["size"];
                return result;
            }

            if (!IsCombinationAllowed(behavior, placement))
            {
                result.status = ObjectParseStatus.InvalidCombination;
                result.error = $"{behavior} 不允许放在 {placement}";
                return result;
            }

            result.status = ObjectParseStatus.Ok;
            result.definition = new ObjectDefinition
            {
                objectName = values["objectName"].Trim(),
                objectDescription = objectDescription,
                sizeDescription = sizeDescription,
                behavior = behavior,
                placement = placement,
                size = size,
                capabilities = ParseCapabilities(values),
            };

            return result;
        }

        /// <summary>
        /// 解析可选 capabilities 字段：逗号 / 分号 / 顿号 / 空格分隔的 ID 串。
        /// 未注册的 ID 只告警忽略（不让物件失败）；通过 ObjectCapabilityIds 规范化并去重。
        /// </summary>
        private static string[] ParseCapabilities(Dictionary<string, string> values)
        {
            if (!values.TryGetValue(CapabilitiesKey, out string raw) || string.IsNullOrWhiteSpace(raw))
                return Array.Empty<string>();

            var accepted = new List<string>();
            string[] tokens = raw.Split(new[] { ',', ';', '、', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (ObjectCapabilityIds.TryNormalize(token, out string id))
                {
                    if (!accepted.Contains(id)) accepted.Add(id);
                }
                else
                {
                    Debug.LogWarning($"[PrototypeC] LLM 能力：忽略未注册的能力 ID「{token}」（不影响物件生成）");
                }
            }

            return accepted.Count > 0 ? accepted.ToArray() : Array.Empty<string>();
        }

        /// <summary>Touch → 全部；Rest → 仅 Ground；Play → Ground/Wall；Ignore → 全部。判定统一走 BehaviorCells，避免和装配表两套表。</summary>
        public static bool IsCombinationAllowed(ObjectBehavior behavior, ObjectPlacement placement) =>
            BehaviorCells.IsLegal(behavior, placement);

        /// <summary>严格枚举匹配：大小写必须一致，不接受数字或部分匹配。</summary>
        private static bool TryParseEnum<T>(string raw, out T value) where T : struct, Enum
        {
            value = default;
            if (string.IsNullOrEmpty(raw)) return false;

            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, raw, StringComparison.Ordinal))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }

            return false;
        }
    }
}
