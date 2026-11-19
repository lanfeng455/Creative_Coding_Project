// PrototypeCBehaviorPrefabMenu.cs —— Prototype C：编辑器辅助菜单（行为装配预制件）
// 只做「生成 9 格预制件 / 回填查表 / 检查缺格」这类用户手动动作，不改场景文件结构。
// 菜单：Tools/Prototype C/…

using System.IO;
using PrototypeC;
using UnityEditor;
using UnityEngine;

public static class PrototypeCBehaviorPrefabMenu
{
    private const string PrefabDir = "Assets/Prefabs/PrototypeC_Behaviors";

    [MenuItem("Tools/Prototype C/生成 9 个行为装配预制件")]
    public static void CreateBehaviorPrefabs()
    {
        EnsureFolder(PrefabDir);

        int created = 0;
        int updated = 0;

        foreach ((ObjectBehavior behavior, ObjectPlacement placement) in BehaviorCells.LegalCells())
        {
            string name = $"Behavior_{behavior}_{placement}";
            string path = $"{PrefabDir}/{name}.prefab";

            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                // 已存在的预制件保留用户调过的参数；只在缺配置组件时补一个默认的
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (contents.GetComponent<ObjectBehaviorProfile>() == null)
                    {
                        var added = contents.AddComponent<ObjectBehaviorProfile>();
                        BehaviorDefaults.Apply(added, behavior, placement);
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        updated++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }

                continue;
            }

            var host = new GameObject(name);
            try
            {
                var profile = host.AddComponent<ObjectBehaviorProfile>();
                BehaviorDefaults.Apply(profile, behavior, placement);
                PrefabUtility.SaveAsPrefabAsset(host, path);
                created++;
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[PrototypeC] 行为装配预制件：新建 {created} 个，补配置 {updated} 个，目录 {PrefabDir}");
        FillTable();
    }

    [MenuItem("Tools/Prototype C/把行为装配预制件回填到场景查表")]
    public static void FillTable()
    {
        ObjectModelService service = Object.FindFirstObjectByType<ObjectModelService>(FindObjectsInactive.Include);
        if (service == null)
        {
            Debug.LogWarning("[PrototypeC] 当前场景里没有 ObjectModelService：先打开 TemplateC_LanMay 场景再回填");
            return;
        }

        BehaviorAssemblyTable table = service.behaviorAssemblies;
        if (table == null)
        {
            table = BehaviorAssemblyTable.CreateDefault();
            service.behaviorAssemblies = table;
        }

        Undo.RecordObject(service, "Fill Behavior Assembly Table");
        table.EnsureComplete();

        int filled = 0;
        int missing = 0;
        foreach ((ObjectBehavior behavior, ObjectPlacement placement) in BehaviorCells.LegalCells())
        {
            string path = $"{PrefabDir}/Behavior_{behavior}_{placement}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                missing++;
                continue;
            }

            table.AssignPrefab(behavior, placement, prefab);
            filled++;
        }

        EditorUtility.SetDirty(service);
        Debug.Log($"[PrototypeC] 行为装配表已回填：{filled} 格" +
                  (missing > 0 ? $"，仍有 {missing} 格没有预制件（运行时会用内置默认兜底并报错）" : "，九格齐全"));
    }

    [MenuItem("Tools/Prototype C/检查行为装配表缺格")]
    public static void CheckTable()
    {
        ObjectModelService service = Object.FindFirstObjectByType<ObjectModelService>(FindObjectsInactive.Include);
        if (service == null)
        {
            Debug.LogWarning("[PrototypeC] 当前场景里没有 ObjectModelService");
            return;
        }

        if (service.behaviorAssemblies == null || service.behaviorAssemblies.slots == null)
        {
            Debug.LogWarning("[PrototypeC] 行为装配表为空：九格都会用内置默认兜底");
            return;
        }

        int missing = 0;
        foreach ((ObjectBehavior behavior, ObjectPlacement placement) in BehaviorCells.LegalCells())
        {
            GameObject prefab = service.behaviorAssemblies.FindPrefab(behavior, placement);
            if (prefab == null)
            {
                missing++;
                Debug.LogWarning($"[PrototypeC] 行为装配缺格：{BehaviorCells.Label(behavior, placement)}");
            }
        }

        Debug.Log(missing == 0
            ? "[PrototypeC] 行为装配表九格齐全"
            : $"[PrototypeC] 行为装配表缺 {missing} 格（缺格运行时会用内置默认兜底，并写 Error 日志）");
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string leaf = Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
