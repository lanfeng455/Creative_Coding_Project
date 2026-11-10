using System.Collections;
using UnityEngine;

internal static class B2SpriteOneShot
{
    internal static void Play(MonoBehaviour owner, Sprite[] frames, string effectName,
        Vector3 position, float width, float frameTime, Color tint, int sortingOrder)
    {
        var effect = new GameObject(effectName);
        effect.transform.SetParent(owner.transform);
        effect.transform.position = position;
        effect.transform.localScale = Vector3.one * width;
        var sprite = effect.AddComponent<SpriteRenderer>();
        sprite.color = tint;
        sprite.sortingOrder = sortingOrder;
        owner.StartCoroutine(Animate(sprite, frames, frameTime));
        Object.Destroy(effect, frames.Length * frameTime);
    }

    private static IEnumerator Animate(SpriteRenderer sprite, Sprite[] frames, float frameTime)
    {
        float elapsed = 0f;
        while (sprite != null && elapsed < frames.Length * frameTime)
        {
            sprite.sprite = frames[Mathf.Min((int)(elapsed / frameTime), frames.Length - 1)];
            yield return null;
            elapsed += Time.deltaTime;
        }
    }

    internal static Sprite[] Slice(Texture2D sheet, int width, int height, int count, Vector2 pivot)
    {
        if (sheet == null) return null;
        var frames = new Sprite[count];
        for (int i = 0; i < count; i++)
        {
            var cell = new Rect(i % 8 * width, sheet.height - (i / 8 + 1) * height, width, height);
            frames[i] = Sprite.Create(sheet, cell, pivot, width, 0, SpriteMeshType.FullRect);
        }
        return frames;
    }

    internal static void Release(Sprite[] frames)
    {
        if (frames == null) return;
        foreach (Sprite frame in frames) Object.Destroy(frame);
    }
}
