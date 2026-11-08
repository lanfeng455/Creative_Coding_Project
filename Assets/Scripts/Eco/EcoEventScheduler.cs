// EcoEventScheduler.cs —— 随机事件调度器
// 只负责"什么时候发生什么"：开局延迟 → 随机间隔 → 抽红/紫威胁 → 持续 → 冷却。
// 球不再由事件生成：庇护所完工时生成、被毁时炸碎（生命周期与巢共享，见 DinoAgent.SpawnBall）。

using UnityEngine;

public class EcoEventScheduler : MonoBehaviour
{
    public enum EventType { None, RedThreat, PurpleThreat }

    [Header("通用调度")]
    [Tooltip("游戏开始后多久才允许第一个事件")]
    public float firstDelay = 18f;
    [Tooltip("两次事件之间的随机间隔范围（秒）—— 上一个事件结束后再等这么久")]
    public Vector2 eventIntervalRange = new Vector2(16f, 32f);

    [Header("威胁事件")]
    [Tooltip("威胁事件最长持续多久（红字符分批生成完 + 场上字符清空即提前结束）")]
    public float threatDuration = 14f;
    [Range(0f, 1f), Tooltip("威胁事件里红色的占比（其余为紫色）")]
    public float redWeight = 0.5f;

    [Header("引用（留空自动找）")]
    public ThreatSystem threats;

    private EventType _current = EventType.None;
    private float _timer;
    private float _eventTime;
    private int _redStreak;

    private void Awake()
    {
        if (threats == null) threats = GetComponent<ThreatSystem>();
        if (threats == null) threats = FindObjectOfType<ThreatSystem>();
        _timer = firstDelay;
    }

    private void Update()
    {
        switch (_current)
        {
            case EventType.None:
                _timer -= Time.deltaTime;
                if (_timer <= 0f) RollNext();
                break;

            case EventType.RedThreat:
                _eventTime += Time.deltaTime;
                if (_eventTime >= threatDuration && !threats.HasActiveThreat) EndEvent();
                else if (_eventTime >= threatDuration + 8f) EndEvent(); // 兜底：总有字符赖着不走
                break;

            case EventType.PurpleThreat:
                _eventTime += Time.deltaTime;
                // 紫字符落地即事件"发作"完成，剩下的交给腐化区域自然演化
                if (_eventTime >= 3f && !threats.HasActiveThreat) EndEvent();
                break;
        }
    }

    private void RollNext()
    {
        if (threats == null) { EndEvent(); return; }
        // 红紫轮换保底：连续 2 次红色后强制紫色，避免运气不好永远看不到紫字符
        bool red = _redStreak >= 2 ? false : Random.value < redWeight;
        _redStreak = red ? _redStreak + 1 : 0;
        if (red) threats.StartRedEvent(); else threats.StartPurpleEvent();
        _current = red ? EventType.RedThreat : EventType.PurpleThreat;
        _eventTime = 0f;
    }

    private void EndEvent()
    {
        _current = EventType.None;
        _timer = Random.Range(eventIntervalRange.x, eventIntervalRange.y);
    }

    // —— 手动测试（Inspector 右键组件 → 对应菜单，不用等随机调度） ——
    [ContextMenu("Test Red Event")] private void TestRed() => threats?.StartRedEvent();
    [ContextMenu("Test Purple Event")] private void TestPurple() => threats?.StartPurpleEvent();
}
