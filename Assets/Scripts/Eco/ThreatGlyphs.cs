// ThreatGlyphs.cs —— 威胁字符系统（红色追踪字符 + 紫色腐化字符）
// 红色：从屏幕上方生成后追踪恐龙（限转向速率的 homing）；撞到恐龙 → 恐龙受惊（不致死）；
//       撞到庇护所 → 巢耐久受损，耐久归零 → 巢被破坏（恐龙 NotifyNestDestroyed）。
// 紫色：竖直下落，落地生成 CorruptionZone（区域参数由本组件模板值传入）。
// 两种字符都会被玩家的双食指屋檐挡住（复用 GlyphRain.RoofBlocksWorld 的同一判定），
// 被挡时弹开 + 白闪淡出 —— 玩家能直观看到"攻击被阻止"。
// 恐龙感知威胁通过节流广播（NotifyThreatNearby / NotifyThreatCleared），不轮询。

using System.Collections.Generic;
using UnityEngine;

public class ThreatSystem : MonoBehaviour
{
    [Header("引用（留空自动找一次）")]
    public DinoAgent dino;
    public LetterAlphabetBar bar;
    public GlyphRain rain;

    [Header("红色字符")]
    public int redBatchCount = 4;             // 一次威胁事件总共生成几只
    public float redSpeed = 2.7f;             // 移动速度
    public float redTurnRate = 3.4f;          // 追踪转向速率（rad/s），越小越"笨"
    public float redLifeMax = 20f;            // 单只寿命
    public float redHitRadius = 0.32f;        // 命中恐龙的判定半径
    [Tooltip("生成高度 = 雨区顶边向下这个偏移（与乱码雨同区，不从屏幕最顶掉下来）；rain 引用为空时兜底")]
    public float redSpawnBelowRoof = 0.3f;
    public float redSpawnYFallback = 1.2f;    // 找不到 GlyphRain 时的固定生成高度
    public float redVisualScale = 0.42f;      // 比普通雨滴大一圈
    public Color redColor = new Color(1f, 0.28f, 0.22f, 1f);

    [Header("紫色字符")]
    public int purpleBatchCount = 2;          // 一次事件生成几颗
    public float purpleFallSpeed = 2.6f;
    public float purpleVisualScale = 0.42f;
    public Color purpleColor = new Color(0.72f, 0.34f, 1f, 1f);

    [Header("荧光与拖尾（HDR + Bloom）")]
    [Tooltip("本体颜色强度倍率（>1 时经 URP Bloom 发荧光）")]
    public float glowIntensity = 1.8f;
    [Tooltip("拖尾残影发射间隔（秒）")]
    public float trailInterval = 0.05f;

    [Header("腐化区域模板（生成 CorruptionZone 时拷贝）")]
    public float corruptionRadius = 1.6f;
    [Tooltip("每秒扣植物能量（>最大生长速度 0.25 才能压制生长）")]
    public float corruptionWitherRate = 0.6f;
    public float corruptionDuration = 30f;
    [Tooltip("雨淋暴露多少秒后净化消散")]
    public float corruptionPurifyNeed = 6f;

    [Header("庇护所")]
    public int nestMaxHp = 3;                 // 红字符撞击几次破坏
    public float nestHitRadius = 1.05f;

    [Header("屋檐阻挡")]
    public float roofHitBandPx = 34f;         // 与屋檐线的判定带宽（bar 像素）

    private struct Threat
    {
        public Transform T; public SpriteRenderer Sr;
        public Vector2 Vel;
        public float Life, Fade;
        public bool Purple, Deflected;
    }
    private readonly List<Threat> _actives = new List<Threat>();
    private GlyphTrail _trail;
    private int _nestHp;
    private float _pingTimer;
    private int _redToSpawn; private float _redSpawnTimer;

    public bool HasActiveThreat => _actives.Count > 0 || _redToSpawn > 0;

    /// <summary>雨区顶边的世界 y（威胁字符从这里出现，与乱码雨同一片天地）</summary>
    private float SpawnY => rain != null ? rain.RainWorldYMax - redSpawnBelowRoof : redSpawnYFallback;

    private void Awake()
    {
        if (dino == null) dino = GetComponentInParent<DinoAgent>();
        if (dino == null) dino = FindObjectOfType<DinoAgent>();
        if (bar == null) bar = FindObjectOfType<LetterAlphabetBar>();
        if (rain == null) rain = FindObjectOfType<GlyphRain>();
        _nestHp = nestMaxHp;
        _trail = new GlyphTrail(trailInterval);
    }

    // —————————————— 事件入口（由 EcoEventScheduler 调用） ——————————————

    /// <summary>红色威胁事件开始：在 duration 内分批生成 redBatchCount 只</summary>
    public void StartRedEvent()
    {
        _redToSpawn = redBatchCount;
        _redSpawnTimer = 0.4f;
    }

    /// <summary>紫色威胁事件：立即生成 purpleBatchCount 颗下落字符</summary>
    public void StartPurpleEvent()
    {
        for (int i = 0; i < purpleBatchCount; i++)
        {
            float x = Random.Range(dino != null ? dino.boundsXMin + 1f : -8f,
                                   dino != null ? dino.boundsXMax - 1f : 8f);
            Spawn(new Vector3(x, SpawnY - i * 0.25f, 0f), purple: true);
        }
    }

    // —————————————— 生成与更新 ——————————————

    private void Spawn(Vector3 pos, bool purple)
    {
        var go = new GameObject(purple ? "PurpleThreat" : "RedThreat");
        go.transform.position = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = LetterPlant.RandomGlyphSprite();
        var baseCol = purple ? purpleColor : redColor;
        sr.color = new Color(baseCol.r * glowIntensity, baseCol.g * glowIntensity,
                             baseCol.b * glowIntensity, 1f); // HDR：>1 分量经 Bloom 发荧光
        sr.sortingOrder = 3; // 最前景
        go.transform.localScale = Vector3.one * (purple ? purpleVisualScale : redVisualScale);
        _actives.Add(new Threat
        {
            T = go.transform, Sr = sr,
            Vel = purple ? Vector2.down * purpleFallSpeed : Vector2.down * (redSpeed * 0.6f),
            Purple = purple
        });
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // 红色事件分批生成
        if (_redToSpawn > 0)
        {
            _redSpawnTimer -= dt;
            if (_redSpawnTimer <= 0f)
            {
                _redSpawnTimer = 1.6f;
                _redToSpawn--;
                float x = Random.Range(-9.5f, 9.5f);
                Spawn(new Vector3(x, SpawnY, 0f), purple: false);
            }
        }

        for (int i = _actives.Count - 1; i >= 0; i--)
        {
            var t = _actives[i];
            t.Life += dt;

            // 拖尾残影（被弹开后的白闪不拖尾）
            if (!t.Deflected && t.Sr != null && t.Sr.sprite != null)
                _trail.Tick(t.T.position, t.Sr.sprite,
                            new Color(t.Sr.color.r, t.Sr.color.g, t.Sr.color.b, 1f) * 0.8f,
                            (t.Purple ? purpleVisualScale : redVisualScale));

            if (t.Deflected) // 被屋檐弹开：抛物线淡出
            {
                t.Vel.y -= 14f * dt;
                t.T.position += (Vector3)(t.Vel * dt);
                t.Fade -= dt * 1.8f;
                var c = t.Sr.color; c.a = Mathf.Clamp01(t.Fade); t.Sr.color = c;
                t.T.Rotate(0f, 0f, 540f * dt);
                if (t.Fade <= 0f) { Destroy(t.T.gameObject); _actives.RemoveAt(i); }
                else _actives[i] = t;
                continue;
            }

            if (t.Purple) UpdatePurple(ref t, dt);
            else UpdateRed(ref t, dt);

            // 寿命到 → 干瘪消散
            if (t.Life >= (t.Purple ? 30f : redLifeMax) && !t.Deflected)
                Deflect(ref t, Vector2.up * 2f);

            _actives[i] = t;
        }
        // 威胁感知广播（0.25s 节流；恐龙据此 Escaping，不逐帧通知）
        _pingTimer -= dt;
        if (_pingTimer <= 0f)
        {
            _pingTimer = 0.25f;
            if (dino == null) return;
            Vector3? nearest = null; float best = float.MaxValue;
            foreach (var t in _actives)
            {
                if (t.Purple || t.Deflected || t.T == null) continue; // 紫色不追踪恐龙，不吓它
                float d = Vector3.Distance(t.T.position, dino.transform.position);
                if (d < best) { best = d; nearest = t.T.position; }
            }
            // 近威胁广播位置（恐龙 Escaping）；没有威胁或威胁已跑远（≥6f）都广播"解除"，
            // 否则恐龙的威胁中断检查会一直把它拽回 Escaping 死循环
            if (nearest.HasValue && best < 6f) dino.NotifyThreatNearby(nearest.Value);
            else dino.NotifyThreatCleared();
        }
    }

    private void UpdateRed(ref Threat t, float dt)
    {
        // 攻击目标：恐龙与庇护所之中较近者（巢建成后恐龙常在巢边，两者轮流挨打）
        Transform target = dino != null ? dino.transform : null;
        Transform nest = dino != null ? dino.Nest : null;
        if (nest != null && (target == null ||
            Vector3.Distance(t.T.position, nest.position) < Vector3.Distance(t.T.position, target.position)))
            target = nest;

        // 追踪（限转向速率）：速度方向逐渐转向目标
        if (target != null)
        {
            Vector2 to = ((Vector2)(target.position - t.T.position) + Vector2.up * 0.25f).normalized;
            float ang = Vector2.SignedAngle(t.Vel.normalized, to);
            float maxAng = redTurnRate * dt * Mathf.Rad2Deg;
            ang = Mathf.Clamp(ang, -maxAng, maxAng);
            t.Vel = Rotate(t.Vel.normalized, ang) * redSpeed;
        }
        t.T.position += (Vector3)(t.Vel * dt);
        t.T.Rotate(0f, 0f, -240f * dt); // 逆时针自旋的威胁感

        // 屋檐阻挡：弹开 + 白闪（玩家直观看到攻击被阻止）
        if (rain != null && rain.RoofBlocksWorld(t.T.position, roofHitBandPx))
        {
            Deflect(ref t, new Vector2(-t.Vel.x * 0.5f, 4.5f));
            t.Sr.color = new Color(1f, 1f, 1f, 0.95f); // 被挡瞬间白闪
            return;
        }

        // 命中恐龙 → 字符碎裂 + 受惊（不致死）
        if (dino != null && Vector3.Distance(t.T.position, dino.transform.position) < redHitRadius + 0.25f)
        {
            ShatterAt(t.T.position, redColor);
            dino.NotifyThreatHit(t.T.position);
            Deflect(ref t, Vector2.up * 1.5f, fade: 0.6f);
            return;
        }

        // 袭击庇护所 → 字符碎裂动画 + 耐久扣（归零则巢毁）
        if (nest != null && Vector3.Distance(t.T.position, nest.position) < nestHitRadius)
        {
            ShatterAt(t.T.position, redColor);
            DamageNest(nest.position);
            Deflect(ref t, Vector2.up * 2.5f, fade: 0.8f);
            return;
        }
    }

    /// <summary>字符碎裂：6 个小字符碎片向外爆散（命中恐龙/巢的可见反馈），自管理生命周期</summary>
    private void ShatterAt(Vector3 pos, Color color)
    {
        AudioManager.PlayAt(Sfx.ThreatHit, pos);
        for (int i = 0; i < 6; i++)
        {
            var go = new GameObject("ThreatShard");
            go.transform.position = pos;
            float ang = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            var vel = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang) * 0.8f + 0.6f, 0f) * Random.Range(2f, 4.5f);
            go.AddComponent<GlyphShard>().Init(LetterPlant.RandomGlyphSprite(),
                new Color(color.r * glowIntensity, color.g * glowIntensity, color.b * glowIntensity, 0.95f),
                0.22f, vel, Random.Range(-540f, 540f), 0.55f, order: 4);
        }
    }

    private void UpdatePurple(ref Threat t, float dt)
    {
        t.T.position += (Vector3)(t.Vel * dt);
        t.T.Rotate(0f, 0f, 160f * dt);

        // 屋檐同样能挡紫色字符（同一判定）
        if (rain != null && rain.RoofBlocksWorld(t.T.position, roofHitBandPx))
        {
            Deflect(ref t, new Vector2(-0.5f + Random.value, 4f));
            t.Sr.color = new Color(1f, 1f, 1f, 0.95f);
            return;
        }

        // 落地 → 生成腐化区域
        float groundY = dino != null ? dino.groundY : -4.04f;
        if (t.T.position.y <= groundY + 0.15f)
        {
            var zoneGo = new GameObject("CorruptionZone");
            zoneGo.transform.position = new Vector3(t.T.position.x, groundY + 0.9f, 0f);
            var zone = zoneGo.AddComponent<CorruptionZone>();
            zone.Init(bar, corruptionRadius, corruptionWitherRate, corruptionDuration, corruptionPurifyNeed);
            AudioManager.PlayAt(Sfx.Corruption, zoneGo.transform.position);
            Deflect(ref t, Vector2.zero, fade: 0.3f); // 字符本体融入区域后消散
        }
    }

    private void Deflect(ref Threat t, Vector2 vel, float fade = 1f)
    {
        t.Deflected = true;
        t.Vel = vel;
        t.Fade = fade;
    }

    private static Vector2 Rotate(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    // —————————————— 庇护所耐久 ——————————————

    private void DamageNest(Vector3 nestPos)
    {
        _nestHp--;
        if (dino != null) dino.OnNestDamaged(nestPos, (float)_nestHp / nestMaxHp);
        if (_nestHp <= 0)
        {
            _nestHp = nestMaxHp; // 恢复满耐久，下次重建后重新扣
            if (dino != null) dino.NotifyNestDestroyed();
        }
    }
}
