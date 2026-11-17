using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Single source for the six material requests, their order and their delivery icons.</summary>
public static class AFrameBuildPlan
{
    private static readonly string[] Words = { "STONE", "WOOD", "BEAM", "PLANK", "TILE", "GLASS" };
    public const int StageCount = 6;
    public static string WordAt(int stage) => stage >= 0 && stage < StageCount ? Words[stage] : null;
    public static bool IsMaterial(string word) => Array.IndexOf(Words, word) >= 0;
    private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

    // Small code-authored material swatches: no third-party textures or downloaded models.
    public static Sprite Icon(string word)
    {
        if (Icons.TryGetValue(word, out Sprite cached) && cached != null) return cached;
        const int w = 40, h = 24;
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { name = "AFrame_" + word, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
        var pixels = new Color32[w * h];
        Color wood = new Color(0.59f, 0.42f, 0.27f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool solid;
                Color color = wood;
                switch (word)
                {
                    case "STONE":
                        solid = y > 1 && y < 18 && x > 4 + Mathf.Abs(y - 10) / 2 && x < 36 - Mathf.Abs(y - 9) / 3;
                        color = y > 11 ? new Color(0.64f, 0.66f, 0.59f) : new Color(0.43f, 0.47f, 0.42f);
                        break;
                    case "GLASS":
                        solid = x > 6 && x < 33 && y > 1 && y < 22;
                        color = x < 9 || x > 30 || y < 4 || y > 19 ? wood : new Color(0.60f, 0.82f, 0.79f, 0.75f);
                        if (Mathf.Abs(x - y - 9) < 2) color = new Color(0.84f, 0.95f, 0.88f);
                        break;
                    case "TILE":
                        solid = y > 1 && y < 18 && x > 5 + y / 5 && x < 36 - y / 6;
                        color = y % 6 == 0 ? new Color(0.25f, 0.36f, 0.34f) : new Color(0.40f, 0.51f, 0.45f);
                        break;
                    case "PLANK":
                        solid = x > 2 && x < 38 && y > 1 && y < 18 && y % 6 != 0;
                        color *= y % 6 == 5 ? 1.2f : 1f;
                        break;
                    default:
                        solid = x > 2 && x < 38 && y > 1 && y < (word == "BEAM" ? 10 : 15);
                        color = x > 30 ? new Color(0.77f, 0.61f, 0.39f) : wood * (y > 8 ? 1.15f : 0.9f);
                        break;
                }
                pixels[y * w + x] = solid ? color : Color.clear;
            }
        texture.SetPixels32(pixels); texture.Apply(false, true);
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 40f);
        sprite.name = texture.name; sprite.hideFlags = HideFlags.HideAndDontSave;
        Icons[word] = sprite;
        return sprite;
    }
}
