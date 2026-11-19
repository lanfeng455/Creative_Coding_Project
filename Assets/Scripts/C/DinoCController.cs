// DinoCController.cs —— Prototype C 专属恐龙控制器（独立于 B1 玩法）
// 只负责：走动 / 躺下休息 / 压扁晃动等表现 / 活动范围 / 与场景生成物件互动。
// 不含：需求词、收集建材、盖房、火灾逃跑、玩球、椅子。
// 由 PrototypeCSceneBootstrap 在 TemplateC_LanMay 里运行时挂到 dino 上，
// 同时停用场景原有的 DinoAgentB1（组件保留，场景序列化数据不丢）。

using TMPro;
using UnityEngine;

namespace PrototypeC
{
    /// <summary>恐龙地面分区（世界坐标）：某段 X 上的地面高度。用于「房间地面略高、两侧室外略低」。</summary>
    [System.Serializable]
    public class DinoGroundZone
    {
        public string name = "outdoor";
        [Tooltip("世界坐标 X 左端")]
        public float xMin = -8.5f;
        [Tooltip("世界坐标 X 右端")]
        public float xMax = 8.5f;
        [Tooltip("这段地面的高度（恐龙脚底的世界 Y）")]
        public float groundY = 1.86f;
    }

    [RequireComponent(typeof(SpriteRenderer))]
    public class DinoCController : MonoBehaviour
    {
        private enum CState { Wandering, Resting, Interacting }

        // —— 活动范围（世界坐标，米）——
        // 场景里 dino 的实测值：地面 Y=1.86，X∈[-5.95, 5.9]
        public float groundY = 1.86f;
        public float activityXMin = -5.95f;
        public float activityXMax = 5.9f;
        public float edgeMargin = 0.8f;
        public float clampMargin = 0.4f;

        [Header("地面分区（室内/室外；跨区自动小跳）")]
        [Tooltip("留空时使用 RoomLayout 上的「恐龙地面分区」；都为空则全程用上面的 groundY")]
        public System.Collections.Generic.List<DinoGroundZone> groundZones = new System.Collections.Generic.List<DinoGroundZone>();
        [Tooltip("用 RoomLayout 配置的分区时，自动把活动范围扩到所有分区的并集")]
        public bool autoRangeFromZones = true;
        [Tooltip("前方地面比脚下高出超过这个值就起跳（米）")]
        public float climbStepThreshold = 0.12f;
        [Tooltip("上台阶时跳跃速度相对 hopForce 的倍数")]
        public float climbHopMultiplier = 3.2f;
        [Tooltip("下台阶时的小跳倍数（0 = 直接走下去）")]
        public float dropHopMultiplier = 1.1f;
        [Tooltip("提前探测台阶的距离（米）")]
        public float zoneProbeLookAhead = 0.35f;

        // —— 移动 / 念头 ——
        public float walkSpeed = 2.2f;
        public float wanderIntervalMin = 2f;
        public float wanderIntervalMax = 5f;
        public float restDurationMin = 4f;
        public float restDurationMax = 9f;
        public Vector2 moodInterval = new Vector2(6f, 14f);
        public float wanderWeight = 5f;
        public float restWeight = 2f;
        public float interactWeight = 4f;

        // —— 新物件响应 ——
        public bool newItemResponse = true;
        public float touchPushOverlap = 0.35f;
        public float touchDuration = 6f;
        public float touchSuccessDistance = 0.15f;
        public int playHops = 4;
        public float playHopInterval = 0.35f;
        public float observeDuration = 1.6f;
        public float newTargetMinInterval = 2.5f;
        public float interactStandDistance = 0.9f;
        public Vector2 interactCooldown = new Vector2(4f, 10f);
        public float interactNoticeRange = 12f;

        // —— 视觉 ——
        public float hopForce = 1.2f;
        public float squashAmount = 0.12f;
        public float restLieAngle = 90f;
        public float restLieDuration = 0.55f;
        public float restLieLift = 0.35f;
        public float restJumpMultiplier = 2.2f;
        public float restBedYOffset = 0.02f;
        public float restBedApproachPadding = 0.08f;

        // —— 场景遗留气泡：C 阶段不索要任何词，只负责把它隐藏 ——
        public TextMeshPro bubble;

        private float RangeLo => activityXMin + edgeMargin;
        private float RangeHi => activityXMax - edgeMargin;
        private float ClampLo => activityXMin + clampMargin;
        private float ClampHi => activityXMax - clampMargin;
        private float FootY => _restOnBed ? _restBedTopY + restBedYOffset : GroundYAtX(transform.position.x);

        private System.Collections.Generic.List<DinoGroundZone> _zones;
        private bool _zonesResolved;

        /// <summary>解析地面分区：优先用本组件 Inspector 的列表，其次用 RoomLayout 的配置；
        /// 两者都为空时退回单一分区（全程 groundY，行为与旧版一致）。</summary>
        private void ResolveGroundZones()
        {
            if (_zonesResolved) return;
            _zonesResolved = true;

            var layout = FindObjectOfType<RoomLayout>();

            if (groundZones != null && groundZones.Count > 0)
            {
                _zones = groundZones;
            }
            else if (layout != null && layout.dinoGroundZones != null && layout.dinoGroundZones.Count > 0)
            {
                _zones = layout.dinoGroundZones;
            }

            if (_zones == null || _zones.Count == 0)
            {
                // 没填分区：至少让恐龙能走遍整个世界（地面高度先沿用旧值）
                float lo = activityXMin, hi = activityXMax;
                if (layout != null)
                {
                    lo = Mathf.Min(layout.worldUsableXMin, layout.worldUsableXMax);
                    hi = Mathf.Max(layout.worldUsableXMin, layout.worldUsableXMax);
                }

                _zones = new System.Collections.Generic.List<DinoGroundZone>
                {
                    new DinoGroundZone { name = "ground", xMin = lo, xMax = hi, groundY = groundY },
                };

                Debug.LogWarning("[DinoC] 没有配置地面分区：恐龙会全程用旧地面高度。\n" +
                                 "要做出「进出房间跳一下」，请在 RoomLayout 的 Inspector 里填 dinoGroundZones（房间一段、左右室外各一段）。");
            }

            if (autoRangeFromZones)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var z in _zones)
                {
                    if (z == null) continue;
                    lo = Mathf.Min(lo, Mathf.Min(z.xMin, z.xMax));
                    hi = Mathf.Max(hi, Mathf.Max(z.xMin, z.xMax));
                }
                if (hi > lo)
                {
                    activityXMin = lo;
                    activityXMax = hi;
                }
            }
        }

        /// <summary>x 处的地面高度：落在分区内用分区高度；所有分区之外取最近分区的边缘高度。</summary>
        private float GroundYAtX(float x)
        {
            if (_zones == null || _zones.Count == 0) return groundY;

            float bestY = groundY;
            float bestDistance = float.MaxValue;

            foreach (var z in _zones)
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

        private CState _state;
        private float _vy, _timer, _stateDur, _restCooldown = 8f;
        private float _desiredX;
        private Vector3 _baseScale = Vector3.one;
        private bool _baseScaleCaptured;
        private Quaternion _baseLocalRotation = Quaternion.identity;
        private float _visualY = 1f;
        private float _lieT, _lieEase;

        private PlacedItem _interactItem;
        private PlacedItem _pendingNewItem;
        private Transform _interactTarget;
        private float _interactStartedAt;
        private float _interactCooldown;
        private float _pushLastX, _pushMoved;
        private bool _pushPraised;
        private int _playHopsLeft;
        private float _playHopTimer;

        private float _restBedTopY;
        private bool _restOnBed;
        private RoomOccupancy _occupancy;

        private float _externalTargetX, _externalHoldUntil, _externalHopCd;

        private void Start()
        {
            if (bubble != null) bubble.gameObject.SetActive(false);

            ResolveGroundZones();

            var p = transform.position;
            p.y = GroundYAtX(p.x);
            p.x = Mathf.Clamp(p.x, ClampLo, ClampHi);
            transform.position = p;

            _interactCooldown = Random.Range(
                Mathf.Min(interactCooldown.x, interactCooldown.y),
                Mathf.Max(interactCooldown.x, interactCooldown.y));

            if (newItemResponse) ObjectModelService.ItemSettled += OnItemSettled;

            Debug.Log($"[DinoC] C 控制器启用：地面 Y={groundY}，X∈[{activityXMin}, {activityXMax}]｜"
                    + $"念头权重 wander={wanderWeight} rest={restWeight} interact={interactWeight}｜"
                    + $"新物件响应={(newItemResponse ? "开" : "关")}");

            EnterWander();
        }

        private void OnDestroy()
        {
            ObjectModelService.ItemSettled -= OnItemSettled;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _timer += dt;
            _restCooldown -= dt;
            _interactCooldown -= dt;

            // 挂起的新物件：回到自主漫步、且外部玩法不再占用时优先响应
            if (_pendingNewItem != null && _state == CState.Wandering
                && Time.time >= _externalHoldUntil)
            {
                TryConsumePendingItem();
            }

            switch (_state)
            {
                case CState.Wandering: UpdateWandering(); break;
                case CState.Resting: UpdateResting(); break;
                case CState.Interacting: UpdateInteracting(); break;
            }

            UpdateLieBlend(dt);
            ApplyMotion(dt);
            ApplyVisual();
        }

        /// <summary>
        /// 躺下动画：Resting 且贴地 → 躺平进度奔 1；其余状态平滑爬起。
        /// 旋转在 ApplyVisual 应用；地面高度按进度抬升（ApplyMotion）。
        /// </summary>
        private void UpdateLieBlend(float dt)
        {
            // 贴地判定必须带上躺下的抬升量，否则会出现「抬起→判定离地→爬起→落地→又躺」的抽搐循环
            float lieGroundY = _restOnBed ? _restBedTopY + restBedYOffset : GroundYAtX(transform.position.x);
            bool grounded = transform.position.y <= lieGroundY + restLieLift * _lieEase + 0.01f && _vy <= 0.01f;
            bool wantLie = restLieAngle > 1f && _state == CState.Resting && grounded;
            _lieT = Mathf.MoveTowards(_lieT, wantLie ? 1f : 0f, dt / Mathf.Max(0.05f, restLieDuration));
            _lieEase = _lieT * _lieT * (3f - 2f * _lieT);
        }

        /// <summary>按权重挑下一个念头：发呆 / 休息 / 找物件互动。找不到可互动对象时把互动权重让给发呆。</summary>
        private void PickMood()
        {
            _timer = 0f;
            _stateDur = Random.Range(
                Mathf.Min(moodInterval.x, moodInterval.y),
                Mathf.Max(moodInterval.x, moodInterval.y));

            float wWander = Mathf.Max(0f, wanderWeight);
            float wRest = _restCooldown <= 0f ? Mathf.Max(0f, restWeight) : 0f;
            float wInteract = _interactCooldown <= 0f ? Mathf.Max(0f, interactWeight) : 0f;

            if (wInteract > 0f && FindInteractItem() == null) wInteract = 0f;

            float total = wWander + wRest + wInteract;
            if (total <= 0f) return;

            float roll = Random.value * total;

            if ((roll -= wInteract) < 0f)
            {
                EnterItemInteraction(FindInteractItem(), fromNotification: false);
                return;
            }
            if ((roll -= wRest) < 0f)
            {
                EnterResting();
                return;
            }

            // 发呆 = 不额外动作，只换个漫步目标
            _state = CState.Wandering;
            _stateDur = Random.Range(wanderIntervalMin, wanderIntervalMax);
            PickWanderTarget();
        }

        /// <summary>在已登记占位的地面物件里，挑一个横向最近、有场景实例的作为互动对象。</summary>
        private PlacedItem FindInteractItem()
        {
            if (_occupancy == null) _occupancy = FindObjectOfType<RoomOccupancy>();
            if (_occupancy == null) return null;

            PlacedItem best = null;
            float bestDistance = Mathf.Max(0.1f, interactNoticeRange);

            foreach (PlacedItem item in _occupancy.Items)
            {
                if (item == null || item.instance == null) continue;
                // 只跟地面上的互动：挂墙和天花板上的够不着
                if (item.band != RoomBand.Ground) continue;

                float distance = Mathf.Abs(item.instance.transform.position.x - transform.position.x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = item;
                }
            }

            return best;
        }

        /// <summary>目标物件是否够得着：横版视角只看 X。</summary>
        private bool ItemReachable(PlacedItem item)
        {
            if (item == null || item.instance == null || item.band != RoomBand.Ground) return false;
            float x = item.instance.transform.position.x;
            return x >= RangeLo - 1f && x <= RangeHi + 1f;
        }

        /// <summary>ObjectModelService.ItemSettled 回调：新物件落地落定后决定要不要响应。</summary>
        private void OnItemSettled(PlacedItem item)
        {
            if (!newItemResponse) return;
            if (!ItemReachable(item)) return;
            if (_interactItem != null && _interactItem.id == item.id) return;

            // 正在互动且还没过防抖窗口 → 只记下最新的一个，等当前互动结束再响应
            if (_state == CState.Interacting && Time.time - _interactStartedAt < newTargetMinInterval)
            {
                _pendingNewItem = item;
                return;
            }

            // 外部玩法（守门 / 踩球）占用中 → 挂起
            if (Time.time < _externalHoldUntil)
            {
                _pendingNewItem = item;
                return;
            }

            EnterItemInteraction(item, fromNotification: true);
        }

        /// <summary>消费挂起的新物件（只保留最新一个）；目标已失效或够不着则返回 false。</summary>
        private bool TryConsumePendingItem()
        {
            PlacedItem pending = _pendingNewItem;
            _pendingNewItem = null;
            if (pending == null || !ItemReachable(pending)) return false;

            EnterItemInteraction(pending, fromNotification: true);
            return true;
        }

        /// <summary>进入物件互动：按 behavior 设定时长与动作（分派见 UpdateInteracting）。</summary>
        private void EnterItemInteraction(PlacedItem item, bool fromNotification)
        {
            if (item == null || item.instance == null)
            {
                EnterWander();
                return;
            }

            _interactItem = item;
            _interactTarget = item.instance.transform;
            _state = CState.Interacting;
            _timer = 0f;
            _interactStartedAt = Time.time;
            _pushLastX = _interactTarget.position.x;
            _pushMoved = 0f;
            _pushPraised = false;
            _playHopsLeft = Mathf.Max(0, playHops);
            _playHopTimer = 0f;

            switch (item.behavior)
            {
                case "Touch":
                    _stateDur = Mathf.Max(2f, touchDuration);
                    break;
                case "Rest":
                    _stateDur = 10f;   // 走到床边 + 跳起 + 落稳 的总时限
                    if (GeneratedObjectPhysics.TryGetWorldBounds(item.instance, true, out Bounds bedBounds))
                        _restBedTopY = bedBounds.max.y;
                    else
                        _restBedTopY = item.instance.transform.position.y + Mathf.Max(0.3f, Mathf.Abs(item.size.y) * 0.5f);
                    _restOnBed = false;
                    break;
                case "Play":
                    _stateDur = playHops * Mathf.Max(0.15f, playHopInterval) + 3f;
                    break;
                default:              // Ignore / 未知标签 → 观察一会
                    _stateDur = Mathf.Max(0.8f, observeDuration);
                    break;
            }

            if (fromNotification)
                Debug.Log($"[DinoC] 新物件响应：{item.objectName}（{item.behavior}）→ {DescribeInteraction(item.behavior)}");
        }

        private static string DescribeInteraction(string behavior)
        {
            switch (behavior)
            {
                case "Touch": return "走过去推一推";
                case "Rest": return "走过去躺下休息";
                case "Play": return "走过去蹦跳玩耍";
                default: return "站远处观察一下";
            }
        }

        /// <summary>互动主循环：目标有效性检查 → 按 behavior 分派。</summary>
        private void UpdateInteracting()
        {
            if (_interactTarget == null || _interactItem == null)
            {
                ExitInteraction("目标物件已移除");
                return;
            }

            float targetX = _interactTarget.position.x;

            if (targetX < RangeLo - 1.5f || targetX > RangeHi + 1.5f)
            {
                ExitInteraction("目标超出活动范围");
                return;
            }

            switch (_interactItem.behavior)
            {
                case "Touch": UpdateTouchInteract(targetX); break;
                case "Rest": UpdateRestInteract(targetX); break;
                case "Play": UpdatePlayInteract(targetX); break;
                default: UpdateObserveInteract(targetX); break;
            }
        }

        /// <summary>Touch：压进物件边缘推动它（靠本体碰撞箱 + GeneratedObjectPushRelay 的物理），并感知位移。</summary>
        private void UpdateTouchInteract(float targetX)
        {
            float halfWidth = Mathf.Max(0.2f, Mathf.Abs(_interactItem.size.x) * 0.5f);
            float side = transform.position.x <= targetX ? -1f : 1f;
            float standX = Mathf.Clamp(targetX + side * (halfWidth - touchPushOverlap), RangeLo, RangeHi);
            _desiredX = standX;
            Face(targetX >= transform.position.x ? 1 : -1);

            float nowX = _interactTarget.position.x;
            _pushMoved += Mathf.Abs(nowX - _pushLastX);
            _pushLastX = nowX;

            if (!_pushPraised && _pushMoved >= touchSuccessDistance)
            {
                _pushPraised = true;
                if (_vy <= 0.01f) _vy = hopForce * 1.6f;   // 推动了！开心跳一下
                AudioManager.PlayAt(Sfx.DinoVoice, transform.position);
            }

            if (_timer >= _stateDur || _pushMoved > 0.8f)
            {
                Debug.Log($"[DinoC] Touch 结束：{_interactItem.objectName} 累计位移 {_pushMoved:F2} 米"
                        + (_pushPraised ? "（推动成功）" : "（没怎么推动：太重或卡住了）"));
                ExitInteraction(string.Empty);
            }
        }

        /// <summary>Rest：走到床边→跳起→落到床面→切 Resting 躺下；结束后自动掉回地面。</summary>
        private void UpdateRestInteract(float targetX)
        {
            float halfWidth = Mathf.Max(0.2f, Mathf.Abs(_interactItem.size.x) * 0.5f);
            float side = transform.position.x <= targetX ? -1f : 1f;
            float standX = Mathf.Clamp(targetX + side * (halfWidth - restBedApproachPadding), RangeLo, RangeHi);
            _desiredX = standX;

            if (_restOnBed) { ExitInteraction(string.Empty); return; }

            if (Mathf.Abs(transform.position.x - standX) < 0.25f)
            {
                Face(targetX >= transform.position.x ? 1 : -1);
                if (_vy <= 0.01f)
                {
                    _vy = hopForce * restJumpMultiplier;
                    Debug.Log($"[DinoC] Rest：跳上床（顶面 Y={_restBedTopY:F2}）");
                }
            }

            if (transform.position.y >= _restBedTopY - 0.05f && _vy >= -0.5f)
            {
                Vector3 p = transform.position;
                p.y = _restBedTopY + restBedYOffset;
                transform.position = p;
                _vy = 0f;
                _restOnBed = true;

                string name = _interactItem != null ? _interactItem.objectName : "床";
                _interactItem = null;
                _interactTarget = null;
                _state = CState.Resting;
                _timer = 0f;
                _stateDur = Random.Range(restDurationMin, restDurationMax);
                _restCooldown = Random.Range(12f, 26f);
                Debug.Log($"[DinoC] Rest：已落到 {name} 顶面开始休息（停留 {_stateDur:0} 秒）");
                return;
            }

            if (_timer > 10f) ExitInteraction("半天走不到床/跳不上去，算了");
        }

        /// <summary>Play：站到物件旁，面对它原地小蹦跳几次，跳完结束。</summary>
        private void UpdatePlayInteract(float targetX)
        {
            float halfWidth = Mathf.Max(0.2f, Mathf.Abs(_interactItem.size.x) * 0.5f);
            float side = transform.position.x <= targetX ? -1f : 1f;
            float standX = Mathf.Clamp(targetX + side * (halfWidth + 0.5f), RangeLo, RangeHi);
            _desiredX = standX;
            Face(targetX >= transform.position.x ? 1 : -1);

            if (Mathf.Abs(transform.position.x - standX) > 0.3f) return;

            _playHopTimer -= Time.deltaTime;
            if (_playHopTimer <= 0f && _playHopsLeft > 0)
            {
                _playHopsLeft--;
                _playHopTimer = Mathf.Max(0.15f, playHopInterval);
                if (_vy <= 0.01f) _vy = hopForce * Random.Range(1.4f, 2.2f);
                _desiredX = Mathf.Clamp(standX + Random.Range(-0.25f, 0.25f), RangeLo, RangeHi);
                if (_playHopsLeft % 2 == 0) AudioManager.PlayAt(Sfx.DinoVoice, transform.position);
            }

            if (_playHopsLeft <= 0 && transform.position.y <= FootY + 0.02f)
                ExitInteraction(string.Empty);

            if (_timer >= _stateDur) ExitInteraction("玩耍超时");
        }

        /// <summary>Ignore：站得稍远面对物件观察，到点恢复自主行为。</summary>
        private void UpdateObserveInteract(float targetX)
        {
            float side = transform.position.x <= targetX ? -1f : 1f;
            float standX = Mathf.Clamp(targetX + side * Mathf.Max(0.6f, interactStandDistance * 1.6f), RangeLo, RangeHi);
            _desiredX = standX;
            Face(targetX >= transform.position.x ? 1 : -1);

            if (_timer >= _stateDur) ExitInteraction(string.Empty);
        }

        /// <summary>退出互动：清目标、进冷却；挂起队列里有更新的新物件则优先响应它。</summary>
        private void ExitInteraction(string reason)
        {
            if (!string.IsNullOrEmpty(reason))
                Debug.Log($"[DinoC] 互动中断：{(_interactItem != null ? _interactItem.objectName : "目标")}｜{reason}");

            _interactCooldown = Random.Range(
                Mathf.Min(interactCooldown.x, interactCooldown.y),
                Mathf.Max(interactCooldown.x, interactCooldown.y));
            _interactItem = null;
            _interactTarget = null;

            if (TryConsumePendingItem()) return;
            EnterWander();
        }

        private void EnterWander()
        {
            _state = CState.Wandering;
            _timer = 0f;
            _stateDur = Random.Range(wanderIntervalMin, wanderIntervalMax);
            PickWanderTarget();
        }

        private void UpdateWandering()
        {
            // 外部驱动窗口（踩球 / 篮筐辅助）：期间只朝指定目标走，不重新挑点
            if (Time.time < _externalHoldUntil)
            {
                _desiredX = _externalTargetX;
                return;
            }

            bool arrived = Mathf.Abs(transform.position.x - _desiredX) < 0.25f;
            if (arrived || _timer > _stateDur)
            {
                if (_restCooldown <= 0f && Random.value < 0.5f)
                {
                    EnterResting();
                    return;
                }
                _timer = 0f;
                _stateDur = Random.Range(wanderIntervalMin, wanderIntervalMax);
                PickWanderTarget();
            }
        }

        private void EnterResting()
        {
            _state = CState.Resting;
            _timer = 0f;
            _stateDur = Random.Range(restDurationMin, restDurationMax);
            _restCooldown = Random.Range(12f, 26f);
        }

        private void UpdateResting()
        {
            if (_timer < _stateDur) return;

            if (_restOnBed)
            {
                // 从床面起来：清床面标志 → groundY 变回地面 → 自由下落回地面
                _restOnBed = false;
                _vy = -1.5f;
                Debug.Log("[DinoC] Rest：从床上起来，掉落回地面");
            }
            EnterWander();
        }

        private void PickWanderTarget()
        {
            _desiredX = Random.Range(RangeLo, RangeHi);
        }

        private void ApplyMotion(float dt)
        {
            var p = transform.position;
            float lo = ClampLo, hi = ClampHi;

            float speed = _state == CState.Resting ? 0f : walkSpeed;

            float dx = _desiredX - p.x;
            if (speed > 0f && Mathf.Abs(dx) > 0.05f)
            {
                float dir = Mathf.Sign(dx);
                float step = Mathf.Clamp(dx, -speed * dt, speed * dt);
                float targetX = Mathf.Clamp(p.x + step, lo, hi);

                float groundHere = GroundYAtX(p.x);
                float groundAhead = GroundYAtX(targetX + dir * Mathf.Max(0.01f, zoneProbeLookAhead));
                bool grounded = p.y <= groundHere + 0.02f && _vy <= 0.01f;

                // 只有真的在移动才蹦跳（否则到站了还在原地蹦）
                if (grounded && hopForce > 0f)
                {
                    if (groundAhead > groundHere + climbStepThreshold)
                    {
                        // 上台阶（进房间）：按高差反算需要的起跳速度，保证能踩上去
                        float need = groundAhead - groundHere + 0.15f;
                        float needVelocity = Mathf.Sqrt(2f * 9.8f * Mathf.Max(0.05f, need));
                        _vy = Mathf.Min(12f, Mathf.Max(hopForce * Mathf.Max(1f, climbHopMultiplier), needVelocity));
                    }
                    else if (groundAhead < groundHere - climbStepThreshold && dropHopMultiplier > 0f)
                    {
                        // 下台阶（出房间）：小跳下去，避免直直坠落
                        _vy = hopForce * Mathf.Max(0.2f, dropHopMultiplier);
                    }
                    else
                    {
                        _vy = hopForce;
                    }
                }

                // 身体还没升到前方地面之上时，先被台阶挡住（避免横向穿进高地板）
                float blockY = GroundYAtX(targetX) + 0.02f;
                if (p.y >= blockY) p.x = targetX;

                Face(dir > 0f ? 1 : -1);
            }

            _vy -= 9.8f * dt;
            p.y += _vy * dt;
            // 躺下时地面按动画进度抬升，避免身体中线半埋进地板
            float floorY = FootY + restLieLift * _lieEase;
            if (p.y <= floorY) { p.y = floorY; _vy = 0f; }
            transform.position = p;
        }

        private void Face(int dir)
        {
            var s = transform.localScale;
            s.x = Mathf.Abs(s.x) * dir;
            transform.localScale = s;
        }

        private void ApplyVisual()
        {
            // 首帧记录场景里配的缩放；之后绝不重读（否则每帧重采会越拉越瘦）
            if (!_baseScaleCaptured)
            {
                _baseScale = transform.localScale;
                _baseScale.x = Mathf.Abs(_baseScale.x);
                _baseLocalRotation = transform.localRotation;
                _baseScaleCaptured = true;
            }

            bool interacting = _state == CState.Interacting && Mathf.Abs(transform.position.x - _desiredX) < 0.35f;
            bool grounded = transform.position.y <= FootY + restLieLift * _lieEase + 0.01f && _vy <= 0.01f;
            bool lying = _lieEase > 0f;

            float target = interacting ? 0.88f + Mathf.Abs(Mathf.Sin(Time.time * 3.2f)) * 0.14f
                : lying ? 1f
                : grounded ? 0.96f : 1f + squashAmount;
            _visualY = Mathf.Lerp(_visualY, target, grounded ? 0.3f : 0.15f);

            int face = Mathf.Sign(transform.localScale.x) < 0f ? -1 : 1;
            transform.localScale = new Vector3(
                _baseScale.x / Mathf.Max(_visualY, 0.6f) * face,
                _baseScale.y * _visualY, _baseScale.z);
            // 休息时绕脚底旋转躺平；_lieEase 归零后恢复场景原始旋转
            transform.localRotation = Quaternion.Euler(
                0f, 0f, _baseLocalRotation.eulerAngles.z - face * restLieAngle * _lieEase);
        }

        // —— 外部驱动（踩球 / 篮筐辅助；不改本控制器的自主规则）——

        /// <summary>C 阶段没有逃跑/搬运/建造，所以外部指令永远不被占用。</summary>
        public bool ExternalBusy => false;

        /// <summary>外部让恐龙走向某个 x（holdSeconds 秒内不会被随机漫步覆盖）。</summary>
        public void ExternalWalkTo(float x, float holdSeconds = 2.5f)
        {
            // 外部玩法优先于物件互动：清掉互动目标再接管
            _interactItem = null;
            _interactTarget = null;
            _pendingNewItem = null;

            _externalTargetX = Mathf.Clamp(x, ClampLo, ClampHi);
            _externalHoldUntil = Time.time + Mathf.Max(0.1f, holdSeconds);
            _desiredX = _externalTargetX;
            _timer = 0f;
            if (_state != CState.Wandering)
            {
                _state = CState.Wandering;
                _stateDur = Mathf.Max(0.1f, holdSeconds);
            }
        }

        /// <summary>外部让恐龙转向：dir &gt; 0 朝右，dir &lt; 0 朝左。</summary>
        public void ExternalFace(int dir)
        {
            if (dir != 0) Face(dir > 0 ? 1 : -1);
        }

        /// <summary>外部让恐龙跳一下（贴地才生效，0.5 秒冷却）。multiplier 相对 hopForce。</summary>
        public void ExternalHop(float multiplier = 3f)
        {
            if (Time.time < _externalHopCd) return;
            if (transform.position.y > FootY + 0.02f) return;
            _vy = hopForce * Mathf.Max(0.5f, multiplier);
            _externalHopCd = Time.time + 0.5f;
        }

        /// <summary>是否正在下落且离地（恐龙下压球用）。</summary>
        public bool IsFalling => _vy < -0.1f && transform.position.y > FootY + 0.02f;

        /// <summary>是否离地（上升或下落都算）。</summary>
        public bool IsAirborne => transform.position.y > FootY + 0.02f;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            float y = groundY;
            Gizmos.DrawLine(new Vector3(RangeLo, y + 2f, 0f), new Vector3(RangeHi, y + 2f, 0f));
            Gizmos.DrawLine(new Vector3(ClampLo, y, 0f), new Vector3(ClampHi, y, 0f));
            Gizmos.DrawLine(new Vector3(RangeLo, y, 0f), new Vector3(RangeLo, y + 2f, 0f));
            Gizmos.DrawLine(new Vector3(RangeHi, y, 0f), new Vector3(RangeHi, y + 2f, 0f));

            // 地面分区：每段画一条自己的地面线，并标出高差跳点
            var zones = groundZones;
            if ((zones == null || zones.Count == 0) && Application.isPlaying && _zones != null) zones = _zones;
            if (zones == null) return;

            Gizmos.color = Color.yellow;
            foreach (var z in zones)
            {
                if (z == null) continue;
                float lo = Mathf.Min(z.xMin, z.xMax);
                float hi = Mathf.Max(z.xMin, z.xMax);
                Gizmos.DrawLine(new Vector3(lo, z.groundY, 0f), new Vector3(hi, z.groundY, 0f));
                Gizmos.DrawLine(new Vector3(lo, z.groundY, 0f), new Vector3(lo, z.groundY + 0.6f, 0f));
                Gizmos.DrawLine(new Vector3(hi, z.groundY, 0f), new Vector3(hi, z.groundY + 0.6f, 0f));
            }
        }
    }
}
