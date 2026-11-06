// DinoAgent.cs —— Prototype B：字母树下的自主小恐龙（桌宠式生态系统原型）
// 世界空间 SpriteRenderer + 简单运动学（无 Rigidbody / 无寻路 / 无 Animator，动画为程序化缩放）
// 状态机：Idle → Wandering / SeekingLeaf → CollectingLeaf → BuildingNest → Resting → ...
// 叶片通过 LetterPlant.onLeafAppeared/onLeafVanished 事件驱动，不轮询、不全局搜索场景对象

using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class DinoAgent : MonoBehaviour
{
    public enum State { Idle, Wandering, SeekingLeaf, CollectingLeaf, BuildingNest, Resting }

    [Header("引用（字母树容器，用于订阅植物事件）")]
    [SerializeField] private LetterAlphabetBar bar;

    [Header("活动范围（世界坐标，z=0 平面）")]
    public float boundsXMin = -9.86f;
    public float boundsXMax = 9.86f;
    [Tooltip("地面高度 = 站立时的脚底 y，也是活动下界")]
    public float groundY = -3.77f;
    [Tooltip("日常活动上界（出生位置钳制用；跳跃由物理上限自然约束）")]
    public float maxY = 0.86f;

    [Header("移动")]
    public float moveSpeed = 2.4f;
    public float gravity = 22f;
    public float jumpVelocity = 6.2f;
    public float arriveDistance = 0.12f;
    [Range(0f, 1f)] public float wanderHopChancePerSec = 0.3f;

    [Header("叶片平台")]
    [Tooltip("恐龙可以把树上的大叶片当跳台：站上去就能逐级够到更高的叶子")]
    public bool allowLeafPlatforms = true;
    [Tooltip("脚底探测余量：叶片表面在脚下方这个距离内才算支撑")]
    public float platformSnapDistance = 0.35f;

    [Header("采摘")]
    [Tooltip("目标叶片失效/不可达时多久重新评估一次")]
    public float leafReevaluateInterval = 1.2f;
    [Tooltip("愿意为一片叶子跑多远")]
    public float maxLeafSeekRange = 14f;
    [Tooltip("采摘咬合距离：横向 |dx|≤该值×1.2、纵向从脚面到嘴部+该值以内的叶片都能直接吃")]
    public float harvestDistance = 0.22f;
    [Tooltip("采摘动作持续时间（秒）")]
    public float harvestDuration = 0.6f;

    [Header("行为防抖（避免呆站 / 原地空跳）")]
    [Tooltip("连续起跳失败多少次后，暂停一段时间不再为叶片起跳")]
    public int failedJumpLimit = 2;
    [Tooltip("禁跳持续时间（秒）")]
    public float jumpBanDuration = 12f;
    [Tooltip("两次起跳的最小间隔（秒）")]
    public float hopCooldown = 0.5f;
    [Tooltip("闲逛目标与当前位置的最小距离，避免原地打转")]
    public float minWanderDistance = 2.5f;

    [Header("搭建庇护所")]
    public int leavesPerNest = 5;
    [Tooltip("庇护所的 x 坐标（建在地面 groundY 上，建成后长期存在）")]
    public float nestX = 0f;
    public float buildDuration = 4f;
    [Tooltip("庇护所用多少个字符搭成（列）：越大越气派")]
    public int nestCols = 13;
    [Tooltip("庇护所用多少个字符搭成（行）")]
    public int nestRows = 10;
    [Tooltip("每个字符格的世界高度（宽 = 高 × 5/7，与字符宽高比一致）")]
    public float nestCellHeight = 0.24f;
    public Color nestWallColor = new Color(0.30f, 0.72f, 0.42f, 0.95f);
    public Color nestRoofColor = new Color(0.42f, 0.9f, 0.52f, 0.95f);

    [Header("休息")]
    public float restDuration = 6f;
    [Tooltip("两次休息之间的最小间隔（秒）")]
    public float restCooldown = 25f;

    [Header("外观")]
    [Tooltip("整体缩放（1 = dino.png 原始尺寸）")]
    public float dinoScale = 1f;
    public bool faceRightByDefault = true;
    [Tooltip("左上角显示状态/叶片库存的小 HUD，便于验收")]
    public bool showDebugHud = true;

    [Header("水壶（本阶段仅预留接口，天灾系统未实现）")]
    [SerializeField] private WateringCan wateringCan = new WateringCan();

    // —— 对外只读状态 ——
    public State CurrentState => _state;
    public int LeafInventory => _leafInventory;
    public Transform Nest => _nest;
    public WateringCan WateringCan => wateringCan;

    // —— 内部状态 ——
    private SpriteRenderer _sr;
    private State _state;
    private float _stateTime;      // 当前状态已持续时间
    private float _stateLimit;     // 状态超时保护（防卡死）
    private float _desiredX;       // 水平移动目标
    private bool _wantJump;
    private float _vy;
    private bool _grounded = true;
    private int _leafInventory;
    private Transform _nest;
    private float _restCooldownTimer;
    private float _reevaluateTimer;
    private float _buildTimer = -1f;   // <0 = 还没开始建造（先走到工地）
    private float _nextBuildHop;
    private float _restTimer = -1f;    // <0 = 还没到窝边（先走到窝）
    private float _squash;             // 0..1，程序化挤压动画量
    private float _tintTimer;          // 采摘成功后的绿色闪光
    private bool _jumpForLeaf;         // 本次起跳是为了够叶片（落地没摘到就换目标）
    private float _hopCooldownTimer;   // 起跳冷却，防原地连续弹跳
    private float _stuckTimer;         // SeekingLeaf 贴着目标却吃不到/跳不了的发呆计时
    private float _jumpStartSupport;   // 起跳时的支撑高度（判断有没有跳上更高的叶片）
    private int _failedJumps;          // 最近窗口内的失败起跳次数
    private float _failedJumpWindowStart;
    private float _jumpBanUntil;       // 禁跳截止时间
    private bool _holdInAir;           // 采摘时悬在叶片上
    private readonly Dictionary<int, float> _abandonedUntil = new Dictionary<int, float>(); // 暂时够不着的叶片 → 恢复时间
    private struct Puff { public Transform T; public SpriteRenderer Sr; public float Life, MaxLife; public Vector3 Vel; public float Spin, Gravity, Drag; }
    private readonly List<Puff> _puffs = new List<Puff>();
    private static Sprite _puffSprite;

    // —— 庇护所：每个字符一个 SpriteRenderer，建造时逐层点亮 ——
    private struct NestCell
    {
        public Transform T; public SpriteRenderer Sr;
        public Vector3 Target; public float Delay, Pop; public bool Revealed;
    }
    private readonly List<NestCell> _nestCells = new List<NestCell>();
    private float _nestProgress;   // 0..1 建造进度
    private static Sprite _shadeSprite;

    private readonly HashSet<LetterPlant> _registered = new HashSet<LetterPlant>();
    private struct TrackedLeaf { public LetterPlant Plant; public int CellIndex; }
    private readonly List<TrackedLeaf> _leaves = new List<TrackedLeaf>();
    private TrackedLeaf? _target;

    private float JumpHeight => jumpVelocity * jumpVelocity / (2f * Mathf.Max(gravity, 0.01f));

    /// <summary>pivot 到精灵底边（脚底）的偏移：dino.png pivot 在底部时为 0，transform.y 就是脚底 y（落地时 = groundY）</summary>
    private float FeetOffset => _sr != null && _sr.sprite != null
        ? -_sr.sprite.bounds.min.y * dinoScale : 0.1f * dinoScale;

    /// <summary>精灵半高（脚底到身体中心的距离，与 pivot 位置无关）</summary>
    private float HalfHeight => _sr != null && _sr.sprite != null
        ? _sr.sprite.bounds.size.y * 0.5f * dinoScale : 0.1f * dinoScale;

    /// <summary>嘴部高度（脚底上方约 1.9 个半高 ≈ 头顶附近），换算成相对 pivot 的偏移</summary>
    private float MouthOffset => HalfHeight * 1.9f - FeetOffset;

    /// <summary>起跳后嘴部能触及的最高叶片高度（与采摘判定保持一致，避免"够不着还一直跳"）</summary>
    private float ReachTopY => transform.position.y + JumpHeight + MouthOffset + harvestDistance * 0.8f;

    private bool CanJumpNow() => Time.time >= _jumpBanUntil;

    /// <summary>叶片是否在咬合范围内：竖直带状判定（横向 |dx|，纵向从脚面到嘴部上沿）。
    /// 早期用"嘴部点距离"会让脚边/正踩着的叶子永远差一点点距离，恐龙只能干瞪眼发呆。</summary>
    private bool InBiteRange(Vector3 leafWorld, float tolerance = 1f)
    {
        float dx = Mathf.Abs(leafWorld.x - transform.position.x);
        float feetY = transform.position.y - FeetOffset;
        return dx <= harvestDistance * 1.2f * tolerance
            && leafWorld.y >= feetY - 0.12f * tolerance
            && leafWorld.y <= transform.position.y + MouthOffset + harvestDistance * tolerance;
    }

    /// <summary>脚下的支撑高度：地面，或恰好在脚下的大叶片表面。
    /// 叶片是半穿透平台：只有"上一帧脚在支撑面上方"才会落上去（从侧面走进叶片不会瞬移到叶顶）。</summary>
    private float GetSupportHeight(Vector3 centerPos)
    {
        float support = groundY;
        if (!allowLeafPlatforms) return support;
        float feetY = centerPos.y - FeetOffset;
        Vector3 probe = new Vector3(centerPos.x, feetY + 0.03f, centerPos.z);
        foreach (var plant in _registered)
        {
            if (plant == null) continue;
            float? y = plant.LeafSupportWorldY(probe);
            if (y.HasValue && y.Value > support && y.Value <= feetY + platformSnapDistance)
                support = y.Value;
        }
        return support;
    }

    private void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
        var p = transform.position;
        p.y = Mathf.Clamp(p.y, groundY, maxY);
        transform.position = p;
    }

    private void OnEnable()
    {
        if (bar != null)
        {
            bar.onPlantSpawned += RegisterPlant;
            // bar.Awake 先跑过的场景：一次性登记既有植物
            if (bar.Plants != null)
                foreach (var plant in bar.Plants) RegisterPlant(plant);
        }
        Enter(State.Idle);
    }

    private void OnDisable()
    {
        if (bar != null) bar.onPlantSpawned -= RegisterPlant;
        foreach (var plant in _registered)
        {
            if (plant == null) continue;
            plant.onLeafAppeared -= OnLeafAppeared;
            plant.onLeafVanished -= OnLeafVanished;
        }
        _registered.Clear();
        _leaves.Clear();
        _target = null;
    }

    private void OnDestroy()
    {
        // 粒子程序化纹理不被场景卸载自动回收，手动清理（字符贴图是全局共享的，不能销毁）
        foreach (var p in _puffs) if (p.T != null) Destroy(p.T.gameObject);
        _puffs.Clear();
        if (_puffSprite != null) { Destroy(_puffSprite.texture); _puffSprite = null; }
    }

    // —————————————— 叶片事件登记 ——————————————

    private void RegisterPlant(LetterPlant plant)
    {
        if (plant == null || _registered.Contains(plant)) return;
        _registered.Add(plant);
        plant.onLeafAppeared += OnLeafAppeared;
        plant.onLeafVanished += OnLeafVanished;
    }

    private void OnLeafAppeared(LetterPlant plant, int cellIndex)
        => _leaves.Add(new TrackedLeaf { Plant = plant, CellIndex = cellIndex });

    private void OnLeafVanished(LetterPlant plant, int cellIndex)
    {
        for (int i = _leaves.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_leaves[i].Plant, plant) && _leaves[i].CellIndex == cellIndex)
            { _leaves.RemoveAt(i); break; }
        }
        if (_target is TrackedLeaf t && ReferenceEquals(t.Plant, plant) && t.CellIndex == cellIndex)
            _target = null; // 目标消失（凋亡或被采摘）
    }

    // —————————————— 状态机 ——————————————

    private void Update()
    {
        float dt = Time.deltaTime;
        _stateTime += dt;
        if (_restCooldownTimer > 0f) _restCooldownTimer -= dt;
        if (_hopCooldownTimer > 0f) _hopCooldownTimer -= dt;
        _squash = Mathf.Lerp(_squash, 0f, dt * 8f);
        _wantJump = false;

        switch (_state)
        {
            case State.Idle: UpdateIdle(); break;
            case State.Wandering: UpdateWandering(dt); break;
            case State.SeekingLeaf: UpdateSeekingLeaf(); break;
            case State.CollectingLeaf: UpdateCollecting(); break;
            case State.BuildingNest: UpdateBuildingNest(dt); break;
            case State.Resting: UpdateResting(dt); break;
        }

        ApplyMotion(dt);
        ApplyVisuals(dt);
        UpdateNestCellAnim(dt);
        UpdatePuffs(dt);
    }

    private void Enter(State s)
    {
        _state = s;
        _stateTime = 0f;
        _holdInAir = false;
        _stuckTimer = 0f;
        if (s != State.SeekingLeaf) _jumpForLeaf = false;
        switch (s)
        {
            case State.Idle:
                _stateLimit = Random.Range(0.8f, 2.4f);
                _desiredX = transform.position.x;
                if (Random.value < 0.4f) _sr.flipX = Random.value < 0.5f; // 偶尔改变朝向
                break;
            case State.Wandering:
                _stateLimit = Random.Range(4f, 9f);
                _desiredX = PickWanderTarget();
                break;
            case State.SeekingLeaf:
                _stateLimit = 18f;
                _reevaluateTimer = leafReevaluateInterval;
                // 没有"现在够得着"的叶子 → 去闲逛，不要原地发呆（否则会 Idle↔Seeking 死循环）
                if (!TryPickNearestLeaf()) { Enter(State.Wandering); return; }
                break;
            case State.CollectingLeaf:
                _stateLimit = harvestDuration + 1.4f;
                _desiredX = transform.position.x;
                _holdInAir = !_grounded;   // 空中摘叶：咬住叶子悬停一下，避免边掉边啃
                if (_holdInAir) _vy = 0f;
                break;
            case State.BuildingNest:
                _stateLimit = buildDuration + 30f; // 含走路时间的兜底
                _desiredX = Mathf.Clamp(nestX, boundsXMin, boundsXMax);
                _buildTimer = -1f;
                _nextBuildHop = 0.7f;
                break;
            case State.Resting:
                _stateLimit = restDuration + 30f;
                _desiredX = (_nest != null ? _nest.position.x : nestX) + Random.Range(-0.3f, 0.3f);
                _restTimer = -1f;
                break;
        }
    }

    /// <summary>闲逛目标：至少离当前位置 minWanderDistance，避免"走两步就停"的原地打转</summary>
    private float PickWanderTarget()
    {
        float lo = boundsXMin + 0.5f, hi = boundsXMax - 0.5f;
        float cur = transform.position.x;
        for (int i = 0; i < 6; i++)
        {
            float x = Random.Range(lo, hi);
            if (Mathf.Abs(x - cur) >= minWanderDistance) return x;
        }
        return cur < (lo + hi) * 0.5f ? hi : lo;
    }

    /// <summary>决策下一状态：建巢 > 找叶子 > 休息(冷却结束) > 闲逛/发呆</summary>
    private void DecideNext()
    {
        if (_leafInventory >= leavesPerNest && _nest == null) { Enter(State.BuildingNest); return; }
        CleanupLeaves();
        if (_leaves.Count > 0) { Enter(State.SeekingLeaf); return; }
        if (_nest != null && _restCooldownTimer <= 0f) { Enter(State.Resting); return; }
        if (Random.value < 0.7f) Enter(State.Wandering); else Enter(State.Idle);
    }

    private void UpdateIdle()
    {
        if (_stateTime >= _stateLimit) DecideNext();
    }

    private void UpdateWandering(float dt)
    {
        if (Random.value < wanderHopChancePerSec * dt) _wantJump = true; // ApplyMotion 里只允许落地时起跳
        bool arrived = Mathf.Abs(_desiredX - transform.position.x) <= arriveDistance;
        if (arrived || _stateTime >= _stateLimit) DecideNext();
    }

    private void UpdateSeekingLeaf()
    {
        _reevaluateTimer -= Time.deltaTime;
        bool repick = _target == null || !TargetAlive();
        if (!repick && _reevaluateTimer <= 0f)
        {
            _reevaluateTimer = leafReevaluateInterval;
            // 禁跳期间目标却必须跳才够得着 → 换成别的目标，别在原地干跳
            if (!CanJumpNow() && TargetNeedsJump()) repick = true;
        }
        if (repick && !TryPickNearestLeaf()) { Enter(State.Wandering); return; }

        Vector3 leafWorld = TargetWorld();
        _desiredX = leafWorld.x;
        float dx = Mathf.Abs(leafWorld.x - transform.position.x);
        float dy = leafWorld.y - transform.position.y;

        // 叶子进入咬合范围（竖直带状：脚边、正踩着的叶子都算）就开吃
        if (InBiteRange(leafWorld)) { Enter(State.CollectingLeaf); return; }

        // 已经贴到活动边界、目标还在边界外 → 永远站不到正下方，直接放弃（否则会站着发呆）
        bool atBound = (transform.position.x <= boundsXMin + 0.02f && leafWorld.x < transform.position.x - 0.05f)
                    || (transform.position.x >= boundsXMax - 0.02f && leafWorld.x > transform.position.x + 0.05f);
        if (atBound) { AbandonTarget(); return; }

        // 发呆看门狗：已贴到目标跟前、既咬不到也不会跳（比如叶子在脚下方）→ 换一片，别盯着看
        bool idleAtTarget = _grounded && dx <= arriveDistance * 2f && dy <= harvestDistance * 0.75f;
        _stuckTimer = idleAtTarget ? _stuckTimer + Time.deltaTime : 0f;
        if (_stuckTimer >= 1.5f) { AbandonTarget(); return; }

        // 站到正下方再起跳；已经被判定够不着（ReachTopY 过滤）的不会走到这里
        if (CanJumpNow() && dx < harvestDistance * 0.5f && dy > harvestDistance * 0.75f
            && dy <= ReachTopY - transform.position.y)
        {
            _wantJump = true;
            _jumpForLeaf = true;
        }

        if (_stateTime >= _stateLimit) Enter(State.Wandering); // 兜底：怎么都够不着就去逛逛
    }

    private void UpdateCollecting()
    {
        // 采摘动作：原地啄一下（squash 曲线），计时结束才真正摘到
        _squash = Mathf.Sin(Mathf.Clamp01(_stateTime / Mathf.Max(harvestDuration, 0.01f)) * Mathf.PI) * 0.9f;
        if (_stateTime < harvestDuration) return;

        var cached = _target; // HarvestLeaf 内部会触发 onLeafVanished 把 _target 置空，先缓存
        if (cached is TrackedLeaf t && t.Plant != null && StillInReach(t))
        {
            Vector3 leafPos = t.Plant.CellToWorld(t.CellIndex);
            if (t.Plant.HarvestLeaf(t.CellIndex))
            {
                _leafInventory++;
                _tintTimer = 0.75f;          // 绿色闪光反馈
                _squash = 1f;                // 咬一口的挤压
                SpawnPuff(leafPos, new Color(0.55f, 1f, 0.6f, 0.95f), 12, 2.6f); // 叶片碎屑
                SpawnLeafDebris(leafPos);    // 叶片本体碎片飘落
                if (_grounded) { _vy = jumpVelocity * 0.55f; _grounded = false; } // 开心小跳
            }
        }
        _holdInAir = false; // 采摘结束恢复重力
        _target = null;
        DecideNext();
    }

    private void UpdateBuildingNest(float dt)
    {
        if (Mathf.Abs(transform.position.x - _desiredX) > arriveDistance * 2f) return; // 还在走向工地
        if (_buildTimer < 0f)
        {
            _buildTimer = 0f;
            BeginNestBuild(); // 先把"空壳"摆好（全部隐藏），随后逐层点亮
        }
        _buildTimer += dt;
        if (_stateTime >= _nextBuildHop) // 建造动画：周期性小跳
        {
            _wantJump = true;
            _nextBuildHop += 0.7f;
        }
        _nestProgress = Mathf.Clamp01(_buildTimer / Mathf.Max(buildDuration, 0.01f));
        RevealNestCells(_nestProgress);
        if (_buildTimer >= buildDuration)
        {
            _nestProgress = 1f;
            RevealNestCells(1f);
            _leafInventory -= leavesPerNest;
            _wantJump = true;
            DecideNext();
        }
    }

    private void UpdateResting(float dt)
    {
        if (Mathf.Abs(transform.position.x - _desiredX) > arriveDistance * 2f) return; // 还在走向窝
        if (_restTimer < 0f) _restTimer = 0f;
        _restTimer += dt;
        if (_restTimer >= restDuration)
        {
            _restCooldownTimer = restCooldown;
            DecideNext();
        }
    }

    // —————————————— 运动学与外观 ——————————————

    private void ApplyMotion(float dt)
    {
        float dx = _desiredX - transform.position.x;
        float dirX = Mathf.Abs(dx) > arriveDistance * 0.5f ? Mathf.Sign(dx) : 0f;

        if (_wantJump && _grounded && _hopCooldownTimer <= 0f)
        {
            _vy = jumpVelocity;
            _jumpStartSupport = GetSupportHeight(transform.position);
            _grounded = false;
            _hopCooldownTimer = hopCooldown;
        }
        if (!_grounded && !_holdInAir) _vy -= gravity * dt;

        var p = transform.position;
        float prevFeetY = p.y - FeetOffset;
        p.x = Mathf.Clamp(p.x + dirX * moveSpeed * dt, boundsXMin, boundsXMax);
        if (!_holdInAir) p.y += _vy * dt;

        float support = GetSupportHeight(p);
        float feetY = p.y - FeetOffset;
        if (_vy <= 0f && feetY <= support + 0.001f && prevFeetY >= support - 0.15f)
        {
            bool justLanded = !_grounded;
            p.y = support + FeetOffset;
            _vy = 0f; _grounded = true;
            if (justLanded)
            {
                SpawnPuff(new Vector3(p.x, support + 0.02f, p.z), new Color(0.85f, 0.85f, 0.78f, 0.7f), 4, 1.0f); // 落地灰尘
                if (_jumpForLeaf) OnLeafJumpLanded(support);
            }
        }
        else if (feetY > support + 0.02f) _grounded = false; // 走出平台边缘/叶片凋亡 → 下落
        transform.position = p;

        if (dirX != 0f) _sr.flipX = (dirX > 0f) != faceRightByDefault;
    }

    /// <summary>为叶片起跳后落地：跳上更高的叶片就继续追，否则记一次失败并换目标（不再原地空跳）</summary>
    private void OnLeafJumpLanded(float landedSupport)
    {
        _jumpForLeaf = false;
        bool climbed = landedSupport > _jumpStartSupport + 0.06f;
        if (climbed && TargetAlive() && TargetStillReachable())
            return; // 站上了更高的叶片 → 继续够同一片
        RegisterFailedJump();
        AbandonTarget();
    }

    private void RegisterFailedJump()
    {
        if (Time.time - _failedJumpWindowStart > 10f) { _failedJumpWindowStart = Time.time; _failedJumps = 0; }
        _failedJumps++;
        if (_failedJumps >= Mathf.Max(1, failedJumpLimit))
        {
            _jumpBanUntil = Time.time + jumpBanDuration;
            _failedJumps = 0;
        }
    }

    private bool TargetNeedsJump()
        => TargetAlive() && !InBiteRange(TargetWorld()); // 站着咬不到才算"必须跳"

    /// <summary>站上更高处之后，同一片叶子是否已经进入起跳可及范围</summary>
    private bool TargetStillReachable()
        => TargetAlive() && TargetWorld().y <= ReachTopY;

    private void ApplyVisuals(float dt)
    {
        float sx = 1f, sy = 1f;
        if (_state == State.Resting && _restTimer >= 0f) { sx = 1.25f; sy = 0.72f; } // 窝里趴着的姿势
        // 待机时轻微呼吸，避免"呆呆站着"看起来像卡住了
        if (_state == State.Idle || (_state == State.Wandering && Mathf.Abs(_desiredX - transform.position.x) <= arriveDistance))
        {
            float breath = Mathf.Sin(Time.time * 2.4f) * 0.04f;
            sy *= 1f + breath;
            sx *= 1f - breath * 0.6f;
        }
        sy *= 1f - _squash * 0.5f;
        sx *= 1f + _squash * 0.4f;
        float tilt = _grounded ? 0f : (_sr.flipX ? -8f : 8f); // 空中轻微前倾
        transform.localScale = new Vector3(dinoScale * sx, dinoScale * sy, 1f);
        transform.localRotation = Quaternion.Euler(0f, 0f, tilt);

        if (_tintTimer > 0f)
        {
            _tintTimer -= dt;
            _sr.color = Color.Lerp(Color.white, new Color(0.5f, 1f, 0.6f), Mathf.Clamp01(_tintTimer / 0.55f));
        }
        else _sr.color = Color.white;
    }

    // —————————————— 叶片目标选择 ——————————————

    private bool TargetAlive()
        => _target is TrackedLeaf t && t.Plant != null && t.Plant.IsLeafAlive(t.CellIndex);

    private Vector3 TargetWorld()
        => _target is TrackedLeaf t && t.Plant != null
            ? t.Plant.CellToWorld(t.CellIndex)
            : transform.position;

    private bool StillInReach(TrackedLeaf t)
        => InBiteRange(t.Plant.CellToWorld(t.CellIndex), 1.5f);

    private void CleanupLeaves()
    {
        for (int i = _leaves.Count - 1; i >= 0; i--)
            if (_leaves[i].Plant == null || !_leaves[i].Plant.IsLeafAlive(_leaves[i].CellIndex))
                _leaves.RemoveAt(i);
    }

    /// <summary>挑最近的、跳起来够得着的叶片作为目标（跳失败过的会暂时跳过）</summary>
    private bool TryPickNearestLeaf()
    {
        CleanupLeaves();
        // 清理过期的"放弃"记录
        if (_abandonedUntil.Count > 0)
        {
            List<int> expired = null;
            foreach (var kv in _abandonedUntil)
                if (kv.Value <= Time.time) { expired ??= new List<int>(); expired.Add(kv.Key); }
            if (expired != null) foreach (var k in expired) _abandonedUntil.Remove(k);
        }
        if (_leaves.Count == 0) return false;

        // 从"当前站立高度"起跳能到的范围：站上叶片平台后 reachTop 跟着抬高，
        // 恐龙会自然地逐级挑能落脚的叶片，一路往上够（贪心攀爬）
        float reachTop = ReachTopY;
        bool canJump = CanJumpNow();
        bool hasBest = false;
        TrackedLeaf best = default;
        float bestDist = float.MaxValue;

        foreach (var leaf in _leaves)
        {
            if (_abandonedUntil.TryGetValue(LeafKey(leaf.Plant, leaf.CellIndex), out float until)
                && Time.time < until) continue; // 刚跳失败过，先换一片
            Vector3 w = leaf.Plant.CellToWorld(leaf.CellIndex);
            if (w.y > reachTop) continue;                                  // 跳起来也够不着
            if (w.x < boundsXMin - 0.5f || w.x > boundsXMax + 0.5f) continue; // 超出活动边界
            // 禁跳期间只接受"站着就能吃到"的叶片，避免反复空跳
            if (!canJump && !InBiteRange(w)) continue;
            float d = Vector2.Distance(transform.position, w);
            if (d < bestDist) { bestDist = d; best = leaf; hasBest = true; }
        }
        if (!hasBest || bestDist > maxLeafSeekRange) return false;
        _target = best;
        return true;
    }

    private static int LeafKey(LetterPlant plant, int cellIndex)
        => plant.GetInstanceID() * 100000 + cellIndex;

    /// <summary>起跳后仍没够到的叶片：记入冷却，马上换下一片（不再原地反复弹跳）</summary>
    private void AbandonTarget()
    {
        if (_target is TrackedLeaf t && t.Plant != null)
            _abandonedUntil[LeafKey(t.Plant, t.CellIndex)] = Time.time + Mathf.Max(jumpBanDuration, 8f);
        _target = null;
        if (!TryPickNearestLeaf()) Enter(State.Wandering); // 没有别的目标就去逛逛，别站着发呆
    }

    // —————————————— 庇护所（用同一套字符搭建） ——————————————

    /// <summary>摆好空壳：每个字符一个 SpriteRenderer，初始 scale=0（隐藏）。
    /// 形状 = 底部矩形墙 + 上部椭圆穹顶 + 中间门洞，字符取自叶片同一套符号集</summary>
    private void BeginNestBuild()
    {
        var root = new GameObject("Dino Nest");
        // z 稍微推远：与恐龙同 sortingOrder 时按 z 排序，巢稳定地画在恐龙身后（恐龙走进去=进窝）
        root.transform.position = new Vector3(Mathf.Clamp(nestX, boundsXMin, boundsXMax), groundY, 0.1f);
        _nest = root.transform;
        _nestCells.Clear();
        _nestProgress = 0f;

        int cols = Mathf.Max(3, nestCols);
        int rows = Mathf.Max(4, nestRows);
        float cellH = Mathf.Max(0.06f, nestCellHeight);
        float cellW = cellH * 5f / 7f;                 // 与 5×7 字符同一宽高比
        float totalW = cols * cellW, totalH = rows * cellH;
        int cx = (cols - 1) / 2;
        int wallRows = Mathf.Max(2, rows / 3);         // 底部 1/3 是墙，其余是穹顶
        int doorHalf = Mathf.Max(1, cols / 8);

        // 内部阴影：让"钻进去休息"的空间层级可读（放最里层，字符盖在上面）
        var shade = new GameObject("Shade");
        shade.transform.SetParent(root.transform, false);
        shade.transform.localPosition = new Vector3(0f, totalH * 0.42f, 0.02f);
        shade.transform.localScale = new Vector3(totalW * 0.9f, totalH * 0.78f, 1f);
        var shadeSr = shade.AddComponent<SpriteRenderer>();
        shadeSr.sprite = ShadeSprite;
        shadeSr.color = new Color(0.15f, 0.35f, 0.2f, 0.18f);
        shadeSr.sortingOrder = 1;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            bool isWall = r < wallRows;
            if (isWall)
            {
                if (Mathf.Abs(c - cx) <= doorHalf) continue; // 门洞：恐龙能钻进窝里
            }
            else
            {
                // 穹顶：越往上越窄，形成蒙古包的圆弧轮廓
                float t = (r - wallRows + 1f) / Mathf.Max(1f, rows - wallRows + 1f);
                float halfW = cols * 0.5f * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
                if (Mathf.Abs(c - cx) > halfW) continue;
            }

            var go = new GameObject($"Char {r}-{c}");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3((c - cx) * cellW, r * cellH + cellH * 0.5f, 0f);
            var sr = go.AddComponent<SpriteRenderer>();
            char ch = LetterPlant.LeafAlphabet[Random.Range(0, LetterPlant.LeafAlphabet.Length)];
            sr.sprite = LetterPlant.GlyphSprite(ch);
            sr.color = isWall ? nestWallColor : nestRoofColor;
            sr.sortingOrder = 1;
            go.transform.localScale = Vector3.zero; // 建造时才逐层点亮
            _nestCells.Add(new NestCell
            {
                T = go.transform, Sr = sr,
                Target = Vector3.one * cellH,
                Delay = 0.88f * (r / (float)(rows - 1)) + Random.Range(0f, 0.06f),
                Pop = -1f, Revealed = false
            });
        }
    }

    /// <summary>按建造进度逐层点亮字符：底层先建，向上逐层封顶，每个字符落位时扬一小团尘屑</summary>
    private void RevealNestCells(float progress)
    {
        for (int i = 0; i < _nestCells.Count; i++)
        {
            var cell = _nestCells[i];
            if (cell.Revealed || cell.Delay > progress) continue;
            cell.Revealed = true;
            cell.Pop = 0f;
            SpawnPuff(cell.T.position, cell.Sr.color, 2, 0.8f);
            _nestCells[i] = cell;
        }
    }

    /// <summary>字符落位的弹出动画（带轻微回弹）</summary>
    private void UpdateNestCellAnim(float dt)
    {
        for (int i = 0; i < _nestCells.Count; i++)
        {
            var cell = _nestCells[i];
            if (!cell.Revealed || cell.Pop < 0f || cell.Pop >= 1f) continue;
            cell.Pop = Mathf.Min(1f, cell.Pop + dt / 0.28f);
            float k = 1f - Mathf.Pow(1f - cell.Pop, 3f);       // 落位
            k *= 1f + 0.22f * Mathf.Sin(cell.Pop * Mathf.PI);  // 轻微回弹
            if (cell.T != null) cell.T.localScale = cell.Target * k;
            _nestCells[i] = cell;
        }
    }

    /// <summary>1×1 世界单位、边缘柔化的方形遮罩（庇护所内部阴影）</summary>
    private static Sprite ShadeSprite
        => _shadeSprite != null ? _shadeSprite : (_shadeSprite = BuildShadeSprite());

    private static Sprite BuildShadeSprite()
    {
        const int n = 32;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
        { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float nx = Mathf.Abs((x + 0.5f) / n * 2f - 1f);
            float ny = Mathf.Abs((y + 0.5f) / n * 2f - 1f);
            float d = Mathf.Max(nx, ny);
            float a = Mathf.Clamp01((0.95f - d) / 0.3f); // 边缘羽化
            px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n); // 1×1 世界单位
    }

    // —————————————— 尘土/碎屑粒子反馈 ——————————————

    private void SpawnPuff(Vector3 pos, Color color, int count, float speed)
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Puff");
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * Random.Range(1.2f, 2.4f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetPuffSprite();
            sr.sortingOrder = 1; // 画在恐龙(0)前面
            sr.color = color;
            float ang = Random.Range(0.6f, Mathf.PI - 0.6f); // 上半圆扇形喷出
            float v = speed * Random.Range(0.45f, 1f);
            _puffs.Add(new Puff
            {
                T = go.transform, Sr = sr,
                Life = 0f, MaxLife = Random.Range(0.35f, 0.7f),
                Vel = new Vector3(Mathf.Cos(ang) * v, Mathf.Sin(ang) * v, 0f),
                Spin = 0f, Gravity = 2f, Drag = 3f
            });
        }
    }

    /// <summary>采摘掉落的字符碎片：与叶片同一套字符，边转边飘落（最直观的"摘到了"反馈）</summary>
    private void SpawnLeafDebris(Vector3 pos)
    {
        for (int i = 0; i < 5; i++)
        {
            var go = new GameObject("LeafDebris");
            go.transform.position = pos + (Vector3)(Random.insideUnitCircle * 0.1f);
            go.transform.localScale = Vector3.one * Random.Range(0.16f, 0.28f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = LetterPlant.RandomGlyphSprite();
            sr.sortingOrder = 2;
            sr.color = new Color(0.55f, 1f, 0.6f, 1f);
            _puffs.Add(new Puff
            {
                T = go.transform, Sr = sr,
                Life = 0f, MaxLife = Random.Range(0.45f, 0.9f),
                Vel = new Vector3(Random.Range(-1.8f, 1.8f), Random.Range(0.7f, 2.2f), 0f),
                Spin = Random.Range(-540f, 540f), Gravity = 4f, Drag = 0.8f
            });
        }
    }

    private void UpdatePuffs(float dt)
    {
        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var p = _puffs[i];
            p.Life += dt;
            if (p.Life >= p.MaxLife) { Destroy(p.T.gameObject); _puffs.RemoveAt(i); continue; }
            p.T.position += p.Vel * dt;
            if (p.Spin != 0f) p.T.Rotate(0f, 0f, p.Spin * dt);
            p.Vel *= Mathf.Max(0f, 1f - p.Drag * dt); // 阻尼
            p.Vel.y -= p.Gravity * dt;                // 下坠
            var c = p.Sr.color;
            c.a = Mathf.Clamp01(1f - p.Life / p.MaxLife);
            p.Sr.color = c;
            _puffs[i] = p;
        }
    }

    /// <summary>8x8 软圆点贴图（所有粒子共用，程序化生成不依赖素材）</summary>
    private static Sprite GetPuffSprite()
    {
        if (_puffSprite != null) return _puffSprite;
        const int s = 8;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false)
        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[s * s];
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s / 2f, s / 2f)) / (s / 2f);
            px[y * s + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(1f - d) * 255));
        }
        tex.SetPixels32(px);
        tex.Apply();
        _puffSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
        return _puffSprite;
    }

    // —————————————— 验收 HUD ——————————————

    private void OnGUI()
    {
        if (!showDebugHud) return;
        GUI.Label(new Rect(10f, 10f, 560f, 22f),
            $"[Dino] state={_state}  leaves={_leafInventory}/{leavesPerNest}  " +
            $"nest={(_nest != null ? $"{_nestProgress * 100f:0}% ({_nestCells.Count} chars)" : "none")}  " +
            $"treeLeafCells={_leaves.Count}");
    }
}
