// ObjectPropertyDictionary.cs —— Prototype C：物件特性判定字典（可编辑的缓存字典）
// 作用：
//   1) 已判定过的物件直接命中字典，不再重复请求 LLM；
//   2) 用户可直接编辑 JSON 文件纠正 LLM 对某个物件的判定，纠错永远优先于 LLM 输出；
//   3) 新物件解析时，字典中相似条目会作为参考示例注入 Prompt，让 LLM 的口径与已有判定保持一致。
// 文件位置：工程根 llm_object_dictionary.json（便于用户编辑）优先；缺省回退 persistentDataPath 同名文件。
// 条目字段与 LLM Schema 完全一致（objectName / objectDescription / sizeDescription / behavior / placement / size），
// 加载时逐条走 ObjectDefinitionValidator 严格校验，非法条目跳过并在日志中指出原因，但保存时原样保留，不会删掉用户手写的内容。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PrototypeC
{
    [Serializable]
    public class ObjectDictionaryEntry
    {
        public string objectName;
        public string objectDescription;
        public string sizeDescription;
        public string behavior;
        public string placement;
        public string size;

        /// <summary>可选：能力 ID（逗号分隔字符串，与 LLM Schema 的 capabilities 同格式）；空 = 无能力。</summary>
        public string capabilities = string.Empty;
    }

    [Serializable]
    public class ObjectDictionaryFile
    {
        public List<ObjectDictionaryEntry> entries = new List<ObjectDictionaryEntry>();
    }

    public static class ObjectPropertyDictionary
    {
        public const string FileName = "llm_object_dictionary.json";

        /// <summary>给 Prompt 注入参考条目的默认上限：条目太多会挤占 800 字符外的会话预算（System Prompt 不动，示例走 User Prompt）。</summary>
        public const int DefaultSimilarCount = 4;

        public static string ProjectRootPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));

        public static string PersistentPath =>
            Path.Combine(Application.persistentDataPath, FileName);

        private static readonly Dictionary<string, ObjectDefinition> _valid =
            new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase);

        // 文件里的原始条目（含未通过校验的），保存时原样写回，避免把用户手写但暂不合法的内容悄悄删掉
        private static readonly List<ObjectDictionaryEntry> _rawEntries = new List<ObjectDictionaryEntry>();

        private static string _activePath;
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static bool _loaded;
        private static bool _dirtyLoggedEmptyHint;

        public static string ActivePath
        {
            get { EnsureLoaded(); return _activePath; }
        }

        /// <summary>当前可用（已通过校验）的条目数。</summary>
        public static int Count
        {
            get { EnsureLoaded(); return _valid.Count; }
        }

        /// <summary>
        /// 按物件名（或玩家原始输入）查字典；key 会先走 ObjectSpec.NormalizeId 归一化，
        /// 单复数 / 大小写 / 分隔符差异不影响命中。命中返回 true 并给出可直接使用的定义。
        /// </summary>
        public static bool TryGet(string objectNameOrInput, out ObjectDefinition definition)
        {
            definition = null;
            EnsureLoaded();

            if (string.IsNullOrWhiteSpace(objectNameOrInput)) return false;
            return _valid.TryGetValue(ObjectSpec.NormalizeId(objectNameOrInput), out definition);
        }

        /// <summary>
        /// 给某个输入挑字典里「最像」的条目，作为 LLM 的参考示例（不含精确命中，那走 TryGet）。
        /// 打分是轻量启发式：子串包含 > 词元重合 > 相同尾词 > 共同前缀。
        /// </summary>
        public static List<ObjectDefinition> FindSimilar(string input, int maxCount = DefaultSimilarCount)
        {
            EnsureLoaded();

            var result = new List<ObjectDefinition>();
            if (_valid.Count == 0 || string.IsNullOrWhiteSpace(input) || maxCount <= 0) return result;

            string inKey = ObjectSpec.NormalizeId(input);
            string inFlat = inKey.Replace("_", string.Empty);
            var inTokens = new HashSet<string>(inKey.Split('_'), StringComparer.Ordinal);

            var scored = new List<(int score, string name, ObjectDefinition def)>();
            foreach (var pair in _valid)
            {
                string key = pair.Key;
                if (string.Equals(key, inKey, StringComparison.OrdinalIgnoreCase)) continue;

                string keyFlat = key.Replace("_", string.Empty);
                int score = 0;

                if (keyFlat.Contains(inFlat) || inFlat.Contains(keyFlat)) score += 5;

                int shared = 0;
                foreach (string token in key.Split('_'))
                {
                    if (inTokens.Contains(token)) shared++;
                }
                if (shared > 0) score += 3 * shared;

                // 相同尾词（中心名词相同）：potted_plant vs tall_plant
                string[] keyParts = key.Split('_');
                string[] inParts = inKey.Split('_');
                if (keyParts[keyParts.Length - 1] == inParts[inParts.Length - 1]) score += 2;

                int prefix = 0;
                while (prefix < keyFlat.Length && prefix < inFlat.Length && keyFlat[prefix] == inFlat[prefix]) prefix++;
                if (prefix >= 3) score += 1;

                if (score > 0) scored.Add((score, pair.Value.objectName, pair.Value));
            }

            scored.Sort((a, b) =>
            {
                int byScore = b.score.CompareTo(a.score);
                return byScore != 0 ? byScore : string.Compare(a.name, b.name, StringComparison.Ordinal);
            });

            for (int i = 0; i < scored.Count && i < maxCount; i++) result.Add(scored[i].def);
            return result;
        }

        /// <summary>
        /// 新判定写入字典（同 objectName 覆盖旧条目）并立即落盘。
        /// 只应由「LLM 判定成功且字典没有该物件」的路径调用；用户手工纠错通过直接编辑文件实现。
        /// </summary>
        public static void AddOrReplace(ObjectDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.objectName)) return;
            EnsureLoaded();

            string key = ObjectSpec.NormalizeId(definition.objectName);
            var entry = new ObjectDictionaryEntry
            {
                objectName = definition.objectName,
                objectDescription = definition.objectDescription,
                sizeDescription = definition.sizeDescription,
                behavior = definition.behavior.ToString(),
                placement = definition.placement.ToString(),
                size = definition.size.ToString(),
                capabilities = definition.capabilities != null && definition.capabilities.Length > 0
                    ? string.Join(",", definition.capabilities)
                    : string.Empty,
            };

            bool replaced = false;
            for (int i = 0; i < _rawEntries.Count; i++)
            {
                if (string.Equals(ObjectSpec.NormalizeId(_rawEntries[i].objectName ?? string.Empty), key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _rawEntries[i] = entry;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) _rawEntries.Add(entry);

            _valid[key] = definition;
            Save();
            Debug.Log($"[PrototypeC] 物件字典：{(replaced ? "更新" : "新增")} {definition.objectName}（共 {_valid.Count} 条，已写入 {_activePath}）");
        }

        // —— 加载 / 保存 ——

        private static void EnsureLoaded()
        {
            if (_loaded && !FileChangedOnDisk()) return;
            Load();
        }

        private static bool FileChangedOnDisk()
        {
            if (_activePath == null) return false;
            try
            {
                return File.GetLastWriteTimeUtc(_activePath) != _lastWriteUtc;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Load()
        {
            _loaded = true;
            _valid.Clear();
            _rawEntries.Clear();

            string path = File.Exists(ProjectRootPath) ? ProjectRootPath : PersistentPath;
            if (!File.Exists(path))
            {
                _activePath = ProjectRootPath; // 文件还没有：首次写入落在工程根，方便用户找到并编辑
                _lastWriteUtc = DateTime.MinValue;
                if (!_dirtyLoggedEmptyHint)
                {
                    _dirtyLoggedEmptyHint = true;
                    Debug.Log($"[PrototypeC] 物件字典：暂无文件，首次 LLM 判定后将创建 {_activePath}（该文件可直接编辑，用于纠正判定）");
                }
                return;
            }

            _activePath = path;
            _lastWriteUtc = File.GetLastWriteTimeUtc(path);

            try
            {
                ObjectDictionaryFile file = JsonUtility.FromJson<ObjectDictionaryFile>(File.ReadAllText(path));
                if (file == null || file.entries == null) return;

                foreach (ObjectDictionaryEntry entry in file.entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.objectName)) continue;
                    _rawEntries.Add(entry);

                    ObjectParseResult check = ObjectDefinitionValidator.Validate(ComposeFlatJson(entry));
                    if (check.status == ObjectParseStatus.Ok)
                    {
                        _valid[ObjectSpec.NormalizeId(check.definition.objectName)] = check.definition;
                    }
                    else
                    {
                        Debug.LogWarning($"[PrototypeC] 物件字典：条目 {entry.objectName} 未通过校验，已跳过（{check.error}）。" +
                                         $"可直接编辑 {_activePath} 修正后自动生效。");
                    }
                }

                Debug.Log($"[PrototypeC] 物件字典：已加载 {_valid.Count} 条（来源：{_activePath}" +
                          (_rawEntries.Count > _valid.Count ? $"，另有 {_rawEntries.Count - _valid.Count} 条未通过校验被跳过）" : "）"));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 物件字典：读取失败（{path}，{e.Message}）");
            }
        }

        private static void Save()
        {
            if (_activePath == null) _activePath = ProjectRootPath;

            try
            {
                string dir = Path.GetDirectoryName(_activePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(_activePath, JsonUtility.ToJson(new ObjectDictionaryFile { entries = _rawEntries }, true));
                _lastWriteUtc = File.GetLastWriteTimeUtc(_activePath);
            }
            catch (Exception e)
            {
                // 工程根写不进去（比如打包后只读）就退到 persistentDataPath，别让字典功能拖垮生成流程
                if (string.Equals(_activePath, ProjectRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    _activePath = PersistentPath;
                    Save();
                    return;
                }
                Debug.LogWarning($"[PrototypeC] 物件字典：写入失败（{_activePath}，{e.Message}）");
            }
        }

        /// <summary>把条目拼回扁平 JSON，交给既有的严格校验器复检（枚举 / 组合 / 202 字符 / 能力 ID 等规则一处维护）。</summary>
        private static string ComposeFlatJson(ObjectDictionaryEntry entry) =>
            "{\"objectName\":\"" + Escape(entry.objectName) + "\"," +
            "\"objectDescription\":\"" + Escape(entry.objectDescription) + "\"," +
            "\"sizeDescription\":\"" + Escape(entry.sizeDescription) + "\"," +
            "\"behavior\":\"" + Escape(entry.behavior) + "\"," +
            "\"placement\":\"" + Escape(entry.placement) + "\"," +
            "\"size\":\"" + Escape(entry.size) + "\"" +
            (string.IsNullOrWhiteSpace(entry.capabilities)
                ? string.Empty
                : ",\"capabilities\":\"" + Escape(entry.capabilities) + "\"") +
            "}";

        private static string Escape(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return raw.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
