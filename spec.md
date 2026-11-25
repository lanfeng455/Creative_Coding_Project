# PINOSAUR — 地面落位修复 / 场景与 Gizmo 剪枝

> 上一轮「上层手绘化 / 前景描边 / 光照可见性」的实现记录仍在 `checklist.md`，本轮不动那部分代码。

## 1. 现象与归因

| 现象 | 根因（已核实） |
| --- | --- |
| 右侧椅子悬空 | `ObjectModelService.PlaceInstance` 的 Ground 分支固定用 `ToWorldY(groundBandMinY)`（世界 1.88），与 X 无关；右侧室外分区实际是 1.61，所有室外生成物浮空 0.27 m |
| 房内床嵌进地里 | 房内可视地板高于分区值 1.88 m，物件底面按 1.88 摆，视觉上陷入地板 |
| Scene 视图全是格子 | `ObjectModelService.showPlacementGizmos=1` 与 `RoomLayout.showRoomGizmos=1` 同时挂在同一个 `PrototypeC_Controller` 上，选中即叠加地面/墙/天花板带、旧槽位 s0~s5、命名锚点、禁放矩形等两套绘制 |
| 初始摆放物件没有前景材质 | 材质切换只有编辑器菜单入口，尚未对场景手工物件执行 |

地面高度的唯一真相是 `RoomLayout.dinoGroundZones`（世界坐标）：

```
outdoor-left  [-8.5, -3.21] @ 1.61
room          [-3.21, 2.63] @ 1.88 → 2.04
outdoor-right [ 2.63,  8.5] @ 1.61
```

`DinoCController.GroundYAtX`、`GroundStepGate`、`EnsureTouchFloor` 都已按这三段分区处理，只有 `PlaceInstance` 还没对齐。

## 2. 改动清单

### 2.1 `Assets/Scripts/C/ObjectModelService.cs` — 按分区取地面高度

在 `ToWorldY`（约 L725）之后新增私有方法：

```csharp
/// <summary>世界 X 处的地面 Y：以 RoomLayout.dinoGroundZones 为唯一真相（世界坐标）；
/// 分区外取最近分区的边缘高度；没有分区时退回锚点 groundBandMinY。</summary>
private float ResolveWorldGroundY(float worldX)
{
    if (roomLayout == null) roomLayout = FindObjectOfType<RoomLayout>();
    List<DinoGroundZone> zones = roomLayout != null ? roomLayout.dinoGroundZones : null;
    if (zones != null && zones.Count > 0)
    {
        float bestY = ToWorldY(groundBandMinY);
        float bestDistance = float.MaxValue;
        foreach (DinoGroundZone zone in zones)
        {
            if (zone == null) continue;
            float lo = Mathf.Min(zone.xMin, zone.xMax);
            float hi = Mathf.Max(zone.xMin, zone.xMax);
            if (worldX >= lo && worldX <= hi) return zone.groundY;
            float d = worldX < lo ? lo - worldX : worldX - hi;
            if (d < bestDistance) { bestDistance = d; bestY = zone.groundY; }
        }
        return bestY;
    }
    return ToWorldY(groundBandMinY);
}
```

把 `PlaceInstance` 的 Ground 分支（约 L908-913）改为：

```csharp
default:
    desiredCenter = new Vector3(
        fitted.center.x,
        ResolveWorldGroundY(fitted.center.x) + fitted.extents.y,
        fitted.center.z);
    break;
```

Wall / Rooftop 分支不动。`fitted` 是已按最终旋转/缩放算出的世界包围盒，`fitted.center.x` 就是落点世界 X，与分区同一口径。

### 2.2 `Assets/Scenes/TemplateC_LanMay.unity` — 房内地面抬高

`RoomLayout.dinoGroundZones` 的 `room.groundY`：`1.88` → `2.04`（约 +0.16 m，对应截图中床被地板前沿遮住的高度）。

- 该值同时驱动：生成物贴地、恐龙脚底、隐藏物理地面、`GroundStepGate` 台阶判定，四处自动一致。
- 室外两段保持 `1.61`。
- 若实机验收发现恐龙略微悬空，只调这一个数（向 1.88 回调）。

### 2.3 Gizmo 剪枝 — 只留一套

**`ObjectModelService.cs`**

- 删除 `OnDrawGizmosSelected`（L1396-1528）及仅它使用的 `Overlaps`（L1530 起）、常量 `GizmoPlaneOffset` / `GizmoSlotColumns`（L1393-1394）。
- 删除仅供该 Gizmo 使用的字段：`showPlacementGizmos`、`groundBandGizmoColor`、`wallBandGizmoColor`、`ceilingBandGizmoColor`、`slotGizmoColor`、`gizmoFillAlpha`、`gizmoConflictColor`（L237-257）。
- 保留数据字段 `groundBandMinY/MaxY`、`wallBandMinY/MaxY`、`ceilingBandMinY/MaxY`、`wallPlaneZ` 等（真实摆放逻辑仍用）。
- 场景中已序列化的这些字段值会被 Unity 自动忽略，不需要额外迁移。

**`RoomLayout.cs`**

- 删除「命名锚点」绘制块（L613-659）与只服务于它的 `anchorColor` 字段。锚点数据本身保留（`LayoutSolver` 仍按锚点求位）。
- 保留：可用区间条带、墙面 + 朝向箭头、墙面禁放矩形（窗户）、地板参考线。
- 新增「恐龙地面分区」绘制（放在墙面块之后）：
  - 用世界坐标绘制（临时 `Gizmos.matrix = Matrix4x4.identity`，绘制完恢复）;
  - 每段画一条 `(xMin, groundY, zWorld) → (xMax, groundY, zWorld)` 的横线 + 两端短竖线，颜色区分室内/室外；
  - `Handles.Label` 标 `"{name}  y={groundY:0.00}  x {xMin:0.00} ~ {xMax:0.00}"`；
  - `zWorld` 取 `frame.TransformPoint(0, 0, groundDepthZ).z`，与地板参考线同一深度。
- 结果：选中 `PrototypeC_Controller` 时只剩一套带标签的绘制，地面分区一眼可见。

### 2.4 场景遗留对象：保留，不做 YAML 删除

核查后发现以下引用仍然存在，直接删 GameObject 会造成断引用：

- `B2_RequestBubble` → `dino.bubble`（`PrototypeCSceneBootstrap` 把它交给 `DinoCController.bubble`）。
- `B2_LowerFloor` → `B2World.lowerFloorVisual`。
- `B2_TextStyle` → 打字机字符碎片样式（`TypeWriterB1` 仍在使用）。
- `B2_Scheduler` / `B2_RainCanvas` / `B2_BackgroundRain` → 字符雨仍保留。
- `B2_TypeWriter` / `Solution` / `RightHandIndexRayProvider` → 输入与手部追踪。
- `BasketHoop` / `BubbleReward` → Prototype C 的投篮奖励。

因此本轮剪枝只针对「旧调试绘制 + 死字段」，不碰仍被运行时引用的场景对象。

### 2.5 初始摆放物件上前景材质（回答，不改代码）

已有入口，无需新代码：

- 选中要处理的物件 → 菜单 `Tools/Prototype C/Rendering/Apply Toon To Selection (Foreground)`。
- 批量：`Tools/Prototype C/Rendering/Apply Toon To Scene Mesh Renderers (Auto Layer)`。
  - `ToonMaterials.ResolveLayer` 的规则：祖先含 `PrototypeC_GeneratedAnchor` → 前景；模型 prefab 实例（.glb/.gltf/.fbx，如房子）→ 背景；其余（恐龙、手工摆放物件）→ 前景。
  - 所以场景初始摆放的物件用 Auto Layer 就会得到前景档（`_StyleBlend=0` + 1.4 px 描边）。

## 3. 验收步骤

1. Unity 编译无报错。
2. 选中 `PrototypeC_Controller`：Scene 视图只有一套绘制（可用区间、墙体/窗、地面参考线、三段恐龙地面），不再出现 s0~s6 槽位与锚点竖线。
3. 进入 Play：
   - 右侧椅子底部贴在室外地面（不再悬空）；
   - 房内床底面贴在可视地板上（不再陷入）；
   - 恐龙在房内/室外分别站在 2.04 / 1.61，跨区仍有小跳；
   - 把房内物件推出台阶仍然会掉到室外，反向推不上去（`GroundStepGate` 回归）。
4. 选中一个初始摆放物件执行 Foreground 菜单，确认出现描边且不受背景降饱和影响。

## 4. 影响面

- 仅改 2 个脚本 + 1 行场景数据；不动 shader、后处理、`layout.json`、缓存。
- 已缓存模型不会重烧：`groundY` 不参与缓存指纹。
