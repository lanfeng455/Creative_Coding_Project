// BouncingBall.cs —— 弹力球（玩家与恐龙的互动玩具）
// 视觉：'@' 字符球（与威胁字符同一套字库），HDR 颜色 + 拖尾残影，URP Bloom 下发荧光。
// 物理：重力 + 地面弹跳（能量衰减）+ 左右边界反弹；静置超时自然消失。
// 玩家垫球：双食指构成"屋檐"线段（接近水平 + 足够宽才有效，复用 MediaPipe 指尖坐标）。
//           屋檐全程画一条细线让玩家看清"垫在哪"；球必须【从上方压到线段且正在下落】才算垫中，
//           垫中后稳定向上弹射（接触点决定水平分量），闪光强化反馈 + 冷却防抖。
// 恐龙顶球：DinoAgent 在 PlayingBall 状态调 Bounce() 给球向上冲量。

using System.Collections.Generic;
using UnityEngine;

public class BouncingBall : MonoBehaviour
{
    [Header("引用（留空自动找）")]
    public RightHandIndexRayProvider rayProvider;
    public DinoAgent dino;

    [Header("物理")]
    public float radius = 0.26f;
    public float gravity = 9.5f;
    [Range(0.4f, 0.95f)] public float groundBounce = 0.68f;   // 地面反弹保留速度比例
    [Range(0f, 1f)] public float horizontalDrag = 0.15f;      // 空中水平阻尼/秒
    public float minX = -9.5f, maxX = 9.5f;                   // 左右反弹边界
    // 球长期存在：与庇护所生命周期共享（巢完工时生成，巢被毁时 Shatter 爆散）

    [Header("玩家屋檐垫球")]
    [Tooltip("手部发力：沿屋檐法线额外叠加的速度（越大垫得越高）")]
    public float roofBounceSpeed = 6.2f;
    [Tooltip("切向速度保留比例：斜着垫时球会顺着屋檐方向飞出（0=完全消掉切向）")]
    [Range(0f, 1f)] public float roofTangentialKeep = 0.85f;
    [Tooltip("球心到屋檐线的接触余量（世界单位），给手抖留一点空间")]
    public float roofHitDepth = 0.10f;
    public float roofMaxSpeed = 15f;          // 反弹后速度上限（防爆）
    public float roofCooldown = 0.22f;        // 两次垫球最小间隔（防抖）
    public float roofMinWidth = 0.7f;         // 有效屋檐的最小宽度（世界单位）
    public float roofMaxTiltDeg = 35f;        // 屋檐允许的最大倾角（放宽，斜着也能垫）
    public Color ballColor = new Color(1f, 0.84f, 0.35f, 1f);
    [Tooltip("本体颜色强度倍率（>1 时经 URP Bloom 发荧光）")]
    public float glowIntensity = 1.8f;
    [Tooltip("拖尾残影发射间隔（秒）")]
    public float trailInterval = 0.05f;

    [Header("恐龙顶球")]
    public float dinoBounceSpeed = 6.2f;

    private SpriteRenderer _sr;
    private Vector2 _vel;
    private float _roofCd, _spin;
    private Vector2 _roofA, _roofB; private bool _roofTracked;
    private float _groundY;
    private GlyphTrail _trail;

    // —— 屋檐可视化：常驻提示线 + 垫球闪光线 ——
    private Transform _roofLine;
    private Transform _roofFlash;
    private static Sprite _whiteSprite;

    public bool IsActive => _vel.sqrMagnitude > 0.6f || transform.position.y > _groundY + radius + 0.05f;

    /// <summary>球被外力弹起（恐龙顶球 / 生成时初速）</summary>
    public void Bounce(Vector2 vel, bool byDino)
    {
        _vel = vel;
        _roofCd = Mathf.Max(_roofCd, byDino ? 0.1f : roofCooldown);
    }

    private void Awake()
    {
        _ballCount++;
        _sr = gameObject.AddComponent<SpriteRenderer>();
        _sr.sprite = LetterPlant.GlyphSprite('@'); // '@' = 圆环字符，天然的"字符球"
        _sr.color = new Color(ballColor.r * glowIntensity, ballColor.g * glowIntensity,
                              ballColor.b * glowIntensity, 1f); // HDR 荧光
        _sr.sortingOrder = 3;
        transform.localScale = Vector3.one * (radius * 2.1f);
        if (rayProvider == null) rayProvider = FindObjectOfType<RightHandIndexRayProvider>();
        if (dino == null) dino = FindObjectOfType<DinoAgent>();
        _groundY = dino != null ? dino.groundY : -4.04f;
        if (dino != null) { minX = Mathf.Max(minX, dino.boundsXMin + 0.2f); maxX = Mathf.Min(maxX, dino.boundsXMax - 0.2f); }
        _trail = new GlyphTrail(trailInterval);
        BuildRoofVisuals();
    }

    /// <summary>屋檐提示线（白细线，常驻）+ 垫球闪光线（金色粗线，命中瞬间）</summary>
    private void BuildRoofVisuals()
    {
        _roofLine = MakeLine("RoofLine", new Color(1f, 1f, 1f, 0.22f), 0.05f);
        _roofFlash = MakeLine("RoofFlash", new Color(1f, 0.9f, 0.45f, 0.95f), 0.14f);
    }

    private Transform MakeLine(string name, Color color, float thickness)
    {
        var go = new GameObject(name);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = WhiteSprite;
        sr.color = color;
        sr.sortingOrder = 4;
        go.transform.localScale = new Vector3(0.01f, thickness, 1f);
        go.SetActive(false);
        return go.transform;
    }

    private static Sprite WhiteSprite
    {
        get
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f); // 1×1 世界单位
            return _whiteSprite;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _roofCd -= dt;

        UpdateRoof(dt);

        // 物理
        _vel.y -= gravity * dt;
        _vel.x *= 1f - horizontalDrag * dt;
        var p = transform.position;
        p.x += _vel.x * dt;
        p.y += _vel.y * dt;

        // 地面弹跳
        if (p.y - radius <= _groundY)
        {
            p.y = _groundY + radius;
            if (Mathf.Abs(_vel.y) > 0.9f)
            {
                _vel.y = -_vel.y * groundBounce;
                _vel.x *= 0.92f;
                AudioManager.PlayAt(Sfx.BallBounce, p);
            }
            else
            {
                _vel.y = 0f;
                _vel.x *= 1f - 2.5f * dt; // 滚动摩擦
            }
        }
        // 左右边界反弹
        if (p.x - radius < minX) { p.x = minX + radius; _vel.x = Mathf.Abs(_vel.x) * 0.8f; AudioManager.PlayAt(Sfx.BallBounce, p); }
        if (p.x + radius > maxX) { p.x = maxX - radius; _vel.x = -Mathf.Abs(_vel.x) * 0.8f; AudioManager.PlayAt(Sfx.BallBounce, p); }
        transform.position = p;

        // 拖尾（有明显速度才拖；残影自带生命周期，球没了也不残留）
        if (_vel.sqrMagnitude > 1.2f)
            _trail.Tick(transform.position, _sr.sprite,
                        new Color(_sr.color.r, _sr.color.g, _sr.color.b, 1f) * 0.7f,
                        radius * 2.1f);

        // 滚动自旋
        _spin -= _vel.x / Mathf.Max(radius, 0.01f) * dt * Mathf.Rad2Deg;
        transform.rotation = Quaternion.Euler(0f, 0f, _spin);
    }

    /// <summary>双食指屋檐：两指尖（EMA 平滑）构成线段，接近水平 + 够宽才有效。
    /// 垫中条件（全部满足）：① 手势有效 ② 球心横向落在线段内 ③ 球心在线段上方且压入深度 ≤ roofHitDepth
    /// ④ 球正在明显下落（vy < -0.5）——四条缺一不可，杜绝"碰到空气自己跳"</summary>
    private void UpdateRoof(float dt)
    {
        bool gestureOk = false;
        Vector2 a = Vector2.zero, b = Vector2.zero;

        if (rayProvider != null && rayProvider.HasTwoTips)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 wa = cam.ScreenToWorldPoint(new Vector3(rayProvider.TipScreenA.x, rayProvider.TipScreenA.y, -cam.transform.position.z));
                Vector3 wb = cam.ScreenToWorldPoint(new Vector3(rayProvider.TipScreenB.x, rayProvider.TipScreenB.y, -cam.transform.position.z));
                // EMA 平滑（手指追踪抖动 → 平滑屋檐）
                _roofA = _roofTracked ? Vector2.Lerp(_roofA, (Vector2)wa, 0.35f) : wa;
                _roofB = _roofTracked ? Vector2.Lerp(_roofB, (Vector2)wb, 0.35f) : wb;
                _roofTracked = true;
                a = _roofA; b = _roofB;

                float width = Vector2.Distance(a, b);
                float tilt = Vector2.Angle(b - a, Vector2.right);
                gestureOk = width >= roofMinWidth && tilt <= roofMaxTiltDeg;
            }
        }
        else _roofTracked = false;

        // 屋檐提示线：手势有效就显示，玩家看得见"垫在哪"
        ShowRoofLine(a, b, gestureOk);
        UpdateFlash(dt);

        if (!gestureOk || _roofCd > 0f) return;

        // 球心在线段上的投影
        Vector2 c = transform.position;
        Vector2 ab = b - a;
        float segLen = ab.magnitude;
        if (segLen < 0.001f) return;
        float t = Vector2.Dot(c - a, ab) / (segLen * segLen);
        if (t < -0.1f || t > 1.1f) return;                                // ①横向不在屋檐上
        Vector2 proj = a + ab * Mathf.Clamp01(t);

        // 真实几何：球是圆、屋檐是线段 → 用球心到线段的有符号距离判接触
        Vector2 tangent = ab / segLen;                                    // 屋檐方向
        Vector2 normal = new Vector2(-tangent.y, tangent.x);              // 屋檐法线
        if (normal.y < 0f) normal = -normal;                              // 法线统一朝上
        float dist = Vector2.Dot(c - proj, normal);                       // 球心在法线上的距离
        if (dist > radius + roofHitDepth) return;                         // ②还没压到屋檐
        if (dist < -radius) return;                                       // ③已经穿到屋檐下方（漏球）

        // 沿法线的接近速度：只有球还在压向屋檐才反弹；静止球(vn≈0)也可被垫起
        float vn = Vector2.Dot(_vel, normal);
        if (vn > 0.35f) return;                                           // ④已经在离开屋檐，避免重复弹

        // —— 真实反射：v' = v - 2(v·n)n，再做手部发力与切向保留 ——
        Vector2 reflected = _vel - 2f * vn * normal;
        reflected += normal * roofBounceSpeed;                            // 手部沿法线的发力
        Vector2 vt = tangent * Vector2.Dot(_vel, tangent);                // 原切向分量
        reflected += vt * (roofTangentialKeep - 1f);                      // 只在保留比例<1时衰减切向
        // 给出一个最小向上速度，确保静止在地上的球一定被垫起来
        if (reflected.y < roofBounceSpeed * 0.6f)
            reflected.y = roofBounceSpeed * 0.6f;
        if (reflected.sqrMagnitude > roofMaxSpeed * roofMaxSpeed)
            reflected = reflected.normalized * roofMaxSpeed;

        Bounce(reflected, byDino: false);
        AudioManager.PlayAt(Sfx.BallKick, transform.position);
        // 贴到接触点：球心 = 投影点 + 法线 × 半径（不会突然瞬移）
        transform.position = new Vector3(proj.x + normal.x * radius,
                                         proj.y + normal.y * radius, 0f);

        // 垫球闪光：金线在接触位置亮 0.3s（直观看到是靠这里垫的）
        FlashRoof(a, b);
        _trail.Emit(transform.position, _sr.sprite,
                    new Color(_sr.color.r, _sr.color.g, _sr.color.b, 1f), radius * 2.1f);
    }

    private Vector3 _flashScale = Vector3.one;
    private float _flashTimer;

    /// <summary>垫中瞬间：金线落在屋檐位置并开始 0.3s 淡出</summary>
    private void FlashRoof(Vector2 a, Vector2 b)
    {
        if (_roofFlash == null) return;
        Vector2 mid = (a + b) * 0.5f;
        _flashScale = new Vector3(Vector2.Distance(a, b), 0.14f, 1f);
        _roofFlash.gameObject.SetActive(true);
        _roofFlash.position = new Vector3(mid.x, mid.y, 0f);
        _roofFlash.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);
        _roofFlash.localScale = _flashScale;
        _flashTimer = 0.3f;
    }

    /// <summary>闪光线独立淡出（不依赖屋檐手势还在不在）</summary>
    private void UpdateFlash(float dt)
    {
        if (_flashTimer <= 0f || _roofFlash == null) return;
        _flashTimer -= dt;
        float k = Mathf.Clamp01(_flashTimer / 0.3f);
        var sr = _roofFlash.GetComponent<SpriteRenderer>();
        if (k <= 0f || sr == null) { _roofFlash.gameObject.SetActive(false); return; }
        sr.color = new Color(1f, 0.9f * k + 0.1f, 0.45f, 0.95f * k);
        _roofFlash.localScale = _flashScale * (1f + (1f - k) * 0.3f); // 亮完微微扩张
    }

    /// <summary>把提示线摆在 A→B 位置（或隐藏）；只管常驻提示线，闪光走 FlashRoof</summary>
    private void ShowRoofLine(Vector2 a, Vector2 b, bool show)
    {
        if (_roofLine == null) return;
        if (!show) { _roofLine.gameObject.SetActive(false); return; }
        Vector2 mid = (a + b) * 0.5f;
        _roofLine.gameObject.SetActive(true);
        _roofLine.position = new Vector3(mid.x, mid.y, 0f);
        _roofLine.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);
        _roofLine.localScale = new Vector3(Vector2.Distance(a, b), 0.05f, 1f);
    }

    /// <summary>球被炸毁（庇护所被破坏时联动）：金色字符碎片爆散 + 销毁。
    /// 拖尾残影是自管理组件，不会跟着残留。</summary>
    public void Shatter()
    {
        if (this == null || !gameObject.activeSelf) return;
        Vector3 pos = transform.position;
        Color col = _sr != null ? _sr.color : ballColor;
        for (int i = 0; i < 8; i++)
        {
            var go = new GameObject("BallShard");
            go.transform.position = pos;
            float ang = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            var vel = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang) * 0.9f + 0.8f, 0f) * Random.Range(2.5f, 5f);
            var goShard = go.AddComponent<GlyphShard>();
            goShard.Init(LetterPlant.RandomGlyphSprite(),
                new Color(col.r, col.g, col.b, 0.95f), 0.26f, vel,
                Random.Range(-600f, 600f), 0.6f, order: 4);
        }
        if (dino != null) dino.NotifyBallDespawned(this);
        if (_roofLine != null) Destroy(_roofLine.gameObject);
        if (_roofFlash != null) Destroy(_roofFlash.gameObject);
        Destroy(gameObject);
    }

    private static int _ballCount;
    private void OnDestroy()
    {
        // 白线贴图静态共享：最后一个球销毁时才回收
        _ballCount--;
        if (_ballCount <= 0 && _whiteSprite != null) { Destroy(_whiteSprite.texture); _whiteSprite = null; }
    }
}

// —————————————————————————————————————————————————————————
// GlyphTrail / GlyphShard —— 字符残影拖尾 + 自毁字符碎片
// 并入本文件：解决 Unity 新 .cs 文件导入时序导致 CS0246 的问题。
// GlyphShard 是自管理生命周期的 MonoBehaviour（owner 先死也不会残留画面）；
// GlyphTrail 只负责按间隔发射残影。
// —————————————————————————————————————————————————————————

public class GlyphTrail
{
    private readonly float _interval;
    private float _timer;

    public GlyphTrail(float interval = 0.05f) => _interval = interval;

    /// <summary>节流发射：调用方每帧 Tick 一次即可</summary>
    public void Tick(Vector3 pos, Sprite sprite, Color color, float scale)
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = _interval;
        var go = new GameObject("TrailGhost");
        go.transform.position = pos;
        go.AddComponent<GlyphShard>().Init(sprite,
            new Color(color.r, color.g, color.b, 0.55f), scale * 0.82f,
            new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(-0.2f, 0.4f), 0f),
            0f, 0.34f);
    }

    /// <summary>立即发射一枚（不限流）</summary>
    public void Emit(Vector3 pos, Sprite sprite, Color color, float scale) => Tick(pos, sprite, color, scale);
}

/// <summary>自毁字符碎片/残影：自带重力、自旋、淡出，寿命到自毁。
/// 拖尾残影和"字符碎裂"爆散碎片共用。</summary>
public class GlyphShard : MonoBehaviour
{
    private SpriteRenderer _sr;
    private Vector3 _vel;
    private float _spin, _life, _maxLife, _startA = 1f;

    public GlyphShard Init(Sprite sprite, Color color, float scale, Vector3 vel, float spin, float life, int order = 2)
    {
        _sr = gameObject.AddComponent<SpriteRenderer>();
        _sr.sprite = sprite;
        _sr.color = color;
        _startA = color.a;
        _sr.sortingOrder = order;
        transform.localScale = Vector3.one * scale;
        _vel = vel; _spin = spin; _life = 0f; _maxLife = life;
        return this;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _life += dt;
        _vel.y -= 6f * dt;                 // 轻重力
        _vel *= 1f - 0.5f * dt;            // 阻尼
        transform.position += _vel * dt;
        transform.Rotate(0f, 0f, _spin * dt);
        if (_sr != null)
        {
            var c = _sr.color;
            c.a = Mathf.Clamp01(1f - _life / _maxLife) * _startA;
            _sr.color = c;
        }
        if (_life >= _maxLife) Destroy(gameObject);
    }
}
