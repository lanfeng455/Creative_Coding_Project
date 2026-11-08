// AudioManager.cs —— 全局音乐 / 音效管理器
// 用法：AudioManager.PlayAt(Sfx.BallGoal, worldPos);  AudioManager.PlayMusic(clip);
// 无需在场景里手动摆放：第一次访问时自动创建并跨场景常驻。
// 在 Inspector 里把 AudioClip 拖到对应 Sfx 条目即可，代码只引用枚举，不做黑盒。

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>音效枚举。新增音效时在这里加一项，然后在 AudioManager 的 Inspector 里拖 clip。</summary>
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

    [Header("音乐")]
    [Range(0f, 1f)] public float musicVolume = 0.7f;

    private AudioSource _music;
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

        _music = gameObject.AddComponent<AudioSource>();
        _music.playOnAwake = false;
        _music.loop = true;
        _music.volume = musicVolume;

        for (int i = 0; i < 3; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f; // 2D（原型阶段不做 3D 衰减）
            _sfxSources.Add(src);
        }

        RebuildClipMap();
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
        if (_music != null) _music.volume = musicVolume;
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

    /// <summary>播放 / 切换背景音乐（loop=true 循环）</summary>
    public static void PlayMusic(AudioClip clip, bool loop = true)
    {
        var mgr = Instance;
        mgr._music.loop = loop;
        mgr._music.clip = clip;
        mgr._music.volume = mgr.musicVolume;
        if (clip != null) mgr._music.Play();
        else mgr._music.Stop();
    }

    public static void StopMusic() => Instance._music.Stop();

    public static void SetMusicVolume(float v)
    {
        var mgr = Instance;
        mgr.musicVolume = Mathf.Clamp01(v);
        mgr._music.volume = mgr.musicVolume;
    }

    public static void SetSfxVolume(float v) => Instance.sfxVolume = Mathf.Clamp01(v);

    private float EntryVolume(Sfx sfx)
    {
        foreach (var e in sfxEntries)
            if (e.sfx == sfx) return Mathf.Clamp01(e.volume <= 0f ? 1f : e.volume);
        return 1f;
    }
}
