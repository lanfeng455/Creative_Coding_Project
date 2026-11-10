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

    [Header("指尖删除（单手指尖悬停在打字机区域）")]
    public bool fingertipDeleteEnabled = true;
    /// <summary>删过一次后必须先让指尖离开文本区域，才允许删下一个（否则手停着会连删）</summary>
    private bool _deleteArmed = true;
    private RightHandIndexRayProvider _ray;

    private readonly List<char> _buffer = new();
    private readonly List<TextMeshPro> _charMeshes = new();
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
        if (_buffer.Count > maxBuffer)
            RemoveLetterAt(0);

        RefreshVisual(addIndex: _buffer.Count - 1);

        if (requestSource != null)
        {
            CheckRequest(bufferChanged: true);
            return;
        }

        // 词库匹配（末尾后缀命中）
        string text = BufferText;
        foreach (var w in words)
        {
            if (text.EndsWith(w, System.StringComparison.Ordinal))
            {
                ConsumeWord(w);
                break;
            }
        }
    }

    private void CheckRequest(bool bufferChanged = false)
    {
        string word = requestSource.RequestedWord;
        if (!bufferChanged && word == _lastCheckedRequest) return;
        _lastCheckedRequest = word;
        if (_world == null || string.IsNullOrEmpty(word) || _buffer.Count < word.Length ||
            System.Array.IndexOf(words, word) < 0)
            return;

        // Reserve a different buffer entry for every letter, including repeated letters.
        var matchedIndices = new List<int>(word.Length);
        foreach (char letter in word)
        {
            int index = _buffer.IndexOf(letter);
            while (index >= 0 && matchedIndices.Contains(index))
                index = _buffer.IndexOf(letter, index + 1);
            if (index < 0) return;
            matchedIndices.Add(index);
        }

        matchedIndices.Sort();
        for (int i = matchedIndices.Count - 1; i >= 0; i--)
            RemoveLetterAt(matchedIndices[i]);
        SpawnItem(word);
        RefreshVisual();
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
        if (index >= _charMeshes.Count) return;
        if (_charMeshes[index] != null) Destroy(_charMeshes[index].gameObject);
        _charMeshes.RemoveAt(index);
    }

    public void Clear()
    {
        _buffer.Clear();
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

    private void RefreshVisual(int addIndex = -1)
    {
        // display 只作为锚点/样式母版，逐字母由 _charMeshes 渲染（避免同一串文字叠两层）
        display.text = "";

        while (_charMeshes.Count < _buffer.Count)
        {
            var tm = B1Text.Make("TW_Char", transform, charFontSize, display.color, 2);
            _charMeshes.Add(tm);
        }
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
            lp.x = charSpacing + i * charSpacing; lp.y = -0.7f; // 留出提示符 ">" 的位置
            _charMeshes[i].transform.localPosition = lp;
        }
        // 光标跟在最后一个字母后面
        if (_cursor != null)
        {
            var cp = _cursor.transform.localPosition;
            cp.x = charSpacing + _charMeshes.Count * charSpacing; cp.y = -0.7f;
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

    /// <summary>指尖落到可见文本行上 → 删除最后一个字母。
    /// 判定框严格贴合画出来的文字（左沿提示符、右沿光标）。
    /// 删一次后必须把指尖移出文本区域才允许删下一个 —— 手停着不会连删一串。</summary>
    private void UpdateFingertipDelete()
    {
        if (!fingertipDeleteEnabled || _ray == null) return;

        var cam = Camera.main;
        if (cam == null) return;

        // 用"实际渲染出来的字符包围盒"做判定（TemplateB 的擦除也是按字母自身位置算，
        // 不是写死一个大框）。这样判定范围永远等于玩家看到的字，不会偏左/偏右。
        if (!TryGetTextBounds(out Bounds box))
        {
            _deleteArmed = true;   // 没字时不锁
            return;
        }
        box.Expand(0.18f);         // 一点点容差，指尖贴着字边也能触发

        float zDist = -cam.transform.position.z;
        bool inside = false;
        foreach (var tip in CandidateTips())
        {
            if (tip == Vector2.zero) continue;
            Vector3 w = cam.ScreenToWorldPoint(new Vector3(tip.x, tip.y, zDist));
            if (w.x >= box.min.x && w.x <= box.max.x &&
                w.y >= box.min.y && w.y <= box.max.y) { inside = true; break; }
        }

        if (!inside) { _deleteArmed = true; return; }  // 指尖移开 → 重新上膛
        if (!_deleteArmed) return;

        // 删除反馈：在被删字母处炸一小团红色碎屑 + 音效
        Vector3 at = _charMeshes.Count > 0
            ? _charMeshes[_charMeshes.Count - 1].transform.position
            : new Vector3(box.center.x, box.center.y, 0f);
        B1Burst.Shatter(at, new Color(1f, 0.5f, 0.5f), 5, 4f);
        AudioManager.PlayAt(Sfx.LeafHarvest, at);
        Undo();
        _deleteArmed = false;
    }

    /// <summary>提示符 + 已录入字母 + 光标 的联合渲染包围盒（世界坐标）</summary>
    private bool TryGetTextBounds(out Bounds box)
    {
        Bounds acc = default;
        bool any = false;

        void Encapsulate(TextMeshPro tm)
        {
            if (tm == null || !tm.gameObject.activeInHierarchy) return;
            var r = tm.GetComponent<Renderer>();
            if (r == null) return;
            if (!any) { acc = r.bounds; any = true; }
            else acc.Encapsulate(r.bounds);
        }

        Encapsulate(_prompt);
        foreach (var m in _charMeshes) Encapsulate(m);
        Encapsulate(_cursor);

        box = acc;
        // 首帧 TMP 网格还没生成时 bounds 是零尺寸，此时不要判定（否则会在原点误删）
        return any && box.size.x > 0.05f && box.size.y > 0.05f;
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
