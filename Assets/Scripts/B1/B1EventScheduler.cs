// B1EventScheduler.cs —— 事件调度：前景 A-Z 字符雨 + 红色威胁 + 火灾
// 从 ThreatB1.cs 拆出，确保 Unity 能正确附加组件（多类文件有时会出现脚本关联问题）

using UnityEngine;

/// <summary>事件调度：字符雨 / 红色威胁 / 火灾</summary>
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

    [Header("红色威胁")]
    public float threatFirstDelay = 45f;
    public Vector2 threatIntervalRange = new Vector2(40f, 60f);
    public int maxLiveThreats = 2;
    private float _threatTimer;

    [Header("火灾")]
    [Tooltip("第一次火灾的延迟（秒）。要给玩家足够时间学会拼词和盖房")]
    public float fireFirstDelay = 120f;
    public Vector2 fireIntervalRange = new Vector2(100f, 160f);
    private float _fireTimer;

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
        _threatTimer = threatFirstDelay;
        _fireTimer = fireFirstDelay;
        Debug.Log($"[B2Sched] Start. world={(world != null ? "已设置" : "null")}, " +
                  $"dino={(dino != null ? "已设置" : "null")}, letterInterval={letterInterval}s");
    }

    private bool _firstLetterLogged;
    /// <summary>word 里还没被打字机拼出来的字母（按重数扣减）。
    /// 已拼的字母无论落在缓冲区哪个位置都算数，和 TypeWriterB1 的匹配规则一致。</summary>
    private string MissingLetters(string word)
    {
        string typed = typeWriter != null ? typeWriter.BufferText : "";
        var rest = new System.Text.StringBuilder(word);
        foreach (char t in typed)
        {
            int i = rest.ToString().IndexOf(t);
            if (i >= 0) rest.Remove(i, 1);
        }
        return rest.ToString();
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        // —— 字符雨（下层顶部随机 x，避开缺口免得白白掉下去）——
        _letterAcc += dt;
        if (_letterAcc >= letterInterval && PhysicsLetter.All.Count < maxLiveLetters)
        {
            _letterAcc = 0f;

            // 动态难度：当前焦点词 = 火灾时 WATER，其次恐龙需求（WOOD/STONE/BALL）。
            // 再把打字机里已经拼出来的字母扣掉 —— 只强化"还缺的那几个字"，
            // 例如 WOOD 已拼 WO → 只高频掉 O、D，W 不再刷屏。
            string focus = FireHazard.ActiveFire != null ? "WATER"
                         : dino != null ? dino.CurrentNeed : null;
            if (!string.IsNullOrEmpty(focus)) focus = MissingLetters(focus);

            char c;
            if (!string.IsNullOrEmpty(focus) && Random.value < needLetterChance)
            {
                c = focus[Random.Range(0, focus.Length)];      // 还缺的字母：高频
            }
            else if (!string.IsNullOrEmpty(focus) && Random.value < unrelatedSkipChance)
            {
                return;                                         // 无关字母：偶发（本次跳过）
            }
            else
            {
                c = Alpha[Random.Range(0, Alpha.Length)];
            }

            var go = new GameObject("Letter");
            var l = go.AddComponent<PhysicsLetter>();
            l.color = letterColor;               // 统一配色，拖尾跟随
            float x = Random.Range(world.boundsXMin + 0.5f, world.GapXMin - 0.5f);
            l.Init(world, c, new Vector3(x, world.lowerCeilingY + 0.5f, 0f), letterFallSpeed);
            if (!_firstLetterLogged)
            {
                _firstLetterLogged = true;
                Debug.Log($"[B2Sched] 首个字母已生成，后续不再重复日志（PhysicsLetter.Init 会打印每个）");
            }
        }

        // —— 红色威胁（上层天花板）——
        _threatTimer -= dt;
        if (_threatTimer <= 0f && RedThreatGlyph.All.Count < maxLiveThreats)
        {
            _threatTimer = Random.Range(threatIntervalRange.x, threatIntervalRange.y);
            var go = new GameObject("RedThreat");
            go.AddComponent<RedThreatGlyph>();
            go.GetComponent<RedThreatGlyph>().Init(world, dino,
                new Vector3(Random.Range(world.upperFloorXMin + 1f, world.GapXMin - 1f),
                            world.upperCeilingY - 0.3f, 0f));
        }

        // —— 火灾（上层地板，同时最多一场）——
        _fireTimer -= dt;
        if (_fireTimer <= 0f)
        {
            if (FireHazard.ActiveFire == null)
            {
                _fireTimer = Random.Range(fireIntervalRange.x, fireIntervalRange.y);
                var go = new GameObject("Fire");
                go.AddComponent<FireHazard>().Init(world);
            }
            else _fireTimer = 8f; // 已有火，稍后再看
        }
    }
}
