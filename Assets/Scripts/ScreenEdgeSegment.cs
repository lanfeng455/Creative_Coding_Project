// 屏幕边缘高亮段
// 挂在一条屏幕边缘对应的 UI Image / SpriteRenderer / MeshRenderer 上
// 通过 SetHighlight() 设置高亮强度，脚本自己做平滑衰减

using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class ScreenEdgeSegment : MonoBehaviour
{
    [Header("目标组件（留空会自动查找）")]
    [Tooltip("UI Image（屏幕边缘通常用 UI 做）")]
    public Image image;

    [Tooltip("如果用 SpriteRenderer / MeshRenderer 表示边缘")]
    public SpriteRenderer spriteRenderer;
    public MeshRenderer meshRenderer;

    [Header("颜色设置")]
    public Color baseColor = new Color(1f, 1f, 1f, 0.3f);
    public Color highlightColor = new Color(1f, 0.85f, 0.2f, 1f);

    [Header("动画参数")]
    [Tooltip("从 100% 高亮衰减到 0% 的时间（秒）")]
    public float fadeDuration = 0.8f;

    [Tooltip("额外的脉冲缩放（0 = 不变，比如 0.1 = 放大到 1.1 倍）")]
    public float pulseScale = 0.08f;

    [Tooltip("SetHighlight(1) 之后锁定的最小持续时间，防止抖动")]
    public float minHoldTime = 0.05f;

    // 内部状态
    private float _intensity;       // 当前高亮强度 0~1
    private float _pulseTimer;      // 脉冲计时
    private float _holdTimer;      // 锁定保持计时
    private Vector3 _baseScale;
    private Material _cachedMaterial;
    private bool _hasMaterialColorSet;

    private void Awake()
    {
        if (image == null) image = GetComponent<Image>();
        if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

        _baseScale = transform.localScale;
        _intensity = 0f;
        _pulseTimer = 0f;
        _holdTimer = 0f;

        ApplyVisual(0f);
    }

    /// <summary>
    /// 设置高亮目标强度。
    /// intensity: 0 = 完全不高亮，1 = 完全高亮
    /// </summary>
    public void SetHighlight(float intensity)
    {
        if (intensity > _intensity)
        {
            _intensity = intensity;
            _pulseTimer = 0f;
            _holdTimer = minHoldTime;
            ApplyVisual(_intensity);
        }
    }

    /// <summary>立刻完全取消高亮（例如手丢失时）</summary>
    public void ClearHighlight()
    {
        _intensity = 0f;
        _holdTimer = 0f;
        _pulseTimer = fadeDuration; // 让它立即衰减完
        ApplyVisual(0f);
    }

    private void Update()
    {
        // 保持锁定
        if (_holdTimer > 0f)
        {
            _holdTimer -= Time.deltaTime;
            return;
        }

        if (_intensity > 0f)
        {
            _pulseTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_pulseTimer / fadeDuration);
            // 用 ease-out quad 做衰减
            float eased = 1f - (1f - t) * (1f - t);
            _intensity = 1f - eased;

            if (_pulseTimer >= fadeDuration)
            {
                _intensity = 0f;
            }

            ApplyVisual(_intensity);
        }
    }

    private void ApplyVisual(float intensity)
    {
        // 颜色插值
        Color c = Color.Lerp(baseColor, highlightColor, intensity);

        if (image != null)
        {
            image.color = c;
        }
        else if (spriteRenderer != null)
        {
            spriteRenderer.color = c;
        }
        else if (meshRenderer != null)
        {
            var mat = meshRenderer.material;
            if (mat != null)
            {
                // 兼容不同 shader
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            }
        }

        // 脉冲缩放
        if (pulseScale > 0f && intensity > 0f)
        {
            float s = 1f + pulseScale * intensity;
            transform.localScale = _baseScale * s;
        }
        else
        {
            transform.localScale = _baseScale;
        }
    }
}
