// MeshGenerationPlaceholder.cs —— Prototype C：Meshy 生成期间的字符画占位
// 用途：输入一个缓存里没有的物件时，等待 Meshy 的几十秒里场景不是空的，
// 而在最终落点摆一块「施工中」的字符画牌子；真模型加载成功后在同一锚点原地替换。
//
// 约束（与需求一一对应）：
//   - 恐龙无法交互：只有 TMP 文字，没有 Collider、没有 B1Item / B1Resource 组件、
//     raycastTarget = false，因此既不进 B1 的静态注册表，也不会被物理或指尖命中。
//   - 出现时机：由 ObjectModelService 在「本地缓存 / 工程镜像都未命中 + 已确认有 API Key」
//     之后、提交 Meshy 之前创建；缓存命中路径根本不会走到这里，所以不会闪。
//   - 不跳位：锚点、yaw、尺寸都来自同一个 ObjectSpec，且槽位索引在创建占位时就预留好，
//     真实模型走同一个 slot → 位置 / 朝向 / 尺寸归一一致。
//   - 失败 / 超时 / 取消：Fail() 把牌子改成 FAILED 并在 holdSeconds 后自毁；
//     面板状态文本由 ObjectModelService 的 StatusChanged 继续保留失败原因。
//   - 不引入框架：复用 B1Text.Make（与 B1 字符雨 / 打字机同一套 TMP 字体与排序层）。
//   - 不每帧分配：Update 只改 alpha；文字只在百分比档位（5% 一档）变化时重建一次。
//
// 不需要用户在场景里挂任何东西：占位是运行时由 ObjectModelService 创建的临时物体。

using TMPro;
using UnityEngine;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class MeshGenerationPlaceholder : MonoBehaviour
    {
        // 字符画是按等宽假定的：内框 6 列，整行 = "| " + 6 + " |" = 10 列，5 行。
        private const int InnerWidth = 6;
        private const int TotalChars = InnerWidth + 4;
        private const int Lines = 5;

        // B1Text.Make 的字号约定：fontSize 5 ≈ 0.5 世界单位高（每点 0.1 世界单位）；
        // 行高按 1.1 倍字号、字宽按 0.5 倍字号估算，用来把牌子缩到 spec.size 的量级。
        private const float FontWorldFactor = 0.1f;
        private const float LineHeightFactor = 0.11f;
        private const float CharWidthFactor = 0.05f;

        private const int BucketStep = 5;

        private TMP_Text _text;
        private string _label = "OBJECT";
        private int _bucket = -1;
        private bool _failed;

        /// <summary>在 anchor 处创建占位（anchor 与真实模型最终的落点完全一致）。</summary>
        public static MeshGenerationPlaceholder Create(
            string objectName, Vector3 anchor, float yawDegrees, Vector3 size, Color color)
        {
            var root = new GameObject("MeshPlaceholder");
            root.transform.position = anchor;
            // 与真实模型一致的朝向（ApplyPlacement 用的也是 spec.yawDegrees）；
            // 牌子文字在下面单独朝相机，保证可读，但不影响根节点的位置 / 朝向 / 尺寸口径。
            root.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            MeshGenerationPlaceholder sign = root.AddComponent<MeshGenerationPlaceholder>();
            sign.Build(objectName, size, color);
            return sign;
        }

        /// <summary>LayoutSolver 解完后把牌子瞬移到真实落点（并重新缩放字号适配新尺寸）。</summary>
        public void SetAnchorPosition(Vector3 worldPos, float yawDegrees, Vector3 size)
        {
            if (this == null) return;
            transform.position = worldPos;
            transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);
            RescaleFor(size);

            // 重新对齐相机与 Y 偏移（Build 里做过的那套）
            Camera cam = Camera.main;
            if (cam != null && _text != null) _text.transform.rotation = cam.transform.rotation;
        }

        private void RescaleFor(Vector3 size)
        {
            if (_text == null) return;
            float byWidth = Mathf.Max(0.01f, size.x) / (TotalChars * CharWidthFactor);
            float byHeight = Mathf.Max(0.01f, size.y) / (Lines * LineHeightFactor);
            float fontSize = Mathf.Clamp(Mathf.Min(byWidth, byHeight), 0.9f, 12f);

            // B1Text.Make 之后再改 fontSize 会自动缩放，但我们不想重 Make
            // 所以用 localScale 补偿：字号不变，缩放到新 size 对应的比例
            float worldHeight = Lines * LineHeightFactor * fontSize;
            Vector3 s = _text.transform.localScale;
            // 先把 text 的字号"拉满"，再用 scale 降到实际需要的效果
            _text.fontSize = fontSize;
            _text.transform.localScale = s;
            _text.transform.position = transform.position + Vector3.up * (worldHeight * 0.5f);
        }

        private void Build(string objectName, Vector3 size, Color color)
        {
            _label = MakeLabel(objectName);

            float byWidth = Mathf.Max(0.01f, size.x) / (TotalChars * CharWidthFactor);
            float byHeight = Mathf.Max(0.01f, size.y) / (Lines * LineHeightFactor);
            float fontSize = Mathf.Clamp(Mathf.Min(byWidth, byHeight), 0.9f, 12f);

            _text = B1Text.Make("PlaceholderSign", transform, fontSize, color, 5);
            _text.alignment = TextAlignmentOptions.Center;

            Camera cam = Camera.main;
            if (cam != null) _text.transform.rotation = cam.transform.rotation;

            // 牌子底边对齐锚点（真实模型也是底面对齐锚点，见 ApplyPlacement 的 bounds.min.y 补偿）
            float worldHeight = Lines * LineHeightFactor * fontSize;
            _text.transform.position = transform.position + Vector3.up * (worldHeight * 0.5f);

            SetPercent(0, force: true);
        }

        /// <summary>生成进度（0~100）。按 5% 一档刷新，避免每帧拼字符串。</summary>
        public void SetPercent(int percent, bool force = false)
        {
            if (_text == null || _failed) return;

            int bucket = Mathf.Clamp(percent / BucketStep, 0, 100 / BucketStep) * BucketStep;
            if (!force && bucket == _bucket) return;

            _bucket = bucket;
            _text.text = ComposeProgress();
        }

        /// <summary>失败 / 超时 / 取消：牌子改成 FAILED，短暂停留后自己消失（状态原因留在面板上）。</summary>
        public void Fail(float holdSeconds = 2.5f)
        {
            if (_text == null)
            {
                Dispose();
                return;
            }
            if (_failed) return;

            _failed = true;
            _text.color = new Color(1f, 0.55f, 0.4f, 1f);
            _text.text = Border() + "\n" + Row(_label) + "\n" + Row("FAILED") + "\n" + Row("....") + "\n" + Border();

            if (Application.isPlaying) Destroy(gameObject, holdSeconds);
            else DestroyImmediate(gameObject);
        }

        /// <summary>真模型就绪时立刻撤掉占位（同一锚点由真模型接手，不会看到两个物体）。</summary>
        public void Dispose()
        {
            if (this == null) return;
            if (Application.isPlaying) Destroy(gameObject);
            else DestroyImmediate(gameObject);
        }

        private void Update()
        {
            if (_text == null) return;
            // 只改 alpha，不产生任何托管分配
            _text.alpha = _failed ? 0.4f : 0.7f + Mathf.Sin(Time.time * 3f) * 0.3f;
        }

        private string ComposeProgress()
        {
            int filled = Mathf.RoundToInt(_bucket / 100f * InnerWidth);
            var bar = new char[InnerWidth];
            for (int i = 0; i < InnerWidth; i++) bar[i] = i < filled ? '#' : '.';

            return Border() + "\n" +
                   Row(_label) + "\n" +
                   Row((_bucket + "%").PadLeft(InnerWidth)) + "\n" +
                   Row(new string(bar)) + "\n" +
                   Border();
        }

        private static string Border()
        {
            return "+" + new string('-', TotalChars - 2) + "+";
        }

        private static string Row(string content)
        {
            return "| " + content.PadRight(InnerWidth) + " |";
        }

        /// <summary>物件名 → 6 列以内的 ASCII 标签（LiberationSans 一定有字形；非 ASCII 会被丢掉）。</summary>
        private static string MakeLabel(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return "OBJECT";

            var sb = new System.Text.StringBuilder(InnerWidth);
            for (int i = 0; i < objectName.Length && sb.Length < InnerWidth; i++)
            {
                char c = char.ToUpperInvariant(objectName[i]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }

            string label = sb.ToString().Trim();
            return label.Length == 0 ? "OBJECT" : label;
        }
    }
}
