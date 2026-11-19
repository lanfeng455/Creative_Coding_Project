using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public sealed class PorchHomecoming : MonoBehaviour
{
    public const string Lamp = "LAMP", Bell = "BELL";
    public enum Phase { Return, Choice, Hang, Listen, Enter, Wave, Finished }
    public Phase CurrentPhase { get; private set; }
    public string GiftWord { get; private set; }
    public bool Choosing => CurrentPhase == Phase.Choice;

    DinoAgentB1 dino;
    PorchDetails porch;
    RightHandIndexRayProvider hand;
    Camera cam;
    AudioSource sound;
    Vector3 from, porchStep, startCamera, dinoScale;
    float startZoom, age, lostTime, swipeTravel;
    Vector2 previousPoint;
    bool held, lastGrip, previousValid, handInput, mouseDrag, nearHook, replaying;
    static float Ease(float value) => Mathf.SmoothStep(0, 1, Mathf.Clamp01(value));

    internal void Begin(DinoAgentB1 builder)
    {
        dino = builder;
        cam = Camera.main;
        startCamera = cam.transform.position;
        startZoom = cam.orthographicSize;
        from = dino.transform.position;
        dinoScale = dino.transform.localScale;
        porchStep = transform.TransformPoint(new Vector3(-1.18f, .05f, 0));
        porchStep.z = from.z;
        porch = gameObject.AddComponent<PorchDetails>();
        porch.Build(dino);
        hand = FindFirstObjectByType<RightHandIndexRayProvider>();
        if (hand) hand.TrackPorchPinch = true;
        TypeWriterB1.Instance?.Clear();
        sound = gameObject.AddComponent<AudioSource>();
        sound.playOnAwake = false;
        AudioManager.PlayAmbience(Sfx.Ambience, 1f);
        PlaySound("ForestReturn", .65f);
    }

    void PlaySound(string clipName, float volume)
    {
        var clip = Resources.Load<AudioClip>("Homecoming/" + clipName);
        if (clip) sound.PlayOneShot(clip, volume * AudioManager.Instance.sfxVolume);
    }

    void Change(Phase phase)
    {
        CurrentPhase = phase;
        age = 0;
        previousValid = false;
        switch (phase)
        {
            case Phase.Choice: porch.Message("LAMP  /  BELL", "Spell a gift for the porch."); break;
            case Phase.Hang: porch.Message("Hang the " + GiftWord.ToLowerInvariant(), "Pinch or drag it to the brass ring."); break;
            case Phase.Listen: porch.Message("Ring the bell", "Sweep your hand or mouse past it."); break;
            case Phase.Finished: porch.Message("Home, sweet home.", "Play again  >"); break;
            default: porch.Message(null,null); break;
        }
    }

    internal void Choose(string word)
    {
        if (!Choosing || (word != Lamp && word != Bell)) return;
        GiftWord = word;
        // Stop the arcade input after the choice. The porch still reads the hand directly.
        var writer = TypeWriterB1.Instance;
        if (writer) { writer.Clear(); writer.gameObject.SetActive(false); }
        var band = FindFirstObjectByType<SlingshotBand>();
        if (band) band.gameObject.SetActive(false);
        foreach (var letter in PhysicsLetter.All)
            if (letter && letter.gameObject.scene == gameObject.scene) Destroy(letter.gameObject);
        Vector2 screen = Input.mousePosition;
        if (hand && hand.TryGetPorchPointer(out var tip,out _)) screen = tip;
        Vector3 view = cam.ScreenToViewportPoint(screen);
        view.x = Mathf.Clamp(view.x,.08f,.92f);
        view.y = Mathf.Clamp(view.y,.12f,.90f);
        Vector3 at = cam.ViewportToWorldPoint(new Vector3(view.x, view.y, -1f-cam.transform.position.z));
        porch.MakeGift(word == Lamp, at);
        Change(Phase.Hang);
    }

    void Update()
    {
        age += Time.deltaTime;
        switch (CurrentPhase)
        {
            case Phase.Return:
                float duration = Mathf.Max(1.1f, Vector2.Distance(from,porchStep)/2.1f);
                Walk(from, porchStep, age/duration);
                if (age >= duration + .28f) Change(Phase.Choice);
                break;
            case Phase.Hang:
            case Phase.Listen:
            case Phase.Finished:
                ReadPointer();
                if (CurrentPhase == Phase.Listen)
                    porch.Gift.localRotation = Quaternion.Euler(0,0,4*Mathf.Sin(age*4)*Mathf.Exp(-age*2));
                if (CurrentPhase == Phase.Finished && Input.GetKeyDown(KeyCode.Return)) Replay();
                break;
            case Phase.Enter: EnterHouse(); break;
            case Phase.Wave:
                FrameHouse(1);
                porch.WindowWave(age);
                if (age >= 3.7f) Change(Phase.Finished);
                break;
        }
        if (!replaying) porch.Layout(cam, CurrentPhase == Phase.Finished);
    }

    void ReadPointer()
    {
        Vector2 screen = Input.mousePosition;
        bool grip = Input.GetMouseButton(0);
        if (Input.GetMouseButtonDown(0)) mouseDrag = true;
        bool useHand = false;
        if (!mouseDrag && hand && hand.TryGetPorchPointer(out var tip, out bool pinch))
        {
            screen = tip; grip = pinch; useHand = true;
        }
        if (useHand != handInput)
        {
            // Tracking loss is never a release at the hook or a swipe from stale coordinates.
            if (handInput && !mouseDrag && (lostTime += Time.unscaledDeltaTime) < .18f) return;
            previousValid = false; held = false; swipeTravel = 0;
        }
        lostTime = 0; handInput = useHand;
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -1f-cam.transform.position.z));
        MovePointer(world, grip);
        if (!Input.GetMouseButton(0)) mouseDrag = false;
    }

    // Screen devices converge here after conversion to the game's orthographic plane.
    internal void MovePointer(Vector2 point, bool grip)
    {
        porch.Pointer(point, true);
        Vector2 hook = porch.Hook;
        if (CurrentPhase == Phase.Hang)
        {
            Vector2 giftCenter = (Vector2)porch.Gift.position + Vector2.down*.35f;
            if (!held && grip && Vector2.Distance(point,giftCenter) < 1.0f) held = true;
            if (held)
            {
                nearHook = Vector2.Distance(point,hook) <= .87f;
                if (nearHook) porch.Gift.position = porch.Hook;
                else
                {
                    var pos = cam.WorldToViewportPoint(new Vector3(point.x, point.y + .15f, -1));
                    pos.x = Mathf.Clamp(pos.x, .035f, .965f);
                    pos.y = Mathf.Clamp(pos.y, .105f, .96f);
                    porch.Gift.position = cam.ViewportToWorldPoint(pos);
                }
                if (!grip)
                {
                    held = false;
                    if (nearHook)
                    {
                        porch.Mount();
                        if (GiftWord == Lamp) StartEntry();
                        else Change(Phase.Listen);
                        previousValid = false;
                        lastGrip = false;
                        return;
                    }
                }
            }
            porch.PreviewHook(held && nearHook);
        }
        else if (CurrentPhase == Phase.Listen && age >= .35f && previousValid && !grip)
        {
            Vector2 bell = hook + Vector2.down*.38f;
            Vector2 delta = point - previousPoint;
            float along = Mathf.Clamp01(Vector2.Dot(bell-previousPoint,delta)/Mathf.Max(.00001f,delta.sqrMagnitude));
            if (Vector2.Distance(previousPoint+delta*along,bell) < .64f && delta.magnitude < 2.5f)
            {
                if (swipeTravel*delta.x < 0) swipeTravel = 0;
                swipeTravel += delta.x;
                if (Mathf.Abs(swipeTravel) > .50f) StartEntry();
            }
            else swipeTravel = 0;
        }
        else if (CurrentPhase == Phase.Finished && previousValid && grip && !lastGrip && porch.OverReplay(point))
            Replay();
        lastGrip = grip;
        previousValid = true;
        previousPoint = point;
    }

    void StartEntry()
    {
        if (GiftWord == Bell) PlaySound("PorchChime", .90f);
        porch.LightUp(GiftWord == Lamp);
        porch.Pointer(Vector2.zero,false);
        Change(Phase.Enter);
    }

    void Walk(Vector3 a, Vector3 b, float t)
    {
        float u = Ease(t);
        Vector3 p = Vector3.Lerp(a,b,u);
        p.y += Mathf.Abs(Mathf.Sin(u*Mathf.PI*4))*.075f;
        dino.transform.position = p;
    }

    void EnterHouse()
    {
        FrameHouse(Ease(age/2.9f));
        porch.LightLevel(Ease(age/.70f));
        if (GiftWord == Bell)
            porch.Gift.localRotation = Quaternion.Euler(0,0,27*Mathf.Sin(age*10)*Mathf.Exp(-1.55f*age));
        // A short look back at the bell / down into the light before crossing the threshold.
        float glance = Mathf.Sin(Mathf.PI*Mathf.Clamp01(age/.8f));
        dino.transform.localRotation = Quaternion.Euler(0,0,(GiftWord == Bell ? 9 : -7)*glance);
        float walk = Mathf.InverseLerp(.85f,2.12f,age);
        Walk(porchStep, new Vector3(porch.Door.x, porch.Door.y, porchStep.z),walk);
        dino.transform.localScale = dinoScale*Mathf.Lerp(1,.08f,Ease(Mathf.InverseLerp(.62f,1,walk)));
        float open = Ease(Mathf.InverseLerp(.72f,1.28f,age))*(1-Ease(Mathf.InverseLerp(2.15f,2.65f,age)));
        if (age >= .72f) porch.OpenDoor(open);
        if (age >= 2.15f && dino.gameObject.activeSelf) dino.gameObject.SetActive(false);
        if (age >= 2.9f) Change(Phase.Wave);
    }

    void FrameHouse(float t)
    {
        float zoom = Mathf.Max(1.88f, 2.85f / cam.aspect);
        Vector3 end = transform.position + new Vector3(-zoom*cam.aspect*.30f, 1.26f,0);
        end.z = startCamera.z;
        cam.orthographicSize = Mathf.Lerp(startZoom,zoom,t);
        cam.transform.position = Vector3.Lerp(startCamera,end,t);
    }

    internal void Replay()
    {
        if (replaying || CurrentPhase != Phase.Finished) return;
        replaying = true;
        var scene = gameObject.scene;
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path,new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(scene.buildIndex);
#endif
    }

    void OnDestroy()
    {
        if (hand) hand.TrackPorchPinch = false;
    }
}
