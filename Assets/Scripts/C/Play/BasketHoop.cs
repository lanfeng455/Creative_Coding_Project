// BasketHoop.cs —— Prototype C：篮筐投入检测（横版，无 Z 判定）
// 挂在场景里的篮筐（model 实例）上。判定规则：
//   球心「向下」穿过筐口平面、且水平落在筐口半宽内 → 进球；向上穿过不算。
//   进球 → 全屏礼花 + 暂停文字雨（用 B2BackgroundRain 的 owner 暂停，定时恢复）+ 触发泡泡奖励。
//   连续没进若干次 → 恐龙靠近、"计算角度"、起跳辅助弹射（有可调概率，不保证命中）。

using System.Collections.Generic;
using UnityEngine;
using PrototypeC;

[DisallowMultipleComponent]
public class BasketHoop : MonoBehaviour
{
    [Header("筐口位置")]
    [Tooltip("筐口中心相对本物体的偏移（世界单位；场景里篮筐物体有旋转也能用）")]
    public Vector3 rimLocalOffset = new Vector3(0f, 0.35f, 0f);
    [Tooltip("筐口半宽（米）：球心水平落在 ±这个范围内才算进")]
    public float rimHalfWidth = 0.55f;
    [Tooltip("只有向下的速度大于这个值才算「向下穿过」；向上穿过一律不算")]
    public float downSpeedThreshold = 0.5f;

    [Header("未进计数 / 恐龙辅助")]
    [Tooltip("辅助判定平面：在筐口上方这么高的地方，球向下穿过（且靠近筐口）就开始计一次「这次可能没进」")]
    public float assistTriggerHeight = 0.35f;
    [Tooltip("连续没进多少次后，恐龙过来帮忙弹射")]
    public int missesBeforeAssist = 2;
    [Tooltip("辅助弹射的命中概率（0~1）：不命中时会随机偏一点，所以不是必进")]
    [Range(0f, 1f)] public float assistChance = 0.65f;
    [Tooltip("辅助弹射的飞行时间（秒）：越短越平、越长越高")]
    public float assistFlightTime = 1.1f;
    [Tooltip("恐龙走到位后，停顿多久再弹射（秒）：给「计算角度」的表现留时间")]
    public float assistAimDelay = 0.8f;
    [Tooltip("球静止多久后让恐龙主动投一次（秒）：避免一开始没人把球抛起来，投入判定永远不触发")]
    public float idleThrowSeconds = 8f;

    [Header("恐龙守门（玩家上抛时的拦截 / 帮助）")]
    [Tooltip("是否启用：球被玩家从下层弹上上层、且恐龙在篮筐附近时，恐龙会跳起守门")]
    public bool goaltendEnabled = true;
    [Tooltip("向上穿过分界线多快才算「玩家上抛」（米/秒）：慢速漂上来的不算进攻")]
    public float playerLaunchUpSpeed = 3f;
    [Tooltip("恐龙离筐口水平多远内才算「在篮筐附近」（米）")]
    public float dinoGoaltendRange = 2.5f;
    [Tooltip("守门够得着的水平半宽（米）：球离恐龙身体多近才算真的顶到（锚在恐龙身上，不是筐口）")]
    public float goaltendReachX = 0.7f;
    [Tooltip("守门够得着的垂直高度（米）：恐龙从脚底往上这么高范围内能顶到球")]
    public float goaltendReachHeight = 1.6f;
    [Tooltip("球离恐龙多近就起跳（米）：比够得着的范围稍大一点，跳起来等球升进来")]
    public float goaltendJumpRange = 1.4f;
    [Tooltip("玩家累计上抛多少次后，恐龙从「拦截」转为「帮忙顶球」（进球后重新计数）")]
    public int playerLaunchesBeforeHelp = 3;
    [Tooltip("拦截：把球拍开的水平速度（米/秒），方向永远是远离筐口")]
    public float blockKnockX = 4.5f;
    [Tooltip("拦截：把球拍下去的向下速度（米/秒）")]
    public float blockKnockDown = 2.5f;
    [Tooltip("帮助顶球的飞行时间（秒）：越短越平、越长越高")]
    public float helpFlightTime = 0.9f;
    [Tooltip("帮助顶球朝筐口方向的概率（0~1）：不保证进球")]
    [Range(0f, 1f)] public float helpAimChance = 0.65f;
    [Tooltip("两次守门的最小间隔（秒）")]
    public float goaltendCooldown = 1.8f;
    [Tooltip("一次守门的等待超时（秒）：球没飞进守门带就作罢")]
    public float goaltendTimeout = 1.6f;

    [Header("庆祝")]
    [Tooltip("进球后触发的泡泡奖励（可留空）")]
    public BubbleReward bubbleReward;
    [Tooltip("进球后交给「吹泡泡奖励」过场控制器；留空会自动在场景里找。找不到就退回旧的直接发泡泡")]
    public PrototypeC.DinoBubbleSequence sequence;
    [Tooltip("文字雨暂停/礼花持续的时间（秒）")]
    public float celebrateSeconds = 1.6f;
    [Tooltip("礼花颜色 A")]
    public Color confettiColorA = new Color(1f, 0.85f, 0.3f);
    [Tooltip("礼花颜色 B")]
    public Color confettiColorB = new Color(0.45f, 0.95f, 0.7f);
    [Tooltip("礼花粒子数量")]
    public int confettiCount = 90;
    [Tooltip("礼花粒子大小（世界单位）")]
    public float confettiSize = 0.12f;

    [Header("重复计分保护")]
    [Tooltip("进球后，这颗球要重新升到筐口上方这么高，才允许再次计分（防止同一颗球反复触发奖励）")]
    public float scoreUnlockHeight = 0.6f;

    [Header("快速测试：一键投篮（Play 模式）")]
    [Tooltip("是否启用下面的快捷键")]
    public bool quickDropEnabled = true;
    [Tooltip("快捷键：按一下把球重置到筐口正上方并给一个向下速度（默认 F9）")]
    public KeyCode quickDropKey = KeyCode.F9;
    [Tooltip("重置位置：筐口中心正上方多高（米）")]
    public float quickDropHeight = 2.2f;
    [Tooltip("重置后赋予的向下初速度（米/秒）")]
    public float quickDropDownSpeed = 4f;

    private readonly Dictionary<BallFormController, float> _lastY = new Dictionary<BallFormController, float>();
    private readonly List<BallFormController> _stale = new List<BallFormController>();
    private readonly Dictionary<BallFormController, bool> _wasUpper = new Dictionary<BallFormController, bool>();
    private readonly List<BallFormController> _staleUpper = new List<BallFormController>();
    private readonly HashSet<BallFormController> _scored = new HashSet<BallFormController>();

    private int _misses;
    private bool _missArmed;
    private bool _assistPending;
    private float _assistAt;
    private float _celebrateUntil;
    private float _idleTimer;
    private B2BackgroundRain _rain;
    private ParticleSystem _confetti;
    private bool _confettiInitialized;

    // —— 恐龙守门 ——
    private DinoCController _dino;
    private int _playerLaunches;          // 玩家把球弹上上层的累计次数（进球清零）
    private BallFormController _goaltendBall;
    private bool _goaltendHelp;           // 本次守门是「帮忙顶球」还是「拦截」
    private float _goaltendDeadline;
    private float _goaltendCd;

    private Vector3 RimPosition => transform.TransformPoint(rimLocalOffset);
    private float RimY => RimPosition.y;
    private float RimX => RimPosition.x;

    private void Awake()
    {
        // 场景里的 bubbleReward 引用是 none：自动在场景里找一次，避免奖励静默丢失
        ResolveBubbleReward();
        if (sequence == null) sequence = FindObjectOfType<PrototypeC.DinoBubbleSequence>();
    }

    private void ResolveBubbleReward()
    {
        if (bubbleReward != null) return;
        bubbleReward = FindObjectOfType<BubbleReward>();
        if (bubbleReward != null)
            Debug.LogWarning("[Basket] 篮筐的 bubbleReward 未在 Inspector 里连接，已在场景中自动找到 BubbleReward 组件");
    }

    private void OnEnable()
    {
        // 奖励过场期间本组件被禁用，恢复时 _lastY 还是过场前的位置，
        // 会误判成「刚刚向下穿过了筐口」，先把追踪清空，避免恢复瞬间又进一球。
        _lastY.Clear();
        _wasUpper.Clear();
    }

    private void Update()
    {
        if (quickDropEnabled && Input.GetKeyDown(quickDropKey)) QuickDropBall();

        PollBalls();
        PollGoaltend();

        if (!_assistPending && _idleTimer >= idleThrowSeconds && NearestUpperBall() != null)
        {
            _idleTimer = 0f;
            BeginAssist("球静了一会儿，恐龙主动投一次：计算角度…");
        }

        if (_assistPending && Time.time >= _assistAt) FireAssist();

        if (_celebrateUntil > 0f && Time.time >= _celebrateUntil)
        {
            _celebrateUntil = 0f;
            if (_rain != null) _rain.Resume(this);
        }
    }

    // —— 投入判定 ——

    private void PollBalls()
    {
        var balls = BallFormController.All;
        bool anyMoving = false;

        _stale.Clear();
        foreach (var kv in _lastY)
            if (kv.Key == null) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) _lastY.Remove(_stale[i]);
        _scored.RemoveWhere(b => b == null);

        for (int i = 0; i < balls.Count; i++)
        {
            var ball = balls[i];
            if (ball == null) continue;

            Vector3 p = ball.transform.position;
            if (Mathf.Abs(ball.Velocity.y) > 1f || Mathf.Abs(ball.Velocity.x) > 1f) anyMoving = true;

            float lastY;
            bool had = _lastY.TryGetValue(ball, out lastY);
            _lastY[ball] = p.y;

            // 已计分的球：在网里来回碰、或过场后恢复物理时都会再次「向下穿过筐口」，
            // 必须等它重新升到筐口上方足够高才解锁，否则会无限触发奖励。
            if (_scored.Contains(ball))
            {
                if (p.y > RimY + scoreUnlockHeight) _scored.Remove(ball);
                continue;
            }

            if (!had) continue;

            float vy = ball.Velocity.y;
            bool movingDown = vy < -downSpeedThreshold;
            if (!movingDown) continue;

            // 辅助平面：向下穿过且靠近筐口 → 这一次「可能会没进」
            float assistY = RimY + assistTriggerHeight;
            if (lastY > assistY && p.y <= assistY && Mathf.Abs(p.x - RimX) <= rimHalfWidth * 3f)
                _missArmed = true;

            if (!(lastY > RimY && p.y <= RimY)) continue;

            if (Mathf.Abs(p.x - RimX) <= rimHalfWidth)
            {
                _missArmed = false;
                Score(ball);
            }
            else if (_missArmed)
            {
                _missArmed = false;
                OnMiss();
            }
        }

        // 球一直在飞 → 不算"静太久"；停下来才开始计时，避免辅助投掷打断正常飞行
        _idleTimer = anyMoving ? 0f : _idleTimer + Time.deltaTime;
    }

    // —— 恐龙守门：玩家把球从下层弹上上层时，篮筐旁的恐龙跳起拦截 / 帮忙顶球 ——

    private void PollGoaltend()
    {
        if (!goaltendEnabled) return;
        _goaltendCd -= Time.deltaTime;

        if (_dino == null) _dino = FindObjectOfType<DinoCController>();

        // 1) 检测「玩家上抛」：上一帧还在下层、这一帧到上层、且仍在快速上升。
        //    只有玩家弹力带会这样送球上来；FireAssist 在上层原地起球、踩球向下，都不会误触发。
        var balls = BallFormController.All;
        _staleUpper.Clear();
        foreach (var kv in _wasUpper)
            if (kv.Key == null) _staleUpper.Add(kv.Key);
        for (int i = 0; i < _staleUpper.Count; i++) _wasUpper.Remove(_staleUpper[i]);

        for (int i = 0; i < balls.Count; i++)
        {
            var ball = balls[i];
            if (ball == null) continue;

            bool upperNow = ball.IsUpper;
            bool had = _wasUpper.TryGetValue(ball, out bool wasUpper);
            _wasUpper[ball] = upperNow;

            if (!had || wasUpper || !upperNow) continue;
            if (ball.Velocity.y < playerLaunchUpSpeed) continue;
            OnPlayerLaunch(ball);
        }

        // 2) 已进入守门：恐龙快步贴向球 → 球到跟前起跳 → 空中且球真进入身体范围才结算。
        //    结算锚在恐龙身体上（goaltendReachX × goaltendReachHeight），够不着就只是白跳，
        //    不会在离球很远的地方凭空给球一股力。
        if (_goaltendBall == null) return;

        var b = _goaltendBall;
        if (b == null || Time.time > _goaltendDeadline || _dino == null || _dino.ExternalBusy)
        {
            _goaltendBall = null;
            return;
        }

        Vector3 bp = b.transform.position;
        Vector3 dp = _dino.transform.position;
        float dx = Mathf.Abs(bp.x - dp.x);
        float headY = dp.y + goaltendReachHeight;

        // 贴身：球不在横向够得着的范围 → 边守门边朝球走（hold 刷新，球漂了目标也跟着更新）
        if (dx > goaltendReachX)
            _dino.ExternalWalkTo(bp.x, 0.8f);

        // 起跳：球快到了且还没高过头顶 → 跳（ExternalHop 自带冷却，没跳成下一帧继续试）
        if (!_dino.IsAirborne && dx <= goaltendJumpRange && bp.y < headY)
        {
            _dino.ExternalFace(bp.x > dp.x ? 1 : -1);
            _dino.ExternalHop(3.2f);
        }

        // 结算：恐龙在空中 + 球进入身体范围 + 球还在上升 → 真的顶到了
        bool inReach = dx <= goaltendReachX && bp.y >= dp.y + 0.1f && bp.y <= headY;
        if (_dino.IsAirborne && inReach && b.Velocity.y > 0.5f)
            ApplyGoaltend(b);
    }

    private void OnPlayerLaunch(BallFormController ball)
    {
        _playerLaunches++;
        if (_goaltendCd > 0f || _dino == null || _dino.ExternalBusy) return;
        if (Mathf.Abs(_dino.transform.position.x - RimX) > dinoGoaltendRange) return;

        // 玩家试得太多 → 从使坏转为帮忙；进球后 _playerLaunches 清零，恐龙重新开始拦截。
        // 这里只登记意图，不立刻跳：起跳时机由球和恐龙的距离决定（见 PollGoaltend 第 2 步）。
        _goaltendHelp = _playerLaunches > playerLaunchesBeforeHelp;
        _goaltendBall = ball;
        _goaltendDeadline = Time.time + goaltendTimeout;

        Debug.Log($"[Basket] 恐龙守门：玩家第 {_playerLaunches} 次上抛，" +
                  (_goaltendHelp ? "冲过去帮忙顶球" : "冲过去拦截"));
    }

    private void ApplyGoaltend(BallFormController ball)
    {
        _goaltendBall = null;
        _goaltendCd = goaltendCooldown;

        float dinoDx = _dino != null
            ? Mathf.Abs(ball.transform.position.x - _dino.transform.position.x)
            : -1f;

        if (_goaltendHelp)
        {
            // 帮忙顶球：抛物线解算朝筐口顶一下，按概率带误差，不保证进球
            Vector2 from = ball.transform.position;
            float t = Mathf.Max(0.3f, helpFlightTime);
            Vector2 v = new Vector2(
                (RimX - from.x) / t,
                (RimY + 0.15f - from.y) / t + 0.5f * ball.Gravity * t);
            bool accurate = Random.value <= helpAimChance;
            if (!accurate)
            {
                v.x *= Random.Range(0.6f, 1.4f);
                v.y *= Random.Range(0.85f, 1.15f);
            }
            ball.Launch(v);
            Debug.Log($"[Basket] 恐龙顶球助攻：离球 {dinoDx:F2} 米，v=({v.x:F2}, {v.y:F2})，" +
                      (accurate ? "朝筐口顶" : "顶偏了（故意）"));
        }
        else
        {
            // 拦截：朝远离筐口的方向把球拍下去（之后球会躺在上层，由踩球机制送回下层）
            float away = Mathf.Sign(ball.transform.position.x - RimX);
            if (away == 0f) away = 1f;
            ball.Launch(new Vector2(away * blockKnockX, -Mathf.Abs(blockKnockDown)));
            Debug.Log($"[Basket] 恐龙拦截：离球 {dinoDx:F2} 米，把球拍开了");
        }

        AudioManager.PlayAt(Sfx.BallKick, ball.transform.position);
    }

    private void BeginAssist(string reason)
    {
        if (_assistPending) return;

        _assistPending = true;
        _assistAt = Time.time + assistAimDelay;

        // 表现：恐龙靠近筐口、面向筐，做「计算角度」的停顿（真正弹射在 FireAssist）
        var dino = FindObjectOfType<DinoCController>();
        if (dino != null)
        {
            dino.ExternalWalkTo(RimX - 1.2f, 1.5f);
            dino.ExternalFace(RimX > dino.transform.position.x ? 1 : -1);
        }
        Debug.Log($"[Basket] {reason}");
    }

    private void Score(BallFormController ball)
    {
        _misses = 0;
        _missArmed = false;
        _playerLaunches = 0;   // 进球 → 玩家成功了，恐龙下轮重新从「拦截」开始守门
        _scored.Add(ball);     // 同一颗球在重新升到筐口上方之前不会再计分
        Debug.Log($"[Basket] 进球！球心 {ball.transform.position} 向下穿过筐口（rim y={RimY:F2}, 半宽={rimHalfWidth:F2}）");

        // 新流程：礼花 + 暂停 + 泡泡水降落 + 恐龙过场 + 吹泡泡奖励，全部交给过场控制器
        if (sequence != null && sequence.CanPlay)
        {
            sequence.Play();
            return;
        }

        // 兜底（场景里没有过场控制器）：保持旧的「直接发泡泡 + 礼花」
        ResolveBubbleReward();
        if (bubbleReward != null) bubbleReward.Show();
        else Debug.LogError("[Basket] 进球但泡泡奖励无法启动：篮筐的 bubbleReward 未连接，且场景里找不到 BubbleReward 组件");
        Celebrate();
    }

    /// <summary>供奖励过场等外部玩法复用同一套礼花（不会影响本组件的进球/计数逻辑）。</summary>
    public void PlayConfettiNow()
    {
        try { SpawnConfetti(); }
        catch (System.Exception e) { Debug.LogWarning($"[Basket] 礼花播放失败（已忽略）：{e.Message}"); }
    }

    private void OnMiss()
    {
        _misses++;
        Debug.Log($"[Basket] 没进（连续 {_misses}/{missesBeforeAssist}）");
        if (_misses < missesBeforeAssist || _assistPending) return;

        _misses = 0;
        BeginAssist("恐龙过来帮忙：计算角度…");
    }

    private void FireAssist()
    {
        _assistPending = false;

        var ball = NearestUpperBall();
        if (ball == null)
        {
            Debug.Log("[Basket] 辅助取消：上层没有可弹射的球");
            return;
        }

        var dino = FindObjectOfType<DinoCController>();
        if (dino != null)
        {
            dino.ExternalFace(ball.transform.position.x > dino.transform.position.x ? 1 : -1);
            if (!dino.IsAirborne) dino.ExternalHop(2.4f);
        }

        Vector2 from = ball.transform.position;
        Vector2 target = new Vector2(RimX, RimY + 0.15f);
        float t = Mathf.Max(0.3f, assistFlightTime);
        Vector2 v = new Vector2(
            (target.x - from.x) / t,
            (target.y - from.y) / t + 0.5f * ball.Gravity * t);

        // 不保证命中：按辅助概率决定是否加误差
        bool accurate = Random.value <= assistChance;
        if (!accurate)
        {
            v.x *= Random.Range(0.6f, 1.4f);
            v.y *= Random.Range(0.85f, 1.15f);
        }

        ball.Launch(v);
        Debug.Log($"[Basket] 辅助弹射：v=({v.x:F2}, {v.y:F2})，{(accurate ? "瞄准筐口" : "角度偏了（故意）")}");
    }

    private BallFormController NearestUpperBall()
    {
        BallFormController best = null;
        float bestDist = float.MaxValue;
        var balls = BallFormController.All;
        for (int i = 0; i < balls.Count; i++)
        {
            var b = balls[i];
            if (b == null || !b.IsUpper) continue;
            float d = Mathf.Abs(b.transform.position.x - RimX) + Mathf.Abs(b.transform.position.y - RimY);
            if (d < bestDist) { bestDist = d; best = b; }
        }
        return best;
    }

    // —— 快速测试：一键投篮 ——

    /// <summary>
    /// 把场上那颗球重置到筐口正上方并给一个向下速度，让它走真实的 B1Item 物理与 PollBalls 的筐口判定进球。
    /// 不直接调 Score()、不绕过判定，所以能真正测出判定本身对不对。
    /// 只挪位置/清速度，不销毁重建，B1Ball.Balls 的注册表状态不受影响。
    /// </summary>
    private void QuickDropBall()
    {
        BallFormController target = NearestUpperBall();
        if (target == null)
        {
            var balls = BallFormController.All;
            for (int i = 0; i < balls.Count; i++)
                if (balls[i] != null) { target = balls[i]; break; }
        }

        if (target == null)
        {
            Debug.LogWarning("[Basket] 快速投篮：场景里没有可用的球（BallFormController.All 为空），先确认球在场上");
            return;
        }

        Vector3 rim = RimPosition;
        float downSpeed = Mathf.Abs(quickDropDownSpeed);
        Vector2 drop = new Vector2(rim.x, rim.y + Mathf.Max(0.2f, quickDropHeight));

        // 清状态：结束「无视上层地板」的穿板窗口、速度整体覆盖（含水平残余）、强制回实体形态
        if (target.ball != null) target.ball.IgnoreUpperFloorFor(0f);
        target.ForceSolidForm();
        _scored.Remove(target);   // 快速测试：允许这颗球重新计分
        target.transform.position = new Vector3(drop.x, drop.y, target.transform.position.z);
        _lastY[target] = drop.y;
        target.Launch(new Vector2(0f, -downSpeed));

        Debug.Log($"[Basket] 快速投篮：球重置到筐口正上方 ({drop.x:F2}, {drop.y:F2})，" +
                  $"向下速度 {downSpeed:F2} 米/秒（走真实物理与筐口判定）");
    }

    // —— 庆祝 ——

    private void Celebrate()
    {
        _rain = _rain != null ? _rain : FindObjectOfType<B2BackgroundRain>();
        if (_rain != null)
        {
            _rain.Suspend(this);
            _celebrateUntil = Time.time + celebrateSeconds;
        }

        // 礼花只是表现：任何异常都不能中断奖励流程（泡泡已经在 Score 里先起了）
        try
        {
            SpawnConfetti();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Basket] 礼花播放失败（已忽略，不影响奖励）：{e.Message}");
        }
    }

    /// <summary>
    /// 礼花粒子系统只创建并配置一次（主模块的 duration / startLifetime 等参数一旦开始播放就不能再写，
    /// 否则 Unity 会抛「Setting the duration while system is still playing is not supported」）。
    /// 之后每次庆祝只更新位置和形状宽度，然后 Stop(true, StopEmittingAndClear) + Play()。
    /// </summary>
    private void SpawnConfetti()
    {
        var camera = Camera.main;
        float left = -6f, right = 6f, top = 6f;
        if (camera != null && camera.orthographic)
        {
            Vector3 bl = camera.ViewportToWorldPoint(new Vector3(0f, 0f, 10f));
            Vector3 tr = camera.ViewportToWorldPoint(new Vector3(1f, 1f, 10f));
            left = bl.x; right = tr.x; top = tr.y;
        }

        if (!_confettiInitialized || _confetti == null) BuildConfetti();
        if (_confetti == null) return;

        // 播放前只更新「跟着画面变」的参数：位置 + 发射盒宽度。先停再写，避免播放中改模块参数
        _confetti.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        _confetti.transform.position = new Vector3((left + right) * 0.5f, top + 0.4f, 0f);
        var shape = _confetti.shape;
        shape.scale = new Vector3(Mathf.Max(2f, right - left), 0.2f, 1f);

        _confetti.Play();
    }

    /// <summary>首次创建礼花粒子系统：先 Stop 再写主模块（新建的系统默认处于播放状态）。</summary>
    private void BuildConfetti()
    {
        var go = new GameObject("BasketConfetti");
        go.transform.position = new Vector3(0f, 6f, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        // 新建的粒子系统默认是播放状态，先停下来才能改 duration 等主模块参数
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 6.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(confettiSize * 0.6f, confettiSize * 1.5f);
        main.gravityModifier = 0.65f;
        main.maxParticles = Mathf.Max(8, confettiCount * 2);
        main.startColor = new ParticleSystem.MinMaxGradient(confettiColorA, confettiColorB);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Max(1, confettiCount)) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(12f, 0.2f, 1f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null) renderer.material = new Material(shader);
        }

        _confetti = ps;
        _confettiInitialized = true;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 rim = RimPosition;
        Gizmos.color = new Color(1f, 0.85f, 0.3f);
        Gizmos.DrawLine(new Vector3(rim.x - rimHalfWidth, rim.y, 0f), new Vector3(rim.x + rimHalfWidth, rim.y, 0f));
        Gizmos.color = new Color(0.4f, 0.9f, 1f);
        Gizmos.DrawLine(new Vector3(rim.x, rim.y - 0.35f, 0f), new Vector3(rim.x, rim.y + 0.35f, 0f));
    }
}
