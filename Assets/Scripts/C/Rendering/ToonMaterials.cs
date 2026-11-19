// ToonMaterials.cs —— Prototype C：把现有材质 / 运行时加载的 GLB 材质切到卡通化 Shader
// 用途有三个：
//   1. 运行时（glTFast 材质生成器）把 GLB 材质换成 toon；
//   2. 场景里既有的模型（例如搬进来的建筑）一键套用；
//   3. 前景 / 背景两个风格档的批量指定（_StyleBlend = 0 / 1）。
// 只搬 albedo 贴图、基础色、alpha clip；不搬 PBR 参数（toon 不用）。

using System;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>
    /// 黄/褐/土色的「中性化」纠偏参数。只作用于生成物的运行时材质：
    /// 不写回 .glb、不写缓存、不进 styleFingerprint，所以改这些不会让已有缓存失效。
    /// 做法是压低饱和度 + 适度提亮，让木色向浅木/米白/灰白收敛；
    /// 不改变色相，所以不会把木色染成绿，也不会碰红/粉/绿等区间外的颜色。
    /// </summary>
    [Serializable]
    public struct WarmCorrectionSettings
    {
        [Tooltip("开启后，落在暖色区间的像素只做「降饱和 + 提亮」；区间外的红/粉/绿/蓝完全不动")]
        public bool enabled;

        [Tooltip("暖色区间的色相下限（0~1）：0.0694 ≈ 25°，正好在红色（0~20°）之外")]
        public float hueMin;

        [Tooltip("暖色区间的色相上限（0~1）：0.1944 ≈ 70°，只到土黄/黄绿前，粉色/品红（300~360°）不在此区间")]
        public float hueMax;

        [Tooltip("降饱和强度（0 = 不压，1 = 完全压到目标饱和度）：默认 0.8，黄褐明显变浅但还看得出木纹")]
        [Range(0f, 1f)] public float desaturation;

        [Tooltip("目标饱和度上限（0~0.6）：越高越保留原有颜色。0.35 是浅木、0.22 偏米白、0.12 接近灰白")]
        [Range(0f, 0.6f)] public float maxSaturation;

        [Tooltip("提亮强度（0 = 不提亮，0.3 = 明显变浅）：只抬亮度，不改色相，保留原有明暗层次")]
        [Range(0f, 1f)] public float lighten;

        [Tooltip("不参与纠偏的物件 id（归一化 id，例如 plant）；留空表示全部参与")]
        public string[] excludeIds;

        public static WarmCorrectionSettings Default
        {
            get
            {
                return new WarmCorrectionSettings
                {
                    enabled = true,
                    hueMin = 25f / 360f,
                    hueMax = 70f / 360f,
                    desaturation = 0.80f,
                    maxSaturation = 0.22f,
                    lighten = 0.30f,
                    excludeIds = new string[0],
                };
            }
        }
    }

    /// <summary>卡通化风格档：前景对比/饱和更高，背景明显压低。</summary>
    public enum ToonLayer
    {
        Foreground = 0,
        Backdrop = 1,
    }

    public static class ToonMaterials
    {
        public const string ShaderName = "PrototypeC/Toon";

        // albedo 贴图候选属性名，按优先级排列：
        //   URP Lit / HDRP = _BaseMap / _BaseColorMap；glTFast 生成的 ShaderGraph 材质 = baseColorTexture；
        //   旧 Standard = _MainTex。
        private static readonly string[] AlbedoTextureProperties =
        {
            "_BaseMap", "baseColorTexture", "_BaseColorMap", "_BaseColorTexture", "_MainTex",
        };

        // albedo 常量色候选属性名（URP Lit = _BaseColor，glTFast ShaderGraph = baseColorFactor）
        private static readonly string[] AlbedoColorProperties =
        {
            "_BaseColor", "baseColorFactor", "_Color", "_MainColor", "_BaseColorFactor",
        };

        // 贴图缩放/偏移候选属性名
        private static readonly string[] AlbedoScaleTransformProperties =
        {
            "_BaseMap_ST", "baseColorTexture_ST", "_BaseColorMap_ST", "_MainTex_ST",
        };

        // 兜底扫描时排除的辅助贴图关键字，避免把法线/金属度贴图当 albedo
        private static readonly string[] NonAlbedoHints =
        {
            "normal", "bump", "occlusion", "metallic", "roughness", "smoothness",
            "emissive", "emission", "specular", "clearcoat", "transmission", "detail", "height",
        };

        private static Shader _shader;

        public static Shader ToonShader
        {
            get
            {
                if (_shader == null) _shader = Shader.Find(ShaderName);
                return _shader;
            }
        }

        public static bool IsToon(Material material)
        {
            return material != null && material.shader != null && material.shader.name == ShaderName;
        }

        /// <summary>把风格档写进材质；前景 = 0，背景 = 1。</summary>
        public static void SetLayer(Material material, ToonLayer layer)
        {
            if (material == null || !material.HasProperty("_StyleBlend")) return;
            material.SetFloat("_StyleBlend", layer == ToonLayer.Backdrop ? 1f : 0f);
        }

        /// <summary>AI 生成物的统一挂载父节点名；它之下的一切都算前景层。</summary>
        public const string GeneratedAnchorName = "PrototypeC_GeneratedAnchor";

        /// <summary>
        /// 按层级结构判定风格档，让层归属成为结构性规则而不是每次手选菜单：
        ///   1. 祖先链含 PrototypeC_GeneratedAnchor → 前景（AI 生成物都挂在这里）；
        ///   2. 祖先链属于某个模型 prefab 实例（.glb/.gltf/.fbx，例如上层建筑 Untitled.glb 那栋房子）→ 背景；
        ///   3. 其余（恐龙、场景内手工物件）→ 前景。
        /// </summary>
        public static ToonLayer ResolveLayer(GameObject target, ToonLayer fallback = ToonLayer.Foreground)
        {
            if (target == null) return fallback;

            Transform current = target.transform;
            while (current != null)
            {
                if (current.name == GeneratedAnchorName) return ToonLayer.Foreground;
                current = current.parent;
            }

            return IsModelPrefabInstance(target) ? ToonLayer.Backdrop : fallback;
        }

        // 上层建筑是「模型 prefab 实例」；场景里手工摆的物件、恐龙、预制体（.prefab）都不算。
        private static bool IsModelPrefabInstance(GameObject target)
        {
#if UNITY_EDITOR
            if (UnityEditor.PrefabUtility.GetOutermostPrefabInstanceRoot(target) == null) return false;

            string path = UnityEditor.PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            if (string.IsNullOrEmpty(path)) return false;

            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return extension == ".glb" || extension == ".gltf" || extension == ".fbx";
#else
            return false;
#endif
        }

        /// <summary>
        /// 从任意材质（URP/Lit、glTFast 生成材质等）造一个 toon 材质，保留 albedo 贴图与基础色。
        /// 已经是 toon 的材质只改风格档，不重复创建。
        /// </summary>
        public static Material Create(Material source, ToonLayer layer, string nameSuffix = "_Toon", bool logMissingAlbedo = false)
        {
            Shader shader = ToonShader;
            if (shader == null)
            {
                Debug.LogError($"[PrototypeC] 找不到 shader {ShaderName}，无法切换到卡通化材质");
                return source;
            }

            if (IsToon(source))
            {
                SetLayer(source, layer);
                return source;
            }

            var toon = new Material(shader)
            {
                name = (source != null ? source.name : "Toon") + nameSuffix,
            };

            if (source != null)
            {
                bool hasAlbedo = CopyAlbedoFrom(source, toon);

                if (source.HasProperty("_Cutoff") && toon.HasProperty("_Cutoff"))
                {
                    toon.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
                }

                if (source.IsKeywordEnabled("_ALPHATEST_ON")) toon.EnableKeyword("_ALPHATEST_ON");

                if (!hasAlbedo && logMissingAlbedo)
                {
                    Debug.LogWarning(
                        $"[PrototypeC] 材质「{source.name}」(shader: {(source.shader != null ? source.shader.name : "null")}) " +
                        "没有取到 albedo 贴图，新材质只能使用 _BaseColor 常量色。");
                }
            }

            SetLayer(toon, layer);
            return toon;
        }

        /// <summary>
        /// 把 source 的 albedo 贴图 / 常量色 / UV 缩放偏移搬到 toon 材质。
        /// 兼容 URP Lit(_BaseMap)、glTFast ShaderGraph(baseColorTexture)、旧 Standard(_MainTex)；
        /// 都不匹配时按「第一个非空的非辅助贴图」兜底。返回是否搬到了贴图。
        /// </summary>
        public static bool CopyAlbedoFrom(Material source, Material toon)
        {
            if (source == null || toon == null) return false;

            bool copiedTexture = false;

            Texture texture = GetAlbedoTexture(source);
            if (texture != null && toon.HasProperty("_BaseMap"))
            {
                toon.SetTexture("_BaseMap", texture);
                copiedTexture = true;
            }

            if (toon.HasProperty("_BaseColor") && TryGetBaseColor(source, out Color color))
            {
                toon.SetColor("_BaseColor", color);
            }

            if (toon.HasProperty("_BaseMap_ST") && TryGetScaleTransform(source, out Vector4 scaleTransform))
            {
                toon.SetVector("_BaseMap_ST", scaleTransform);
            }

            return copiedTexture;
        }

        /// <summary>按候选属性名取 albedo 贴图，取不到再扫描全部贴图属性兜底。</summary>
        public static Texture GetAlbedoTexture(Material source)
        {
            if (source == null) return null;

            for (int i = 0; i < AlbedoTextureProperties.Length; i++)
            {
                string property = AlbedoTextureProperties[i];
                if (!source.HasProperty(property)) continue;

                Texture texture = source.GetTexture(property);
                if (IsRealTexture(texture)) return texture;
            }

            // 兜底：材质可能用别的属性名（例如自定义 ShaderGraph），挑第一张非空且不像辅助贴图的
            string[] textureProperties = source.GetTexturePropertyNames();
            for (int i = 0; i < textureProperties.Length; i++)
            {
                if (IsAuxiliaryTextureProperty(textureProperties[i])) continue;

                Texture texture = source.GetTexture(textureProperties[i]);
                if (IsRealTexture(texture)) return texture;
            }

            return null;
        }

        /// <summary>读 albedo 常量色（_BaseColor / baseColorFactor / _Color）。</summary>
        public static bool TryGetBaseColor(Material source, out Color color)
        {
            color = Color.white;
            if (source == null) return false;

            for (int i = 0; i < AlbedoColorProperties.Length; i++)
            {
                string property = AlbedoColorProperties[i];
                if (!source.HasProperty(property)) continue;

                color = source.GetColor(property);
                return true;
            }

            return false;
        }

        /// <summary>读 albedo 贴图的 UV 缩放/偏移。</summary>
        public static bool TryGetScaleTransform(Material source, out Vector4 scaleTransform)
        {
            scaleTransform = new Vector4(1f, 1f, 0f, 0f);
            if (source == null) return false;

            for (int i = 0; i < AlbedoScaleTransformProperties.Length; i++)
            {
                string property = AlbedoScaleTransformProperties[i];
                if (!source.HasProperty(property)) continue;

                scaleTransform = source.GetVector(property);
                return true;
            }

            return false;
        }

        private static bool IsRealTexture(Texture texture)
        {
            if (texture == null) return false;

            // 未赋值的贴图槽会返回 shader 里声明的默认贴图（UnityWhite / UnityDefault2D 等）
            string textureName = texture.name;
            return !string.IsNullOrEmpty(textureName)
                   && !textureName.StartsWith("Unity")
                   && !textureName.StartsWith("unity_");
        }

        private static bool IsAuxiliaryTextureProperty(string property)
        {
            string lower = property.ToLowerInvariant();
            for (int i = 0; i < NonAlbedoHints.Length; i++)
            {
                if (lower.Contains(NonAlbedoHints[i])) return true;
            }

            return false;
        }

        /// <summary>
        /// 就地替换 shader（常用于 glTFast 刚生成、还没有别人引用的材质，避免多克隆一份材质）。
        /// 注意：glTFast 的 URP 材质是 ShaderGraph（贴图在 baseColorTexture），换 shader 后该属性会丢，
        /// 所以必须先取出 albedo 再写回 _BaseMap。
        /// </summary>
        public static Material SwitchInPlace(Material material, ToonLayer layer)
        {
            if (material == null) return null;

            Shader shader = ToonShader;
            if (shader == null || IsToon(material))
            {
                SetLayer(material, layer);
                return material;
            }

            Texture albedo = GetAlbedoTexture(material);
            bool hasColor = TryGetBaseColor(material, out Color baseColor);
            bool hasScaleTransform = TryGetScaleTransform(material, out Vector4 scaleTransform);
            bool alphaClip = material.IsKeywordEnabled("_ALPHATEST_ON");
            float cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;

            material.shader = shader;

            if (albedo != null && material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", albedo);
            if (hasColor && material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (hasScaleTransform && material.HasProperty("_BaseMap_ST")) material.SetVector("_BaseMap_ST", scaleTransform);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", cutoff);
            if (alphaClip) material.EnableKeyword("_ALPHATEST_ON");

            SetLayer(material, layer);
            return material;
        }

        /// <summary>批量套用到一个 Renderer（保留多材质槽位数量）。</summary>
        public static void Apply(Renderer renderer, ToonLayer layer)
        {
            if (renderer == null) return;

            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                materials[i] = Create(materials[i], layer);
            }

            renderer.sharedMaterials = materials;
        }

        /// <summary>批量套用到整棵层级（含未激活子节点）。</summary>
        public static void Apply(GameObject root, ToonLayer layer)
        {
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++) Apply(renderers[i], layer);
        }

        /// <summary>
        /// 前景生成物的确定性色相偏移（渲染期处理）。
        /// 只改刚实例化出来的运行时材质：_BaseColor 乘一个「近白 + 稳定色相」的 tint，
        /// 同一 id 每次加载得到同一个色调，不同 id 之间才有差异。
        /// 不写回 .glb、不进 styleFingerprint、不碰 Meshy Prompt → 不会让已有缓存失效。
        /// </summary>
        public static void ApplyStableTint(GameObject root, string id, float strength)
        {
            if (root == null || strength <= 0f) return;
            if (ResolveLayer(root) != ToonLayer.Foreground) return;

            float hue = StableHue(id);
            Color tint = Color.HSVToRGB(hue, Mathf.Clamp01(strength), 1f);

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                // 运行时材质是 glTFast 刚实例化的，用 sharedMaterials 才不会多克隆一份
                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null || !material.HasProperty("_BaseColor")) continue;

                    Color baseColor = material.GetColor("_BaseColor");
                    material.SetColor("_BaseColor", new Color(
                        baseColor.r * tint.r, baseColor.g * tint.g, baseColor.b * tint.b, baseColor.a));
                }
            }
        }

        // FNV-1a：同一字符串在任何进程/任何次加载都得到同一个值（string.GetHashCode 不保证跨进程稳定）
        private static float StableHue(string id)
        {
            unchecked
            {
                uint hash = 2166136261u;
                if (id != null)
                {
                    for (int i = 0; i < id.Length; i++) hash = (hash ^ id[i]) * 16777619u;
                }

                return (hash % 360u) / 360f;
            }
        }

        /// <summary>
        /// 黄/褐/土色的中性化纠偏：给生成物的运行时材质写入 Shader 的「降饱和 + 提亮」参数。
        /// 先按材质主色判断（常量色明显有色就用它；贴图可读就抽样取平均色），
        /// 判断不了时交给 Shader 逐像素判断（白色/灰色/冷色/红色/粉色像素仍然不动）。
        /// 参数只由设置和 id 决定，没有随机数：同一物件每次加载颜色一致。
        /// </summary>
        public static void ApplyWarmCorrection(GameObject root, string id, WarmCorrectionSettings settings)
        {
            if (root == null) return;
            if (ResolveLayer(root) != ToonLayer.Foreground) return;

            // 硬边界，和 Shader 里一致：只允许 25°~70°（黄/褐/土黄）。
            // 红色 0°~20°、粉色/品红 300°~360°、绿色 90°~160° 在任何参数组合下都不会被处理。
            const float warmHueFloor = 25f / 360f;
            const float warmHueCeil = 70f / 360f;

            float hueMin = Mathf.Clamp(Mathf.Min(settings.hueMin, settings.hueMax), warmHueFloor, warmHueCeil);
            float hueMax = Mathf.Clamp(Mathf.Max(settings.hueMin, settings.hueMax), warmHueFloor, warmHueCeil);
            if (hueMax <= hueMin)
            {
                hueMin = warmHueFloor;
                hueMax = warmHueCeil;
            }

            float neutralize = 0f;
            float lighten = 0f;
            float maxSaturation = 0f;

            if (settings.enabled && !IsExcluded(settings.excludeIds, id))
            {
                neutralize = Mathf.Clamp01(settings.desaturation);
                lighten = Mathf.Clamp01(settings.lighten);
                maxSaturation = Mathf.Clamp(settings.maxSaturation, 0f, 0.6f);
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material material = materials[m];
                    if (material == null || !material.HasProperty("_NeutralStrength")) continue;

                    // 材质本身是明确的冷色/红色就不用处理；判断不了时保持强度，让 Shader 只处理暖色像素
                    bool warm = LooksWarm(material, hueMin, hueMax);

                    material.SetFloat("_NeutralStrength", warm ? neutralize : 0f);
                    material.SetFloat("_NeutralLighten", warm ? lighten : 0f);
                    material.SetFloat("_NeutralMaxSat", warm ? maxSaturation : 0f);
                    material.SetFloat("_WarmHueMin", hueMin);
                    material.SetFloat("_WarmHueMax", hueMax);
                }
            }
        }

        /// <summary>材质主色是否落在暖色区间；贴图不可读且常量色接近白时返回 true（交给 Shader 逐像素判断）。</summary>
        public static bool LooksWarm(Material material, float hueMin, float hueMax)
        {
            if (material == null) return false;

            if (TryGetBaseColor(material, out Color baseColor))
            {
                Color.RGBToHSV(baseColor, out float baseHue, out float baseSaturation, out _);
                if (baseSaturation >= 0.15f) return IsWarmHue(baseHue, hueMin, hueMax);
            }

            if (TrySampleTextureHue(material, out float textureHue, out float textureSaturation))
            {
                if (textureSaturation >= 0.12f) return IsWarmHue(textureHue, hueMin, hueMax);
                return false;
            }

            return true;
        }

        private static bool IsWarmHue(float hue, float hueMin, float hueMax)
        {
            return hue >= hueMin && hue <= hueMax;
        }

        private static bool IsExcluded(string[] excludeIds, string id)
        {
            if (excludeIds == null || string.IsNullOrWhiteSpace(id)) return false;

            for (int i = 0; i < excludeIds.Length; i++)
            {
                string candidate = excludeIds[i];
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                if (string.Equals(candidate.Trim(), id, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>贴图可读时抽样取平均色（低配 16×16 网格）；不可读返回 false。</summary>
        private static bool TrySampleTextureHue(Material material, out float hue, out float saturation)
        {
            hue = 0f;
            saturation = 0f;

            if (!(GetAlbedoTexture(material) is Texture2D texture) || !texture.isReadable) return false;

            int stepX = Mathf.Max(1, texture.width / 16);
            int stepY = Mathf.Max(1, texture.height / 16);
            float r = 0f, g = 0f, b = 0f;
            int count = 0;

            for (int y = stepY / 2; y < texture.height; y += stepY)
            {
                for (int x = stepX / 2; x < texture.width; x += stepX)
                {
                    Color pixel = texture.GetPixel(x, y);
                    if (pixel.a < 0.5f) continue;

                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    count++;
                }
            }

            if (count == 0) return false;

            Color.RGBToHSV(new Color(r / count, g / count, b / count), out hue, out saturation, out _);
            return true;
        }
    }
}
