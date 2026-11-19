// LlmObjectParser.cs —— Prototype C：LLM 物件解析通道（请求 → 严格 JSON → Schema 校验）
// 只负责「单词 → ObjectDefinition」，不构造 ObjectSpec、不碰生成参数与场景布局。
// 同时实现 IPromptFiller，把最近一次成功解析的两段描述以同步方式提供给旧的填充接口。
// 判定顺序：物件字典（直接命中 / 规范名纠错，均不请求 LLM）→ LLM（带字典相似条目参考）→ 新判定写回字典。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PrototypeC
{
    public class LlmObjectParser : IPromptFiller
    {
        /// <summary>Schema 不合法或组合非法时的总尝试次数（含首次）；「不可识别」是合法结果，不重试。</summary>
        public int maxAttempts = 2;

        private readonly ILlmChatClient _client;
        private ObjectParseResult _lastResult;
        private string _lastInput = string.Empty;

        public LlmObjectParser(ILlmChatClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>按 LlmConfig 的配置创建真实通道。</summary>
        public static LlmObjectParser CreateDefault()
        {
            var client = new LlmChatClient(LlmConfig.ApiKey, LlmConfig.BaseUrl, LlmConfig.Model)
            {
                disableThinking = LlmConfig.DisableThinking,
            };

            return new LlmObjectParser(client);
        }

        public ObjectParseResult LastResult => _lastResult;

        public async Task<ObjectParseResult> ParseAsync(string input, CancellationToken ct = default)
        {
            string word = (input ?? string.Empty).Trim();
            _lastInput = word;

            if (string.IsNullOrEmpty(word))
            {
                return Remember(new ObjectParseResult
                {
                    status = ObjectParseStatus.InvalidSchema,
                    error = "输入为空",
                });
            }

            // —— 字典直接命中：不再请求 LLM（用户在字典里纠正过的判定也走这条路，永远优先）——
            if (ObjectPropertyDictionary.TryGet(word, out ObjectDefinition dictionaryHit))
            {
                return Remember(new ObjectParseResult
                {
                    status = ObjectParseStatus.Ok,
                    definition = dictionaryHit,
                    fromDictionary = true,
                });
            }

            // 新物件：把字典里相似条目作为参考交给 LLM，保持同类物件的判定口径一致
            List<ObjectDefinition> similar = ObjectPropertyDictionary.FindSimilar(word);

            if (_client is LlmChatClient http && string.IsNullOrEmpty(http.apiKey))
            {
                return Remember(new ObjectParseResult
                {
                    status = ObjectParseStatus.RequestFailed,
                    error = $"未配置 LLM API Key（来源：{LlmConfig.Source}）。" +
                            LlmConfig.DescribeSearchedLocations() +
                            $"\n修复：设置环境变量 {LlmConfig.ApiKeyEnvironmentVariable}，" +
                            $"或写入 {LlmConfig.ProjectRootConfigPath}（{{\"apiKey\":\"sk-...\"}}）；" +
                            $"也可在 {LlmConfig.FallbackFileName} 里补 \"llmApiKey\":\"sk-...\"。",
                });
            }

            string lastError = "未知错误";
            string lastRaw = string.Empty;
            ObjectParseStatus lastStatus = ObjectParseStatus.InvalidSchema;

            for (int attempt = 1; attempt <= Mathf.Max(1, maxAttempts); attempt++)
            {
                // 第一次用标准 Prompt + 字典相似条目参考；重试时把上一次的拒绝原因（Schema / 非法组合）带回去
                string prompt = attempt == 1
                    ? LlmObjectPrompt.BuildUserPrompt(word, similar)
                    : LlmObjectPrompt.BuildRetryPrompt(word, lastError, similar);

                string raw;
                try
                {
                    raw = await _client.CompleteAsync(LlmObjectPrompt.SystemPrompt, prompt, ct);
                }
                catch (OperationCanceledException)
                {
                    return Remember(new ObjectParseResult
                    {
                        status = ObjectParseStatus.RequestFailed,
                        error = "请求已取消",
                    });
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    if (attempt < maxAttempts) continue;
                    return Remember(new ObjectParseResult
                    {
                        status = ObjectParseStatus.RequestFailed,
                        error = lastError,
                    });
                }

                ObjectParseResult result = ObjectDefinitionValidator.Validate(raw);
                lastRaw = raw;

                if (result.status == ObjectParseStatus.Ok || result.status == ObjectParseStatus.Unrecognized)
                {
                    if (result.status == ObjectParseStatus.Ok)
                    {
                        // LLM 已归一出规范名：若字典里已有该物件（多半是用户纠正过），以字典为准，不写回
                        if (ObjectPropertyDictionary.TryGet(result.definition.objectName, out ObjectDefinition corrected))
                        {
                            result.definition = corrected;
                            result.fromDictionary = true;
                        }
                        else
                        {
                            // 全新物件：写进字典，之后的相同输入直接命中，同时成为后续相似物件的参考
                            ObjectPropertyDictionary.AddOrReplace(result.definition);
                        }
                    }
                    return Remember(result);
                }

                lastError = result.error;
                lastStatus = result.status;

                if (attempt < maxAttempts)
                {
                    Debug.LogWarning($"[PrototypeC] LLM 输出未通过校验（{result.error}），重试第 {attempt + 1} 次");
                }
            }

            return Remember(new ObjectParseResult
            {
                status = lastStatus,
                error = lastError,
                rawText = lastRaw,
            });
        }

        /// <summary>把最近一次成功解析的两段描述交给旧填充接口；输入需与最近一次解析一致。</summary>
        public bool TryFill(string objectName, out string objectDescription, out string sizeDescription)
        {
            objectDescription = null;
            sizeDescription = null;

            if (_lastResult == null || !_lastResult.IsSuccess || _lastResult.definition == null) return false;
            if (!string.Equals((objectName ?? string.Empty).Trim(), _lastInput, StringComparison.OrdinalIgnoreCase)) return false;

            objectDescription = _lastResult.definition.objectDescription;
            sizeDescription = _lastResult.definition.sizeDescription;
            return true;
        }

        private ObjectParseResult Remember(ObjectParseResult result)
        {
            _lastResult = result;
            return result;
        }
    }
}
