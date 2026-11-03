// LetterAlphabetBar.cs —— 底部 26 字母植物条
// 挂在一个拉伸全宽的 UI 物体（RectTransform）上，运行时自动生成 A-Z 26 格：
// 每格 = 底部 TMP 字母 + 上方 LetterPlant（RawImage）
//
// 每帧轮询 RightHandIndexRayProvider 的底边命中点：
//   - 焦点字母 input=1（长得最快）
//   - 容忍半径 tolerancePx 内按高斯衰减（与 BottomLine 高光共用同一 σ）
//   - 半径外 input=0 → 植物萎缩
//
// 果实：植物能量长满（触及分割线）→ 在该字母列上方生成 TMP 果实，树冻结；
//       新果实出现时，所有旧果实整体上推 fruitSpacing 像素。

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
    [SerializeField, Min(0f)] private float minimumLetterScreenSize = 18f;
    [SerializeField, Min(0f)] private float minimumFruitScreenSize = 24f;
    [SerializeField, Min(0f)] private float letterBottomInset = 24f;

    [Header("容忍区间（与 BottomLine 高光共用同一高斯宽度）")]
    [Tooltip("焦点字母两侧多少像素内的字母也会生长（高斯衰减，2.5σ≈该距离）")]
    public float tolerancePx = 120f;

    [Header("植物参数（生成时传给每棵植物）")]
    public float growDuration = 4f;
    public float witherDuration = 5f;
    public Color stemColor = new Color(0.88f, 1f, 0.88f, 1f);

    [Header("果实（植物长到分割线后）")]
    public int fruitFontSize = 40;
    public Color fruitColor = new Color(1f, 0.9f, 0.4f, 1f);
    [Tooltip("新果实出现时，已有果实向上推的像素距离")]
    public float fruitSpacing = 56f;
    [Tooltip("果实底部与分割线的初始间距（像素）")]
    public float fruitBaseOffset = 8f;
    [Tooltip("果实上推平滑速度（越大越快）")]
    public float fruitRiseSpeed = 6f;

    [Header("BottomLine 高光联动（拖入 BottomLine 上的 EdgeHitGlow）")]
    [Tooltip("由本组件驱动：高光位置=焦点，高斯宽度=容忍区间")]
    public EdgeHitGlow bottomGlow;

    public enum PlayMode { RayPoint, GlyphRain }

    [Tooltip("结果后树是否冻结保留；关闭则树会继续退化，跌离终点线后同一字母可再次结果")]
    public bool freezeAfterFruit = true;

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
    private RectTransform _divider;
    private TextMeshProUGUI[] _letterLabels;
    private readonly Vector3[] _dividerCorners = new Vector3[4];
    private readonly Vector3[] _canvasCorners = new Vector3[4];
    private float _displayLetterSize, _displayFruitSize, _displayRowHeight;
    private float _displayFruitBase, _displayFruitSpacing;

    // Glyph rain must land at the actual displayed row, including its safe inset.
    public float LetterRowDisplayHeight => _displayRowHeight > 0f ? _displayRowHeight : letterRowHeight;

    private class FruitEntry { public RectTransform rect; public float targetY; }
    private readonly List<FruitEntry> _fruits = new List<FruitEntry>();

    private void Awake()
    {
        ApplyNumHands(); // 必须赶在 Runner 消费配置前：Bootstrap 初始化要数帧，Awake 一定更早
        _rect = GetComponent<RectTransform>();
        _canvas = GetComponentInParent<Canvas>();
        if (transform.parent != null)
            _divider = transform.parent.Find("Image") as RectTransform;
        LoadFont();
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
            _labelMaterial.EnableKeyword("OUTLINE_ON");
            _labelMaterial.SetColor("_OutlineColor", new Color(0.04f, 0.05f, 0.05f, 1f));
            _labelMaterial.SetFloat("_OutlineWidth", 0.1f);
        }
    }

    private void Build()
    {
        // 清理上次 Play 的残留（禁用域重载时字段还在但子物体已销毁）
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (c.name.StartsWith("Slot_") || c.name.StartsWith("Fruit_"))
                Destroy(c.gameObject);
        }
        _fruits.Clear();

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
            plant.stemColor = stemColor;
            int idx = i;
            plant.onFinished += _ => SpawnFruit(idx);
            _plants[i] = plant;
        }
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
        float fruitBase = Mathf.Max(0f, dividerTop - _rect.rect.yMax) + Mathf.Max(0f, fruitBaseOffset);
        float fruitSize = Mathf.Max(fruitFontSize, minimumFruitScreenSize / scale);
        if (_canvas != null)
        {
            ((RectTransform)_canvas.transform).GetWorldCorners(_canvasCorners);
            float available = _rect.InverseTransformPoint(_canvasCorners[1]).y - _rect.rect.yMax - fruitBase - 12f;
            // Keep the newest five letters legible inside the upper area.
            float fitSize = (available - 4f * 8f) / (5f * 1.4f);
            if (fitSize > 0f) fruitSize = Mathf.Min(fruitSize, Mathf.Max(12f / scale, fitSize));
        }
        float fruitStep = Mathf.Max(fruitSpacing, fruitSize * 1.4f + 8f);
        bool changed = force || !Mathf.Approximately(_displayLetterSize, letterSize) ||
            !Mathf.Approximately(_displayFruitSize, fruitSize) || !Mathf.Approximately(_displayRowHeight, rowHeight) ||
            !Mathf.Approximately(_displayFruitBase, fruitBase) || !Mathf.Approximately(_displayFruitSpacing, fruitStep);
        _displayLetterSize = letterSize; _displayFruitSize = fruitSize; _displayRowHeight = rowHeight;
        _displayFruitBase = fruitBase; _displayFruitSpacing = fruitStep;
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
        foreach (var fruit in _fruits)
        {
            if (fruit.rect == null) continue;
            fruit.rect.GetComponent<TextMeshProUGUI>().fontSize = fruitSize;
            fruit.rect.sizeDelta = new Vector2(fruitSize * 2f, fruitSize * 1.4f);
        }
        RepackFruits();
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

    /// <summary>植物到达终点 → 在该字母列、分割线上方生成果实，并把旧果实上推一格</summary>
    private void SpawnFruit(int letterIndex)
    {
        float fx = (letterIndex + 0.5f) / _plants.Length;
        var tmp = CreateLabel(transform, $"Fruit_{letters[letterIndex]}", fruitFontSize, fruitColor);
        tmp.text = letters[letterIndex].ToString();
        tmp.fontSize = _displayFruitSize;

        var fr = (RectTransform)tmp.transform;
        fr.anchorMin = fr.anchorMax = new Vector2(fx, 1f); // 锚在条顶边（分割线）
        fr.pivot = new Vector2(0.5f, 0f);
        fr.sizeDelta = new Vector2(_displayFruitSize * 2f, _displayFruitSize * 1.4f);
        fr.anchoredPosition = new Vector2(0f, _displayFruitBase);

        _fruits.Add(new FruitEntry { rect = fr, targetY = fruitBaseOffset });
        RepackFruits(); // 旧果实整体上推一格

        Debug.Log($"[AlphabetBar] 🍎 {letters[letterIndex]} 结果！当前果实数={_fruits.Count}");
    }

    /// <summary>擦掉 localPos 半径内的果实（屋檐举到雨区上方时的橡皮擦）。返回是否擦掉一个</summary>
    public bool EraseFruitAt(Vector2 localPos, float radius)
    {
        for (int i = 0; i < _fruits.Count; i++)
        {
            var f = _fruits[i];
            if (f.rect == null) { _fruits.RemoveAt(i); RepackFruits(); return true; }

            if ((FruitLocalPos(f) - localPos).sqrMagnitude <= radius * radius)
            {
                Destroy(f.rect.gameObject);
                _fruits.RemoveAt(i);
                RepackFruits(); // 剩余果实重新分配 offset，动画滑到新位置
                return true;
            }
        }
        return false;
    }

    /// <summary>果实当前在 bar 局部坐标里的位置（锚点为 (fx,1)，pivot 落在 anchor + anchoredPosition）</summary>
    private Vector2 FruitLocalPos(FruitEntry f)
    {
        var r = _rect.rect;
        Vector2 ap = f.rect.anchoredPosition;
        // pivot 在果实底边，判定点取果实文字中心（往上约 0.7 个字高）
        return new Vector2(r.xMin + f.rect.anchorMin.x * r.width + ap.x, r.yMax + ap.y + _displayFruitSize * 0.7f);
    }

    /// <summary>按创建顺序重排 targetY：最新果实贴分割线，越老越高</summary>
    private void RepackFruits()
    {
        for (int i = 0; i < _fruits.Count; i++)
            _fruits[i].targetY = _displayFruitBase + (_fruits.Count - 1 - i) * _displayFruitSpacing;
    }

    private void LateUpdate()
    {
        if (_plants == null) return;
        RefreshDisplayLayout();

        // —— 果实上推动画 ——
        float k = 1f - Mathf.Exp(-fruitRiseSpeed * Time.deltaTime);
        foreach (var f in _fruits)
        {
            if (f.rect == null) continue;
            Vector2 pos = f.rect.anchoredPosition;
            pos.y = Mathf.Lerp(pos.y, f.targetY, k);
            f.rect.anchoredPosition = pos;
        }

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
            _plants[i].freezeWhenFinished = freezeAfterFruit;
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
            _plants[i].freezeWhenFinished = freezeAfterFruit;
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
