// MeshyConfig.cs —— Prototype C：Meshy API Key 的外部配置读取
// 优先级：环境变量 MESHY_API_KEY → 项目根目录 meshy_config.json → persistentDataPath/meshy_config.json
// 两处 json 格式相同：{ "apiKey": "msy-..." }
// 项目根目录的配置文件已加入 .gitignore，禁止提交；Key 不会写入日志（只打印长度与来源）。

using System;
using System.IO;
using UnityEngine;

namespace PrototypeC
{
    [Serializable]
    internal class MeshyConfigFile
    {
        public string apiKey;
    }

    public static class MeshyConfig
    {
        public const string EnvironmentVariable = "MESHY_API_KEY";
        public const string FileName = "meshy_config.json";

        private static bool _loaded;
        private static string _apiKey = string.Empty;
        private static string _source = "未配置";

        public static string ProjectRootConfigPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName));

        public static string PersistentConfigPath =>
            Path.Combine(Application.persistentDataPath, FileName);

        public static string ApiKey { get { EnsureLoaded(); return _apiKey; } }
        public static string Source { get { EnsureLoaded(); return _source; } }
        public static bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

        /// <summary>外部配置改动后（例如新写入了配置文件）可主动刷新。</summary>
        public static void Reload() => _loaded = false;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            _apiKey = string.Empty;
            _source = "未配置";

            string env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                _apiKey = env.Trim();
                _source = "环境变量 " + EnvironmentVariable;
                return;
            }

            if (TryReadFile(ProjectRootConfigPath, "项目根目录 " + FileName)) return;
            if (TryReadFile(PersistentConfigPath, FileName + "（persistentDataPath）")) return;

            Debug.LogWarning(
                "[PrototypeC] 未找到 Meshy API Key，生成流程会直接报错。请任选一种方式配置：\n" +
                $"  1. 环境变量 {EnvironmentVariable}\n" +
                $"  2. 在 {ProjectRootConfigPath} 写入 {{\"apiKey\":\"msy-...\"}}（已在 .gitignore 中）\n" +
                $"  3. 在 {PersistentConfigPath} 写入同样的内容");
        }

        private static bool TryReadFile(string path, string sourceLabel)
        {
            if (!File.Exists(path)) return false;

            try
            {
                MeshyConfigFile cfg = JsonUtility.FromJson<MeshyConfigFile>(File.ReadAllText(path));
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.apiKey))
                {
                    _apiKey = cfg.apiKey.Trim();
                    _source = sourceLabel;
                    Debug.Log($"[PrototypeC] 已读取 Meshy API Key（来源：{sourceLabel}，长度 {_apiKey.Length}，内容已隐藏）");
                    return true;
                }

                Debug.LogWarning($"[PrototypeC] 配置文件存在但 apiKey 为空：{path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 读取配置文件失败：{path}，{e.Message}");
            }

            return false;
        }
    }
}
