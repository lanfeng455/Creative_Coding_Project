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
    [Tooltip("叶片大小：1≈小，2≈大，3≈超大。大叶片可当恐龙跳台（越大生成越稀疏）")]
    [Range(0, 3)] public int leafBlobRadius = 2;
    [Tooltip("生长点随机死亡概率（死亡后从当前最高点续长，不回底部）")]
    [Range(0f, 0.2f)] public float tipDeathChance = 0.008f;
    public int maxTips = 3;

    [Header("外观")]
    public Color stemColor = new Color(0.88f, 1f, 0.88f, 1f);
    [Tooltip("叶片颜色（与枝干区分；恐龙只采摘登记在册的叶片格）")]
    public Color leafColor = new Color(0.42f, 1f, 0.5f, 1f);
    [Tooltip("凋亡时每 tick 透明度衰减量")]
    [Range(0.02f, 0.5f)] public float witherFadePerTick = 0.15f;
    [Tooltip("叶片用字符贴图渲染（!#$%^&* 这类符号，恐龙拿同样的字符搭庇护所）；关闭则退回方块状 CA 像素")]
    public bool useLeafSprites = true;

    // —— 内部状态 ——
    private float _energy;          // 0..1
    private float _focusInput;      // 本帧输入 0..1
    private float[] _cells;         // 每格 alpha 0..1
    private Texture2D _tex;
    private Color32[] _pixels;
    private RawImage _img;
    private float _tickTimer;
    private bool _dirty;
    private bool _corrupted;       // 本帧是否处于腐化区内（屏蔽生长输入）

    private struct Tip { public int x, y; public float drift; }
    private readonly List<Tip> _tips = new List<Tip>();
    private System.Random _rng;

    private float _growSpeed;   // energy/sec at input=1
    private float _witherSpeed; // energy/sec

    /// <summary>当前能量（0..1），调试/外部可用</summary>
    public float Energy => _energy;

    /// <summary>是否已到达终点（植物触及分割线）。冻结模式下 true 后不再变化；允许退化模式下跌离终点线会重新武装</summary>
    public bool IsFinished { get; private set; }

    [Tooltip("到达终点后是否冻结（由 LetterAlphabetBar.freezeAfterTyping 每帧同步，勿手改）")]
    public bool freezeWhenFinished = true;

    /// <summary>到达终点时触发（参数为自身）</summary>
    public event System.Action<LetterPlant> onFinished;

    // —— 叶片登记（Prototype B：供恐龙等外部系统采摘；不改变 CA 生长/凋亡规则）——
    // 现状：CA 里"叶片"只是生长点侧向点出的一格半透明细胞（alpha=0.85），与枝干
    // （alpha=1）共用同一张 _cells 数组，原本没有独立身份。这里给它加登记表：
    // 出现/消失时对外发事件；采摘只把该格 alpha 清零，空缺由后续正常生长自然补充。
    public event System.Action<LetterPlant, int> onLeafAppeared;  // (plant, 叶片中心格索引)
    public event System.Action<LetterPlant, int> onLeafVanished;  // 凋亡或被采摘都触发
    private readonly HashSet<int> _leafCells = new HashSet<int>();

    /// <summary>一片完整叶片（一个斑块 = 一个对外身份），采摘/事件都用它的中心格索引</summary>
    private class LeafBlob
    {
        public int Anchor;          // 中心格索引
        public int Cx, Cy;          // 中心格坐标
        public int Rx, Ry;          // 横向/纵向半径（格）
        public int Side;            // 长在枝干的哪一侧（-1 左 / +1 右）
        public char Char;           // 叶片字符（与庇护所共用同一套字符集）
        public float Tilt;          // 轻微倾斜，避免所有字符都正襟危坐
        public float Tint = 1f;     // 随机明暗，避免整棵树的叶子一模一样
        public List<int> Cells = new List<int>();
        public Image Visual;        // 独立叶片贴图（useLeafSprites=false 时为空）
        public RectTransform Rt;
    }
    private readonly Dictionary<int, LeafBlob> _blobs = new Dictionary<int, LeafBlob>();

    /// <summary>已采摘/凋亡叶片的飞散动画</summary>
    private struct LeafFx
    {
        public Image Img;
        public RectTransform Rt;
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Rot, Spin;
        public float SignX;         // 镜像叶子的缩放符号（飞散动画要保留；PlayLeafFx 里总是赋值）
    }
    private readonly List<LeafFx> _leafFx = new List<LeafFx>();
    private Vector2 _lastRectSize = Vector2.zero;

    // ———— 字符点阵：叶片与庇护所共用同一套字符，语义统一 ————

    /// <summary>5×7 点阵字库（1=有墨，第 0 行是字形顶部）</summary>
    private static readonly Dictionary<char, string[]> Glyph5x7 = new Dictionary<char, string[]>
    {
        ['!'] = new[] { "00100", "00100", "00100", "00100", "00100", "00000", "00100" },
        ['#'] = new[] { "01010", "11111", "01010", "01010", "11111", "01010", "01010" },
        ['$'] = new[] { "00100", "01111", "10100", "01110", "00101", "11110", "00100" },
        ['%'] = new[] { "11001", "11010", "00100", "00100", "01000", "10011", "10011" },
        ['&'] = new[] { "01100", "10010", "01100", "01010", "10001", "10001", "01110" },
        ['*'] = new[] { "00000", "10101", "01110", "11111", "01110", "10101", "00000" },
        ['@'] = new[] { "01110", "10001", "10111", "10101", "10111", "10000", "01110" },
        ['?'] = new[] { "01110", "10001", "00001", "00010", "00100", "00000", "00100" },
        ['+'] = new[] { "00000", "00100", "00100", "11111", "00100", "00100", "00000" },
        ['='] = new[] { "00000", "00000", "11111", "00000", "11111", "00000", "00000" },
        ['~'] = new[] { "00000", "00000", "01001", "10110", "00000", "00000", "00000" },
        ['^'] = new[] { "00100", "01010", "10001", "00000", "00000", "00000", "00000" },
        ['/'] = new[] { "00001", "00001", "00010", "00100", "01000", "10000", "10000" },
        ['\\'] = new[] { "10000", "10000", "01000", "00100", "00010", "00001", "00001" },
        ['|'] = new[] { "00100", "00100", "00100", "00100", "00100", "00100", "00100" },
        ['_'] = new[] { "00000", "00000", "00000", "00000", "00000", "00000", "11111" },
    };

    /// <summary>叶片/庇护所使用的字符集（!#$%^&* 这类符号）</summary>
    public static readonly char[] LeafAlphabet =
        { '!', '#', '$', '%', '&', '*', '@', '?', '+', '=', '~', '^', '/', '\\', '|', '_' };

    private static readonly Dictionary<char, Sprite> _glyphCache = new Dictionary<char, Sprite>();

    /// <summary>取字符贴图：白色（靠 Image.color / SpriteRenderer.color 染色），高 1 世界单位，宽高比 5:7</summary>
    public static Sprite GlyphSprite(char c)
    {
        if (_glyphCache.TryGetValue(c, out var cached) && cached != null) return cached;
        string[] rows = Glyph5x7.TryGetValue(c, out var g) ? g : Glyph5x7['*'];
        const int w = 5, h = 7;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "glyph " + c };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool ink = y < rows.Length && x < rows[y].Length && rows[y][x] == '1';
            px[(h - 1 - y) * w + x] = new Color32(255, 255, 255, ink ? (byte)255 : (byte)0);
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 7f);
        _glyphCache[c] = sp;
        return sp;
    }

    /// <summary>随机一个字符贴图（采摘碎屑 / 建造动画用）</summary>
    public static Sprite RandomGlyphSprite()
        => GlyphSprite(LeafAlphabet[Random.Range(0, LeafAlphabet.Length)]);

    /// <summary>随机一个字符（叶片本体用）</summary>
    public static char RandomGlyphChar()
        => LeafAlphabet[Random.Range(0, LeafAlphabet.Length)];

    /// <summary>每 tick 生成叶片的概率：叶片越大越稀疏，避免整株变成一团绿色方块</summary>
    private float LeafBlobChance => leafChance / (1f + 2f * Mathf.Clamp(leafBlobRadius, 0, 3));

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

    /// <summary>外部腐化：直接扣能量（复用现有"能量衰减→顶部凋亡"规则，不碰 CA 核心）。
    /// 紫色字符的腐化区域每帧调用；停止扣能量后植物即可被正常浇灌恢复。</summary>
    public void DrainEnergy(float amount)
    {
        if (IsFinished && freezeWhenFinished) return; // 已冻结入仓的树不受腐化
        _energy = Mathf.Max(0f, _energy - amount);
    }

    /// <summary>世界坐标点（及其左右 ±radius 采样）是否落在本植物矩形内（腐化区域覆盖判定用）</summary>
    public bool WorldHitsPlant(Vector3 world, float radius)
    {
        if (WorldToCell(world, out _, out _)) return true;
        if (radius <= 0f) return false;
        return WorldToCell(world + Vector3.left * radius, out _, out _)
            || WorldToCell(world + Vector3.right * radius, out _, out _);
    }

    private void Update()
    {
        // 叶片动画/布局与植物生长无关，冻结状态下也要继续跑
        UpdateLeafFx(Time.deltaTime);
        CheckRectResize();

        if (IsFinished)
        {
            if (freezeWhenFinished) return; // 冻结模式：树定格，不再退化也不再生长
            // 允许退化：顶部跌离终点线一定距离后重新武装 → 之后再次长到顶会再次结果
            if (MaxCellY() < gridHeight - finishMarginCells - rearmMarginCells)
                IsFinished = false;
        }

        // 1) 能量积分（能量只决定"允许长多高"，不直接触发结果）
        // 阈值 0.01：微小残余输入不算"有输入"，否则能量永远缓慢增长、永不退化
        // 腐化区内的植物：屏蔽所有能量增益，只衰减（让它快速枯死，期间长不出东西）
        _corrupted = CorruptionZone.IsPositionCorrupted(CellToWorld(gridWidth / 2));
        if (_focusInput > 0.01f && !_corrupted)
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
            SyncLeafVisuals();
            _dirty = false;
        }
    }

    private float TargetHeight => _energy * gridHeight;

    private void Tick()
    {
        float targetH = TargetHeight;
        bool hasInput = _focusInput >= 0.05f && !_corrupted; // 腐化区内不生长

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

                // 叶子：侧向长出一整片叶斑（Prototype B：可采摘 + 可当跳台）
                if (_rng.NextDouble() < LeafBlobChance)
                {
                    int side = _rng.Next(2) == 0 ? -1 : 1;
                    StampLeafBlob(t.x + side, t.y, side);
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
                    if (_cells[idx] <= 0f)
                    {
                        _cells[idx] = 0f;
                        if (_leafCells.Contains(idx))
                            KillBlobContaining(idx); // 叶斑任何一格凋亡 → 整片叶子一起落下
                        else if (_leafCells.Remove(idx))
                            onLeafVanished?.Invoke(this, idx);
                    }
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
        int idx = y * gridWidth + x;
        // 枝干长穿叶片：该格交还给枝干（否则叶片贴图模式下会在这格留个透明洞）
        if (alpha >= 0.999f && _leafCells.Contains(idx)) DetachLeafCell(idx);
        _cells[idx] = alpha;
        _dirty = true;
    }

    /// <summary>把某一格从它所属的叶片里摘出来（枝干覆盖时用）；整片叶子被吃光就整片移除</summary>
    private void DetachLeafCell(int cellIndex)
    {
        _leafCells.Remove(cellIndex);
        foreach (var blob in _blobs.Values)
        {
            if (!blob.Cells.Remove(cellIndex)) continue;
            if (blob.Cells.Count == 0) { RemoveBlob(blob, false); }
            return; // 已修改字典/集合，立即返回
        }
    }

    // ———— 叶片外部接口（Prototype B）————

    /// <summary>该叶片（中心格索引）是否仍存活</summary>
    public bool IsLeafAlive(int cellIndex) => _blobs.ContainsKey(cellIndex);

    /// <summary>采摘一整片叶子：清空叶斑所有格，触发消失事件 + 飞散动画；不改 CA 规则</summary>
    public bool HarvestLeaf(int cellIndex)
    {
        if (!_blobs.TryGetValue(cellIndex, out LeafBlob blob)) return false;
        RemoveBlob(blob, true);
        return true;
    }

    /// <summary>在 (cx,cy) 附近长出一整片叶子：横向椭圆叶斑 + 独立叶形贴图，不覆盖枝干/其他叶片</summary>
    private void StampLeafBlob(int cx, int cy, int side)
    {
        if (cx < 0 || cx >= gridWidth || cy < 0 || cy >= gridHeight) return;
        int anchor = cy * gridWidth + cx;
        if (_cells[anchor] > 0f || _leafCells.Contains(anchor)) return; // 中心格必须是空的

        int r = Mathf.Clamp(leafBlobRadius, 0, 3);
        int rx = Mathf.Clamp(r + 1, 1, 4);   // 横向铺开：单元格本身竖长，横向多几格才像叶片
        int ry = 1;

        var cells = new List<int>(24);
        for (int dy = -ry; dy <= ry; dy++)
        for (int dx = -rx; dx <= rx; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight) continue;
            float nx = dx / (rx + 0.5f);
            float ny = dy / (ry + 0.5f);
            float d2 = nx * nx + ny * ny;
            if (d2 > 1f) continue;                                     // 椭圆轮廓
            if (Mathf.Abs(dy) == 1 && Mathf.Abs(dx) > rx - 1) continue; // 削掉上下两行的四角（别又长成矩形）
            if (Mathf.Abs(dx) >= rx && _rng.NextDouble() < 0.3f) continue; // 只有叶尖偶尔缺一格，保证顶部能站人
            int idx = y * gridWidth + x;
            if (_cells[idx] > 0f || _leafCells.Contains(idx)) continue; // 不覆盖枝干/已有叶片
            cells.Add(idx);
        }
        if (cells.Count < 4) return;

        for (int i = 0; i < cells.Count; i++) _cells[cells[i]] = 0.85f;
        _dirty = true;

        var blob = new LeafBlob
        {
            Anchor = anchor, Cx = cx, Cy = cy, Rx = rx, Ry = ry, Side = side, Cells = cells
        };
        _blobs[anchor] = blob;
        foreach (int c in cells) _leafCells.Add(c);

        if (useLeafSprites) CreateLeafVisual(blob);
        onLeafAppeared?.Invoke(this, anchor);
    }

    /// <summary>移除整片叶子（采摘或凋亡）：清格、播动画、发事件</summary>
    private void RemoveBlob(LeafBlob blob, bool harvested)
    {
        if (!_blobs.Remove(blob.Anchor)) return;
        for (int i = 0; i < blob.Cells.Count; i++)
        {
            int idx = blob.Cells[i];
            _leafCells.Remove(idx);
            if (_cells[idx] <= 0.9f) _cells[idx] = 0f; // 不误删已经长穿叶片的枝干格
        }
        _dirty = true;
        PlayLeafFx(blob, harvested);
        onLeafVanished?.Invoke(this, blob.Anchor);
    }

    /// <summary>按格子找它所属的叶片并整片移除（凋亡路径用）</summary>
    private void KillBlobContaining(int cellIndex)
    {
        foreach (var blob in _blobs.Values)
        {
            if (!blob.Cells.Contains(cellIndex)) continue;
            RemoveBlob(blob, false);
            return; // 已修改字典，立即返回
        }
    }

    // ———— 叶片贴图与动画 ————

    private void CreateLeafVisual(LeafBlob blob)
    {
        var go = new GameObject("Leaf", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        go.layer = gameObject.layer;
        var img = go.GetComponent<Image>();
        blob.Char = RandomGlyphChar();
        blob.Tilt = Random.Range(-12f, 12f);
        blob.Tint = Random.Range(0.82f, 1.05f); // 每片叶子明暗略有不同
        img.sprite = GlyphSprite(blob.Char);
        img.color = leafColor;
        img.raycastTarget = false;
        img.preserveAspect = false;
        blob.Visual = img;
        blob.Rt = (RectTransform)go.transform;
        LayoutLeafVisual(blob);
        go.transform.SetAsLastSibling(); // 叶片压在枝干之上
    }

    /// <summary>叶片字符的位置/尺寸：字符顶边对齐"站立支撑面"，恐龙不会看起来浮空</summary>
    private void LayoutLeafVisual(LeafBlob blob)
    {
        if (blob.Rt == null) return;
        Rect r = ((RectTransform)transform).rect;
        float cw = r.width / gridWidth;
        float ch = r.height / gridHeight;
        float wpx = Mathf.Min((2 * blob.Rx + 1) * cw * 1.05f, r.width * 0.95f);
        float hpx = wpx * 1.4f; // 5×7 字形的高宽比
        float topEdge = r.yMin + (blob.Cy + blob.Ry + 1.5f) * ch; // LeafSupportWorldY 的支撑面
        float localX = r.xMin + (blob.Cx + 0.5f) * cw;
        float localY = topEdge - hpx * 0.5f;
        blob.Rt.anchorMin = blob.Rt.anchorMax = Vector2.zero;
        blob.Rt.pivot = new Vector2(0.5f, 0.5f);
        blob.Rt.sizeDelta = new Vector2(wpx, hpx);
        // anchor=(0,0) 时 localPosition = rect.min + anchoredPosition，这样与父级 pivot 无关
        blob.Rt.anchoredPosition = new Vector2(localX - r.xMin, localY - r.yMin);
        blob.Rt.localRotation = Quaternion.Euler(0f, 0f, blob.Tilt);
    }

    private void SyncLeafVisuals()
    {
        foreach (var blob in _blobs.Values)
        {
            if (blob.Visual == null) continue;
            float a = 0f;
            for (int i = 0; i < blob.Cells.Count; i++)
                a = Mathf.Max(a, _cells[blob.Cells[i]]);
            var c = leafColor;
            c.r *= blob.Tint; c.g *= blob.Tint; c.b *= blob.Tint;
            c.a *= Mathf.Clamp01(a);
            blob.Visual.color = c;
        }
    }

    /// <summary>画布尺寸变化（布局重排）时重新摆叶片，避免叶片和植物错位</summary>
    private void CheckRectResize()
    {
        Rect r = ((RectTransform)transform).rect;
        if (Mathf.Abs(r.width - _lastRectSize.x) < 0.05f &&
            Mathf.Abs(r.height - _lastRectSize.y) < 0.05f) return;
        _lastRectSize = new Vector2(r.width, r.height);
        foreach (var blob in _blobs.Values) LayoutLeafVisual(blob);
    }

    private void PlayLeafFx(LeafBlob blob, bool harvested)
    {
        if (blob.Visual == null || blob.Rt == null) return;
        var fx = new LeafFx
        {
            Img = blob.Visual,
            Rt = blob.Rt,
            Pos = blob.Rt.anchoredPosition,
            Rot = 0f,
            Life = 0f,
            MaxLife = harvested ? 0.5f : 0.25f,
            Vel = harvested
                ? new Vector2(blob.Side * 140f, Random.Range(120f, 190f))  // 采摘：向外上方飞散
                : new Vector2(Random.Range(-30f, 30f), Random.Range(30f, 70f)) // 凋亡：轻轻飘落
        };
        fx.Spin = harvested ? -blob.Side * Random.Range(220f, 420f) : Random.Range(-60f, 60f);
        fx.SignX = 1f; // 字符不做镜像（镜像的字符看着别扭）
        _leafFx.Add(fx);
        blob.Visual = null;
        blob.Rt = null;
    }

    private void UpdateLeafFx(float dt)
    {
        for (int i = _leafFx.Count - 1; i >= 0; i--)
        {
            var fx = _leafFx[i];
            fx.Life += dt;
            if (fx.Rt == null)
            {
                _leafFx.RemoveAt(i);
                continue;
            }
            if (fx.Life >= fx.MaxLife)
            {
                Destroy(fx.Rt.gameObject);
                _leafFx.RemoveAt(i);
                continue;
            }
            fx.Pos += fx.Vel * dt;
            fx.Vel.y -= 260f * dt;
            fx.Vel.x *= Mathf.Max(0f, 1f - 1.5f * dt);
            fx.Rot += fx.Spin * dt;
            float t = fx.Life / fx.MaxLife;
            fx.Rt.anchoredPosition = fx.Pos;
            fx.Rt.localRotation = Quaternion.Euler(0f, 0f, fx.Rot);
            float s = Mathf.Lerp(1f, 0.55f, t);
            fx.Rt.localScale = new Vector3(fx.SignX * s, s, 1f);
            var c = fx.Img.color;
            c.a = Mathf.Clamp01(1f - t * t);
            fx.Img.color = c;
            _leafFx[i] = fx;
        }
    }

    private Canvas _uiCanvas;

    /// <summary>UI 相机：Overlay 传 null；ScreenSpace-Camera/WorldSpace 传画布相机（兼容两种 RenderMode）</summary>
    private Camera UiCamera
    {
        get
        {
            var canvas = _uiCanvas ?? (_uiCanvas = GetComponentInParent<Canvas>());
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
            if (canvas.worldCamera != null) return canvas.worldCamera;
            return Camera.main; // 画布没指相机的兜底
        }
    }

    /// <summary>把格子中心换算成世界坐标（z=0 平面），供恐龙寻位。
    /// 兼容 Overlay 与 ScreenSpace-Camera 两种画布模式：局部→画布世界→屏幕→主相机 z=0 平面。</summary>
    public Vector3 CellToWorld(int cellIndex)
    {
        int cx = cellIndex % gridWidth, cy = cellIndex / gridWidth;
        RectTransform rt = (RectTransform)transform;
        Rect r = rt.rect;
        Vector2 local = new Vector2(
            r.xMin + (cx + 0.5f) * r.width / gridWidth,
            r.yMin + (cy + 0.5f) * r.height / gridHeight);
        Vector3 canvasWorld = rt.TransformPoint(local);
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(UiCamera, canvasWorld);
        Camera cam = Camera.main;
        if (cam == null) return canvasWorld;
        // 主相机看向 +Z：z 参数 = 相机到目标平面的距离
        return cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
    }

    /// <summary>世界点 → 本植物网格坐标（不在本植物矩形内返回 false）</summary>
    public bool WorldToCell(Vector3 world, out int cx, out int cy)
    {
        cx = -1; cy = -1;
        Camera cam = Camera.main;
        if (cam == null) return false;
        RectTransform rt = (RectTransform)transform;
        Vector2 screen = cam.WorldToScreenPoint(world);
        if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(rt, screen, UiCamera, out Vector3 planePoint))
            return false;
        Vector2 local = rt.InverseTransformPoint(planePoint);
        Rect r = rt.rect;
        if (!r.Contains(local)) return false;
        cx = Mathf.FloorToInt((local.x - r.xMin) / r.width * gridWidth);
        cy = Mathf.FloorToInt((local.y - r.yMin) / r.height * gridHeight);
        return cx >= 0 && cx < gridWidth && cy >= 0 && cy < gridHeight;
    }

    /// <summary>恐龙踩叶片平台：给脚底探测点，返回脚下最近叶斑的表面世界高度；附近没有叶片返回 null。
    /// 半穿透平台语义：由恐龙的落地判定保证"只有从上方落下才能站上去"。</summary>
    public float? LeafSupportWorldY(Vector3 worldPos)
    {
        if (!WorldToCell(worldPos, out int cx, out int cy)) return null;
        int bestY = -1, bestX = -1;
        for (int dx = -1; dx <= 1; dx++)          // 恐龙身体可能横跨 2~3 格
        {
            int x = cx + dx;
            if (x < 0 || x >= gridWidth) continue;
            for (int dy = -1; dy <= 2; dy++)      // 脚下 1 格到头上 2 格范围
            {
                int y = cy + dy;
                if (y < 0 || y >= gridHeight) continue;
                int idx = y * gridWidth + x;
                if (_leafCells.Contains(idx) && _cells[idx] > 0f)
                {
                    if (y > bestY) { bestY = y; bestX = x; }
                    break;
                }
            }
        }
        if (bestY < 0) return null;
        int above = Mathf.Min(bestY + 1, gridHeight - 1); // 叶片上一格的中心 = 表面高度
        return CellToWorld(above * gridWidth + bestX).y;
    }

    private void FlushTexture()
    {
        byte r = (byte)(stemColor.r * 255);
        byte g = (byte)(stemColor.g * 255);
        byte b = (byte)(stemColor.b * 255);
        byte lr = (byte)(leafColor.r * 255);
        byte lg = (byte)(leafColor.g * 255);
        byte lb = (byte)(leafColor.b * 255);
        for (int i = 0; i < _cells.Length; i++)
        {
            float a = _cells[i];
            if (a <= 0f) { _pixels[i] = new Color32(0, 0, 0, 0); continue; }
            // 叶片由独立贴图绘制（有叶形），CA 网格里只留透明；关闭贴图时退回像素块着色
            if (useLeafSprites && _leafCells.Contains(i))
            { _pixels[i] = new Color32(0, 0, 0, 0); continue; }
            // 登记在册且未被枝干覆盖（alpha<1）的格子按叶片颜色渲染
            if (_leafCells.Contains(i) && a < 0.999f)
                _pixels[i] = new Color32(lr, lg, lb, (byte)(Mathf.Clamp01(a) * 255));
            else
                _pixels[i] = new Color32(r, g, b, (byte)(Mathf.Clamp01(a) * 255));
        }
        _tex.SetPixels32(_pixels);
        _tex.Apply(false);
    }
}
