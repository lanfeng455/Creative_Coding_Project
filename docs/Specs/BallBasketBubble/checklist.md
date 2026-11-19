# Checklist｜球的上下层玩法 + 篮筐 + 泡泡奖励

## 编译验证
- [x] 运行时程序集：`%TEMP%\pc_build_check.ps1` → `INJECTED=3`、`EXIT=0`
  （脚本已扩到同时注入 `Assets/Scripts/C/Play`，否则新文件不进临时 csproj）
- [x] 编辑器程序集：`%TEMP%\pc_editor_check.ps1` → `INJECTED=1`、`EXIT=0`

## 静态自检
- [x] 新增 public 字段全部带中文 `[Tooltip]`（BallFormController / BasketHoop / BubbleReward / B2BackgroundRain 新增项）
- [x] `BubbleReward` 的所有退出分支（自动拍照、戳破、OnDisable、OnDestroy、拍照异常）都汇聚到 `FinishSession()`
- [x] `FinishSession()` 幂等（`_finished` 守卫），且一定会调 `B2BackgroundRain.Resume(this)`
- [x] `B2BackgroundRain.enabled` 未被任何新代码修改；暂停用 owner 集合，庆祝与泡泡互不覆盖
- [x] 雨恢复是渐进密度（`resumeSeconds = 0.8`，`_density` 由 `MoveTowards` 过渡），不是瞬间满屏
- [x] 无 `Rigidbody`、无新的全局 Manager、无 `.unity` 场景文件改动
- [x] 篮筐判定要求 `vy < -downSpeedThreshold`，向上穿过不会触发
- [x] 拍照相机 `cullingMask` 排除 `bubbleLayer`，泡泡根（含倒计时 TMP）整体在该 layer
- [x] 照片路径 `Application.persistentDataPath/BubblePhotos/bubble_yyyyMMdd_HHmmss.png`，日志与状态栏都给出完整路径

## 未验证（需要你在 Unity 里看）
- [ ] 泡泡 shader 的实际观感（`_Distortion` / `_FresnelPower` / `_RimColor`）
- [ ] 拍照那一刻的画面是否符合预期（恐龙/粒子可能差一帧）
- [ ] 恐龙下压、辅助弹射的手感与频率
