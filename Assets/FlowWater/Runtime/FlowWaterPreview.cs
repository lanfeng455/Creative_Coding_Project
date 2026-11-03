using UnityEngine;
using UnityEngine.UI;

namespace Focus.FlowWater
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Canvas))]
    public sealed class FlowWaterPreview : MonoBehaviour
    {
        [Header("Independent water preview")]
        [SerializeField] private RawImage waterImage;
        [SerializeField] private Material surfaceMaterial;
        [SerializeField] private Material silhouetteMaterial;
        [Tooltip("Loop only in the standalone art preview. WATER encounters play once.")]
        [SerializeField] private bool autoLoop=true;

        [Header("Water timing")]
        [SerializeField,Min(0)] private float startDelay=1.5f;
        [Tooltip("Left to right sweep time in both preview and WATER encounters.")]
        [SerializeField,Min(0.1f)] private float sweepDuration=5f;
        [Tooltip("Hold after full coverage. WATER encounters then clear both effects.")]
        [SerializeField,Min(0)] private float holdDuration=2f;
        [SerializeField,Min(0.1f)] private float fadeDuration=1.2f;
        [SerializeField,Min(0)] private float loopPause=1.5f;
        [SerializeField,Range(0,1)] private float opacity=1f;
        [SerializeField,Min(0)] private float rippleSpeed=0.65f;

        [Header("Render quality")]
        [SerializeField,Range(0.25f,1)] private float renderScale=0.65f;
        [SerializeField,Range(64,512)] private int maximumTextureHeight=256;
        [SerializeField,Range(10,60)] private int surfaceFrameRate=30;

        [Header("Read only screen boundary")]
        [SerializeField,Range(0,1)] private float fallbackBoundary=0.55f;

        private Material surfaceInstance, silhouetteInstance;
        private RenderTexture surfaceTexture;
        private RectTransform rootRect, divider;
        private Canvas dividerCanvas;
        private readonly Vector3[] corners=new Vector3[4];
        private float startTime, lastRenderTime=-100f;
        private bool inspectFrame;
        private bool wordTriggeredMode;
        private float triggeredStartTime;
        public enum WordPlaybackState { Preview, WaitingForWord, Sweeping, Holding, Complete }
        public WordPlaybackState PlaybackState { get; private set; }
        public float PreviewProgress { get; private set; }
        public bool FireExtinguished { get; private set; }

        // TemplateA uses this gated mode; the isolated art scene keeps its loop.
        public void WaitForWord()
        {
            wordTriggeredMode=true;
            inspectFrame=false;
            PlaybackState=WordPlaybackState.WaitingForWord;
            HideWater();
        }

        public bool PlayForCompletedWord()
        {
            if(!wordTriggeredMode || PlaybackState!=WordPlaybackState.WaitingForWord)
                return false;
            triggeredStartTime=Time.time;
            PreviewProgress=0f;
            PlaybackState=WordPlaybackState.Sweeping;
            return true;
        }

        private void HideWater()
        {
            if(silhouetteInstance!=null) silhouetteInstance.SetFloat("_Opacity",0f);
        }

        public void Configure(RawImage image,Material surface,Material silhouette)
        {
            waterImage=image;surfaceMaterial=surface;silhouetteMaterial=silhouette;
        }

        private void OnEnable()
        {
            rootRect=(RectTransform)transform;
            if(waterImage==null || surfaceMaterial==null || silhouetteMaterial==null)
            {
                Debug.LogError("[FlowWater] Preview prefab references are incomplete.",this);
                enabled=false;
                return;
            }
            if(surfaceInstance==null)
                surfaceInstance=new Material(surfaceMaterial) {hideFlags=HideFlags.DontSave};
            if(silhouetteInstance==null)
                silhouetteInstance=new Material(silhouetteMaterial) {hideFlags=HideFlags.DontSave};
            waterImage.material=silhouetteInstance;
            waterImage.raycastTarget=false;
            foreach(GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if(root.name!="Canvas") continue;
                Transform candidate=root.transform.Find("Image");
                if(candidate!=null && candidate.GetComponent<UnityEngine.UI.Image>()!=null)
                {
                    divider=candidate as RectTransform;
                    dividerCanvas=root.GetComponent<Canvas>();
                    break;
                }
            }
            RestartPreview();
        }

        [ContextMenu("Replay Water Preview")]
        public void RestartPreview()
        {
            if(wordTriggeredMode)
            {
                // A completed encounter cannot be replayed by existing letters.
                if(PlaybackState==WordPlaybackState.WaitingForWord) HideWater();
                return;
            }
            inspectFrame=false;
            startTime=Time.time;lastRenderTime=-100f;PreviewProgress=0f;
            if(silhouetteInstance!=null)
            {
                silhouetteInstance.SetFloat("_Progress",0);
                silhouetteInstance.SetFloat("_Opacity",0);
            }
        }

        // A paused artistic inspection point, independent of gameplay or letters.
        public void SetInspectionFrame(float progress,float waterTime)
        {
            inspectFrame=true;
            LayoutUpperArea();
            EnsureTexture();
            PreviewProgress=Mathf.Clamp01(progress);
            RenderSurface(waterTime);
            silhouetteInstance.SetFloat("_Progress",PreviewProgress);
            silhouetteInstance.SetFloat("_WaterTime",waterTime);
            silhouetteInstance.SetFloat("_Opacity",opacity);
            if(PreviewProgress>=1f && opacity>0f) ExtinguishCoveredFire();
        }

        private void LateUpdate()
        {
            if(wordTriggeredMode)
            {
                TickWordPlayback();
                return;
            }
            if(inspectFrame) return;
            LayoutUpperArea();
            EnsureTexture();
            float elapsed=Mathf.Max(0,Time.time-startTime-startDelay);
            float cycle=sweepDuration+holdDuration+fadeDuration+loopPause;
            if(autoLoop) elapsed%=Mathf.Max(0.1f,cycle);
            float visible=Time.time-startTime>=startDelay ? 1f:0f;
            PreviewProgress=Mathf.Clamp01(elapsed/Mathf.Max(0.1f,sweepDuration));
            if(elapsed>sweepDuration+holdDuration)
                visible*=1f-Mathf.Clamp01((elapsed-sweepDuration-holdDuration)/Mathf.Max(0.1f,fadeDuration));
            float waterTime=(Time.time-startTime)*rippleSpeed;
            if(visible>0 && Time.time-lastRenderTime>=1f/Mathf.Max(1,surfaceFrameRate))
                RenderSurface(waterTime);
            silhouetteInstance.SetFloat("_Progress",PreviewProgress);
            silhouetteInstance.SetFloat("_WaterTime",waterTime);
            silhouetteInstance.SetFloat("_Opacity",visible*opacity);
            if(PreviewProgress>=1f && visible*opacity>0f) ExtinguishCoveredFire();
        }

        private void TickWordPlayback()
        {
            if(PlaybackState==WordPlaybackState.WaitingForWord ||
                PlaybackState==WordPlaybackState.Complete)
            {
                HideWater();
                return;
            }

            float elapsed=Mathf.Max(0f,Time.time-triggeredStartTime);
            float duration=Mathf.Max(0.1f,sweepDuration);
            if(elapsed>=duration+Mathf.Max(0f,holdDuration))
            {
                ExtinguishCoveredFire();
                PreviewProgress=1f;
                PlaybackState=WordPlaybackState.Complete;
                HideWater();
                return;
            }

            LayoutUpperArea();
            EnsureTexture();
            PreviewProgress=Mathf.Clamp01(elapsed/duration);
            PlaybackState=PreviewProgress>=1f ? WordPlaybackState.Holding : WordPlaybackState.Sweeping;
            float waterTime=elapsed*rippleSpeed;
            if(Time.time-lastRenderTime>=1f/Mathf.Max(1,surfaceFrameRate)) RenderSurface(waterTime);
            silhouetteInstance.SetFloat("_Progress",PreviewProgress);
            silhouetteInstance.SetFloat("_WaterTime",waterTime);
            silhouetteInstance.SetFloat("_Opacity",opacity);
            if(PreviewProgress>=1f) ExtinguishCoveredFire();
        }

        // Change only the runtime fire overlay, never its source asset or scripts.
        // The latch is deliberately not reset by a water preview loop or replay.
        private void ExtinguishCoveredFire()
        {
            if(FireExtinguished) return;
            foreach(GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if(root.name!="FlowFire Upper Screen (independent)" ||
                    root.GetComponent<Canvas>()==null) continue;
                root.SetActive(false);
                FireExtinguished=true;
                break;
            }
        }

        private void LayoutUpperArea()
        {
            if(waterImage==null || rootRect==null) return;
            float boundary=fallbackBoundary;
            if(divider!=null)
            {
                divider.GetWorldCorners(corners);
                Camera camera=dividerCanvas!=null && dividerCanvas.renderMode!=RenderMode.ScreenSpaceOverlay
                    ? dividerCanvas.worldCamera:null;
                float screenY=float.MinValue;
                foreach(Vector3 p in corners)
                    screenY=Mathf.Max(screenY,RectTransformUtility.WorldToScreenPoint(camera,p).y);
                if(RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRect,
                    new Vector2(0,screenY),null,out Vector2 local))
                    boundary=Mathf.Clamp01((local.y-rootRect.rect.yMin)/Mathf.Max(1,rootRect.rect.height));
            }
            RectTransform r=waterImage.rectTransform;
            r.anchorMin=new Vector2(0,boundary);r.anchorMax=Vector2.one;
            r.offsetMin=Vector2.zero;r.offsetMax=Vector2.zero;
        }

        private void EnsureTexture()
        {
            Rect rect=waterImage.rectTransform.rect;
            float aspect=Mathf.Max(0.1f,rect.width/Mathf.Max(1,rect.height));
            surfaceInstance.SetFloat("_AspectRatio",aspect);
            silhouetteInstance.SetFloat("_AspectRatio",aspect);
            float scale=GetComponent<Canvas>().scaleFactor;
            int h=Mathf.Clamp(Mathf.RoundToInt(rect.height*scale*renderScale),64,maximumTextureHeight);
            int w=Mathf.Clamp(Mathf.RoundToInt(h*aspect),64,2048);
            if(surfaceTexture==null || surfaceTexture.width!=w || surfaceTexture.height!=h)
            {
                ReleaseTexture();
                surfaceTexture=new RenderTexture(w,h,0,RenderTextureFormat.ARGB32)
                {
                    name="FlowWater Surface Preview",filterMode=FilterMode.Bilinear,
                    wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.DontSave
                };
                surfaceTexture.Create();waterImage.texture=surfaceTexture;
                lastRenderTime=-100f;
                RenderSurface((Time.time-startTime)*rippleSpeed);
            }
        }

        private void RenderSurface(float time)
        {
            if(surfaceTexture==null || surfaceInstance==null) return;
            surfaceInstance.SetFloat("_WaterTime",time);
            RenderTexture previous=RenderTexture.active;
            try { Graphics.Blit(Texture2D.blackTexture,surfaceTexture,surfaceInstance); }
            finally { RenderTexture.active=previous; }
            lastRenderTime=Time.time;
        }

        private static void Dispose(Object value)
        {
            if(value==null)return;
            if(Application.isPlaying) Destroy(value);else DestroyImmediate(value);
        }
        private void ReleaseTexture()
        {
            if(surfaceTexture==null) return;
            surfaceTexture.Release();Dispose(surfaceTexture);surfaceTexture=null;
        }
        private void OnDestroy()
        {
            ReleaseTexture();
            Dispose(surfaceInstance);Dispose(silhouetteInstance);
        }
    }
}
