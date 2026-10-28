# 测试技能效果引用更新（2026-10-27）

后续进展：用户授权后，已另行完成 [随身施法效果正式接入](caster-effect-runtime-integration.md)。下文保留本次引用替换的交付边界与当时验证记录。

依据本次 `annotations.json / Groups` 的整组“使用”决定、技能类型、元素、备注及飞行朝向，更新现有测试战斗的正式效果引用。玩法技能 ID 与资源组编号是不同概念，例如玩法 `20050 / 九霄雷劫` 仍使用已审核的雷系资源 `skill_43`，不能按编号直接替换成弹丸资源 `skill_50`。

## 当前引用

| 玩法技能 ID | 名称 | 当前效果资源组 | 正式片段 ID | 本次处理 |
|---|---|---|---|---|
| 20001 | 追星飞刃 | skill_1 | f 30002、h 30003 | 保留 |
| 20014 | 焚天炎符 | skill_14 | f 30026、h 30027 | 保留 |
| 20028 | 落雷法印 | skill_28 | area 30045 | 保留 |
| 20033 | 寒霜灵阵 | skill_37 | area 30055 | 保留，已是冰系持续区域 |
| 20043 | 护体罡环 | skill_66 | area 30097 | 从 skill_43 的 30063/30064/30065 改为已审核护环 |
| 20016 | 连锁灵梭 | skill_32 | f 30051、h 30052 | 替换已标“不用”的 skill_16 |
| 20044 | 万象星羽 | skill_44 | f 30067、h 30068 | 保留，补齐正式飞行朝向 |
| 20027 | 焚天龙符 | skill_27 | f 30042、h 30043 | 保留，补齐正式飞行朝向；审核类型与元素仍待用户填写 |
| 20050 | 九霄雷劫 | skill_43 | area 30063 | 保留，已是雷系落点爆发 |
| 20007 | 玄冰天阵 | skill_37 | area 30055 | 保留，未引用标“不用”的 skill_7 |
| 20037 | 不灭罡域 | skill_66 | area 30097 | 从雷系片段 30065 改为已审核护环 |
| 20030 | 御龙连梭 | skill_50 | f 30077、h 30078 | 替换已标“不用”的 skill_30 |

上述资源组全部明确标记“使用”。`skill_33` 是风系移动范围，不替换固定冰阵；`skill_203` 画面为塔形，不用于护环。基础与进化护体共用同一已审核护环资源，本轮不新增进化专属素材。

## 源表与生成物

- `j_技能_SkillConfig.xlsx / TbSkill`：只修改 20016、20030 的 projectileClipId/impactClipId，以及 20037、20043 的 areaClipIds，共六个单元格。
- `d_动画片段_AnimationClipConfig.xlsx / TbAnimationClip`：30042、30051、30066、30067、30077 的 flipX 改为 true；30002 保留原有 true。30077 明确为循环 Projectile，30078 为 Impact，30097 为 Area。
- `z_表现层级_CombatRenderConfig.xlsx / data`：绑定 1006/1007/1011/1012/1016/1017 指向新片段；护环队列分别为 3000/3001。清空退役绑定 1018/1019，移除原雷击光柱层。
- `z_战斗表现_CombatPresentationConfig.xlsx`：renderConfigIds 移除 1018/1019。
- 通过 `Tools/Config/generate.ps1` 更新四份 bytes：tbanimationclip、tbskill、tbcombatrender、tbcombatpresentation；未改变表结构及生成 C#。

源表沿用原始工作簿容器和单元格样式，逐项验证计划外单元格及其他包成员未改变。审核决定、类型、元素、备注逐组对比无改动。没有新增纹理、网格、材质或其他预览资产。

## 朝向记录兼容

旧版额外翻转在正式 flipX 修改前迁移为最终朝向。审核窗口改为“正式配置 / 水平镜像 / 原始方向”，组合与分段播放一致；选择最终镜像后，正式 flipX 同步为 true 仍只镜像一次。旧字段保留用于留档，后续使用 ProjectileFacingMode。

## 范围与参考

采用现有 CombatVisualResources 的显式片段加载与资源释放、CombatRenderCatalog 的技能片段归属检查、CombatEntityVisuals 的现有飞行/命中/区域播放。调整数据引用、队列及审核朝向兼容；拒绝按玩法 ID 猜资源组或把移动龙卷风用于固定冰阵。既有 Tower 同类参考路径不可访问，沿用本工程加载链，不引入外部资源框架。

玩法数值、投射速度、命中判定、持续时间、技能配置档及进化规则均未修改。审核工具中 `_s` 跟随施法者的演示保留；本次没有新增正式战斗的 `_s` 随身施法层字段或播放逻辑，也不将审核的“两秒飞行演示”写成实际技能时长。

## 验证

- Luban 生成成功；validate.ps1 复生成比对通过，Excel 与生成物一致。
- validate-combat.ps1 通过：配置检查 28,983 项、动画检查 1,945 项、运行规则检查 42 项。旧“仅 skill_1 翻转”断言更新为本次已审核飞行片段集合。
- Unity MCP EditMode：CombatRenderLayerTests、CombatAnimationTests、SkillEffectReviewTests、SkillEffectReviewSequenceTests 共 43 项通过，覆盖真实资源预载、队列/材质隔离、失败释放及最终朝向不重复翻转。
- Unity MCP PlayMode：RealResourcesInputCameraAndRepeatedExit 通过，覆盖真实 YooAsset 加载、移动自动攻击、相机、暂停、重开与退出清理。该测试不代表逐一人工验收全部十二个技能画面。
- Unity 内重新读取当前 Luban 数据，核对上述十二个技能及六个镜像片段；编译无错误。

原始输出、源表备份及逐单元格计划保存在 `.local/skill-reference-update-20261027/`。待用户在测试战斗中确认整体视觉；未提交、推送或构建 Win64。
