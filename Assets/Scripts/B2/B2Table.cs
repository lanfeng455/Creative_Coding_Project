// B2Table.cs —— 用字符画拼的小桌子（取代原来的恐龙巢穴）
// 桌面 = 一行 5×7 字符，桌腿 = 左右各一列字符。造型极简，放在上层地板供恐龙闲逛/坐靠。
// 字符贴图复用 LetterPlant.GlyphSprite（运行时程序贴图）。

using UnityEngine;

[DisallowMultipleComponent]
public class B2Table : MonoBehaviour
{
    [Header("引用（留空自动 Find）")]
    public B1LayerConfig world;

    [Header("位置（桌面中心 x；y 自动=上层地板）")]
    public float tableX = -6f;

    [Header("形状")]
    [Tooltip("桌面字符数")]
    public int topCols = 9;
    [Tooltip("桌腿高度（字符数）")]
    public int legRows = 4;
    [Tooltip("单字符世界高度")]
    public float cellSize = 0.22f;
    public Color tableColor = new Color(0.72f, 0.52f, 0.34f, 0.95f);

    private void Start()
    {
        if (world == null) world = FindObjectOfType<B1LayerConfig>();
        float groundY = world != null ? world.upperFloorY : transform.position.y;
        float x = world != null
            ? Mathf.Clamp(tableX, world.upperFloorXMin + 2.5f, world.GapXMin - 2.5f)
            : tableX;
        Build(new Vector3(x, groundY, 0.1f)); // z 推远，画在恐龙身后
    }

    private void Build(Vector3 rootPos)
    {
        transform.position = rootPos;

        float cellH = Mathf.Max(0.06f, cellSize);
        float cellW = cellH * 5f / 7f;   // 5×7 字符宽高比
        int half = topCols / 2;

        // 桌面：一行字符
        for (int c = 0; c < topCols; c++)
        {
            var go = new GameObject($"Top_{c}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3((c - half) * cellW, legRows * cellH + cellH * 0.5f, 0f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = LetterPlant.GlyphSprite(
                LetterPlant.LeafAlphabet[Random.Range(0, LetterPlant.LeafAlphabet.Length)]);
            sr.color = tableColor;
            sr.sortingOrder = 1;
            go.transform.localScale = Vector3.one * cellH;
        }

        // 两根桌腿（左、右各一列）
        int[] legCols = { 1, topCols - 2 };
        foreach (int lc in legCols)
        {
            for (int r = 0; r < legRows; r++)
            {
                var go = new GameObject($"Leg_{lc}_{r}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3((lc - half) * cellW, r * cellH + cellH * 0.5f, 0f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = LetterPlant.GlyphSprite(
                    LetterPlant.LeafAlphabet[Random.Range(0, LetterPlant.LeafAlphabet.Length)]);
                sr.color = tableColor;
                sr.sortingOrder = 1;
                go.transform.localScale = Vector3.one * cellH;
            }
        }
    }
}
