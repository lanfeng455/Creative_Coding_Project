// B1Items.cs —— 单词生成的物品：BALL / CHAIR / WATER
// 共用一套自研运动学（重力/落地反弹/边界反弹/缺口跨层），与字符物理一致，不依赖 Rigidbody。
// 跨层规则：上层地板只在缺口(GapXMin..GapXMax)处可穿越，其余位置会撞到地板底面弹回。
// SlingshotBand 通过 Launch() 把下层物品弹向上层。

using System.Collections.Generic;
using UnityEngine;

/// <summary>物品共同基类：注册表 + 存活标记 + 弹力带可作用的物理核</summary>
public abstract class B1Item : MonoBehaviour
{
    public bool Alive { get; protected set; } = true;

    protected static readonly List<B1Item> _all = new();
    public static IReadOnlyList<B1Item> All => _all;

    [Header("物理参数（运行时可调）")]
    [Tooltip("重力加速度（米/秒²）")]
    public float gravity = 9.8f;
    [Tooltip("落地恢复系数 restitution：0 = 不弹，1 = 完全弹回。新球建议 0.65~0.8")]
    [Range(0f, 1f)] public float groundBounce = 0.45f;
    [Tooltip("左右边界反弹系数")]
    public float wallBounce = 0.75f;
    [Tooltip("水平空气阻力（每秒衰减比例）")]
    public float drag = 0.15f;
    [Tooltip("最小反弹速度：落地速度低于它才允许静止，否则继续弹")]
    public float minBounceSpeed = 0.8f;
    [Tooltip("落地时保留的水平速度比例（1 = 不损失）")]
    [Range(0f, 1f)] public float groundHorizontalDamping = 0.92f;

    [Header("水平活动范围（Prototype C 的球会夹到相机可视范围内；默认沿用世界边界）")]
    [Tooltip("勾上后用下面的左右边界，不再用 B1LayerConfig.boundsXMin / boundsXMax")]
    public bool useCustomHorizontalBounds = false;
    [Tooltip("自定义左边界（世界 X，米）")]
    public float horizontalBoundsMin = -8.4f;
    [Tooltip("自定义右边界（世界 X，米）")]
    public float horizontalBoundsMax = 8.4f;

    protected B1LayerConfig _world;
    protected Vector2 _vel;
    protected float _radius = 0.25f;
    protected SpriteRenderer _sr;
    protected float _ignoreUpperFloorUntil;

    /// <summary>物理半径（Prototype C 的球按渲染包围盒写入）。</summary>
    public float Radius => _radius;

    /// <summary>本帧是否接触地面（StepPhysics 写入；持续微弹用它判断能不能补冲量）。</summary>
    public bool Grounded { get; private set; }

    /// <summary>把水平活动范围夹到给定区间（球滚出画面就看不到了）。</summary>
    public void SetHorizontalBounds(float min, float max)
    {
        useCustomHorizontalBounds = true;
        horizontalBoundsMin = Mathf.Min(min, max);
        horizontalBoundsMax = Mathf.Max(min, max);
    }

    /// <summary>在 seconds 秒内无视上层地板（恐龙把球压下 / 强制穿板用）。</summary>
    public void IgnoreUpperFloorFor(float seconds) => _ignoreUpperFloorUntil = Time.time + Mathf.Max(0f, seconds);

    protected virtual void Awake()
    {
        _all.Add(this);
        _sr = GetComponent<SpriteRenderer>();
    }

    protected virtual void OnDestroy() => _all.Remove(this);

    protected virtual void Start()
    {
        if (_world == null) _world = FindObjectOfType<B1LayerConfig>();
    }

    /// <summary>弹力带/恐龙踢：赋予速度</summary>
    public void Launch(Vector2 v) => _vel = v;

    public Vector2 Velocity => _vel;

    /// <summary>是否停在地面上（速度很小）</summary>
    public bool IsResting => _vel.sqrMagnitude < 0.04f;

    /// <summary>通用步进：重力 + 落地 + 墙壁。
    /// 不再与上层地板底面碰撞——被弹力带弹起时可以穿过分界线到上层（和字符一致），
    /// 下落时则自然落在上层地板上。返回本帧事件：None/Rest/Ground/Wall</summary>
    protected string StepPhysics(float dt, float? floorY = null)
    {
        string evt = "None";
        var p = (Vector2)transform.position;
        Grounded = false;

        _vel.y -= gravity * dt;
        _vel.x *= 1f - drag * dt;
        p += _vel * dt;

        // 左右边界：默认用世界边界，球可以覆盖成相机可视范围（B1 原物品不受影响）
        float minX = useCustomHorizontalBounds ? horizontalBoundsMin : _world.boundsXMin;
        float maxX = useCustomHorizontalBounds ? horizontalBoundsMax : _world.boundsXMax;
        float lo = minX + _radius, hi = maxX - _radius;
        if (hi < lo) { float mid = (lo + hi) * 0.5f; lo = hi = mid; }
        if (p.x < lo) { p.x = lo; _vel.x = Mathf.Abs(_vel.x) * wallBounce; evt = "Wall"; }
        if (p.x > hi) { p.x = hi; _vel.x = -Mathf.Abs(_vel.x) * wallBounce; evt = "Wall"; }

        // 支撑地面（缺口感知）
        float ground = floorY ?? _world.GroundYAt(p.x, p.y);
        // 强制穿板窗口：忽略上层地板（恐龙下压球 / 需要把球送下层时用）
        if (floorY == null && Time.time < _ignoreUpperFloorUntil) ground = _world.lowerGroundY;
        if (p.y - _radius <= ground && _vel.y <= 0f)
        {
            Grounded = true;
            p.y = ground + _radius;
            if (Mathf.Abs(_vel.y) > minBounceSpeed)
            {
                _vel.y = -_vel.y * groundBounce;
                _vel.x *= groundHorizontalDamping;
                evt = "Ground";
            }
            else { _vel.y = 0f; if (evt == "None") evt = "Rest"; }
        }

        // 出界销毁（掉出世界下方兜底）
        if (p.y < _world.lowerGroundY - 8f) { Alive = false; Destroy(gameObject); return evt; }

        transform.position = new Vector3(p.x, p.y, 0f);
        return evt;
    }
}

// ——————————————————————————————————————

/// <summary>BALL：永久玩伴。恐龙追它、踢它；玩家把它从下层弹回上层。</summary>
public class B1Ball : B1Item
{
    public static readonly List<B1Ball> Balls = new();

    [Tooltip("球体颜色（HDR 可发光）")]
    public Color ballColor = new Color(1f, 0.85f, 0.3f);
    [Range(0.15f, 0.5f)] public float size = 0.26f;
    [Tooltip("拖尾残影间隔（秒）")]
    public float trailInterval = 0.05f;
    [Tooltip("球速低于此值时不出拖尾（停在地上不拉尾）")]
    public float trailMinSpeed = 0.5f;

    [Header("下层保底弹跳（默认关，保持 B1 原行为；Prototype C 的预制体球会打开）")]
    [Tooltip("下层最低弹射高度（米）：落地后的反弹高度低于它就补回这个高度；0 = 关闭。用 v=√(2g·高度) 直接赋值，不依赖恢复系数链")]
    public float layerBounceMinHeight = 0f;

    private GlyphTrail _trail;

    protected override void Awake()
    {
        base.Awake();
        Balls.Add(this);
        _trail = new GlyphTrail(trailInterval);
        _radius = size;
        // 没有 SpriteRenderer 的宿主（例如 3D 预制体球）不套字符贴图、也不改缩放，
        // 只复用这里的物理与"恐龙会来踢"的注册表。
        if (_sr != null)
        {
            _sr.sprite = LetterPlant.GlyphSprite('@');
            _sr.color = ballColor;
            _sr.sortingOrder = 3;
            transform.localScale = Vector3.one * (size * 2f);
        }
    }

    /// <summary>设定物理半径（预制体球按真实渲染包围盒算，避免半个球埋进地板）。</summary>
    public void SetPhysicsRadius(float r) => _radius = Mathf.Max(0.02f, r);

    protected override void OnDestroy() { base.OnDestroy(); Balls.Remove(this); }

    private void Update()
    {
        float dt = Time.deltaTime;
        string evt = StepPhysics(dt);
        if (evt == "Ground" || evt == "Wall")
            AudioManager.PlayAt(Sfx.BallBounce, transform.position);

        TickLayerBounce();

        // TemplateB 同款拖尾：按固定间隔甩出渐隐的 "@" 残影
        if (_sr != null && _trail != null && _vel.sqrMagnitude > trailMinSpeed * trailMinSpeed)
            _trail.Tick(transform.position, _sr.sprite, ballColor, size * 2f);
    }

    /// <summary>复用本球已有的字符拖尾（没有 SpriteRenderer 的宿主，例如 3D 预制体球）。</summary>
    public void EmitTrail(Vector3 pos, Sprite sprite, Color color, float scale) => _trail?.Tick(pos, sprite, color, scale);

    /// <summary>调整拖尾发射间隔（重建同一个 GlyphTrail，不另写一套实现）。</summary>
    public void SetTrailInterval(float interval) => _trail = new GlyphTrail(Mathf.Max(0.01f, interval));

    /// <summary>
    /// 下层保底弹跳：只在"这一帧刚落地"时检查，落地后的反弹高度（v²/2g）低于
    /// layerBounceMinHeight 就把它补到这个高度（v = √(2g·高度)）。
    /// 因为冲量是直接赋值、不经过恢复系数链，所以不会继续衰减，也不会停在 _vel.y = 0 的静止分支。
    /// 上层不生效；不提供任何自动跨层/自动上弹。
    /// </summary>
    private void TickLayerBounce()
    {
        if (layerBounceMinHeight <= 0.01f || _world == null) return;
        if (!Grounded) return;                              // 只在落地那一帧判定
        if (_world.OnUpperFloor(transform.position)) return; // 上层不保底

        float needV = Mathf.Sqrt(2f * gravity * layerBounceMinHeight);
        if (_vel.y < needV) _vel.y = needV;
    }

    /// <summary>最近的、位于上层地板上的球（恐龙找玩伴用）</summary>
    public static B1Ball NearestOnUpper(Vector3 pos, float maxDist)
    {
        B1Ball best = null; float bd = maxDist * maxDist;
        foreach (var b in Balls)
        {
            if (b == null || !b.Alive) continue;
            if (!b.OnUpper) continue;
            float d = (b.transform.position - pos).sqrMagnitude;
            if (d < bd) { bd = d; best = b; }
        }
        return best;
    }

    public bool OnUpper => _world != null && _world.OnUpperFloor(transform.position);
}

// ——————————————————————————————————————

/// <summary>CHAIR：下层是可搬运物体，落到上层地板停稳后"落地生根"成为家具，恐龙会来坐。</summary>
public class B1Chair : B1Item
{
    public static readonly List<B1Chair> Chairs = new();

    [Tooltip("椅子颜色")]
    public Color chairColor = new Color(0.62f, 0.47f, 0.32f);

    private bool _placed;

    protected override void Awake()
    {
        base.Awake();
        Chairs.Add(this);
        _radius = 0.3f;
        _sr.sprite = B1Visuals.Chair;
        _sr.color = chairColor;
        _sr.sortingOrder = 1;
    }

    protected override void OnDestroy() { base.OnDestroy(); Chairs.Remove(this); }

    private void Update()
    {
        if (_placed) return;
        string evt = StepPhysics(Time.deltaTime);

        // 在上层地板上停稳 → 落地生根
        if (evt == "Rest" && _world.OnUpperFloor(transform.position))
        {
            _placed = true;
            var p = transform.position;
            p.y = _world.upperFloorY - 0.05f;
            transform.position = p;
            AudioManager.PlayAt(Sfx.NestComplete, p);
        }
    }

    /// <summary>恐龙找椅子：只找已摆好的</summary>
    public static B1Chair NearestChair(Vector3 pos, float maxDist)
    {
        B1Chair best = null; float bd = maxDist * maxDist;
        foreach (var c in Chairs)
        {
            if (c == null || !c.Alive || !c._placed) continue;
            float d = (c.transform.position - pos).sqrMagnitude;
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }
}

// ——————————————————————————————————————

/// <summary>WATER：灭火水滴。下层可被弹起；进入上层后自动飞向火焰，命中灭火。</summary>
public class B1Water : B1Item
{
    public Color waterColor = new Color(0.35f, 0.75f, 1f);
    [Tooltip("进入上层后朝火焰水平追踪的加速度")]
    public float homingAccel = 10f;

    private bool _homing;

    protected override void Awake()
    {
        base.Awake();
        _radius = 0.2f;
        _sr.sprite = B1Visuals.Circle;
        _sr.color = waterColor;
        _sr.sortingOrder = 3;
        transform.localScale = Vector3.one * 0.4f;
    }

    private void Update()
    {
        var fire = FireHazard.ActiveFire;

        // 进入上层 → 开始追踪火焰
        if (!_homing && _world.OnUpperFloor(transform.position)) _homing = true;
        if (_homing && fire != null)
        {
            float dir = Mathf.Sign(fire.transform.position.x - transform.position.x);
            _vel.x += dir * homingAccel * Time.deltaTime;
            _vel.x = Mathf.Clamp(_vel.x, -7f, 7f);
        }

        StepPhysics(Time.deltaTime);

        // 命中火焰
        if (fire != null && Vector2.Distance(transform.position, fire.transform.position) < 0.9f)
        {
            fire.Extinguish();
            B1Visuals.Shatter(transform.position, waterColor, 8);
            AudioManager.PlayAt(Sfx.RainStart, transform.position);
            Alive = false;
            Destroy(gameObject);
        }

        // 没火可灭又落地：几秒后蒸发（避免下层积水）
        if (fire == null && IsResting) { _evap += Time.deltaTime; if (_evap > 5f) { Alive = false; Destroy(gameObject); } }
    }
    private float _evap;

    /// <summary>场上是否有 WATER 在飞（恐龙气泡逻辑用）</summary>
    public static bool WaterInFlight()
    {
        foreach (var it in _all)
            if (it is B1Water w && w.Alive) return true;
        return false;
    }
}

// ——————————————————————————————————————

/// <summary>WOOD/STONE 建材：下层可被弹起，落到上层地板后躺平，恐龙来捡去盖房。</summary>
public class B1Resource : B1Item
{
    public enum Kind { Wood, Stone }
    public static readonly List<B1Resource> All = new();

    [Header("材质（SpawnItem 里赋值，Awake 之后 Start 之前）")]
    public Kind kind = Kind.Wood;

    [Tooltip("木头颜色")]
    public Color woodColor = new Color(0.72f, 0.5f, 0.28f);
    [Tooltip("石头颜色")]
    public Color stoneColor = new Color(0.62f, 0.65f, 0.7f);

    public Color MatColor => kind == Kind.Wood ? woodColor : stoneColor;
    public bool IsConstructionLog => _constructionSprite != null;
    private Sprite _constructionSprite;
    public string ConstructionWord { get; private set; }

    internal void ConfigureConstructionLog(Sprite sprite, B1LayerConfig world)
    {
        ConstructionWord = "WOOD";
        _constructionSprite = sprite;
        _world = world;
        // The sprite has a bottom pivot, so the visible log rests on the platform.
        _radius = 0f;
        groundBounce = 0f;
        transform.localScale = Vector3.one * 1.5f;
    }

    internal void ConfigureConstructionMaterial(string word, B1LayerConfig world)
    {
        ConfigureConstructionLog(AFrameBuildPlan.Icon(word), world);
        ConstructionWord = word;
        kind = word == "STONE" ? Kind.Stone : Kind.Wood;
        transform.localScale = Vector3.one;
    }

    protected override void Awake()
    {
        base.Awake();
        All.Add(this);
        _radius = 0.22f;
        _sr.sortingOrder = 3;
        transform.localScale = Vector3.one * 0.6f;
    }

    protected override void Start()
    {
        base.Start();
        // 贴图/颜色都在 Start 里设：kind 由 SpawnItem 在 AddComponent 之后赋值，
        // Awake 时还拿不到（会把石头也画成木头）。
        // 字符画素材：木头用 "="（叠木头/木板），石头用 "#"（砌石）
        _sr.sprite = _constructionSprite ?? LetterPlant.GlyphSprite(kind == Kind.Wood ? '=' : '#');
        _sr.color = IsConstructionLog ? Color.white : MatColor;
    }

    protected override void OnDestroy() { base.OnDestroy(); All.Remove(this); }

    private void Update()
    {
        StepPhysics(Time.deltaTime, IsConstructionLog ? _world.upperFloorY : null);
    }

    internal void Carry(Transform carrier)
    {
        AudioManager.PlayAt(Sfx.LeafHarvest, transform.position);
        Alive = false;
        enabled = false;
        transform.SetParent(carrier, false);
        transform.localPosition = new Vector3(0.27f, 0.28f);
        transform.localScale = Vector3.one * 0.9f;
    }

    /// <summary>恐龙捡走</summary>
    public void Consume()
    {
        B1Visuals.Shatter(transform.position, MatColor, 5);
        AudioManager.PlayAt(Sfx.LeafHarvest, transform.position);
        Alive = false;
        Destroy(gameObject);
    }

    /// <summary>上层地板上、指定材质的最近资源（恐龙捡建材用）</summary>
    public static B1Resource NearestOnUpper(Vector3 pos, float maxDist, Kind kind, string constructionWord = null)
    {
        B1Resource best = null; float bd = maxDist * maxDist;
        foreach (var r in All)
        {
            if (r == null || !r.Alive || r.kind != kind) continue;
            if (constructionWord != null && r.ConstructionWord != constructionWord) continue;
            if (r._world == null || !r._world.OnUpperFloor(r.transform.position)) continue;
            if (r.IsConstructionLog && (!r.IsResting || r.transform.position.y > r._world.upperFloorY + 0.01f))
                continue;
            float d = (r.transform.position - pos).sqrMagnitude;
            if (d < bd) { bd = d; best = r; }
        }
        return best;
    }
}

// ——————————————————————————————————————

/// <summary>程序化视觉资产（静态缓存）：圆形 / 椅子 / 碎裂特效。都是纯视觉，不承载实体数据。</summary>
public static class B1Visuals
{
    private static Sprite _circle;
    public static Sprite Circle
    {
        get
        {
            if (_circle != null) return _circle;
            const int n = 48;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                byte a = (byte)(Mathf.Clamp01((1f - d) * n * 0.5f) * 255);
                px[y * n + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px); tex.Apply(false, true);
            _circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            return _circle;
        }
    }

    private static Sprite _square;
    /// <summary>白色方块（弹力带指尖矩形等 UI 感视觉用）</summary>
    public static Sprite Square
    {
        get
        {
            if (_square != null) return _square;
            const int n = 4;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px); tex.Apply(false, true);
            _square = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            return _square;
        }
    }

    private static Sprite _chair;
    /// <summary>椅子：座面 + 靠背 + 四条腿（128×128 程序绘制）</summary>
    public static Sprite Chair
    {
        get
        {
            if (_chair != null) return _chair;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool solid =
                    (y > 24 && y < 36 && x > 8 && x < 120) ||   // 座面
                    (y > 36 && y < 100 && x > 96 && x < 112) || // 靠背
                    (y > 4 && y < 26 && x > 10 && x < 22) ||    // 腿×4
                    (y > 4 && y < 26 && x > 40 && x < 52) ||
                    (y > 4 && y < 26 && x > 76 && x < 88) ||
                    (y > 4 && y < 26 && x > 106 && x < 118);
                px[y * n + x] = solid ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
            }
            tex.SetPixels32(px); tex.Apply(false, true);
            _chair = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f); // 100px/单位 → 高约 1.28 世界单位
            return _chair;
        }
    }

    /// <summary>字符碎裂：TMP 字符爆散（字体/发光跟随 B1TextStyle）</summary>
    public static void Shatter(Vector3 pos, Color color, int count)
        => B1Burst.Shatter(pos, color, count, 5f);
}
