// AirflowResponder.cs —— Prototype C：INPUT 能力 respond_to_airflow（受气流摆动）
// 独立组件：手动摆放的物件直接挂本组件即可；生成物件由 CapabilityAssembler 按能力 ID 挂上。
// 行为：每帧查询 AirflowEmitter 注册表——在气流范围内 → 沿气流方向轻微、持续地摆动（小幅旋转）；
//       离开范围 → 平滑恢复到静止姿态。多个气流源取最强影响；多个物件各自独立响应。
// 旋转写在「视觉枢轴」上（CapabilityVisualPivot）：根节点有物理组件时不转根节点，
// 不与碰撞体 / FreezeRotation / 墙面晃动（WallTouchWobble 写位置）发生写冲突。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class AirflowResponder : MonoBehaviour
    {
        /// <summary>场上所有受气流影响的物件（外部系统观察用）。</summary>
        public static readonly List<AirflowResponder> All = new List<AirflowResponder>();

        [Header("摆动表现（Inspector 可实时调）")]
        [Tooltip("最大倾斜角（度）：摆动的整体幅度，含顺势倾斜 + 来回摇摆两部分")]
        public float maxLeanDegrees = 6f;

        [Tooltip("来回摆动的速度（次/秒）")]
        public float swaySpeed = 2.2f;

        [Tooltip("进入气流后达到满幅摆动的过渡时间（秒）")]
        public float easeInSeconds = 0.8f;

        [Tooltip("离开气流后恢复静止的时间（秒）")]
        public float recoverSeconds = 1.4f;

        /// <summary>当前摆动强度（0~1，0 = 静止）。恐龙等外部系统可读取。</summary>
        public float SwayIntensity => _intensity;

        /// <summary>当前是否正在摆动（强度超过阈值）。</summary>
        public bool IsSwaying => _intensity > 0.02f;

        private Transform _sway;            // 摆动目标：视觉枢轴（生成物）或根节点（纯视觉物件）
        private Quaternion _restWorld;      // 摆动目标的初始世界旋转
        private float _intensity;           // 0~1，进入气流爬升、离开气流回落
        private float _leanDirection;       // 1 = 顺 +X 气流，-1 = 顺 -X 气流
        private float _phase;               // 摆动相位（持续累加，保证波形连续）

        private void OnEnable()
        {
            All.Add(this);
            _sway = CapabilityVisualPivot.Ensure(gameObject);
            if (_sway != null) _restWorld = _sway.rotation;
        }

        private void OnDisable()
        {
            All.Remove(this);
            RestoreRest();
        }

        private void Update()
        {
            if (_sway == null) return;

            bool inFlow = AirflowEmitter.TryGetInfluence(_sway.position, out float influenceX);

            if (inFlow)
            {
                _leanDirection = influenceX >= 0f ? 1f : -1f;
                float target = Mathf.Clamp01(Mathf.Abs(influenceX));
                _intensity = Mathf.MoveTowards(_intensity, target,
                    Time.deltaTime / Mathf.Max(0.01f, easeInSeconds));
            }
            else
            {
                _intensity = Mathf.MoveTowards(_intensity, 0f,
                    Time.deltaTime / Mathf.Max(0.01f, recoverSeconds));
            }

            // 完全静止：恢复初始姿态后不再写旋转，避免与其它系统抢 transform
            if (_intensity <= 0.0001f && !inFlow)
            {
                if (_sway.rotation != _restWorld) RestoreRest();
                return;
            }

            _phase += Time.deltaTime * Mathf.Max(0.1f, swaySpeed) * Mathf.PI * 2f;

            // 顺势倾斜一半 + 正弦来回摇摆一半，两者都随强度缩放 → 强度归零时角度平滑归零
            float lean = _leanDirection * maxLeanDegrees * _intensity * 0.5f;
            float oscillate = Mathf.Sin(_phase) * maxLeanDegrees * _intensity * 0.5f;
            _sway.rotation = Quaternion.Euler(0f, 0f, lean + oscillate) * _restWorld;
        }

        private void RestoreRest()
        {
            if (_sway == null) return;
            _sway.rotation = _restWorld;
        }
    }
}
