using UnityEngine;
using UnityEngine.SceneManagement;

namespace Focus.FlowFire
{
    // Additive runtime entry point: no edits or dependencies on gameplay scripts.
    internal static class FlowFireBootstrap
    {
        private const string SceneName = "TemplateA";
        private const string PrefabPath = "FocusFlowFire/UpperFireOverlay";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallInitialScene()
        {
            Install(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Install(scene);
        }

        private static void Install(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.name != SceneName)
                return;

            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.GetComponent<FlowFireReveal>() != null)
                    return;

            GameObject prefab = Resources.Load<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[FlowFire] Missing Resources/" + PrefabPath + " prefab.");
                return;
            }

            GameObject overlay = Object.Instantiate(prefab);
            overlay.name = "FlowFire Upper Screen (independent)";
            SceneManager.MoveGameObjectToScene(overlay, scene);
        }
    }
}
