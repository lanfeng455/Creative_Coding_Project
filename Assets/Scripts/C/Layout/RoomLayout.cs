// RoomLayout.cs —— Prototype C：房间几何描述（LLM 布局的唯一事实来源）
// 场景是「3D 伪 2D 横向卷轴」：正交相机从正面看，房间在屏幕上横向铺开。
// 所以这里的所有几何都是「沿 X 的一维区间」，Z 只是每个分带的一个固定值，不参与排布。
// 手工配置 + Scene 视图 gizmo 标定：地面可用 X 区间 / 禁放 X 区间 / 墙面 X 区间 / 命名锚点（X 位置）。
// 所有坐标都是 anchor 的局部坐标（X 右、Y 上、Z 朝房间内侧，单位米）。

using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PrototypeC
{
    public enum RoomRegionKind
    {
        /// <summary>可放东西的横向区间。</summary>
        Usable,
        /// <summary>禁放区间：区间内部必须保持空的横向段（预留区域、演出占位等）。</summary>
        KeepOut,
    }

    /// <summary>
    /// 沿 X 的一维区间（米）。地面可用区与禁放区都用它；
    /// 不再有 Z 范围——每个分带的 Z 由 RoomLayout.groundDepthZ / ceilingDepthZ / 墙面 planeZ 固定。
    /// </summary>
    [Serializable]
    public class RoomRegion
    {
        [Tooltip("区间名，会原样出现在 LLM 的输入里，请用英文短语（例如 main / reserved）")]
        public string id = "main";

        [Tooltip("Usable = 可放东西的横向区间；KeepOut = 禁放（这段必须留空），两者重叠时以禁放为准")]
        public RoomRegionKind kind = RoomRegionKind.Usable;

        [Tooltip("是否主区间：AI 没说清时默认落在这里")]
        public bool primary = true;

        [Tooltip("X 左端（锚点局部坐标，米）")]
        public float xMin = -5.5f;

        [Tooltip("X 右端（锚点局部坐标，米）")]
        public float xMax = 5.5f;
    }

    [Serializable]
    public class RoomWall
    {
        [Tooltip("墙面名（back），会原样出现在 LLM 的输入里")]
        public string id = "back";

        [Tooltip("墙面所在的局部 Z（米，与 ObjectModelService.wallPlaneZ 一致）")]
        public float planeZ = 2f;

        [Tooltip("法线方向：-1 表示朝 Z 减小的一侧（面朝房间内），1 表示朝外")]
        public float normalSign = -1f;

        [Tooltip("沿墙可用的 X 左端（米）")]
        public float alongMin = -5.5f;

        [Tooltip("沿墙可用的 X 右端（米）")]
        public float alongMax = 5.5f;

        [Tooltip("挂载高度下沿（相对地板，米）")]
        public float yMin = 0.6f;

        [Tooltip("挂载高度上沿（相对地板，米）")]
        public float yMax = 2.2f;
    }

    [Serializable]
    public class RoomWallKeepOut
    {
        [Tooltip("名字，只用于 Scene 视图标注（例如 window）")]
        public string id = "window";

        [Tooltip("哪一面墙：填 walls 里的 id（默认 back）。留空表示对所有墙生效")]
        public string wallId = "back";

        [Tooltip("禁放矩形左端 X（锚点局部坐标，米）")]
        public float xMin = -1f;

        [Tooltip("禁放矩形右端 X（锚点局部坐标，米）")]
        public float xMax = 1f;

        [Tooltip("禁放矩形下沿（相对地板的高度，米）：和墙面 yMin/yMax 同一口径")]
        public float yMin = 1.2f;

        [Tooltip("禁放矩形上沿（相对地板的高度，米）")]
        public float yMax = 2.2f;
    }

    [Serializable]
    public class RoomAnchorPoint
    {
        [Tooltip("锚点名，会原样出现在 LLM 的输入里（例如 floor.center / floor.left / wall.back）")]
        public string id = "floor.center";

        [Tooltip("锚点的横向位置 X（锚点局部坐标，米）；Y 由分带决定，Z 由分带的固定 depth 决定")]
        public float x = 0f;

        [Tooltip("这个锚点对应的分带：求解器只会把同分带物件放到它附近")]
        public RoomBand band = RoomBand.Ground;

        [Tooltip("允许的 X 方向偏移范围（±米）：AI 给的 offsetX 会被夹到这个范围里")]
        public float maxOffsetX = 1.5f;

        // 旧版（XZ 二维）配置留在场景里的字段：只在第一次读取时把 localPosition.x 迁到 x。
        // 迁移后立刻清零，Inspector 不显示，也不参与任何校验——不会出现两套坐标并存。
        [SerializeField, HideInInspector] private Vector3 localPosition;
        [SerializeField, HideInInspector] private float maxOffsetZ;

        public void MigrateLegacyConfig()
        {
            if (Mathf.Abs(x) < 0.0001f && Mathf.Abs(localPosition.x) > 0.0001f) x = localPosition.x;
            localPosition = Vector3.zero;
            maxOffsetZ = 0f;
        }
    }

    [DisallowMultipleComponent]
    public class RoomLayout : MonoBehaviour
    {
        // Inspector 字段按「配置动线」排序：先核对 → 横向范围 → 挂墙/参考点 → 调试。
        // Unity 按字段名序列化，调换顺序不会丢已配好的值。

        [Header("① 先核对这几项")]
        [Tooltip("几何基准：所有区间/墙面/锚点坐标都是这个物体的局部坐标（米）；留空自动取 ObjectModelService.spawnParent")]
        public Transform anchor;

        [Tooltip("地板在锚点局部坐标的 Y（米，一般 0，对应世界 1.6）；应与 ObjectModelService.groundBandMinY 一致")]
        public float floorY = 0f;

        [Tooltip("可选：只用来读取三带 Y 做一致性提示与 gizmo 高度，不参与几何定义；留空自动找 ObjectModelService")]
        public ObjectModelService service;

        [Header("② 横向能放哪、哪段要留空")]
        [Tooltip("沿 X 的一维区间：Usable = 能放东西，KeepOut = 禁放（这段必须留空）。默认为空；需要预留时再加。重叠时以禁放为准。单位米，锚点局部坐标")]
        public List<RoomRegion> regions = new List<RoomRegion>();

        [Tooltip("物件之间的最小间距（米）：LLM 提示词与求解器共用这一个值，改这里两边同时生效")]
        public float clearance = 0.15f;

        [Tooltip("地面物件固定的局部 Z（米）：仅用于确定前后位置，不参与排布；默认 0 ≈ 世界 z -1.5，正对相机")]
        public float groundDepthZ = 0f;

        [Tooltip("天花板物件固定的局部 Z（米）：不参与排布")]
        public float ceilingDepthZ = 0f;

        [Header("③ 挂墙与参考点（一般不用改）")]
        [Tooltip("墙面列表：Wall 类物件挂在哪一面、挂多高、左右可用 X 区间（锚点局部坐标，米）")]
        public List<RoomWall> walls = new List<RoomWall>();

        [Tooltip("墙面上的矩形禁放区（窗户、空调孔等）：某面墙 + X 区间 + Y 区间；挂墙物件只要和它相交就不放。默认为空")]
        public List<RoomWallKeepOut> wallKeepOuts = new List<RoomWallKeepOut>();

        [Tooltip("命名锚点：AI 只能从这些点里挑，只能左右偏移；floor.* 给地面，wall.* 给墙面")]
        public List<RoomAnchorPoint> anchors = new List<RoomAnchorPoint>();

        [Tooltip("房间外廓：高(Y) × 深(Z)，单位米；只用于向 AI 描述房间。宽(X) 已废弃——实际宽度由 regions 里的 Usable 区间推导")]
        public Vector3 roomSize = new Vector3(9.6f, 5f, 4f);

        [Header("④ 调试与 Scene 视图（仅编辑器，不参与运行时）")]
        public bool showRoomGizmos = true;

        [Tooltip("可放东西的区间颜色")]
        public Color usableColor = new Color(0.30f, 0.90f, 0.45f);

        [Tooltip("禁放区颜色")]
        public Color keepOutColor = new Color(1f, 0.35f, 0.25f);

        [Tooltip("墙面颜色")]
        public Color wallColor = new Color(0.30f, 0.65f, 1f);

        [Tooltip("墙面禁放区（窗户等）的颜色")]
        public Color wallKeepOutColor = new Color(1f, 0.35f, 0.85f);

        [Tooltip("锚点颜色")]
        public Color anchorColor = new Color(0.95f, 0.85f, 0.35f);

        [Tooltip("半透明填充的透明度（只影响 Scene 视图）")]
        [Range(0.02f, 0.6f)] public float fillAlpha = 0.12f;

        [Header("⑤ 恐龙活动地面 & 生成范围（C 专用）")]
        [Tooltip("恐龙的地面分区（世界坐标 X 区间 + 脚底 Y）：房间一段、左右室外各一段。留空 = 恐龙全程用旧的单一地面高度")]
        public List<DinoGroundZone> dinoGroundZones = new List<DinoGroundZone>();

        [Tooltip("把主可用区间自动扩到整个世界（不再只限房间 -5.5~5.5）；暂不审核室内/室外")]
        public bool autoExpandUsableToWorld = true;

        [Tooltip("整个世界可生成物件的世界 X 左端")]
        public float worldUsableXMin = -8.5f;

        [Tooltip("整个世界可生成物件的世界 X 右端")]
        public float worldUsableXMax = 8.5f;

        private void Awake()
        {
            ResolveReferences();
            if (autoExpandUsableToWorld) ExpandUsableToWorld();
        }

        /// <summary>把主可用区间扩到 [worldUsableXMin, worldUsableXMax]（世界坐标 → 锚点局部坐标）。</summary>
        public void ExpandUsableToWorld()
        {
            if (!TryGetPrimaryRegion(out RoomRegion region))
            {
                region = new RoomRegion { id = "main", kind = RoomRegionKind.Usable, primary = true };
                if (regions == null) regions = new List<RoomRegion>();
                regions.Add(region);
            }

            Transform frame = anchor != null ? anchor : transform;
            float lo = frame.InverseTransformPoint(new Vector3(Mathf.Min(worldUsableXMin, worldUsableXMax), 0f, 0f)).x;
            float hi = frame.InverseTransformPoint(new Vector3(Mathf.Max(worldUsableXMin, worldUsableXMax), 0f, 0f)).x;

            region.xMin = Mathf.Min(lo, hi);
            region.xMax = Mathf.Max(lo, hi);
        }

        /// <summary>Inspector 右键菜单：一键填三段地面分区（房间 + 左右室外），再把室外的 groundY 改成实际高度即可。</summary>
        [ContextMenu("预填恐龙地面分区（房间 + 左右室外）")]
        private void PrefillDinoGroundZones()
        {
            Transform frame = anchor != null ? anchor : transform;
            float roomY = frame.TransformPoint(new Vector3(0f, floorY, 0f)).y;

            float roomLo = -5.5f, roomHi = 5.5f;
            if (TryGetPrimaryRegion(out RoomRegion primary))
            {
                roomLo = Mathf.Min(primary.xMin, primary.xMax);
                roomHi = Mathf.Max(primary.xMin, primary.xMax);
                roomLo = frame.TransformPoint(new Vector3(roomLo, 0f, 0f)).x;
                roomHi = frame.TransformPoint(new Vector3(roomHi, 0f, 0f)).x;
            }

            dinoGroundZones = new List<DinoGroundZone>
            {
                new DinoGroundZone { name = "outdoor-left", xMin = worldUsableXMin, xMax = roomLo, groundY = roomY },
                new DinoGroundZone { name = "room", xMin = roomLo, xMax = roomHi, groundY = roomY },
                new DinoGroundZone { name = "outdoor-right", xMin = roomHi, xMax = worldUsableXMax, groundY = roomY },
            };

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>补齐 anchor / service / 锚点数（只填空，不覆盖 Inspector 里已经配好的值）。</summary>
        public void ResolveReferences()
        {
            SanitizeRegions();

            if (service == null) service = GetComponent<ObjectModelService>();
            if (service == null) service = FindFirstObjectByType<ObjectModelService>();
            if (anchor == null && service != null) anchor = service.spawnParent;
            if (anchor == null) anchor = transform;
            if (service != null && service.roomLayout == null) service.roomLayout = this;

            if (anchors != null)
            {
                foreach (RoomAnchorPoint point in anchors)
                {
                    if (point != null) point.MigrateLegacyConfig();
                }
            }
        }

        /// <summary>
        /// 房间的实际可用横向范围：所有 Usable 区间的并集。
        /// 这是宽度的唯一真实来源；没有 Usable 区间时才退回 roomSize.x。
        /// </summary>
        public void GetUsableBounds(out float xMin, out float xMax)
        {
            bool found = false;
            xMin = float.MaxValue;
            xMax = float.MinValue;

            if (regions != null)
            {
                foreach (RoomRegion region in regions)
                {
                    if (region == null || region.kind != RoomRegionKind.Usable) continue;

                    GetSpan(region, out float lo, out float hi);
                    xMin = Mathf.Min(xMin, lo);
                    xMax = Mathf.Max(xMax, hi);
                    found = true;
                }
            }

            if (!found)
            {
                float half = Mathf.Max(0.01f, roomSize.x) * 0.5f;
                xMin = -half;
                xMax = half;
            }
        }

        /// <summary>可放东西的横向总宽度（由 Usable 区间推导，roomSize.x 仅作为无区间时的兜底）。</summary>
        public float UsableWidth
        {
            get
            {
                GetUsableBounds(out float xMin, out float xMax);
                return Mathf.Max(0.01f, xMax - xMin);
            }
        }

        /// <summary>告诉 AI 的房间外廓：宽度由 Usable 区间推导，高/深用配置值。</summary>
        public Vector3 DescribedRoomSize => new Vector3(UsableWidth, roomSize.y, roomSize.z);

        /// <summary>
        /// 清掉历史残留的「门洞禁放区」：场景没有门，这条一直挡在屋子正中间。
        /// 只移除 id 恰好是 door_path 的 KeepOut，不动用户自己加的其它禁放区。
        /// 场景里已序列化的旧值无法直接改 .unity，这里在读取/编辑时自动剔除。
        /// </summary>
        private void SanitizeRegions()
        {
            if (regions == null) return;

            int removed = regions.RemoveAll(region =>
                region != null
                && region.kind == RoomRegionKind.KeepOut
                && string.Equals((region.id ?? string.Empty).Trim(), "door_path", StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                Debug.LogWarning($"[PrototypeC] RoomLayout：已移除 {removed} 条残留的 door_path 禁放区" +
                                 "（场景没有门，禁放区以 Inspector 当前内容为准）");
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            SanitizeRegions();
        }
#endif

        /// <summary>主区间（没有标记 primary 时取第一个 Usable）。</summary>
        public bool TryGetPrimaryRegion(out RoomRegion region)
        {
            region = null;
            if (regions == null) return false;

            foreach (RoomRegion candidate in regions)
            {
                if (candidate != null && candidate.kind == RoomRegionKind.Usable && candidate.primary)
                {
                    region = candidate;
                    return true;
                }
            }

            foreach (RoomRegion candidate in regions)
            {
                if (candidate != null && candidate.kind == RoomRegionKind.Usable)
                {
                    region = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetWall(string id, out RoomWall wall)
        {
            wall = null;
            if (walls == null || string.IsNullOrWhiteSpace(id)) return false;

            foreach (RoomWall candidate in walls)
            {
                if (candidate != null && string.Equals(candidate.id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    wall = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>某个分带物件应使用的固定局部 Z（墙面要用自己那面墙的 planeZ，不走这里）。</summary>
        public float GetBandDepth(RoomBand band)
        {
            return band == RoomBand.Rooftop ? ceilingDepthZ : groundDepthZ;
        }

        /// <summary>把区间规整成 min/max（配置写反时也能正确判断）。</summary>
        public static void GetSpan(RoomRegion region, out float xMin, out float xMax)
        {
            xMin = Mathf.Min(region.xMin, region.xMax);
            xMax = Mathf.Max(region.xMin, region.xMax);
        }

        /// <summary>某个横向区间 [x-halfWidth, x+halfWidth] 是否完全落在该区间内部（留 margin 余量）。</summary>
        public static bool ContainsX(RoomRegion region, float x, float halfWidth, float margin = 0f)
        {
            GetSpan(region, out float xMin, out float xMax);
            return x - halfWidth >= xMin + margin && x + halfWidth <= xMax - margin;
        }

        /// <summary>横向区间是否被禁放区挡住（物件宽度与禁放区间相交即视为不可放）。</summary>
        public bool BlockedByKeepOut(float x, float halfWidth)
        {
            if (regions == null) return false;

            foreach (RoomRegion region in regions)
            {
                if (region == null || region.kind != RoomRegionKind.KeepOut) continue;

                GetSpan(region, out float xMin, out float xMax);
                if (x + halfWidth > xMin && x - halfWidth < xMax) return true;
            }

            return false;
        }

        /// <summary>
        /// 挂墙物件是否压到墙面禁放矩形（窗户等）。
        /// 只有 X 区间与 Y 区间同时相交才算命中；yMin/yMax 是相对地板的高度。
        /// </summary>
        public bool WallBlockedByKeepOut(RoomWall wall, float x, float halfWidth, float itemYMin, float itemYMax)
        {
            if (wallKeepOuts == null) return false;

            float itemXMin = x - halfWidth;
            float itemXMax = x + halfWidth;
            float itemYLo = Mathf.Min(itemYMin, itemYMax);
            float itemYHi = Mathf.Max(itemYMin, itemYMax);

            foreach (RoomWallKeepOut keepOut in wallKeepOuts)
            {
                if (keepOut == null) continue;

                // 墙 id 留空 = 对所有墙生效
                if (!string.IsNullOrWhiteSpace(keepOut.wallId)
                    && wall != null
                    && !string.Equals(keepOut.wallId.Trim(), wall.id, StringComparison.OrdinalIgnoreCase))
                    continue;

                float kxMin = Mathf.Min(keepOut.xMin, keepOut.xMax);
                float kxMax = Mathf.Max(keepOut.xMin, keepOut.xMax);
                if (itemXMax <= kxMin || itemXMin >= kxMax) continue;

                float kyMin = Mathf.Min(keepOut.yMin, keepOut.yMax);
                float kyMax = Mathf.Max(keepOut.yMin, keepOut.yMax);
                if (itemYHi <= kyMin || itemYLo >= kyMax) continue;

                return true;
            }

            return false;
        }

        /// <summary>某面墙上的全部禁放矩形（gizmo / LLM 描述用）。</summary>
        public List<RoomWallKeepOut> GetWallKeepOuts(string wallId)
        {
            var result = new List<RoomWallKeepOut>();
            if (wallKeepOuts == null) return result;

            foreach (RoomWallKeepOut keepOut in wallKeepOuts)
            {
                if (keepOut == null) continue;
                if (!string.IsNullOrWhiteSpace(keepOut.wallId)
                    && !string.Equals(keepOut.wallId.Trim(), wallId, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(keepOut);
            }

            return result;
        }

        /// <summary>场景里添加组件时给一份与当前房间大致相符的初始配置，避免一上来是空的。</summary>
        private void Reset()
        {
            regions = new List<RoomRegion>
            {
                new RoomRegion { id = "main", kind = RoomRegionKind.Usable, primary = true, xMin = -5.5f, xMax = 5.5f },
            };
            // 默认不生成任何禁放区：没有必须留空的固定段，也就不预设任何"门"。
            // 以后要预留（演出/任务占位/避免穿模）时，自己加一条 kind = KeepOut 的区间即可。

            walls = new List<RoomWall>
            {
                new RoomWall { id = "back", planeZ = 2f, normalSign = -1f, alongMin = -5.5f, alongMax = 5.5f, yMin = 0.6f, yMax = 2.2f },
            };

            anchors = new List<RoomAnchorPoint>
            {
                new RoomAnchorPoint { id = "floor.center", x = 0f, maxOffsetX = 1.5f },
                new RoomAnchorPoint { id = "floor.left", x = -4f, maxOffsetX = 1.2f },
                new RoomAnchorPoint { id = "floor.right", x = 4f, maxOffsetX = 1.2f },
                new RoomAnchorPoint { id = "wall.back", x = 0f, band = RoomBand.Wall, maxOffsetX = 5f },
            };
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器辅助：把可用区间、禁放区间、墙面、命名锚点画成「正对相机的竖直面片」，
        /// 从正交侧视一眼就能读出「从哪到哪能用、哪段要留空」。
        /// 一律用 anchor.localToWorldMatrix 绘制；只在编辑器里编译与调用，不进构建。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!showRoomGizmos) return;

            Transform frame = anchor != null ? anchor : (service != null && service.spawnParent != null ? service.spawnParent : transform);

            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.matrix = frame.localToWorldMatrix;

            float groundTop = service != null
                ? Mathf.Max(service.groundBandMinY, service.groundBandMaxY)
                : floorY + 0.6f;
            float ceilingBottom = service != null
                ? Mathf.Min(service.ceilingBandMinY, service.ceilingBandMaxY)
                : floorY + 2.8f;

            Color Fill(Color c)
            {
                return new Color(c.r, c.g, c.b, fillAlpha);
            }

            GUIStyle Label(Color c)
            {
                var style = new GUIStyle(EditorStyles.boldLabel);
                style.normal.textColor = c;
                return style;
            }

            // 竖直面片：在 X-Y 平面上展开，贴在分带固定 depth 上，正对相机
            void Panel(float xMin, float xMax, float yMin, float yMax, float z, Color color, bool conflict, string text)
            {
                float lo = Mathf.Min(xMin, xMax);
                float hi = Mathf.Max(xMin, xMax);
                Color edge = conflict ? keepOutColor : color;
                Vector3 center = new Vector3((lo + hi) * 0.5f, (yMin + yMax) * 0.5f, z);
                Vector3 size = new Vector3(Mathf.Max(0.01f, hi - lo), Mathf.Max(0.01f, yMax - yMin), 0.01f);

                Gizmos.color = Fill(edge);
                Gizmos.DrawCube(center, size);
                Gizmos.color = edge;
                Gizmos.DrawWireCube(center, size);
                Handles.Label(frame.TransformPoint(new Vector3(lo, yMax + 0.06f, z)), text, Label(edge));
            }

            // 地面可用区间 / 禁放区间：竖直条带，标 X 起止
            if (regions != null)
            {
                foreach (RoomRegion region in regions)
                {
                    if (region == null) continue;

                    GetSpan(region, out float xMin, out float xMax);
                    bool inverted = region.xMin > region.xMax;
                    bool keepOut = region.kind == RoomRegionKind.KeepOut;
                    Color color = keepOut ? keepOutColor : usableColor;
                    float lift = keepOut ? 0.02f : 0f;

                    Panel(xMin, xMax, floorY, groundTop, groundDepthZ + lift, color, inverted,
                        $"{region.id}{(keepOut ? "（禁放）" : string.Empty)}  x {xMin:0.00} ~ {xMax:0.00} m" +
                        (inverted ? "  配置反了" : string.Empty));
                }
            }

            // 墙面：竖板 + 朝向箭头 + 可用 X 起止
            if (walls != null)
            {
                foreach (RoomWall wall in walls)
                {
                    if (wall == null) continue;

                    float alongMin = Mathf.Min(wall.alongMin, wall.alongMax);
                    float alongMax = Mathf.Max(wall.alongMin, wall.alongMax);
                    float yMin = Mathf.Min(wall.yMin, wall.yMax);
                    float yMax = Mathf.Max(wall.yMin, wall.yMax);
                    bool inverted = wall.alongMin > wall.alongMax || wall.yMin > wall.yMax;

                    Panel(alongMin, alongMax, floorY + yMin, floorY + yMax, wall.planeZ, wallColor, inverted,
                        $"{wall.id}  x {alongMin:0.00} ~ {alongMax:0.00} m｜y {yMin:0.00} ~ {yMax:0.00} m" +
                        (inverted ? "  配置反了" : string.Empty));

                    Vector3 normal = new Vector3(0f, 0f, Mathf.Sign(wall.normalSign == 0f ? -1f : wall.normalSign));
                    Vector3 center = new Vector3((alongMin + alongMax) * 0.5f, floorY + (yMin + yMax) * 0.5f, wall.planeZ);
                    Gizmos.color = inverted ? keepOutColor : wallColor;
                    Gizmos.DrawLine(center, center + normal * 0.6f);

                    // 墙面禁放矩形（窗户等）：画在墙面稍前一点，颜色区别于墙的可用范围
                    List<RoomWallKeepOut> windows = GetWallKeepOuts(wall.id);
                    foreach (RoomWallKeepOut window in windows)
                    {
                        if (window == null) continue;

                        float wxMin = Mathf.Min(window.xMin, window.xMax);
                        float wxMax = Mathf.Max(window.xMin, window.xMax);
                        float wyMin = Mathf.Min(window.yMin, window.yMax);
                        float wyMax = Mathf.Max(window.yMin, window.yMax);
                        bool windowInverted = window.xMin > window.xMax || window.yMin > window.yMax;

                        Panel(wxMin, wxMax, floorY + wyMin, floorY + wyMax, wall.planeZ + 0.03f,
                            wallKeepOutColor, windowInverted,
                            $"{window.id}（墙面禁放）  x {wxMin:0.00} ~ {wxMax:0.00} m｜y {wyMin:0.00} ~ {wyMax:0.00} m" +
                            (windowInverted ? "  配置反了" : string.Empty));
                    }
                }
            }

            // 命名锚点：竖线 + 允许左右偏移范围（X 一维）+ 名字与 X 值
            if (anchors != null)
            {
                foreach (RoomAnchorPoint point in anchors)
                {
                    if (point == null) continue;

                    float yLo;
                    float yHi;
                    float baseY;
                    switch (point.band)
                    {
                        case RoomBand.Wall:
                            yLo = floorY + 0.6f;
                            yHi = floorY + 2.2f;
                            baseY = (yLo + yHi) * 0.5f;
                            break;
                        case RoomBand.Rooftop:
                            yLo = ceilingBottom;
                            yHi = ceilingBottom + 0.4f;
                            baseY = yHi;
                            break;
                        default:
                            yLo = floorY;
                            yHi = groundTop;
                            baseY = floorY;
                            break;
                    }

                    float depth = point.band == RoomBand.Wall && walls != null && walls.Count > 0 && walls[0] != null
                        ? walls[0].planeZ - 0.06f
                        : GetBandDepth(point.band) + 0.04f;

                    Gizmos.color = anchorColor;
                    Gizmos.DrawLine(new Vector3(point.x, yLo, depth), new Vector3(point.x, yHi, depth));
                    Gizmos.DrawWireSphere(new Vector3(point.x, baseY, depth), 0.06f);

                    float offset = Mathf.Max(0f, point.maxOffsetX);
                    if (offset > 0.001f)
                        Gizmos.DrawLine(new Vector3(point.x - offset, baseY, depth), new Vector3(point.x + offset, baseY, depth));

                    Handles.Label(
                        frame.TransformPoint(new Vector3(point.x, yHi + 0.06f, depth)),
                        $"{point.id}  x={point.x:0.00}",
                        Label(anchorColor));
                }
            }

            // 地板参考线：从地面主区间左端拉到右端
            if (TryGetPrimaryRegion(out RoomRegion primary))
            {
                GetSpan(primary, out float floorLeft, out float floorRight);
                Gizmos.color = usableColor;
                Gizmos.DrawLine(new Vector3(floorLeft, floorY, groundDepthZ), new Vector3(floorRight, floorY, groundDepthZ));
            }

            // 一致性提示：地板高度与三带口径不一致时标出来（两套参数不同步会让 AI 看到错误 Y）
            if (service != null && Mathf.Abs(floorY - service.groundBandMinY) > 0.001f)
            {
                Handles.Label(
                    frame.TransformPoint(new Vector3(-5.5f, floorY + 0.4f, groundDepthZ + 0.1f)),
                    $"配置冲突：RoomLayout.floorY={floorY:0.00} 与 ObjectModelService.groundBandMinY={service.groundBandMinY:0.00} 不一致",
                    Label(keepOutColor));
            }

            Gizmos.matrix = previousMatrix;
        }
#endif
    }
}
