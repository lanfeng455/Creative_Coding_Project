// LlmChatClient.cs —— Prototype C：OpenAI 兼容的 chat/completions 客户端（当前对接 DeepSeek）
// 依据 DeepSeek 官方文档：
//   POST {baseUrl}/chat/completions（baseUrl 形如 https://api.deepseek.com/v1）
//   response_format: {"type":"json_object"} 可用，但要求 prompt 里出现 "json"
//   thinking: {"type":"enabled"|"disabled"}，默认 enabled；解析任务用 disabled 更稳更快
//   文档提示 JSON 模式偶尔会返回空内容 → 这里对空内容做有限次重试
// 若服务端不接受 response_format（换别家时），自动降级为普通返回，由上层的稳健 JSON 提取兜底。

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PrototypeC
{
    public class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    /// <summary>可替换的对话通道：真实实现走 HTTP，测试用 mock 实现。</summary>
    public interface ILlmChatClient
    {
        Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct);
    }

    public class LlmChatClient : ILlmChatClient
    {
        public string baseUrl;
        public string apiKey;
        public string model;

        [Tooltip("单次请求超时（秒）")]
        public int timeoutSeconds = 60;

        [Tooltip("总尝试次数（含首次），对连接错误 / 5xx / 429 / 空内容做重试")]
        public int maxAttempts = 3;

        public bool useJsonMode = true;
        public bool disableThinking = true;
        public int maxTokens = 600;

        /// <summary>最近一次成功响应的原始文本，便于测试与排错。</summary>
        public string LastRawResponse { get; private set; } = string.Empty;

        public LlmChatClient(string apiKey, string baseUrl, string model)
        {
            this.apiKey = apiKey;
            this.baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            this.model = model;
        }

        public async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new LlmException("缺少 LLM API Key");

            string endpoint = baseUrl + "/chat/completions";
            string lastError = "未知错误";

            for (int attempt = 1; attempt <= Mathf.Max(1, maxAttempts); attempt++)
            {
                bool jsonMode = useJsonMode;
                string body = BuildBody(systemPrompt, userContent, jsonMode);

                using (var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + apiKey);
                    request.timeout = timeoutSeconds;

                    try
                    {
                        await SendAsync(request, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        lastError = "请求异常：" + e.Message;
                        if (attempt < maxAttempts) await DelayAsync(attempt, ct);
                        continue;
                    }

                    long code = request.responseCode;
                    string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                    if (request.result == UnityWebRequest.Result.ConnectionError)
                    {
                        lastError = "连接失败：" + request.error;
                        if (attempt < maxAttempts) await DelayAsync(attempt, ct);
                        continue;
                    }

                    if (code < 200 || code >= 300)
                    {
                        lastError = $"HTTP {code}：{ExtractError(text)}";

                        // 换供应商时若不支持 response_format，降级重试一次
                        if (code == 400 && useJsonMode && text.IndexOf("response_format", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Debug.LogWarning("[PrototypeC] 服务端不接受 response_format: json_object，降级为普通返回 + JSON 提取");
                            useJsonMode = false;
                            attempt--;
                            continue;
                        }

                        // 401/403 属配置问题，重试无意义
                        if (code == 401 || code == 403) throw new LlmException(lastError);
                        if ((code == 429 || code >= 500) && attempt < maxAttempts)
                        {
                            await DelayAsync(attempt, ct);
                            continue;
                        }

                        throw new LlmException(lastError);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        lastError = "响应为空";
                        if (attempt < maxAttempts) await DelayAsync(attempt, ct);
                        continue;
                    }

                    if (!TryExtractContent(text, out string content, out string parseError))
                    {
                        lastError = "响应解析失败：" + parseError;
                        if (attempt < maxAttempts) await DelayAsync(attempt, ct);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        // DeepSeek 文档明确提示 JSON 模式下可能偶发空内容
                        lastError = "模型返回空内容";
                        if (attempt < maxAttempts) await DelayAsync(attempt, ct);
                        continue;
                    }

                    LastRawResponse = text;
                    return content;
                }
            }

            throw new LlmException(lastError);
        }

        private string BuildBody(string systemPrompt, string userContent, bool jsonMode)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"model\":\"").Append(Escape(model)).Append("\",");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Escape(systemPrompt)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Escape(userContent)).Append("\"}");
            sb.Append("],");
            sb.Append("\"stream\":false,");
            sb.Append("\"max_tokens\":").Append(Mathf.Clamp(maxTokens, 64, 4096));

            if (jsonMode) sb.Append(",\"response_format\":{\"type\":\"json_object\"}");
            if (disableThinking) sb.Append(",\"thinking\":{\"type\":\"disabled\"}");

            sb.Append('}');
            return sb.ToString();
        }

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

        private static async Task DelayAsync(int attempt, CancellationToken ct)
        {
            await Task.Delay(400 * attempt, ct);
        }

        private static bool TryExtractContent(string responseText, out string content, out string error)
        {
            content = null;
            error = null;

            try
            {
                ChatCompletionResponse resp = JsonUtility.FromJson<ChatCompletionResponse>(responseText);
                if (resp == null || resp.choices == null || resp.choices.Length == 0 ||
                    resp.choices[0] == null || resp.choices[0].message == null)
                {
                    error = "choices 为空";
                    return false;
                }

                content = resp.choices[0].message.content;
                if (string.IsNullOrEmpty(content))
                {
                    // 少数兼容实现会把内容放在 reasoning_content 之外的空字段，这里明确报出来
                    error = "message.content 为空";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static string ExtractError(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "无响应内容";

            try
            {
                ChatErrorEnvelope err = JsonUtility.FromJson<ChatErrorEnvelope>(body);
                if (err != null && err.error != null && !string.IsNullOrWhiteSpace(err.error.message))
                {
                    return err.error.message;
                }
            }
            catch
            {
                // 忽略，退回截断文本
            }

            return body.Length <= 300 ? body : body.Substring(0, 300) + "…";
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length + 16);
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        [Serializable]
        private class ChatCompletionResponse
        {
            public ChatChoice[] choices;
        }

        [Serializable]
        private class ChatChoice
        {
            public ChatMessage message;
        }

        [Serializable]
        private class ChatMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        private class ChatErrorEnvelope
        {
            public ChatError error;
        }

        [Serializable]
        private class ChatError
        {
            public string message;
        }
    }
}
