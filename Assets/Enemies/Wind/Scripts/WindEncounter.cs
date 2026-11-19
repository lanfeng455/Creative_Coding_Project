using UnityEngine;

// Placed only in TemplateB2. The transform is the wisp's hovering centre.
public sealed class WindEncounter : ConstructionEncounter
{
    public const string CounterWord = "VORTEX";
    public enum Phase { Waiting, Summoning, Fighting, Dispersing, Cleared, GameOver }
    [SerializeField] private MeshRenderer character;
    [SerializeField] private Texture2D summonSheet, tornadoSheet, vortexSheet, smokeSheet;
    public Phase CurrentPhase { get; private set; }
    public override string Request => CurrentPhase == Phase.Fighting && !counterFlying ? CounterWord : null;
    WindFlipbook summon, tornado, vortex, smoke;
    const float VortexHeight = .78f;
    const float VortexSpeed = 3.2f;
    Vector2 counterCenter, counterAim;
    Vector3 restScale;
    Quaternion restRotation;
    float age, untilAttack, tornadoAge, counterAge;
    float tornadoX;
    bool tornadoFlying, counterFlying;

    void Awake()
    {
        if (!dino || !character || !summonSheet || !tornadoSheet || !vortexSheet || !smokeSheet)
        {
            Debug.LogError("Missing WindEncounter scene reference.", this);
            enabled = false;
            return;
        }
        restScale = character.transform.localScale;
        restRotation = character.transform.localRotation;
        character.enabled = false;
        summon = new WindFlipbook(transform,summonSheet,40,64,19,.06f,2.5f,12);
        summon.Renderer.color = new Color(.75f,.65f,.94f);
        tornado = new WindFlipbook(transform,tornadoSheet,48,72,14,.065f,1.05f,14);
        vortex = new WindFlipbook(transform,vortexSheet,72,48,20,.07f,VortexHeight,15);
        smoke = new WindFlipbook(transform,smokeSheet,64,56,21,.07f,2.55f,16);
    }

    Vector3 Floor(float x) => new Vector3(x, dino.world.upperFloorY + .015f, -.7f);

    Vector3 MistBase => transform.position - Vector3.up * .93f;

    protected override int AfterStep => 1;
    protected override void BeginEncounter()
    {
        CurrentPhase = Phase.Summoning;
        age = 0;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        age += dt;
        if (CurrentPhase == Phase.Summoning)
        {
            // Gather the last, wispy frames inward as the body appears.
            float forming = Mathf.Clamp01(age / 1.45f);
            smoke.Draw(Mathf.Lerp(1.32f, .42f, forming), MistBase, false,
                Mathf.Sin(Mathf.PI * forming));
            summon.Draw(age * .65f, MistBase, false);
            float reveal = Mathf.SmoothStep(0,1,Mathf.InverseLerp(.36f,1.5f,age));
            character.enabled = reveal > 0;
            character.transform.localScale = restScale * reveal;
            Pose(age,0);
            if (age >= 1.85f)
            {
                smoke.Renderer.enabled = false;
                CurrentPhase = Phase.Fighting;
                untilAttack = attackIntervalSeconds;
                age = 0;
            }
        }
        else if (CurrentPhase == Phase.Fighting)
        {
            untilAttack -= dt;
            float casting = Mathf.Clamp01(1f - untilAttack / 1.3f);
            Pose(age,casting);
            if (untilAttack <= 0 && !tornadoFlying)
            {
                tornadoFlying = true;
                tornadoAge = 0;
                tornadoX = transform.position.x;
                untilAttack += attackIntervalSeconds;
            }
            if (counterFlying) AdvanceCounter(dt);
            if (CurrentPhase == Phase.Fighting && tornadoFlying) AdvanceTornado(dt);
        }
        else if (CurrentPhase == Phase.Dispersing)
        {
            vortex.Renderer.enabled = false;
            smoke.Draw(age, MistBase, false);
            character.transform.localScale = restScale * (1f - Mathf.SmoothStep(0,1,age/.62f));
            character.enabled = age < .62f;
            if (age >= smoke.Duration)
            {
                CurrentPhase = Phase.Cleared;
                ReleaseBuilder();
                AudioManager.PlayMusic(Music.Main, 1.5f);   // 小怪清完回到日常
            }
        }
    }

    void Pose(float t,float casting)
    {
        character.transform.localPosition = Vector3.up * (.10f * Mathf.Sin(t * 2.2f) + casting * .11f);
        character.transform.localRotation = restRotation * Quaternion.Euler(-10f * casting, 12f * Mathf.Sin(t * 1.7f), 5f * Mathf.Sin(t * 2.3f));
    }

    internal override void LaunchCounter()
    {
        if (Request != CounterWord) return;
        counterAge = 0;
        counterAim = character.bounds.center;
        float side = Mathf.Sign(counterAim.x - dino.transform.position.x);
        counterCenter = (Vector2)dino.transform.position + new Vector2(.24f * side, .44f);
        counterFlying = true;
        DrawCounter();
    }

    void DrawCounter()
    {
        // Flipbook frames use a bottom pivot. Aim the visible centre at the wisp.
        var pivot = new Vector3(counterCenter.x, counterCenter.y - VortexHeight * .5f, -.7f);
        vortex.Draw(counterAge, pivot, true);
    }

    void AdvanceCounter(float dt)
    {
        counterAge += dt;
        counterCenter = Vector2.MoveTowards(counterCenter, counterAim, VortexSpeed * dt);
        DrawCounter();
        if (counterCenter != counterAim) return;
        counterFlying = tornadoFlying = false;
        tornado.Renderer.enabled = false;
        CurrentPhase = Phase.Dispersing;
        age = 0;
    }

    void AdvanceTornado(float dt)
    {
        tornadoAge += dt;
        float target = dino.transform.position.x;
        tornadoX = Mathf.MoveTowards(tornadoX,target,2f*dt);
        tornado.Draw(tornadoAge,Floor(tornadoX),true);
        if (Mathf.Abs(tornadoX-target) > .26f) return;
        tornadoFlying = false;
        tornado.Renderer.enabled = false;
        HitDino(new Color(.8f, .57f, .86f));
        if (!IsGameOver) return;
        CurrentPhase = Phase.GameOver;
        counterFlying = false;
        vortex.Renderer.enabled = false;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        summon?.Dispose();
        tornado?.Dispose();
        vortex?.Dispose();
        smoke?.Dispose();
    }
}
