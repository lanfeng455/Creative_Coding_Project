// BallFormController.cs —— Prototype C：球的「表现层」适配器（双形态 + 上下层切换）
// 物理不再自研：直接复用 B1 的 B1Ball / B1Item（重力、落地反弹、边界、缺口跨层、恐龙会来踢）。
// 本组件只做三件事：
//   1. 给手动拖进场景的 3D 预制体球套上同一套 toon 材质（前景档）；
//   2. 上层显示实体、下层显示同一个球的多行字符画，按 Y 阈值交叉过渡（不改物理状态）；
//   3. 恐龙靠近 + 起跳 + 下落时，让 B1Ball 短暂无视上层地板，把球压到下层。
// 物理半径按渲染包围盒自动推导，避免"半个球埋进地板"看起来像被压扁。

using System.Collections.Generic;
using UnityEngine;
using TMPro;
using PrototypeC;

[RequireComponent(typeof(B1Ball))]
[DisallowMultipleComponent]
public class BallFormController : MonoBehaviour
{
    /// <summary>场上所有双形态球（篮筐投入判定、辅助弹射遍历用）。</summary>
    public static readonly List<BallFormController> All = new List<BallFormController>();

    [Header("引用")]
    [Tooltip("双层世界配置；留空会自动找场景里的 B1LayerConfig")]
    public B1LayerConfig world;
    [Tooltip("负责物理与恐龙互动的球组件（同物体上，留空自动取；没有就自动添加）")]
    public B1Ball ball;

    [Header("弹性（转交给 B1Ball / B1Item，上层与下层共用同一套）")]
    [Tooltip("重力加速度（米/秒²）")]
    public float gravity = 9.8f;
    [Tooltip("落地恢复系数 restitution：0.65~0.8 是比较像球的弹性；1 = 永远弹不衰减")]
    [Range(0f, 1f)] public float restitution = 0.72f;
    [Tooltip("左右边界反弹系数")]
    [Range(0f, 1f)] public float wallBounce = 0.75f;
    [Tooltip("水平空气阻力（每秒衰减比例）")]
    public float damping = 0.15f;
    [Tooltip("最小反弹速度（米/秒）：落地速度低于它才允许静止")]
    public float minBounceSpeed = 0.8f;
    [Tooltip("落地时保留的水平速度比例（1 = 不损失）")]
    [Range(0f, 1f)] public float groundHorizontalDamping = 0.92f;

    [Header("下层保底弹跳（只保底，不自动上弹、不跨层）")]
    [Tooltip("下层最低弹射高度（米）：球每次落地后的反弹高度低于它，就补回到这个高度；0 = 关闭。默认 1.5 米（球直径约 1 米，相当于一个半球高）")]
    [Range(0f, 4f)] public float layerBounceMinHeight = 1.5f;

    [Header("水平活动范围（球滚出画面就看不到了）")]
    [Tooltip("把球的水平活动范围夹在相机可视宽度内（推荐开）；关闭则沿用 B1 的世界边界 ±9.86")]
    public bool clampToCameraView = true;
    [Tooltip("相机左右各留的余量（米），避免球贴着画面边缘")]
    public float viewMargin = 0.35f;
    [Tooltip("相机不是正交相机 / 取不到时的兜底半宽（米）")]
    public float fallbackHalfWidth = 8f;

    [Header("双形态（上层实体 / 下层字符画）")]
    [Tooltip("分界线 Y：0 = 使用 B1LayerConfig.upperFloorY（场景里已有的上层地板高度）")]
    public float splitY = 0f;
    [Tooltip("实体形态的根节点；留空 = 用本物体下所有 Renderer（不含字符画 TMP）")]
    public Transform solidForm;
    [Tooltip("形态过渡时间（秒）：只影响字符画的 alpha 与实体显隐，不动任何变换")]
    public float formFadeSeconds = 0.18f;
    [Tooltip("物理半径按渲染包围盒自动推导：避免半径与视觉尺寸不一致导致半个球埋进地板")]
    public bool autoRadiusFromBounds = true;

    [Header("下层字符表现（单个 @ 字符，走 B1 的字符拖尾）")]
    [Tooltip("下层用来表示球的字符（只用第 1 个字符）")]
    public string glyphChar = "@";
    [Tooltip("字符字号（TMP 字号）：越大字符纹理越清晰，最终世界尺寸由球直径决定")]
    public float glyphFontSize = 1.6f;
    [Tooltip("字符高度相对实体球直径的比例（1 = 完全一致，上下切换时不跳尺寸）")]
    [Range(0.3f, 1.5f)] public float glyphDiameterScale = 0.95f;
    [Tooltip("字符颜色（alpha 是显示上限，切换时按它淡入淡出）")]
    public Color glyphColor = new Color(0.6f, 1f, 0.75f, 0.9f);
    [Tooltip("字符的渲染排序（数字越大越靠前）")]
    public int glyphSortOrder = 6;
    [Tooltip("下层是否甩字符残影拖尾（复用 B1Ball 已有的 GlyphTrail）")]
    public bool trailEnabled = true;
    [Tooltip("拖尾残影的发射间隔（秒）")]
    public float trailInterval = 0.05f;
    [Tooltip("球速低于此值不发射拖尾（停着不拉尾）")]
    public float trailMinSpeed = 0.5f;

    [Header("恐龙下压（靠近 + 起跳 + 下压，把球送过分界线）")]
    [Tooltip("是否允许恐龙主动靠近并跳起把球压到下层")]
    public bool dinoPressEnabled = true;
    [Tooltip("恐龙在多远内会注意到球并走过来（米）")]
    public float dinoNoticeRange = 3.2f;
    [Tooltip("恐龙离球多近才起跳下压（米）")]
    public float dinoPressRadius = 0.9f;
    [Tooltip("被压下时给球的向下速度（米/秒）")]
    public float pressDownSpeed = 7f;
    [Tooltip("穿板时间窗（秒）：这段时间内球无视上层地板，直接落到下层")]
    public float passThroughSeconds = 0.6f;

    private Renderer[] _solidRenderers;
    private TextMeshPro _glyph;
    private Sprite _glyphSprite;
    private float _formT = 1f;            // 1 = 实体，0 = 字符画
    private DinoCController _dino;
    private bool _dinoEngaged;
    private float _pressCooldown;
    private float _ballRadius = 0.5f;
    private float _lastAspect = -1f;

    /// <summary>当前速度（篮筐判定要读 vy 判断是否向下）。直接取 B1Ball 的运动结果。</summary>
    public Vector2 Velocity => ball != null ? ball.Velocity : Vector2.zero;

    /// <summary>重力（篮筐辅助弹射的抛体解算用）。</summary>
    public float Gravity => ball != null ? ball.gravity : gravity;

    /// <summary>是否位于上层（实体形态）。位置由 B1Ball 的物理步进写入。</summary>
    public bool IsUpper => transform.position.y >= SplitY;

    private float SplitY => splitY > 0f ? splitY : (world != null ? world.upperFloorY : 0f);

    private void Awake()
    {
        All.Add(this);
        if (world == null) world = FindObjectOfType<B1LayerConfig>();

        // 复用 B1 的球：没有就补一个（物理、反弹、恐龙踢球全走它）
        if (ball == null) ball = GetComponent<B1Ball>();
        if (ball == null) ball = gameObject.AddComponent<B1Ball>();

        NormalizeNonUniformScale();
        CollectSolidRenderers();
        ApplyToon();
        ConfigurePhysics();
        CreateGlyph();
    }

    private void OnDestroy() => All.Remove(this);

    private void Update()
    {
        // 画面比例变了（改窗口大小）就重新夹一次水平范围
        if (clampToCameraView)
        {
            Camera cam = Camera.main;
            float aspect = cam != null ? cam.aspect : 0f;
            if (!Mathf.Approximately(aspect, _lastAspect)) ApplyHorizontalBounds();
        }

        UpdateForm(Time.deltaTime);
        UpdateTrail();
        UpdateDinoPress();
    }

    /// <summary>下层用单个字符 + 字符残影拖尾（拖尾复用 B1Ball 已有的 GlyphTrail）。</summary>
    private void UpdateTrail()
    {
        if (!trailEnabled || ball == null || _glyph == null || _glyphSprite == null) return;
        if (_formT >= 0.5f) return;                                    // 只有字符形态才拉尾
        if (ball.Velocity.sqrMagnitude < trailMinSpeed * trailMinSpeed) return;
        ball.EmitTrail(transform.position, _glyphSprite, glyphColor, 2f * _ballRadius);
    }

    /// <summary>篮筐辅助弹射 / 恐龙踢球：直接赋速度（交给 B1Ball）。</summary>
    public void Launch(Vector2 velocity)
    {
        if (ball != null) ball.Launch(velocity);
    }

    /// <summary>恐龙下压：让球在 passThroughSeconds 内无视上层地板，直接掉到下层。</summary>
    public void PressDown()
    {
        if (ball == null) return;
        ball.IgnoreUpperFloorFor(passThroughSeconds);
        ball.Launch(new Vector2(ball.Velocity.x * 0.5f, -Mathf.Abs(pressDownSpeed)));
    }

    // —— 配置（把 Inspector 参数转交给 B1Ball / B1Item）——

    private void ConfigurePhysics()
    {
        if (ball == null) return;

        ball.gravity = gravity;
        ball.groundBounce = restitution;
        ball.wallBounce = wallBounce;
        ball.drag = damping;
        ball.minBounceSpeed = minBounceSpeed;
        ball.groundHorizontalDamping = groundHorizontalDamping;
        ball.layerBounceMinHeight = layerBounceMinHeight;
        ball.SetTrailInterval(trailInterval);

        if (autoRadiusFromBounds && TryGetSolidBounds(out Bounds bounds))
        {
            // 取水平/垂直里较大的半轴：宁可略微悬空，也不让球埋进地板（埋进去看起来就是被压扁）
            _ballRadius = Mathf.Max(bounds.extents.x, bounds.extents.y);
            ball.SetPhysicsRadius(_ballRadius);
            Debug.Log($"[Ball] 物理半径按渲染包围盒取 {_ballRadius:F3} 米" +
                      $"（包围盒 {bounds.size.x:F3}×{bounds.size.y:F3}×{bounds.size.z:F3}）");
        }
        else if (ball != null)
        {
            _ballRadius = Mathf.Max(0.02f, ball.Radius);
        }

        ApplyHorizontalBounds();
    }

    /// <summary>
    /// 球的水平活动范围夹到相机可视宽度内：B1 的世界边界是 ±9.86，
    /// 而正交相机（size 4.885，16:9）只能看到 ±8.68，沿用世界边界就会滚出画面。
    /// </summary>
    private void ApplyHorizontalBounds()
    {
        if (ball == null) return;

        if (!clampToCameraView)
        {
            ball.useCustomHorizontalBounds = false;
            _lastAspect = -1f;
            return;
        }

        Camera cam = Camera.main;
        float halfWidth = fallbackHalfWidth;
        if (cam != null && cam.orthographic) halfWidth = cam.orthographicSize * cam.aspect;
        _lastAspect = cam != null ? cam.aspect : 0f;

        float limit = Mathf.Max(0.5f, halfWidth - viewMargin - _ballRadius);
        ball.SetHorizontalBounds(-limit, limit);
        Debug.Log($"[Ball] 水平活动范围夹到 ±{limit:F2} 米（相机半宽 {halfWidth:F2}，球半径 {_ballRadius:F2}）");
    }

    private bool TryGetSolidBounds(out Bounds bounds)
    {
        bounds = new Bounds(transform.position, Vector3.zero);
        bool has = false;
        for (int i = 0; i < _solidRenderers.Length; i++)
        {
            var r = _solidRenderers[i];
            if (r == null) continue;
            if (!has) { bounds = r.bounds; has = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return has;
    }

    /// <summary>
    /// 球的资源本身是标准单位球（±0.5，identity），但预制体根节点或父节点可能被非等比缩放，
    /// 那样看起来就是"压扁的球"。这里检测世界缩放，按最大轴补偿成本地缩放，并明确打日志。
    /// </summary>
    private void NormalizeNonUniformScale()
    {
        Vector3 worldScale = transform.lossyScale;
        float max = Mathf.Max(worldScale.x, Mathf.Max(worldScale.y, worldScale.z));
        float min = Mathf.Min(worldScale.x, Mathf.Min(worldScale.y, worldScale.z));
        if (max <= 0.0001f || min / max >= 0.999f) return;

        Vector3 local = transform.localScale;
        transform.localScale = new Vector3(
            local.x * max / Mathf.Max(0.0001f, worldScale.x),
            local.y * max / Mathf.Max(0.0001f, worldScale.y),
            local.z * max / Mathf.Max(0.0001f, worldScale.z));

        Debug.LogWarning($"[Ball] 球的缩放不均匀（世界缩放 {worldScale.x:F3}, {worldScale.y:F3}, {worldScale.z:F3}），" +
                         "看起来会被压扁；已按最大轴补偿。请检查预制体根节点/父节点的 Scale 是否被非等比改过。", this);
    }

    // —— 双形态（只改显隐与 alpha，不写 transform 的位移/缩放）——

    private void UpdateForm(float dt)
    {
        float target = IsUpper ? 1f : 0f;
        float step = formFadeSeconds > 0.001f ? dt / formFadeSeconds : 1f;
        _formT = Mathf.MoveTowards(_formT, target, step);

        bool solidVisible = _formT > 0.5f;
        for (int i = 0; i < _solidRenderers.Length; i++)
            if (_solidRenderers[i] != null) _solidRenderers[i].enabled = solidVisible;

        if (_glyph != null)
        {
            bool glyphVisible = _formT < 0.98f;
            if (_glyph.gameObject.activeSelf != glyphVisible) _glyph.gameObject.SetActive(glyphVisible);
            _glyph.alpha = glyphColor.a * (1f - _formT);
        }
    }

    /// <summary>
    /// 立刻切成实体形态（快速测试把球重置到筐口上方时用）：直接把过渡值拍到位并刷新一次显隐，
    /// 不写 transform、不影响物理。
    /// </summary>
    public void ForceSolidForm()
    {
        _formT = 1f;
        UpdateForm(0f);
    }

    private void CollectSolidRenderers()
    {
        Transform root = solidForm != null ? solidForm : transform;
        var list = new List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (r.GetComponent<TMP_Text>() != null) continue; // 字符画不参与实体形态
            list.Add(r);
        }
        _solidRenderers = list.ToArray();
    }

    private void ApplyToon()
    {
        // 预制体不走 ObjectModelService 的生成管线，只能自己套；
        // 另注意 ResolveLayer 会把 .glb 预制体实例判成背景档，所以这里必须显式指定前景。
        for (int i = 0; i < _solidRenderers.Length; i++)
            ToonMaterials.Apply(_solidRenderers[i], ToonLayer.Foreground);
    }

    private void CreateGlyph()
    {
        _glyph = B1Text.Make("BallGlyph", transform, glyphFontSize, glyphColor, glyphSortOrder);
        _glyph.transform.localPosition = Vector3.zero;
        _glyph.alignment = TextAlignmentOptions.Center;
        _glyph.enableAutoSizing = false;
        _glyph.overflowMode = TextOverflowModes.Overflow;

        // 下层只用一个字符（和 B1 原本的字符表现一致），不再拼多行字符画
        char c = string.IsNullOrEmpty(glyphChar) ? '@' : glyphChar[0];
        _glyphSprite = LetterPlant.GlyphSprite(c);
        _glyph.text = c.ToString();
        _glyph.ForceMeshUpdate();

        // 字符高度缩放到实体球直径：上下切换时尺寸一致，不会跳
        float target = 2f * _ballRadius * glyphDiameterScale;
        Renderer glyphRenderer = _glyph.GetComponent<Renderer>();
        float worldHeight = glyphRenderer != null ? glyphRenderer.bounds.size.y : 0f;
        // 兜底：包围盒这一帧还没刷新时用 preferredHeight（与包围盒同量级）
        if (worldHeight <= 0.0001f)
            worldHeight = _glyph.preferredHeight * Mathf.Abs(transform.lossyScale.y);
        if (worldHeight > 0.0001f)
            _glyph.transform.localScale *= Mathf.Max(0.01f, target / worldHeight);

        _glyph.alpha = glyphColor.a * (1f - _formT);
        Debug.Log($"[Ball] 下层字符 '{c}'，视觉高度缩到 {target:F2} 米（球直径 {2f * _ballRadius:F2}）");
    }

    // —— 恐龙下压 ——

    private void UpdateDinoPress()
    {
        _pressCooldown -= Time.deltaTime;
        if (!dinoPressEnabled || ball == null) return;

        if (_dino == null) _dino = FindObjectOfType<DinoCController>();
        if (_dino == null) return;

        if (!IsUpper)
        {
            _dinoEngaged = false;
            return;
        }

        float dx = transform.position.x - _dino.transform.position.x;
        float adx = Mathf.Abs(dx);

        if (!_dinoEngaged)
        {
            // 球在动 / 没躺在地上 / 离得远 → 不理会（踩球只针对停稳在上层地板上的球）
            if (adx > dinoNoticeRange || ball.Velocity.sqrMagnitude > 1.2f || !ball.Grounded) return;
            _dinoEngaged = true;
        }

        if (adx > dinoPressRadius)
        {
            _dino.ExternalWalkTo(transform.position.x, 1.2f);
            _dino.ExternalFace(dx > 0f ? 1 : -1);
            return;
        }

        if (!_dino.IsAirborne) _dino.ExternalHop(3.2f);

        if (_dino.IsFalling && _pressCooldown <= 0f)
        {
            PressDown();
            _pressCooldown = 2.5f;
            _dinoEngaged = false;
            Debug.Log($"[Ball] 恐龙下压：球被送过分界线（splitY={SplitY:F2}）→ 下层字符画形态");
        }
    }
}
