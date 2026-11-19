using UnityEngine;

/// <summary>Presentation only. Reads the existing agent without changing its movement or tasks.</summary>
[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public sealed class DinoTaskVisual : MonoBehaviour
{
    [SerializeField] private Transform modelPivot;
    [SerializeField] private SpriteRenderer originalSprite;
    [SerializeField] private float turnSpeed = 540f;
    [SerializeField] private float frontYaw = 145f;

    internal MeshFilter CharacterMesh => modelPivot ? modelPivot.GetComponentInChildren<MeshFilter>(true) : null;

    private DinoAgentB1 agent;
    private Renderer[] modelRenderers;
    private MaterialPropertyBlock properties;
    private Vector3 previousPosition;
    private Vector3 baseScale;
    private float facing = 1f;
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

    public void Configure(Transform pivot, SpriteRenderer sprite)
    {
        modelPivot = pivot;
        originalSprite = sprite;
    }

    private void OnEnable()
    {
        agent = GetComponent<DinoAgentB1>();
        if (modelPivot == null || originalSprite == null || agent == null)
            return;

        properties = new MaterialPropertyBlock();
        modelRenderers = modelPivot.GetComponentsInChildren<Renderer>(true);
        previousPosition = transform.position;
        baseScale = modelPivot.localScale;
        baseScale.x = Mathf.Abs(baseScale.x);
        originalSprite.enabled = false;
        modelPivot.gameObject.SetActive(true);
    }

    private void LateUpdate()
    {
        if (modelRenderers == null || modelPivot == null) return;
        if (!agent.isActiveAndEnabled)
        {
            modelPivot.gameObject.SetActive(false);
            originalSprite.enabled = true;
            previousPosition = transform.position;
            return;
        }
        modelPivot.gameObject.SetActive(true);
        originalSprite.enabled = false;
        float dx = transform.position.x - previousPosition.x;
        bool moving = Mathf.Abs(dx) > 0.0001f;
        if (moving) facing = Mathf.Sign(dx);

        // The existing agent mirrors its root to face left. Cancel that reflection
        // on the mesh only, then rotate in 3D. The agent still owns root squash/hops.
        float mirror = transform.localScale.x < 0f ? -1f : 1f;
        float yaw = moving ? facing * 90f : frontYaw;
        float lean = 0f;
        float height = 1f;
        var state = agent.CurrentState;
        switch (state)
        {
            case DinoAgentB1.State.Collecting:
                if (!moving)
                {
                    yaw = -facing * 25f; // Back toward the viewer while reaching down.
                    lean = -18f;
                    height = 0.92f;
                }
                break;
            case DinoAgentB1.State.Building:
                yaw = -facing * 35f; // Carrying/building: expose the back and tail.
                lean = -10f;
                break;
            case DinoAgentB1.State.Resting:
                yaw = frontYaw;
                height = 0.82f;
                lean = 8f;
                break;
            case DinoAgentB1.State.Sitting:
                height = moving ? 1f : 0.86f;
                lean = moving ? 0f : 12f;
                break;
            case DinoAgentB1.State.PlayingBall:
                yaw = facing * 90f;
                lean = -8f;
                break;
            case DinoAgentB1.State.Fleeing:
                yaw = facing * 90f;
                lean = -14f;
                break;
        }

        var encounter = agent.ActiveEncounter;
        if ((encounter is FireEncounter || encounter is SeedEncounter) && !encounter.IsGameOver)
        {
            yaw = encounter.transform.position.x >= transform.position.x ? 90f : -90f;
            lean = 0f;
            height = 1f;
        }

        // Bend relative to the character's own forward direction, not world axes.
        Quaternion target = Quaternion.Euler(0f, yaw * mirror, 0f) * Quaternion.Euler(lean, 0f, 0f);
        modelPivot.localRotation = Quaternion.RotateTowards(modelPivot.localRotation, target,
            turnSpeed * Time.deltaTime);
        Vector3 targetScale = new Vector3(baseScale.x, baseScale.y * height, baseScale.z);
        Vector3 scale = modelPivot.localScale;
        scale.x = Mathf.Abs(scale.x);
        scale = Vector3.Lerp(scale, targetScale, 1f - Mathf.Exp(-12f * Time.deltaTime));
        scale.x *= mirror;
        modelPivot.localScale = scale;

        // Preserve existing hit flashes and draw order while the sprite is hidden.
        properties.SetColor(BaseColor, originalSprite.color);
        foreach (Renderer mesh in modelRenderers)
        {
            mesh.sortingLayerID = originalSprite.sortingLayerID;
            mesh.sortingOrder = originalSprite.sortingOrder;
            mesh.SetPropertyBlock(properties);
        }
        previousPosition = transform.position;
    }

    private void OnDisable()
    {
        if (originalSprite != null && modelRenderers != null)
            originalSprite.enabled = true;
        if (modelPivot != null)
        {
            if (modelRenderers != null) modelPivot.localScale = baseScale;
            modelPivot.gameObject.SetActive(false);
        }
    }
}
