using UnityEngine;
using UnityEngine.SceneManagement;

namespace Focus.FlowWater
{
    internal static class FlowWaterBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallInitial() => Install(SceneManager.GetActiveScene());

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Install(scene);

        private static void Install(Scene scene)
        {
            if(!scene.IsValid() || !scene.isLoaded || scene.name!="TemplateA") return;
            foreach(GameObject root in scene.GetRootGameObjects())
                if(root.GetComponent<FlowWaterPreview>()!=null) return;
            GameObject prefab=Resources.Load<GameObject>("FocusFlowWater/UpperWaterPreview");
            if(prefab==null) return;
            GameObject overlay=Object.Instantiate(prefab);
            overlay.name="FlowWater Preview (independent)";
            SceneManager.MoveGameObjectToScene(overlay,scene);
            overlay.AddComponent<WaterWordEncounter>();
        }
    }
}
