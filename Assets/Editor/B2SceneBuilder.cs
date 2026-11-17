// B2SceneBuilder.cs —— 编辑器工具：一键搭建 TemplateB2 场景
// 菜单：Tools/FOCUS/B2/搭建 TemplateB2 场景
// 策略：完整保留 TemplateB 的架构与视觉资产（dino/巢/四条边缘线/MediaPipe 栈/相机/Global Volume），
//       只剥离旧玩法组件（AlphabetBar+GlyphRain/LetterPlant 体系/ThreatSystem/EcoEventScheduler/CorruptionZone/BouncingBall），
//       再按任务书在原场景上叠加新玩法：
//         双层世界(B1LayerConfig) + 双层字符雨（B2BackgroundRain 远景 + PhysicsLetter 前景 A-Z）
//         + 双食指弹力带(SlingshotBand) + 打字机(TypeWriterB1) + 恐龙(DinoAgentB1)+巢(B2Nest) + 威胁/火灾(ThreatB1)。
// 所有管理器与视觉物都是场景真实物体，Inspector 可调；动态实体（字符/物品/威胁）运行时生成。

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using TMPro;

public static class B2SceneBuilder
{
    private const string SrcScene = "Assets/Scenes/TemplateB.unity";
    private const string DstScene = "Assets/Scenes/TemplateB2.unity";
    private const string WhiteTexPath = "Assets/Art/B1_WhiteSquare.png";

    [MenuItem("Tools/FOCUS/B2/搭建 TemplateB2 场景", priority = 0)]
    private static void Build()
    {
        // 1) 拷贝场景（已存在则不覆盖，直接继续搭建）
        if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(DstScene))
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SrcScene) == null)
            { Debug.LogError($"[B2] 找不到源场景 {SrcScene}"); return; }
            if (!System.IO.File.Exists(Application.dataPath + "/Scenes/TemplateB2.unity"))
                System.IO.File.Copy(Application.dataPath + "/Scenes/TemplateB.unity",
                                    Application.dataPath + "/Scenes/TemplateB2.unity");
            AssetDatabase.Refresh();
        }
        var scene = EditorSceneManager.OpenScene(DstScene, OpenSceneMode.Single);

        StripOldSystems();

        EnsureTextStyle();
        var world = EnsureWorld();
        EnsureFloors(world);
        EnsureBackgroundRain(world);
        var provider = EnsureProvider();
        EnsureBand(world, provider);
        EnsureTypeWriter(world);
        EnsureScheduler(world);
        EnsureTable(world);
        EnsureDino(world);
        FrameCamera(world);

        EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = world.gameObject;
        Debug.Log("[B2] TemplateB2 搭建完成：保留 TemplateB 架构（dino/巢/边缘线/MediaPipe/Volume），" +
                  "叠加 双层世界 / 背景字符雨 / 前景A-Z字符雨(带拖尾) / 弹力带 / 打字机 / 恐龙 / 威胁 / 火灾。");
    }

    /// <summary>移除旧玩法组件，保留场景骨架与视觉资产（TemplateB 原场景不受影响）</summary>
    private static void StripOldSystems()
    {
        foreach (var name in new[] { "AlphabetBar", "EdgeHighlightController" })
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }

        // 旧场景 UI 里横穿屏幕中央的一条细线（TemplateB 遗留，会误导成"地面/分界线"）
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
        var go = new GameObject("B2World");
        return go.AddComponent<B1LayerConfig>();
    }

    private static RightHandIndexRayProvider EnsureProvider()
    {
        var p = Object.FindObjectOfType<RightHandIndexRayProvider>();
        if (p == null) Debug.LogWarning("[B2] 场景里没有 RightHandIndexRayProvider（MediaPipe 手部追踪）——请确认 Solution 栈完整");
        return p;
    }

    /// <summary>下层地面线 / 上层地板（右端留缺口）</summary>
    private static void EnsureFloors(B1LayerConfig world)
    {
        var sprite = WhiteSquare();

        var lower = FindOrCreate("B2_LowerFloor");
        lower.transform.position = new Vector3(0f, world.lowerGroundY - 0.08f, 0f);
        SetBar(lower, sprite, world.boundsXMin, world.boundsXMax, 0.16f,
               new Color(0.9f, 0.93f, 0.96f, 1f), 0);
        world.lowerFloorVisual = lower.GetComponent<SpriteRenderer>();

        var upper = FindOrCreate("B2_UpperFloor");
        upper.transform.position = new Vector3(0f, world.upperFloorY - 0.14f, 0f);
        SetBar(upper, sprite, world.upperFloorXMin, world.upperFloorXMax, 0.28f,
               new Color(0.82f, 0.88f, 0.95f, 1f), 0);
        world.upperFloorVisual = upper.GetComponent<SpriteRenderer>();

        // 缺口两端的小标记（视觉提示：东西从这里掉/弹）
        var gapMark = FindOrCreate("B2_GapMark");
        gapMark.transform.position = new Vector3(world.GapCenterX, world.lowerGroundY + 0.02f, 0f);
        SetBar(gapMark, sprite, world.GapXMin, world.GapXMax, 0.06f,
               new Color(1f, 0.85f, 0.3f, 0.9f), 1);

        EditorUtility.SetDirty(world);
    }

    /// <summary>远景背景字符雨——World Space Canvas (z=5)，世界空间字母在 z=0 天然在前面</summary>
    private static void EnsureBackgroundRain(B1LayerConfig world)
    {
        // 清理旧版残留
        foreach (var name in new[] { "B2_RainCanvas", "B2_LetterCanvas" })
        {
            var old = GameObject.Find(name);
            if (old != null) Object.DestroyImmediate(old);
        }
        var oldRain = GameObject.Find("B2_BackgroundRain");
        if (oldRain != null) Object.DestroyImmediate(oldRain);

        // 专用 World Space Canvas：放在 z=5（世界物体在 z=0），渲染在所有世界物体之后。
        // 注意 sortingOrder 必须是正数（1）：填 -100 时整个 Canvas 不参与渲染，雨会整片消失。
        // 1 = 压在地板/边缘线(0)之上、前景字母(2)之下。
        var canvasGo = new GameObject("B2_RainCanvas", typeof(RectTransform), typeof(Canvas));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 1;
        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(1920f, 1080f);

        // 按相机视野缩放，让 GlyphRain 的像素参数直接映射到世界单位
        float viewH = (world.upperCeilingY - world.lowerGroundY) + 0.8f;
        float scale = viewH / 1080f;
        canvasGo.transform.localScale = new Vector3(scale, scale, 1f);
        canvasGo.transform.position = new Vector3(0f, (world.upperCeilingY + world.lowerGroundY) * 0.5f, 5f);

        // B2_BackgroundRain 铺满整个 Canvas
        var go = new GameObject("B2_BackgroundRain", typeof(RectTransform));
        go.transform.SetParent(canvasGo.transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);

        // 复用原版 GlyphRain（bar 留空=不浇水，纯视觉）
        var rain = go.AddComponent<GlyphRain>();
        rain.bar = null;
        rain.rayProvider = Object.FindObjectOfType<RightHandIndexRayProvider>();
        rain.rainEnabled = true;
        rain.spawnRate = 32f;
        rain.fallSpeedPx = 360f;
        rain.fontSize = 22;
        rain.glyphColor = new Color(0.35f, 0.8f, 0.5f, 0.55f);
        rain.headColor = new Color(0.55f, 0.92f, 0.68f, 0.8f);
        rain.trailLength = 7;
        rain.dropAngleDeg = 8f;
        EditorUtility.SetDirty(rain);
    }

    /// <summary>弹力带管理器（引用手部追踪；Start 里会关掉指向射线）</summary>
    private static void EnsureBand(B1LayerConfig world, RightHandIndexRayProvider provider)
    {
        var go = GameObject.Find("B2_SlingshotBand");
        if (go == null) go = new GameObject("B2_SlingshotBand");
        var band = go.GetComponent<SlingshotBand>() ?? go.AddComponent<SlingshotBand>();
        band.world = world;
        band.rayProvider = provider;
        // 视觉：TemplateB 屋檐风（细线 + 指尖矩形，左青右橙）——强制写入覆盖旧序列化值
        band.lineWidth = 0.045f;
        band.tipDotSize = 0.26f;
        band.pipDotSize = 0.14f;
        band.bandColor = new Color(1f, 1f, 1f, 0.55f);
        band.bandHotColor = new Color(0.4f, 1f, 0.7f, 0.95f);
        band.leftTipColor = new Color(0.4f, 0.9f, 1f, 1f);
        band.rightTipColor = new Color(1f, 0.65f, 0.35f, 1f);
        band.pipColor = new Color(1f, 1f, 1f, 0.8f);
        EditorUtility.SetDirty(band);
    }

    /// <summary>统一字符风格（字体资产/材质）：场景里一个实体，运行时动态字符自动跟随</summary>
    private static void EnsureTextStyle()
    {
        var go = GameObject.Find("B2_TextStyle");
        if (go == null) go = new GameObject("B2_TextStyle");
        var style = go.GetComponent<B1TextStyle>() ?? go.AddComponent<B1TextStyle>();
        // 字体留空会导致全部 TMP 隐形：自动指定项目字体（优先 NotoSerifCJKsc-Black SDF）
        if (style.font == null)
        {
            foreach (var g in AssetDatabase.FindAssets("t:TMP_FontAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (path.Contains("NotoSerifCJKsc-Black SDF"))
                { style.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path); break; }
            }
        }
        if (style.font == null && TMP_Settings.defaultFontAsset == null)
            Debug.LogWarning("[B2] TMP 默认字体为空：请先导入 TMP Essential Resources，或在 B2_TextStyle 上指定字体资产");
        EditorUtility.SetDirty(style);
    }

    /// <summary>打字机（世界空间 TMP，编辑器里可直接改字体/描边/发光）</summary>
    private static void EnsureTypeWriter(B1LayerConfig world)
    {
        var go = GameObject.Find("B2_TypeWriter");
        bool twIsNew = go == null; // 只在首次创建时设位置，之后不覆盖用户的调整
        if (go == null) go = new GameObject("B2_TypeWriter");
        var tw = go.GetComponent<TypeWriterB1>() ?? go.AddComponent<TypeWriterB1>();

        // 词库必须在这里显式赋值：场景里旧组件会把 words 序列化住，
        // 改代码里的字段初值不会生效（WOOD/STONE 曾因此永远匹配不到）。
        tw.words = new[] { "BALL", "CHAIR", "WATER", "WOOD", "STONE" };

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

        // 位置：只在首次创建时设置默认值（分界线下方附近）；之后用户可在 Inspector/Scene 自由拖动，重建不会覆盖
        if (twIsNew)
        {
            go.transform.position = new Vector3(world.boundsXMin + 2.6f, world.upperFloorY - 1.1f, 0f);
            tw.spawnAnchor = new Vector3(world.boundsXMin + 2.6f, world.upperFloorY - 1.8f, 0f);
        }
        EditorUtility.SetDirty(tw);
    }

    /// <summary>事件调度器：前景 A-Z 字符雨（更大/更慢/更稀疏/带拖尾）+ 红色威胁 + 火灾</summary>
    private static void EnsureScheduler(B1LayerConfig world)
    {
        var go = GameObject.Find("B2_Scheduler");
        if (go == null) go = new GameObject("B2_Scheduler");
        go.SetActive(true);
        var s = go.GetComponent<B1EventScheduler>();
        bool added = s == null;
        if (added) s = go.AddComponent<B1EventScheduler>();
        s.enabled = true;
        s.world = world;
        // 前景雨节奏：比远景大、慢，但密度要够（字落地即碎，寿命约 1.3 秒）
        s.letterInterval = 0.45f;
        s.maxLiveLetters = 20;
        s.letterFallSpeed = 0.8f;

        // 灾害节奏：先留足时间让玩家学会拼词/盖房，再出事
        s.threatFirstDelay = 45f;
        s.threatIntervalRange = new Vector2(40f, 60f);
        s.fireFirstDelay = 120f;
        s.fireIntervalRange = new Vector2(100f, 160f);
        EditorUtility.SetDirty(s);
        Debug.Log($"[B2] 调度器 B2_Scheduler: 组件{(added ? "新建" : "已存在")}, enabled={s.enabled}, active={go.activeSelf}");
    }

    /// <summary>字符画小桌子（取代原恐龙巢穴）：上层地板常驻装饰</summary>
    private static void EnsureTable(B1LayerConfig world)
    {
        // 清理旧的巢物体（若有）
        var oldNest = GameObject.Find("B2_Nest");
        if (oldNest != null) Object.DestroyImmediate(oldNest);

        var go = GameObject.Find("B2_Table");
        if (go == null) go = new GameObject("B2_Table");
        var table = go.GetComponent<B2Table>() ?? go.AddComponent<B2Table>();
        table.world = world;
        EditorUtility.SetDirty(table);
    }

    /// <summary>恐龙：保留 TemplateB 外观，换 DinoAgentB1（常驻上层）+ 求救气泡</summary>
    private static void EnsureDino(B1LayerConfig world)
    {
        var dino = GameObject.Find("dino");
        if (dino == null) { Debug.LogWarning("[B2] 场景里没有 dino 物体"); return; }

        var agent = dino.GetComponent<DinoAgentB1>() ?? dino.AddComponent<DinoAgentB1>();
        agent.world = world;

        var bubbleGo = dino.transform.Find("B2_RequestBubble")?.gameObject;
        if (bubbleGo == null)
        {
            bubbleGo = new GameObject("B2_RequestBubble");
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

        // 出生点：上层地板，巢门附近
        dino.transform.position = new Vector3(-5.5f, world.upperFloorY, 0f);
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
        // 按 sprite 实际世界尺寸反算缩放（兼容任意 PPU，避免线只显示中间一小段）
        float sw = sprite.bounds.size.x;
        float sh = sprite.bounds.size.y;
        t.localScale = new Vector3((x1 - x0) / Mathf.Max(sw, 0.001f), h / Mathf.Max(sh, 0.001f), 1f);
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
