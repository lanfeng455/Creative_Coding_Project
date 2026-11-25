// GroundStepGate.cs —— Prototype C：地面可推物件的单向台阶闸
// 场景地形：室内地面高（约 1.88）、左右两侧室外低（约 1.61），落差约 0.27 m。
// 目标：物件能被从室内推落到室外，但不能从室外推上台阶进室内。
// 原理：Touch/Play 地面物件默认锁 Y（只允许 X 平推，见 BehaviorDefaults.MakePushable），
//   本组件在 FixedUpdate 里对照「当前 X 的地面高度」（数据源 RoomLayout.dinoGroundZones，世界坐标）：
//   ① 前方地面比脚下高出超过 maxStep → 视为台阶侧壁，把 X 钳回上一个合法位置（外→内被挡）；
//   ② 脚下地面比当前低超过 maxStep → 已被推出台阶口悬空：临时解锁 Y 让重力把它放到低处，
//      落定后重新锁 Y（内→外落下，之后该物件再也无法回到室内——单向）。
// 边界：下落途中又被推回高台上方 → 取消下落、回到原高度，不会卡在半空。
// 不修改恐龙、不修改 BehaviorDefaults；没有配置地面分区时本组件不会被挂载（见 EnsureGroundStepGate）。

using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class GroundStepGate : MonoBehaviour
    {
        [Tooltip("高差超过这个值就算「墙」（米）；与恐龙 climbStepThreshold 同口径")]
        public float maxStep = 0.12f;

        private Rigidbody _body;
        private List<DinoGroundZone> _zones;
        private float _halfHeight;
        private float _lastFreeX;
        private bool _descending;
        private float _descendTargetY;
        private float _descendStartBottomY;

        /// <summary>由 ObjectModelService 在装配可推地面物件后调用；zones 为空则本组件不生效。</summary>
        public void Setup(List<DinoGroundZone> zones)
        {
            _zones = zones;
            _body = GetComponent<Rigidbody>();

            Collider col = GetComponent<Collider>();
            _halfHeight = col != null ? col.bounds.extents.y : 0.3f;

            _lastFreeX = transform.position.x;
        }

        private void FixedUpdate()
        {
            if (_body == null || _zones == null || _zones.Count == 0) return;

            Vector3 pos = _body.position;

            if (_descending)
            {
                UpdateDescent(pos);
                return;
            }

            float bottom = pos.y - _halfHeight;
            float groundY = GroundYAtX(pos.x);
            float rise = groundY - bottom;

            if (rise > maxStep)
            {
                // 台阶侧壁：X 钳回上一个合法位置，并清掉水平速度，避免持续抵墙抖动
                _body.position = new Vector3(_lastFreeX, pos.y, pos.z);
                Vector3 v = _body.linearVelocity;
                v.x = 0f;
                _body.linearVelocity = v;
                return;
            }

            _lastFreeX = pos.x;

            if (bottom - groundY > maxStep)
            {
                // 脚下已是更低的地面（被推出台阶口）：解锁 Y 开始下落
                _descending = true;
                _descendTargetY = groundY;
                _descendStartBottomY = bottom;
                _body.constraints &= ~RigidbodyConstraints.FreezePositionY;
                Debug.Log($"[PrototypeC] 台阶闸：{name} 被推出室内，落到室外地面（{bottom:0.00} → {groundY:0.00}）");
            }
        }

        private void UpdateDescent(Vector3 pos)
        {
            float groundY = GroundYAtX(pos.x);

            // 下落途中又被推回高台上方：取消下落，回到原高度（不会卡在半空）
            if (groundY >= _descendStartBottomY - 0.02f)
            {
                StopDescent(_descendStartBottomY);
                return;
            }

            float bottom = pos.y - _halfHeight;
            bool landed = bottom <= _descendTargetY + 0.02f;
            bool stuck = bottom < _descendStartBottomY - 0.05f && Mathf.Abs(_body.linearVelocity.y) < 0.05f;
            if (landed || stuck) StopDescent(_descendTargetY);
        }

        private void StopDescent(float bottomY)
        {
            _body.constraints |= RigidbodyConstraints.FreezePositionY;

            Vector3 pos = _body.position;
            _body.position = new Vector3(pos.x, bottomY + _halfHeight, pos.z);

            Vector3 v = _body.linearVelocity;
            v.y = 0f;
            _body.linearVelocity = v;

            _descending = false;
            _lastFreeX = _body.position.x;
        }

        /// <summary>与 DinoCController.GroundYAtX 同口径：分区内取分区高度，所有分区之外取最近分区边缘的高度。</summary>
        private float GroundYAtX(float x)
        {
            float bestY = 1.61f;
            float bestDistance = float.MaxValue;

            foreach (DinoGroundZone z in _zones)
            {
                if (z == null) continue;

                float lo = Mathf.Min(z.xMin, z.xMax);
                float hi = Mathf.Max(z.xMin, z.xMax);
                if (x >= lo && x <= hi) return z.groundY;

                float d = x < lo ? lo - x : x - hi;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestY = z.groundY;
                }
            }

            return bestY;
        }
    }
}
