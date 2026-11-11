// TypeWriterB1.cs —— B1 打字机
// 字符被弹过上层缺口 → Add(char) 追加到缓冲区显示。
// 匹配词库或恐龙当前需求 → 消费字母、生成物品。
// 键盘 Backspace 删最后一个、Esc 清空（后期可换成手势操作）。

using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class TypeWriterB1 : MonoBehaviour
{
    public static TypeWriterB1 Instance { get; private set; }

    [Header("显示（世界空间 TMP，编辑器里可调字体/描边/发光）")]
    public TextMeshPro display;
    [Tooltip("显示位置（世界坐标，物品从这里上方生成）")]
    public Vector3 spawnAnchor = new Vector3(-6f, -2.8f, 0f);
    [Tooltip("缓冲区最大长度，超出自动清最旧")]
    public int maxBuffer = 12;

    [Header("词库（可扩展：单词 → 物品）")]
    public string[] words = { "BALL", "CHAIR", "WATER", "WOOD", "STONE" };
    [SerializeField, Tooltip("指定恐龙后，只匹配它当前喊出的词，字母顺序不限")]
    private DinoAgentB1 requestSource;

    [Header("显示")]
    [Tooltip("字母间距（世界单位）。调大更宽松")]
    public float charSpacing = 0.72f;
    [Tooltip("字母字号")]
    public float charFontSize = 4.5f;

    [Header("手感校正")]
    [Tooltip("删除判定区整体水平偏移（世界单位，正值=右移）。偏右就调负、偏左就调正")]
    public float deleteZoneOffsetX = 0f;

    [Header("指尖删除（单手指尖悬停在打字机区域）")]
    public bool fingertipDeleteEnabled = true;
    [Tooltip("指尖要在同一个字母格上停留多久才删（秒）。调大更稳，防手抖连删")]
    public float deleteDwell = 0.16f;
    [Tooltip("删一次后，指尖要离开文本区多久才解锁下一次删除（秒）")]
    public float reArmDelay = 0.35f;
    [Tooltip("判定\"离开\"时判据外扩的横向余量（世界单位）：手抖刚出框不算离开")]
    public float leaveMarginX = 0.12f;
    [Tooltip("判定\"离开\"时判据外扩的纵向余量（世界单位）")]
    public float leaveMarginY = 0.20f;
    /// <summary>删过一次后必须先让指尖离开文本区域，才允许删下一个（否则手停着会连删）</summary>
    private bool _deleteArmed = true;
    private float _insideTime;              // 指尖在当前格上停留的时长
    private int _dwellSlot = int.MinValue;  // 当前停留的槽位
    private float _outsideTime;             // 指尖在外扩框之外的持续时长
    private RightHandIndexRayProvider _ray;

    private readonly List<char> _buffer = new();
    private readonly List<TextMeshPro> _charMeshes = new();
    /// <summary>每个字母占用的槽位（删字后槽位留空，等指尖离开文本行才整体缩进）</summary>
    private readonly List<int> _slots = new();
    private int _nextSlot;   // 下一个新字母使用的槽位
    private B1LayerConfig _world;
    private TextMeshPro _cursor;
    private TextMeshPro _prompt;
    private string _lastCheckedRequest;

    public string BufferText => new string(_buffer.ToArray());

    /// <summary>所有玩法必须能拼出的词（场景里旧组件序列化的 words 可能缺项，这里补齐）</summary>
    private static readonly string[] RequiredWords = { "BALL", "CHAIR", "WATER", "WOOD", "STONE" };

    /// <summary>把 RequiredWords 合并进 words（保持原有顺序，只补缺）</summary>
    private void MergeRequiredWords()
    {
        var list = new List<string>(words ?? new string[0]);
        foreach (var w in RequiredWords)
            if (!list.Contains(w)) list.Add(w);
        words = list.ToArray();
    }

    private void Awake()
    {
        Instance = this;
        MergeRequiredWords();
        if (display == null)
        {
            display = B1Text.Make("TW_Display", transform, 5f, new Color(0.55f, 1f, 0.78f, 1f), 2);
            display.alignment = TextAlignmentOptions.Left;
        }
        // 提示符 ">" 与闪烁光标，保证打字机在缓冲区为空时也可见
        _prompt = B1Text.Make("TW_Prompt", transform, 5f, new Color(0.45f, 0.9f, 0.65f, 0.9f), 2);
        _prompt.text = ">";
        _prompt.transform.localPosition = new Vector3(0f, -0.7f, 0f);
        _cursor = B1Text.Make("TW_Cursor", transform, 5f, new Color(0.7f, 1f, 0.85f, 1f), 2);
        _cursor.text = "▌";
        display.text = "";
    }

    private void Start()
    {
        _world = FindObjectOfType<B1LayerConfig>();
        _ray = FindObjectOfType<RightHandIndexRayProvider>();
        if (spawnAnchor == Vector3.zero && _world != null)
            spawnAnchor = new Vector3(_world.boundsXMin + 2f, _world.lowerGroundY + 1.2f, 0f);
        RefreshVisual(); // 初始空状态也把光标定位到正确位置（否则停在中心上方）
    }

    /// <summary>弹上来的字符录入</summary>
    public void Add(char c)
    {
        c = char.ToUpperInvariant(c);
        if (!char.IsLetter(c)) return;

        _buffer.Add(c);
        _slots.Add(_nextSlot++);
        if (_buffer.Count > maxBuffer)
            RemoveLetterAt(0);

        // 没有"删出来的空缺"时按常规紧凑排版；有空缺就保持原样，等指尖移开再缩进
        RefreshVisual(addIndex: _buffer.Count - 1, compact: _nextSlot == _buffer.Count);
        AudioManager.PlayAt(Sfx.LetterRecord, transform.position);

        // 词库匹配：缓冲区末尾必须按顺序等于该词。
        // 不看恐龙此刻在喊什么 —— 火灾期间它只喊 WATER，但 BALL/WOOD/STONE
        // 照样可以合法拼出来堆到上层（火灭后恐龙照样会去用）。
        foreach (var w in words)
        {
            if (BufferEndsWith(w))
            {
                ConsumeWord(w);
                break;
            }
        }

        // 恐龙当前需求单独确认一次（需求可能在字打出来之后才出现，例如火刚烧起来）
        if (requestSource != null) CheckRequest(bufferChanged: true);
    }

    /// <summary>缓冲区末尾 N 个字母是否按顺序等于 word（前面允许多余字母，但要按顺序接在末尾）</summary>
    private bool BufferEndsWith(string word)
    {
        if (string.IsNullOrEmpty(word) || _buffer.Count < word.Length) return false;
        int start = _buffer.Count - word.Length;
        for (int i = 0; i < word.Length; i++)
            if (_buffer[start + i] != word[i]) return false;
        return true;
    }

    private void CheckRequest(bool bufferChanged = false)
    {
        string word = requestSource.RequestedWord;
        if (!bufferChanged && word == _lastCheckedRequest) return;
        _lastCheckedRequest = word;
        if (_world == null || string.IsNullOrEmpty(word) ||
            System.Array.IndexOf(words, word) < 0)
            return;

        // 和词库同一条规则：末尾必须按顺序拼出该词，不认"散着凑齐字母"
        if (!BufferEndsWith(word)) return;

        ConsumeWord(word);
    }

    public void Undo()
    {
        if (_buffer.Count == 0) return;
        RemoveLetterAt(_buffer.Count - 1);
        RefreshVisual();
    }

    private void RemoveLetterAt(int index)
    {
        _buffer.RemoveAt(index);
        if (index < _slots.Count) _slots.RemoveAt(index);
        if (index >= _charMeshes.Count) return;
        if (_charMeshes[index] != null) Destroy(_charMeshes[index].gameObject);
        _charMeshes.RemoveAt(index);
    }

    public void Clear()
    {
        _buffer.Clear();
        _slots.Clear();
        _nextSlot = 0;
        foreach (var m in _charMeshes) if (m != null) Destroy(m.gameObject);
        _charMeshes.Clear();
        RefreshVisual();
    }

    /// <summary>命中单词：吃掉对应字母 + 生成物品</summary>
    private void ConsumeWord(string w)
    {
        // 移除缓冲区末尾 len 个字母
        for (int i = 0; i < w.Length && _buffer.Count > 0; i++)
            RemoveLetterAt(_buffer.Count - 1);
        SpawnItem(w);
        RefreshVisual();
    }

    /// <summary>单词 → 物品（下层生成，玩家再弹上去；这正是核心循环）</summary>
    private void SpawnItem(string word)
    {
        if (_world == null) return;
        if (word == "WATER" && _world.TryHandleCraftedWater()) return;
        var pos = spawnAnchor + new Vector3(Random.Range(-0.3f, 0.3f), 2.5f, 0f);
        Sprite logSprite = word == "WOOD" && requestSource != null ? requestSource.ConstructionLogSprite : null;
        if (logSprite != null)
        {
            float dropX = Mathf.Clamp(-3.2f + (pos.x - spawnAnchor.x),
                _world.upperFloorXMin + 1f, _world.GapXMin - 1f);
            pos = new Vector3(dropX, _world.upperCeilingY + 0.65f);
        }

        // 先加 SpriteRenderer（B1Item.Awake 里 GetComponent 要拿到它），再加物品组件（Awake 立即执行）
        var go = new GameObject("B1_" + word);
        go.AddComponent<SpriteRenderer>();
        go.transform.position = pos;
        if (word == "WOOD") AudioManager.PlayAt(Sfx.WoodDeliver, pos);

        switch (word)
        {
            case "BALL": go.AddComponent<B1Ball>(); break;
            case "CHAIR": go.AddComponent<B1Chair>(); break;
            case "WATER": go.AddComponent<B1Water>(); break;
            case "WOOD":
                var wood = go.AddComponent<B1Resource>();
                wood.kind = B1Resource.Kind.Wood;
                if (logSprite != null) wood.ConfigureConstructionLog(logSprite, _world);
                break;
            case "STONE": go.AddComponent<B1Resource>().kind = B1Resource.Kind.Stone; break;
            default: Destroy(go); return;
        }

        AudioManager.PlayAt(Sfx.NestComplete, pos);
        B1Burst.Shatter(pos, new Color(1f, 0.9f, 0.4f), 8, 5f);
    }

    // —— 显示：主文本行 + 逐字母动画（世界空间 TMP） ——

    /// <summary>重排字母。compact=true 时把字母压到 0..n-1 槽位（缺口缩进）；
    /// compact=false 时各字母保留原槽位（删除后留空，等指尖移开再缩进）。</summary>
    private void RefreshVisual(int addIndex = -1, bool compact = true)
    {
        // display 只作为锚点/样式母版，逐字母由 _charMeshes 渲染（避免同一串文字叠两层）
        display.text = "";

        if (compact)
        {
            _slots.Clear();
            for (int i = 0; i < _buffer.Count; i++) _slots.Add(i);
            _nextSlot = _buffer.Count;
        }
        while (_slots.Count < _buffer.Count) _slots.Add(_nextSlot++);
        while (_slots.Count > _buffer.Count) _slots.RemoveAt(_slots.Count - 1);

        while (_charMeshes.Count < _buffer.Count)
            _charMeshes.Add(B1Text.Make("TW_Char", transform, charFontSize, display.color, 2));
        while (_charMeshes.Count > _buffer.Count)
        {
            Destroy(_charMeshes[_charMeshes.Count - 1].gameObject);
            _charMeshes.RemoveAt(_charMeshes.Count - 1);
        }

        for (int i = 0; i < _charMeshes.Count; i++)
        {
            if (_charMeshes[i] == null) continue;
            _charMeshes[i].text = _buffer[i].ToString();
            var lp = _charMeshes[i].transform.localPosition;
            lp.x = charSpacing * (_slots[i] + 1); lp.y = -0.7f; // 留出提示符 ">" 的位置
            _charMeshes[i].transform.localPosition = lp;
        }
        // 光标跟在最后一个槽位后面
        if (_cursor != null)
        {
            var cp = _cursor.transform.localPosition;
            cp.x = charSpacing * (_nextSlot + 1); cp.y = -0.7f;
            _cursor.transform.localPosition = cp;
        }
        if (addIndex >= 0 && addIndex < _charMeshes.Count && _charMeshes[addIndex] != null)
            _charMeshes[addIndex].gameObject.AddComponent<TWPop>();
    }

    private float _cursorBlink;
    private void UpdateCursorBlink()
    {
        _cursorBlink += Time.deltaTime;
        if (_cursor != null) _cursor.gameObject.SetActive(Mathf.FloorToInt(_cursorBlink * 2f) % 2 == 0);
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Backspace)) Undo();
        if (Input.GetKeyDown(KeyCode.Escape)) Clear();
        if (requestSource != null) CheckRequest();
        UpdateCursorBlink();
        UpdateFingertipDelete();
    }

    /// <summary>指尖落到哪个字母的格子上就删哪个（不再固定删末尾）。
    /// 删除后该槽位留空、其余字母不移动；等指尖彻底离开文本区后，空缺才整体缩进补上。
    /// 三重防误删：① 停留 deleteDwell 秒才删 ② 删一次后要离开外扩框 reArmDelay 秒才解锁
    /// ③ 外扩框做滞回，手抖刚出框不算离开 —— 避免"缩进 → 字跑到指尖下 → 又删一个"的连锁清空。</summary>
    private void UpdateFingertipDelete()
    {
        if (!fingertipDeleteEnabled || _ray == null) return;

        var cam = Camera.main;
        if (cam == null) return;

        if (!TryGetTextBounds(out Bounds box))
        {
            _deleteArmed = true;
            _insideTime = 0f; _dwellSlot = int.MinValue; _outsideTime = 0f;
            return;
        }

        // 外扩框只用于判定"离开"；内框才是真正能删字的范围
        Bounds outer = box;
        outer.Expand(new Vector3(leaveMarginX * 2f, leaveMarginY * 2f, 0f));

        float zDist = -cam.transform.position.z;
        bool inOuter = false;
        int tipCount = 0;
        int slot = int.MinValue;
        foreach (var tip in CandidateTips())
        {
            if (tip == Vector2.zero) continue;
            tipCount++;
            Vector3 w = cam.ScreenToWorldPoint(new Vector3(tip.x, tip.y, zDist));
            if (w.x < outer.min.x || w.x > outer.max.x ||
                w.y < outer.min.y || w.y > outer.max.y) continue;

            inOuter = true;
            if (w.x >= box.min.x && w.x <= box.max.x &&
                w.y >= box.min.y && w.y <= box.max.y)
            {
                // 指尖落在哪个槽位：槽位 = round(局部x / 字距) - 1（每个字占一格）
                int s = Mathf.RoundToInt(transform.InverseTransformPoint(w).x / charSpacing) - 1;
                if (_slots.IndexOf(s) >= 0) slot = s;
            }
        }

        // 没检测到手（追踪掉帧）：既不算离开也不算命中，状态原样保持
        if (tipCount == 0) return;

        // —— 指尖在外扩框之外：累计离开时间，够久才解锁并缩进 ——
        if (!inOuter)
        {
            _outsideTime += Time.deltaTime;
            _insideTime = 0f;
            _dwellSlot = int.MinValue;
            if (_outsideTime >= reArmDelay)
            {
                _deleteArmed = true;
                if (_nextSlot != _buffer.Count) RefreshVisual();   // 空缺缩进补上
            }
            return;
        }
        _outsideTime = 0f;

        // —— 上膛中且稳定停在某个字母格上，才删 ——
        if (!_deleteArmed) return;
        if (slot != _dwellSlot) { _dwellSlot = slot; _insideTime = 0f; }
        if (slot == int.MinValue) return;      // 指在空白/光标上：不删，也不算删过

        _insideTime += Time.deltaTime;
        if (_insideTime < deleteDwell) return;

        int index = _slots.IndexOf(slot);
        if (index < 0) return;
        Vector3 at = index < _charMeshes.Count && _charMeshes[index] != null
            ? _charMeshes[index].transform.position
            : new Vector3(box.center.x, box.center.y, 0f);
        B1Burst.Shatter(at, new Color(1f, 0.5f, 0.5f), 5, 4f);
        AudioManager.PlayAt(Sfx.LetterDelete, at);
        RemoveLetterAt(index);         // 只删这一个字母，不重排
        _deleteArmed = false;          // 等指尖离开外扩框足够久才解锁
        _insideTime = 0f;
        _dwellSlot = int.MinValue;
    }

    /// <summary>按实际排版算出的文本行矩形（世界坐标）。
    /// 不用 TMP 的 Renderer.bounds —— 它含字体内边距/行高，比看到的字大一圈，
    /// 指尖还没碰到字就会触发。这里直接用排版参数：第一个字母 → 光标。</summary>
    private bool TryGetTextBounds(out Bounds box)
    {
        box = default;
        if (_prompt == null) return false;

        float halfH = charFontSize * 0.1f * 0.55f;   // 字高的一半（字号 10 ≈ 1 世界单位，约等于字形高度）
        float halfW = charSpacing * 0.35f;           // 单字横向半宽（约半个字距）
        // 左沿 = 第一个还占着的槽位；还没拼字时以 ">" 提示符为准（提示符本身不可删）。
        int minSlot = -1;
        if (_slots.Count > 0)
        {
            minSlot = _slots[0];
            foreach (int s in _slots) if (s < minSlot) minSlot = s;
        }
        float left = _slots.Count > 0
            ? charSpacing * (minSlot + 1) - halfW - 0.06f
            : -halfW - 0.06f;
        left += deleteZoneOffsetX;
        float right = charSpacing * (_nextSlot + 1) + halfW + 0.06f + deleteZoneOffsetX; // 光标右沿
        float y = -0.7f;

        Vector3 a = transform.TransformPoint(new Vector3(left, y - halfH, 0f));
        Vector3 b = transform.TransformPoint(new Vector3(right, y + halfH, 0f));
        box.SetMinMax(Vector3.Min(a, b), Vector3.Max(a, b));
        return box.size.x > 0.05f && box.size.y > 0.05f;
    }

    /// <summary>当前可用的指尖屏幕坐标（单手 / 双手 A、B 都算）</summary>
    private readonly List<Vector2> _tips = new();
    private List<Vector2> CandidateTips()
    {
        _tips.Clear();
        if (_ray.HasTwoTips) { _tips.Add(_ray.TipScreenA); _tips.Add(_ray.TipScreenB); }
        else if (_ray.HasOneTip) _tips.Add(_ray.TipScreenSingle);
        return _tips;
    }

    /// <summary>新字母的弹跳缩放动画</summary>
    private class TWPop : MonoBehaviour
    {
        private float _t;
        private void Update()
        {
            _t += Time.deltaTime * 6f;
            float s = 1f + Mathf.Sin(Mathf.Min(_t, Mathf.PI)) * 0.6f;
            transform.localScale = Vector3.one * s;
            if (_t >= Mathf.PI) { transform.localScale = Vector3.one; Destroy(this); }
        }
    }
}
