# 战斗反馈表现配置迁移方案

- 日期：2026-09-12
- 范围：头顶血条与伤害跳字的正式运行链
- 状态：方案已确认并进入实现；Unity 回归待占用窗口释放后执行

## 目标与边界

本阶段只迁移 `CombatFeedbackVisuals` 中属于策划/美术配置的资源引用、字形协议和跳字动画参数，不改战斗结算、碰撞、Prefab、Scene 或存档结构。运行时继续使用同一 DOTS World 和 Entities Graphics，不生成或保存预览资产。

## Tower 定向审查

按战斗、UIKit/Window、TableData/Generate/ExcelTools 范围检查 Tower 参考工程约定目录，本机当前不可访问对应目录，因此没有可验证的同类入口、资源所有权或配置读取实现可采用。后续实现依据当前项目的 Luban、YooAsset、Entities 1.0 与资源释放约束完成，不以缺少参考工程为由跳过配置和测试。

## 采用、调整与拒绝

| 分类 | 决策 |
|---|---|
| 采用 | `TbCombatPresentation` 作为反馈表现唯一配置入口；资源继续通过 `IResourceService` 加载，运行时副本由 `CombatFeedbackVisuals.Dispose` 释放 |
| 调整 | 血条/数字图集不再假定固定尺寸；按正式纹理真实尺寸合并内存图集，数字单元宽度由图集宽度除以配置字形数得到 |
| 调整 | 跳字时长、上升像素、起止缩放、字距、字形缩放在入局时换算并缓存，逐帧路径不重复解析配置 |
| 拒绝 | 不保留资源 ID、纹理尺寸、字形顺序或动画参数的代码默认值；缺失或非法配置直接拒绝入局 |
| 拒绝 | 不新增 ScriptableObject、Prefab/Scene 字段或预览纹理来维护第二套表现配置 |

## 表结构与数据流

`TbCombatPresentation` 新增以下字段：

| 字段组 | 字段 | 约束 |
|---|---|---|
| 资源 | `healthBarFrameResourceId`、`healthBarFillResourceId`、`damageNumberAtlasResourceId` | 必须解析到 `TbResource` 的 Texture 行，路径非空 |
| 字形 | `damageNumberGlyphs` | 字符唯一，必须覆盖 `0`–`9`；数字图集宽度必须能整除字符数 |
| 动画 | `damageNumberRiseDurationMilli`、`damageNumberRiseHeightPixels` | 时长大于零；高度有限且非负 |
| 布局 | `damageNumberScaleStartPermille`、`damageNumberScaleEndPermille`、`damageNumberKerningPermille`、`damageNumberGlyphScalePermille` | 换算后有限且大于零 |

数据流：Excel 源表 → Luban 生成 C# 与 bytes → `CombatRunDefinition/PerformanceScenario` 解析引用 → `CombatFeedbackVisuals` 校验并缓存 → Entities Graphics 渲染。

## 技术常量边界

保留渲染层级、隐藏坐标、Shader 属性名、透明混合枚举、UV 半像素收边和材质/网格索引等技术协议常量。这些值不表达玩法规则或美术调参，不进入 Luban。

## 兼容影响与验收

- `tbcombatpresentation.bytes` 布局发生变化，Excel、生成 C# 与 bytes 必须同批发布。
- 不修改 Prefab、Scene、碰撞和存档；不新增预览资产。
- 非 Unity 验收：Luban 可重复生成且生成物稳定；源表保留检查通过；配置语义校验通过；Rendering 与 PlayMode 测试程序集编译为 0 警告、0 错误；硬编码扫描无旧资源 ID/尺寸/动画值。
- Unity 验收：EditMode 全量、PlayMode 定向正式关卡、真实 YooAsset 纹理加载、血条与金色伤害数字像素证据全部通过。
