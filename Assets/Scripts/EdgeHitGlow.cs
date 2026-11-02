// EdgeHitGlow.cs —— 屏幕边缘"命中点"局部高亮
// 挂在每条边缘的 UI Image 上
// 自动替换 Image 的 Material 为 EdgeGlow shader，设置自己的 _EdgeAxis

using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
[DisallowMultipleComponent]
public class EdgeHitGlow : MonoBehaviour
{
    public enum EdgeType { Left, Right, Top, Bottom }

    [Header("边缘类型（决定沿 X 还是 Y 轴做钟形）")]
    public EdgeType edgeType = EdgeType.Left;

    [Header("颜色 / 形状")]
    public Color baseColor = new Color(1f, 1f, 1f, 0.1f);
    public Color glowColor = new Color(1f, 0.85f, 0.2f, 1f);

    [Tooltip("钟形高斯的 σ 值（越小越窄，0.08=尖，0.25=宽）")]
    [Range(0.02f, 0.5f)]
    public float spread = 0.14f;

    [Header("动画")]
    [Tooltip("从强度 1 衰减到 0 的时间（秒）")]
    public float fadeDuration = 0.6f;
    [Tooltip("命中后最少 hold 多久（防止抖动）")]
    public float minHoldTime = 0.08f;

    // —— 内部状态 ——
    private Image _image;
    private Material _glowMat;

    private float _hitPos;       // 沿边的归一化位置 0~1
    private float _intensity;    // 当前高亮强度 0~1
    private float _fadeTimer;    // 衰减计时
    private float _holdTimer;    // 锁定 hold 计时

    private static readonly int _PropEdgeAxis = Shader.PropertyToID("_EdgeAxis");
    private static readonly int _PropHitPos   = Shader.PropertyToID("_HitPos");
    private static readonly int _PropIntensity = Shader.PropertyToID("_Intensity");
    private static readonly int _PropSpread   = Shader.PropertyToID("_Spread");
    private static readonly int _PropBaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int _PropGlowColor = Shader.PropertyToID("_GlowColor");

    public EdgeType Type => edgeType;

    /// <summary>
    /// 外部（EdgeHighlightController）命中时调用。
    /// normalizedPos: 沿边缘方向的位置，0=屏幕最左/最下，1=屏幕最右/最上
    /// </summary>
    public void SetHit(float normalizedPos)
    {
        _hitPos = Mathf.Clamp01(normalizedPos);
        _intensity = 1f;
        _fadeTimer = 0f;
        _holdTimer = minHoldTime;
        ApplyToMaterial();
    }

    /// <summary>立刻清掉高亮</summary>
    public void ClearHit()
    {
        _intensity = 0f;
        _holdTimer = 0f;
        _fadeTimer = fadeDuration; // 让它立即衰减完
        ApplyToMaterial();
    }

    private void Awake()
    {
        _image = GetComponent<Image>();
        EnsureMaterial();
        ResetVisual();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying) return;
        ResetVisual();
    }

    private void ResetVisual()
    {
        if (_glowMat == null) return;
        _intensity = 0f;
        _hitPos = 0.5f;
        ApplyToMaterial();
    }

    private void EnsureMaterial()
    {
        var shader = Shader.Find("UI/EdgeGlow");
        if (shader == null)
        {
            Debug.LogError("[EdgeHitGlow] 找不到 Shader: UI/EdgeGlow，边缘高亮不会显示。请确认 EdgeGlow.shader 已导入并编译通过。");
            enabled = false;
            return;
        }

        // 给这个 Image 一个专属材质实例，避免多个 Edge 共享同一 Material
        _glowMat = new Material(shader);
        _image.material = _glowMat;

        // 决定 EdgeAxis：Left / Right 垂直边缘沿 Y 轴 → 1；Top / Bottom 水平边缘沿 X 轴 → 0
        float axis = (edgeType == EdgeType.Left || edgeType == EdgeType.Right) ? 1f : 0f;
        _glowMat.SetFloat(_PropEdgeAxis, axis);
        _glowMat.SetFloat(_PropHitPos, 0.5f);
        _glowMat.SetFloat(_PropIntensity, 0f);
        _glowMat.SetFloat(_PropSpread, spread);
        _glowMat.SetColor(_PropBaseColor, baseColor);
        _glowMat.SetColor(_PropGlowColor, glowColor);
    }

    private void Update()
    {
        if (_glowMat == null) return;

        // hold 锁定期间不衰减
        if (_holdTimer > 0f)
        {
            _holdTimer -= Time.deltaTime;
            return;
        }

        if (_intensity > 0f)
        {
            _fadeTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_fadeTimer / Mathf.Max(fadeDuration, 0.0001f));
            // ease-out quad：开始快衰减，后面慢消失
            float eased = 1f - (1f - t) * (1f - t);
            _intensity = 1f - eased;

            if (_fadeTimer >= fadeDuration)
            {
                _intensity = 0f;
            }

            _glowMat.SetFloat(_PropIntensity, _intensity);
        }
    }

    /// <summary>每次 SetHit 后调用，把最新参数写进材质</summary>
    private void ApplyToMaterial()
    {
        if (_glowMat == null) return;
        _glowMat.SetFloat(_PropHitPos, _hitPos);
        _glowMat.SetFloat(_PropIntensity, _intensity);
        _glowMat.SetFloat(_PropSpread, spread);
        _glowMat.SetColor(_PropBaseColor, baseColor);
        _glowMat.SetColor(_PropGlowColor, glowColor);
    }
}
