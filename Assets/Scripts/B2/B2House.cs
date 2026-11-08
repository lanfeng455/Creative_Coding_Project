// B2House.cs —— 恐龙之家：PCG 火柴盒房间（泰拉瑞亚式堆叠）
// 恐龙每交一份建材（WOOD/STONE）→ AddRoom 盖一间火柴盒：
//   · 字符画墙体（+ - | 角/横/竖），材质定色（木棕/石灰），底排房间带门洞
//   · 新房间随机接在现有房间的左/右/上方（横向 60% / 向上 40%），上层房间下方必须有支撑
// 红色威胁字符可以命中并摧毁一个房间（RemoveRoom）。

using System.Collections.Generic;
using UnityEngine;
using TMPro;

[DisallowMultipleComponent]
public class B2House : MonoBehaviour
{
    [Header("房间尺寸（世界单位）")]
    public float roomW = 2.3f;
    public float roomH = 1.7f;
    [Tooltip("最多几间房（建满后恐龙不再要建材）")]
    public int maxRooms = 6;
    [Tooltip("最高堆几层")]
    public int maxRows = 3;

    [Header("材质颜色")]
    public Color woodColor = new Color(0.72f, 0.5f, 0.28f);
    public Color stoneColor = new Color(0.62f, 0.65f, 0.7f);

    public class Room
    {
        public int col, row;
        public string material;
        public GameObject root;
        public Vector3 center;
        public Color color;
    }

    private readonly List<Room> _rooms = new();
    private readonly HashSet<Vector2Int> _occupied = new();
    private B1LayerConfig _world;
    private Vector3 _origin; // 首间房中心
    private bool _inited;

    public IReadOnlyList<Room> Rooms => _rooms;
    public bool CanGrow => _inited && _rooms.Count < maxRooms;

    /// <summary>恐龙盖房时站的工地 x（首间房门口）</summary>
    public float BuildSiteX => _origin.x;

    public void Init(B1LayerConfig world, float originX)
    {
        _world = world;
        _origin = new Vector3(originX, world.upperFloorY + roomH * 0.5f - 0.08f, 0f);
        transform.position = _origin;
        _inited = true;
    }

    /// <summary>盖一间房。返回新房（失败返回 null）。material: "WOOD"/"STONE"</summary>
    public Room AddRoom(string material)
    {
        if (!CanGrow) return null;

        Vector2Int cell;
        if (_rooms.Count == 0)
        {
            cell = Vector2Int.zero;
        }
        else
        {
            // 候选：每个已占格子的 左/右/上 邻位（上方要求不超限），按 横向60%/向上40% 加权随机
            var cands = new List<Vector2Int>();
            var ups = new List<Vector2Int>();
            foreach (var c in _occupied)
            {
                TryAdd(c + Vector2Int.left, cands, ups, false);
                TryAdd(c + Vector2Int.right, cands, ups, false);
                TryAdd(c + Vector2Int.up, cands, ups, true);
            }
            if (cands.Count == 0 && ups.Count == 0) return null;
            bool goUp = ups.Count > 0 && (cands.Count == 0 || Random.value < 0.4f);
            var pool = goUp ? ups : cands;
            cell = pool[Random.Range(0, pool.Count)];
        }

        var room = new Room
        {
            col = cell.x, row = cell.y,
            material = material,
            color = material == "STONE" ? stoneColor : woodColor,
            center = CellCenter(cell),
        };
        room.root = BuildRoomVisual(room);
        _rooms.Add(room);
        _occupied.Add(cell);

        // 落成特效
        B1Burst.Shatter(room.center, room.color, 10, 4.5f);
        return room;
    }

    private void TryAdd(Vector2Int c, List<Vector2Int> side, List<Vector2Int> up, bool isUp)
    {
        if (_occupied.Contains(c)) return;
        if (c.y < 0 || c.y >= maxRows) return;
        if (c.y > 0 && !_occupied.Contains(c + Vector2Int.down)) return; // 上层必须有支撑
        (isUp ? up : side).Add(c);
    }

    private Vector3 CellCenter(Vector2Int c)
        => _origin + new Vector3(c.x * roomW, c.y * roomH, 0f);

    /// <summary>红字符随机挑一个房间来砸（没有则返回 null）</summary>
    public Room RandomRoom()
    {
        if (_rooms.Count == 0) return null;
        return _rooms[Random.Range(0, _rooms.Count)];
    }

    /// <summary>摧毁一个房间（红字符命中）：碎裂反馈 + 从结构里移除</summary>
    public void DestroyRoom(Room room)
    {
        if (room == null || !_rooms.Contains(room)) return;
        // 若上方有房间，先一起塌（简化：只拆本间，支撑检查留给 TryAdd 的下次）
        B1Burst.Shatter(room.center, room.color, 14, 5f);
        B1Burst.Shatter(room.center, new Color(1f, 0.3f, 0.3f), 6, 4f);
        AudioManager.PlayAt(Sfx.ThreatHit, room.center);
        if (room.root != null) Destroy(room.root);
        _rooms.Remove(room);
        _occupied.Remove(new Vector2Int(room.col, room.row));
    }

    // —— 字符画房间 ——

    private const int Cols = 6, Rows = 4;

    private GameObject BuildRoomVisual(Room room)
    {
        var root = new GameObject($"Room_{room.col}_{room.row}_{room.material}");
        root.transform.SetParent(transform, false);
        root.transform.position = room.center;

        float cw = roomW / Cols;   // 字符单元宽
        float ch = roomH / Rows;   // 字符单元高
        // TMP 字号 ≈ 单元格尺寸 ×10（字号10 ≈ 1 世界单位高），墙字随房间缩放
        float fontSz = Mathf.Min(cw, ch) * 10f;
        int doorCol = Random.Range(1, Cols - 2); // 门洞位置（仅底排房间）

        for (int r = 0; r < Rows; r++)
        for (int c = 0; c < Cols; c++)
        {
            bool top = r == Rows - 1, bottom = r == 0;
            bool left = c == 0, right = c == Cols - 1;
            char? glyph = null;

            if (top || bottom || left || right)
            {
                // 墙
                bool corner = (top || bottom) && (left || right);
                glyph = corner ? '+' : (top || bottom) ? '-' : '|';
                // 底排房间的门洞
                if (bottom && room.row == 0 && c == doorCol) glyph = null;
            }

            if (glyph == null) continue;

            var tm = B1Text.Make($"wall_{r}_{c}", root.transform, fontSz, room.color, 1);
            tm.text = glyph.ToString();
            tm.transform.localPosition = new Vector3(
                (c - (Cols - 1) * 0.5f) * cw,
                (r - (Rows - 1) * 0.5f) * ch,
                0f);
        }
        return root;
    }
}
