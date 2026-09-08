# 战斗碰撞预制体微调

本轮实现前已按碰撞配置范围检查 Tower 参考工程；约定目录在本机不可访问，因此继续遵循当前项目的 Luban、Entities 1.0 与 Prefab 编辑规范实现。

## 资源目录

- Hero：`Assets/Prefabs/Combat/Hero/`，6 个神将本体（B/H/J/K/Y/Z）。
- Monster：`Assets/Prefabs/Combat/Monster/`，当前怪物表全部 79 个。
- Weapon：`Assets/Prefabs/Combat/Weapon/<神将本体名>/`，24 个独立武器层，没有移动碰撞组件。
- 全部 6 个角色与 79 个怪物已把移动阻挡与受击判定合并为一个 `碰撞范围`，统一使用纵向 `CapsuleCollider2D` 编辑。
- 独立武器只有预览定位节点和表 ID，不配置单位碰撞。
- 外观在开启 Gizmos 的 Scene/Prefab 视图按需读取源 PNG/ANI 绘制；网格、材质与纹理仅保存在编辑器内存，不保存 `Preview/*.asset`，不参与运行时 ECS 实例生成。
- 怪物 10043 没有待机片段标记，其预览节点命名为 `VisualPreview_NoIdle_FirstClip`，仅编辑预览使用表内首个片段。

## 调整步骤

1. 双击角色或怪物 Prefab，选择 `碰撞范围`，直接使用 Unity 原生 `CapsuleCollider2D` 的 **编辑碰撞器**、`Offset` 和 `Size` 调整同一份移动阻挡/受击范围。
2. 选择同级 `命中特效挂点`，用 Transform 的 Position X/Y 或场景移动手柄调整被弹丸命中时的特效播放点；挂点与碰撞范围互不影响。
3. 胶囊必须保持 `Vertical`、`Is Trigger`，且 `Size Y >= Size X`；不要增加 Rigidbody2D，也不要把 Physics2D 当运行时判定源。
4. 不要用根节点或碰撞节点缩放修改碰撞。根缩放只控制视觉；保存时工具按 `TbCombatRules.worldUnitsPerPixel` 将 Collider2D 的 Offset/Size 换算为像素。
5. 保存 Prefab，打开 **Roguelike > 战斗 > 碰撞配置 > 打开配置窗口**，点击 **导出全部预制体并生成配置**。角色表、怪物表、技能表和 Luban 只生成一次；未保存的 Prefab Stage 会拒绝导出。
6. 重新进入战斗。运行中不会读取正在编辑的 Prefab 参数。

Collider2D 编辑平面与战斗平面均为 XY。导出后 DOTS 使用等价的纵向胶囊数学进行移动扫掠、受击、近战、弹丸和范围命中；运行时不启用、不查询 Physics2D。

圆柱参数以用户保存的微调为准。首次缺失资源按预览帧尺寸初始化，仅作为待微调起点；武器层不初始化圆柱。

## 神将与武器映射

`TbCharacter` 只保留 6 个本体，ID 为 10001、10006、10011、10016、10021、10026。`TbHeroWeapon` 保存全部 30 条档位映射，原资源 ID 和 `TbVisualSet` 不变。

每位神将有 0～4 档武器：第 0 档已包含于 `*_0` 本体，1～4 档 `*_d` 是独立武器层。武器目录按本体名分组，组件的 WeaponConfigId 对应武器表。当前战斗继续使用已有第 0 档外观，本次不增加运行时换武器功能。

批量工具按配置归属处理武器，已有本体与怪物预制体保持不变。文件重命名后通过组件 ID 识别，不会因此覆盖或重复生成。

## 批量工具

统一入口为 `Roguelike > 战斗 > 碰撞配置 > 打开配置窗口`，每个组件顶部都会说明节点用途，底部提示统一导出路径。

- **生成缺失的角色/怪物碰撞预制体**：增量创建缺失项，已有 Prefab 和微调保持不变。
- **生成缺失的技能碰撞预制体**：按技能投递类型创建 `弹丸命中范围` 或 `范围技能命中范围`。
- **迁移测试关卡 Collider2D**：只迁移 TbStage 1 引用闭包；单位使用纵向胶囊，弹丸/目标位置范围技能使用圆形 Collider2D，近战不创建独立碰撞编辑器。
- **迁移全部角色怪物技能 Collider2D**：迁移全部角色、怪物及所有弹丸/目标位置范围技能；近战和未接入战斗的技能不创建无意义碰撞圆。
- **导出全部预制体并生成配置**：统一更新三张业务 Excel 和表现缩放，再调用一次生成脚本。日志为 `Library/CombatCollisionExport.log`。
- **刷新修改状态**：检查模板与生成 bytes 一致性，列出待导出资源；窗口获得焦点时也会刷新。

移动字段为 `moveRadiusPixelsMilli`、`moveHeightPixelsMilli`、`moveOffsetXPixelsMilli`、`moveOffsetYPixelsMilli`、`moveElevationPixelsMilli`；受击字段为 `bodyRadiusPixelsMilli`、`bodyOffsetXPixelsMilli`、`bodyOffsetYPixelsMilli`；命中特效挂点字段为 `hitEffectOffsetXPixelsMilli`、`hitEffectOffsetYPixelsMilli`。合并胶囊会把前两组字段写成同一几何，`collisionShape=VerticalCapsule`。全部存储“像素×1000”的整数。Prefab 是编辑源，表是运行时可审计结果；运行时只读 Luban，不读 Prefab。

## 技能碰撞

`Assets/Prefabs/Combat/Skill/` 按 `TbSkill.id` 保存技能编辑节点。弹丸使用 `弹丸命中范围`，目标位置范围技使用 `范围技能命中范围`。Inspector 直接填写半径、横向偏移和前向偏移像素；范围技能不显示无效偏移。

保存后执行统一导出。弹丸写入 `projectileRadiusPixelsMilli`、`projectileOffsetXPixelsMilli`、`projectileOffsetYPixelsMilli`；范围技写入 `areaRadiusPixelsMilli`。根节点缩放不再二次乘入碰撞，旧英文节点首次导出时会按当前可见大小一次性迁移为最终像素值。

所有已接入弹丸或目标位置范围投递的技能按实际资源拥有独立表现节点：`弹丸表现`、`命中特效表现`、`范围特效表现`。Position X/Y 控制表现偏移，Scale X/Y/Z 必须相同并控制显示大小；这些值写入 TbSkill 对应的 `*VisualOffset*PixelsMilli` 与 `*VisualScalePermille`，不会改变 CircleCollider2D 或 DOTS 命中范围。选中表现节点时，Prefab Stage 的内存预览会切换到该节点对应的正式 ANI 片段。

`TbSkillCombat` 不再持有弹丸半径，继续定义发射方式、间隔、射程、速度、寿命及近战前摇。同一战斗配置可供不同半径的技能共用。ECS 在发射时旋转碰撞与弹丸表现的局部偏移；几何接触后，命中特效使用目标 `命中特效挂点` 再叠加技能自己的 `命中特效表现` 偏移。

打开技能预制体时直接读取正式 ANI/PNG，在内存中显示首帧；关闭或域重载释放临时对象。不得保存独立预览纹理、材质、网格或其引用。**校验保存节点与配置** 菜单只读检查，不覆盖手动编辑。

技能预览在 Prefab Stage 初始化后延迟创建，将 ANI 的 XY 图像映射到碰撞使用的 XZ 编辑平面，并以正交俯视同时展示特效与命中圆；视角改变后点击 Inspector 的 **刷新技能表现并俯视命中范围** 恢复。该显示只存在于编辑器内存，不保存预览资产。

配置小数统一采用千分整数；既有万分比维持原单位，属性大值使用 `long`。修改源表后运行 `Tools/Config/generate.ps1`，再运行 `Tools/Config/validate.ps1` 确认生成代码及 bytes 可复现，重新进入战斗消费新快照。

## 移动行为

ECS 固定步进按空间网格查询相邻形状，全部角色和怪物按纵向胶囊进行连续扫掠与接触滑动。主角与怪物双向阻挡，不施加推力；攻击前摇期间怪物维持原位置。出生检查使用同一胶囊几何计算占位，整圈被占时延迟当前出生事件，不丢失波内数量。

这里只处理单位间移动阻挡，不引入 NavMesh、跳跃或障碍物路径规划。美术贴图仍可能相互遮挡，阻挡依据是脚底圆柱，而非整张图像矩形。

先 Unity Editor 验证，再由用户微调及人工验收；全部功能完成且用户明确要求后才构建 Win64。工具不保存 Scene、不提交或推送代码。

## 预览缓存与可复现性

- 强制禁止生成、落盘或提交独立预览资产；本节“缓存/重建”仅指内存中的临时绘制对象，不生成 Assets、Library 或其他目录下的预览文件。
- 新机器无需下载预览副本：配置生成物与源 PNG/ANI 就绪后，双击 Prefab 并开启 Scene Gizmos 即可显示首帧。
- 预览使用原 `VisualPreview` 节点变换，不改碰撞形状、节点位置或资源 GUID；Prefab 不保存缓存引用。
- 项目资源变更、Play 状态切换、程序集重载或退出编辑器时释放缓存；下一次查看自动重建。
- Project 面板缩略图不依赖持久化网格；请在 Prefab 视图查看外观。Gizmos 关闭时外观与碰撞辅助绘制隐藏。
