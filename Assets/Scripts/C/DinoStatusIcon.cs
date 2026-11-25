// DinoStatusIcon.cs —— 恐龙状态图标系统（占位版）
// 结构：独立子节点（不参与恐龙的左右翻转 / 挤压拉伸 / 躺下旋转）
//   底图 SpriteRenderer（统一气泡底） + 内容 SpriteRenderer（七种状态切换）
// 动画：出现=缩放+淡入、消失=缩小+淡出、切换=淡出换图淡入；同一状态持续时不重播。
//   工程未安装 DOTween，补间集中在本组件内实现；将来接入 DOTween 只需替换本文件。
// 图标内容后续由手绘 Sprite 替换：把本组件挂到场景 dino 上、在 Inspector 填 Sprite 即可；
//   运行时自动挂载（未手动配置）时使用内置占位图（程序生成的像素图形）。

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PrototypeC
{
    public enum DinoIconType
    {
        None = 0,
        Thinking,   // ...  发呆、没有明确意图
        Notice,     // !    注意到新事物
        Curious,    // ?    对具体物件好奇
        Love,       // ♥    喜欢某个物件
        Sleep,      // ZZZ  真正进入睡眠
        Happy,      // ☺    愉快、满足
        Tired,      // 💦   疲惫
    }

    [DisallowMultipleComponent]
    public class DinoStatusIcon : MonoBehaviour
    {
        /// <summary>图标锚定方式。</summary>
        public enum IconAnchor
        {
            FeetOffset = 0,
            SpriteTop = 1,
        }

        [Header("手绘 Sprite（留空 = 内置占位图）")]
        public Sprite thinkingSprite;
        public Sprite noticeSprite;
        public Sprite curiousSprite;
        public Sprite loveSprite;
        public Sprite sleepSprite;
        public Sprite happySprite;
        public Sprite tiredSprite;
        [Tooltip("图标底图；留空 = 内置圆角气泡占位")]
        public Sprite backgroundSprite;

        [Header("位置与尺寸")]
        [Tooltip("图标锚定方式：FeetOffset=脚底上方固定高度；SpriteTop=跟随恐龙精灵顶部（转身/躺下自动跟随）")]
        public IconAnchor anchorMode = IconAnchor.SpriteTop;
        [Tooltip("FeetOffset 模式：图标中心相对恐龙脚底的世界高度（米）")]
        public float heightAboveFeet = 1.55f;
        [Tooltip("SpriteTop 模式：图标中心高于精灵包围盒顶部的距离（米）")]
        public float topMargin = 0.3f;
        [Tooltip("SpriteTop 模式：图标朝恐龙面朝方向偏移（米）。恐龙朝右时正数=图标在右，0=正上方")]
        public float headOffset = 0.45f;
        [Tooltip("内容图标的边长（世界米，按最长边等比）")]
        public float worldSize = 0.55f;
        [Tooltip("底图相对内容图标的放大倍数；必须 ≥1，否则气泡会比内容小导致内容溢出")]
        [Min(1f)] public float backgroundScale = 1.5f;
        [Tooltip("渲染排序：要盖过恐龙与一般生成物")]
        public int sortingOrder = 120;

        [Header("动画（秒）")]
        public float popInDuration = 0.16f;
        public float popOutDuration = 0.14f;
        public float swapDuration = 0.09f;
        [Tooltip("同一图标的最短显示时间：防止状态抖动造成闪烁")]
        public float minHoldTime = 0.5f;

        private Transform _root;      // 补偿节点：抵消父级镜像/缩放/旋转
        private Transform _anim;      // 动画节点：补间只改它的缩放
        private SpriteRenderer _background;
        private SpriteRenderer _content;
        private SpriteRenderer _host; // 恐龙本体渲染器：用于 SpriteTop 锚定

        private DinoIconType _current = DinoIconType.None;
        private DinoIconType _persistent = DinoIconType.None;
        private float _hideAt = -1f;
        private float _shownSince = -1f;
        private DinoIconType _queued = DinoIconType.None;
        private float _queuedDuration;
        private Coroutine _animRoutine;

        private static readonly Dictionary<DinoIconType, Sprite> Placeholders = new Dictionary<DinoIconType, Sprite>();
        private static Sprite _placeholderBackground;

        /// <summary>挂在恐龙身上（已有则复用），并确保渲染结构已建好。</summary>
        public static DinoStatusIcon EnsureAttached(GameObject host)
        {
            if (host == null) return null;
            var icon = host.GetComponent<DinoStatusIcon>();
            if (icon == null) icon = host.AddComponent<DinoStatusIcon>();
            icon.Build();
            return icon;
        }

        private void Awake()
        {
            _host = GetComponent<SpriteRenderer>();
            Build();
        }

        private void Build()
        {
            if (_root != null) return;

            _root = new GameObject("DinoC_StatusIcon").transform;
            _root.SetParent(transform, false);
            _anim = new GameObject("anim").transform;
            _anim.SetParent(_root, false);

            // 关键：背景必须挂在 anim 的子节点上。
            // anim 的 localScale 会被出场/切换/收起的补间反复改写（结束时统一设回 1），
            // 如果 SpriteRenderer 直接挂 anim，按 backgroundScale 算好的尺寸每次都会被抹成 1，
            // 结果就是 content(1.47) 比 background(1) 还大。
            var backgroundGo = new GameObject("background");
            backgroundGo.transform.SetParent(_anim, false);
            _background = backgroundGo.AddComponent<SpriteRenderer>();
            _background.sprite = backgroundSprite != null ? backgroundSprite : GetBackgroundPlaceholder();
            _background.sortingOrder = sortingOrder;

            var contentGo = new GameObject("content");
            contentGo.transform.SetParent(_anim, false);
            _content = contentGo.AddComponent<SpriteRenderer>();
            _content.sortingOrder = sortingOrder + 1;

            FitScale(_background, worldSize * Mathf.Max(1f, backgroundScale));
            FitScale(_content, worldSize);
            SetAlpha(0f);
            _background.enabled = false;
            _content.enabled = false;
        }

        /// <summary>短暂显示（到时自动收起）；持续状态激活时被忽略。</summary>
        public void Show(DinoIconType type, float duration)
        {
            if (type == DinoIconType.None || _root == null) return;
            if (_persistent != DinoIconType.None) return;

            float now = Time.time;
            if (_current == type)
            {
                _hideAt = Mathf.Max(_hideAt, now + Mathf.Max(0.2f, duration));
                return;
            }

            if (_current != DinoIconType.None && now - _shownSince < minHoldTime)
            {
                // 最短显示时间内来了新请求：只保留最新一个，等当前图标收起后再播
                _queued = type;
                _queuedDuration = duration;
                return;
            }

            Display(type, now + Mathf.Max(0.2f, duration));
        }

        /// <summary>持续显示（睡眠 / 疲惫这类「状态型」图标），直到 HidePersistent。</summary>
        public void ShowPersistent(DinoIconType type)
        {
            if (type == DinoIconType.None || _root == null) return;
            if (_persistent == type && _current == type) return;

            _persistent = type;
            _queued = DinoIconType.None;
            Display(type, float.PositiveInfinity);
        }

        /// <summary>结束持续状态；若当前没有别的等待项则收起图标。</summary>
        public void HidePersistent()
        {
            if (_persistent == DinoIconType.None) return;
            _persistent = DinoIconType.None;

            if (_current != DinoIconType.None)
            {
                _current = DinoIconType.None;
                _shownSince = -1f;
                HideNow();
            }
        }

        /// <summary>收起当前的短暂图标（外部玩法接管时用）；不影响持续状态。</summary>
        public void HideBrief()
        {
            if (_persistent != DinoIconType.None) return;
            _queued = DinoIconType.None;
            if (_current == DinoIconType.None) return;

            _current = DinoIconType.None;
            _shownSince = -1f;
            HideNow();
        }

        private void Display(DinoIconType type, float hideAt)
        {
            _current = type;
            _hideAt = hideAt;
            _shownSince = Time.time;

            bool wasVisible = _background.enabled && _background.color.a > 0.01f;
            if (_animRoutine != null) StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(wasVisible ? PlaySwapIn(type) : PlayPopIn(type));
        }

        /// <summary>把内容层换成指定类型的 Sprite（含占位/手绘与尺寸归一）。</summary>
        private void ApplyContent(DinoIconType type)
        {
            _content.sprite = ResolveSprite(type, out bool isPlaceholder);
            _content.color = isPlaceholder ? ResolveTint(type) : Color.white;
            FitScale(_content, worldSize);
            _background.enabled = true;
            _content.enabled = true;
        }

        private IEnumerator PlayPopIn(DinoIconType type)
        {
            ApplyContent(type);
            float t = 0f;
            while (t < popInDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / popInDuration);
                float ease = k * k * (3f - 2f * k);
                _anim.localScale = Vector3.one * Mathf.Lerp(0.55f, 1.06f, ease);
                SetAlpha(ease);
                yield return null;
            }

            _anim.localScale = Vector3.one;
            SetAlpha(1f);
            _animRoutine = null;
        }

        private IEnumerator PlaySwapIn(DinoIconType type)
        {
            // 切换：先把当前内容淡出 → 替换 Sprite → 再淡入（内容替换在淡出之后）
            float t = 0f;
            while (t < swapDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / swapDuration);
                SetAlpha(1f - k);
                _anim.localScale = Vector3.one * Mathf.Lerp(1f, 0.85f, k);
                yield return null;
            }

            ApplyContent(type);

            t = 0f;
            while (t < popInDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / popInDuration);
                float ease = k * k * (3f - 2f * k);
                _anim.localScale = Vector3.one * Mathf.Lerp(0.85f, 1.04f, ease);
                SetAlpha(ease);
                yield return null;
            }

            _anim.localScale = Vector3.one;
            SetAlpha(1f);
            _animRoutine = null;
        }

        private void HideNow()
        {
            if (_animRoutine != null) StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(PlayHide());
        }

        private IEnumerator PlayHide()
        {
            float t = 0f;
            float startAlpha = _content.color.a;
            Vector3 startScale = _anim.localScale;
            while (t < popOutDuration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / popOutDuration);
                SetAlpha(startAlpha * (1f - k));
                _anim.localScale = Vector3.Lerp(startScale, startScale * 0.8f, k);
                yield return null;
            }

            _background.enabled = false;
            _content.enabled = false;
            _anim.localScale = Vector3.one;
            _animRoutine = null;

            // 收起后如果期间有排队的短暂请求，接着播它
            if (_persistent == DinoIconType.None && _queued != DinoIconType.None)
            {
                var type = _queued;
                float duration = _queuedDuration;
                _queued = DinoIconType.None;
                Show(type, duration);
            }
        }

        private void LateUpdate()
        {
            if (_root == null) return;

            // ① 抵消父级（恐龙）的镜像 / 挤压缩放：无论朝向、蹦跳、躺下，图标保持恒定世界尺寸且永不镜像
            Vector3 ps = transform.lossyScale;
            float sx = Mathf.Abs(ps.x) < 0.0001f ? 1f : 1f / ps.x;
            float sy = Mathf.Abs(ps.y) < 0.0001f ? 1f : 1f / ps.y;
            _root.localScale = new Vector3(sx, sy, 1f);

            // ② 抵消躺下时的父级旋转：图标永远正立
            _root.rotation = Quaternion.identity;

            // ③ 位置：跟随恐龙当前姿态
            //    SpriteTop = 精灵包围盒顶部（躺下时 AABB 变矮、中心偏移，图标自动跟着身体走）
            //    FeetOffset = 旧的固定高度
            if (anchorMode == IconAnchor.SpriteTop && _host != null && _host.sprite != null)
            {
                // 朝向由恐龙根节点 localScale.x 的正负决定；朝右时图标向右偏（在头这侧），朝左反之
                float face = Mathf.Sign(transform.lossyScale.x);
                if (Mathf.Approximately(face, 0f)) face = 1f;

                Bounds b = _host.bounds;
                _root.position = new Vector3(
                    transform.position.x + face * headOffset,
                    b.max.y + topMargin,
                    transform.position.z);
            }
            else
            {
                _root.position = transform.position + Vector3.up * heightAboveFeet;
            }

            // ④ 短暂图标的到时收起
            if (_persistent == DinoIconType.None && _current != DinoIconType.None
                && _hideAt <= Time.time && Time.time - _shownSince >= minHoldTime)
            {
                _current = DinoIconType.None;
                _shownSince = -1f;
                HideNow();
            }
        }

        private void SetAlpha(float a)
        {
            Color bg = _background.color;
            bg.a = a;
            _background.color = bg;

            Color c = _content.color;
            c.a = a;
            _content.color = c;
        }

        private void FitScale(SpriteRenderer renderer, float targetSize)
        {
            if (renderer == null || renderer.sprite == null) return;
            float w = renderer.sprite.bounds.size.x;
            float h = renderer.sprite.bounds.size.y;
            float longest = Mathf.Max(w, h);
            if (longest > 0.0001f) renderer.transform.localScale = Vector3.one * (targetSize / longest);
        }

        private Sprite ResolveSprite(DinoIconType type, out bool isPlaceholder)
        {
            Sprite assigned = null;
            switch (type)
            {
                case DinoIconType.Thinking: assigned = thinkingSprite; break;
                case DinoIconType.Notice: assigned = noticeSprite; break;
                case DinoIconType.Curious: assigned = curiousSprite; break;
                case DinoIconType.Love: assigned = loveSprite; break;
                case DinoIconType.Sleep: assigned = sleepSprite; break;
                case DinoIconType.Happy: assigned = happySprite; break;
                case DinoIconType.Tired: assigned = tiredSprite; break;
            }

            if (assigned != null)
            {
                isPlaceholder = false;
                return assigned;
            }

            isPlaceholder = true;
            if (!Placeholders.TryGetValue(type, out Sprite placeholder))
            {
                placeholder = CreatePlaceholder(GetBitmap(type));
                Placeholders[type] = placeholder;
            }

            return placeholder;
        }

        /// <summary>占位图的类型色（手绘 Sprite 不染色，见 Display）。</summary>
        private Color ResolveTint(DinoIconType type)
        {
            switch (type)
            {
                case DinoIconType.Thinking: return new Color(0.42f, 0.40f, 0.38f, 1f);
                case DinoIconType.Notice: return new Color(0.88f, 0.30f, 0.16f, 1f);
                case DinoIconType.Curious: return new Color(0.18f, 0.45f, 0.86f, 1f);
                case DinoIconType.Love: return new Color(0.91f, 0.27f, 0.44f, 1f);
                case DinoIconType.Sleep: return new Color(0.38f, 0.38f, 0.64f, 1f);
                case DinoIconType.Happy: return new Color(0.95f, 0.61f, 0.12f, 1f);
                case DinoIconType.Tired: return new Color(0.26f, 0.60f, 0.84f, 1f);
                default: return Color.white;
            }
        }

        // —— 占位图：白色像素图（由 ResolveTint 染色），后续整体替换为手绘 Sprite ——

        private static string[] GetBitmap(DinoIconType type)
        {
            switch (type)
            {
                case DinoIconType.Thinking:
                    return new[]
                    {
                        "...........",
                        "...........",
                        "...........",
                        "...........",
                        "...........",
                        ".##..##..##",
                        ".##..##..##",
                        "...........",
                        "...........",
                        "...........",
                        "...........",
                    };
                case DinoIconType.Notice:
                    return new[]
                    {
                        "...........",
                        "...........",
                        "....##.....",
                        "....##.....",
                        "....##.....",
                        "....##.....",
                        "....##.....",
                        "...........",
                        "....##.....",
                        "....##.....",
                        "...........",
                    };
                case DinoIconType.Curious:
                    return new[]
                    {
                        "...........",
                        "..#####....",
                        ".##...##...",
                        ".......##..",
                        "......##...",
                        "....##.....",
                        "....##.....",
                        "...........",
                        "....##.....",
                        "....##.....",
                        "...........",
                    };
                case DinoIconType.Love:
                    return new[]
                    {
                        "...........",
                        ".###...###.",
                        "#####.#####",
                        "###########",
                        "###########",
                        ".#########.",
                        "..#######..",
                        "...#####...",
                        "....###....",
                        ".....#.....",
                        "...........",
                    };
                case DinoIconType.Sleep:
                    return new[]
                    {
                        "...........",
                        "...........",
                        "...........",
                        "...........",
                        "###.###.###",
                        ".#...#...#.",
                        "###.###.###",
                        "...........",
                        "...........",
                        "...........",
                        "...........",
                    };
                case DinoIconType.Happy:
                    return new[]
                    {
                        "...#####...",
                        "..#######..",
                        ".#########.",
                        ".##.###.##.",
                        ".#########.",
                        ".#########.",
                        ".###...###.",
                        "..#######..",
                        "...#####...",
                        "...........",
                        "...........",
                    };
                case DinoIconType.Tired:
                    return new[]
                    {
                        "...........",
                        "...........",
                        "...........",
                        ".#.........",
                        ".#.........",
                        ".###....#..",
                        ".......#...",
                        "......###..",
                        "..........#",
                        "...........",
                        "...........",
                    };
                default:
                    return new[] { "...........", "....##.....", "....##.....", "..........." };
            }
        }

        private static Sprite CreatePlaceholder(string[] bitmap)
        {
            const int cell = 5;
            const int pad = 4;

            int gridW = 0;
            foreach (string row in bitmap) gridW = Mathf.Max(gridW, row.Length);
            int gridH = bitmap.Length;

            int w = gridW * cell + pad * 2;
            int h = gridH * cell + pad * 2;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var clear = new Color32(0, 0, 0, 0);
            var solid = new Color32(255, 255, 255, 255);
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            for (int gy = 0; gy < gridH; gy++)
            {
                string row = bitmap[gridH - 1 - gy];   // 位图第一行在上方
                for (int gx = 0; gx < row.Length; gx++)
                {
                    if (row[gx] != '#') continue;
                    for (int dy = 0; dy < cell; dy++)
                    for (int dx = 0; dx < cell; dx++)
                    {
                        int px = pad + gx * cell + dx;
                        int py = pad + gy * cell + dy;
                        pixels[py * w + px] = solid;
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), Mathf.Max(1f, w * 0.75f));
        }

        private static Sprite GetBackgroundPlaceholder()
        {
            if (_placeholderBackground != null) return _placeholderBackground;

            const int size = 64;
            const int radius = 16;
            const int outline = 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var fill = new Color32(252, 250, 244, 240);
            var line = new Color32(58, 46, 38, 255);
            var empty = new Color32(0, 0, 0, 0);
            var pixels = new Color32[size * size];

            // 圆角矩形带描边：先按「内缩 outline」的圆角矩形填面色，再对外圈描深色边
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundedDistance(x, y, size, size, radius);
                if (d <= 0f)
                    pixels[y * size + x] = fill;
                else if (d <= outline)
                    pixels[y * size + x] = line;
                else
                    pixels[y * size + x] = empty;
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            _placeholderBackground = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size * 0.75f);
            return _placeholderBackground;
        }

        /// <summary>点到圆角矩形边缘的距离：&lt;0 在内部，0~outline 是描边带。</summary>
        private static float RoundedDistance(int x, int y, int sizeX, int sizeY, float radius)
        {
            float cx = Mathf.Clamp(x, radius, sizeX - 1 - radius);
            float cy = Mathf.Clamp(y, radius, sizeY - 1 - radius);
            float dx = x - cx;
            float dy = y - cy;
            return Mathf.Sqrt(dx * dx + dy * dy) - radius;
        }
    }
}
