using UnityEngine;

// Keep B1Item registration and lower-floor physics so the existing slingshot
// and the dinosaur's WATER request can still see the same item.
public sealed class B2FireWaterItem : B1Water
{
    private B2FireWaterEncounter _encounter;
    private bool _inDelivery;
    private float _dryTime;

    internal void Bind(B2FireWaterEncounter encounter, B1Water original)
    {
        _encounter = encounter;
        gravity = original.gravity;
        groundBounce = original.groundBounce;
        wallBounce = original.wallBounce;
        drag = original.drag;
        waterColor = original.waterColor;
        _sr.color = waterColor;
        Launch(original.Velocity);
    }

    // The B2 item has its own Unity Update message. B1Water's automatic
    // homing and collision are replaced for this item only.
    private void Update()
    {
        if (_inDelivery) return;

        StepPhysics(Time.deltaTime);
        if (!Alive) return;

        if (_world.OnUpperFloor(transform.position) && _encounter.TryReceive(this)) return;

        _dryTime = FireHazard.ActiveFire == null && IsResting ? _dryTime + Time.deltaTime : 0f;
        if (_dryTime >= 5f) Consume();
    }

    internal void BeginDelivery()
    {
        _inDelivery = true;
        Launch(Vector2.zero);
    }

    internal void HideForWindup() => _sr.enabled = false;

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
