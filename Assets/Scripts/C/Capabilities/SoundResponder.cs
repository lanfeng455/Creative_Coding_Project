// SoundResponder.cs —— Prototype C：INPUT 能力 respond_to_sound（听到声音后轻抖）
// 收到 SoundEmitter 的声音脉冲（半径内）→ 视觉枢轴做一次快速衰减的上下轻抖；
// 无脉冲时完全静止。多次脉冲受 minReactInterval 限流，不会连成持续抽搐。
// 位置抖动写在「视觉枢轴」（CapabilityVisualPivot）的 localPosition 上：
// 与 AirflowResponder（写枢轴旋转）、墙面晃动（写根节点位置）各占一个通道，互不覆盖。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class SoundResponder : MonoBehaviour
    {
        /// <summary>场上所有声敏物件（外部系统观察用）。</summary>
        public static readonly List<SoundResponder> All = new List<SoundResponder>();

        [Header("抖动表现（Inspector 可实时调）")]
        [Tooltip("听到声音后的抖动幅度（米，上下轻抖）")]
        public float jiggleAmplitude = 0.04f;

        [Tooltip("抖动衰减时长（秒）")]
        public float jiggleSeconds = 0.5f;

        [Tooltip("抖动频率（次/秒）")]
        public float jiggleSpeed = 14f;

        [Tooltip("两次反应的最小间隔（秒）")]
        public float minReactInterval = 0.4f;

        /// <summary>当前反应强度（0~1，0 = 静止）。恐龙等外部系统可读取。</summary>
        public float ReactIntensity => _t;

        private Transform _pivot;           // 视觉枢轴（生成物）或根节点（纯视觉物件）
        private Vector3 _restLocalPos;
        private float _t;
        private float _nextAllowed;

        private void OnEnable()
        {
            All.Add(this);
            _pivot = CapabilityVisualPivot.Ensure(gameObject);
            if (_pivot != null) _restLocalPos = _pivot.localPosition;
        }

        private void OnDisable()
        {
            All.Remove(this);
            RestoreRest();
        }

        /// <summary>
        /// 声音脉冲广播：半径（X-Y 平面距离）内的所有声敏物件各自反应。
        /// 由 SoundEmitter 调用；也可由其他系统直接调用（自定义声源）。
        /// </summary>
        public static void BroadcastPulse(Vector3 source, float radius, float strength)
        {
            if (radius <= 0.01f) return;

            for (int i = 0; i < All.Count; i++)
            {
                SoundResponder responder = All[i];
                if (responder == null) continue;

                float dx = responder.transform.position.x - source.x;
                float dy = responder.transform.position.y - source.y;
                if (dx * dx + dy * dy > radius * radius) continue;

                responder.HearPulse(Mathf.Clamp01(strength));
            }
        }

        private void HearPulse(float strength)
        {
            if (Time.time < _nextAllowed) return;
            _nextAllowed = Time.time + Mathf.Max(0.05f, minReactInterval);
            _t = Mathf.Max(_t, strength);
        }

        private void Update()
        {
            if (_pivot == null) return;

            if (_t <= 0.0001f)
            {
                if (_pivot.localPosition != _restLocalPos) RestoreRest();
                return;
            }

            _t = Mathf.MoveTowards(_t, 0f, Time.deltaTime / Mathf.Max(0.01f, jiggleSeconds));

            float wave = Mathf.Sin(Time.time * Mathf.Max(1f, jiggleSpeed) * Mathf.PI * 2f) * Mathf.Abs(_t);
            _pivot.localPosition = _restLocalPos + Vector3.up * (jiggleAmplitude * wave);
        }

        private void RestoreRest()
        {
            if (_pivot == null) return;
            _pivot.localPosition = _restLocalPos;
        }
    }
}
