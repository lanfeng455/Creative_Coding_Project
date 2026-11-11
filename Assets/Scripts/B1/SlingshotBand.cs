// SlingshotBand.cs —— 双食指弹力带（PIP→指尖 折线）
// 几何：两只食指各自是一条"板面"（第二关节 PIP → 指尖 TIP），
//       整条弹力带 = PIP_A → TIP_A → TIP_B → PIP_B 的折线（单手时退化为单指 PIP→TIP）。
// 力度：记录折线中心连续位置 → 速度 + 加速度；快甩=强弹，轻移=弱弹，静止=掉上来的字符碎裂。
// 作用对象：PhysicsLetter（弹上上层→录入 / 静止带上碎裂）与 B1Item（下层物品弹回上层）。

using System.Collections.Generic;
using UnityEngine;

public class SlingshotBand : MonoBehaviour
{
    [Header("引用")]
    public RightHandIndexRayProvider rayProvider;
    public B1LayerConfig world;

    [Header("弹力带几何")]
    [Tooltip("带的半宽（世界单位，物体碰到即受力）")]
    public float bandHalfWidth = 0.09f;

    [Header("力度（核心手感参数）")]
    [Tooltip("必须向上甩才弹：带向上的速度低于此值时不弹射（避免手一碰就飞）")]
    public float minUpFlingSpeed = 1.0f;
    [Tooltip("向上甩速度达到此值 = 满力")]
    public float fullPowerUpSpeed = 6f;
    [Tooltip("最小弹射速度（轻甩）")]
    public float minFlingSpeed = 2.6f;
    [Tooltip("最大弹射速度（重甩），决定能不能顶到上层")]
    public float maxFlingSpeed = 8.5f;
    [Tooltip("加速度加成上限：手部追踪的抖动会被这个夹住，不会变成导弹")]
    public float accelBonusCap = 1.5f;
    [Tooltip("带速度低于此值视为静止：掉上来的字符直接穿过落地碎")]
    public float stillBandSpeed = 0.6f;

    [Header("平滑与采样")]
    [Range(0.05f, 1f)] public float smoothing = 0.3f;
    [Tooltip("速度采样回看窗口（秒）")]
    public float velWindow = 0.15f;

    [Header("视觉（TemplateB 屋檐风：细线 + 指尖矩形）")]
    public Color bandColor = new Color(1f, 1f, 1f, 0.55f);
    public Color bandHotColor = new Color(0.4f, 1f, 0.7f, 0.95f);
    [Tooltip("折线（手指段）宽度")]
    public float lineWidth = 0.045f;
    [Tooltip("指尖矩形边长（世界单位）")]
    public float tipDotSize = 0.26f;
    [Tooltip("关节矩形边长")]
    public float pipDotSize = 0.14f;
    public Color leftTipColor = new Color(0.4f, 0.9f, 1f, 1f);
    public Color rightTipColor = new Color(1f, 0.65f, 0.35f, 1f);
    public Color pipColor = new Color(1f, 1f, 1f, 0.8f);

    // —— 带状态（世界坐标） ——
    private readonly Vector2[] _pts = new Vector2[4];   // PIP_A, TIP_A, TIP_B, PIP_B
    private int _ptCount;                               // 4=双手，2=单手
    private bool _valid;
    private bool _tipsInLowerLayer = true;   // 指尖是否都在下层（上层=不弹）
    private Vector2 _midVel, _midAccel;
    private readonly Queue<(float t, Vector2 p)> _history = new();

    private LineRenderer _line;
    private readonly SpriteRenderer[] _dots = new SpriteRenderer[4];
    private readonly Dictionary<Object, float> _itemCd = new();

    private void Start()
    {
        if (rayProvider == null) rayProvider = FindObjectOfType<RightHandIndexRayProvider>();
        if (world == null) world = FindObjectOfType<B1LayerConfig>();
        // B1 不需要"指向射线"（TemplateB 里乱码雨模式同样会关掉它），只保留弹力带
        if (rayProvider != null) rayProvider.rayVisualEnabled = false;
        BuildVisuals();
    }

    private void BuildVisuals()
    {
        _line = GetComponent<LineRenderer>();
        if (_line == null) _line = gameObject.AddComponent<LineRenderer>();
        _line.positionCount = 4;
        _line.useWorldSpace = true;
        _line.widthMultiplier = lineWidth;   // 细线（手指段）
        _line.material = new Material(Shader.Find("Sprites/Default")) { color = bandColor };
        _line.sortingOrder = 5;

        string[] names = { "BandPipA", "BandTipA", "BandTipB", "BandPipB" };
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject(names[i]);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = B1Visuals.Square;    // 矩形点（指尖/关节）
            sr.sortingOrder = 5;
            go.transform.localScale = Vector3.one * tipDotSize;
            _dots[i] = sr;
        }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        UpdateBandState(dt);
        UpdateVisuals();

        if (!_valid) { _itemCd.Clear(); return; }

        // 有指尖跑到上层（分界线以上）→ 整条带不参与弹射。
        // 否则伸手去打字机删字时，手往上抬会被当成"甩腕"，把路过的字母打成导弹。
        if (!_tipsInLowerLayer) return;

        var keys = new List<Object>(_itemCd.Keys);
        foreach (var k in keys) { _itemCd[k] -= dt; if (_itemCd[k] <= 0f) _itemCd.Remove(k); }

        float bandSpeed = _midVel.magnitude;
        bool still = bandSpeed < stillBandSpeed;

        // —— 字符 ——
        for (int i = PhysicsLetter.All.Count - 1; i >= 0; i--)
        {
            var l = PhysicsLetter.All[i];
            if (l == null) continue;
            if (DistToBand(l.transform.position) > l.Radius + bandHalfWidth) continue;

            // 带在动 → 弹射；带静止 → 不施力，字母自然穿过去落地碎掉（避免悬停看不见）
            if (!still && !l.OnBandCooldown) Fling(l.transform.position, l.Launch); // 没向上甩就自然穿过
        }

        // —— 物品（球/椅/水）——
        foreach (var item in B1Item.All)
        {
            if (item == null || !item.Alive) continue;
            if (item is B1Resource { IsConstructionLog: true }) continue;
            if (item.IsResting && still) continue;
            if (DistToBand(item.transform.position) > 0.3f + bandHalfWidth) continue;

            if (!still && !_itemCd.ContainsKey(item))
            {
                var it = item;
                // 只有真的向上甩成功才进入冷却；否则物品继续下落/停留
                if (Fling(item.transform.position, v => it.Launch(v))) _itemCd[item] = 0.2f;
            }
            else if (still)
            {
                var n = BandNormal(item.transform.position);
                var v = item.Velocity;
                if (Vector2.Dot(v, n) < -0.5f) item.Launch(Vector2.Reflect(v, n) * 0.5f);
                _itemCd[item] = 0.15f;
            }
        }
    }

    // —— 带状态追踪 ——

    private void UpdateBandState(float dt)
    {
        var cam = Camera.main;
        if (rayProvider == null || cam == null) { _valid = false; }
        else if (rayProvider.HasTwoTips)
        {
            float z = -cam.transform.position.z;
            _pts[0] = cam.ScreenToWorldPoint(new Vector3(rayProvider.PipScreenA.x, rayProvider.PipScreenA.y, z));
            _pts[1] = cam.ScreenToWorldPoint(new Vector3(rayProvider.TipScreenA.x, rayProvider.TipScreenA.y, z));
            _pts[2] = cam.ScreenToWorldPoint(new Vector3(rayProvider.TipScreenB.x, rayProvider.TipScreenB.y, z));
            _pts[3] = cam.ScreenToWorldPoint(new Vector3(rayProvider.PipScreenB.x, rayProvider.PipScreenB.y, z));
            _ptCount = 4;
            _valid = true;
        }
        else if (rayProvider.HasOneTip)
        {
            float z = -cam.transform.position.z;
            _pts[0] = cam.ScreenToWorldPoint(new Vector3(rayProvider.PipScreenSingle.x, rayProvider.PipScreenSingle.y, z));
            _pts[1] = cam.ScreenToWorldPoint(new Vector3(rayProvider.TipScreenSingle.x, rayProvider.TipScreenSingle.y, z));
            _ptCount = 2;
            _valid = true;
        }
        else _valid = false;

        _line.enabled = _valid;
        for (int i = 0; i < 4; i++)
            if (_dots[i] != null) _dots[i].enabled = _valid && i < _ptCount;
        if (!_valid) { _history.Clear(); return; }

        // 指尖是否都在下层（四点是索引 1、2；单手是索引 1）
        _tipsInLowerLayer = true;
        if (world != null)
        {
            float div = world.upperFloorY;
            if (_ptCount >= 4) _tipsInLowerLayer = _pts[1].y < div && _pts[2].y < div;
            else if (_ptCount >= 2) _tipsInLowerLayer = _pts[1].y < div;
        }

        // —— 折线中心的历史 → 速度与加速度 ——
        float now = Time.time;
        Vector2 mid = BandCenter();
        _history.Enqueue((now, mid));
        while (_history.Count > 2 && now - _history.Peek().t > velWindow * 2f)
            _history.Dequeue();

        (float t0, Vector2 p0) oldest = _history.Peek();
        foreach (var s in _history)
            if (now - s.t <= velWindow) { oldest = s; break; }

        float span = now - oldest.t0;
        if (span > 0.016f)
        {
            Vector2 rawVel = (mid - oldest.p0) / span;
            Vector2 rawAcc = (rawVel - _midVel) / Mathf.Max(dt, 0.001f);
            _midVel = Vector2.Lerp(_midVel, rawVel, smoothing);
            _midAccel = Vector2.Lerp(_midAccel, rawAcc, smoothing * 0.5f);
        }
    }

    private Vector2 BandCenter()
    {
        Vector2 c = Vector2.zero;
        for (int i = 0; i < _ptCount; i++) c += _pts[i];
        return c / _ptCount;
    }

    // —— 碰撞与力度 ——

    /// <summary>点到整条折线的最短距离（逐段求 min）</summary>
    private float DistToBand(Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < _ptCount - 1; i++)
            best = Mathf.Min(best, DistToSegment(p, _pts[i], _pts[i + 1]));
        return best;
    }

    private static float DistToSegment(Vector3 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot((Vector2)p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>离物体最近的段的法线（统一朝上）</summary>
    private Vector2 BandNormal(Vector3 p)
    {
        int bestI = 0; float bestD = float.MaxValue;
        for (int i = 0; i < _ptCount - 1; i++)
        {
            float d = DistToSegment(p, _pts[i], _pts[i + 1]);
            if (d < bestD) { bestD = d; bestI = i; }
        }
        Vector2 ab = (_pts[bestI + 1] - _pts[bestI]).normalized;
        Vector2 n = new Vector2(-ab.y, ab.x);
        if (n.y < 0f) n = -n;
        return n;
    }

    /// <summary>按"向上甩的速度"算弹力：没往上甩 = 不弹（返回 false）。
    /// 力度只在 最小~最大 之间连续变化，加速度只作为小额加成且有上限，
    /// 所以手部追踪的抖动不会突然把字甩到天上。</summary>
    private bool Fling(Vector3 pos, System.Action<Vector2> apply)
    {
        float up = Vector2.Dot(_midVel, Vector2.up);
        if (up < minUpFlingSpeed) return false;

        float t = Mathf.Clamp01(up / Mathf.Max(0.01f, fullPowerUpSpeed));
        float speed = Mathf.Lerp(minFlingSpeed, maxFlingSpeed, t);
        float accelUp = Mathf.Clamp(Vector2.Dot(_midAccel, Vector2.up), 0f, 120f);
        speed += Mathf.Min(accelUp * 0.012f, accelBonusCap);
        speed = Mathf.Clamp(speed, 0f, maxFlingSpeed + accelBonusCap);

        // 方向以正上方为主，手的横向速度只给一点偏转
        Vector2 dir = new Vector2(_midVel.x * 0.35f, 1f).normalized;
        apply(dir * speed);
        AudioManager.PlayAt(Sfx.LetterFling, pos);
        return true;
    }

    private void UpdateVisuals()
    {
        if (!_valid) return;
        _line.positionCount = _ptCount;
        for (int i = 0; i < _ptCount; i++)
        {
            _line.SetPosition(i, new Vector3(_pts[i].x, _pts[i].y, 0f));
            if (_dots[i] != null)
            {
                _dots[i].transform.position = new Vector3(_pts[i].x, _pts[i].y, 0f);
                bool isTip = i == 1 || i == 2;
                _dots[i].transform.localScale = Vector3.one * (isTip ? tipDotSize : pipDotSize);
            }
        }
        // 细线：按带速做力度反馈渐变；矩形点：固定配色（左指尖青/右指尖橙/关节白，单手=青）
        float power = Mathf.Clamp01(_midVel.magnitude / maxFlingSpeed);
        _line.startColor = _line.endColor = Color.Lerp(bandColor, bandHotColor, power);
        if (_ptCount == 2)
        {
            if (_dots[0] != null) _dots[0].color = pipColor;
            if (_dots[1] != null) _dots[1].color = leftTipColor;
        }
        else if (_ptCount == 4)
        {
            bool aIsLeft = _pts[1].x <= _pts[2].x;
            if (_dots[0] != null) _dots[0].color = pipColor;
            if (_dots[3] != null) _dots[3].color = pipColor;
            if (_dots[1] != null) _dots[1].color = aIsLeft ? leftTipColor : rightTipColor;
            if (_dots[2] != null) _dots[2].color = aIsLeft ? rightTipColor : leftTipColor;
        }
    }
}
