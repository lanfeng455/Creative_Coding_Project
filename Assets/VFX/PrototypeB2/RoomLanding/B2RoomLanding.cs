using System;
using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(B1LayerConfig))]
public sealed class B2RoomLanding : MonoBehaviour
{
    public event Action<Vector3, float, string> Landed;

    private B1LayerConfig _world;

    private void Awake() => _world = GetComponent<B1LayerConfig>();

    private void OnEnable() => _world.RoomBuilt += OnRoomBuilt;

    private void OnDisable() => _world.RoomBuilt -= OnRoomBuilt;

    private void OnRoomBuilt(Transform room, float width, float height, string material)
    {
        StartCoroutine(SettleRoom(room, width, height, material));
    }

    private IEnumerator SettleRoom(Transform room, float width, float height, string material)
    {
        Vector3 center = room.position;
        Vector3 lifted = center + Vector3.up * (height * 0.18f);
        const float dropDuration = 0.14f;
        for (float elapsed = 0f; elapsed < dropDuration; elapsed += Time.deltaTime)
        {
            if (room == null) yield break;
            float progress = elapsed / dropDuration;
            room.position = Vector3.Lerp(lifted, center, progress * progress);
            yield return null;
        }

        if (room == null) yield break;
        Landed?.Invoke(center - Vector3.up * (height * 0.5f), width, material);

        const float settleDuration = 0.16f;
        for (float elapsed = 0f; elapsed < settleDuration; elapsed += Time.deltaTime)
        {
            if (room == null) yield break;
            float squash = 1f - Mathf.SmoothStep(0f, 1f, elapsed / settleDuration);
            room.localScale = new Vector3(1f + 0.06f * squash, 1f - 0.12f * squash, 1f);
            room.position = center - Vector3.up * (height * 0.06f * squash);
            yield return null;
        }

        if (room == null) yield break;
        room.position = center;
        room.localScale = Vector3.one;
    }
}
