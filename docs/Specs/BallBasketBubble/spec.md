# Spec｜球的上下层玩法 + 篮筐 + 泡泡奖励（Prototype C）

场景：`Assets/Scenes/TemplateC_LanMay.unity`
硬约束：不启动 Unity、不改 `.unity` 场景文件、不引入新的全局 Manager、所有新参数进 Inspector 并带中文 Tooltip、不声称"跑过 Unity"。

---

## 0. 先回答：为什么 Ball 没有变成可互动物件

证据来自 `C:\Users\lanfe\AppData\Local\Unity\Editor\Editor.log` 中那一次生成的记录：

| 项 | 日志里的值 |
| --- | --- |
| AI 解析 behavior | `Play`（不是 `Ignore` / `Rest`） |
| placement | `Ground` |
| size | `Small` |
| 落位结果 | "尺寸/布局/模型已就绪" |
| 拒绝落位 / 缺少组件 | **没有**这两类日志 |

结论（非推测）：
1. LLM 确实把球标成了"可玩（Play）"，落位也成功了，所以不是解析或布局失败。
2. 问题在运行时玩法侧：`Play + Ground` 在 `ObjectModelService.ApplyBehavior` 里只给一个可推的 `Rigidbody`，而恐龙追球/踢球逻辑只认 `B1Ball` 静态注册表与 `RoomOccupancy` 的物件，生成球两者都不在，因此没有任何球类玩法挂上去。
3. 本轮补的就是这条缺口：球不再依赖 AI 生成，改为**预制体 + 专用 `BallFormController`**。

---

## 1. 现状事实（已核实，作为实现依据）

- 相机 `Main Camera`：`(0, 0.715, -10)`，正交 `orthographicSize = 4.885`，cullingMask 全开。
- `B1LayerConfig`（`Assets/Scripts/B1/B1LayerConfig.cs`）：
  `upperFloorY = 1.6`、`lowerGroundY = -3.77`、`upperCeilingY = 5.2`、
  `boundsXMin/-9.86`、`boundsXMax/9.86`、`upperFloorXMax = 8.0`、`GapXMin = 8.05`；
  已有 `GroundYAt(x, y)`、`InGap(x)`、`OnUpperFloor(p)`。分界线直接用 `upperFloorY`。
- 场景里已有 `ball.glb` 实例（名字 `ball`，位置 `(-1.475, 3.67, 0)`，GUID `bd29f6d4bd4af3940920044a66631fb2`）。
- 场景里已有 `model.glb` 实例（用户口头叫 `model`，场景对象名 `篮筐`，位置 `(3.5, 4.01, 0)`，y 旋转 180°）。
- 恐龙 `dino` 挂 `DinoAgentB1`，活动范围 `groundY = 1.84`、`activityXMin = -5.95`、`activityXMax = 5.9`；
  `RangeY / RangeLo / RangeHi / ClampLo / ClampHi / _vy / Face(int) / ApplyMotion` 均为 private，不能外部调用。
- 雨：`Assets/Scripts/B2/B2BackgroundRain.cs`（133 行），3D TMP 字符池，`Start` 里一次性建池，**当前没有任何开关/密度 API**。
- toon：`Assets/Scripts/C/Rendering/ToonMaterials.cs`，`public static void Apply(GameObject root, ToonLayer layer)`、`ToonShader`。
  着色器 `Assets/Shaders/PrototypeC_Toon.shader` 是 Opaque（`Queue=Geometry`）→ **不能用 alpha 淡入**。
- 输入：`ProjectSettings.asset` 里 `activeInputHandler: 0`（旧 Input Manager）→ 可用 `Input.GetMouseButtonDown(0)` / `Input.touchCount`。
- 工程里没有 `Assets/Prefabs` 目录，也没有 ParticleSystem 相关代码。

### 1.1 预制体球拿不到 toon 材质的原因

1. `ToonMaterials` 只在两条路径被调用：`ObjectModelService` 的生成管线，以及编辑器菜单手动执行。手动拖进场景的预制体不会经过任何一条，因此仍是 `ball.glb` 自带的材质。
2. 即使手动调用，`ToonMaterials.ResolveLayer` 对 `.glb/.gltf/.fbx` 预制体实例一律判为 `Backdrop`（背景档）。球属于前景，必须**显式传 `ToonLayer.Foreground`**，不能走 `ResolveLayer` 自动判定。

解决：`BallFormController.Awake()` 对自身 GameObject 调用 `ToonMaterials.Apply(gameObject, ToonLayer.Foreground)`。
`ToonMaterials.Create` 内部会 `new Material(...)`，不改 `.glb` 资源、不改场景文件，运行时生效，预制体与场景实例都能拿到同一套 toon。

---

## 2. 阶段 1｜球预制体 + 双形态

### 新增 `Assets/Scripts/C/Play/BallFormController.cs`

职责：预制体球的自研轻量物理 + 上下层双形态 + 对外 `Launch`。

字段（全部 `[Tooltip]` 中文）：
- `B1LayerConfig world`（留空自动 Find）
- `float radius = 0.34f`
- `float gravity = 9.8f`、`float groundBounce = 0.45f`、`float wallBounce = 0.75f`、`float drag = 0.15f`
- `float splitY = 0f`（0 = 用 `world.upperFloorY`，即场景已有分界线位置）
- `Transform solidForm`（留空 = 自身所有 Renderer 视为实体形态）
- `float formFadeSeconds = 0.18f`
- 字符画：`string glyphRows = "  .-.  \n /   \\ \n|     |\n \\   / \n  '-'  "`、`float glyphFontSize = 4f`、`Color glyphColor`、`int glyphSortOrder = 6`
- 恐龙下压：`bool dinoPressEnabled = true`、`float dinoNoticeRange = 3.2f`、`float dinoPressRadius = 0.9f`、`float dinoHopDelay = 0.45f`、`float pressDownSpeed = 7f`、`float passThroughSeconds = 0.6f`

核心逻辑：
- **静态注册表** `public static readonly List<BallFormController> All`，供 `BasketHoop` / 恐龙逻辑遍历；`Awake` 加入、`OnDestroy` 移除。
- `Awake()`：
  - `ToonMaterials.Apply(gameObject, ToonLayer.Foreground)`（见 §1.1）；
  - 收集实体形态 Renderer 列表（`solidForm` 为空则 `GetComponentsInChildren<Renderer>(true)`，排除字符画 TMP 的 Renderer）；
  - 用 `B1Text.Make` 建字符画 TMP 子物体（多行文本），初始 alpha 按当前层；
  - `_radius = radius`。
- `Update()`：
  - `StepPhysics(dt)`：重力 + 阻力 + 积分 + 左右边界反弹；
  - 支撑地面：`world.GroundYAt(x, y)`；若 `Time.time < _passThroughUpperUntil` 则强制 `world.lowerGroundY`（恐龙下压穿板）；
  - 落地反弹 / 静止；
  - `UpdateForm(dt)`：`IsUpper = y >= splitY`；
    - 实体形态：Opaque shader 不能 alpha 淡入 → 用"激活 + 缩放弹出"（`solidRoot.localScale = Vector3.Lerp(0.72, 1, t)`），完全下层时 `SetActive(false)`；
    - 字符画：`tmp.alpha` 1→0 / 0→1 交叉过渡（`formFadeSeconds`）；
    - 两层同时可见的总时长 ≤ `formFadeSeconds`，不做真遮罩。
- 对外 API：
  - `public void Launch(Vector2 velocity)`（蓝筐辅助弹射用）
  - `public void PressDown()`（恐龙下压：设 `_passThroughUpperUntil = Time.time + passThroughSeconds`、`_vy = -pressDownSpeed`）
  - `public Vector2 Velocity => _vel;`、`public bool IsUpper => ...`
- 恐龙下压触发（在 `Update` 里，`dinoPressEnabled`）：
  1. 缓存 `FindObjectOfType<DinoAgentB1>()`；
  2. 球在上层、恐龙水平距离 < `dinoNoticeRange`、且球在地面附近（`|vy| < 1`）→ 调 `dino.ExternalWalkTo(ballX)`；
  3. 恐龙水平距离 < `dinoPressRadius` → `dino.ExternalHop()`（内部带冷却）；
  4. 恐龙离地且 `dino.IsFalling` 且水平距离 < `dinoPressRadius` → `PressDown()`，球穿过 `splitY` 落到下层。

### 新增 `Assets/Editor/PrototypeCBallPrefabMenu.cs`

菜单 `Tools/Prototype C/`：
- `创建球预制体`：从 `Assets/Art/Model/ball.glb` 实例化 → 加 `BallFormController` → 存为 `Assets/Prefabs/PrototypeC_Ball.prefab`（目录不存在则创建）。
- `为选中的篮筐添加投入检测`：给当前选中对象加 `BasketHoop`（已存在则跳过）。
- `在场景中添加泡泡奖励`：创建挂 `BubbleReward` 的空物体 `BubbleReward`。

---

## 3. 阶段 2｜篮筐与投入判定

### 新增 `Assets/Scripts/C/Play/BasketHoop.cs`（挂在 `篮筐` 上）

参数：
- `Vector3 rimLocalOffset = new(0, 0.35f, 0)`（筐口相对物体原点）
- `float rimHalfWidth = 0.55f`
- `float downSpeedThreshold = 0.5f`（`vy < -阈值` 才算向下）
- `float assistTriggerHeight = 1.6f`（未进球判定用的辅助平面高度）
- `int missesBeforeAssist = 2`
- `float assistChance = 0.65f`、`float assistFlightTime = 1.1f`
- `BubbleReward bubbleReward`（可留空）
- `float celebrateSeconds = 1.6f`、`Color confettiColorA/B`、`int confettiCount = 90`、`float confettiSize = 0.12f`

判定（横版无 Z）：
- 每帧遍历 `BallFormController.All`，用 `Dictionary<BallFormController, float> _lastY` 记录上一帧 Y。
- 进球条件（三条全满足）：
  1. `lastY > rimY && currentY <= rimY`（本帧向下穿过筐口平面）；
  2. `ball.Velocity.y < -downSpeedThreshold`（**向下**；向上穿过不触发）；
  3. `|ballX - rimX| <= rimHalfWidth`。
- 上次穿过筐口平面但没进（`dx` 超出半宽且向下）→ `_misses++`；到达 `missesBeforeAssist` 触发辅助。
- 辅助：恐龙 `ExternalWalkTo(rimX - 1.2f)` → `ExternalFace(+1)` → `ExternalHop()`，延迟后对球做抛体解算：
  `vx = dx / t`、`vy = dy / t + 0.5 * g * t`（`g = ball.gravity`）；
  以 `assistChance` 决定是否加误差（否则 `vx *= Random(0.6, 1.4)`、`vy *= Random(0.85, 1.15)`），然后 `ball.Launch(v)`。**不保证命中**。
- 庆祝：运行时 `new GameObject` + `ParticleSystem`（全屏礼花，从画面上方撒下，`confettiCount`、颜色 A/B、重力、销毁 `celebrateSeconds + 2s`）；
  `B2BackgroundRain` 若存在 → `Suspend(this)`，`celebrateSeconds` 后 `Resume(this)`；
  `bubbleReward` 非空 → `bubbleReward.Show()`（同一时间只允许一个泡泡，已在 `BubbleReward` 内部去重）。

---

## 4. 阶段 3｜泡泡奖励

### 新增 `Assets/Shaders/PrototypeC_Bubble.shader`

- Unlit 透明，`Queue = Transparent`、`ZWrite Off`、`Cull Off`。
- 采样 `_MainTex`（外部传入的 RenderTexture）：
  - 主采样：屏幕空间 UV（`ComputeScreenPos`）→ 表面直接显示当前相机画面；
  - 法线扰动：用视图空间法线的 xy 给 UV 加一个小偏移（`_Distortion`），制造球面折射感；
  - fresnel：`pow(1 - saturate(dot(N, V)), _FresnelPower)` → 边缘提亮（`_RimColor`）。
- `_Alpha` 整体透明度、`_RimColor`、`_Distortion`、`_FresnelPower` 全部可调。

### 新增 `Assets/Scripts/C/Play/BubbleReward.cs`

- 触发：`public void Show()`（篮筐进球时调用；`_active` 时直接返回）。
- 生成：
  - 泡泡根节点放在**相机画面正中央**（`mainCam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 10f))`，正交相机 Z 不影响）；
  - 整个泡泡根（含 TMP 倒计时）`layer = bubbleLayer`（默认 30）；
  - 球体：`GameObject.CreatePrimitive(PrimitiveType.Sphere)` + 新材质（`PrototypeC_Bubble.shader`）；
  - 倒计时：世界空间 TMP（`B1Text.Make`），3 秒，`countdownSeconds = 3f`，每秒刷新整数，字号可调。
- 拍照用的独立相机（关键坑）：
  - 运行时 `new GameObject("BubbleCaptureCamera")` + `Camera`，复制主相机 `transform`、`orthographic`、`orthographicSize`、`nearClipPlane`、`farClipPlane`、`backgroundColor`；
  - `cullingMask = mainCam.cullingMask & ~(1 << bubbleLayer)` → **泡泡本身（含倒计时文字）不入画**；
  - `targetTexture = new RenderTexture(photoWidth, photoHeight, 24)`（默认 1024×1024，或用主相机像素尺寸）；
  - 该相机 `enabled = false`，只在 `CapturePhoto()` 里手动 `Render()`（避免每帧额外开销）。
- 生命周期与统一收尾：
  - `Update()`：倒计时递减；`Input.GetMouseButtonDown(0)` / `Input.touchCount > 0` 时把屏幕点转世界点，距离 ≤ 泡泡半径 → `FinishSession(cancelled: true)`；
  - 倒计时到 0 → `CapturePhoto()` → `FinishSession(cancelled: false)`；
  - **`FinishSession()` 是唯一收尾入口**（幂等，`_finished` 守卫）：停倒计时 → `B2BackgroundRain.Resume(this)`（雨按 `resumeSeconds = 0.8s` 渐进恢复）→ 销毁 capture camera / 释放 RenderTexture → 播"缩小到恐龙大小 + 上升 + 房间上部随机飘动"（`shrinkTargetScale`、`riseSpeed`、`driftAmplitude`）；
  - `OnDisable` / `OnDestroy` 也调 `FinishSession()`，保证异常中断也能恢复雨；
  - 失败分支：`CapturePhoto` 内 `try/catch`，失败写 `Debug.LogError` 仍然走 `FinishSession()`。
- 拍照：
  `path = Path.Combine(Application.persistentDataPath, "BubblePhotos", $"bubble_{DateTime.Now:yyyyMMdd_HHmmss}.png")`；
  `RenderTexture.active = rt` → `Texture2D.ReadPixels` → `EncodeToPNG` → `File.WriteAllBytes`；
  成功后 `Debug.Log($"[Bubble] 照片已保存：{path}")`，并调 `PrototypeCTestUI`（若存在）的状态栏接口显示路径；不存在则只写 Console（不新增全局 Manager）。
- 展示期间的雨：`Show()` 时 `Suspend(this)`（暂停者集合），保证拍照画面里没有满屏字符雨；`FinishSession` 里 `Resume(this)`。
- 泡泡机占位：`public Transform bubbleMachine;`（可为空，仅用于显示生成位置/后续接模型）。

### 修改 `Assets/Scripts/B2/B2BackgroundRain.cs`

- 新增 `HashSet<object> _suspends` + `public void Suspend(object owner)` / `public void Resume(object owner)`。
- 新增 `float _density = 1f`、`float _densityTarget`、`float suspendSeconds = 0.25f`、`float resumeSeconds = 0.8f`（中文 Tooltip）。
- `Start` 里为每个槽位记录基础 alpha（`Column.baseAlpha[]`，取创建时的颜色 alpha）。
- `Update` 里 `_density = Mathf.MoveTowards(_density, _densityTarget, dt / 时长)`，然后对每个槽位 `tmp.alpha = baseAlpha * _density`。
- **不修改 `enabled`**，不影响其他雨开关（庆祝特效与泡泡各持 owner，互不覆盖）。

### 修改 `Assets/Scripts/B1/DinoAgentB1.cs`

只**新增**一段 `Externally driven` 公共 API，不改原状态机：
- `public void ExternalWalkTo(float x)`：设 `_desiredX`、`_state = State.Wandering`、重置 `_timer`；
- `public void ExternalFace(int dir)`：调已有 private `Face(dir)`；
- `public void ExternalHop()`：只在贴地时给 `_vy = hopForce`，带内部冷却字段；
- `public bool IsFalling => _vy < -0.1f;`、`public bool IsAirborne => Mathf.Abs(_vy) > 0.01f;`
- `public bool ExternalBusy => _state == State.Fleeing || _state == State.Collecting || _state == State.Building;`

实现前先读取文件末尾 `ApplyMotion / Face` 区段，确认 `_vy` 的符号与 `transform.position.y` 的写入方式，避免重复施加位移。

---

## 5. 文档

`Docs/PrototypeC_Placement.md` 追加一节「玩法：球 / 篮筐 / 泡泡」：预制体球与 toon 说明、上下层切换、下压触发、筐口判定规则（向下才有效）、庆祝与雨、泡泡拍照路径与图层排除、用户操作清单、已知风险。

---

## 6. 验证方式（不启动 Unity）

1. 运行时程序集编译：`%TEMP%\pc_build_check.ps1` → 期望 `EXIT=0`（`INJECTED=0`）。
2. 编辑器程序集编译：`%TEMP%\pc_editor_check.ps1` → 期望 `EXIT=0`。
3. 静态自检清单：
   - 所有新增 public 字段都有 `[Tooltip("中文")]`；
   - `BubbleReward` 的所有退出分支都汇聚到 `FinishSession()`；
   - `B2BackgroundRain` 的 `enabled` 未被任何新代码修改；
   - 无 `Rigidbody`、无新 Manager、无场景文件改动。

---

## 7. 需要用户在编辑器里做的动作（不改场景文件）

1. 运行菜单 `Tools/Prototype C/创建球预制体` 生成 `Assets/Prefabs/PrototypeC_Ball.prefab`。
2. 把该预制体拖进场景（替换或删除已有的 `ball` 实例），位置随意（建议上层 `y≈3.6`）。
3. 选中 `篮筐`（`model` 实例）→ 菜单 `Tools/Prototype C/为选中的篮筐添加投入检测`，然后在 Inspector 里调 `rimLocalOffset / rimHalfWidth` 对准筐口，并把场景里的 `BubbleReward` 拖到 `bubbleReward`。
4. 菜单 `Tools/Prototype C/在场景中添加泡泡奖励`（若还没加），确认其 `mainCamera` 自动找到 `Main Camera`。
5. 确认 `DinoAgentB1` 的 `groundY` 仍是 `1.84`（下压/辅助的表现基准）。

---

## 8. 已知风险

1. **泡泡观感**：屏幕 UV 采样 + 法线扰动的折射是近似，正交相机下球面边缘会有拉伸；需在 Inspector 调 `_Distortion`、`_FresnelPower`、`_RimColor`。
2. **拍照时序**：capture 相机只在 `CapturePhoto()` 手动 `Render()`，拿到的是"倒计时归零那一帧"的画面，不是玩家看到的上一帧；雨已暂停、泡泡已排除，但恐龙/粒子的瞬时姿态可能差一帧。
3. **雨恢复**：密度恢复依赖 `B2BackgroundRain` 实例存在；若场景里雨被禁用（`enabled=false`），`Suspend/Resume` 不生效但不报错。
4. **恐龙 API**：新增的 `External*` 会打断当前状态机（散步/发呆），连续触发可能让恐龙看起来"被玩家指使"，属预期。
5. **预制体球材质**：`ToonMaterials.Apply` 在运行时创建材质实例，不进 Asset，重启 Play 会重建；若球有多个材质槽会各自生成 toon 材质。
