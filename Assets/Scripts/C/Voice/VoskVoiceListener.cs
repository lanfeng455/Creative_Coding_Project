// VoskVoiceListener.cs —— Prototype C：Vosk 离线语音识别 → 字符雨字母聚焦
// 说 TOY → B1EventScheduler 在接下来几秒里主要掉 T / O / Y（和火灾时 WATER 加权同一套机制）。
// 只处理整句/整词；单词发音的字母（"tea" → T）会走同音映射，方便单字母输入。
// 默认不限定词表（公众装置：谁也不知道玩家会说什么），只做整词/整句识别。
// 想提高某个场景的精度，可临时打开 autoGrammar 或填 grammarWords（会漏掉表外词）。
// 模型目录：Assets/StreamingAssets/vosk-model-en-us-0.22-lgraph（可改 modelFolder）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Vosk;

namespace PrototypeC
{
    public class VoskVoiceListener : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public B1EventScheduler scheduler;

        [Header("Vosk 模型")]
        [Tooltip("相对 Assets/StreamingAssets 的模型目录名。lgraph 比 small 精度高很多")]
        public string modelFolder = "vosk-model-en-us-0.22-lgraph";
        [Tooltip("录音采样率，必须和模型一致（小模型 16k）")]
        public int sampleRate = 16000;
        [Tooltip("麦克风设备名；留空用系统默认设备")]
        public string microphoneDevice = "";

        [Header("识别")]
        [Tooltip("限定词表（逗号/换行分隔，小写）。留空 = 开放词表（公众装置默认，谁说什么都能识别）")]
        [TextArea(2, 4)]
        public string grammarWords = "";
        [Tooltip("「限定词表」模式：合并常用物件词 + 字母同音词 + [unk]。只适合你能预设玩家会说哪些词的场景；公众装置请保持关闭")]
        public bool autoGrammar = false;
        [Tooltip("autoGrammar 开启时，自动合并的常用物件词（小写）")]
        public string[] autoGrammarWords =
        {
            "toy", "apple", "water", "fire", "house", "dino", "dinosaur",
            "ball", "basket", "tree", "flower", "chair", "table", "book", "car", "robot"
        };
        [Tooltip("把 tea/bee/sea 这类单词发音当成单个字母 T/B/C")]
        public bool letterHomophones = true;
        [Tooltip("句子里出现多个词时，是否每个词都触发一次聚焦（只保留最后一个更稳）")]
        public bool emitEveryWord = false;
        [Tooltip("两次识别之间的最小间隔（秒），防止连续刷屏")]
        public float minEmitInterval = 0.35f;

        [Header("调试")]
        public bool testWithKeyboard;
        public KeyCode testKey = KeyCode.V;
        public string testWord = "TOY";
        [Tooltip("打印识别到的实时 partial / 最终文字（按 toggleVerboseKey 开关，会被记住）")]
        public bool verboseTranscript = true;
        [Tooltip("实时 partial 的打印间隔（秒），避免每帧刷屏")]
        public float partialLogInterval = 0.4f;
        [Tooltip("按这个键轮换麦克风设备（会把选择记到 PlayerPrefs）")]
        public KeyCode cycleMicrophoneKey = KeyCode.F8;
        [Tooltip("按这个键开关文字打印")]
        public KeyCode toggleVerboseKey = KeyCode.F9;
        [Tooltip("按这个键用当前词表重建识别器（Play 中改完词表不用退出）")]
        public KeyCode rebuildKey = KeyCode.F7;

        /// <summary>识别到的词（已转大写；字母同音会转成单个字母）。</summary>
        public event Action<string> WordRecognized;

        private Model _model;
        private VoskRecognizer _recognizer;
        private AudioClip _clip;
        private string _device;
        private readonly float[] _sampleBuffer = new float[2048];
        private short[] _pcm = new short[2048];
        private int _lastPosition;
        private bool _ready;
        private float _lastEmitTime = -10f;
        private string _lastPartial = string.Empty;
        private float _lastPartialLogTime;

        private const string DevicePrefKey = "PrototypeC.Voice.Microphone";
        private const string VerbosePrefKey = "PrototypeC.Voice.Verbose";

        private static readonly Dictionary<string, string> LetterSounds = new Dictionary<string, string>
        {
            { "A", "A" }, { "AY", "A" }, { "EH", "A" },
            { "B", "B" }, { "BEE", "B" }, { "BEA", "B" },
            { "C", "C" }, { "SEE", "C" }, { "SEA", "C" }, { "SI", "C" },
            { "D", "D" }, { "DEE", "D" },
            { "E", "E" }, { "EE", "E" },
            { "F", "F" }, { "EF", "F" }, { "EFF", "F" },
            { "G", "G" }, { "GEE", "G" }, { "JEE", "G" },
            { "H", "H" }, { "AITCH", "H" }, { "HAITCH", "H" },
            { "I", "I" }, { "EYE", "I" }, { "AYE", "I" },
            { "J", "J" }, { "JAY", "J" },
            { "K", "K" }, { "KAY", "K" },
            { "L", "L" }, { "EL", "L" }, { "ELL", "L" },
            { "M", "M" }, { "EM", "M" },
            { "N", "N" }, { "EN", "N" },
            { "O", "O" }, { "OH", "O" }, { "OWE", "O" },
            { "P", "P" }, { "PEA", "P" }, { "PEE", "P" },
            { "Q", "Q" }, { "CUE", "Q" }, { "QUEUE", "Q" },
            { "R", "R" }, { "ARE", "R" }, { "AR", "R" },
            { "S", "S" }, { "ESS", "S" },
            { "T", "T" }, { "TEA", "T" }, { "TEE", "T" },
            { "U", "U" }, { "YOU", "U" }, { "EWE", "U" },
            { "V", "V" }, { "VEE", "V" },
            { "W", "W" }, { "DOUBLEU", "W" }, { "DOUBLEYOU", "W" },
            { "X", "X" }, { "EX", "X" },
            { "Y", "Y" }, { "WHY", "Y" },
            { "Z", "Z" }, { "ZEE", "Z" }, { "ZED", "Z" },
        };

        private void Start()
        {
            if (scheduler == null) scheduler = FindObjectOfType<B1EventScheduler>();

            // 上次按 F8 选的麦克风 / F9 的打印开关
            string savedDevice = PlayerPrefs.GetString(DevicePrefKey, string.Empty);
            if (string.IsNullOrWhiteSpace(microphoneDevice) && !string.IsNullOrWhiteSpace(savedDevice))
                microphoneDevice = savedDevice;
            verboseTranscript = PlayerPrefs.GetInt(VerbosePrefKey, verboseTranscript ? 1 : 0) != 0;

            string modelPath = System.IO.Path.Combine(Application.streamingAssetsPath, modelFolder);
            if (!System.IO.Directory.Exists(modelPath))
            {
                Debug.LogError($"[Vosk] 找不到模型目录：{modelPath}");
                return;
            }

            try
            {
                // libvosk 依赖同目录的 libgcc/libstdc++/libwinpthread：显式把插件目录加进 DLL 搜索路径
                string pluginDir = PluginDirectory();
                if (System.IO.Directory.Exists(pluginDir)) SetDllDirectory(pluginDir);

                _model = new Model(modelPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Vosk] 初始化失败：{e.GetType().Name} {e.Message}\n" +
                               "请确认 Assets/ThirdParty/Vosk/Plugins/Windows 下的 libvosk.dll 等原生库存在，且模型目录正确。");
                return;
            }

            LogMicrophones();
            StartListening();
        }

        private void Update()
        {
            if (Input.GetKeyDown(cycleMicrophoneKey)) CycleMicrophone();
            if (Input.GetKeyDown(rebuildKey))
            {
                StartListening();
                Debug.Log("[Vosk] 已按当前词表重建识别器");
            }
            if (Input.GetKeyDown(toggleVerboseKey))
            {
                verboseTranscript = !verboseTranscript;
                PlayerPrefs.SetInt(VerbosePrefKey, verboseTranscript ? 1 : 0);
                PlayerPrefs.Save();
                Debug.Log($"[Vosk] 听到的文字打印：{(verboseTranscript ? "开" : "关")}");
            }

            if (testWithKeyboard && Input.GetKeyDown(testKey)) Emit(testWord);
            if (!_ready) return;

            int position = Microphone.GetPosition(_device);
            if (position < 0) return;

            int samples = _clip.samples;
            int available = position - _lastPosition;
            if (available < 0) available += samples;

            while (available > 0)
            {
                int chunk = Mathf.Min(available, _sampleBuffer.Length);
                chunk = Mathf.Min(chunk, samples - _lastPosition);
                if (chunk <= 0) break;

                if (!_clip.GetData(_sampleBuffer, _lastPosition)) break;
                Feed(_sampleBuffer, chunk);

                _lastPosition = (_lastPosition + chunk) % samples;
                available -= chunk;
            }
        }

        private string CurrentDeviceLabel()
        {
            return string.IsNullOrWhiteSpace(_device) ? "系统默认" : _device;
        }

        private void LogMicrophones()
        {
            string[] devices = Microphone.devices;
            Debug.Log($"[Vosk] 可用麦克风（{devices.Length}）：" +
                      (devices.Length == 0 ? "无" : string.Join(" | ", devices)) + "\n" +
                      $"当前使用：{CurrentDeviceLabel()}\n" +
                      $"按 {cycleMicrophoneKey} 轮换设备，按 {rebuildKey} 重建词表，按 {toggleVerboseKey} 开关「听到什么」的打印。");
        }

        private void StartListening()
        {
            if (_model == null) return;

            StopListening();

            _device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice.Trim();
            if (string.IsNullOrWhiteSpace(_device) && Microphone.devices.Length > 0)
            {
                _device = Microphone.devices[0];
                microphoneDevice = _device;
            }

            _clip = Microphone.Start(_device, true, 1, Mathf.Max(8000, sampleRate));
            if (_clip == null)
            {
                Debug.LogError($"[Vosk] 麦克风启动失败（设备：{CurrentDeviceLabel()}）。可能没有设备、被其它程序占用，或 Windows 麦克风权限被关。");
                return;
            }

            // 采样率以实际打开的录音为准，避免设备不接受 16k 时识别全是乱码
            string grammar = BuildGrammar(out int grammarCount);
            try
            {
                _recognizer = grammar == null
                    ? new VoskRecognizer(_model, _clip.frequency)
                    : new VoskRecognizer(_model, _clip.frequency, grammar);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Vosk] 限定词表创建失败（{e.GetType().Name}），退回不限定识别：{e.Message}");
                grammar = null;
                grammarCount = 0;
                _recognizer = new VoskRecognizer(_model, _clip.frequency);
            }
            _recognizer.SetWords(false);
            _lastPosition = 0;
            _lastPartial = string.Empty;
            _ready = true;
            Debug.Log($"[Vosk] 就绪：设备={CurrentDeviceLabel()}，采样率={_clip.frequency}Hz，文字打印={verboseTranscript}，" +
                      $"词表={(grammar != null ? $"限定 {grammarCount} 词" : "不限定")}" +
                      $"（{rebuildKey}=重建词表，{cycleMicrophoneKey}=换麦克风，{toggleVerboseKey}=文字打印）");
        }

        private void StopListening()
        {
            _ready = false;
            _recognizer?.Dispose();
            _recognizer = null;

            if (_clip != null)
            {
                if (Microphone.IsRecording(_device)) Microphone.End(_device);
                _clip = null;
            }
        }

        private void CycleMicrophone()
        {
            string[] devices = Microphone.devices;
            if (devices.Length == 0)
            {
                Debug.LogWarning("[Vosk] 没有检测到任何麦克风设备");
                return;
            }

            int index = -1;
            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(devices[i], _device, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            }

            index = (index + 1) % devices.Length;
            microphoneDevice = devices[index];
            PlayerPrefs.SetString(DevicePrefKey, microphoneDevice);
            PlayerPrefs.Save();

            Debug.Log($"[Vosk] 切换麦克风 → {microphoneDevice}（{index + 1}/{devices.Length}）");
            StartListening();
        }

        /// <summary>
        /// 生成 Vosk grammar JSON。autoGrammar 开启时合并「常用物件词 + 字母同音词 + 手动词表」；
        /// 若最终一个词都没有则返回 null（不限定）。grammarCount 返回实际词数（不含 [unk]）。
        /// 注意：Vosk 的限定词表必须带 [unk]，否则表外语音可能让它一直不返回结果。
        /// </summary>
        private string BuildGrammar(out int grammarCount)
        {
            var words = new List<string>();

            AddWordsFromText(words, grammarWords);

            if (autoGrammar)
            {
                if (autoGrammarWords != null)
                {
                    foreach (string word in autoGrammarWords) AddWord(words, word);
                }

                // 字母同音词（tea/bee/sea…）本身就是模型词表里的词，用它来"说字母"
                if (letterHomophones)
                {
                    foreach (KeyValuePair<string, string> pair in LetterSounds)
                    {
                        if (pair.Key.Length > 1) AddWord(words, pair.Key);
                    }
                }
            }

            grammarCount = words.Count;
            if (words.Count == 0) return null;

            words.Add("[unk]"); // 表外语音落到 [unk]，避免识别器卡住不返回

            var json = new System.Text.StringBuilder("[");
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append('"').Append(words[i].Replace("\"", "\\\"")).Append('"');
            }
            json.Append(']');
            return json.ToString();
        }

        private static void AddWordsFromText(List<string> words, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            foreach (string part in raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddWord(words, part);
            }
        }

        private static void AddWord(List<string> words, string raw)
        {
            string word = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (word.Length == 0) return;
            if (word == "[unk]") return;
            if (!words.Contains(word)) words.Add(word);
        }

        private void Feed(float[] source, int count)
        {
            if (_pcm.Length < count) _pcm = new short[count];
            for (int i = 0; i < count; i++)
            {
                float v = Mathf.Clamp(source[i], -1f, 1f);
                _pcm[i] = (short)(v * 32767f);
            }

            if (_recognizer.AcceptWaveform(_pcm, count))
            {
                HandleJson(_recognizer.Result(), false);
            }
            else if (verboseTranscript)
            {
                HandleJson(_recognizer.PartialResult(), true);
            }
        }

        [Serializable]
        private class VoskJson
        {
            public string text;
            public string partial;
        }

        private void HandleJson(string json, bool partial)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            string text;
            try
            {
                VoskJson data = JsonUtility.FromJson<VoskJson>(json);
                text = partial ? data.partial : data.text;
            }
            catch { return; }

            if (string.IsNullOrEmpty(text)) return;

            if (partial)
            {
                // 实时半句：只在文字变化 + 间隔到了才打印，避免每帧刷屏
                if (!verboseTranscript || text == _lastPartial) return;
                if (Time.time - _lastPartialLogTime < Mathf.Max(0f, partialLogInterval)) return;
                _lastPartial = text;
                _lastPartialLogTime = Time.time;
                Debug.Log($"[Vosk] 听到（实时）：{text}");
                return;
            }

            Debug.Log($"[Vosk] 听到：{text}");
            Emit(text);
        }

        /// <summary>外部也可以直接喂词（键盘测试 / 其它输入源）。</summary>
        public void Emit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Time.time - _lastEmitTime < Mathf.Max(0f, minEmitInterval)) return;

            string[] words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;

            // 限定词表下，表外语音会返回 [unk]：丢掉，别让它变成 U/N/K 的聚焦
            var kept = new List<string>(words.Length);
            foreach (string word in words)
            {
                string trimmed = word.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "[unk]" || trimmed == "<unk>") continue;
                kept.Add(trimmed);
            }
            if (kept.Count == 0) return;
            words = kept.ToArray();

            _lastEmitTime = Time.time;

            // 逐字母拼读："T O Y" → 合成一个 TOY 聚焦
            bool allSingleLetters = words.Length > 1;
            foreach (string w in words)
            {
                if (w.Trim().Length != 1) { allSingleLetters = false; break; }
            }

            if (allSingleLetters)
            {
                Notify(string.Concat(words));
                return;
            }

            if (!emitEveryWord && words.Length > 1)
            {
                // 一句话只聚焦最后一个词（"I want a toy" → TOY）
                Notify(words[words.Length - 1]);
                return;
            }

            foreach (string word in words) Notify(word);
        }

        private void Notify(string raw)
        {
            string word = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (word.Length == 0) return;

            if (letterHomophones && word.IndexOf(' ') < 0 && LetterSounds.TryGetValue(word, out string letter))
            {
                // 单字母：直接聚焦这个字母
                if (word.Length > 1) word = letter;
            }

            Debug.Log($"[Vosk] 识别 → {word}");
            WordRecognized?.Invoke(word);
            if (scheduler != null) scheduler.VoiceFocusWord(word);
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string PluginDirectory()
        {
#if UNITY_EDITOR
            return System.IO.Path.Combine(Application.dataPath, "ThirdParty/Vosk/Plugins/Windows");
#else
            return System.IO.Path.Combine(Application.dataPath, "Plugins/x86_64");
#endif
        }

        private void OnDestroy()
        {
            StopListening();
            _model?.Dispose();
            _model = null;
        }
    }
}
