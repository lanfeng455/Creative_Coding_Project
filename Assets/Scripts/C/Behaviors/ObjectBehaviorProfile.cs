// ObjectBehaviorProfile.cs —— Prototype C：behavior × placement 单个格子的装配配置
// 一个格子 = 一个预制件 = 一份配置；所有模型共用同一份配置，不再由代码里按 behavior 写死 if 分支。
// 预制件本身可以是空节点，碰撞体 / 刚体 / 晃动反馈都由这里的配置在运行时生成并适配到模型包围盒。

using UnityEngine;

namespace PrototypeC
{
    /// <summary>碰撞体形状：None = 不加碰撞体（纯装饰），Box/Capsule/Sphere 按模型包围盒生成。</summary>
    public enum BehaviorColliderMode
    {
        None,
        Box,
        Capsule,
        Sphere,
    }

    /// <summary>碰撞体尺寸策略：适配包围盒 / 适配包围盒并压薄朝向轴 / 固定尺寸。</summary>
    public enum BehaviorColliderSizing
    {
        FitBounds,
        FitBoundsThinDepth,
        FixedSize,
    }

    /// <summary>刚体形态：Static = 只有静态碰撞体；Dynamic = 受物理模拟；Kinematic = 只跟着 Transform 走但能推动别人。</summary>
    public enum BehaviorBodyMode
    {
        Static,
        Dynamic,
        Kinematic,
    }

    /// <summary>装配时传给附加组件的信息（模型根节点、装配后的局部包围盒、格子标识）。</summary>
    public struct BehaviorAssemblyContext
    {
        public GameObject modelRoot;
        public Bounds localBounds;
        public ObjectBehavior behavior;
        public ObjectPlacement placement;
    }

    /// <summary>预制体上的附加组件实现它，即可在装配完成时拿到模型包围盒与格子信息。</summary>
    public interface IBehaviorAssemblyHook
    {
        void OnBehaviorAssembled(BehaviorAssemblyContext context);
    }

    [DisallowMultipleComponent]
    public class ObjectBehaviorProfile : MonoBehaviour
    {
        [Header("① 格子标识（只用于 Inspector 辨识与日志）")]
        [Tooltip("这一格对应的 behavior；由编辑器菜单生成预制件时自动写入")]
        public ObjectBehavior behavior = ObjectBehavior.Ignore;

        [Tooltip("这一格对应的 placement；由编辑器菜单生成预制件时自动写入")]
        public ObjectPlacement placement = ObjectPlacement.Ground;

        [Header("② 碰撞体")]
        [Tooltip("碰撞体形状：None 不加碰撞体；Box / Capsule / Sphere 按模型包围盒生成")]
        public BehaviorColliderMode colliderMode = BehaviorColliderMode.Box;

        [Tooltip("尺寸策略：FitBounds 贴合模型包围盒；FitBoundsThinDepth 贴合但把朝向轴压到最大厚度；FixedSize 用固定尺寸")]
        public BehaviorColliderSizing colliderSizing = BehaviorColliderSizing.FitBounds;

        [Tooltip("碰撞体尺寸倍率（1 = 与模型包围盒一致，略大于 1 更容易被碰到）")]
        [Range(0.5f, 2f)] public float colliderPadding = 1f;

        [Tooltip("FitBoundsThinDepth 时沿模型局部 Z 的最大厚度（米）：挂墙物件压薄，避免突出一大块")]
        public float colliderMaxThickness = 0.18f;

        [Tooltip("FixedSize 时使用的碰撞体尺寸（米，锚点局部坐标下的盒子）")]
        public Vector3 fixedSize = new Vector3(0.5f, 0.5f, 0.5f);

        [Header("③ 刚体（只有 Dynamic / Kinematic 才创建）")]
        [Tooltip("Static 只有静态碰撞体推不动；Dynamic 受物理模拟；Kinematic 不受物理但能把别人顶开")]
        public BehaviorBodyMode bodyMode = BehaviorBodyMode.Static;

        [Tooltip("质量（kg）：越大越难被推动")]
        public float mass = 2f;

        [Tooltip("线性阻尼：越大越快停下来")]
        public float linearDamping = 1.5f;

        [Tooltip("角阻尼：越大越不容易持续打转")]
        public float angularDamping = 4f;

        [Tooltip("是否受重力；关掉后需要自己锁住位置，否则会飘走")]
        public bool useGravity = true;

        [Tooltip("锁定垂直移动（Y）：勾上后只能被横推、绝不会掉")]
        public bool lockY = false;

        [Tooltip("锁定纵深移动（Z）：勾上后只在左右方向滑动（挂墙物件常用）")]
        public bool lockZ = false;

        [Tooltip("锁定旋转：勾上后推不翻，只会平移")]
        public bool freezeRotation = false;

        [Tooltip("启动即休眠：静置的家具勾上后不会被轻微碰撞推着慢慢滑")]
        public bool startAsleep = false;

        [Header("④ 交互能力")]
        [Tooltip("是否可被推动：开启后恐龙撞上会把它顶开（需要 Dynamic 刚体）")]
        public bool pushable = false;

        [Tooltip("附加推力系数（米/秒）：恐龙碰到时额外给一点水平速度，0 = 只靠物理接触")]
        public float pushFactor = 0f;

        [Tooltip("是否需要隐藏地面托住：动态物件勾上后不会掉出房间（静态物件不需要）")]
        public bool needsSupportFloor = false;

        [Header("⑤ 晃动反馈（挂墙 / 屋顶被碰到时，不参与物理模拟）")]
        [Tooltip("被恐龙碰到时沿法线轻推一下再回弹；只有静态物件可以开启（动态物件用物理弹开）")]
        public bool wobbleOnTouch = false;

        [Tooltip("晃动幅度（米）")]
        public float wobbleAmplitude = 0.05f;

        [Tooltip("一次晃动来回的总时长（秒）")]
        public float wobbleDuration = 0.45f;

        [Tooltip("晃动来回次数（正弦周期数）")]
        [Range(0.5f, 4f)] public float wobbleOscillations = 1.5f;

        [Tooltip("触发范围外扩（米）：恐龙身体进入包围盒外这么近就算碰到")]
        public float wobbleProbePadding = 0.08f;

        /// <summary>按这一格的配置装配到模型根节点上（碰撞体 / 刚体 / 晃动反馈 / 附加组件钩子）。</summary>
        public void Assemble(GameObject modelRoot, Bounds localBounds, ObjectBehavior cellBehavior,
            ObjectPlacement cellPlacement)
        {
            if (modelRoot == null) return;

            AddCollider(modelRoot, localBounds);
            Rigidbody body = AddBody(modelRoot);

            if (wobbleOnTouch)
            {
                if (bodyMode == BehaviorBodyMode.Static)
                {
                    var wobble = modelRoot.GetComponent<WallTouchWobble>();
                    if (wobble == null) wobble = modelRoot.AddComponent<WallTouchWobble>();
                    wobble.Configure(wobbleAmplitude, wobbleDuration, wobbleOscillations, wobbleProbePadding);
                }
                else
                {
                    Debug.LogWarning($"[PrototypeC] 行为装配：{BehaviorCells.Label(cellBehavior, cellPlacement)} 开启了晃动反馈，" +
                                     "但这一格是动态刚体，晃动已跳过（动态物件由物理弹开）");
                }
            }

            if (pushable && bodyMode == BehaviorBodyMode.Dynamic && body != null && pushFactor > 0f)
            {
                var relay = modelRoot.GetComponent<GeneratedObjectPushRelay>();
                if (relay == null) relay = modelRoot.AddComponent<GeneratedObjectPushRelay>();
                relay.Init(body, pushFactor);
            }

            // 预制体上的附加组件：统一在装配完成时拿到模型包围盒与格子信息
            var context = new BehaviorAssemblyContext
            {
                modelRoot = modelRoot,
                localBounds = localBounds,
                behavior = cellBehavior,
                placement = cellPlacement,
            };

            MonoBehaviour[] extras = GetComponents<MonoBehaviour>();
            for (int i = 0; i < extras.Length; i++)
            {
                if (extras[i] is IBehaviorAssemblyHook hook) hook.OnBehaviorAssembled(context);
            }
        }

        private void AddCollider(GameObject modelRoot, Bounds localBounds)
        {
            if (colliderMode == BehaviorColliderMode.None) return;
            if (modelRoot.GetComponent<Collider>() != null) return;

            Vector3 center = localBounds.center;
            Vector3 size = Vector3.Max(ApplySizing(localBounds.size), new Vector3(0.02f, 0.02f, 0.02f));
            size *= Mathf.Max(0.01f, colliderPadding);

            switch (colliderMode)
            {
                case BehaviorColliderMode.Capsule:
                {
                    var capsule = modelRoot.AddComponent<CapsuleCollider>();
                    capsule.direction = 1;
                    capsule.center = center;
                    capsule.height = Mathf.Max(size.y, 0.02f);
                    capsule.radius = Mathf.Max(0.01f, Mathf.Min(size.x, size.z) * 0.5f);
                    break;
                }

                case BehaviorColliderMode.Sphere:
                {
                    var sphere = modelRoot.AddComponent<SphereCollider>();
                    sphere.center = center;
                    sphere.radius = Mathf.Max(0.01f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f);
                    break;
                }

                default:
                {
                    var box = modelRoot.AddComponent<BoxCollider>();
                    box.center = center;
                    box.size = size;
                    break;
                }
            }
        }

        private Vector3 ApplySizing(Vector3 boundsSize)
        {
            switch (colliderSizing)
            {
                case BehaviorColliderSizing.FitBoundsThinDepth:
                    return new Vector3(
                        boundsSize.x,
                        boundsSize.y,
                        Mathf.Min(boundsSize.z, Mathf.Max(0.01f, colliderMaxThickness)));

                case BehaviorColliderSizing.FixedSize:
                    return fixedSize;

                default:
                    return boundsSize;
            }
        }

        private Rigidbody AddBody(GameObject modelRoot)
        {
            if (bodyMode == BehaviorBodyMode.Static) return null;

            Rigidbody body = modelRoot.GetComponent<Rigidbody>();
            if (body == null) body = modelRoot.AddComponent<Rigidbody>();

            body.isKinematic = bodyMode == BehaviorBodyMode.Kinematic;
            body.useGravity = bodyMode == BehaviorBodyMode.Dynamic && useGravity;
            body.mass = Mathf.Max(0.1f, mass);
            body.linearDamping = Mathf.Max(0f, linearDamping);
            body.angularDamping = Mathf.Max(0f, angularDamping);

            RigidbodyConstraints constraints = RigidbodyConstraints.None;
            if (lockY) constraints |= RigidbodyConstraints.FreezePositionY;
            if (lockZ) constraints |= RigidbodyConstraints.FreezePositionZ;
            if (freezeRotation) constraints |= RigidbodyConstraints.FreezeRotation;
            if (bodyMode == BehaviorBodyMode.Kinematic)
            {
                body.useGravity = false;
                constraints = RigidbodyConstraints.FreezeAll;
            }

            body.constraints = constraints;
            if (startAsleep && bodyMode == BehaviorBodyMode.Dynamic) body.Sleep();
            return body;
        }
    }
}
