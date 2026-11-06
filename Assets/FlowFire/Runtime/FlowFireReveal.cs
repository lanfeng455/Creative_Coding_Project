using UnityEngine;
using UnityEngine.UI;

namespace Focus.FlowFire
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Canvas))]
    public sealed class FlowFireReveal : MonoBehaviour
    {
        [Header("Independent effect")]
        [SerializeField] private RawImage fireImage;
        [SerializeField] private Material sourceMaterial;

        [Header("Diagonal appearance (from the upper right)")]
        [SerializeField, Min(0f)] private float startDelay = 0.6f;
        [SerializeField, Min(0.1f)] private float revealDuration = 8f;
        [SerializeField, Range(0f, 1f)] private float opacity = 0.96f;
        [SerializeField, Min(0f)] private float flowSpeed = 1f;

        [Header("Read only gameplay boundary")]
        [SerializeField, Range(0f, 1f)] private float fallbackBoundary = 0.55f;

        private static readonly int FlowTimeId = Shader.PropertyToID("_FlowTime");
        private static readonly int RevealId = Shader.PropertyToID("_Reveal");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int AspectId = Shader.PropertyToID("_AspectRatio");
        private static readonly int SplitStartId = Shader.PropertyToID("_SplitStart");
        private static readonly int SplitEndId = Shader.PropertyToID("_SplitEnd");
        private static readonly int SplitFeatherId = Shader.PropertyToID("_SplitFeather");
        private static readonly int SplitSpanId = Shader.PropertyToID("_SplitSpan");
        private static readonly int SplitEnabledId = Shader.PropertyToID("_SplitEnabled");
        private readonly Vector3[] corners = new Vector3[4];
        private Material instanceMaterial;
        private RectTransform rootRect;
        private RectTransform divider;
        private Canvas dividerCanvas;
        private Transform splitStart;
        private Transform splitEnd;
        private float startTime;

        public float RevealProgress { get; private set; }

        private void OnEnable()
        {
            rootRect = (RectTransform)transform;
            if (fireImage == null)
                fireImage = GetComponentInChildren<RawImage>(true);
            if (fireImage == null || sourceMaterial == null)
            {
                Debug.LogError("[FlowFire] Assign the effect image and source material.", this);
                enabled = false;
                return;
            }

            instanceMaterial = new Material(sourceMaterial)
            {
                name = "FlowFire Runtime Material",
                hideFlags = HideFlags.DontSave
            };
            fireImage.material = instanceMaterial;
            fireImage.raycastTarget = false;
            FindDivider();
            RestartReveal();
        }

        // Used only by the prefab creation utility; never changes existing scene data.
        public void Configure(RawImage image, Material material)
        {
            fireImage = image;
            sourceMaterial = material;
        }

        private void FindDivider()
        {
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if (root.name != "Canvas") continue;
                Transform candidate = root.transform.Find("Image");
                if (candidate == null || candidate.GetComponent<UnityEngine.UI.Image>() == null)
                    continue;
                divider = candidate as RectTransform;
                dividerCanvas = root.GetComponent<Canvas>();
                break;
            }
        }

        [ContextMenu("Replay Diagonal Flame")]
        public void RestartReveal()
        {
            startTime = Time.time;
            RevealProgress = 0f;
            if (instanceMaterial != null)
                instanceMaterial.SetFloat(RevealId, 0f);
        }

        private void LateUpdate()
        {
            if (instanceMaterial == null) return;
            LayoutUpperArea();
            if (splitStart == null || splitEnd == null) FindSplitLine();
            ApplySplitClip();
            float elapsed = Mathf.Max(0f, Time.time - startTime);
            float progress = Mathf.Clamp01((elapsed - startDelay) / Mathf.Max(0.1f, revealDuration));
            RevealProgress = Mathf.SmoothStep(0f, 1f, progress);
            instanceMaterial.SetFloat(FlowTimeId, elapsed * flowSpeed);
            instanceMaterial.SetFloat(RevealId, RevealProgress);
            instanceMaterial.SetFloat(OpacityId, opacity);
            Rect r = fireImage.rectTransform.rect;
            instanceMaterial.SetFloat(AspectId, r.width / Mathf.Max(1f, r.height));
        }

        // The 45-degree boundary is authored by the gameplay bar. Only its two
        // endpoint markers are read here, so the fire module stays decoupled.
        private void FindSplitLine()
        {
            splitStart = null;
            splitEnd = null;
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if (root.name != "Canvas") continue;
                Transform rig = root.transform.Find("UpperSplitRig");
                if (rig == null) continue;
                splitStart = rig.Find("UpperSplitStart");
                splitEnd = rig.Find("UpperSplitEnd");
                break;
            }
        }

        private void ApplySplitClip()
        {
            bool hasLine = splitStart != null && splitEnd != null;
            instanceMaterial.SetFloat(SplitEnabledId, hasLine ? 1f : 0f);
            if (!hasLine) return;

            RectTransform rectTransform = fireImage.rectTransform;
            Rect r = rectTransform.rect;
            Vector2 start = rectTransform.InverseTransformPoint(splitStart.position);
            Vector2 end = rectTransform.InverseTransformPoint(splitEnd.position);
            Vector2 split = end - start;
            float length = split.magnitude;
            if (length < 0.001f)
            {
                instanceMaterial.SetFloat(SplitEnabledId, 0f);
                return;
            }

            // The shader works in the RawImage's local space (1 unit = 1 reference
            // pixel on both axes), where the normal points into the flame wedge.
            Vector2 dir = split / length;
            Vector2 normal = new Vector2(-dir.y, dir.x);
            float span = Mathf.Max(1f, Mathf.Max(
                Mathf.Max(Vector2.Dot(new Vector2(r.xMin, r.yMin) - start, normal),
                          Vector2.Dot(new Vector2(r.xMax, r.yMin) - start, normal)),
                Mathf.Max(Vector2.Dot(new Vector2(r.xMin, r.yMax) - start, normal),
                          Vector2.Dot(new Vector2(r.xMax, r.yMax) - start, normal))));

            instanceMaterial.SetVector(SplitStartId, new Vector4(start.x, start.y, 0f, 0f));
            instanceMaterial.SetVector(SplitEndId, new Vector4(end.x, end.y, 0f, 0f));
            instanceMaterial.SetFloat(SplitSpanId, span);
            instanceMaterial.SetFloat(SplitFeatherId, 6f);
        }

        private void LayoutUpperArea()
        {
            float boundary = fallbackBoundary;
            if (divider != null)
            {
                divider.GetWorldCorners(corners);
                Camera camera = dividerCanvas != null &&
                    dividerCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? dividerCanvas.worldCamera : null;
                float topScreen = float.MinValue;
                foreach (Vector3 corner in corners)
                    topScreen = Mathf.Max(topScreen,
                        RectTransformUtility.WorldToScreenPoint(camera, corner).y);

                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRect,
                    new Vector2(0f, topScreen), null, out Vector2 local))
                    boundary = Mathf.Clamp01((local.y - rootRect.rect.yMin) /
                        Mathf.Max(1f, rootRect.rect.height));
            }

            RectTransform rect = fireImage.rectTransform;
            rect.anchorMin = new Vector2(0f, boundary);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void OnDisable()
        {
            if (fireImage != null && instanceMaterial != null)
                fireImage.material = sourceMaterial;
            if (instanceMaterial != null)
            {
                if (Application.isPlaying) Destroy(instanceMaterial);
                else DestroyImmediate(instanceMaterial);
                instanceMaterial = null;
            }
        }
    }
}
