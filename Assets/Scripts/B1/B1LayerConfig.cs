// B1LayerConfig.cs —— Prototype B1 双层世界配置（单一事实来源）
// 上下两层：下层=玩家操作区（字符雨/弹力带/TypeWriter），上层=恐龙生活区。
// 上层地板在爬梯通道处留缺口，物品从这里掉下去/被弹上来。

using System;
using UnityEngine;

[DisallowMultipleComponent]
public class B1LayerConfig : MonoBehaviour
{
    [Header("水平边界（两层共用）")]
    public float boundsXMin = -9.86f;
    public float boundsXMax = 9.86f;

    [Header("下层（玩家操作区）")]
    [Tooltip("下层地面高度（字符落地的 y）")]
    public float lowerGroundY = -3.77f;
    [Tooltip("字符雨生成高度")]
    public float lowerCeilingY = 0.3f;

    [Header("上层（恐龙生活区）")]
    [Tooltip("上层地板上表面高度")]
    public float upperFloorY = 1.6f;
    [Tooltip("上层天花板（红色威胁从这里出现）")]
    public float upperCeilingY = 5.2f;
    [Tooltip("上层地板左端 x")]
    public float upperFloorXMin = -9.86f;
    [Tooltip("上层地板右端 x（缺口起点，缺口一直开到 boundsXMax）")]
    public float upperFloorXMax = 8.0f;

    [Header("层间缺口（物品掉落 / 弹射通道）")]
    [Tooltip("缺口宽度中心 = (upperFloorXMax+boundsXMax)/2，自动计算，无需填")]
    public float gapPadding = 0.05f;

    [Header("视觉（编辑器摆放，可留空）")]
    public SpriteRenderer lowerFloorVisual;
    public SpriteRenderer upperFloorVisual;

    public event Action<Vector3, float> LetterRecorded;
    public event Action<Transform, float, float, string> RoomBuilt;

    internal bool NotifyLetterRecorded(Vector3 position, float size)
    {
        var listeners = LetterRecorded;
        if (listeners == null) return false;
        listeners(position, size);
        return true;
    }

    internal bool NotifyRoomBuilt(Transform room, float width, float height, string material)
    {
        var listeners = RoomBuilt;
        if (listeners == null) return false;
        listeners(room, width, height, material);
        return true;
    }

    /// <summary>层间缺口左沿（上层地板右端）</summary>
    public float GapXMin => upperFloorXMax + gapPadding;
    /// <summary>层间缺口右沿（右边界）</summary>
    public float GapXMax => boundsXMax;
    /// <summary>缺口中心</summary>
    public float GapCenterX => (GapXMin + GapXMax) * 0.5f;

    /// <summary>x 是否在层间缺口内（缺口=上层地板没铺的地方）</summary>
    public bool InGap(float x) => x > GapXMin && x < GapXMax;

    /// <summary>物体在 (x,y) 处的支撑地面：
    /// 只有位于上层地板面之上（y ≥ upperFloorY）且不在缺口 → upperFloorY；
    /// 其余（下层、板下、缺口内）→ lowerGroundY。</summary>
    public float GroundYAt(float x, float y)
    {
        if (y >= upperFloorY && !InGap(x)) return upperFloorY;
        return lowerGroundY;
    }

    /// <summary>物体当前是否"站在"上层地板上</summary>
    public bool OnUpperFloor(Vector3 p) => p.y >= upperFloorY - 0.01f && !InGap(p.x);
}
