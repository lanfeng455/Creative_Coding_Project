// TypewriterBackdrop.cs —— Prototype C：给打字机区域加一块「与背景同色」的不透明遮挡底板
// 背景：打字机不是 Screen Space Overlay（文字是 z≈0 的 3D TextMeshPro，日文雨在 World Space Canvas z=5），
// 所以 3D 建筑的地基会「透」进打字机区域。这里在打字机文字与世界之间（默认 z=8）放一块
// 不透明、不受光的四边形，遮住建筑等地基；z=0 的文字仍在它前面，不受影响。
//
// 颜色策略（autoMatchBackground = 开，默认）：
//   本场景相机是 Skybox 清屏 + 程序化天空盒，天空是「垂直渐变」（上蓝下灰），不是纯色。
//   所以底板不能只用一个颜色：用探针相机（cullingMask = 0，只清屏/画天空盒）按底板自己的
//   「顶边 → 底边」逐行采样，生成一张 2×N 的渐变贴图，塞进 _BaseMap，视觉上整块融进天空。
//   - 相机 Clear Flags ≠ Skybox（纯色清屏）时直接用 backgroundColor 平涂，不需要采样；
//   - Play 首帧自动采样一次；编辑器里可右键组件 →「吸色」手动采（编辑器默认不自动采样，避免拖 Inspector 卡顿）。
//   采样的是「后处理前」的背景色：底板和背景在同一相机里会经过同一条后处理链
//   （本场景 Global Volume 有 Bloom / SplitToning / LiftGammaGain，没有 Tonemapping），同色进才能同色出。
//   底板材质是 URP/Unlit：不受光照；颜色/贴图只通过 MaterialPropertyBlock 覆盖，不改材质资产。
//
// 用法：Tools/Prototype C/Rendering/Add Typewriter Backdrop To Open Scene（幂等，不会重建场景）。
// 若某些构件比 z=8 更靠近相机，把 Z 调小（例如 2.5）即可。
//
// 已知取舍：测试面板在 Screen Space - Camera 画布上（平面距离 100，比 z=8 更远），不透明底板会盖住它，
// 所以 Rebuild 会自动把底板底边抬到面板顶边之上。底板不能再往下铺到面板背后，
// 若面板没有铺满整个屏宽，面板两侧仍可能露出一点地基——这是「不挡面板」与「挡住全部地基」的硬冲突。

using System.Collections;
using UnityEngine;

namespace PrototypeC
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TypewriterBackdrop : MonoBehaviour
    {
        private const int GradientRows = 64;

        [Tooltip("底板所在的 z（世界坐标）。必须大于打字机文字（约 0）且小于要遮挡的 3D 物体")]
        public float z = 8f;

        [Tooltip("底板顶边的世界 Y（略高于打字机区域上沿即可）")]
        public float topWorldY = 2.2f;

        [Tooltip("底板底边的世界 Y（覆盖到画面底部下方）")]
        public float bottomWorldY = -5.5f;

        [Tooltip("底板宽度（世界单位）；比屏幕宽一点，避免宽高比变化露边")]
        public float width = 20f;

        [Tooltip("手动颜色：autoMatchBackground 关闭、或相机不是天空盒清屏时使用")]
        public Color color = new Color(0.043f, 0.063f, 0.106f, 1f);

        [Header("与背景同色（推荐）")]
        [Tooltip("开启后底板自动对齐相机背景：纯色清屏 → 取 backgroundColor 平涂；天空盒 → 采样天空渐变贴图")]
        public bool autoMatchBackground = true;

        [Tooltip("自动让开测试面板：底板上沿不变，底边抬到面板顶边之上，避免不透明底板压住输入框/按钮")]
        public bool avoidTestPanel = true;

        [Tooltip("测试面板对象名（PrototypeC 挂载工具创建的节点）")]
        public string testPanelName = "PrototypeC_TestPanel";

        [Tooltip("底板底边与面板顶边之间留的余量（世界单位）")]
        public float panelMargin = 0.08f;

        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;

        // 天空渐变贴图（运行时生成，不序列化）
        [System.NonSerialized] private Texture2D _gradient;
        [System.NonSerialized] private float _gradientTopY;
        [System.NonSerialized] private float _gradientBottomY;
        [System.NonSerialized] private bool _gradientValid;
        [System.NonSerialized] private bool _allowEditModeSample;

        private void OnEnable() => Rebuild();

        // OnValidate / OnEnable 里都不允许 Camera.Render（会抛「Camera rendering during OnValidate is not allowed」），
        // 所以这两处只更新几何与「不需要渲染」的颜色（天空盒用 _SkyTint 近似，或复用已采样好的缓存），
        // 真正的天空采样延后到运行时首帧（SampleAfterFirstFrame）或用户右键「吸色」。
        private void OnValidate() => Rebuild();

        private void Start()
        {
            // Start 在 ExecuteAlways 下编辑模式也会被调用，这里只在运行时采样
            if (!Application.isPlaying || !autoMatchBackground) return;
            StartCoroutine(SampleAfterFirstFrame());
        }

        private void OnDestroy() => DestroySafe(_gradient);

        private IEnumerator SampleAfterFirstFrame()
        {
            // 等相机至少渲染完一帧：探针拿到的天空才和画面一致
            yield return null;
            yield return new WaitForEndOfFrame();
            SampleBackgroundColor();
        }

        /// <summary>重建几何与上色。allowSample = false 时绝不渲染（OnEnable / OnValidate 走这条）。</summary>
        private void Rebuild(bool allowSample = false)
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();

            var filter = GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh == null)
            {
                filter.sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            }

            if (_renderer != null)
            {
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                if (_renderer.sharedMaterial == null && Application.isPlaying)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader != null)
                    {
                        var material = new Material(shader) { name = "TypewriterBackdrop" };
                        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
                        _renderer.sharedMaterial = material;
                    }
                }
            }

            float bottom = bottomWorldY;
            if (avoidTestPanel)
            {
                float panelTopY = GetTestPanelTopWorldY();
                if (!float.IsNegativeInfinity(panelTopY))
                {
                    // 底板上沿不动，只把底边抬到面板顶边之上
                    bottom = Mathf.Max(bottom, panelTopY + panelMargin);
                }
            }

            // 防止面板顶边高过底板上沿导致底板塌成一条线
            bottom = Mathf.Min(bottom, topWorldY - 0.01f);

            float height = Mathf.Max(0.01f, topWorldY - bottom);
            transform.position = new Vector3(0f, (topWorldY + bottom) * 0.5f, z);
            transform.rotation = Quaternion.identity;
            transform.localScale = new Vector3(width, height, 1f);

            // 先定好几何再上色：渐变要按最终的顶边/底边采样
            if (_renderer != null)
            {
                if (_block == null) _block = new MaterialPropertyBlock();
                ApplyColor(bottom, allowSample);
            }
        }

        private void ApplyColor(float bottomWorldY_, bool allowSample)
        {
            if (_renderer == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();

            Camera camera = ResolveCamera();
            bool skybox = camera != null && camera.clearFlags == CameraClearFlags.Skybox;

            // 渐变只有两种来源：已经采好的缓存（不需要渲染），或者当前上下文允许采样时新采一张
            bool wantGradient = false;
            if (autoMatchBackground && skybox)
            {
                if (HasCachedGradient(topWorldY, bottomWorldY_)) wantGradient = true;
                else if (allowSample && (_allowEditModeSample || Application.isPlaying))
                    wantGradient = EnsureGradient(camera, topWorldY, bottomWorldY_);
            }

            _renderer.GetPropertyBlock(_block);

            if (wantGradient && _renderer.sharedMaterial != null && _renderer.sharedMaterial.HasProperty("_BaseMap"))
            {
                _block.SetTexture("_BaseMap", _gradient);
                _block.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
                _block.SetColor("_BaseColor", Color.white);
            }
            else
            {
                Color resolved = ResolveFlatColor(camera);
                // 清掉上一次可能残留的渐变贴图，避免切回平涂时还挂着旧纹理
                _block.Clear();
                _block.SetColor("_BaseColor", resolved);
                if (_renderer.sharedMaterial != null && _renderer.sharedMaterial.HasProperty("_Color"))
                    _block.SetColor("_Color", resolved);
            }

            _renderer.SetPropertyBlock(_block);
        }

        private Color ResolveFlatColor(Camera camera)
        {
            if (!autoMatchBackground) return WithAlpha(color);
            if (camera == null) return WithAlpha(color);
            if (camera.clearFlags != CameraClearFlags.Skybox) return WithAlpha(camera.backgroundColor);

            // 天空盒但还没有采样（编辑器里）：用最接近地平线的近似色垫一下
            return WithAlpha(ApproximateSkyColor());
        }

        private static Color WithAlpha(Color c) => new Color(c.r, c.g, c.b, 1f);

        private static Camera ResolveCamera()
        {
            Camera camera = Camera.main;
            if (camera != null) return camera;

            Camera[] cameras = Camera.allCameras;
            return cameras.Length > 0 ? cameras[0] : null;
        }

        // 天空盒是逐像素渐变的，编辑器里没法便宜地取到真实颜色：用天空盒 _SkyTint × _Exposure 近似。
        // 进 Play 首帧会用探针相机精确采样，以采样结果为准。
        private Color ApproximateSkyColor()
        {
            Material skybox = RenderSettings.skybox;
            if (skybox != null && skybox.HasProperty("_SkyTint"))
            {
                Color tint = skybox.GetColor("_SkyTint");
                float exposure = skybox.HasProperty("_Exposure") ? skybox.GetFloat("_Exposure") : 1f;
                return tint * exposure;
            }

            return color;
        }

        /// <summary>采样天空渐变并重新上色（Play 首帧自动调用一次）。</summary>
        private void SampleBackgroundColor()
        {
            _gradientValid = false;
            if (_block == null) _block = new MaterialPropertyBlock();

            Camera camera = ResolveCamera();
            if (camera == null) return;

            // 以最终几何为准重算一次（含面板让位）
            float bottom = bottomWorldY;
            if (avoidTestPanel)
            {
                float panelTopY = GetTestPanelTopWorldY();
                if (!float.IsNegativeInfinity(panelTopY)) bottom = Mathf.Max(bottom, panelTopY + panelMargin);
            }
            bottom = Mathf.Min(bottom, topWorldY - 0.01f);

            _allowEditModeSample = true;
            ApplyColor(bottom, true);
            _allowEditModeSample = false;

            if (!_gradientValid) return;
            Debug.Log($"[PrototypeC] 遮挡底板已吸色：天空渐变 {GradientRows} 行，" +
                      $"顶 #{ColorUtility.ToHtmlStringRGB(_gradient.GetPixel(0, GradientRows - 1))} " +
                      $"底 #{ColorUtility.ToHtmlStringRGB(_gradient.GetPixel(0, 0))}");
        }

        /// <summary>右键组件 →「吸色」：把真实背景色采进底板（编辑器/运行时都能用）。</summary>
        [ContextMenu("吸色：采样相机背景色")]
        public void SampleBackgroundColorMenu() => SampleBackgroundColor();

        // 渐变缓存是否可用（只读缓存，不触发任何渲染）
        private bool HasCachedGradient(float topY, float bottomY)
        {
            return _gradient != null && _gradientValid &&
                   Mathf.Approximately(_gradientTopY, topY) &&
                   Mathf.Approximately(_gradientBottomY, bottomY);
        }

        // 渐变缓存：只有顶边/底边变化才重采，避免拖 Inspector 时反复渲染
        private bool EnsureGradient(Camera camera, float topY, float bottomY)
        {
            if (HasCachedGradient(topY, bottomY)) return true;

            if (!TrySampleSkyGradient(camera, topY, bottomY, out Texture2D texture)) return false;

            DestroySafe(_gradient);
            _gradient = texture;
            _gradientTopY = topY;
            _gradientBottomY = bottomY;
            _gradientValid = true;
            return true;
        }

        /// <summary>
        /// 用隐藏探针相机只渲染「清屏 + 天空盒」（cullingMask = 0），按底板顶边→底边逐行取色，
        /// 生成 2×N 的垂直渐变贴图。关掉后处理是刻意的：底板在主相机里会和背景走同一条后处理链。
        /// </summary>
        private bool TrySampleSkyGradient(Camera camera, float topY, float bottomY, out Texture2D texture)
        {
            texture = null;

            RenderTexture rt = RenderTexture.GetTemporary(2, GradientRows, 24, RenderTextureFormat.ARGB32);
            var probeGo = new GameObject("~BackdropSkyProbe") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                var probe = probeGo.AddComponent<Camera>();
                probe.CopyFrom(camera);
                // CopyFrom 不复制 Transform：天空盒方向跟相机朝向有关，必须摆到和主相机一样的位置/朝向
                probe.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
                probe.cullingMask = 0;                                  // 不画任何场景物体，只剩清屏/天空盒
                probe.clearFlags = CameraClearFlags.Skybox;
                probe.allowHDR = false;
                probe.targetTexture = rt;
                probe.rect = new Rect(0f, 0f, 1f, 1f);
                probe.pixelRect = new Rect(0f, 0f, 2, GradientRows);   // 必须在设好 targetTexture 之后再写
                probe.enabled = false;                                  // 不参与正常渲染，只手动 Render 一次
                probe.Render();

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                var shot = new Texture2D(2, GradientRows, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0f, 0f, 2, GradientRows), 0, 0);
                shot.Apply();
                RenderTexture.active = previous;

                // 世界 Y → 屏幕（视口）Y：探针和主相机投影一致，所以行号可以直接换算
                Vector3 topScreen = camera.WorldToScreenPoint(new Vector3(0f, topY, z));
                Vector3 bottomScreen = camera.WorldToScreenPoint(new Vector3(0f, bottomY, z));
                float topV = Mathf.Clamp01(topScreen.y / Mathf.Max(1f, Screen.height));
                float bottomV = Mathf.Clamp01(bottomScreen.y / Mathf.Max(1f, Screen.height));

                texture = new Texture2D(2, GradientRows, TextureFormat.RGBA32, false)
                {
                    name = "TypewriterBackdropSkyGradient",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };

                var pixels = new Color32[2 * GradientRows];
                for (int row = 0; row < GradientRows; row++)
                {
                    float t = GradientRows == 1 ? 0f : row / (float)(GradientRows - 1);
                    float v = Mathf.Lerp(bottomV, topV, t);
                    int sourceRow = Mathf.Clamp(Mathf.RoundToInt(v * (GradientRows - 1)), 0, GradientRows - 1);
                    Color c = shot.GetPixel(0, sourceRow);
                    c.a = 1f;
                    Color32 c32 = c;
                    pixels[row * 2] = c32;
                    pixels[row * 2 + 1] = c32;
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);

                DestroySafe(shot);
                return true;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                DestroySafe(probeGo);
            }
        }

        private static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }

        // 取测试面板在「底板所在平面」上的世界坐标顶边；找不到面板/相机时返回 -inf 表示不做让位。
        private float GetTestPanelTopWorldY()
        {
            if (string.IsNullOrEmpty(testPanelName)) return float.NegativeInfinity;

            GameObject panel = GameObject.Find(testPanelName);
            if (panel == null) return float.NegativeInfinity;

            var rect = panel.transform as RectTransform;
            if (rect == null) return float.NegativeInfinity;

            Camera camera = null;
            Canvas canvas = panel.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera)
            {
                camera = canvas.worldCamera;
            }

            if (camera == null) camera = Camera.main;
            if (camera == null) return float.NegativeInfinity;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            float topScreenY = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                topScreenY = Mathf.Max(topScreenY, camera.WorldToScreenPoint(corners[i]).y);
            }

            if (float.IsNegativeInfinity(topScreenY)) return float.NegativeInfinity;

            // 面板在 Screen Space - Camera 画布上（平面距离可能远大于底板），先转屏幕再投到底板平面
            float distance = Mathf.Max(0.01f, Mathf.Abs(z - camera.transform.position.z));
            Vector3 world = camera.ScreenToWorldPoint(new Vector3(Screen.width * 0.5f, topScreenY, distance));
            return world.y;
        }
    }
}
