// ObjectModelService.cs —— Prototype C：生成 / 下载 / 运行时加载 / 本地缓存 的编排层
// 对外只暴露两个能力：
//   GetOrCreateAsync(ObjectSpec) → GameObject（缓存命中优先，未命中才调用 Meshy）
//   StatusChanged 事件 → 供 UI 显示状态文本
// 同一规范化物件标识在请求进行中时复用同一个 Task，避免重复创建 Meshy 任务。

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PrototypeC
{
    public enum ModelRequestState
    {
        Idle,
        CheckingCache,
        CacheHit,
        Submitting,
        Generating,
        Downloading,
        Loading,
        Completed,
        Failed,
    }

    public struct ModelStatus
    {
        public ModelRequestState state;
        public string message;

        public ModelStatus(ModelRequestState state, string message)
        {
            this.state = state;
            this.message = message;
        }
    }

    public class ObjectModelService : MonoBehaviour
    {
        // Inspector 字段按「配置动线」排序：① 先核对 → ② 摆放规则 → ③ 尺寸 → ④ 物理
        //                              → ⑤ 生成/缓存/网络 → ⑥ 旧槽位（仅关求解器时）→ ⑦ 调试。
        // Unity 按字段名序列化，调换顺序不会丢已配好的值。

        [Header("① 先核对这几项")]
        [Tooltip("生成物挂载的父节点（房间锚点 PrototypeC_GeneratedAnchor）；留空则放在场景根下，所有局部坐标会失去基准")]
        public Transform spawnParent;

        [Tooltip("开启后用 RoomLayout + LayoutSolver 求位（推荐）；关闭则完全沿用旧的槽位排布，不做禁放区/重叠校验（回退开关）")]
        public bool useLayoutSolver = true;

        [Tooltip("地面带下沿（锚点局部 Y，米）：Ground 物件的包围盒底面贴在这里；0 ≈ 上层地板上表面，场景里实际是 0.28")]
        public float groundBandMinY = 0f;

        [Tooltip("房间几何描述（横向能放哪 / 哪段要留空 / 墙面 / 参考点）；留空自动从场景里找")]
        public RoomLayout roomLayout;

        [Tooltip("已落位物件占用表（容量 / 先放先淘汰 / layout.json）；留空自动从场景里找，没有时运行时补挂")]
        public RoomOccupancy occupancy;

        [Tooltip("落点求解器（位置冲突时逐级退让）；留空自动从场景里找，没有时运行时补挂")]
        public LayoutSolver layoutSolver;

        [Header("② 摆放规则（一般不用改）")]
        [Tooltip("墙面所在的 Z（锚点局部坐标，米）：默认 2.0 ≈ 世界 z 0.5，位于字符平面之后、遮挡底板之前")]
        public float wallPlaneZ = 2f;

        [Tooltip("允许挂墙的 X 左端（锚点局部坐标，米）；超出范围时夹紧")]
        public float wallXMin = -5.5f;

        [Tooltip("允许挂墙的 X 右端（锚点局部坐标，米）；超出范围时夹紧")]
        public float wallXMax = 5.5f;

        [Tooltip("挂墙时固定朝向（避免画背对房间）。取消勾选则沿用物件自己的 yawDegrees")]
        public bool overrideWallYaw = true;

        [Tooltip("挂墙朝向（绕 Y 轴的角度，180 = 面朝相机）")]
        public float wallYawDegrees = 180f;

        [Tooltip("地面带上沿（锚点局部 Y，米）；只用于标明带的范围，不参与校验")]
        public float groundBandMaxY = 0.6f;

        [Tooltip("墙带下沿（锚点局部 Y，米）：Wall 物件的垂直中心放在墙带中线")]
        public float wallBandMinY = 0.6f;

        [Tooltip("墙带上沿（锚点局部 Y，米）：挂画/壁饰大致在离地板 0.6~2.2 m 之间")]
        public float wallBandMaxY = 2.2f;

        [Tooltip("天花板带下沿（锚点局部 Y，米）；Rooftop 物件包围盒顶面贴上沿")]
        public float ceilingBandMinY = 2.8f;

        [Tooltip("天花板带上沿（锚点局部 Y，米）：默认 3.4 ≈ 离上层地板 3.4 m，低于场景天花板")]
        public float ceilingBandMaxY = 3.4f;

        [Header("③ 尺寸与缩放（一般不用改）")]
        [Tooltip("开启后按 size 档位（或兜底 size）的目标包围盒等比缩放模型（Meshy 输出尺寸不固定）")]
        public bool autoFitHeight = true;

        [Tooltip("关闭 autoFitHeight 时使用的固定缩放倍数")]
        public float defaultScale = 1f;

        [Tooltip("Small 档目标包围盒（宽 × 高 × 深，米）；模型按最长边等比缩放到这一档")]
        public Vector3 sizeSmall = new Vector3(0.35f, 0.45f, 0.35f);

        [Tooltip("Medium 档目标包围盒（宽 × 高 × 深，米）；模型按最长边等比缩放到这一档")]
        public Vector3 sizeMedium = new Vector3(0.65f, 0.90f, 0.60f);

        [Tooltip("Large 档目标包围盒（宽 × 高 × 深，米）；模型按最长边等比缩放到这一档")]
        public Vector3 sizeLarge = new Vector3(1.10f, 1.60f, 0.80f);

        [Tooltip("SuperLarge 档目标包围盒（宽 × 高 × 深，米）；最长边 2.4 m，缩完仍顶天花板时会再整体缩一次")]
        public Vector3 sizeSuperLarge = new Vector3(1.60f, 2.40f, 1.20f);

        [Tooltip("挂墙物件最大厚度（米）：Wall 类会压薄朝向轴（局部 Z），宽高不动，避免画突出一大块")]
        public float wallMaxThickness = 0.18f;

        [Header("④ 颜色纠偏（只改运行时材质，不进缓存、不改 Prompt）")]
        [Tooltip("生成物偏黄/褐/土色时，把色相推向冷色或鲜绿并提饱和，避免和木屋背景糊在一起。只改运行时材质，不写回 .glb、不进缓存指纹")]
        public WarmCorrectionSettings warmCorrection = WarmCorrectionSettings.Default;

        [Header("⑤ 物理兜底参数（仅当行为装配表缺格时使用；格子配了预制件就以预制件为准）")]
        [Tooltip("生成物质量（kg）：越大越难被恐龙推动")]
        public float touchMass = 2f;

        [Tooltip("线性阻尼：越大越快停下来（0 = 一直滑）")]
        public float touchLinearDamping = 1.5f;

        [Tooltip("角阻尼：越大越不容易持续打转")]
        public float touchAngularDamping = 4f;

        [Tooltip("锁定垂直移动（Y）：勾上后只能被横推、绝不会掉；取消则靠隐藏的地面碰撞体托住")]
        public bool touchLockY = false;

        [Tooltip("锁定旋转：勾上后推不翻，只会平移")]
        public bool touchFreezeRotation = false;

        [Tooltip("附加推力系数（米/秒）：恐龙碰到时额外给一点水平速度，0 = 只靠物理接触")]
        public float touchPushFactor = 0.8f;

        [Tooltip("恐龙碰撞箱的 X/Y 放大倍数：按恐龙自身精灵的世界包围盒缩放；调大可让接触更稳，调小可减少误碰")]
        public float dinoColliderScale = 1.2f;

        [Tooltip("恐龙碰撞箱的纵深（世界米，沿 Z）：恐龙在 z=0、生成物锚点 PrototypeC_GeneratedAnchor 在 z=-1.5，默认 2 米把两边一起罩住；过大可能碰到远处的物件")]
        public float dinoColliderDepth = 2f;

        [Tooltip("恐龙碰撞箱在 Z 上的中心偏移（世界米）：默认 -0.75 = 跨在恐龙（z 0）与生成物（z -1.5）中间；场景 Z 改了就把这里设成两者的中点")]
        public float dinoColliderZOffset = -0.75f;

        [Tooltip("Play 后自动生成「Touch 测试推手」（默认关闭，恐龙本体碰撞箱已可直接验证）；开启后测试期间会临时关掉恐龙碰撞箱，避免同一次接触加两次力")]
        public bool createTouchTestPusher = false;

        [Header("⑥ 行为装配表（behavior × placement → 预制件；缺格时用内置默认兜底并报错）")]
        [Tooltip("九格合法组合各自指向一个装配预制件；预制件上的 ObjectBehaviorProfile 决定碰撞体/刚体/晃动。留空 = 该格用内置默认")]
        public BehaviorAssemblyTable behaviorAssemblies = BehaviorAssemblyTable.CreateDefault();

        [Header("⑦ 生成、缓存与网络（一般不用改）")]
        [Tooltip("轮询 Meshy 任务的间隔（秒）；服务端返回 Retry-After 时优先使用它")]
        public float pollIntervalSeconds = 5f;

        [Tooltip("单个 Meshy 任务（preview / refine 各自）最长等待秒数")]
        public float taskTimeoutSeconds = 900f;

        [Tooltip("GLB 下载超时（秒）")]
        public int downloadTimeoutSeconds = 300;

        [Tooltip("生成成功后自动把本机缓存同步到 <工程根>/PrototypeC_Cache；本机缺失时也会先从这里播种")]
        public bool syncProjectMirror = true;

        [Tooltip("开启后在最终落点摆一块字符画「施工中」牌子；真模型加载成功后原位替换。缓存命中不会出现占位")]
        public bool showGenerationPlaceholder = true;

        [Tooltip("占位牌子的颜色（默认取场景里 B2_GapMark 的琥珀色，跟 B1 字符风格一致）")]
        public Color placeholderColor = new Color(1f, 0.85f, 0.3f, 0.95f);

        [Tooltip("前景辨识度：对生成物的基础色做确定性色相偏移（0 = 不偏移，0.15 = 轻微）。同一物件每次加载色调一致；只改运行时材质，不写回 .glb / 缓存")]
        [Range(0f, 0.4f)] public float foregroundTintStrength = 0.15f;

        [Header("⑧ 快速模式（默认关闭；只影响之后新生成的条目）")]
        [Tooltip("开启后用更快的几何/贴图模型与更低面数，把 2~3 分钟压到几十秒；缓存命中不受影响。关闭时行为与以前完全一致")]
        public bool fastGeneration = false;

        [Tooltip("快速模式的几何模型：meshy-t2（最快，自动切 smart-topology，约 4 秒出素模）／meshy-6-lite（标准模型里最快）／留空 = 沿用 GenerationParams.aiModel")]
        public string fastAiModel = "meshy-t2";

        [Tooltip("快速模式的目标面数：meshy-t2 上限 15000、meshy-t2.5 上限 30000、标准模型 100~300000")]
        public int fastTargetPolycount = 4000;

        [Tooltip("快速模式是否跳过 refine（只出素模、没有任何贴图，最快）。勾上后模型是纯色白模")]
        public bool fastPreviewOnly = false;

        [Tooltip("快速模式的贴图模型：meshy-6-lite（约 35 秒，文字 preview 可直接 refine）／meshy-6（更细但慢）／meshy-7·latest（最细）／meshy-n1（最快但必须另有参考图，见校验提示）／留空 = 沿用 preview 模型")]
        public string fastTextureModel = "meshy-6-lite";

        [Tooltip("快速模式贴图分辨率：2k / 4k / 8k；meshy-n1 只支持 2k，会自动纠正为 2k")]
        public string fastTextureResolution = "2k";

        [Tooltip("快速模式是否生成 PBR 贴图；meshy-n1 不支持，会自动关闭")]
        public bool fastEnablePbr = false;

        [Header("⑨ 旧槽位排布（只在关闭求解器时生效）")]
        [Tooltip("开启后新生成的物件依次摆进网格槽位；useLayoutSolver 开启时只影响「施工中」占位牌的临时位置")]
        public bool autoArrangeSlots = true;

        [Tooltip("每行槽位数")]
        public int slotsPerRow = 3;

        [Tooltip("同排槽位间距（米）")]
        public float slotSpacing = 0.8f;

        [Tooltip("换行时的纵深间距（米）")]
        public float slotRowSpacing = 0.6f;

        [Header("⑨-2 地面动态落位（抓娃娃机式投放）")]
        [Tooltip("地面物件求出随机落点后，从最终位置上方落下；关闭则直接原地出现（墙面/天花板物件不受影响）")]
        public bool dropFromAbove = true;

        [Tooltip("下落起始高度（米，相对最终落点）")]
        public float dropHeight = 2.2f;

        [Tooltip("下落重力加速度（米/秒²）")]
        public float dropGravity = 9.8f;

        [Tooltip("落地小回弹的速度保留比（0~1）：0 = 直接停住，0.12 左右有一次很小的弹跳")]
        [Range(0f, 0.6f)] public float dropBounce = 0.12f;

        [Header("⑩ 调试与 Scene 视图（仅编辑器，不参与运行时、不进缓存）")]
        [Tooltip("在 Scene 视图画出地面带 / 墙带 / 天花板带 / 墙面 / 槽位序号；区间配错时用红色提示")]
        public bool showPlacementGizmos = true;

        [Tooltip("地面带颜色")]
        public Color groundBandGizmoColor = new Color(0.30f, 0.90f, 0.45f);

        [Tooltip("墙带颜色（同时用于墙面片）")]
        public Color wallBandGizmoColor = new Color(0.30f, 0.65f, 1f);

        [Tooltip("天花板带颜色")]
        public Color ceilingBandGizmoColor = new Color(1f, 0.72f, 0.30f);

        [Tooltip("槽位小方块与序号文字颜色")]
        public Color slotGizmoColor = new Color(0.92f, 0.92f, 0.92f);

        [Tooltip("半透明填充的透明度（只影响 Scene 视图）")]
        [Range(0.02f, 0.6f)] public float gizmoFillAlpha = 0.15f;

        [Tooltip("冲突提示颜色（区间反转、带重叠时使用）")]
        public Color gizmoConflictColor = new Color(1f, 0.25f, 0.2f);

        public event Action<ModelStatus> StatusChanged;

        /// <summary>
        /// 物件完成落位并登记占用后触发（地面物件在落地那一刻，墙面/天花板物件在摆好那一刻）。
        /// 恐龙的新物件响应（DinoAgentB1）订阅它；参数是占用表里的最新记录。
        /// </summary>
        public static event Action<PlacedItem> ItemSettled;

        /// <summary>状态栏里的生成模式标记：只在真正调用 Meshy 的阶段出现，缓存命中不显示。</summary>
        private string GenerationModeTag => fastGeneration ? "［快速模式］" : string.Empty;

        public bool IsBusy { get; private set; }
        public string LastFailure { get; private set; } = string.Empty;
        public GameObject LastInstance { get; private set; }

        private readonly Dictionary<string, Task<GameObject>> _inFlight = new Dictionary<string, Task<GameObject>>();

        // 已生成但还没落位的占位牌：等 PlaceInstance 把真模型摆好的同一帧再撤掉，避免「牌子没了、模型还没到位」的空白
        private readonly Dictionary<string, MeshGenerationPlaceholder> _pendingPlaceholders =
            new Dictionary<string, MeshGenerationPlaceholder>();
        private int _placedCount;
        private Transform _touchFloor;
        private bool _dinoBodyReady;
        private TouchTestPusher _touchTestPusher;

        private void Awake()
        {
            ResolveLayoutReferences();

            Debug.Log($"[PrototypeC] 布局组件：RoomLayout {(roomLayout != null ? "已找到" : "缺失")}｜" +
                      $"RoomOccupancy {(occupancy != null ? "已找到" : "缺失")}｜" +
                      $"LayoutSolver {(layoutSolver != null ? "已找到" : "缺失")}｜" +
                      $"useLayoutSolver={useLayoutSolver}");
        }

        /// <summary>
        /// 一段式入口（兼容旧调用）：模型就绪后立刻按规则求位。
        /// 需要先拿真实包围盒再让 LLM 决定位置时，请用 GenerateOnlyAsync + PlaceInstance。
        /// </summary>
        public async Task<GameObject> GetOrCreateAsync(ObjectSpec spec, CancellationToken cancellationToken = default)
        {
            GameObject instance = await GenerateOnlyAsync(spec, cancellationToken);
            if (instance != null) PlaceInstance(instance, spec, null);
            return instance;
        }

        /// <summary>一段式入口（带布局意图）：模型就绪后按给定意图求位。</summary>
        public async Task<GameObject> GetOrCreateAsync(ObjectSpec spec, LayoutIntent intent, CancellationToken cancellationToken = default)
        {
            GameObject instance = await GenerateOnlyAsync(spec, cancellationToken);
            if (instance != null) PlaceInstance(instance, spec, intent);
            return instance;
        }

        /// <summary>
        /// 两段式的第一段：只负责生成 / 取缓存 / 运行时加载 / 按档位缩放，返回的实例还没有落位。
        /// 失败返回 null，原因在 LastFailure 与状态事件里。
        /// </summary>
        public async Task<GameObject> GenerateOnlyAsync(ObjectSpec spec, CancellationToken cancellationToken = default)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            string key = spec.NormalizedId;

            if (_inFlight.TryGetValue(key, out Task<GameObject> running))
            {
                Report(ModelRequestState.CheckingCache, $"已有 {key} 的请求在进行中，复用同一个请求…");
                return await running;
            }

            Task<GameObject> task = RunAsync(spec, key, cancellationToken);
            _inFlight[key] = task;
            try
            {
                return await task;
            }
            finally
            {
                _inFlight.Remove(key);
            }
        }

        private async Task<GameObject> RunAsync(ObjectSpec spec, string key, CancellationToken ct)
        {
            IsBusy = true;
            LastFailure = string.Empty;

            MeshGenerationPlaceholder placeholder = null;
            bool succeeded = false;

            try
            {
                // 快速模式 + 风格指纹统一在这里归一化：缓存查询与真正提交 Meshy 必须用同一份参数，
                // 否则会出现「生成用快速档、查缓存用标准档」的错位（布局回放也会全部跳过）。
                // 旧场景里序列化的字段可能和代码默认值不一致（例如 fastTextureModel 还是 meshy-n1），
                // 这里一并做自动纠正，并把纠正后的参数写回 spec.generation —— 后面的缓存指纹、metadata
                // 都用这份「实际提交的参数」，不会拿配置里的旧值算指纹。
                var corrections = new List<string>();
                GenerationParams generation = ResolveEffectiveGeneration(spec.generation, spec.style, corrections);
                spec.generation = generation;

                string correctionNote = corrections.Count > 0
                    ? "已自动纠正：" + string.Join("；", corrections) + "｜"
                    : string.Empty;
                if (corrections.Count > 0)
                {
                    Debug.LogWarning($"[PrototypeC] 生成参数已自动纠正：{spec.objectName}（{key}）｜" +
                                     $"{string.Join("；", corrections)}｜实际使用 {DescribeGeneration(generation)}");
                }

                // 风格层是 Prompt 的唯一来源；风格指纹已在上面的归一化里补上
                if (string.IsNullOrWhiteSpace(spec.prompt)) spec.ComposePrompt();

                Report(ModelRequestState.CheckingCache, $"{correctionNote}正在检查本地缓存（{key}）…");

                // 缓存别名：把 plant 这类 id 指向另一个已缓存条目（例如吊兰）。
                // 优先级：别名「精确档」立即用；别名只有「另一档」时先挂起，等自己的档查完再决定，
                // 避免出现 plant 自己明明有 12:34 的快速档、却用了 bracket_plant 的标准档。
                string aliasTarget = ObjectCache.ResolveAlias(key);
                ObjectCache.CacheEntry aliasFallbackEntry = null;
                string aliasFallbackPath = null;
                if (!string.IsNullOrEmpty(aliasTarget)
                    && !string.Equals(aliasTarget, key, StringComparison.OrdinalIgnoreCase))
                {
                    Report(ModelRequestState.CheckingCache, $"别名：{key} → {aliasTarget}，按目标条目查缓存…");

                    ObjectCache.CacheHitKind aliasHit =
                        ObjectCache.TryGet(aliasTarget, generation, out ObjectCache.CacheEntry aliasCached);
                    if (aliasHit == ObjectCache.CacheHitKind.Exact)
                    {
                        Report(ModelRequestState.CacheHit,
                            $"别名命中：{key} → {aliasTarget}（缓存时间 {aliasCached.metadata.createdAtUtc}，档 {aliasCached.tier}）");
                        GameObject aliased = await LoadModelAsync(aliasCached.modelPath, spec, key, ct);
                        Report(ModelRequestState.Completed,
                            $"完成（别名缓存）：{spec.objectName}｜{key} → {aliasTarget}");
                        return aliased;
                    }

                    if (aliasHit == ObjectCache.CacheHitKind.Fallback)
                    {
                        // 先挂起：自己的档更贴近输入，等下面查完自己的档再决定用哪个
                        aliasFallbackEntry = aliasCached;
                    }
                    else
                    {
                        string aliasSeedReason = syncProjectMirror ? string.Empty : "工程镜像同步已关闭";
                        if (syncProjectMirror
                            && ObjectCache.TrySeedFromProjectMirror(aliasTarget, generation, out ObjectMetadata aliasSeeded,
                                out string aliasSeededPath, out bool aliasSeedExact, out aliasSeedReason))
                        {
                            if (aliasSeedExact)
                            {
                                Report(ModelRequestState.CacheHit, $"别名命中（来自工程镜像，同档）：{key} → {aliasTarget}");
                                GameObject aliased = await LoadModelAsync(aliasSeededPath, spec, key, ct);
                                Report(ModelRequestState.Completed,
                                    $"完成（别名 + 工程镜像播种）：{spec.objectName}｜{key} → {aliasTarget}｜同档");
                                return aliased;
                            }

                            // 镜像里也只有「另一档」：同样挂起，让自己的档先有机会
                            aliasFallbackPath = aliasSeededPath;
                        }
                        else
                        {
                            Debug.LogWarning($"[PrototypeC] 别名目标不可用：{key} → {aliasTarget}｜{aliasSeedReason}，回退到按 {key} 自己处理");
                        }
                    }
                }

                ObjectCache.CacheHitKind hitKind = ObjectCache.TryGet(key, generation, out ObjectCache.CacheEntry cached);
                if (hitKind != ObjectCache.CacheHitKind.Miss)
                {
                    if (aliasFallbackEntry != null || !string.IsNullOrEmpty(aliasFallbackPath))
                    {
                        Debug.Log($"[PrototypeC] 自己的缓存优先于别名回退：{key}｜自己的档 {ObjectCache.DescribeTier(cached)}｜" +
                                  $"忽略 {aliasTarget} 的另一档缓存");
                    }

                    string fallbackNote = hitKind == ObjectCache.CacheHitKind.Fallback
                        ? $"｜当前显示的是另一档参数生成的模型（{ObjectCache.DescribeTier(cached)}）"
                        : string.Empty;
                    Report(ModelRequestState.CacheHit,
                        $"缓存命中：{key}（缓存时间 {cached.metadata.createdAtUtc}，档 {cached.tier}）{fallbackNote}");
                    GameObject cachedInstance = await LoadModelAsync(cached.modelPath, spec, key, ct);
                    Report(ModelRequestState.Completed,
                        $"完成（缓存命中）：{spec.objectName}｜{cached.modelPath}{fallbackNote}");
                    return cachedInstance;
                }

                // 本机缓存缺失：先尝试从工程镜像播种，命中同样不调用 Meshy（没有 API Key 也能用）
                if (syncProjectMirror)
                {
                    Report(ModelRequestState.CheckingCache, $"本机缓存缺失（{key}），尝试从工程镜像播种…");
                    if (ObjectCache.TrySeedFromProjectMirror(key, generation, out ObjectMetadata seeded,
                            out string seededPath, out bool seedExact, out string seedReason))
                    {
                        string seedNote = seedExact ? "同档" : "另一档参数生成的模型";
                        Report(ModelRequestState.CacheHit, $"缓存命中（来自工程镜像，{seedNote}）：{key}，已播种到本机缓存");
                        GameObject seededInstance = await LoadModelAsync(seededPath, spec, key, ct);
                        Report(ModelRequestState.Completed,
                            $"完成（工程镜像播种）：{spec.objectName}｜本机 {seededPath}｜{seedNote}");
                        return seededInstance;
                    }

                    Debug.Log($"[PrototypeC] 工程镜像未命中：{seedReason}");
                }

                // 自己的档（本机 + 工程镜像）都没有，才回头用别名的「另一档」缓存，
                // 这样别名回退只是兜底，不会抢在自己精确档前面。
                if (aliasFallbackEntry != null)
                {
                    string aliasNote = $"｜当前显示的是另一档参数生成的模型（{ObjectCache.DescribeTier(aliasFallbackEntry)}）";
                    Report(ModelRequestState.CacheHit,
                        $"别名回退命中：{key} → {aliasTarget}（缓存时间 {aliasFallbackEntry.metadata.createdAtUtc}，档 {aliasFallbackEntry.tier}）{aliasNote}");
                    GameObject aliased = await LoadModelAsync(aliasFallbackEntry.modelPath, spec, key, ct);
                    Report(ModelRequestState.Completed,
                        $"完成（别名回退缓存）：{spec.objectName}｜{key} → {aliasTarget}{aliasNote}");
                    return aliased;
                }

                if (!string.IsNullOrEmpty(aliasFallbackPath))
                {
                    const string aliasNote = "｜当前显示的是另一档参数生成的模型";
                    Report(ModelRequestState.CacheHit, $"别名回退命中（来自工程镜像）：{key} → {aliasTarget}{aliasNote}");
                    GameObject aliased = await LoadModelAsync(aliasFallbackPath, spec, key, ct);
                    Report(ModelRequestState.Completed,
                        $"完成（别名回退 + 工程镜像播种）：{spec.objectName}｜{key} → {aliasTarget}{aliasNote}");
                    return aliased;
                }

                if (!MeshyConfig.HasApiKey)
                {
                    return Fail(
                        $"未配置 Meshy API Key（当前来源：{MeshyConfig.Source}）。" +
                        $"请设置环境变量 {MeshyConfig.EnvironmentVariable}，" +
                        $"或写入 {MeshyConfig.ProjectRootConfigPath} ／ {MeshyConfig.PersistentConfigPath}，" +
                        "格式 {\"apiKey\":\"msy-...\"}（修改后重启 Play）");
                }

                // 提交前先做参数兼容性校验：像 meshy-n1 缺少参考图这种错误，
                // 必须在花时间跑完几何 preview 之前拦下，而不是等 refine 阶段吃 400。
                string generationError = ValidateGeneration(
                    generation,
                    spec.style != null ? spec.style.texturePrompt : string.Empty);
                if (!string.IsNullOrEmpty(generationError))
                {
                    string message = $"{generationError}（未提交任何 Meshy 任务，没有消耗额度）｜" +
                                     $"当前参数 {DescribeGeneration(generation)}";
                    LastFailure = message;
                    Debug.LogError($"[PrototypeC] 生成参数不兼容：{spec.objectName}（{key}）｜{generationError}");
                    Report(ModelRequestState.Failed, message);
                    return null;
                }

                // 确认缓存未命中 + 有 API Key：先摆一块「施工中」占位牌（槽位序号只是占位牌的临时位置，真模型由求解器落位）
                int slotIndex = autoArrangeSlots ? _placedCount : -1;
                placeholder = SpawnPlaceholder(spec, slotIndex);

                var client = new MeshyClient(MeshyConfig.ApiKey)
                {
                    PollIntervalSeconds = pollIntervalSeconds,
                    TimeoutSeconds = taskTimeoutSeconds,
                    DownloadTimeoutSeconds = downloadTimeoutSeconds,
                };

                Report(ModelRequestState.Submitting, $"{GenerationModeTag}正在提交生成任务（{DescribeGeneration(generation)}）…");
                string previewTaskId = await client.CreatePreviewTaskAsync(spec.prompt, generation, ct);
                Debug.Log($"[PrototypeC] preview 任务已创建：{previewTaskId}");

                Report(ModelRequestState.Generating, $"{GenerationModeTag}正在生成模型（preview 0%）");
                MeshyTaskState preview = await client.WaitForTaskAsync(
                    previewTaskId,
                    "模型生成（preview）",
                    s =>
                    {
                        Report(ModelRequestState.Generating, $"{GenerationModeTag}正在生成模型（preview {s.progress}%）");
                        if (placeholder != null) placeholder.SetPercent(s.progress);
                    },
                    ct);

                string glbUrl = preview.glbUrl;
                string refineTaskId = string.Empty;

                if (generation.enableTexture)
                {
                    string textureLabel = string.IsNullOrEmpty(generation.textureModel)
                        ? "沿用 preview 模型"
                        : generation.textureModel;
                    Report(ModelRequestState.Submitting,
                        $"{GenerationModeTag}正在提交贴图任务（{textureLabel} @ {generation.textureResolution}）…");
                    refineTaskId = await client.CreateRefineTaskAsync(
                        previewTaskId, generation, spec.style != null ? spec.style.texturePrompt : string.Empty, ct);
                    Debug.Log($"[PrototypeC] refine 任务已创建：{refineTaskId}");

                    Report(ModelRequestState.Generating, $"{GenerationModeTag}正在生成模型（贴图 0%）");
                    MeshyTaskState refine = await client.WaitForTaskAsync(
                        refineTaskId,
                        "模型生成（refine）",
                        s =>
                        {
                            Report(ModelRequestState.Generating, $"{GenerationModeTag}正在生成模型（贴图 {s.progress}%）");
                            if (placeholder != null) placeholder.SetPercent(s.progress);
                        },
                        ct);

                    if (!string.IsNullOrWhiteSpace(refine.glbUrl)) glbUrl = refine.glbUrl;
                }

                if (string.IsNullOrWhiteSpace(glbUrl)) return Fail("Meshy 任务成功但没有返回 GLB 下载地址");

                Report(ModelRequestState.Downloading, "正在下载模型…");
                string tempPath = Path.Combine(
                    Application.temporaryCachePath,
                    $"meshy_{key}_{DateTime.UtcNow.Ticks}.glb");

                await client.DownloadAsync(glbUrl, tempPath, ct);

                var metadata = new ObjectMetadata
                {
                    objectName = spec.objectName,
                    prompt = spec.prompt,
                    behavior = spec.behavior,
                    placementKind = spec.placementKind,
                    sizeClass = spec.sizeClass,
                    placement = spec.placement,
                    yawDegrees = spec.yawDegrees,
                    size = spec.size,
                    aiModel = generation.aiModel,
                    targetPolycount = generation.targetPolycount,
                    shouldRemesh = generation.shouldRemesh,
                    enableTexture = generation.enableTexture,
                    textureResolution = generation.textureResolution,
                    modelType = generation.modelType,
                    textureModel = generation.textureModel,
                    enablePbr = generation.enablePbr,
                    generationFingerprint = generation.ToCacheFingerprint(),
                    previewTaskId = previewTaskId,
                    refineTaskId = refineTaskId,
                    styleId = spec.style != null ? spec.style.styleId : string.Empty,
                    styleVersion = spec.style != null ? spec.style.styleVersion : 0,
                    styleFingerprint = generation.styleFingerprint,
                    promptTrimmed = spec.promptTrimmed,
                    promptTrimNote = spec.promptTrimNote,
                };

                string savedFolder = ObjectCache.Save(key, tempPath, metadata);
                TryDelete(tempPath);
                string savedModelPath = Path.Combine(savedFolder, ObjectCache.ModelFileName);
                Debug.Log($"[PrototypeC] 模型已缓存：{savedModelPath}（档 {ObjectCache.GetTierKey(generation)}）");

                TrySyncProjectMirror(key);

                Report(ModelRequestState.Loading, "正在加载模型…");
                // 只加载模型；真正落位与占位牌替换由调用方（GetOrCreateAsync / PlaceInstance）完成
                GameObject instance = await LoadModelAsync(savedModelPath, spec, key, ct);
                succeeded = true;
                Report(ModelRequestState.Completed,
                    $"{correctionNote}完成：{spec.objectName}｜{DescribeGeneration(generation)}｜缓存目录 {ObjectCache.GetFolder(key)}");
                return instance;
            }
            catch (OperationCanceledException)
            {
                return Fail("请求已取消");
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
            finally
            {
                IsBusy = false;

                // 占位收尾：成功 → 先留着，等 PlaceInstance 把真模型摆好的同一帧再撤（避免中间出现空白帧）；
                // 失败 / 超时 / 取消 → 转成 FAILED 后自毁
                if (placeholder != null)
                {
                    if (succeeded)
                    {
                        MeshGenerationPlaceholder previous;
                        if (_pendingPlaceholders.TryGetValue(key, out previous) && previous != null) previous.Dispose();
                        _pendingPlaceholders[key] = placeholder;
                    }
                    else
                    {
                        placeholder.Fail();
                    }
                }
            }
        }

        /// <summary>用 glTFast 运行时加载本地 GLB，并按尺寸档位缩放；落位由 PlaceInstance 单独完成。</summary>
        private async Task<GameObject> LoadModelAsync(
            string glbPath, ObjectSpec spec, string key, CancellationToken ct)
        {
            if (!File.Exists(glbPath)) throw new MeshyException($"本地 GLB 不存在：{glbPath}");

            byte[] data = File.ReadAllBytes(glbPath);

            // 编辑器（非 Play）环境下 glTFast 默认的分帧代理会调用 DontDestroyOnLoad 而报错，
            // 这里改用不等待分帧的代理，只为让同一套加载代码在编辑器里也能验证。
            // 材质生成器换成 toon 装饰器：GLB 材质自动映射到 PrototypeC/Toon 并保留 albedo 贴图。
            // 风格档按挂载父节点自动判定：GeneratedAnchor 之下 = 前景（AI 生成物）。
            ToonLayer toonLayer = ToonMaterials.ResolveLayer(spawnParent != null ? spawnParent.gameObject : null);
            GltfImport gltf = Application.isPlaying
                ? new GltfImport(null, null, ToonMaterialGenerator.Create(toonLayer))
                : new GltfImport(null, new ImmediateDeferAgent(), ToonMaterialGenerator.Create(toonLayer));

            bool loaded = await gltf.Load(data, null, null, ct);
            if (!loaded) throw new MeshyException("GLB 解析失败（glTFast 返回 false），文件可能损坏");

            var root = new GameObject($"Object_{key}");

            // 落位完成前绝不渲染：整个节点先保持未激活，加载 / 缩放 / 上材质期间一帧都不会出现在原点或旧位置。
            root.SetActive(false);
            try
            {
                root.transform.SetParent(spawnParent, false);

                var instantiator = new GameObjectInstantiator(gltf, root.transform);
                bool instantiated = await gltf.InstantiateMainSceneAsync(instantiator, ct);
                if (!instantiated) throw new MeshyException("GLB 场景实例化失败");

                // 只做「归零变换 + 按档位缩放」；位置/朝向/占用登记在 PlaceInstance 里完成
                PrepareModel(root, spec);
                // 渲染期色相微调：只改刚实例化出来的运行时材质，不写回 .glb / 缓存，不影响缓存命中
                ToonMaterials.ApplyStableTint(root, key, foregroundTintStrength);
                // 黄/褐/土色中性化：同样只作用于运行时材质，参数与 id 决定，同 id 每次加载颜色一致
                ToonMaterials.ApplyWarmCorrection(root, key, warmCorrection);

                // 先把所有 Renderer 的渲染关掉再激活：
                // 这样 Awake/OnEnable 与包围盒测量都正常（不依赖「未激活」的隐式行为），
                // 但画面里要等 PlaceInstance 结束时才一次性出现，不会先闪一下再瞬移。
                SetRenderingEnabled(root, false);
                root.SetActive(true);

                LastInstance = root;
                return root;
            }
            catch
            {
                DestroyObject(root);
                throw;
            }
        }

        private enum PlacementZone
        {
            Ground,
            Wall,
            Rooftop,
        }

        /// <summary>LLM 只给枚举：Ground / Wall / Rooftop；空值或未知按 Ground 处理。</summary>
        private static PlacementZone ResolveZone(ObjectSpec spec)
        {
            if (string.Equals(spec.placementKind, "Wall", StringComparison.OrdinalIgnoreCase)) return PlacementZone.Wall;
            if (string.Equals(spec.placementKind, "Rooftop", StringComparison.OrdinalIgnoreCase)) return PlacementZone.Rooftop;
            return PlacementZone.Ground;
        }

        private Vector3 ToWorld(Vector3 local)
        {
            return spawnParent != null ? spawnParent.TransformPoint(local) : local;
        }

        private float ToWorldY(float localY)
        {
            return ToWorld(new Vector3(0f, localY, 0f)).y;
        }

        private static Vector3 SlotLocalOffset(int index, int columns, float spacing, float rowSpacing)
        {
            int safeColumns = Mathf.Max(1, columns);
            int column = index % safeColumns;
            int row = index / safeColumns;
            float x = (column - (safeColumns - 1) * 0.5f) * spacing;
            return new Vector3(x, 0f, row * rowSpacing);
        }

        /// <summary>
        /// 锚点局部 X/Z：placement 给基准，槽位给排布。
        /// placement 主管 Y 带（Ground/Wall/Rooftop），X/Z 仍走槽位；Wall 只保留槽位 X（Z 由贴墙决定），并把 X 夹进挂墙范围。
        /// </summary>
        private Vector3 AnchorLocalXZ(ObjectSpec spec, int slotIndex, PlacementZone zone)
        {
            Vector3 local = spec.placement;
            float x = local.x;
            float z = local.z;

            if (autoArrangeSlots && slotIndex >= 0)
            {
                Vector3 slot = SlotLocalOffset(slotIndex, slotsPerRow, slotSpacing, slotRowSpacing);
                x += slot.x;
                z += slot.z;
            }

            if (zone == PlacementZone.Wall)
            {
                x = Mathf.Clamp(x, wallXMin, wallXMax);
                z = wallPlaneZ;
            }

            return new Vector3(x, 0f, z);
        }

        private float EffectiveYaw(ObjectSpec spec)
        {
            return ResolveZone(spec) == PlacementZone.Wall && overrideWallYaw ? wallYawDegrees : spec.yawDegrees;
        }

        /// <summary>模型加载后、求位之前：归零变换并按尺寸档位缩放（此时还没有位置）。</summary>
        private void PrepareModel(GameObject root, ObjectSpec spec)
        {
            PlacementZone zone = ResolveZone(spec);

            root.transform.localScale = Vector3.one;
            // 量尺寸前先把旋转归零：按模型自身的宽/高/深缩放，而不是世界 AABB（yaw=90° 时 x/z 会互换）
            root.transform.localRotation = Quaternion.identity;
            root.transform.position = ToWorld(Vector3.zero);

            ApplySize(root, spec, zone);
        }

        /// <summary>
        /// 编辑器工具用：把刚加载的 GLB 整理成和运行时一致的外观（档位缩放 + toon + 色调纠偏），
        /// 但不落位、不登记占用、不写 layout.json。
        /// </summary>
        public void DressEditorInstance(GameObject root, ObjectSpec spec)
        {
            if (root == null || spec == null) return;

            PrepareModel(root, spec);
            ToonMaterials.ApplyStableTint(root, spec.NormalizedId, foregroundTintStrength);
            ToonMaterials.ApplyWarmCorrection(root, spec.NormalizedId, warmCorrection);
        }

        /// <summary>
        /// 两段式的第二段：拿到真实包围盒后求位、落地、登记占用。
        /// intent 为 null 时走纯规则（主区域 / 墙面 + 网格兜底）；有意图时先按意图求位，冲突再阶梯兜底。
        /// 返回 false 表示拒绝落位：禁放区/间距校验没有通过，或布局组件缺失——此时实例会被销毁，
        /// 但模型缓存保留，补齐配置后再次生成即可命中缓存。
        /// </summary>
        public bool PlaceInstance(GameObject instance, ObjectSpec spec, LayoutIntent intent = null)
        {
            if (instance == null || spec == null) return false;

            PlacementZone zone = ResolveZone(spec);

            // 朝向：挂墙且开启 overrideWallYaw 时固定面朝房间；其余由求解器「落点确定后再推导」
            // （背靠更近的侧墙、正面朝房间中央），可用 LayoutSolver.yawOverrides 按 id 覆盖。
            float wallYaw = zone == PlacementZone.Wall && overrideWallYaw ? wallYawDegrees : float.NaN;
            float yaw = intent != null && !(zone == PlacementZone.Wall && overrideWallYaw)
                ? intent.yawDegrees
                : EffectiveYaw(spec);
            string facing = string.Empty;

            // 求解器要「未旋转」的尺寸（它自己按 yaw 投影），所以先量再转
            if (!TryGetBounds(instance, out Bounds preRotation))
                return RejectPlacement(instance, spec, "量不到模型包围盒，无法校验占地");

            Vector3 localSize = ToLocalSize(preRotation.size);

            // 旋转与位置都是引擎侧摆放，不进 Prompt、不进缓存指纹，所以改朝向/分区不会让已有缓存失效。
            LayoutIntent effective = intent != null ? intent.Clone() : null;
            if (effective != null) effective.yawDegrees = yaw;

            Vector3 localXZ;
            if (useLayoutSolver)
            {
                // 严格模式：三件套缺一不可。缺了就明确拒绝，绝不退回旧槽位把物件硬塞进房间。
                ResolveLayoutReferences();
                string missing = MissingLayoutComponents();
                if (!string.IsNullOrEmpty(missing))
                    return RejectPlacement(instance, spec, $"缺少 {missing}，禁放区与重叠校验不可用");

                // 地面物件优先随机落位（抓娃娃机式）；随机采不到合法点时回退确定性阶梯，仍不行才拒绝
                bool haveSolution = false;
                LayoutSolution solution = default;
                if (zone == PlacementZone.Ground && layoutSolver.randomGroundPlacement)
                {
                    solution = layoutSolver.SolveRandomGround(spec, localSize);
                    haveSolution = solution.success;
                    if (!haveSolution)
                    {
                        Debug.LogWarning($"[PrototypeC] 布局：{spec.objectName} 随机落位未命中" +
                                         $"（{solution.reason}），改用确定性求解");
                    }
                }

                if (!haveSolution)
                    solution = layoutSolver.Solve(spec, localSize, effective, wallYaw);
                if (!solution.success)
                {
                    return RejectPlacement(instance, spec,
                        $"空间不足，没有满足禁放区与间距要求的落点（{solution.reason}）");
                }

                localXZ = solution.localXZ;
                yaw = solution.yawDegrees;
                facing = solution.facing;

                string detail = solution.degraded
                    ? $"降级：{solution.reason}"
                    : $"来源 {solution.reason}";
                if (!string.Equals(solution.reason, "random"))
                {
                    Debug.Log($"[PrototypeC] 布局：{spec.objectName}（{spec.NormalizedId}）｜{RoomBands.ToText(solution.band)}｜" +
                              $"锚点 {solution.anchorId}｜横向 X {localXZ.x:0.00}（depth {localXZ.z:0.00}）｜" +
                              $"yaw {yaw:0}｜朝向 {Facings.ToText(facing)}｜{detail}");
                }
            }
            else
            {
                // 仅在显式关闭求解器时使用：旧槽位排布，不做禁放区/重叠校验
                int slot = autoArrangeSlots ? _placedCount++ : -1;
                localXZ = AnchorLocalXZ(spec, slot, zone);
                Debug.LogWarning($"[PrototypeC] 布局：{spec.objectName}（{spec.NormalizedId}）｜已关闭求解器，" +
                                 $"按旧槽位落位，未做禁放区/重叠校验｜横向 X {localXZ.x:0.00}（depth {localXZ.z:0.00}）");
            }

            if (effective != null) effective.yawDegrees = yaw;
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.position = ToWorld(localXZ);

            // 分区对齐 + pivot 补偿：先按包围盒算目标中心，再整体平移，保证旋转/缩放后仍落在带内。
            //   Ground  → 底面贴地面带下沿
            //   Wall    → 垂直居中于墙带，背面贴墙面（Z），朝向由 yaw 决定
            //   Rooftop → 顶面贴天花板带上沿（默认不翻转模型，避免倒挂；需要"尖朝下"请自行改 yaw/模型）
            if (TryGetBounds(instance, out Bounds fitted))
            {
                Vector3 desiredCenter;
                switch (zone)
                {
                    case PlacementZone.Wall:
                        float planeZ = ToWorld(new Vector3(0f, 0f, wallPlaneZ)).z;
                        desiredCenter = new Vector3(
                            fitted.center.x,
                            ToWorldY((wallBandMinY + wallBandMaxY) * 0.5f),
                            planeZ - fitted.extents.z);
                        break;

                    case PlacementZone.Rooftop:
                        desiredCenter = new Vector3(
                            fitted.center.x,
                            ToWorldY(ceilingBandMaxY) - fitted.extents.y,
                            fitted.center.z);
                        break;

                    default:
                        desiredCenter = new Vector3(
                            fitted.center.x,
                            ToWorldY(groundBandMinY) + fitted.extents.y,
                            fitted.center.z);
                        break;
                }

                instance.transform.position += desiredCenter - fitted.center;
            }

            // 牌子跟到真实落点：旧槽位/随机落位解出来的位置可能完全不同，
            // 所以等模型的最终 transform.position 确定后，牌子瞬移过来对齐
            if (_pendingPlaceholders.TryGetValue(spec.NormalizedId, out MeshGenerationPlaceholder ph) && ph != null)
                ph.SetAnchorPosition(instance.transform.position, yaw, localSize);

            // 地面动态落位：先预登记占用（下落期间占住这段横向位置），模型从最终落点上方掉下来，
            // 装配（碰撞体/刚体）与真实占用登记推迟到落地回调——下落中没有任何物理组件，不会被误推。
            if (zone == PlacementZone.Ground && dropFromAbove)
            {
                ReserveGroundFootprint(spec, localXZ.x, localXZ.z, localSize, yaw);

                Vector3 finalPos = instance.transform.position;
                instance.transform.position = finalPos + Vector3.up * Mathf.Max(0.3f, dropHeight);
                var drop = instance.AddComponent<ObjectDropIn>();
                drop.Init(finalPos.y, dropGravity, dropBounce,
                    () => FinishGroundPlacement(instance, spec, yaw, effective));

                LastInstance = instance;
                SetRenderingEnabled(instance, true);
                // ReleasePlaceholder 不在这调：牌子要继续在最终落点显示进度条，
                // 等 FinishGroundPlacement（落地回调）里 FinishPlacement 之后再交接
                Debug.Log($"[PrototypeC] 动态落位：{spec.objectName}（{spec.NormalizedId}）" +
                          $"从上方 {Mathf.Max(0.3f, dropHeight):F1} 米落向 x={localXZ.x:0.00}（落地后登记真实占用）");
                return true;
            }

            // 墙面 / 天花板 / 关闭下落：直接装配 + 登记（原有路径）
            FinishPlacement(instance, spec, zone, yaw, effective);
            LastInstance = instance;

            // 落位完成：同一帧里「把模型显出来 + 撤掉占位牌」，玩家看到的是从牌子直接变成已就位的模型
            SetRenderingEnabled(instance, true);
            ReleasePlaceholder(spec, true);
            return true;
        }

        /// <summary>装配（碰撞体/刚体/晃动）+ 登记真实占用 + 广播「物件已落定」。</summary>
        private void FinishPlacement(GameObject instance, ObjectSpec spec, PlacementZone zone, float yaw,
            LayoutIntent intent)
        {
            // behavior × placement → 查表装配（碰撞体 / 刚体 / 晃动），只改运行时实例，不影响缓存
            BehaviorAssemblyResult assembly = ApplyBehaviorAssembly(instance, spec, zone);
            if (assembly.pushable)
            {
                EnsureDinoBodyCollider();
                EnsureTouchTestPusher(instance);
            }

            if (assembly.needsSupportFloor) EnsureTouchFloor();
            RegisterOccupancy(instance, spec, zone, yaw, intent, assembly.pushable);
            NotifyItemSettled(spec);
        }

        /// <summary>地面物件落地回调：完成装配与真实占用登记；下落途中被销毁则释放预留位。</summary>
        private void FinishGroundPlacement(GameObject instance, ObjectSpec spec, float yaw, LayoutIntent intent)
        {
            if (instance == null)
            {
                if (occupancy != null) occupancy.Unregister(spec.NormalizedId);
                Debug.LogWarning($"[PrototypeC] 动态落位：{spec.objectName} 下落途中被销毁，预留位已释放");
                return;
            }

            FinishPlacement(instance, spec, PlacementZone.Ground, yaw, intent);
            // 真模型已落地装配，牌子在同一帧交接销毁
            ReleasePlaceholder(spec, true);
        }

        /// <summary>下落期间先把横向足迹登记进占用表（按旋转投影宽度估算），连续生成不会选到同一片空位。</summary>
        private void ReserveGroundFootprint(ObjectSpec spec, float localX, float localZ, Vector3 localSize, float yaw)
        {
            if (occupancy == null) return;

            LayoutSolver.RotatedExtents(localSize, yaw, out float halfX, out float halfZ);
            Vector3 size = new Vector3(halfX * 2f, localSize.y, halfZ * 2f);
            Vector3 center = new Vector3(localX, groundBandMinY + localSize.y * 0.5f, localZ);
            occupancy.Register(null, spec.NormalizedId, spec.objectName, RoomBand.Ground, center, size, yaw,
                false, null, spec.behavior, spec.sizeClass, null);
        }

        /// <summary>广播 ItemSettled：读占用表拿最新记录（含 instance / behavior），供恐龙等订阅方响应。</summary>
        private void NotifyItemSettled(ObjectSpec spec)
        {
            if (occupancy == null || spec == null) return;
            if (occupancy.TryGetItem(spec.NormalizedId, out PlacedItem placed))
                ItemSettled?.Invoke(placed);
        }

        /// <summary>拒绝落位：销毁未校验的实例（模型缓存保留），并把原因同时写进状态栏与日志。</summary>
        private bool RejectPlacement(GameObject instance, ObjectSpec spec, string reason)
        {
            string message = $"未落位：{reason}｜模型已缓存，修好配置后再次生成即可命中缓存";
            Debug.LogError($"[PrototypeC] 未落位：{spec.objectName}（{spec.NormalizedId}）｜{reason}");
            LastFailure = reason;
            Report(ModelRequestState.Failed, message);
            DestroyObject(instance);
            ReleasePlaceholder(spec, false);
            return false;
        }

        /// <summary>重新解析布局组件（允许在 Play 中补挂组件后立刻生效）。</summary>
        private void ResolveLayoutReferences()
        {
            if (roomLayout == null) roomLayout = FindFirstObjectByType<RoomLayout>();
            if (occupancy == null) occupancy = FindFirstObjectByType<RoomOccupancy>();
            if (layoutSolver == null) layoutSolver = FindFirstObjectByType<LayoutSolver>();

            // RoomLayout 是房间几何的唯一事实来源；有它才补挂另外两个纯规则组件。
            // 没有 RoomLayout 时不做任何猜测：宁可拒绝落位，也不按默认矩形硬塞。
            if (useLayoutSolver && roomLayout != null)
            {
                if (occupancy == null)
                {
                    occupancy = gameObject.AddComponent<RoomOccupancy>();
                    Debug.LogWarning($"[PrototypeC] 场景里没有 RoomOccupancy，已在运行时补挂（maxObjects={occupancy.maxObjects}）");
                }

                if (layoutSolver == null)
                {
                    layoutSolver = gameObject.AddComponent<LayoutSolver>();
                    Debug.LogWarning("[PrototypeC] 场景里没有 LayoutSolver，已在运行时补挂（默认步长/网格参数）");
                }
            }

            if (layoutSolver != null)
            {
                if (layoutSolver.layout == null) layoutSolver.layout = roomLayout;
                if (layoutSolver.occupancy == null) layoutSolver.occupancy = occupancy;
            }

            if (occupancy != null && occupancy.layout == null) occupancy.layout = roomLayout;
        }

        /// <summary>严格模式下缺哪个组件就报哪个，方便在状态栏直接给出可操作提示。</summary>
        private string MissingLayoutComponents()
        {
            var missing = new List<string>();
            if (roomLayout == null) missing.Add("RoomLayout");
            if (occupancy == null) missing.Add("RoomOccupancy");
            if (layoutSolver == null) missing.Add("LayoutSolver");
            return string.Join(" / ", missing);
        }

        /// <summary>量「未旋转」实例在锚点局部坐标下的包围盒尺寸（米），供 LLM 布局拿到真实足迹。</summary>
        public bool TryMeasureLocalSize(GameObject instance, out Vector3 size)
        {
            size = Vector3.zero;
            if (instance == null || !TryGetBounds(instance, out Bounds bounds)) return false;

            size = ToLocalSize(bounds.size);
            return true;
        }

        /// <summary>世界尺寸 → 锚点局部尺寸（求解器、占用表都只用局部坐标）。</summary>
        private Vector3 ToLocalSize(Vector3 worldSize)
        {
            if (spawnParent == null) return worldSize;

            Vector3 scale = spawnParent.lossyScale;
            return new Vector3(
                Mathf.Abs(scale.x) > 0.0001f ? worldSize.x / Mathf.Abs(scale.x) : worldSize.x,
                Mathf.Abs(scale.y) > 0.0001f ? worldSize.y / Mathf.Abs(scale.y) : worldSize.y,
                Mathf.Abs(scale.z) > 0.0001f ? worldSize.z / Mathf.Abs(scale.z) : worldSize.z);
        }

        /// <summary>把实际占地登记进 RoomOccupancy（含容量 FIFO 与其他状态提示）。</summary>
        private void RegisterOccupancy(GameObject instance, ObjectSpec spec, PlacementZone zone, float yaw,
            LayoutIntent intent, bool dynamicItem)
        {
            if (occupancy == null || !TryGetBounds(instance, out Bounds bounds)) return;

            Vector3 localCenter = bounds.center;
            Vector3 localSize = ToLocalSize(bounds.size);

            if (spawnParent != null) localCenter = spawnParent.InverseTransformPoint(bounds.center);

            RoomBand band = zone == PlacementZone.Wall ? RoomBand.Wall
                : zone == PlacementZone.Rooftop ? RoomBand.Rooftop : RoomBand.Ground;

            var evicted = new List<PlacedItem>();
            occupancy.Register(instance, spec.NormalizedId, spec.objectName, band, localCenter, localSize,
                yaw, dynamicItem, evicted, spec.behavior, spec.sizeClass, intent);

            foreach (PlacedItem gone in evicted)
            {
                Report(ModelRequestState.Completed,
                    $"容量已满（上限 {occupancy.Capacity} 个）：已销毁最早放置的 {gone.objectName}（加入于 {gone.addedAt}），模型缓存保留");
            }
        }

        /// <summary>
        /// LLM 的 size 档位 → 目标包围盒（宽 × 高 × 深，米）。
        /// sizeClass 缺失或无法识别时退回 ObjectSpec.size（面板的 0.55/0.95/0.55 兜底）。
        /// 尺寸只影响运行期缩放，不写进 spec.generation，所以不进缓存指纹、已有缓存照常命中。
        /// </summary>
        private Vector3 ResolveTargetSize(ObjectSpec spec)
        {
            Vector3 fallback = spec != null ? spec.size : new Vector3(0.55f, 0.95f, 0.55f);
            if (spec == null || string.IsNullOrWhiteSpace(spec.sizeClass)) return Sanitize(fallback);

            Vector3 target;
            switch (spec.sizeClass.Trim().ToLowerInvariant())
            {
                case "small": target = sizeSmall; break;
                case "medium": target = sizeMedium; break;
                case "large": target = sizeLarge; break;
                case "superlarge":
                case "super large":
                case "super_large":
                    target = sizeSuperLarge; break;
                default: return Sanitize(fallback);
            }

            return Sanitize(target);
        }

        private static Vector3 Sanitize(Vector3 v)
        {
            if (v.x <= 0.0001f || v.y <= 0.0001f || v.z <= 0.0001f)
                return new Vector3(0.55f, 0.95f, 0.55f);
            return v;
        }

        /// <summary>
        /// 按目标包围盒缩放：取「目标最大边 / 模型最大边」做等比缩放（不拉变形）；
        /// 缩完若高度超过房间可用高度（地面带下沿 → 天花板带上沿），再整体缩一次防顶穿；
        /// 挂墙物件最后额外压薄厚度，避免画突出一大块。
        /// </summary>
        private void ApplySize(GameObject root, ObjectSpec spec, PlacementZone zone)
        {
            if (!autoFitHeight || !TryGetBounds(root, out Bounds raw))
            {
                root.transform.localScale = Vector3.one * Mathf.Max(0.0001f, defaultScale);
                return;
            }

            float rawMax = Mathf.Max(raw.size.x, Mathf.Max(raw.size.y, raw.size.z));
            if (rawMax <= 0.0001f)
            {
                root.transform.localScale = Vector3.one * Mathf.Max(0.0001f, defaultScale);
                return;
            }

            Vector3 target = ResolveTargetSize(spec);
            float targetMax = Mathf.Max(target.x, Mathf.Max(target.y, target.z));
            float scale = targetMax / rawMax;

            float roomHeight = Mathf.Max(0.1f, ToWorldY(ceilingBandMaxY) - ToWorldY(groundBandMinY));
            float scaledHeight = raw.size.y * scale;
            bool ceilingClamped = false;
            if (scaledHeight > roomHeight)
            {
                scale *= roomHeight / scaledHeight;
                ceilingClamped = true;
            }

            root.transform.localScale = Vector3.one * Mathf.Max(0.0001f, scale);

            if (zone == PlacementZone.Wall) ApplyWallThinning(root);

            if (TryGetBounds(root, out Bounds fitted))
            {
                string classTag = string.IsNullOrWhiteSpace(spec.sizeClass) ? "无（用兜底 size）" : spec.sizeClass;
                Debug.Log($"[PrototypeC] 尺寸：{spec.objectName}｜档位 {classTag}｜原始包围盒 " +
                          $"{raw.size.x:0.000}×{raw.size.y:0.000}×{raw.size.z:0.000} m｜目标最长边 {targetMax:0.00} m｜" +
                          $"缩放 {scale:0.000}{(ceilingClamped ? "（已按天花板限高再缩）" : string.Empty)}｜最终 " +
                          $"{fitted.size.x:0.000}×{fitted.size.y:0.000}×{fitted.size.z:0.000} m");
            }
        }

        /// <summary>
        /// 挂墙压薄：只压朝向轴（模型局部 Z）。调用时 root 旋转为 identity，所以世界 Z 就是模型厚度。
        /// 厚度上限 = wallMaxThickness，宽高保持等比缩放后的值。
        /// </summary>
        private void ApplyWallThinning(GameObject root)
        {
            float limit = Mathf.Max(0.01f, wallMaxThickness);
            if (!TryGetBounds(root, out Bounds bounds)) return;

            float depth = bounds.size.z;
            if (depth <= limit) return;

            Vector3 scale = root.transform.localScale;
            root.transform.localScale = new Vector3(scale.x, scale.y, scale.z * (limit / depth));
        }

        /// <summary>
        /// behavior × placement → 装配表（见 Behaviors/ObjectBehaviorProfile）。
        /// 不再按 behavior 写 if：九格各自一个预制件，由预制件决定碰撞体 / 刚体 / 晃动反馈。
        /// 缺格时由装配器用内置默认兜底（带明确日志与 *_FALLBACK 子节点），不会被静默忽略。
        /// </summary>
        private BehaviorAssemblyResult ApplyBehaviorAssembly(GameObject root, ObjectSpec spec, PlacementZone zone)
        {
            if (behaviorAssemblies == null) behaviorAssemblies = BehaviorAssemblyTable.CreateDefault();
            behaviorAssemblies.EnsureComplete();

            // 内置兜底只借用这里的手感参数（有预制件的格子完全以预制件配置为准）
            var fallback = new BehaviorFallbackSettings
            {
                mass = touchMass,
                linearDamping = touchLinearDamping,
                angularDamping = touchAngularDamping,
                lockY = touchLockY,
                freezeRotation = touchFreezeRotation,
                pushFactor = touchPushFactor,
            };

            // 旧数据可能没有 placementKind：用实际落位补齐，避免把挂墙物件当成 Ground 动态物件掉下来
            ObjectPlacement? placementHint = zone switch
            {
                PlacementZone.Wall => ObjectPlacement.Wall,
                PlacementZone.Rooftop => ObjectPlacement.Rooftop,
                _ => ObjectPlacement.Ground,
            };

            return ObjectBehaviorAssembler.Assemble(root, spec, behaviorAssemblies, fallback, placementHint);
        }

        /// <summary>
        /// 占位的落点：与旧的槽位口径一致，换算成「牌子底边」的世界坐标。
        /// 尺寸取 size 档位的目标包围盒（Wall 压到 wallMaxThickness），所以牌子占据的体块与真模型大致同尺寸。
        /// </summary>
        private Vector3 ResolvePlacementWorld(ObjectSpec spec, int slotIndex)
        {
            PlacementZone zone = ResolveZone(spec);
            Vector3 localXZ = AnchorLocalXZ(spec, slotIndex, zone);
            Vector3 baseWorld = ToWorld(localXZ);

            Vector3 target = ResolveTargetSize(spec);
            float roomHeight = Mathf.Max(0.1f, ToWorldY(ceilingBandMaxY) - ToWorldY(groundBandMinY));
            float height = Mathf.Clamp(target.y, 0.01f, roomHeight);
            float depth = Mathf.Max(0.01f, target.z);
            if (zone == PlacementZone.Wall) depth = Mathf.Min(depth, Mathf.Max(0.01f, wallMaxThickness));

            switch (zone)
            {
                case PlacementZone.Wall:
                    return new Vector3(
                        baseWorld.x,
                        ToWorldY((wallBandMinY + wallBandMaxY) * 0.5f) - height * 0.5f,
                        ToWorld(new Vector3(0f, 0f, wallPlaneZ)).z - depth * 0.5f);

                case PlacementZone.Rooftop:
                    return new Vector3(baseWorld.x, ToWorldY(ceilingBandMaxY) - height, baseWorld.z);

                default:
                    return new Vector3(baseWorld.x, ToWorldY(groundBandMinY), baseWorld.z);
            }
        }

        /// <summary>在最终落点创建字符画占位（showGenerationPlaceholder 关闭时返回 null）。</summary>
        private MeshGenerationPlaceholder SpawnPlaceholder(ObjectSpec spec, int slotIndex)
        {
            if (!showGenerationPlaceholder) return null;

            Vector3 anchor = ResolvePlacementWorld(spec, slotIndex);
            Vector3 target = ResolveTargetSize(spec);
            if (ResolveZone(spec) == PlacementZone.Wall)
                target.z = Mathf.Min(target.z, Mathf.Max(0.01f, wallMaxThickness));

            return MeshGenerationPlaceholder.Create(spec.objectName, anchor, EffectiveYaw(spec), target, placeholderColor);
        }

        /// <summary>
        /// 给恐龙本体加运动学碰撞箱（只做一次；不改恐龙自己的移动逻辑、活动范围与动画）。
        /// Z 纵深由 dinoColliderDepth 单独控制，用来跨过恐龙（z 0）与生成物（z -1.5）的错位。
        /// </summary>
        private void EnsureDinoBodyCollider()
        {
            if (_dinoBodyReady) return;

            DinoAgentB1 dino = FindFirstObjectByType<DinoAgentB1>();
            if (dino == null)
            {
                Debug.LogWarning("[PrototypeC] 场景里没有 DinoAgentB1，Touch 生成物不会被推动");
                return;
            }

            _dinoBodyReady = GeneratedObjectPhysics.AddDinoBodyCollider(
                dino.gameObject, dinoColliderScale, dinoColliderDepth, dinoColliderZOffset) != null;
        }

        /// <summary>
        /// 生成 / 复用「Touch 测试推手」：恐龙 AI 不一定马上走到物件旁边，
        /// Play 里用它立刻验证碰撞体 + 动态刚体 + 推力这条链路（右键组件 → ① 跟随恐龙 / ② 从物件左侧扫过）。
        /// </summary>
        private void EnsureTouchTestPusher(GameObject instance)
        {
            if (!createTouchTestPusher || instance == null) return;

            if (_touchTestPusher == null)
            {
                var go = new GameObject("PrototypeC_TouchTestPusher");
                _touchTestPusher = go.AddComponent<TouchTestPusher>();
                Debug.Log("[PrototypeC] Touch 测试推手已就绪：选中 PrototypeC_TouchTestPusher → 组件右上角 ⋮ → " +
                          "「① 跟随恐龙」或「② 从物件左侧扫过」，用真实物理接触验证 Touch");
            }

            _touchTestPusher.SetTarget(instance.transform);
        }

        /// <summary>隐藏的物理地面（无 Renderer），只用来托住 Touch 生成物，避免它们掉到下层或穿地。</summary>
        private void EnsureTouchFloor()
        {
            if (_touchFloor == null)
            {
                var go = new GameObject("PrototypeC_TouchFloor");
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(60f, 0.3f, 12f);
                _touchFloor = go.transform;
            }

            _touchFloor.position = new Vector3(0f, ToWorldY(groundBandMinY) - 0.15f, 0f);
        }

#if UNITY_EDITOR
        // 横向卷轴视角：所有辅助图形都画成「正对相机的竖直面片」，不再画水平薄片
        private const float GizmoPlaneOffset = 0.12f;
        private const int GizmoSlotColumns = 6;

        /// <summary>
        /// 编辑器辅助：把「地面带 / 墙带 / 天花板带 / 墙面 / 旧槽位」画在 Scene 视图里。
        /// 一律用锚点的 localToWorldMatrix 绘制，所以锚点被旋转/缩放时形状也跟着走；
        /// 只在编辑器里编译与调用，不进构建、不影响运行时。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!showPlacementGizmos) return;

            Transform anchor = spawnParent != null ? spawnParent : transform;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.matrix = anchor.localToWorldMatrix;

            float xMin = Mathf.Min(wallXMin, wallXMax);
            float xMax = Mathf.Max(wallXMin, wallXMax);
            float xCenter = (xMin + xMax) * 0.5f;
            float xSize = Mathf.Max(0.01f, xMax - xMin);
            float zPlot = wallPlaneZ - GizmoPlaneOffset;

            bool groundInverted = groundBandMinY > groundBandMaxY;
            bool wallInverted = wallBandMinY > wallBandMaxY;
            bool ceilingInverted = ceilingBandMinY > ceilingBandMaxY;
            bool xInverted = wallXMin > wallXMax;

            bool groundWallOverlap = Overlaps(groundBandMinY, groundBandMaxY, wallBandMinY, wallBandMaxY);
            bool wallCeilingOverlap = Overlaps(wallBandMinY, wallBandMaxY, ceilingBandMinY, ceilingBandMaxY);
            bool groundCeilingOverlap = Overlaps(groundBandMinY, groundBandMaxY, ceilingBandMinY, ceilingBandMaxY);

            var problems = new List<string>();
            if (xInverted) problems.Add("wallXMin > wallXMax：挂墙横向范围反了，所有 Wall 物件会被夹到同一侧");
            if (groundInverted) problems.Add("地面带：起始 Y > 结束 Y（字段填反了）");
            if (wallInverted) problems.Add("墙带：起始 Y > 结束 Y（字段填反了）");
            if (ceilingInverted) problems.Add("天花板带：起始 Y > 结束 Y（字段填反了）");
            if (groundWallOverlap) problems.Add("地面带与墙带重叠：Ground / Wall 的物件会落在同一高度");
            if (wallCeilingOverlap) problems.Add("墙带与天花板带重叠：挂画可能与 Rooftop 物件撞在一起");
            if (groundCeilingOverlap) problems.Add("地面带与天花板带重叠");

            Color Fill(Color c, bool conflict)
            {
                Color b = conflict ? gizmoConflictColor : c;
                return new Color(b.r, b.g, b.b, gizmoFillAlpha);
            }

            GUIStyle Label(Color c)
            {
                var style = new GUIStyle(EditorStyles.boldLabel);
                style.normal.textColor = c;
                return style;
            }

            // 竖直面片：在 X-Y 平面上展开，贴在墙面之前，正对相机
            void Band(string name, float minY, float maxY, Color color, bool conflict)
            {
                float lo = Mathf.Min(minY, maxY);
                float hi = Mathf.Max(minY, maxY);
                Vector3 center = new Vector3(xCenter, (lo + hi) * 0.5f, zPlot);
                Vector3 size = new Vector3(xSize, Mathf.Max(0.01f, hi - lo), 0.01f);
                Color edge = conflict ? gizmoConflictColor : color;

                Gizmos.color = Fill(color, conflict);
                Gizmos.DrawCube(center, size);
                Gizmos.color = edge;
                Gizmos.DrawWireCube(center, size);
                Handles.Label(
                    anchor.TransformPoint(new Vector3(xMin, hi + 0.06f, zPlot)),
                    $"{name}  {lo:0.00} ~ {hi:0.00} m",
                    Label(edge));
            }

            // 三条竖直带：横向范围共用 wallXMin/Max，高度各按自己的 Y 区间
            Band("地面带 Ground", groundBandMinY, groundBandMaxY, groundBandGizmoColor,
                groundInverted || groundWallOverlap || groundCeilingOverlap);
            Band("墙带 Wall", wallBandMinY, wallBandMaxY, wallBandGizmoColor,
                wallInverted || groundWallOverlap || wallCeilingOverlap);
            Band("天花板带 Rooftop", ceilingBandMinY, ceilingBandMaxY, ceilingBandGizmoColor,
                ceilingInverted || wallCeilingOverlap || groundCeilingOverlap);

            // 墙面：wallPlaneZ 处的一片竖板，横跨挂墙 X 范围、纵向覆盖墙带
            float wallLo = Mathf.Min(wallBandMinY, wallBandMaxY);
            float wallHi = Mathf.Max(wallBandMinY, wallBandMaxY);
            Vector3 planeCenter = new Vector3(xCenter, (wallLo + wallHi) * 0.5f, wallPlaneZ);
            Vector3 planeSize = new Vector3(xSize, Mathf.Max(0.01f, wallHi - wallLo), 0.02f);
            bool planeConflict = xInverted || wallInverted;
            Gizmos.color = Fill(wallBandGizmoColor, planeConflict);
            Gizmos.DrawCube(planeCenter, planeSize);
            Gizmos.color = planeConflict ? gizmoConflictColor : wallBandGizmoColor;
            Gizmos.DrawWireCube(planeCenter, planeSize);
            Handles.Label(
                anchor.TransformPoint(planeCenter + new Vector3(0f, 0f, 0.12f)),
                $"墙面 wallPlaneZ = {wallPlaneZ:0.00}",
                Label(planeConflict ? gizmoConflictColor : wallBandGizmoColor));

            // 挂墙朝向箭头（拖动 wallYawDegrees 可直观看到正反）
            float yaw = overrideWallYaw ? wallYawDegrees : 180f;
            Vector3 facing = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 arrowStart = planeCenter;
            Vector3 arrowEnd = arrowStart + facing * 0.8f;
            Vector3 arrowSide = Vector3.Cross(Vector3.up, facing).normalized * 0.08f;
            Gizmos.color = wallBandGizmoColor;
            Gizmos.DrawLine(arrowStart, arrowEnd);
            Gizmos.DrawLine(arrowEnd, arrowEnd - facing * 0.15f + arrowSide);
            Gizmos.DrawLine(arrowEnd, arrowEnd - facing * 0.15f - arrowSide);

            // 旧槽位（仅在关闭求解器时生效）：沿 X 一排竖线 + 序号，顺带标出是否越出挂墙 X 范围
            int columns = Mathf.Max(1, Mathf.Min(GizmoSlotColumns, slotsPerRow * 2));
            for (int i = 0; i < columns; i++)
            {
                float slotX = xCenter + (i - (columns - 1) * 0.5f) * slotSpacing;
                bool outside = slotX < xMin || slotX > xMax;
                Color slotColor = outside ? gizmoConflictColor : slotGizmoColor;

                Gizmos.color = slotColor;
                Gizmos.DrawLine(
                    new Vector3(slotX, groundBandMinY, zPlot),
                    new Vector3(slotX, groundBandMinY + 0.25f, zPlot));
                Handles.Label(
                    anchor.TransformPoint(new Vector3(slotX, groundBandMinY + 0.3f, zPlot)),
                    $"s{i}",
                    Label(slotColor));
            }

            if (problems.Count > 0)
            {
                float top = Mathf.Max(Mathf.Max(groundBandMinY, groundBandMaxY), Mathf.Max(wallBandMinY, wallBandMaxY));
                top = Mathf.Max(top, Mathf.Max(ceilingBandMinY, ceilingBandMaxY));
                Handles.Label(
                    anchor.TransformPoint(new Vector3(xMin, top + 0.25f, zPlot)),
                    "配置冲突：\n- " + string.Join("\n- ", problems),
                    Label(gizmoConflictColor));
            }

            Gizmos.matrix = previousMatrix;
        }

        private static bool Overlaps(float aMin, float aMax, float bMin, float bMax)
        {
            float aLo = Mathf.Min(aMin, aMax);
            float aHi = Mathf.Max(aMin, aMax);
            float bLo = Mathf.Min(bMin, bMax);
            float bHi = Mathf.Max(bMin, bMax);
            return aHi > bLo && bHi > aLo;
        }
#endif

        private static bool TryGetBounds(GameObject root, out Bounds bounds)
        {
            bounds = new Bounds(root.transform.position, Vector3.zero);
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            bool hasAny = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;

                if (!hasAny)
                {
                    bounds = renderer.bounds;
                    hasAny = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasAny;
        }

        /// <summary>把本机缓存发布到工程镜像；失败只告警，不影响生成/加载流程。</summary>
        private void TrySyncProjectMirror(string key)
        {
            if (!syncProjectMirror) return;

            try
            {
                ObjectCache.MirrorResult result = ObjectCache.MirrorToProject(key, out string detail);
                if (result == ObjectCache.MirrorResult.Failed)
                {
                    Debug.LogWarning($"[PrototypeC] 工程镜像同步失败：{detail}");
                }
                else
                {
                    Debug.Log($"[PrototypeC] 工程镜像 {result}：{ObjectCache.GetProjectMirrorFolder(key)}（{detail}）");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 工程镜像同步异常：{e.Message}");
            }
        }

        private GameObject Fail(string reason)
        {
            LastFailure = reason;
            Report(ModelRequestState.Failed, "失败：" + reason);
            Debug.LogError("[PrototypeC] " + reason);
            return null;
        }

        private void Report(ModelRequestState state, string message)
        {
            Debug.Log($"[PrototypeC] {state}｜{message}");
            StatusChanged?.Invoke(new ModelStatus(state, message));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PrototypeC] 临时文件删除失败：{path}，{e.Message}");
            }
        }

        private static void DestroyObject(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 只开关渲染，不动 activeSelf / enabled：包围盒、物理、脚本生命周期都保持正常，
        /// 但相机与阴影不会画出来。用于「模型已加载、还没落位」的隐藏窗口。
        /// </summary>
        private static void SetRenderingEnabled(GameObject root, bool visible)
        {
            if (root == null) return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].forceRenderingOff = !visible;
            }
        }

        /// <summary>占位牌与真模型的交接：真模型可见的同一帧再撤掉占位；被拒绝时让占位显示失败。</summary>
        private void ReleasePlaceholder(ObjectSpec spec, bool success)
        {
            if (spec == null) return;

            MeshGenerationPlaceholder placeholder;
            if (!_pendingPlaceholders.TryGetValue(spec.NormalizedId, out placeholder)) return;
            _pendingPlaceholders.Remove(spec.NormalizedId);

            if (placeholder == null) return;
            if (success) placeholder.Dispose();
            else placeholder.Fail();
        }

        /// <summary>
        /// 算出「这次真正会提交给 Meshy 的参数」：快速模式覆盖 + 风格指纹注入。
        /// 缓存查询、真正提交、布局回放都必须先调用它再算指纹，否则会出现
        /// 「生成用快速档、查缓存用标准档」的错位（回放会全部跳过）。
        /// 返回的是新对象（ApplyFastGeneration 内部 Clone），不会污染传入的 spec.generation。
        /// </summary>
        /// <param name="corrections">非空时按条追加「纠正了什么、为什么」。</param>
        public GenerationParams ResolveEffectiveGeneration(
            GenerationParams source, PromptStyle style, List<string> corrections = null)
        {
            GenerationParams generation = source != null ? source.Clone() : new GenerationParams();
            if (fastGeneration) generation = ApplyFastGeneration(generation, corrections);
            if (style != null) generation.styleFingerprint = style.Fingerprint();
            return generation;
        }

        /// <summary>
        /// 把 ObjectModelService 的「快速模式」覆盖到这次的生成参数上，并就地纠正「旧场景里存下的不兼容值」。
        /// 只影响新提交的 Meshy 任务；纠正后的参数会写进缓存 metadata 的 generationFingerprint，
        /// 并落到该指纹对应的缓存档位（ObjectCache/&lt;id&gt;/&lt;tier&gt;/），与其它档位并存、互不覆盖。
        /// </summary>
        /// <param name="corrections">非空时按条追加「纠正了什么、为什么」，供状态栏与 Console 告知用户。</param>
        private GenerationParams ApplyFastGeneration(GenerationParams source, List<string> corrections)
        {
            GenerationParams generation = source.Clone();

            if (!string.IsNullOrWhiteSpace(fastAiModel)) generation.aiModel = fastAiModel.Trim();

            bool smartTopology = generation.aiModel.StartsWith("meshy-t", StringComparison.OrdinalIgnoreCase);
            generation.modelType = smartTopology ? "smart-topology" : "standard";
            // smart-topology 会忽略 remesh，显式关掉避免服务端仍走 remesh 流程
            if (smartTopology) generation.shouldRemesh = false;

            int polyCeil = smartTopology
                ? (string.Equals(generation.aiModel, "meshy-t2", StringComparison.OrdinalIgnoreCase) ? 15000 : 30000)
                : 300000;
            int requestedPolycount = fastTargetPolycount;
            generation.targetPolycount = Mathf.Clamp(requestedPolycount, 100, polyCeil);
            if (generation.targetPolycount != requestedPolycount)
            {
                corrections?.Add($"fastTargetPolycount={requestedPolycount} 超出 {generation.aiModel} 的允许范围 " +
                                 $"100~{polyCeil}，已夹到 {generation.targetPolycount}");
            }

            generation.enableTexture = !fastPreviewOnly;
            generation.textureModel = fastPreviewOnly ? string.Empty : (fastTextureModel ?? string.Empty).Trim();
            generation.textureResolution = string.IsNullOrWhiteSpace(fastTextureResolution)
                ? "2k"
                : fastTextureResolution.Trim();

            bool hasReferenceImage = !string.IsNullOrWhiteSpace(generation.textureImageUrl);
            bool n1 = string.Equals(generation.textureModel, "meshy-n1", StringComparison.OrdinalIgnoreCase);

            // 旧场景里 fastTextureModel 可能还是 meshy-n1：n1 是「从参考图上色」，文字 preview 不保存参考图，
            // 直接 refine 必然 400。这里选择「换贴图模型、保留 texture_prompt」而不是「清空 texture_prompt」：
            // 提示词是风格一致性的唯一来源，清掉会让模型完全失去风格约束，结果比换模型更不可控。
            if (n1 && !hasReferenceImage)
            {
                corrections?.Add("fastTextureModel=meshy-n1 需要参考图（texture_image_url），文字 preview 无法直接 refine；" +
                                 "已自动改用 meshy-6-lite（保留 texture_prompt）");
                generation.textureModel = "meshy-6-lite";
                n1 = false;
            }

            // n1 只支持 2K、不支持 PBR：按模型能力收敛，同样告知（仍想用 n1 时这两项无法协商）
            if (n1 && !string.Equals(generation.textureResolution, "2k", StringComparison.OrdinalIgnoreCase))
            {
                corrections?.Add($"meshy-n1 只支持 2k，已把 fastTextureResolution={generation.textureResolution} 改为 2k");
                generation.textureResolution = "2k";
            }

            bool requestedPbr = fastEnablePbr;
            generation.enablePbr = requestedPbr && !n1;
            if (requestedPbr && n1)
            {
                corrections?.Add("meshy-n1 不支持 PBR，已自动关闭 fastEnablePbr");
            }

            Debug.Log($"[PrototypeC] 快速模式：几何 {generation.aiModel}（{generation.modelType}）｜" +
                      $"面数 {generation.targetPolycount}｜" +
                      (generation.enableTexture
                          ? $"贴图 {generation.textureModel} @ {generation.textureResolution}（PBR {(generation.enablePbr ? "开" : "关")}）"
                          : "只出素模（无贴图）"));

            return generation;
        }

        /// <summary>状态栏用的一句话摘要：这次实际提交给 Meshy 的几何 / 面数 / 贴图参数。</summary>
        private static string DescribeGeneration(GenerationParams generation)
        {
            if (generation == null) return "默认参数";

            string geometry = string.IsNullOrEmpty(generation.aiModel) ? "latest" : generation.aiModel;
            string text = $"{geometry} · {generation.targetPolycount} 面";
            if (!generation.enableTexture) return text + " · 只出素模";

            string texture = string.IsNullOrEmpty(generation.textureModel) ? "沿用 preview 模型" : generation.textureModel;
            text += $" · 贴图 {texture}@{generation.textureResolution}";
            if (!generation.enablePbr) text += "（无 PBR）";
            return text;
        }

        /// <summary>
        /// 提交前校验生成参数组合，返回空字符串表示可以提交。
        /// 这里只保留「无法安全自动纠正」的硬冲突；n1 缺参考图、n1 的 2K / PBR 限制、面数越界
        /// 已经在 ApplyFastGeneration 里纠正过并告知用户，不会再走到这里。
        /// 1) meshy-t2 / meshy-t2.5 必须配 model_type=smart-topology；
        /// 2) 面数必须落在对应模型 / remesh 的范围内；
        /// 3) meshy-n1 不能与 texture_prompt 同传、不能开 PBR、只能 2K；
        /// 4) meshy-n1 需要参考图：只有 meshy-7.1 / latest 的文字 preview 会保存参考图，其它 preview 必须给 texture_image_url；
        /// 5) texture_image_url 与 texture_prompt 互斥；4K/8K 只支持 meshy-6 / meshy-7 / latest。
        /// </summary>
        private static string ValidateGeneration(GenerationParams generation, string texturePrompt)
        {
            if (generation == null) return string.Empty;

            string geometryModel = (generation.aiModel ?? string.Empty).Trim().ToLowerInvariant();
            string modelType = (generation.modelType ?? string.Empty).Trim().ToLowerInvariant();

            bool smartGeometry = geometryModel == "meshy-t2" || geometryModel == "meshy-t2.5";

            if (modelType == "smart-topology" && !smartGeometry)
            {
                return $"参数冲突：modelType=smart-topology 只能配 aiModel=meshy-t2 或 meshy-t2.5" +
                       $"（当前 aiModel=\"{generation.aiModel}\"）";
            }

            if (smartGeometry && modelType != "smart-topology")
            {
                return $"参数冲突：aiModel={generation.aiModel} 需要 modelType=smart-topology" +
                       $"（当前 modelType=\"{generation.modelType}\"），否则 Meshy 返回 400";
            }

            if (!smartGeometry)
            {
                bool knownStandard = string.IsNullOrEmpty(geometryModel)
                                     || geometryModel == "latest"
                                     || geometryModel == "meshy-6-lite"
                                     || geometryModel == "meshy-6"
                                     || geometryModel == "meshy-7"
                                     || geometryModel == "meshy-7.1";
                if (!knownStandard)
                {
                    return $"aiModel=\"{generation.aiModel}\" 不是可用的几何模型：" +
                           "standard 用 meshy-6-lite / meshy-6 / meshy-7.1 / latest，smart-topology 用 meshy-t2 / meshy-t2.5";
                }
            }

            if (smartGeometry || generation.shouldRemesh)
            {
                int polyCeil = smartGeometry
                    ? (geometryModel == "meshy-t2" ? 15000 : 30000)
                    : 300000;
                if (generation.targetPolycount < 100 || generation.targetPolycount > polyCeil)
                {
                    return $"参数冲突：targetPolycount={generation.targetPolycount} 超出" +
                           $"{(smartGeometry ? generation.aiModel : "remesh")} 的允许范围 100~{polyCeil}";
                }
            }

            if (!generation.enableTexture) return string.Empty;

            string textureModel = (generation.textureModel ?? string.Empty).Trim().ToLowerInvariant();
            bool isN1 = textureModel == "meshy-n1";
            bool knownTexture = string.IsNullOrEmpty(textureModel)
                                || textureModel == "latest"
                                || textureModel == "meshy-7"
                                || textureModel == "meshy-6"
                                || textureModel == "meshy-6-lite"
                                || isN1;
            if (!knownTexture)
            {
                return $"textureModel=\"{generation.textureModel}\" 不是可用的贴图模型：" +
                       "可用 meshy-7 / latest / meshy-6 / meshy-6-lite / meshy-n1，留空 = 沿用 preview 模型";
            }

            string resolution = (generation.textureResolution ?? string.Empty).Trim().ToLowerInvariant();
            bool hasImageUrl = !string.IsNullOrWhiteSpace(generation.textureImageUrl);
            bool hasPrompt = !string.IsNullOrWhiteSpace(texturePrompt);

            if (isN1)
            {
                // 参考图 + 贴图提示词同时显式给了，属于用户自相矛盾，自动纠正会违背其中一边的意图 → 直接拦
                if (hasPrompt && hasImageUrl)
                {
                    return "参数冲突：textureImageUrl（texture_image_url）与 texture_prompt 互斥（meshy-n1 只能二选一）；" +
                           "请清空风格的贴图提示词，或改用 meshy-6-lite（它会保留 texture_prompt）";
                }

                if (hasPrompt)
                {
                    return "参数冲突：textureModel=meshy-n1 不能与 texture_prompt 同传（Meshy 返回 400）；" +
                           "请清空风格的贴图提示词，或把贴图模型换成 meshy-6-lite";
                }

                if (generation.enablePbr)
                {
                    return "参数冲突：textureModel=meshy-n1 不支持 PBR，enablePbr 必须为 false（否则 Meshy 返回 400）";
                }

                if (!string.IsNullOrEmpty(resolution) && resolution != "2k")
                {
                    return $"参数冲突：textureModel=meshy-n1 只支持 texture_resolution=2k" +
                           $"（当前 {generation.textureResolution}）";
                }

                bool previewStoresReference = string.IsNullOrEmpty(geometryModel)
                                              || geometryModel == "latest"
                                              || geometryModel == "meshy-7.1";
                if (!hasImageUrl && !previewStoresReference)
                {
                    return $"参数冲突：textureModel=meshy-n1 需要参考图，而 aiModel={generation.aiModel} 的文字 preview 不保存参考图" +
                           "（只有 meshy-7.1 / latest 会保存）；请把贴图模型换成 meshy-6-lite，" +
                           "或在 GenerationParams.textureImageUrl 填入参考图 URL（texture_image_url）";
                }

                return string.Empty;
            }

            if (hasImageUrl && hasPrompt)
            {
                return "参数冲突：textureImageUrl（texture_image_url）与 texture_prompt 互斥，只能二选一";
            }

            bool highRes = resolution == "4k" || resolution == "8k";
            bool modelSupportsHighRes = textureModel == "meshy-6"
                                        || textureModel == "meshy-7"
                                        || textureModel == "latest";
            if (highRes && !modelSupportsHighRes)
            {
                return $"参数冲突：texture_resolution={generation.textureResolution} 需要贴图模型 meshy-6 / meshy-7 / latest" +
                       $"（当前 \"{generation.textureModel}\"）";
            }

            return string.Empty;
        }
    }

    /// <summary>不等待分帧的 glTFast 加载代理（编辑器内验证/小模型使用）。</summary>
    internal class ImmediateDeferAgent : IDeferAgent
    {
        public bool ShouldDefer() => false;
        public bool ShouldDefer(float duration) => false;
        public Task BreakPoint() => Task.CompletedTask;
        public Task BreakPoint(float duration) => Task.CompletedTask;
    }

    /// <summary>
    /// 动态落位的下落动画：从生成点上方以重力加速度落到目标 Y，落地带一次很小的回弹后停住。
    /// 只写 transform.position.y，不碰旋转/缩放/物理；落地（或下落途中被销毁）时回调一次 onLanded，
    /// 由 ObjectModelService 完成「装配碰撞体/刚体 + 登记真实占用 + 通知恐龙」。
    /// </summary>
    [DisallowMultipleComponent]
    public class ObjectDropIn : MonoBehaviour
    {
        private float _targetY;
        private float _gravity = 9.8f;
        private float _bounceRatio;
        private System.Action _onLanded;
        private float _vy;
        private bool _bounced;
        private bool _finished;

        public void Init(float targetY, float gravity, float bounceRatio, System.Action onLanded)
        {
            _targetY = targetY;
            _gravity = Mathf.Max(0.5f, gravity);
            _bounceRatio = Mathf.Clamp01(bounceRatio);
            _onLanded = onLanded;
            _vy = 0f;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _vy -= _gravity * dt;
            float y = transform.position.y + _vy * dt;

            if (y <= _targetY)
            {
                // 第一次触地：按保留比回弹一次（很小的高度）；第二次触地或回弹太弱 → 停住并落定
                if (!_bounced && _bounceRatio > 0.02f && Mathf.Abs(_vy) > 0.5f)
                {
                    _bounced = true;
                    _vy = Mathf.Abs(_vy) * _bounceRatio;
                    y = _targetY;
                }
                else
                {
                    transform.position = new Vector3(transform.position.x, _targetY, transform.position.z);
                    Finish();
                    return;
                }
            }

            transform.position = new Vector3(transform.position.x, y, transform.position.z);
        }

        private void OnDestroy()
        {
            // 下落途中被销毁（容量淘汰/场景清理）：也要回调一次，让预留的占用位被释放
            Finish();
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            enabled = false;
            _onLanded?.Invoke();
        }
    }
}
