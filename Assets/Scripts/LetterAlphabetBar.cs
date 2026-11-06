// LetterAlphabetBar.cs —— 底部 26 字母植物条
// 挂在一个拉伸全宽的 UI 物体（RectTransform）上，运行时自动生成 A-Z 26 格：
// 每格 = 底部 TMP 字母 + 上方 LetterPlant（RawImage）
//
// 每帧轮询 RightHandIndexRayProvider 的底边命中点：
//   - 焦点字母 input=1（长得最快）
//   - 容忍半径 tolerancePx 内按高斯衰减（与 BottomLine 高光共用同一 σ）
//   - 半径外 input=0 → 植物萎缩
//
// 打字机：植物能量长满（触及分割线）→ 字母键入上半屏左侧的打字机区域，树冻结；
//       字母按从左到右、从上到下的顺序排列，指尖涂抹可以擦除。
// 上半屏右侧是威胁（火焰）区，打字机与威胁区之间是 45° 斜分割线。

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class LetterAlphabetBar : MonoBehaviour
{
    [Header("射线来源（留空自动 Find）")]
    public RightHandIndexRayProvider rayProvider;

    [Header("字母行")]
    public string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public int letterFontSize = 28;
    public Color letterColor = new Color(0.92f, 0.92f, 0.92f, 1f);
    [Tooltip("字母行高度（像素），植物从字母上方开始长")]
    public float letterRowHeight = 48f;

    [Header("字母显示（不影响选择与生长规则）")]
    [Tooltip("英文字母专用字体；留空使用工程自带的 LiberationSans SDF，不使用全局乱码雨字体")]
    [SerializeField] private TMP_FontAsset displayFont;
    /// <summary>GlyphRain 等子组件会读这个来同步字体。</summary>
    public TMP_FontAsset DisplayFont => displayFont;
    [SerializeField, Min(0f)] private float minimumLetterScreenSize = 18f;
    [SerializeField, Min(0f)] private float letterBottomInset = 24f;

    [Header("容忍区间（与 BottomLine 高光共用同一高斯宽度）")]
    [Tooltip("焦点字母两侧多少像素内的字母也会生长（高斯衰减，2.5σ≈该距离）")]
    public float tolerancePx = 120f;

    [Header("植物参数（生成时传给每棵植物）")]
    [Tooltip("input=1 时能量从 0 积满所需秒数，越小越快")]
    public float growDuration = 4f;
    [Tooltip("没有输入时能量从满衰减到 0 所需秒数")]
    public float witherDuration = 5f;
    [Tooltip("CA 细胞自动机每秒 tick 次数，越大植物长越频繁（与 growDuration 共同决定视觉速度）")]
    public float tickRate = 8f;
    public Color stemColor = new Color(0.88f, 1f, 0.88f, 1f);
    [Tooltip("叶片颜色（与枝干区分；恐龙只采摘登记在册的叶片格）")]
    public Color leafColor = new Color(0.42f, 1f, 0.5f, 1f);
    [Tooltip("叶片大小：1≈小，2≈大，3≈超大。大叶片可当恐龙跳台（越大生成越稀疏）")]
    [Range(0, 3)] public int leafBlobRadius = 2;

    [Header("上半屏：左打字机 / 右威胁（斜 45° 分割）")]
    public Color typewriterColor = new Color(0.55f, 1f, 0.78f, 1f);
    public Color typewriterCaretColor = new Color(0.55f, 1f, 0.78f, 0.7f);
    [Tooltip("打字机基础字号（参考分辨率像素）")]
    public float typewriterFontSize = 84f;
    [Tooltip("打字块左边距（像素）")]
    public float typewriterLeftInset = 64f;
    [Tooltip("打字块距屏幕顶部的距离（像素）。参考图里打字块在上半屏偏下位置，留足上方呼吸空间")]
    public float typewriterTopInset = 260f;
    [Tooltip("打字机重排平滑速度（越大越快）")]
    public float typewriterReflowSpeed = 16f;

    [Header("斜分割线（打字机区与威胁区的边界，角度可调）")]
    [Range(0.15f, 0.7f), Tooltip("斜线上端在屏幕宽度上的起始比例（0=最左，1=最右）")]
    public float diagonalTopFraction = 0.46f;
    [Range(15f, 75f), Tooltip("与水平线的夹角（°）。45° 时水平推进速度=垂直推进速度，越小火焰越平，越大火焰越陡")]
    public float diagonalAngleDeg = 45f;
    public Color diagonalColor = new Color(0.55f, 1f, 0.78f, 0.55f);
    public float diagonalThickness = 2f;

    [Header("打字机字母 发光/描边（编辑器可调，自动保存）")]
    public bool typewriterUseOutline = true;
    public Color typewriterOutlineColor = new Color(0.02f, 0.14f, 0.07f, 1f);
    [Range(0f, 1f)] public float typewriterOutlineWidth = 0.22f;
    public bool typewriterUseGlow = true;
    public Color typewriterGlowColor = new Color(0.45f, 1f, 0.72f, 1f);
    [Range(0f, 2f)] public float typewriterGlowInner = 0.5f;
    [Range(0f, 6f)] public float typewriterGlowOuter = 1.6f;
    [Range(0.5f, 6f)] public float typewriterGlowPower = 2.8f;

    [Header("底部 26 字母 发光/描边（编辑器可调，自动保存）")]
    public bool labelUseOutline = true;
    public Color labelOutlineColor = new Color(0.04f, 0.05f, 0.05f, 1f);
    [Range(0f, 1f)] public float labelOutlineWidth = 0.08f;
    public bool labelUseGlow = true;
    public Color labelGlowColor = new Color(1f, 1f, 1f, 0.85f);
    [Range(0f, 2f)] public float labelGlowInner = 0.4f;
    [Range(0f, 6f)] public float labelGlowOuter = 1.2f;
    [Range(0.5f, 6f)] public float labelGlowPower = 2.0f;

    [Header("BottomLine 高光联动（拖入 BottomLine 上的 EdgeHitGlow）")]
    [Tooltip("由本组件驱动：高光位置=焦点，高斯宽度=容忍区间")]
    public EdgeHitGlow bottomGlow;

    public enum PlayMode { RayPoint, GlyphRain }

    [Tooltip("字母键入后树是否冻结保留；关闭则树会继续退化，跌离终点线后同一字母可再次键入")]
    public bool freezeAfterTyping = true;

    [Header("玩法模式")]
    [Tooltip("RayPoint=食指射线指点；GlyphRain=双食指屋檐挡雨浇灌")]
    public PlayMode playMode = PlayMode.RayPoint;
    [Tooltip("乱码雨组件（GlyphRain 模式用，可挂在同一物体上）")]
    public GlyphRain glyphRain;
    [Tooltip("雨滴热量衰减速度（/秒），越大停雨后植物越快开始萎缩")]
    public float rainHeatDecay = 2.2f;
    [Tooltip("缝隙保底输入：字母被缝隙完整罩住时的基础生长输入（雨是额外加成）。解决缝隙太窄时雨滴通过率过低的问题")]
    [Range(0f, 1f)] public float apertureInput = 0.6f;

    private float[] _rainHeat;

    private RectTransform _rect;
    private LetterPlant[] _plants;
    private Canvas _canvas;
    private TMP_FontAsset _font;
    private Material _labelMaterial;
    private Material _typedMaterial;
    private RectTransform _divider;
    private TextMeshProUGUI[] _letterLabels;
    private readonly Vector3[] _dividerCorners = new Vector3[4];
    private readonly Vector3[] _canvasCorners = new Vector3[4];
    private float _displayLetterSize, _displayRowHeight;

    // Glyph rain must land at the actual displayed row, including its safe inset.
    public float LetterRowDisplayHeight => _displayRowHeight > 0f ? _displayRowHeight : letterRowHeight;

    /// <summary>Build() 每创建一棵植物后触发（供恐龙等外部系统订阅，避免全局搜索）</summary>
    public event System.Action<LetterPlant> onPlantSpawned;

    /// <summary>当前全部植物（Build 之后有效；供外部一次性登记既有植物）</summary>
    public IReadOnlyList<LetterPlant> Plants => _plants;

    private class TypedEntry
    {
        public RectTransform rect;
        public Vector2 target;
        public float alpha;
    }
    private readonly List<TypedEntry> _typed = new List<TypedEntry>();

    private TextMeshProUGUI _caret;
    private RectTransform _upperRig;
    private RectTransform _diagStart;
    private RectTransform _diagEnd;
    private Image _diagLine;
    private float _typedFontSize, _typedCellWidth, _typedCellHeight;
    private int _typedColumns, _typedRows;
    private float _typedOriginX, _typedOriginY;

    private void Awake()
    {
        ApplyNumHands(); // 必须赶在 Runner 消费配置前：Bootstrap 初始化要数帧，Awake 一定更早
        _rect = GetComponent<RectTransform>();
        _canvas = GetComponentInParent<Canvas>();
        if (transform.parent != null)
            _divider = transform.parent.Find("Image") as RectTransform;
        LoadFont();
        BuildUpperRig();
        Build();
    }

    /// <summary>按玩法模式设置检测手数：乱码雨需要 2 只手，射线模式 1 只就够（省算力）</summary>
    private void ApplyNumHands()
    {
        var runner = FindObjectOfType<Mediapipe.Unity.Sample.HandLandmarkDetection.HandLandmarkerRunner>();
        if (runner == null) return;
        int want = playMode == PlayMode.GlyphRain ? 2 : 1;
        if (runner.config.NumHands != want)
        {
            runner.config.NumHands = want;
            Debug.Log($"[AlphabetBar] 按玩法模式 {playMode} 设置 NumHands={want}");
        }
    }

    private void Start()
    {
        if (rayProvider == null)
            rayProvider = FindObjectOfType<RightHandIndexRayProvider>();
        if (glyphRain == null)
            glyphRain = GetComponent<GlyphRain>();
    }

    private void LoadFont()
    {
        _font = displayFont;
        if (_font == null) _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (_font == null) _font = TMP_Settings.defaultFontAsset;
        if (_font == null)
            Debug.LogWarning("[AlphabetBar] 找不到 TMP 字体资产，字母可能不显示。请 Window > TextMeshPro > Import TMP Essential Resources");
        else
        {
            _labelMaterial = new Material(_font.material) { name = "Alphabet readable labels", hideFlags = HideFlags.DontSave };
            _typedMaterial = new Material(_font.material) { name = "Typewriter letters", hideFlags = HideFlags.DontSave };
            ApplyMaterialStyles();
        }
    }

    /// <summary>把 Inspector 上的 glow/outline 参数刷到材质上。OnValidate 和 LoadFont 都调它。</summary>
    private void ApplyMaterialStyles()
    {
        if (_labelMaterial == null || _typedMaterial == null) return;

        // —— 底部 26 字母 ——
        if (labelUseOutline)
        {
            _labelMaterial.EnableKeyword("OUTLINE_ON");
            _labelMaterial.SetColor("_OutlineColor", labelOutlineColor);
            _labelMaterial.SetFloat("_OutlineWidth", labelOutlineWidth);
        }
        else _labelMaterial.DisableKeyword("OUTLINE_ON");

        if (labelUseGlow)
        {
            _labelMaterial.EnableKeyword("GLOW_ON");
            _labelMaterial.SetColor("_GlowColor", labelGlowColor);
            _labelMaterial.SetFloat("_GlowInner", labelGlowInner);
            _labelMaterial.SetFloat("_GlowOuter", labelGlowOuter);
            _labelMaterial.SetFloat("_GlowPower", labelGlowPower);
        }
        else _labelMaterial.DisableKeyword("GLOW_ON");

        // —— 打字机 ——
        if (typewriterUseOutline)
        {
            _typedMaterial.EnableKeyword("OUTLINE_ON");
            _typedMaterial.SetColor("_OutlineColor", typewriterOutlineColor);
            _typedMaterial.SetFloat("_OutlineWidth", typewriterOutlineWidth);
        }
        else _typedMaterial.DisableKeyword("OUTLINE_ON");

        if (typewriterUseGlow)
        {
            _typedMaterial.EnableKeyword("GLOW_ON");
            _typedMaterial.SetColor("_GlowColor", typewriterGlowColor);
            _typedMaterial.SetFloat("_GlowInner", typewriterGlowInner);
            _typedMaterial.SetFloat("_GlowOuter", typewriterGlowOuter);
            _typedMaterial.SetFloat("_GlowPower", typewriterGlowPower);
        }
        else _typedMaterial.DisableKeyword("GLOW_ON");
    }

#if UNITY_EDITOR
    /// <summary>编辑器里调 Inspector 参数 → 实时刷新材质（PlayMode 和 EditMode 都生效，退出 Play 保留）</summary>
    private void OnValidate()
    {
        if (_labelMaterial == null || _typedMaterial == null) return;
        ApplyMaterialStyles();
    }
#endif

    private void Build()
    {
        // 清理上次 Play 的残留（禁用域重载时字段还在但子物体已销毁）
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (c.name.StartsWith("Slot_") || c.name.StartsWith("Typed_") || c.name == "TypewriterCaret")
                Destroy(c.gameObject);
        }
        _typed.Clear();

        int n = letters.Length;
        _plants = new LetterPlant[n];
        _letterLabels = new TextMeshProUGUI[n];
        _rainHeat = new float[n];

        for (int i = 0; i < n; i++)
        {
            float f0 = i / (float)n;
            float f1 = (i + 1) / (float)n;

            // 格子：按 anchor 均分，随条宽自适应
            var slotGo = new GameObject($"Slot_{letters[i]}", typeof(RectTransform));
            slotGo.transform.SetParent(transform, false);
            slotGo.layer = gameObject.layer;
            var slotRect = (RectTransform)slotGo.transform;
            slotRect.anchorMin = new Vector2(f0, 0f);
            slotRect.anchorMax = new Vector2(f1, 1f);
            slotRect.offsetMin = Vector2.zero;
            slotRect.offsetMax = Vector2.zero;

            // 字母 TMP：贴格子底部
            var letter = CreateLabel(slotGo.transform, "Letter", letterFontSize, letterColor);
            letter.text = letters[i].ToString();
            _letterLabels[i] = letter;
            var letterRect = (RectTransform)letter.transform;
            letterRect.anchorMin = new Vector2(0f, 0f);
            letterRect.anchorMax = new Vector2(1f, 0f);
            letterRect.offsetMin = new Vector2(0f, 0f);
            letterRect.offsetMax = new Vector2(0f, letterRowHeight);

            // 植物 RawImage：字母上方区域
            var plantGo = new GameObject("Plant", typeof(RectTransform), typeof(RawImage), typeof(LetterPlant));
            plantGo.transform.SetParent(slotGo.transform, false);
            plantGo.layer = gameObject.layer;
            var plantRect = (RectTransform)plantGo.transform;
            plantRect.anchorMin = new Vector2(0f, 0f);
            plantRect.anchorMax = new Vector2(1f, 1f);
            plantRect.offsetMin = new Vector2(0f, letterRowHeight);
            plantRect.offsetMax = Vector2.zero;
            plantGo.GetComponent<RawImage>().raycastTarget = false;

            var plant = plantGo.GetComponent<LetterPlant>();
            plant.growDuration = growDuration;
            plant.witherDuration = witherDuration;
            plant.tickRate = tickRate;
            plant.stemColor = stemColor;
            plant.leafColor = leafColor;
            plant.leafBlobRadius = leafBlobRadius;
            int idx = i;
            plant.onFinished += _ => SpawnTypeLetter(idx);
            _plants[i] = plant;
            onPlantSpawned?.Invoke(plant);
        }
        BuildCaret();
        RefreshDisplayLayout(true);
    }

    private void RefreshDisplayLayout(bool force = false)
    {
        float scale = Mathf.Max(0.01f, _canvas != null ? _canvas.scaleFactor : 1f);
        float dividerTop = _rect.rect.yMax;
        if (_divider != null)
        {
            _divider.GetWorldCorners(_dividerCorners);
            var parent = _rect.parent as RectTransform;
            if (parent != null && Mathf.Approximately(_rect.anchorMin.y, 0f) && Mathf.Approximately(_rect.anchorMax.y, 0f))
            {
                float bottom = parent.rect.yMin + _rect.anchoredPosition.y - _rect.pivot.y * _rect.rect.height;
                float targetHeight = parent.InverseTransformPoint(_dividerCorners[0]).y - bottom;
                if (targetHeight > letterRowHeight && Mathf.Abs(targetHeight - _rect.rect.height) > 0.01f)
                {
                    _rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
                    Vector2 position = _rect.anchoredPosition;
                    position.y = bottom - parent.rect.yMin + _rect.pivot.y * targetHeight;
                    _rect.anchoredPosition = position;
                    force = true;
                }
            }
            dividerTop = _rect.InverseTransformPoint(_dividerCorners[1]).y;
        }

        float letterSize = Mathf.Max(letterFontSize, minimumLetterScreenSize / scale);
        float rowHeight = Mathf.Max(letterRowHeight, letterBottomInset + letterSize * 1.4f + 4f);

        bool changed = force || !Mathf.Approximately(_displayLetterSize, letterSize) ||
            !Mathf.Approximately(_displayRowHeight, rowHeight);
        _displayLetterSize = letterSize; _displayRowHeight = rowHeight;

        // 上半屏几何每帧重算：屏幕尺寸或分割线位置变化时，打字机与斜线都要跟随
        float canvasTop = _rect.rect.yMax;
        if (_canvas != null)
        {
            ((RectTransform)_canvas.transform).GetWorldCorners(_canvasCorners);
            canvasTop = _rect.InverseTransformPoint(_canvasCorners[1]).y;
        }
        UpdateUpperLayout(canvasTop, dividerTop);

        if (!changed) return;

        for (int i = 0; i < _letterLabels.Length; i++)
        {
            var label = _letterLabels[i];
            label.fontSize = letterSize;
            label.rectTransform.offsetMin = new Vector2(4f, letterBottomInset);
            label.rectTransform.offsetMax = new Vector2(-4f, rowHeight);
            var plantRect = (RectTransform)_plants[i].transform;
            plantRect.offsetMin = new Vector2(0f, rowHeight);
        }
    }

    private TextMeshProUGUI CreateLabel(Transform parent, string name, int fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.layer = gameObject.layer;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.font = _font;
        if (_labelMaterial != null) tmp.fontSharedMaterial = _labelMaterial;
        tmp.fontSize = fontSize;
        tmp.enableAutoSizing = false;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.extraPadding = true;
        tmp.margin = Vector4.zero;
        tmp.color = color;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>上半屏分割线的运行时容器：两个端点标记供 FlowFire 读取，一条可见的 45° 斜线</summary>
    private void BuildUpperRig()
    {
        if (_rect == null || _rect.parent == null) return;

        var existing = _rect.parent.Find("UpperSplitRig");
        if (existing != null) Destroy(existing.gameObject);

        var rigGo = new GameObject("UpperSplitRig", typeof(RectTransform));
        rigGo.transform.SetParent(_rect.parent, false);
        rigGo.layer = gameObject.layer;
        _upperRig = (RectTransform)rigGo.transform;
        _upperRig.anchorMin = Vector2.zero;
        _upperRig.anchorMax = Vector2.one;
        _upperRig.offsetMin = Vector2.zero;
        _upperRig.offsetMax = Vector2.zero;
        _upperRig.SetAsLastSibling(); // 斜线压在打字机字母之上

        _diagStart = CreateRigMarker("UpperSplitStart");
        _diagEnd = CreateRigMarker("UpperSplitEnd");

        var lineGo = new GameObject("UpperSplitLine", typeof(RectTransform), typeof(Image));
        lineGo.transform.SetParent(_upperRig, false);
        lineGo.layer = gameObject.layer;
        _diagLine = lineGo.GetComponent<Image>();
        _diagLine.color = diagonalColor;
        _diagLine.raycastTarget = false;
        var lineRect = (RectTransform)_diagLine.transform;
        lineRect.anchorMin = lineRect.anchorMax = new Vector2(0.5f, 0.5f);
        lineRect.pivot = new Vector2(0.5f, 0.5f);
    }

    private RectTransform CreateRigMarker(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(_upperRig, false);
        go.layer = gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = Vector2.zero;
        return rt;
    }

    /// <summary>打字机光标：显示在下一个待键入位置</summary>
    private void BuildCaret()
    {
        _caret = CreateLabel(transform, "TypewriterCaret", Mathf.RoundToInt(typewriterFontSize), typewriterCaretColor);
        if (_typedMaterial != null) _caret.fontSharedMaterial = _typedMaterial;
        _caret.text = "_";
        var rt = _caret.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
    }

    /// <summary>植物到达终点 → 把字母键入上半屏左侧的打字机区域</summary>
    private void SpawnTypeLetter(int letterIndex)
    {
        char c = letters[letterIndex];
        var tmp = CreateLabel(transform, $"Typed_{c}", Mathf.RoundToInt(_typedFontSize), typewriterColor);
        if (_typedMaterial != null) tmp.fontSharedMaterial = _typedMaterial;
        tmp.text = c.ToString();
        tmp.color = new Color(typewriterColor.r, typewriterColor.g, typewriterColor.b, 0f);

        var rt = tmp.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(_typedCellWidth, _typedCellHeight);

        var entry = new TypedEntry { rect = rt, alpha = 0f };
        _typed.Add(entry);
        EnsureTypewriterCapacity();
        ApplyTypewriterLayout();
        rt.anchoredPosition = entry.target - new Vector2(0f, 10f);

        Debug.Log($"[AlphabetBar] ⌨ {c} 已键入，当前字母数={_typed.Count}");
    }

    /// <summary>擦掉 localPos 半径内的打字机字母（指尖涂抹）。返回是否擦掉一个</summary>
    public bool EraseTypedAt(Vector2 localPos, float radius)
    {
        for (int i = 0; i < _typed.Count; i++)
        {
            var e = _typed[i];
            if (e.rect == null)
            {
                _typed.RemoveAt(i);
                ApplyTypewriterLayout();
                return true;
            }

            if ((TypedLocalPos(e) - localPos).sqrMagnitude <= radius * radius)
            {
                Destroy(e.rect.gameObject);
                _typed.RemoveAt(i);
                ApplyTypewriterLayout(); // 后面的字母重新排到前面的空格
                return true;
            }
        }
        return false;
    }

    /// <summary>字母当前在 bar 局部坐标里的中心位置（锚点固定在左上角）</summary>
    private Vector2 TypedLocalPos(TypedEntry e)
    {
        return new Vector2(_rect.rect.xMin + e.rect.anchoredPosition.x,
                           _rect.rect.yMax + e.rect.anchoredPosition.y);
    }

    /// <summary>根据屏幕上半区与斜线边界，更新打字机排版和斜线端点</summary>
    private void UpdateUpperLayout(float canvasTop, float dividerTop)
    {
        float upperHeight = Mathf.Max(1f, canvasTop - dividerTop);
        float diagTopX = _rect.rect.xMin + _rect.rect.width * diagonalTopFraction;
        float angleRad = diagonalAngleDeg * Mathf.Deg2Rad;
        float diagBottomX = diagTopX + Mathf.Tan(angleRad) * upperHeight;

        // 不要把斜线推到屏幕最右侧，火焰区至少留一点宽度
        float limit = _rect.rect.xMax - 120f;
        if (diagBottomX > limit)
        {
            float shift = diagBottomX - limit;
            diagTopX -= shift;
            diagBottomX = limit;
        }

        _typedFontSize = Mathf.Max(24f, Mathf.Min(typewriterFontSize, upperHeight * 0.2f));
        _typedCellWidth = _typedFontSize * 0.86f;
        _typedCellHeight = _typedFontSize * 1.28f;

        float left = _rect.rect.xMin + typewriterLeftInset;
        float top = canvasTop - typewriterTopInset;
        float bottom = dividerTop + 24f;
        float usableWidth = Mathf.Max(_typedCellWidth, diagTopX - 24f - left);
        float usableHeight = Mathf.Max(_typedCellHeight, top - bottom);
        _typedColumns = Mathf.Max(1, Mathf.FloorToInt(usableWidth / _typedCellWidth));
        _typedRows = Mathf.Max(1, Mathf.FloorToInt(usableHeight / _typedCellHeight));
        _typedOriginX = left;
        _typedOriginY = top;

        UpdateDiagonalLine(diagTopX, diagBottomX, canvasTop, dividerTop);
        EnsureTypewriterCapacity();
        ApplyTypewriterLayout();
    }

    private void UpdateDiagonalLine(float topX, float bottomX, float topY, float bottomY)
    {
        if (_upperRig == null || _diagLine == null) return;

        Vector2 top = RectToRigLocal(new Vector2(topX, topY));
        Vector2 bottom = RectToRigLocal(new Vector2(bottomX, bottomY));
        _diagStart.anchoredPosition = top;
        _diagEnd.anchoredPosition = bottom;

        Vector2 delta = bottom - top;
        _diagLine.rectTransform.anchoredPosition = (top + bottom) * 0.5f;
        _diagLine.rectTransform.sizeDelta = new Vector2(delta.magnitude, Mathf.Max(1f, diagonalThickness));
        _diagLine.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        _diagLine.color = diagonalColor;
    }

    private Vector2 RectToRigLocal(Vector2 pointInBar)
    {
        Vector3 world = _rect.TransformPoint(new Vector3(pointInBar.x, pointInBar.y, 0f));
        Vector2 local = _upperRig.InverseTransformPoint(world);
        return local - _upperRig.rect.center;
    }

    private void EnsureTypewriterCapacity()
    {
        int capacity = Mathf.Max(1, _typedColumns * _typedRows);
        while (_typed.Count > capacity)
        {
            var oldest = _typed[0];
            _typed.RemoveAt(0);
            if (oldest.rect != null) Destroy(oldest.rect.gameObject);
        }
    }

    /// <summary>按键入顺序从左到右、从上到下排字；光标跟在最后一个字母之后</summary>
    private void ApplyTypewriterLayout()
    {
        if (_typedColumns <= 0) return;

        for (int i = 0; i < _typed.Count; i++)
        {
            var e = _typed[i];
            if (e.rect == null) continue;

            int row = i / _typedColumns;
            int col = i % _typedColumns;
            e.target = new Vector2(
                _typedOriginX + (col + 0.5f) * _typedCellWidth - _rect.rect.xMin,
                _typedOriginY - (row + 0.5f) * _typedCellHeight - _rect.rect.yMax);
            e.rect.sizeDelta = new Vector2(_typedCellWidth, _typedCellHeight);

            var tmp = e.rect.GetComponent<TextMeshProUGUI>();
            if (tmp != null) tmp.fontSize = _typedFontSize;
        }
        UpdateCaretLayout();
    }

    private void UpdateCaretLayout()
    {
        if (_caret == null || _typedColumns <= 0) return;

        int capacity = Mathf.Max(1, _typedColumns * _typedRows);
        int index = _typed.Count;
        if (index >= capacity)
        {
            _caret.gameObject.SetActive(false);
            return;
        }

        _caret.gameObject.SetActive(true);
        int row = index / _typedColumns;
        int col = index % _typedColumns;
        _caret.rectTransform.anchoredPosition = new Vector2(
            _typedOriginX + (col + 0.5f) * _typedCellWidth - _rect.rect.xMin,
            _typedOriginY - (row + 0.5f) * _typedCellHeight - _rect.rect.yMax);
        _caret.rectTransform.sizeDelta = new Vector2(_typedCellWidth, _typedCellHeight);
        _caret.fontSize = _typedFontSize;
    }

    private void AnimateTypewriter(float dt)
    {
        float moveK = 1f - Mathf.Exp(-typewriterReflowSpeed * dt);
        float fadeK = 1f - Mathf.Exp(-12f * dt);

        foreach (var e in _typed)
        {
            if (e.rect == null) continue;

            if (e.alpha < 1f)
            {
                e.alpha = Mathf.Min(1f, Mathf.Lerp(e.alpha, 1f, fadeK));
                var tmp = e.rect.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    Color c = typewriterColor;
                    c.a *= e.alpha;
                    tmp.color = c;
                }
            }

            if ((e.rect.anchoredPosition - e.target).sqrMagnitude > 0.01f)
                e.rect.anchoredPosition = Vector2.Lerp(e.rect.anchoredPosition, e.target, moveK);
        }

        if (_caret != null && _caret.gameObject.activeSelf)
            _caret.canvasRenderer.SetAlpha(0.35f + 0.65f * Mathf.Abs(Mathf.Sin(Time.time * 3.2f)));
    }

    private void LateUpdate()
    {
        if (_plants == null) return;
        RefreshDisplayLayout();

        // —— 打字机字母淡入 / 重排动画 ——
        AnimateTypewriter(Time.deltaTime);

        // —— 模式联动开关 ——
        bool rainMode = playMode == PlayMode.GlyphRain && glyphRain != null;
        if (glyphRain != null) glyphRain.rainEnabled = rainMode;
        if (rayProvider != null) rayProvider.rayVisualEnabled = !rainMode;

        if (rainMode) TickRainMode();
        else TickRayMode();
    }

    private void OnDestroy()
    {
        if (_labelMaterial != null) Destroy(_labelMaterial);
        if (_typedMaterial != null) Destroy(_typedMaterial);
    }

    /// <summary>模式一：食指射线指点（原逻辑）</summary>
    private void TickRayMode()
    {
        // —— 焦点计算 ——
        bool hasFocus = false;
        float hitLocalX = 0f;

        if (rayProvider != null
            && rayProvider.LastHitEdge.HasValue
            && rayProvider.LastHitEdge.Value == RightHandIndexRayProvider.ScreenEdge.Bottom
            && TryScreenToLocal(rayProvider.LastScreenHitPoint, out Vector2 local))
        {
            hitLocalX = local.x;
            hasFocus = true;
        }

        float barWidth = _rect.rect.width;
        float barLeft = _rect.rect.xMin;   // 局部坐标以 pivot 为原点，左边缘不一定是 0
        int n = _plants.Length;
        float letterW = barWidth / n;
        float sigmaPx = Mathf.Max(tolerancePx / 2.5f, 1f); // 与 BottomLine 高光共用同一 σ

        for (int i = 0; i < n; i++)
        {
            float input = 0f;
            if (hasFocus && barWidth > 1f)
            {
                float centerX = barLeft + (i + 0.5f) * letterW;
                float d = Mathf.Abs(hitLocalX - centerX);
                float half = letterW * 0.5f;
                if (d <= half) input = 1f;                                        // 焦点字母满输入
                else input = Mathf.Exp(-(d * d) / (2f * sigmaPx * sigmaPx));      // 容忍区高斯衰减
            }
            if (input < 0.02f) input = 0f; // 截断微小输入，否则植物永不退化
            _plants[i].freezeWhenFinished = freezeAfterTyping;
            _plants[i].SetFocus(input);
        }

        // —— BottomLine 高光与焦点/容忍区一致 ——
        if (bottomGlow != null && barWidth > 1f)
        {
            bottomGlow.spread = Mathf.Clamp(sigmaPx / barWidth, 0.02f, 0.5f); // σ 换成归一化宽度
            if (hasFocus)
                bottomGlow.SetHit(Mathf.Clamp01((hitLocalX - barLeft) / barWidth));
            // 不命中时不 ClearHit，让它按自己的 fadeDuration 自然消退
        }
    }

    /// <summary>模式二：双食指屋檐挡雨浇灌</summary>
    private void TickRainMode()
    {
        float dt = Time.deltaTime;
        int n = _plants.Length;

        // 缝隙（bar 局部坐标，由 GlyphRain 统一计算：双手=两指之间，单手=边缘到指尖）
        float barLeft = _rect.rect.xMin;
        float gapL = barLeft, gapR = barLeft;
        bool gapValid = glyphRain != null && glyphRain.TryGetGapLocal(out gapL, out gapR);
        // 单手模式缝隙一侧会超出 bar 边缘，钳回范围内（覆盖/高光都按可见部分算）
        gapL = Mathf.Max(gapL, barLeft);
        gapR = Mathf.Min(gapR, _rect.rect.xMax);

        float letterW = _rect.rect.width / n;
        float decay = Mathf.Exp(-rainHeatDecay * dt);

        for (int i = 0; i < n; i++)
        {
            _rainHeat[i] *= decay;
            float input = Mathf.Clamp01(_rainHeat[i]);

            // 缝隙覆盖保底：字母被缝隙罩住的比例 → 基础输入（雨浇是额外加成）
            if (gapValid && letterW > 0.01f)
            {
                float half = letterW * 0.5f;
                float cx = barLeft + (i + 0.5f) * letterW;
                float overlap = Mathf.Min(cx + half, gapR) - Mathf.Max(cx - half, gapL);
                float coverage = Mathf.Clamp01(overlap / letterW);
                input = Mathf.Max(input, coverage * apertureInput);
            }
            if (input < 0.02f) input = 0f; // 热量指数衰减永远趋近 0 但不归零，截断它植物才能进入退化
            _plants[i].freezeWhenFinished = freezeAfterTyping;
            _plants[i].SetFocus(input);
        }

        // BottomLine 高光跟着缝隙走：位置=缝隙中心，宽度≈缝隙
        if (bottomGlow != null && gapValid)
        {
            float barWidth = _rect.rect.width;
            if (barWidth > 1f)
            {
                float center = (gapL + gapR) * 0.5f;
                float halfW = (gapR - gapL) * 0.5f;
                bottomGlow.spread = Mathf.Clamp(halfW * 0.5f / barWidth, 0.02f, 0.5f);
                bottomGlow.SetHit(Mathf.Clamp01((center - barLeft) / barWidth));
            }
        }
    }

    /// <summary>乱码雨落地回调：给落点附近的字母加热量（高斯扩散）</summary>
    public void WaterAtLocalX(float localX, float amount, float sigmaPx)
    {
        if (playMode != PlayMode.GlyphRain || _rainHeat == null) return;
        float barLeft = _rect.rect.xMin;
        float letterW = _rect.rect.width / _rainHeat.Length;
        float twoSigmaSq = 2f * sigmaPx * sigmaPx;

        for (int i = 0; i < _rainHeat.Length; i++)
        {
            float d = localX - (barLeft + (i + 0.5f) * letterW);
            _rainHeat[i] = Mathf.Min(_rainHeat[i] + amount * Mathf.Exp(-(d * d) / twoSigmaSq), 1.5f);
        }
    }

    private bool TryScreenToLocal(Vector2 screenPos, out Vector2 local)
    {
        Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? _canvas.worldCamera : null;
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, screenPos, cam, out local);
    }
}
