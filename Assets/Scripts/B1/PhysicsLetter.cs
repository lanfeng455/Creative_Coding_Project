// PhysicsLetter.cs —— 真实物理的字符实体（世界空间 TMP 渲染，与 BouncingBall 同款）
// 生命周期：从下层顶部落下 → 落地碎掉 / 被弹力带弹起 → 越过上层分界线 → 上飞一段炸开录入
// 渲染：世界空间 TextMeshPro（不经过 UGUI 坐标转换，与球一样丝滑）。
//       背景雨在 World Space Canvas (z=5)，字母在 z=0，天然前后分层。

using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class PhysicsLetter : MonoBehaviour
{
    public static readonly List<PhysicsLetter> All = new();

    [Header("物理（运行时可调）")]
    public float gravity = 4.5f;
    [Tooltip("字符的世界高度（单位）。TMP 字号 = size × 10")]
    public float size = 0.62f;
    public Color color = new Color(0.85f, 0.95f, 1f, 1f);

    [Header("拖尾（彗星式渐隐残影）")]
    public bool trailEnabled = true;
    public float trailInterval = 0.06f;
    [Range(0f, 1f)] public float trailAlpha = 0.65f;
    public float trailLife = 0.5f;
    private float _trailAcc;

    public char Letter { get; private set; }
    public Vector2 Vel => _vel;
    public float Radius => size * 0.5f;

    private B1LayerConfig _world;
    private Vector2 _vel;
    private TextMeshPro _tmp;     // 世界空间 TMP（与球同款渲染路径，无 UGUI 转换）
    private bool _dead;
    private float _bandCd;

    private void Awake()
    {
        _tmp = B1Text.Make("Letter", transform, size * 10f, color, 2);
    }

    private void OnDestroy()
    {
        All.Remove(this);
        if (_tmp != null) Destroy(_tmp.gameObject);
    }

    /// <summary>由雨生成器调用：随机字母 + 初始下落速度</summary>
    public void Init(B1LayerConfig world, char letter, Vector2 pos, float fallSpeed)
    {
        _world = world;
        Letter = letter;
        All.Add(this);
        transform.position = pos;
        _vel = new Vector2(Random.Range(-0.3f, 0.3f), -fallSpeed);
        _tmp.text = letter.ToString();
        _tmp.color = color;
        Debug.Log($"[B2Letter] 生成字符 '{letter}' @ {pos}, font={(_tmp.font != null ? _tmp.font.name : "null")}");
    }

    /// <summary>弹力带/外部赋予速度</summary>
    public void Launch(Vector2 v) { _vel = v; _bandCd = 0.15f; }

    private void Update()
    {
        if (_dead) return;
        float dt = Time.deltaTime;
        _bandCd -= dt;

        // —— 物理 ——
        var p = (Vector2)transform.position;
        _vel.y -= gravity * dt;
        _vel.x *= 1f - 0.1f * dt;
        p += _vel * dt;

        // 左右墙
        float r = Radius;
        float lo = _world.boundsXMin + r, hi = _world.boundsXMax - r;
        if (p.x < lo) { p.x = lo; _vel.x = Mathf.Abs(_vel.x) * 0.7f; }
        if (p.x > hi) { p.x = hi; _vel.x = -Mathf.Abs(_vel.x) * 0.7f; }

        // —— 越过上层分界线：继续上飞一段（惯性）→ 炸开 + 录入打字机 ——
        if (p.y > _world.upperFloorY + 0.6f)
        {
            var writer = TypeWriterB1.Instance;
            bool recorded = writer != null && char.IsLetter(Letter);
            if (recorded) writer.Add(Letter);
            _dead = true;
            if (!recorded || !_world.NotifyLetterRecorded(new Vector3(p.x, p.y, transform.position.z), size))
                B1Burst.Shatter(transform.position, new Color(0.55f, 1f, 0.8f), 7, size * 8f);
            AudioManager.PlayAt(Sfx.LeafHarvest, transform.position);
            Destroy(gameObject);
            return;
        }

        // —— 落地：直接碎掉（不弹、不滑、不滞留）——
        float ground = _world.GroundYAt(p.x, p.y);
        if (p.y - r <= ground && _vel.y <= 0f)
        {
            _dead = true;
            B1Burst.Shatter(transform.position, color, 6, size * 8f);
            AudioManager.PlayAt(Sfx.ThreatHit, transform.position);
            Destroy(gameObject);
            return;
        }

        transform.position = new Vector3(p.x, p.y, 0f);

        // —— 彗星拖尾：残影带部分速度跟随，形成连续渐变尾 ——
        if (trailEnabled && _vel.sqrMagnitude > 1.5f)
        {
            _trailAcc += dt;
            while (_trailAcc >= trailInterval)
            {
                _trailAcc -= trailInterval;
                var go = new GameObject("LetterTrail");
                go.transform.position = transform.position;
                var shard = go.AddComponent<B1Shard>();
                shard.Init(Letter, new Color(color.r, color.g, color.b, trailAlpha),
                    size * 9f, (Vector3)(_vel * 0.35f), 0f, trailLife, sortOrder: 1);
                shard.gravityScale = 0.15f;
            }
        }
        else _trailAcc = 0f;
    }

    /// <summary>被静止的弹力带压碎（屋檐碎裂语义）</summary>
    public void Shatter()
    {
        if (_dead) return;
        _dead = true;
        B1Burst.Shatter(transform.position, color, 5, size * 8f);
        AudioManager.PlayAt(Sfx.ThreatHit, transform.position);
        Destroy(gameObject);
    }

    /// <summary>弹射冷却中？（弹力带防抖）</summary>
    public bool OnBandCooldown => _bandCd > 0f;
}
