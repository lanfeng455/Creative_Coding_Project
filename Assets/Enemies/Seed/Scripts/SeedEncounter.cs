using UnityEngine;

// The root marks the ground under the pod; the dinosaur keeps ownership of health and construction.
public sealed class SeedEncounter : ConstructionEncounter
{
    public const string CounterWord = "SHIELD";
    public enum Phase { Waiting, Emerging, Fighting, Reflected, Burrowing, Cleared }
    public Phase CurrentPhase { get; private set; }
    public override string Request => CurrentPhase == Phase.Fighting && !shieldReady && !IsGameOver ? CounterWord : null;
    protected override int AfterStep => 3;

    [SerializeField] MeshRenderer pod;
    [SerializeField] Transform thorn;
    [SerializeField] Texture2D sporesSheet, leavesSheet, shieldSheet, parrySheet;
    const float BodyScale = 1.45f;
    [SerializeField, Min(1f)] float shotSpeed = 3f;

    WindFlipbook spores, leaves, barrier, parry;
    Vector3 restPosition, restScale, launch, destination, shieldContact;
    Quaternion restRotation;
    float age, nextAttack, flightTime, travelDuration, shieldAge;
    bool inFlight, shieldReady;

    Vector3 Ground => transform.position + Vector3.back * .4f;
    float Side => Mathf.Sign(transform.position.x - dino.transform.position.x);
    Vector3 DinoChest => new Vector3(dino.transform.position.x, dino.transform.position.y + .36f, Ground.z);
    Vector3 Muzzle => Ground + new Vector3(-Side * .30f, .50f, 0) * BodyScale;
    Vector3 Guard => DinoChest + Vector3.right * (.42f * Side);

    void Awake()
    {
        if (!dino || !pod || !thorn || !sporesSheet || !leavesSheet || !shieldSheet || !parrySheet)
        {
            Debug.LogError("Missing SeedEncounter scene reference.", this);
            enabled = false;
            return;
        }
        restPosition = pod.transform.localPosition * BodyScale;
        restRotation = pod.transform.localRotation;
        restScale = pod.transform.localScale * BodyScale;
        thorn.localScale *= 1.2f;
        pod.enabled = false;
        thorn.gameObject.SetActive(false);
        spores = new WindFlipbook(transform, sporesSheet, 64, 56, 20, .08f, 2.06f, 15);
        leaves = new WindFlipbook(transform, leavesSheet, 80, 56, 18, .065f, 1.72f, 16);
        barrier = new WindFlipbook(transform, shieldSheet, 40, 56, 16, .10f, .87f, 16);
        parry = new WindFlipbook(transform, parrySheet, 96, 80, 13, .06f, 1.14f, 18);
    }

    protected override void BeginEncounter() => Enter(Phase.Emerging);

    void Enter(Phase phase)
    {
        CurrentPhase = phase;
        age = 0;
    }

    void Pose(float size, float height, float yaw, float lean)
    {
        pod.enabled = size > .01f;
        pod.transform.localScale = Vector3.Scale(restScale, new Vector3(1f / height, height, 1)) * size;
        pod.transform.localPosition = restPosition * (height * size);
        pod.transform.localRotation = restRotation * Quaternion.Euler(0, yaw, lean);
    }

    void Update()
    {
        if (CurrentPhase == Phase.Waiting || CurrentPhase == Phase.Cleared || IsGameOver) return;
        age += Time.deltaTime;
        switch (CurrentPhase)
        {
            case Phase.Emerging:
                // Leaves close in around the feet, followed by a green burst as the shell rises.
                leaves.Draw(Mathf.Max(0, leaves.Duration - .001f - age), Ground, false, Mathf.Clamp01(1 - age / leaves.Duration));
                spores.Draw(age, Ground - Vector3.up * .07f, false);
                float rise = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.24f, 1.15f, age));
                Pose(rise, Mathf.Lerp(.66f, 1, rise), (1 - rise) * 38, 0);
                if (age >= spores.Duration)
                {
                    leaves.Renderer.enabled = false;
                    Enter(Phase.Fighting);
                    nextAttack = attackIntervalSeconds;
                }
                break;
            case Phase.Fighting:
                Fight();
                break;
            case Phase.Reflected:
                barrier.Draw(.2f + age, ShieldBase(shieldContact), false);
                parry.Draw(age, shieldContact - Vector3.up * .57f, false);
                if (Fly(.18f))
                {
                    thorn.gameObject.SetActive(false);
                    Enter(Phase.Burrowing);
                }
                break;
            case Phase.Burrowing:
                barrier.Renderer.enabled = parry.Renderer.enabled = false;
                float fold = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.22f, 1.1f, age));
                Pose(1 - fold, 1 - fold * .28f, fold * 112, -Side * 14 * Mathf.Sin(age * 10) * (1-fold));
                leaves.Draw(age, Ground + Vector3.up * .11f, false);
                spores.Draw(age, Ground - Vector3.up * .07f, false);
                if (age >= spores.Duration)
                {
                    Enter(Phase.Cleared);
                    ReleaseBuilder();
                }
                break;
        }
    }

    internal override void LaunchCounter()
    {
        if (Request != CounterWord) return;
        shieldReady = true;
        shieldAge = 0;
        DrawGuard();
        if (!inFlight) FireSeed();
        else if ((thorn.position.x - Guard.x) * Side <= 0f) Reflect(thorn.position);
    }

    Vector3 ShieldBase(Vector3 contact)
    {
        // The painted face is left of the centre of each transparent frame.
        return contact + new Vector3(Side * .09f, -.42f, 0);
    }

    void DrawGuard()
    {
        barrier.Renderer.flipX = Side < 0;
        barrier.Draw(Mathf.Min(shieldAge, .11f), ShieldBase(Guard), false);
        barrier.Renderer.transform.localScale = Vector3.one * (1 + .035f * Mathf.Sin(shieldAge * 4));
    }

    void Fight()
    {
        float aim = Mathf.Clamp01(1 - (nextAttack - age) / 1.7f);
        float recoil = inFlight ? Mathf.Clamp01(1 - flightTime / .32f) : 0;
        Pose(1, 1 + .025f * Mathf.Sin(age * 3) - aim * .12f, Side * (aim * 28 + recoil * 8), Side * (aim * 8 - recoil * 17));
        if (shieldReady)
        {
            shieldAge += Time.deltaTime;
            DrawGuard();
        }
        if (!inFlight && age >= nextAttack)
        {
            nextAttack += attackIntervalSeconds;
            FireSeed();
        }
        if (!inFlight) return;
        float before = (thorn.position.x - Guard.x) * Side;
        bool arrived = Fly(.32f);
        float after = (thorn.position.x - Guard.x) * Side;
        if (shieldReady && before >= 0 && after <= 0)
        {
            float t = Mathf.InverseLerp(launch.x, destination.x, Guard.x);
            Reflect(Vector3.Lerp(launch, destination, t) + Vector3.up * (.32f * Mathf.Sin(Mathf.PI * t)));
        }
        else if (arrived)
        {
            inFlight = false;
            thorn.gameObject.SetActive(false);
            HitDino(new Color(.82f, .67f, .26f));
        }
    }

    void FireSeed()
    {
        inFlight = true;
        BeginShot(Muzzle, DinoChest, shotSpeed);
    }

    void Reflect(Vector3 contact)
    {
        inFlight = false;
        shieldContact = contact;
        BeginShot(contact, Muzzle, shotSpeed * 1.65f);
        Enter(Phase.Reflected);
    }

    void BeginShot(Vector3 from, Vector3 to, float speed)
    {
        launch = from;
        destination = to;
        flightTime = 0;
        travelDuration = Mathf.Max(.15f, Vector3.Distance(from, to) / speed);
        thorn.position = from;
        thorn.gameObject.SetActive(true);
    }

    bool Fly(float arc)
    {
        flightTime += Time.deltaTime;
        float t = Mathf.Clamp01(flightTime / travelDuration);
        thorn.position = Vector3.Lerp(launch, destination, t) + Vector3.up * (arc * Mathf.Sin(t * Mathf.PI));
        Vector3 tangent = destination - launch + Vector3.up * (arc * Mathf.PI * Mathf.Cos(t * Mathf.PI));
        thorn.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(tangent.y, tangent.x) * Mathf.Rad2Deg)
            * Quaternion.AngleAxis(flightTime * 270, Vector3.right);
        return t >= 1;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        spores?.Dispose();
        leaves?.Dispose();
        barrier?.Dispose();
        parry?.Dispose();
    }
}
