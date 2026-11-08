// CorruptionZone.cs —— 紫色字符落地后生成的腐化区域
// 行为：
//   1) 区域内植物被持续 DrainEnergy 快速萎缩，且 LetterPlant 检测到腐化后停止生长输入；
//   2) 地面持续掉落紫色字符碎片（直观的"这里被腐化了"反馈）；
//   3) 净化改为"雨淋暴露时长"：只有 GlyphRain.RainFalling=true 时才累计净化时间，
//      累计到 purifyNeed（秒）后区域消散；
//   4) 腐化期间区域内不能生长东西（LetterPlant 端屏蔽能量增益）。
// 视觉：径向渐变紫圆 + 脉动 + 下落字符碎片。

using System.Collections.Generic;
using UnityEngine;

public class CorruptionZone : MonoBehaviour
{
    [Header("腐化参数（由 ThreatSystem 生成时拷贝模板值，也可直接调这里）")]
    public float radius = 1.6f;           // 覆盖半径（世界单位）
    [Tooltip("每秒扣掉的植物能量（1=满能量）。设大一点让树快速萎缩")]
    public float witherRate = 0.6f;
    public float duration = 30f;          // 最长存在时间（兜底自然消散）
    [Tooltip("净化需要雨淋暴露的时长（秒）")]
    public float purifyNeed = 6f;
    public Color zoneColor = new Color(0.62f, 0.28f, 1f, 0.35f);

    [Header("地面紫色字符雨")]
    [Tooltip("每秒生成多少个紫色字符碎片")]
    public float glyphRainRate = 14f;
    [Tooltip("字符碎片下落速度")]
    public float glyphFallSpeed = 3.2f;
    public Color glyphColor = new Color(0.72f, 0.34f, 1f, 0.9f);

    private float _purifyTimer;           // 雨淋暴露累计时长（秒）
    private float _life;
    private SpriteRenderer _sr;
    private LetterAlphabetBar _bar;
    private float _fade = 1f;             // 消散动画（<1 开始淡出）
    private bool _dying;
    private float _glyphSpawnAcc;

    // —— 静态注册表：GlyphRain 落地净化查询走这里，不做全局搜索 ——
    private static readonly List<CorruptionZone> _zones = new List<CorruptionZone>();
    public static IReadOnlyList<CorruptionZone> Zones => _zones;

    /// <summary>由 GlyphRain 每帧设置：当前是否有雨正在下（腐化区据此累计净化时长）</summary>
    public static bool RainFalling;

    public float PurifyProgress => Mathf.Clamp01(_purifyTimer / Mathf.Max(purifyNeed, 0.01f));

    /// <summary>某世界坐标是否处于任意活跃腐化区内（LetterPlant 用它屏蔽生长）</summary>
    public static bool IsPositionCorrupted(Vector3 worldPos)
    {
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null) { _zones.RemoveAt(i); continue; }
            if (!z._dying && Mathf.Abs(worldPos.x - z.transform.position.x) <= z.radius)
                return true;
        }
        return false;
    }

    /// <summary>雨滴落地命中腐化区：返回 true（让 GlyphRain 播紫色水花），不再累计水量，
    /// 净化改由 Update 里的雨淋时长驱动。</summary>
    public static bool PurifyAt(Vector3 worldPos, float amount)
    {
        for (int i = _zones.Count - 1; i >= 0; i--)
        {
            var z = _zones[i];
            if (z == null) { _zones.RemoveAt(i); continue; }
            if (!z._dying && Mathf.Abs(worldPos.x - z.transform.position.x) <= z.radius)
                return true;
        }
        return false;
    }

    public void Init(LetterAlphabetBar barSource, float radiusWorld, float wither, float dur, float need)
    {
        _bar = barSource;
        radius = radiusWorld; witherRate = wither; duration = dur; purifyNeed = need;
    }

    private void Awake()
    {
        _sr = gameObject.AddComponent<SpriteRenderer>();
        _sr.sprite = ZoneSprite;
        _sr.color = zoneColor;
        _sr.sortingOrder = 2; // 恐龙/巢(1)之上、前景字符(3)之下
        transform.localScale = Vector3.one * (radius * 2f);
    }

    private void OnEnable() => _zones.Add(this);
    private void OnDisable() => _zones.Remove(this);

    private void Update()
    {
        float dt = Time.deltaTime;
        _life += dt;

        if (!_dying)
        {
            // 腐化覆盖的植物：扣能量（植物自身凋亡规则负责视觉）
            if (_bar != null && _bar.Plants != null)
                foreach (var plant in _bar.Plants)
                {
                    if (plant == null) continue;
                    if (plant.WorldHitsPlant(transform.position, radius))
                        plant.DrainEnergy(witherRate * dt);
                }

            // 雨淋暴露 → 累计净化时长
            if (RainFalling) _purifyTimer += dt;

            // 地面紫色字符雨：在区域范围内随机掉落紫色字符碎片
            _glyphSpawnAcc += glyphRainRate * dt;
            while (_glyphSpawnAcc >= 1f)
            {
                _glyphSpawnAcc -= 1f;
                SpawnGlyphShard();
            }

            // 视觉：净化进度越高越淡 + 轻微脉动
            float pulse = 1f + Mathf.Sin(_life * 3.2f) * 0.06f;
            float vis = Mathf.Lerp(1f, 0.3f, PurifyProgress);
            _sr.color = new Color(zoneColor.r, zoneColor.g, zoneColor.b, zoneColor.a * vis) * Mathf.Max(_fade, 0f);
            transform.localScale = Vector3.one * (radius * 2f * pulse * Mathf.Lerp(1f, 0.7f, PurifyProgress));

            if (_purifyTimer >= purifyNeed || _life >= duration) _dying = true;
        }
        else
        {
            // 消散：快速放大 + 淡出
            _fade -= dt * 2.2f;
            transform.localScale *= 1f + dt * 1.4f;
            var c = _sr.color; c.a = Mathf.Max(0f, _fade * 0.4f); _sr.color = c;
            if (_fade <= 0f) { _zones.Remove(this); Destroy(gameObject); }
        }
    }

    /// <summary>在区域范围内随机位置生成一个紫色字符碎片，从区域顶部下落</summary>
    private void SpawnGlyphShard()
    {
        float x = transform.position.x + Random.Range(-radius, radius);
        float topY = transform.position.y + radius * 0.6f;
        var go = new GameObject("CorruptGlyph");
        go.transform.position = new Vector3(x, topY, 0f);
        var shard = go.AddComponent<GlyphShard>();
        shard.Init(LetterPlant.RandomGlyphSprite(),
            new Color(glyphColor.r, glyphColor.g, glyphColor.b, glyphColor.a),
            Random.Range(0.18f, 0.3f),
            new Vector3(Random.Range(-0.4f, 0.4f), -glyphFallSpeed, 0f),
            Random.Range(-180f, 180f),
            Random.Range(0.5f, 1.1f),
            order: 2);
    }

    private void OnDestroy()
    {
        _zones.Remove(this);
        if (_zoneTex != null) { Destroy(_zoneTex); _zoneTex = null; }
    }

    // —— 程序化径向渐变圆贴图（静态共享） ——
    private static Texture2D _zoneTex;
    private static Sprite _zoneSprite;
    private static Sprite ZoneSprite
    {
        get
        {
            if (_zoneSprite != null) return _zoneSprite;
            const int n = 64;
            _zoneTex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            float c0 = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Max(Mathf.Abs(x - c0), Mathf.Abs(y - c0)) / c0; // 方形渐变（贴合字符格）
                float a = Mathf.Clamp01((1f - d) / 0.85f);
                a = a * a * (3f - 2f * a); // smoothstep
                px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            _zoneTex.SetPixels32(px);
            _zoneTex.Apply(false, true);
            _zoneSprite = Sprite.Create(_zoneTex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n); // 1×1 世界单位
            return _zoneSprite;
        }
    }
}
