// FingertipChoiceButton.cs —— 世界空间「指尖选择」按钮（吹泡泡奖励的二选一）
// 交互复刻打字机那套：指尖在判定框内停留 dwellSeconds → 触发一次；触发后必须离开外扩框
// reArmSeconds 才重新上膛，避免手停在原处连续触发。
// 放在世界空间（不是 UI Canvas）是为了和右手指尖射线（RightHandIndexRayProvider）的
// 「屏幕坐标 → 世界坐标」判定直接对齐，也方便在场景里摆位置。

using System;
using TMPro;
using UnityEngine;

namespace PrototypeC
{
    public class FingertipChoiceButton : MonoBehaviour
    {
        [Tooltip("按钮文字；留空则只做可点击的热区")]
        public TMP_Text label;
        [Tooltip("判定框大小（世界单位）")]
        public Vector2 hitSize = new Vector2(2.2f, 1.0f);
        [Tooltip("指尖要在框内停留多久才触发（秒）")]
        public float dwellSeconds = 0.3f;
        [Tooltip("触发后指尖要离开外扩框多久才重新上膛（秒）")]
        public float reArmSeconds = 0.5f;
        [Tooltip("判定「离开」时外扩的余量（世界单位）")]
        public float leaveMargin = 0.25f;
        [Tooltip("悬停时临时变成这个颜色；离开后恢复成 TMP 上原本设置的颜色")]
        public Color hoverColor = new Color(1f, 0.92f, 0.45f, 1f);
        [Tooltip("关掉后悬停也不改颜色（文字颜色完全由 TMP 决定）")]
        public bool tintOnHover = true;
        [Tooltip("关掉后不再响应指尖（两个选项只会有一个生效）")]
        public bool interactable = true;

        public event Action Clicked;

        private RightHandIndexRayProvider _ray;
        private Color _idleColor = Color.white;
        private bool _idleCaptured;
        private float _insideTime;
        private float _outsideTime;
        private bool _armed = true;
        private bool _hover;
        private readonly System.Collections.Generic.List<Vector2> _tips = new();

        /// <summary>按统一外观创建一个按钮（文字带半透明底，省得再生成精灵/材质）。</summary>
        public static FingertipChoiceButton Create(
            string text, Transform parent, Vector3 worldPos, float fontSize,
            Color idle, Color hover, int sortingOrder = 610)
        {
            var go = new GameObject($"Choice_{text}");
            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;

            var button = go.AddComponent<FingertipChoiceButton>();
            button.label = B1Text.Make("Label", go.transform, fontSize, idle, sortingOrder);
            button.label.alignment = TextAlignmentOptions.Center;
            button.label.text = text;

            button._idleColor = idle;
            button._idleCaptured = true;
            button.hoverColor = hover;
            button.hitSize = new Vector2(
                Mathf.Max(1.8f, text.Length * fontSize * 0.11f + 1.0f),
                Mathf.Max(0.8f, fontSize * 0.36f));
            return button;
        }

        private void Awake()
        {
            if (label == null) label = GetComponentInChildren<TMP_Text>();
            CaptureIdle();
        }

        /// <summary>记住 TMP 上原本设置的颜色，悬停结束后恢复它（不再强行改成白色）。</summary>
        private void CaptureIdle()
        {
            if (_idleCaptured || label == null) return;
            _idleColor = label.color;
            _idleCaptured = true;
        }

        private void Update()
        {
            if (_ray == null) _ray = FindObjectOfType<RightHandIndexRayProvider>();

            if (!interactable)
            {
                _insideTime = 0f;
                SetHover(false);
                return;
            }

            UpdateFingertip();
            UpdateVisual();
        }

        private void UpdateFingertip()
        {
            if (_ray == null) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            GetBounds(out Vector3 min, out Vector3 max);
            Vector3 outerMin = min - new Vector3(leaveMargin, leaveMargin, 0f);
            Vector3 outerMax = max + new Vector3(leaveMargin, leaveMargin, 0f);
            float zDist = -cam.transform.position.z;

            bool inside = false;
            bool outsideOuter = true;
            int tipCount = 0;

            _tips.Clear();
            if (_ray.HasTwoTips) { _tips.Add(_ray.TipScreenA); _tips.Add(_ray.TipScreenB); }
            else if (_ray.HasOneTip) _tips.Add(_ray.TipScreenSingle);

            foreach (Vector2 tip in _tips)
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
            if (_insideTime >= dwellSeconds)
            {
                _armed = false;
                _insideTime = 0f;
                _outsideTime = 0f;
                Clicked?.Invoke();
            }
        }

        private void UpdateVisual()
        {
            if (label == null) return;

            if (tintOnHover)
            {
                CaptureIdle();
                label.color = _hover ? hoverColor : _idleColor;
            }

            float s = _hover ? 1.08f : 1f;
            label.transform.localScale = Vector3.Lerp(label.transform.localScale, Vector3.one * s, 0.3f);
        }

        private void SetHover(bool value)
        {
            _hover = value;
            if (label != null && tintOnHover)
            {
                CaptureIdle();
                label.color = _idleColor;
            }
        }

        /// <summary>立刻失效（选完以后两个按钮一起收掉）。</summary>
        public void ConsumeAndDestroy(float delay = 0f)
        {
            interactable = false;
            if (delay <= 0f) Destroy(gameObject);
            else Destroy(gameObject, delay);
        }

        private void GetBounds(out Vector3 min, out Vector3 max)
        {
            Vector3 p = transform.position;
            min = p - new Vector3(hitSize.x * 0.5f, hitSize.y * 0.5f, 0f);
            max = p + new Vector3(hitSize.x * 0.5f, hitSize.y * 0.5f, 0f);
        }

        private void OnDrawGizmosSelected()
        {
            GetBounds(out Vector3 min, out Vector3 max);
            Gizmos.color = new Color(1f, 0.85f, 0.3f);
            Gizmos.DrawWireCube((min + max) * 0.5f, max - min);
        }
    }
}
