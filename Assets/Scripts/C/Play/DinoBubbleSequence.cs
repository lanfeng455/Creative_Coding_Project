// DinoBubbleSequence.cs —— 投篮进球后的「吹泡泡奖励」过场总控
// 流程：
//   ① 礼花 + 游戏暂停（字符雨停、投篮/打字机/生成停；球体继续自然下落），全屏压暗 + BONUS TIME
//   ② 泡泡水（HDR 高亮）从天上落回编辑器位置
//   ③ 恐龙靠近，跳两下表示高兴
//   ④ 恐龙走出画面右侧
//   ⑤ Dino奖励气泡面板出现，dino (1) 大恐龙从右侧走进编辑器位置
//   ⑥ Canvas 显现，提示玩家对着麦克风吹气
//   ⑦ 麦克风音量过阈值 → 泡泡区域从小到大长出泡泡
//   ⑧ Canvas 倒计时 3 秒 → 拍下摄像头画面（复用 BubbleReward）
//   ⑨ 指尖停留选择「保留 / 不保留」：保留 = 泡泡升到天花板后恢复；不保留 = 泡泡消失并立刻恢复
//   超时 20 秒按「不保留」处理。
// 所有可调参数都在 Inspector；引用留空会按名字/类型自动查找。

using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PrototypeC
{
    [DisallowMultipleComponent]
    public class DinoBubbleSequence : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        [Tooltip("场景里的恐龙控制器")]
        public DinoCController dino;
        [Tooltip("泡泡奖励组件（球体 + 摄像头画面 + 拍照）")]
        public BubbleReward bubbleReward;
        [Tooltip("泡泡水（从天而降的那个物体）")]
        public Transform bubbleWater;
        [Tooltip("Dino奖励气泡 根节点")]
        public Transform rewardRoot;
        [Tooltip("根节点下的全屏压暗层")]
        public Transform rewardBackdrop;
        [Tooltip("根节点下的大恐龙 dino (1)")]
        public Transform rewardDino;
        [Tooltip("根节点下的 Canvas（吹气提示）")]
        public Canvas promptCanvas;
        [Tooltip("Canvas 里的提示文本")]
        public TMP_Text promptText;
        [Tooltip("泡泡出生点（dino(1) 棍子末端、圆环正中心）")]
        public Transform bubbleArea;
        [Tooltip("场景里的「保留」按钮；留空会在运行时临时生成")]
        public FingertipChoiceButton keepButton;
        [Tooltip("场景里的「不保留」按钮；留空会在运行时临时生成")]
        public FingertipChoiceButton discardButton;
        [Tooltip("篮筐（只用来复用它的礼花）")]
        public BasketHoop hoop;
        [Tooltip("字符雨（暂停/恢复）")]
        public GlyphRain rain;
        [Tooltip("语音监听（复用它的麦克风电平判断吹气）")]
        public WhisperVoiceListener whisper;

        [Header("暂停反馈")]
        [Tooltip("场景里做好的 BONUS TIME 文本（世界空间 TMP）。文字/字号/颜色/位置直接在场景里改")]
        public TMP_Text bonusTimeText;
        [Tooltip("暂停时全屏压暗到的透明度（0~1）")]
        [Range(0f, 1f)] public float backdropDimAlpha = 0.55f;
        public float pauseFadeSeconds = 0.35f;

        [Header("泡泡水降落")]
        [Tooltip("从编辑器位置上方多少米开始下落")]
        public float waterFallHeight = 7f;
        public float waterFallSeconds = 0.85f;
        [Tooltip("落地压扁的幅度（0.2 = 压扁 20%）")]
        [Range(0f, 0.5f)] public float waterLandSquash = 0.18f;
        public Color waterGlowColor = new Color(0.55f, 0.95f, 1f);
        [Tooltip("光晕直径（世界单位）")]
        public float waterGlowSize = 3.2f;
        [Tooltip("光晕亮度倍数（>1 才会被 Bloom 吃到，形成 HDR 炫光）")]
        public float waterGlowIntensity = 2.2f;

        [Header("恐龙反应")]
        [Tooltip("靠近泡泡水时站在它左边多少米")]
        public float cheerStandOffset = -1.4f;
        [Tooltip("高兴地跳几次")]
        public int cheerHops = 2;
        public float cheerHopInterval = 0.45f;
        [Tooltip("走出画面时超出屏幕边缘多少米")]
        public float dinoExitPadding = 2f;
        [Tooltip("小恐龙离场时把泡泡水带在身侧的偏移（相对脚底，世界单位）")]
        public Vector3 dinoCarryOffset = new Vector3(0.95f, 0.55f, 0f);
        [Tooltip("把泡泡水吸附到身侧用多久（秒）")]
        public float dinoPickupSeconds = 0.25f;

        [Header("奖励大恐龙入场")]
        [Tooltip("入场起点超出屏幕右边缘多少米")]
        public float rewardDinoEnterPadding = 2.5f;
        [Tooltip("大恐龙离场时额外多走出的距离（防止手里棍子还露在画面里就被隐藏）")]
        public float rewardDinoExitExtra = 3f;
        public float rewardDinoWalkSpeed = 2.6f;
        [Tooltip("入场时上下起伏的幅度（世界单位）")]
        public float rewardDinoBobAmount = 0.18f;
        [Tooltip("入场时起伏频率")]
        public float rewardDinoBobFrequency = 5f;

        [Header("吹气判定（复用 Whisper 的麦克风电平）")]
        [Tooltip("判定为吹气的 RMS 阈值。环境吵就调大；可开 logBlowLevel 边看数字边调")]
        [Range(0.001f, 0.5f)] public float blowRmsThreshold = 0.06f;
        [Tooltip("音量要持续超过阈值多久才算吹气（秒）")]
        public float blowHoldSeconds = 0.12f;
        [Tooltip("在 Console 里每 0.5 秒打印一次当前电平，方便调阈值")]
        public bool logBlowLevel = true;

        [Header("泡泡")]
        [Tooltip("泡泡刚出现时的直径（世界单位）")]
        public float bubbleStartDiameter = 0.18f;
        [Tooltip("从小长到最终大小用多久")]
        public float bubbleGrowSeconds = 1.1f;
        [Tooltip("泡泡最终半径（世界单位）")]
        public float bubbleTargetRadius = 1.25f;
        [Tooltip("只是场景里的尺寸参考：棍子圆环的直径（不参与运行时逻辑），用来对照美术大小")]
        public float wandRingDiameter = 0.5f;
        [Tooltip("倒计时数字字号（UI）")]
        public float countdownFontSize = 130f;
        [Tooltip("倒计时数字在 Canvas 里的锚点偏移")]
        public Vector2 countdownAnchoredPos = new Vector2(0f, -160f);
        public Color countdownColor = new Color(1f, 1f, 1f, 1f);

        [Header("保留选择")]
        public string choicePrompt = "是否保留本次泡泡？";
        public string keepLabel = "保留";
        public string discardLabel = "不保留";
        public float choiceFontSize = 3.0f;
        [Tooltip("「保留」按钮相对泡泡的位置")]
        public Vector3 keepOffset = new Vector3(-2.0f, -0.2f, -1f);
        [Tooltip("「不保留」按钮相对泡泡的位置")]
        public Vector3 discardOffset = new Vector3(2.0f, -0.2f, -1f);
        [Tooltip("等待吹气 / 等待选择的超时（秒）；超时按「不保留」处理")]
        public float timeoutSeconds = 20f;

        [Header("调试")]
        [Tooltip("播放整套过场")]
        public KeyCode debugPlayKey = KeyCode.F10;
        [Tooltip("跳过当前等待阶段")]
        public KeyCode debugSkipKey = KeyCode.F11;
        [Tooltip("按住这个键当作「正在吹气」")]
        public KeyCode debugBlowKey = KeyCode.B;
        public bool logStages = true;

        private bool _running;
        private bool _skipRequested;
        private bool _countdownDone;
        private bool _choiceMade;
        private bool _keepChoice;
        private bool _blowSucceeded;
        private float _blowLevel;

        private readonly List<Behaviour> _paused = new List<Behaviour>();
        private Vector3 _waterHome;
        private bool _waterHomeCaptured;
        private bool _rainWasEnabled = true;
        private Vector3 _rewardDinoHome;
        private float _dinoHomeX;
        private float _rewardDinoHalfWidth;
        private bool _autoResumeRainBackup = true;
        private bool _clickToPopBackup = true;

        private TMP_Text _bonusText;
        private Vector3 _bonusBaseScale = Vector3.one;
        private bool _bonusScaleCaptured;
        private string _blowPrompt;
        private bool _blowPromptCaptured;
        private bool _choiceButtonsFromScene;
        private TMP_Text _countdownText;
        private SpriteRenderer _glow;
        private Sprite _glowSprite;
        private FingertipChoiceButton _keepButton;
        private FingertipChoiceButton _discardButton;
        private Tween _promptTween;
        private Tween _bonusTween;
        private Vector3 _promptBaseScale = Vector3.one;
        private bool _rewardDinoHomeCaptured;
        private bool _dinoReturned;

        public bool IsRunning => _running;
        public bool CanPlay => !_running;

        private void Awake()
        {
            ResolveRefs();
            CaptureBlowPrompt();

            // 面板和泡泡水在编辑器里可以随便关（不影响查找）；运行时统一由本流程控制显示
            if (rewardRoot != null) rewardRoot.gameObject.SetActive(false);
            if (bubbleWater != null) bubbleWater.gameObject.SetActive(false);
            if (keepButton != null) keepButton.gameObject.SetActive(false);
            if (discardButton != null) discardButton.gameObject.SetActive(false);
        }

        private void Start()
        {
            ResolveRefs();
        }

        private void Update()
        {
            if (debugPlayKey != KeyCode.None && Input.GetKeyDown(debugPlayKey)) Play();
            if (debugSkipKey != KeyCode.None && Input.GetKeyDown(debugSkipKey)) _skipRequested = true;
        }

        // —— 入口 ——

        public void Play()
        {
            if (_running) return;

            ResolveRefs();
            if (bubbleReward == null)
            {
                Debug.LogError("[Sequence] 场景里找不到 BubbleReward，吹泡泡奖励无法进行");
                return;
            }

            StartCoroutine(RunSequence());
        }

        private void ResolveRefs()
        {
            if (dino == null) dino = FindObjectOfType<DinoCController>();
            if (bubbleReward == null) bubbleReward = FindObjectOfType<BubbleReward>();
            if (rain == null) rain = FindObjectOfType<GlyphRain>();
            if (whisper == null) whisper = FindObjectOfType<WhisperVoiceListener>();
            if (hoop == null) hoop = FindObjectOfType<BasketHoop>();

            if (rewardRoot == null)
            {
                rewardRoot = FindRootTransform("Dino奖励气泡");
            }

            if (bubbleWater == null)
            {
                bubbleWater = FindRootTransform("泡泡水");
            }

            if (rewardRoot != null)
            {
                if (rewardBackdrop == null) rewardBackdrop = rewardRoot.Find("背景叠加层");
                if (rewardDino == null) rewardDino = rewardRoot.Find("dino (1)");
                if (promptCanvas == null) promptCanvas = rewardRoot.GetComponentInChildren<Canvas>(true);
                if (promptText == null && promptCanvas != null)
                    promptText = promptCanvas.GetComponentInChildren<TMP_Text>(true);
                if (keepButton == null)
                {
                    var t = rewardRoot.Find("Choice_Keep");
                    if (t != null) keepButton = t.GetComponent<FingertipChoiceButton>();
                }
                if (discardButton == null)
                {
                    var t = rewardRoot.Find("Choice_Discard");
                    if (t != null) discardButton = t.GetComponent<FingertipChoiceButton>();
                }
                if (bubbleArea == null && rewardDino != null)
                    bubbleArea = rewardDino.Find("泡泡区域");   // 未激活也能 Find 到
                if (bubbleArea == null)
                {
                    var marker = GameObject.Find("泡泡区域");
                    if (marker != null) bubbleArea = marker.transform;
                }
            }
        }

        /// <summary>记住场景里「吹气」提示的原文：第一次跑完会被改成「是否保留」，第二次要能还原。</summary>
        private void CaptureBlowPrompt()
        {
            if (_blowPromptCaptured || promptText == null) return;

            _blowPrompt = promptText.text;
            if (string.IsNullOrWhiteSpace(_blowPrompt))
                _blowPrompt = "对着麦克风吹气！\nBlow air to the microphone!";
            _blowPromptCaptured = true;
        }

        /// <summary>按名字找场景根物体（包括未激活的），这样编辑器里关掉也能找到。</summary>
        private static Transform FindRootTransform(string name)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                if (roots[i] != null && roots[i].name == name) return roots[i].transform;

            return null;
        }

        // —— 主流程 ——

        private IEnumerator RunSequence()
        {
            _running = true;
            _skipRequested = false;
            _blowSucceeded = false;
            _choiceMade = false;
            _countdownDone = false;

            _autoResumeRainBackup = bubbleReward.autoResumeRain;
            _clickToPopBackup = bubbleReward.clickToPopEnabled;
            bubbleReward.autoResumeRain = false;    // 由本流程决定何时恢复文字雨
            bubbleReward.clickToPopEnabled = false; // 改用指尖二选一

            _dinoReturned = false;
            if (dino != null) _dinoHomeX = dino.transform.position.x;
            if (rewardDino != null && !_rewardDinoHomeCaptured)
            {
                _rewardDinoHome = rewardDino.position;
                _rewardDinoHomeCaptured = true;
            }

            try
            {
                // ① 礼花 + 暂停
                Stage("暂停 + 礼花");
                EnterPause();
                ShowBackdrop();
                ShowBonusTime();
                if (hoop != null) hoop.PlayConfettiNow();
                yield return Wait(pauseFadeSeconds + 0.25f);

                // ② 泡泡水 HDR 降落
                Stage("泡泡水降落");
                yield return BottleFall();

                // ③ 恐龙靠近 + 跳两下
                Stage("恐龙高兴");
                yield return DinoCheer();

                // ④ 恐龙走出画面
                Stage("恐龙离场");
                yield return DinoExit();

                // ⑤ 奖励面板 + 大恐龙入场
                Stage("大恐龙入场");
                yield return RewardDinoEnter();

                // ⑥ 提示吹气
                Stage("提示吹气");
                yield return PromptIn();

                // ⑦ 等吹气
                Stage("等待吹气");
                yield return WaitForBlow();

                if (!_blowSucceeded)
                {
                    Debug.LogWarning("[Sequence] 超时未检测到吹气 → 按「取消」处理，直接回到正常游戏");
                    yield break;
                }

                // ⑧ 泡泡长大 + 倒计时
                Stage("泡泡长大");
                yield return GrowBubble();

                if (!bubbleReward.IsActive)
                {
                    Debug.LogError("[Sequence] 泡泡没有成功生成（可能缺摄像头画面 / 相机），跳过奖励阶段");
                    yield break;
                }

                Stage("倒计时");
                yield return WaitForCountdown();

                // ⑨ 指尖二选一
                Stage("等待选择");
                yield return WaitForChoice();

                Stage(_keepChoice ? "保留泡泡" : "不保留泡泡");
                yield return FinishChoice();
            }
            finally
            {
                Stage("恢复");
                CleanupAndResume();
                _running = false;
            }
        }

        // —— ① 暂停 ——

        private void EnterPause()
        {
            if (rain == null) rain = FindObjectOfType<GlyphRain>();
            if (rain != null)
            {
                // GlyphRain 没有 Suspend API：直接停组件，字符会冻在原地（暂停感明显）
                _rainWasEnabled = rain.enabled;
                rain.enabled = false;
                Debug.Log("[Sequence] 字符雨已暂停");
            }
            else
            {
                Debug.LogWarning("[Sequence] 找不到 GlyphRain：字符雨不会暂停（检查该物体是否被禁用）");
            }

            // 恐龙整段过场交给脚本：先定在原地；期间不会自主躺下/互动/被打断
            if (dino != null && dino.gameObject.activeSelf)
            {
                dino.ExternalLock(true);
                dino.ExternalWalkTo(dino.transform.position.x, 999f);
            }

            PauseAll<BasketHoop>();
            PauseAll<TypeWriterB1>();
            PauseAll<TypewriterSendButton>();
            PauseAll<PrototypeCTestUI>();

            // 球体故意不暂停：进球判定平面比篮筐视觉更高，触发时球还在空中，
            // 冻结会让它悬停；让它继续自然下落，篮筐已经禁用，不会再计分。
        }

        private void PauseAll<T>() where T : Behaviour
        {
            foreach (var b in FindObjectsByType<T>(FindObjectsSortMode.None)) PauseBehaviour(b);
        }

        private void PauseBehaviour(Behaviour b)
        {
            if (b == null || !b.enabled) return;
            b.enabled = false;
            _paused.Add(b);
        }

        private void ResumePausedBehaviours()
        {
            for (int i = 0; i < _paused.Count; i++)
                if (_paused[i] != null) _paused[i].enabled = true;
            _paused.Clear();
        }

        private void ShowBackdrop()
        {
            if (rewardRoot != null && !rewardRoot.gameObject.activeSelf)
                rewardRoot.gameObject.SetActive(true);

            if (rewardDino != null) rewardDino.gameObject.SetActive(false);

            // Canvas 保持激活，这样你放进 Canvas 的 BONUS TIME 也能显示；
            // 只把「吹气提示」和倒计时单独藏起来。
            if (promptCanvas != null)
            {
                promptCanvas.gameObject.SetActive(true);
                promptCanvas.transform.localScale = Vector3.one;
            }
            if (promptText != null) promptText.gameObject.SetActive(false);
            if (_countdownText != null) _countdownText.gameObject.SetActive(false);

            if (rewardBackdrop == null) return;
            var sr = rewardBackdrop.GetComponent<SpriteRenderer>();
            if (sr == null) return;

            Color c = sr.color;
            c.a = 0f;
            sr.color = c;
            DOTween.To(() => sr.color, x => sr.color = x,
                new Color(c.r, c.g, c.b, backdropDimAlpha), pauseFadeSeconds);
        }

        private void ShowBonusTime()
        {
            _bonusText = bonusTimeText;
            if (_bonusText == null)
            {
                Debug.LogWarning("[Sequence] 没有指定 BONUS TIME 文本（场景里的 TMP），暂停时只显示压暗层");
                return;
            }

            // 位置/文字/字号/颜色全部以场景里摆好的为准，这里只做弹出 + 呼吸
            if (!_bonusScaleCaptured)
            {
                _bonusBaseScale = _bonusText.transform.localScale;
                if (_bonusBaseScale.sqrMagnitude < 0.0001f) _bonusBaseScale = Vector3.one;
                _bonusScaleCaptured = true;
            }

            _bonusText.gameObject.SetActive(true);
            _bonusText.transform.DOKill();
            _bonusText.transform.localScale = _bonusBaseScale * 0.85f;

            var seq = DOTween.Sequence();
            seq.Append(_bonusText.transform.DOScale(_bonusBaseScale, 0.28f).SetEase(Ease.OutBack));
            seq.Append(_bonusText.transform.DOScale(_bonusBaseScale * 1.07f, 0.6f).SetEase(Ease.InOutSine));
            seq.Append(_bonusText.transform.DOScale(_bonusBaseScale, 0.6f).SetEase(Ease.InOutSine));
            seq.SetLoops(-1);
            _bonusTween = seq;
        }

        // —— ② 泡泡水降落 ——

        private IEnumerator BottleFall()
        {
            if (bubbleWater == null) yield break;

            bubbleWater.gameObject.SetActive(true);   // 运行时默认是隐藏的，这里才登场
            _waterHome = bubbleWater.position;
            _waterHomeCaptured = true;
            Vector3 start = _waterHome + Vector3.up * Mathf.Max(0.5f, waterFallHeight);
            bubbleWater.position = start;

            SpawnGlow(start);

            float t = 0f;
            while (t < waterFallSeconds)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / waterFallSeconds);
                float e = k * k;   // 越落越快
                bubbleWater.position = Vector3.Lerp(start, _waterHome, e);
                if (_glow != null) _glow.transform.position = bubbleWater.position + Vector3.up * 0.2f;
                yield return null;
            }

            bubbleWater.position = _waterHome;

            // 落地：压扁回弹 + 光晕炸开
            Vector3 scale = bubbleWater.localScale;
            Vector3 squashed = scale;
            squashed.y *= 1f - waterLandSquash;
            squashed.x *= 1f + waterLandSquash * 0.6f;

            bubbleWater.DOKill();
            bubbleWater.DOScale(squashed, 0.08f)
                .OnComplete(() => bubbleWater.DOScale(scale, 0.2f).SetEase(Ease.OutBack));

            if (_glow != null)
            {
                _glow.transform.DOScale(waterGlowSize * 1.5f, 0.25f).SetEase(Ease.OutQuad);
                _glow.DOFade(0.35f, 0.4f);
            }

            yield return Wait(0.4f);
        }

        private void SpawnGlow(Vector3 pos)
        {
            if (_glowSprite == null) _glowSprite = CreateRadialSprite();

            var go = new GameObject("BubbleWaterGlow");
            go.transform.SetParent(transform, false);
            go.transform.position = pos + Vector3.up * 0.2f;
            go.transform.localScale = Vector3.one * waterGlowSize;

            _glow = go.AddComponent<SpriteRenderer>();
            _glow.sprite = _glowSprite;
            _glow.sortingOrder = 600;
            _glow.color = new Color(
                waterGlowColor.r * waterGlowIntensity,
                waterGlowColor.g * waterGlowIntensity,
                waterGlowColor.b * waterGlowIntensity,
                0.9f);
        }

        private static Sprite CreateRadialSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var px = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d);
                a = a * a;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }

            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // —— ③ 恐龙靠近 ——

        private IEnumerator DinoCheer()
        {
            if (dino == null || !dino.gameObject.activeSelf)
            {
                yield return Wait(0.4f);
                yield break;
            }

            float targetX = bubbleWater != null
                ? bubbleWater.position.x + cheerStandOffset
                : dino.transform.position.x;

            dino.ExternalWalkTo(targetX, 6f);

            float deadline = Time.time + 5f;
            while (Mathf.Abs(dino.transform.position.x - targetX) > 0.35f && Time.time < deadline)
            {
                if (ConsumeSkip()) break;
                yield return null;
            }

            dino.ExternalFace(bubbleWater != null && bubbleWater.position.x > dino.transform.position.x ? 1 : -1);

            int hops = Mathf.Max(1, cheerHops);
            for (int i = 0; i < hops; i++)
            {
                dino.ExternalHop(3.4f);
                if (dino.statusIcon != null) dino.statusIcon.Show(DinoIconType.Happy, 0.9f);
                yield return Wait(cheerHopInterval);
            }

            yield return Wait(0.3f);
        }

        // —— ④ 恐龙离场 ——

        private IEnumerator DinoExit()
        {
            if (dino == null)
            {
                yield return Wait(0.3f);
                yield break;
            }

            // 小恐龙把泡泡水一起带走：先"抱起"（短距离吸附到身侧），再跟着走出画面
            bool carrying = bubbleWater != null && bubbleWater.gameObject.activeSelf;
            Vector3 carryOffset = dinoCarryOffset;

            if (carrying)
            {
                Vector3 bottleFrom = bubbleWater.position;
                Vector3 bottleTo = dino.transform.position + carryOffset;
                float pickup = Mathf.Max(0.01f, dinoPickupSeconds);
                float k = 0f;
                while (k < pickup)
                {
                    k += Time.deltaTime;
                    bubbleWater.position = Vector3.Lerp(bottleFrom, bottleTo, Mathf.Clamp01(k / pickup));
                    yield return null;
                }
            }

            bool left = false;
            dino.ExternalLeaveScreen(1, dinoExitPadding, () => left = true);

            float deadline = Time.time + 12f;
            while (!left && Time.time < deadline)
            {
                // 每帧把泡泡水钉在恐龙身侧，直到两者一起走出画面
                if (carrying && bubbleWater != null)
                    bubbleWater.position = dino.transform.position + carryOffset;
                yield return null;
            }

            if (carrying && bubbleWater != null)
                bubbleWater.gameObject.SetActive(false);

            yield return Wait(0.2f);
        }

        // —— ⑤ 大恐龙入场 ——

        private IEnumerator RewardDinoEnter()
        {
            if (rewardDino == null)
            {
                yield return Wait(0.3f);
                yield break;
            }

            _rewardDinoHome = rewardDino.position;
            rewardDino.gameObject.SetActive(true);   // 先激活，边界盒才是有效值

            _rewardDinoHalfWidth = 3f;
            var sr = rewardDino.GetComponent<SpriteRenderer>();
            if (sr != null) _rewardDinoHalfWidth = sr.bounds.extents.x;

            Camera cam = Camera.main;
            float halfW = cam != null && cam.orthographic ? cam.orthographicSize * cam.aspect : 8.7f;
            float startX = halfW + _rewardDinoHalfWidth + Mathf.Max(0.2f, rewardDinoEnterPadding);

            rewardDino.position = new Vector3(startX, _rewardDinoHome.y, _rewardDinoHome.z);

            float distance = Mathf.Abs(startX - _rewardDinoHome.x);
            float duration = distance / Mathf.Max(0.2f, rewardDinoWalkSpeed);

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                float eased = k * k * (3f - 2f * k);
                float x = Mathf.Lerp(startX, _rewardDinoHome.x, eased);
                float bob = Mathf.Abs(Mathf.Sin(t * rewardDinoBobFrequency)) * rewardDinoBobAmount;
                rewardDino.position = new Vector3(x, _rewardDinoHome.y + bob, _rewardDinoHome.z);

                if (ConsumeSkip()) break;
                yield return null;
            }

            rewardDino.position = _rewardDinoHome;
            yield return Wait(0.15f);
        }

        // —— ⑥ 提示 ——

        private IEnumerator PromptIn()
        {
            // 注意：BONUS TIME 在整个暂停期间都要保留（包括吹气提示、倒计时、选择阶段），
            // 只在离开暂停（FinishChoice 的收尾）时才收起。

            if (promptCanvas != null)
            {
                promptCanvas.gameObject.SetActive(true);
                promptCanvas.transform.localScale = Vector3.one;
            }

            if (promptText != null)
            {
                CaptureBlowPrompt();
                promptText.text = _blowPrompt;   // 还原成「吹气」提示（第二次也要从这句开始）
                promptText.gameObject.SetActive(true);
                _promptBaseScale = promptText.transform.localScale;
                if (_promptBaseScale.sqrMagnitude < 0.01f) _promptBaseScale = Vector3.one;

                promptText.transform.DOKill();
                promptText.transform.localScale = Vector3.zero;
                promptText.transform
                    .DOScale(_promptBaseScale, 0.35f)
                    .SetEase(Ease.OutBack)
                    .OnComplete(() =>
                    {
                        _promptTween = promptText.transform
                            .DOScale(_promptBaseScale * 1.06f, 0.55f)
                            .SetEase(Ease.InOutSine)
                            .SetLoops(-1, LoopType.Yoyo);
                    });
            }

            yield return Wait(0.35f);
        }

        // —— ⑦ 等吹气 ——

        private IEnumerator WaitForBlow()
        {
            _blowLevel = 0f;
            float hold = 0f;
            float elapsed = 0f;
            float nextLog = 0f;
            bool blown = false;

            if (whisper == null)
                Debug.LogWarning("[Sequence] 场景里没有 WhisperVoiceListener：麦克风电平不可用，只能用调试键模拟吹气");
            else if (!whisper.IsReady)
                Debug.LogWarning("[Sequence] 麦克风还没启动，吹气检测不到（检查麦克风设备/权限；也可用 B 键模拟）");

            while (elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;

                SampleBlowLevel();
                bool blowing = _blowLevel >= blowRmsThreshold;
                if (debugBlowKey != KeyCode.None && Input.GetKey(debugBlowKey)) blowing = true;

                hold = blowing ? hold + Time.deltaTime : 0f;
                if (hold >= blowHoldSeconds)
                {
                    blown = true;
                    break;
                }

                if (logBlowLevel && Time.time >= nextLog)
                {
                    nextLog = Time.time + 0.5f;
                    Debug.Log($"[Sequence] 吹气电平 RMS={_blowLevel:F3}（阈值 {blowRmsThreshold:F3}）");
                }

                if (ConsumeSkip())
                {
                    blown = true;
                    break;
                }

                yield return null;
            }

            if (!blown)
                Debug.LogWarning($"[Sequence] 等待吹气超时（{timeoutSeconds:F0} 秒）→ 取消本次奖励");

            _blowSucceeded = blown;
            Debug.Log(blown ? "[Sequence] 检测到吹气" : "[Sequence] 吹气超时 → 取消");
        }

        private void SampleBlowLevel()
        {
            // 语音监听是运行时由 Bootstrap 挂的：第一次可能还没创建，这里按需再找一次
            if (whisper == null) whisper = FindObjectOfType<WhisperVoiceListener>();

            float rms = whisper != null ? whisper.CurrentRms : 0f;
            _blowLevel = Mathf.Max(rms, _blowLevel * 0.82f);
        }

        // —— ⑧ 泡泡长大 ——

        private IEnumerator GrowBubble()
        {
            if (bubbleArea == null)
            {
                Debug.LogError("[Sequence] 找不到泡泡出生点（泡泡区域），跳过泡泡阶段");
                yield break;
            }

            bubbleReward.bubbleRadius = Mathf.Max(0.1f, bubbleTargetRadius);
            bubbleReward.countdownUI = GetOrCreateCountdownText(true);
            bubbleReward.ShowGrowing(bubbleArea.position, bubbleStartDiameter, bubbleGrowSeconds);

            yield return Wait(bubbleGrowSeconds + 0.1f);
        }

        private TMP_Text GetOrCreateCountdownText(bool create)
        {
            if (_countdownText != null) return _countdownText;
            if (promptCanvas == null)
            {
                if (create) Debug.LogWarning("[Sequence] 没有 Canvas，倒计时会退回世界空间数字");
                return null;
            }

            if (!create) return null;

            var go = new GameObject("BubbleCountdownUI");
            go.transform.SetParent(promptCanvas.transform, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
            tmp.fontSize = countdownFontSize;
            tmp.color = countdownColor;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableAutoSizing = false;

            var rt = tmp.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = countdownAnchoredPos;
            rt.sizeDelta = new Vector2(900f, 420f);

            _countdownText = tmp;
            return tmp;
        }

        private IEnumerator WaitForCountdown()
        {
            _countdownDone = false;
            bubbleReward.CountdownCompleted -= OnCountdownCompleted;
            bubbleReward.CountdownCompleted += OnCountdownCompleted;

            float limit = Mathf.Max(1f, bubbleReward.countdownSeconds) + 10f;
            float elapsed = 0f;
            while (!_countdownDone && elapsed < limit)
            {
                // 倒计时不让 F11 跳过：拍照必须在倒计时结束后发生，否则会卡在「等选择」之前
                elapsed += Time.deltaTime;
                yield return null;
            }

            bubbleReward.CountdownCompleted -= OnCountdownCompleted;
        }

        private void OnCountdownCompleted() => _countdownDone = true;

        // —— ⑨ 保留 / 不保留 ——

        private IEnumerator WaitForChoice()
        {
            // 提示文字切换 + 弹一下
            if (promptText != null)
            {
                if (_promptTween != null) { _promptTween.Kill(); _promptTween = null; }
                promptText.text = choicePrompt;
                promptText.transform.DOKill();
                promptText.transform.localScale = Vector3.zero;
                promptText.transform.DOScale(_promptBaseScale, 0.3f).SetEase(Ease.OutBack);
            }

            if (_countdownText != null) _countdownText.gameObject.SetActive(false);

            Transform parent = rewardRoot != null ? rewardRoot : transform;
            Vector3 center = bubbleArea != null ? bubbleArea.position : transform.position;

            _choiceMade = false;
            _keepChoice = false;

            // 优先用场景里摆好的按钮（位置/配色/大小都能在编辑器里改）；没有再临时生成
            _choiceButtonsFromScene = keepButton != null && discardButton != null;

            if (_choiceButtonsFromScene)
            {
                _keepButton = keepButton;
                _discardButton = discardButton;
                _keepButton.gameObject.SetActive(true);
                _discardButton.gameObject.SetActive(true);
                _keepButton.interactable = true;
                _discardButton.interactable = true;
            }
            else
            {
                _keepButton = FingertipChoiceButton.Create(
                    keepLabel, parent, center + keepOffset, choiceFontSize,
                    new Color(1f, 1f, 1f, 1f), new Color(1f, 0.9f, 0.4f, 1f), 620);
                _discardButton = FingertipChoiceButton.Create(
                    discardLabel, parent, center + discardOffset, choiceFontSize,
                    new Color(1f, 1f, 1f, 1f), new Color(1f, 0.65f, 0.55f, 1f), 620);
            }

            _keepButton.Clicked -= OnKeepClicked;
            _keepButton.Clicked += OnKeepClicked;
            _discardButton.Clicked -= OnDiscardClicked;
            _discardButton.Clicked += OnDiscardClicked;

            float elapsed = 0f;
            while (!_choiceMade && elapsed < timeoutSeconds)
            {
                elapsed += Time.deltaTime;
                if (ConsumeSkip()) break;
                yield return null;
            }

            if (!_choiceMade)
            {
                _keepChoice = false;
                Debug.LogWarning($"[Sequence] 等待选择超时（{timeoutSeconds:F0} 秒）→ 按「不保留」处理");
            }

            if (_keepButton != null)
            {
                _keepButton.Clicked -= OnKeepClicked;
                if (_choiceButtonsFromScene) _keepButton.gameObject.SetActive(false);
                else _keepButton.ConsumeAndDestroy();
            }
            if (_discardButton != null)
            {
                _discardButton.Clicked -= OnDiscardClicked;
                if (_choiceButtonsFromScene) _discardButton.gameObject.SetActive(false);
                else _discardButton.ConsumeAndDestroy();
            }

            _keepButton = null;
            _discardButton = null;
        }

        private void OnKeepClicked()
        {
            if (_choiceMade) return;
            _choiceMade = true;
            _keepChoice = true;
        }

        private void OnDiscardClicked()
        {
            if (_choiceMade) return;
            _choiceMade = true;
            _keepChoice = false;
        }

        private IEnumerator FinishChoice()
        {
            if (promptText != null) promptText.gameObject.SetActive(false);
            if (_countdownText != null) _countdownText.gameObject.SetActive(false);

            if (_keepChoice)
            {
                bubbleReward.KeepCurrent();

                // 等泡泡升到天花板（最多 8 秒），这期间游戏仍保持暂停
                float deadline = Time.time + 8f;
                while (!bubbleReward.ReachedCeiling && Time.time < deadline)
                {
                    if (ConsumeSkip()) break;
                    yield return null;
                }
            }
            else
            {
                bubbleReward.DiscardCurrent();
                yield return Wait(0.5f);
            }

            // 面板淡出 → 大恐龙走出画面 → 原恐龙从右侧走回来（对称返回）
            FadeBackdropOut();
            HideBonusTime();
            yield return OutroExitAndReturn();
        }

        private IEnumerator OutroExitAndReturn()
        {
            // 大恐龙空手离场（泡泡水已经在小恐龙离场时被带走了）
            if (rewardDino != null && rewardDino.gameObject.activeSelf && _rewardDinoHomeCaptured)
            {
                Camera cam = Camera.main;
                float halfW = cam != null && cam.orthographic ? cam.orthographicSize * cam.aspect : 8.7f;
                float endX = halfW + _rewardDinoHalfWidth + Mathf.Max(0.2f, rewardDinoEnterPadding)
                             + Mathf.Max(0f, rewardDinoExitExtra);
                Vector3 from = rewardDino.position;

                float distance = Mathf.Abs(endX - from.x);
                float duration = distance / Mathf.Max(0.2f, rewardDinoWalkSpeed);
                float t = 0f;
                while (t < duration)
                {
                    t += Time.deltaTime;
                    float k = Mathf.Clamp01(t / duration);
                    float eased = k * k * (3f - 2f * k);
                    float x = Mathf.Lerp(from.x, endX, eased);
                    float bob = Mathf.Abs(Mathf.Sin(t * rewardDinoBobFrequency)) * rewardDinoBobAmount;
                    rewardDino.position = new Vector3(x, from.y + bob, from.z);

                    if (ConsumeSkip()) break;
                    yield return null;
                }

                rewardDino.gameObject.SetActive(false);
            }

            if (dino != null && !_dinoReturned)
            {
                if (!dino.gameObject.activeSelf) dino.gameObject.SetActive(true);
                dino.ExternalEnterFromScreen(1, _dinoHomeX, dinoExitPadding);
                _dinoReturned = true;
            }

            yield return Wait(0.7f);
        }

        private void FadeBackdropOut()
        {
            if (rewardBackdrop == null) return;
            var sr = rewardBackdrop.GetComponent<SpriteRenderer>();
            if (sr == null) return;

            DOTween.To(() => sr.color, x => sr.color = x,
                new Color(sr.color.r, sr.color.g, sr.color.b, 0f), 0.4f);
        }

        private void HideBonusTime()
        {
            if (_bonusTween != null) { _bonusTween.Kill(); _bonusTween = null; }
            if (_bonusText == null) return;
            _bonusText.transform.DOKill();
            _bonusText.transform.DOScale(0f, 0.25f).SetEase(Ease.InBack);
        }

        // —— 收尾 ——

        private void CleanupAndResume()
        {
            // 面板
            if (_promptTween != null) { _promptTween.Kill(); _promptTween = null; }
            if (promptText != null) promptText.transform.DOKill();
            if (promptCanvas != null) promptCanvas.gameObject.SetActive(false);
            if (_countdownText != null) _countdownText.gameObject.SetActive(false);

            if (_keepButton != null)
            {
                _keepButton.Clicked -= OnKeepClicked;
                if (_choiceButtonsFromScene) _keepButton.gameObject.SetActive(false);
                else _keepButton.ConsumeAndDestroy();
                _keepButton = null;
            }
            if (_discardButton != null)
            {
                _discardButton.Clicked -= OnDiscardClicked;
                if (_choiceButtonsFromScene) _discardButton.gameObject.SetActive(false);
                else _discardButton.ConsumeAndDestroy();
                _discardButton = null;
            }

            if (_glow != null)
            {
                _glow.transform.DOKill();
                Destroy(_glow.gameObject);
                _glow = null;
            }

            if (bubbleWater != null)
            {
                bubbleWater.DOKill();
                if (_waterHomeCaptured) bubbleWater.position = _waterHome;
                bubbleWater.gameObject.SetActive(false);
            }

            if (rewardDino != null)
            {
                rewardDino.DOKill();
                if (_rewardDinoHomeCaptured) rewardDino.position = _rewardDinoHome;
                rewardDino.gameObject.SetActive(false);
            }

            if (rewardBackdrop != null)
            {
                var sr = rewardBackdrop.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    Color c = sr.color;
                    c.a = 0f;
                    sr.color = c;
                }
            }

            // 兜底：正常流程里原恐龙已经在 OutroExitAndReturn 里回来了
            if (dino != null && !_dinoReturned)
            {
                if (dino.gameObject.activeSelf)
                {
                    // 还没走到「离场」就中断：直接解除接管，让它继续正常漫步
                    dino.ExternalLock(false);
                }
                else
                {
                    dino.gameObject.SetActive(true);
                    dino.ExternalEnterFromScreen(1, _dinoHomeX, dinoExitPadding);
                }
            }

            if (_bonusTween != null) { _bonusTween.Kill(); _bonusTween = null; }
            if (_bonusText != null)
            {
                // 场景里的 TMP：只恢复缩放和显示，不销毁
                _bonusText.transform.DOKill();
                _bonusText.transform.localScale = _bonusBaseScale;
                _bonusText.gameObject.SetActive(true);
                _bonusText = null;
            }

            if (rewardRoot != null) rewardRoot.gameObject.SetActive(false);

            // 兜底：异常中断时别把泡泡留在「等选择」状态
            if (bubbleReward != null)
            {
                if (bubbleReward.IsAwaitingChoice) bubbleReward.DiscardCurrent();
                else if (bubbleReward.IsActive) bubbleReward.FinishSession(cancelled: true);
            }

            if (bubbleReward != null) bubbleReward.countdownUI = null;

            // 恢复被暂停的系统
            ResumePausedBehaviours();
            if (rain != null)
            {
                rain.enabled = _rainWasEnabled;
                Debug.Log("[Sequence] 字符雨恢复");
            }

            if (bubbleReward != null)
            {
                bubbleReward.autoResumeRain = _autoResumeRainBackup;
                bubbleReward.clickToPopEnabled = _clickToPopBackup;
            }

            _skipRequested = false;
        }

        // —— 小工具 ——

        private void Stage(string name)
        {
            if (logStages) Debug.Log($"[Sequence] {name}");
        }

        /// <summary>带跳过支持的等待。</summary>
        private IEnumerator Wait(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                if (ConsumeSkip()) yield break;
                t += Time.deltaTime;
                yield return null;
            }
        }

        // —— 场景参考 Gizmo：泡泡最大尺寸 / 棍子圆环参考大小 ——

        private void OnDrawGizmos()
        {
            Transform target = ResolveGizmoBubbleArea();
            if (target == null) return;

            Vector3 center = target.position;
            float maxR = Mathf.Max(0.01f, bubbleTargetRadius);
            float startR = Mathf.Max(0.001f, bubbleStartDiameter * 0.5f);
            float ringR = Mathf.Max(0.001f, wandRingDiameter * 0.5f);

            // 最大泡泡（青）：这就是吹气结束后泡泡的实际大小
            Gizmos.color = new Color(0.35f, 0.95f, 1f, 0.95f);
            Gizmos.DrawWireSphere(center, maxR);
            DrawCircleXY(center, maxR, 72);

            // 起始小泡泡（白）
            Gizmos.color = new Color(1f, 1f, 1f, 0.45f);
            Gizmos.DrawWireSphere(center, startR);

            // 棍子圆环参考（橙）：只用于对照美术，不参与逻辑
            Gizmos.color = new Color(1f, 0.7f, 0.25f, 0.95f);
            DrawCircleXY(center, ringR, 48);
            Gizmos.DrawLine(center + Vector3.left * ringR, center + Vector3.right * ringR);
            Gizmos.DrawLine(center + Vector3.down * ringR, center + Vector3.up * ringR);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                center + Vector3.up * (maxR + 0.2f),
                $"泡泡最大 ⌀{maxR * 2f:F2} m（半径 {maxR:F2}）\n" +
                $"起始 ⌀{startR * 2f:F2} m\n" +
                $"圆环参考 ⌀{ringR * 2f:F2} m");
#endif
        }

        /// <summary>编辑器里也能用：优先用场景里连好的标记，其次按名字找。</summary>
        private Transform ResolveGizmoBubbleArea()
        {
            if (bubbleArea != null) return bubbleArea;

            if (rewardRoot == null)
            {
                rewardRoot = FindRootTransform("Dino奖励气泡");
            }
            if (rewardDino == null && rewardRoot != null) rewardDino = rewardRoot.Find("dino (1)");
            if (rewardDino != null)
            {
                var found = rewardDino.Find("泡泡区域");
                if (found != null) return found;
            }

            return null;
        }

        private static void DrawCircleXY(Vector3 center, float radius, int segments)
        {
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 p = center + new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }

        private bool ConsumeSkip()
        {
            if (!_skipRequested) return false;
            _skipRequested = false;
            Debug.Log("[Sequence] 跳过当前阶段");
            return true;
        }
    }
}
