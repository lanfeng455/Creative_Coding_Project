# 组员协作指南：模型资源与 API 配置

> 适用仓库：`Creative_Coding_Project` · 场景 `Assets/Scenes/TemplateC_LanMay.unity`
> 最后更新：2026-11-25

本指南说明三件事：**大模型资源怎么获取**、**Git LFS 拉取怎么用**、**API Key 怎么配**。照着做就能跑通 TemplateC。

---

## 0. 这次仓库做了什么调整（为什么之前 push 卡住）

之前往 `origin/main` 推送时卡在 `pack-reused 0 (from 0)`，根因是 **vosk 语音模型（~270 MB）和 `.glb` 模型文件没走 Git LFS，直接塞进了 git pack**，单次要传几百 MB 二进制，被 GitHub 掐断或慢到像死机。

现已修复（提交 `44129e7`）：

| 资源 | 处理方式 |
|---|---|
| `*.glb`（场景模型 + 家具缓存，24 个，~215 MB） | 走 **Git LFS**，仓库里只存 130 字节指针 |
| vosk 语音模型（`vosk-model-en-us-0.22-lgraph` / `vosk-model-small-en-us-0.15`，~270 MB） | **不进仓库**，组员自行下载放置（见下） |
| Whisper 模型 `ggml-base.en.bin`（141 MB） | 已走 LFS（之前就配好） |
| 字体 `NotoSerifCJKsc-Black SDF.asset`（140 MB） | 已走 LFS |
| Vosk 的 C# wrapper + Windows DLL（`Assets/ThirdParty/Vosk/`） | 保留在仓库（小文件，组员不用管） |

修复后 git pack 只有 **278 KB**，LFS 对象走分块并发上传，推送正常。

---

## 1. Vosk 语音模型：组员自行获取（可直接转发）

### 问题
Vosk 语音模型体积大（两个合约 270 MB），放进 git 仓库会导致 push 失败、仓库臃肿。所以模型**不进版本库**，每位组员在本地自行下载放置。仓库已在 `.gitignore` 忽略这两个目录，放置后不会被误提交。

### 解决方法（3 步）

1. **下载模型**：打开 <https://alphacephei.com/vosk/models> ，下载这两个（都是英文小模型，项目代码引用的就是它们）：
   - `vosk-model-en-us-0.22-lgraph`（约 1.3 GB 解压后，识别准）
   - `vosk-model-small-en-us-0.15`（约 40 MB，轻量）

   > 只下一个也能跑，但代码里可能两个都会尝试加载，建议都放。

2. **解压放置**：把解压后的两个文件夹放到项目的：
   ```
   Assets/StreamingAssets/vosk-model-en-us-0.22-lgraph/
   Assets/StreamingAssets/vosk-model-small-en-us-0.15/
   ```
   即每个文件夹里直接是 `am/`、`graph/`、`ivector/`、`conf/` 等子目录，不要多套一层。

3. **回 Unity**：让编辑器重新导入。`.meta` 文件会由 Unity 自动生成（仓库已忽略模型目录的 meta）。

### 验证
- 运行场景后 Console 不报 `DllNotFoundException` 或找不到模型路径的错，即 OK。
- 对应代码：`Assets/Scripts/C/Voice/VoskVoiceListener.cs`。
- 注意：Vosk 只用于**语音识别**（说 "TOY" 聚焦字母等）。如果暂时不用语音功能，不放模型也能跑其他玩法；吹气功能用的是 Whisper/麦克风电平，不依赖 vosk。

---

## 2. `.glb` 走了 LFS，GitHub Desktop 能直接 Fetch 吗？

**能。** GitHub Desktop 内置 Git LFS 支持，clone / fetch / pull 时会自动把 LFS 对象（`.glb` 真实内容）下载到本地，组员无需额外操作。

### 正常情况
- 首次 clone 仓库：LFS 文件会自动拉取，`.glb` 是真实模型，能正常在 Unity 里显示。
- 日常 `Fetch origin` / `Pull`：新增的 `.glb` 也会自动下载。

### 如果拉下来的 `.glb` 只有 130 字节（是指针不是模型）
说明 LFS 对象没拉下来，手动补一下：
```bash
git lfs pull
```
在仓库根目录执行即可。若提示 `git lfs` 不是命令，装一下 Git LFS：<https://git-lfs.com>（装完重启终端，GitHub Desktop 一般自带不用装）。

### 关于 LFS 配额（重要）
GitHub 免费版 LFS 配额：**1 GB 存储 + 1 GB/月带宽**。本仓库 LFS 文件约 490 MB（glb 215 + whisper 141 + 字体 140）。
- **存储**：目前还在 1 GB 以内，够放。
- **带宽**：每位组员 clone 一次就消耗约 490 MB 下载流量。**全组 clone 两次就可能超 1 GB/月**，超了之后其他人当月无法再拉 LFS（要等下月重置或仓库所有者升级付费套餐）。
- 建议：**不要反复删了重 clone**；用 `git pull` 更新即可。大文件尽量一次拉全。

---

## 3. 家具（生成模型）缓存放哪里？

放仓库根目录的 **`PrototypeC_Cache/`** 文件夹。

- 这个目录在 `.gitignore` 里被**强制不忽略**（`!/PrototypeC_Cache/`），设计上就是要随工程提交的「模型缓存镜像」，全组共享。
- 现有结构：`PrototypeC_Cache/<类别>/<hash>/model.glb`，已有 `apple / ball / bed / chair / table / toy / plant / lamp / fan / wind_chime` 等。
- 你用 Meshy 等生成的新家具 `.glb`，放进 `PrototypeC_Cache/你的类别/` 下，提交后其他人 pull 就能拿到（`.glb` 走 LFS）。
- **个人临时草稿、不想共享的文件**：别放这里，放本地别的目录（比如桌面），否则会被提交进仓库。
- 生成的 API Key 不在这里，见下一节。

---

## 4. LLM 和生成模型的 API Key 怎么配？

API Key **绝不提交到 git**。项目用本地配置文件读取，这两个文件已在 `.gitignore` 里忽略，放心写自己的 Key：

| 用途 | 配置文件（放仓库根目录） | 对应代码 |
|---|---|---|
| LLM 对话（DeepSeek / OpenAI 兼容） | `llm_config.json` | `Assets/Scripts/C/Llm/LlmConfig.cs` |
| 生成模型（Meshy） | `meshy_config.json` | `Assets/Scripts/C/MeshyConfig.cs` |

### 4.1 配 LLM（DeepSeek）

在仓库根目录新建 `llm_config.json`：
```json
{
  "apiKey": "sk-你的deepseek-key",
  "baseUrl": "https://api.deepseek.com/v1",
  "model": "deepseek-chat",
  "disableThinking": true
}
```
- `baseUrl` 和 `model` 不写也行，默认就是上面的值。
- 也支持环境变量 `DEEPSEEK_API_KEY` / `DEEPSEEK_BASE_URL` / `DEEPSEEK_MODEL`（优先级最高）。
- 没配 Key 时 AI 解析不可用，但可在场景里关闭「AI 解析」用手动模式，不影响其他功能。

### 4.2 配生成模型（Meshy）

在仓库根目录新建 `meshy_config.json`：
```json
{
  "apiKey": "msy-你的meshy-key"
}
```
- 也支持环境变量 `MESHY_API_KEY`。
- 偷懒写法：可以把 LLM 的 Key 也写进 `meshy_config.json`，代码会兜底读取（字段 `llmApiKey` / `deepseekApiKey` 或嵌套 `llm.apiKey`），但**推荐分开写**，清晰。

### 4.3 配置放哪都行（三选一，按优先级）

1. **环境变量**（最高优先级）：`DEEPSEEK_API_KEY`、`MESHY_API_KEY` 等。
2. **仓库根目录的 json 文件**（推荐，最直观）：`llm_config.json` / `meshy_config.json`，已被 git 忽略。
3. **`persistentDataPath` 下的 json**：Unity 的持久化数据目录（各系统路径不同，Console 会打印完整路径），也已忽略。

> Key 不会写进日志——运行时 Console 只打印「来源 + 长度」，内容隐藏。可放心在自己机器上配置。

---

## 5. 快速自检清单

clone 完仓库后，依次确认：

- [ ] `git lfs pull` 已执行（或 GitHub Desktop 自动拉过），`.glb` 文件不是 130 字节指针。
- [ ] 已下载 vosk 两个模型并放到 `Assets/StreamingAssets/` 对应目录（不用语音可跳过）。
- [ ] 仓库根目录有 `llm_config.json`（用 AI 时）和/或 `meshy_config.json`（生成模型时），填了自己的 Key。
- [ ] Unity 打开 `Assets/Scenes/TemplateC_LanMay.unity` 能正常编译、无红色 Console 报错。
- [ ] 不要把 `llm_config.json` / `meshy_config.json` / vosk 模型目录提交到 git（已被忽略，正常 `git add` 不会加进去）。
