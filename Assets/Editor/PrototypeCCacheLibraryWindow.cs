// PrototypeCCacheLibraryWindow.cs —— 缓存库：查看已缓存模型、放进当前场景、维护缓存别名
// 菜单：Tools/Prototype C/缓存库（预览 / 放入场景 / 别名）
// 只读缓存目录、按需实例化到场景；默认不写 layout.json、不占运行时容量。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using PrototypeC;
using UnityEditor;
using UnityEngine;

namespace PrototypeC.EditorTools
{
    public class PrototypeCCacheLibraryWindow : EditorWindow
    {
        private class Entry
        {
            public string id;
            /// <summary>选中的档位名（8 位指纹哈希，或旧平铺 legacy）。</summary>
            public string tier;
            /// <summary>该 id 下的可用档位数量（&gt;1 表示同一物件存了多套参数）。</summary>
            public int tierCount;
            public bool isLegacy;
            public string modelPath;
            public string folder;
            public bool fromMirror;
            public ObjectMetadata meta;
            public long bytes;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<ObjectCache.CacheAlias> _aliases = new List<ObjectCache.CacheAlias>();

        private Vector2 _scroll;
        private string _filter = string.Empty;
        private bool _registerOccupancy;
        private string _aliasFrom = string.Empty;
        private string _aliasTo = string.Empty;
        private string _aliasNote = string.Empty;
        private bool _placing;
        private ObjectModelService _service;

        [MenuItem("Tools/Prototype C/缓存库（预览 / 放入场景 / 别名）")]
        public static void Open()
        {
            var window = GetWindow<PrototypeCCacheLibraryWindow>("Prototype C 缓存库");
            window.minSize = new Vector2(560f, 420f);
            window.Refresh();
        }

        private void OnEnable()
        {
            Refresh();
            ReloadAliases();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("缓存库", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "列出本机缓存（persistentDataPath/ObjectCache）与工程镜像（PrototypeC_Cache）。\n" +
                "「放入场景」= 普通 GameObject：不写 layout.json、不占运行时容量；模型与运行时同样套 toon 材质与尺寸档位。\n" +
                "缩略图未做（GLB 不在 AssetDatabase 里，需要运行时渲染）；先看尺寸/来源/贴图标记再放入。",
                MessageType.Info);

            DrawToolbar();
            DrawEntries();
            EditorGUILayout.Space(8f);
            DrawAliases();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("过滤", _filter);
                if (GUILayout.Button("刷新", GUILayout.Width(70f))) Refresh();
                if (GUILayout.Button("打开缓存目录", GUILayout.Width(110f)))
                {
                    if (Directory.Exists(ObjectCache.RootPath)) EditorUtility.RevealInFinder(ObjectCache.RootPath);
                    else EditorUtility.DisplayDialog("Prototype C", $"缓存目录不存在：\n{ObjectCache.RootPath}", "好");
                }
            }

            _registerOccupancy = EditorGUILayout.ToggleLeft(
                "放入时同时登记占位（会写 layout.json 并占运行时容量，默认关）", _registerOccupancy);

            if (_placing) EditorGUILayout.HelpBox("正在加载模型…", MessageType.None);
        }

        private void DrawEntries()
        {
            EditorGUILayout.LabelField($"条目：{_entries.Count}", EditorStyles.miniBoldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(180f));
            foreach (Entry entry in _entries)
            {
                if (!MatchesFilter(entry)) continue;
                DrawEntryRow(entry);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawEntryRow(Entry entry)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(entry.id, EditorStyles.boldLabel, GUILayout.Width(160f));

                    ObjectMetadata meta = entry.meta;
                    string sizeText = meta != null && meta.size != Vector3.zero
                        ? $"{meta.size.x:0.00}×{meta.size.y:0.00}×{meta.size.z:0.00} m"
                        : "尺寸未记录";
                    string classText = meta != null && !string.IsNullOrEmpty(meta.sizeClass) ? meta.sizeClass : "档位未记录";
                    string sourceText = meta != null && !string.IsNullOrEmpty(meta.cacheOrigin) ? meta.cacheOrigin : "unknown";

                    GUILayout.Label($"{classText}｜{sizeText}", GUILayout.Width(220f));
                    GUILayout.Label(entry.fromMirror ? "工程镜像" : "本机", GUILayout.Width(64f));
                    GUILayout.Label(sourceText, GUILayout.Width(120f));
                    GUILayout.Label(meta != null && meta.enableTexture ? "有贴图" : "无贴图", GUILayout.Width(56f));

                    GUI.enabled = !_placing;
                    if (GUILayout.Button("放入场景", GUILayout.Width(80f)))
                    {
                        Entry captured = entry;
                        PlaceEntryAsync(captured);
                    }
                    GUI.enabled = true;

                    if (GUILayout.Button("设为别名目标", GUILayout.Width(96f)))
                    {
                        _aliasTo = entry.id;
                        GUI.FocusControl(null);
                    }
                }

                EditorGUILayout.LabelField(
                    $"档 {(entry.isLegacy ? "旧平铺" : entry.tier)}" +
                    (entry.tierCount > 1 ? $"（同 id 共 {entry.tierCount} 档，此处取最新）" : string.Empty) +
                    $"｜{entry.modelPath}",
                    EditorStyles.miniLabel);
            }
        }

        private void DrawAliases()
        {
            EditorGUILayout.LabelField("缓存别名", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"文件：{ObjectCache.AliasPath}\n" +
                "格式：{\"aliases\":[{\"from\":\"plant\",\"to\":\"pothos\",\"note\":\"...\"}]}\n" +
                "只指路、不复制文件；目标必须已缓存且风格指纹一致，否则回退为按原 id 正常处理。",
                MessageType.None);

            for (int i = 0; i < _aliases.Count; i++)
            {
                ObjectCache.CacheAlias alias = _aliases[i];
                if (alias == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    alias.from = EditorGUILayout.TextField(alias.from, GUILayout.Width(140f));
                    GUILayout.Label("→", GUILayout.Width(18f));
                    alias.to = EditorGUILayout.TextField(alias.to, GUILayout.Width(140f));
                    alias.note = EditorGUILayout.TextField(alias.note);

                    if (GUILayout.Button("删除", GUILayout.Width(56f)))
                    {
                        _aliases.RemoveAt(i);
                        i--;
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _aliasFrom = EditorGUILayout.TextField(_aliasFrom, GUILayout.Width(140f));
                GUILayout.Label("→", GUILayout.Width(18f));
                _aliasTo = EditorGUILayout.TextField(_aliasTo, GUILayout.Width(140f));
                _aliasNote = EditorGUILayout.TextField(_aliasNote);

                if (GUILayout.Button("添加", GUILayout.Width(56f)))
                {
                    string from = (_aliasFrom ?? string.Empty).Trim();
                    string to = (_aliasTo ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                    {
                        EditorUtility.DisplayDialog("Prototype C", "别名两端都要填：from（要替换的 id）和 to（已缓存的 id）。", "好");
                    }
                    else if (FindEntry(to) == null)
                    {
                        EditorUtility.DisplayDialog("Prototype C",
                            $"目标 id「{to}」在缓存库里找不到。\n请先让它生成一次（或从工程镜像播种），再建别名。", "好");
                    }
                    else
                    {
                        _aliases.Add(new ObjectCache.CacheAlias { from = from, to = to, note = _aliasNote });
                        _aliasFrom = string.Empty;
                        _aliasNote = string.Empty;
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存别名表", GUILayout.Width(120f)))
                {
                    if (ObjectCache.SaveAliases(_aliases))
                    {
                        ObjectCache.LoadAliases(true);
                        Debug.Log($"[PrototypeC] 别名表已保存：{ObjectCache.AliasPath}（{_aliases.Count} 条）");
                    }
                }

                if (GUILayout.Button("重新读取", GUILayout.Width(90f)))
                {
                    ReloadAliases();
                }
            }

            if (_aliases.Count == 0)
            {
                EditorGUILayout.LabelField("（暂无别名）", EditorStyles.miniLabel);
            }
        }

        private void Refresh()
        {
            _entries.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(ObjectCache.RootPath, false, seen);
            Collect(ObjectCache.ProjectMirrorRoot, true, seen);

            _entries.Sort((a, b) => string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase));
            Repaint();
        }

        private void Collect(string root, bool fromMirror, HashSet<string> seen)
        {
            if (!Directory.Exists(root)) return;

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrototypeC] 读取缓存目录失败：{root}｜{ex.Message}");
                return;
            }

            foreach (string folder in folders)
            {
                string id = Path.GetFileName(folder);

                // 同一 id 现在可能有多个档位目录（快速/标准各一档）：按 id 去重，只展示最新一档，
                // 避免同一物件因为路径分层被当成两个条目。
                List<ObjectCache.CacheEntry> tiers = ObjectCache.ScanEntries(folder, id);
                if (tiers.Count == 0) continue;
                if (!fromMirror && !seen.Add(id)) continue;
                if (fromMirror && seen.Contains(id)) continue;

                ObjectCache.CacheEntry newest = ObjectCache.SelectNewest(tiers);
                if (newest == null) continue;

                _entries.Add(new Entry
                {
                    id = id,
                    tier = newest.tier,
                    tierCount = tiers.Count,
                    isLegacy = newest.isLegacy,
                    folder = newest.folder,
                    modelPath = newest.modelPath,
                    fromMirror = fromMirror,
                    meta = newest.metadata,
                    bytes = newest.bytes,
                });
            }
        }

        private void ReloadAliases()
        {
            _aliases.Clear();

            List<ObjectCache.CacheAlias> loaded = ObjectCache.LoadAliases(true);
            foreach (ObjectCache.CacheAlias alias in loaded)
            {
                if (alias != null) _aliases.Add(alias);
            }

            Repaint();
        }

        private bool MatchesFilter(Entry entry)
        {
            if (string.IsNullOrWhiteSpace(_filter)) return true;
            return entry.id != null
                   && entry.id.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Entry FindEntry(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (Entry entry in _entries)
            {
                if (string.Equals(entry.id, id, StringComparison.OrdinalIgnoreCase)) return entry;
            }

            return null;
        }

        private async void PlaceEntryAsync(Entry entry)
        {
            if (entry == null || _placing) return;

            if (Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Prototype C", "请先退出 Play 模式，再把缓存模型放进场景。", "好");
                return;
            }

            _service = FindFirstObjectByType<ObjectModelService>();
            if (_service == null)
            {
                EditorUtility.DisplayDialog("Prototype C",
                    "当前场景里没有 ObjectModelService，无法套用尺寸档位与 toon 材质。\n" +
                    "请打开 TemplateC_LanMay 再试。", "好");
                return;
            }

            _placing = true;
            Repaint();

            try
            {
                ObjectSpec spec = BuildSpec(entry);

                byte[] data = await Task.Run(() => File.ReadAllBytes(entry.modelPath));
                var gltf = new GltfImport(null, null,
                    ToonMaterialGenerator.Create(ToonLayer.Foreground));

                bool loaded = await gltf.Load(data, null, null, default);
                if (!loaded)
                {
                    EditorUtility.DisplayDialog("Prototype C", $"GLB 解析失败：{entry.modelPath}", "好");
                    return;
                }

                var root = new GameObject($"Placed_{entry.id}");
                Undo.RegisterCreatedObjectUndo(root, "放入缓存模型");

                if (_service.spawnParent != null) root.transform.SetParent(_service.spawnParent, false);

                var instantiator = new GameObjectInstantiator(gltf, root.transform);
                bool instantiated = await gltf.InstantiateMainSceneAsync(instantiator, default);
                if (!instantiated)
                {
                    DestroyImmediate(root);
                    EditorUtility.DisplayDialog("Prototype C", "GLB 场景实例化失败。", "好");
                    return;
                }

                // 与运行时一致：档位缩放 + 稳定色相 + 黄褐色纠偏
                _service.DressEditorInstance(root, spec);

                if (_registerOccupancy) RegisterOccupancy(root, spec, entry);

                Selection.activeGameObject = root;
                EditorGUIUtility.PingObject(root);
                Debug.Log($"[PrototypeC] 已把缓存模型放进场景：{entry.id}｜{entry.modelPath}" +
                          (_registerOccupancy ? "｜已登记占位" : "｜未登记占位（不占容量）"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Prototype C", $"放入失败：{ex.Message}", "好");
                Debug.LogException(ex);
            }
            finally
            {
                _placing = false;
                Repaint();
            }
        }

        private static ObjectSpec BuildSpec(Entry entry)
        {
            ObjectMetadata meta = entry.meta;
            var spec = new ObjectSpec
            {
                objectName = entry.id,
                behavior = meta != null && !string.IsNullOrEmpty(meta.behavior) ? meta.behavior : "Decoration",
                placementKind = meta != null ? meta.placementKind : string.Empty,
                sizeClass = meta != null ? meta.sizeClass : string.Empty,
                size = meta != null ? meta.size : Vector3.zero,
                yawDegrees = meta != null ? meta.yawDegrees : 90f,
                placement = meta != null ? meta.placement : Vector3.zero,
            };
            return spec;
        }

        private void RegisterOccupancy(GameObject root, ObjectSpec spec, Entry entry)
        {
            RoomOccupancy occupancy = _service.occupancy;
            if (occupancy == null)
            {
                Debug.LogWarning("[PrototypeC] 场景里没有 RoomOccupancy，跳过占位登记");
                return;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            Vector3 localCenter = occupancy.anchor != null
                ? occupancy.anchor.InverseTransformPoint(bounds.center)
                : bounds.center;

            RoomBand band = RoomBands.FromPlacementKind(spec.placementKind);
            occupancy.Register(root, spec.NormalizedId, spec.objectName, band, localCenter, bounds.size,
                spec.yawDegrees, false, null, spec.behavior, spec.sizeClass);
            Debug.Log($"[PrototypeC] 已登记占位：{entry.id}（写 layout.json；会占运行时容量）");
        }
    }
}
