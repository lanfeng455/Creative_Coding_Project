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

        [Header("Top to bottom appearance")]
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
        private readonly Vector3[] corners = new Vector3[4];
        private Material instanceMaterial;
        private RectTransform rootRect;
        private RectTransform divider;
        private Canvas dividerCanvas;
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

        [ContextMenu("Replay Top Down Flame")]
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
            float elapsed = Mathf.Max(0f, Time.time - startTime);
            float progress = Mathf.Clamp01((elapsed - startDelay) / Mathf.Max(0.1f, revealDuration));
            RevealProgress = Mathf.SmoothStep(0f, 1f, progress);
            instanceMaterial.SetFloat(FlowTimeId, elapsed * flowSpeed);
            instanceMaterial.SetFloat(RevealId, RevealProgress);
            instanceMaterial.SetFloat(OpacityId, opacity);
            Rect r = fireImage.rectTransform.rect;
            instanceMaterial.SetFloat(AspectId, r.width / Mathf.Max(1f, r.height));
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
