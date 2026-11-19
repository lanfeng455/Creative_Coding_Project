// PrototypeCRenderingTool.cs —— Prototype C：渲染相关的一键菜单
// 1) 给打字机区域加不透明遮挡底板（问题1），幂等，不重建场景。
// 2) 把场景里既有的模型（房子等）套用卡通化 shader；档位按层级结构自动判定
//    （模型 prefab 实例 = 背景档，GeneratedAnchor 之下 / 恐龙 / 其它 = 前景档），也可手动强制。
// 只操作选中对象或目标根节点下的 MeshRenderer；不碰 SpriteRenderer / TMP 文本 / 场景层级结构。

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PrototypeC.EditorTools
{
    public static class PrototypeCRenderingTool
    {
        private const string MaterialFolder = "Assets/PrototypeC/Materials";
        private const string BackdropName = "PrototypeC_TypewriterBackdrop";

        [MenuItem("Tools/Prototype C/Rendering/Add Typewriter Backdrop To Open Scene", false, 20)]
        public static void AddTypewriterBackdrop()
        {
            GameObject existing = GameObject.Find(BackdropName);
            if (existing == null)
            {
                existing = new GameObject(BackdropName);
                Undo.RegisterCreatedObjectUndo(existing, "Add Typewriter Backdrop");
                existing.AddComponent<MeshFilter>();
                existing.AddComponent<MeshRenderer>();
                existing.AddComponent<TypewriterBackdrop>();
            }

            var backdrop = existing.GetComponent<TypewriterBackdrop>();
            if (backdrop == null) backdrop = Undo.AddComponent<TypewriterBackdrop>(existing);

            var renderer = existing.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial == null)
            {
                renderer.sharedMaterial = GetOrCreateBackdropMaterial();
            }

            Selection.activeGameObject = existing;
            EditorGUIUtility.PingObject(existing);
            Debug.Log($"[PrototypeC] 打字机遮挡底板已就绪：{BackdropName}（z={backdrop.z}）。若仍有构件露出，把 Z 调小。");
        }

        [MenuItem("Tools/Prototype C/Rendering/Apply Toon To Selection (Foreground)", false, 21)]
        public static void ApplyToonForegroundToSelection() => ApplyToon(Selection.gameObjects, ToonLayer.Foreground);

        [MenuItem("Tools/Prototype C/Rendering/Apply Toon To Selection (Backdrop)", false, 22)]
        public static void ApplyToonBackdropToSelection() => ApplyToon(Selection.gameObjects, ToonLayer.Backdrop);

        // 层归属由 ToonMaterials.ResolveLayer 按层级结构判定：模型 prefab（房子）→ 背景，
        // GeneratedAnchor 之下 / 恐龙 / 其它 → 前景。不需要再手选前景还是背景。
        [MenuItem("Tools/Prototype C/Rendering/Apply Toon To Scene Mesh Renderers (Auto Layer)", false, 23)]
        public static void ApplyToonAutoToScene()
        {
            var roots = new List<GameObject>();
            foreach (GameObject go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                roots.Add(go);
            }

            ApplyToon(roots.ToArray(), null);
        }

        // 修复已经生成、但 albedo 为空的 toon 材质（旧版工具只认 _BaseMap，漏了 glTFast 的 baseColorTexture）。
        // 不删材质、不重装模型：按「预制体原始渲染器 -> 同名源材质」找回原材质，把 albedo 贴图补进现有 *.mat。
        [MenuItem("Tools/Prototype C/Rendering/Repair Toon Materials In Scene (Fix Missing Albedo)", false, 24)]
        public static void RepairToonMaterialsInScene()
        {
            int checkedSlots = 0;
            int repairedSlots = 0;
            var repairedMaterials = new HashSet<Material>();
            var unresolved = new HashSet<string>();
            Dictionary<string, Material> sourceByName = null;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null || materials.Length == 0) continue;

                    var sourceRenderer = PrefabUtility.GetCorrespondingObjectFromSource(renderer) as Renderer;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        Material toon = materials[i];
                        if (!ToonMaterials.IsToon(toon)) continue;

                        checkedSlots++;

                        // 档位跟着层级走：房子在模型 prefab 之下 → 背景，永远不会被猜成前景。
                        ToonMaterials.SetLayer(toon, ToonMaterials.ResolveLayer(renderer.gameObject));
                        EditorUtility.SetDirty(toon);

                        if (toon.HasProperty("_BaseMap") && toon.GetTexture("_BaseMap") != null) continue;

                        Material source = null;
                        if (sourceRenderer != null && sourceRenderer.sharedMaterials != null
                            && i < sourceRenderer.sharedMaterials.Length)
                        {
                            source = sourceRenderer.sharedMaterials[i];
                        }

                        if (source == null || ToonMaterials.IsToon(source))
                        {
                            sourceByName ??= BuildSourceMaterialMap();
                            sourceByName.TryGetValue(StripToonSuffix(toon.name), out source);
                        }

                        if (source == null || ToonMaterials.IsToon(source))
                        {
                            unresolved.Add(toon.name);
                            continue;
                        }

                        if (!ToonMaterials.CopyAlbedoFrom(source, toon))
                        {
                            unresolved.Add(toon.name);
                            continue;
                        }

                        EditorUtility.SetDirty(toon);
                        repairedMaterials.Add(toon);
                        repairedSlots++;
                    }
                }
            }

            AssetDatabase.SaveAssets();

            if (repairedSlots > 0)
            {
                Debug.Log($"[PrototypeC] 已为 {repairedMaterials.Count} 个 toon 材质补回 albedo 贴图（{repairedSlots} 个材质槽）。");
            }

            if (checkedSlots == 0)
            {
                Debug.LogWarning("[PrototypeC] 场景里没有找到 toon 材质，先执行 Apply Toon To Selection 再修复。");
            }

            if (unresolved.Count > 0)
            {
                Debug.LogWarning(
                    "[PrototypeC] 以下材质仍未取到 albedo（原模型本身可能就没有贴图）：" +
                    string.Join(", ", unresolved));
            }
        }

        // 从工程内的模型 / 预制体 / 材质资产里建「材质名 -> 材质」索引，用于按名字回查原始材质。
        private static Dictionary<string, Material> BuildSourceMaterialMap()
        {
            var map = new Dictionary<string, Material>();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/")) continue;

                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".glb" && extension != ".gltf" && extension != ".fbx"
                    && extension != ".prefab" && extension != ".mat")
                {
                    continue;
                }

                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Material material && !map.ContainsKey(material.name))
                    {
                        map[material.name] = material;
                    }
                }
            }

            return map;
        }

        // 去掉 toon 材质名的后缀，还原源材质名（Material_0_Toon_Backdrop -> Material_0）。
        private static string StripToonSuffix(string materialName)
        {
            const string backdropSuffix = "_Toon_Backdrop";
            const string foregroundSuffix = "_Toon";

            if (string.IsNullOrEmpty(materialName)) return materialName;

            if (materialName.EndsWith(backdropSuffix))
            {
                return materialName.Substring(0, materialName.Length - backdropSuffix.Length);
            }

            if (materialName.EndsWith(foregroundSuffix))
            {
                return materialName.Substring(0, materialName.Length - foregroundSuffix.Length);
            }

            return materialName;
        }

        private static void ApplyToon(GameObject[] roots, ToonLayer? forcedLayer)
        {
            if (roots == null || roots.Length == 0)
            {
                Debug.LogWarning("[PrototypeC] 没有选中对象，跳过套用卡通化材质");
                return;
            }

            int renderers = 0;
            int switched = 0;
            int foreground = 0;
            int backdrop = 0;

            foreach (GameObject root in roots)
            {
                if (root == null) continue;

                foreach (MeshRenderer meshRenderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderers++;
                    ToonLayer layer = forcedLayer ?? ToonMaterials.ResolveLayer(meshRenderer.gameObject);
                    if (layer == ToonLayer.Backdrop) backdrop++;
                    else foreground++;

                    Material[] materials = meshRenderer.sharedMaterials;
                    bool changed = false;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null) continue;

                        Material toon = GetOrCreateToonMaterial(materials[i], layer);
                        if (toon == null || toon == materials[i])
                        {
                            ToonMaterials.SetLayer(materials[i], layer);
                            continue;
                        }

                        materials[i] = toon;
                        changed = true;
                        switched++;
                    }

                    if (changed)
                    {
                        Undo.RecordObject(meshRenderer, "Apply Toon Materials");
                        meshRenderer.sharedMaterials = materials;
                        EditorUtility.SetDirty(meshRenderer);
                    }
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[PrototypeC] 卡通化材质已套用：{renderers} 个 MeshRenderer（前景 {foreground} / 背景 {backdrop}），替换 {switched} 个材质槽" +
                      $"（{(forcedLayer.HasValue ? (forcedLayer.Value == ToonLayer.Backdrop ? "强制背景档" : "强制前景档") : "按层级自动分档")}）");
        }

        private static Material GetOrCreateToonMaterial(Material source, ToonLayer layer)
        {
            if (source == null) return null;
            if (ToonMaterials.IsToon(source))
            {
                ToonMaterials.SetLayer(source, layer);
                EditorUtility.SetDirty(source);
                return source;
            }

            EnsureFolder(MaterialFolder);

            string baseName = Sanitize(source.name);
            string suffix = layer == ToonLayer.Backdrop ? "_Toon_Backdrop" : "_Toon";
            string path = $"{MaterialFolder}/{baseName}{suffix}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                // 复用已有材质时也要重新搬一次 albedo：旧版本工具只认 _BaseMap，glTFast 材质的
                // baseColorTexture 没被搬过来，导致复用出来的材质是没有贴图的白模。
                ToonMaterials.SetLayer(existing, layer);
                ToonMaterials.CopyAlbedoFrom(source, existing);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            Material toon = ToonMaterials.Create(source, layer, string.Empty, true);
            if (toon == null) return null;

            toon.name = baseName + suffix;
            AssetDatabase.CreateAsset(toon, path);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static Material GetOrCreateBackdropMaterial()
        {
            EnsureFolder(MaterialFolder);
            string path = $"{MaterialFolder}/TypewriterBackdrop.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogError("[PrototypeC] 找不到 URP/Unlit shader，遮挡底板材质创建失败");
                return null;
            }

            var material = new Material(shader) { name = "TypewriterBackdrop" };
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.043f, 0.063f, 0.106f, 1f));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Material";

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }

            return builder.ToString();
        }
    }
}
