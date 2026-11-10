using UnityEngine;

// Stay registered as WATER while the encounter animates the shot.
public sealed class B2FireWaterItem : B1Water
{
    protected override void Awake()
    {
        base.Awake();
        _sr.enabled = false;
        enabled = false;
    }

    internal void ShowSpitAt(Vector3 mouth)
    {
        transform.position = mouth;
        transform.localScale = Vector3.one * 0.18f;
        _sr.sortingOrder = 5;
        _sr.enabled = true;

        var highlight = new GameObject("Water Highlight").AddComponent<SpriteRenderer>();
        highlight.transform.SetParent(transform, false);
        highlight.transform.localPosition = new Vector3(-0.16f, 0.19f);
        highlight.transform.localScale = new Vector3(0.3f, 0.23f, 1f);
        highlight.sprite = B1Visuals.Circle;
        highlight.color = new Color(0.82f, 1f, 1f);
        highlight.sortingOrder = 6;
    }

    internal void GrowSpitBall(float time)
        => transform.localScale = Vector3.one * Mathf.Lerp(0.18f, 0.34f, time / 0.065f);

    internal void Consume()
    {
        Alive = false;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }
}
