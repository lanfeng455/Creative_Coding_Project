// EdgeHighlightController —— 把 RightHandIndexRayProvider 的命中状态
// 分发给四条边缘的 EdgeHitGlow 组件
// 采用"每帧轮询 Provider 状态"的方式，比事件更稳

using UnityEngine;

[DisallowMultipleComponent]
public class EdgeHighlightController : MonoBehaviour
{
    [Header("射线提供者（可留空自动 Find）")]
    public RightHandIndexRayProvider rayProvider;

    [Header("四条边缘的 EdgeHitGlow 组件")]
    public EdgeHitGlow leftEdge;
    public EdgeHitGlow rightEdge;
    public EdgeHitGlow topEdge;
    public EdgeHitGlow bottomEdge;

    private RightHandIndexRayProvider.ScreenEdge? _lastEdge;
    private Vector2 _lastHitPoint;
    private bool _initialized;

    private void Start()
    {
        if (rayProvider == null)
        {
            rayProvider = FindObjectOfType<RightHandIndexRayProvider>();
        }
        _initialized = rayProvider != null;
        ClearAll();
    }

    private void Update()
    {
        if (!_initialized) return;

        var edge = rayProvider.LastHitEdge;
        var pt = rayProvider.LastScreenHitPoint;

        // 状态没变化就跳过（节省 SetHit 调用）
        if (edge == _lastEdge && edge.HasValue)
        {
            // edge 没变，但 hitPoint 变化了 → 更新命中点位置但重置强度
            if (Vector2.Distance(pt, _lastHitPoint) > 4f)
            {
                var glow = EdgeFor(edge.Value);
                if (glow != null)
                {
                    float norm = NormalizeHitPoint(edge.Value, pt);
                    glow.SetHit(norm);
                    _lastHitPoint = pt;
                }
            }
            return;
        }

        // edge 变了 → 清旧的，设新的
        ClearAll();

        if (edge.HasValue && edge.Value != RightHandIndexRayProvider.ScreenEdge.None)
        {
            var glow = EdgeFor(edge.Value);
            if (glow != null)
            {
                float norm = NormalizeHitPoint(edge.Value, pt);
                glow.SetHit(norm);
            }
            _lastHitPoint = pt;
        }

        _lastEdge = edge;
    }

    private EdgeHitGlow EdgeFor(RightHandIndexRayProvider.ScreenEdge edge)
    {
        return edge switch
        {
            RightHandIndexRayProvider.ScreenEdge.Left   => leftEdge,
            RightHandIndexRayProvider.ScreenEdge.Right  => rightEdge,
            RightHandIndexRayProvider.ScreenEdge.Top    => topEdge,
            RightHandIndexRayProvider.ScreenEdge.Bottom => bottomEdge,
            _ => null
        };
    }

    /// <summary>
    /// 把屏幕坐标命中点，转成该边缘方向的归一化位置 0~1
    /// </summary>
    private static float NormalizeHitPoint(RightHandIndexRayProvider.ScreenEdge edge, Vector2 hit)
    {
        // Left / Right：沿屏幕高度方向
        if (edge == RightHandIndexRayProvider.ScreenEdge.Left || edge == RightHandIndexRayProvider.ScreenEdge.Right)
        {
            return Mathf.Clamp01(hit.y / Mathf.Max(Screen.height, 1f));
        }
        // Top / Bottom：沿屏幕宽度方向
        return Mathf.Clamp01(hit.x / Mathf.Max(Screen.width, 1f));
    }

    private void ClearAll()
    {
        leftEdge?.ClearHit();
        rightEdge?.ClearHit();
        topEdge?.ClearHit();
        bottomEdge?.ClearHit();
    }
}
