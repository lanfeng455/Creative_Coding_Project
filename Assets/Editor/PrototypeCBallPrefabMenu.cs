// PrototypeCBallPrefabMenu.cs —— Prototype C：编辑器辅助菜单
// 只做"生成预制体 / 给选中对象挂组件"这类用户手动动作，不改场景文件、不启动流程。
// 菜单：Tools/Prototype C/…

using System.IO;
using UnityEditor;
using UnityEngine;

public static class PrototypeCBallPrefabMenu
{
    private const string BallModelPath = "Assets/Art/Model/ball.glb";
    private const string PrefabDir = "Assets/Prefabs";
    private const string BallPrefabPath = PrefabDir + "/PrototypeC_Ball.prefab";

    [MenuItem("Tools/Prototype C/创建球预制体")]
    public static void CreateBallPrefab()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(BallModelPath);
        if (model == null)
        {
            Debug.LogError($"[PrototypeC] 找不到球模型：{BallModelPath}");
            return;
        }

        if (!Directory.Exists(PrefabDir)) Directory.CreateDirectory(PrefabDir);

        GameObject instance = PrefabUtility.InstantiatePrefab(model) as GameObject;
        if (instance == null)
        {
            Debug.LogError("[PrototypeC] 实例化 ball.glb 失败");
            return;
        }

        try
        {
            if (instance.GetComponent<BallFormController>() == null)
                instance.AddComponent<BallFormController>();

            var saved = PrefabUtility.SaveAsPrefabAsset(instance, BallPrefabPath);
            if (saved != null)
            {
                Debug.Log($"[PrototypeC] 球预制体已创建：{BallPrefabPath}（已挂 BallFormController）");
                Selection.activeObject = saved;
                EditorGUIUtility.PingObject(saved);
            }
            else Debug.LogError("[PrototypeC] 保存球预制体失败");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    [MenuItem("Tools/Prototype C/为选中的篮筐添加投入检测")]
    public static void AddBasketHoop()
    {
        var target = Selection.activeGameObject;
        if (target == null)
        {
            Debug.LogWarning("[PrototypeC] 先在 Hierarchy 里选中篮筐（名字可能是 篮筐 / model）");
            return;
        }

        if (target.GetComponent<BasketHoop>() != null)
        {
            Debug.Log($"[PrototypeC] {target.name} 已经有 BasketHoop，跳过");
            return;
        }

        Undo.AddComponent<BasketHoop>(target);
        Undo.RecordObject(target, "Add BasketHoop");
        EditorUtility.SetDirty(target);
        Debug.Log($"[PrototypeC] 已给 {target.name} 添加 BasketHoop：接下来在 Inspector 里调 rimLocalOffset / rimHalfWidth 对准筐口");
    }

    [MenuItem("Tools/Prototype C/在场景中添加泡泡奖励")]
    public static void AddBubbleReward()
    {
        var existing = Object.FindObjectOfType<BubbleReward>();
        if (existing != null)
        {
            Selection.activeGameObject = existing.gameObject;
            Debug.Log($"[PrototypeC] 场景里已有泡泡奖励：{existing.name}");
            return;
        }

        var go = new GameObject("BubbleReward");
        Undo.RegisterCreatedObjectUndo(go, "Add BubbleReward");
        var bubble = go.AddComponent<BubbleReward>();
        bubble.mainCamera = Camera.main;
        Selection.activeGameObject = go;
        Debug.Log("[PrototypeC] 已创建 BubbleReward：把篮筐的 BasketHoop.bubbleReward 指向它");
    }
}
