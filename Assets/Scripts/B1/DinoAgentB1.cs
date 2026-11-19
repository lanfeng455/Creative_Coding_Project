// DinoAgentB1.cs —— Prototype B1 恐龙（常驻上层）
// 行为：漫步 / 休息 / 追球玩球 / 把球踢进右侧缺口掉回下层
// 依赖：B1LayerConfig、B1Ball（弱引用静态注册表）、B1Item（椅子）

using System.Collections.Generic;
using UnityEngine;
using TMPro;

[RequireComponent(typeof(SpriteRenderer))]
public class DinoAgentB1 : MonoBehaviour
{
    public enum State { Wandering, Resting, PlayingBall, Sitting, Fleeing, Collecting, Building }

    [Header("引用")]
    public B1LayerConfig world;
    [Tooltip("求救/需求气泡（世界空间 TMP，编辑器里摆好拖进来，可留空）")]
    public TextMeshPro bubble;
    [SerializeField] private Sprite constructionLogSprite;
    internal Sprite ConstructionLogSprite => constructionLogSprite;

    [Header("需求系统")]
    [Tooltip("两次需求之间的随机间隔（秒）")]
    public Vector2 needIntervalRange = new Vector2(7f, 13f);
    /// <summary>当前需求：建材 / null。</summary>
    public string CurrentNeed { get; private set; }

    internal PorchHomecoming Homecoming { get; private set; }
    internal void StartHomecoming()
    {
        if (Homecoming || !_house || !_house.Cottage) return;
        SetBubble(null);
        Homecoming = _house.Cottage.gameObject.AddComponent<PorchHomecoming>();
        Homecoming.Begin(this);
    }

    internal ConstructionEncounter ActiveEncounter { get; set; }
    internal event System.Action<int> ConstructionStepInstalled;
    internal bool WatchingFirstStone { get; set; }
    private float firstStoneFlashUntil;
    internal void FlashStoneInitial() => firstStoneFlashUntil = Time.time + .55f;
    public int EncounterLives { get; private set; } = 5;
    private float _lastEncounterHit;

    public string RequestedWord
    {
        get
        {
            if (Homecoming) return null;
            if (ActiveEncounter) return ActiveEncounter.Request;
            if (_state == State.Fleeing) return null;
            if (NeedsWoodHouse) return CanOrderConstructionLog() ? _house.NextConstructionWord : null;
            return CurrentNeed;
        }
    }

    private float _needTimer;
    private B2House _house;
    private string _carrying;          // 已捡起的建材："WOOD"/"STONE"
    private B1Resource _targetRes;
    private B1Resource _carriedLog;

    internal bool UsesConstructionPlan => constructionLogSprite != null && _house != null;
    internal bool AwaitingFoundation => UsesConstructionPlan && _house.WoodStepsBuilt == 0;
    private bool NeedsWoodHouse => UsesConstructionPlan
        && _house.WoodStepsBuilt < B2House.WoodStepsRequired;

    private bool CanOrderConstructionLog()
    {
        if (_carriedLog != null) return false;
        foreach (var resource in B1Resource.All)
            if (resource != null && resource.Alive && resource.IsConstructionLog &&
                resource.ConstructionWord == _house.NextConstructionWord &&
                resource.gameObject.scene == gameObject.scene)
                return false;
        return NeedsWoodHouse;
    }

    internal bool IsConstructionRequest(string word)
        => NeedsWoodHouse && word == _house.NextConstructionWord && CanOrderConstructionLog();

    [Header("移动参数")]
    public float walkSpeed = 2.2f;
    public float playSpeed = 3.0f;
    public float fleeSpeed = 3.8f;
    public float wanderIntervalMin = 2f;
    public float wanderIntervalMax = 5f;
    public float restDurationMin = 4f;
    public float restDurationMax = 9f;

    [Header("玩球")]
    [Tooltip("看到多远的球会去玩")]
    public float ballNoticeRange = 6f;
    [Tooltip("距球多近算踢到")]
    public float kickRange = 1.1f;
    [Tooltip("踢球速度")]
    public float kickSpeed = 6.5f;
    [Tooltip("踢球间隔（秒）")]
    public float kickCooldown = 1.2f;
    [Tooltip("不想玩了改干别的的概率（每次踢球后）")]
    [Range(0f, 1f)] public float boredChance = 0.25f;

    [Header("视觉")]
    public float hopForce = 1.2f;
    public float squashAmount = 0.12f;

    private State _state;
    private SpriteRenderer _sr;
    private float _vy, _timer, _stateDur, _kickCd, _restCooldown = 8f;
    private float _desiredX;
    private Vector3 _baseScale = Vector3.one;
    private bool _baseScaleCaptured;
    private float _visualY = 1f;
    private B1Ball _targetBall;
    private B1Item _targetChair;

    private void Awake() { _sr = GetComponent<SpriteRenderer>(); }

    private void Start()
    {
        if (world == null) world = FindObjectOfType<B1LayerConfig>();
        if (world == null) { Debug.LogError("[DinoB1] 缺少 B1LayerConfig", this); enabled = false; return; }

        // 常驻上层：出生贴上层地板
        var p = transform.position;
        p.y = world.upperFloorY;
        p.x = Mathf.Clamp(p.x, world.boundsXMin + 1f, world.GapXMin - 1f);
        transform.position = p;

        // 房子（运行时自动创建，地基在上层地板左半区）
        _house = FindObjectOfType<B2House>();
        if (_house == null)
        {
            var go = new GameObject("B2_House");
            _house = go.AddComponent<B2House>();
            // 建在画面正中间（x=0），不是挤在左边
            _house.Init(world, 0f);
        }
        _needTimer = Random.Range(needIntervalRange.x, needIntervalRange.y);

        EnterWander();
        SetBubble(null);
    }

    private void Update()
    {
        if (Homecoming) return;
        if (WatchingFirstStone && IsConstructionRequest("STONE"))
        {
            SetBubble("STONE!");
            return;
        }
        float dt = Time.deltaTime;
        if (ActiveEncounter)
        {
            if (ActiveEncounter.IsGameOver) return;
            Face(ActiveEncounter.transform.position.x >= transform.position.x ? 1 : -1);
            SetBubble(ActiveEncounter.Request == null ? null : ActiveEncounter.Request + "!");
            if (EncounterLives < 5 && Time.time - _lastEncounterHit < .45f) _visualY = .82f;
            else _visualY = 1f + (EncounterLives == 1 ? Mathf.Sin(Time.time * 5f) * .04f : 0f);
            ApplyVisual();
            UpdateHitFlash(dt);
            return;
        }
        _timer += dt;
        _restCooldown -= dt;
        _kickCd -= dt;

        // —— 红色威胁：靠近就提前躲，而不是等贴脸命中才反应 ——
        if (_state != State.Fleeing)
        {
            RedThreatGlyph nearest = null;
            float best = threatNoticeRange;
            foreach (var t in RedThreatGlyph.All)
            {
                if (t == null) continue;
                float d = Vector2.Distance(t.transform.position, transform.position);
                if (d < best) { best = d; nearest = t; }
            }
            if (nearest != null) Scare(nearest.transform.position.x, hit: false);
        }

        // —— 需求计时：当前无需求、不在搬建材/盖房时倒数 ——
        if (NeedsWoodHouse) CurrentNeed = _house.NextConstructionWord;
        else if (CurrentNeed == null
            && _state != State.Collecting && _state != State.Building && _state != State.Fleeing)
        {
            _needTimer -= dt;
            if (_needTimer <= 0f) PickNeed();
        }

        if (NeedsWoodHouse && _state != State.Fleeing &&
            _state != State.Collecting && _state != State.Building)
        {
            _targetRes = B1Resource.NearestOnUpper(transform.position, 100f,
                _house.NextConstructionWord == "STONE" ? B1Resource.Kind.Stone : B1Resource.Kind.Wood,
                _house.NextConstructionWord);
            if (_targetRes != null) _state = State.Collecting;
        }

        switch (_state)
        {
            case State.Wandering: UpdateWandering(); break;
            case State.Resting: UpdateResting(); break;
            case State.PlayingBall: UpdatePlayingBall(); break;
            case State.Sitting: UpdateSitting(); break;
            case State.Fleeing: UpdateFleeing(); break;
            case State.Collecting: UpdateCollecting(); break;
            case State.Building: UpdateBuilding(dt); break;
        }

        // 显示当前需求
        string request = RequestedWord;
        SetBubble(request == null ? null : request + "!");

        TryStompBall();
        ApplyMotion(dt);
        ApplyVisual();
        UpdateHitFlash(dt);
    }

    /// <summary>房屋还能扩建时请求石材，否则保持空闲。</summary>
    private void PickNeed()
    {
        if (HouseCanGrow)
        {
            // 木头没搭完的情况上面已经强制 WOOD，这里只可能是扩石屋
            CurrentNeed = "STONE";
            return;
        }
        CurrentNeed = null;
        _needTimer = Random.Range(needIntervalRange.x, needIntervalRange.y);
    }

    // —— 收集建材 / 盖房 ——

    private void UpdateCollecting()
    {
        if (_targetRes == null || !_targetRes.Alive) { _targetRes = null; EnterWander(); return; }
        _desiredX = _targetRes.transform.position.x;
        if (Mathf.Abs(transform.position.x - _desiredX) < 0.55f)
        {
            _carrying = _targetRes.ConstructionWord ?? (_targetRes.kind == B1Resource.Kind.Wood ? "WOOD" : "STONE");
            if (_targetRes.IsConstructionLog)
            {
                _carriedLog = _targetRes;
                _carriedLog.Carry(transform);
            }
            else _targetRes.Consume();
            _targetRes = null;
            if (_carriedLog == null) CurrentNeed = null;
            _buildTimer = 0f;
            _state = State.Building;
        }
    }

    private float _buildTimer;
    private void UpdateBuilding(float dt)
    {
        if (_house == null || _carrying == null) { _carrying = null; EnterWander(); return; }
        _desiredX = _house.BuildSiteX;
        if (Mathf.Abs(transform.position.x - _desiredX) < 0.4f)
        {
            Face(1);
            _buildTimer += dt;
            if (_buildTimer >= 1.1f)
            {
                if (_carriedLog != null)
                {
                    int before = _house.WoodStepsBuilt;
                    if (_house.AddConstructionStep(_carrying))
                        ConstructionStepInstalled?.Invoke(_house.WoodStepsBuilt);
                    Destroy(_carriedLog.gameObject);
                    _carriedLog = null;
                    bool roofDone = _house.WoodStepsBuilt == B2House.WoodStepsRequired
                                    && _house.WoodStepsBuilt > before;
                    // 每落一根木头一声；封顶换成完工音
                    AudioManager.PlayAt(roofDone ? Sfx.HouseComplete : Sfx.WoodPlace, transform.position);
                }
                else
                {
                    _house.AddRoom(_carrying);
                    AudioManager.PlayAt(Sfx.HouseComplete, transform.position);
                }
                _carrying = null;
                CurrentNeed = NeedsWoodHouse ? _house.NextConstructionWord : null;
                _needTimer = Random.Range(needIntervalRange.x, needIntervalRange.y);
                EnterWander();
            }
        }
        else _buildTimer = 0f;
    }

    // —— 踩球：下落时脚下方有球 → 把球踩向缺口方向，自己借力弹起 ——

    private void TryStompBall()
    {
        if (_vy > -0.5f) return; // 只在下落时踩
        foreach (var b in B1Ball.Balls)
        {
            if (b == null || !b.Alive) continue;
            var bp = b.transform.position;
            float ddx = bp.x - transform.position.x;
            float ddy = transform.position.y - bp.y;
            if (Mathf.Abs(ddx) < 0.6f && ddy > 0f && ddy < 1.0f)
            {
                float dir = Mathf.Sign(world.GapCenterX - bp.x);
                if (dir == 0f) dir = 1f;
                b.Launch(new Vector2(dir * Random.Range(4.5f, 7f), 1.2f));
                _vy = hopForce * 1.8f;
                AudioManager.PlayAt(Sfx.BallKick, bp);
                break;
            }
        }
    }

    // —— 状态 ——

    private void EnterWander()
    {
        _state = State.Wandering;
        _timer = 0f;
        _stateDur = Random.Range(wanderIntervalMin, wanderIntervalMax);
        PickWanderTarget();
    }

    private void UpdateWandering()
    {
        // 需求是建材且上层有对应资源 → 去捡（石头任何时候都能捡；木头走圆木分支）
        if (CurrentNeed == "STONE" || (constructionLogSprite == null && CurrentNeed == "WOOD"))
        {
            var kind = CurrentNeed == "STONE" ? B1Resource.Kind.Stone : B1Resource.Kind.Wood;
            var res = B1Resource.NearestOnUpper(transform.position, 100f, kind);
            if (res != null) { _targetRes = res; _state = State.Collecting; return; }
        }

        // 有球在上层且够近 → 去玩
        if (_kickCd <= 0f)
        {
            _targetBall = B1Ball.NearestOnUpper(transform.position, ballNoticeRange);
            if (_targetBall != null) { _state = State.PlayingBall; return; }
        }

        bool arrived = Mathf.Abs(transform.position.x - _desiredX) < 0.25f;
        if (arrived || _timer > _stateDur)
        {
            // 优先找椅子坐；否则休息或继续逛
            _targetChair = B1Chair.NearestChair(transform.position, 12f);
            if (_targetChair != null && Random.value < 0.45f) { _state = State.Sitting; _timer = 0f; _stateDur = Random.Range(restDurationMin, restDurationMax); return; }
            if (_restCooldown <= 0f && Random.value < 0.5f)
            {
                _state = State.Resting; _timer = 0f;
                _stateDur = Random.Range(restDurationMin, restDurationMax);
                _restCooldown = Random.Range(12f, 26f);
                return;
            }
            _timer = 0f;
            _stateDur = Random.Range(wanderIntervalMin, wanderIntervalMax);
            PickWanderTarget();
        }
    }

    private void UpdateResting()
    {
        if (_timer >= _stateDur) EnterWander();
    }

    private void UpdatePlayingBall()
    {
        // 球没了（掉进缺口/被收走）→ 回日常
        if (_targetBall == null) { _targetBall = null; EnterWander(); return; }
        if (!_targetBall.Alive) { _targetBall = null; EnterWander(); return; }

        _desiredX = _targetBall.transform.position.x;

        float dx = _targetBall.transform.position.x - transform.position.x;
        if (Mathf.Abs(dx) <= kickRange && _kickCd <= 0f)
        {
            // 踢球：偏向把球往缺口方向踢（缺口在右侧），偶尔乱踢
            bool towardGap = transform.position.x < world.GapCenterX || Random.value < 0.35f;
            float dir = towardGap && Random.value < 0.75f ? 1f : (Random.value < 0.5f ? -1f : 1f);
            _targetBall.Launch(new Vector2(dir * kickSpeed * Random.Range(0.7f, 1.15f),
                                           Random.Range(2.2f, 3.8f)));
            AudioManager.PlayAt(Sfx.BallKick, _targetBall.transform.position);
            _kickCd = kickCooldown;
            if (Random.value < boredChance) { _targetBall = null; EnterWander(); }
        }
    }

    private void UpdateSitting()
    {
        if (_targetChair == null || !_targetChair.Alive) { EnterWander(); return; }
        _desiredX = _targetChair.transform.position.x;
        if (Mathf.Abs(transform.position.x - _desiredX) < 0.3f)
        {
            // 坐下：贴椅子位置，压扁一点
            if (_timer >= _stateDur) { _targetChair = null; EnterWander(); }
        }
        else if (_timer > 8f) { _targetChair = null; EnterWander(); } // 够不着就算了
    }

    [Tooltip("受惊时与威胁保持的安全距离")]
    public float fleeSafeDist = 4.2f;
    [Tooltip("红色威胁进入这个距离才开始躲。别调太大：红字符在天花板生成时离恐龙约 2.4，调大就会\"被空气吓到\"")]
    public float threatNoticeRange = 1.8f;
    [Tooltip("演示胶水：一根木头推进几格建造。B2House 共 4 格（地板/左柱/右柱/屋顶），设 4 = 一根木头直接搭完一间")]
    public int woodStepsPerLog = 4;
    [Tooltip("演示胶水：整栋房子最多几间（1 木 + 1 石 = 2）。0 = 不限制，交给 B2House.maxRooms")]
    public int demoMaxRooms = 2;
    private bool HouseCanGrow => _house != null && _house.CanGrow
        && (demoMaxRooms <= 0 || _house.Rooms.Count < demoMaxRooms);

    private void UpdateFleeing()
    {
        _fleeTimer += Time.deltaTime;
        if (_fleeTimer > 2.5f)
        {
            if (_carriedLog != null) _state = State.Building;
            else EnterWander();
            return;
        }

        float fromX = _fleeFromX;
        float lo = world.upperFloorXMin + 0.8f, hi = world.GapXMin - 0.8f;

        // 已经够远 → 不再像雕像一样杵着，原地小幅紧张踱步
        if (Mathf.Abs(transform.position.x - fromX) >= fleeSafeDist)
        {
            if (Mathf.Abs(transform.position.x - _desiredX) < 0.2f)
                _desiredX = Mathf.Clamp(transform.position.x + Random.Range(-0.9f, 0.9f), lo, hi);
            return;
        }
        // 逃到距威胁 fleeSafeDist 的同侧安全点，夹在活动范围内
        _desiredX = transform.position.x < fromX
            ? Mathf.Clamp(fromX - fleeSafeDist, lo, hi)
            : Mathf.Clamp(fromX + fleeSafeDist, lo, hi);
    }

    private void PickWanderTarget()
    {
        // 上层活动范围：地板左端 ~ 缺口左沿（不往缺口里走，免得掉下去）
        float lo = world.upperFloorXMin + 0.8f;
        float hi = world.GapXMin - 0.8f;
        _desiredX = Random.Range(lo, hi);
    }

    // —— 运动与视觉 ——

    private void ApplyMotion(float dt)
    {
        var p = transform.position;
        float lo = world.upperFloorXMin + 0.4f, hi = world.GapXMin - 0.4f;

        float speed = _state switch
        {
            State.PlayingBall => playSpeed,
            State.Fleeing => fleeSpeed,
            State.Collecting => playSpeed,
            State.Resting or State.Sitting => 0f,
            _ => walkSpeed,
        };

        float dx = _desiredX - p.x;
        if (speed > 0f && Mathf.Abs(dx) > 0.05f)
        {
            float step = Mathf.Clamp(dx, -speed * dt, speed * dt);
            p.x = Mathf.Clamp(p.x + step, lo, hi);
            Face(dx > 0f ? 1 : -1);
            // 只有真的在移动才蹦跳（否则到站了还在原地蹦，像顶墙跑）
            if (p.y <= world.upperFloorY + 0.001f && _vy <= 0f && hopForce > 0f && _state != State.Sitting)
                _vy = hopForce * (speed / walkSpeed); // 越急跳越高
        }

        _vy -= 9.8f * dt;
        p.y += _vy * dt;
        if (p.y <= world.upperFloorY) { p.y = world.upperFloorY; _vy = 0f; }
        transform.position = p;
    }

    private void Face(int dir)
    {
        var s = transform.localScale;
        s.x = Mathf.Abs(s.x) * dir;
        transform.localScale = s;
    }

    private void ApplyVisual()
    {
        // 首帧记录用户在编辑器里调的缩放；之后绝不重读（旧版每帧重采当 scale==1 时会越拉越瘦）
        if (!_baseScaleCaptured)
        {
            _baseScale = transform.localScale;
            _baseScale.x = Mathf.Abs(_baseScale.x);
            _baseScaleCaptured = true;
        }
        bool sitting = _state == State.Sitting && Mathf.Abs(transform.position.x - _desiredX) < 0.35f;
        bool grounded = transform.position.y <= world.upperFloorY + 0.01f && _vy <= 0.01f;
        float target = sitting ? 0.82f : grounded ? 0.96f : 1f + squashAmount;
        _visualY = Mathf.Lerp(_visualY, target, grounded ? 0.3f : 0.15f);

        int face = Mathf.Sign(transform.localScale.x) < 0f ? -1 : 1;
        transform.localScale = new Vector3(
            _baseScale.x / Mathf.Max(_visualY, 0.6f) * face,
            _baseScale.y * _visualY, _baseScale.z);
    }

    private string _lastBubbleText;
    private float _bubblePopT = -1f; // >=0 表示正在弹跳
    private bool _bubbleOrderFixed;

    private void SetBubble(string text)
    {
        if (bubble == null) return;
        if (!_bubbleOrderFixed)
        {
            _bubbleOrderFixed = true;
            // 气泡永远压在房子墙字(0/1)和恐龙之上，不然一盖房就被挡住
            var r = bubble.GetComponent<Renderer>();
            if (r != null) r.sortingOrder = 20;
        }
        if (text != _lastBubbleText)
        {
            _lastBubbleText = text;
            if (text != null)
            {
                _bubblePopT = 0f; // 新内容出现 → 弹跳强调
                AudioManager.PlayAt(Sfx.DinoVoice, transform.position); // 一并来一声叫
            }
        }
        bubble.gameObject.SetActive(text != null);
        if (text != null)
        {
            bubble.text = text == "STONE!" && Time.time < firstStoneFlashUntil
                ? "<color=#6BFFFF>S</color>TONE!" : text;
            bubble.transform.position = transform.position
                + new Vector3(0f, 1.4f * Mathf.Max(1f, Mathf.Abs(transform.localScale.y)), 0f);
            // 恐龙左右翻转会把子物体的文字镜像：用局部 X 缩放抵消
            float mirror = Mathf.Sign(transform.localScale.x) == 0f ? 1f : Mathf.Sign(transform.localScale.x);
            // 弹跳动画：出现时 scale 0 → 1.25 → 1 弹簧收束（0.55s）
            float pop = 1f;
            if (_bubblePopT >= 0f)
            {
                _bubblePopT += Time.deltaTime;
                float t = _bubblePopT / 0.55f;
                if (t >= 1f) { _bubblePopT = -1f; }
                else pop = (1f - Mathf.Cos(t * Mathf.PI * 3f) * Mathf.Exp(-t * 3.5f));
            }
            bubble.transform.localScale = new Vector3(mirror * pop, pop, 1f);
        }
    }

    // —— 受击反馈 ——

    private float _hitFlash;
    private Color _dinoBaseColor = Color.white;
    private bool _baseColorCaptured;

    private void UpdateHitFlash(float dt)
    {
        if (_sr == null) return;
        if (!_baseColorCaptured) { _dinoBaseColor = _sr.color; _baseColorCaptured = true; }
        if (_hitFlash > 0f)
        {
            _hitFlash -= dt;
            // 红白快速闪烁（0.45s）
            float f = Mathf.PingPong(_hitFlash * 9f, 1f);
            _sr.color = Color.Lerp(_dinoBaseColor, new Color(1f, 0.25f, 0.25f), f);
            if (_hitFlash <= 0f) _sr.color = _dinoBaseColor;
        }
    }

    internal void TakeEncounterHit(Color color)
    {
        EncounterLives--;
        _lastEncounterHit = Time.time;
        _hitFlash = .45f;
        AudioManager.PlayAt(Sfx.DinoHurt, transform.position);
        B1Burst.Shatter(transform.position + Vector3.up * .45f, color, 5, 3f);
    }

    public State CurrentState => _state;

    /// <summary>受惊（红色字符命中时调用）：受击反馈 + 跳起 + 逃向反方向</summary>
    public void Scare(float fromX, bool hit = true)
    {
        if (_vy <= 0.01f) _vy = hopForce * 2f;
        _state = State.Fleeing;
        _fleeFromX = fromX;
        _fleeTimer = 0f;
        _targetBall = null;
        _targetRes = null;
        if (!hit) return;   // 只是提前躲开，不播受击演出
        // 受击反馈：红闪 + 压扁 + 碎屑
        _hitFlash = 0.45f;
        _visualY = 0.7f;
        AudioManager.PlayAt(Sfx.DinoHurt, transform.position);
        B1Burst.Shatter(transform.position + Vector3.up * 0.6f, new Color(1f, 0.35f, 0.35f), 5, 4f);
    }
    private float _fleeFromX;
    private float _fleeTimer;

    private void OnDrawGizmosSelected()
    {
        if (world == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(new Vector3(world.boundsXMin, world.upperFloorY + 2f, 0f),
                        new Vector3(world.GapXMin, world.upperFloorY + 2f, 0f));
    }
}
