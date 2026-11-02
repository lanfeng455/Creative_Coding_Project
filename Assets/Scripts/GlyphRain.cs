// GlyphRain.cs —— 乱码雨（可带倾角） + 双食指"屋檐"遮挡
// 挂在 LetterAlphabetBar 同一物体上，雨滴落在 bar 区域内：
//   - 雨滴从 bar 顶（分割线）上方生成，沿 dropAngleDeg 指定的方向下落（默认略偏，避免死板）
//   - 两根食指尖构成屋檐：缝隙之外的雨滴落到指尖高度时被挡住（销毁）
//   - 缝隙之内的雨滴穿过，落到字母行 → 调用 bar.WaterAtLocalX 浇灌
// 启用与否由 LetterAlphabetBar.playMode 驱动（本组件只看 rainEnabled）

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class GlyphRain : MonoBehaviour
{
    [Header("引用（留空自动 Find）")]
    public LetterAlphabetBar bar;
    public RightHandIndexRayProvider rayProvider;

    [Header("雨参数")]
    [Tooltip("每秒生成的雨滴数")]
    public float spawnRate = 26f;
    [Tooltip("下落速度（像素/秒）")]
    public float fallSpeedPx = 420f;
    [Tooltip("雨滴相对竖直方向的倾角（度）：0=完全竖直，正值向右飘，负值向左飘。建议 5~15，太大会像斜雨")]
    [Range(-60f, 60f)] public float dropAngleDeg = 10f;
    public int fontSize = 20;
    public Color glyphColor = new Color(0.65f, 1f, 0.65f, 0.9f);
    [Tooltip("随机字符池")]
    public string glyphPool = "01アイウエオカキクケコサシスセソタチツテトナニヌネノ∴∵※□△◇";
    [Tooltip("雨滴头部颜色（比拖尾亮，黑客雨那种发白的头）")]
    public Color headColor = new Color(0.85f, 1f, 0.9f, 1f);

    [Header("黑客风拖尾")]
    [Tooltip("每个雨滴最多拖几个渐隐字符（0=关闭拖尾，退回单字符）。每个雨滴会在 1~该值之间随机取长度")]
    public int trailLength = 6;
    [Tooltip("拖尾字符纵向间距（按字号倍数）")]
    public float trailSpacingFactor = 1.05f;
    [Tooltip("拖尾逐级透明度衰减：第 i 个影子 alpha = 头部 alpha × 该值的 i 次方")]
    [Range(0.1f, 1f)] public float trailFade = 0.72f;
    [Tooltip("每秒随机换字次数（0=不换，静止拖尾）")]
    public float glyphFlickerRate = 2.5f;

    [Header("浇灌")]
    [Tooltip("雨滴落地时浇灌的横向扩散（高斯 σ，像素）")]
    public float splashSigmaPx = 30f;
    [Tooltip("每滴雨给落点字母的热量（积累成生长输入）")]
    public float waterPerDrop = 0.35f;

    [Header("状态（由 LetterAlphabetBar 驱动，勿手改）")]
    public bool rainEnabled = false;

    [Header("屋檐可视化（双食指位置反馈）")]
    public Color leftTipColor = new Color(0.4f, 0.9f, 1f, 1f);
    public Color rightTipColor = new Color(1f, 0.65f, 0.35f, 1f);
    public Color roofColor = new Color(1f, 1f, 1f, 0.55f);
    public float tipDotSize = 18f;
    public float roofThickness = 3f;

    // 屋檐视觉：两条横线 + 两个指尖点
    private Image _leftRoof, _rightRoof, _leftDot, _rightDot;
    private bool _roofShown;

    [Header("擦除（屋檐举到雨区上方时）")]
    [Tooltip("指尖举到分割线（bar 顶边）以上时不再遮挡雨，改为用指尖方块擦掉已有的果实")]
    public bool eraserEnabled = true;
    [Tooltip("擦除半径（像素）")]
    public float eraseRadiusPx = 30f;

    private readonly List<Vector2> _erasePoints = new List<Vector2>();

    [Header("水花（雨滴落地/撞屋檐的反馈）")]
    public Color landSplashColor = new Color(0.75f, 1f, 0.75f, 0.9f);
    public Color roofSplashColor = new Color(1f, 1f, 1f, 0.8f);
    [Tooltip("每次水花炸出的碎粒数")]
    public int splashParticles = 7;
    [Tooltip("碎粒初始速度（像素/秒）")]
    public float splashSpeed = 260f;
    [Tooltip("碎粒边长（像素）")]
    public float splashParticleSize = 5f;
    public float splashDuration = 0.3f;

    private class SplashParticle
    {
        public RectTransform rect; public Image img; public Color color;
        public Vector2 pos, vel; public float t; public bool alive;
    }
    private readonly List<SplashParticle> _splashes = new List<SplashParticle>();

    private class Drop
    {
        public RectTransform root;          // 容器：整体移动，不动子物体
        public TextMeshProUGUI head;        // 头（最下方，最亮）
        public TextMeshProUGUI[] trail;     // 拖尾（向上排列，逐级变淡）
        public int trailCount;              // 本次使用的拖尾长度（1~trailLength 随机）
        public float x, y;                  // bar 局部坐标（头的位置）
        public float flickerAcc;
        public bool alive;
    }

    private readonly List<Drop> _pool = new List<Drop>();
    private RectTransform _rect;
    private RectTransform _rainLayer;   // 雨滴容器（带 RectMask2D，拖尾不会溢出到分割线上方）
    private Canvas _canvas;
    private TMP_FontAsset _font;
    private float _spawnAcc;
    private System.Random _rng;

    // 当前下落方向（单位向量，由 dropAngleDeg 换算；默认竖直向下）
    private float _dirX = 0f, _dirY = -1f;

    // 指尖屋檐的 bar 局部坐标缓存（丢手时保持最后位置）
    private Vector2 _roofLeftLocal, _roofRightLocal;
    private bool _roofValid;
    private bool _singleMode; // 单手屋檐：缝隙 = 屏幕边缘 → 指尖

    private void Awake()
    {
        _rect = GetComponent<RectTransform>();
        _canvas = GetComponentInParent<Canvas>();
        _rng = new System.Random();

        _font = TMP_Settings.defaultFontAsset;
        if (_font == null) _font = Resources.Load<TMP_FontAsset>("Fonts/LiberationSans SDF");

        // 清理上次 Play 残留的雨滴（含容器）
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (c.name.StartsWith("Drop_") || c.name.StartsWith("Splash_") || c.name.StartsWith("RainLayer"))
                Destroy(c.gameObject);
        }
        _pool.Clear();
        _splashes.Clear();

        BuildRainLayer();
        BuildRoofVisuals();
    }

    /// <summary>雨滴容器：和 bar 同尺寸并带 RectMask2D，拖尾超出 bar 的部分自动裁掉（不会压到分割线上方的果实）</summary>
    private void BuildRainLayer()
    {
        var go = new GameObject("RainLayer", typeof(RectTransform), typeof(RectMask2D));
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        _rainLayer = rt;
    }

    /// <summary>创建屋檐视觉：两条纯色横线（屋檐）+ 两个指尖点。无 sprite 的 Image 就是纯色矩形</summary>
    private void BuildRoofVisuals()
    {
        _leftRoof = MakeBarImage("RoofLeft", roofColor);
        _rightRoof = MakeBarImage("RoofRight", roofColor);
        _leftDot = MakeBarImage("TipDotLeft", leftTipColor);
        _rightDot = MakeBarImage("TipDotRight", rightTipColor);

        foreach (var img in new[] { _leftRoof, _rightRoof })
        {
            img.rectTransform.pivot = new Vector2(0f, 0.5f); // 左端对齐，用 sizeDelta.x 当长度
        }
        foreach (var img in new[] { _leftDot, _rightDot })
        {
            img.rectTransform.sizeDelta = new Vector2(tipDotSize, tipDotSize);
        }
        foreach (var img in new[] { _leftRoof, _rightRoof, _leftDot, _rightDot })
            img.gameObject.SetActive(false); // 初始隐藏（_roofShown 默认 false，不能直接走 SetRoofVisible）
    }

    private Image MakeBarImage(string name, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private void SetRoofVisible(bool v)
    {
        if (_roofShown == v) return;
        _roofShown = v;
        foreach (var img in new[] { _leftRoof, _rightRoof, _leftDot, _rightDot })
        {
            if (img == null) continue;
            img.gameObject.SetActive(v);
            if (v) img.transform.SetAsLastSibling(); // 显示时置顶，不被雨滴盖住
        }
    }

    private void Start()
    {
        if (bar == null) bar = GetComponent<LetterAlphabetBar>();
        if (rayProvider == null) rayProvider = FindObjectOfType<RightHandIndexRayProvider>();
    }

    private void Update()
    {
        if (!rainEnabled)
        {
            // 停用时清空所有雨滴和水花
            foreach (var d in _pool)
                if (d.alive) { d.alive = false; d.root.gameObject.SetActive(false); }
            foreach (var s in _splashes)
                if (s.alive) { s.alive = false; s.rect.gameObject.SetActive(false); }
            _spawnAcc = 0f;
            SetRoofVisible(false);
            return;
        }

        UpdateRoof();
        UpdateRoofVisuals();
        EraseFruits();

        // —— 下落方向（倾角） ——
        UpdateFallDirection();

        // —— 生成 ——
        _spawnAcc += spawnRate * Time.deltaTime;
        while (_spawnAcc >= 1f)
        {
            _spawnAcc -= 1f;
            SpawnDrop();
        }

        // —— 下落 + 遮挡 + 落地 ——
        var r = _rect.rect;
        float floorY = r.yMin + bar.letterRowHeight;  // 字母行顶
        float dt = Time.deltaTime;

        foreach (var d in _pool)
        {
            if (!d.alive) continue;

            d.x += _dirX * fallSpeedPx * dt;
            d.y += _dirY * fallSpeedPx * dt;

            // 飘出 bar 左右边界 → 回收（已被 RectMask2D 裁掉，屏幕上看不见，不给水花）
            if (d.x < r.xMin - fontSize || d.x > r.xMax + fontSize)
            {
                Kill(d);
                continue;
            }

            // 屋檐遮挡：缝隙之外且在屋檐线以下 → 挡住销毁。
            // 用位置判定而非穿越判定：屋檐会随手上下移动，穿越判定会漏掉"被屋檐跳过"的雨滴
            if (_roofValid)
            {
                float roofY = float.MinValue;
                if (d.x < _roofLeftLocal.x) roofY = _roofLeftLocal.y;
                else if (d.x > _roofRightLocal.x) roofY = _roofRightLocal.y;
                // 缝隙内 roofY 保持 MinValue → 永不遮挡
                // 屋檐举到分割线以上（雨区上方）→ 此侧不遮挡，改为擦除果实
                if (!IsRoofAboveRain(roofY) && d.y <= roofY)
                {
                    SpawnSplash(d.x, roofY, roofSplashColor, -60f); // 撞屋檐 → 白花向下滴
                    Kill(d);
                    continue;
                }
            }

            // 落地 → 浇灌
            if (d.y <= floorY)
            {
                if (bar != null) bar.WaterAtLocalX(d.x, waterPerDrop, splashSigmaPx);
                SpawnSplash(d.x, floorY, landSplashColor, 160f); // 落地 → 绿花向上弹
                Kill(d);
                continue;
            }

            UpdateDrop(dt, d);
        }

        UpdateSplashes(dt);
    }

    /// <summary>在 bar 局部坐标 (x,y) 炸开一朵水花：碎粒随机方向爆发 + 重力下坠 + 淡出。yBias 给初速度一个偏置（落地向上弹/屋檐向下滴）</summary>
    private void SpawnSplash(float x, float y, Color color, float yBias)
    {
        for (int i = 0; i < splashParticles; i++)
        {
            var p = GetFreeParticle();
            float ang = (float)(_rng.NextDouble() * Mathf.PI * 2.0);
            float spd = splashSpeed * (0.4f + 0.6f * (float)_rng.NextDouble());
            p.pos = new Vector2(x, y);
            p.vel = new Vector2(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd + yBias);
            p.t = 0f;
            p.color = color;
            p.alive = true;
            p.img.color = color;
            p.rect.sizeDelta = new Vector2(splashParticleSize, splashParticleSize);
            p.rect.anchoredPosition = LocalToAnchored(x, y);
            p.rect.gameObject.SetActive(true);
            p.rect.SetAsLastSibling();
        }
    }

    private SplashParticle GetFreeParticle()
    {
        foreach (var p in _splashes)
            if (!p.alive) return p;

        var go = new GameObject($"Splash_{_splashes.Count}", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        var np = new SplashParticle { rect = rt, img = go.GetComponent<Image>() };
        np.img.raycastTarget = false;
        _splashes.Add(np);
        return np;
    }

    private void UpdateSplashes(float dt)
    {
        foreach (var p in _splashes)
        {
            if (!p.alive) continue;
            p.t += dt;
            float k = p.t / Mathf.Max(splashDuration, 0.01f);
            if (k >= 1f)
            {
                p.alive = false;
                p.rect.gameObject.SetActive(false);
                continue;
            }

            // 阻力 + 重力 → 炸裂后散落
            p.vel *= Mathf.Exp(-4f * dt);
            p.vel.y -= 700f * dt;
            p.pos += p.vel * dt;

            p.rect.anchoredPosition = LocalToAnchored(p.pos.x, p.pos.y);
            float size = splashParticleSize * (1f - 0.6f * k);
            p.rect.sizeDelta = new Vector2(size, size);
            var c = p.color;
            c.a *= 1f - k;
            p.img.color = c;
        }
    }

    /// <summary>把双食指尖（屏幕坐标）转成 bar 局部坐标缓存起来</summary>
    private void UpdateRoof()
    {
        _erasePoints.Clear();
        if (rayProvider == null) return;

        Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? _canvas.worldCamera : null;
        var r = _rect.rect;

        if (rayProvider.HasTwoTips
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, rayProvider.TipScreenA, cam, out Vector2 a)
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, rayProvider.TipScreenB, cam, out Vector2 b))
        {
            // 双手：缝隙 = 两指尖之间
            _roofLeftLocal = a;
            _roofRightLocal = b;
            _roofValid = true;
            _singleMode = false;
            CollectErasePoint(a, r);
            CollectErasePoint(b, r);
        }
        else if (rayProvider.HasOneTip
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, rayProvider.TipScreenSingle, cam, out Vector2 s))
        {
            // 单手：指尖充当一侧屋檐，缝隙 = 最近屏幕边缘 → 指尖
            // 指尖在左半屏 → 开放 [左缘, 指尖]（够 A）；右半屏 → 开放 [指尖, 右缘]（够 Z）
            if (s.x < r.center.x)
            {
                _roofLeftLocal = new Vector2(r.xMin - 200f, s.y); // 左边界推到 bar 外 = 左侧不挡
                _roofRightLocal = s;
            }
            else
            {
                _roofLeftLocal = s;
                _roofRightLocal = new Vector2(r.xMax + 200f, s.y); // 右侧不挡
            }
            _roofValid = true;
            _singleMode = true;
            CollectErasePoint(s, r);
        }
        else if (_roofValid)
        {
            // 手全丢 → 屋檐闭合（保持最后位置，缝隙视为零宽）
            float midX = (_roofLeftLocal.x + _roofRightLocal.x) * 0.5f;
            _roofLeftLocal.x = midX;
            _roofRightLocal.x = midX;
            _singleMode = false;
        }
    }

    /// <summary>指尖高于分割线（bar 顶边 = 雨区上方）→ 该指尖进入擦除模式，不再是屋檐</summary>
    private void CollectErasePoint(Vector2 tipLocal, Rect r)
    {
        if (eraserEnabled && tipLocal.y >= r.yMax)
            _erasePoints.Add(tipLocal);
    }

    /// <summary>某个屋檐高度是否已举到雨区上方（此侧不再遮挡雨）</summary>
    private bool IsRoofAboveRain(float roofY)
    {
        return roofY >= _rect.rect.yMax - 1f;
    }

    /// <summary>当前缝隙的 bar 局部 x 区间（双手/单手都适用；单手时一侧会超出 bar 边缘）。
    /// 屋檐举到雨区上方时不再是屋檐，返回 false（此时只做擦除，不提供缝隙浇灌）</summary>
    public bool TryGetGapLocal(out float gapL, out float gapR)
    {
        gapL = _roofLeftLocal.x;
        gapR = _roofRightLocal.x;
        return _roofValid
            && !IsRoofAboveRain(_roofLeftLocal.y)
            && !IsRoofAboveRain(_roofRightLocal.y);
    }

    /// <summary>每帧把屋檐线/指尖点摆到当前屋檐位置</summary>
    private void UpdateRoofVisuals()
    {
        if (!_roofValid)
        {
            SetRoofVisible(false);
            return;
        }
        SetRoofVisible(true);

        var r = _rect.rect;
        PlaceHLine(_leftRoof, r.xMin, _roofLeftLocal.x, _roofLeftLocal.y);
        PlaceHLine(_rightRoof, _roofRightLocal.x, r.xMax, _roofRightLocal.y);
        _leftDot.rectTransform.anchoredPosition = LocalToAnchored(_roofLeftLocal.x, _roofLeftLocal.y);
        _rightDot.rectTransform.anchoredPosition = LocalToAnchored(_roofRightLocal.x, _roofRightLocal.y);

        // 单手模式：只显示真实存在的那个指尖点（开放侧的点在 bar 外，藏起来）
        bool leftOpen = _singleMode && _roofLeftLocal.x < r.xMin;
        bool rightOpen = _singleMode && _roofRightLocal.x > r.xMax;
        _leftDot.gameObject.SetActive(!leftOpen);
        _rightDot.gameObject.SetActive(!rightOpen);

        // 屋檐举到雨区上方时不再是遮挡物 → 隐藏该侧遮挡线，只留指尖方块当橡皮擦
        _leftRoof.gameObject.SetActive(!IsRoofAboveRain(_roofLeftLocal.y));
        _rightRoof.gameObject.SetActive(!IsRoofAboveRain(_roofRightLocal.y));
    }

    /// <summary>屋檐举到雨区上方时，用指尖方块擦掉已有的果实</summary>
    private void EraseFruits()
    {
        if (!eraserEnabled || bar == null || _erasePoints.Count == 0) return;

        foreach (var p in _erasePoints)
        {
            if (bar.EraseFruitAt(p, eraseRadiusPx))
                SpawnSplash(p.x, p.y, roofSplashColor, -40f); // 擦除反馈：白花
        }
    }

    /// <summary>摆一条水平屋檐线：从 x0 到 x1，高度 y（bar 局部坐标）</summary>
    private void PlaceHLine(Image img, float x0, float x1, float y)
    {
        var rt = img.rectTransform;
        rt.anchoredPosition = LocalToAnchored(x0, y);
        rt.sizeDelta = new Vector2(Mathf.Max(x1 - x0, 0f), roofThickness);
    }

    /// <summary>把倾角（相对竖直方向，正值向右飘）换算成单位下落向量</summary>
    private void UpdateFallDirection()
    {
        float ang = dropAngleDeg * Mathf.Deg2Rad;
        _dirX = Mathf.Sin(ang);
        _dirY = -Mathf.Cos(ang);
    }

    /// <summary>雨滴从顶部落到 bar 底部期间，横向会飘多远（像素）</summary>
    private float DriftOverBarHeight(Rect r)
    {
        float fallHeight = Mathf.Max(1f, (r.yMax + fontSize) - r.yMin);
        float tanAngle = Mathf.Abs(_dirX) / Mathf.Max(0.01f, Mathf.Abs(_dirY));
        return tanAngle * fallHeight;
    }

    private void SpawnDrop()
    {
        var r = _rect.rect;
        var d = GetFreeDrop();

        // 带倾角时向"上风口"扩展生成范围：斜着飘出去的雨滴会被回收，扩展后整条雨区仍均匀有雨
        float drift = DriftOverBarHeight(r);
        float spawnXMin = r.xMin + Mathf.Min(0f, -_dirX * drift);
        float spawnXMax = r.xMax + Mathf.Max(0f, -_dirX * drift);

        d.x = Mathf.Lerp(spawnXMin, spawnXMax, (float)_rng.NextDouble());
        d.y = r.yMax + fontSize; // 从分割线上方落进来
        d.alive = true;
        d.flickerAcc = 0f;
        d.root.gameObject.SetActive(true);
        d.root.anchoredPosition = LocalToAnchored(d.x, d.y);

        // 头部最亮，拖尾在身后逐级变淡（每个雨滴随机长度，避免整齐划一）
        d.head.text = RandomGlyph();
        d.head.color = headColor;

        int maxTrail = d.trail.Length;
        d.trailCount = maxTrail <= 0 ? 0 : 1 + _rng.Next(maxTrail); // 1..maxTrail
        float spacing = fontSize * trailSpacingFactor;
        for (int i = 0; i < maxTrail; i++)
        {
            var t = d.trail[i];
            bool used = i < d.trailCount;
            t.gameObject.SetActive(used);
            if (!used) continue;

            t.text = RandomGlyph();
            var c = glyphColor;
            c.a *= Mathf.Pow(trailFade, i + 1);
            t.color = c;
            t.rectTransform.anchoredPosition = new Vector2(-_dirX, -_dirY) * (spacing * (i + 1)); // 沿下落反方向排（头在最前）
        }
    }

    private string RandomGlyph()
    {
        return glyphPool[_rng.Next(glyphPool.Length)].ToString();
    }

    private void UpdateDrop(float dt, Drop d)
    {
        // 整体移动（子物体相对偏移不动）
        d.root.anchoredPosition = LocalToAnchored(d.x, d.y);

        // 黑客雨那种字符随机跳变
        if (glyphFlickerRate <= 0f || d.trailCount <= 0) return;
        d.flickerAcc += dt;
        float interval = 1f / glyphFlickerRate;
        if (d.flickerAcc < interval) return;
        d.flickerAcc = 0f;
        d.trail[_rng.Next(d.trailCount)].text = RandomGlyph();
    }

    private Drop GetFreeDrop()
    {
        foreach (var d in _pool)
            if (!d.alive) return d;

        // 容器 + 头 + trailLength 个拖尾字符（拖尾过多/过少的雨滴用 SetActive 控制）
        var rootGo = new GameObject($"Drop_{_pool.Count}", typeof(RectTransform));
        rootGo.transform.SetParent(_rainLayer != null ? _rainLayer : transform, false);
        rootGo.layer = gameObject.layer;
        var rootRect = (RectTransform)rootGo.transform;
        rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = new Vector2(fontSize * 1.5f, fontSize * 1.5f);

        var head = CreateDropGlyph(rootRect, "Head");
        var trail = new TextMeshProUGUI[Mathf.Max(0, trailLength)];
        for (int i = 0; i < trail.Length; i++)
            trail[i] = CreateDropGlyph(rootRect, $"Trail_{i}");

        var drop = new Drop { root = rootRect, head = head, trail = trail };
        _pool.Add(drop);
        return drop;
    }

    private TextMeshProUGUI CreateDropGlyph(RectTransform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.layer = gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(fontSize * 1.5f, fontSize * 1.5f);
        rect.anchoredPosition = Vector2.zero;

        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.font = _font;
        tmp.fontSize = fontSize;
        tmp.color = glyphColor;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return tmp;
    }

    private void Kill(Drop d)
    {
        d.alive = false;
        d.root.gameObject.SetActive(false);
    }

    /// <summary>bar 局部坐标（pivot 原点）→ 以 bar 中心为锚的 anchoredPosition</summary>
    private Vector2 LocalToAnchored(float x, float y)
    {
        var r = _rect.rect;
        return new Vector2(x - r.center.x, y - r.center.y);
    }
}
