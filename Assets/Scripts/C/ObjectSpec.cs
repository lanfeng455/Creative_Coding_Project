// ObjectSpec.cs —— Prototype C：标准化物件定义
// 本阶段由测试 UI 填写；后续接入 LLM 时，LLM 直接产出同一结构。
// 缓存 Key 只依赖 NormalizedId + 生成参数指纹（含风格指纹），不同输入表达可以映射到同一种物件。

using System;
using System.Text;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>Meshy 模型生成参数；参数变化视为缓存失效。</summary>
    [Serializable]
    public class GenerationParams
    {
        /// <summary>缓存指纹结构版本：破坏性调整缓存语义时 +1，让旧缓存自然失效。</summary>
        public const int CacheSchemaVersion = 2;

        [Tooltip("Meshy AI 模型标识；留空表示使用服务端默认（latest）")]
        public string aiModel = "";

        [Tooltip("目标面数（should_remesh 开启时生效）")]
        public int targetPolycount = 30000;

        [Tooltip("是否由 Meshy 重网格化（remesh）")]
        public bool shouldRemesh = true;

        [Tooltip("是否在 preview 之后再提交 refine 贴图任务；关闭则只有素模，没有贴图/材质")]
        public bool enableTexture = true;

        [Tooltip("贴图分辨率（refine 阶段）：2k / 4k / 8k")]
        public string textureResolution = "2k";

        [Tooltip("风格层指纹；由 PromptStyle.Fingerprint() 计算，参与缓存判定")]
        public string styleFingerprint = "";

        [Tooltip("几何模型类型：standard / smart-topology；留空 = 服务端默认 standard。meshy-t2 / meshy-t2.5 必须配 smart-topology，否则会 400")]
        public string modelType = "";

        [Tooltip("refine 阶段的贴图模型（ai_model）：留空 = 沿用 preview 模型；meshy-n1 更快但只支持 2K 基础色、不支持 PBR")]
        public string textureModel = "";

        [Tooltip("refine 是否同时生成 PBR 贴图（金属/粗糙/法线）：关闭可缩短贴图阶段；meshy-n1 不支持，会返回 400")]
        public bool enablePbr = true;

        [Tooltip("refine 的参考图（texture_image_url）：公开 URL 或 data:image/...;base64。只有 meshy-n1 必须提供；与 texture_prompt 互斥")]
        public string textureImageUrl = "";

        /// <summary>用于判断缓存是否仍对应当前生成参数（含风格模板版本与内容）。</summary>
        public string ToCacheFingerprint()
        {
            // 保持与旧版本的默认指纹完全一致：新字段只在「非默认值」时才追加，
            // 这样升级代码后没开快速模式，已有缓存仍然命中。
            string fingerprint = string.Join("|",
                "v" + CacheSchemaVersion,
                aiModel,
                targetPolycount,
                shouldRemesh,
                enableTexture,
                textureResolution,
                styleFingerprint);

            if (!string.IsNullOrEmpty(modelType)) fingerprint += "|mt=" + modelType;
            if (!string.IsNullOrEmpty(textureModel)) fingerprint += "|tm=" + textureModel;
            if (!enablePbr) fingerprint += "|pbr=0";
            // 参考图可能是很长的 data URI：只放稳定哈希，避免 metadata.json 膨胀
            if (!string.IsNullOrEmpty(textureImageUrl)) fingerprint += "|ti=" + StableHash(textureImageUrl);

            return fingerprint;
        }

        private static uint StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        public GenerationParams Clone()
        {
            return (GenerationParams)MemberwiseClone();
        }
    }

    /// <summary>标准化物件定义：模型生成接口的最小输入结构。</summary>
    [Serializable]
    public class ObjectSpec
    {
        [Tooltip("物件显示名，同时是规范化标识的来源（例如 CHAIR → chair）")]
        public string objectName = "CHAIR";

        [Tooltip("填充者给出的物件描述，替换模板里的 [OBJECT DESCRIPTION]")]
        public string objectDescription = "a simple wooden chair";

        [Tooltip("填充者给出的尺寸参考，替换模板里的 [SIZE DESCRIPTION]")]
        public string sizeDescription = "Roughly 0.5 m wide, 0.5 m deep, 0.9 m tall.";

        [Tooltip("可配置的风格层（模板 + 版本 + 贴图风格）")]
        public PromptStyle style = new PromptStyle();

        [Tooltip("最终提交给 Meshy 的 Prompt；由 ComposePrompt() 写入")]
        public string prompt = "";

        [Tooltip("Prompt 是否因 Meshy 800 字符上限被裁剪")]
        public bool promptTrimmed;

        [Tooltip("Prompt 裁剪说明（可追溯）")]
        public string promptTrimNote = "";

        [Tooltip("行为标签（LLM 解析时为 Touch/Rest/Play/Ignore；本阶段只存储与展示）")]
        public string behavior = "Decoration";

        [Tooltip("LLM 解析出的放置类型（Ground/Wall/Rooftop）；本阶段只存储与展示，不参与布局")]
        public string placementKind = "";

        [Tooltip("LLM 解析出的尺寸档位（Small/Medium/Large/SuperLarge）；本阶段只存储与展示")]
        public string sizeClass = "";

        [Tooltip("LLM 分配的能力 ID（已按 ObjectCapabilityIds 规范化）；由 CapabilityAssembler 在落位后装配，不参与缓存指纹")]
        public string[] capabilities = new string[0];

        [Tooltip("初始放置位置（相对 spawnParent 的局部坐标）；y 视为落地高度")]
        public Vector3 placement = Vector3.zero;

        [Tooltip("朝向（绕模型自身 pivot 的 Y 轴，以 spawnParent 局部坐标为准）；默认 90，想反向就填 -90")]
        public float yawDegrees = 90f;

        [Tooltip("期望尺寸（米）；autoFitHeight 开启时按 height 缩放模型")]
        public Vector3 size = new Vector3(0.55f, 0.95f, 0.55f);

        public GenerationParams generation = new GenerationParams();

        /// <summary>规范化物件标识，缓存目录名与 Key 都由它派生。</summary>
        public string NormalizedId => NormalizeId(objectName);

        /// <summary>
        /// 按风格层拼装最终 Prompt，并同步风格指纹到生成参数（参与缓存判定）。
        /// 填充内容由 objectDescription / sizeDescription 提供，将来 LLM 只需填这两个字段。
        /// </summary>
        public string ComposePrompt()
        {
            if (style == null) style = new PromptStyle();
            if (generation == null) generation = new GenerationParams();

            PromptComposeReport report = PromptComposer.Compose(style, objectDescription, sizeDescription);
            prompt = report.prompt;
            promptTrimmed = report.trimmed;
            promptTrimNote = report.trimNote;
            generation.styleFingerprint = style.Fingerprint();

            if (report.trimmed)
            {
                UnityEngine.Debug.LogWarning($"[PrototypeC] {promptTrimNote}（{objectName}）");
            }

            return prompt;
        }

        /// <summary>
        /// 预留的同义词/别名归一化入口。后续接入 LLM 或词表时，在这里把
        /// "座椅/凳子/chair" 之类的不同表达映射为同一个标识。
        /// 返回空字符串表示回退到内置规则。
        /// </summary>
        public static Func<string, string> CustomNormalizer;

        /// <summary>把任意输入收敛成稳定的、可用于目录名的标识（小写 + 下划线）。</summary>
        public static string NormalizeId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unnamed";

            if (CustomNormalizer != null)
            {
                string custom = CustomNormalizer(raw);
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }

            string lower = raw.Trim().ToLowerInvariant();
            var sb = new StringBuilder(lower.Length <= 40 ? lower.Length : 40);
            bool lastWasSeparator = false;

            for (int i = 0; i < lower.Length && sb.Length < 40; i++)
            {
                char c = lower[i];
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(c);
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator && sb.Length > 0)
                {
                    sb.Append('_');
                    lastWasSeparator = true;
                }
            }

            string result = sb.ToString().Trim('_');
            return string.IsNullOrEmpty(result) ? "unnamed" : result;
        }
    }
}
