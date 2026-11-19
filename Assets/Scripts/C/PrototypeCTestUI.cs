// PrototypeCTestUI.cs —— Prototype C：最小测试入口
// Canvas 上的输入框（默认 CHAIR）+ Generate 按钮 + 状态文本，点击后跑完整流程。
// 本阶段：开启 AI 解析时由 LLM 决定 objectName / 描述 / behavior / placement / size，
// Inspector 里的字段只做默认值与兜底（sizeClassOverride 可强制覆盖档位）。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PrototypeC
{
    public class PrototypeCTestUI : MonoBehaviour
    {
        [Header("引用")]
        public ObjectModelService service;
        public TMP_InputField inputField;
        public Button generateButton;
        public TMP_Text statusText;

        [Header("物件定义（本阶段固定）")]
        [Tooltip("默认物件名；输入框为空时使用")]
        public string objectName = "CHAIR";

        [Tooltip("输入框内容会替换模板里的 [OBJECT DESCRIPTION]；留空时用 objectName")]
        public string objectDescriptionOverride = "";

        [Tooltip("替换模板里的 [SIZE DESCRIPTION]；将来由 LLM 决策，本阶段是可编辑默认值")]
        public string sizeDescription = "Roughly 0.5 m wide, 0.5 m deep, 0.9 m tall.";

        [Tooltip("可配置的风格层：共享模板 + 版本 + 贴图风格（参与缓存指纹）")]
        public PromptStyle style = new PromptStyle();

        [Tooltip("行为标签（仅写入 metadata）")]
        public string behavior = "Decoration";

        [Tooltip("初始放置位置；y 为落地高度")]
        public Vector3 placement = Vector3.zero;

        [Tooltip("朝向（绕模型自身 pivot 的 Y 轴，以锚点局部坐标为准）；默认 90，想反向改成 -90")]
        public float yawDegrees = 90f;

        [Tooltip("期望尺寸（米）：仅在 AI 解析关闭、或 LLM 没给 size 时作为兜底；height 不再单独使用")]
        public Vector3 size = new Vector3(0.55f, 0.95f, 0.55f);

        [Tooltip("强制尺寸档位：留空 = 用 LLM 给的 size；填 Small / Medium / Large / SuperLarge 可对单个物件手动覆盖（例如 CHAIR 想固定 0.9 m）")]
        public string sizeClassOverride = "";

        public GenerationParams generation = new GenerationParams();

        [Header("AI 解析（LLM 物件定义）")]
        [Tooltip("开启后先用 LLM 把输入解析成标准物件定义，再交给生成链路；关闭则走手动填写")]
        public bool useAiParsing = true;

        [Tooltip("测试面板上的「AI 解析」开关；留空时只读 useAiParsing")]
        public Toggle aiParseToggle;

        [Header("生成队列")]
        [Tooltip("排队上限（不含正在生成的那个）；超过后新的提交会被拒绝并提示")]
        public int maxQueueLength = 5;

        [Header("布局回放")]
        [Tooltip("进入 Play 时自动按 layout.json 回放（默认关）。手动回放：组件右上角 ⋮ →「立即按 layout.json 回放」，或调用 ReplayLayoutNow()。只复用本机缓存，不调 Meshy")]
        public bool autoReplayLayoutOnStart = false;

        [Header("布局（LLM 意图）")]
        [Tooltip("开启后每个物件在模型就绪后多一次 LLM 布局调用（带真实包围盒）；失败自动退回规则求解")]
        public bool useLlmLayout = true;

        [Tooltip("发给 LLM 的同分区占用条目上限（房间总容量是 RoomOccupancy.maxObjects）")]
        public int maxLayoutOccupants = 12;

        private bool _running;
        private string _runningInput = string.Empty;
        private readonly Queue<PendingRequest> _queue = new Queue<PendingRequest>();
        private LlmObjectParser _parser;
        private LlmLayoutPlanner _layoutPlanner;

        /// <summary>排队项：parse 非空表示已经过 LLM/字典判定，生成时不再重复解析。</summary>
        private sealed class PendingRequest
        {
            public string input;
            public ObjectParseResult parse;
        }

        private void Awake()
        {
            if (service == null) service = GetComponent<ObjectModelService>();

            if (service != null) service.StatusChanged += OnStatusChanged;
            if (generateButton != null) generateButton.onClick.AddListener(OnGenerateClicked);

            if (aiParseToggle != null)
            {
                aiParseToggle.isOn = useAiParsing;
                aiParseToggle.onValueChanged.AddListener(OnAiParsingToggled);
            }
        }

        private void Start()
        {
            if (inputField != null && string.IsNullOrEmpty(inputField.text)) inputField.text = objectName;
            SetStatus($"就绪｜缓存目录 {ObjectCache.RootPath}");
            // 自动回放默认关：旧场景里序列化的 replayLayoutOnStart=1 已经不再被读取（字段已更名为
            // autoReplayLayoutOnStart，默认 false），所以进 Play 是空房间，需要时手动回放。
            _ = RunLoopAsync(autoReplayLayoutOnStart);
        }

        private void OnDestroy()
        {
            if (service != null) service.StatusChanged -= OnStatusChanged;
            if (generateButton != null) generateButton.onClick.RemoveListener(OnGenerateClicked);
            if (aiParseToggle != null) aiParseToggle.onValueChanged.RemoveListener(OnAiParsingToggled);
        }

        private void OnAiParsingToggled(bool enabled)
        {
            useAiParsing = enabled;
            SetStatus(enabled
                ? "AI 解析已开启：输入单词后由 LLM 生成标准物件定义"
                : "AI 解析已关闭：使用手动填写的 objectDescription / sizeDescription");
        }

        /// <summary>
        /// 点击只负责入队：等待期间输入框和按钮都保持可用。
        /// 队列按 FIFO 串行执行，前一个结束后自动开始下一个；每个物件在 ObjectModelService 里各自占槽、各自出占位牌。
        /// </summary>
        private void OnGenerateClicked()
        {
            string input = inputField != null ? inputField.text.Trim() : objectName;
            if (string.IsNullOrEmpty(input))
            {
                SetStatus("请输入物件名称（例如 CHAIR）");
                return;
            }

            if (IsQueuedOrRunning(input))
            {
                SetStatus($"已在队列中：{input}｜等它跑完再提交，避免重复生成");
                return;
            }

            int cap = Mathf.Max(1, maxQueueLength);
            if (_queue.Count >= cap)
            {
                SetStatus($"队列已满（上限 {cap} 个）｜等前面的完成后再提交");
                return;
            }

            _queue.Enqueue(new PendingRequest { input = input });

            if (_running)
            {
                SetStatus($"已加入队列：{input}｜排队中 {_queue.Count} 个");
            }
            else
            {
                SetStatus($"开始生成：{input}");
                _ = RunLoopAsync(false);
            }
        }

        /// <summary>
        /// 打字机发送键入口：先判定合法性（物件字典 → LLM），合法才入队走生成链路。
        /// 返回 false 表示「不合法 / 排队已满 / 判定失败」，调用方据此在打字机上打红叉。
        /// 判定结果随排队项一起携带，生成时不会第二次请求 LLM。
        /// </summary>
        public async Task<bool> SubmitTypewriterWordAsync(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (input.Length == 0) return false;

            // 已经排队过同一个词：视为合法（不重复提交，也不打红叉）
            if (IsQueuedOrRunning(input))
            {
                SetStatus($"已在队列中：{input}｜等它跑完再提交");
                return true;
            }

            int cap = Mathf.Max(1, maxQueueLength);
            if (_queue.Count >= cap)
            {
                SetStatus($"队列已满（上限 {cap} 个）｜{input} 先不提交");
                return false;
            }

            if (_parser == null) _parser = LlmObjectParser.CreateDefault();

            SetStatus($"判定中：{input}…");
            ObjectParseResult parse;
            try
            {
                parse = await _parser.ParseAsync(input);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 打字机发送判定异常（{input}）：{e.Message}");
                SetStatus($"判定失败：{input}｜{e.Message}");
                return false;
            }

            if (parse == null || !parse.IsSuccess)
            {
                string reason = parse != null ? parse.Describe() : "判定结果为空";
                SetStatus($"不合法：{input}｜{reason}");
                Debug.LogWarning($"[PrototypeC] 打字机发送被拒绝（{input}）：{reason}" +
                                 (parse != null ? $"\n原始输出：{parse.rawText}" : string.Empty));
                return false;
            }

            _queue.Enqueue(new PendingRequest { input = input, parse = parse });

            if (_running)
            {
                SetStatus($"合法：{input}｜已加入队列，排队中 {_queue.Count} 个");
            }
            else
            {
                SetStatus($"合法：{input}｜{(parse.fromDictionary ? "物件字典" : "LLM")}，开始生成");
                _ = RunLoopAsync(false);
            }

            return true;
        }

        /// <summary>
        /// 手动回放入口：组件右上角 ⋮ →「立即按 layout.json 回放」，也可以在其它脚本里直接调用。
        /// 只复用本机缓存、不调 Meshy；生成/回放进行中会被拒绝，避免并发。
        /// </summary>
        [ContextMenu("立即按 layout.json 回放")]
        public void ReplayLayoutNow()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[PrototypeC] 手动回放只能在 Play 模式下进行");
                return;
            }

            if (service == null) service = GetComponent<ObjectModelService>();
            if (service == null || service.occupancy == null)
            {
                SetStatus("回放失败：缺少 ObjectModelService / RoomOccupancy");
                return;
            }

            if (_running)
            {
                SetStatus("正在生成或回放，等当前任务结束后再试");
                return;
            }

            SetStatus("手动回放 layout.json…");
            _ = RunLoopAsync(true);
        }

        /// <summary>队列去重：正在跑的和排队中的同名物件都算重复（按输入文本，忽略大小写）。</summary>
        private bool IsQueuedOrRunning(string input)
        {
            if (_running && string.Equals(_runningInput, input, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (PendingRequest queued in _queue)
            {
                if (queued != null && string.Equals(queued.input, input, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 队列循环：replayFirst 为 true 时先回放 layout.json，再按 FIFO 处理用户的提交。
        /// 回放与排队都算「正在跑」，所以回放期间点击只会入队，不会并发。
        /// </summary>
        private async Task RunLoopAsync(bool replayFirst)
        {
            if (_running) return;
            _running = true;

            try
            {
                if (replayFirst)
                {
                    try
                    {
                        await ReplaySavedLayoutAsync();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[PrototypeC] 布局回放异常：" + e);
                    }
                }

                while (_queue.Count > 0)
                {
                    PendingRequest request = _queue.Dequeue();
                    _runningInput = request.input;
                    try
                    {
                        await GenerateOneAsync(request);
                    }
                    catch (Exception e)
                    {
                        // 单个物件失败不阻塞队列，继续跑下一个
                        Debug.LogError("[PrototypeC] 队列项异常（" + _runningInput + "）：" + e);
                        SetStatus($"失败：{_runningInput}｜{e.Message}");
                    }
                }
            }
            finally
            {
                _running = false;
                _runningInput = string.Empty;
            }
        }

        /// <summary>
        /// 按 layout.json 回放：只复用本机缓存，绝不重新调用 Meshy；
        /// 位置不复用旧坐标，交给 LayoutSolver 按当前占位重新求解。
        /// </summary>
        private async Task ReplaySavedLayoutAsync()
        {
            if (service == null || service.occupancy == null) return;

            var pending = new List<PlacedItem>(service.occupancy.Items);
            int total = 0;
            foreach (PlacedItem item in pending)
            {
                if (item != null && item.instance == null) total++;
            }

            if (total == 0) return;

            // 回放必须和真正生成用同一份参数：快速模式覆盖 + 风格指纹都在这里归一化一次，
            // 否则快速模式下会拿「标准档指纹」去查「快速档缓存」，回放会全部落空。
            GenerationParams effectiveGeneration = service.ResolveEffectiveGeneration(generation, style);

            int done = 0;
            foreach (PlacedItem item in pending)
            {
                if (item == null || item.instance != null) continue;
                if (string.IsNullOrWhiteSpace(item.id)) continue;

                done++;

                // 精确档或同 id 的其它档都能复用（后者只提示，不花额度）；一档都没有才跳过，
                // 宁可少一个也不在回放里偷偷花 Meshy 额度。
                ObjectCache.CacheHitKind hit = ObjectCache.TryGet(
                    item.id, effectiveGeneration, out ObjectCache.CacheEntry cached);
                if (hit == ObjectCache.CacheHitKind.Miss)
                {
                    Debug.LogWarning($"[PrototypeC] 布局回放跳过：{item.id} 没有任何可用模型缓存（可能换过风格或已清理），不重新调用 Meshy");
                    continue;
                }

                SetStatus($"回放布局 {done}/{total}：{item.objectName}…");

                ObjectSpec spec = new ObjectSpec
                {
                    objectName = string.IsNullOrWhiteSpace(item.objectName) ? item.id : item.objectName,
                    objectDescription = string.IsNullOrWhiteSpace(objectDescriptionOverride) ? item.objectName : objectDescriptionOverride.Trim(),
                    sizeDescription = sizeDescription,
                    style = style,
                    behavior = string.IsNullOrWhiteSpace(item.behavior) ? behavior : item.behavior,
                    placement = placement,
                    yawDegrees = item.yawDegrees,
                    size = size,
                    generation = effectiveGeneration,
                    sizeClass = item.sizeClass,
                    placementKind = RoomBands.ToPlacementKind(item.band),
                };
                spec.ComposePrompt();

                try
                {
                    GameObject instance = await service.GenerateOnlyAsync(spec);
                    if (instance != null && !service.PlaceInstance(instance, spec, item.intent))
                        Debug.LogWarning($"[PrototypeC] 布局回放未落位：{item.objectName}（{item.id}）｜{service.LastFailure}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PrototypeC] 布局回放失败：{item.id}｜{e.Message}");
                }
            }

            SetStatus($"布局回放完成：{done} 个");
        }

        private async Task GenerateOneAsync(PendingRequest request)
        {
            string input = request.input;
            ObjectSpec spec;
            string parseTag = string.Empty;

            if (request.parse != null)
            {
                // 打字机发送键已经判定过：直接复用同一份解析结果，不再请求 LLM
                ObjectParseResult parse = request.parse;
                spec = ObjectSpecFactory.Create(parse.definition, style, generation, yawDegrees, size, placement);
                parseTag = parse.definition.Describe() + (parse.fromDictionary ? "｜来源：物件字典" : "｜来源：LLM");
                SetStatus("AI 解析：" + parseTag);
                Debug.Log($"[PrototypeC] 复用发送键判定：{parseTag}｜objectName={parse.definition.objectName}");
            }
            else if (useAiParsing)
            {
                if (_parser == null) _parser = LlmObjectParser.CreateDefault();

                SetStatus($"AI 解析中：{input}…");
                ObjectParseResult parse = await _parser.ParseAsync(input);

                if (!parse.IsSuccess)
                {
                    string hint = parse.status == ObjectParseStatus.Unrecognized
                        ? "该输入不可识别，未生成模型（可关闭「AI 解析」改用手动模式）"
                        : "解析失败，未生成模型（可关闭「AI 解析」改用手动模式）";
                    SetStatus($"AI 解析：{parse.Describe()}｜{hint}");
                    Debug.LogWarning($"[PrototypeC] AI 解析未通过（{input}）：{parse.Describe()}\n原始输出：{parse.rawText}");
                    return;
                }

                spec = ObjectSpecFactory.Create(parse.definition, style, generation, yawDegrees, size, placement);
                parseTag = parse.definition.Describe() + (parse.fromDictionary ? "｜来源：物件字典" : "｜来源：LLM");
                SetStatus("AI 解析：" + parseTag);
                Debug.Log($"[PrototypeC] AI 解析：{parseTag}｜objectName={parse.definition.objectName}");
            }
            else
            {
                spec = new ObjectSpec
                {
                    objectName = input,
                    objectDescription = string.IsNullOrWhiteSpace(objectDescriptionOverride) ? input : objectDescriptionOverride.Trim(),
                    sizeDescription = sizeDescription,
                    style = style,
                    behavior = behavior,
                    placement = placement,
                    yawDegrees = yawDegrees,
                    size = size,
                    generation = generation,
                };
                spec.ComposePrompt();
            }

            if (!string.IsNullOrWhiteSpace(sizeClassOverride))
            {
                spec.sizeClass = sizeClassOverride.Trim();
                Debug.Log($"[PrototypeC] 尺寸档位手动覆盖：{spec.sizeClass}（面板 sizeClassOverride）");
            }

            // 两段式：先拿到模型与真实包围盒，再让 LLM 给布局意图，最后由求解器校验落位
            GameObject instance = await service.GenerateOnlyAsync(spec);
            if (instance != null)
            {
                LayoutIntent intent = await PlanLayoutAsync(instance, spec);
                SetStatus("布局中…");

                if (!service.PlaceInstance(instance, spec, intent))
                {
                    // 拒绝落位时状态栏保留 PlaceInstance 写下的原因，不要被「已生成」覆盖
                    SetStatus("未落位：" + service.LastFailure);
                    return;
                }

                Debug.Log($"[PrototypeC] 模型已就绪：{instance.name}");
                if (!string.IsNullOrEmpty(parseTag)) SetStatus("已生成｜" + parseTag);
            }
        }

        /// <summary>
        /// 模型已就绪、尺寸确定后求布局意图。任何失败都返回 null（PlaceInstance 会退回纯规则），
        /// 所以布局调用不会阻断生成；位置/朝向不进缓存指纹。
        /// </summary>
        private async Task<LayoutIntent> PlanLayoutAsync(GameObject instance, ObjectSpec spec)
        {
            if (!useLlmLayout || service == null) return null;

            if (service.roomLayout == null || service.occupancy == null || service.layoutSolver == null)
            {
                Debug.LogWarning("[PrototypeC] 布局：RoomLayout / RoomOccupancy / LayoutSolver 不完整，跳过 LLM 意图；" +
                                 "落位阶段会直接拒绝（不会退回旧槽位）");
                return null;
            }

            if (!service.TryMeasureLocalSize(instance, out Vector3 size))
            {
                Debug.LogWarning("[PrototypeC] 布局：量不到包围盒，改用规则求解");
                return null;
            }

            if (_layoutPlanner == null) _layoutPlanner = LlmLayoutPlanner.CreateDefault();

            SetStatus($"布局规划中：{spec.objectName}…");

            try
            {
                return await _layoutPlanner.PlanAsync(
                    spec, size, service.roomLayout, service.occupancy.Items, maxLayoutOccupants);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PrototypeC] 布局意图调用异常，改用规则求解：" + e.Message);
                return null;
            }
        }

        private void OnStatusChanged(ModelStatus status)
        {
            SetStatus(WithQueueSummary(status.message));
        }

        /// <summary>状态文本统一带上队列汇总，避免排队信息被单物件状态覆盖掉。</summary>
        private string WithQueueSummary(string message)
        {
            return _queue.Count > 0 ? $"{message}｜排队 {_queue.Count} 个" : message;
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }
    }
}
