# Tasks｜球的上下层玩法 + 篮筐 + 泡泡奖励

依赖顺序即执行顺序。每完成一项勾掉，不跳步。

## T1 基础设施：雨的暂停/恢复（无依赖，先做，其他都依赖它）

- [x] `Assets/Scripts/B2/B2BackgroundRain.cs`
  - 新增 `HashSet<object> _suspends`、`Suspend(object)`、`Resume(object)`。
  - 新增 `_density` / `_densityTarget` / `suspendSeconds = 0.25f` / `resumeSeconds = 0.8f`（中文 Tooltip）。
  - `Column` 增加 `float[] baseAlpha`，`Start` 建池时记录。
  - `Update` 末尾按 `baseAlpha * _density` 写回每个 `tmp.alpha`。
  - 禁止修改 `enabled`。

## T2 恐龙最小外部 API

- [x] 读 `Assets/Scripts/B1/DinoAgentB1.cs` 的 `ApplyMotion` / `Face` 区段，确认 `_vy` 与 y 写入方式。
- [x] 在类末尾新增：`ExternalWalkTo(float)`、`ExternalFace(int)`、`ExternalHop()`、`IsFalling`、`IsAirborne`、`ExternalBusy`。
- [x] 不修改 `Update` 的状态分支逻辑（只在 `UpdateWandering` 开头加"外部窗口"提前返回）。

## T3 球预制体 + 双形态

- [x] 新增 `Assets/Scripts/C/Play/BallFormController.cs`（见 spec §2：物理、注册表、toon、字符画、`Launch` / `PressDown`、恐龙下压触发）。
- [x] 新增 `Assets/Editor/PrototypeCBallPrefabMenu.cs`（三个菜单项）。

## T4 篮筐

- [x] 新增 `Assets/Scripts/C/Play/BasketHoop.cs`
  - 筐口平面穿越 + 向下的速度判定；向上穿过不触发。
  - 未进计数 → 恐龙辅助抛体弹射（可调概率/力度/误差）；另加 `idleThrowSeconds` 打破"没人抛球就永远不触发"的死循环。
  - 庆祝礼花 `ParticleSystem` + `rain.Suspend(this)` 定时 `Resume` + `BubbleReward.Show()`。

## T5 泡泡奖励

- [x] 新增 `Assets/Shaders/PrototypeC_Bubble.shader`（Unlit 透明、屏幕 UV 采样 RT、法线扰动、fresnel）。
- [x] 新增 `Assets/Scripts/C/Play/BubbleReward.cs`
  - `Show()` → 居中泡泡 + 3D RenderTexture capture 相机（cullingMask 排除 `bubbleLayer`）+ 3 秒世界 TMP 倒计时。
  - 点击/触摸戳破 → `FinishSession(true)`；到点 → `CapturePhoto()` → `FinishSession(false)`。
  - `FinishSession()` 幂等，统一恢复雨、销毁相机/RT、缩小上升到房间上部飘动。
  - PNG 存 `Application.persistentDataPath/BubblePhotos/`，日志 + 状态栏给路径。
  - `bubbleMachine` 占位字段。

## T6 文档

- [x] `Docs/PrototypeC_Placement.md` 追加「玩法：球 / 篮筐 / 泡泡」一节（含用户操作清单与风险）。

## T7 编译验证

- [x] `%TEMP%\pc_build_check.ps1` → `EXIT=0`。
- [x] `%TEMP%\pc_editor_check.ps1` → `EXIT=0`。
- [x] 静态自检：Tooltip 齐全、`FinishSession` 单入口、雨 `enabled` 未改、无场景文件改动。

## 交付

- [x] 一次性中文汇总：每阶段改动、文件清单、用户操作、风险（泡泡观感 / 拍照时序）。
