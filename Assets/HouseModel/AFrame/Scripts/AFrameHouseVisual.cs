using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Six independent mesh layers, authored for the game's fixed orthographic view.</summary>
public sealed class AFrameHouseVisual : MonoBehaviour
{
    private readonly List<GameObject> _stages = new List<GameObject>();
    private readonly List<Mesh> _meshes = new List<Mesh>();
    private Material _material;
    private bool _ownsMaterial;
    private readonly Color _wood = new Color(0.56f, 0.40f, 0.25f);
    private readonly Color _edge = new Color(0.77f, 0.61f, 0.40f);
    private readonly Color _roof = new Color(0.34f, 0.44f, 0.38f);
    private readonly Color _stone = new Color(0.50f, 0.54f, 0.47f);
    private static readonly Vector2 Left = new Vector2(-1.35f, 0.28f);
    private static readonly Vector2 Right = new Vector2(1.05f, 0.28f);
    private static readonly Vector2 Peak = new Vector2(-0.15f, 2.65f);
    private static readonly Vector2 Depth = new Vector2(0.65f, 0.14f);

    public void ShowStage(int completed, bool animate = true)
    {
        if (_stages.Count == 0) Build();
        completed = Mathf.Clamp(completed, 0, AFrameBuildPlan.StageCount);
        for (int i = 0; i < _stages.Count; i++)
        {
            bool visible = i < completed;
            bool newlyVisible = visible && !_stages[i].activeSelf;
            _stages[i].SetActive(visible);
            if (newlyVisible && animate && Application.isPlaying)
                StartCoroutine(Assemble(_stages[i].transform));
        }
    }

    private IEnumerator Assemble(Transform stage)
    {
        Vector3 rest = stage.localPosition;
        for (float t = 0f; t < 0.55f; t += Time.deltaTime)
        {
            if (stage == null) yield break;
            float eased = Mathf.SmoothStep(0f, 1f, t / 0.55f);
            stage.localPosition = rest + Vector3.up * (0.12f * (1f - eased));
            stage.localScale = Vector3.one * Mathf.Lerp(0.93f, 1f, eased);
            yield return null;
        }
        if (stage != null) { stage.localPosition = rest; stage.localScale = Vector3.one; }
    }

    private void Build()
    {
        _material = Resources.Load<Material>("AFrameHouse");
        if (_material == null)
        {
            Shader shader = Shader.Find("WordDefense/AFrame House");
            if (shader == null) throw new System.InvalidOperationException("AFrame house shader is missing.");
            _material = new Material(shader); _ownsMaterial = true;
        }
        for (int stage = 0; stage < AFrameBuildPlan.StageCount; stage++)
        {
            var shape = new Shape();
            switch (stage)
            {
                case 0: Foundation(shape); break;
                case 1: Deck(shape); break;
                case 2: Frame(shape); break;
                case 3: Walls(shape); break;
                case 4: Roof(shape); break;
                case 5: Windows(shape); break;
            }
            var go = new GameObject($"Stage_{stage + 1}_{AFrameBuildPlan.WordAt(stage)}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 2f - stage * 0.02f);
            Mesh mesh = shape.Mesh(); _meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material; renderer.sortingOrder = 0;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            go.SetActive(false); _stages.Add(go);
        }
    }

    private void Foundation(Shape s)
    {
        for (int i = 0; i < 7; i++)
        {
            float x = -1.52f + i * 0.46f;
            s.BevelBox(new Vector2(x + 0.21f, 0.115f), new Vector2(0.44f, 0.22f),
                _stone * (i % 2 == 0 ? 1f : 0.92f));
        }
        // The visible side rises in projection with the deck, but its base stays
        // on the ground. Close this face so the rear eave cannot appear to float.
        Vector2 sideFront = new Vector2(1.12f, 0.23f);
        Vector2 sideBack = sideFront + Depth;
        Color sideStone = _stone * 0.78f; sideStone.a = 1f;
        Color mortar = _stone * 0.58f; mortar.a = 1f;
        s.Quad(new Vector2(sideFront.x, 0.005f), new Vector2(sideBack.x, 0.005f),
            sideBack, sideFront, sideStone);
        for (int i = 1; i < 3; i++)
        {
            Vector2 joint = Vector2.Lerp(sideFront, sideBack, i / 3f);
            s.Beam(new Vector2(joint.x, 0.015f), joint, 0.018f, mortar);
        }
        s.BevelBox(new Vector2(-0.15f, 0.035f), new Vector2(0.80f, 0.09f), _stone * 1.15f);
    }

    private void Deck(Shape s)
    {
        // Solid side fascia follows the same perspective as the boards and
        // overlaps the stone support below; no open strip beneath the roof.
        Vector2 sideFront = new Vector2(1.12f, 0.24f);
        Vector2 sideBack = sideFront + Depth;
        Color sideWood = _wood * 0.78f; sideWood.a = 1f;
        s.Quad(sideFront - Vector2.up * 0.06f, sideBack - Vector2.up * 0.06f,
            sideBack, sideFront, sideWood);
        for (int i = 0; i < 7; i++)
        {
            // Lay the boards along one deck plane, rather than stacking full-depth
            // sheets upwards where their left ends protrude through the sloping roof.
            Vector2 front = Depth * (i / 7f);
            Vector2 back = Depth * ((i + 1f) / 7f);
            s.Quad(new Vector2(-1.4f, 0.24f) + front, new Vector2(1.12f, 0.24f) + front,
                new Vector2(1.12f, 0.24f) + back, new Vector2(-1.4f, 0.24f) + back,
                _wood * (i % 2 == 0 ? 1.13f : 1.05f));
        }
        // Keep the front fascia tucked under the eave instead of exposing a raised end cap.
        s.BevelBox(new Vector2(-0.14f, 0.24f), new Vector2(2.56f, 0.12f), _wood);
        // Inset both supports so their upper corners stay inside the A-frame silhouette.
        s.BevelBox(new Vector2(-1.14f, 0.42f), new Vector2(0.14f, 0.30f), _wood);
        s.BevelBox(new Vector2(0.84f, 0.42f), new Vector2(0.14f, 0.30f), _wood);
    }

    private void Frame(Shape s)
    {
        s.Beam(Left + Depth, Peak + Depth, 0.105f, _wood * 0.82f);
        s.Beam(Right + Depth, Peak + Depth, 0.105f, _wood * 0.82f);
        s.Beam(Peak, Peak + Depth, 0.105f, _edge);
        s.Beam(Left, Left + Depth, 0.11f, _wood);
        s.Beam(Right, Right + Depth, 0.11f, _wood);
        s.Beam(Left, Peak, 0.14f, _edge);
        s.Beam(Right, Peak, 0.14f, _edge);
        s.Beam(Left + Vector2.up * 0.04f, Right + Vector2.up * 0.04f, 0.11f, _edge);
        s.Beam(new Vector2(-0.77f, 1.4f), new Vector2(0.47f, 1.4f), 0.10f, _wood);
        s.Beam(new Vector2(-0.5f, 0.35f), new Vector2(-0.5f, 1.4f), 0.09f, _edge);
        s.Beam(new Vector2(0.2f, 0.35f), new Vector2(0.2f, 1.4f), 0.09f, _edge);
    }

    private void Walls(Shape s)
    {
        Color dark = _wood * 0.66f;
        // A dark interior closes plank seams and gives unfinished window openings depth.
        s.Triangle(Left, Right, Peak, _wood * 0.48f);
        for (int row = 0; row < 17; row++)
        {
            float y = 0.34f + row * 0.132f, top = y + 0.12f;
            float half = (2.65f - top) / 2.37f * 1.2f - 0.055f;
            if (half <= 0f) continue;
            float left = -0.15f - half, right = -0.15f + half;
            // Open door and upper window remain genuine gaps until their own components arrive.
            float gap = y < 1.38f ? 0.34f : y >= 1.55f && y < 2.11f ? 0.24f : 0f;
            Color tint = _wood * (row % 3 == 0 ? 1.08f : 1f);
            if (gap > 0f)
            {
                if (left < -0.15f - gap) s.Box(new Vector2((left - 0.15f - gap) * 0.5f, y + 0.06f), new Vector2(-0.15f - gap - left, 0.12f), tint);
                if (right > -0.15f + gap) s.Box(new Vector2((right - 0.15f + gap) * 0.5f, y + 0.06f), new Vector2(right + 0.15f - gap, 0.12f), tint);
            }
            else s.Box(new Vector2(-0.15f, y + 0.06f), new Vector2(half * 2f, 0.12f), tint);
        }
        // Simple panelled door uses timber from this PLANK delivery.
        s.Box(new Vector2(-0.15f, 0.84f), new Vector2(0.62f, 1.02f), dark);
        for (int i = 0; i < 4; i++)
            s.Box(new Vector2(-0.39f + i * 0.16f, 0.83f), new Vector2(0.145f, 0.98f), _wood * 0.84f);
        s.Box(new Vector2(0.06f, 0.79f), new Vector2(0.045f, 0.055f), _stone * 0.62f);
    }

    private void Roof(Shape s)
    {
        Vector2 depth = Depth + new Vector2(0.06f, 0.02f);
        Vector2 ridge = Peak + Vector2.up * 0.06f, eave = Right + new Vector2(0.11f, -0.03f);
        for (int row = 0; row < 12; row++)
        {
            Vector2 a = Vector2.Lerp(eave, ridge, row / 12f);
            Vector2 b = Vector2.Lerp(eave, ridge, (row + 1f) / 12f);
            s.Quad(a, a + depth, b + depth, b, _roof * (row % 2 == 0 ? 1f : 1.1f));
            s.Beam(a, a + depth, 0.018f, _roof * 0.7f);
        }
        s.Beam(ridge, ridge + depth, 0.095f, _roof * 0.7f);
        s.Beam(eave, eave + depth, 0.085f, _roof * 0.7f);
        s.Beam(Left + new Vector2(-0.06f, -0.02f), ridge, 0.08f, _roof);
        s.Beam(eave, ridge, 0.075f, _edge * 0.9f);
    }

    private void Windows(Shape s)
    {
        Color warm = new Color(0.96f, 0.77f, 0.43f);
        s.BevelBox(new Vector2(-0.15f, 1.84f), new Vector2(0.53f, 0.56f), _wood * 0.68f);
        s.Box(new Vector2(-0.15f, 1.84f), new Vector2(0.41f, 0.44f), warm);
        s.Triangle(new Vector2(-0.34f, 1.65f), new Vector2(0.04f, 2.04f), new Vector2(-0.34f, 2.04f), warm * 1.05f);
        s.Beam(new Vector2(-0.15f, 1.61f), new Vector2(-0.15f, 2.07f), 0.035f, _wood);
        s.Beam(new Vector2(-0.38f, 1.84f), new Vector2(0.08f, 1.84f), 0.035f, _wood);
        s.BevelBox(new Vector2(-0.15f, 1.15f), new Vector2(0.34f, 0.27f), _edge);
        s.Box(new Vector2(-0.15f, 1.15f), new Vector2(0.25f, 0.18f), warm);
        s.BevelBox(new Vector2(-0.15f, 1.53f), new Vector2(0.65f, 0.065f), _edge);
    }

    private void OnDestroy()
    {
        foreach (Mesh mesh in _meshes)
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
        if (_ownsMaterial)
            if (Application.isPlaying) Destroy(_material); else DestroyImmediate(_material);
    }

    internal sealed class Shape
    {
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<int> _t = new List<int>();
        public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int n = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            Color tint = QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;
            _c.Add(tint); _c.Add(tint); _c.Add(tint);
            _t.Add(n); _t.Add(n + 1); _t.Add(n + 2);
        }
        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        { Triangle(a, b, c, color); Triangle(a, c, d, color); }
        public void Box(Vector2 p, Vector2 size, Color color)
        {
            Vector2 h = size * 0.5f;
            Quad(p + new Vector2(-h.x, -h.y), p + new Vector2(h.x, -h.y),
                p + new Vector2(h.x, h.y), p + new Vector2(-h.x, h.y), color);
        }
        public void BevelBox(Vector2 p, Vector2 size, Color color)
        {
            Box(p, size, color * 0.78f);
            Box(p + Vector2.up * 0.015f, size - Vector2.one * 0.035f, color);
            Box(p + Vector2.up * (size.y * 0.5f - 0.02f), new Vector2(size.x - 0.04f, 0.025f), color * 1.17f);
        }
        public void Beam(Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 d = (b - a).normalized, n = new Vector2(-d.y, d.x) * thickness * 0.5f;
            Quad(a - n, b - n, b + n, a + n, color);
            Quad(a, b, b + n * 0.65f, a + n * 0.65f, color * 1.12f);
        }
        public Mesh Mesh()
        {
            var mesh = new Mesh { name = "AFrame stage mesh" };
            mesh.SetVertices(_v); mesh.SetColors(_c); mesh.SetTriangles(_t, 0); mesh.RecalculateBounds();
            return mesh;
        }
    }
}
