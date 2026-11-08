// ThreatB1.cs —— B1 危机系统（上层恐龙世界）
// ① RedThreatGlyph：从上层天花板出现，追踪恐龙，命中 → 恐龙受惊（不会死）+ 字符碎裂
// ② FireHazard：上层随机起火，恐龙逃离并头顶 WATER! 求救；WATER 物品命中 → 灭火
// ③ B1EventScheduler 已拆到独立文件 B1EventScheduler.cs

using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>红色攻击字符（上层）</summary>
public class RedThreatGlyph : MonoBehaviour
{
    public static readonly List<RedThreatGlyph> All = new();

    [Header("参数")]
    public float chaseSpeed = 1.6f;
    public float steer = 3.5f;
    public float lifetime = 14f;
    public float hitRadius = 0.6f;
    public Color color = new Color(1f, 0.25f, 0.3f);

    [Tooltip("有房间时，改为拆房（而非追恐龙）的概率")]
    [Range(0f, 1f)] public float roomTargetChance = 0.45f;

    private Transform _dino;
    private Vector2 _vel;
    private TextMeshPro _tmp;
    private float _life;
    private B1LayerConfig _world;
    private B2House.Room _targetRoom; // 非 null → 目标是拆房
    private Vector3 _targetPos;

    private void Awake()
    {
        All.Add(this);
        _tmp = B1Text.Make("Threat", transform, 4.2f, color, 4);
        _tmp.text = B1Text.RandomLetter().ToString();
    }

    private void OnDestroy() => All.Remove(this);

    public void Init(B1LayerConfig world, DinoAgentB1 dino, Vector3 pos)
    {
        _world = world;
        _dino = dino != null ? dino.transform : null;
        transform.position = pos;
        _vel = Vector2.down * chaseSpeed;

        // 有房子时，概率改为瞄准一个房间（拆家威胁）
        var house = FindObjectOfType<B2House>();
        if (house != null && house.Rooms.Count > 0 && Random.value < roomTargetChance)
        {
            _targetRoom = house.RandomRoom();
            if (_targetRoom != null) _targetPos = _targetRoom.center;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _life += dt;
        if (_life > lifetime) { ShatterSelf(false); return; }

        // 目标位置：拆房 → 房间中心；否则 → 恐龙（转向式追踪，不会瞬间转向 → 有躲避余地）
        Vector3 aim = _targetRoom != null ? _targetPos
                    : _dino != null ? _dino.position : transform.position;
        if (_targetRoom != null || _dino != null)
        {
            Vector2 to = (Vector2)(aim - transform.position);
            Vector2 want = to.normalized * chaseSpeed;
            _vel = Vector2.Lerp(_vel, want, steer * dt);
            transform.position += (Vector3)(_vel * dt);
            transform.Rotate(0f, 0f, 120f * dt); // 旋转的威胁字符
        }

        // 命中房间 → 摧毁房间（带反馈）+ 碎裂
        if (_targetRoom != null && Vector2.Distance(transform.position, _targetPos) < hitRadius + 0.5f)
        {
            var house = FindObjectOfType<B2House>();
            if (house != null) house.DestroyRoom(_targetRoom);
            ShatterSelf(true);
            return;
        }

        // 命中恐龙 → 受击反馈 + 受惊 + 碎裂（恐龙不会死）
        if (_targetRoom == null && _dino != null && Vector2.Distance(transform.position, _dino.position) < hitRadius)
        {
            var dino = _dino.GetComponent<DinoAgentB1>();
            if (dino != null) dino.Scare(transform.position.x);
            ShatterSelf(true);
        }

        // 闪烁提醒
        float f = 0.75f + Mathf.Sin(_life * 10f) * 0.25f;
        _tmp.color = new Color(color.r * f, color.g, color.b * f, 1f);
    }

    private void ShatterSelf(bool hit)
    {
        B1Burst.Shatter(transform.position, color, 6, 4f);
        if (hit) AudioManager.PlayAt(Sfx.ThreatHit, transform.position);
        Destroy(gameObject);
    }
}

/// <summary>火灾（上层）。场上最多一场（ActiveFire）。</summary>
public class FireHazard : MonoBehaviour
{
    public static FireHazard ActiveFire { get; private set; }

    [Header("参数")]
    public Color fireColor = new Color(1f, 0.55f, 0.15f);
    public float flickerSpeed = 9f;

    private TextMeshPro _tmp;
    private float _t;
    private B1LayerConfig _world;

    private void Awake()
    {
        _tmp = B1Text.Make("Fire", transform, 5.5f, fireColor, 3);
        _tmp.text = "F"; // 火苗形态：字符语义，F=FIRE
    }

    public void Init(B1LayerConfig world)
    {
        _world = world;
        // 上层地板随机位置（避开缺口）
        float x = Random.Range(world.upperFloorXMin + 1.5f, world.GapXMin - 1.5f);
        transform.position = new Vector3(x, world.upperFloorY + 0.4f, 0f);
        ActiveFire = this;
        AudioManager.PlayAt(Sfx.Corruption, transform.position);
    }

    private void OnDestroy()
    {
        if (ActiveFire == this) ActiveFire = null;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        // 火苗抖动 + 呼吸缩放 + 火星字符飞溅（TMP 火星，自管理碎屑）
        float s = 0.9f + Mathf.Sin(_t * flickerSpeed) * 0.12f + Mathf.Sin(_t * 2.3f) * 0.08f;
        transform.localScale = Vector3.one * s;

        _sparkAcc += Time.deltaTime * 6f;
        while (_sparkAcc >= 1f)
        {
            _sparkAcc -= 1f;
            var go = new GameObject("FireSpark");
            go.transform.position = transform.position + new Vector3(Random.Range(-0.3f, 0.3f), 0f, 0f);
            var shard = go.AddComponent<B1Shard>();
            shard.Init(B1Text.RandomLetter(),
                new Color(1f, Random.Range(0.3f, 0.6f), 0.1f, 1f),
                Random.Range(1.2f, 2.4f),
                new Vector3(Random.Range(-0.5f, 0.5f), Random.Range(1.5f, 3f), 0f),
                Random.Range(-120f, 120f), Random.Range(0.3f, 0.6f), sortOrder: 4);
        }
    }
    private float _sparkAcc;

    /// <summary>WATER 命中 → 灭火</summary>
    public void Extinguish()
    {
        B1Burst.Shatter(transform.position, new Color(0.4f, 0.8f, 1f), 10, 5f);
        AudioManager.PlayAt(Sfx.RainStart, transform.position);
        ActiveFire = null;
        Destroy(gameObject);
    }
}
