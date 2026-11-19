using UnityEngine;

[DisallowMultipleComponent]
public sealed class BossSkyTransition : MonoBehaviour
{
    [SerializeField, Min(.1f)] private float entranceSeconds = 5f;
    internal float EntranceDuration => entranceSeconds;
    [SerializeField, Min(.05f)] private float darkenSeconds = .22f;
    [SerializeField, Min(.1f)] private float restoreSeconds = 2.8f;
    [SerializeField, Range(0f, .9f)] private float peakDimming = .60f;

    // Sky, distant forest, side trees, ground, foreground leaves.
    private static readonly float[] LayerDelays = { 0f, .10f, .20f, .28f, .24f };
    private static readonly int RedTexture = Shader.PropertyToID("_BossRedTex");
    private static readonly int Reveal = Shader.PropertyToID("_BossReveal");
    private static readonly int Dim = Shader.PropertyToID("_BossDim");
    private static readonly int Daylight = Shader.PropertyToID("_DaylightReturn");

    private enum Phase { Clear, Entering, Red, Darkening, Restoring }
    private Phase phase;
    internal bool IsClear => phase == Phase.Clear;
    private Texture2D[] redLayers;
    private float progress, dim, daylight;

    internal void Bind(ForestGladeBackground.Layer[] layers)
    {
        redLayers = new Texture2D[layers.Length];
        for (int i = 0; i < layers.Length; i++)
        {
            Sprite sprite = layers[i].renderer ? layers[i].renderer.sprite : null;
            if (sprite) redLayers[i] = Resources.Load<Texture2D>("BossRedSky/" + sprite.name);
            if (redLayers[i]) continue;
            Debug.LogError($"Missing Boss sky texture for forest layer {i + 1}.", this);
            redLayers = null;
            return;
        }
    }

    private void OnDisable()
    {
        phase = Phase.Clear;
        progress = dim = daylight = 0f;
    }

    [ContextMenu("Preview Boss Entrance")]
    public void BeginBossEntrance()
    {
        if (Application.isPlaying && redLayers != null && phase != Phase.Red) phase = Phase.Entering;
    }

    [ContextMenu("Preview Boss Retreat")]
    public void BeginBossRetreat()
    {
        if (!Application.isPlaying || phase == Phase.Darkening || phase == Phase.Restoring) return;
        phase = progress > 0f ? Phase.Darkening : Phase.Clear;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        switch (phase)
        {
            case Phase.Entering:
                progress = Mathf.MoveTowards(progress, 1f, dt / entranceSeconds);
                dim = Mathf.MoveTowards(dim, 0f, dt / darkenSeconds);
                daylight = Mathf.MoveTowards(daylight, 0f, dt / entranceSeconds);
                if (progress == 1f && dim == 0f && daylight == 0f) phase = Phase.Red;
                break;
            case Phase.Darkening:
                dim = Mathf.MoveTowards(dim, peakDimming, dt * peakDimming / darkenSeconds);
                if (dim == peakDimming) phase = Phase.Restoring;
                break;
            case Phase.Restoring:
                // Keep the current wave footprint, including an interrupted entrance.
                // Fade that image through shadow to daylight instead of emitting a second wave.
                daylight = Mathf.MoveTowards(daylight, 1f, dt / restoreSeconds);
                dim = Mathf.MoveTowards(dim, 0f, dt * peakDimming / restoreSeconds);
                if (daylight == 1f && dim == 0f)
                {
                    progress = daylight = 0f;
                    phase = Phase.Clear;
                }
                break;
        }
    }

    internal void ApplyLayer(MaterialPropertyBlock properties, int index)
    {
        if (redLayers == null || (progress <= 0f && dim <= 0f)) return;
        float delay = index < LayerDelays.Length ? LayerDelays[index] : 0f;
        float eased = Mathf.Lerp(progress, Mathf.SmoothStep(0f, 1f, progress), .35f);
        properties.SetFloat(Reveal, Mathf.InverseLerp(delay, 1f, eased));
        properties.SetFloat(Dim, dim);
        properties.SetFloat(Daylight, Mathf.SmoothStep(0f, 1f, daylight));
        properties.SetTexture(RedTexture, redLayers[index]);
    }
}
