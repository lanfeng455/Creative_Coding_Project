using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RiftbarkEncounter : ConstructionEncounter
{
    public enum Phase { Waiting, AfterGlass, Summoning, Threatening, Incoming, Reflected, Exposed, Vortex, Staggered, Departing, Cleared }
    public Phase CurrentPhase { get; private set; }
    public override string Request
    {
        get
        {
            if (IsGameOver) return null;
            if (lodgedThorn && (CurrentPhase == Phase.Exposed || CurrentPhase == Phase.Incoming))
                return WindEncounter.CounterWord;
            bool circleIsOpen = CurrentPhase == Phase.Summoning && age >= 1.1f;
            return !guarding && (circleIsOpen || CurrentPhase == Phase.Threatening || CurrentPhase == Phase.Incoming)
                ? SeedEncounter.CounterWord : null;
        }
    }
    protected override int AfterStep => AFrameBuildPlan.StageCount;

    [SerializeField] MeshRenderer body, heart;
    [SerializeField] Transform thorn;
    [SerializeField] Texture2D circleSheet, riftSheet, shieldSheet, parrySheet, vortexSheet;
    [SerializeField, Min(.1f)] float bodyScale = 3.48f;
    const float ChargeSeconds = 2.2f, ThornRadius = .40f, RiftHeight = 5.6f;
    static readonly int Split = Shader.PropertyToID("_Split");
    static readonly int Windup = Shader.PropertyToID("_Windup");
    static readonly int Materialize = Shader.PropertyToID("_Materialize");
    MaterialPropertyBlock bark;
    BossSkyTransition sky;
    WindFlipbook circle, rift, shield, impact, vortex;
    Transform lodgedThorn;
    float age, flightDuration, guardAge, summonReveal = 1f, vortexDuration;
    double summonAt, nextLaunch, launchedAt, exposedAt, vortexHitAt;
    bool guarding, incoming, circlePlaying;
    Vector3 launch, destination, contact, vortexFrom, vortexTarget;

    void Reset() => attackIntervalSeconds = 5f;

    // Each B2 scene gets its own encounter, including a replay after defeat.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Register()
    {
        SceneManager.sceneLoaded -= Install;
        SceneManager.sceneLoaded += Install;
    }

    static void Install(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "TemplateB2") return;
        DinoAgentB1 builder = null;
        BossSkyTransition background = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (!builder) builder = root.GetComponentInChildren<DinoAgentB1>();
            if (!background) background = root.GetComponentInChildren<BossSkyTransition>();
        }
        var asset = Resources.Load<GameObject>("Riftbark Boss");
        var prefab = asset ? asset.GetComponent<RiftbarkEncounter>() : null;
        if (!prefab || !builder || !builder.world || !background)
        {
            Debug.LogError("Riftbark needs its encounter prefab, the B2 builder and the forest sky.");
            return;
        }
        // The prefab is inactive so all scene references are bound before OnEnable subscribes.
        var boss = Instantiate(prefab, builder.transform);
        boss.transform.SetParent(null, false);
        boss.name = prefab.name;
        boss.dino = builder;
        boss.sky = background;
        boss.transform.position = new Vector3(builder.world.upperFloorXMax - 3.3f, builder.world.upperFloorY, 0);
        boss.gameObject.SetActive(true);
    }

    void Awake()
    {
        bark = new MaterialPropertyBlock();
        body.enabled = false;
        thorn.gameObject.SetActive(false);
        heart.enabled = false;
        circle = new WindFlipbook(transform, circleSheet, 96, 64, 20, .08f, 3.75f, 10);
        rift = new WindFlipbook(transform, riftSheet, 96, 96, 16, .05f, RiftHeight, 10);
        shield = new WindFlipbook(transform, shieldSheet, 40, 56, 16, .09f, 1.23f, 17);
        impact = new WindFlipbook(transform, parrySheet, 96, 80, 13, .055f, 1.5f, 19);
        vortex = new WindFlipbook(transform, vortexSheet, 72, 48, 20, .065f, 1.05f, 18);
    }

    Vector3 Ground => transform.position;
    Vector3 Defender => new Vector3(dino.transform.position.x, dino.world.upperFloorY + .48f, -1.7f);
    Vector3 Guard => Defender + new Vector3(.54f, .14f, 0);
    Vector3 ThrowHand => body.transform.TransformPoint(new Vector3(-.48f, .37f, -.18f));
    Vector3 ThornSocket => body.transform.TransformPoint(new Vector3(.045f, .335f, -.235f));

    protected override void BeginEncounter()
    {
        summonAt = Time.timeAsDouble + 2.0;
        Enter(Phase.AfterGlass);
    }

    void Enter(Phase phase)
    {
        CurrentPhase = phase;
        age = 0;
    }

    void Pose(float size, float yaw = 0, float lean = 0, float windup = 0, float split = 0)
    {
        var t = body.transform;
        t.localScale = Vector3.one * (bodyScale * size * Mathf.Lerp(.94f, 1, summonReveal));
        t.localRotation = Quaternion.Euler(0, 14f + yaw + (1 - summonReveal) * 24, lean - (1 - summonReveal) * 3);
        body.enabled = size > .002f && summonReveal > 0;
        bark.SetFloat(Windup, windup);
        bark.SetFloat(Split, split);
        bark.SetFloat(Materialize, summonReveal);
        body.SetPropertyBlock(bark);
    }

    void Update()
    {
        if (CurrentPhase == Phase.Waiting || CurrentPhase == Phase.Cleared || IsGameOver) return;
        age += Time.deltaTime;
        if (CurrentPhase == Phase.AfterGlass && Time.timeAsDouble >= summonAt)
        {
            Enter(Phase.Summoning);
            circlePlaying = true;
            sky.BeginBossEntrance();
        }
        if (circlePlaying) UpdateCircle();
        switch (CurrentPhase)
        {
            case Phase.Summoning: Pose(1); break;
            case Phase.Threatening:
            case Phase.Incoming:
            case Phase.Exposed: Fight(); break;
            case Phase.Reflected: AdvanceReflection(); break;
            case Phase.Vortex:
                AdvanceVortex();
                if (CurrentPhase == Phase.Vortex) Fight();
                break;
            case Phase.Staggered: FinishHit(); break;
            case Phase.Departing: Depart(); break;
        }
    }

    void UpdateCircle()
    {
        float t = Mathf.Clamp01((float)(Time.timeAsDouble - summonAt) / sky.EntranceDuration);
        float pillar = Mathf.SmoothStep(.56f, 1, Mathf.InverseLerp(.18f, .55f, t));
        circle.Renderer.transform.localScale = new Vector3(1.2f, pillar, 1);
        circle.Draw(t * circle.Duration, Ground + new Vector3(0, -.30f, 1.65f), false);
        summonReveal = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.07f, .83f, t));
        if (t < 1) return;
        circlePlaying = false;
        if (CurrentPhase == Phase.Summoning)
        {
            Enter(Phase.Threatening);
            nextLaunch = Time.timeAsDouble + attackIntervalSeconds;
        }
    }

    void Fight()
    {
        float charge = incoming ? 0 : Mathf.Clamp01(1f - (float)(nextLaunch - Time.timeAsDouble) / ChargeSeconds);
        float recoil = incoming ? Mathf.Clamp01(1 - (float)(Time.timeAsDouble - launchedAt) / .45f) : 0;
        if (CurrentPhase != Phase.Vortex) BattlePose(charge, recoil);
        DrawGuard();
        if (incoming)
        {
            AdvanceAttack();
            return;
        }
        if (charge > 0)
        {
            thorn.gameObject.SetActive(true);
            thorn.position = ThrowHand;
            thorn.localScale = Vector3.one * ThornRadius * Mathf.SmoothStep(0, 1, Mathf.Min(1, charge * 2));
            thorn.rotation = Quaternion.Euler(20, age * 90, age * 56);
        }
        if (Time.timeAsDouble >= nextLaunch) FireThorn();
    }

    void FireThorn()
    {
        nextLaunch = Time.timeAsDouble + attackIntervalSeconds;
        incoming = true;
        thorn.gameObject.SetActive(true);
        thorn.localScale = Vector3.one * ThornRadius;
        StartThornFlight(ThrowHand, Defender, 3.65f);
        if (CurrentPhase != Phase.Vortex) Enter(Phase.Incoming);
    }

    void StartThornFlight(Vector3 from, Vector3 to, float speed)
    {
        launchedAt = Time.timeAsDouble;
        launch = from;
        thorn.position = from;
        destination = to;
        flightDuration = Mathf.Max(.2f, Vector3.Distance(from, to) / speed);
    }

    float FlyThorn(float arc, float endRadius)
    {
        float t = Mathf.Clamp01((float)(Time.timeAsDouble - launchedAt) / flightDuration);
        thorn.position = Vector3.Lerp(launch, destination, t) + Vector3.up * (arc * Mathf.Sin(Mathf.PI * t));
        thorn.Rotate(new Vector3(80, 30, 240) * Time.deltaTime, Space.World);
        thorn.localScale = Vector3.one * Mathf.Lerp(ThornRadius, endRadius, t);
        return t;
    }

    void DrawGuard()
    {
        if (!guarding) return;
        guardAge += Time.deltaTime;
        shield.Draw(Mathf.Min(.17f, guardAge), Guard + new Vector3(.11f, -.53f, -.12f), false);
    }

    void AdvanceAttack()
    {
        float t = FlyThorn(.40f, ThornRadius);
        if (guarding && thorn.position.x <= Guard.x)
        {
            contact = thorn.position;
            incoming = guarding = false;
            Enter(Phase.Reflected);
            StartThornFlight(contact, ThornSocket, 5.8f);
        }
        else if (t >= 1)
        {
            incoming = false;
            thorn.gameObject.SetActive(false);
            HitDino(new Color(.87f, .16f, .38f));
            if (CurrentPhase != Phase.Vortex) Enter(lodgedThorn ? Phase.Exposed : Phase.Threatening);
        }
    }

    void AdvanceReflection()
    {
        Pose(1);
        shield.Draw(.18f + age, contact - Vector3.up * .52f, false);
        impact.Draw(age, contact - Vector3.up * .75f, false);
        destination = ThornSocket;
        if (FlyThorn(.26f, .22f) < 1) return;
        shield.Renderer.enabled = impact.Renderer.enabled = false;
        // Leave the reflected core in the bark. The moving thorn can now be reused for attacks.
        lodgedThorn = Instantiate(thorn, body.transform, true);
        lodgedThorn.name = "Lodged Briar Core";
        thorn.gameObject.SetActive(false);
        heart.enabled = true;
        exposedAt = Time.timeAsDouble;
        nextLaunch = exposedAt + attackIntervalSeconds;
        Enter(Phase.Exposed);
        BattlePose(0, 0);
    }

    void BattlePose(float charge, float recoil)
    {
        float sinceHit = (float)(Time.timeAsDouble - exposedAt);
        float open = lodgedThorn ? Mathf.SmoothStep(0, 1, Mathf.Clamp01(sinceHit / .55f)) : 0;
        Pose(1, -18 * charge + 8 * recoil - 9 * open, -6 * charge + 7 * recoil - 3 * open, charge, open);
        if (!lodgedThorn) return;
        float pulse = open * (1 + .055f * Mathf.Sin(sinceHit * 5));
        heart.transform.localScale = new Vector3(.084f, .117f, .075f) * pulse;
        impact.Draw(sinceHit, ThornSocket - Vector3.up * .75f + Vector3.back * .45f, false);
    }

    internal override void LaunchCounter()
    {
        if (Request == SeedEncounter.CounterWord)
        {
            guarding = true;
            guardAge = 0;
            DrawGuard();
            if (!incoming) FireThorn();
        }
        else if (Request == WindEncounter.CounterWord)
        {
            // An already-composed word can land on the first frame the heart opens.
            exposedAt = System.Math.Min(exposedAt, Time.timeAsDouble - .55);
            BattlePose(0, 0);
            impact.Renderer.enabled = false;
            vortexFrom = Defender + Vector3.right * .36f;
            vortexTarget = heart.transform.position;
            vortexDuration = Mathf.Max(.2f, Vector3.Distance(vortexFrom, vortexTarget) / 5f);
            Enter(Phase.Vortex);
            AdvanceVortex();
        }
    }

    void AdvanceVortex()
    {
        float t = Mathf.Clamp01(age / vortexDuration);
        vortex.Draw(age, Vector3.Lerp(vortexFrom, vortexTarget, t) - Vector3.up * .525f, true);
        if (t < 1) return;
        vortexHitAt = Time.timeAsDouble;
        incoming = false;
        vortex.Renderer.enabled = false;
        thorn.gameObject.SetActive(false);
        lodgedThorn.gameObject.SetActive(false);
        Enter(Phase.Staggered);
        FinishHit();
    }

    void FinishHit()
    {
        // A player can finish both words during summoning. Stop attacks on impact
        // and let the five-second entrance finish before opening the exit portal.
        if (circlePlaying)
        {
            Pose(1, -9, -3, split: 1);
            DrawVortexImpact();
            return;
        }
        Enter(Phase.Departing);
        sky.BeginBossRetreat();
        Depart();
    }

    void Depart()
    {
        float gate = Mathf.SmoothStep(0, 1, Mathf.Clamp01(age / .55f))
            * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1.75f, 2.65f, age)));
        var centre = Ground + new Vector3(0, 1.83f, 2f);
        rift.Renderer.transform.localScale = new Vector3(gate, gate, 1);
        rift.Draw(age, centre - Vector3.up * (RiftHeight * gate * .5f), true);
        float pull = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.35f, 1.7f, age));
        body.transform.localPosition = new Vector3(0, 1.83f, 1.72f) * pull;
        Pose(1 - pull, -9 + pull * 135, -3 + pull * 18, 0, 1 - pull);
        heart.enabled = body.enabled;
        DrawVortexImpact();
        if (age < 2.65f) return;
        rift.Renderer.enabled = impact.Renderer.enabled = false;
        if (!sky.IsClear) return;
        Enter(Phase.Cleared);
        dino.StartHomecoming();
        ReleaseBuilder();
        AudioManager.PlayMusic(Music.Victory, 1.2f);   // BOSS 击破 → 弦乐收尾（回家阶段沿用，不再切换）
    }

    void DrawVortexImpact()
    {
        impact.Draw((float)(Time.timeAsDouble - vortexHitAt), vortexTarget - Vector3.up * .75f + Vector3.back * .45f, false);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        circle?.Dispose(); rift?.Dispose(); shield?.Dispose(); impact?.Dispose(); vortex?.Dispose();
    }
}
