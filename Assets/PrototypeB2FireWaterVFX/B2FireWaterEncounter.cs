using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public sealed class B2FireWaterEncounter : MonoBehaviour
{
    private const string ScenePath = "Assets/Scenes/TemplateB2.unity";
    private const string PreviewMenu = "Tools/FOCUS/B2/Preview Fire and Water VFX";

    private B1LayerConfig _world;
    private DinoAgentB1 _dino;
    private Transform _mouth;
    private B2FireWaterEffects _effects;
    private FireHazard _fire;
    private SpriteRenderer _flames;
    private B2FireWaterItem _spit;
    private Vector3 _foot;
    private bool _aiming;
    private float _anticipation;
    private float _recoilUntil;
    private bool _releaseAtMouth;
    private Vector3 _releaseOrigin;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        for (int i = 0; i < SceneManager.sceneCount; i++)
            OnSceneLoaded(SceneManager.GetSceneAt(i), LoadSceneMode.Single);
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.path != ScenePath || !scene.isLoaded) return;

        B1LayerConfig world = null;
        DinoAgentB1 dino = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponent<B2FireWaterEncounter>() != null) return;
            if (world == null) world = root.GetComponentInChildren<B1LayerConfig>();
            if (dino == null) dino = root.GetComponentInChildren<DinoAgentB1>();
        }
        if (world == null || dino == null) return;

        var rootObject = new GameObject("B2 Fire Water VFX");
        SceneManager.MoveGameObjectToScene(rootObject, scene);
        var encounter = rootObject.AddComponent<B2FireWaterEncounter>();
        encounter.Initialize(world, dino);
    }

    private void Initialize(B1LayerConfig world, DinoAgentB1 dino)
    {
        _world = world;
        _dino = dino;
        _effects = new B2FireWaterEffects(this);
        enabled = _effects.IsReady;
        if (!enabled) return;

        _mouth = new GameObject("B2 Mouth Anchor").transform;
        _mouth.SetParent(dino.transform, false);
        // The original 18 x 20 sprite uses 32 pixels per unit and a bottom-center pivot.
        _mouth.localPosition = new Vector3(8.5f / 32f, 16.5f / 32f);
        _world.WaterCrafted += TrySpitWater;
    }

    private void Update()
    {
        if (_fire != null) return;
        if (_flames != null) HideFlames();

        var active = FireHazard.ActiveFire;
        if (active != null && active.gameObject.scene == gameObject.scene)
            StartCoroutine(Ignite(active));
    }

    private IEnumerator Ignite(FireHazard fire)
    {
        _fire = fire;
        float sceneCenter = (_world.boundsXMin + _world.boundsXMax) * 0.5f;
        float rightEdge = _world.upperFloorXMax;
        float rightZoneStart = Mathf.Lerp(sceneCenter, rightEdge, 0.5f);
        float rightmostCenter = rightEdge - B2FireWaterEffects.GroundFlameWidth * 0.5f - 0.2f;
        var firePosition = fire.transform.position;
        firePosition.x = Random.Range(Mathf.Min(rightZoneStart, rightmostCenter), rightmostCenter);
        fire.transform.position = firePosition;
        _foot = new Vector3(firePosition.x, _world.upperFloorY + 0.02f);
        // Keep the existing hazard registration and request logic. The package
        // supplies the fire loop and replaces the original stream of glyph sparks.
        fire.enabled = false;
        var glyph = fire.GetComponentInChildren<TextMeshPro>();
        if (glyph != null)
        {
            glyph.enabled = false;
            glyph.transform.localPosition += Vector3.up * 0.55f;
        }

        yield return _effects.Fireball(_foot);
        if (fire == null || FireHazard.ActiveFire != fire) yield break;

        _flames = _effects.Ignite(_foot);
        if (glyph != null) glyph.enabled = true;
    }

    private void LateUpdate()
    {
        if (_dino != null && _fire != null && (_aiming || Time.time < _recoilUntil))
        {
            var dinoTransform = _dino.transform;
            Vector3 scale = dinoTransform.localScale;
            float direction = _foot.x < dinoTransform.position.x ? -1f : 1f;
            float compression = _aiming ? _anticipation : -0.5f * Mathf.Clamp01((_recoilUntil - Time.time) / 0.12f);
            scale.x = Mathf.Abs(scale.x) * direction * (1f + 0.14f * compression);
            scale.y *= 1f - 0.14f * compression;
            dinoTransform.localScale = scale;
        }

        // Read the anchor after both the dinosaur's normal motion and its spit pose.
        if (_releaseAtMouth)
        {
            _releaseAtMouth = false;
            if (CanContinue(_spit, _fire))
            {
                _releaseOrigin = _mouth.position;
                _spit.ShowSpitAt(_releaseOrigin);
            }
        }
    }

    private bool TrySpitWater()
    {
        var fire = FireHazard.ActiveFire;
        if (!isActiveAndEnabled || _mouth == null || fire == null || fire.gameObject.scene != gameObject.scene)
            return false;
        if (_spit != null) return true;

        var shot = new GameObject("B2 Water Spit", typeof(SpriteRenderer));
        shot.transform.SetParent(transform);
        shot.transform.position = _mouth.position;
        _spit = shot.AddComponent<B2FireWaterItem>();
        StartCoroutine(SpitWater(_spit, fire));
        return true;
    }

    private IEnumerator SpitWater(B2FireWaterItem water, FireHazard fire)
    {
        while (CanContinue(water, fire) && _flames == null)
            yield return null;

        _aiming = true;
        const float windup = 0.12f;
        for (float t = 0f; t < windup && CanContinue(water, fire); t += Time.deltaTime)
        {
            _anticipation = t / windup;
            yield return null;
        }
        _aiming = false;

        if (CanContinue(water, fire))
        {
            _recoilUntil = Time.time + 0.12f;
            _releaseAtMouth = true;
            yield return null;
        }

        if (CanContinue(water, fire))
        {
            Vector3 start = _releaseOrigin;
            Vector3 target = _foot + Vector3.up * 0.14f;
            float duration = Mathf.Clamp(Vector3.Distance(start, target) / 5.7f, 0.45f, 1.1f);
            float trailTimer = 0.065f;
            for (float t = Time.deltaTime; t < duration && CanContinue(water, fire); t += Time.deltaTime)
            {
                float progress = t / duration;
                water.GrowSpitBall(t);
                water.transform.position = Vector3.Lerp(start, target, progress)
                    + Vector3.up * (Mathf.Sin(progress * Mathf.PI) * 0.40f);
                if (t >= trailTimer)
                {
                    _effects.Droplet(water.transform.position);
                    trailTimer = t + 0.065f;
                }
                yield return null;
            }

            if (CanContinue(water, fire))
            {
                water.transform.position = target;
                fire.gameObject.SetActive(false);
                Destroy(fire.gameObject);
                HideFlames();
                _effects.Quench(_foot);
            }
        }

        if (water != null) water.Consume();
        _spit = null;
    }

    private bool CanContinue(B2FireWaterItem water, FireHazard fire)
        => water != null && water.Alive && _mouth != null && fire != null && FireHazard.ActiveFire == fire;

    private void HideFlames()
    {
        if (_flames == null) return;
        _flames.gameObject.SetActive(false);
        Destroy(_flames.gameObject);
        _flames = null;
    }

    private void OnDestroy()
    {
        if (_world != null) _world.WaterCrafted -= TrySpitWater;
        if (_mouth != null) Destroy(_mouth.gameObject);
        _effects?.Release();
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem(PreviewMenu, true)]
    private static bool CanPreview()
        => Application.isPlaying && SceneManager.GetActiveScene().path == ScenePath;

    [UnityEditor.MenuItem(PreviewMenu)]
    private static void Preview()
    {
        var encounter = FindFirstObjectByType<B2FireWaterEncounter>();
        if (encounter == null || !encounter.enabled) return;
        if (FireHazard.ActiveFire != null || B1Water.WaterInFlight())
        {
            Debug.LogWarning("[B2 Fire Water VFX] Finish the active fire and water encounter before previewing.");
            return;
        }
        encounter.StartCoroutine(encounter.RunPreview());
    }

    private IEnumerator RunPreview()
    {
        var fire = new GameObject("Fire").AddComponent<FireHazard>();
        fire.Init(_world);

        yield return new WaitUntil(() => _flames != null || fire == null);
        if (fire == null) yield break;
        yield return new WaitForSeconds(0.35f);
        if (fire == null) yield break;

        TrySpitWater();
    }
#endif
}
