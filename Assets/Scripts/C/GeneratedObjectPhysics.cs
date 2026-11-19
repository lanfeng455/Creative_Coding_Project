// GeneratedObjectPhysics.cs —— Prototype C：生成物物理的公共工具
// behavior × placement 的碰撞体/刚体配置已经移到 Behaviors/（ObjectBehaviorProfile + 行为装配表），
// 这里只保留三件与具体格子无关的事：
//   1) TryGetLocalBounds / TryGetWorldBounds：把 Renderer 包围盒换算到局部/世界空间（碰撞体/尺寸/占地共用）；
//   2) AddDinoBodyCollider + DinoBodyContactSync：直接在恐龙本体上加运动学碰撞箱，并把恐龙每帧直写的
//      transform 同步进物理世界，让动态生成物被真实接触推动；
//   3) GeneratedObjectPushRelay：运动学推手碰到动态生成物时补一点水平速度并打印「Touch 生效」日志。
//
// 碰撞体选择：GLB 没有碰撞体；MeshCollider(convex) 限制 255 面、对 3 万面的生成物开销大且容易失败，
// 所以一律用「包围盒」近似。代价便宜、稳定，缺点是异形模型（树枝、画框角）会偏大。

using UnityEngine;

namespace PrototypeC
{
    public static class GeneratedObjectPhysics
    {
        /// <summary>
        /// 直接在恐龙本体上加碰撞箱（BoxCollider + kinematic Rigidbody），不再另建替身节点。
        /// X/Y：恐龙「自身」SpriteRenderer 的世界包围盒 × xyScale（不含气泡/图标等子物体）；
        /// Z：由 depth 单独控制（世界米），用来跨过恐龙与生成物在 Z 上的错位。
        /// 注意 Collider.size 是局部单位，而恐龙 localScale = 2，所以必须除以 |lossyScale|，不能直接塞世界尺寸。
        /// 返回加好的碰撞体，失败返回 null。
        /// </summary>
        public static Collider AddDinoBodyCollider(GameObject host, float xyScale, float depth, float zOffset)
        {
            if (host == null) return null;

            Collider existing = host.GetComponent<Collider>();
            if (existing != null && !(existing is BoxCollider))
            {
                Debug.LogWarning($"[PrototypeC] {host.name} 身上已有非 Box 的 Collider，跳过恐龙碰撞箱");
                return existing;
            }

            if (!TryGetWorldBounds(host, false, out Bounds worldBounds))
            {
                Debug.LogWarning($"[PrototypeC] 无法为 {host.name} 计算碰撞体（自身没有 Renderer），Touch 物件不会被推动");
                return null;
            }

            BoxCollider box = existing as BoxCollider;
            if (box == null) box = host.AddComponent<BoxCollider>();

            Vector3 lossy = host.transform.lossyScale;
            float ax = Mathf.Max(0.0001f, Mathf.Abs(lossy.x));
            float ay = Mathf.Max(0.0001f, Mathf.Abs(lossy.y));
            float az = Mathf.Max(0.0001f, Mathf.Abs(lossy.z));

            float worldWidth = Mathf.Max(0.02f, worldBounds.size.x * Mathf.Max(0.01f, xyScale));
            float worldHeight = Mathf.Max(0.02f, worldBounds.size.y * Mathf.Max(0.01f, xyScale));
            float worldDepth = Mathf.Max(0.05f, depth);

            // 世界中心 → 局部中心：恐龙靠 localScale.x 取负来转身，走 InverseTransformPoint 才不会被镜像搞反
            Vector3 localCenter = host.transform.InverseTransformPoint(worldBounds.center);

            box.center = new Vector3(localCenter.x, localCenter.y, localCenter.z + zOffset / az);
            box.size = new Vector3(worldWidth / ax, worldHeight / ay, worldDepth / az);

            Rigidbody body = host.GetComponent<Rigidbody>();
            if (body == null) body = host.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.None;

            // 恐龙每帧直写 transform.position / localScale，PhysX 要等下一次物理步才看到新姿态；
            // 这个组件在 LateUpdate 里把位移与缩放刷进物理世界，不改恐龙的移动逻辑。
            if (host.GetComponent<DinoBodyContactSync>() == null) host.AddComponent<DinoBodyContactSync>();

            float centerZ = host.transform.TransformPoint(box.center).z;
            Debug.Log($"[PrototypeC] 恐龙本体碰撞箱：{host.name}｜Collider.size（局部）" +
                      $"{box.size.x:0.00}×{box.size.y:0.00}×{box.size.z:0.00}｜世界 " +
                      $"{worldWidth:0.00}×{worldHeight:0.00}×{worldDepth:0.00} m｜" +
                      $"Z 范围 {centerZ - worldDepth * 0.5f:0.00}…{centerZ + worldDepth * 0.5f:0.00}");
            return box;
        }

        /// <summary>各 Renderer 世界包围盒的并集；includeChildren = false 时只看宿主自身。</summary>
        public static bool TryGetWorldBounds(GameObject root, bool includeChildren, out Bounds world)
        {
            world = default;
            if (root == null) return false;

            Renderer[] renderers = includeChildren
                ? root.GetComponentsInChildren<Renderer>()
                : root.GetComponents<Renderer>();

            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!any)
                {
                    world = renderer.bounds;
                    any = true;
                }
                else
                {
                    world.Encapsulate(renderer.bounds);
                }
            }

            return any;
        }

        /// <summary>
        /// 把各 Renderer 世界包围盒的 8 个角点换算回 root 局部空间，得到贴合当前朝向的局部盒。
        /// 直接拿世界 AABB 除 scale 会在 yaw = 90° 时把长宽对调，所以统一走「角点 → 局部」。
        /// includeChildren = false 时只看宿主自身的 Renderer（恐龙用，避免把对话气泡算进身体）。
        /// </summary>
        public static bool TryGetLocalBounds(GameObject root, bool includeChildren, out Bounds local)
        {
            local = new Bounds(Vector3.zero, Vector3.zero);
            if (root == null) return false;

            Transform t = root.transform;
            Renderer[] renderers = includeChildren
                ? root.GetComponentsInChildren<Renderer>()
                : root.GetComponents<Renderer>();

            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            bool any = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;

                Bounds b = renderer.bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z);
                    Vector3 p = t.InverseTransformPoint(corner);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                any = true;
            }

            if (!any) return false;

            local = new Bounds((min + max) * 0.5f, max - min);
            return true;
        }
    }

    /// <summary>
    /// 恐龙撞上生成物时补一点水平速度，让「推一下」的反馈更明确。
    /// 系数 0 时不会挂这个组件，完全靠物理接触。
    /// </summary>
    [DisallowMultipleComponent]
    public class GeneratedObjectPushRelay : MonoBehaviour
    {
        private Rigidbody _body;
        private float _factor;
        private float _nextLogTime;

        public void Init(Rigidbody body, float factor)
        {
            _body = body;
            _factor = factor;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_body == null) return;

            // 只对运动学刚体（恐龙推手 / 测试推手）生效；地面等静态碰撞体不额外加力
            Rigidbody other = collision.rigidbody;
            if (other == null || !other.isKinematic) return;

            Vector3 dir = transform.position - collision.transform.position;
            dir.y = 0f;
            dir.z = 0f;   // 横版只沿 X 轴推，避免恐龙 z=0 而物件 z=-1.5 时每次碰撞都把物件推向房子
            if (dir.sqrMagnitude < 0.0001f) return;

            _body.AddForce(dir.normalized * _factor, ForceMode.VelocityChange);
            _body.WakeUp();

            // 验证 Touch 的直接证据：谁碰到了、往哪推、额外速度多少
            if (Time.time >= _nextLogTime)
            {
                _nextLogTime = Time.time + 0.5f;
                Debug.Log($"[PrototypeC] Touch 生效：{collision.collider.name} 碰到 {name}｜推力 {_factor:0.00} m/s｜" +
                          $"方向 ({dir.normalized.x:0.00}, {dir.normalized.z:0.00})");
            }
        }
    }

    /// <summary>
    /// DinoAgentB1 每帧直接写 transform.position / localScale（不是 MovePosition），
    /// 这种「瞬移」要等到下一次物理步才进 PhysX，贴边或快速蹦跳时会漏掉接触。
    /// 这里在 LateUpdate（所有移动与动画逻辑之后）发现位移/缩放变化就 Physics.SyncTransforms()，
    /// 不接管 transform，也不改恐龙的状态机、活动范围与动画时序。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class DinoBodyContactSync : MonoBehaviour
    {
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastScale;

        private void OnEnable() => Capture();

        private void LateUpdate()
        {
            if (transform.position == _lastPosition &&
                transform.rotation == _lastRotation &&
                transform.localScale == _lastScale)
            {
                return;
            }

            Capture();
            Physics.SyncTransforms();
        }

        private void Capture()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;
        }
    }
}
