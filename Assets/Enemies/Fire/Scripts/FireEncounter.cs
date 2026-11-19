using UnityEngine;

// Origin is the monster's standing point. Only TemplateB2 owns this encounter.
public sealed class FireEncounter : ConstructionEncounter
{
    public const string CounterWord = "WATER";
    public enum Phase { Waiting, Pillar, Fighting, Steam, Retreat, Cleared }
    public Phase CurrentPhase { get; private set; }
    public override string Request => CurrentPhase == Phase.Fighting && !spraying && !IsGameOver ? CounterWord : null;

    [SerializeField] MeshRenderer character;
    [SerializeField] Mesh cooledMesh;
    [SerializeField] Material cooledMaterial;
    [SerializeField] Texture2D pillarSheet, auraSheet, flameSheet, splashSheet, smokeSheet;
    const float BodyScale = 1.45f;
    const float FlameHeight = .90f;
    const float ChargeSeconds = 2.4f;
    const float BreathSeconds = 1.05f;

    WindFlipbook pillar, aura, flame, splash, smoke;
    LineRenderer stream, foam;
    Vector3 standingPosition, standingScale;
    Quaternion standingRotation;
    float age, nextBreath, breathAge, sprayAge, retreatEnd;
    bool breathing, hitThisBreath, spraying, cooled;

    void Awake()
    {
        if (!dino || !character || !cooledMesh || !cooledMaterial ||
            !pillarSheet || !auraSheet || !flameSheet || !splashSheet || !smokeSheet)
        {
            Debug.LogError("Incomplete FireEncounter references.", this);
            enabled = false;
            return;
        }
        standingPosition = character.transform.localPosition * BodyScale;
        standingRotation = character.transform.localRotation;
        standingScale = character.transform.localScale * BodyScale;
        character.enabled = false;
        pillar = new WindFlipbook(transform, pillarSheet, 64, 112, 24, .07f, 3.4f, 15);
        aura = new WindFlipbook(transform, auraSheet, 48, 64, 12, .075f, 1.63f, 11);
        flame = new WindFlipbook(transform, flameSheet, 128, 48, 16, .06f, FlameHeight, 14);
        splash = new WindFlipbook(transform, splashSheet, 112, 80, 22, .07f, 1.95f, 16);
        smoke = new WindFlipbook(transform, smokeSheet, 64, 56, 21, .07f, 2.3f, 17);
        smoke.Renderer.color = new Color(.83f, .93f, 1f);
        stream = WaterLine("Water Jet", new Color(.14f,.73f,.94f), .082f, 14);
        foam = WaterLine("Water Jet Highlight", new Color(.76f,.97f,1f), .031f, 15);
    }

    LineRenderer WaterLine(string label, Color color, float width, int order)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.sharedMaterial = splash.Renderer.sharedMaterial;
        line.sortingOrder = order;
        line.startColor = line.endColor = color;
        line.startWidth = width;
        line.endWidth = width * .65f;
        line.positionCount = 16;
        line.numCapVertices = 3;
        line.enabled = false;
        return line;
    }

    Vector3 Floor => transform.position - Vector3.forward * .35f;
    float TowardDino => dino.transform.position.x < transform.position.x ? -1f : 1f;
    Vector3 DinoMouth => new Vector3(dino.transform.position.x - TowardDino * .24f, dino.transform.position.y + .44f, Floor.z);
    Vector3 FireMouth => Floor + new Vector3(TowardDino * .25f, .49f, 0) * BodyScale;

    protected override int AfterStep => 2;
    protected override void BeginEncounter() => Enter(Phase.Pillar);

    void Update()
    {
        if (CurrentPhase == Phase.Waiting || CurrentPhase == Phase.Cleared || IsGameOver) return;
        age += Time.deltaTime;
        switch (CurrentPhase)
        {
            case Phase.Pillar:
                pillar.Draw(age, Floor - Vector3.up * .18f, false);
                float reveal = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.43f, 1.19f, age));
                character.enabled = reveal > 0;
                Pose(reveal, 1, 0, 0);
                if (age >= pillar.Duration)
                {
                    Enter(Phase.Fighting);
                    nextBreath = attackIntervalSeconds;
                }
                break;
            case Phase.Fighting:
                if (spraying) UpdateSpray();
                if (CurrentPhase == Phase.Fighting) UpdateAttack();
                break;
            case Phase.Steam:
                splash.Draw(age, Floor, false);
                smoke.Draw(age, Floor, false);
                if (!cooled && age >= .24f)
                {
                    character.GetComponent<MeshFilter>().sharedMesh = cooledMesh;
                    character.sharedMaterial = cooledMaterial;
                    Pose(1, 1, 0, 0);
                    cooled = true;
                }
                if (age >= Mathf.Max(smoke.Duration, splash.Duration))
                {
                    var cam = Camera.main;
                    retreatEnd = cam ? cam.ViewportToWorldPoint(new Vector3(1, .5f,
                        Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward))).x + character.bounds.extents.x + .25f : Floor.x + 12f;
                    Enter(Phase.Retreat);
                }
                break;
            case Phase.Retreat:
                float hop = Mathf.Abs(Mathf.Sin(age * Mathf.PI / .46f));
                character.transform.position = transform.TransformPoint(standingPosition) +
                    new Vector3(age * 3.3f, .36f * hop, 0);
                character.transform.localRotation = standingRotation * Quaternion.Euler(0, 62, -9 * hop);
                if (character.transform.position.x >= retreatEnd)
                {
                    character.enabled = false;
                    Enter(Phase.Cleared);
                    ReleaseBuilder();
                }
                break;
        }
    }

    void Enter(Phase phase)
    {
        CurrentPhase = phase;
        age = 0;
    }

    void Pose(float size, float stretch, float yaw, float lean)
    {
        var t = character.transform;
        t.localScale = Vector3.Scale(standingScale, new Vector3(1f / stretch, stretch, 1)) * size;
        t.localPosition = standingPosition * (stretch * size);
        t.localRotation = standingRotation * Quaternion.Euler(0, yaw, lean);
    }

    void UpdateAttack()
    {
        float charge = Mathf.Clamp01(1f - (nextBreath - age) / ChargeSeconds);
        if (!breathing && age >= nextBreath)
        {
            breathing = true;
            hitThisBreath = false;
            breathAge = 0;
            nextBreath += attackIntervalSeconds;
        }
        float pulse = Mathf.Sin(age * Mathf.Lerp(3, 17, charge));
        Pose(1, 1f + .026f * pulse - .14f * charge, 5f * pulse, -TowardDino * (charge * 12 - (breathing ? 10 : 0)));
        aura.Draw(age, Floor - Vector3.up * .14f, true, .19f + .8f * charge);
        aura.Renderer.transform.localScale = Vector3.one * (1 + charge * .35f);
        if (!breathing) return;

        breathAge += Time.deltaTime;
        var origin = FireMouth;
        var target = DinoMouth;
        Vector3 direction = target - origin;
        float gap = direction.magnitude;
        float angle = Mathf.Atan2(direction.y * TowardDino, Mathf.Abs(direction.x)) * Mathf.Rad2Deg;
        var rotation = Quaternion.Euler(0f, 0f, angle);
        float travel = Mathf.Clamp01(breathAge / .42f);
        // The sheet's nozzle is at x=6 and the opaque jet reaches roughly x=114.
        float span = (gap + .32f) * travel;
        float spriteWidth = span * 128f / 108f;
        flame.Renderer.flipX = TowardDino < 0;
        flame.Renderer.transform.localScale = new Vector3(spriteWidth / (FlameHeight * 128 / 48), 1, 1);
        flame.Renderer.transform.rotation = rotation;
        var nozzleOffset = new Vector3(TowardDino * spriteWidth * (64f - 6f) / 128f, -FlameHeight / 3f, 0);
        flame.Draw(breathAge, origin + rotation * nozzleOffset, true);
        if (!hitThisBreath && span >= gap)
        {
            hitThisBreath = true;
            HitDino(new Color(1f, .43f, .16f));
        }
        if (breathAge >= BreathSeconds || IsGameOver)
        {
            breathing = false;
            flame.Renderer.enabled = false;
        }
    }

    internal override void LaunchCounter()
    {
        if (Request != CounterWord) return;
        spraying = true;
        sprayAge = 0;
    }

    void UpdateSpray()
    {
        stream.enabled = foam.enabled = true;
        sprayAge += Time.deltaTime;
        var start = DinoMouth;
        var target = FireMouth;
        float travel = Mathf.Clamp01(sprayAge * 11f / Mathf.Max(.1f, Vector3.Distance(start, target)));
        for (int i = 0; i < stream.positionCount; i++)
        {
            float t = travel * i / (stream.positionCount - 1);
            var p = Vector3.Lerp(start, target, t);
            p.y += Mathf.Sin(Mathf.PI * t) * (.13f + .018f * Mathf.Sin(t * 26 - sprayAge * 24));
            stream.SetPosition(i, p);
            foam.SetPosition(i, p + Vector3.up * .017f);
        }
        if (travel < 1) return;
        stream.enabled = foam.enabled = false;
        spraying = breathing = false;
        flame.Renderer.enabled = aura.Renderer.enabled = false;
        Enter(Phase.Steam);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        pillar?.Dispose();
        aura?.Dispose();
        flame?.Dispose();
        splash?.Dispose();
        smoke?.Dispose();
    }
}
