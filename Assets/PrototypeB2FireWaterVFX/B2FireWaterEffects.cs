using System.Collections;
using UnityEngine;

internal sealed class B2FireWaterEffects
{
    internal const float GroundFlameWidth = 2.61f;
    private const string ResourcePath = "PrototypeB2FireWaterVFX/";
    private readonly MonoBehaviour _owner;
    private readonly Sprite[] _fireball;
    private readonly Sprite[] _flames;
    private readonly Sprite[] _splash;
    private AudioSource _audio;
    private AudioClip _hiss;

    internal bool IsReady => _fireball != null && _flames != null && _splash != null;

    internal B2FireWaterEffects(MonoBehaviour owner)
    {
        _owner = owner;
        _fireball = Slice("FireballImpact", 96, 64, 15, 72, 38);
        _flames = Slice("GroundFlames", 64, 40, 16, 32, 33);
        _splash = Slice("WaterSplash", 112, 80, 22, 56, 66);
    }

    internal IEnumerator Fireball(Vector3 foot)
    {
        const float flightTime = 0.38f;
        Vector3 start = foot + new Vector3(-1.05f, 2.35f);
        Vector3 heading = foot - start;
        var ball = Spawn("B2 Small Fireball", _fireball[0], start, 0.74f, 5);
        ball.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(heading.y, heading.x) * Mathf.Rad2Deg);

        for (float t = 0f; t < flightTime; t += Time.deltaTime)
        {
            float progress = t / flightTime;
            ball.transform.position = Vector3.Lerp(start, foot, progress * progress);
            ball.sprite = _fireball[(int)(t / 0.055f) % 4];
            yield return null;
        }

        ball.gameObject.SetActive(false);
        Object.Destroy(ball.gameObject);
        var impact = Spawn("B2 Fireball Landing", _fireball[4], foot, 0.80f, 4);
        yield return Animate(impact, _fireball, 4, 4, 0.035f, false);
    }

    internal SpriteRenderer Ignite(Vector3 foot)
    {
        var flames = Spawn("B2 Ground Flames", _flames[0], foot, GroundFlameWidth, 2);
        flames.transform.localScale = new Vector3(GroundFlameWidth, 1.74f, 1f);
        _owner.StartCoroutine(Animate(flames, _flames, 0, _flames.Length, 0.075f, true));
        return flames;
    }

    internal void Droplet(Vector3 position)
    {
        var drop = Spawn("B2 Water Trail", B1Visuals.Circle, position, 0.12f, 4);
        _owner.StartCoroutine(Drift(drop, new Vector3(0f, -0.13f), 0.22f, 0.3f,
            new Color(0.53f, 0.89f, 1f, 0.72f)));
    }

    internal void Quench(Vector3 foot)
    {
        var splash = Spawn("B2 Water Splash", _splash[2], foot, 1.14f, 4);
        _owner.StartCoroutine(Animate(splash, _splash, 2, 13, 0.028f, false));
        _owner.StartCoroutine(Steam(foot));
        PlayHiss();
    }

    private IEnumerator Steam(Vector3 foot)
    {
        yield return new WaitForSeconds(0.08f);
        var color = new Color(0.80f, 0.90f, 0.94f, 0.62f);
        for (int i = 0; i < 3; i++)
        {
            float offset = (i - 1) * 0.13f;
            var puff = Spawn("B2 Steam Puff", B1Visuals.Circle,
                foot + new Vector3(offset, 0.12f + i * 0.10f), 0.18f + i * 0.035f, 3);
            _owner.StartCoroutine(Drift(puff, new Vector3(offset * 0.9f, 0.52f + i * 0.07f),
                0.72f + i * 0.08f, 2.5f, color));
            yield return new WaitForSeconds(0.055f);
        }
    }

    private static IEnumerator Animate(SpriteRenderer renderer, Sprite[] frames, int first,
        int count, float frameTime, bool loop)
    {
        float duration = count * frameTime;
        for (float t = 0f; renderer != null; t += Time.deltaTime)
        {
            if (!loop && t >= duration) break;
            renderer.sprite = frames[first + Mathf.Min((int)(t % duration / frameTime), count - 1)];
            yield return null;
        }
        if (renderer != null) Object.Destroy(renderer.gameObject);
    }

    private static IEnumerator Drift(SpriteRenderer renderer, Vector3 offset, float duration,
        float endScale, Color color)
    {
        Transform visual = renderer.transform;
        Vector3 origin = visual.position;
        Vector3 scale = visual.localScale;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            float progress = t / duration;
            visual.position = origin + offset * progress;
            visual.localScale = scale * Mathf.Lerp(1f, endScale, progress);
            color.a = Mathf.Min(color.a, (1f - progress) * (1f - progress));
            renderer.color = color;
            yield return null;
        }
        Object.Destroy(renderer.gameObject);
    }

    private SpriteRenderer Spawn(string name, Sprite sprite, Vector3 position, float width, int order)
    {
        var renderer = new GameObject(name).AddComponent<SpriteRenderer>();
        renderer.transform.SetParent(_owner.transform, false);
        renderer.transform.position = position;
        renderer.transform.localScale = Vector3.one * width;
        renderer.sprite = sprite;
        renderer.sortingOrder = order;
        return renderer;
    }

    private static Sprite[] Slice(string name, int width, int height, int count, int pivotX, int pivotY)
    {
        var sheet = Resources.Load<Texture2D>(ResourcePath + name);
        int rows = (count + 7) / 8;
        if (sheet == null || sheet.width < width * 8 || sheet.height < height * rows)
        {
            Debug.LogError($"[B2 Fire Water VFX] Missing or invalid texture: {name}.");
            return null;
        }

        var frames = new Sprite[count];
        var pivot = new Vector2(pivotX / (float)width, 1f - pivotY / (float)height);
        for (int i = 0; i < frames.Length; i++)
        {
            var rect = new Rect(i % 8 * width, sheet.height - (i / 8 + 1) * height, width, height);
            frames[i] = Sprite.Create(sheet, rect, pivot, width, 0, SpriteMeshType.FullRect);
        }
        return frames;
    }

    private void PlayHiss()
    {
        if (_hiss == null)
        {
            const int rate = 24000;
            var samples = new float[rate * 32 / 100];
            var random = new System.Random(482);
            float lowPass = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float noise = (float)(random.NextDouble() * 2 - 1);
                lowPass = Mathf.Lerp(lowPass, noise, 0.20f);
                float t = i / (float)samples.Length;
                float envelope = Mathf.Min(t * 35f, 1f) * (1f - t) * (1f - t);
                samples[i] = (noise - lowPass) * envelope * 0.23f;
            }

            _hiss = AudioClip.Create("B2 Steam Hiss", samples.Length, 1, rate, false);
            _hiss.SetData(samples, 0);
            _audio = _owner.gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
        }

        _audio.PlayOneShot(_hiss, 0.72f);
    }

    internal void Release()
    {
        foreach (var sheet in new[] { _fireball, _flames, _splash })
        {
            if (sheet == null) continue;
            foreach (Sprite frame in sheet) Object.Destroy(frame);
        }
        if (_hiss != null) Object.Destroy(_hiss);
    }
}
