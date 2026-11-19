using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PrototypeC.Rendering
{
    /// <summary>
    /// 上下屏分屏后处理（视口裁切版）。
    /// <para>两个预置相机分别渲染屏幕的上/下部分，各自通过 UniversalAdditionalCameraData.volumeLayerMask
    /// 使用不同的 Volume，从而实现「上屏 / 下屏不同后处理」。</para>
    /// <para>做法：相机 <see cref="Camera.rect"/> 指定视口，再用正交尺寸 + 相机纵向偏移把画面「裁」成对应区域。
    /// 不使用自定义投影矩阵，避免破坏 Unity/URP 的深度与 Y 轴约定（那会让场景物体整片消失）。</para>
    /// <para>运行时只修改预置相机参数，不创建任何 GameObject / RenderTexture / 材质。</para>
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ScreenSplitVolumeCompositor : MonoBehaviour
    {
        [Header("场景引用（预置相机）")]
        [Tooltip("主相机（本组件所在相机）：只画 UI 层，最后渲染，覆盖在分屏画面之上。")]
        public Camera mainCamera;

        [Tooltip("下屏相机：显示整屏画面的下方部分，VolumeLayerMask 指向 VolumeLower 所在 Layer。")]
        public Camera lowerCamera;

        [Tooltip("上屏相机：显示整屏画面的上方部分，VolumeLayerMask 指向 VolumeUpper 所在 Layer。")]
        public Camera upperCamera;

        [Header("分屏")]
        [Range(0.05f, 0.95f)]
        [Tooltip("下屏占屏幕高度的比例（0=全上屏，1=全下屏）")]
        public float lowerScreenRatio = 0.58f;

        private int _mainCullingMask;
        private CameraClearFlags _mainClearFlags;

        void Awake()
        {
            if (mainCamera == null) mainCamera = GetComponent<Camera>();
            if (mainCamera == null || lowerCamera == null || upperCamera == null)
            {
                Debug.LogError("[ScreenSplit] 相机引用不完整：请在 Main Camera 的 ScreenSplitVolumeCompositor 上绑定 " +
                               "Main / Lower / Upper 三个相机。", this);
                enabled = false;
                return;
            }

            _mainCullingMask = mainCamera.cullingMask;
            _mainClearFlags = mainCamera.clearFlags;

            // ScreenSpaceCamera 模式的 Canvas 由主相机渲染，所以主相机必须保留 UI 层。
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5;
            int uiMask = 1 << uiLayer;

            // 主相机：只画 UI（场景交给两个分屏相机），不清颜色（保留分屏画面），最后渲染。
            // 注意：URP 里 CameraClearFlags.Depth 仍会清颜色，必须用 Nothing 才能保留画面。
            mainCamera.cullingMask = uiMask;
            mainCamera.clearFlags = CameraClearFlags.Nothing;
            mainCamera.allowMSAA = false;
            mainCamera.depth = 0;
            mainCamera.rect = new Rect(0f, 0f, 1f, 1f);
            var mainData = mainCamera.GetUniversalAdditionalCameraData();
            if (mainData != null) mainData.renderPostProcessing = false;

            // 分屏相机只画场景，排除 UI 层，避免把 UI 重复画进分屏视口。
            int sceneMask = _mainCullingMask & ~uiMask;
            ConfigureSplitCamera(lowerCamera, sceneMask, -2);
            ConfigureSplitCamera(upperCamera, sceneMask, -1);

            ApplySplit();
        }

        void LateUpdate()
        {
            if (mainCamera == null) return;
            ApplySplit();
        }

        void OnDisable()
        {
            if (mainCamera == null) return;
            mainCamera.cullingMask = _mainCullingMask;
            mainCamera.clearFlags = _mainClearFlags;
        }

        private void ConfigureSplitCamera(Camera cam, int cullingMask, int depth)
        {
            cam.cullingMask = cullingMask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = mainCamera.backgroundColor;
            cam.depth = depth;
            cam.targetTexture = null;
            cam.rect = new Rect(0f, 0f, 1f, 1f);

            // 同步镜头参数；顺带清掉可能残留的自定义投影矩阵。
            cam.orthographic = mainCamera.orthographic;
            cam.fieldOfView = mainCamera.fieldOfView;
            cam.nearClipPlane = mainCamera.nearClipPlane;
            cam.farClipPlane = mainCamera.farClipPlane;
            cam.ResetProjectionMatrix();
        }

        /// <summary>
        /// 把整屏画面按 ratio 切成上下两块：
        /// 视口 rect 决定显示在哪里，orthographicSize 决定这块视口看到的高度，纵向偏移决定看到画面的哪一段。
        /// 三者配合后，上下两块像素尺寸一致、无缝拼接，且不产生拉伸。
        /// </summary>
        private void ApplySplit()
        {
            if (lowerCamera == null || upperCamera == null) return;

            float ratio = Mathf.Clamp(lowerScreenRatio, 0.01f, 0.99f);

            lowerCamera.rect = new Rect(0f, 0f, 1f, ratio);
            upperCamera.rect = new Rect(0f, ratio, 1f, 1f - ratio);

            lowerCamera.orthographic = mainCamera.orthographic;
            upperCamera.orthographic = mainCamera.orthographic;

            if (!mainCamera.orthographic) return;

            float halfH = mainCamera.orthographicSize;   // 整屏半高（世界单位）

            // 视口只占 ratio 高度，相机可视高度也随之缩小，保证像素比例与整屏一致。
            lowerCamera.orthographicSize = halfH * ratio;
            upperCamera.orthographicSize = halfH * (1f - ratio);

            // 纵向偏移：下屏看向画面下半段，上屏看向画面上半段。
            // 相机是主相机的子物体，用 localPosition 让它随主相机移动/旋转。
            lowerCamera.transform.localPosition = new Vector3(0f, -halfH * (1f - ratio), 0f);
            upperCamera.transform.localPosition = new Vector3(0f, halfH * ratio, 0f);
        }
    }
}
