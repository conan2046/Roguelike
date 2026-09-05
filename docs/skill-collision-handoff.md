# 技能碰撞配置与整数协议交接

## 功能与数据

- 配置小数统一以千分整数存储，属性大值使用 long，既有万分比保持原单位；Generated/Extensions/ConfigNumber.cs 负责还原，不维护默认值副本。
- TbSkill 独立保存弹丸半径及局部右向、前向偏移；ECS 发射时按方向旋转偏移，扫掠使用碰撞中心，表现原点保持不变。
- 93 个正式技能 Prefab 位于 Assets/Prefabs/Combat/Skill。技能 20001 当前半径 0.7、右向偏移 0.513、前向偏移 0，均来自用户保存节点与源表。
- 技能外观只在编辑器内存中显示，域重载后恢复，切换或关闭时释放；无独立预览资产文件。原始 ANI 映射到 XZ 编辑平面，采用俯视辅助编辑。
- 已确认的 TbSkillCombat 2 射程由 0.1 改为 0.41，以匹配已调整的单位阻挡范围；玩家默认技能仍为远程 20001。
- 修复退出时 World 已提前销毁而访问失效 EntityManager 或重复 Dispose；PlayMode 测试隔离自动入口资源所有权。

## 验证记录

- 本次提交前重新执行 pwsh -NoProfile -File Tools/Config/validate.ps1：通过，Excel 与生成代码、bytes 一致。
- 本次提交前重新执行 pwsh -NoProfile -File Tools/Config/validate-combat.ps1：2756 项通过，含运行核心 42 项、动画检查 1934 项。
- 本次通过 Unity 检查角色/怪物及 93 个技能保存节点与 bytes 一致；编译已完成，控制台无错误。
- 另一对话的 EditMode 记录：83 通过、1 失败、1 显式负载测试跳过。唯一失败 MeleeAttackStartsAtContactFromEveryDirection 在上述射程修正后专项复测 1/1 通过，不表述为最终全量重跑。
- PlayMode 2/2 通过：加载中取消、真实资源、输入、暂停、重开、死亡、重复退出及 World 提前销毁。
- 技能预制体重载恢复、切换与关闭清理已验证，Prefab Stage 无脏标记；相关日志、XML、复测回执和截图保留在本地 outputs/skill-config-validation。

## 边界

不构建 Win64，不扩展弹丸正式美术表现、元素、Boss 数值或千怪性能验收。原有 Scene、项目/URP 设置、旧配置加载脚本及临时测试场景不纳入本次提交，保留在工作区。
