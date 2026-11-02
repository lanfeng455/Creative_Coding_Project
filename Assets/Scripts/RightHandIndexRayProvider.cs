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

    // EMA 缓存
    private Vector2 _smoothedPip;
    private Vector2 _smoothedTip;
    private Vector2 _smoothedHit;
    private bool _emaWarmedUp; // 第一帧直接用原始值，不做平滑

    // 方向短帧复用
    private Vector2? _cachedDir;

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

    private Vector2 _tipSmA, _tipSmB, _tipSmS;
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

        var raw = _controllerCurrentTargetField.GetValue(annotationController);
        if (raw == null)
        {
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
            SetStatusOnce(FrameStatus.NoRightHand, $"landmarksList.Count={landmarksList.Count}，连 index 0 都取不到");
            return;
        }

        _lastHandDetected = true;

        // 双食指尖提取（乱码雨模式用；手序帧间可能交换 → 内部按 x 排序）
        UpdateTwoTips(landmarksList);

        // landmarksList[chosenHandIndex] 是 NormalizedLandmarks struct
        // 同样存在 SDK 缓冲竞争风险，越界就当丢手帧跳过
        object normalizedLandmarksStruct;
        try
        {
            normalizedLandmarksStruct = landmarksList[chosenHandIndex];
        }
        catch (System.ArgumentOutOfRangeException)
        {
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
            Debug.Log($"[IndexRay] ✅ 命中 {edgeName}  edge  screenHit=({hit.point.x:F0},{hit.point.y:F0})  PIP=({_lastScreenPip.x:F0},{_lastScreenPip.y:F0})  TIP=({_lastScreenTip.x:F0},{_lastScreenTip.y:F0})");
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

    /// <summary>提取两只手的食指尖（landmark 8），按屏幕 x 排序后 EMA 平滑，写入 TipScreenA/B</summary>
    private void UpdateTwoTips(System.Collections.IList landmarksList)
    {
        if (landmarksList.Count >= 2
            && TryGetTipScreen(landmarksList, 0, out Vector2 t0)
            && TryGetTipScreen(landmarksList, 1, out Vector2 t1))
        {
            // 手序帧间可能交换 → 按 x 排序，A 左 B 右
            Vector2 a = t0.x <= t1.x ? t0 : t1;
            Vector2 b = t0.x <= t1.x ? t1 : t0;

            if (!_tipSmInit)
            {
                _tipSmA = a; _tipSmB = b; _tipSmInit = true;
            }
            else
            {
                _tipSmA = Vector2.Lerp(_tipSmA, a, smoothingAlpha);
                _tipSmB = Vector2.Lerp(_tipSmB, b, smoothingAlpha);
            }
            TipScreenA = _tipSmA;
            TipScreenB = _tipSmB;
            HasTwoTips = true;
            HasOneTip = false;
        }
        else if (landmarksList.Count >= 1 && TryGetTipScreen(landmarksList, 0, out Vector2 ts))
        {
            // 单手：暴露单指尖，屋檐退化为"指尖到最近屏幕边缘"的缝隙
            if (!_tipSmSInit) { _tipSmS = ts; _tipSmSInit = true; }
            else _tipSmS = Vector2.Lerp(_tipSmS, ts, smoothingAlpha);
            TipScreenSingle = _tipSmS;
            HasTwoTips = false;
            HasOneTip = true;
            _tipSmInit = false;
        }
        else
        {
            HasTwoTips = false;
            HasOneTip = false;
            _tipSmInit = false;
            _tipSmSInit = false;
            // 注意：TipScreenA/B/Single 保留最后值（屋檐位置不塌），由 GlyphRain 决定怎么处理
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

        // 丢手或初始化失败 → 清空 EMA/方向/指尖缓存，下次手回来从头开始
        if (s == FrameStatus.LostHand || s == FrameStatus.NoRightHand)
        {
            _emaWarmedUp = false;
            _cachedDir = null;
            _lastHandDetected = false;
            _lastHitEdge = null;
            HasTwoTips = false;
            HasOneTip = false;
            _tipSmInit = false;
            _tipSmSInit = false;
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
