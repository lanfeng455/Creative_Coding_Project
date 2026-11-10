using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(B1LayerConfig))]
public sealed class B2LetterSpark : MonoBehaviour
{
    [SerializeField] private Texture2D sheet;

    private B1LayerConfig _world;
    private Sprite[] _frames;

    private void Awake()
    {
        _world = GetComponent<B1LayerConfig>();
        // PVFX Foundry Hit Spark, Strict Grid. Use the five bright frames.
        _frames = B2SpriteOneShot.Slice(sheet, 64, 64, 5, new Vector2(0.5f, 0.5f));
    }

    private void OnEnable()
    {
        if (_frames != null) _world.LetterRecorded += OnLetterRecorded;
    }

    private void OnDisable() => _world.LetterRecorded -= OnLetterRecorded;

    private void OnLetterRecorded(Vector3 position, float letterSize)
    {
        B2SpriteOneShot.Play(this, _frames, "Letter Recorded Spark", position,
            letterSize * 1.35f, 0.045f, Color.white, 4);
    }

    private void OnDestroy() => B2SpriteOneShot.Release(_frames);
}
