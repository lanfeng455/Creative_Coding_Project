// LayoutTypes.cs —— Prototype C：布局相关的公共类型
// 只放数据与枚举，不做计算；求解器（LayoutSolver）、占用表（RoomOccupancy）、
// LLM 布局调用都共用这里的定义，避免各写一套。

using System;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>与 ObjectModelService 的三带一一对应（Ground / Wall / Rooftop）。</summary>
    public enum RoomBand
    {
        Ground,
        Wall,
        Rooftop,
    }

    public static class RoomBands
    {
        /// <summary>ObjectSpec.placementKind（LLM 给的枚举字符串）→ RoomBand；空值/未知按 Ground。</summary>
        public static RoomBand FromPlacementKind(string placementKind)
        {
            if (string.Equals(placementKind, "Wall", StringComparison.OrdinalIgnoreCase)) return RoomBand.Wall;
            if (string.Equals(placementKind, "Rooftop", StringComparison.OrdinalIgnoreCase)) return RoomBand.Rooftop;
            return RoomBand.Ground;
        }

        /// <summary>RoomBand → ObjectSpec.placementKind 用的字符串。</summary>
        public static string ToPlacementKind(RoomBand band)
        {
            switch (band)
            {
                case RoomBand.Wall: return "Wall";
                case RoomBand.Rooftop: return "Rooftop";
                default: return "Ground";
            }
        }

        public static string ToText(RoomBand band)
        {
            switch (band)
            {
                case RoomBand.Wall: return "wall";
                case RoomBand.Rooftop: return "rooftop";
                default: return "ground";
            }
        }
    }

    /// <summary>
    /// 朝向语义：物件「正面」朝向哪里。伪 2D 侧视下，家具应背靠更近的侧墙、正面朝房间内部。
    /// 与 yawDegrees 的换算假定模型正面是局部 +Z（挂墙默认 180° 朝相机就是按这个约定）。
    /// </summary>
    public static class Facings
    {
        public const string Left = "left";
        public const string Right = "right";
        public const string TowardCamera = "toward_camera";
        public const string AwayFromCamera = "away_from_camera";

        public static bool TryNormalize(string raw, out string facing)
        {
            facing = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string key = raw.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            switch (key)
            {
                case "left":
                case "face_left":
                case "left_wall":
                    facing = Left;
                    return true;
                case "right":
                case "face_right":
                case "right_wall":
                    facing = Right;
                    return true;
                case "toward_camera":
                case "to_camera":
                case "camera":
                case "front":
                    facing = TowardCamera;
                    return true;
                case "away_from_camera":
                case "away":
                case "back":
                    facing = AwayFromCamera;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>朝向 → 绕 Y 的旋转角（度）。正面局部 +Z：左 -90、右 +90、朝相机 180、背相机 0。</summary>
        public static float ToYaw(string facing)
        {
            switch (facing)
            {
                case Left: return -90f;
                case Right: return 90f;
                case TowardCamera: return 180f;
                default: return 0f;
            }
        }

        /// <summary>
        /// 按最终落点推朝向：更靠近左墙 → 正面朝右（+90，背靠左墙）；
        /// 更靠近右墙 → 正面朝左（-90，背靠右墙）。
        /// 正好等距时优先采纳 LLM 给的同侧 left/right；没给、给的是朝相机或与位置矛盾，就固定贴右墙（-90）。
        /// </summary>
        public static string FromX(float x, float xMin, float xMax, string preferredFacing)
        {
            float distLeft = x - xMin;
            float distRight = xMax - x;

            if (distLeft < distRight) return Right;
            if (distRight < distLeft) return Left;

            if (TryNormalize(preferredFacing, out string preferred)
                && (string.Equals(preferred, Left, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(preferred, Right, StringComparison.OrdinalIgnoreCase)))
                return preferred;

            return Left;
        }

        public static string ToText(string facing)
        {
            if (string.Equals(facing, Left, StringComparison.OrdinalIgnoreCase)) return "朝左（背靠右墙）";
            if (string.Equals(facing, Right, StringComparison.OrdinalIgnoreCase)) return "朝右（背靠左墙）";
            if (string.Equals(facing, TowardCamera, StringComparison.OrdinalIgnoreCase)) return "朝相机";
            if (string.Equals(facing, AwayFromCamera, StringComparison.OrdinalIgnoreCase)) return "背对相机";
            return "默认";
        }
    }

    /// <summary>
    /// 摆放意图：LLM 的输出（阶段 4 填充），或求解器内部的候选。
    /// 只描述「相对锚点的意图」，不是自由坐标；最终落点由 LayoutSolver 校验后决定。
    /// </summary>
    [Serializable]
    public class LayoutIntent
    {
        [Tooltip("地面/墙面/天花板；留空则沿用 ObjectSpec.placementKind")]
        public string band = string.Empty;

        [Tooltip("命名锚点 id（RoomLayout 里配置的），留空则用主区域")]
        public string anchor = string.Empty;

        [Tooltip("相对锚点的 X 偏移（米）：横向卷轴视角下唯一有意义的偏移方向")]
        public float offsetX = 0f;

        [Tooltip("朝向（度）；仅作兜底。填了 facing 时以 facing / 最终落点为准")]
        public float yawDegrees = 90f;

        [Tooltip("朝向语义：left / right / toward_camera / away_from_camera；落点确定后会按左右半区重新推导")]
        public string facing = string.Empty;

        [Tooltip("可选：贴墙物件的沿墙位置（米，锚点局部 X）；仅 Wall 使用")]
        public float alongWall = 0f;

        [Tooltip("预留：想贴着哪个已落位物件（物件 id）；v1 只记录不使用，求解器不会读取")]
        public string adjacentTo = string.Empty;

        [Tooltip("LLM 自评置信度（0~1），只用于日志与兜底排序，不参与几何计算")]
        public float confidence = 0f;

        [Tooltip("LLM 给的理由，只写日志，不参与计算")]
        public string hint = string.Empty;

        [Tooltip("来源标记：llm / rule / llm-repair")]
        public string source = "rule";

        public LayoutIntent Clone()
        {
            return new LayoutIntent
            {
                band = band,
                anchor = anchor,
                offsetX = offsetX,
                yawDegrees = yawDegrees,
                facing = facing,
                alongWall = alongWall,
                adjacentTo = adjacentTo,
                confidence = confidence,
                hint = hint,
                source = source,
            };
        }
    }

    /// <summary>求解结果：最终横向落点 X + 分带固定 depth + 朝向 + 是否降级，以及降级原因。</summary>
    public struct LayoutSolution
    {
        public bool success;
        public bool degraded;
        public RoomBand band;
        public string anchorId;
        public Vector3 localXZ;
        public float yawDegrees;
        /// <summary>实际采用的朝向语义（Wall 为空，由 overrideWallYaw 决定）。</summary>
        public string facing;
        public string reason;

        public static LayoutSolution Failed(RoomBand band, string reason)
        {
            return new LayoutSolution
            {
                success = false,
                degraded = true,
                band = band,
                anchorId = string.Empty,
                localXZ = Vector3.zero,
                yawDegrees = 0f,
                facing = string.Empty,
                reason = reason,
            };
        }
    }
}
