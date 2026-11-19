// PrototypeCMountTool.cs —— Prototype C：在「整份复制过来的 TemplateB2 场景」上做最小挂载
// 菜单：Tools/Prototype C/Mount PrototypeC Into Open Scene
//
// 前置：Assets/Scenes/TemplateC_LanMay.unity 已由文件级复制自 TemplateB2.unity（内容完全一致）。
// 本工具只新增 PrototypeC 自己的节点与测试 UI，不移动 / 不隐藏 / 不改色任何原有物体；
// 重复执行是幂等的：已存在的节点按名字复用，不会重复创建。

using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PrototypeC.EditorTools
{
    public static class PrototypeCMountTool
    {
        private const string TargetScenePath = "Assets/Scenes/TemplateC_LanMay.unity";
        private const string FontPath = "Assets/Fonts/NotoSerifCJKsc-Black SDF.asset";

        private const string RigName = "PrototypeC_Controller";
        private const string AnchorName = "PrototypeC_GeneratedAnchor";
        private const string PanelName = "PrototypeC_TestPanel";

        /// <summary>
        /// 生成物锚点：B2 上层区域（upperFloorY = 1.6），落在镜头中央偏下、恐龙右侧的空地。
        /// z 取 -1.5，位于 B2 的雨幕画布（z = 0）与上层房间画布（z = 5）之前，避免被世界空间 UI 盖住。
        /// </summary>
        private static readonly Vector3 AnchorLocalPosition = new Vector3(0f, 1.6f, -1.5f);

        [MenuItem("Tools/Prototype C/Mount PrototypeC Into Open Scene")]
        public static void MountFromMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!string.Equals(scene.path, TargetScenePath, System.StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog(
                    "Prototype C",
                    "当前打开的场景不是 TemplateC_LanMay：\n" +
                    (string.IsNullOrEmpty(scene.path) ? "（未保存的新场景）" : scene.path) +
                    "\n\n为避免误改 TemplateB2，请先打开 TemplateC_LanMay 再执行。",
                    "OK");
                return;
            }

            int created = Mount(scene);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog(
                "Prototype C",
                created == 0
                    ? "场景里已有完整的 PrototypeC 挂载，本次未新增任何节点。"
                    : $"已新增 {created} 个 PrototypeC 节点 / 组件。",
                "OK");

            Debug.Log($"[PrototypeC] 挂载完成，新增节点数：{created}");
        }

        private static int Mount(Scene scene)
        {
            int created = 0;

            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null)
            {
                Debug.LogWarning($"[PrototypeC] 未找到字体资源：{FontPath}，UI 将使用 TMP 默认字体（中文可能显示为方块）");
            }

            // ---- 生成物锚点 ----
            GameObject rig = FindRoot(scene, RigName);
            if (rig == null)
            {
                rig = new GameObject(RigName);
                SceneManager.MoveGameObjectToScene(rig, scene);
                Undo.RegisterCreatedObjectUndo(rig, "Create PrototypeC_Controller");
                created++;
            }

            Transform anchor = rig.transform.Find(AnchorName);
            if (anchor == null)
            {
                var anchorGO = new GameObject(AnchorName);
                anchorGO.transform.SetParent(rig.transform, false);
                Undo.RegisterCreatedObjectUndo(anchorGO, "Create PrototypeC_GeneratedAnchor");
                created++;
                anchor = anchorGO.transform;
            }
            anchor.localPosition = AnchorLocalPosition;

            // ---- 服务与测试入口 ----
            ObjectModelService service = rig.GetComponent<ObjectModelService>();
            bool addedService = false;
            if (service == null)
            {
                service = rig.AddComponent<ObjectModelService>();
                addedService = true;
                created++;
            }

            if (addedService)
            {
                service.spawnParent = anchor;
                service.autoFitHeight = true;
                service.pollIntervalSeconds = 5f;
                service.taskTimeoutSeconds = 900f;
                service.syncProjectMirror = true;
                service.autoArrangeSlots = true;
                service.slotsPerRow = 3;
                service.slotSpacing = 0.8f;
                service.slotRowSpacing = 0.6f;
            }
            else if (service.spawnParent == null)
            {
                service.spawnParent = anchor;
            }

            PrototypeCTestUI ui = rig.GetComponent<PrototypeCTestUI>();
            bool addedUI = false;
            if (ui == null)
            {
                ui = rig.AddComponent<PrototypeCTestUI>();
                addedUI = true;
                created++;
            }

            if (addedUI)
            {
                ui.objectName = "CHAIR";
                ui.sizeDescription = "Roughly 0.5 m wide, 0.5 m deep, 0.9 m tall.";
                ui.style = new PromptStyle();
                ui.behavior = "Decoration";
                ui.placement = Vector3.zero;
                ui.yawDegrees = 90f;
                ui.size = new Vector3(0.55f, 0.95f, 0.55f);
                ui.generation.enableTexture = true;
                ui.generation.textureResolution = "2k";
                ui.generation.targetPolycount = 30000;
                ui.generation.shouldRemesh = true;
            }
            ui.service = service;

            // ---- 测试面板：挂在 B2 原有 Canvas 下，只占左下角一小块 ----
            Canvas canvas = FindRootCanvas(scene);
            if (canvas == null)
            {
                Debug.LogWarning("[PrototypeC] 当前场景未找到根 Canvas，跳过测试 UI（生成能力本身不受影响）");
            }
            else
            {
                Transform panel = canvas.transform.Find(PanelName);
                if (panel == null)
                {
                    panel = CreateTestPanel(canvas.transform, font);
                    Undo.RegisterCreatedObjectUndo(panel.gameObject, "Create PrototypeC_TestPanel");
                    created += 5; // 面板 + 输入框 + 按钮 + AI 开关 + 状态文本
                }
                else if (panel.Find("AIParseToggle") == null)
                {
                    Toggle toggle = CreateAiToggle(panel, font);
                    Undo.RegisterCreatedObjectUndo(toggle.gameObject, "Create PrototypeC_AIParseToggle");
                    created++;
                }

                LayoutTestPanel(panel);

                ui.inputField = FindChild<TMP_InputField>(panel, "InputField");
                ui.generateButton = FindChild<Button>(panel, "GenerateButton");
                ui.statusText = FindChild<TMP_Text>(panel, "Status");
                ui.aiParseToggle = FindChild<Toggle>(panel, "AIParseToggle");

                if (ui.inputField != null && string.IsNullOrEmpty(ui.inputField.text)) ui.inputField.text = ui.objectName;
            }

            EditorUtility.SetDirty(rig);
            EditorUtility.SetDirty(service);
            EditorUtility.SetDirty(ui);
            if (canvas != null) EditorUtility.SetDirty(canvas);

            return created;
        }

        private static Transform CreateTestPanel(Transform canvasTransform, TMP_FontAsset font)
        {
            var panelGO = new GameObject(PanelName, typeof(RectTransform), typeof(Image));
            panelGO.layer = 5;
            panelGO.transform.SetParent(canvasTransform, false);

            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0f, 0f);
            panelRT.anchorMax = new Vector2(0f, 0f);
            panelRT.pivot = new Vector2(0f, 0f);
            panelRT.anchoredPosition = new Vector2(24f, 24f);
            panelRT.sizeDelta = new Vector2(620f, 164f);

            var background = panelGO.GetComponent<Image>();
            background.color = new Color(0.06f, 0.09f, 0.14f, 0.78f);

            var accent = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accent.layer = 5;
            accent.transform.SetParent(panelGO.transform, false);
            var accentRT = accent.GetComponent<RectTransform>();
            accentRT.anchorMin = new Vector2(0f, 1f);
            accentRT.anchorMax = new Vector2(1f, 1f);
            accentRT.pivot = new Vector2(0.5f, 1f);
            accentRT.anchoredPosition = Vector2.zero;
            accentRT.sizeDelta = new Vector2(0f, 2f);
            accent.GetComponent<Image>().color = new Color(0.6006f, 0.7822f, 1f, 0.9f);

            var fieldGO = new GameObject("InputField", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            fieldGO.layer = 5;
            fieldGO.transform.SetParent(panelGO.transform, false);
            PlaceTopLeft(fieldGO.GetComponent<RectTransform>(), new Vector2(14f, -14f), new Vector2(330f, 40f));
            var fieldBackground = fieldGO.GetComponent<Image>();
            fieldBackground.color = new Color(1f, 1f, 1f, 0.14f);

            var viewport = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            viewport.layer = 5;
            viewport.transform.SetParent(fieldGO.transform, false);
            var viewportRT = viewport.GetComponent<RectTransform>();
            viewportRT.anchorMin = Vector2.zero;
            viewportRT.anchorMax = Vector2.one;
            viewportRT.offsetMin = new Vector2(12f, 6f);
            viewportRT.offsetMax = new Vector2(-12f, -6f);

            TMP_Text fieldText = CreateText(viewport.transform, "Text", "CHAIR", font, 22f, TextAlignmentOptions.MidlineLeft);
            var fieldTextRT = fieldText.rectTransform;
            fieldTextRT.anchorMin = Vector2.zero;
            fieldTextRT.anchorMax = Vector2.one;
            fieldTextRT.offsetMin = Vector2.zero;
            fieldTextRT.offsetMax = Vector2.zero;
            fieldText.raycastTarget = true;

            var inputField = fieldGO.GetComponent<TMP_InputField>();
            inputField.textViewport = viewportRT;
            inputField.textComponent = fieldText;
            inputField.fontAsset = font;
            inputField.pointSize = 22f;
            inputField.lineType = TMP_InputField.LineType.SingleLine;
            inputField.caretWidth = 2;
            inputField.targetGraphic = fieldBackground;
            inputField.text = "CHAIR";

            var buttonGO = new GameObject("GenerateButton", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonGO.layer = 5;
            buttonGO.transform.SetParent(panelGO.transform, false);
            PlaceTopLeft(buttonGO.GetComponent<RectTransform>(), new Vector2(352f, -14f), new Vector2(148f, 40f));

            var buttonImage = buttonGO.GetComponent<Image>();
            buttonImage.color = new Color(0.32f, 0.58f, 0.95f, 1f);

            var button = buttonGO.GetComponent<Button>();
            button.targetGraphic = buttonImage;

            TMP_Text buttonLabel = CreateText(buttonGO.transform, "Label", "Generate", font, 22f, TextAlignmentOptions.Center);
            var buttonLabelRT = buttonLabel.rectTransform;
            buttonLabelRT.anchorMin = Vector2.zero;
            buttonLabelRT.anchorMax = Vector2.one;
            buttonLabelRT.offsetMin = Vector2.zero;
            buttonLabelRT.offsetMax = Vector2.zero;

            CreateAiToggle(panelGO.transform, font);

            TMP_Text status = CreateText(panelGO.transform, "Status", "就绪", font, 16f, TextAlignmentOptions.TopLeft);
            status.color = new Color(0.8f, 0.87f, 1f);
            PlaceTopLeft(status.rectTransform, new Vector2(14f, -62f), new Vector2(592f, 92f));

            return panelGO.transform;
        }

        /// <summary>「AI 解析」开关：默认开启，关闭后用面板里的手动描述生成。</summary>
        private static Toggle CreateAiToggle(Transform parent, TMP_FontAsset font)
        {
            var toggleGO = new GameObject("AIParseToggle", typeof(RectTransform), typeof(Toggle));
            toggleGO.layer = 5;
            toggleGO.transform.SetParent(parent, false);
            PlaceTopLeft(toggleGO.GetComponent<RectTransform>(), new Vector2(510f, -16f), new Vector2(96f, 36f));

            var boxGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            boxGO.layer = 5;
            boxGO.transform.SetParent(toggleGO.transform, false);
            var boxRT = boxGO.GetComponent<RectTransform>();
            boxRT.anchorMin = new Vector2(0f, 0.5f);
            boxRT.anchorMax = new Vector2(0f, 0.5f);
            boxRT.pivot = new Vector2(0f, 0.5f);
            boxRT.anchoredPosition = new Vector2(0f, 0f);
            boxRT.sizeDelta = new Vector2(22f, 22f);

            var boxImage = boxGO.GetComponent<Image>();
            boxImage.color = new Color(1f, 1f, 1f, 0.18f);

            var checkGO = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            checkGO.layer = 5;
            checkGO.transform.SetParent(boxGO.transform, false);
            var checkRT = checkGO.GetComponent<RectTransform>();
            checkRT.anchorMin = Vector2.zero;
            checkRT.anchorMax = Vector2.one;
            checkRT.offsetMin = new Vector2(4f, 4f);
            checkRT.offsetMax = new Vector2(-4f, -4f);

            var checkImage = checkGO.GetComponent<Image>();
            checkImage.color = new Color(0.32f, 0.58f, 0.95f, 1f);

            TMP_Text label = CreateText(toggleGO.transform, "Label", "AI 解析", font, 16f, TextAlignmentOptions.MidlineLeft);
            var labelRT = label.rectTransform;
            labelRT.anchorMin = new Vector2(0f, 0f);
            labelRT.anchorMax = new Vector2(1f, 1f);
            labelRT.offsetMin = new Vector2(28f, 0f);
            labelRT.offsetMax = Vector2.zero;

            var toggle = toggleGO.GetComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = checkImage;
            toggle.isOn = true;

            return toggle;
        }

        /// <summary>面板尺寸与四件套位置统一在这里收口，重复执行也能收敛到同一布局。</summary>
        private static void LayoutTestPanel(Transform panel)
        {
            var panelRT = panel as RectTransform;
            if (panelRT != null) panelRT.sizeDelta = new Vector2(620f, 164f);

            PlaceTopLeft(FindRect(panel, "InputField"), new Vector2(14f, -14f), new Vector2(330f, 40f));
            PlaceTopLeft(FindRect(panel, "GenerateButton"), new Vector2(352f, -14f), new Vector2(148f, 40f));
            PlaceTopLeft(FindRect(panel, "AIParseToggle"), new Vector2(510f, -16f), new Vector2(96f, 36f));
            PlaceTopLeft(FindRect(panel, "Status"), new Vector2(14f, -62f), new Vector2(592f, 92f));
        }

        private static RectTransform FindRect(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child as RectTransform;
        }

        private static TMP_Text CreateText(Transform parent, string name, string content, TMP_FontAsset font,
            float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.text = content;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = alignment;
            text.raycastTarget = false;

            return text;
        }

        private static void PlaceTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            if (rect == null) return;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static GameObject FindRoot(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
            }
            return null;
        }

        private static Canvas FindRootCanvas(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var canvas = root.GetComponent<Canvas>();
                if (canvas != null && canvas.isRootCanvas) return canvas;
            }
            return null;
        }

        private static T FindChild<T>(Transform parent, string name) where T : Component
        {
            Transform child = parent.Find(name);
            return child != null ? child.GetComponent<T>() : null;
        }
    }
}
