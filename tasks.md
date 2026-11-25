# Tasks — 地面落位修复 / 场景与 Gizmo 剪枝

## 阶段 A：地面高度

- [x] A1. `ObjectModelService.cs` 新增 `ResolveWorldGroundY(float worldX)`
  - 以 `roomLayout.dinoGroundZones` 为唯一真相；分区内取 `groundY`，分区外取最近分区边缘；无分区退回 `ToWorldY(groundBandMinY)`
  - 位于 `ToWorldY` 之后，仅用 `On` 生命周期之外的纯计算
- [x] A2. `PlaceInstance` Ground 分支改用 `ResolveWorldGroundY(fitted.center.x) + fitted.extents.y`
  - Wall / Rooftop 分支不动
- [x] A3. 场景 `TemplateC_LanMay.unity` 的 `room.groundY`：`1.88` → `2.04`
  - 室外两段保持 1.61

## 阶段 B：Gizmo 剪枝

- [x] B1. 删除 `ObjectModelService.OnDrawGizmosSelected`、`Overlaps`、`GizmoPlaneOffset`、`GizmoSlotColumns`
- [x] B2. 删除 7 个仅 Gizmo 使用的字段：`showPlacementGizmos`、`groundBandGizmoColor`、`wallBandGizmoColor`、`ceilingBandGizmoColor`、`slotGizmoColor`、`gizmoFillAlpha`、`gizmoConflictColor`
- [x] B3. `RoomLayout.OnDrawGizmosSelected` 删除「命名锚点」绘制块和 `anchorColor`
- [x] B4. `RoomLayout.OnDrawGizmosSelected` 新增「恐龙地面分区」世界坐标绘制（横线 + 端柱 + 标签，含 name/groundY/x 范围）

## 阶段 C：验证

- [x] C1. 静态检查：无残留引用（`showPlacementGizmos`、`Overlaps`、`anchorColor`）；两个脚本括号配平
- [ ] C2. Unity 编译通过；选中控制器只显示一套 Gizmo
- [ ] C3. Play 验收：椅子贴室外地面、床贴房内可视地板、恐龙分区站立与跨区小跳、台阶单向性
- [ ] C4. 选一个初始摆放物件执行 `Apply Toon To Selection (Foreground)`，确认描边

## 阶段 D：不在本轮范围

- 恐龙反躺姿势（用户明确后处理）
- `layout.json` 回放仍用存档 `sizeClass`（风铃 Small）的尺寸问题

## 恐龙行为与状态图标轮（已实现，待 Unity 内验收）

- [x] E1. 盘点：DinoCController 全文、场景 dino 层级（根节点 localScale 翻转 + 底部枢轴 18×20 贴图原生朝右）、PlacedItem 字段、DOTween 不在工程内
- [x] E2. 新增 `Assets/Scripts/C/DinoStatusIcon.cs`：底图+内容双 SpriteRenderer、7 类占位图、出现/消失/切换补间、防镜像与恒定世界尺寸（LateUpdate 补偿父级 lossyScale/旋转）、持续图标与短暂图标分离 + 最短显示防抖
- [x] E3. `DinoCController` 接入图标：! 新物件、? 首见、♥ 喜欢、☺ 推动/玩完/睡饱、ZZZ 睡着、💦 疲惫、... 到站发呆
- [x] E4. 精力系统：漫步/互动消耗、躺下恢复（睡着更快）、疲惫阈值调休息权重、耗尽强制休息、睡饱自然醒
- [x] E5. 兴趣记忆：每物件互动次数 + 好感度；选择评分=距离×(好感+新奇−重复)；玩腻/刚玩过跳过
- [x] E6. 反躺修复：躺下旋转 `-face` → `+face`（头朝上坡）
- [x] E7. 外部玩法接管时收起短暂图标（HideBrief）
- [x] E7b. 真实资源接入 + 编辑器可调：场景 dino 上预置 `DinoStatusIcon`（绑定 PinosaurIconBg 与 PinosaurIcons-Sheet 七张子图，所有参数序列化）与 `DinoCController`（精力/兴趣/图标时长等参数均进 Inspector，运行时不再黑盒）；bootstrap 已支持复用场景内已存在组件
- [ ] E8. Unity 编译 + Play 验收（见 checklist「恐龙轮」）

## 吹泡泡奖励过场轮（已实现，待 Unity 内验收）

- [x] F1. 新增 `DinoBubbleSequence`：礼花 + 暂停（BONUS TIME）、泡泡水 HDR 降落、恐龙靠近跳两下、恐龙走出画面、大恐龙入场、Canvas 提示、麦克风吹气、泡泡长大、Canvas 3 秒倒计时、指尖二选一、恢复游戏
- [x] F2. `BubbleReward` 扩展：`ShowGrowing`（原地从小长大）、倒计时改走 UI、拍照后等待选择、`KeepCurrent / DiscardCurrent`、升到天花板检测；旧 `Show()` 行为保持不变
- [x] F3. 新增 `FingertipChoiceButton`：指尖停留触发（复用打字机删除手势的停留/离开解锁）
- [x] F4. `DinoCController` 增加 `ExternalLeaveScreen / ExternalEnterFromScreen`（过场期间绕过活动范围夹取）
- [x] F5. `WhisperVoiceListener` 暴露 `CurrentRms`：吹气判定复用同一路麦克风，不再开第二路
- [x] F6. 场景：新增 `PrototypeC_BubbleSequence` 与 `泡泡区域` 标记（挂在 dino (1) 下）；压暗层 / 大恐龙排序提到 500 / 501
- [x] F7. 接入 DOTween（`Assets/Plugins/Demigiant/DOTween`），新过场的补间全部走 DOTween
- [ ] F8. Unity 编译 + Play 验收（见 checklist「吹泡泡奖励轮」；F10 播放 / F11 跳过 / 按住 B 模拟吹气）
