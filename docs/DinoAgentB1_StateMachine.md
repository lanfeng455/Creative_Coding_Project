# DinoAgentB1 状态机 / 物件交互 现状梳理

> 只读梳理，未改任何代码、场景与预制件。
> 依据文件：`Assets/Scripts/B1/DinoAgentB1.cs`（899 行）、`Assets/Scenes/TemplateC_LanMay.unity`、`Assets/Scripts/C/PrototypeCStage.cs`、`Assets/Scripts/C/ObjectModelService.cs`、`Assets/Scripts/C/GeneratedObjectPhysics.cs`、`Assets/Scripts/C/Layout/RoomOccupancy.cs`、`Assets/Scripts/B1/B1EventScheduler.cs`、`Assets/Scripts/B1/TypeWriterB1.cs`。
> 下文行号若无特别说明均指 `DinoAgentB1.cs`。

---

## 0. 结论速览

1. 状态机共 **8 个状态**（`State` 枚举 L13），全部有实现代码，**没有「未开始」或「纯留桩」的状态**。
2. C 阶段实际只跑 **3 个**：`Wandering`、`Resting`、`Interacting`。其余 5 个（`Sitting` / `PlayingBall` / `Fleeing` / `Collecting` / `Building`）的进入路径在 `TemplateC_LanMay` 里全部被门控或缺少触发源。
3. 状态机对生成物件**唯一的动作是 `Interacting`**：走过去、停下、面向它、做压扁动画。**不施力、不检测接触、不读 `behavior`/`placement`**（只读 `band == Ground`）。
4. 真正让 plant 被推动的是**物理层**，不在状态机里：恐龙本体的 `BoxCollider` + 运行时补的 kinematic `Rigidbody`，碰到物件时由物件身上的 `GeneratedObjectPushRelay.OnCollisionEnter` 加 `0.80 m/s` 速度。恐龙这一侧没有任何碰撞回调，**不知道发生过推动**。
5. “主动要 BALL”的旧逻辑在代码里仍在，但 C 阶段被三处切断：`RequestedWord` 直接返回 null（L67）、`PickNeed` 改走 `PickMood`（L310-314）、`UpdateWandering` 不追球（L570）、`TryStompBall` 直接 return（L519）。场景里还残留三个已不在代码中的序列化字段（`cStageBallKick*`），属于孤儿数据。

---

## 1. 状态机总览表

| 状态 | 实现位置 | 进入条件（实际数值） | 持续时间 | 对物件做了什么 | C 阶段 | 完成度 |
|---|---|---|---|---|---|---|
| `Wandering` 漫步 | L543-593 | `Start` L208；任何状态退出时 `EnterWander()`；C 阶段由 `PickMood` 的「发呆」分支换目标（L372-375） | `wanderIntervalMin~Max` = **2~5 s**，或到点（<0.25 m）提前结束 | 非 C 分支会查找 `B1Resource`（建材）、`B1Ball`（6 m）、`B1Chair`（12 m）；C 分支**不查找任何物件** | 可达 | 已实现 |
| `Resting` 休息 | L595-598（进入点 L363-369、L582-588） | ① `PickMood` 权重 **2**，且 `_restCooldown <= 0`；② 漫步结束时有 **50%** 概率，且 `_restCooldown <= 0` | `restDurationMin~Max` = **4~9 s**；结束后冷却 `Random(12, 26)` s | 无 | 可达 | 已实现 |
| `PlayingBall` 玩球 | L600-620 | `_kickCd <= 0` 且 `!_cStageActive` 且球在 `ballNoticeRange` = **6 m** 内（L569-574） | 无固定时长，踢到不想玩为止（每次踢后 **25%** `boredChance` 退出）；踢球冷却 **1.2 s** | 查找 `B1Ball`；距离 ≤ `kickRange` = **1.1 m** 时 `Launch(6.5×[0.7,1.15], vy 2.2~3.8)`，直接给球速度 | **不可达**（L570 门控） | 已实现（C 关闭） |
| `Sitting` 坐椅子 | L622-632 | 漫步结束：`B1Chair.NearestChair(12 m)` 非空且 **45%** 概率（L580-581） | `restDurationMin~Max` = **4~9 s**；够不到 8 s 放弃 | 只查找 `B1Chair`；坐下仅压扁视觉（L736、L741 的 0.82），不施力 | **不可达**（场景无椅子 + 拼词造椅被关，见 §4） | 已实现（C 不可达） |
| `Interacting` 互动 | 进入 L405-419，运行 L422-448，选目标 L379-403 | `PickMood` 权重 **4**，且 `_interactCooldown <= 0`，且 `FindInteractTarget()` 非空 | `interactDuration` = **2.5~5 s**；结束后冷却 `Random(4, 10)` s | 读 `RoomOccupancy.Items` 里 `band == Ground` 的物件，按 **X 距离** 取最近（≤12 m）；走到 `targetX ± 0.9 m`（夹在活动范围内），到点后面向 + 上下压扁 | 可达（C 专属） | 已实现（仅视觉） |
| `Fleeing` 逃跑 | L646-676；受惊入口 `Scare()` L823-838 | ① 场上存在 `FireHazard.ActiveFire`（L221-228，最高优先级）；② `RedThreatGlyph` 进入 `threatNoticeRange` = **3.2 m**（L231-242）；③ 外部调用 `Scare()` | 火灭后 **2.5 s** 退出；受惊后 **1.5 s** 优先躲威胁；与危险源保持 `fleeSafeDist` = **4.2 m** | 无 | **不可达**（天灾被关 + 场景无火，见 §4） | 已实现（C 无触发源） |
| `Collecting` 捡建材 | L452-470 | `NeedsWoodHouse`（`constructionEnabled` 为真）且上层有 `B1Resource`（L261-266、L562-567） | 走到资源 **0.55 m** 内即拾取 | 查找 `B1Resource`（Wood/Stone），`Consume()` 或 `Carry()` | **不可达**（L186 关建造） | 已实现（C 关闭） |
| `Building` 盖房 | L473-510 | 由 `Collecting` 拾取后转到（L468），或逃跑回来时手上还拿着圆木（L652） | 到 `BuildSiteX` 的 **0.4 m** 内累计 **1.1 s**（拿圆木时立即） | 操作 `B2House.AddWoodStep/AddRoom` | **不可达**（同上） | 已实现（C 关闭） |

---

## 2. 逐状态明细

### 2.1 `Wandering` 漫步（L543-593）

- 进入：`Start()` 末尾 L208；任何状态结束都经 `EnterWander()`（L543-549），重置 `_timer=0`、`_stateDur=Random(2,5)`、`PickWanderTarget()`（L678-682，在 `[RangeLo, RangeHi]` 内随机）。
- 每帧 `UpdateWandering`：
  - **外部驱动优先**（L555-559）：`Time.time < _externalHoldUntil` 时只朝 `_externalTargetX` 走，不重新挑点。这是全文件唯一一处为外部玩法加的分支。
  - 非 C 阶段：建材需求 → 切 `Collecting`（L562-567）；球在 6 m 内 → 切 `PlayingBall`（L569-574）。
  - C 阶段（`_cStageActive`）这两个分支都不成立：`constructionEnabled=false`，且 L570 显式 `!_cStageActive`。
  - 到点（`|x-_desiredX| < 0.25`）或 `_timer > _stateDur` 后：椅子（12 m / 45%）→ 坐在原地休息（`_restCooldown<=0` 且 50%）→ 否则重挑漫步点（L576-592）。
- 对物件：只做「查找」，不产生接触、不施力、不读 `behavior/placement`。
- 活动范围：`RangeLo/Hi` 由 `activityXMin/Max` + `edgeMargin 0.8` 决定；场景值 `-5.95 / 5.9`，即**漫步范围 X ∈ [-5.15, 5.1]**（L113-114）。硬夹紧用 `ClampLo/Hi`（±0.4），`ApplyMotion` L704 每帧夹一次。
- 完成度：**已实现**。已知限制：C 阶段不会主动走向任何物件。

### 2.2 `Resting` 休息（L595-598 + C 阶段躺下）

- 进入两条：`PickMood`（L363-369，权重 2，`_restCooldown<=0`）与漫步结束（L582-588，50%）。两处都设 `_stateDur=Random(4,9)`、`_restCooldown=Random(12,26)`。
- 运行：只等计时（L597）。`ApplyMotion` 中 `Resting` 速度为 0（L696），不移动。
- C 阶段额外表现：`UpdateLieBlend`（L296-304）在 `_cStageActive && restLieAngle>1 && 贴地` 时把 `_lieT` 推向 1，`ApplyVisual` L752-753 绕脚底旋转 `restLieAngle=90°`，同时 `ApplyMotion` L714 把地面抬 `restLieLift=0.35 m` 防止半埋。旋转/抬起共 `restLieDuration=0.55 s`。
- 对物件：无。
- 完成度：**已实现**。

### 2.3 `PlayingBall` 玩球（L600-620）

- 进入：仅在 `UpdateWandering` L569-574，条件 `_kickCd<=0 && !_cStageActive && B1Ball.NearestOnUpper(6 m) != null`。
- 行为：`_desiredX = 球.x`；当 `|dx| <= 1.1` 且冷却到 → 给球 `Launch(dir*6.5*[0.7,1.15], vy 2.2~3.8)`，`_kickCd=1.2`，25% 概率退出。
- 注意：踢球方向有「往缺口送」的偏置（L612-613），这是 B1 时代的玩法。
- 对物件：**对球施加物理速度**（唯一一处状态机主动施加力/速度的地方，对象是 `B1Ball`，不是生成物件）。
- 完成度：**已实现**，C 阶段被 L570 关掉。

### 2.4 `Sitting` 坐椅子（L622-632）

- 进入：漫步结束，`B1Chair.NearestChair(12 m)` 非空且 `Random.value < 0.45`（L580-581）。
- 行为：走到椅子 X，**0.3 m** 内坐下（纯压扁 0.82，L736/L741），`_stateDur` 到点或 8 s 够不到就退出。
- 对物件：只查找 `B1Chair`，无接触检测、无施力。
- 完成度：**已实现**；C 阶段**不可达**：场景无 `B1Chair` 实例，且拼出 CHAIR 生成椅子的路径被 `TypeWriterB1` L96 关闭。

### 2.5 `Interacting` 互动（L379-448，C 阶段专属）

- 进入：只有 `PickMood` L358-362 一条路（`_cStageActive` 才会走到）。权重 `cStageInteractWeight=4`，前提 `_interactCooldown<=0`（初始 `Random(4,10)`，L192）且能找到目标（否则权重让给发呆，L351）。
- 选目标 `FindInteractTarget()`（L379-403）：
  - 取 `RoomOccupancy.Items`（`RoomOccupancy.cs` 里 `PlacedItem`，含 `instance / band / behavior / pushable` 等字段）；
  - **只过滤 `item.band != RoomBand.Ground`**（L391）；
  - 距离只算 **`|candidate.x - dino.x|`**（L394），上限 `interactNoticeRange=12 m`；
  - 不做 Z 判断、不做可达性/遮挡判断、不读 `behavior`、不读 `pushable`。
- 运行 `UpdateInteracting()`（L422-448）：
  - `side` = 恐龙当前在物件左边还是右边；`standX = targetX + side*0.9`，再 `Clamp(RangeLo, RangeHi)`（L432）；
  - `_desiredX = standX`，`ApplyMotion` 以 `walkSpeed=2.2 m/s` 走过去（`Interacting` 落入 default 分支，L697）；
  - 到 `|x-_desiredX| < 0.25` 后停下（`ApplyMotion` 的 0.05 阈值先让它停），`Face()` 朝向物件；
  - 计时到 `_stateDur`（2.5~5 s）→ 设 `_interactCooldown=Random(4,10)`、清目标、`EnterWander()`；
  - 视觉：`ApplyVisual` L738/L742 在 `|x-_desiredX| < 0.35` 时把 `_visualY` 拉成 `0.88 + |sin(t*3.2)|*0.14`，即每秒约 0.5 次的「顶一下」压扁。
- **不施力、不检测接触、不读取 `behavior`/`placement`、不产生任何结果**（物件是否移动、是否可推，状态机都不知道）。
- 完成度：**已实现（仅视觉）**。已知限制：若物件在活动范围外，`standX` 被夹到边界，恐龙会贴着边界对空气压扁，仍算「已到位」。

### 2.6 `Fleeing` 逃跑（L646-676 + L823-838）

- 进入：① `FireHazard.ActiveFire != null`（L221-228，强制）；② `RedThreatGlyph.All` 中最近者 < `threatNoticeRange=3.2 m`（L231-242，`hit:false`）；③ 外部 `Scare(x, hit)`。
- 行为：逃到离危险源 `fleeSafeDist=4.2 m` 的同侧点（夹在活动范围），速度 `fleeSpeed=3.8 m/s`（L694）；已够远则原地小幅踱步（±0.9 m）。火灭后 `_fleeTimer>2.5 s` 退出；受惊后 1.5 s 内优先躲威胁（L657-662）。
- 对物件：无。
- 完成度：**已实现**；C 阶段**无触发源**（见 §4）。

### 2.7 `Collecting` 捡建材（L452-470）

- 进入：`NeedsWoodHouse` 时 `Update` L261-266 强制切；或漫步中 L562-567。
- 行为：`_desiredX = 资源.x`，`|dx| < 0.55` 时 `Consume()`（石头）或 `Carry()`（圆木），然后切 `Building`；速度 `playSpeed=3.0`（L695）。
- 对物件：对 `B1Resource` 做消耗/搬运，不读 `behavior`。
- 完成度：**已实现**；C 阶段**不可达**（L186 `constructionEnabled=false` → `NeedsWoodHouse` 恒 false）。

### 2.8 `Building` 盖房（L473-510）

- 进入：`Collecting` 拾取后（L468）；或逃跑结束时手上还有圆木（L652）。
- 行为：走到 `_house.BuildSiteX`，`|dx| < 0.4` 累计 1.1 s（拿圆木时立即）→ `AddWoodStep`（一次推进 `woodStepsPerLog=4` 格）或 `AddRoom`；完成后 `CurrentNeed` 置 `WOOD`/`BALL` 并回漫步。
- 完成度：**已实现**；C 阶段**不可达**。

---

## 3. 物件交互专项

### 3.1 状态机层：只有 `Interacting`，且只到「视觉」

| 问题 | 答案 | 证据 |
|---|---|---|
| 是否寻找物件 | 是，但只找 `RoomOccupancy` 已登记的**地面**物件，按 X 最近 | L379-403 |
| 是否检测接触 | **否**。没有任何 `OnCollision*` / `OnTrigger*` / 距离 < 自身尺寸的判断 | 全文件无碰撞回调 |
| 是否施加力/位移给物件 | **否**。`Interacting` 只改恐龙自己的 `_desiredX`、朝向与缩放 | L422-448 |
| 是否读 `behavior/placement` | **否**。只读 `PlacedItem.band`，且只判 `== Ground`；`behavior`（Touch/Rest/Play/Ignore）与 `pushable` 完全没读 | L391 |
| 是否有结果反馈 | **否**。互动结束只清目标 + 进冷却 | L440-447 |

### 3.2 物理层：真正推动物件的是谁

现况是两段互相不知道对方存在的链路：

**恐龙侧**
- 场景里 `dino` 已经有一个手填的 `BoxCollider`（`TemplateC_LanMay.unity` L4239-4259）：
  - `m_Size = {0.5625, 0.625, 4.59}`（局部单位；`dino` 的 `localScale=2`，世界尺寸 = **1.125 × 1.25 × 9.18 m**）
  - `m_Center = {0, 0.3125, 0}`，`m_IsTrigger = 0`，**没有序列化的 `Rigidbody`**。
- 运行时：只要有 pushable 物件落位，`ObjectModelService` L879-883 → `EnsureDinoBodyCollider()` → `GeneratedObjectPhysics.AddDinoBodyCollider`（L25-85）：
  - 复用场景里的 `BoxCollider`，**按精灵世界包围盒 × `dinoColliderScale` 重算 X/Y，按 `dinoColliderDepth` 重算 Z**；
  - 加 `isKinematic = true` 的 `Rigidbody`；
  - 加 `DinoBodyContactSync`（L201-232）：`LateUpdate` 检测到恐龙 transform 变化就 `Physics.SyncTransforms()`，让每帧直写的位移/缩放进入 PhysX。
  - 场景当前值：`dinoColliderScale=1.2`、`dinoColliderDepth=2`、`dinoColliderZOffset=-0.75`、`createTouchTestPusher=1`（`TemplateC_LanMay.unity` 的 `ObjectModelService` 段）。
  - **冲突点**：运行时会把场景里手填的 Z=4.59（世界 9.18 m）覆盖成 `dinoColliderDepth=2`（世界 2 m）。想保留长盒就把 `dinoColliderDepth` 调到 4.59；具体实际数值以运行时日志 `[PrototypeC] 恐龙本体碰撞箱：…` 为准。
- 恐龙的状态机**没有碰撞回调**：被物件挡住、推开了物件，`DinoAgentB1` 都感知不到（它只写 `transform.position`，物理不会反过来推 kinematic 刚体）。

**物件侧**
- Touch+Ground 的物件在装配时挂 `GeneratedObjectPushRelay`（`GeneratedObjectPhysics.cs` L156-191）。
- `OnCollisionEnter` 条件（L173-174）：`collision.rigidbody != null && collision.rigidbody.isKinematic`。
- 满足后 `AddForce(dir * 0.8, VelocityChange)` + `WakeUp()`（L180-181），日志 `[PrototypeC] Touch 生效：…`。
- 因为恐龙本体是 kinematic，条件成立；**若 `EnsureDinoBodyCollider` 没跑过（只放了非 pushable 物件），场景里那个 `BoxCollider` 是静态碰撞体，`collision.rigidbody` 为 null，不会推**。

### 3.3 BALL 相关现状

| 项 | 现状 | 证据 |
|---|---|---|
| 主动要 BALL（气泡喊 BALL） | C 阶段**已切断**：`RequestedWord` 在 `_cStageActive` 时直接 `return null` | L62-74（L67） |
| 需求系统选 BALL | 旧逻辑仍在：`PickNeed` 在建造关闭或无球时设 `CurrentNeed="BALL"`；但 C 阶段 L310-314 直接转 `PickMood`，不设置任何需求 | L307-334 |
| 作为字雨权重 | `CurrentNeed` 被 `B1EventScheduler` 用于加权；C 阶段 `CurrentNeed` 除不可达分支外恒为 null | L59-60、L245-252 |
| 追球 | C 阶段**关掉**：`!_cStageActive` 才进 `PlayingBall` | L569-574 |
| `TryStompBall` | C 阶段**首行 return**（L519）；B1/B2 触发条件：`_vy <= -0.5`、`|ddx| < 0.6`、`ddy ∈ (-0.2, 1.0)` → `Launch(dir*[4.5,7], 1.2)`、恐龙弹起 `hopForce*1.8 = 2.16` | L516-539 |
| 场景残留字段 | `cStageBallKickDirectionX: 0`、`cStageBallKickSpeed: {4.5, 7}`、`cStageBallKickUpSpeed: 0` 三个字段**当前脚本里不存在**（`TemplateC_LanMay.unity` L4206-4208），是某次改动留下的孤儿数据，Unity 下次保存会把它们丢掉。说明曾有一版把 C 阶段踢球参数化，现已不在代码里 | — |

### 3.4 「靠近物件并与之交互」的所有路径

| 路径 | 谁发起 | 对象 | 现状 |
|---|---|---|---|
| `Interacting` | 恐龙自己（C 阶段权重轮盘） | 生成物件（`RoomOccupancy` 地面项） | 已实现，仅走到旁边 + 面向 + 压扁，不施力 |
| `WalkTo / Face / Hop` 外部驱动 | `BasketHoop`（场景 L1073，如 L252-259 的守门/起跳）、`BallFormController` | 球 | 已实现；C 场景**没有 `BallFormController` 实例、也没有球**，所以当前不触发 |
| `TryStompBall` | 恐龙自己（下落时） | 球 | B1/B2 已实现；C 关闭 |
| `Scare` | `RedThreatGlyph` | 无 | B1 已实现；C 无威胁 |

`ExternalBusy`（L849）只保护 `Fleeing/Collecting/Building`，**不保护 `Interacting`**：外部驱动（球）可以在互动中途把恐龙拉走（`ExternalWalkTo` L860-864 会把状态改回 `Wandering`）。

---

## 4. TemplateB1 遗留 vs C 阶段新加（门控点）

门控总开关：`PrototypeCStage.IsTrimmedScene()`，按场景名 `"TemplateC_LanMay"` 判定（`PrototypeCStage.cs` L17、L20-23）。

| 门控点 | 位置 | 效果 |
|---|---|---|
| `constructionEnabled = false` | `DinoAgentB1.cs` L186 | 关掉 `Collecting` / `Building` / `NeedsWoodHouse`，也不再建 `B2_House`（L198-205） |
| `_cStageActive = cStageMoodEnabled && IsTrimmedScene` | `DinoAgentB1.cs` L189 | 启用 `PickMood` 轮盘、躺下休息、`Interacting`；同时关掉喊 BALL 与追球/踩球 |
| `threatsEnabled = false` | `B1EventScheduler.cs` L52 | 不生成 `FireHazard` / `RedThreatGlyph` → `Fleeing` 无触发源 |
| `chairCraftingEnabled = false` | `TypeWriterB1.cs` L96 | 拼 CHAIR 不再生成椅子 → `Sitting` 无目标 |
| （附带）`B2Table.cs` L31 | 同场景判定 | 与状态机无关，但同属 C 裁剪 |

**TemplateB1 遗留、在 C 被关掉**：`Collecting`、`Building`、`PlayingBall`、`Sitting`、`Fleeing`、`PickNeed` 的 BALL/建材分支、`TryStompBall`、气泡需求（`RequestedWord`）、造椅。

**C 阶段新加**：

| 内容 | 位置 |
|---|---|
| `State.Interacting` 枚举值 | L13 |
| `_cStageActive` 与相关 Inspector 字段 | L26-52、L165、L189-195 |
| `PickMood()` 权重轮盘 | L340-376 |
| `FindInteractTarget()` / `EnterInteracting()` / `UpdateInteracting()` | L379-448 |
| 躺下休息（`restLie*`、`_lieT/_lieEase`、`UpdateLieBlend`、`ApplyVisual` 旋转、`ApplyMotion` 抬升） | L143-149、L296-304、L714、L740、L752-753 |
| `RequestedWord` 的 C 阶段短路 | L67 |

---

## 5. 缺口清单（只描述，不实现）

要把「恐龙主动跟生成物件互动」做成真的，还缺下面几块。每条都标注当前归到哪一档。

1. **碰撞结果回传（未开始）**
   状态机没有任何碰撞/触发回调，`GeneratedObjectPushRelay` 也不知道恐龙是谁。缺：恐龙侧的接触事件、把「撞到了某个 `PlacedItem.instance`、推力多大」回传给状态机的通道。

2. **按 `behavior/placement` 区分互动（未开始）**
   `Interacting` 只读 `band`。缺：`Touch/Rest/Play/Ignore` 与 `pushable` 的分支——例如 Ignore 不该被凑近顶、Rest 不该被压扁、Wall/Rooftop 需要另一套动作。

3. **物件位移/状态变化感知（未开始）**
   状态机不知道物件有没有被推动、被推动多远。缺：读取 `PlacedItem` 记录的初始位置或刚体速度，形成「推开了 → 有反应」的闭环。

4. **可达性与 Z 对齐（未开始）**
   `FindInteractTarget` 只比 X，且 `standX` 被活动范围硬夹。生成物锚点在 z=-1.5，恐龙在 z=0；物件若在范围外（如 X < -5.95），恐龙会贴着边界对空气互动。缺：Z 容忍度判定、范围外物件的取舍、被其他物件挡住时的处理。

5. **「新物件生成」事件驱动（未开始）**
   现在只有 6~14 s 的随机轮盘 + 4~10 s 冷却，不会因为「刚生成了一个东西」而立刻过去看。缺：`ObjectModelService` 落位完成事件 → 恐龙订阅 → 抬高该次互动的权重或直接插队。

6. **互动产出与对话联动（未开始）**
   `Interacting` 不弹气泡（C 阶段 `RequestedWord` 恒 null）、不改 occupancy、不给 LLM 任何反馈。缺：互动时的气泡/音效/结果上报通道。

7. **外部驱动与互动的优先级（留桩）**
   `ExternalBusy`（L849）不包含 `Interacting`，外部 `ExternalWalkTo` 会把正在互动的恐龙拉走；同时 `createTouchTestPusher` 在场景里为 1，测试推手会临时关掉恐龙碰撞箱。缺：把 `Interacting` 纳入外部占用判断的约定（现在只是没写，不是写错）。

8. **BALL 参数化的残留（留桩）**
   场景里 `cStageBallKick*` 三个字段已无对应代码。要么恢复字段，要么让 Unity 重新保存场景清掉它们，否则以后读场景的人会误以为 C 阶段有踢球参数。
