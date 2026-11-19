// PrototypeCSceneBootstrap.cs —— 只在 TemplateC_LanMay 里做 C 阶段的运行时裁剪
// 目的：C 侧不再修改 B1 共用脚本，也不改 .unity：
//   ① 停用 DinoAgentB1（组件保留，场景序列化数据不丢），改挂 DinoCController
//   ② 打字机词库移除 CHAIR（CHAIR 在 Awake 里会被 RequiredWords 补回，所以必须在 Awake 之后摘）
//   ③ 停用 B2Table
//   ④ 天灾/火灾计时推远：字符雨保留，但 C 场景没有 RequestedWord → 焦点为空、纯随机字母
// 其他场景（TemplateB1 / TemplateB2）不受影响。

using UnityEngine;
using UnityEngine.SceneManagement;

namespace PrototypeC
{
    public static class PrototypeCSceneBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Apply(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Apply(scene);

        private static void Apply(Scene scene)
        {
            if (!string.Equals(scene.name, PrototypeCStage.TrimmedSceneName)) return;

            var dino = UnityEngine.Object.FindFirstObjectByType<DinoAgentB1>();
            if (dino == null)
            {
                Debug.LogWarning("[PrototypeC] TemplateC_LanMay 里没有 DinoAgentB1，C 恐龙控制器未挂载");
            }
            else
            {
                if (!dino.TryGetComponent<DinoCController>(out var ctl))
                {
                    ctl = dino.gameObject.AddComponent<DinoCController>();
                    ctl.bubble = dino.bubble;
                    Debug.Log("[PrototypeC] 已停用 DinoAgentB1，改为 DinoCController（未改场景文件）");
                }

                // 无论组件是运行时挂的还是场景里手动挂的，都不要再让 B1 恐龙逻辑跑
                dino.enabled = false;
            }

            foreach (var tw in UnityEngine.Object.FindObjectsByType<TypeWriterB1>(FindObjectsSortMode.None))
            {
                tw.words = System.Array.FindAll(
                    tw.words ?? new string[0],
                    w => !string.Equals(w, "CHAIR", System.StringComparison.OrdinalIgnoreCase));

                // 打字机右侧的临时发送键：指尖触发 → LLM 合法性判定 → 生成链路
                // 注意：必须是独立子物体，不能挂在打字机身上（否则定位会移动整台打字机）
                if (tw.GetComponentInChildren<TypewriterSendButton>(true) == null)
                {
                    var sendGo = new GameObject("PrototypeC_SendButton");
                    sendGo.transform.SetParent(tw.transform, false);
                    var send = sendGo.AddComponent<TypewriterSendButton>();
                    send.typeWriter = tw;
                }
            }

            foreach (var table in UnityEngine.Object.FindObjectsByType<B2Table>(FindObjectsSortMode.None))
                table.gameObject.SetActive(false);

            foreach (var scheduler in UnityEngine.Object.FindObjectsByType<B1EventScheduler>(FindObjectsSortMode.None))
            {
                scheduler.threatFirstDelay = 1e9f;
                scheduler.fireFirstDelay = 1e9f;
            }

            // 语音监听（Whisper 离线）：开放词表，说 TOY → 字符雨主要掉 T/O/Y
            if (UnityEngine.Object.FindFirstObjectByType<WhisperVoiceListener>() == null)
            {
                var voice = new GameObject("PrototypeC_WhisperListener");
                voice.AddComponent<WhisperVoiceListener>();
            }
        }
    }
}
