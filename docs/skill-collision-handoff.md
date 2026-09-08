# 技能碰撞配置与整数协议交接

## 功能与数据

- 配置小数统一以千分整数存储，属性大值使用 long，既有万分比保持原单位；Generated/Extensions/ConfigNumber.cs 负责还原，不维护默认值副本。
- TbSkill 独立保存弹丸半径及碰撞偏移，并分别保存弹丸、命中、范围三类表现的偏移与相对缩放；ECS 入局时按 `worldUnitsPerPixel` 转换，碰撞和视觉互不反向修改。
- 正式技能 Prefab 位于 Assets/Prefabs/Combat/Skill。节点按用途命名为 `弹丸命中范围` 或 `范围技能命中范围`，Inspector 参数和说明均为中文。
- 全部已接入弹丸或目标位置范围投递的技能按实际资源补齐 `弹丸表现`、`命中特效表现`、`范围特效表现`；选择节点后以 Transform Position X/Y 调位置、统一 Scale 调大小，内存预览自动切换对应 ANI。
- 技能 20001 已将旧根缩放的可见几何一次性换算为最终像素值；当前保存的 Prefab 为唯一编辑源，根缩放不再二次乘入碰撞。
- 技能外观只在编辑器内存中显示，域重载后恢复，切换或关闭时释放；无独立预览资产文件。原始 ANI 映射到 XZ 编辑平面，采用俯视辅助编辑。
- 已确认的 TbSkillCombat 2 射程由 0.1 改为 0.41，以匹配已调整的单位阻挡范围；玩家默认技能仍为远程 20001。
- 修复退出时 World 已提前销毁而访问失效 EntityManager 或重复 Dispose；PlayMode 测试隔离自动入口资源所有权。

## 验证记录

- 2026-09-08：全量迁移 6 个角色、79 个怪物及 5 个弹丸/目标位置范围技能；85/85 单位使用统一胶囊且均有命中特效挂点，5/5 技能碰撞及表现节点完整。Luban 可复现通过，战斗配置检查 3405 项通过；Unity EditMode 124/124 通过（2 项显式压力测试跳过），PlayMode 5/5 通过。

- 2026-09-08：命中特效挂点与三类表现节点试用迁移成功；Luban 可复现与战斗配置检查通过 3170 项；相关 EditMode 普通回归 51/51、PlayMode 4/4 通过。

- 2026-09-08：统一 Prefab→Luban 导出成功；配置检查 3149 项通过，CombatSession EditMode 30/30 通过。

- 本次提交前重新执行 pwsh -NoProfile -File Tools/Config/validate.ps1：通过，Excel 与生成代码、bytes 一致。
- 本次提交前重新执行 pwsh -NoProfile -File Tools/Config/validate-combat.ps1：2756 项通过，含运行核心 42 项、动画检查 1934 项。
- 本次通过 Unity 检查角色/怪物及 93 个技能保存节点与 bytes 一致；编译已完成，控制台无错误。
- 另一对话的 EditMode 记录：83 通过、1 失败、1 显式负载测试跳过。唯一失败 MeleeAttackStartsAtContactFromEveryDirection 在上述射程修正后专项复测 1/1 通过，不表述为最终全量重跑。
- PlayMode 2/2 通过：加载中取消、真实资源、输入、暂停、重开、死亡、重复退出及 World 提前销毁。
- 技能预制体重载恢复、切换与关闭清理已验证，Prefab Stage 无脏标记；相关日志、XML、复测回执和截图保留在本地 outputs/skill-config-validation。

## 边界

不构建 Win64，不扩展弹丸正式美术表现、元素、Boss 数值或千怪性能验收。原有 Scene、项目/URP 设置、旧配置加载脚本及临时测试场景不纳入本次提交，保留在工作区。
