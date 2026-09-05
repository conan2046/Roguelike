# 圆柱碰撞预制体微调

## 资源目录

- Hero：`Assets/Prefabs/Combat/Hero/`，6 个神将本体（B/H/J/K/Y/Z）。
- Monster：`Assets/Prefabs/Combat/Monster/`，当前怪物表全部 79 个。
- Weapon：`Assets/Prefabs/Combat/Weapon/<神将本体名>/`，24 个独立武器层，没有移动碰撞组件。
- 本体/怪物 Prefab 包含 `VisualPreview` 定位节点和 `MovementCollision` 圆柱节点；独立武器只有预览定位节点和表 ID。
- 外观在开启 Gizmos 的 Scene/Prefab 视图按需读取源 PNG/ANI 绘制；网格、材质与纹理仅保存在编辑器内存，不保存 `Preview/*.asset`，不参与运行时 ECS 实例生成。
- 怪物 10043 没有待机片段标记，其预览节点命名为 `VisualPreview_NoIdle_FirstClip`，仅编辑预览使用表内首个片段。

## 调整步骤

1. 双击目标 Prefab，选择 `MovementCollision`。
2. 开启 Scene Gizmos；拖拽绿色/青色圆柱手柄调整 Radius、Height，移动节点调整中心。圆柱上下端面是平面。
3. 保持根节点位置为零、节点旋转为零、缩放为一。尺寸使用组件字段或手柄调整。
4. 保存 Prefab，打开 **Roguelike > Combat > 圆柱碰撞配置**，点击 **导出全部已保存圆柱并生成 Luban**。统一窗口显示待导出列表，未保存的 Prefab Stage 会拒绝导出。
5. 重新进入战斗。运行中不会读取正在编辑的 Prefab 参数。

编辑预览采用 Y 轴竖直、XZ 地面。节点 X/Z 对应现有战斗模拟的 X/Y；节点 Y 减去半高为底面高度。高度区间重合时，双方按底面圆阻挡；两端没有胶囊半球。

圆柱参数以用户保存的微调为准。首次缺失资源按预览帧尺寸初始化，仅作为待微调起点；武器层不初始化圆柱。

## 神将与武器映射

`TbCharacter` 只保留 6 个本体，ID 为 10001、10006、10011、10016、10021、10026。`TbHeroWeapon` 保存全部 30 条档位映射，原资源 ID 和 `TbVisualSet` 不变。

每位神将有 0～4 档武器：第 0 档已包含于 `*_0` 本体，1～4 档 `*_d` 是独立武器层。武器目录按本体名分组，组件的 WeaponConfigId 对应武器表。当前战斗继续使用已有第 0 档外观，本次不增加运行时换武器功能。

批量工具按配置归属处理武器，已有本体与怪物预制体保持不变。文件重命名后通过组件 ID 识别，不会因此覆盖或重复生成。

## 批量工具

统一入口为 `Roguelike > Combat > 圆柱碰撞配置`，组件面板没有单独导出按钮。

- **批量生成缺失预制体**：遍历全部角色/怪物表，增量创建缺失项，已有 Prefab 和微调保持不变。
- **导出全部已保存圆柱并生成 Luban**：更新两张源 Excel 的圆柱字段并调用项目生成脚本。日志为 `Library/CombatCylinderExport.log`。
- **刷新修改状态**：检查模板与生成 bytes 一致性，列出待导出资源；窗口获得焦点时也会刷新。

源表使用 `moveRadiusPixelsMilli`、`moveHeightPixelsMilli`、`moveOffsetXPixelsMilli`、`moveOffsetYPixelsMilli`、`moveElevationPixelsMilli`，存储逻辑像素乘 1000 后的整数。`bodyRadiusMilli` 保留为独立伤害判定半径的千分整数。运行时统一通过 `ConfigNumber` 还原单位。

## 技能碰撞

`Assets/Prefabs/Combat/Skill/` 按 `TbSkill.id` 保存技能编辑节点。选择 `ProjectileCollision` 调整半径及位置：X 为技能局部右向，Z 为发射前向，Y 必须为零；根位置和旋转为零，缩放为一。

保存后执行 **Roguelike > Combat > 技能碰撞配置 > 导出已保存技能并生成 Luban**。工具写入 `TbSkill.projectileRadiusMilli`、`projectileOffsetXMilli`、`projectileOffsetYMilli`，按世界单位乘 1000 四舍五入；只导出已配置为弹丸的技能。非弹丸条目保留空值，`int?` 的问号仍表示允许为空。

`TbSkillCombat` 不再持有弹丸半径，继续定义发射方式、间隔、射程、速度、寿命及近战前摇。同一战斗配置可供不同半径的技能共用。ECS 在发射时旋转局部偏移，扫掠使用碰撞中心，表现原点不变。

打开技能预制体时直接读取正式 ANI/PNG，在内存中显示首帧；关闭或域重载释放临时对象。不得保存独立预览纹理、材质、网格或其引用。**校验保存节点与配置** 菜单只读检查，不覆盖手动编辑。

技能预览在 Prefab Stage 初始化后延迟创建，将 ANI 的 XY 图像映射到碰撞使用的 XZ 编辑平面，并以正交俯视同时展示特效与圆形范围。选择 `ProjectileCollision` 后可调整半径与 X/Z 偏移；视角被手动改变时，点击 Inspector 的 **刷新技能表现并俯视碰撞范围** 恢复。该映射仅用于编辑参考，不改变运行时表现，也不会移动已保存的碰撞节点。

配置小数统一采用千分整数；既有万分比维持原单位，属性大值使用 `long`。修改源表后运行 `Tools/Config/generate.ps1`，再运行 `Tools/Config/validate.ps1` 确认生成代码及 bytes 可复现，重新进入战斗消费新快照。

## 移动行为

ECS 固定步进按空间网格查询相邻圆柱，进行连续扫掠与接触滑动。主角与怪物双向阻挡，不施加推力；攻击前摇期间怪物维持原位置。圆周出生检查占位，沿圆周寻找空位，整圈被占时延迟当前出生事件，不丢失波内数量。

这里只处理单位间移动阻挡，不引入 NavMesh、跳跃或障碍物路径规划。美术贴图仍可能相互遮挡，阻挡依据是脚底圆柱，而非整张图像矩形。

先 Unity Editor 验证，再由用户微调及人工验收；全部功能完成且用户明确要求后才构建 Win64。工具不保存 Scene、不提交或推送代码。

## 预览缓存与可复现性

- 强制禁止生成、落盘或提交独立预览资产；本节“缓存/重建”仅指内存中的临时绘制对象，不生成 Assets、Library 或其他目录下的预览文件。
- 新机器无需下载预览副本：配置生成物与源 PNG/ANI 就绪后，双击 Prefab 并开启 Scene Gizmos 即可显示首帧。
- 预览使用原 `VisualPreview` 节点变换，不改碰撞形状、节点位置或资源 GUID；Prefab 不保存缓存引用。
- 项目资源变更、Play 状态切换、程序集重载或退出编辑器时释放缓存；下一次查看自动重建。
- Project 面板缩略图不依赖持久化网格；请在 Prefab 视图查看外观。Gizmos 关闭时外观与碰撞辅助绘制隐藏。
