// B1Text.cs —— B1 的 TMP 文本基础设施
// ① B1TextStyle：场景实体（Builder 自动创建），统一指定 TMP 字体与材质效果（描边/发光/渐变）。
//    所有动态字符（字符雨/威胁/火焰/打字机/气泡/碎屑）都从这里取字体 → 你只调一处，全局生效。
// ② B1Text.Make()：创建世界空间 TextMeshPro 的快捷方法。
// ③ B1Shard：TMP 字符碎屑（自管理：速度/自旋/淡出/自毁），替代旧 GlyphShard。

using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>全局文本样式（挂在场景物体上，Inspector 调字体/材质；动态实体自动引用）</summary>
[DisallowMultipleComponent]
public class B1TextStyle : MonoBehaviour
{
    public static B1TextStyle Instance { get; private set; }

    [Header("字体（拖入 Project 里的 TMP Font Asset）")]
    public TMP_FontAsset font;

    [Header("材质预设（描边/发光/渐变效果，可留空=用字体自带材质）")]
    public Material material;

    [Header("碎屑参数")]
    public float shardLife = 0.7f;

    private void Awake()
    {
        Instance = this;
        if (font == null) font = TMP_Settings.defaultFontAsset;
    }

    /// <summary>兜底：若 Awake 没设置 Instance（脚本执行顺序问题），主动找一次</summary>
    public static B1TextStyle Get()
    {
        if (Instance == null)
        {
            var s = FindObjectOfType<B1TextStyle>();
            if (s != null) { Instance = s; if (s.font == null) s.font = TMP_Settings.defaultFontAsset; }
        }
        return Instance;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }
}

public static class B1Text
{
    public const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static char RandomLetter() => Letters[Random.Range(0, Letters.Length)];

    /// <summary>创建一个世界空间 TMP 文本物体</summary>
    public static TextMeshPro Make(string name, Transform parent, float fontSize, Color color, int sortOrder)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshPro>();
        var style = B1TextStyle.Get(); // 用 Get() 兜底，避免 Instance 为 null 时字体回退
        if (style != null && style.font != null) tmp.font = style.font;
        else if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
        if (style != null && style.material != null) tmp.fontSharedMaterial = style.material;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.raycastTarget = false;
        var r = go.GetComponent<Renderer>();
        if (r != null) { r.sortingOrder = sortOrder; }
        return tmp;
    }
}

/// <summary>TMP 字符碎屑：速度/自旋/淡出/寿命到自毁，与 owner 解耦（球/字符销毁后不残留）</summary>
public class B1Shard : MonoBehaviour
{
    [Tooltip("重力系数：0=原地悬停渐隐（残影拖尾用），1=正常抛物线")]
    public float gravityScale = 1f;

    private TextMeshPro _tmp;
    private Vector3 _vel;
    private float _spin, _life, _maxLife, _baseAlpha = 1f;

    public void Init(char c, Color color, float fontSize, Vector3 vel, float spinDeg, float life, int sortOrder = 4)
    {
        _tmp = B1Text.Make("Shard", transform, fontSize, color, sortOrder);
        _tmp.text = c.ToString();
        _baseAlpha = color.a;
        _vel = vel; _spin = spinDeg;
        _life = _maxLife = life;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        _life -= dt;
        if (_life <= 0f) { Destroy(gameObject); return; }

        _vel.y -= 6f * gravityScale * dt;        // 轻重力
        _vel *= 1f - 1.2f * dt;                  // 空气阻力
        transform.position += _vel * dt;
        transform.Rotate(0f, 0f, _spin * dt);
        if (_tmp != null) _tmp.alpha = Mathf.Clamp01(_life / _maxLife) * _baseAlpha;
    }
}

/// <summary>TMP 字符爆散特效（B1 全系统共用）</summary>
public static class B1Burst
{
    /// <summary>在 pos 爆出 count 个字符碎屑（fontSize 是 TMP 字号：5 ≈ 0.5 世界单位高）</summary>
    public static void Shatter(Vector3 pos, Color color, int count, float fontSize = 5f)
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("B1Shard");
            go.transform.position = pos;
            var shard = go.AddComponent<B1Shard>();
            float ang = Random.Range(0f, Mathf.PI * 2f);
            shard.Init(B1Text.RandomLetter(), color,
                fontSize * Random.Range(0.5f, 0.8f),
                new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * Random.Range(2f, 5f),
                Random.Range(-240f, 240f),
                Random.Range(0.35f, 0.8f));
        }
    }
}
