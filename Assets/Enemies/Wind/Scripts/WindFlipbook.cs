using UnityEngine;

// Both current and legacy PVFX grid sheets start at the top left.
internal sealed class WindFlipbook
{
    readonly Sprite[] frames;
    readonly float secondsPerFrame;
    internal readonly SpriteRenderer Renderer;
    internal float Duration => frames.Length * secondsPerFrame;

    internal WindFlipbook(Transform parent, Texture2D sheet, int width, int height, int count,
        float frameTime, float worldHeight, int sortingOrder)
    {
        secondsPerFrame = frameTime;
        frames = new Sprite[count];
        int columns = sheet.width / width;
        for (int i = 0; i < count; i++)
        {
            var rect = new Rect((i % columns) * width, sheet.height - (i / columns + 1) * height, width, height);
            frames[i] = Sprite.Create(sheet, rect, new Vector2(.5f, 0f), height / worldHeight, 0, SpriteMeshType.FullRect);
        }
        var root = new GameObject(sheet.name);
        root.transform.SetParent(parent, false);
        Renderer = root.AddComponent<SpriteRenderer>();
        Renderer.sortingOrder = sortingOrder;
        Renderer.enabled = false;
    }

    internal void Draw(float age, Vector3 position, bool loop, float opacity = 1f)
    {
        int index = Mathf.FloorToInt(age / secondsPerFrame);
        Renderer.enabled = age >= 0 && (loop || index < frames.Length);
        if (!Renderer.enabled) return;
        Renderer.sprite = frames[index % frames.Length];
        Renderer.transform.position = position;
        var color = Renderer.color;
        color.a = Mathf.Clamp01(opacity);
        Renderer.color = color;
    }

    internal void Dispose()
    {
        foreach (var frame in frames) Object.Destroy(frame);
    }
}
