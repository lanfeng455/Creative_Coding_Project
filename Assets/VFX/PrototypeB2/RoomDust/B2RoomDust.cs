using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(B2RoomLanding))]
public sealed class B2RoomDust : MonoBehaviour
{
    [SerializeField] private Texture2D sheet;

    private B2RoomLanding _landing;
    private Sprite[] _frames;

    private void Awake()
    {
        _landing = GetComponent<B2RoomLanding>();
        // PVFX Foundry Dust Puff, Strict Grid. The pivot anchors the dust to the floor.
        _frames = B2SpriteOneShot.Slice(sheet, 72, 40, 14, new Vector2(0.5f, 7f / 40f));
    }

    private void OnEnable()
    {
        if (_frames != null) _landing.Landed += OnRoomLanded;
    }

    private void OnDisable() => _landing.Landed -= OnRoomLanded;

    private void OnRoomLanded(Vector3 foot, float width, string material)
    {
        Color dust = material == "STONE"
            ? new Color(0.75f, 0.90f, 1f)
            : new Color(1f, 0.91f, 0.81f);

        B2SpriteOneShot.Play(this, _frames, "Room Landing Dust", foot,
            width * 1.15f, 0.07f, dust, 2);
    }

    private void OnDestroy() => B2SpriteOneShot.Release(_frames);
}
