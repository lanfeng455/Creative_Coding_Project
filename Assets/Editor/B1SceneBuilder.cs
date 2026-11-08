// B1SceneBuilder.cs —— 编辑器工具：一键搭建 Prototype B1 场景
// 菜单：Tools/FOCUS/B1/搭建 TemplateB1 场景
// 拷贝 TemplateB → 剥离旧系统 → 摆放 B1 全套（世界配置/双层地板/弹力带/打字机/调度器/恐龙气泡）
// 所有管理器与视觉物都是场景真实物体，Inspector 可调；动态实体（字符/物品）由调度器在运行时生成。

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;

public static class B1SceneBuilder
{
    private const string SrcScene = "Assets/Scenes/TemplateB.unity";
    private const string DstScene = "Assets/Scenes/TemplateB1.unity";
    private const string WhiteTexPath = "Assets/Art/B1_WhiteSquare.png";

    [MenuItem("Tools/FOCUS/B1/搭建 TemplateB1 场景", priority = 0)]
    private static void Build()
    {
        // 1) 拷贝场景（已存在则不覆盖，直接继续搭建）
        if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(DstScene))
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SrcScene) == null)
            { Debug.LogError($"[B1] 找不到源场景 {SrcScene}"); return; }
            if (!System.IO.File.Exists(Application.dataPath + "/Scenes/TemplateB1.unity"))
                System.IO.File.Copy(Application.dataPath + "/Scenes/TemplateB.unity",
                                    Application.dataPath + "/Scenes/TemplateB1.unity");
            AssetDatabase.Refresh();
        }
        var scene = EditorSceneManager.OpenScene(DstScene, OpenSceneMode.Single);

        StripOldSystems();

        EnsureTextStyle();
        var world = EnsureWorld();
        EnsureFloors(world);
        var provider = EnsureProvider();
        EnsureBand(world, provider);
        EnsureTypeWriter(world);
        EnsureScheduler(world);
        EnsureDino(world);
        FrameCamera(world);

        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = world.gameObject;
        Debug.Log("[B1] TemplateB1 搭建完成：B1World(配置) / 地板 / 弹力带 / 打字机 / 调度器 / 恐龙(+气泡)。");
    }

    /// <summary>移除 B1 不再需要的旧物体/组件（TemplateB 原场景不受影响）</summary>
    private static void StripOldSystems()
    {
        foreach (var name in new[] { "AlphabetBar", "EdgeHighlightController" })
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // 旧场景 UI 里横穿屏幕中央的一条细线（TemplateB 遗留，B1 里会误导成"地面/分界线"）
        var strayLine = GameObject.Find("Canvas/Image");
        if (strayLine != null) Object.DestroyImmediate(strayLine);

        var dino = GameObject.Find("dino");
        if (dino != null)
            foreach (var t in dino.GetComponents<Component>())
                if (t is DinoAgent || t is ThreatSystem || t is EcoEventScheduler || t is CorruptionZone)
                    Object.DestroyImmediate(t);

        foreach (var go in Object.FindObjectsOfType<GameObject>())
        {
            if (go.GetComponent<ThreatSystem>() != null || go.GetComponent<EcoEventScheduler>() != null
                || go.GetComponent<CorruptionZone>() != null || go.GetComponent<BouncingBall>() != null)
                Object.DestroyImmediate(go);
        }
    }

    private static B1LayerConfig EnsureWorld()
    {
        var existing = Object.FindObjectOfType<B1LayerConfig>();
        if (existing != null) return existing;
        var go = new GameObject("B1World");
        return go.AddComponent<B1LayerConfig>();
    }

    private static RightHandIndexRayProvider EnsureProvider()
    {
        var p = Object.FindObjectOfType<RightHandIndexRayProvider>();
        if (p == null) Debug.LogWarning("[B1] 场景里没有 RightHandIndexRayProvider（MediaPipe 手部追踪）——请确认 Solution 栈完整");
        return p;
    }

    /// <summary>下层地面线 / 上层地板（右端留缺口）</summary>
    private static void EnsureFloors(B1LayerConfig world)
    {
        var sprite = WhiteSquare();

        var lower = FindOrCreate("B1_LowerFloor");
        lower.transform.position = new Vector3(0f, world.lowerGroundY - 0.08f, 0f);
        SetBar(lower, sprite, world.boundsXMin, world.boundsXMax, 0.16f,
               new Color(0.9f, 0.93f, 0.96f, 1f), 0);
        world.lowerFloorVisual = lower.GetComponent<SpriteRenderer>();

        var upper = FindOrCreate("B1_UpperFloor");
        upper.transform.position = new Vector3(0f, world.upperFloorY - 0.14f, 0f);
        SetBar(upper, sprite, world.upperFloorXMin, world.upperFloorXMax, 0.28f,
               new Color(0.82f, 0.88f, 0.95f, 1f), 0);
        world.upperFloorVisual = upper.GetComponent<SpriteRenderer>();

        // 缺口两端的小标记（视觉提示：东西从这里掉/弹）
        var gapMark = FindOrCreate("B1_GapMark");
        gapMark.transform.position = new Vector3(world.GapCenterX, world.lowerGroundY + 0.02f, 0f);
        SetBar(gapMark, sprite, world.GapXMin, world.GapXMax, 0.06f,
               new Color(1f, 0.85f, 0.3f, 0.9f), 1);

        EditorUtility.SetDirty(world);
    }

    /// <summary>弹力带管理器（引用手部追踪）</summary>
    private static void EnsureBand(B1LayerConfig world, RightHandIndexRayProvider provider)
    {
        var go = GameObject.Find("B1_SlingshotBand");
        if (go == null) go = new GameObject("B1_SlingshotBand");
        var band = go.GetComponent<SlingshotBand>() ?? go.AddComponent<SlingshotBand>();
        band.world = world;
        band.rayProvider = provider;
        EditorUtility.SetDirty(band);
    }

    /// <summary>统一字符风格（字体资产/材质）：场景里一个实体，运行时动态字符自动跟随</summary>
    private static void EnsureTextStyle()
    {
        var go = GameObject.Find("B1_TextStyle");
        if (go == null) go = new GameObject("B1_TextStyle");
        var style = go.GetComponent<B1TextStyle>() ?? go.AddComponent<B1TextStyle>();
        if (style.font == null && TMP_Settings.defaultFontAsset == null)
            Debug.LogWarning("[B1] TMP 默认字体为空：请先导入 TMP Essential Resources，或在 B1_TextStyle 上指定字体资产");
        EditorUtility.SetDirty(style);
    }

    /// <summary>打字机（世界空间 TMP，编辑器里可直接改字体/描边/发光）</summary>
    private static void EnsureTypeWriter(B1LayerConfig world)
    {
        var go = GameObject.Find("B1_TypeWriter");
        if (go == null) go = new GameObject("B1_TypeWriter");
        var tw = go.GetComponent<TypeWriterB1>() ?? go.AddComponent<TypeWriterB1>();

        // 清理旧版 TextMesh（迁移 TMP 后不再需要）
        var oldTm = go.GetComponent<TextMesh>();
        if (oldTm != null) Object.DestroyImmediate(oldTm);

        var displayGo = go.transform.Find("TW_Display")?.gameObject;
        if (displayGo == null)
        {
            displayGo = new GameObject("TW_Display");
            displayGo.transform.SetParent(go.transform, false);
        }
        var tmp = displayGo.GetComponent<TextMeshPro>() ?? displayGo.AddComponent<TextMeshPro>();
        var style = Object.FindObjectOfType<B1TextStyle>();
        if (tmp.font == null && style != null && style.font != null) tmp.font = style.font;
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
        tmp.fontSize = 5f;
        tmp.color = new Color(0.55f, 1f, 0.78f, 1f);
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.raycastTarget = false;
        tw.display = tmp;

        go.transform.position = new Vector3(world.boundsXMin + 1f, world.lowerGroundY + 1.9f, 0f);
        EditorUtility.SetDirty(tw);
    }

    /// <summary>事件调度器</summary>
    private static void EnsureScheduler(B1LayerConfig world)
    {
        var go = GameObject.Find("B1_Scheduler");
        if (go == null) go = new GameObject("B1_Scheduler");
        var s = go.GetComponent<B1EventScheduler>() ?? go.AddComponent<B1EventScheduler>();
        s.world = world;
        EditorUtility.SetDirty(s);
    }

    /// <summary>恐龙：DinoAgentB1 + 求救气泡（常驻上层）</summary>
    private static void EnsureDino(B1LayerConfig world)
    {
        var dino = GameObject.Find("dino");
        if (dino == null) { Debug.LogWarning("[B1] 场景里没有 dino 物体"); return; }

        var agent = dino.GetComponent<DinoAgentB1>() ?? dino.AddComponent<DinoAgentB1>();
        agent.world = world;

        var bubbleGo = dino.transform.Find("B1_RequestBubble")?.gameObject;
        if (bubbleGo == null)
        {
            bubbleGo = new GameObject("B1_RequestBubble");
            bubbleGo.transform.SetParent(dino.transform, false);
            bubbleGo.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            bubbleGo.SetActive(false);
        }
        var oldBubbleTm = bubbleGo.GetComponent<TextMesh>();
        if (oldBubbleTm != null) Object.DestroyImmediate(oldBubbleTm);

        var bubbleTmp = bubbleGo.GetComponent<TextMeshPro>() ?? bubbleGo.AddComponent<TextMeshPro>();
        var style = Object.FindObjectOfType<B1TextStyle>();
        if (bubbleTmp.font == null && style != null && style.font != null) bubbleTmp.font = style.font;
        if (bubbleTmp.font == null && TMP_Settings.defaultFontAsset != null) bubbleTmp.font = TMP_Settings.defaultFontAsset;
        bubbleTmp.fontSize = 4f;
        bubbleTmp.color = new Color(1f, 0.5f, 0.3f, 1f);
        bubbleTmp.alignment = TextAlignmentOptions.Center;
        bubbleTmp.textWrappingMode = TextWrappingModes.NoWrap;
        bubbleTmp.raycastTarget = false;
        agent.bubble = bubbleTmp;

        dino.transform.position = new Vector3(-4f, world.upperFloorY, 0f);
        EditorUtility.SetDirty(agent);
    }

    /// <summary>相机取景：正交，覆盖上下两层</summary>
    private static void FrameCamera(B1LayerConfig world)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var c = cam.GetComponent<Camera>();
        c.orthographic = true;
        c.orthographicSize = (world.upperCeilingY - world.lowerGroundY) * 0.5f + 0.4f;
        c.transform.position = new Vector3(0f, (world.upperCeilingY + world.lowerGroundY) * 0.5f, -10f);
        EditorUtility.SetDirty(c);
    }

    private static GameObject FindOrCreate(string name)
    {
        var go = GameObject.Find(name);
        if (go == null) go = new GameObject(name);
        if (go.GetComponent<SpriteRenderer>() == null) go.AddComponent<SpriteRenderer>();
        return go;
    }

    private static void SetBar(GameObject go, Sprite sprite, float x0, float x1, float h, Color color, int order)
    {
        var sr = go.GetComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = order;
        var t = go.transform;
        t.position = new Vector3((x0 + x1) * 0.5f, t.position.y, 0f);
        t.localScale = new Vector3(x1 - x0, h, 1f);
    }

    private static Sprite WhiteSquare()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(WhiteTexPath);
        if (existing != null) return existing;

        // 资产已存在但拿不到 Sprite 子资产 → 导入类型错了（textureType=Default），强制改回 Sprite
        var old = AssetImporter.GetAtPath(WhiteTexPath) as TextureImporter;
        if (old != null)
        {
            old.textureType = TextureImporterType.Sprite;
            old.spriteImportMode = SpriteImportMode.Single;
            old.filterMode = FilterMode.Point;
            old.SaveAndReimport();
            var fixedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(WhiteTexPath);
            if (fixedSprite != null) return fixedSprite;
        }

        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply();
        System.IO.Directory.CreateDirectory(Application.dataPath + "/Art");
        System.IO.File.WriteAllBytes(WhiteTexPath, tex.EncodeToPNG());
        AssetDatabase.ImportAsset(WhiteTexPath);
        var ti = (TextureImporter)AssetImporter.GetAtPath(WhiteTexPath);
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.filterMode = FilterMode.Point;
        ti.SaveAndReimport();
        Object.DestroyImmediate(tex);
        return AssetDatabase.LoadAssetAtPath<Sprite>(WhiteTexPath);
    }
}
