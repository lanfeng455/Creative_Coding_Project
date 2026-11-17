using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Read-only presentation adapter. No input, physics or gameplay state is changed.</summary>
[ExecuteAlways, DisallowMultipleComponent]
public sealed class CyberControlVisual : MonoBehaviour
{
    [Header("Scene references (read only)")]
    [SerializeField] private B1LayerConfig world;
    [SerializeField] private SpriteRenderer upperBoundary;
    [SerializeField] private SlingshotBand band;
    [SerializeField] private TypeWriterB1 writer;
    [SerializeField] private Material material;

    [Header("Palette")]
    [SerializeField] private Color background = new Color(0.031f, 0.059f, 0.098f, 1f);
    [SerializeField] private Color streamColor = new Color(0.137f, 0.396f, 0.467f, 1f);
    [SerializeField] private Color cyan = new Color(0.443f, 0.910f, 0.949f, 1f);
    [SerializeField] private Color amber = new Color(1f, 0.780f, 0.475f, 1f);

    [Header("Digital rain")]
    [SerializeField, Range(8, 48)] private int columns = 28;
    [SerializeField, Range(0f, 0.3f)] private float streamOpacity = 0.14f;
    [SerializeField, Range(0.1f, 2f)] private float streamSpeed = 0.55f;
    [SerializeField, Range(0f, 1f)] private float frameOpacity = 0.42f;
    [SerializeField] private bool selectionFeedback = true;
    [SerializeField] private bool recordingDust = true;

    private Geometry _base, _rain, _feedback;
    private Rect _rect;
    private float _clock;
    private B1LayerConfig _subscribedWorld;
    private LineRenderer _bandLine;
    private readonly List<Mote> _motes = new List<Mote>(192);
    // Visual randomness must never advance UnityEngine.Random's gameplay sequence.
    private readonly System.Random _random = new System.Random(7319);
    private float _flash;
    private Vector2 _entry;
    private int _lastCount;
    private bool _frameDirty;

    private struct Mote
    {
        public Vector2 position, velocity;
        public float age, life, radius;
    }

    private void OnEnable()
    {
        if (world == null || material == null) return;
        _base = new Geometry(transform, "Control Frame", material, 0, 4.5f);
        _rain = new Geometry(transform, "Digital Streams", material, 0, 4.4f);
        _feedback = new Geometry(transform, "Contact and Entry Dust", material, 4, -0.1f);
        if (Application.isPlaying)
        {
            _subscribedWorld = world;
            _subscribedWorld.LetterRecorded += OnRecorded;
        }
        _rect = GetRect();
        BuildFrame();
        DrawRain();
    }

    private Rect GetRect()
    {
        float top = upperBoundary != null ? upperBoundary.bounds.min.y : world.upperFloorY - 0.54f;
        return Rect.MinMaxRect(world.boundsXMin, world.lowerGroundY, world.boundsXMax, top);
    }

    private void OnValidate() => _frameDirty = true;

    private void LateUpdate()
    {
        if (_base == null || world == null) return;
        Rect current = GetRect();
        if (current != _rect || _frameDirty || !Application.isPlaying)
        {
            _rect = current;
            BuildFrame();
            _frameDirty = false;
        }
        float dt = Application.isPlaying ? Time.deltaTime : 0f;
        _clock += dt;
        DrawRain();
        DrawFeedback(dt);
        if (writer != null) _lastCount = writer.BufferText.Length;
    }

    private void BuildFrame()
    {
        _base.Clear();
        // Match the existing order-0 camera Canvas, but render in front of it at z=4.5.
        _base.Box(_rect.center, _rect.size, background);
        _base.Box(_rect.center, _rect.size * 0.95f, Alpha(streamColor, 0.055f), true);
        float left = _rect.xMin + 0.3f, right = _rect.xMax - 0.3f;
        float bottom = _rect.yMin + 0.18f, top = _rect.yMax - 0.18f;
        Color faint = Alpha(streamColor, 0.065f);
        for (float x = left; x < right; x += 0.9f)
            _base.Line(new Vector2(x, bottom), new Vector2(x, top), 0.008f, faint);
        for (float y = bottom; y < top; y += 0.8f)
            _base.Line(new Vector2(left, y), new Vector2(right, y), 0.008f, faint);
        Color frame = Alpha(streamColor, frameOpacity);
        _base.Line(new Vector2(left, bottom + 0.3f), new Vector2(left, top - 0.3f), 0.014f, frame);
        _base.Line(new Vector2(right, bottom + 0.3f), new Vector2(right, top - 0.3f), 0.014f, frame);
        _base.Line(new Vector2(left + 0.4f, bottom), new Vector2(right - 0.4f, bottom), 0.014f, frame);
        _base.Corners(_rect.center, new Vector2(_rect.width * 0.5f - 0.3f, _rect.height * 0.5f - 0.18f),
            0.3f, 0.024f, Alpha(cyan, frameOpacity));
        for (float y = bottom + 0.5f; y < top - 0.4f; y += 0.23f)
        {
            _base.Line(new Vector2(left, y), new Vector2(left + 0.09f, y), 0.012f, frame);
            _base.Line(new Vector2(right - 0.09f, y), new Vector2(right, y), 0.012f, frame);
        }
        // Small segmented status strips are decorative, with no fictitious gameplay counters.
        for (int i = 0; i < 9; i++)
            _base.Box(new Vector2(right - 0.15f - i * 0.13f, bottom + 0.07f),
                new Vector2(0.07f, 0.025f), Alpha(i < 2 ? amber : streamColor, 0.4f));
        _base.Apply();
    }

    private void DrawRain()
    {
        _rain.Clear();
        int count = Mathf.Clamp(columns, 8, 48);
        float travel = _rect.height + 1.7f;
        for (int col = 0; col < count; col++)
        {
            float x = Mathf.Lerp(_rect.xMin + 0.55f, _rect.xMax - 0.55f, (col + 0.5f) / count);
            float speed = streamSpeed * (0.65f + Hash(col, 7) * 0.7f);
            float head = _rect.yMax + 0.1f - Mathf.Repeat(_clock * speed + Hash(col, 3) * travel, travel);
            float scale = col % 3 == 0 ? 0.095f : 0.065f;
            for (int k = 0; k < 7; k++)
            {
                float y = head + k * 0.21f;
                if (y - scale < _rect.yMin + 0.24f || y + scale > _rect.yMax - 0.15f) continue;
                float edge = Mathf.Min(y - _rect.yMin, _rect.yMax - y);
                float opacity = streamOpacity * (1f - k / 8f) * Mathf.Clamp01(edge / 0.5f);
                // The center stays quieter than the periphery, behind real A-Z gameplay letters.
                opacity *= Mathf.Lerp(0.5f, 1f, Mathf.Abs(x - _rect.center.x) / (_rect.width * 0.5f));
                int digit = Hash(col + k * 37, Mathf.FloorToInt(_clock * 2f)) > 0.5f ? 1 : 0;
                Digit(_rain, new Vector2(x, y), scale, digit, Alpha(streamColor, opacity));
            }
        }
        _rain.Apply();
    }

    private void DrawFeedback(float dt)
    {
        _feedback.Clear();
        if (selectionFeedback && Application.isPlaying && band != null && band.isActiveAndEnabled)
        {
            if (_bandLine == null) _bandLine = band.GetComponent<LineRenderer>();
            bool active = _bandLine != null && _bandLine.enabled && _bandLine.positionCount >= 2;
            if (active)
                foreach (var letter in PhysicsLetter.All)
                {
                    if (letter == null || !letter.isActiveAndEnabled || !_rect.Contains(letter.transform.position)) continue;
                    Vector2 p = letter.transform.position;
                    float distance = float.MaxValue;
                    for (int i = 1; i < _bandLine.positionCount; i++)
                    {
                        Vector3 a = _bandLine.GetPosition(i - 1), b = _bandLine.GetPosition(i);
                        if (!_bandLine.useWorldSpace)
                        { a = _bandLine.transform.TransformPoint(a); b = _bandLine.transform.TransformPoint(b); }
                        distance = Mathf.Min(distance, Distance(p, a, b));
                    }
                    if (distance > letter.Radius + band.bandHalfWidth + 0.06f && !letter.OnBandCooldown) continue;
                    float radius = letter.Radius + 0.13f;
                    _feedback.Corners(p, Vector2.one * radius, 0.12f, 0.018f, Alpha(cyan, 0.85f));
                }
        }
        _flash = Mathf.Max(0f, _flash - dt);
        if (_flash > 0f)
            _feedback.Corners(_entry, new Vector2(0.29f, 0.19f), 0.09f, 0.018f, Alpha(amber, _flash * 2f));
        for (int i = _motes.Count - 1; i >= 0; i--)
        {
            Mote mote = _motes[i];
            mote.age += dt;
            if (mote.age >= mote.life) { _motes.RemoveAt(i); continue; }
            mote.position += mote.velocity * dt;
            mote.velocity *= Mathf.Exp(-2.5f * dt);
            mote.velocity.y += 0.035f * dt;
            float fade = 1f - mote.age / mote.life;
            _feedback.Box(mote.position, Vector2.one * mote.radius * (1.2f - fade * 0.2f),
                Alpha(amber, fade * 0.7f), true);
            _motes[i] = mote;
        }
        _feedback.Apply();
    }

    private void OnRecorded(Vector3 position, float size)
    {
        if (!recordingDust || writer == null) return;
        // Add() has already run, and may have consumed a completed word. Retain its last slot.
        int count = writer.BufferText.Length;
        int slot = Mathf.Clamp(count > 0 ? count : _lastCount + 1, 1, writer.maxBuffer);
        _entry = writer.transform.TransformPoint(new Vector3(slot * writer.charSpacing, -0.7f, 0f));
        _flash = 0.35f;
        for (int i = 0; i < 28 && _motes.Count < 192; i++)
        {
            float angle = Next() * Mathf.PI * 2f;
            _motes.Add(new Mote
            {
                position = _entry + new Vector2((Next() - 0.5f) * 0.22f, (Next() - 0.5f) * 0.16f),
                velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle) * 0.45f) * (0.2f + Next() * 0.7f),
                radius = i % 5 == 0 ? 0.12f : 0.025f + Next() * 0.04f,
                life = 0.4f + Next() * 0.5f
            });
        }
        _lastCount = writer.BufferText.Length;
    }

    private float Next() => (float)_random.NextDouble();
    private static Color Alpha(Color color, float alpha) { color.a = Mathf.Clamp01(alpha); return color; }
    private static float Hash(int x, int seed) => Mathf.Repeat(Mathf.Sin(x * 127.1f + seed * 311.7f) * 43758.5453f, 1f);
    private static float Distance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        return Vector2.Distance(p, a + d * Mathf.Clamp01(Vector2.Dot(p - a, d) / Mathf.Max(d.sqrMagnitude, 0.00001f)));
    }

    private static void Digit(Geometry mesh, Vector2 p, float h, int digit, Color color)
    {
        float w = h * 0.48f, t = h * 0.12f;
        mesh.Line(p + new Vector2(w, -h), p + new Vector2(w, h), t, color);
        if (digit == 1) return;
        mesh.Line(p + new Vector2(-w, -h), p + new Vector2(-w, h), t, color);
        mesh.Line(p + new Vector2(-w, h), p + new Vector2(w, h), t, color);
        mesh.Line(p + new Vector2(-w, -h), p + new Vector2(w, -h), t, color);
    }

    private void OnDisable()
    {
        if (_subscribedWorld != null) _subscribedWorld.LetterRecorded -= OnRecorded;
        _subscribedWorld = null;
        _base?.Dispose(); _rain?.Dispose(); _feedback?.Dispose();
        _base = _rain = _feedback = null;
        _motes.Clear(); _flash = 0f; _clock = 0f; _lastCount = 0;
    }

    private sealed class Geometry : IDisposable
    {
        private readonly GameObject _go;
        private readonly Mesh _mesh;
        private readonly Transform _root;
        private readonly bool _linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        private readonly List<Vector3> _vertices = new List<Vector3>(8192);
        private readonly List<Vector3> _uv = new List<Vector3>(8192);
        private readonly List<Color> _colors = new List<Color>(8192);
        private readonly List<int> _indices = new List<int>(12288);
        public Geometry(Transform parent, string name, Material mat, int order, float z)
        {
            _go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            _go.transform.SetParent(parent, false);
            _root = _go.transform;
            _mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.sortingOrder = order;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _go.transform.position = new Vector3(0f, 0f, z);
        }
        public void Clear() { _vertices.Clear(); _uv.Clear(); _colors.Clear(); _indices.Clear(); }
        public void Box(Vector2 center, Vector2 size, Color color, bool soft = false)
        {
            Vector2 h = size * 0.5f;
            Quad(center + new Vector2(-h.x, -h.y), center + new Vector2(h.x, -h.y),
                center + new Vector2(h.x, h.y), center + new Vector2(-h.x, h.y), color, soft);
        }
        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 d = (b - a).normalized;
            Vector2 n = new Vector2(-d.y, d.x) * width * 0.5f;
            Quad(a - n, b - n, b + n, a + n, color, false);
        }
        public void Corners(Vector2 p, Vector2 radius, float arm, float width, Color color)
        {
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                {
                    Vector2 corner = p + new Vector2(radius.x * x, radius.y * y);
                    Line(corner, corner - Vector2.right * (x * arm), width, color);
                    Line(corner, corner - Vector2.up * (y * arm), width, color);
                }
        }
        private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color, bool soft)
        {
            int index = _vertices.Count;
            Vertex(a, new Vector2(0, 0), color, soft); Vertex(b, new Vector2(1, 0), color, soft);
            Vertex(c, new Vector2(1, 1), color, soft); Vertex(d, new Vector2(0, 1), color, soft);
            _indices.Add(index); _indices.Add(index + 1); _indices.Add(index + 2);
            _indices.Add(index); _indices.Add(index + 2); _indices.Add(index + 3);
        }
        private void Vertex(Vector2 point, Vector2 uv, Color color, bool soft)
        {
            _vertices.Add(_root.InverseTransformPoint(new Vector3(point.x, point.y, _root.position.z)));
            _uv.Add(new Vector3(uv.x, uv.y, soft ? 1f : 0f));
            _colors.Add(_linear ? color.linear : color);
        }
        public void Apply()
        {
            _mesh.Clear(); _mesh.SetVertices(_vertices); _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _uv); _mesh.SetTriangles(_indices, 0); _mesh.RecalculateBounds();
        }
        public void Dispose()
        {
            if (Application.isPlaying) { Destroy(_go); Destroy(_mesh); }
            else { DestroyImmediate(_go); DestroyImmediate(_mesh); }
        }
    }
}
