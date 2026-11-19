// TouchTestPusher.cs —— Prototype C：验证 Touch+Ground「可推动」的测试推手
// 为什么需要它：恐龙的活动范围是有限区间，自动 AI 可能很久都不走到物件旁边；
// 想在 Play 里马上确认「碰撞体 + 动态刚体 + 推力」这条链路通不通，就需要一个可控的推手。
//
// 它不是直接调用推力函数：推手是 Kinematic Rigidbody + BoxCollider，用 MovePosition 真的撞上去，
// 让 PhysX 产生真实接触，再由物件上的 GeneratedObjectPushRelay 打出「Touch 生效」日志。
//
// 用法（Play 模式）：选中 PrototypeC_TouchTestPusher → 组件右上角 ⋮ →
//   ① 跟随恐龙（真实物理接触）：推手贴着恐龙走，恐龙碰到可推动物件时就会触发；
//   ② 从物件左侧扫过（真实物理接触）：推手从物件左边扫到右边，结束后打印物件位移。
//
// 默认关闭（ObjectModelService.createTouchTestPusher = false），因为恐龙本体已经有碰撞箱。
// 开启后，测试期间会临时关掉恐龙本体碰撞箱，避免同一次接触由两边各加一次力；停止时自动恢复。

using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class TouchTestPusher : MonoBehaviour
    {
        public enum Mode
        {
            Idle,
            FollowDino,
            Sweep,
        }

        [Header("推手形状（世界尺寸，米；可随手改）")]
        [Tooltip("推手盒子的宽 × 高（米）")]
        public Vector2 boxSize = new Vector2(0.5f, 0.8f);

        [Tooltip("推手盒子的纵深（米）：要覆盖住物件的厚度，太薄会从物件前后漏过去")]
        public float boxDepth = 0.6f;

        [Header("跟随恐龙")]
        [Tooltip("相对恐龙身体中心的额外偏移（米）")]
        public Vector2 followOffset = Vector2.zero;

        [Header("扫过测试")]
        [Tooltip("扫过速度（米/秒）")]
        public float sweepSpeed = 1.5f;

        [Tooltip("扫过时在物件左右各多留的距离（米）")]
        public float sweepPadding = 0.9f;

        private BoxCollider _box;
        private Rigidbody _body;
        private Transform _target;
        private DinoCController _dino;
        private Mode _mode = Mode.Idle;
        private Vector3 _sweepFrom;
        private Vector3 _sweepTo;
        private float _sweepDistance;
        private float _sweepTravelled;
        private Vector3 _targetStartPosition;
        private Vector3 _followWorldOffset;
        private int _contactCount;
        private Collider _dinoCollider;
        private bool _dinoColliderTakenOver;

        public Mode CurrentMode => _mode;

        /// <summary>由 ObjectModelService 在装配可推动物件后调用，指定这次要测的物件。</summary>
        public void SetTarget(Transform target)
        {
            _target = target;
        }

        [ContextMenu("① 跟随恐龙（真实物理接触）")]
        public void StartFollowDino()
        {
            EnsureBuilt();

            _dino = FindFirstObjectByType<DinoCController>();
            if (_dino == null)
            {
                Debug.LogWarning("[PrototypeC] Touch 测试：场景里没有 DinoCController，无法跟随");
                return;
            }

            _followWorldOffset = Vector3.zero;
            if (GeneratedObjectPhysics.TryGetWorldBounds(_dino.gameObject, false, out Bounds dinoBounds))
                _followWorldOffset = dinoBounds.center - _dino.transform.position;

            _followWorldOffset += new Vector3(followOffset.x, followOffset.y, 0f);

            MoveTo(_dino.transform.position + _followWorldOffset);
            _box.enabled = true;
            _mode = Mode.FollowDino;
            _contactCount = 0;
            TakeOverDinoCollider(true);

            Debug.Log($"[PrototypeC] Touch 测试：推手开始跟随恐龙｜盒 {boxSize.x:0.00}×{boxSize.y:0.00}×{boxDepth:0.00} m｜" +
                      "碰到可推动物件会打印「Touch 生效：…」");
        }

        [ContextMenu("② 从物件左侧扫过（真实物理接触）")]
        public void SweepTarget()
        {
            EnsureBuilt();

            if (_target == null) _target = FindAnyTarget();
            if (_target == null)
            {
                Debug.LogWarning("[PrototypeC] Touch 测试：没有可推动的目标物件，先生成一个 Touch 物件");
                return;
            }

            if (!GeneratedObjectPhysics.TryGetWorldBounds(_target.gameObject, true, out Bounds bounds))
            {
                Debug.LogWarning($"[PrototypeC] Touch 测试：{_target.name} 没有 Renderer，量不到包围盒");
                return;
            }

            float halfWidth = bounds.extents.x;
            float y = bounds.center.y;
            float z = bounds.center.z;

            _sweepFrom = new Vector3(bounds.center.x - halfWidth - boxSize.x * 0.5f - sweepPadding, y, z);
            _sweepTo = new Vector3(bounds.center.x + halfWidth + boxSize.x * 0.5f + sweepPadding, y, z);
            _sweepDistance = Vector3.Distance(_sweepFrom, _sweepTo);
            _sweepTravelled = 0f;
            _targetStartPosition = _target.position;
            _contactCount = 0;

            MoveTo(_sweepFrom);
            _box.enabled = true;
            _mode = Mode.Sweep;
            TakeOverDinoCollider(true);

            Debug.Log($"[PrototypeC] Touch 测试：推手开始扫过 {_target.name}｜从 X {_sweepFrom.x:0.00} 扫到 {_sweepTo.x:0.00}｜" +
                      $"速度 {sweepSpeed:0.00} m/s");
        }

        [ContextMenu("③ 停止（收起推手）")]
        public void StopPusher()
        {
            _mode = Mode.Idle;
            if (_box != null) _box.enabled = false;
            TakeOverDinoCollider(false);
            Debug.Log("[PrototypeC] Touch 测试：推手已停止");
        }

        private void OnDisable()
        {
            TakeOverDinoCollider(false);
        }

        /// <summary>
        /// 测试推手生效期间临时关掉恐龙本体碰撞箱：否则同一次接触会由两边各加一次力（推力翻倍）。
        /// 停止或结束时恢复。
        /// </summary>
        private void TakeOverDinoCollider(bool takeOver)
        {
            if (_dinoCollider == null)
            {
                DinoCController dino = _dino != null ? _dino : FindFirstObjectByType<DinoCController>();
                if (dino != null) _dinoCollider = dino.GetComponent<Collider>();
            }

            if (_dinoCollider == null || _dinoColliderTakenOver == takeOver) return;

            _dinoColliderTakenOver = takeOver;
            _dinoCollider.enabled = !takeOver;
            Debug.Log(takeOver
                ? "[PrototypeC] Touch 测试：已临时关闭恐龙本体碰撞箱（避免重复施力），测试结束后自动恢复"
                : "[PrototypeC] Touch 测试：恐龙本体碰撞箱已恢复");
        }

        private void Awake()
        {
            EnsureBuilt();
        }

        private void FixedUpdate()
        {
            if (_mode == Mode.FollowDino)
            {
                if (_dino == null) return;
                _body.MovePosition(_dino.transform.position + _followWorldOffset);
                return;
            }

            if (_mode != Mode.Sweep) return;

            _sweepTravelled += Mathf.Max(0.05f, sweepSpeed) * Time.fixedDeltaTime;
            float t = _sweepDistance <= 0.0001f ? 1f : Mathf.Clamp01(_sweepTravelled / _sweepDistance);
            _body.MovePosition(Vector3.Lerp(_sweepFrom, _sweepTo, t));

            if (t >= 1f) FinishSweep();
        }

        private void FinishSweep()
        {
            _mode = Mode.Idle;
            if (_box != null) _box.enabled = false;
            TakeOverDinoCollider(false);

            float moved = _target != null
                ? Vector3.Distance(_target.position, _targetStartPosition)
                : 0f;

            string verdict = moved >= 0.01f ? "可推动生效" : "没有移动（检查碰撞体/刚体/是否被卡住）";
            Debug.Log($"[PrototypeC] Touch 测试完成：{(_target != null ? _target.name : "目标")}｜碰到 {_contactCount} 次｜" +
                      $"位移 {moved:0.000} m｜{verdict}");
        }

        private void OnCollisionEnter(Collision collision)
        {
            Rigidbody other = collision.rigidbody;
            if (other == null || other.isKinematic) return;

            _contactCount++;
            Debug.Log($"[PrototypeC] Touch 测试：推手碰到 {collision.collider.name}（动态刚体）");
        }

        private void EnsureBuilt()
        {
            if (_box == null)
            {
                _box = GetComponent<BoxCollider>();
                if (_box == null) _box = gameObject.AddComponent<BoxCollider>();
            }

            if (_body == null)
            {
                _body = GetComponent<Rigidbody>();
                if (_body == null) _body = gameObject.AddComponent<Rigidbody>();
            }

            _body.isKinematic = true;
            _body.useGravity = false;
            _body.constraints = RigidbodyConstraints.None;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _box.center = Vector3.zero;
            _box.size = new Vector3(
                Mathf.Max(0.05f, boxSize.x),
                Mathf.Max(0.05f, boxSize.y),
                Mathf.Max(0.02f, boxDepth));
            _box.enabled = _mode != Mode.Idle;
        }

        private void MoveTo(Vector3 worldPosition)
        {
            transform.position = worldPosition;
            if (_body != null) _body.position = worldPosition;
        }

        private Transform FindAnyTarget()
        {
            GeneratedObjectPushRelay[] relays = FindObjectsByType<GeneratedObjectPushRelay>(FindObjectsSortMode.None);
            return relays.Length > 0 ? relays[0].transform : null;
        }
    }
}
