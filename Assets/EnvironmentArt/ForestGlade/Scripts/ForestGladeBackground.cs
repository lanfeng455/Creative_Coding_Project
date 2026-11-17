using System;
using UnityEngine;

/// <summary>Visual background only. The subject and world configuration are read-only inputs.</summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class ForestGladeBackground : MonoBehaviour
{
    [Serializable]
    public struct Layer
    {
        public SpriteRenderer renderer;
        public Vector2 maximumUvOffset;
    }

    [SerializeField] private B1LayerConfig world;
    [SerializeField] private Transform subject;
    [SerializeField] private Layer[] layers = Array.Empty<Layer>();
    [SerializeField] private Vector3 referencePosition;
    [SerializeField, Min(0.1f)] private float response = 2.5f;
    [SerializeField, Range(0.6f, 0.9f)] private float groundLineFromTop = 0.755f;
    [Tooltip("The lower separator. Soil stops at its top edge, leaving the area below it clear.")]
    [SerializeField] private SpriteRenderer groundBottomBoundary;
    [Tooltip("Soil thickness in world units when no lower separator is assigned.")]
    [SerializeField, Range(0f, 1.5f)] private float groundEdgeThickness = 0.54f;
    private Vector2 smoothedOffset;
    private MaterialPropertyBlock properties;
    private static readonly int Scroll = Shader.PropertyToID("_Scroll");
    private static readonly int ClipRect = Shader.PropertyToID("_ClipRect");

    public void Configure(B1LayerConfig configuration, Transform target, Layer[] backgroundLayers,
        SpriteRenderer bottomBoundary = null)
    {
        world = configuration;
        subject = target;
        layers = backgroundLayers;
        groundBottomBoundary = bottomBoundary;
        referencePosition = target != null ? target.position : Vector3.zero;
        smoothedOffset = Vector2.zero;
        UpdateParallax(0f);
    }

    private void OnEnable()
    {
        smoothedOffset = Vector2.zero;
        foreach (var layer in layers)
            if (layer.renderer != null) layer.renderer.enabled = true;
        UpdateParallax(0f);
    }

    private void LateUpdate() => UpdateParallax(Time.deltaTime);

    /// <summary>Also usable in an editor preview with a temporary stand-in target.</summary>
    public void UpdateParallax(float deltaTime)
    {
        if (world == null) return;
        properties ??= new MaterialPropertyBlock();
        float width = world.boundsXMax - world.boundsXMin;
        if (width <= 0f) return;
        float top = world.upperCeilingY + 0.4f;
        float height = (top - world.upperFloorY) / groundLineFromTop;
        if (height <= 0f) return;
        float bottom = top - height;
        float visibleBottom = groundBottomBoundary != null
            ? groundBottomBoundary.bounds.max.y
            : world.upperFloorY - groundEdgeThickness;
        visibleBottom = Mathf.Clamp(visibleBottom, bottom, world.upperFloorY);
        Vector2 target = Vector2.zero;
        if (subject != null)
        {
            Vector3 displacement = subject.position - referencePosition;
            target = new Vector2(Mathf.Clamp(displacement.x / (width * 0.5f), -1f, 1f),
                Mathf.Clamp(displacement.y / 2f, -1f, 1f));
        }
        smoothedOffset = Vector2.Lerp(smoothedOffset, target,
            1f - Mathf.Exp(-response * Mathf.Max(0f, deltaTime)));

        for (int i = 0; i < layers.Length; i++)
        {
            Layer layer = layers[i];
            SpriteRenderer sprite = layer.renderer;
            if (sprite == null || sprite.sprite == null) continue;
            // Every image keeps the same full canvas. Only sampling moves; the
            // stage boundary, fixed ground and lower interaction area never drift.
            sprite.transform.position = new Vector3((world.boundsXMin + world.boundsXMax) * 0.5f,
                (top + bottom) * 0.5f, 5f - i * 0.02f);
            Vector3 size = sprite.sprite.bounds.size;
            sprite.transform.localScale = new Vector3(width / size.x, height / size.y, 1f);
            // The existing screen-space-camera backdrop uses order 0 at z=90.
            // Match that order and sort our five layers by depth, ahead of the
            // backdrop but behind gameplay at z=0. Negative orders hide in Play Mode.
            sprite.sortingOrder = 0;
            float right = i == 3 ? world.upperFloorXMax : world.boundsXMax;
            properties.SetVector(ClipRect, new Vector4(world.boundsXMin,
                visibleBottom, right, top));
            properties.SetVector(Scroll, new Vector4(smoothedOffset.x * layer.maximumUvOffset.x,
                smoothedOffset.y * layer.maximumUvOffset.y, 0f, 0f));
            sprite.SetPropertyBlock(properties);
        }
    }

    private void OnDisable()
    {
        foreach (var layer in layers)
            if (layer.renderer != null) layer.renderer.enabled = false;
    }
}
