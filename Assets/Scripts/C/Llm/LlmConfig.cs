// LlmConfig.cs —— Prototype C：LLM（DeepSeek / OpenAI 兼容）配置读取
// 优先级（逐字段）：环境变量 → 工程根 llm_config.json → persistentDataPath/llm_config.json
// 环境变量：DEEPSEEK_API_KEY / DEEPSEEK_BASE_URL / DEEPSEEK_MODEL
// json 格式：{ "apiKey": "...", "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "disableThinking": true }
// 兜底：上面都拿不到 Key 时，再去 meshy_config.json 里找 llmApiKey / deepseekApiKey / llm.apiKey
//       （用户习惯把 Key 都写在 meshy_config.json 里，这条兜底避免"配了却读不到"）。
// Key 只打印来源与长度，永不写入日志。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PrototypeC
{
    [Serializable]
    internal class LlmConfigFile
    {
        public string apiKey;
        public string baseUrl;
        public string model;
        public bool disableThinking = true;
    }

    // meshy_config.json 里可能承载 LLM 配置的嵌套结构
    [Serializable]
    internal class LlmSection
    {
        public string apiKey;
        public string baseUrl;
        public string model;
    }

    [Serializable]
    internal class LlmFallbackFile
    {
        public string llmApiKey;
        public string deepseekApiKey;
        public string deepseekKey;
        public string baseUrl;
        public string model;
        public LlmSection llm;
        public LlmSection deepseek;
    }

    public static class LlmConfig
    {
        public const string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
        public const string BaseUrlEnvironmentVariable = "DEEPSEEK_BASE_URL";
        public const string ModelEnvironmentVariable = "DEEPSEEK_MODEL";
        public const string FileName = "llm_config.json";

        /// <summary>兜底配置文件（Meshy 的配置，允许顺带写 LLM Key）。</summary>
        public const string FallbackFileName = "meshy_config.json";

        public const string DefaultBaseUrl = "https://api.deepseek.com/v1";
        public const string DefaultModel = "deepseek-chat";

        private static bool _loaded;
        private static string _apiKey = string.Empty;
        private static string _baseUrl = DefaultBaseUrl;
        private static string _model = DefaultModel;
        private static bool _disableThinking = true;
        private static string _source = "未配置";
        private static readonly List<string> _searchLog = new List<string>();

        public static string ProjectRootConfigPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));

        public static string PersistentConfigPath =>
            Path.Combine(Application.persistentDataPath, FileName);

        public static string ProjectRootFallbackPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FallbackFileName));

        public static string PersistentFallbackPath =>
            Path.Combine(Application.persistentDataPath, FallbackFileName);

        public static string WorkingDirectoryFallbackPath =>
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), FallbackFileName));

        public static string ApiKey { get { EnsureLoaded(); return _apiKey; } }
        public static string BaseUrl { get { EnsureLoaded(); return _baseUrl; } }
        public static string Model { get { EnsureLoaded(); return _model; } }
        public static bool DisableThinking { get { EnsureLoaded(); return _disableThinking; } }
        public static string Source { get { EnsureLoaded(); return _source; } }
        public static bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

        /// <summary>外部配置改动后（例如新写入了配置文件）可主动刷新。</summary>
        public static void Reload() => _loaded = false;

        /// <summary>逐条列出本次查找过的位置与结果，用于把"未配置"提示变成可操作的检查清单。</summary>
        public static string DescribeSearchedLocations()
        {
            EnsureLoaded();
            if (_searchLog.Count == 0) return string.Empty;
            return "\n已查找：\n" + string.Join("\n", _searchLog);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _apiKey = string.Empty;
            _baseUrl = DefaultBaseUrl;
            _model = DefaultModel;
            _disableThinking = true;
            _source = "未配置";
            _searchLog.Clear();

            // 1) llm_config.json（工程根优先，其次 persistentDataPath）
            string fileSource = string.Empty;
            string rootLabel = "工程根 " + FileName;
            string persistentLabel = FileName + "（persistentDataPath）";

            if (RecordProbe(ProjectRootConfigPath, rootLabel, "apiKey")
                && TryReadFile(ProjectRootConfigPath, rootLabel))
            {
                fileSource = rootLabel;
            }
            else if (RecordProbe(PersistentConfigPath, persistentLabel, "apiKey")
                     && TryReadFile(PersistentConfigPath, persistentLabel))
            {
                fileSource = persistentLabel;
            }

            // 2) 环境变量逐字段覆盖
            var overrides = new List<string>();
            string env;

            env = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                _apiKey = env.Trim();
                overrides.Add(ApiKeyEnvironmentVariable);
            }

            env = Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                _baseUrl = env.Trim();
                overrides.Add(BaseUrlEnvironmentVariable);
            }

            env = Environment.GetEnvironmentVariable(ModelEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                _model = env.Trim();
                overrides.Add(ModelEnvironmentVariable);
            }

            // 3) 兜底：meshy_config.json 里的 LLM Key（工程根 → persistentDataPath → 当前工作目录）
            if (string.IsNullOrEmpty(_apiKey))
            {
                TryFallbackFiles(ref fileSource);
            }

            _baseUrl = NormalizeBaseUrl(_baseUrl);

            if (overrides.Count > 0)
            {
                _source = "环境变量 " + string.Join(" / ", overrides);
                if (!string.IsNullOrEmpty(fileSource)) _source += "（覆盖 " + fileSource + "）";
            }
            else if (!string.IsNullOrEmpty(fileSource))
            {
                _source = fileSource;
            }

            if (HasApiKey)
            {
                Debug.Log($"[PrototypeC] LLM 配置：baseUrl={_baseUrl}｜model={_model}｜" +
                          $"Key 来源={_source}，长度 {_apiKey.Length}（内容已隐藏）");
            }
            else
            {
                Debug.LogWarning(
                    "[PrototypeC] 未配置 LLM API Key，AI 解析不可用（可关闭「AI 解析」用手动模式）。" +
                    "\n期望配置（任选一种）：" +
                    $"\n  1. 环境变量 {ApiKeyEnvironmentVariable}=sk-..." +
                    $"\n  2. 文件 {ProjectRootConfigPath}，内容 {{\"apiKey\":\"sk-...\",\"baseUrl\":\"{DefaultBaseUrl}\",\"model\":\"{DefaultModel}\"}}" +
                    $"\n  3. 文件 {PersistentConfigPath}，字段同上" +
                    $"\n  4. 兜底：在 {FallbackFileName} 里写 \"llmApiKey\":\"sk-...\" 或 \"llm\":{{\"apiKey\":\"sk-...\"}}" +
                    DescribeSearchedLocations());
            }
        }

        // 记录一次探测结果：文件在不在、字段有没有值，方便用户 30 秒内自检。
        private static bool RecordProbe(string path, string label, string expectedField)
        {
            if (!File.Exists(path))
            {
                _searchLog.Add($"  · {label}：文件不存在（{path}）");
                return false;
            }

            _searchLog.Add($"  · {label}：文件存在，找字段 {expectedField}");
            return true;
        }

        private static void TryFallbackFiles(ref string fileSource)
        {
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TryFallbackFile(ProjectRootFallbackPath, "工程根 " + FallbackFileName, ref fileSource, tried);
            if (!string.IsNullOrEmpty(_apiKey)) return;

            TryFallbackFile(PersistentFallbackPath, FallbackFileName + "（persistentDataPath）", ref fileSource, tried);
            if (!string.IsNullOrEmpty(_apiKey)) return;

            TryFallbackFile(WorkingDirectoryFallbackPath, FallbackFileName + "（当前工作目录）", ref fileSource, tried);
        }

        private static void TryFallbackFile(string path, string label, ref string fileSource, HashSet<string> tried)
        {
            string fullPath = Path.GetFullPath(path);
            if (!tried.Add(fullPath)) return;

            if (!RecordProbe(fullPath, label, "llmApiKey / deepseekApiKey / llm.apiKey")) return;

            try
            {
                LlmFallbackFile cfg = JsonUtility.FromJson<LlmFallbackFile>(File.ReadAllText(fullPath));
                if (cfg == null) return;

                string key = FirstNonEmpty(
                    cfg.llmApiKey,
                    cfg.deepseekApiKey,
                    cfg.deepseekKey,
                    cfg.llm != null ? cfg.llm.apiKey : null,
                    cfg.deepseek != null ? cfg.deepseek.apiKey : null);

                if (string.IsNullOrEmpty(key))
                {
                    _searchLog[_searchLog.Count - 1] += " → 未找到非空的 LLM Key 字段";
                    return;
                }

                _apiKey = key.Trim();
                _searchLog[_searchLog.Count - 1] += " → 命中";

                if (!string.IsNullOrWhiteSpace(cfg.baseUrl)) _baseUrl = cfg.baseUrl.Trim();
                else if (cfg.llm != null && !string.IsNullOrWhiteSpace(cfg.llm.baseUrl)) _baseUrl = cfg.llm.baseUrl.Trim();
                else if (cfg.deepseek != null && !string.IsNullOrWhiteSpace(cfg.deepseek.baseUrl)) _baseUrl = cfg.deepseek.baseUrl.Trim();

                if (!string.IsNullOrWhiteSpace(cfg.model)) _model = cfg.model.Trim();
                else if (cfg.llm != null && !string.IsNullOrWhiteSpace(cfg.llm.model)) _model = cfg.llm.model.Trim();
                else if (cfg.deepseek != null && !string.IsNullOrWhiteSpace(cfg.deepseek.model)) _model = cfg.deepseek.model.Trim();

                fileSource = label + "（兜底字段）";
            }
            catch (Exception e)
            {
                _searchLog[_searchLog.Count - 1] += " → 解析失败：" + e.Message;
                Debug.LogWarning($"[PrototypeC] 读取兜底配置失败：{fullPath}，{e.Message}");
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])) return values[i];
            }

            return string.Empty;
        }

        private static bool TryReadFile(string path, string sourceLabel)
        {
            try
            {
                LlmConfigFile cfg = JsonUtility.FromJson<LlmConfigFile>(File.ReadAllText(path));
                if (cfg == null) return false;

                if (!string.IsNullOrWhiteSpace(cfg.apiKey)) _apiKey = cfg.apiKey.Trim();
                if (!string.IsNullOrWhiteSpace(cfg.baseUrl)) _baseUrl = cfg.baseUrl.Trim();
                if (!string.IsNullOrWhiteSpace(cfg.model)) _model = cfg.model.Trim();
                _disableThinking = cfg.disableThinking;

                if (string.IsNullOrEmpty(_apiKey))
                {
                    _searchLog[_searchLog.Count - 1] += " → apiKey 为空";
                    return false;
                }

                _searchLog[_searchLog.Count - 1] += " → 命中";
                Debug.Log($"[PrototypeC] 已读取 LLM 配置（来源：{sourceLabel}）");
                return true;
            }
            catch (Exception e)
            {
                _searchLog[_searchLog.Count - 1] += " → 解析失败：" + e.Message;
                Debug.LogWarning($"[PrototypeC] 读取 LLM 配置失败：{path}，{e.Message}");
                return false;
            }
        }

        private static string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return DefaultBaseUrl;
            return url.Trim().TrimEnd('/');
        }
    }
}
