// 只处理右手食指的射线提供者
// 从食指 PIP（第二个关节, landmark index=6）出发，经过 TIP（指尖, landmark index=8）
// 计算射线方向，与屏幕四边做碰撞检测，发出命中事件

using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

using Mediapipe.Tasks.Vision.HandLandmarker;

public class RightHandIndexRayProvider : MonoBehaviour
{
    [Header("引用（可留空，会自动在场景中查找）")]
    [Tooltip("挂着 HandLandmarkerResultAnnotationController 的物体")]
    public MonoBehaviour handLandmarkerRunner;

    [Tooltip("HandLandmarkerResultAnnotationController 组件")]
    public MonoBehaviour annotationController;

    [Header("射线参数")]
    [Tooltip("射线在屏幕空间内的最大长度（像素）。超过屏幕对角线就够了")]
    public float maxScreenRayLength = 5000f;

    [Header("镜像设置（必须与 AnnotationController 保持一致）")]
    [Tooltip("前置摄像头通常会镜像，需与 HandLandmarkerResultAnnotationController.isMirrored 一致")]
    public bool isMirrored;

    [Header("LineRenderer（可选，用于可视化射线）")]
    public LineRenderer lineRenderer;
    [Range(0f, 1f)] public float lineWidth = 0.02f;
    public Gradient lineColor;

    [Header("屏幕边缘事件")]
    public UnityEvent onLeftEdgeHit;
    public UnityEvent onRightEdgeHit;
    public UnityEvent onTopEdgeHit;
    public UnityEvent onBottomEdgeHit;
    public UnityEvent onNoHit;
    public UnityEvent onLostHand;

    [Header("调试")]
    [Tooltip("每帧打印一条 log 还是只在状态变化时打 log")]
    public bool logEveryFrame = false;

    [Header("稳定性（抗抖动）")]
    [Tooltip("EMA 平滑系数，0.05=很稳但拖影重，0.4=灵敏但可能抖。推荐 0.15~0.25")]
    [Range(0.01f, 0.8f)]
    public float smoothingAlpha = 0.2f;

    [Tooltip("食指方向（PIP→TIP）低于这个像素长度时，沿用上一帧方向。伸直时方向短，检测噪声大")]
    public float minDirectionLengthPx = 40f;

    [Tooltip("命中点额外 EMA 平滑（让高光更稳）")]
    [Range(0.01f, 0.8f)]
    public float hitPointAlpha = 0.25f;

    [Header("快速挥动丢帧保护（手指跟丢/中断）")]
    [Tooltip("手部追踪偶尔漏检一两帧（快速上下挥动时最常见）时，保持最后一次指尖位置的最大时间（秒）。0 = 关闭宽限，一丢就失效")]
    public float trackingGraceSeconds = 0.25f;

    [Tooltip("宽限期内是否用最后一次观测到的指尖速度继续外推，让弹床跟着手走而不是定在原地")]
    public bool predictDuringGrace = true;

    [Tooltip("宽限期内允许外推的最大屏幕速度（像素/秒），防止追踪噪声把弹床甩飞")]
    public float maxPredictSpeedPx = 3500f;

    [Tooltip("自适应平滑：单帧指尖位移速度超过该值（屏幕像素/秒）时几乎不再平滑，让快甩跟手；0 = 关闭自适应，始终用 smoothingAlpha")]
    public float fastMotionSpeedPx = 2000f;

    // EMA 缓存
    private Vector2 _smoothedPip;
    private Vector2 _smoothedTip;
    private Vector2 _smoothedHit;
    private bool _emaWarmedUp; // 第一帧直接用原始值，不做平滑

    // 方向短帧复用
    private Vector2? _cachedDir;

    // —— 指尖状态机（双手 / 单手 / 失效）与丢帧宽限 ——
    private const int TipModeNone = 0;
    private const int TipModeSingle = 2;
    private const int TipModeTwo = 4;
    private int _tipMode = TipModeNone;           // 当前生效的模式
    private float _lastTwoSampleTime = -1f;       // 上一次"真正拿到双手指尖"的时间
    private float _lastOneSampleTime = -1f;       // 上一次"真正拿到单手指尖"的时间
    private bool _lastFreshHadTwo;                // 最近一帧新结果是否给出了双手
    private bool _lastFreshHadHands;              // 最近一帧新结果是否至少给出了手
    private Vector2 _tipVelA, _tipVelB, _tipVelS; // 指尖速度（像素/秒），仅用于宽限期外推
    private Vector2 _pipVelA, _pipVelB;           // PIP 速度（像素/秒）
    private bool _pipSmSInit;
    private bool _holdLogged;
    private float _lastHoldLogTime = -10f;

    // 结果版本号（反射自 HandLandmarkerRunner.ResultVersion）
    private PropertyInfo _runnerVersionProp;
    private int _lastResultVersion = int.MinValue;

    [Header("双指屋檐模式（乱码雨用）")]
    [Tooltip("关闭后不再显示食指调试射线（乱码雨模式下由 LetterAlphabetBar 自动关掉）")]
    public bool rayVisualEnabled = true;

    /// <summary>是否同时检测到两根食指尖（需要 Runner NumHands=2）</summary>
    public bool HasTwoTips { get; private set; }
    /// <summary>是否只检测到一根食指尖（单手屋檐模式用）</summary>
    public bool HasOneTip { get; private set; }
    /// <summary>单手时的食指尖（屏幕坐标，y 向上）</summary>
    public Vector2 TipScreenSingle { get; private set; }
    /// <summary>左侧食指尖（屏幕坐标，y 向上）</summary>
    public Vector2 TipScreenA { get; private set; }
    /// <summary>右侧食指尖（屏幕坐标，y 向上）</summary>
    public Vector2 TipScreenB { get; private set; }

    /// <summary>左侧食指第二关节 PIP（屏幕坐标）——B1 弹力带用：PIP→TIP 是手指板面</summary>
    public Vector2 PipScreenA { get; private set; }
    /// <summary>右侧食指第二关节 PIP（屏幕坐标）</summary>
    public Vector2 PipScreenB { get; private set; }
    /// <summary>单手时的 PIP（屏幕坐标）</summary>
    public Vector2 PipScreenSingle { get; private set; }

    // The porch uses the same mirrored screen coordinates as the letter slingshot.
    internal bool TrackPorchPinch { get; set; }
    private int pinchFrame = -10;
    private bool pinched;
    private Vector2 pinchTip;
    internal bool TryGetPorchPointer(out Vector2 tip, out bool grip)
    {
        tip = pinchTip;
        grip = pinched;
        return isActiveAndEnabled && Time.frameCount - pinchFrame <= 1;
    }

    private Vector2 _tipSmA, _tipSmB, _tipSmS;
    private Vector2 _pipSmA, _pipSmB, _pipSmS;
    private bool _tipSmInit, _tipSmSInit;

    // 食指 landmark 索引
    private const int IndexPip = 6;
    private const int IndexTip = 8;

    // 缓存的反射字段
    private FieldInfo _controllerCurrentTargetField;
    private FieldInfo _runnerAnnotationField;
    private bool _reflectionInitialized;

    // 状态追踪（用来做"状态变化才打印 log"）
    private enum FrameStatus { Booting, Ok, LostHand, NoRightHand, ReflectFail, NoAnnotation, NoRunner }
    private FrameStatus _lastStatus = FrameStatus.Booting;

    // 当前帧状态
    private bool _lastHandDetected;
    private ScreenEdge? _lastHitEdge;
    private Vector2 _lastScreenPip;
    private Vector2 _lastScreenTip;
    private Vector2 _lastScreenHitPoint;

    public bool HasHitEdge => _lastHitEdge.HasValue;
    public ScreenEdge? LastHitEdge => _lastHitEdge;
    public Vector2 LastScreenPip => _lastScreenPip;
    public Vector2 LastScreenTip => _lastScreenTip;
    public Vector2 LastScreenHitPoint => _lastScreenHitPoint;

    public enum ScreenEdge { None, Left, Right, Top, Bottom }

    private void Awake()
    {
        Debug.Log("[IndexRay] Awake() 开始...");
        TryAutoFindReferences();
        CreateLineRendererIfNeeded();

        // 启动总结
        Debug.Log($"[IndexRay] 启动完成:" +
                  $" Runner={(handLandmarkerRunner != null ? handLandmarkerRunner.GetType().Name + " ✓" : "null ✗")}, " +
                  $" Controller={(annotationController != null ? annotationController.GetType().Name + " ✓" : "null ✗")}, " +
                  $" isMirrored={isMirrored}, " +
                  $" LineRenderer={(lineRenderer != null ? lineRenderer.gameObject.name + " ✓" : "null ✗")}, " +
                  $" Screen={Screen.width}x{Screen.height}");
    }

    private void CreateLineRendererIfNeeded()
    {
        if (lineRenderer != null) return;

        var lrGo = new GameObject("AutoDebugRayLine");
        lrGo.transform.SetParent(transform, false);
        lineRenderer = lrGo.AddComponent<LineRenderer>();
        lineRenderer.useWorldSpace = true;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        lineRenderer.positionCount = 2;
        lineRenderer.enabled = false;
        lineRenderer.sortingOrder = 32000; // 尽量靠前，不被 UI 挡住

        // 尝试选一个能在场景里看到的 Shader
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Legacy Shaders/Sprites/Default");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
        {
            Debug.LogWarning("[IndexRay] 没找到可用的 LineRenderer shader，线会渲染成粉色。可在 Inspector 手动换一个。");
            shader = Shader.Find("Diffuse");
        }

        var mat = new Material(shader);
        // 默认渐变色：食指→亮黄，屏幕端→淡黄
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.7f, 0.1f, 1f), 0f), new GradientColorKey(new Color(1f, 0.4f, 0.1f, 0.6f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 1f) }
        );
        lineRenderer.colorGradient = grad;
        lineRenderer.material = mat;
    }

    private void TryAutoFindReferences()
    {
        if (handLandmarkerRunner == null)
        {
            var allBehaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (var m in allBehaviours)
            {
                if (m.GetType().Name == "HandLandmarkerRunner")
                {
                    handLandmarkerRunner = m;
                    Debug.Log($"[IndexRay] 自动找到 Runner: {m.name} ({m.GetType().FullName})");
                    break;
                }
            }
            if (handLandmarkerRunner == null)
                Debug.LogWarning("[IndexRay] 没找到 HandLandmarkerRunner！请检查 TemplateA 场景的 Solution 物体是否还在。");
        }

        if (annotationController == null && handLandmarkerRunner != null)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var field = handLandmarkerRunner.GetType().GetField("_handLandmarkerResultAnnotationController", flags);
            if (field != null)
            {
                annotationController = field.GetValue(handLandmarkerRunner) as MonoBehaviour;
                Debug.Log($"[IndexRay] 从 Runner 反射拿到 Controller: {(annotationController != null ? annotationController.GetType().Name : "null")}");
            }
            else
            {
                Debug.LogWarning($"[IndexRay] Runner 上找不到字段 _handLandmarkerResultAnnotationController！Runner 类型={handLandmarkerRunner.GetType().FullName}");
            }
        }

        if (handLandmarkerRunner != null && _runnerVersionProp == null)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _runnerVersionProp = handLandmarkerRunner.GetType().GetProperty("ResultVersion", flags);
            if (_runnerVersionProp == null)
                Debug.LogWarning("[IndexRay] Runner 上没有 ResultVersion，丢帧宽限将退化为逐帧刷新（旧行为）");
        }

        if (annotationController == null)
        {
            var allBehaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (var m in allBehaviours)
            {
                if (m.GetType().Name == "HandLandmarkerResultAnnotationController")
                {
                    annotationController = m;
                    Debug.Log($"[IndexRay] 全局扫描找到 Controller: {m.name}");
                    break;
                }
            }
            if (annotationController == null)
                Debug.LogWarning("[IndexRay] 全局也没找到 HandLandmarkerResultAnnotationController！");
        }

        if (annotationController != null)
        {
            var binding = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var prop = annotationController.GetType().GetProperty("isMirrored", binding);
            if (prop != null)
            {
                var mirrored = (bool)prop.GetValue(annotationController);
                if (mirrored != isMirrored)
                {
                    Debug.Log($"[IndexRay] 同步 isMirrored: {isMirrored} → {mirrored}");
                    isMirrored = mirrored;
                }
            }
            else
            {
                Debug.LogWarning("[IndexRay] Controller 上找不到 isMirrored property");
            }
        }
    }

    private void InitializeReflection()
    {
        if (_reflectionInitialized) return;
        if (annotationController == null) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = annotationController.GetType().GetField("_currentTarget", flags);
        if (field != null)
        {
            _controllerCurrentTargetField = field;
            _reflectionInitialized = true;
            Debug.Log($"[IndexRay] 反射链 OK — Controller={annotationController.GetType().FullName}, _currentTarget field 已拿到");
        }
        else
        {
            Debug.LogError($"[IndexRay] 反射失败！Controller 类型={annotationController.GetType().FullName}, 字段列表:");
            foreach (var f in annotationController.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Debug.Log($"  - {f.Name} : {f.FieldType.Name}");
            foreach (var p in annotationController.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Debug.Log($"  - prop {p.Name} : {p.PropertyType.Name}");
        }
    }

    private void LateUpdate()
    {
        float now = Time.unscaledTime;

        // 先按"距上一次真正看见手过了多久"决定弹床/指尖是否还有效（丢帧宽限）。
        // 必须在读新结果之前刷新：没有新结果时它负责把上一位姿顶住，而不是立刻失效。
        RefreshTipHold(now);

        if (annotationController == null)
        {
            SetStatusOnce(FrameStatus.NoAnnotation, "Controller 未找到，整个系统不运行。检查 Console 前面的警告。");
            return;
        }

        InitializeReflection();
        if (!_reflectionInitialized)
        {
            SetStatusOnce(FrameStatus.ReflectFail, "反射链断裂，拿不到 _currentTarget。见 Console。");
            return;
        }

        // 只有 Runner 收到新推理结果时版本号才会变；同一份结果被逐帧重复读到时不重复推滤波
        bool fresh = IsFreshResult();

        var raw = _controllerCurrentTargetField.GetValue(annotationController);
        if (raw == null)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.LostHand, "_currentTarget 为 null — HandLandmarker 可能还没启动或没有手");
            return;
        }

        // raw 是 HandLandmarkerResult（readonly struct），通过装箱后用反射读其字段
        var resultType = raw.GetType();
        var handednessField = resultType.GetField("handedness", BindingFlags.Instance | BindingFlags.Public);
        var handLandmarksField = resultType.GetField("handLandmarks", BindingFlags.Instance | BindingFlags.Public);

        if (handednessField == null || handLandmarksField == null)
        {
            SetStatusOnce(FrameStatus.ReflectFail, $"HandLandmarkerResult 上找不到 handedness/handLandmarks 字段！resultType={resultType.FullName}");
            return;
        }

        var handednessList = handednessField.GetValue(raw) as System.Collections.IList;
        var landmarksList = handLandmarksField.GetValue(raw) as System.Collections.IList;

        if (handednessList == null || landmarksList == null)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.LostHand, $"handednessList={handednessList?.Count ?? -1}, landmarksList={landmarksList?.Count ?? -1}");
            return;
        }

        // 第一次命中时 dump handedness 完整内容（包括 Count 和每只手的完整 Classifications）
        if (_lastHandednessDump < 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"handedness.Count={handednessList.Count}, landmarks.Count={landmarksList.Count}");
            for (int i = 0; i < handednessList.Count; i++)
            {
                var cls = handednessList[i];
                sb.AppendLine($"  [handedness[{i}] type={cls.GetType().FullName}]");
                foreach (var f in cls.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    sb.AppendLine($"    field {f.Name} : {f.FieldType.Name} = {f.GetValue(cls)}");
                foreach (var p in cls.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    string val = "?";
                    try { val = p.GetValue(cls)?.ToString() ?? "null"; } catch { val = "<异常>"; }
                    sb.AppendLine($"    prop  {p.Name} : {p.PropertyType.Name} = {val}");
                }
                var catProp = cls.GetType().GetProperty("categories");
                if (catProp != null)
                {
                    var cats = catProp.GetValue(cls) as System.Collections.IList;
                    if (cats != null)
                    {
                        sb.AppendLine($"    categories.Count = {cats.Count}");
                        for (int j = 0; j < cats.Count; j++)
                        {
                            var cat = cats[j];
                            foreach (var f2 in cat.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                                sb.AppendLine($"      [{j}].{f2.Name} = {f2.GetValue(cat)}");
                            foreach (var p2 in cat.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                            {
                                string v2 = "?"; try { v2 = p2.GetValue(cat)?.ToString() ?? "null"; } catch { }
                                sb.AppendLine($"      [{j}].{p2.Name} = {v2}");
                            }
                        }
                    }
                    else sb.AppendLine($"    categories = null (IList cast 失败)");
                }
            }
            Debug.Log($"[IndexRay] 🔍 handedness 完整 dump:\n{sb}");
            _lastHandednessDump = 0; // dump 过了就不再 dump
        }

        // 取一只手：优先 Right label → 否则任一只 label 非空的手 → 否则 index 0
        int chosenHandIndex = -1;
        for (int i = 0; i < landmarksList.Count; i++)
        {
            string label = null;
            if (i < handednessList.Count)
            {
                var cls = handednessList[i];
                var catProp = cls.GetType().GetProperty("categories", BindingFlags.Instance | BindingFlags.Public);
                if (catProp != null)
                {
                    var cats = catProp.GetValue(cls) as System.Collections.IList;
                    if (cats != null && cats.Count > 0)
                    {
                        var cat0 = cats[0];
                        var nameProp = cat0.GetType().GetProperty("categoryName", BindingFlags.Instance | BindingFlags.Public);
                        if (nameProp != null)
                        {
                            label = (string)nameProp.GetValue(cat0);
                        }
                    }
                }
            }

            // 1st choice: label 是 Right
            if (label == "Right") { chosenHandIndex = i; break; }
            // 2nd choice: 找一只 label 非空的手（MediaPipe 有时返回 "Left"）
            if (chosenHandIndex < 0 && !string.IsNullOrEmpty(label)) chosenHandIndex = i;
        }

        // 3rd choice: handedness 彻底没返回有效标签 → 直接用 index 0
        if (chosenHandIndex < 0 && landmarksList.Count > 0)
        {
            chosenHandIndex = 0;
            if (_lastHandednessFallback != chosenHandIndex)
            {
                Debug.LogWarning($"[IndexRay] handedness 没有效 Right label，兜底用 landmarks index=0");
                _lastHandednessFallback = chosenHandIndex;
            }
        }

        if (chosenHandIndex < 0)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.NoRightHand, $"landmarksList.Count={landmarksList.Count}，连 index 0 都取不到");
            return;
        }

        _lastHandDetected = true;

        // 双食指尖提取（乱码雨模式用；手序帧间可能交换 → 内部按 x 排序）
        UpdateTwoTips(landmarksList, fresh, now);

        // landmarksList[chosenHandIndex] 是 NormalizedLandmarks struct
        // 同样存在 SDK 缓冲竞争风险，越界就当丢手帧跳过
        object normalizedLandmarksStruct;
        try
        {
            normalizedLandmarksStruct = landmarksList[chosenHandIndex];
        }
        catch (System.ArgumentOutOfRangeException)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.LostHand, $"landmarksList 越界 Count={landmarksList.Count} index={chosenHandIndex}（SDK 缓冲竞争，跳过本帧）");
            return;
        }
        var nlType = normalizedLandmarksStruct.GetType();
        var innerLandmarksField = nlType.GetField("landmarks", BindingFlags.Instance | BindingFlags.Public);
        if (innerLandmarksField == null)
        {
            SetStatusOnce(FrameStatus.ReflectFail, $"NormalizedLandmarks 上找不到 landmarks 字段！type={nlType.FullName}");
            return;
        }
        var landmarkList = innerLandmarksField.GetValue(normalizedLandmarksStruct) as System.Collections.IList;
        if (landmarkList == null || landmarkList.Count < 9)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.LostHand, $"landmarkList 数量不够: {landmarkList?.Count ?? -1}");
            return;
        }

        // 取 PIP 和 TIP
        // 注意：landmarkList 是 SDK 内部共享引用，检测线程可能在 Count 检查后替换缓冲，偶发越界 → 当丢手帧跳过
        object pipNl, tipNl;
        try
        {
            pipNl = landmarkList[IndexPip];
            tipNl = landmarkList[IndexTip];
        }
        catch (System.ArgumentOutOfRangeException)
        {
            MarkNoHandResult(fresh);
            SetStatusOnce(FrameStatus.LostHand, $"landmarkList 越界 Count={landmarkList.Count}（SDK 缓冲竞争，跳过本帧）");
            return;
        }

        var nlType2 = pipNl.GetType();
        var xField = nlType2.GetField("x", BindingFlags.Instance | BindingFlags.Public);
        var yField = nlType2.GetField("y", BindingFlags.Instance | BindingFlags.Public);
        if (xField == null || yField == null)
        {
            SetStatusOnce(FrameStatus.ReflectFail, $"NormalizedLandmark 上找不到 x/y 字段！type={nlType2.FullName}");
            return;
        }

        float pipX = (float)xField.GetValue(pipNl);
        float pipY = (float)yField.GetValue(pipNl);
        float tipX = (float)xField.GetValue(tipNl);
        float tipY = (float)yField.GetValue(tipNl);

        if (logEveryFrame)
        {
            Debug.Log($"[IndexRay] 手 #{chosenHandIndex}  PIP(norm)=({pipX:F3},{pipY:F3}) TIP(norm)=({tipX:F3},{tipY:F3})  isMirrored={isMirrored}");
        }

        if (isMirrored)
        {
            pipX = 1f - pipX;
            tipX = 1f - tipX;
        }

        _lastScreenPip = new Vector2(pipX * Screen.width, (1f - pipY) * Screen.height);
        _lastScreenTip = new Vector2(tipX * Screen.width, (1f - tipY) * Screen.height);

        if (TrackPorchPinch &&
            TryGetLandmarkScreen(landmarksList, chosenHandIndex, 4, out var thumb) &&
            TryGetLandmarkScreen(landmarksList, chosenHandIndex, 0, out var wrist) &&
            TryGetLandmarkScreen(landmarksList, chosenHandIndex, 9, out var knuckle))
        {
            float palm = Vector2.Distance(wrist, knuckle);
            if (palm > 5f)
            {
                bool grip = Vector2.Distance(thumb, _lastScreenTip) < palm * (pinched ? .52f : .34f);
                // Judge pickup and release at the observed fingertip, even after a fast drag.
                float blend = 1f - Mathf.Exp(-32f * Time.unscaledDeltaTime);
                pinchTip = grip != pinched || Time.frameCount - pinchFrame > 1
                    ? _lastScreenTip : Vector2.Lerp(pinchTip, _lastScreenTip, blend);
                pinched = grip;
                pinchFrame = Time.frameCount;
            }
        }
        // —— EMA 平滑 ——
        Vector2 rawPip = _lastScreenPip;
        Vector2 rawTip = _lastScreenTip;

        if (!_emaWarmedUp)
        {
            // 第一帧直接用原始值初始化，跳过平滑
            _smoothedPip = rawPip;
            _smoothedTip = rawTip;
            _smoothedHit = Vector2.zero;
            _emaWarmedUp = true;
        }
        else
        {
            float a = smoothingAlpha;
            _smoothedPip = Vector2.Lerp(_smoothedPip, rawPip, a);
            _smoothedTip = Vector2.Lerp(_smoothedTip, rawTip, a);
        }

        // 后面全部用平滑后的坐标
        _lastScreenPip = _smoothedPip;
        _lastScreenTip = _smoothedTip;

        Vector2 dir = (_lastScreenTip - _lastScreenPip);
        float len = dir.magnitude;
        Vector2 endPoint;

        // 方向太短 → 沿用上一帧方向（如果有缓存），防止伸直时抖动
        if (len < minDirectionLengthPx && _cachedDir.HasValue)
        {
            // 复用上次方向，PIP 还是用当前平滑后的位置（起点跟着手走）
            endPoint = _lastScreenPip + _cachedDir.Value.normalized * maxScreenRayLength;
        }
        else
        {
            if (len < 1f)
            {
                SetStatusOnce(FrameStatus.Ok, $"方向太短 len={len:F1} < 1px，静止帧跳过");
                return;
            }
            Vector2 dirNorm = dir / Mathf.Max(len, 0.001f);
            endPoint = _lastScreenPip + dirNorm * maxScreenRayLength;
            _cachedDir = dirNorm;
        }

        // 命中点计算 + 命中点 EMA 平滑
        var hit = ComputeFinalHit(_lastScreenPip, endPoint);
        _lastScreenHitPoint = hit.point;

        // 状态变化才 log
        if (hit.edge != _lastHitEdge)
        {
            var edgeName = hit.edge == ScreenEdge.None ? "None(射线指向屏幕外)" : hit.edge.ToString();
           //Debug.Log($"[IndexRay] ✅ 命中 {edgeName}  edge  screenHit=({hit.point.x:F0},{hit.point.y:F0})  PIP=({_lastScreenPip.x:F0},{_lastScreenPip.y:F0})  TIP=({_lastScreenTip.x:F0},{_lastScreenTip.y:F0})");
        }
        _lastHitEdge = hit.edge;
        _lastStatus = FrameStatus.Ok;

        // 事件广播
        switch (hit.edge)
        {
            case ScreenEdge.Left:   onLeftEdgeHit?.Invoke();   break;
            case ScreenEdge.Right:  onRightEdgeHit?.Invoke();  break;
            case ScreenEdge.Top:    onTopEdgeHit?.Invoke();    break;
            case ScreenEdge.Bottom: onBottomEdgeHit?.Invoke(); break;
            default:                onNoHit?.Invoke();         break;
        }

        UpdateLineRenderer3D(_lastScreenPip, _lastScreenHitPoint);
    }

    // 上一次打印 handedness 数量（避免刷屏）
    private int _lastHandednessDump = -1;
    private int _lastHandednessFallback = -1;

    /// <summary>提取两只手的食指尖（landmark 8），按屏幕 x 排序后 EMA 平滑，写入 TipScreenA/B。
    /// 同时成对提取 PIP（landmark 6，跟随同一只手），写入 PipScreenA/B（B1 弹力带用）。
    /// PIP 读取失败只影响 PIP 输出（保留旧值），不影响指尖判定。
    /// 只在"新的一帧推理结果"上推进滤波与速度；同一份结果的重复读取直接跳过，
    /// 这样丢帧宽限期（RefreshTipHold）才能按真实推理间隔计时。</summary>
    private void UpdateTwoTips(System.Collections.IList landmarksList, bool fresh, float now)
    {
        if (!fresh) return;

        if (landmarksList.Count >= 2
            && TryGetTipScreen(landmarksList, 0, out Vector2 t0)
            && TryGetTipScreen(landmarksList, 1, out Vector2 t1))
        {
            // 手序帧间可能交换 → 按 x 排序，A 左 B 右；PIP 与指尖取自同一只手再排序
            TryGetLandmarkScreen(landmarksList, 0, IndexPip, out Vector2 p0raw);
            TryGetLandmarkScreen(landmarksList, 1, IndexPip, out Vector2 p1raw);
            Vector2 a, b, pa, pb;
            if (t0.x <= t1.x) { a = t0; b = t1; pa = p0raw; pb = p1raw; }
            else              { a = t1; b = t0; pa = p1raw; pb = p0raw; }

            // 模式切换（单手→双手 / 失效→双手）时重新吸附，避免从旧位置慢慢爬
            if (_tipMode != TipModeTwo)
            {
                _tipSmInit = false;
                _lastTwoSampleTime = -1f;
                _tipVelA = _tipVelB = _pipVelA = _pipVelB = Vector2.zero;
            }

            float dt = _lastTwoSampleTime >= 0f ? now - _lastTwoSampleTime : 0f;
            bool warm = _tipSmInit && dt > 0.001f;
            float alpha = warm
                ? AdaptiveAlpha((a - _tipSmA).magnitude / dt, (b - _tipSmB).magnitude / dt)
                : 1f;

            if (!warm)
            {
                _tipSmA = a; _tipSmB = b;
                _pipSmA = pa; _pipSmB = pb;   // 首帧 PIP 无效时暂用指尖，后续帧会收敛
            }
            else
            {
                Vector2 na = Vector2.Lerp(_tipSmA, a, alpha);
                Vector2 nb = Vector2.Lerp(_tipSmB, b, alpha);
                Vector2 npa = Vector2.Lerp(_pipSmA, pa, alpha);
                Vector2 npb = Vector2.Lerp(_pipSmB, pb, alpha);

                // 速度按"两次真实采样"的间隔算，只用于宽限期外推
                _tipVelA = Vector2.Lerp(_tipVelA, (na - _tipSmA) / dt, 0.5f);
                _tipVelB = Vector2.Lerp(_tipVelB, (nb - _tipSmB) / dt, 0.5f);
                _pipVelA = Vector2.Lerp(_pipVelA, (npa - _pipSmA) / dt, 0.5f);
                _pipVelB = Vector2.Lerp(_pipVelB, (npb - _pipSmB) / dt, 0.5f);

                _tipSmA = na; _tipSmB = nb;
                _pipSmA = npa; _pipSmB = npb;
            }

            _tipSmInit = true;
            _tipMode = TipModeTwo;
            _lastTwoSampleTime = now;
            _lastOneSampleTime = -1f;
            _lastFreshHadTwo = true;
            _lastFreshHadHands = true;

            TipScreenA = _tipSmA;
            TipScreenB = _tipSmB;
            PipScreenA = _pipSmA;
            PipScreenB = _pipSmB;
            HasTwoTips = true;
            HasOneTip = false;
        }
        else if (landmarksList.Count >= 1 && TryGetTipScreen(landmarksList, 0, out Vector2 ts))
        {
            // 刚少了一只手：先由宽限期顶住双手弹床，不立刻退化成单手（否则弹床会瞬间断成一半）
            if (_tipMode == TipModeTwo && now - _lastTwoSampleTime < trackingGraceSeconds)
            {
                _lastFreshHadTwo = false;
                _lastFreshHadHands = true;
                return;
            }

            // 单手：暴露单指尖，屋檐退化为"指尖到最近屏幕边缘"的缝隙
            if (_tipMode != TipModeSingle)
            {
                _tipSmSInit = false;
                _pipSmSInit = false;
                _lastOneSampleTime = -1f;
                _tipVelS = Vector2.zero;
            }

            float dt = _lastOneSampleTime >= 0f ? now - _lastOneSampleTime : 0f;
            bool warmS = _tipSmSInit && dt > 0.001f;
            float alphaS = warmS ? AdaptiveAlpha((ts - _tipSmS).magnitude / dt) : 1f;

            if (!warmS) { _tipSmS = ts; _tipVelS = Vector2.zero; }
            else
            {
                Vector2 ns = Vector2.Lerp(_tipSmS, ts, alphaS);
                _tipVelS = Vector2.Lerp(_tipVelS, (ns - _tipSmS) / dt, 0.5f);
                _tipSmS = ns;
            }
            _tipSmSInit = true;

            if (TryGetLandmarkScreen(landmarksList, 0, IndexPip, out Vector2 ps))
            {
                if (!_pipSmSInit) { _pipSmS = ps; _pipSmSInit = true; }
                else _pipSmS = Vector2.Lerp(_pipSmS, ps, alphaS);
            }
            else if (!_pipSmSInit) { _pipSmS = ts; _pipSmSInit = true; }

            _tipMode = TipModeSingle;
            _lastOneSampleTime = now;
            _lastFreshHadTwo = false;
            _lastFreshHadHands = true;

            TipScreenSingle = _tipSmS;
            PipScreenSingle = _pipSmS;
            HasTwoTips = false;
            HasOneTip = true;
        }
        else
        {
            // 这一帧拿到了手但指尖读不出来（SDK 缓冲竞争）：交给宽限期顶住，不立刻清空
            _lastFreshHadTwo = false;
            _lastFreshHadHands = true;
        }
    }

    /// <summary>是否拿到了一帧"新的"推理结果。Runner 没有 ResultVersion（旧版）时退化为每帧都算新。</summary>
    private bool IsFreshResult()
    {
        if (_runnerVersionProp == null) return true;
        int v;
        try { v = (int)_runnerVersionProp.GetValue(handLandmarkerRunner); }
        catch { return true; }
        if (v == _lastResultVersion) return false;
        _lastResultVersion = v;
        return true;
    }

    /// <summary>这一帧的新结果里没有可用的手：只记录状态，是否失效交给宽限期判断。</summary>
    private void MarkNoHandResult(bool fresh)
    {
        if (!fresh) return;
        _lastFreshHadTwo = false;
        _lastFreshHadHands = false;
        // 宽限关闭时立刻失效，保持旧行为
        if (trackingGraceSeconds <= 0f) _tipMode = TipModeNone;
    }

    /// <summary>按指尖位移速度自动调整 EMA 系数：慢速强平滑抗抖，快速几乎不平滑以跟手。</summary>
    private float AdaptiveAlpha(float speedPx)
    {
        if (fastMotionSpeedPx <= 0f) return smoothingAlpha;
        return Mathf.Lerp(smoothingAlpha, 0.9f, Mathf.Clamp01(speedPx / fastMotionSpeedPx));
    }

    private float AdaptiveAlpha(float speedPxA, float speedPxB)
        => AdaptiveAlpha(Mathf.Max(speedPxA, speedPxB));

    /// <summary>每帧刷新指尖有效性：
    /// 距最后一次真正看见手不超过 trackingGraceSeconds 就保持住（必要时按速度外推），
    /// 超过才真正失效。这样快速挥动时漏检一两帧不会让弹床闪断、速度历史被清空。</summary>
    private void RefreshTipHold(float now)
    {
        if (trackingGraceSeconds > 0f)
        {
            if (_tipMode == TipModeTwo && now - _lastTwoSampleTime > trackingGraceSeconds) _tipMode = TipModeNone;
            if (_tipMode == TipModeSingle && now - _lastOneSampleTime > trackingGraceSeconds) _tipMode = TipModeNone;
        }

        if (_tipMode == TipModeNone)
        {
            if (HasTwoTips || HasOneTip || _tipSmInit || _tipSmSInit)
            {
                HasTwoTips = false;
                HasOneTip = false;
                _tipSmInit = false;
                _tipSmSInit = false;
                _pipSmSInit = false;
                _tipVelA = _tipVelB = _tipVelS = Vector2.zero;
                _pipVelA = _pipVelB = Vector2.zero;
            }
            return;
        }

        float age = _tipMode == TipModeTwo ? now - _lastTwoSampleTime : now - _lastOneSampleTime;
        age = Mathf.Clamp(age, 0f, Mathf.Max(0f, trackingGraceSeconds));

        bool holding = _tipMode == TipModeTwo ? !_lastFreshHadTwo : !_lastFreshHadHands;
        bool extrapolate = holding && predictDuringGrace && age > 0f;

        if (!holding) _holdLogged = false;
        else if (extrapolate && !_holdLogged && now - _lastHoldLogTime > 1f)
        {
            _holdLogged = true;
            _lastHoldLogTime = now;
            Debug.Log($"[IndexRay] 手部追踪短暂丢失，指尖保持/外推 {age * 1000f:F0} ms（宽限上限 {trackingGraceSeconds * 1000f:F0} ms）");
        }

        if (_tipMode == TipModeTwo)
        {
            HasTwoTips = true;
            HasOneTip = false;
            if (extrapolate)
            {
                TipScreenA = _tipSmA + PredictOffset(_tipVelA, age);
                TipScreenB = _tipSmB + PredictOffset(_tipVelB, age);
                PipScreenA = _pipSmA + PredictOffset(_pipVelA, age);
                PipScreenB = _pipSmB + PredictOffset(_pipVelB, age);
            }
            else
            {
                TipScreenA = _tipSmA;
                TipScreenB = _tipSmB;
                PipScreenA = _pipSmA;
                PipScreenB = _pipSmB;
            }
        }
        else
        {
            HasTwoTips = false;
            HasOneTip = true;
            TipScreenSingle = extrapolate ? _tipSmS + PredictOffset(_tipVelS, age) : _tipSmS;
            PipScreenSingle = _pipSmS;
        }
    }

    private Vector2 PredictOffset(Vector2 velocityPxPerSec, float age)
        => Vector2.ClampMagnitude(velocityPxPerSec, Mathf.Max(0f, maxPredictSpeedPx)) * age;

    /// <summary>从 landmarksList[handIndex] 取任意 landmark（屏幕坐标，含镜像）。失败返回 false（screenPos=default）</summary>
    private bool TryGetLandmarkScreen(System.Collections.IList landmarksList, int handIndex, int landmarkIndex, out Vector2 screenPos)
    {
        screenPos = default;
        try
        {
            var nls = landmarksList[handIndex];
            var f = nls.GetType().GetField("landmarks", BindingFlags.Instance | BindingFlags.Public);
            if (f == null) return false;
            if (f.GetValue(nls) is not System.Collections.IList ll || ll.Count <= landmarkIndex) return false;
            var lm = ll[landmarkIndex];
            var t = lm.GetType();
            var xf = t.GetField("x", BindingFlags.Instance | BindingFlags.Public);
            var yf = t.GetField("y", BindingFlags.Instance | BindingFlags.Public);
            if (xf == null || yf == null) return false;

            float nx = (float)xf.GetValue(lm);
            float ny = (float)yf.GetValue(lm);
            if (isMirrored) nx = 1f - nx;
            screenPos = new Vector2(nx * Screen.width, (1f - ny) * Screen.height);
            return true;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return false; // SDK 缓冲竞争，跳过本帧
        }
    }

    /// <summary>从 landmarksList[handIndex] 反射取 TIP(8)，转成屏幕坐标。任何一步失败返回 false</summary>
    private bool TryGetTipScreen(System.Collections.IList landmarksList, int handIndex, out Vector2 screenPos)
    {
        screenPos = default;
        try
        {
            var nls = landmarksList[handIndex];
            var f = nls.GetType().GetField("landmarks", BindingFlags.Instance | BindingFlags.Public);
            if (f == null) return false;
            if (f.GetValue(nls) is not System.Collections.IList ll || ll.Count <= IndexTip) return false;
            var tip = ll[IndexTip];
            var t = tip.GetType();
            var xf = t.GetField("x", BindingFlags.Instance | BindingFlags.Public);
            var yf = t.GetField("y", BindingFlags.Instance | BindingFlags.Public);
            if (xf == null || yf == null) return false;

            float nx = (float)xf.GetValue(tip);
            float ny = (float)yf.GetValue(tip);
            if (isMirrored) nx = 1f - nx;
            screenPos = new Vector2(nx * Screen.width, (1f - ny) * Screen.height);
            return true;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return false; // SDK 缓冲竞争，跳过本帧
        }
    }

    private void SetStatusOnce(FrameStatus s, string msg)
    {
        if (_lastStatus == s) return;
        _lastStatus = s;
        Debug.LogWarning($"[IndexRay] ⚠ 状态={s} — {msg}");

        // 丢手或初始化失败 → 清空 EMA/方向缓存，下次手回来从头开始。
        // 指尖/弹床状态不在这里清：改由 RefreshTipHold 按 trackingGraceSeconds 判定，
        // 否则快速挥动时漏检一帧就会把弹床和速度历史一起打断。
        if (s == FrameStatus.LostHand || s == FrameStatus.NoRightHand)
        {
            _emaWarmedUp = false;
            _cachedDir = null;
            _lastHandDetected = false;
            _lastHitEdge = null;
            UpdateLineRenderer3D(Vector2.zero, Vector2.zero, visible: false);
        }
        else
        {
            // 其他状态只是隐藏 LineRenderer
            UpdateLineRenderer3D(Vector2.zero, Vector2.zero, visible: false);
        }
    }

    /// <summary>
    /// 计算射线 p1→p2 与屏幕四边的第一个交点。
    /// 返回命中的边和交点；若没命中返回 None。
    /// </summary>
    private static (ScreenEdge edge, Vector2 point) ComputeRayScreenIntersection(Vector2 p1, Vector2 p2)
    {
        // 屏幕边界
        float L = 0f;
        float R = Screen.width;
        float T = Screen.height;
        float B = 0f;

        Vector2 dir = p2 - p1;

        ScreenEdge bestEdge = ScreenEdge.None;
        Vector2 bestPoint = default;
        float bestT = float.MaxValue;

        // 与左边 x=L
        if (Mathf.Abs(dir.x) > 1e-6f)
        {
            float t = (L - p1.x) / dir.x;
            if (t >= 0f)
            {
                float y = p1.y + t * dir.y;
                if (y >= B - 0.5f && y <= T + 0.5f && t < bestT)
                {
                    bestT = t;
                    bestEdge = ScreenEdge.Left;
                    bestPoint = new Vector2(L, y);
                }
            }
        }

        // 与右边 x=R
        if (Mathf.Abs(dir.x) > 1e-6f)
        {
            float t = (R - p1.x) / dir.x;
            if (t >= 0f)
            {
                float y = p1.y + t * dir.y;
                if (y >= B - 0.5f && y <= T + 0.5f && t < bestT)
                {
                    bestT = t;
                    bestEdge = ScreenEdge.Right;
                    bestPoint = new Vector2(R, y);
                }
            }
        }

        // 与下边 y=B
        if (Mathf.Abs(dir.y) > 1e-6f)
        {
            float t = (B - p1.y) / dir.y;
            if (t >= 0f)
            {
                float x = p1.x + t * dir.x;
                if (x >= L - 0.5f && x <= R + 0.5f && t < bestT)
                {
                    bestT = t;
                    bestEdge = ScreenEdge.Bottom;
                    bestPoint = new Vector2(x, B);
                }
            }
        }

        // 与上边 y=T
        if (Mathf.Abs(dir.y) > 1e-6f)
        {
            float t = (T - p1.y) / dir.y;
            if (t >= 0f)
            {
                float x = p1.x + t * dir.x;
                if (x >= L - 0.5f && x <= R + 0.5f && t < bestT)
                {
                    bestT = t;
                    bestEdge = ScreenEdge.Top;
                    bestPoint = new Vector2(x, T);
                }
            }
        }

        // 如果射线起点已经在屏幕外，需要回退到 p2（延长后的方向）再次检测
        // 上面已经覆盖了起点在边缘内侧或外侧的情况
        return (bestEdge, bestPoint);
    }

    /// <summary>
    /// 包装 ComputeRayScreenIntersection + 命中点 EMA 平滑。
    /// 返回 (edge, smoothedPoint)。
    /// </summary>
    private (ScreenEdge edge, Vector2 point) ComputeFinalHit(Vector2 p1, Vector2 p2)
    {
        var (edge, rawPoint) = ComputeRayScreenIntersection(p1, p2);

        // EMA 平滑命中点（edge 切换时重置，避免跨边缘拖影）
        if (!_emaWarmedUp || _lastHitEdge != edge || _smoothedHit == Vector2.zero)
        {
            _smoothedHit = rawPoint;
        }
        else
        {
            _smoothedHit = Vector2.Lerp(_smoothedHit, rawPoint, hitPointAlpha);
        }

        return (edge, _smoothedHit);
    }

    /// <summary>
    /// 用 LineRenderer 在 3D 空间里画射线（从食指位置到屏幕边缘）
    /// </summary>
    private void UpdateLineRenderer3D(Vector2 screenPip, Vector2 screenHit, bool visible = true)
    {
        if (lineRenderer == null) return;

        if (!visible || !_lastHandDetected || !_lastHitEdge.HasValue || !rayVisualEnabled)
        {
            lineRenderer.enabled = false;
            return;
        }

        var cam = Camera.main;
        if (cam == null) { lineRenderer.enabled = false; return; }

        lineRenderer.enabled = true;
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;

        float zDist = 5f;
        Vector3 worldPip = cam.ScreenToWorldPoint(new Vector3(screenPip.x, screenPip.y, zDist));
        Vector3 worldHit = cam.ScreenToWorldPoint(new Vector3(screenHit.x, screenHit.y, zDist));

        lineRenderer.positionCount = 2;
        lineRenderer.SetPosition(0, worldPip);
        lineRenderer.SetPosition(1, worldHit);

        if (lineColor != null && lineColor.colorKeys.Length > 0)
        {
            lineRenderer.colorGradient = lineColor;
        }
    }
}
