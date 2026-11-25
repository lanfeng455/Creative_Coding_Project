// BubbleReward.cs —— Prototype C：泡泡奖励
// 流程：
//   1. 篮筐进球后 Show()：画面正中央出现泡泡，表面显示「摄像头里的人」；
//   2. 画面来源不自建相机/RT：直接读 MediaPipe 已有的 WebCamTexture
//      （Mediapipe.Unity.Sample.ImageSourceProvider.ImageSource.GetCurrentTexture()）；
//   3. 泡泡 shader 按「球面经纬（equirect）」采样这张纹理：整张画面绕球面铺开，
//      正面看只看到前半球（预期结果，不做屏幕投影特写）；画面亮度整体压暗，边缘只留一圈薄高光；
//   4. 倒计时结束拍照：同一张画面 Blit → ReadPixels → 把镜像/翻转烘进像素 → 存 PNG，
//      并把这枚 Texture2D 定为该泡泡的主贴图（frozen），之后飘动期间显示拍照那一刻的定格画面；
//   5. 泡泡上有 3 秒世界空间倒计时：玩家点破 = 取消奖励（不定格）；到点 = 自动拍照 + 定格；
//   6. FinishSession() 是唯一收尾入口：恢复文字雨（0.8s 渐进）、泡泡缩小到恐龙大小并上升飘动；
//      keepSeconds = 0（默认）时永久保留，再次进球叠加新泡泡，旧泡泡不销毁；
//   7. 多个泡泡并存：每个泡泡各自 new Material + 各自快照 Texture2D，互不串图。

using System.Collections;
using System.Collections.Generic;
using System.IO;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class BubbleReward : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("主相机；留空会自动用 Camera.main")]
    public Camera mainCamera;
    [Tooltip("泡泡机模型（占位，可留空）：有值时泡泡从它的位置升起；后续接模型只需换这个引用")]
    public Transform bubbleMachine;

    [Header("泡泡")]
    [Tooltip("泡泡半径（世界单位）")]
    public float bubbleRadius = 1.1f;
    [Tooltip("倒计时秒数：到点自动拍照")]
    public float countdownSeconds = 3f;
    [Tooltip("世界空间倒计时字号")]
    public float countdownFontSize = 1.2f;
    [Tooltip("倒计时颜色")]
    public Color countdownColor = new Color(1f, 1f, 1f, 0.95f);

    [Header("画面亮度（压暗，避免过曝 / Bloom 爆开）")]
    [Tooltip("摄像头画面亮度系数：1 = 原亮度。默认 0.55，明显压暗，保证能看清画面里的人")]
    public float feedBrightness = 0.55f;
    [Tooltip("边缘高光强度：默认 0.3，0 = 完全没有边缘光")]
    public float rimStrength = 0.3f;
    [Tooltip("边缘高光宽度：越大圈越窄，默认 6（只留薄薄一圈，不会整圈发白）")]
    public float rimPower = 6f;
    [Tooltip("泡泡整体不透明度")]
    [Range(0f, 1f)]
    public float bubbleAlpha = 0.9f;

    [Header("新流程（吹泡泡奖励）")]
    [Tooltip("倒计时数字改由这个 UI 文本显示；留空则用世界空间数字")]
    public TMP_Text countdownUI;
    [Tooltip("点击泡泡戳破（旧玩法）。新流程会关掉它，改用指尖二选一")]
    public bool clickToPopEnabled = true;
    [Tooltip("倒计时结束时是否自动恢复文字雨。新流程自己控制在「泡泡升到天花板」之后再恢复")]
    public bool autoResumeRain = true;

    [Header("拍照")]
    [Tooltip("照片宽度（像素）；高度按摄像头画面宽高比自动算，再用下面的高度封顶")]
    public int photoWidth = 1024;
    [Tooltip("照片高度上限（像素）")]
    public int photoHeight = 1024;

    [Header("结束表现（拍完 / 戳破后）")]
    [Tooltip("缩小后的直径（世界单位）：约等于恐龙大小")]
    public float shrinkToDiameter = 0.9f;
    [Tooltip("缩小完成后向上飘的速度（米/秒）")]
    public float riseSpeed = 1.4f;
    [Tooltip("在房间上部左右飘动的幅度（米）")]
    public float driftAmplitude = 1.6f;
    [Tooltip("结束表现持续多久后销毁（秒）；0 = 永久保留（默认）。之后再次进球会生成新的泡泡，旧的同时存在")]
    public float keepSeconds = 0f;

    /// <summary>一枚泡泡的运行时数据：各自独立的根节点、材质实例、快照纹理。</summary>
    private class Bubble
    {
        public GameObject root;
        public Transform sphere;
        public Material material;
        public TextMeshPro countdown;
        public Texture2D snapshot;   // 定格快照（仅自动拍照成功的泡泡有）
        public bool active;          // 还在倒计时（只允许一个）
        public bool frozen;          // 已经定格，不再跟随实时摄像头
        public float timeLeft;
        public float riseT;
        public Vector3 spawnPos;
        public Vector3 center;
        public Vector3 floatOrigin;
        public float shrinkT;
        public float floatT;
        public float driftSeed;
        public float topOffset;
        public float startDiameter;
    }

    private readonly List<Bubble> _bubbles = new List<Bubble>();
    private Bubble _session;
    private B2BackgroundRain _rain;

    /// <summary>泡泡当前所处的阶段（新奖励流程用；旧 Show() 直接进 Counting）。</summary>
    private enum Phase { None, Growing, Counting, AwaitingChoice }

    private Phase _phase = Phase.None;
    private bool _awaitChoice;   // true = 新流程：拍完照后等玩家二选一；false = 旧流程：拍完直接收尾
    private float _growT;
    private float _growDuration;
    private float _growFromDiameter;
    private float _growToDiameter;
    private Bubble _rising;   // 已「保留」、正在上升的泡泡（用来判断是否升到天花板）

    /// <summary>倒计时结束、已定格，接下来等玩家选「保留 / 不保留」。</summary>
    public event System.Action CountdownCompleted;

    /// <summary>当前是否在等玩家选择。</summary>
    public bool IsAwaitingChoice => _session != null && _phase == Phase.AwaitingChoice;

    /// <summary>上一次「保留」的泡泡是否已经升到天花板（新流程据此恢复游戏）。</summary>
    public bool ReachedCeiling
    {
        get
        {
            if (_rising == null || _rising.root == null) return false;
            float topLimit = 5.2f - 0.6f - _rising.topOffset;
            return _rising.root.transform.position.y >= topLimit - 0.03f;
        }
    }

    private ImageSource _imageSource;
    private Texture _feedTexture;
    private Texture2D _placeholder;
    private bool _feedBound;
    private bool _flipX;
    private bool _flipY;
    private float _longitudeScale = 1f;
    private float _nextFeedWarn;

    /// <summary>当前是否有一个正在倒计时的泡泡。</summary>
    public bool IsActive => _session != null && _session.active;

    /// <summary>触发泡泡奖励（篮筐进球时调用）。已经在倒计时中则忽略。</summary>
    public void Show()
    {
        if (IsActive)
        {
            Debug.Log("[Bubble] 上一个泡泡还在倒计时，本次进球不再新建（等它拍完/被戳破后再进）");
            return;
        }

        if (!ValidateBeforeShow(out Shader bubbleShader)) return;

        _rain = _rain != null ? _rain : FindObjectOfType<B2BackgroundRain>();
        if (_rain != null) _rain.Suspend(this);

        _awaitChoice = false;   // 旧流程：拍照后直接收尾，不等玩家选择
        Bubble bubble = CreateBubble(bubbleShader);
        _session = bubble;

        // 材质已建好，立即把摄像头画面接上（拿不到就用占位色，运行时继续重试）
        RefreshFeed(logProblems: false, out _);
        ApplyFeedTo(bubble);

        if (!ValidateSpawned(bubble))
        {
            FinishSession(cancelled: true);
            return;
        }

        Debug.Log($"[Bubble] 泡泡出现：{countdownSeconds:F0} 秒内点击可戳破取消，到点自动拍照；" +
                  $"当前画面上共有 {_bubbles.Count} 个泡泡");
    }

    /// <summary>
    /// 新流程：在指定世界坐标生成泡泡，并由小长大到 bubbleRadius*2；
    /// 长大结束后自动进入倒计时（原地不动，不响应点击戳破）。
    /// </summary>
    public void ShowGrowing(Vector3 worldPos, float startDiameter, float growSeconds)
    {
        if (IsActive)
        {
            Debug.LogWarning("[Bubble] 上一个泡泡还没结束，ShowGrowing 被忽略");
            return;
        }

        if (!ValidateBeforeShow(out Shader bubbleShader)) return;

        _rain = _rain != null ? _rain : FindObjectOfType<B2BackgroundRain>();
        // 新流程（autoResumeRain=false）里由 DinoBubbleSequence 统一暂停/恢复文字雨，
        // 这里不能再以 BubbleReward 的身份挂一个永不解除的暂停。
        if (autoResumeRain && _rain != null) _rain.Suspend(this);

        Bubble bubble = CreateBubble(bubbleShader, worldPos);
        _session = bubble;
        _phase = Phase.Growing;
        _awaitChoice = true;    // 新流程：倒计时结束后等玩家选「保留 / 不保留」
        _growFromDiameter = Mathf.Max(0.02f, startDiameter);
        _growToDiameter = Mathf.Max(_growFromDiameter, bubbleRadius * 2f);
        _growDuration = Mathf.Max(0.05f, growSeconds);
        _growT = 0f;
        _rising = null;

        if (bubble.sphere != null) bubble.sphere.localScale = Vector3.one * _growFromDiameter;
        if (bubble.countdown != null) bubble.countdown.gameObject.SetActive(false);
        if (countdownUI != null) countdownUI.gameObject.SetActive(false);

        RefreshFeed(logProblems: false, out _);
        ApplyFeedTo(bubble);

        if (!ValidateSpawned(bubble))
        {
            FinishSession(cancelled: true);
            return;
        }

        Debug.Log($"[Bubble] 吹泡泡奖励：从 ({worldPos.x:F2}, {worldPos.y:F2}) 开始，" +
                  $"直径 {_growFromDiameter:F2} → {_growToDiameter:F2}（{_growDuration:F2} 秒），随后倒计时");
    }

    /// <summary>玩家选择「保留」：走原有缩小上升流程。</summary>
    public void KeepCurrent()
    {
        if (_session == null || _phase != Phase.AwaitingChoice) return;
        _phase = Phase.None;
        FinishSession(cancelled: false);
    }

    /// <summary>玩家选择「不保留」：泡泡缩小消失，然后恢复文字雨。</summary>
    public void DiscardCurrent()
    {
        Bubble bubble = _session;
        if (bubble == null || _phase != Phase.AwaitingChoice) return;

        _phase = Phase.None;
        _session = null;
        if (bubble.countdown != null) bubble.countdown.gameObject.SetActive(false);
        if (countdownUI != null) countdownUI.gameObject.SetActive(false);

        StartCoroutine(PopAndRelease(bubble));
    }

    private IEnumerator PopAndRelease(Bubble bubble)
    {
        Vector3 start = bubble.sphere != null ? bubble.sphere.localScale : Vector3.one;
        float alpha0 = bubbleAlpha;
        float t = 0f;
        const float dur = 0.32f;

        while (t < dur)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / dur);
            if (bubble.sphere != null) bubble.sphere.localScale = Vector3.Lerp(start, Vector3.zero, k);
            if (bubble.material != null) bubble.material.SetFloat("_Alpha", Mathf.Clamp01(alpha0 * (1f - k)));
            yield return null;
        }

        if (autoResumeRain && _rain != null) _rain.Resume(this);
        ReleaseBubble(bubble);
        _bubbles.Remove(bubble);
        Debug.Log("[Bubble] 玩家选择不保留：泡泡缩小消失");
    }

    /// <summary>
    /// 生成前的一次性自检：把「缺什么」列清楚并打 LogError，避免再出现"什么都没有但也不报错"。
    /// 只有缺主相机是致命的；其余项尽量补救后继续。
    /// </summary>
    private bool ValidateBeforeShow(out Shader bubbleShader)
    {
        bubbleShader = null;

        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("[Bubble] 自检失败：缺 mainCamera（Inspector 未连、Camera.main 也为空），泡泡无法定位，奖励中止");
            return false;
        }

        var problems = new List<string>();

        bubbleShader = Shader.Find("PrototypeC/Bubble");
        if (bubbleShader == null)
        {
            problems.Add("找不到 shader PrototypeC/Bubble（已退回 Unlit/Transparent，不会显示摄像头画面）");
            bubbleShader = Shader.Find("Unlit/Transparent");
            if (bubbleShader == null) bubbleShader = Shader.Find("Sprites/Default");
            if (bubbleShader == null)
            {
                Debug.LogError("[Bubble] 自检失败：PrototypeC/Bubble 与备用 shader 都找不到，泡泡无法渲染（奖励中止）");
                return false;
            }
        }

        if (photoWidth < 64) problems.Add($"photoWidth={photoWidth} 太小，已按 64 处理");

        // 画面来源自检：只读 MediaPipe 的 WebCamTexture，不新建捕获
        if (!RefreshFeed(logProblems: true, out string feedWhy))
            problems.Add(feedWhy);

        if (problems.Count > 0)
        {
            Debug.LogError($"[Bubble] 自检发现问题（已尽量补救并继续）：{string.Join("；", problems)}");
        }

        return true;
    }

    /// <summary>生成后的自检：球体存在、Renderer 开着、包围盒不为空、位置在画面里。返回 false = 不可见。</summary>
    private bool ValidateSpawned(Bubble bubble)
    {
        if (bubble == null || bubble.root == null)
        {
            Debug.LogError("[Bubble] 自检失败：泡泡根节点没有生成，奖励中止");
            return false;
        }

        var renderer = bubble.sphere != null ? bubble.sphere.GetComponent<Renderer>() : null;
        if (renderer == null)
        {
            Debug.LogError("[Bubble] 自检失败：泡泡球体没有 Renderer，必然不可见");
            return false;
        }

        if (!renderer.enabled)
            Debug.LogError("[Bubble] 自检：球体 Renderer.enabled = false（已强制打开）");
        renderer.enabled = true;

        Bounds b = renderer.bounds;
        if (b.size.sqrMagnitude < 0.0001f)
        {
            Debug.LogError($"[Bubble] 自检失败：球体包围盒为空 size={b.size}（scale={bubble.sphere.localScale}），不可见");
            return false;
        }

        Vector3 vp = mainCamera.WorldToViewportPoint(bubble.root.transform.position);
        bool inView = vp.z > 0f && vp.x > -0.5f && vp.x < 1.5f && vp.y > -0.5f && vp.y < 1.5f;

        string feedDesc = _feedTexture != null
            ? $"{_feedTexture.width}×{_feedTexture.height}（{(_imageSource != null ? _imageSource.sourceName : "?")}）"
            : "未就绪（已用占位色，运行时继续尝试接入）";

        Debug.Log($"[Bubble] 自检通过：球体中心 {bubble.root.transform.position}｜半径 {bubbleRadius:F2}" +
                  $"（包围盒 {b.size.x:F2}×{b.size.y:F2}）｜在画面内={inView}（视口 {vp.x:F2}, {vp.y:F2}）" +
                  $"｜material={bubble.material?.shader?.name}｜摄像头画面={feedDesc}" +
                  $"｜球面经纬覆盖={_longitudeScale:F2}（正面 = 画面中部，只看到前半球）" +
                  $"｜亮度={feedBrightness:F2}｜边缘光={rimStrength:F2}/{rimPower:F1}" +
                  $"｜镜像X={_flipX}｜翻转Y={_flipY}");

        if (!inView)
            Debug.LogWarning("[Bubble] 泡泡中心不在相机视口内，可能看不到；检查 mainCamera 与 bubbleRadius");

        return true;
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // 摄像头：只取一次引用即可（WebCamTexture 自己出帧），拿不到时每帧重试
        if (!_feedBound && _bubbles.Count > 0)
            RefreshFeed(logProblems: IsActive && Time.time >= _nextFeedWarn, out _);

        if (IsActive) UpdateSession(_session, dt);

        // 结束后飘动的泡泡（含以前留下的）：keepSeconds = 0 时永久保留
        for (int i = _bubbles.Count - 1; i >= 0; i--)
        {
            Bubble b = _bubbles[i];
            if (b == null) { _bubbles.RemoveAt(i); continue; }
            if (b.active) continue;

            if (!UpdateFloating(b, dt))
            {
                ReleaseBubble(b);
                _bubbles.RemoveAt(i);
            }
        }
    }

    // —— 倒计时中的泡泡 ——

    private void UpdateSession(Bubble bubble, float dt)
    {
        if (bubble == null || bubble.root == null)
        {
            Debug.LogError("[Bubble] 泡泡根节点丢失，结束本次奖励");
            FinishSession(cancelled: true);
            return;
        }

        // —— 新流程：先从小长大，长大完才开始倒计时 ——
        if (_phase == Phase.Growing)
        {
            _growT += dt;
            float k = Mathf.Clamp01(_growT / _growDuration);
            float eased = 1f - Mathf.Pow(1f - k, 3f);
            float d = Mathf.Lerp(_growFromDiameter, _growToDiameter, eased);
            if (bubble.sphere != null) bubble.sphere.localScale = Vector3.one * d;

            if (k >= 1f)
            {
                _phase = Phase.Counting;
                bubble.timeLeft = Mathf.Max(0.1f, countdownSeconds);
                if (bubble.countdown != null)
                {
                    bubble.countdown.gameObject.SetActive(countdownUI == null);
                    bubble.countdown.text = Mathf.CeilToInt(bubble.timeLeft).ToString();
                }
                if (countdownUI != null)
                {
                    countdownUI.gameObject.SetActive(true);
                    countdownUI.text = Mathf.CeilToInt(bubble.timeLeft).ToString();
                }
                Debug.Log($"[Bubble] 泡泡长大完成，开始 {countdownSeconds:F0} 秒倒计时");
            }
            return;
        }

        // —— 已定格、等玩家二选一 ——
        if (_phase == Phase.AwaitingChoice)
        {
            if (bubble.sphere != null)
            {
                float breathe = _growToDiameter * (1f + Mathf.Sin(Time.time * 2.2f) * 0.012f);
                bubble.sphere.localScale = Vector3.one * breathe;
            }
            return;
        }

        // 从泡泡机位置升到画面正中（没有泡泡机 / 新流程指定坐标时直接就在目标点）
        bubble.riseT = Mathf.MoveTowards(bubble.riseT, 1f, dt / 0.35f);
        bubble.root.transform.position = Vector3.Lerp(bubble.spawnPos, bubble.center, bubble.riseT);

        bubble.timeLeft -= dt;
        int shown = Mathf.Max(0, Mathf.CeilToInt(bubble.timeLeft));
        if (countdownUI != null)
        {
            string uiText = shown.ToString();
            if (countdownUI.text != uiText) countdownUI.text = uiText;
        }
        else if (bubble.countdown != null)
        {
            string text = shown.ToString();
            if (bubble.countdown.text != text) bubble.countdown.text = text;
        }

        // 点击 / 触摸：点到泡泡上 = 提前戳破，取消奖励（新流程里会关掉这条分支）
        Vector2 screenPos;
        if (clickToPopEnabled && TryGetPointerDown(out screenPos) && HitBubble(bubble, screenPos))
        {
            Debug.Log("[Bubble] 玩家戳破泡泡 → 取消奖励（这个泡泡继续实时显示并永久飘在上方）");
            FinishSession(cancelled: true);
            return;
        }

        if (bubble.timeLeft <= 0f)
        {
            CapturePhoto(bubble);

            if (!_awaitChoice)
            {
                // 旧流程：拍完照直接收尾（缩小上升）
                FinishSession(cancelled: false);
                return;
            }

            // 新流程：拍照定格后不结束，停下来等玩家选「保留 / 不保留」
            _phase = Phase.AwaitingChoice;

            if (bubble.countdown != null) bubble.countdown.gameObject.SetActive(false);
            if (countdownUI != null) countdownUI.gameObject.SetActive(false);

            Debug.Log("[Bubble] 倒计时结束：已拍照定格，等待玩家选择是否保留");
            CountdownCompleted?.Invoke();
        }
    }

    // —— 摄像头画面来源（复用 MediaPipe 的 WebCamTexture）——

    /// <summary>
    /// 读 MediaPipe 已经在用的摄像头纹理。不新建相机、不渲染、不改追踪链路。
    /// 返回 false 时 why 说明卡在哪一步（ImageSource 为空 / 未就绪 / 纹理为空 / 还没出帧）。
    /// </summary>
    private bool RefreshFeed(bool logProblems, out string why)
    {
        why = null;

        if (_imageSource == null)
        {
            _imageSource = ImageSourceProvider.ImageSource;
            if (_imageSource != null)
                Debug.Log($"[Bubble] 摄像头画面来源：MediaPipe {_imageSource.GetType().Name}（{_imageSource.sourceName}）");
        }

        if (_imageSource == null)
        {
            why = "MediaPipe ImageSourceProvider.ImageSource 为空（Bootstrap 还没初始化完）";
        }
        else if (!_imageSource.isPrepared)
        {
            why = $"MediaPipe 画面源还没准备好（isPrepared=false，{_imageSource.sourceName}）";
        }
        else
        {
            Texture tex = null;
            try
            {
                tex = _imageSource.GetCurrentTexture();
            }
            catch (System.Exception e)
            {
                why = $"GetCurrentTexture 抛异常：{e.Message}";
            }

            if (why == null)
            {
                if (tex == null) why = "GetCurrentTexture 返回 null";
                else if (tex.width <= 16 || tex.height <= 16)
                    why = $"摄像头纹理还没开始出帧（{tex.width}×{tex.height}）";
                else
                {
                    _flipX = _imageSource.isFrontFacing;        // 前置摄像头：水平镜像，和原来 Canvas 里看到的一致
                    _flipY = _imageSource.isVerticallyFlipped;  // WebCamTexture 上下颠倒的平台

                    // 球面经纬铺开范围：经度覆盖 2/aspect 圈（16:9 ≈ 1.13 圈），正面看脸不会被拉宽/压扁；
                    // 上限 2 圈刚好让可见前半球（180°）落在 0~1 的 UV 内，不出现边缘拉丝
                    float aspect = tex.width / (float)Mathf.Max(1, tex.height);
                    _longitudeScale = Mathf.Clamp(2f / aspect, 1f, 2f);
                    _feedTexture = tex;

                    for (int i = 0; i < _bubbles.Count; i++) ApplyFeedTo(_bubbles[i]);

                    if (!_feedBound)
                    {
                        _feedBound = true;
                        Debug.Log($"[Bubble] 摄像头画面已接入：{tex.width}×{tex.height}" +
                                  $"（宽高比 {aspect:F2}，球面经度覆盖 {_longitudeScale:F2} 圈）" +
                                  $"｜镜像X={_flipX}｜翻转Y={_flipY}");
                    }
                    return true;
                }
            }
        }

        if (logProblems)
        {
            _nextFeedWarn = Time.time + 1f;
            Debug.LogError($"[Bubble] 拿不到摄像头画面：{why}");
        }

        return false;
    }

    /// <summary>把当前摄像头画面与视觉参数套到某一个泡泡上（已定格的泡泡不覆盖，避免串图）。</summary>
    private void ApplyFeedTo(Bubble bubble)
    {
        if (bubble == null || bubble.frozen || bubble.material == null || _feedTexture == null) return;

        bubble.material.SetTexture("_MainTex", _feedTexture);
        bubble.material.SetVector("_UvScale", new Vector4(_longitudeScale, 1f, 0f, 0f));
        bubble.material.SetVector("_Flip", new Vector4(_flipX ? 1f : 0f, _flipY ? 1f : 0f, 0f, 0f));
        ApplyLook(bubble.material);
    }

    /// <summary>亮度相关参数统一从这里写进材质（每颗泡泡自己的材质实例）。</summary>
    private void ApplyLook(Material mat)
    {
        if (mat == null) return;
        mat.SetFloat("_Exposure", Mathf.Max(0.05f, feedBrightness));
        mat.SetFloat("_Alpha", Mathf.Clamp01(bubbleAlpha));
        mat.SetFloat("_RimStrength", Mathf.Clamp01(rimStrength));
        mat.SetFloat("_FresnelPower", Mathf.Max(0.5f, rimPower));
    }

    // —— 统一收尾（所有分支都走这里）——

    /// <summary>结束倒计时流程：恢复文字雨、泡泡转入缩小上升。幂等；不会销毁泡泡。</summary>
    public void FinishSession(bool cancelled)
    {
        Bubble bubble = _session;
        if (bubble == null || !bubble.active) return;

        bubble.active = false;
        if (bubble.countdown != null) bubble.countdown.gameObject.SetActive(false);
        if (countdownUI != null) countdownUI.gameObject.SetActive(false);

        _phase = Phase.None;
        _awaitChoice = false;

        // 关键：无论自动拍照、戳破、异常中断，都在这里恢复雨（B2BackgroundRain 按 0.8s 渐进恢复）。
        // 新流程会把 autoResumeRain 关掉，等泡泡升到天花板后再自己恢复。
        if (autoResumeRain && _rain != null) _rain.Resume(this);

        if (bubble.root != null)
        {
            bubble.startDiameter = bubbleRadius * 2f;
            bubble.floatOrigin = bubble.root.transform.position;
            bubble.shrinkT = 0f;
            bubble.floatT = 0f;
            bubble.driftSeed = Random.Range(0f, 100f);
            bubble.topOffset = Random.Range(0f, 0.9f);   // 多个泡泡停在不同高度，避免完全重叠
            _rising = bubble;                            // 供新流程判断「升到天花板」
        }

        _session = null;

        string endState = cancelled ? "玩家戳破" : (bubble.frozen ? "已拍照定格" : "自动拍照失败，继续实时画面");
        string lifetime = keepSeconds > 0.01f ? $"{keepSeconds:F1} 秒后销毁" : "永久保留";
        Debug.Log($"[Bubble] 泡泡流程结束（{endState}）：文字雨开始恢复，泡泡{lifetime}");
    }

    private void OnDisable()
    {
        if (IsActive) FinishSession(cancelled: true);
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _bubbles.Count; i++) ReleaseBubble(_bubbles[i]);
        _bubbles.Clear();
        _session = null;

        if (_placeholder != null)
        {
            Destroy(_placeholder);
            _placeholder = null;
        }
    }

    // —— 构建 ——

    private Vector3 ViewCenter()
    {
        return mainCamera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 10f));
    }

    private Bubble CreateBubble(Shader bubbleShader, Vector3? worldPos = null)
    {
        var bubble = new Bubble();
        if (worldPos.HasValue)
        {
            // 新流程：泡泡就在指定位置原地长大，不做「从泡泡机升到画面中央」的位移
            bubble.center = worldPos.Value;
            bubble.spawnPos = worldPos.Value;
            bubble.riseT = 1f;
        }
        else
        {
            bubble.center = ViewCenter();
            bubble.spawnPos = bubbleMachine != null ? bubbleMachine.position + Vector3.up * 0.4f : bubble.center;
            bubble.riseT = bubbleMachine != null ? 0f : 1f;
        }
        bubble.timeLeft = Mathf.Max(0.1f, countdownSeconds);
        bubble.active = true;
        bubble.frozen = false;

        bubble.root = new GameObject("BubbleReward_Bubble");
        bubble.root.transform.position = bubble.spawnPos;

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Sphere";
        sphere.transform.SetParent(bubble.root.transform, false);
        // 内置 Sphere 直径是 1，所以 scale = 直径 = 半径 × 2
        sphere.transform.localScale = Vector3.one * (bubbleRadius * 2f);
        var collider = sphere.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        bubble.sphere = sphere.transform;

        // 每颗泡泡一份材质实例：颜色/亮度/贴图互不影响，多个泡泡并存不会串图
        bubble.material = new Material(bubbleShader);
        bubble.material.SetTexture("_MainTex", PlaceholderTexture());
        bubble.material.SetVector("_UvScale", new Vector4(_longitudeScale, 1f, 0f, 0f));
        bubble.material.SetVector("_Flip", Vector4.zero);
        bubble.material.SetColor("_Tint", Color.white);
        bubble.material.SetColor("_RimColor", new Color(0.62f, 0.95f, 1f, 1f));
        ApplyLook(bubble.material);

        var renderer = sphere.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = bubble.material;
            renderer.enabled = true;
            renderer.sortingOrder = 510;   // 压过奖励面板的压暗层(500)与大恐龙(501)
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        bubble.countdown = B1Text.Make("BubbleCountdown", bubble.root.transform, countdownFontSize, countdownColor, 20);
        bubble.countdown.transform.localPosition = new Vector3(0f, 0f, -0.4f);
        bubble.countdown.text = Mathf.CeilToInt(bubble.timeLeft).ToString();
        // 新流程要等「长大完成」才开始显示倒计时，而且通常会改用 UI 文本
        bubble.countdown.gameObject.SetActive(worldPos == null && countdownUI == null);

        _bubbles.Add(bubble);
        return bubble;
    }

    /// <summary>占位色：摄像头还没出帧时也不会是纯白/纯黑；配合默认亮度 0.55 也不会过曝。</summary>
    private Texture PlaceholderTexture()
    {
        if (_placeholder != null) return _placeholder;
        _placeholder = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var px = new Color32[16];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(60, 95, 120, 255);
        _placeholder.SetPixels32(px);
        _placeholder.Apply();
        return _placeholder;
    }

    // —— 点击 / 拍照 ——

    private bool TryGetPointerDown(out Vector2 screenPos)
    {
        screenPos = Vector2.zero;

        if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                screenPos = touch.position;
                return true;
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            screenPos = Input.mousePosition;
            return true;
        }

        return false;
    }

    private bool HitBubble(Bubble bubble, Vector2 screenPos)
    {
        if (mainCamera == null || bubble == null || bubble.root == null) return false;
        Vector3 world = mainCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 10f));
        return Vector2.Distance(world, bubble.root.transform.position) <= bubbleRadius * 1.15f;
    }

    /// <summary>
    /// 存摄像头画面并给这枚泡泡定格：同一张 WebCamTexture Blit 到临时 RT → ReadPixels
    /// （WebCamTexture 不能直接 ReadPixels）→ 按和泡泡表面相同的镜像/翻转处理像素 → PNG，
    /// 然后把这张 Texture2D 作为该泡泡的主贴图并停止跟随实时画面。每颗泡泡一份，互不共享。
    /// </summary>
    private void CapturePhoto(Bubble bubble)
    {
        Texture src = _feedTexture;
        if (src == null || src.width <= 16 || src.height <= 16)
        {
            Debug.LogError("[Bubble] 拍照失败：没有可用的摄像头画面（WebCamTexture 未就绪），本次不生成照片，泡泡保持实时画面");
            return;
        }

        RenderTexture tmp = null;
        Texture2D tex = null;
        try
        {
            int w = Mathf.Max(64, photoWidth);
            int h = Mathf.Max(64, Mathf.RoundToInt(w * (float)src.height / Mathf.Max(1, src.width)));
            int maxH = Mathf.Max(64, photoHeight);
            if (h > maxH)
            {
                w = Mathf.Max(64, Mathf.RoundToInt(w * maxH / (float)h));
                h = maxH;
            }

            tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, tmp);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = tmp;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;

            ApplyDisplayFlips(tex, w, h);

            byte[] png = tex.EncodeToPNG();

            string dir = Path.Combine(Application.persistentDataPath, "BubblePhotos");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"bubble_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, png);

            Debug.Log($"[Bubble] 照片已保存：{path}（{w}×{h}，来源 {src.width}×{src.height} 摄像头画面）");
            ReportStatus($"[Bubble] 照片已保存：{path}");

            // —— 定格：这张纹理归这枚泡泡所有，之后不再接收实时画面 ——
            bubble.snapshot = tex;
            tex = null;   // 所有权已交给泡泡，不在 finally 里销毁
            bubble.frozen = true;
            if (bubble.material != null)
            {
                bubble.material.SetTexture("_MainTex", bubble.snapshot);
                // 冻结的是和实时完全相同的经纬映射；镜像/翻转已经烘进像素，所以 _Flip 归零
                bubble.material.SetVector("_UvScale", new Vector4(_longitudeScale, 1f, 0f, 0f));
                bubble.material.SetVector("_Flip", Vector4.zero);
                ApplyLook(bubble.material);
            }
            Debug.Log("[Bubble] 画面已定格：这枚泡泡之后显示拍照那一刻的静态画面，不再跟随实时摄像头");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Bubble] 拍照失败：{e.Message}（文字雨仍会恢复）");
        }
        finally
        {
            if (tex != null) Destroy(tex);
            if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
        }
    }

    /// <summary>把镜像/翻转烘进像素：Texture2D 的 0 行是底部，翻转 Y = 交换行，镜像 X = 反转每行。</summary>
    private void ApplyDisplayFlips(Texture2D tex, int w, int h)
    {
        if (!_flipX && !_flipY) return;

        Color32[] px = tex.GetPixels32();
        if (_flipY)
        {
            var row = new Color32[w];
            for (int y = 0; y < h / 2; y++)
            {
                int a = y * w;
                int b = (h - 1 - y) * w;
                System.Array.Copy(px, a, row, 0, w);
                System.Array.Copy(px, b, px, a, w);
                System.Array.Copy(row, 0, px, b, w);
            }
        }

        if (_flipX)
        {
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * w;
                for (int x = 0; x < w / 2; x++)
                {
                    int i = rowStart + x;
                    int j = rowStart + (w - 1 - x);
                    Color32 t = px[i];
                    px[i] = px[j];
                    px[j] = t;
                }
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
    }

    private void ReportStatus(string message)
    {
        var ui = FindObjectOfType<PrototypeC.PrototypeCTestUI>();
        if (ui != null && ui.statusText != null) ui.statusText.text = message;
    }

    // —— 结束后的漂浮表现（永久或按 keepSeconds 销毁）——

    /// <summary>返回 false = 这枚泡泡已经到了销毁时间（只有 keepSeconds &gt; 0 时才会发生）。</summary>
    private bool UpdateFloating(Bubble bubble, float dt)
    {
        if (bubble.root == null) return false;

        bubble.floatT += dt;
        if (keepSeconds > 0.01f && bubble.floatT >= keepSeconds) return false;

        bubble.shrinkT = Mathf.MoveTowards(bubble.shrinkT, 1f, dt / 0.8f);
        if (bubble.sphere != null)
            bubble.sphere.localScale = Vector3.one * Mathf.Lerp(bubble.startDiameter, shrinkToDiameter, bubble.shrinkT);

        // 上限 5.2 - 0.6；每枚泡泡再随机降一点，多个泡泡并存时不会叠成一坨
        float topLimit = 5.2f - 0.6f - bubble.topOffset;
        Vector3 p = bubble.root.transform.position;
        p.y = Mathf.Min(p.y + riseSpeed * dt, topLimit);
        p.x = bubble.floatOrigin.x + (Mathf.PerlinNoise(Time.time * 0.25f + bubble.driftSeed, 0.37f) - 0.5f) * driftAmplitude * 2f;
        bubble.root.transform.position = p;
        return true;
    }

    /// <summary>销毁一枚泡泡自己的全部资源（根节点 + 材质实例 + 快照纹理）。</summary>
    private void ReleaseBubble(Bubble bubble)
    {
        if (bubble == null) return;
        if (bubble.root != null) Destroy(bubble.root);
        if (bubble.material != null) Destroy(bubble.material);
        if (bubble.snapshot != null) Destroy(bubble.snapshot);
        bubble.root = null;
        bubble.material = null;
        bubble.snapshot = null;
    }
}
