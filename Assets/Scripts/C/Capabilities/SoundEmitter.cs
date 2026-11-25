// SoundEmitter.cs —— Prototype C：OUTPUT 能力 emit_sound（事件发声 + 声音脉冲）
// 触发完全解耦：任何碰撞（OnCollisionEnter，含速度阈值 + 冷却）都算一次「敲击」，
// 不认物件类型、不认恐龙。也可由其他系统调用 PlayPulse() 主动触发。
// 一次敲击做两件事：
//   1) AudioManager.PlayAt 播一条池化音效（枚举 Sfx.ObjectChime，在 AudioManager Inspector 绑 clip；
//      不绑 clip 只广播不发声，PlayAt 对空 clip 静默跳过）；
//   2) 向半径内的 SoundResponder 广播声音脉冲（声音传播 = X-Y 平面距离判定）。
// 防刷屏：minInterval 最小发声间隔（AudioManager 自己还有 minRepeatInterval 二道保险）。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class SoundEmitter : MonoBehaviour
    {
        /// <summary>场上所有可发声物件（调试 / 外部系统观察用）。</summary>
        public static readonly List<SoundEmitter> All = new List<SoundEmitter>();

        [Header("碰撞触发（任何碰撞者都算，不认类型）")]
        [Tooltip("两次发声的最小间隔（秒），防止持续碰撞刷屏")]
        public float minInterval = 0.8f;

        [Tooltip("碰撞相对速度低于它不算（避免静置接触 / 轻微擦碰误触发）")]
        public float minImpactSpeed = 0.3f;

        [Header("播放")]
        [Tooltip("发声用的音效条目（clip 在 AudioManager 的 Inspector 里绑定；不绑则只广播不发声）")]
        public Sfx chimeSfx = Sfx.ObjectChime;

        [Header("声音传播")]
        [Tooltip("声音脉冲半径（米；X-Y 平面距离）")]
        public float radius = 3f;

        [Tooltip("脉冲强度（0~1，决定声敏物件的反馈幅度）")]
        [Range(0f, 1f)] public float strength = 0.8f;

        private float _nextAllowed;

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void OnCollisionEnter(Collision collision)
        {
            if (Time.time < _nextAllowed) return;
            if (collision == null || collision.relativeVelocity.magnitude < minImpactSpeed) return;

            _nextAllowed = Time.time + Mathf.Max(0.05f, minInterval);
            PlayPulse();
        }

        /// <summary>
        /// 发声 + 广播一次声音脉冲。公开方法：碰撞之外的事件（演出、按钮等）也可以直接调用。
        /// </summary>
        public void PlayPulse()
        {
            _nextAllowed = Time.time + Mathf.Max(0.05f, minInterval);

            AudioManager.PlayAt(chimeSfx, transform.position);
            SoundResponder.BroadcastPulse(transform.position, radius, strength);

            Debug.Log($"[PrototypeC] 声音脉冲：{name}｜半径 {radius:0.0} m｜强度 {strength:0.00}（Sfx={chimeSfx}）");
        }
    }
}
