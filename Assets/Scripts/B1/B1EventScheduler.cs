// B1EventScheduler.cs —— 事件调度：前景 A-Z 字符雨
// 从 ThreatB1.cs 拆出，确保 Unity 能正确附加组件（多类文件有时会出现脚本关联问题）

using UnityEngine;

/// <summary>事件调度：字符雨</summary>
public class B1EventScheduler : MonoBehaviour
{
    [Header("引用")]
    public B1LayerConfig world;
    public DinoAgentB1 dino;
    [Tooltip("留空自动 Find。用来读出已拼的字母，算出还缺哪些")]
    public TypeWriterB1 typeWriter;

    [Header("字符雨")]
    [Tooltip("生成间隔（秒）。越小越密；字落地就碎，寿命约 1.3 秒，建议 0.35~0.55")]
    public float letterInterval = 0.45f;
    [Tooltip("同屏最多几个字。想更热闹就调大")]
    public int maxLiveLetters = 20;
    public float letterFallSpeed = 1.5f;
    [Tooltip("前景字母雨颜色（含拖尾基色）")]
    public Color letterColor = new Color(0.85f, 0.95f, 1f, 1f);
    [Tooltip("恐龙有需求时，需求相关字母的出现概率（其余字母偶发）")]
    [Range(0f, 1f)] public float needLetterChance = 0.75f;
    [Tooltip("需求期间，无关字母被跳过的概率（无关字母减少到偶发）")]
    [Range(0f, 1f)] public float unrelatedSkipChance = 0.55f;
    private float _letterAcc;
    private string _letterFocus;
    private int _focusLetterIndex, _focusAttempts;

    [Header("语音聚焦（Whisper，玩家说词 → 对应字母高频掉落）")]
    [Tooltip("说完一个词后，词内字母保持高频的时长（秒）")]
    public float voiceFocusSeconds = 5f;
    [Tooltip("语音聚焦期间，掉出聚焦字母的概率")]
    [Range(0f, 1f)] public float voiceLetterChance = 0.9f;
    [Tooltip("语音聚焦期间，无关字母被跳过的概率")]
    [Range(0f, 1f)] public float voiceUnrelatedSkipChance = 0.6f;
    private string _voiceFocus;
    private float _voiceUntil;
    private FirstStoneGuide opening;

    /// <summary>外部（语音识别）调用：让 word 里的字母在接下来几秒内高频掉落。优先于恐龙需求/Homecoming。</summary>
    public void VoiceFocusWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;

        string upper = word.Trim().ToUpperInvariant();
        var letters = new System.Text.StringBuilder();
        foreach (char ch in upper)
        {
            if (ch < 'A' || ch > 'Z') continue;
            if (letters.ToString().IndexOf(ch) < 0) letters.Append(ch);
        }
        if (letters.Length == 0) return;

        _voiceFocus = letters.ToString();
        _voiceUntil = Time.time + Mathf.Max(0.1f, voiceFocusSeconds);
        Debug.Log($"[B2Sched] 语音聚焦：{upper} → {_voiceFocus}（{voiceFocusSeconds:0.#}s）");
    }

    /// <summary>当前仍在生效的语音聚焦字母；过期返回 null。</summary>
    public string ActiveVoiceFocus => Time.time < _voiceUntil ? _voiceFocus : null;

    private const string Alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private void Awake()
    {
        Debug.Log($"[B2Sched] Awake. this.enabled={enabled}, gameObject.activeSelf={gameObject.activeSelf}");
    }

    private void Start()
    {
        if (world == null) world = FindObjectOfType<B1LayerConfig>();
        if (dino == null) dino = FindObjectOfType<DinoAgentB1>();
        if (typeWriter == null) typeWriter = FindObjectOfType<TypeWriterB1>();
        if (gameObject.scene.name == "TemplateB2" && dino && dino.ConstructionLogSprite && world && typeWriter)
        {
            var band = FindFirstObjectByType<SlingshotBand>();
            if (band)
            {
                var guide = new GameObject("First STONE tutorial");
                guide.transform.SetParent(transform, false);
                opening = guide.AddComponent<FirstStoneGuide>();
                opening.Bind(this, band);
            }
        }
        Debug.Log($"[B2Sched] Start. world={(world != null ? "已设置" : "null")}, " +
                  $"dino={(dino != null ? "已设置" : "null")}, letterInterval={letterInterval}s");
    }

    private bool _firstLetterLogged;
    /// <summary>word 里还没被打字机拼出来的字母（按重数扣减）。
    /// 已拼的字母无论落在缓冲区哪个位置都算数，和 TypeWriterB1 的匹配规则一致。</summary>
    private string MissingLetters(string word)
    {
        string typed = typeWriter != null ? typeWriter.BufferText : "";
        return TypeWriterB1.MissingLetters(word, typed);
    }

    private void Update()
    {
        if (world == null || (dino && dino.Homecoming && !dino.Homecoming.Choosing)) return;
        if (opening && opening.GuideDrop()) return;
        float dt = Time.deltaTime;

        // —— 字符雨（下层顶部随机 x，避开缺口免得白白掉下去）——
        _letterAcc += dt;
        if (_letterAcc >= letterInterval && PhysicsLetter.All.Count < maxLiveLetters)
        {
            _letterAcc = 0f;

            // 再把打字机里已经拼出来的字母扣掉 —— 只强化"还缺的那几个字"，
            // 例如 WOOD 已拼 WO → 只高频掉 O、D，W 不再刷屏。
            // 语音聚焦优先于恐龙需求/Homecoming：玩家刚说 TOY，就主要掉 T/O/Y。
            string voice = ActiveVoiceFocus;
            bool voiceActive = !string.IsNullOrEmpty(voice);

            string focus = voiceActive ? voice
                         : dino != null ? dino.RequestedWord : null;
            if (!voiceActive)
            {
                if (!string.IsNullOrEmpty(focus)) focus = MissingLetters(focus);
                if (dino && dino.Homecoming)
                    focus = MissingLetters(PorchHomecoming.Lamp) + MissingLetters(PorchHomecoming.Bell);
            }
            if (focus != _letterFocus)
            {
                _letterFocus = focus;
                _focusLetterIndex = _focusAttempts = 0;
            }

            float letterChance = voiceActive ? voiceLetterChance : needLetterChance;
            float skipChance = voiceActive ? voiceUnrelatedSkipChance : unrelatedSkipChance;

            char c = '\0';
            if (!string.IsNullOrEmpty(focus) && (opening || _focusAttempts++ % 3 == 0 || Random.value < letterChance))
            {
                // Cycle every character, including repetitions (GLASS), rather than starving a needed letter.
                c = focus[_focusLetterIndex++ % focus.Length];
            }
            else if (!string.IsNullOrEmpty(focus) && Random.value < skipChance)
            {
                // 跳过本次无关字母。
            }
            else
            {
                c = Alpha[Random.Range(0, Alpha.Length)];
            }

            if (c != '\0')
            {
                float x = Random.Range(world.boundsXMin + 0.5f, world.GapXMin - 0.5f);
                DropLetter(c, new Vector2(x, world.lowerCeilingY + 0.5f), letterFallSpeed);
                if (!_firstLetterLogged)
                {
                    _firstLetterLogged = true;
                    Debug.Log($"[B2Sched] 首个字母已生成，后续不再重复日志（PhysicsLetter.Init 会打印每个）");
                }
            }
        }
    }

    internal PhysicsLetter DropLetter(char letter, Vector2 position, float speed, bool guided = false)
    {
        var instance = new GameObject("Letter").AddComponent<PhysicsLetter>();
        instance.color = letterColor;
        instance.Init(world, letter, position, speed, guided);
        return instance;
    }
}
