// TypewriterSendButton.cs —— Prototype C：打字机右侧的临时发送键（指尖触发）
// 流程：读取打字机整行内容 → 立刻清空打字机 → 交给 PrototypeCTestUI 判定合法性
//       → 合法：入队走模型生成链路；不合法：在打字机原位置打一个 1~2 秒的红色 X。
// 位置：默认贴在打字机同一行的右侧（按 charSpacing × 槽位数计算），可用 slotPadding 微调。

using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace PrototypeC
{
    public class TypewriterSendButton : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public TypeWriterB1 typeWriter;
        public PrototypeCTestUI submitTarget;
        public RightHandIndexRayProvider ray;

        [Header("位置（相对打字机，本地坐标）")]
        [Tooltip("按钮中心在文字行右侧的第几个槽位之后：默认 = maxBuffer + slotPadding")]
        public int slotPadding = 2;
        [Tooltip("文字行相对打字机的本地 Y（TypeWriterB1 里字都排在 -0.7）")]
        public float rowLocalY = -0.7f;

        [Header("外观")]
        public string label = "SEND";
        public float fontSize = 4.2f;
        public Color idleColor = new Color(0.35f, 0.85f, 1f, 1f);
        public Color hoverColor = new Color(0.75f, 1f, 0.95f, 1f);
        public Color busyColor = new Color(0.55f, 0.6f, 0.65f, 1f);
        public Color rejectColor = new Color(1f, 0.35f, 0.3f, 1f);

        [Header("指尖触发")]
        [Tooltip("指尖在按钮上停留多久触发（秒）")]
        public float dwellSeconds = 0.18f;
        [Tooltip("触发后要等指尖离开多久才重新上膛（秒）")]
        public float reArmSeconds = 0.6f;
        [Tooltip("判定「离开」时外扩的余量（世界单位）")]
        public float leaveMargin = 0.18f;

        [Header("红色 X")]
        [Tooltip("红叉停留时间（秒）")]
        public float redXSeconds = 1.6f;
        [Tooltip("红叉字号（相对打字机字号倍数）")]
        public float redXScale = 1.5f;

        private TextMeshPro _label;
        private Vector2 _hitSize = new Vector2(2.1f, 0.9f);
        private float _insideTime;
        private float _outsideTime;
        private bool _armed = true;
        private bool _pending;
        private float _cooldownUntil;
        private bool _hover;
        private TextMeshPro _redX;
        private float _redXUntil;
        private Color _lastVisualColor;

        private void Start()
        {
            if (typeWriter == null) typeWriter = FindObjectOfType<TypeWriterB1>();
            if (submitTarget == null) submitTarget = FindObjectOfType<PrototypeCTestUI>();
            if (ray == null) ray = FindObjectOfType<RightHandIndexRayProvider>();

            BuildLabel();
            PlaceButton();
        }

        private void BuildLabel()
        {
            if (_label != null) return;
            _label = B1Text.Make("SendButton", transform, fontSize, idleColor, 3);
            _label.alignment = TextAlignmentOptions.Center;
            _label.text = MarkedLabel();
        }

        private void PlaceButton()
        {
            if (typeWriter == null) return;

            float spacing = Mathf.Max(0.1f, typeWriter.charSpacing);
            int slot = Mathf.Max(1, typeWriter.maxBuffer) + Mathf.Max(1, slotPadding);
            transform.position = typeWriter.transform.TransformPoint(new Vector3(spacing * slot, rowLocalY, 0f));

            // 命中框按字号估算：TMP 字号 ≈ 世界高度的 10 倍
            _hitSize = new Vector2(
                Mathf.Max(1.2f, label.Length * fontSize * 0.075f + 0.9f),
                Mathf.Max(0.6f, fontSize * 0.16f));
        }

        private void Update()
        {
            if (_label == null) BuildLabel();
            if (typeWriter == null) return;

            if (_redX != null && Time.time >= _redXUntil)
            {
                Destroy(_redX.gameObject);
                _redX = null;
            }

            UpdateFingertip();
            UpdateVisual();
        }

        private void UpdateFingertip()
        {
            if (ray == null) return;

            var cam = Camera.main;
            if (cam == null) return;

            bool inside = false;
            bool outsideOuter = true;
            int tipCount = 0;
            float zDist = -cam.transform.position.z;

            GetBounds(out Vector3 min, out Vector3 max);
            Vector3 outerMin = min - new Vector3(leaveMargin, leaveMargin, 0f);
            Vector3 outerMax = max + new Vector3(leaveMargin, leaveMargin, 0f);

            foreach (var tip in CandidateTips())
            {
                if (tip == Vector2.zero) continue;
                tipCount++;
                Vector3 w = cam.ScreenToWorldPoint(new Vector3(tip.x, tip.y, zDist));
                bool inOuter = w.x >= outerMin.x && w.x <= outerMax.x && w.y >= outerMin.y && w.y <= outerMax.y;
                if (inOuter) outsideOuter = false;
                if (w.x >= min.x && w.x <= max.x && w.y >= min.y && w.y <= max.y) inside = true;
            }

            // 没检测到手：保持状态，不误判离开
            if (tipCount == 0) return;

            _hover = inside;

            if (_pending || Time.time < _cooldownUntil)
            {
                _insideTime = 0f;
                return;
            }

            if (!inside)
            {
                _insideTime = 0f;
                if (outsideOuter)
                {
                    _outsideTime += Time.deltaTime;
                    if (_outsideTime >= reArmSeconds) _armed = true;
                }
                return;
            }

            _outsideTime = 0f;
            if (!_armed) return;

            _insideTime += Time.deltaTime;
            if (_insideTime >= dwellSeconds) Trigger();
        }

        private void Trigger()
        {
            if (_pending || typeWriter == null) return;

            string word = typeWriter.BufferText;
            if (string.IsNullOrWhiteSpace(word)) return;

            _pending = true;
            _armed = false;
            _insideTime = 0f;

            // 不管合法与否，打字机立刻清空，为下一次输入做初始化
            typeWriter.Clear();

            _ = SendAsync(word);
        }

        private async Task SendAsync(string word)
        {
            bool ok = false;
            try
            {
                ok = submitTarget != null && await submitTarget.SubmitTypewriterWordAsync(word);
                if (submitTarget == null)
                    Debug.LogWarning("[TypewriterSend] 找不到 PrototypeCTestUI，无法判定合法性");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TypewriterSend] 发送判定异常（{word}）：{e.Message}");
            }

            _pending = false;
            _cooldownUntil = Time.time + Mathf.Max(0.1f, reArmSeconds);

            if (!ok) ShowRedX();
        }

        private void ShowRedX()
        {
            float x = typeWriter != null ? typeWriter.charSpacing * 1.5f : 0.7f;
            if (_redX == null)
            {
                // 挂在打字机下面，打红叉的位置就是打字机原位置（不是发送键旁边）
                Transform parent = typeWriter != null ? typeWriter.transform : transform;
                _redX = B1Text.Make("SendRedX", parent, fontSize * Mathf.Max(1f, redXScale), rejectColor, 4);
                _redX.alignment = TextAlignmentOptions.Center;
                _redX.text = "X";
            }

            _redX.transform.localPosition = new Vector3(x, rowLocalY, 0f);
            _redX.color = rejectColor;
            _redX.gameObject.SetActive(true);
            _redXUntil = Time.time + Mathf.Max(0.2f, redXSeconds);
        }

        private void UpdateVisual()
        {
            if (_label == null) return;

            Color c = _pending ? busyColor
                : Time.time < _cooldownUntil ? idleColor
                : _hover ? hoverColor
                : idleColor;

            if (c != _lastVisualColor)
            {
                _lastVisualColor = c;
                _label.color = c;
                _label.text = MarkedLabel();
            }

            // 悬停时轻轻放大，给指尖一点反馈
            float s = _hover && !_pending ? 1.12f : 1f;
            _label.transform.localScale = Vector3.Lerp(_label.transform.localScale, Vector3.one * s, 0.25f);
        }

        private void GetBounds(out Vector3 min, out Vector3 max)
        {
            Vector3 p = transform.position;
            min = p - new Vector3(_hitSize.x * 0.5f, _hitSize.y * 0.5f, 0f);
            max = p + new Vector3(_hitSize.x * 0.5f, _hitSize.y * 0.5f, 0f);
        }

        private readonly System.Collections.Generic.List<Vector2> _tips = new();
        private System.Collections.Generic.List<Vector2> CandidateTips()
        {
            _tips.Clear();
            if (ray == null) return _tips;
            if (ray.HasTwoTips) { _tips.Add(ray.TipScreenA); _tips.Add(ray.TipScreenB); }
            else if (ray.HasOneTip) _tips.Add(ray.TipScreenSingle);
            return _tips;
        }

        /// <summary>TMP 的 mark 标签画一块半透明底色，省得为「临时按钮」再生成精灵/材质。</summary>
        private string MarkedLabel()
        {
            return $"<mark=#0B1B2ACC>{label}</mark>";
        }
    }
}
