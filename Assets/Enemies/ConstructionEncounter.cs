using UnityEngine;

// Each encounter claims the builder once its material has actually been installed.
public abstract class ConstructionEncounter : MonoBehaviour
{
    [SerializeField] protected DinoAgentB1 dino;
    [SerializeField, Min(1f)] protected float attackIntervalSeconds = 8f;
    bool started;
    float previousTimeScale;
    bool pausedByDeath;
    float defeatedAt;
    GUIStyle defeatTitle, restartLabel;

    public abstract string Request { get; }
    internal abstract void LaunchCounter();
    public bool IsGameOver => dino && dino.EncounterLives == 0;

    protected abstract int AfterStep { get; }
    protected abstract void BeginEncounter();

    protected void OnEnable()
    {
        if (dino) dino.ConstructionStepInstalled += OnInstallation;
    }

    protected void OnDisable()
    {
        if (dino) dino.ConstructionStepInstalled -= OnInstallation;
    }

    void OnInstallation(int step)
    {
        if (started || step != AfterStep || dino.ActiveEncounter) return;
        started = true;
        dino.ActiveEncounter = this;
        // 小怪战共用 Tension，BOSS（Riftbark）单独用 Boss
        AudioManager.PlayMusic(this is RiftbarkEncounter ? Music.Boss : Music.Tension);
        BeginEncounter();
    }

    protected void ReleaseBuilder()
    {
        if (dino && dino.ActiveEncounter == this) dino.ActiveEncounter = null;
    }

    protected void HitDino(Color color)
    {
        if (dino.ActiveEncounter != this || IsGameOver) return;
        dino.TakeEncounterHit(color);
        if (!IsGameOver) return;
        dino.bubble?.gameObject.SetActive(false);
        dino.transform.Rotate(0, 0, -75f);
        previousTimeScale = Time.timeScale;
        pausedByDeath = true;
        defeatedAt = Time.unscaledTime;
        Time.timeScale = 0;
    }

    protected void OnGUI()
    {
        if (!pausedByDeath) return;
        float elapsed = Time.unscaledTime - defeatedAt;
        float wash = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / .32f));
        int previousDepth = GUI.depth;
        GUI.depth = -1000;
        var screen = new Rect(0, 0, Screen.width, Screen.height);
        Fill(screen, new Color(.48f, .018f, .065f, .93f * wash));
        bool restart = elapsed >= .32f && DrawDefeatMessage(screen);
        GUI.depth = previousDepth;
        if (!restart) return;
        Time.timeScale = previousTimeScale;
        pausedByDeath = false;
        var scene = gameObject.scene;
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path,
            new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
#else
        UnityEngine.SceneManagement.SceneManager.LoadScene(scene.buildIndex);
#endif
    }

    bool DrawDefeatMessage(Rect screen)
    {
        if (defeatTitle == null)
        {
            var font = dino.bubble ? dino.bubble.font : B1TextStyle.Get()?.font;
            defeatTitle = new GUIStyle(GUI.skin.label)
            {
                font = font ? font.sourceFontFile : null,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false
            };
            defeatTitle.normal.textColor = new Color(1f, .96f, .86f);
            restartLabel = new GUIStyle(defeatTitle);
        }

        float unit = Mathf.Min(screen.width / 960f, screen.height / 540f);
        defeatTitle.fontSize = Mathf.Max(1, Mathf.RoundToInt(53f * unit));
        restartLabel.fontSize = Mathf.Max(1, Mathf.RoundToInt(20f * unit));
        var center = screen.center;
        GUI.Label(new Rect(0, center.y - 127f * unit, screen.width, 160f * unit),
            "One more round?\nGot my back this time?", defeatTitle);

        var button = new Rect(center.x - 90f * unit, center.y + 42f * unit, 180f * unit, 46f * unit);
        Fill(button, new Color(.99f, .83f, .74f));
        float inset = Mathf.Max(1f, 1.5f * unit);
        var inner = new Rect(button.x + inset, button.y + inset, button.width - inset * 2, button.height - inset * 2);
        Fill(inner, button.Contains(Event.current.mousePosition)
            ? new Color(.52f, .095f, .12f) : new Color(.29f, .025f, .065f));
        return GUI.Button(button, "You bet!", restartLabel);
    }

    static void Fill(Rect area, Color color)
    {
        GUI.DrawTexture(area, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, 0f);
    }

    protected virtual void OnDestroy()
    {
        ReleaseBuilder();
        if (pausedByDeath) Time.timeScale = previousTimeScale;
    }
}
