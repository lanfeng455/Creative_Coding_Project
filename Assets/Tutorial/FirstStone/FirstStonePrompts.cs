using TMPro;
using UnityEngine;

// Two mirrored index fingers share one silhouette. The live slingshot draws the actual band.
internal sealed class FirstStonePrompts
{
    readonly Transform root;
    readonly Sprite hand;
    readonly TextMeshPro caption;
    readonly HandCue left, right;
    readonly float top, floor;

    internal FirstStonePrompts(Transform parent, B1LayerConfig world, float ceiling, SlingshotBand band)
    {
        root = new GameObject("Gesture prompts").transform;
        root.SetParent(parent, false);
        top = ceiling;
        floor = world.lowerGroundY;
        var texture = Resources.Load<Texture2D>("FirstStone/IndexHand");
        hand = Sprite.Create(texture, new Rect(0,0,texture.width,texture.height),
            new Vector2(.69f,.94f), texture.height / 1.35f);
        float center = (world.boundsXMin + world.boundsXMax) * .5f;
        float span = Mathf.Min(2.05f, (world.boundsXMax - world.boundsXMin) * .17f);
        float y = Mathf.Lerp(floor, top, .60f);
        left = new HandCue(root, hand, false, band.leftTipColor, new Vector2(center-span,y));
        right = new HandCue(root, hand, true, band.rightTipColor, new Vector2(center+span,y));
        caption = B1Text.Make("Gesture instruction", root, 3.7f, new Color(.86f,.96f,.97f), 10);
        caption.fontSharedMaterial = caption.font.material;
        caption.transform.position = new Vector3(center, floor+.40f, -.8f);
        Draw(false, default, false, default, false, false, -1);
    }

    internal void Draw(bool hasLeft, Vector2 a, bool hasRight, Vector2 b, bool paired, bool cue, float demo)
    {
        caption.text = paired ? "碰到字母时，双手一起向上弹" : "伸出两只食指，左右分开";
        left.Draw(hasLeft, a, cue, demo, floor, top);
        right.Draw(hasRight, b, cue, demo, floor, top);
    }

    internal void Fade(float alpha)
    {
        caption.alpha = alpha;
        left.Fade(alpha);
        right.Fade(alpha);
        if (alpha == 0) root.gameObject.SetActive(false);
    }

    internal void Dispose() { if (hand) Object.Destroy(hand); }

    sealed class HandCue
    {
        readonly SpriteRenderer ghost;
        readonly SpriteRenderer[] arrow = new SpriteRenderer[3];
        readonly Transform arrowRoot;
        readonly Color tint;
        readonly Vector2 home;
        readonly float side;
        float ghostOpacity;

        internal HandCue(Transform parent, Sprite hand, bool right, Color color, Vector2 rest)
        {
            home = rest;
            side = right ? 1 : -1;
            tint = color;
            ghost = SpritePart(right ? "Orange index" : "Cyan index", parent, hand, 7);
            ghost.flipX = right;
            arrowRoot = new GameObject("Upstroke").transform;
            arrowRoot.SetParent(parent,false);
            for (int i=0;i<3;i++)
            {
                arrow[i] = SpritePart("Stroke", arrowRoot, B1Visuals.Square, 8);
                var t = arrow[i].transform;
                t.localScale = new Vector3(.044f, i == 0 ? .35f : .19f, 1);
                if (i == 0) continue;
                float sign = i == 1 ? 1 : -1;
                t.localRotation = Quaternion.Euler(0,0,sign*40);
                t.localPosition = new Vector3(sign*.061f,.10f,0);
            }
        }

        static SpriteRenderer SpritePart(string name, Transform parent, Sprite sprite, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent,false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = order;
            return renderer;
        }

        internal void Draw(bool tracked, Vector2 tip, bool cue, float demo, float floor, float ceiling)
        {
            ghost.enabled = !tracked || demo >= 0;
            ghostOpacity = tracked ? .50f : .76f;
            Vector2 g = home;
            if (tracked && demo >= 0)
            {
                float lift = Mathf.SmoothStep(0,1,Mathf.Clamp01((demo-.15f)/.58f));
                g = tip + new Vector2(side*.16f, -.10f + .34f*lift);
            }
            // The fingertip is the sprite pivot; leave space underneath for the wrist.
            g.y = Mathf.Clamp(g.y, floor+1.20f, ceiling-.13f);
            ghost.transform.position = new Vector3(g.x, g.y, -.6f);
            arrowRoot.gameObject.SetActive(cue && tracked);
            if (cue && tracked)
                arrowRoot.position = new Vector3(tip.x + side*.31f,
                    Mathf.Clamp(tip.y+.48f, floor+.25f, ceiling-.23f), -.6f);
            Fade(1);
        }

        internal void Fade(float value)
        {
            Color c = tint;
            c.a = value * ghostOpacity; ghost.color = c;
            c.a = value;
            foreach (var stroke in arrow) stroke.color = c;
        }
    }
}
