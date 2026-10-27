# 模型与技能片段独立层级交付记录

2026-10-26。用户已确认逐模型、逐技能片段配置，并保留 Prefab Inspector 编辑入口。

## 结果

- 新增 `TbCombatRender`，目前配置 110 个模型和 22 个实际战斗技能片段；每条队列独立可调。模型初值为 3005，不再由统一模型字段强制覆盖。
- 护体罡环 20043 的落剑 30063 为 3015（模型前），蓝色光柱 30064 为 3002（模型后），地面阵纹 30065 为 3000（模型后）。其他技能的飞行、命中及 Area 片段也分别绑定。
- 同一纹理的不同绑定可使用不同队列；同纹理同队列继续复用材质。模型待机、移动、攻击和白闪读取同一模型绑定。血条和数字仍为原有反馈队列 3020。
- 角色/怪物碰撞节点、技能碰撞节点和技能表现节点的 Inspector 可编辑层级；独立武器标识也显示其模型绑定。数值越大越靠前，保存按钮只写层级源表并运行 Luban，不写组件或 Prefab，不改变 Transform。
- 缺失、重复、错误归属、错误片段和非法队列均明确拒绝；旧 `unitRenderQueue` / `skillRenderQueue` 保留为遗留字段，已不参与该运行链或缺值回退。

## 数据与代码

源表及注册：

- `Config/Datas/Tables/z_表现层级_CombatRenderConfig.xlsx`：模型行 1–110，技能片段行 1001–1022。
- `Config/Datas/__tables__.xlsx`：新增 `TbCombatRender` 注册。
- `Config/Datas/Tables/z_战斗表现_CombatPresentationConfig.xlsx`：新增 `renderConfigIds` 强引用列表。
- Luban 新增 `CombatRenderConfig.cs`、`TbCombatRender.cs`、`tbcombatrender.bytes`，同步更新 `Tables.cs`、`CombatPresentationConfig.cs`、`tbcombatpresentation.bytes` 及 Unity 元信息。

运行链为 Excel → Luban → 表现方案绑定目录 → `CombatRenderCatalog` → `CombatVisualResources` 逐片段材质队列。层级只在加载时查询，不增加每实体材质或每帧资源查找。共享资源所有权、取消令牌、实体池与退出回收沿用现有代码。

编辑器新增 `CombatRenderInspector`、`HeroWeaponEditor`，并接入既有模型/技能 Inspector；现有只驻留内存的编辑外观也读取正式绑定。尚未配置正式投递的旧技能素材仍保持原有只读查看兼容分支，不作为正式战斗层级回退。

## 验证

| 项目 | 实际结果 |
|---|---|
| 三张相关源表逐格检查 | 1744 个单元格通过；原有两张工作簿除工作表 XML 外的 ZIP 内容不变 |
| Luban | 生成成功；独立重新生成与源 Excel 一致 |
| 战斗配置校验 | 28985 项通过；另有运行时 42 项、动画 1945 项通过 |
| Unity EditMode | 40/40 通过，覆盖新层级、真实 ANI 材质、原表现回归、隔离导出和冲突拒绝 |
| Unity PlayMode | 原生测试 XML 4/4 通过，覆盖真实资源/镜头/重复退出、实际半透明白闪、借劫加载取消与失败重试 |
| Inspector | 实际查看技能三片段独立控件及模型队列控件，名称与数值可读 |
| Inspector 保存处理流程 | 将光柱从 3002 临时导出为 3003，经 Excel / Luban 到实际 bytes，再走同一处理流程恢复 3002；恢复 bytes 完全一致，Prefab 未脏。此项调用的是按钮处理方法，不冒充鼠标点击证据 |
| 实际战斗 | 通过 EventSystem 射线选角进入两个职业；实际材质层级和画面正确，磐岳角色在阵纹/光柱之前，飞刃和命中仍在前景 |
| 保护用户资产 | 422 个既有战斗 Prefab、Scene 及附属文件哈希不变 |
| 最终状态 | Rouge（8082）仍在 Play，选角页两个按钮可用；6 个基线 World、1 个 EventSystem，Scene 未脏、未编译、Console 错误 0 |

PlayMode 的 MCP 作业状态曾报告初始化超时；同次原生 `TestResults.xml` 的开始/结束时间为 2026-10-26 15:17:47Z / 15:17:58Z，四项均 Passed。保留原生 XML 作为实际结果，没有重复运行已通过测试。

截图与完整证据保存在 `.local/combat-render-layer-20261026/`：

- `stage-101-layered.png`：护体罡环三个片段、角色及前景飞刃/命中。
- `stage-102-layered.png`：后景冰阵与前景炎符。
- `editmode-result.json`、`native-play-results.xml`、`source-verification.json`、`final-diff-audit.json`。

## 边界与未决项

等待用户对整体层级和各技能遮挡强度进行人工试玩验收。未改变伤害、投递、碰撞、模型尺寸、镜头或构筑；未扩展按 Y 坐标排序。不同队列需要不同共享材质，当前已验证纹理仍共享、队列互不串改；本轮不宣称重新完成千怪性能门禁。

没有保存独立预览资产、修改 MCP 配置、构建 Win64、提交或推送。
