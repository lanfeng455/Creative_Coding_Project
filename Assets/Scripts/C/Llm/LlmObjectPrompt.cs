// LlmObjectPrompt.cs —— Prototype C：物件解析用的 System Prompt（DeepSeek / OpenAI 兼容）
// 只产出两段英文描述 + 三个枚举；不产出 yawDegrees / generation / style 等 Unity 字段。
// 两段描述合计 ≤ 202 字符（对应 800 字符 Prompt 上限的预算），写死在约束里。

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace PrototypeC
{
    public static class LlmObjectPrompt
    {
        /// <summary>房间尺寸占位符：真实值由 RoomLayout 提供，避免提示词和场景配置各写一套。</summary>
        public const string RoomWidthPlaceholder = "{ROOM_WIDTH}";
        public const string RoomHeightPlaceholder = "{ROOM_HEIGHT}";

        /// <summary>没有 RoomLayout 时的兜底房间尺寸（米）。</summary>
        public static readonly Vector3 FallbackRoomSize = new Vector3(9.6f, 5f, 4f);

        /// <summary>能力目录占位符：真实清单由 ObjectCapabilityIds.DescribeCatalog() 提供，单一事实来源。</summary>
        public const string CapabilityCatalogPlaceholder = "{CAPABILITY_CATALOG}";

        public const string SystemPromptTemplate =
@"You are an object parser for a 3D asset pipeline in a game prototype. The setting is a small dinosaur's cozy, unfinished apartment.

The user sends one English word or short phrase typed by a player. You convert it into ONE standardized object definition as strict JSON.

IDENTIFICATION RULES
1. Identify the concrete physical 3D object the input refers to. Furniture, household items, decorations, plants and compound objects are allowed.
2. A compound object is a single object: ""flower pot"", ""bookshelf"", ""bunk bed"" each count as one complete object.
3. Plants are allowed. Animals and any other self-directed living beings are NOT allowed.
4. Abstract concepts, emotions, actions, sounds, or anything that cannot be a single physical 3D object are NOT allowed.
5. For ambiguous words, choose the interpretation that makes the most sense inside a small dinosaur's apartment.
6. Normalize synonyms, near-synonyms and plural/singular forms to the SAME canonical objectName, so identical meanings reuse the same cache (e.g. ""chair"" and ""chairs"" both -> ""chair"").

OUTPUT FORMAT
Return ONLY one JSON object. No markdown, no code fences, no explanation.

If the input maps to a valid object, return exactly these six keys and nothing else:
{""objectName"":""..."",""objectDescription"":""..."",""sizeDescription"":""..."",""behavior"":""Touch|Rest|Play|Ignore"",""placement"":""Ground|Wall|Rooftop"",""size"":""Small|Medium|Large|SuperLarge""}
You may optionally add ONE extra key ""capabilities"" (see CAPABILITY RULES below). No other extra keys.

FIELD RULES
- objectName: short lowercase canonical English noun or compound noun, singular, no articles, no adjectives. Must be identical for semantically identical inputs.
- objectDescription: English, describes appearance and structure of the object. 60-120 characters.
- sizeDescription: English, describes real-world size in words, e.g. ""about 1.2 m wide, 0.6 m deep, 0.8 m tall"". 30-70 characters.
- objectDescription and sizeDescription together MUST be at most 202 characters. Never exceed this.
- All six keys are required. No nested values. All values are strings.
- Enum values must match exactly, including capitalization.

CAPABILITY RULES
- capabilities (optional): a single string of comma-separated capability IDs, chosen ONLY from this registered catalog: {CAPABILITY_CATALOG}.
- OUTPUT capabilities make this object affect others; INPUT capabilities make this object react to others. An object may have several at once.
- Pick only IDs that physically make sense for the object (e.g. a fan emits airflow; a floor lamp emits light; a wind chime receives force and emits sound; a plant responds to airflow and light; a radio emits sound).
- Never invent IDs, component names, script names or code. Unknown IDs are ignored.
- Omit the key entirely (or leave it empty) for objects with no special capabilities.

ALLOWED BEHAVIOR / PLACEMENT COMBINATIONS
- Touch: Ground, Wall, Rooftop
- Rest: Ground only
- Play: Ground, Wall
- Ignore: Ground, Wall, Rooftop

SIZE CLASS RUBRIC
The room is about {ROOM_WIDTH} m long and {ROOM_HEIGHT} m high. The size class sets the object's LONGEST own edge (not necessarily height), so pick the class by how big the real object is.
- Small: about 0.45 m. Handheld or tabletop props only: cup, mug, bowl, vase, book, candle, small toy, phone.
- Medium: about 0.90 m. Single-seat furniture and mid-size props: chair, stool, side table, small crate, floor lamp, potted plant.
- Large: about 1.60 m. Multi-person furniture and tall storage: sofa, dining table, bed, bookshelf, cabinet.
- SuperLarge: about 2.40 m. Room-scale pieces: double bed, large wardrobe, tall shelf unit, room divider.
Furniture is NEVER Small. A chair, table, sofa, bed or shelf must be Medium or larger.
Floor-standing appliances with a base or pole (fan, floor lamp, heater, coat rack) are NEVER Small; pole-mounted standing fans and tall heaters are Large.
Hanging ornaments (wind chime, mobile, hanging lamp, hanging plant) are NEVER Small; their longest dimension is about 0.8-1.0 m.
If two classes both seem plausible, choose the larger one.
sizeDescription must agree with the chosen class (its longest dimension should be close to that class value).

If the input cannot become a suitable concrete object (abstract concept, animal, or nonsense), do NOT invent an object. Return exactly:
{""unrecognized"":true,""reason"":""<short English reason>""}
In that rejection form only, ""unrecognized"" is the JSON boolean true (not a string).";

        public static string BuildUserPrompt(string input) =>
            "Parse this player input and return the json object:\n" + (input ?? string.Empty).Trim();

        /// <summary>
        /// 带字典参考的解析 Prompt：字典里相似物件的已认定条目一并列出，
        /// 让 LLM 对同类物件保持一致的枚举选择、尺寸档位与描述风格。
        /// </summary>
        public static string BuildUserPrompt(string input, IReadOnlyList<ObjectDefinition> dictionaryExamples)
        {
            string prompt = BuildUserPrompt(input);
            if (dictionaryExamples == null || dictionaryExamples.Count == 0) return prompt;
            return prompt + "\n\n" + BuildDictionarySection(dictionaryExamples);
        }

        /// <summary>
        /// 校验失败后的重试用 Prompt：把拒绝原因原样带回去，并重申合法组合，
        /// 避免模型第二次仍然返回同一个非法组合。
        /// </summary>
        public static string BuildRetryPrompt(string input, string rejection) =>
            "Parse this player input and return the json object:\n" + (input ?? string.Empty).Trim() +
            "\n\nYour previous answer was rejected: " + (rejection ?? "unknown reason") +
            "\nReturn only the six-key JSON object. Allowed combinations: " +
            "Touch with Ground/Wall/Rooftop; Rest with Ground; Play with Ground/Wall; Ignore with Ground/Wall/Rooftop.";

        public static string BuildRetryPrompt(string input, string rejection, IReadOnlyList<ObjectDefinition> dictionaryExamples)
        {
            string prompt = BuildRetryPrompt(input, rejection);
            if (dictionaryExamples == null || dictionaryExamples.Count == 0) return prompt;
            return prompt + "\n\n" + BuildDictionarySection(dictionaryExamples);
        }

        /// <summary>字典参考段：只描述「同类物件怎么判」，不改变输出格式与 Schema。</summary>
        private static string BuildDictionarySection(IReadOnlyList<ObjectDefinition> examples)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("Known objects from the project dictionary (already approved definitions). ");
            sb.AppendLine("If the input is similar to any of them, stay consistent with their enum choices, size class and description style:");
            for (int i = 0; i < examples.Count; i++)
            {
                ObjectDefinition e = examples[i];
                sb.Append(e.objectName).Append(" -> ")
                  .Append("{\"objectName\":\"").Append(e.objectName).Append('"')
                  .Append(",\"objectDescription\":\"").Append(e.objectDescription).Append('"')
                  .Append(",\"sizeDescription\":\"").Append(e.sizeDescription).Append('"')
                  .Append(",\"behavior\":\"").Append(e.behavior).Append('"')
                  .Append(",\"placement\":\"").Append(e.placement).Append('"')
                  .Append(",\"size\":\"").Append(e.size).Append('"');
                if (e.capabilities != null && e.capabilities.Length > 0)
                    sb.Append(",\"capabilities\":\"").Append(string.Join(",", e.capabilities)).Append('"');
                sb.Append("}\n");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>兼容旧调用：没有房间上下文时用兜底尺寸，避免调用点全部改名。</summary>
        public static string SystemPrompt => BuildSystemPrompt(FallbackRoomSize);

        /// <summary>按房间实际尺寸生成 System Prompt；layout 为空时用兜底尺寸。</summary>
        public static string BuildSystemPrompt(RoomLayout layout)
        {
            Vector3 size = layout != null ? layout.DescribedRoomSize : FallbackRoomSize;
            return BuildSystemPrompt(size);
        }

        public static string BuildSystemPrompt(Vector3 roomSize)
        {
            return SystemPromptTemplate
                .Replace(RoomWidthPlaceholder, roomSize.x.ToString("0.##", CultureInfo.InvariantCulture))
                .Replace(RoomHeightPlaceholder, roomSize.y.ToString("0.##", CultureInfo.InvariantCulture))
                .Replace(CapabilityCatalogPlaceholder, ObjectCapabilityIds.DescribeCatalog());
        }
    }
}
