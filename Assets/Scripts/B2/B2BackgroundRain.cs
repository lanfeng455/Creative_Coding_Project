// B2BackgroundRain.cs —— 远景背景字符雨（挂在 Canvas 下，纯视觉，不可交互）
// 架构：B2_RainCanvas(WorldSpace) → B2_BackgroundRain → 各 glyph（3D TextMeshPro）。
//       用 3D TMP 而非 UGUI 是为了渲染可靠（UGUI 在 World Space Canvas 下尺寸难控），
//       层级上仍在 Canvas 下面，满足组织要求。glyph 排序 -10，压在所有世界物体之后。
// 覆盖整个双层世界的矩阵式字符雨：多、小、暗、特殊符号、带头亮尾暗的拖尾。
// 所有 glyph 在 Start 一次性建池（列数 × trailLength），运行时只改位置与字符，零分配。

using System.Collections.Generic;
using UnityEngine;
using TMPro;

[DisallowMultipleComponent]
public class B2BackgroundRain : MonoBehaviour
{
    [Header("引用（留空自动 Find）")]
    public B1LayerConfig world;

    [Header("列布局")]
    [Tooltip("列间距（世界单位）——越小越密")]
    public float columnSpacing = 0.62f;
    public float marginX = 0.4f;
    [Tooltip("每列下落速度随机范围（世界单位/秒）")]
    public Vector2 speedRange = new Vector2(2.2f, 4.2f);

    [Header("字符（ASCII 安全池）")]
    public string glyphPool = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ!@#$%^&*+-=[]{}|:;<>,.?/~`";
    [Tooltip("3D TMP 字号（世界高度 ≈ fontSize/10）")]
    public float fontSize = 5f;
    [Tooltip("列头颜色（最亮的一级）")]
    public Color headColor = new Color(0.55f, 0.95f, 0.7f, 0.85f);
    [Tooltip("拖尾基础色（alpha 按 trailFade 逐级衰减）")]
    public Color trailBaseColor = new Color(0.35f, 0.8f, 0.5f, 0.6f);

    [Header("拖尾")]
    [Range(2, 12)] public int trailLength = 7;
    [Tooltip("拖尾字符纵向间距（世界单位）")]
    public float trailSpacing = 0.44f;
    [Range(0.1f, 1f)] public float trailFade = 0.72f;
    [Tooltip("随机换字符的平均间隔（秒/列）")]
    public float flickerInterval = 0.14f;

    [Header("暂停 / 恢复（庆祝、泡泡等外部流程调用；不关闭本组件）")]
    [Tooltip("被暂停时淡出到完全看不见所需的时间（秒）")]
    public float suspendSeconds = 0.25f;
    [Tooltip("恢复时从无到满密度所需的时间（秒）：泡泡结束后用 0.8 左右，让雨慢慢回来而不是瞬间满屏")]
    public float resumeSeconds = 0.8f;

    private class Column
    {
        public TextMeshPro[] slots; // [0]=头（最下），其余向上排列逐级变淡
        public float[] baseAlpha;   // 每个槽位创建时的 alpha（密度缩放乘在它上面）
        public float headY, speed, flicker;
    }

    private Column[] _cols;
    private float _top, _bottom;

    // 暂停者集合：庆祝、泡泡各自持有自己的 owner，互不覆盖；最后一个 owner 解除后才恢复。
    private readonly HashSet<object> _suspends = new HashSet<object>();
    private float _density = 1f;
    private float _densityTarget = 1f;

    /// <summary>暂停字符雨（按 owner 计数）。不会修改 enabled，不影响其他系统对雨的开关。</summary>
    public void Suspend(object owner)
    {
        if (owner == null) return;
        if (_suspends.Add(owner)) _densityTarget = 0f;
    }

    /// <summary>解除某个 owner 的暂停。全部解除后按 resumeSeconds 渐进恢复密度。</summary>
    public void Resume(object owner)
    {
        if (owner == null) return;
        if (_suspends.Remove(owner) && _suspends.Count == 0) _densityTarget = 1f;
    }

    private void Start()
    {
        Debug.Log($"[B2Rain] Start. world={(world != null ? "已设置" : "null")}, " +
                  $"B1TextStyle.Instance={(B1TextStyle.Instance != null ? B1TextStyle.Instance.name : "null")}, " +
                  $"TMP.defaultFont={(TMP_Settings.defaultFontAsset != null ? TMP_Settings.defaultFontAsset.name : "null")}");

        if (world == null) world = FindObjectOfType<B1LayerConfig>();
        if (world == null) { Debug.LogError("[B2Rain] 缺少 B1LayerConfig", this); enabled = false; return; }

        // 字体由 B1Text.Make 内部处理（B1TextStyle.Get 兜底），这里只需确保 Instance 不为空
        B1TextStyle.Get();

        _top = world.upperCeilingY + 0.8f;
        _bottom = world.lowerGroundY - 0.6f;

        float w = world.boundsXMax - world.boundsXMin - marginX * 2f;
        int n = Mathf.Max(2, Mathf.FloorToInt(w / columnSpacing));
        _cols = new Column[n];
        Debug.Log($"[B2Rain] 创建 {n} 列 × {trailLength} 级 = {n * trailLength} 个 glyph（3D TMP），" +
                  $"x范围[{world.boundsXMin + marginX:F2}, {world.boundsXMax - marginX:F2}]");

        for (int i = 0; i < n; i++)
        {
            float x = world.boundsXMin + marginX + (i + 0.5f) * (w / n);
            var c = new Column { slots = new TextMeshPro[trailLength], baseAlpha = new float[trailLength] };
            for (int k = 0; k < trailLength; k++)
            {
                Color col = k == 0 ? headColor : new Color(
                    trailBaseColor.r, trailBaseColor.g, trailBaseColor.b,
                    trailBaseColor.a * Mathf.Pow(trailFade, k));
                c.slots[k] = MakeGlyph($"BgRain_{i}_{k}", x, col);
                c.baseAlpha[k] = col.a;
            }
            ResetColumn(c, initial: true);
            _cols[i] = c;
        }
        Debug.Log($"[B2Rain] 建池完成，首列首glyph='{_cols[0].slots[0].text}' " +
                  $"pos={_cols[0].slots[0].transform.position}");
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // 密度缓动：暂停 → 淡出到 0；恢复 → 用 resumeSeconds 渐进回到 1（不瞬间满屏）
        float ramp = _densityTarget < _density ? suspendSeconds : resumeSeconds;
        _density = ramp > 0.001f
            ? Mathf.MoveTowards(_density, _densityTarget, dt / ramp)
            : _densityTarget;

        float trainLen = (trailLength - 1) * trailSpacing;
        foreach (var c in _cols)
        {
            c.headY -= c.speed * dt;
            if (c.headY + trainLen < _bottom) ResetColumn(c, initial: false);

            for (int k = 0; k < trailLength; k++)
            {
                var t = c.slots[k].transform;
                var p = t.position;
                p.y = c.headY + k * trailSpacing;
                t.position = p;
                // 基础 alpha × 密度：暂停/恢复都只改 alpha，不重建、不销毁字符
                c.slots[k].alpha = c.baseAlpha[k] * _density;
            }

            c.flicker -= dt;
            if (c.flicker <= 0f)
            {
                c.flicker = flickerInterval * Random.Range(0.6f, 1.6f);
                c.slots[Random.Range(0, trailLength)].text = RandomGlyph();
            }
        }
    }

    /// <summary>创建一个 3D TextMeshPro 字符（排序 -10，在所有世界物体之后）</summary>
    private TextMeshPro MakeGlyph(string name, float x, Color color)
    {
        var tmp = B1Text.Make(name, transform, fontSize, color, -10);
        tmp.text = RandomGlyph();
        tmp.transform.position = new Vector3(x, 0f, 0f);
        return tmp;
    }

    private void ResetColumn(Column c, bool initial)
    {
        c.speed = Random.Range(speedRange.x, speedRange.y);
        float trainLen = (trailLength - 1) * trailSpacing;
        c.headY = initial ? Random.Range(_bottom, _top + trainLen)
                          : _top + Random.Range(0f, 3f);
        c.flicker = Random.Range(0f, flickerInterval);
    }

    private string RandomGlyph() => glyphPool[Random.Range(0, glyphPool.Length)].ToString();
}
