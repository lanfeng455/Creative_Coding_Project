// MeshyClient.cs —— Prototype C：Meshy Text-to-3D API 客户端
// 依据官方文档 https://docs.meshy.ai/en/api/text-to-3d 实现：
//   POST https://api.meshy.ai/openapi/v2/text-to-3d        （mode=preview / refine）→ { "result": "<task_id>" }
//   GET  https://api.meshy.ai/openapi/v2/text-to-3d/{id}   → 任务对象（status / progress / model_urls.glb / task_error）
// API Key 由 MeshyConfig 传入，仅放在 Authorization 头里，不落盘、不打印。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PrototypeC
{
    public class MeshyException : Exception
    {
        public MeshyException(string message) : base(message) { }
    }

    public class MeshyTaskState
    {
        public string id;
        public string status;
        public int progress;
        public string glbUrl;
        public string errorMessage;

        public bool IsSucceeded => status == "SUCCEEDED";

        public bool IsFinished =>
            status == "SUCCEEDED" || status == "FAILED" || status == "CANCELED" || status == "EXPIRED";
    }

    [Serializable]
    internal class MeshyCreateResponse
    {
        public string result;
    }

    [Serializable]
    internal class MeshyModelUrls
    {
        public string glb;
    }

    [Serializable]
    internal class MeshyTaskError
    {
        public string message;
    }

    [Serializable]
    internal class MeshyTaskResponse
    {
        public string id;
        public string status;
        public int progress;
        public MeshyModelUrls model_urls;
        public MeshyTaskError task_error;
    }

    [Serializable]
    internal class MeshyErrorBody
    {
        public string message;
    }

    public class MeshyClient
    {
        public const string Endpoint = "https://api.meshy.ai/openapi/v2/text-to-3d";

        /// <summary>轮询间隔（秒）；服务端返回 Retry-After 时优先使用。</summary>
        public float PollIntervalSeconds = 5f;

        /// <summary>单个任务（preview 或 refine 各自）的最长等待秒数。</summary>
        public float TimeoutSeconds = 900f;

        public int ApiRequestTimeoutSeconds = 60;
        public int DownloadTimeoutSeconds = 300;

        private readonly string _apiKey;
        private float _nextPollDelay;

        public MeshyClient(string apiKey)
        {
            _apiKey = apiKey ?? string.Empty;
        }

        /// <summary>提交 preview（素模）任务，返回 task id。</summary>
        /// <remarks>
        /// prompt 已由 PromptComposer 带上完整风格模板（Meshy 上限 800 字符）。
        /// 官方文档标注 art_style 已废弃且被忽略、text-to-3d 无 negative_prompt 字段，因此不发送。
        /// </remarks>
        public async Task<string> CreatePreviewTaskAsync(string prompt, GenerationParams generation, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new MeshyException("Prompt 为空，无法提交生成任务");
            string body = BuildPreviewBody(prompt, generation);
            return await CreateTaskAsync(body, "preview", ct);
        }

        /// <summary>基于已成功的 preview 任务提交 refine（贴图）任务，返回 task id。</summary>
        /// <param name="texturePrompt">贴图提示词（官方 texture_prompt，上限 800 字符），用于延续同一风格。</param>
        public async Task<string> CreateRefineTaskAsync(
            string previewTaskId,
            GenerationParams generation,
            string texturePrompt,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(previewTaskId)) throw new MeshyException("preview 任务 id 为空");
            string body = BuildRefineBody(previewTaskId, generation, texturePrompt);
            return await CreateTaskAsync(body, "refine", ct);
        }

        private async Task<string> CreateTaskAsync(string json, string stageLabel, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiKey)) throw new MeshyException("缺少 Meshy API Key");

            using (var request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                request.timeout = ApiRequestTimeoutSeconds;

                await SendAsync(request, ct);
                EnsureTransportOk(request, $"提交 {stageLabel} 任务");

                if (request.responseCode < 200 || request.responseCode >= 300)
                {
                    throw new MeshyException(
                        $"提交 {stageLabel} 任务失败（HTTP {request.responseCode}）：{ExtractError(request.downloadHandler?.text)}");
                }

                MeshyCreateResponse parsed = ParseJson<MeshyCreateResponse>(request.downloadHandler.text, "创建任务响应");
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.result))
                {
                    throw new MeshyException($"提交 {stageLabel} 任务失败：响应中没有 task id");
                }

                return parsed.result;
            }
        }

        /// <summary>查询单个任务状态。</summary>
        public async Task<MeshyTaskState> GetTaskAsync(string taskId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_apiKey)) throw new MeshyException("缺少 Meshy API Key");

            string url = Endpoint + "/" + UnityWebRequest.EscapeURL(taskId);

            using (var request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _apiKey);
                request.timeout = ApiRequestTimeoutSeconds;

                await SendAsync(request, ct);
                EnsureTransportOk(request, "查询任务状态");

                if (request.responseCode < 200 || request.responseCode >= 300)
                {
                    throw new MeshyException(
                        $"查询任务状态失败（HTTP {request.responseCode}）：{ExtractError(request.downloadHandler?.text)}");
                }

                MeshyTaskResponse resp = ParseJson<MeshyTaskResponse>(request.downloadHandler.text, "任务状态响应");
                if (resp == null) throw new MeshyException("查询任务状态失败：响应为空");

                _nextPollDelay = ParseRetryAfter(request.GetResponseHeader("Retry-After"));

                return new MeshyTaskState
                {
                    id = string.IsNullOrEmpty(resp.id) ? taskId : resp.id,
                    status = resp.status ?? string.Empty,
                    progress = resp.progress,
                    glbUrl = resp.model_urls != null ? resp.model_urls.glb : null,
                    errorMessage = resp.task_error != null ? resp.task_error.message : null,
                };
            }
        }

        /// <summary>轮询直到任务完成；失败/超时抛出 MeshyException。onUpdate 会收到每次轮询的状态。</summary>
        public async Task<MeshyTaskState> WaitForTaskAsync(
            string taskId,
            string stageLabel,
            Action<MeshyTaskState> onUpdate,
            CancellationToken ct)
        {
            float startTime = Time.realtimeSinceStartup;
            _nextPollDelay = PollIntervalSeconds;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                MeshyTaskState state = await GetTaskAsync(taskId, ct);
                onUpdate?.Invoke(state);

                if (state.IsSucceeded)
                {
                    if (string.IsNullOrWhiteSpace(state.glbUrl))
                    {
                        throw new MeshyException($"{stageLabel}成功但未返回 GLB 地址（任务 {taskId}）");
                    }

                    return state;
                }

                if (state.IsFinished)
                {
                    string reason = string.IsNullOrWhiteSpace(state.errorMessage)
                        ? state.status
                        : $"{state.status}：{state.errorMessage}";
                    throw new MeshyException($"{stageLabel}失败（{reason}）");
                }

                if (Time.realtimeSinceStartup - startTime > TimeoutSeconds)
                {
                    throw new MeshyException(
                        $"{stageLabel}超时（等待超过 {TimeoutSeconds:0} 秒，任务 {taskId} 仍为 {state.status} {state.progress}%）");
                }

                float delay = _nextPollDelay > 0f ? _nextPollDelay : PollIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }

        /// <summary>下载 GLB 到指定本地路径。</summary>
        public async Task DownloadAsync(string url, string destinationPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new MeshyException("下载地址为空");

            string folder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(destinationPath);
                request.timeout = DownloadTimeoutSeconds;

                await SendAsync(request, ct);
                EnsureTransportOk(request, "下载模型");

                if (request.responseCode < 200 || request.responseCode >= 300)
                {
                    throw new MeshyException($"下载模型失败（HTTP {request.responseCode}）");
                }
            }

            if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length <= 0)
            {
                throw new MeshyException("下载模型失败：文件为空");
            }
        }

        // ---------- 内部工具 ----------

        private static async Task SendAsync(UnityWebRequest request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<bool>();
            using (ct.Register(() =>
                   {
                       if (!request.isDone) request.Abort();
                   }))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                operation.completed += _ => completion.TrySetResult(true);
                await completion.Task;
            }

            ct.ThrowIfCancellationRequested();
        }

        private static void EnsureTransportOk(UnityWebRequest request, string action)
        {
            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new MeshyException($"{action}失败：{request.error}");
            }
        }

        private static T ParseJson<T>(string json, string label) where T : class
        {
            try
            {
                T parsed = JsonUtility.FromJson<T>(json);
                if (parsed == null) throw new MeshyException($"解析{label}失败：内容为空");
                return parsed;
            }
            catch (MeshyException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new MeshyException($"解析{label}失败：{e.Message}");
            }
        }

        private static string ExtractError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "无响应内容";

            try
            {
                MeshyErrorBody err = JsonUtility.FromJson<MeshyErrorBody>(body);
                if (err != null && !string.IsNullOrWhiteSpace(err.message)) return err.message;
            }
            catch
            {
                // 响应体不是预期 JSON，直接回显截断后的原文
            }

            string trimmed = body.Trim();
            return trimmed.Length > 300 ? trimmed.Substring(0, 300) + "…" : trimmed;
        }

        private static float ParseRetryAfter(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return 0f;
            if (float.TryParse(header.Trim(), out float seconds)) return Mathf.Clamp(seconds, 1f, 60f);
            return 0f;
        }

        private static string BuildPreviewBody(string prompt, GenerationParams generation)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            AppendStringField(sb, "mode", "preview");
            sb.Append(',');
            AppendStringField(sb, "prompt", prompt);
            sb.Append(",\"target_formats\":[\"glb\"]");

            if (generation != null)
            {
                // smart-topology（meshy-t2 / meshy-t2.5）必须显式声明 model_type，否则服务端按 standard 处理会 400
                if (!string.IsNullOrWhiteSpace(generation.modelType))
                {
                    sb.Append(',');
                    AppendStringField(sb, "model_type", generation.modelType.Trim());
                }

                if (!string.IsNullOrWhiteSpace(generation.aiModel))
                {
                    sb.Append(',');
                    AppendStringField(sb, "ai_model", generation.aiModel.Trim());
                }

                if (generation.shouldRemesh)
                {
                    sb.Append(",\"should_remesh\":true");
                    sb.Append(",\"target_polycount\":")
                      .Append(Mathf.Clamp(generation.targetPolycount, 100, 300000));
                }
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildRefineBody(string previewTaskId, GenerationParams generation, string texturePrompt)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            AppendStringField(sb, "mode", "refine");
            sb.Append(',');
            AppendStringField(sb, "preview_task_id", previewTaskId);

            bool pbr = generation == null || generation.enablePbr;
            sb.Append(pbr ? ",\"enable_pbr\":true" : ",\"enable_pbr\":false");
            sb.Append(",\"target_formats\":[\"glb\"]");

            bool usesSmartTexture = generation != null &&
                                    string.Equals(generation.textureModel, "meshy-n1", System.StringComparison.OrdinalIgnoreCase);

            // 可换用更快的贴图模型（官方 refine 的 ai_model）；留空则沿用 preview 的模型
            if (generation != null && !string.IsNullOrWhiteSpace(generation.textureModel))
            {
                sb.Append(',');
                AppendStringField(sb, "ai_model", generation.textureModel.Trim());
            }

            if (generation != null && !string.IsNullOrWhiteSpace(generation.textureResolution))
            {
                sb.Append(',');
                AppendStringField(sb, "texture_resolution", generation.textureResolution.Trim());
            }

            bool hasImageUrl = generation != null && !string.IsNullOrWhiteSpace(generation.textureImageUrl);
            if (hasImageUrl)
            {
                sb.Append(',');
                AppendStringField(sb, "texture_image_url", generation.textureImageUrl.Trim());
            }

            // texture_prompt 与 texture_image_url 互斥；meshy-n1 也不允许同时传 texture_prompt（会 400）
            if (!string.IsNullOrWhiteSpace(texturePrompt) && !usesSmartTexture && !hasImageUrl)
            {
                string trimmed = texturePrompt.Trim();
                if (trimmed.Length > 800) trimmed = trimmed.Substring(0, 800);

                sb.Append(',');
                AppendStringField(sb, "texture_prompt", trimmed);
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendStringField(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":\"");
            AppendEscaped(sb, value);
            sb.Append('"');
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            if (value == null) return;

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
        }
    }
}
