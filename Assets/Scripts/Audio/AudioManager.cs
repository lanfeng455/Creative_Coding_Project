// AudioManager.cs —— 全局音乐 / 音效管理器
// 用法：AudioManager.PlayAt(Sfx.BallGoal, worldPos);  AudioManager.PlayMusic(clip);
// 无需在场景里手动摆放：第一次访问时自动创建并跨场景常驻。
// 在 Inspector 里把 AudioClip 拖到对应 Sfx 条目即可，代码只引用枚举，不做黑盒。

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>音效枚举。新增音效时在这里加一项，然后在 AudioManager 的 Inspector 里拖 clip。
/// 只能往末尾追加，不要改动已有项的顺序（Inspector 里存的是枚举序号）。</summary>
public enum Sfx
{
    None = 0,
    BallKick,       // 玩家垫球
    BallBounce,     // 球落地 / 撞墙
    BallGoal,       // 球垫进房子入口（进球）
    LeafHarvest,    // 恐龙摘叶
    NestBuild,      // 庇护所建造中（字符落位）
    NestComplete,   // 庇护所完工
    ThreatHit,      // 红色字符命中
    Corruption,     // 紫色腐化出现
    RainStart,      // 乱码雨开始

    // —— 玩家输入（B2）——
    LetterRecord,   // 字母弹上上层、录入打字机
    LetterDelete,   // 指尖删掉打字机里的字母
    LetterFling,    // 弹力带把字母 / 物品弹上去

    // —— 恐龙 ——
    DinoVoice,      // 恐龙叫（冒出需求 / 尾巴音效）
    DinoHurt,       // 恐龙受击、受惊
    DinoHappy,      // 需求达成 / 房子盖好

    // —— 天灾 / 环境 ——
    FireStart,      // 起火（点火瞬间）
    FireOut,        // 火被 WATER 浇灭（咝的一声）
    Ambience,       // 环境底噪（由调用方决定循环与否）

    // —— 建材 / 盖房（B2）——
    WoodDeliver,    // 木头打出后落到上层（建材送达）
    WoodPlace,      // 恐龙把一截木头落位（盖房每一步）
    HouseComplete,  // 一间房子封顶完工
}

/// <summary>背景音乐槽位。和 Sfx 一样在 Inspector 里拖 clip，代码只引用枚举。
/// 以后要做"战斗/紧张/胜利"分层，就往这里加一项即可。</summary>
public enum Music
{
    None = 0,
    Main,       // 日常
    Tension,    // 火灾 / 威胁
    Victory,    // 收尾 / 建成
}

[Serializable]
public struct MusicEntry
{
    public Music music;
    public AudioClip clip;
    [Range(0f, 1f)] public float volume;   // 相对音量（0 视为 1）
}

[Serializable]
public struct SfxEntry
{
    public Sfx sfx;
    public AudioClip clip;
    [Range(0f, 1f)] public float volume;
}

[DisallowMultipleComponent]
public class AudioManager : MonoBehaviour
{
    private static AudioManager _instance;

    /// <summary>全局实例（不存在时自动创建）</summary>
    public static AudioManager Instance
    {
        get
        {
            if (_instance != null) return _instance;
            var go = new GameObject("AudioManager (auto)");
            _instance = go.AddComponent<AudioManager>();
            DontDestroyOnLoad(go);
            return _instance;
        }
    }

    [Header("音效映射（按枚举拖入 clip）")]
    public SfxEntry[] sfxEntries = Array.Empty<SfxEntry>();
    [Range(0f, 1f)] public float sfxVolume = 1f;
    [Tooltip("同类音效最小间隔（秒），避免每帧刷声")]
    public float minRepeatInterval = 0.06f;

    [Header("音乐（按枚举拖入 clip）")]
    public MusicEntry[] musicEntries = Array.Empty<MusicEntry>();
    [Range(0f, 1f)] public float musicVolume = 0.7f;
    [Tooltip("默认交叉淡入淡出时长（秒）")]
    public float musicCrossFade = 0.8f;

    [Header("环境音（循环播放，从 sfxEntries 里取 clip）")]
    [Range(0f, 1f)] public float ambienceVolume = 0.5f;
    [Tooltip("环境音淡入淡出默认时长（秒）")]
    public float ambienceFade = 1.5f;

    [Header("开场自动播放")]
    [Tooltip("进入场景后自动播 Music.Main（没拖 clip 时自动跳过）")]
    public bool autoStartMusic = true;
    [Tooltip("进入场景后自动循环播 Sfx.Ambience（没拖 clip 时自动跳过）")]
    public bool autoStartAmbience = true;

    private AudioSource _ambience;
    private Coroutine _ambienceFade_;
    private Sfx _currentAmbience = Sfx.None;

    private AudioSource[] _musicSources;
    private int _activeMusic;
    private Music _currentMusic = Music.None;
    private Coroutine _musicFade;
    private readonly Dictionary<Music, AudioClip> _musicClips = new Dictionary<Music, AudioClip>();

    /// <summary>当前正在播的音乐槽位（None = 没在放）</summary>
    public static Music CurrentMusic => Instance._currentMusic;

    private readonly List<AudioSource> _sfxSources = new List<AudioSource>();
    private readonly Dictionary<Sfx, AudioClip> _clips = new Dictionary<Sfx, AudioClip>();
    private readonly Dictionary<Sfx, float> _lastPlay = new Dictionary<Sfx, float>();
    private int _sfxCursor;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;

        // 两个音乐源轮换，用来做交叉淡入淡出
        _musicSources = new AudioSource[2];
        for (int i = 0; i < 2; i++)
        {
            var m = gameObject.AddComponent<AudioSource>();
            m.playOnAwake = false;
            m.loop = true;
            m.volume = 0f;
            _musicSources[i] = m;
        }

        for (int i = 0; i < 3; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f; // 2D（原型阶段不做 3D 衰减）
            _sfxSources.Add(src);
        }

        // 环境音单独一条源：循环播放，不跟一次性音效抢
        _ambience = gameObject.AddComponent<AudioSource>();
        _ambience.playOnAwake = false;
        _ambience.loop = true;
        _ambience.spatialBlend = 0f;
        _ambience.volume = 0f;

        RebuildClipMap();
    }

    private void Start()
    {
        // 开局没人调用的话 BGM / 环境音就是静音 —— 这里统一兜住
        if (autoStartMusic && _musicClips.ContainsKey(Music.Main))
            PlayMusic(Music.Main, 1.5f);
        if (autoStartAmbience && _clips.ContainsKey(Sfx.Ambience))
            PlayAmbience(Sfx.Ambience, ambienceFade);
    }

    /// <summary>Inspector 改完条目后调用（OnValidate 也会触发）</summary>
    public void RebuildClipMap()
    {
        _clips.Clear();
        foreach (var e in sfxEntries)
        {
            if (e.sfx == Sfx.None || e.clip == null) continue;
            _clips[e.sfx] = e.clip;
        }
        _musicClips.Clear();
        foreach (var e in musicEntries)
        {
            if (e.music == Music.None || e.clip == null) continue;
            _musicClips[e.music] = e.clip;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        RebuildClipMap();
    }
#endif

    // —— 静态便捷入口 ——

    /// <summary>播放音效（世界坐标只用于将来的 3D 衰减；当前统一 2D 播放）</summary>
    public static void PlayAt(Sfx sfx, Vector3 worldPos)
    {
        var mgr = Instance;
        if (sfx == Sfx.None) return;
        var clip = mgr._clips.TryGetValue(sfx, out var c) ? c : null;
        if (clip == null) return;

        if (mgr._lastPlay.TryGetValue(sfx, out float last)
            && Time.unscaledTime - last < mgr.minRepeatInterval) return;
        mgr._lastPlay[sfx] = Time.unscaledTime;

        var src = mgr._sfxSources[mgr._sfxCursor];
        mgr._sfxCursor = (mgr._sfxCursor + 1) % mgr._sfxSources.Count;
        src.PlayOneShot(clip, mgr.sfxVolume * mgr.EntryVolume(sfx));
    }

    /// <summary>不带坐标的播放（UI 等）</summary>
    public static void Play(Sfx sfx) => PlayAt(sfx, Vector3.zero);

    /// <summary>切换背景音乐（按枚举，交叉淡入淡出）。
    /// 例：AudioManager.PlayMusic(Music.Tension); 火灭后 AudioManager.PlayMusic(Music.Main, 1.5f);</summary>
    public static void PlayMusic(Music music, float fade = -1f)
    {
        var mgr = Instance;
        if (fade < 0f) fade = mgr.musicCrossFade;
        if (music == mgr._currentMusic && mgr.ActiveMusic.isPlaying) return;
        mgr._currentMusic = music;
        mgr._musicClips.TryGetValue(music, out var clip);
        if (music == Music.None || clip == null)
        {
            if (music != Music.None)
                Debug.LogWarning($"[Audio] 音乐 {music} 没有在 AudioManager 的 musicEntries 里拖 clip");
            mgr.CrossFadeMusic(null, 0f, fade);
            return;
        }
        mgr.CrossFadeMusic(clip, mgr.musicVolume * mgr.MusicEntryVolume(music), fade);
    }

    /// <summary>直接播一个临时 clip（不走枚举，适合调试 / 一次性过场）</summary>
    public static void PlayMusic(AudioClip clip, float fade = -1f)
    {
        var mgr = Instance;
        if (fade < 0f) fade = mgr.musicCrossFade;
        mgr._currentMusic = Music.None;
        mgr.CrossFadeMusic(clip, clip != null ? mgr.musicVolume : 0f, fade);
    }

    /// <summary>淡出停止（默认 0.6 秒）</summary>
    public static void StopMusic(float fade = 0.6f)
    {
        var mgr = Instance;
        mgr._currentMusic = Music.None;
        mgr.CrossFadeMusic(null, 0f, fade);
    }

    public static void SetMusicVolume(float v)
    {
        var mgr = Instance;
        mgr.musicVolume = Mathf.Clamp01(v);
        // 立即生效（不打断正在进行的淡入淡出逻辑之后的状态）
        mgr.ActiveMusic.volume = mgr._currentMusic != Music.None
            ? mgr.musicVolume * mgr.MusicEntryVolume(mgr._currentMusic)
            : mgr.ActiveMusic.volume;
    }

    public static void SetSfxVolume(float v) => Instance.sfxVolume = Mathf.Clamp01(v);

    // —— 环境音（循环）——

    /// <summary>循环播放环境音（clip 从 sfxEntries 里取，通常传 Sfx.Ambience）。
    /// 已在播同一个就直接忽略，不会重启。</summary>
    public static void PlayAmbience(Sfx sfx, float fade = -1f)
    {
        var mgr = Instance;
        if (fade < 0f) fade = mgr.ambienceFade;
        if (mgr._currentAmbience == sfx && mgr._ambience.isPlaying) return;

        if (sfx == Sfx.None || !mgr._clips.TryGetValue(sfx, out var clip))
        {
            if (sfx != Sfx.None)
                Debug.LogWarning($"[Audio] 环境音 {sfx} 没有在 AudioManager 的 sfxEntries 里拖 clip");
            StopAmbience(fade);
            return;
        }

        mgr._currentAmbience = sfx;
        mgr._ambience.clip = clip;
        mgr._ambience.loop = true;
        if (!mgr._ambience.isPlaying) mgr._ambience.volume = 0f;
        mgr._ambience.Play();
        mgr.FadeAmbience(mgr.ambienceVolume * mgr.SfxEntryVolume(sfx), fade);
    }

    /// <summary>淡出并停止环境音</summary>
    public static void StopAmbience(float fade = -1f)
    {
        var mgr = Instance;
        if (fade < 0f) fade = mgr.ambienceFade;
        mgr._currentAmbience = Sfx.None;
        mgr.FadeAmbience(0f, fade);
    }

    public static void SetAmbienceVolume(float v)
    {
        var mgr = Instance;
        mgr.ambienceVolume = Mathf.Clamp01(v);
        if (mgr._ambience != null) mgr._ambience.volume = mgr.ambienceVolume;
    }

    private void FadeAmbience(float target, float fade)
    {
        if (_ambienceFade_ != null) StopCoroutine(_ambienceFade_);
        _ambienceFade_ = StartCoroutine(FadeAmbienceRoutine(target, fade));
    }

    private System.Collections.IEnumerator FadeAmbienceRoutine(float target, float fade)
    {
        fade = Mathf.Max(0.02f, fade);
        float start = _ambience.volume;
        float t = 0f;
        while (t < fade)
        {
            t += Time.unscaledDeltaTime;
            _ambience.volume = Mathf.Lerp(start, target, Mathf.Clamp01(t / fade));
            yield return null;
        }
        _ambience.volume = target;
        if (target <= 0.001f) _ambience.Stop();
        _ambienceFade_ = null;
    }

    private float SfxEntryVolume(Sfx sfx)
    {
        foreach (var e in sfxEntries)
            if (e.sfx == sfx) return Mathf.Clamp01(e.volume <= 0f ? 1f : e.volume);
        return 1f;
    }

    private AudioSource ActiveMusic => _musicSources[_activeMusic];

    private float MusicEntryVolume(Music m)
    {
        foreach (var e in musicEntries)
            if (e.music == m) return Mathf.Clamp01(e.volume <= 0f ? 1f : e.volume);
        return 1f;
    }

    /// <summary>把当前音乐源淡出、另一个源淡入到 targetVolume（clip=null 表示只淡出）</summary>
    private void CrossFadeMusic(AudioClip clip, float targetVolume, float fade)
    {
        if (_musicFade != null) StopCoroutine(_musicFade);

        int from = _activeMusic;
        int to = 1 - from;
        var srcFrom = _musicSources[from];
        var srcTo = _musicSources[to];

        if (clip != null)
        {
            srcTo.clip = clip;
            srcTo.loop = true;
            srcTo.volume = 0f;
            srcTo.Play();
        }
        _activeMusic = to;
        _musicFade = StartCoroutine(FadeMusicRoutine(srcFrom, srcTo, clip != null ? targetVolume : 0f, fade));
    }

    private System.Collections.IEnumerator FadeMusicRoutine(AudioSource from, AudioSource to, float target, float fade)
    {
        fade = Mathf.Max(0.02f, fade);
        float startFrom = from.volume;
        float t = 0f;
        while (t < fade)
        {
            t += Time.unscaledDeltaTime;   // 用 unscaled，暂停/慢动作时音乐照常淡出
            float k = Mathf.Clamp01(t / fade);
            from.volume = Mathf.Lerp(startFrom, 0f, k);
            to.volume = Mathf.Lerp(0f, target, k);
            yield return null;
        }
        from.Stop();
        from.volume = 0f;
        to.volume = target;
        _musicFade = null;
    }

    private float EntryVolume(Sfx sfx)
    {
        foreach (var e in sfxEntries)
            if (e.sfx == sfx) return Mathf.Clamp01(e.volume <= 0f ? 1f : e.volume);
        return 1f;
    }
}
