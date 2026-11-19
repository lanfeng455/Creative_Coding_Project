# A-frame 小屋 v01

六阶段：STONE 石基 → WOOD 地板 → BEAM 三角屋架 → PLANK 墙板和木门 → TILE 屋面 → GLASS 窗户及暖光。

房屋采用适配当前固定正交镜头的 2.5D 网格分层；六层几何和材质集中于 Assets/HouseModel/AFrame，不依赖外部模型下载。轮廓参考用户选择的三角小屋，部件拆分参考 Rural Cabin Exterior 的墙面／屋顶／地基／装饰分类；没有导入第三方模型或贴图。

AFrameBuildPlan 负责材料顺序与材料图标；AFrameHouseVisual 负责六层网格与安装动画。B2House 负责进度和完工登记。TypeWriterB1 按字母数量匹配，顺序不限，优先匹配恐龙当前需求，只消费所需字母并保留其他字母，增加四个词 BEAM、PLANK、TILE、GLASS。B1Resource 保留原有运动学、搬运和落地过程，新增建材词身份。DinoAgentB1 只调整材料需求、收集筛选与安装调用；ApplyMotion、Face、ApplyVisual、PickWanderTarget 保持原样。

运输期间不继续请求同一阶段的材料。B1EventScheduler 对当前需求词循环提供字母，每三次生成尝试至少有一次提供需求字母，避免纯随机导致长期缺少某个字母；GLASS 中的两个 S 按两个条目参与循环。

启动：运行 TemplateB2。恐龙从 STONE 开始请求；玩家拼出当前单词后生成相应材料，沿用上层掉落、恐龙拾取搬运、到工地安装的链路。房屋竣工调用现有 RoomBuilt 落地与 dust 效果。完工房屋被毁后重新从 STONE 开始。

编辑器验证入口：AFrameConstructionCheck.Begin()，只在全新的 Play 会话中手动执行。检查会临时关闭事件调度、自动录入测试词、启用后台运行，并输出各阶段相机截图。它不是手部识别测试；测试结束后退出 Play 即可恢复正常场景配置。

原文件备份保存在 output/aframe-house-v01/backup。实际集成测试结果见同目录 integration-check.txt。
