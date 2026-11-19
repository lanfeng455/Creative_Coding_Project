// LayoutSolver.cs —— Prototype C：摆放意图 → 实际落点（纯规则，不联网）
// 场景是横向卷轴视角：物件只沿 X 排布，Z 由分带的固定 depth 决定，Y 由分带高度决定。
// 所有候选都只在一维 X 上移动，校验是「X 区间相交 + 实际宽度（含 yaw 投影）+ 余量」。
// 冲突时的阶梯：微调 X → 换锚点（X 上的其它点）→ 沿 X 等距扫描 → 降级（仍必须过硬校验，否则拒绝落位）。
// 本类只算 X 与朝向；Y（贴地 / 墙带居中 / 顶面贴天花板）仍由 ObjectModelService 统一处理。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class LayoutSolver : MonoBehaviour
    {
        // Inspector 字段按「配置动线」排序：先核对引用 → 调参。
        // Unity 按字段名序列化，调换顺序不会丢已配好的值。

        [Header("① 先核对这几项")]
        [Tooltip("房间几何描述（可用 X 区间/禁放区/墙面/参考点）；留空自动从场景里找")]
        public RoomLayout layout;

        [Tooltip("已落位占用表（防止压到别的东西）；留空自动从场景里找，没有时只做区间校验")]
        public RoomOccupancy occupancy;

        [Header("② 撞了怎么退让（一般不用改）")]
        [Tooltip("第 2 级：以建议位置为中心左右挪动的步长（米）")]
        public float nudgeStep = 0.05f;

        [Tooltip("第 2 级：左右各试几格（6 → 最多挪 ±0.30 m）")]
        public int nudgeRadius = 6;

        [Tooltip("第 3 级：同一分带里最多再试几个参考点（按离建议位置的远近排序）")]
        public int maxAnchorTries = 5;

        [Tooltip("第 4 级：沿 X 扫描空位时的间距（米）")]
        public float gridStep = 0.6f;

        [Tooltip("第 4 级：最多试多少个候选点，防止大房间卡顿")]
        public int maxGridCandidates = 400;

        [Header("④ 地面随机落位（抓娃娃机式投放）")]
        [Tooltip("地面物件在可用区间里随机取点选落位；关闭则回到「就近确定性」求解")]
        public bool randomGroundPlacement = true;

        [Tooltip("随机落位每次的尝试次数：在可用区间均匀取 X 逐个校验（禁放区 / 重叠 / 区间内）")]
        public int randomTries = 24;

        [Header("③ 朝向（落点确定后再推导）")]
        [Tooltip("按物件 id 覆盖朝向（度，绕 Y）。命中时优先于落点推导、也优先于 LLM 的 facing；挂墙仍由 ObjectModelService 的 overrideWallYaw 决定")]
        public List<YawOverride> yawOverrides = new List<YawOverride>();

        /// <summary>按 id 覆盖朝向：命中时直接用 yawDegrees，忽略「按落点推导」。</summary>
        [System.Serializable]
        public class YawOverride
        {
            [Tooltip("物件 id（大小写不敏感，如 plant / plant_01）")]
            public string id = string.Empty;

            [Tooltip("朝向（度，绕 Y）：0 = 背对相机、180 = 朝相机、-90 = 朝左、90 = 朝右")]
            public float yawDegrees = 90f;
        }

        // 当前求解的挂墙物件垂直占位（相对地板），只在本帧 Solve 内使用
        private float _probeYMin;
        private float _probeYMax;

        private void Awake()
        {
            ResolveReferences();
        }

        public void ResolveReferences()
        {
            if (layout == null) layout = GetComponent<RoomLayout>();
            if (layout == null) layout = FindFirstObjectByType<RoomLayout>();
            if (occupancy == null) occupancy = GetComponent<RoomOccupancy>();
            if (occupancy == null) occupancy = FindFirstObjectByType<RoomOccupancy>();
            if (layout != null && layout.anchor == null) layout.ResolveReferences();
            if (occupancy != null && occupancy.layout == null) occupancy.layout = layout;
        }

        /// <summary>
        /// 求一个落点。size 是缩放后的实际包围盒（米，未旋转）；intent 为 null 时走纯规则。
        /// forcedWallYaw 用于挂墙：ObjectModelService 开启 overrideWallYaw 时传入固定朝向，避免宽度口径不一致。
        /// 返回的 localXZ 的 X 是横向落点、Z 是该分带的固定 depth、Y 恒为 0；
        /// yawDegrees / facing 是「落点确定之后」才推导的朝向。
        /// </summary>
        public LayoutSolution Solve(ObjectSpec spec, Vector3 size, LayoutIntent intent, float forcedWallYaw = float.NaN)
        {
            ResolveReferences();

            RoomBand band = intent != null && !string.IsNullOrWhiteSpace(intent.band)
                ? ParseBand(intent.band)
                : RoomBands.FromPlacementKind(spec != null ? spec.placementKind : string.Empty);

            string ignoreId = spec != null ? spec.NormalizedId : string.Empty;

            string source = intent != null && !string.IsNullOrWhiteSpace(intent.anchor) ? "intent" : "rule";
            float baseX = ResolveBaseX(band, intent, out string anchorId, out RoomRegion primary, out RoomWall wall);

            if (band == RoomBand.Wall && wall == null)
                return LayoutSolution.Failed(band, "RoomLayout 里没有配置可用的墙面");

            if (band != RoomBand.Wall && primary == null)
                return LayoutSolution.Failed(band, "RoomLayout 里没有配置可用的横向区间");

            // 挂墙物件的垂直占位（相对地板）：垂直居中在墙带里，用真实高度对窗户矩形做 Y 相交判断
            _probeYMin = 0f;
            _probeYMax = 0f;
            if (band == RoomBand.Wall && wall != null)
            {
                float bandLo = Mathf.Min(wall.yMin, wall.yMax);
                float bandHi = Mathf.Max(wall.yMin, wall.yMax);
                float bandMid = (bandLo + bandHi) * 0.5f;
                float itemHalfHeight = Mathf.Abs(size.y) * 0.5f;
                _probeYMin = bandMid - itemHalfHeight;
                _probeYMax = bandMid + itemHalfHeight;
            }

            float depth = band == RoomBand.Wall ? wall.planeZ : layout.GetBandDepth(band);

            string rejectReason = string.Empty;

            // 1) 意图点（AI 建议的 X，或主区间中心 / 墙面中心）
            if (Evaluate(spec, intent, band, ignoreId, baseX, size, primary, wall, forcedWallYaw,
                    out float yaw, out string facing, out _, out string reason1))
                return Success(band, anchorId, baseX, depth, yaw, facing, source, spec, intent);

            rejectReason = reason1;

            // 2) 微调：以意图点为中心，左右各挪一小步，由近到远
            float step = Mathf.Max(0.01f, nudgeStep);
            int radius = Mathf.Max(0, nudgeRadius);
            for (int i = 1; i <= radius; i++)
            {
                float rightX = baseX + i * step;
                if (Evaluate(spec, intent, band, ignoreId, rightX, size, primary, wall, forcedWallYaw,
                        out float rightYaw, out string rightFacing, out _, out _))
                    return Success(band, anchorId, rightX, depth, rightYaw, rightFacing, "intent+nudge", spec, intent);

                float leftX = baseX - i * step;
                if (Evaluate(spec, intent, band, ignoreId, leftX, size, primary, wall, forcedWallYaw,
                        out float leftYaw, out string leftFacing, out _, out _))
                    return Success(band, anchorId, leftX, depth, leftYaw, leftFacing, "intent+nudge", spec, intent);
            }

            // 3) 换锚点：同分带、离意图位置最近的几个（只比 X 距离）
            if (layout != null && layout.anchors != null)
            {
                var anchors = new List<RoomAnchorPoint>();
                foreach (RoomAnchorPoint point in layout.anchors)
                {
                    if (point != null && point.band == band) anchors.Add(point);
                }
                anchors.Sort((a, b) => Mathf.Abs(a.x - baseX).CompareTo(Mathf.Abs(b.x - baseX)));

                int tried = 0;
                foreach (RoomAnchorPoint point in anchors)
                {
                    if (tried >= Mathf.Max(0, maxAnchorTries)) break;
                    tried++;

                    if (Evaluate(spec, intent, band, ignoreId, point.x, size, primary, wall, forcedWallYaw,
                            out float anchorYaw, out string anchorFacing, out _, out _))
                        return Success(band, point.id, point.x, depth, anchorYaw, anchorFacing, "anchor", spec, intent);
                }
            }

            // 4) 沿 X 等距扫描：在主区间（或墙面可用段）里找最近的空位；
            //    挂墙也扫，否则窗户把建议位置挡住时只能靠 ±0.30 m 的微调，退不开
            if (band == RoomBand.Wall && wall != null)
            {
                if (TryScanAlongWall(spec, intent, band, wall, baseX, ignoreId, size, primary, forcedWallYaw,
                        out float wallX, out float wallYaw, out string wallFacing))
                    return Success(band, wall.id, wallX, depth, wallYaw, wallFacing, "grid", spec, intent);
            }
            else if (primary != null)
            {
                if (TryScanAlongX(spec, intent, band, primary, baseX, ignoreId, size, forcedWallYaw,
                        out float gridX, out float gridYaw, out string gridFacing))
                    return Success(band, primary.id, gridX, depth, gridYaw, gridFacing, "grid", spec, intent);
            }

            // 5) 兜底：夹进可用区间后，仍必须通过禁放区与重叠校验。
            //    过不了就明确失败（由调用方拒绝落位），绝不把物件塞进禁放区或已有物件里。
            //    夹紧会改变落点、可能改变朝向与宽度，所以夹两遍后再校验。
            Evaluate(spec, intent, band, ignoreId, baseX, size, primary, wall, forcedWallYaw,
                out _, out _, out float baseHalfWidth, out _);
            float fallbackX = ClampToSpan(band, baseX, baseHalfWidth, primary, wall);
            Evaluate(spec, intent, band, ignoreId, fallbackX, size, primary, wall, forcedWallYaw,
                out _, out _, out float fallbackHalfWidth, out _);
            fallbackX = ClampToSpan(band, fallbackX, fallbackHalfWidth, primary, wall);

            if (Evaluate(spec, intent, band, ignoreId, fallbackX, size, primary, wall, forcedWallYaw,
                    out float fallbackYaw, out string fallbackFacing, out _, out string lastReason))
            {
                var solution = Success(band, anchorId, fallbackX, depth, fallbackYaw, fallbackFacing, "degraded", spec, intent);
                solution.degraded = true;
                solution.reason = string.IsNullOrEmpty(rejectReason) ? "没有空位" : rejectReason;
                Debug.LogWarning($"[PrototypeC] 布局降级：{spec?.objectName}（{ignoreId}）｜{band}｜原因：{solution.reason}｜" +
                                 $"落点 x={fallbackX:0.00}（z={depth:0.00}）");
                return solution;
            }

            string finalReason = string.IsNullOrEmpty(lastReason)
                ? (string.IsNullOrEmpty(rejectReason) ? "没有空位" : rejectReason)
                : lastReason;
            Debug.LogWarning($"[PrototypeC] 布局放弃：{spec?.objectName}（{ignoreId}）｜{band}｜" +
                             $"没有通过禁放区/间距校验的横向落点（{finalReason}）");
            return LayoutSolution.Failed(band, finalReason);
        }

        /// <summary>纯规则求解（没有 LLM 意图时）。</summary>
        public LayoutSolution Solve(ObjectSpec spec, Vector3 size)
        {
            return Solve(spec, size, null);
        }

        /// <summary>
        /// 地面随机落位：在所有可用横向区间里均匀取 X，走与确定性求解完全相同的硬校验
        /// （区间内 + 禁放区 + 已落位重叠 + 按该 X 推导朝向后的真实足迹），
        /// 收集若干合法候选后随机挑一个；一个合法点都没有时返回失败，由调用方走确定性阶梯或拒绝落位。
        /// </summary>
        public LayoutSolution SolveRandomGround(ObjectSpec spec, Vector3 size)
        {
            ResolveReferences();
            if (layout == null || layout.regions == null)
                return LayoutSolution.Failed(RoomBand.Ground, "没有 RoomLayout，无法随机采样");

            // 收集可用区间并按宽度加权（长区间被采到的机会多，落点分布才均匀）
            float totalWidth = 0f;
            var spans = new List<(float xMin, float xMax)>();
            foreach (RoomRegion region in layout.regions)
            {
                if (region == null || region.kind != RoomRegionKind.Usable) continue;
                RoomLayout.GetSpan(region, out float xMin, out float xMax);
                if (xMax <= xMin) continue;
                spans.Add((xMin, xMax));
                totalWidth += xMax - xMin;
            }
            if (spans.Count == 0 || totalWidth <= 0.01f)
                return LayoutSolution.Failed(RoomBand.Ground, "没有可用的横向区间");

            RoomBand band = RoomBands.FromPlacementKind(spec != null ? spec.placementKind : string.Empty);
            string ignoreId = spec != null ? spec.NormalizedId : string.Empty;
            layout.TryGetPrimaryRegion(out RoomRegion primary);

            var valid = new List<(float x, float yaw, string facing)>();
            int tries = Mathf.Max(4, randomTries);
            for (int i = 0; i < tries && valid.Count < 6; i++)
            {
                // 加权抽一个区间，再在区间内均匀取 X（含半宽收窄，减少白采样）
                float roll = Random.value * totalWidth;
                float xMin = 0f, xMax = 0f;
                foreach (var span in spans)
                {
                    float w = span.xMax - span.xMin;
                    if (roll <= w) { xMin = span.xMin; xMax = span.xMax; break; }
                    roll -= w;
                }

                float x = Random.Range(xMin, xMax);
                if (!Evaluate(spec, null, band, ignoreId, x, size, primary, null, float.NaN,
                        out float yaw, out string facing, out _, out _)) continue;

                valid.Add((x, yaw, facing));
            }

            if (valid.Count == 0)
                return LayoutSolution.Failed(RoomBand.Ground, "随机采样没有找到合法落点");

            var pick = valid[Random.Range(0, valid.Count)];
            float depth = layout.GetBandDepth(band);
            Debug.Log($"[PrototypeC] 布局：随机落位 {spec?.objectName}（{ignoreId}）｜" +
                      $"候选 {valid.Count} 个｜选中 x={pick.x:0.00}｜yaw {pick.yaw:0}｜{Facings.ToText(pick.facing)}");
            return new LayoutSolution
            {
                success = true,
                degraded = false,
                band = band,
                anchorId = "random",
                localXZ = new Vector3(pick.x, 0f, depth),
                yawDegrees = pick.yaw,
                facing = pick.facing,
                reason = "random",
            };
        }

        /// <summary>建议落点（只在 X 上有意义）。</summary>
        private float ResolveBaseX(RoomBand band, LayoutIntent intent, out string anchorId,
            out RoomRegion primary, out RoomWall wall)
        {
            primary = null;
            wall = null;
            anchorId = string.Empty;

            if (layout == null)
                return intent != null ? intent.offsetX : 0f;

            layout.TryGetPrimaryRegion(out primary);

            // 意图指定了锚点：用它；否则用主区间中心 / 第一面墙中心
            RoomAnchorPoint anchorPoint = null;
            if (intent != null && !string.IsNullOrWhiteSpace(intent.anchor) && layout.anchors != null)
            {
                foreach (RoomAnchorPoint candidate in layout.anchors)
                {
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.id, intent.anchor.Trim(), System.StringComparison.OrdinalIgnoreCase)) continue;
                    anchorPoint = candidate;
                    break;
                }
            }

            if (band == RoomBand.Wall)
            {
                string wallId = anchorPoint != null ? WallIdFromAnchor(anchorPoint.id) : string.Empty;
                if (!layout.TryGetWall(wallId, out wall))
                {
                    if (layout.walls != null && layout.walls.Count > 0) wall = layout.walls[0];
                }
            }

            float x;
            if (anchorPoint != null)
            {
                anchorId = anchorPoint.id;
                x = anchorPoint.x;
                if (intent != null)
                {
                    float maxX = Mathf.Max(0f, anchorPoint.maxOffsetX);
                    x += Mathf.Clamp(intent.offsetX, -maxX, maxX);
                }
            }
            else if (band == RoomBand.Wall)
            {
                anchorId = wall != null ? wall.id : string.Empty;
                x = wall != null ? (wall.alongMin + wall.alongMax) * 0.5f : 0f;
            }
            else if (primary != null)
            {
                RoomLayout.GetSpan(primary, out float xMin, out float xMax);
                anchorId = primary.id;
                x = (xMin + xMax) * 0.5f;
                if (intent != null) x += intent.offsetX;
            }
            else
            {
                x = 0f;
            }

            // 意图里的 alongWall 只对墙面生效：沿墙平移
            if (band == RoomBand.Wall && intent != null && Mathf.Abs(intent.alongWall) > 0.0001f)
                x += intent.alongWall;

            return x;
        }

        private static string WallIdFromAnchor(string anchorId)
        {
            if (string.IsNullOrWhiteSpace(anchorId)) return string.Empty;
            const string prefix = "wall.";
            return anchorId.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)
                ? anchorId.Substring(prefix.Length)
                : anchorId;
        }

        /// <summary>一维校验：可用 X 区间（或墙面 X 区间）+ 禁放区 + 已落位宽度，全部按区间相交算。</summary>
        private bool TryAccept(RoomBand band, string ignoreId, float x, float halfWidth,
            RoomWall wall, out string reason)
        {
            reason = string.Empty;

            if (band == RoomBand.Wall)
            {
                if (wall == null)
                {
                    reason = "没有墙面";
                    return false;
                }

                float alongMin = Mathf.Min(wall.alongMin, wall.alongMax) + halfWidth;
                float alongMax = Mathf.Max(wall.alongMin, wall.alongMax) - halfWidth;
                if (alongMax < alongMin)
                {
                    reason = "墙面可用宽度小于物件宽度";
                    return false;
                }

                if (x < alongMin || x > alongMax)
                {
                    reason = "超出墙面可用范围";
                    return false;
                }

                // 墙面矩形禁放区（窗户等）：X 与 Y 同时相交才拒绝
                if (layout != null && layout.WallBlockedByKeepOut(wall, x, halfWidth, _probeYMin, _probeYMax))
                {
                    reason = "落在墙面禁放区（窗户等）";
                    return false;
                }

                if (occupancy != null && occupancy.HasOverlap(RoomBand.Wall, ignoreId, x, halfWidth * 2f,
                        Clearance))
                {
                    reason = "与已挂物件重叠";
                    return false;
                }

                return true;
            }

            if (layout == null)
            {
                reason = "没有 RoomLayout";
                return false;
            }

            bool insideAny = false;
            if (layout.regions != null)
            {
                foreach (RoomRegion region in layout.regions)
                {
                    if (region == null || region.kind != RoomRegionKind.Usable) continue;
                    if (!RoomLayout.ContainsX(region, x, halfWidth)) continue;
                    insideAny = true;
                    break;
                }
            }

            if (!insideAny)
            {
                reason = "超出可用横向区间";
                return false;
            }

            if (layout.BlockedByKeepOut(x, halfWidth))
            {
                reason = "落在禁放区";
                return false;
            }

            if (occupancy != null && occupancy.HasOverlap(band, ignoreId, x, halfWidth * 2f, Clearance))
            {
                reason = "与已放置物件重叠";
                return false;
            }

            return true;
        }

        /// <summary>沿 X 等距扫描主区间，取离建议位置最近的、能通过校验的落点（朝向随该点重新推导）。</summary>
        private bool TryScanAlongX(ObjectSpec spec, LayoutIntent intent, RoomBand band, RoomRegion region,
            float baseX, string ignoreId, Vector3 size, float forcedWallYaw,
            out float resultX, out float resultYaw, out string resultFacing)
        {
            RoomLayout.GetSpan(region, out float xMin, out float xMax);
            return TryScanRange(spec, intent, band, xMin, xMax, baseX, ignoreId, size, region, null, forcedWallYaw,
                out resultX, out resultYaw, out resultFacing);
        }

        /// <summary>沿墙面可用段等距扫描，避开窗户等矩形禁放区。</summary>
        private bool TryScanAlongWall(ObjectSpec spec, LayoutIntent intent, RoomBand band, RoomWall wall,
            float baseX, string ignoreId, Vector3 size, RoomRegion primary, float forcedWallYaw,
            out float resultX, out float resultYaw, out string resultFacing)
        {
            if (wall == null)
            {
                resultX = baseX;
                resultYaw = 0f;
                resultFacing = string.Empty;
                return false;
            }

            float xMin = Mathf.Min(wall.alongMin, wall.alongMax);
            float xMax = Mathf.Max(wall.alongMin, wall.alongMax);
            return TryScanRange(spec, intent, band, xMin, xMax, baseX, ignoreId, size, primary, wall, forcedWallYaw,
                out resultX, out resultYaw, out resultFacing);
        }

        /// <summary>
        /// 在 [xMin, xMax] 上按 gridStep 生成候选，按离建议位置的距离从近到远逐个校验。
        /// 候选不预先按半宽收窄：朝向会随落点变化，宽度必须交给 Evaluate 按该点实际朝向算。
        /// </summary>
        private bool TryScanRange(ObjectSpec spec, LayoutIntent intent, RoomBand band, float xMin, float xMax,
            float baseX, string ignoreId, Vector3 size, RoomRegion primary, RoomWall wall, float forcedWallYaw,
            out float resultX, out float resultYaw, out string resultFacing)
        {
            resultX = baseX;
            resultYaw = 0f;
            resultFacing = string.Empty;

            float step = Mathf.Max(0.1f, gridStep);
            float from = Mathf.Min(xMin, xMax);
            float to = Mathf.Max(xMin, xMax);
            if (to <= from) return false;

            int limit = Mathf.Max(1, maxGridCandidates);
            var candidates = new List<float>(limit);
            for (float x = from; x <= to + 0.0001f && candidates.Count < limit; x += step)
            {
                candidates.Add(x);
            }
            if (candidates.Count == 0) candidates.Add((from + to) * 0.5f);

            candidates.Sort((a, b) => Mathf.Abs(a - baseX).CompareTo(Mathf.Abs(b - baseX)));

            foreach (float x in candidates)
            {
                if (!Evaluate(spec, intent, band, ignoreId, x, size, primary, wall, forcedWallYaw,
                        out float yaw, out string facing, out _, out _)) continue;

                resultX = x;
                resultYaw = yaw;
                resultFacing = facing;
                return true;
            }

            return false;
        }

        private float ClampToSpan(RoomBand band, float x, float halfWidth, RoomRegion primary, RoomWall wall)
        {
            if (band == RoomBand.Wall)
            {
                if (wall == null) return x;
                float alongMin = Mathf.Min(wall.alongMin, wall.alongMax) + halfWidth;
                float alongMax = Mathf.Max(wall.alongMin, wall.alongMax) - halfWidth;
                return alongMax >= alongMin ? Mathf.Clamp(x, alongMin, alongMax)
                    : (wall.alongMin + wall.alongMax) * 0.5f;
            }

            if (primary == null) return x;

            RoomLayout.GetSpan(primary, out float xMin, out float xMax);
            float lo = xMin + halfWidth;
            float hi = xMax - halfWidth;
            return hi >= lo ? Mathf.Clamp(x, lo, hi) : (xMin + xMax) * 0.5f;
        }

        /// <summary>按 yaw 把包围盒投影到世界轴向，得到 XZ 半尺寸（yaw 90° 时长宽互换）。</summary>
        public static void RotatedExtents(Vector3 size, float yawDegrees, out float halfX, out float halfZ)
        {
            float rad = yawDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Abs(Mathf.Cos(rad));
            float sin = Mathf.Abs(Mathf.Sin(rad));
            halfX = (size.x * cos + size.z * sin) * 0.5f;
            halfZ = (size.x * sin + size.z * cos) * 0.5f;
        }

        /// <summary>
        /// 单个候选点的完整判定：先按该 X 解析朝向（朝向会改变实际宽度），
        /// 再按旋转后的真实足迹做区间 / 禁放区 / 重叠校验。
        /// </summary>
        private bool Evaluate(ObjectSpec spec, LayoutIntent intent, RoomBand band, string ignoreId, float x,
            Vector3 size, RoomRegion primary, RoomWall wall, float forcedWallYaw,
            out float yaw, out string facing, out float halfWidth, out string reason)
        {
            yaw = ResolveYaw(spec, intent, band, x, primary, wall, forcedWallYaw, out facing);
            RotatedExtents(size, yaw, out halfWidth, out _);
            return TryAccept(band, ignoreId, x, halfWidth, wall, out reason);
        }

        /// <summary>
        /// 朝向解析顺序：挂墙固定朝向 → 按 id 覆盖表 → 按最终落点贴最近的侧墙
        /// （更近左墙朝右 +90、更近右墙朝左 -90；正好等距时优先采纳 LLM 的同侧 facing，否则贴右墙 -90）。
        /// </summary>
        private float ResolveYaw(ObjectSpec spec, LayoutIntent intent, RoomBand band, float x,
            RoomRegion primary, RoomWall wall, float forcedWallYaw, out string facing)
        {
            if (band == RoomBand.Wall)
            {
                facing = Facings.TowardCamera;
                if (!float.IsNaN(forcedWallYaw)) return forcedWallYaw;
                if (intent != null && Mathf.Abs(intent.yawDegrees) > 0.0001f) return intent.yawDegrees;
                return spec != null ? spec.yawDegrees : 180f;
            }

            string id = spec != null ? spec.NormalizedId : string.Empty;
            if (TryGetYawOverride(id, out float overrideYaw))
            {
                facing = FacingFromYaw(overrideYaw);
                return overrideYaw;
            }

            float xMin = 0f;
            float xMax = 0f;
            if (primary != null) RoomLayout.GetSpan(primary, out xMin, out xMax);

            facing = Facings.FromX(x, xMin, xMax, intent != null ? intent.facing : string.Empty);
            return Facings.ToYaw(facing);
        }

        /// <summary>物件最小间距：有 RoomLayout 时统一用它的 clearance（与 LLM 提示词同源）。</summary>
        private float Clearance
        {
            get
            {
                if (layout != null) return Mathf.Max(0f, layout.clearance);
                return occupancy != null ? Mathf.Max(0f, occupancy.overlapMargin) : 0.05f;
            }
        }

        private bool TryGetYawOverride(string id, out float yawDegrees)
        {
            yawDegrees = 0f;
            if (string.IsNullOrWhiteSpace(id) || yawOverrides == null) return false;

            int count = yawOverrides.Count;
            for (int i = 0; i < count; i++)
            {
                YawOverride entry = yawOverrides[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.id)) continue;
                if (!string.Equals(entry.id.Trim(), id, System.StringComparison.OrdinalIgnoreCase)) continue;

                yawDegrees = entry.yawDegrees;
                return true;
            }

            return false;
        }

        /// <summary>把任意 yaw 归到四个朝向里最近的一个（覆盖表只填角度时用）。</summary>
        private static string FacingFromYaw(float yawDegrees)
        {
            float y = Mathf.Repeat(yawDegrees, 360f);
            if (y > 315f || y <= 45f) return Facings.AwayFromCamera;
            if (y <= 135f) return Facings.Right;
            if (y <= 225f) return Facings.TowardCamera;
            return Facings.Left;
        }

        private static RoomBand ParseBand(string band)
        {
            if (string.Equals(band, "wall", System.StringComparison.OrdinalIgnoreCase)) return RoomBand.Wall;
            if (string.Equals(band, "rooftop", System.StringComparison.OrdinalIgnoreCase)) return RoomBand.Rooftop;
            return RoomBand.Ground;
        }

        private LayoutSolution Success(RoomBand band, string anchorId, float x, float depth, float yaw, string facing,
            string source, ObjectSpec spec, LayoutIntent intent)
        {
            LogFacing(spec, intent, band, x, yaw, facing);
            return new LayoutSolution
            {
                success = true,
                degraded = false,
                band = band,
                anchorId = anchorId,
                localXZ = new Vector3(x, 0f, depth),
                yawDegrees = yaw,
                facing = facing,
                reason = source,
            };
        }

        /// <summary>只在「命中覆盖表」或「LLM 建议与落点不一致」时打日志，正常落位不刷屏。</summary>
        private void LogFacing(ObjectSpec spec, LayoutIntent intent, RoomBand band, float x, float yaw, string facing)
        {
            string id = spec != null ? spec.NormalizedId : string.Empty;
            string name = spec != null ? spec.objectName : string.Empty;

            if (band != RoomBand.Wall && TryGetYawOverride(id, out _))
            {
                Debug.Log($"[PrototypeC] 朝向：{name}（{id}）命中覆盖表 → yaw={yaw:0}°｜{Facings.ToText(facing)}｜" +
                          $"落点 x={x:0.00}");
                return;
            }

            if (intent == null || band == RoomBand.Wall) return;
            if (!Facings.TryNormalize(intent.facing, out string intentFacing)) return;
            if (string.Equals(intentFacing, facing, System.StringComparison.OrdinalIgnoreCase)) return;

            Debug.Log($"[PrototypeC] 朝向以落点为准：{name}（{id}）LLM 建议 {Facings.ToText(intentFacing)}，" +
                      $"但落点 x={x:0.00} → 实际 {Facings.ToText(facing)}（yaw={yaw:0}°）");
        }
    }
}
