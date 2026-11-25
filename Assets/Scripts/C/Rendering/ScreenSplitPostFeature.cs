using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace PrototypeC.Rendering
{
    /// <summary>
    /// 单相机全屏分屏后处理：屏幕分界线上下的画面分别走两套调色参数。
    /// 挂在 Renderer2D 数据资产（URP_Renderer）的 Renderer Features 里使用。
    /// </summary>
    public class ScreenSplitPostFeature : ScriptableRendererFeature
    {
        [SerializeField] Material m_Material;

        [Header("分屏")]
        [Range(0f, 1f)] public float splitY = 0.58f;
        [Range(0f, 0.2f)] public float feather = 0.06f;

        [Header("下层（房间）")]
        [Range(-100f, 100f)] public float lowerTemperature = 0f;
        [Range(-100f, 100f)] public float lowerTint = 0f;
        public Color lowerLift = new Color(0f, 0f, 0f, 1f);
        public Color lowerGamma = new Color(0.986f, 1f, 0.988f, 1f);
        public Color lowerGain = new Color(1f, 1f, 1f, 1f);
        public Color lowerShadowTint = new Color(0.3836f, 0.3836f, 0.3836f, 1f);
        public Color lowerHighlightTint = new Color(0.5205f, 0.5824f, 0.5849f, 1f);
        [Range(0f, 2f)] public float lowerSaturation = 1f;
        [Range(0f, 2f)] public float lowerContrast = 1f;
        [Range(0f, 4f)] public float lowerBloom = 1f;

        [Header("上层（天空 / 手绘淡彩）")]
        [Range(-100f, 100f)] public float upperTemperature = 10f;
        [Range(-100f, 100f)] public float upperTint = 0f;
        public Color upperLift = new Color(0.03f, 0.035f, 0.04f, 1f);
        public Color upperGamma = new Color(1f, 1f, 1f, 1f);
        public Color upperGain = new Color(1f, 1f, 1f, 1f);
        public Color upperShadowTint = new Color(0.56f, 0.47f, 0.40f, 1f);
        public Color upperHighlightTint = new Color(0.98f, 0.96f, 0.90f, 1f);
        [Range(0f, 2f)] public float upperSaturation = 0.94f;
        [Range(0f, 2f)] public float upperContrast = 0.84f;
        [Range(0f, 4f)] public float upperBloom = 0.35f;

        [Header("上层手绘化")]
        [Range(0f, 0.3f)] public float upperShadowLift = 0.06f;
        [Range(0f, 0.3f)] public float upperPaperGrain = 0.05f;
        [Range(0.1f, 10f)] public float upperPaperScale = 2.5f;
        [Range(0f, 1f)] public float upperInkStrength = 0.30f;

        [Header("辉光（上下共用）")]
        [Range(0f, 4f)] public float bloomThreshold = 0.6f;
        [Range(0f, 0.05f)] public float bloomRadius = 0.01f;

        SplitPostPass m_Pass;

        public override void Create()
        {
            m_Pass = new SplitPostPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Material == null)
                return;

            if (renderingData.cameraData.camera.cameraType != UnityEngine.CameraType.Game)
                return;

            PushToMaterial();
            m_Pass.Setup(m_Material);
            renderer.EnqueuePass(m_Pass);
        }

        void PushToMaterial()
        {
            var m = m_Material;
            m.SetFloat("_SplitY", splitY);
            m.SetFloat("_Feather", feather);

            m.SetFloat("_LTemperature", lowerTemperature);
            m.SetFloat("_LTint", lowerTint);
            m.SetColor("_LLift", lowerLift);
            m.SetColor("_LGamma", lowerGamma);
            m.SetColor("_LGain", lowerGain);
            m.SetColor("_LShadowTint", lowerShadowTint);
            m.SetColor("_LHighlightTint", lowerHighlightTint);
            m.SetFloat("_LSaturation", lowerSaturation);
            m.SetFloat("_LContrast", lowerContrast);
            m.SetFloat("_LBloom", lowerBloom);

            m.SetFloat("_UTemperature", upperTemperature);
            m.SetFloat("_UTint", upperTint);
            m.SetColor("_ULift", upperLift);
            m.SetColor("_UGamma", upperGamma);
            m.SetColor("_UGain", upperGain);
            m.SetColor("_UShadowTint", upperShadowTint);
            m.SetColor("_UHighlightTint", upperHighlightTint);
            m.SetFloat("_USaturation", upperSaturation);
            m.SetFloat("_UContrast", upperContrast);
            m.SetFloat("_UBloom", upperBloom);
            m.SetFloat("_UShadowLift", upperShadowLift);
            m.SetFloat("_UPaperGrain", upperPaperGrain);
            m.SetFloat("_UPaperScale", upperPaperScale);
            m.SetFloat("_UInkStrength", upperInkStrength);

            m.SetFloat("_BloomThreshold", bloomThreshold);
            m.SetFloat("_BloomRadius", bloomRadius);
        }

        class SplitPostPass : ScriptableRenderPass
        {
            Material m_Material;

            public void Setup(Material material)
            {
                m_Material = material;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                if (resourceData == null || !resourceData.cameraColor.IsValid())
                    return;

                // 颜色目标已经是 back buffer 时没法读写同一张图，直接跳过
                if (resourceData.isActiveTargetBackBuffer)
                    return;

                var source = resourceData.activeColorTexture;

                var desc = renderGraph.GetTextureDesc(resourceData.cameraColor);
                desc.name = "_ScreenSplitColor";
                desc.clearBuffer = false;

                var destination = renderGraph.CreateTexture(desc);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Screen Split Post", out var passData))
                {
                    passData.source = source;
                    passData.material = m_Material;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, 0);
                    });
                }

                // 后续 Pass 与最终 Blit 都以这张图为颜色源
                resourceData.cameraColor = destination;
            }

            class PassData
            {
                public TextureHandle source;
                public Material material;
            }
        }
    }
}
