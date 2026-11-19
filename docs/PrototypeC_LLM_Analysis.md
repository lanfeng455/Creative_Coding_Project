# Prototype C LLM 通道现状分析（只读，未改动任何文件）

分析范围：`Assets/Scripts/C/` 下的两条 LLM 通道，以及当前唯一有效场景 `Assets/Scenes/TemplateC_LanMay.unity` 的序列化配置。

---

## 一、结论

**LLM 现在承担两件事，而且只承担这两件事：**

1. **单词 → 物件定义**：把玩家输入的英文词/短语转成 6 个字段（名称、两段英文描述、behavior、placement、size 档）。决定的是「这是个什么东西、属于哪个分带、多大一档」。通道：`LlmObjectParser` + `LlmObjectPrompt` + `ObjectDefinitionValidator`。
2. **单个物件 → 一个粗略的横向位置**：在房间已经放好模型、量出真实包围盒之后，让 LLM 在「锚点 + 横向偏移」里挑一个落点。通道：`LlmLayoutPlanner` + `LlmLayoutPrompt`。

**LLM 不承担的部分（全部是规则在兜）：**

- 最终落点 X：LLM 只给「锚点 + offsetX」，求解器还会再做四级退让（微调 → 换锚点 → 沿 X 网格扫描 → 夹紧兜底）。
- 朝向：LLM 的 `facing` / `yawDegrees` 只写日志；实际朝向由「最终落点 X 相对主区间中心的位置」推导，挂墙固定 180°。
- Y 高度、Z 深度：100% 由分带规则决定，LLM 的输入里明确说了不要输出 Z。
- 实际尺寸：只看 `size` 档位枚举 → 目标「最长边」→ 等比缩放，`sizeDescription` 完全不参与运行时尺寸。
- 容量与淘汰：`RoomOccupancy.maxObjects`（场景值 10）FIFO，与 LLM 无关。
- 避让/重叠：求解器的 1D 区间相交校验，不是 LLM 的职责。

**最关键的判断：LLM 并没有"理解这栋建筑"。** 它拿到的是 `RoomLayout` 这一个人工填写的一维区间描述（可用 X 段、禁放 X 段、墙面矩形、命名锚点）。房屋模型（Untitled.glb）的几何从未被运行时读取或测量——`LayoutSolver` 只读 `RoomLayout` 字段（[LayoutSolver.cs:295-376](file:///c:/Unity%20Projects/Creative_Coding_Project/Assets/Scripts/C/Layout/LayoutSolver.cs)），工程里没有对房屋做包围盒/碰撞体扫描的代码。风格模板也从未进入 LLM（只进 Meshy prompt）。

所以更准确的表述是：**LLM 在"一段人工维护的房间摘要"上做局部决策，真正生效的只有 `anchor` + `offsetX` 两个字段。**

---

## 二、发给 LLM 的场景信息（逐项）

### 2.1 通道 A：物件解析（`LlmObjectPrompt.cs`）

System Prompt 全文只有一句场景描述，**没有任何实际数值来自场景**：

| 内容 | 位置 | 形式 |
|---|---|---|
| "a small dinosaur's cozy, unfinished apartment" | `LlmObjectPrompt.cs:10` | 自然语言 |
| "The room is about 9.6 m long and 5 m high" | `LlmObjectPrompt.cs:43` | **硬编码字面量**，不是从 `RoomLayout.roomSize` 读的 |
| behavior / placement / size 枚举与组合矩阵 | `LlmObjectPrompt.cs:26,36-40` | 枚举约束 |
| size 档位的实际米数（0.45 / 0.90 / 1.60 / 2.40） | `LlmObjectPrompt.cs:44-47` | 数值（写死在提示词里） |
| 两段描述合计 ≤ 202 字符 | `LlmObjectPrompt.cs:32` | 硬约束 |

User Prompt 只有一行：`"Parse this player input and return the json object:\n" + 输入词`（`LlmObjectPrompt.cs:55-56`）。**没有任何房间、相机、已有物件信息。**

### 2.2 通道 B：布局（`LlmLayoutPrompt.cs`）

System Prompt（`LlmLayoutPrompt.cs:15-69`）里与"场景"有关的只有视角描述：

| 内容 | 位置 | 有实际数值？ |
|---|---|---|
| 正交相机正面看、房间横向铺开、物件只沿 X 排 | `:16-18` | 否，纯自然语言 |
| X 是唯一摆放轴，Y 由分带决定，Z 固定 | `:22-26` | 半句数值（分带 depth 名） |
| 至少 0.15 m 间距 | `:45` | 数值，但**与求解器实际参数不一致**（实际 `overlapMargin = 0.05`） |
| 朝向规则（右半区朝左、左半区朝右、挂墙朝相机） | `:50-56` | 纯规则文本 |
| 输出 schema（8 个字符串字段） | `:67` | — |

User Prompt（`LlmLayoutPrompt.cs:71-102`）由三块拼成：

**① ITEM 块（`:76-82`）——只有 5 行**

```
objectName / behavior / sizeClass / band / footprint: <宽> m wide (X), <高> m tall (Y)
```

注意：**没有 objectDescription、没有 sizeDescription**。LLM 不知道这个东西长什么样，只知道名字和宽高。

**② ROOM 块（`AppendRoomJson`，`:120-234`）——全部是 `RoomLayout` 的序列化值**

| 字段 | 代码位置 | 当前场景实际值（`TemplateC_LanMay.unity`） | 性质 |
|---|---|---|---|
| `roomSize.width/height/depth` | `:128-131` | 9.6 / 5 / 4 | 数值；**与 usableX 矛盾**（usable 是 −5.5~5.5 = 11 m） |
| `floorY` | `:131` | 0.28 | 数值 |
| `floorDepth` / `ceilingDepth` | `:132-133` | 0 / 0 | 数值 |
| `usableCenterX` | `:136-140` | 主区间中心 0 | 数值 |
| `bands.ground/wall/ceiling` 的 yMin/yMax | `:144-154` | ground 0.28~0.6、wall 0.6~2.2、ceiling 2.8~3.4 | 数值，来自 `ObjectModelService` 字段（`ObjectModelService.cs:60,88,91,94,97,100`） |
| `usableX` 段（id / xMin / xMax / depth） | `:157-159` | `main`：−5.5~5.5 | 数值 |
| `keepOutX` 段 | `:160-161` | `door_path`：−0.6~0.6（**场景里仍在，见 5.1**） | 数值 |
| `walls`（id / planeZ / alongMin / alongMax / yMin / yMax） | `:163-187` | `back`：planeZ 2、along ±5.5、y 0.6~2.2 | 数值 |
| `wallKeepOuts`（wall / xMin / xMax / yMin / yMax） | `:189-212` | 两条：x −4.87~−2.12（y 0.31~3.05）、x 2.63~4.42（y 1.05~3.05），**id 与 wallId 都是空字符串** | 数值，但无名字 |
| `anchors`（id / band / x / maxOffsetX） | `:214-231` | `floor.center`(0,±1.5)、`floor.back_left`(−4,±1.2)、`floor.back_right`(4,±1.2)、`wall.back`(0,±5) | 数值（后两个 x 靠 `MigrateLegacyConfig` 在运行时从 `localPosition.x` 迁移得到，`RoomLayout.cs:115-120`） |

**③ OCCUPANTS 块（`:88-98`）**

- 同分带、最多 `maxOccupants` 条（场景 `maxLayoutOccupants = 10`），每行只有：名称 / `x=...` / `width ... m`。
- 取的是「列表末尾 N 条」（`CollectOccupants`，`:105-118`），不做重要性排序。
- **没有任何高度、behavior、band 交叉信息、容量占用比。**

**④ 风格模板：两条通道都没有。** `PromptStyle.template`（`PromptStyle.cs:19-32`，含 watercolor/whimsical 风格约束）只在 `ObjectSpec.ComposePrompt()` 里拼进 Meshy prompt（`ObjectSpec.cs:143-160`），从未进入 LLM 的 system/user prompt。

---

## 三、输出 Schema 与字段的真实作用

### 3.1 物件解析 Schema（6 字段，全部字符串，字段集合必须完全一致）

定义：`LlmObjectPrompt.cs:25-26`；校验：`ObjectDefinitionValidator.Validate`（`ObjectDefinition.cs:76-198`）。

| 字段 | 枚举/约束 | 运行时作用 |
|---|---|---|
| `objectName` | 小写单数规范名 | **强驱动**：`NormalizedId` → 缓存目录/键、别名解析、`LayoutSolver.yawOverrides` 匹配、占用表 id（`ObjectSpec.cs:137`，`ObjectModelService.cs:284`） |
| `objectDescription` | 60~120 字符 | 只进 Meshy prompt（`ObjectSpec.ComposePrompt`），**不影响运行时任何几何/布局** |
| `sizeDescription` | 30~70 字符，两段合计 ≤202 | 同上，**运行时尺寸不看它** |
| `behavior` | `Touch / Rest / Play / Ignore` | 驱动碰撞体/刚体：`Touch/Play` 且 Ground 才动态，其余静态（`ObjectModelService.cs:1024-1032`，`GeneratedObjectPhysics.cs`） |
| `placement` | `Ground / Wall / Rooftop` | **强驱动**：分带 zone → Y 对齐方式、Z 深度、求解器 band（`ObjectModelService.cs:619-624`，`LayoutSolver.cs:88-90`） |
| `size` | `Small / Medium / Large / SuperLarge` | 驱动缩放：映射到 `sizeSmall/Medium/Large/SuperLarge` 目标盒，取**最长边**等比缩放（`ObjectModelService.cs:926-945, 959-999`） |

拒绝形态：`{"unrecognized":true,"reason":"..."}`（`ObjectDefinition.cs:96-120`）。
组合矩阵：`Rest → 仅 Ground`、`Play → Ground/Wall`、其余不限（`ObjectDefinition.cs:201-212`）。

### 3.2 布局 Schema（8 字段，全部字符串）

定义：`LlmLayoutPrompt.cs:67`；解析：`LlmLayoutPlanner.Parse`（`LlmLayoutPlanner.cs:110-178`）。

| 字段 | 解析方式 | 是否真正生效 |
|---|---|---|
| `anchor` | **唯一必填**；必须存在于 `layout.anchors` 且 band 匹配，否则重试/放弃（`:121-133`） | ✅ 生效（决定基准 X） |
| `offsetX` | 读取后夹到 `±maxOffsetX`（`:135,148-149`） | ✅ 生效（与锚点相加得基准点） |
| `alongWall` | 仅 Wall 时读，换算成 `offsetX = alongWall - anchor.x`（`:138-145`） | ✅ 生效（但只是 offsetX 的另一种写法） |
| `facing` | 规范化为 left/right/toward_camera/away_from_camera（`:153-164`） | ❌ 只写日志；求解器按落点重新推导（`LayoutSolver.cs:490-515, 583-601`） |
| `yawDegrees` | 读入 `intent.yawDegrees`（`:171`） | ❌ 地面/天花板一律被覆盖；本场景 `overrideWallYaw = true`，挂墙也固定 180°（`LayoutSolver.cs:497-503`） |
| `band` | **完全不读**；`LayoutIntent.band` 由 `spec.placementKind` 反推（`:62, 166-177`） | ❌ 无效（LLM 不能改分带） |
| `confidence` | 读入并夹到 0~1（`:174`） | ❌ 只打日志 |
| `hint` | 读入字符串（`:175`） | ❌ 只打日志 |

即：**布局通道真正被采纳的只有「选哪个锚点」和「在锚点附近偏多少」两个自由度。**

---

## 四、从 LLM 输出到最终摆放的完整链路

```
输入词
 └─(1) LlmObjectParser.ParseAsync            LlmObjectParser.cs:39-117
       └─ ObjectDefinitionValidator.Validate  ObjectDefinition.cs:76-198
       └─ ObjectSpecFactory.Create           ObjectSpecFactory.cs:11-37
            （yawDegrees / size / placement 用测试面板默认值，不由 LLM 决定）
 └─(2) GenerateOnlyAsync                      ObjectModelService.cs:280-302
       └─ 缓存 / Meshy 生成 → LoadModelAsync  :557-609
           └─ PrepareModel → ApplySize         :959-999  ← 按 sizeClass 等比缩放到目标最长边
 └─(3) TryMeasureLocalSize                    :874-881   ← 拿到缩放后的真实 X 宽 / Y 高
 └─(4) LlmLayoutPlanner.PlanAsync              LlmLayoutPlanner.cs:51-107
       （无 Key / 网络失败 / JSON 不合规 / 锚点不存在 → 返回 null，退回纯规则）
 └─(5) LayoutSolver.Solve                      LayoutSolver.cs:84-207
       ① 意图点 Evaluate → 成功
       ② ±nudgeStep ×nudgeRadius（场景 0.05×6 = ±0.30 m）
       ③ 同分带锚点按 X 距离，最多 5 个
       ④ 沿 X 网格扫描（gridStep 0.6 m，最多 400 点）
       ⑤ 夹进可用区间后仍要过校验，过不了 → 明确拒绝落位
 └─(6) PlaceInstance                           ObjectModelService.cs:708-816
       设 rotation / position(X, 0, 分带 depth)
       按 zone 对齐 Y：Ground 底面贴地、Wall 墙带垂直居中、Rooftop 顶面贴 ceilingBandMaxY
       ApplyBehavior → RegisterOccupancy
```

**决策归属表**

| 决策 | 谁决定 | 依据 |
|---|---|---|
| 分带 Ground/Wall/Rooftop | 物件解析 LLM（`placement`） | `ObjectModelService.cs:619-624` |
| 基准 X / 候选区间 | 布局 LLM（`anchor` + `offsetX`） | `LlmLayoutPlanner.cs:121-149`，`LayoutSolver.cs:251-272` |
| 最终 X | 求解器（四级退让） | `LayoutSolver.cs:120-207` |
| 朝向 | 求解器（按落点 X 相对主区间中心，死区 0.8 m） | `LayoutSolver.cs:494-515`，`Facings.FromX`（`LayoutTypes.cs:111-117`） |
| Y 高度 | 规则（分带边界） | `ObjectModelService.cs:776-805` |
| Z 深度 | 规则（`groundDepthZ` 0 / `wallPlaneZ` 2 / `ceilingDepthZ` 0） | `LayoutSolver.cs:116`，`RoomLayout.cs:144-147` |
| 实际长宽高 | 规则（档位 → 最长边 → 等比） | `ObjectModelService.cs:959-999` |
| 碰撞/刚体 | 规则（`behavior`） | `ObjectModelService.cs:1017-1032` |

尺寸映射的真实口径：`sizeMedium = (0.65, 0.90, 0.60)`，但 `ApplySize` 只取 `max(x,y,z)` 作为目标最长边（`:974-976`），所以 Medium 实际含义是「最长边 0.90 m」，其余两轴由模型原始比例决定，**不是** 0.65×0.90×0.60。挂墙还会额外把厚度压到 `wallMaxThickness = 0.18`（`:1005-1015`）。

---

## 五、LLM 是否知道"房间里已经放了什么"

**知道一部分，但只是同分带的一维摘要。**

- 只发**同分带**的占用（`CollectOccupants` 过滤 `item.band != band`，`LlmLayoutPrompt.cs:105-118`）；地面物件看不到墙上挂了什么，反之亦然。
- 每条形如 `- name at x=..., width ... m`，**没有高度、没有深度、没有 behavior、没有锚点归属**。
- 只取列表末尾最多 `maxOccupants`（场景 10）条；`RoomOccupancy.maxObjects` 也是 10，所以当前基本不会截断，但一旦调大容量就会静默丢弃早期条目。
- `layout.json` 回放出来的记录（`instance == null`）同样计入占用并发给 LLM（`RoomOccupancy.cs:281-316`）。
- **施工中占位牌不计入占用**：`_pendingPlaceholders` 只做显示替换，`Register` 发生在落位成功之后（`ObjectModelService.cs:240-245, 809-814`），所以 LLM 看不到"正在生成但尚未落位"的物件。
- **待处理队列完全不可见**：`PrototypeCTestUI._queue`（`PrototypeCTestUI.cs:60-62, 123-190`）是 UI 层状态，不进入 LLM 输入。

**超量/FIFO 与避让靠什么保证（都不是 LLM）：**

| 机制 | 实现 | 参数（场景值） |
|---|---|---|
| 全屋容量上限 + 先放先淘汰 | `RoomOccupancy.EnforceCapacity`（`:246-261`），淘汰只销毁实例、保留模型缓存 | `maxObjects = 10` |
| 占用登记（含可推物件实时位置） | `Register` / `RefreshDynamic`（`:120-165`） | — |
| 不重叠 | `RoomOccupancy.HasOverlap` 一维区间相交（`:188-207`） + `LayoutSolver.TryAccept`（`:295-376`） | `overlapMargin = 0.05` |
| 不压禁放区 / 不出可用区间 / 不压窗户 | `BlockedByKeepOut` / `ContainsX` / `WallBlockedByKeepOut`（`RoomLayout.cs:264-321`） | — |
| 实在放不下 | `LayoutSolution.Failed` → `RejectPlacement`（销毁实例、保留缓存、状态栏提示） | `LayoutSolver.cs:204-206`，`ObjectModelService.cs:819-828` |

结论：**"不重叠、不超量、不压窗"是求解器与占用表保证的；LLM 排序质量不影响正确性，只影响观感。**

---

## 六、缺口清单与阶段划分

### 6.0 现有配置/提示词层面的不一致（不算新功能，属于"先对齐"）

| # | 问题 | 证据 |
|---|---|---|
| 1 | 场景里仍残留 `door_path` 禁放段（x −0.6~0.6），与"当前场景无门"的设计决定冲突；LLM 会被要求避开房间正中 | `TemplateC_LanMay.unity:3297-3301` |
| 2 | `usableX` 是 ±5.5（11 m），`roomSize.width` 是 9.6 m，LLM 同时看到两个矛盾宽度 | `TemplateC_LanMay.unity:3292-3296, 3350` |
| 3 | 提示词说"至少 0.15 m 间距"，求解器实际 `overlapMargin = 0.05` | `LlmLayoutPrompt.cs:45` vs `RoomOccupancy.cs:70` |
| 4 | 物件解析提示词把房间写成硬编码 "9.6 m long and 5 m high"，改房间不会同步 | `LlmObjectPrompt.cs:43` |
| 5 | 窗户用空 id / 空 wallId 表示，LLM 只知道"某个墙上有两条矩形"，不知道是窗 | `TemplateC_LanMay.unity:3312-3324` |
| 6 | Rooftop 物件顶面固定贴 `ceilingBandMaxY = 3.4`，而房间高 5 m，视觉上悬在半空 | `ObjectModelService.cs:789-794` |
| 7 | 布局通道 `maxTokens = 400`，`maxAttempts = 2`，重试**不回传错误给模型**（没有 repair prompt） | `LlmLayoutPlanner.cs:21, 42, 100-101` |
| 8 | `LayoutIntent.adjacentTo` 标注"v1 只记录不使用"，`confidence` 不参与任何计算 | `LayoutTypes.cs:154-158` |
| 9 | `sizeDescription` 与 `size` 档位之间无交叉校验，LLM 可以给 Medium 却写 "2 m wide" | `ObjectDefinition.cs:151-177` |

### 6.1 P0：纯配置/数据，零代码

1. 在 Inspector 里删除 `RoomLayout.regions` 的 `door_path` 条目（代码的 `Reset()` 已不再生成，但旧序列化值不会自动消失）。
2. 把 `regions.main` 与 `roomSize.width` 统一到同一个口径（建议 roomSize 改 11 或 usable 改 ±4.8）。
3. 给 `wallKeepOuts` 两条补上 `id`（如 `window_left` / `window_right`）和 `wallId: back`。
4. 把 `RoomOccupancy.overlapMargin` 调到 0.15，或把提示词里的 0.15 改成 0.05（二者选一，别两边不一致）。
5. 命名 `anchors`：`floor.back_left/back_right` 目前靠运行时迁移才变成 −4/+4，建议直接在 Inspector 填 `x` 固化。

### 6.2 P1：只加输入载荷，不改 Schema（收益最大）

改动集中在 `LlmLayoutPrompt.BuildUserPrompt` / `AppendRoomJson` / `CollectOccupants` 与 `LlmObjectPrompt`：

1. **ITEM 块补 `objectDescription` + `sizeDescription`**：让 LLM 知道这是"床"还是"高书架"，现在的行为/档位不足以选锚点。
2. **OCCUPANTS 改成"全屋 + 分带标注"**：每行加 `band`、`height`、`behavior`；至少让地面物件知道墙上有画、头顶有吊灯，避免"看起来"叠在一起。
3. **补一段 `roomFacts`**：进门/无门、窗户清单（名字 + X 区间 + 离地高度）、最大可用高度、当前占用数/容量（`occupancy.Count` / `Capacity`）。容量是"超量淘汰"的唯一先验，现在 LLM 完全不知道。
4. **去掉硬编码房间尺寸**：`LlmObjectPrompt.cs:43` 改为由调用方注入 `RoomLayout.roomSize`（物件解析输出的缓存键只依赖 `objectName`，改提示词不会让 Meshy 缓存失效）。
5. **把真实 `overlapMargin` 注入提示词**，确保模型的世界观与求解器一致。
6. 布局通道 `maxTokens` 从 400 提到 600~800，为后续字段留余量。

### 6.3 P2：扩 Schema + 补校验（让"意图"真的能表达）

1. **高度意图**：`yOffset`（相对分带基准，米，带上下限）。现在 Wall/Rooftop 的 Y 完全写死：挂墙一律墙带垂直居中（`ObjectModelService.cs:781-787`），屋顶一律顶到 3.4（`:789-794`）。要"挂画挂高一点""吊灯吊低一点"，必须有这个字段，并在 `PlaceInstance` 里夹到带内。
2. **关系意图**：把已存在的 `adjacentTo` 真正打通——LLM 输出「贴着哪个已落位物件」，求解器按该物件的最终 X 求候选区间；这是"成组摆放"（床头柜贴床、灯贴沙发）的最低成本方案，不需要批量规划。
3. **候选/置信度生效**：允许输出 1~3 个候选（`candidates` 数组），求解器按顺序尝试；`confidence` 用于排序而不是只打日志。
4. **校验补齐**：
   - 分带能力校验：`Rooftop` 但 `ceilingBandMaxY - groundBandMinY` 放不下该物件高度时，提交前就拒绝并说明。
   - 宽度预校验：目标 `footprint.x` 大于所有 `usableX` 段长度时，直接拒绝，不要先跑一遍 Meshy 再落位失败（现在会跑完生成才在 `PlaceInstance` 里失败）。
   - `facing` 与 `offsetX` 的一致性问题：现在提示词要求二者一致但求解器直接覆盖，建议改为"不一致时降权/拒绝"，否则该字段纯噪音。
   - 重试时把上一轮的错误信息拼进 user prompt（repair），把 `source = "llm-repair"` 用起来。
5. **`sizeDescription` ↔ `size` 一致性校验**：解析后从 `sizeDescription` 里抽数字，与档位最长边偏差超过 ±40% 判为不合规并重试，避免"档位 Medium、描述 2 m"这种自相矛盾。

### 6.4 P3：批量规划 + 从模型取几何（真正"理解这栋建筑"）

1. **批量（整屋）布局**：现在一次只规划一个物件（`PlanAsync` 单物件），所以 LLM 永远做不出"房间整体的构图"。改成「一次提交当前房间摘要 + 本次要放的物件清单，返回一份整体排布」，求解器负责整批冲突消解与失败回滚。改动点：`LlmLayoutPlanner` 新增批量入口、`LayoutSolver` 增加"按批求解、失败时逐个降级"、`RoomOccupancy` 支持批量登记。
2. **放弃部分手填几何**：写一个编辑器工具，从 Untitled.glb 的实际包围盒/墙面反射里烘焙出 `RoomLayout` 的 `walls` / `wallKeepOuts`（窗洞），LLM 与求解器继续吃 `RoomLayout`，但数值来自模型而不是手填。目前 `RoomLayout` 的 `Reset()` 只是默认矩形（`RoomLayout.cs:342-363`），场景里的值全靠手调。
3. **相机可见域**：提示词只说"正交相机正面看"，没有 FOV / orthographic size / 画面宽高比。若要 LLM 做构图（例如"重要的东西别放在画面边缘"），需要把相机正交尺寸与可见 X/Y 范围作为数值输入。
4. **容量与淘汰的语义反馈**：把「FIFO 会淘汰最早的那个」写进提示词，并提供"当前最老物件"的条目，让 LLM 有机会主动避开"刚放进去就被淘汰"的组合。

### 6.5 按改动量排序的一句话总结

- 不改代码就能做：删 `door_path`、统一宽度口径、给窗户命名、对齐 0.05/0.15、固化锚点 X。
- 一天内可做：ITEM 补描述、OCCUPANTS 补全屋+高度+behavior、补 roomFacts（容量/窗户/无门）、去掉硬编码房间尺寸、提 `maxTokens`。
- 一个迭代：`yOffset`、`adjacentTo` 打通、候选数组、宽度/分带预校验、repair 重试、sizeDescription 一致性校验。
- 大改：整屋批量规划、从 glb 烘焙 `RoomLayout`、相机可见域输入。
