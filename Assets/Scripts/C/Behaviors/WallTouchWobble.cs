// WallTouchWobble.cs —— Prototype C：挂墙 / 屋顶 Touch 物件的晃动反馈
// 不参与物理模拟：物件没有刚体，被恐龙碰到时沿法线轻推一下再回弹（阻尼正弦），
// 结束后一定回到装配时记录的静止位置，避免多次触发后位置漂移。

using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class WallTouchWobble : MonoBehaviour
    {
        [Tooltip("晃动幅度（米）")]
        public float amplitude = 0.05f;

        [Tooltip("一次晃动来回的总时长（秒）")]
        public float duration = 0.45f;

        [Tooltip("晃动来回次数")]
        public float oscillations = 1.5f;

        [Tooltip("触发范围外扩（米）")]
        public float probePadding = 0.08f;

        [Tooltip("两次触发之间的最短间隔（秒），避免贴着物件时反复抖动")]
        public float retriggerDelay = 0.25f;

        [Tooltip("打开后每次触发都打印方向与幅度，用于调试")]
        public bool debugLog = false;

        private Vector3 _restPosition;
        private Bounds _probe;
        private bool _configured;
        private bool _wasInside;
        private bool _playing;
        private float _elapsed;
        private float _cooldown;
        private Vector3 _axis = Vector3.back;

        private DinoCController _dino;
        private float _nextDinoSearch;

        /// <summary>装配时调用：记录静止位置与触发范围（此时模型已经在最终落点上）。</summary>
        public void Configure(float wobbleAmplitude, float wobbleDuration, float wobbleOscillations,
            float probePaddingMeters)
        {
            amplitude = Mathf.Max(0f, wobbleAmplitude);
            duration = Mathf.Max(0.05f, wobbleDuration);
            oscillations = Mathf.Max(0.5f, wobbleOscillations);
            probePadding = Mathf.Max(0f, probePaddingMeters);

            _restPosition = transform.position;

            if (TryGetWorldBounds(out Bounds bounds))
            {
                _probe = bounds;
                // 纵深方向放宽：物件贴墙、恐龙在房间内，深度差不应该阻止触发
                _probe.Expand(new Vector3(probePadding * 2f, probePadding * 2f, 2f));
            }
            else
            {
                _probe = new Bounds(_restPosition, new Vector3(0.5f, 0.5f, 2f));
            }

            _configured = true;
        }

        private void LateUpdate()
        {
            if (!_configured) return;

            float dt = Time.deltaTime;
            _cooldown -= dt;

            if (_dino == null && Time.time >= _nextDinoSearch)
            {
                _nextDinoSearch = Time.time + 1f;
                _dino = FindFirstObjectByType<DinoCController>();
            }

            bool inside = TryGetDinoBounds(out Bounds dinoBounds) && _probe.Intersects(dinoBounds);

            if (inside && !_wasInside && !_playing && _cooldown <= 0f)
                StartWobble(dinoBounds.center);

            _wasInside = inside;

            if (!_playing) return;

            _elapsed += dt;
            float u = Mathf.Clamp01(_elapsed / duration);
            if (u >= 1f)
            {
                StopWobble();
                return;
            }

            // 阻尼正弦：先被推出去，再回来，衰减到静止
            float wave = Mathf.Sin(u * Mathf.PI * 2f * oscillations) * Mathf.Exp(-3f * u);
            transform.position = _restPosition + _axis * (amplitude * wave);
        }

        private void StartWobble(Vector3 dinoCenter)
        {
            Vector3 direction = transform.position - dinoCenter;
            direction.y = 0f;

            // 法线方向：取「从恐龙指向物件」的水平方向；退化时朝墙内（-Z）
            _axis = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.back;

            _playing = true;
            _elapsed = 0f;
            _cooldown = duration + Mathf.Max(0f, retriggerDelay);

            if (debugLog)
            {
                Debug.Log($"[PrototypeC] 晃动反馈：{name}｜方向 ({_axis.x:0.00}, {_axis.z:0.00})｜幅度 {amplitude:0.000} m");
            }
        }

        private void StopWobble()
        {
            _playing = false;
            transform.position = _restPosition;
        }

        private void OnDisable()
        {
            StopWobble();
        }

        private bool TryGetDinoBounds(out Bounds bounds)
        {
            bounds = default;
            if (_dino == null) return false;

            // 只取精灵：恐龙身上挂着对话气泡等子节点，算进来会让触发范围明显偏大
            SpriteRenderer[] sprites = _dino.GetComponentsInChildren<SpriteRenderer>();
            bool any = false;
            foreach (SpriteRenderer sprite in sprites)
            {
                if (sprite == null) continue;
                if (!any)
                {
                    bounds = sprite.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(sprite.bounds);
                }
            }

            return any;
        }

        private bool TryGetWorldBounds(out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }
    }
}
