# 这个小屋里，东西是怎么摆进去的

> 讲的是**现在的真实行为**。所有参数都在 Inspector 里，按「① 先核对」到「⑦ 调试」从上往下排好了，不用背字段名。
>
> 前提：这是个**横向卷轴的 3D 场景**——正交相机从正面照过来，房间在屏幕上左右铺开。所以东西只按**左右（X）**排队，前后（Z）是每个分带写死的，不参与排布。

---

## 一、先看这张表：四个组件各管什么

选中 `PrototypeC_Controller`，你会看到几个脚本。它们的分工是这样：

| 组件（Inspector 里的名字） | 脚本文件 | 干什么用 | 你只需要看哪几个字段 |
| --- | --- | --- | --- |
| `ObjectModelService` | `Assets/Scripts/C/ObjectModelService.cs` | 总调度：找模型 / 调 AI 生成 / 定位 / 缩放 / 物理 / 落位 | 「① 先核对」里的 6 项（默认都对，扫一眼即可） |
| `PrototypeCTestUI` | `Assets/Scripts/C/PrototypeCTestUI.cs` | 右下角那个测试面板：输入框、生成按钮、状态栏 | `objectName`、`useAiParsing`、`useLlmLayout`、`replayLayoutOnStart` |
| `RoomLayout` | `Assets/Scripts/C/Layout/RoomLayout.cs` | 房间的尺子：横向从哪到哪能放、哪一段要留空、墙在哪、参考点在哪 | 「① 先核对」2 项 + 「② 横向能放哪」里的区间 |
| `RoomOccupancy` | `Assets/Scripts/C/Layout/RoomOccupancy.cs` | **记住房间里已经放了什么、房间满了淘汰谁**，并把这份账本存进 `layout.json` | 只需要 `maxObjects`（最多放几个） |
| `LayoutSolver` | `Assets/Scripts/C/Layout/LayoutSolver.cs` | 位置冲突时算出该往左/往右挪到哪（微调 → 换参考点 → 沿横向扫空位），并按最终落点推导朝向 | 不用动，全默认；想按 id 固定朝向才动「③ 朝向」 |

### 为什么我没加过 `RoomOccupancy`，它却出现了？

因为它是**运行时自动补挂**的：只要场景里有 `RoomLayout`，进 Play 时 `ObjectModelService` 会把 `RoomOccupancy` 和 `LayoutSolver` 一起加到同一个物体上，日志里会有两条"已在运行时补挂"的黄色警告。

它不是凭空冒出来的第三方脚本，就是上表第 4 行那个"记账本"。想改"最多放几个"，**建议自己手动把 `RoomOccupancy` 加到 Controller 上**（Add Component 搜 `RoomOccupancy`），然后改 `maxObjects`，这样 Play 时就不会再补挂，改的值也留得住。

---

## 二、Inspector 字段的顺序

每个组件的字段都按"配置动线"分组编号，从上往下就是操作顺序：

| 组名 | 要不要动 |
| --- | --- |
| ① 先核对这几项 | 第一次配置时看一眼，之后基本不动 |
| ② 摆放规则（一般不用改） | 不动。房间几何变了才动 |
| ③ 尺寸与缩放 / ③ 朝向（`LayoutSolver`） | 不动 |
| ④ 颜色纠偏 | 觉得家具还偏黄/偏枯时才动 |
| ⑤ 物理与手感 | 想让东西更好推、更推不动时才动 |
| ⑥ 生成、缓存与网络 | 不动 |
| ⑦ 快速模式 | 嫌生成慢时才开，默认关闭 |
| ⑧ 旧槽位排布 | 只在关掉求解器时才生效，平时可以无视 |
| ⑨ 调试与 Scene 视图 | 只想画 gizmo 时才动 |

Unity 是按字段名保存的，所以顺序调过了，你之前配好的值不会丢。（`RoomLayout` 里旧版的锚点坐标会在第一次读取时自动迁到新的"横向位置 X"，不用重填。）

---

## 三、配置顺序（四步）

**第 1 步：加组件**

- 场景里必须有 `ObjectModelService`、`RoomLayout`（这两个不给就摆不了）；
- `RoomOccupancy`、`LayoutSolver` 可以不加，Play 时自动补；想改"最多放几个"就手动加 `RoomOccupancy`；
- 测试面板用 `PrototypeCTestUI`（这个场景里已经接好了）。

**第 2 步：核对 RoomLayout 的两项**

在 `RoomLayout` 的「① 先核对这几项」里：

1. `anchor`（坐标基准）——应该是房间锚点 `PrototypeC_GeneratedAnchor`，留空会自动取；
2. `floorY`（地板高度）——标的配置是 **0.28**，要和 `ObjectModelService` 里「① 先核对」的 `groundBandMinY` 是同一个数。

然后看「② 横向能放哪、哪段要留空」里的区间列表（每个只有左右两端，单位米）：

- 绿色的（`Usable`）是能放东西的横向区间，默认就一条 `main`（-5.5 ~ 5.5）；
- 红色的（`KeepOut`）是禁放区：**这段必须空着**，物件宽度只要和它沾上就一律不放。**默认为空**；以后需要预留（演出/任务占位/避免穿模）时再加一条；
- 区间是**沿 X 的一条横线**：`xMin` 是左端、`xMax` 是右端。物件按自己的实际宽度整段待在绿色区间里。

绿色区间的左右端就是**房间边界**（区间之外不能放，防止飘出房间或穿墙）；红色是**区间内部**主动挖的空洞。两者都写在同一个列表里，靠 `kind` 区分。

墙上如果有窗户，还要在「③ 墙面矩形禁放」里登记（字段 `wallKeepOuts`）：一条 = 一面墙 + 横向 `xMin~xMax` + 离地 `yMin~yMax`。挂墙物件的**横向范围与高度范围同时**压到矩形上才会被拒绝，所以挂画可以挂在窗户上方或旁边。留空 `wallId` 表示对所有墙生效。**这块默认是空的**，要给窗户/通风口留空就手动加一条。

Scene 视图里选中 `RoomLayout`，画出来的都是**正对相机的竖直面片**（不是躺在地上的薄片）：

- 绿色竖条：能放东西的横向区间，条顶写着 `main  x -5.50 ~ 5.50 m`；
- 红色竖条：禁放区间（默认没有），有的话条顶写着名字和起止米数；
- 蓝色竖板：墙面，写着 `back  x -5.50 ~ 5.50 m｜y 0.60 ~ 2.20 m`，还有一个朝房间里的箭头；
- 洋红色矩形：墙面禁放区（窗户等），写着 `window（墙面禁放）  x …｜y …`；
- 黄色竖线 + 短横线：参考点（`floor.` 地面，`wall.` 墙面），标注 `floor.center  x=0.00`，短横线就是这个点允许左右偏移的范围；
- 配反了（起点大于终点）或高度区间重叠，会变红并在上方列出"配置冲突"。

> **如果还看到一条 `door_path（禁放）  x -0.60 ~ 0.60 m`**：那是旧版本留下的默认配置，不是这个房间的真实结构。系统**不会自动删除**你场景里已经存下的条目，需要你手动清一下：展开 `regions` → 找到 `door_path` 那一条 → 点条目右上角的 `-`（或右键 → `Remove Array Element`）。清掉之后红色竖条消失，整条 `main` 都能放东西。

**第 3 步：改开关**

都在 `PrototypeCTestUI` 上（也就面板本身）：

| 字段 | 干什么 | 建议 |
| --- | --- | --- |
| `useAiParsing` | 输入一个词，让 AI 翻译成"什么东西、放哪层、多大" | 开着 |
| `useLlmLayout` | 每个物件多问一次 AI"这屋里放哪儿合适" | 开着；嫌慢或没网就关 |
| `replayLayoutOnStart` | 进 Play 时按上次的 `layout.json` 把东西放回来 | 开着 |

**第 4 步：其余全部保持默认**

剩下的都是调参项，默认值可用。真需要才动：`maxObjects`（最多放几个）、`wallMaxThickness`（挂墙的厚度上限）、`touchMass` 之类的手感参数。

---

## 四、东西放在哪一层

每个物件只落在三层中的一层，由 AI 按物件类型判断：

| 层 | 长什么样 | 高度怎么定 | 前后位置（Z） |
| --- | --- | --- | --- |
| 地面层 | 椅子、床、盆栽这类落地的东西 | 脚底贴地面（`groundBandMinY`，场景里 0.28 m） | 全层共用一个 `groundDepthZ`（默认 0） |
| 墙面层 | 画、壁饰这类挂墙的东西 | 垂直居中在墙中间（默认离地 0.6~2.2 m），**背面贴墙**，正面朝房间 | 由那面墙的 `planeZ` 决定（默认 2.0） |
| 天花板层 | 吊灯、挂饰这类靠天花板的东西 | 顶面贴天花板下沿（默认 3.4 m） | 全层共用一个 `ceilingDepthZ`（默认 0） |

**横向才是排布轴**：同一层的东西只在 X 上互相避让，前后不会"排第二排"——正交侧视下前后本来也看不出来。相机在 `(0, 0.715, -10)` 沿 +Z 看，房间锚点在 `(0, 1.6, -1.5)`，屏幕上的左右就是 X。

高度不是移动模型原点，而是先算出"应该占的那块空间"，再把模型整体挪过去。所以模型原点不在正中间、或者被旋转过，也还是能准确贴地、贴墙、贴顶。

**挂墙的为什么被"拍扁"**：AI 生成的画往往有半米厚，直接挂上去像凸出来的箱子，所以挂墙时只压薄朝向房间的那个方向（默认压到 0.18 m，字段 `wallMaxThickness`），宽高不动。这是故意的。

墙面层默认只支持后墙一面。

**朝向怎么定（伪 2D 的"背靠侧墙"）**：朝向不是 AI 先说了算，而是**落点定下来之后**按"离哪面侧墙更近"推导，没有"朝相机"的中间区：

- 更靠近**右墙** → 背靠右墙、正面朝左（yaw −90；床在右半区时床头贴右墙）；
- 更靠近**左墙** → 背靠左墙、正面朝右（yaw +90）；
- 正好等距（例如 x 正好在可用区间正中）→ 优先用 AI 给的 `left` / `right`；AI 没给、给的是 `toward_camera` 或与位置矛盾时，固定贴右墙（yaw −90）；
- 挂墙物件**不受影响**，仍固定面朝相机（`ObjectModelService.overrideWallYaw`）。

AI 可以直接给 `facing`（`left` / `right` / `toward_camera` / `away_from_camera`），除"正好等距"外一律以落点为准，日志会写"朝向以落点为准：…LLM 建议…→ 实际…"。

个别物件想固定朝向，在 `LayoutSolver` 的「③ 朝向」里往 `yawOverrides` 加一条：`id` + `yawDegrees`（0 = 背对相机、180 = 朝相机、-90 = 朝左、90 = 朝右）。命中时优先于落点推导，日志会写"命中覆盖表"。

> 朝向会影响横向宽度（转 90° 后宽和深互换），所以求解器是**每个候选点先定朝向、再按旋转后的真实宽度**校验的，不会出现"看着放得下、转完压到旁边"。

---

## 五、东西多大，是谁定的

AI 只说"小 / 中 / 大 / 超大"，具体尺寸由四个档位决定：

| 档位 | 目标大小（宽 × 高 × 深） | 大概是什么 |
| --- | --- | --- |
| 小 | 0.35 × 0.45 × 0.35 m | 杯子、碗、书、小摆件 |
| 中 | 0.65 × 0.90 × 0.60 m | 椅子、凳子、边几、盆栽 |
| 大 | 1.10 × 1.60 × 0.80 m | 沙发、餐桌、床、书架 |
| 超大 | 1.60 × 2.40 × 1.20 m | 双人床、大衣柜 |

规则：

1. 整体**等比缩放**，不会拉变形；
2. 缩完如果比房间还高，会再整体缩一次，保证不顶穿天花板；
3. 挂墙的最后再压薄厚度；
4. **改大小、改朝向、换层都不会重新生成模型**，缓存照常命中。

---

## 六、放不进去会怎样

系统先拿到一个"建议位置"：问过 AI 就用 AI 给的参考点 + 左右偏移；没问或 AI 没答好，就用房间中间 / 墙面中间。

然后**只沿左右方向**按顺序试，每一级都检查"整段宽度在不在可用区间里、有没有碰到禁放区、有没有压到别的东西"：

1. **就用建议位置**——通过就放下；
2. **往左或往右挪一点**——每 5 cm 试一格，最多挪 30 cm，由近到远；
3. **换个参考点**——同一层里换最近的参考点再试，最多 5 个；
4. **横向扫一遍**——在可用区间（挂墙则在墙面可用段）里按 60 cm 一格扫过去，取最近的空位，挂墙时会绕开窗户矩形；
5. **实在没有**——**这次不放**。

第 5 步是"拒绝落位"：状态栏写"未落位：空间不足……"，实例被销毁，但**模型缓存保留**。所以不会再出现"全堆在一起"或"床压椅子"。

> 检查用的是**缩放后、旋转后投影到横向的真实宽度**，还留了 5 cm 间距，不是只看一个中心点。前后方向不参与判定。

---

## 七、房间满了会怎样

- 全屋上限 **10 个**（不分层，三层共用；字段 `maxObjects`）；
- 放第 11 个时，淘汰**最早放进去的那个**：场景实例销毁、占位记录删掉、存档里那条也删掉；
- **模型缓存不删**：下次再要同一个物件直接命中，不重新花钱生成；
- 日志会写清楚淘汰了谁、什么时候放进去的；
- 被淘汰的东西如果存档里还在，重新 Play 时会按当时的占用重新找位置，**不复用旧坐标**。

另外，能被推动的地面物件（Touch / Play）占位会跟着实际位置走；挂墙和天花板上的物件占位不动。

> 别和"生成队列"搞混：队列是"还没处理完的请求"，淘汰是"已经放好的物件"。

---

## 八、会不会问 AI 怎么摆

**会，但只问"放哪儿"，不问"放多大"**，每个物件多花一次 AI 调用，可以关（`useLlmLayout`）。

告诉 AI 的：物件叫什么、干什么、放哪层、**横向宽度**和高度；房间的可用横向区间、禁放横向区间、各带的高度范围与固定 depth、墙在哪、有哪些参考点（横向 X 位置）及允许左右偏移多少；同层已经放了什么（最多 12 条，每条只有名字、横向位置、宽度）。

AI 回答的：

| 它回答 | 系统怎么处理 |
| --- | --- |
| 哪一层 | 必须和物件本身的层一致，想改也不让改 |
| 用哪个参考点 | 房间里没有这个点、或层不对，这条回答作废 |
| 往左右偏多少米 | 不能超过该参考点允许的范围，超了夹回来 |
| 往前往后偏多少米 | **不存在这个字段**；旧模型硬塞 `offsetZ` 也会被直接忽略 |
| 朝向 `facing` | 只作建议；落点定下来后按左右半区重新推导，冲突以落点为准并写日志 |
| 转多少度 | 仅剩兜底作用；有 `facing` 或能按落点推导时以推导结果为准 |
| 挂墙的横向位置 | 按绝对 X 换算成相对参考点的偏移，不会重复加 |
| 自信程度 / 理由 | 只写日志，不影响摆放 |

提示词里已经写清"左右是侧墙、背靠最近侧墙、正面朝房间中央"，并给了 3 个示例（右半区的床 → `left`、左半区的书架 → `right`、墙上挂画 → `toward_camera`），还附了 `usableCenterX` 让 AI 能自己判断左右半区。

**AI 答错、超时、没网、没配 Key**：最多再问一次，还不行就当没问，走纯规则摆放。**AI 永远不会卡住生成**。

---

## 九、记在哪，重开还认不认

- 存档：`C:\Users\<你的用户名>\AppData\LocalLow\DefaultCompany\MediaPipeUnityPlugin\ObjectCache\layout.json`
- 记的是：每个物件叫什么、在哪层、位置、占地、朝向、能不能推、加入时间、AI 的建议；
- 写入时机：放下一个、移除一个、清空时；
- 重进 Play：先按存档把东西逐个放回来（**只读本机模型，绝不偷偷去生成**），位置按当时占用重新算，不是照抄旧坐标；状态栏显示"回放布局 3/10"；
- 会跳过某条的情况：本机没有模型缓存、名字为空、面板风格/生成参数和存档时不匹配（指纹对不上）、缓存被清空了。

换过房间风格的话，回放会整体跳过——故意的，避免偷偷花生成额度。

---

## 十、怎么测

### 第 0 步：确认组件

选中 `PrototypeC_Controller`：

- 应该有 `ObjectModelService`、`PrototypeCTestUI`、`RoomLayout`；
- `RoomOccupancy`、`LayoutSolver` 没有也正常，进 Play 会自动补，日志里两条"已在运行时补挂"；
- 想改"最多放几个"，就手动加 `RoomOccupancy` 再改 `maxObjects`（默认 10）。

进 Play 第一行应该是：

```text
[PrototypeC] 布局组件：RoomLayout 已找到｜RoomOccupancy 已找到｜LayoutSolver 已找到｜useLayoutSolver=True
```

写"缺失"的话，后面的摆放会被拒绝，状态栏也会说缺什么。

### 第 1 步：核对房间范围

选中 `RoomLayout`，在 Scene 视图里看（都是正对相机的竖直面片）：

- 绿色竖条：能放东西的横向区间，条顶写着起止米数；
- 红色竖条：禁放区间的横向位置（默认没有；有残留的 `door_path` 就按第 2 步的方法清掉），和绿色沾上就一律不放；
- 蓝色竖板 + 箭头：墙面的横向范围和高度范围、朝向；
- 黄色竖线 + 短横线：参考点的横向位置和允许左右偏移的范围，写着 `floor.center  x=0.00`；
- 出现红字"配置冲突"，按提示把写反的起止或重叠的高度改掉。

### 第 2 步：放两个东西，看会不会打架

输入 `chair` → 生成；再输入 `plant` → 生成。看日志：

```text
[PrototypeC] 尺寸：chair｜档位 Medium｜…｜最终 0.505×0.900×0.580 m
[PrototypeC] 布局意图：chair（chair）｜锚点 floor.center + X 0.00｜facing 默认｜yaw 90｜…
[PrototypeC] 布局：chair（chair）｜ground｜锚点 floor.center｜横向 X -0.30（depth 0.00）｜yaw 90｜朝向 朝右（背靠左墙）｜来源 intent
[PrototypeC] 布局：plant（plant）｜ground｜锚点 floor.right｜横向 X 3.80（depth 0.00）｜yaw -90｜朝向 朝左（背靠右墙）｜来源 intent+nudge
```

- 两个没有穿模；
- 来源是 `intent+nudge` / `anchor` / `grid` 都正常，说明系统自己躲开了；
- 出现"布局放弃"或"未落位：空间不足"，说明确实没位置，回头查第 1 步的绿色/红色竖条。

### 第 3 步：加到第 11 个，看淘汰

```text
[PrototypeC] 容量淘汰：<最早的东西>（<id>）｜加入于 …｜当前 10/10（模型缓存保留，可再次命中）
```

- 最早的从场景里消失；
- `ObjectCache\<被淘汰的 id>\model.glb` 还在；
- 再生成同名物件，日志是"缓存命中"，**不会出现 Meshy 任务**。

### 第 4 步：停止再 Play，看回放

```text
[PrototypeC] 布局回放：从 …\ObjectCache\layout.json 恢复 10 条占用记录
状态栏：回放布局 1/10 … → 布局回放完成：10 个
```

- 全程不该出现 Meshy 任务；
- 位置和上次基本一致（允许因占用不同有微调）。

### 出问题怎么判断

| 现象 | 先查什么 |
| --- | --- |
| 状态栏"未落位：缺少 RoomLayout…" | 场景里没有 `RoomLayout`，它是房间几何的唯一来源，必须挂 |
| 状态栏"未落位：空间不足…" | 绿色竖条太窄 / 红色竖条太宽；或者真的放满了 |
| 一直"来源 degraded" | 都靠兜底夹进去的，位置可能贴边；把参考点挪到更合适的地方 |
| 提示"锚点不存在或分区不匹配" | AI 给的参考点在房间描述里没有，或层不对；看黄色竖线 |
| 重开 Play 是空的 | `layout.json` 在不在；`replayLayoutOnStart` 是不是被关了 |
| 回放全部被跳过 | 多半换过风格，缓存指纹变了 |

---

## 十一、恐龙在这个房间里怎么活动（只在 `TemplateC_LanMay`）

只在裁剪场景自动生效（`DinoAgentB1.cStageMoodEnabled` + `PrototypeCStage.IsTrimmedScene`），B1/B2 的行为完全不变。

**念头只有三种**，每次按权重挑一个：

| 念头 | 权重字段 | 默认权重 | 占比 |
| --- | --- | --- | --- |
| 发呆 / 散步 | `cStageWanderWeight` | 5 | ≈45% |
| 趴下休息 | `cStageRestWeight` | 2 | ≈18% |
| 找地上的生成物互动 | `cStageInteractWeight` | 4 | ≈36% |

- 互动目标只认 `RoomOccupancy` 里登记过的**地面**生成物（也就是 `PrototypeC_GeneratedAnchor` 下那批），不会去碰 B1 的资源和物品；
- 没有可互动的目标时，互动权重自动让位给发呆，不会站着干等；
- 互动有持续时长（`interactDuration` 2.5~5 s）和冷却（`interactCooldown` 4~10 s），不会一直黏着同一件东西；
- **不再主动喊 BALL**：C 阶段 `RequestedWord` 直接返回空，也不会为了球去打字机拼词。

**场上有球时**：恐龙走过去碰到就把它踢向**下层方向**（默认朝 `PrototypeC_Gap` 的缺口中心）。参数都在 `DinoAgentB1` 的「C 阶段」分组：

| 字段 | 作用 | 默认 |
| --- | --- | --- |
| `cStageBallKickDirectionX` | 0 = 自动朝缺口，-1 = 向左，1 = 向右 | 0 |
| `cStageBallKickSpeed` | 水平速度随机区间（米/秒） | 4.5 ~ 7 |
| `cStageBallKickUpSpeed` | 向上分量；0 = 贴地踢出，靠重力掉到下层 | 0 |

> "投进上层篮筐 → 球消失 → 奖励"还没做，现在踢下去就结束了。

---

## 十二、颜色纠偏：只动黄/褐，不动红/粉

生成物整体偏黄褐、跟木屋背景糊在一起，或者那盆 `plant` 看着像枯了——这类问题在**运行时材质**上纠，不改 Prompt、不改 `styleVersion`、不进缓存指纹，所以已有缓存照常命中。

做法是**往浅中性收敛**，不是换一种颜色：

- **只处理色相 25°–70°**。25° 以下偏红/橙（红木、南瓜），70° 以上是黄绿/橄榄（吊兰那种健康绿），两端都不碰；
- **硬边界**：shader 与 C# 主色检测都夹在 `0.0694 ~ 0.1944`（25°–70°），配置里的 `hueMin/hueMax` 只能在这个范围内收窄，写到外面会被夹回来；红色（0°–20°）与粉/品红（300°–360°）永远不会被改；
- 命中像素**只做两件事**：把饱和度压向 `maxSaturation` 上限（只降不升），再按"到白色的剩余距离"提亮 `lighten`。**色相完全不动**，所以木色只会变成浅木/米白/灰白，不会变成绿色或其它颜色；明暗关系也保留，木纹层次还在；
- 纯参数确定、无随机，**同一个 id 每次加载颜色一致**；
- 参数在 `ObjectModelService` 的「④ 颜色纠偏」：`enabled`、`hueMin` / `hueMax`、`desaturation`（降饱和强度）、`maxSaturation`（目标饱和度上限）、`lighten`（提亮强度）、`excludeIds`（按 id 排除，比如填 `plant`）。

默认值（`desaturation 0.8 / maxSaturation 0.22 / lighten 0.3`）是保守档：看起来像浅色木头或白漆家具，不会出现明显彩色倾向。觉得还是太黄就把 `maxSaturation` 降到 0.12；想保留更多木色就提到 0.35。

**为什么红色 / 粉色 / 绿色不受影响**：这三类颜色的色相都不在 25°–70° 内（红 ≈0–20°、粉/品红 ≈300–360°、绿 ≈90–160°），shader 里 `inWarm` 直接为 0、C# 那边 `LooksWarm` 也返回 false，两条路径都写 0 强度。

**验证**：

1. 生成一个明显红色的物件（比如 `red_rug` / `tomato`），选中后在 Inspector 看材质上的 `_NeutralStrength` 与 `_NeutralLighten`，应该都是 0；
2. 生成一个黄褐色的木家具，它应该变浅、变灰白，但**没有绿色倾向**；把 `desaturation` 拉到 1 只会更白，仍然不会偏色；
3. 绿色物件（吊兰）颜色应与之前一致，不会被漂白。

> 低饱和 / 很暗、也没有可读贴图的模型判断不了主色，会交给 shader 逐像素判断，不会误伤整件。
> 如果确实需要"白框 + 彩色坐垫"这种分区配色，只能改 Prompt（等于改 `styleVersion`）：全量缓存失效、所有物件要重新生成，本轮没有走这条路。

---

## 十三、缓存别名（`plant` → 吊兰）与缓存库窗口

如果某个 id 生成出来的模型不满意，可以让它**直接复用另一条缓存的模型**，不用重新生成。

- 别名文件：`...\ObjectCache\aliases.json`，每条只有 `from`（要用的 id）、`to`（目标模型 id）、`note`；只记录映射，**不复制、不改目标目录**；
- 加载顺序：生成前先查别名，命中就按目标的 `model.glb` 加载；但名字、色调、占位仍按原 id 走，所以"同 id 同模型"不变；
- 目标缓存不存在 / 读失败：打一条警告，回退原 id 走正常生成，不会卡住；
- 别名不参与 `styleFingerprint`，换风格后回放仍按原规则跳过。

**怎么编辑**：菜单 `Tools / Prototype C / 缓存库（预览 / 放入场景 / 别名）`，下半部分直接改 `from` / `to` / `note`，保存即写文件；也可以手动编辑 `aliases.json`。

同一个窗口的上半部分是**缓存库**：列出本机 `ObjectCache` 与工程内 `PrototypeC_Cache` 的条目（id、尺寸档位、大小、来源、有无贴图），可以把任意一条**放进当前场景**：

- 只在编辑器里工作，放进去的是普通 GameObject；
- 用与运行时一致的 toon 材质和尺寸档位（同一个 `DressEditorInstance`）；
- **不写 `layout.json`、不占容量**；窗口里勾选"登记占用"才会被占位系统认到；
- 想让布局系统自己也认它：勾选登记占用，或者之后用测试面板再生成一次同名物件（会命中缓存）。

---

## 十四、生成速度：快速模式

Meshy 官方文档里，text-to-3D 是两段：`preview` 出素模，`refine` 上贴图。用官方示例的典型耗时对比（不是本项目实测，但量级一致）：

| 组合 | 几何 | 贴图 | 大致耗时 | 质量 / 代价 |
| --- | --- | --- | --- | --- |
| 当前默认（`ai_model` 留空 = latest，即 meshy-7.1） | 约 75 s | 默认贴图模型，2K 约 40 s 起 | **2 分钟左右 + 下载** | 细节最好；这也是之前"2~3 分钟"的来源 |
| 快速档 A（`meshy-t2` + `meshy-6-lite`，默认快速组合） | 约 4 s（4k 面） | 约 35 s | **约 40~50 s** | 低模 + 标准贴图，文字 preview 可直接 refine |
| 快速档 B（`meshy-6-lite` + `meshy-6-lite`） | 约 40 s | 约 35 s | 约 80 s | 标准拓扑、比默认轻 |
| 快速档 C（`meshy-7.1` + `meshy-n1`，必须另有参考图） | 约 75 s | 约 20 s | 约 100 s | n1 只出 2K 基础色、无 PBR，且**文字 preview 不能用** |
| 只出素模（`fastPreviewOnly`） | 约 4 s（t2）/ 75 s（7.1） | 无 | 最快 | 只有纯色白模，没有贴图 |

**怎么开**：`ObjectModelService` →「⑦ 快速模式」勾 `fastGeneration`，其余保持默认即可：

| 字段 | 作用 | 默认 |
| --- | --- | --- |
| `fastAiModel` | 几何模型；`meshy-t2` 会自动配 `model_type=smart-topology` | `meshy-t2` |
| `fastTargetPolycount` | 目标面数（t2 上限 15000、t2.5 上限 30000） | 4000 |
| `fastPreviewOnly` | 跳过 refine，只出素模 | 关闭 |
| `fastTextureModel` | refine 的贴图模型；文字 preview 可用 `meshy-6-lite`（默认）/ `meshy-6` / `meshy-7` / `latest`；`meshy-n1` 需要参考图 | `meshy-6-lite` |
| `fastTextureResolution` | 2k / 4k / 8k；4K/8K 只支持 meshy-6 / meshy-7 / latest | `2k` |
| `fastEnablePbr` | 是否生成 PBR 贴图；n1 不支持 | 关闭 |

状态栏在真正调 Meshy 的阶段会带 `［快速模式］` 前缀，并显示当前几何模型与面数。

### 贴图模型兼容性（官方文档结论）

| 贴图模型（refine 的 `ai_model`） | 能否从**文字** preview 直接 refine | 说明 |
| --- | --- | --- |
| `meshy-6-lite` / `meshy-6` / `meshy-7` / `latest` | **可以** | 用 preview 的几何 + `texture_prompt` 上贴图；4K/8K 只支持 6 / 7 / latest |
| `meshy-n1` | **不可以**（除非另给参考图） | n1 是"从参考图上色"：只有 `meshy-7.1` 或 `latest` 的文字 preview 会保存参考图；其它 preview（t2 / t2.5 / 6 / 6-lite）会返回 400。文字 preview 走 n1 必须提供 `texture_image_url`；并且 n1 不支持 `texture_prompt`、PBR、4K/8K |

### 提交前：能自动纠正的自动纠正，剩下的才拦

`ObjectModelService` 在调用 Meshy 之前先做参数纠正（`ApplyFastGeneration`），再用 `ValidateGeneration` 兜底。**任何一步命中都不会先跑完几何再在 refine 吃到 400**。纠正后的参数会写回 `spec.generation`，缓存指纹按**实际提交的参数**计算，不会拿 Inspector 里的旧值算。

**自动纠正（继续生成，状态栏与 Console 都写明改了什么）**：

| 情况 | 实际怎么处理 |
| --- | --- |
| `fastTextureModel=meshy-n1` 但没有参考图 | 自动改用 `meshy-6-lite`，并**保留** `texture_prompt` |
| `meshy-n1` + `fastTextureResolution=4k/8k` | 自动改成 `2k` |
| `meshy-n1` + `fastEnablePbr=true` | 自动关闭 PBR（n1 不支持） |
| `fastTargetPolycount` 超出当前几何模型的允许范围 | 夹到上下限 |

> 为什么选"换模型"而不是"清空 `texture_prompt`"：提示词是整套风格的唯一来源，清掉后模型失去风格约束，出来的东西更不可控；换 `meshy-6-lite` 则只是慢一点、细一点，风格还在。

**硬失败（不提交任何任务、不消耗额度，文案点名冲突字段）**：

1. `textureImageUrl`（`texture_image_url`）与 `texture_prompt` 同时显式给出——用户自相矛盾，自动纠正必然违背其中一边；
2. `meshy-t2` / `meshy-t2.5` 与 `modelType=smart-topology` 不配对（反之 `smart-topology` 也只能配这两个模型）；
3. 几何 / 贴图模型不在官方白名单内；
4. 面数越界（非快速档直接传入 `GenerationParams` 的情况）；
5. `texture_resolution=4k/8k` 配了不支持的贴图模型（只支持 meshy-6 / meshy-7 / latest）。

状态栏在提交阶段会显示**实际使用**的参数，例如 `meshy-t2 · 4000 面 · 贴图 meshy-6-lite@2k（无 PBR）`；完成时也会带上同一句摘要。

**旧场景值与推荐值对照**（Unity 按字段名保存 Inspector，代码改默认值不会覆盖场景里已存的值）：

| 字段（⑦ 快速模式） | 旧场景里可能是 | 推荐 | 不改会怎样 |
| --- | --- | --- | --- |
| `fastTextureModel` | `meshy-n1` | `meshy-6-lite` | 生成前自动换成 `meshy-6-lite` 并提示，不会报错 |
| `fastAiModel` | `meshy-t2` | `meshy-t2` | 无差异 |
| `fastTargetPolycount` | `4000` | `4000` | 无差异 |
| `fastPreviewOnly` | `false` | `false` | 无差异 |
| `fastTextureResolution` | `2k` | `2k` | 无差异 |
| `fastEnablePbr` | `false` | `false` | 无差异 |

不改也能正常生成，只是每次都会看到一条"已自动纠正"的黄色日志；想彻底安静就把 `fastTextureModel` 手动改成 `meshy-6-lite`。

**仍想用 n1 的路径**：在生成参数里填 `GenerationParams.textureImageUrl`（公开 URL 或 `data:image/...;base64`），并且清空风格的 `texturePrompt`、关闭 PBR、分辨率用 2k。代码会把它作为 `texture_image_url` 发给 refine，n1 就用这张图当前视图上色。没有参考图就别用 n1，换回 `meshy-6-lite` 即可。

**缓存会不会冲突**：会。生成参数（含 `aiModel`、面数、`modelType`、`textureModel`、`enablePbr`、参考图哈希）都参与 `generationFingerprint`，但缓存目录仍然按 id 一份：同一 id 用快速模式生成会**覆盖**原来的标准缓存，切回标准模式后指纹不一致会重新生成。不想丢标准结果就先别对同一个 id 开开关，或提前备份 `PersistentDataPath/ObjectCache/<id>`。

**参数是否还有效（核对官方 `docs.meshy.ai/en/api/text-to-3d`）**：`ai_model` 仍是官方字段（几何：`meshy-6-lite` / `meshy-6` / `meshy-7.1` / `latest`，或 smart-topology 的 `meshy-t2` / `meshy-t2.5`；贴图：`meshy-6-lite` / `meshy-6` / `meshy-7` / `latest` / `meshy-n1`）；`target_polycount`、`should_remesh`、`enable_pbr`、`texture_resolution`、`texture_image_url` 均有效。

---

## 十五、从加载到落位：为什么不会先闪一下

流程是：GLB 加载 → 按档位缩放 → 求位（可能还要等 LLM 回包，几秒）→ 旋转/贴地 → 登记占用。**这段过程里模型一律不渲染**：

- 实例化期间节点保持未激活，不会在原点或旧位置画出来；
- 加载完先把所有 `Renderer.forceRenderingOff = true`，再激活节点——这样 `Awake/OnEnable`、包围盒测量都照常工作（没有依赖"未激活"的时序），但画面里看不到；
- `PlaceInstance` 落位成功的那一行同时做两件事：打开渲染 + 撤掉占位牌，所以玩家看到的是**占位牌直接变成已经摆好的模型**，中间没有滑动、没有瞬移、没有空白帧；
- 落位被拒绝时占位牌显示失败并自毁，不会留下一个永远"施工中"的牌子。

> 代价：如果调用方拿了模型却一直不调用 `PlaceInstance`（比如只想量尺寸），模型会保持不可见，占位牌也会留着——需要自己调 `PlaceInstance` 或销毁实例。

---

## 十六、现在还做不到的事

1. **只按横向宽度算**：占地取包围盒投影到 X 的宽度，形状不规则会被当成一整块矩形，摆得偏保守。
2. **前后方向固定**：同一层共用一个 depth，不能"往房间里面挪一点"，也没有第二排。
3. **只有一层地面**：没有楼上楼下、台阶。
4. **挂墙的东西动得很有限**：地面上的 Touch/Play 才能被整块推走；挂墙的 Play 锁了上下和旋转，只会在墙面左右滑；挂墙 / 屋顶的 Touch 只有晃动反馈，位置不会真的改变。
5. **被推走的物件可能和别的东西重叠**：会记住新位置，但不会把已放好的东西挪开。
6. **被推走的位置不是每帧存盘**：只在放下、移除、清空时写，退出前最后推的那一下可能没保存。
7. **AI 布局可能降级**：没网、没 Key、回答不合规时退回规则摆放，位置可能不是最优。
8. **AI 只看到同一层的东西**：判断不了"墙上的画会不会被地上高柜挡住"。
9. **满 10 个是硬规则**：第 11 个一定挤掉最早的，没有"合并同类"或"拒绝新增"的选项。
10. **"贴着某个东西放"还没启用**：AI 说了也不会照做。
11. **回放依赖当前面板参数**：换风格后旧布局整体跳过，不会自动迁移。
12. **颜色纠偏只动黄褐**：25°–70° 之外（红、粉、绿、蓝）不改；很暗或没有可读贴图时交给 shader 逐像素判断。
13. **进球奖励只在 C 阶段可用**：篮筐判定与泡泡奖励挂在场景物体上，需要按「十七」的清单手动挂组件，不会自动出现在别的场景。
14. **球要你自己放**：C 阶段不会主动索要球；预制体 `PrototypeC_Ball.prefab` 需要手动拖进场景（有且仅有一颗）。
15. **快速模式会覆盖同 id 缓存**：缓存目录按 id 一份，快速档与标准档的产物不会并存；切回标准档会判定指纹不一致重新生成。
16. **快速档的材质更素**：默认 `meshy-6-lite` 贴图没有 PBR 细节；`meshy-n1` 只有 2K 基础色，而且文字 preview 用不了（必须另给参考图）。

---

## 十七、玩法：球 / 篮筐 / 泡泡（C 阶段）

这一节是手动装配出来的玩法，不依赖 AI 生成，也不改场景文件：组件、预制体、引用都由你在编辑器里挂。

### 17.1 球：预制体 + 双形态

- 用菜单 `Tools/Prototype C/创建球预制体` 从 `Assets/Art/Model/ball.glb` 生成 `Assets/Prefabs/PrototypeC_Ball.prefab`（已挂 `BallFormController`），然后把它拖进场景。
- **为什么之前的预制体球没有 toon 材质**：`ToonMaterials` 只在两条路径被调用——`ObjectModelService` 的生成管线、以及编辑器菜单手动执行。手动拖进场景的实例两条都不走，所以仍是 glTF 自带材质。现在 `BallFormController.Awake()` 会对实体形态的每个 Renderer 调 `ToonMaterials.Apply(renderer, ToonLayer.Foreground)`，运行时创建材质实例（不改 `.glb`、不改场景），和生成物用同一套 `PrototypeC/Toon`。
- 另外 `ToonMaterials.ResolveLayer` 会把 `.glb` 预制体实例判成 `Backdrop`（背景档），所以球必须显式传 `Foreground`，不能走自动判定。
- **上层 = 实体球**（toon），**下层 = 同一个球的单个字符 `@` + 拖尾**。分界线取 `B1LayerConfig.upperFloorY`（场景里已有的上层地板高度），`BallFormController.splitY` 填 0 即用它。
- **下层就是一个 `@` 字符 + 字符拖尾**（和 B1 原本的字符表现一致）：`glyphChar`（默认 `@`）→ `LetterPlant.GlyphSprite` 拿同一张字符贴图 → TMP 只放一个字符，按渲染包围盒把高度缩到 `2 × 球半径 × glyphDiameterScale`（默认 0.95），所以上下切换时尺寸不跳。拖尾直接复用 `B1Ball` 里已有的 `GlyphTrail`（`EmitTrail` 转发），参数 `trailEnabled / trailInterval / trailMinSpeed`。**已删除**上一版的多行字符画与圆形遮罩（`glyphRowCount / glyphChars / BuildGlyphArt` 全部移除，不再有两套并存）。
- 切换：球心向下越过分界线 → 字符画淡出、实体显隐；向上越过 → 反过来。`formFadeSeconds` 是过渡时长。**不做真遮罩，也不改缩放**：toon 是 Opaque 着色器，不能 alpha 淡入，所以实体只做显隐、字符画用 TMP alpha 交叉。
- **物理复用 B1 的球，不再自研**：`BallFormController` 加 `[RequireComponent(typeof(B1Ball))]`，运行时把 Inspector 参数转交给 `B1Ball`（继承 `B1Item`）。运动核只有一份：`B1Item.StepPhysics`（重力、落地反弹、左右边界、缺口跨层、出界销毁），恐龙踢球也直接走 `B1Ball.Balls` 注册表。适配层只做四件事：套 toon 材质、双形态显隐/alpha、`PressDown()` 转发穿板、参数转发。`BallFormController.Velocity / Gravity / Launch()` 都是对 `B1Ball` 的薄封装，篮筐与辅助弹射读的就是 `B1Ball` 的真实运动结果。
- **弹性参数在 `BallFormController`**（都被转发到 `B1Ball`）：`restitution`（落地恢复系数，默认 0.72，建议 0.65~0.8）、`damping`（水平空气阻力）、`wallBounce`、`minBounceSpeed`（落地速度低于它才允许静止）、`groundHorizontalDamping`、`gravity`。`B1Item` 上也有同名 `minBounceSpeed / groundHorizontalDamping`，默认值与旧硬编码一致，所以 TemplateB1 的手感不受影响。
- **下层保底弹跳**：`layerBounceMinHeight`（下层最低弹射高度，默认 **1.5 米**，0 = 关闭）。只在落地那一帧判定：落地后的反弹高度（`v²/2g`）低于它就补到该高度（`v = √(2g·高度)`，直接赋值、不依赖恢复系数链），所以不会越弹越低、也不会停在 `_vel.y = 0` 的静止分支。上层不生效，**没有任何自动上弹/自动跨层**——跨层只由玩家的手指弹床提供向上力。`B1Ball.layerBounceMinHeight` 默认 `0`（关闭），B1 原场景不受影响。
- **水平不会滚出画面**：`clampToCameraView`（默认开）在运行时用 `Camera.main.orthographicSize × aspect` 算可视半宽（场景正交 size 4.885，16:9 → ±8.68），减去 `viewMargin` 与球半径后写进 `B1Ball.SetHorizontalBounds`，撞到边界按 `wallBounce` 反弹。旧版沿用 `B1LayerConfig.boundsXMin/boundsXMax = ±9.86`，比画面宽 1.2 米，球会滚到看不见的地方。B1 的原物品不受影响（`B1Item.useCustomHorizontalBounds` 默认 false）。窗口比例变化时会自动重算。
- **物理半径自动推导**：`autoRadiusFromBounds`（默认开）用实体 Renderer 的渲染包围盒取 `max(extents.x, extents.y)` 作为半径，通过 `B1Ball.SetPhysicsRadius` 写入。单位球真实半径 0.5，而旧版硬编码 `radius=0.34`，会让球心落在地面上 0.34 处、底部埋进地板 0.16——看起来就像被压扁。
- **"扁"的排查结论**（三条线都查过）：① 资源侧 `ball.glb` 的 mesh POSITION 是 `-0.5~0.5`、单 node、matrix 为 identity；`PrototypeC_Ball.prefab` 的 `m_Modifications` 里**没有 `m_LocalScale`** → 资源没有非等比缩放。② 尺寸流程 `ObjectModelService.ApplySize` 是 `Vector3.one * scale` 等比缩放，只有挂墙物件才会被 `ApplyWallThinning` 压薄 Z，球不涉及；而且手动拖入的预制体根本不走这条流程。③ 代码侧是根因：旧 `BallFormController` 的 `radius=0.34` 小于真实半径 0.5（半个球埋进地板），且旧 `UpdateForm` 每帧写 `transform.localScale = _baseScale * Mathf.Lerp(0.72f, 1f, _formT)` 把球整体缩到 0.72。已删除所有对 `transform.localScale` 的写入；另加 `NormalizeNonUniformScale()` 兜底（检测 `lossyScale` 非等比、按最大轴补偿并 `Debug.LogWarning`），覆盖"父节点被非等比缩放"的情况。
- 双形态切换只改 Renderer 的 `enabled` 与 TMP 的 `alpha`，**不碰 transform 的位置与缩放**，所以切换前后速度、位置连续。
- 恐龙下压：球在上层、恐龙在 `dinoNoticeRange` 内且球基本静止 → 恐龙走过来、起跳，下落时触发 `PressDown()`（内部 `B1Ball.IgnoreUpperFloorFor(passThroughSeconds)` + 给一个向下速度），球在 `passThroughSeconds` 内无视上层地板，直接落到下层。参数：`dinoPressEnabled / dinoNoticeRange / dinoPressRadius / pressDownSpeed / passThroughSeconds`。
- 恐龙侧只新增了 `ExternalWalkTo / ExternalFace / ExternalHop / IsFalling / IsAirborne / ExternalBusy`，没有改它原来的状态机；外部驱动的行走只在 `UpdateWandering` 开头加了一个"外部窗口"分支。

### 17.2 篮筐：投入判定

- 选中篮筐（场景里名叫 `篮筐`，模型是 `model.glb`）→ 菜单 `Tools/Prototype C/为选中的篮筐添加投入检测`，或在 Inspector 里手动 `Add Component → BasketHoop`。
- 判定条件（横版没有 Z 轴，只看 X/Y）：
  1. 上一帧球心 Y **高于**筐口平面、这一帧 **低于等于**筐口平面（这一帧穿过去）；
  2. 球速度 `vy < -downSpeedThreshold`，也就是**向下**穿过——**向上穿过不算**；
  3. `|球心X - 筐口X| <= rimHalfWidth`。
- `rimLocalOffset` 是筐口相对物体原点的偏移，`rimHalfWidth` 是半宽；选中篮筐时 Scene 视图有黄线（筐口）与青色竖线（中心）的 Gizmo 辅助对准。
- 进球 → 全屏礼花（运行时创建 `ParticleSystem`，从画面上方撒下，`confettiCount / confettiColorA/B / confettiSize`）+ 文字雨暂停 `celebrateSeconds`（用 `B2BackgroundRain.Suspend(this)`，到点 `Resume(this)`）+ 若填了 `bubbleReward` 则触发泡泡。
- 连续没进 `missesBeforeAssist` 次 → 恐龙靠近筐口、面向筐、停顿 `assistAimDelay` 秒（"计算角度"），然后对最近的上层球做抛体解算并 `Launch`。`assistChance` 是命中概率，没命中会故意把力度/角度随机偏一点，所以**不保证进球**；`assistFlightTime` 控制弹道高低。
- 球一直没被抛起来时判定永远不会触发（没有球穿过筐口平面），所以还有 `idleThrowSeconds`（默认 8 秒）：球静止超过这个时间，恐龙主动投一次，把循环带起来。
- **换成 `B1Ball` 后判定仍然成立**：判定用的是 `BallFormController.Velocity`（= `B1Ball.Velocity`，即 `B1Item._vel`），"上一帧 Y / 这一帧 Y"读的是 `B1Ball` 写回 `transform.position` 的位置，语义没变：球心向下穿过筐口平面且 `vy < -downSpeedThreshold` 才计进球，向上穿过不计。辅助弹射的抛体解算改用 `BallFormController.Gravity`（转发 `B1Ball.gravity`）。同帧 `Update` 顺序可能让判定落到下一帧，但"穿过去 + 向下"的条件不受影响。

### 17.3 泡泡奖励：拍照 + 雨恢复

- 菜单 `Tools/Prototype C/在场景中添加泡泡奖励` 创建 `BubbleReward` 物体，然后把篮筐的 `BasketHoop.bubbleReward` 指向它。
- 泡泡出现在**相机画面正中央**（若填了 `bubbleMachine`，从泡泡机位置升到正中）。表面显示当前相机画面：主相机的画面渲染到 `RenderTexture`，`PrototypeC/Bubble` shader 用屏幕 UV + 球面法线扰动采样这张 RT，边缘按 fresnel 提亮；`_Alpha / _Distortion / _FresnelPower / _RimColor / _Tint` 都可以在材质上实时调。
- **拍照时泡泡不能入画**：`BubbleReward` 会新建一台 `BubbleCaptureCamera`，复制主相机的位置、正交参数、清屏色，但 `cullingMask = 主相机.cullingMask & ~(1 << bubbleLayer)`。泡泡根节点（含倒计时 TMP）整体设为 `bubbleLayer`（默认 30，代码里设置，不用改 TagManager），所以照片里既没有泡泡也没有倒计时数字。
- 倒计时 `countdownSeconds`（默认 3 秒）显示在世界空间 TMP 上。玩家用鼠标左键/触摸点到泡泡（屏幕点转世界点、距离 ≤ 半径）→ **取消奖励**；到点 → **自动拍照**。
- 照片保存到：
  `Application.persistentDataPath/BubblePhotos/bubble_yyyyMMdd_HHmmss.png`
  （Windows 下通常是 `C:\Users\<用户名>\AppData\LocalLow\<公司名>\<产品名>\BubblePhotos\`）。保存成功后 Console 打一行 `[Bubble] 照片已保存：<完整路径>`，同时写到 `PrototypeCTestUI.statusText`（场景里有它才写）。
- 结束表现：泡泡缩小到 `shrinkToDiameter`（约恐龙大小）、以 `riseSpeed` 上升，在房间上部按 Perlin 噪声左右飘 `driftAmplitude`，`floatSeconds` 后销毁。
- **文字雨恢复**：`FinishSession()` 是唯一收尾入口——自动拍照、玩家戳破、`OnDisable`/`OnDestroy`（退出/中断）全都走它，内部调 `B2BackgroundRain.Resume(this)`。雨按 `B2BackgroundRain.resumeSeconds`（默认 0.8 秒）渐进恢复密度，不是瞬间满屏。暂停用 owner 集合，庆祝与泡泡互不覆盖；`B2BackgroundRain.enabled` 不会被改动。

### 17.4 装配清单（需要你在编辑器里做）

1. 菜单 `Tools/Prototype C/创建球预制体` → 把 `Assets/Prefabs/PrototypeC_Ball.prefab` 拖进场景（替换或删除原来那颗 `ball`），建议放在上层 `y≈3.6`。
   - 已经生成过旧版预制体**不用重建**：`BallFormController` 的 `B1Ball` 是运行时自动补挂的（`[RequireComponent]`），旧预制体上序列化的 `radius / groundBounce / wallBounce / drag / glyphRows / glyphChars / microBounceImpulse / crossLayerBounce` 等旧字段已经不存在，Unity 会直接忽略。进 Play 后在球的 Inspector 里确认 `restitution=0.72`、`layerBounceMinHeight=1.5`、`glyphChar=@`、`trailEnabled=✔`、`clampToCameraView=✔`、`autoRadiusFromBounds=✔` 即可。
   - 若担心层级里有残留的非等比缩放，进 Play 看有没有 `[Ball] 球的缩放不均匀` 的 Warning；没有就是干净的。
2. 选中 `篮筐` → 菜单 `Tools/Prototype C/为选中的篮筐添加投入检测` → 在 Scene 视图用 Gizmo 对准筐口，调 `rimLocalOffset / rimHalfWidth`。
3. 菜单 `Tools/Prototype C/在场景中添加泡泡奖励`（若还没有）→ 确认 `mainCamera` 指向 `Main Camera`。
4. 把 `BubbleReward` 拖到 `BasketHoop.bubbleReward`。
5. 确认 `dino` 的 `DinoAgentB1.groundY` 是 `1.84`（恐龙下压/辅助的表现基准）。
6. 泡泡用 layer 30：默认第 30 层在工程里通常是空层，可直接用；若你把它挪作他用，改 `BubbleReward.bubbleLayer` 即可。

### 17.5 已知风险

1. **泡泡观感**：屏幕 UV + 法线扰动是折射的近似，正交相机下球边缘会拉伸；先把 `_Distortion` 从 0.055 往下调，再调 `_FresnelPower`/`_RimColor`。
2. **拍照时序**：`BubbleCaptureCamera` 只在 `CapturePhoto()` 里手动 `Render()`，拿到的是倒计时归零那一帧的画面；雨已暂停、泡泡已排除，但恐龙/粒子的瞬间姿态可能和玩家看到的差一帧。
3. **shader 打包**：`PrototypeC/Bubble` 目前靠 `Shader.Find`，编辑器里没问题；出包前要把它加进 `Project Settings → Graphics → Always Included Shaders`，否则可能被裁掉。
4. **雨恢复依赖实例**：场景里没有 `B2BackgroundRain` 时 `Suspend/Resume` 静默跳过；雨组件本身被 `enabled=false` 时也不会恢复。
5. **恐龙外部指令**：`External*` 会打断它当前的发呆/散步（逃跑、搬建材、盖房时会被 `ExternalBusy` 挡掉），连续触发会显得"被玩家指使"。
6. **球预制体材质**：toon 材质是运行时实例，退出 Play 就没了；预制体资产本身保持原样。
7. **球半径是包围盒推导的**：如果 `ball.glb` 的网格不是严格单位球（或你换了别的模型），半径可能偏大，表现为球略微悬空。把 `autoRadiusFromBounds` 关掉、改 `B1Ball.SetPhysicsRadius` 用的值（或直接调 `B1Ball` 上的 `size` 后不再覆盖）即可。
8. **保底弹跳**：`layerBounceMinHeight` 太大时球会一直弹得比较高（默认 1.5 米 ≈ 一个半球高）；设 0 就完全关闭保底，只保留正常的恢复系数反弹。它只补下限，不会把更高的弹跳压下来，也不会自动把球送到上层。
9. **单字符看起来偏小/偏大**：字符高度按 `2 × 球半径 × glyphDiameterScale` 归一，改 `glyphDiameterScale` 即可；拖尾密度用 `trailInterval`，速度太快拖尾太密就调大它。不再有 `glyphRowCount / glyphChars`（已删除）。
