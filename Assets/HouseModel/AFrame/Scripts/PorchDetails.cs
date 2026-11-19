using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Shape = AFrameHouseVisual.Shape;

// The porch fittings share the cabin's vertex colours and fixed, front-facing projection.
internal sealed class PorchDetails : MonoBehaviour
{
    static readonly Color Brass = new Color(.94f, .66f, .31f);
    static readonly Color Iron = new Color(.17f, .24f, .23f);
    static readonly Color Glass = new Color(1f, .86f, .51f);
    static readonly Color Ink = new Color(.065f, .14f, .14f);
    readonly List<Mesh> meshes = new();
    Material wood;
    DinoAgentB1 builder;
    Sprite glowSprite;
    Texture2D glowTexture;
    Transform panel, cursor, socket, doorLeaf, window, paw;
    SpriteRenderer lampGlow, stepGlow, paneGlow;
    TextMeshPro title, hint;
    public Transform Gift { get; private set; }
    public Vector3 Hook => transform.TransformPoint(new Vector3(-.70f, 1.63f, -.95f));
    public Vector3 Door => transform.TransformPoint(new Vector3(-.15f, .24f, -1f));
    public Vector3 Window => transform.TransformPoint(new Vector3(-.15f, 1.84f, -.8f));

    Transform Part(string label, Transform parent, Vector3 local, Shape shape, int order)
    {
        var part = new GameObject(label).transform;
        part.SetParent(parent, false);
        part.localPosition = local;
        var mesh = shape.Mesh();
        meshes.Add(mesh);
        part.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = part.gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = wood;
        renderer.sortingOrder = order;
        return part;
    }

    static void Ring(Shape s, Vector2 center, float radius, float width, Color color, int sides = 16)
    {
        Vector2 prev = center + Vector2.up * radius;
        for (int i = 1; i <= sides; i++)
        {
            float angle = 2f * Mathf.PI * i / sides;
            var next = center + new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * radius;
            s.Beam(prev, next, width, color);
            prev = next;
        }
    }

    public void Build(DinoAgentB1 dino)
    {
        builder = dino;
        wood = Resources.Load<Material>("AFrameHouse");
        var s = new Shape();
        Ring(s, Vector2.zero, .22f, .034f, Glass);
        s.Beam(new Vector2(0,.25f), new Vector2(0,.37f), .035f, Brass);
        socket = Part("Porch hook guide", transform, transform.InverseTransformPoint(Hook), s, 13);
        socket.gameObject.SetActive(false);

        s = new Shape();
        Ring(s, Vector2.zero, .105f, .028f, Ink);
        Ring(s, Vector2.zero, .085f, .018f, Glass);
        cursor = Part("Porch fingertip", transform, Vector3.zero, s, 24);
        cursor.gameObject.SetActive(false);

        s = new Shape();
        s.Box(Vector2.zero, new Vector2(5.65f, 1.26f), new Color(.049f,.12f,.13f,.94f));
        s.Box(new Vector2(-2.78f,0), new Vector2(.053f,1.26f), Brass);
        panel = Part("Porch message", transform, Vector3.zero, s, 20);
        title = Label("Title", .22f, 4.2f, new Color(1f,.91f,.71f));
        hint = Label("Action", -.29f, 2.65f, new Color(.82f,.91f,.83f));
        Message(null, null);
    }

    TextMeshPro Label(string label, float y, float size, Color color)
    {
        var text = B1Text.Make(label, panel, size, color, 21);
        text.fontSharedMaterial = text.font.material;
        text.transform.localPosition = new Vector3(0,y,-.01f);
        return text;
    }

    public void Message(string heading, string action)
    {
        panel.gameObject.SetActive(heading != null);
        title.text = heading ?? "";
        hint.text = action ?? "";
    }

    public void Layout(Camera cam, bool finished)
    {
        float scale = cam.orthographicSize / 4.885f * Mathf.Min(1, cam.aspect / 1.75f);
        panel.localScale = Vector3.one * scale;
        var anchor = new Vector2(finished ? .24f : .22f, finished ? .52f : .76f);
        panel.position = cam.ViewportToWorldPoint(new Vector3(anchor.x, anchor.y, -3 - cam.transform.position.z));
    }

    public bool OverReplay(Vector2 world)
    {
        Vector3 p = panel.InverseTransformPoint(new Vector3(world.x, world.y, panel.position.z));
        return Mathf.Abs(p.x) < 2.83f && Mathf.Abs(p.y) < .63f;
    }

    public void Pointer(Vector2 pos, bool visible)
    {
        cursor.gameObject.SetActive(visible);
        if (visible) cursor.position = new Vector3(pos.x,pos.y,-3.2f);
    }

    public void MakeGift(bool lamp, Vector3 at)
    {
        var s = new Shape();
        s.Beam(Vector2.zero, new Vector2(0,-.15f), .035f, Iron);
        Ring(s, new Vector2(0,-.19f), .06f, .028f, Brass, 8);
        if (lamp)
        {
            s.Quad(new(-.21f,-.36f),new(.21f,-.36f),new(.11f,-.25f),new(-.11f,-.25f),Iron);
            s.Quad(new(-.17f,-.36f),new(.17f,-.36f),new(.12f,-.64f),new(-.12f,-.64f),Brass);
            s.Quad(new(-.12f,-.38f),new(.12f,-.38f),new(.08f,-.60f),new(-.08f,-.60f),new Color(.46f,.55f,.49f));
            s.Beam(new(0,-.36f),new(0,-.61f),.023f,Iron);
            s.BevelBox(new(0,-.65f),new(.31f,.069f),Iron);
        }
        else
        {
            s.Triangle(new(-.11f,-.31f),new(.11f,-.31f),new(0,-.22f),Brass);
            s.Quad(new(-.11f,-.30f),new(.11f,-.30f),new(.19f,-.53f),new(-.19f,-.53f),Brass);
            s.Beam(new(-.21f,-.54f),new(.21f,-.54f),.055f,Glass);
            s.Beam(new(0,-.55f),new(-.02f,-.70f),.028f,Iron);
            s.BevelBox(new(-.025f,-.71f),new(.09f,.09f),Brass);
        }
        Gift = Part(lamp ? "Porch lantern" : "Porch wind bell", transform, Vector3.zero, s, 14);
        Gift.position = at;
        Gift.localScale = Vector3.one * 1.4f;
        socket.gameObject.SetActive(true);
    }

    public void PreviewHook(bool near)
    {
        socket.localScale = Vector3.one * (near ? 1.3f : 1f + .08f*Mathf.Sin(Time.time*4));
    }

    public void Mount()
    {
        Gift.position = Hook;
        Gift.localScale = Vector3.one;
        socket.gameObject.SetActive(false);
    }

    SpriteRenderer Glow(string label, Vector3 local, Vector2 size, Color color, int order)
    {
        if (!glowSprite)
        {
            const int n = 64;
            glowTexture = new Texture2D(n,n,TextureFormat.RGBA32,false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[n*n];
            for (int y=0; y<n; y++)
                for (int x=0; x<n; x++)
                {
                    float r = new Vector2(2*(x+.5f)/n-1,2*(y+.5f)/n-1).magnitude;
                    pixels[y*n+x] = new Color(1,1,1,Mathf.Pow(Mathf.Max(0,1-r*r),3));
                }
            glowTexture.SetPixels32(pixels); glowTexture.Apply(false,true);
            glowSprite = Sprite.Create(glowTexture,new Rect(0,0,n,n),new Vector2(.5f,.5f),n);
        }
        var go = new GameObject(label);
        go.transform.SetParent(transform,false);
        go.transform.localPosition = local;
        go.transform.localScale = new Vector3(size.x,size.y,1);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = glowSprite; sr.color = color; sr.sortingOrder = order;
        return sr;
    }

    public void LightUp(bool lamp)
    {
        Color gold = new Color(1f,.75f,.30f);
        if (lamp)
        {
            var at = transform.InverseTransformPoint(Hook) + Vector3.down*.48f;
            lampGlow = Glow("Lantern flame", at, new(.37f,.42f), Glass, 15);
            Glow("Lantern halo", at, new(1.16f,1.25f), new Color(1,.77f,.3f,.34f), 13);
            stepGlow = Glow("Light on doorstep",new(-.55f,.20f,-.7f),new(1.9f,.36f),gold,3);
        }
        paneGlow = Glow("Warm window",new(-.15f,1.84f,-.8f),new(.77f,.80f),gold,2);
    }

    public void LightLevel(float level)
    {
        if (lampGlow) lampGlow.color = new Color(1,.94f,.68f,level);
        if (stepGlow) stepGlow.color = new Color(1,.73f,.31f,.66f*level);
        if (paneGlow) paneGlow.color = new Color(1,.81f,.46f,.74f*level);
    }

    public void OpenDoor(float amount)
    {
        if (!doorLeaf)
        {
            var opening = new Shape();
            opening.Box(new(.31f,.51f),new(.62f,1.02f),new Color(.14f,.15f,.12f));
            var hinge = new Vector3(-.46f,.33f,-.82f);
            Part("Doorway", transform, hinge, opening, 1);
            var leaf = new Shape();
            leaf.Box(new(.31f,.51f),new(.62f,1.02f),new Color(.42f,.29f,.19f));
            for(int i=0; i<4; i++)
                leaf.Box(new(.083f+i*.152f,.50f),new(.139f,.96f),new Color(.49f,.34f,.22f));
            leaf.BevelBox(new(.31f,.82f),new(.28f,.21f),Glass);
            leaf.Box(new(.54f,.47f),new(.05f,.06f),Brass);
            doorLeaf = Part("Swinging porch door", transform, hinge, leaf, 2);
        }
        doorLeaf.localRotation = Quaternion.Euler(0,-78f*amount,0);
    }

    public void WindowWave(float time)
    {
        if (!window)
        {
            var s = new Shape();
            window = new GameObject("Dino at the window").transform;
            window.SetParent(transform,false);
            window.position = Window;
            // Share the actual builder mesh and atlas so the close-up keeps the same face.
            var model = new GameObject("Window portrait").transform;
            model.SetParent(window,false);
            var visual = builder.GetComponent<DinoTaskVisual>();
            var dinoModel = visual ? visual.CharacterMesh : null;
            Renderer portrait;
            if (dinoModel && dinoModel.sharedMesh && dinoModel.TryGetComponent(out MeshRenderer source))
            {
                model.localRotation = Quaternion.Euler(0,156,0) * dinoModel.transform.localRotation;
                model.gameObject.AddComponent<MeshFilter>().sharedMesh = dinoModel.sharedMesh;
                portrait = model.gameObject.AddComponent<MeshRenderer>();
                portrait.sharedMaterials = source.sharedMaterials;
            }
            else
            {
                var sprite = model.gameObject.AddComponent<SpriteRenderer>();
                sprite.sprite = builder.GetComponent<SpriteRenderer>().sprite;
                portrait = sprite;
            }
            portrait.sortingOrder = 5;
            FrameHeadAtSill(portrait);
            var blue = new Color(.16f,.48f,.56f);
            s.Beam(Vector2.zero,new(.075f,.061f),.043f,blue);
            s.BevelBox(new(.083f,.073f),new(.052f,.055f),blue);
            paw = Part("Waving paw",window,new(.096f,-.172f,-.30f),s,6);
            s = new Shape();
            s.Box(Vector2.zero,new(.51f,.045f),new Color(.53f,.37f,.24f));
            Part("Window sill",transform,new(-.15f,1.62f,-.9f),s,7);
        }
        window.localScale = Vector3.one*Mathf.SmoothStep(.01f,1,Mathf.Clamp01(time/.3f));
        float wave = Mathf.Clamp01((time-.15f)/1.45f);
        paw.localRotation = Quaternion.Euler(0,0,12 + 36*Mathf.Sin(wave*4*Mathf.PI)*Mathf.Sin(wave*Mathf.PI));
    }

    void FrameHeadAtSill(Renderer portrait)
    {
        Transform model = portrait.transform;
        if (portrait.bounds.size.y <= 0) return;
        model.localScale *= .55f / portrait.bounds.size.y;
        Bounds bounds = portrait.bounds;
        float jaw = Mathf.Lerp(bounds.min.y, bounds.max.y, .54f);
        Vector3 sill = Window + new Vector3(-.068f, -.20f, 0);
        model.position += sill - new Vector3(bounds.center.x, jaw, bounds.center.z);

        if (portrait is SpriteRenderer sprite)
        {
            var mask = new GameObject("Window opening").AddComponent<SpriteMask>();
            mask.transform.SetParent(window, false);
            mask.transform.localScale = new Vector3(.41f, .40f, 1);
            mask.sprite = B1Visuals.Square;
            sprite.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            return;
        }

        // Keep the cut at the jaw even while the portrait scales up to greet the player.
        Vector4 plane = model.localToWorldMatrix.GetRow(1);
        plane.w -= sill.y;
        var properties = new MaterialPropertyBlock();
        properties.SetVector("_PortraitClipPlaneOS", plane);
        portrait.SetPropertyBlock(properties);
    }

    void OnDestroy()
    {
        foreach(var mesh in meshes) Destroy(mesh);
        if (glowSprite) Destroy(glowSprite);
        if (glowTexture) Destroy(glowTexture);
    }
}
