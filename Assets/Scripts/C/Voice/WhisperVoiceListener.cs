// WhisperVoiceListener.cs —— Prototype C：Whisper（whisper.cpp）离线语音识别 → 字符雨字母聚焦
// 说 TOY → B1EventScheduler 在接下来几秒里主要掉 T / O / Y（和火灾时 WATER 加权同一套机制）。
// 开放词表：不预设玩家会说什么，公众装置里谁说什么都能识别。
// 模型：Assets/StreamingAssets/Whisper/ggml-base.en.bin（换成 ggml-small.en.bin 精度更高、更慢）。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Whisper;

namespace PrototypeC
{
    public class WhisperVoiceListener : MonoBehaviour
    {
        [Header("引用（留空自动查找）")]
        public B1EventScheduler scheduler;

        [Header("Whisper 模型")]
        [Tooltip("相对 StreamingAssets 的模型文件路径。base.en 快，small.en 更准")]
        public string modelFile = "Whisper/ggml-base.en.bin";
        [Tooltip("识别语言（ISO 639-1）。公众装置英文固定 en，别用 auto，短词自动检测很容易跑偏")]
        public string language = "en";
        [Tooltip("推理线程数（0 = 处理器核心数 - 1）")]
        public int threads = 0;

        [Header("录音 / 断句")]
        [Tooltip("录音采样率；Whisper 要求 16000")]
        public int sampleRate = 16000;
        [Tooltip("麦克风设备名；留空用系统默认设备")]
        public string microphoneDevice = "";
        [Tooltip("判定「开始说话」的音量阈值（RMS）。环境吵就调大，说话小声就调小")]
        [Range(0.001f, 0.2f)]
        public float speechRmsThreshold = 0.015f;
        [Tooltip("静音多久算一句说完（秒）")]
        public float silenceSeconds = 0.7f;
        [Tooltip("一句话最长录多久（秒）")]
        public float maxUtteranceSeconds = 8f;
        [Tooltip("开口前额外保留的音频（秒），避免吞掉第一个音")]
        public float preRollSeconds = 0.25f;
        [Tooltip("有效语音最短时长（秒），过滤咳嗽/关门声")]
        public float minSpeechSeconds = 0.15f;

        [Header("识别结果")]
        [Tooltip("一句话只取最后一个词（\"I want a toy\" → TOY）。关掉则每个词都触发聚焦")]
        public bool emitOnlyLastWord = true;
        [Tooltip("把 tea/bee/sea 这类单词发音当成单个字母 T/B/C")]
        public bool letterHomophones = true;
        [Tooltip("两次识别之间的最小间隔（秒），防止连续刷屏")]
        public float minEmitInterval = 0.35f;

        [Header("调试")]
        public bool verboseTranscript = true;
        [Tooltip("按这个键轮换麦克风设备（会把选择记到 PlayerPrefs）")]
        public KeyCode cycleMicrophoneKey = KeyCode.F8;
        [Tooltip("按这个键开关「听到什么」的打印")]
        public KeyCode toggleVerboseKey = KeyCode.F9;
        public bool testWithKeyboard;
        public KeyCode testKey = KeyCode.V;
        public string testWord = "TOY";

        /// <summary>识别到的词（已转大写；字母同音会转成单个字母）。</summary>
        public event Action<string> WordRecognized;

        /// <summary>最近一个音频块的整体音量（RMS）。吹气判定等外部玩法直接读这个值，避免再开一路麦克风。</summary>
        public float CurrentRms { get; private set; }

        /// <summary>麦克风是否已启动（吹气电平只需要它，不依赖模型）。</summary>
        public bool IsReady => _micReady;

        /// <summary>语音模型是否加载完成（只有识别才需要）。</summary>
        public bool IsModelReady => _wrapper != null;

        private const string DevicePrefKey = "PrototypeC.Voice.Microphone";
        private const string VerbosePrefKey = "PrototypeC.Voice.Verbose";

        private WhisperWrapper _wrapper;
        private WhisperParams _params;
        private AudioClip _clip;
        private string _device;
        private int _clipFrequency = 16000;
        private bool _micReady;

        private readonly float[] _readBuffer = new float[8192];
        private readonly List<float> _preRoll = new List<float>();
        private readonly List<float> _utterance = new List<float>();
        private int _lastPosition;
        private bool _speaking;
        private float _silenceTime;
        private bool _busy;
        private float _lastEmitTime = -10f;

        // 静音/噪声时 Whisper 常见的幻觉整句，直接丢弃
        private static readonly string[] Hallucinations =
        {
            "thank you", "thanks", "thank you for watching", "thanks for watching",
            "please subscribe", "bye", "bye bye"
        };

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

            // whisper 默认会打一堆加载/推理日志，这里只保留警告
            Whisper.Utils.LogUtils.Level = Whisper.Utils.LogLevel.Warning;

            string savedDevice = PlayerPrefs.GetString(DevicePrefKey, string.Empty);
            if (string.IsNullOrWhiteSpace(microphoneDevice) && !string.IsNullOrWhiteSpace(savedDevice))
                microphoneDevice = savedDevice;
            verboseTranscript = PlayerPrefs.GetInt(VerbosePrefKey, verboseTranscript ? 1 : 0) != 0;

            // 麦克风先启动：吹气判定只需要电平，不能因为模型缺失/加载失败而拿不到声音
            StartListening();

            string modelPath = System.IO.Path.Combine(Application.streamingAssetsPath, modelFile);
            if (!System.IO.File.Exists(modelPath))
            {
                Debug.LogError($"[Whisper] 找不到模型文件：{modelPath}\n" +
                               "请把 ggml-base.en.bin 放进 Assets/StreamingAssets/Whisper/。\n" +
                               "（麦克风电平仍然可用：吹气判定不受影响，只有语音识别不可用）");
                return;
            }

            if (LooksLikeLfsPointer(modelPath, out long pointerSize))
            {
                Debug.LogError($"[Whisper] 模型文件是 Git LFS 指针（{pointerSize} 字节），不是真模型：\n" +
                               "  在仓库里执行 `git lfs pull`，或把真实的 ggml-base.en.bin（约 141 MB）放进 Assets/StreamingAssets/Whisper/。\n" +
                               "（麦克风电平仍然可用：吹气判定不受影响，只有语音识别不可用）");
                return;
            }

            _ = LoadModelAsync(modelPath);
        }

        /// <summary>文件名对、但内容只有一百多字节且以 "version https://git-lfs" 开头 → 未拉取的 LFS 指针。</summary>
        private static bool LooksLikeLfsPointer(string path, out long sizeBytes)
        {
            sizeBytes = 0;
            try
            {
                var info = new System.IO.FileInfo(path);
                sizeBytes = info.Length;
                if (sizeBytes > 1024) return false;

                using (var stream = System.IO.File.OpenRead(path))
                {
                    var buffer = new byte[64];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    string head = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                    return head.StartsWith("version https://git-lfs", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task LoadModelAsync(string modelPath)
        {
            Debug.Log("[Whisper] 正在加载模型（首次可能要几秒）…");
            try
            {
                // 固定走 CPU：公众装置上 GPU/Vulkan 初始化不稳定，base.en 用 CPU 足够
                WhisperContextParams contextParams = WhisperContextParams.GetDefaultParams();
                contextParams.UseGpu = false;
                _wrapper = await WhisperWrapper.InitFromFileAsync(modelPath, contextParams);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Whisper] 模型加载失败：{e.GetType().Name} {e.Message}\n" +
                               "请确认 Assets/ThirdParty/Whisper/Plugins/Windows 下的 libwhisper.dll / ggml*.dll 存在。");
                return;
            }

            if (_wrapper == null)
            {
                Debug.LogError("[Whisper] 模型加载失败（返回空）。检查模型文件是否完整。");
                return;
            }

            int threadCount = threads > 0 ? threads : Mathf.Max(1, SystemInfo.processorCount - 1);
            _params = WhisperParams.GetDefaultParams();
            _params.Language = language;
            _params.Translate = false;
            _params.NoContext = true;
            _params.SingleSegment = true;
            _params.ThreadsCount = Mathf.Clamp(threadCount, 1, 16);
            _params.EnableTokens = false;

            Debug.Log($"[Whisper] 就绪：模型={modelFile}，语言={language}，线程={threadCount}，" +
                      $"文字打印={verboseTranscript}（{cycleMicrophoneKey}=换麦克风，{toggleVerboseKey}=文字打印）");

            LogMicrophones();
            if (!_micReady) StartListening();
        }

        private void Update()
        {
            if (Input.GetKeyDown(cycleMicrophoneKey)) CycleMicrophone();
            if (Input.GetKeyDown(toggleVerboseKey))
            {
                verboseTranscript = !verboseTranscript;
                PlayerPrefs.SetInt(VerbosePrefKey, verboseTranscript ? 1 : 0);
                PlayerPrefs.Save();
                Debug.Log($"[Whisper] 听到的文字打印：{(verboseTranscript ? "开" : "关")}");
            }

            if (testWithKeyboard && Input.GetKeyDown(testKey)) Emit(testWord);
            if (!_micReady) return;

            int position = Microphone.GetPosition(_device);
            if (position < 0) return;

            int samples = _clip.samples;
            int available = position - _lastPosition;
            if (available < 0) available += samples;

            while (available > 0)
            {
                int chunk = Mathf.Min(available, _readBuffer.Length);
                chunk = Mathf.Min(chunk, samples - _lastPosition);
                if (chunk <= 0) break;

                if (!_clip.GetData(_readBuffer, _lastPosition)) break;

                // 电平：不依赖模型，吹气判定随时可读
                float sum = 0f;
                for (int i = 0; i < chunk; i++) sum += _readBuffer[i] * _readBuffer[i];
                float rms = Mathf.Sqrt(sum / chunk);
                CurrentRms = rms;

                // 只有模型就绪才做 VAD / 断句 / 识别
                if (_wrapper != null) ProcessSamples(_readBuffer, chunk, rms);

                _lastPosition = (_lastPosition + chunk) % samples;
                available -= chunk;
            }
        }

        /// <summary>简易能量 VAD：攒够一句话就送去 Whisper（只在模型就绪时调用）。</summary>
        private void ProcessSamples(float[] data, int count, float rms)
        {
            if (count <= 0) return;

            float chunkSeconds = (float)count / _clipFrequency;
            bool voiced = rms >= speechRmsThreshold;

            if (!_speaking)
            {
                AppendPreRoll(data, count);
                if (!voiced) return;

                // 开口：把开口前的音频接上，避免吞掉第一个音
                _speaking = true;
                _silenceTime = 0f;
                _utterance.Clear();
                _utterance.AddRange(_preRoll);
                _preRoll.Clear();
                return;
            }

            for (int i = 0; i < count; i++) _utterance.Add(data[i]);

            _silenceTime = voiced ? 0f : _silenceTime + chunkSeconds;

            float duration = _utterance.Count / (float)_clipFrequency;
            if (_silenceTime >= silenceSeconds || duration >= maxUtteranceSeconds)
                FinishUtterance();
        }

        private void AppendPreRoll(float[] data, int count)
        {
            for (int i = 0; i < count; i++) _preRoll.Add(data[i]);

            int maxSamples = Mathf.RoundToInt(Mathf.Max(0f, preRollSeconds) * _clipFrequency);
            int extra = _preRoll.Count - maxSamples;
            if (extra > 0) _preRoll.RemoveRange(0, extra);
        }

        private void FinishUtterance()
        {
            _speaking = false;
            _silenceTime = 0f;
            _preRoll.Clear();

            float duration = _utterance.Count / (float)_clipFrequency;
            if (duration < minSpeechSeconds)
            {
                _utterance.Clear();
                return;
            }

            if (_busy)
            {
                if (verboseTranscript) Debug.Log("[Whisper] 上一句还在识别，这句丢弃");
                _utterance.Clear();
                return;
            }

            float[] samples = _utterance.ToArray();
            _utterance.Clear();
            _ = TranscribeAsync(samples, _clipFrequency);
        }

        private async Task TranscribeAsync(float[] samples, int frequency)
        {
            _busy = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                WhisperResult result = await _wrapper.GetTextAsync(samples, frequency, 1, _params);
                watch.Stop();
                if (result == null)
                {
                    Debug.LogWarning("[Whisper] 识别返回空");
                    return;
                }

                string text = CleanText(result.Result);
                if (verboseTranscript)
                {
                    Debug.Log($"[Whisper] 听到（{samples.Length / (float)frequency:F1}s / {watch.ElapsedMilliseconds}ms）：{text}");
                }

                Emit(text);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Whisper] 识别失败：{e.GetType().Name} {e.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>去掉标点、方括号音效标记、以及静音时的常见幻觉整句。</summary>
        private static string CleanText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string text = raw.Trim();
            foreach (char c in new[] { '[', '(' })
            {
                int start = text.IndexOf(c);
                while (start >= 0)
                {
                    char close = c == '[' ? ']' : ')';
                    int end = text.IndexOf(close, start + 1);
                    if (end < 0) { text = text.Substring(0, start); break; }
                    text = text.Remove(start, end - start + 1);
                    start = text.IndexOf(c);
                }
            }

            text = text.Replace("♪", string.Empty)
                       .Replace(".", string.Empty)
                       .Replace(",", string.Empty)
                       .Replace("!", string.Empty)
                       .Replace("?", string.Empty)
                       .Replace("\"", string.Empty)
                       .Replace("'", string.Empty)
                       .Trim();

            if (string.IsNullOrEmpty(text)) return string.Empty;

            string lower = text.ToLowerInvariant();
            foreach (string bad in Hallucinations)
            {
                if (lower == bad) return string.Empty;
            }

            return text;
        }

        /// <summary>外部也可以直接喂词（键盘测试 / 其它输入源）。</summary>
        public void Emit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Time.time - _lastEmitTime < Mathf.Max(0f, minEmitInterval)) return;

            string[] words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;

            _lastEmitTime = Time.time;

            // 逐字母拼读："T O Y" → 合成一个 TOY 聚焦
            bool allSingleLetters = words.Length > 1;
            foreach (string w in words)
            {
                if (w.Length != 1) { allSingleLetters = false; break; }
            }

            if (allSingleLetters)
            {
                Notify(string.Concat(words));
                return;
            }

            if (emitOnlyLastWord)
            {
                Notify(words[words.Length - 1]);
                return;
            }

            foreach (string word in words) Notify(word);
        }

        private void Notify(string raw)
        {
            string word = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (word.Length == 0) return;

            if (letterHomophones && LetterSounds.TryGetValue(word, out string letter))
            {
                if (word.Length > 1) word = letter;
            }

            Debug.Log($"[Whisper] 识别 → {word}");
            WordRecognized?.Invoke(word);
            if (scheduler != null) scheduler.VoiceFocusWord(word);
        }

        private string CurrentDeviceLabel()
        {
            return string.IsNullOrWhiteSpace(_device) ? "系统默认" : _device;
        }

        private void LogMicrophones()
        {
            string[] devices = Microphone.devices;
            Debug.Log($"[Whisper] 可用麦克风（{devices.Length}）：" +
                      (devices.Length == 0 ? "无" : string.Join(" | ", devices)) + "\n" +
                      $"当前使用：{CurrentDeviceLabel()}\n" +
                      $"按 {cycleMicrophoneKey} 轮换设备。");
        }

        private void StartListening()
        {
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
                Debug.LogError($"[Whisper] 麦克风启动失败（设备：{CurrentDeviceLabel()}）。可能没有设备、被其它程序占用，或 Windows 麦克风权限被关。");
                return;
            }

            _clipFrequency = _clip.frequency;
            _lastPosition = 0;
            _preRoll.Clear();
            _utterance.Clear();
            _speaking = false;
            _silenceTime = 0f;
            _micReady = true;

            Debug.Log($"[Whisper] 麦克风已启动：设备={CurrentDeviceLabel()}，采样率={_clipFrequency}Hz" +
                      "（电平随时可用；模型就绪后才做语音识别）");
        }

        private void StopListening()
        {
            _micReady = false;
            if (_clip != null)
            {
                if (Microphone.IsRecording(_device)) Microphone.End(_device);
                _clip = null;
            }

            _preRoll.Clear();
            _utterance.Clear();
            _speaking = false;
        }

        private void CycleMicrophone()
        {
            string[] devices = Microphone.devices;
            if (devices.Length == 0)
            {
                Debug.LogWarning("[Whisper] 没有检测到任何麦克风设备");
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

            Debug.Log($"[Whisper] 切换麦克风 → {microphoneDevice}（{index + 1}/{devices.Length}）");
            StartListening();
        }

        private void OnDestroy()
        {
            StopListening();
            _wrapper = null;
        }
    }
}
