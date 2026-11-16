using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ForestGladeBackgroundInstaller
{
    private const string RootPath = "Assets/EnvironmentArt/ForestGlade";

    [MenuItem("Tools/Word Defense/Install Forest Background")]
    public static void Install()
    {
        var world = Object.FindFirstObjectByType<B1LayerConfig>();
        var dino = Object.FindFirstObjectByType<DinoAgentB1>();
        if (world == null || dino == null)
            throw new System.InvalidOperationException("Open the B2 gameplay scene before installing the forest background.");
        string[] names = { "01_Sky", "02_DistantForest", "03_MidgroundTrees", "04_Ground", "05_ForegroundLeaves" };
        var sprites = new Sprite[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            sprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>($"{RootPath}/Layers/{names[i]}.png");
            if (sprites[i] == null) throw new System.InvalidOperationException("Missing sprite: " + names[i]);
        }
        Shader shader = Shader.Find("WordDefense/Forest Background Layer");
        if (shader == null) throw new System.InvalidOperationException("Forest layer shader is not imported yet.");
        string materialPath = RootPath + "/Materials/ForestLayer.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "ForestLayer" };
            AssetDatabase.CreateAsset(material, materialPath);
        }
        var background = Object.FindFirstObjectByType<ForestGladeBackground>();
        if (background == null)
        {
            var root = new GameObject("ForestGlade_Background");
            Undo.RegisterCreatedObjectUndo(root, "Add forest background");
            background = Undo.AddComponent<ForestGladeBackground>(root);
        }
        Undo.RecordObject(background, "Configure forest background");
        Vector2[] offsets = { new Vector2(0.003f, 0.001f), new Vector2(0.012f, 0.003f),
            new Vector2(0.024f, 0.006f), Vector2.zero, new Vector2(0.032f, 0.008f) };
        var layers = new ForestGladeBackground.Layer[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            Transform child = background.transform.Find(names[i]);
            if (child == null)
            {
                var go = new GameObject(names[i]);
                Undo.RegisterCreatedObjectUndo(go, "Add forest layer");
                go.transform.SetParent(background.transform, false);
                child = go.transform;
            }
            var renderer = child.GetComponent<SpriteRenderer>();
            if (renderer == null) renderer = Undo.AddComponent<SpriteRenderer>(child.gameObject);
            Undo.RecordObject(renderer, "Configure forest layer");
            renderer.sprite = sprites[i];
            renderer.sharedMaterial = material;
            layers[i] = new ForestGladeBackground.Layer { renderer = renderer, maximumUvOffset = offsets[i] };
        }
        background.Configure(world, dino.transform, layers);
        EditorUtility.SetDirty(background);
        EditorSceneManager.MarkSceneDirty(background.gameObject.scene);
        AssetDatabase.SaveAssets();
        Debug.Log("Forest background installed. Five visual layers follow the dinosaur; gameplay scripts are unchanged.");
    }
}
