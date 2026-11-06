// WateringCan.cs —— 恐龙的水壶（Prototype B 仅预留接口与数据结构）
// 设定：恐龙用叶片制作、长期持有；天灾发生时可协助玩家当前聚焦的字母生长；
// 可反复使用、不消耗叶片。天灾系统本阶段不实现，这里只留接口，
// 避免后续接入时需要改恐龙状态机。

using UnityEngine;

[System.Serializable]
public class WateringCan
{
    [Tooltip("恐龙是否已制作水壶（后续阶段由恐龙消耗叶片制作）")]
    public bool crafted;

    [Tooltip("使用后的冷却剩余时间（秒），0 表示可用")]
    public float cooldownRemaining;

    /// <summary>水壶可反复使用，不消耗叶片</summary>
    public bool IsReusable => true;

    /// <summary>预留：天灾发生时协助玩家当前聚焦的字母生长。
    /// 未制作 / 冷却中返回 false。
    /// TODO(天灾系统)：实现时对 focusPlant 施加生长增益并设置冷却，
    /// 例如 focusPlant.SetFocus(Mathf.Clamp01(assistance))。</summary>
    public bool TryAssistFocus(LetterPlant focusPlant, float assistance = 0.5f)
    {
        if (!crafted || focusPlant == null || cooldownRemaining > 0f) return false;
        // 具体增益逻辑等天灾系统定型后在此实现
        return false;
    }
}
