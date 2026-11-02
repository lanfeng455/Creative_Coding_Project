// LetterPlant.cs —— 单个字母上方的"元胞自动机植物"
// 挂在带 RawImage 的 UI 物体上，自动创建 24×80 点采样纹理
// 对外只有一个接口：SetFocus(float input) —— input∈[0,1]，0=射线没指过来
// input>0 时能量累积 → 植物向上生长（随机游走+分枝）；input=0 时能量衰减 → 从顶部向下凋亡

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
[DisallowMultipleComponent]
public class LetterPlant : MonoBehaviour
{
    [Header("网格（1 格 = 1 纹理像素，点采样放大）")]
    public int gridWidth = 24;
    public int gridHeight = 80;

    [Header("能量动力学")]
    [Tooltip("input=1 时能量从 0 涨满所需秒数")]
    public float growDuration = 4f;
    [Tooltip("没有输入时能量从满衰减到 0 所需秒数")]
    public float witherDuration = 5f;

    [Header("生长行为（每 tick 的概率）")]
    [Tooltip("CA tick 频率（次/秒）")]
    public float tickRate = 8f;
    [Range(0f, 0.3f)] public float branchChance = 0.025f;  // 调低：分枝太多显乱
    [Range(0f, 0.5f)] public float leafChance = 0.12f;
    [Tooltip("生长点随机死亡概率（死亡后从当前最高点续长，不回底部）")]
    [Range(0f, 0.2f)] public float tipDeathChance = 0.008f;
    public int maxTips = 3;

    [Header("外观")]
    public Color stemColor = new Color(0.88f, 1f, 0.88f, 1f);
    [Tooltip("凋亡时每 tick 透明度衰减量")]
    [Range(0.02f, 0.5f)] public float witherFadePerTick = 0.15f;

    // —— 内部状态 ——
    private float _energy;          // 0..1
    private float _focusInput;      // 本帧输入 0..1
    private float[] _cells;         // 每格 alpha 0..1
    private Texture2D _tex;
    private Color32[] _pixels;
    private RawImage _img;
    private float _tickTimer;
    private bool _dirty;

    private struct Tip { public int x, y; public float drift; }
    private readonly List<Tip> _tips = new List<Tip>();
    private System.Random _rng;

    private float _growSpeed;   // energy/sec at input=1
    private float _witherSpeed; // energy/sec

    /// <summary>当前能量（0..1），调试/外部可用</summary>
    public float Energy => _energy;

    /// <summary>是否已到达终点（植物触及分割线）。冻结模式下 true 后不再变化；允许退化模式下跌离终点线会重新武装</summary>
    public bool IsFinished { get; private set; }

    [Tooltip("到达终点后是否冻结（由 LetterAlphabetBar.freezeAfterFruit 每帧同步，勿手改）")]
    public bool freezeWhenFinished = true;

    /// <summary>到达终点时触发（参数为自身）</summary>
    public event System.Action<LetterPlant> onFinished;

    private void Awake()
    {
        _img = GetComponent<RawImage>();
        _growSpeed = 1f / Mathf.Max(growDuration, 0.01f);
        _witherSpeed = 1f / Mathf.Max(witherDuration, 0.01f);
        _rng = new System.Random(GetInstanceID() ^ Environment_TickCount());
        _cells = new float[gridWidth * gridHeight];
        _pixels = new Color32[gridWidth * gridHeight];

        _tex = new Texture2D(gridWidth, gridHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _tex.SetPixels32(_pixels);
        _tex.Apply();
        _img.texture = _tex;
    }

    private static int Environment_TickCount() => System.Environment.TickCount;

    private void OnDestroy()
    {
        if (_tex != null) Destroy(_tex);
    }

    /// <summary>每帧由 LetterAlphabetBar 调用。input∈[0,1]</summary>
    public void SetFocus(float input)
    {
        _focusInput = Mathf.Clamp01(input);
    }

    private void Update()
    {
        if (IsFinished)
        {
            if (freezeWhenFinished) return; // 冻结模式：树定格，不再退化也不再生长
            // 允许退化：顶部跌离终点线一定距离后重新武装 → 之后再次长到顶会再次结果
            if (MaxCellY() < gridHeight - finishMarginCells - rearmMarginCells)
                IsFinished = false;
        }

        // 1) 能量积分（能量只决定"允许长多高"，不直接触发结果）
        // 阈值 0.01：微小残余输入不算"有输入"，否则能量永远缓慢增长、永不退化
        if (_focusInput > 0.01f)
            _energy = Mathf.Clamp01(_energy + _focusInput * _growSpeed * Time.deltaTime);
        else
            _energy = Mathf.Clamp01(_energy - _witherSpeed * Time.deltaTime);

        // 2) CA tick
        _tickTimer += Time.deltaTime;
        float tickInterval = 1f / Mathf.Max(tickRate, 0.1f);
        while (_tickTimer >= tickInterval)
        {
            _tickTimer -= tickInterval;
            Tick();
        }

        // 3) 有变化才刷纹理
        if (_dirty)
        {
            FlushTexture();
            _dirty = false;
        }
    }

    private float TargetHeight => _energy * gridHeight;

    private void Tick()
    {
        float targetH = TargetHeight;
        bool hasInput = _focusInput >= 0.05f;

        // —— 播种/续长：有输入、有能量、没生长点 ——
        // 已有植株 → 从当前最高处续长；全空 → 底部发芽
        if (hasInput && _energy > 0.04f && _tips.Count == 0)
        {
            int topY = MaxCellY();
            if (topY > 0)
            {
                int topX = FindTopCellX(topY);
                _tips.Add(new Tip { x = topX, y = topY, drift = (float)(_rng.NextDouble() * 0.6 - 0.3) });
            }
            else
            {
                int sx = Mathf.Clamp(gridWidth / 2 + _rng.Next(-3, 4), 1, gridWidth - 2);
                _tips.Add(new Tip { x = sx, y = 0, drift = (float)(_rng.NextDouble() * 0.6 - 0.3) });
                SetCell(sx, 0, 1f);
            }
        }

        // —— 生长点推进（没有输入时冻结：能量只负责维持现有高度，不再长高）——
        if (hasInput)
        {
            for (int i = _tips.Count - 1; i >= 0; i--)
            {
                var t = _tips[i];
                int ny = t.y + 1;

                // 超过能量允许的高度 → 生长点死亡
                if (ny >= gridHeight || ny > targetH || _rng.NextDouble() < tipDeathChance)
                {
                    _tips.RemoveAt(i);
                    continue;
                }

                // 随机游走：drift 缓慢变化，x 按 drift+噪声偏移
                t.drift = Mathf.Clamp(t.drift + (float)(_rng.NextDouble() * 0.6 - 0.3), -1f, 1f);
                int dx = Mathf.RoundToInt(t.drift + (float)(_rng.NextDouble() * 2 - 1) * 0.7f);
                t.x = Mathf.Clamp(t.x + dx, 1, gridWidth - 2);
                t.y = ny;

                SetCell(t.x, t.y, 1f);

                // 叶子：侧向点一格
                if (_rng.NextDouble() < leafChance)
                {
                    int lx = Mathf.Clamp(t.x + (_rng.Next(2) == 0 ? -1 : 1), 0, gridWidth - 1);
                    SetCell(lx, t.y, 0.85f);
                }

                // 分枝
                if (_tips.Count < maxTips && _rng.NextDouble() < branchChance)
                {
                    _tips.Add(new Tip
                    {
                        x = t.x,
                        y = t.y,
                        drift = _rng.Next(2) == 0 ? -0.8f : 0.8f
                    });
                }

                _tips[i] = t;
            }
        }

        // —— 凋亡：高于 targetH 的格子逐渐透明 ——
        int targetRow = Mathf.FloorToInt(targetH);
        for (int y = targetRow + 1; y < gridHeight; y++)
        {
            int rowBase = y * gridWidth;
            for (int x = 0; x < gridWidth; x++)
            {
                int idx = rowBase + x;
                if (_cells[idx] > 0f)
                {
                    _cells[idx] -= witherFadePerTick;
                    if (_cells[idx] <= 0f) _cells[idx] = 0f;
                    _dirty = true;
                }
            }
        }

        // —— 结果判定：实际最高存活格子触及顶部（差 finishMarginCells 格以内）——
        // 注意：不能用能量判定，能量长满 ≠ 植物视觉长到顶（生长受 tick 速率限制）
        if (!IsFinished && MaxCellY() >= gridHeight - finishMarginCells)
        {
            IsFinished = true;
            onFinished?.Invoke(this);
        }
    }

    [Tooltip("最高格子距离顶部多少格以内算到达终点")]
    public int finishMarginCells = 2;
    [Tooltip("允许退化模式下，顶部跌离终点线这么多格后重新武装（滞回，防止在终点线附近反复触发结果）")]
    public int rearmMarginCells = 5;

    /// <summary>从顶向下扫描最高存活格子的行号，无细胞返回 0</summary>
    private int MaxCellY()
    {
        for (int y = gridHeight - 1; y >= 0; y--)
        {
            int rowBase = y * gridWidth;
            for (int x = 0; x < gridWidth; x++)
                if (_cells[rowBase + x] > 0f) return y;
        }
        return 0;
    }

    /// <summary>最高存活行上取一个存活格子的 x（续长用，偏向行中央）</summary>
    private int FindTopCellX(int y)
    {
        int rowBase = y * gridWidth;
        int best = gridWidth / 2;
        int bestDist = int.MaxValue;
        for (int x = 0; x < gridWidth; x++)
        {
            if (_cells[rowBase + x] > 0f)
            {
                int dist = Mathf.Abs(x - gridWidth / 2);
                if (dist < bestDist) { bestDist = dist; best = x; }
            }
        }
        return best;
    }

    private void SetCell(int x, int y, float alpha)
    {
        if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight) return;
        _cells[y * gridWidth + x] = alpha;
        _dirty = true;
    }

    private void FlushTexture()
    {
        byte r = (byte)(stemColor.r * 255);
        byte g = (byte)(stemColor.g * 255);
        byte b = (byte)(stemColor.b * 255);
        for (int i = 0; i < _cells.Length; i++)
        {
            float a = _cells[i];
            if (a <= 0f) { _pixels[i] = new Color32(0, 0, 0, 0); continue; }
            _pixels[i] = new Color32(r, g, b, (byte)(Mathf.Clamp01(a) * 255));
        }
        _tex.SetPixels32(_pixels);
        _tex.Apply(false);
    }
}
