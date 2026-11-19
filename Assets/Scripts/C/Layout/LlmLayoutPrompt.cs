// LlmLayoutPrompt.cs —— Prototype C：LLM 布局（阶段 4）的 System / User Prompt
// 场景是「3D 伪 2D 横向卷轴」：正交相机从正面看，物件只沿 X 排开，Z 是每个分带的固定值。
// 输入是「物件实际宽度 + 房间的可用/禁放 X 区间 + 各带高度与固定 depth + 同分带已占用物件」，
// 输出只允许一个扁平 JSON 意图对象（全部值为字符串，便于复用严格解析器）。

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace PrototypeC
{
    public static class LlmLayoutPrompt
    {
        public const string SystemPrompt =
@"You are an interior layout planner for a 3D game prototype: a small, cozy, unfinished apartment inhabited by a dinosaur.

The game is a SIDE-SCROLLING 3D scene: an orthographic camera looks straight at the room from the front, so the room spreads out HORIZONTALLY on screen. Items stand in a line along X. Depth (Z) is fixed by the band and is NOT a layout dimension: never try to use depth.

You receive ONE furniture or decor item and a precise geometric description of the room. Decide WHERE the item goes along X. A deterministic solver will validate your answer and may nudge it, so prefer sensible, non-overlapping placements over creative ones.

COORDINATE SYSTEM
- All positions are meters in the room's local frame.
- X: left (-) to right (+). This is the ONLY axis you place along.
- Y is not used in your answer: the band decides the height (Ground = on the floor, Wall = hung on a wall, Rooftop = near the ceiling).
- Z is fixed per band (floorDepth / wall planeZ / ceilingDepth) and must not appear in your answer.

ROOM INPUT (the user message contains json)
- roomSize: width (X), height (Y), depth (Z); depth is informational only. width is derived from the usable segments.
- clearance: the minimum gap in meters the solver enforces between items.
- floorY: floor height in local Y.
- bands: the vertical range of each band (ground / wall / ceiling).
- usableX: horizontal segments where items may stand, each with id, xMin, xMax, depth. An item must stay fully inside ONE segment.
- usableCenterX: center of the main usable segment. X values greater than it are the RIGHT half, smaller are the LEFT half; this decides facing.
- keepOutX: horizontal segments that must stay empty by design (reserved area, scripted zone, clearance). The list is often empty: when it is empty, the whole usableX range is available. NEVER overlap an entry.
- walls: id, planeZ, alongMin / alongMax (allowed X range), yMin / yMax (height above the floor).
- wallKeepOuts: rectangles ON a wall that must stay empty (windows, air vents), each with wall id, xMin / xMax and yMin / yMax above the floor. Often empty.
- anchors: named horizontal reference points. x is the anchor position, maxOffsetX is how far you may move from it.
- occupants: items already placed in the same band, each with x and width. Do not overlap them.

DECISION RULES
1. band must stay exactly the band of the item. Never move an item to another band.
2. Choose exactly ONE anchor from the anchor list whose band matches the item. Prefer the anchor that naturally fits this kind of object.
3. The offset is horizontal only: offsetX, within +-maxOffsetX of the chosen anchor. Keep it small. Do NOT output offsetZ or any depth value.
4. Wall: set alongWall to the ABSOLUTE X position on the wall (inside alongMin..alongMax) and offsetX to ""0"".
5. The solver keeps each item's full width inside the usable segments and clear of keepOut segments; keep at least room.clearance meters between neighbours (the solver uses exactly the same value).
6. Distribute items along X instead of stacking everything at the center. Large or heavy pieces go toward the ends of the room; small props can sit beside larger items but must not overlap.
7. Spread wall decorations horizontally: leave at least footprint width + room.clearance meters between two wall items.
8. Respect every keepOutX segment: those spans are reserved and must stay empty. If keepOutX is empty, ignore this rule.
9. Never place a wall item whose X span overlaps a wallKeepOuts rectangle (window, vent). The solver will reject it; pick another X inside alongMin..alongMax.
10. FACING (most important for furniture): this is a side view, so the LEFT and RIGHT walls are the side walls. An item's BACK must rest against the NEAREST side wall and its front must point INTO the room.
    - item that ends up in the RIGHT half of the room -> facing = ""left"" (back against the right wall, front toward the room center)
    - item that ends up in the LEFT half of the room -> facing = ""right"" (back against the left wall, front toward the room center)
    - every Wall item -> facing = ""toward_camera""
    Never turn an item's back to the room (for example a bed in the right half facing right is WRONG).
11. facing and offsetX must agree: the solver derives the real rotation AFTER the final X is fixed and will override a contradicting facing.
12. yawDegrees is only a fallback used when facing is empty. Keep 90 as default: left = -90, right = 90, toward_camera = 180.
13. Only use anchor ids that appear in the room description. Do not invent anchors or coordinates outside the room.

EXAMPLES (offsetX is relative to the anchor; the same rule applies to any anchor)
- Bed, anchor ""ground.center"", offsetX ""2.4"" (right half) -> facing ""left"", yawDegrees ""-90""
- Bookshelf, anchor ""ground.center"", offsetX ""-2.6"" (left half) -> facing ""right"", yawDegrees ""90""
- Wall painting, band Wall, alongWall ""1.5"" -> facing ""toward_camera"", yawDegrees ""180""

OUTPUT
Return ONLY one JSON object. No markdown, no code fences, no explanation.
{""band"":""Ground|Wall|Rooftop"",""anchor"":""<anchor id>"",""offsetX"":""<number>"",""facing"":""left|right|toward_camera|away_from_camera"",""yawDegrees"":""<number>"",""alongWall"":""<number>"",""confidence"":""<0..1>"",""hint"":""<short English reason>""}

All eight values are strings. Numbers use a dot as the decimal separator. No extra keys.";

        public static string BuildUserPrompt(ObjectSpec spec, Vector3 size, RoomBand band, RoomLayout layout,
            IReadOnlyList<PlacedItem> occupants, int maxOccupants)
        {
            var sb = new StringBuilder(2048);

            sb.Append("ITEM\n");
            sb.Append("objectName: ").Append(spec != null ? spec.objectName : string.Empty).Append('\n');
            sb.Append("behavior: ").Append(spec != null ? spec.behavior : string.Empty).Append('\n');
            sb.Append("sizeClass: ").Append(spec != null ? spec.sizeClass : string.Empty).Append('\n');
            sb.Append("band: ").Append(RoomBands.ToPlacementKind(band)).Append('\n');
            sb.Append("footprint: ").Append(N(Mathf.Abs(size.x))).Append(" m wide (X), ")
                .Append(N(Mathf.Abs(size.y))).Append(" m tall (Y)\n\n");

            sb.Append("ROOM\n");
            AppendRoomJson(sb, layout);
            sb.Append("\n\n");

            List<PlacedItem> same = CollectOccupants(occupants, band, Mathf.Max(0, maxOccupants));
            sb.Append("OCCUPANTS (same band, ")
                .Append(same.Count == 0 ? "none" : same.Count + " listed")
                .Append(")\n");

            foreach (PlacedItem item in same)
            {
                sb.Append("- ").Append(string.IsNullOrWhiteSpace(item.objectName) ? item.id : item.objectName)
                    .Append(" at x=").Append(N(item.localCenter.x))
                    .Append(", width ").Append(N(Mathf.Abs(item.size.x))).Append(" m\n");
            }

            sb.Append("\nReturn the json object now.");
            return sb.ToString();
        }

        /// <summary>同分带的占用记录；数量多时保留最近放入的几条（对当前落点影响最大）。</summary>
        private static List<PlacedItem> CollectOccupants(IReadOnlyList<PlacedItem> occupants, RoomBand band, int max)
        {
            var result = new List<PlacedItem>();
            if (occupants == null || max <= 0) return result;

            for (int i = occupants.Count - 1; i >= 0 && result.Count < max; i--)
            {
                PlacedItem item = occupants[i];
                if (item == null || item.band != band) continue;
                result.Insert(0, item);
            }

            return result;
        }

        private static void AppendRoomJson(StringBuilder sb, RoomLayout layout)
        {
            if (layout == null)
            {
                sb.Append("{}");
                return;
            }

            Vector3 described = layout.DescribedRoomSize;
            sb.Append("{\"roomSize\":{\"width\":").Append(N(described.x))
                .Append(",\"height\":").Append(N(described.y))
                .Append(",\"depth\":").Append(N(described.z))
                .Append("},\"floorY\":").Append(N(layout.floorY))
                .Append(",\"clearance\":").Append(N(Mathf.Max(0f, layout.clearance)))
                .Append(",\"floorDepth\":").Append(N(layout.groundDepthZ))
                .Append(",\"ceilingDepth\":").Append(N(layout.ceilingDepthZ));

            // 朝向参考：主可用区间的中心（左右半区以它为界）
            if (layout.TryGetPrimaryRegion(out RoomRegion centerRegion))
            {
                RoomLayout.GetSpan(centerRegion, out float centerMin, out float centerMax);
                sb.Append(",\"usableCenterX\":").Append(N((centerMin + centerMax) * 0.5f));
            }

            // 各带的高度区间：告诉 AI 地面/墙/天花板分别在哪一段 Y
            ObjectModelService service = layout.service;
            sb.Append(",\"bands\":{");
            if (service != null)
            {
                sb.Append("\"ground\":{\"yMin\":").Append(N(service.groundBandMinY))
                    .Append(",\"yMax\":").Append(N(service.groundBandMaxY)).Append("},");
                sb.Append("\"wall\":{\"yMin\":").Append(N(service.wallBandMinY))
                    .Append(",\"yMax\":").Append(N(service.wallBandMaxY)).Append("},");
                sb.Append("\"ceiling\":{\"yMin\":").Append(N(service.ceilingBandMinY))
                    .Append(",\"yMax\":").Append(N(service.ceilingBandMaxY)).Append('}');
            }
            sb.Append('}');

            // 可用 X 区间 与 禁放 X 区间 分成两个数组，避免 AI 把两者混成矩形
            sb.Append(",\"usableX\":[");
            AppendSpans(sb, layout, RoomRegionKind.Usable);

            sb.Append("],\"keepOutX\":[");
            AppendSpans(sb, layout, RoomRegionKind.KeepOut);

            sb.Append("],\"walls\":[");
            if (layout.walls != null)
            {
                bool first = true;
                foreach (RoomWall wall in layout.walls)
                {
                    if (wall == null) continue;

                    float alongMin = Mathf.Min(wall.alongMin, wall.alongMax);
                    float alongMax = Mathf.Max(wall.alongMin, wall.alongMax);
                    float yMin = Mathf.Min(wall.yMin, wall.yMax);
                    float yMax = Mathf.Max(wall.yMin, wall.yMax);

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append("{\"id\":\"").Append(Escape(wall.id))
                        .Append("\",\"planeZ\":").Append(N(wall.planeZ))
                        .Append(",\"alongMin\":").Append(N(alongMin))
                        .Append(",\"alongMax\":").Append(N(alongMax))
                        .Append(",\"yMin\":").Append(N(yMin))
                        .Append(",\"yMax\":").Append(N(yMax))
                        .Append('}');
                }
            }

            sb.Append("],\"wallKeepOuts\":[");
            if (layout.wallKeepOuts != null)
            {
                bool first = true;
                foreach (RoomWallKeepOut keepOut in layout.wallKeepOuts)
                {
                    if (keepOut == null) continue;

                    float kxMin = Mathf.Min(keepOut.xMin, keepOut.xMax);
                    float kxMax = Mathf.Max(keepOut.xMin, keepOut.xMax);
                    float kyMin = Mathf.Min(keepOut.yMin, keepOut.yMax);
                    float kyMax = Mathf.Max(keepOut.yMin, keepOut.yMax);

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append("{\"wall\":\"").Append(Escape(keepOut.wallId))
                        .Append("\",\"xMin\":").Append(N(kxMin))
                        .Append(",\"xMax\":").Append(N(kxMax))
                        .Append(",\"yMin\":").Append(N(kyMin))
                        .Append(",\"yMax\":").Append(N(kyMax))
                        .Append('}');
                }
            }

            sb.Append("],\"anchors\":[");
            if (layout.anchors != null)
            {
                bool first = true;
                foreach (RoomAnchorPoint point in layout.anchors)
                {
                    if (point == null) continue;

                    if (!first) sb.Append(',');
                    first = false;

                    sb.Append("{\"id\":\"").Append(Escape(point.id))
                        .Append("\",\"band\":\"").Append(RoomBands.ToPlacementKind(point.band))
                        .Append("\",\"x\":").Append(N(point.x))
                        .Append(",\"maxOffsetX\":").Append(N(Mathf.Max(0f, point.maxOffsetX)))
                        .Append('}');
                }
            }

            sb.Append("]}");
        }

        private static void AppendSpans(StringBuilder sb, RoomLayout layout, RoomRegionKind kind)
        {
            if (layout.regions == null) return;

            bool first = true;
            foreach (RoomRegion region in layout.regions)
            {
                if (region == null || region.kind != kind) continue;

                RoomLayout.GetSpan(region, out float xMin, out float xMax);

                if (!first) sb.Append(',');
                first = false;

                sb.Append("{\"id\":\"").Append(Escape(region.id))
                    .Append("\",\"xMin\":").Append(N(xMin))
                    .Append(",\"xMax\":").Append(N(xMax))
                    .Append(",\"depth\":").Append(N(layout.groundDepthZ))
                    .Append('}');
            }
        }

        private static string N(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>id 只会是英文短语，这里仍做最小转义，避免配置里带引号时拼出坏 JSON。</summary>
        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
