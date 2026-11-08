// FocusUiBuilder.cs —— 编辑器工具：一键搭建可手工调整的 UI Canvas
// 菜单：Tools/FOCUS/搭建 UI Canvas
// 作用：创建 Canvas（参考分辨率 1920×1080），并在 Canvas 下放两个"模板 TMP"物体，
//       自动挂到 LetterAlphabetBar 的 labelTemplate / typedTemplate 上。
// 之后你只需要在 Inspector 里调这两个模板（字体、字号、描边、发光、间距…），
//       运行时字母/打字机文字会按模板克隆，不再走代码里的硬编码参数。

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

public static class FocusUiBuilder
{
    [MenuItem("Tools/FOCUS/搭建 UI Canvas", priority = 0)]
    private static void BuildCanvas()
    {
        // 1) Canvas（已存在就复用）
        var canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            var canvasGo = new GameObject("Canvas (FOCUS UI)", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create FOCUS Canvas");
            canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        // 2) EventSystem（UI 交互需要）
        if (Object.FindObjectOfType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
        }

        // 3) 模板分组（模板物体保持 inactive，不会出现在画面里）
        var group = canvas.transform.Find("UI_Templates");
        if (group == null)
        {
            var g = new GameObject("UI_Templates", typeof(RectTransform));
            g.transform.SetParent(canvas.transform, false);
            Undo.RegisterCreatedObjectUndo(g, "Create UI_Templates");
            group = g.transform;
        }

        var letterTemplate = CreateTemplate(group, "TMP_LetterTemplate", new Color(0.92f, 0.98f, 1f, 1f), 48f);
        var typedTemplate = CreateTemplate(group, "TMP_TypedTemplate", new Color(0.55f, 1f, 0.78f, 1f), 84f);

        // 4) 接到 LetterAlphabetBar
        var bar = Object.FindObjectOfType<LetterAlphabetBar>();
        if (bar != null)
        {
            Undo.RecordObject(bar, "Assign UI Templates");
            bar.labelTemplate = letterTemplate;
            bar.typedTemplate = typedTemplate;
            EditorUtility.SetDirty(bar);
        }
        else
        {
            Debug.LogWarning("[FocusUiBuilder] 场景里没找到 LetterAlphabetBar，模板已创建但未自动挂接。请手动拖到它的 labelTemplate / typedTemplate 字段。");
        }

        EditorSceneManager.MarkSceneDirty(canvas.gameObject.scene);
        Selection.activeGameObject = group.gameObject;
        Debug.Log("[FocusUiBuilder] UI Canvas 已就绪：调 UI_Templates 下的两个 TMP 模板即可控制所有运行时文字。");
    }

    /// <summary>创建一个 inactive 的 TMP 模板物体（自带 RectTransform，方便你直接调尺寸/位置）</summary>
    private static TextMeshProUGUI CreateTemplate(Transform parent, string name, Color color, float fontSize)
    {
        var existing = parent.Find(name);
        if (existing != null)
            return existing.GetComponent<TextMeshProUGUI>();

        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "Create TMP Template");

        var tmp = go.GetComponent<TextMeshProUGUI>();
        if (TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = "Aa";
        tmp.fontSize = fontSize;
        tmp.enableAutoSizing = false;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.extraPadding = true;
        tmp.color = color;
        tmp.raycastTarget = false;

        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(80f, fontSize * 1.3f);

        go.SetActive(false); // 模板不参与渲染
        return tmp;
    }
}
