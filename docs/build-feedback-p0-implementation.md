# 磐岳／周天选卡、跨局目标与反馈 P0 实施记录

依据用户确认的 V0.1 设计，三块一起实施。新增数值全部是可配置的测试初值，尚未完成平衡验收。

## 交付范围

| P0 条目 | 实际实现与边界 | 主要证据 |
|---|---|---|
| 普通升级修正 | 每局重掷／跳过由表分配；新候选优先、部分替换报告数量；无新项不扣费；旧 offerId、同帧重复回调拒绝；空池沿免费继续；炼器仍固定三分支 | `CombatRunDraft.cs`、`CombatDraftControlTests.cs` |
| 资格与权重 | 角色、模式、内容锁、前置、互斥、已选、等级／槽位先过滤；独立确定性 UpgradeChoice 流；关联、连续未出现救济、倍率上限由表读取；出现协同即清零、重掷不累加、实际成形后关闭救济 | `CombatRunDraftDefinition.cs`、`TbDraftPolicy`、`TbUpgradeRule` |
| 磐岳破围 | 单级、不占武器／被动槽；有效敌方受击链触发的反击范围按表扩大，不额外伤害；新增范围实际扣血才算收益／成形；环境、自伤、递归来源不能刷修饰收益或挑战；原有离散蓄能／铁壁保持 | `CombatGuardState.cs`、`CombatRunFeedback.cs`、`CombatBuildFeedbackTests.cs` |
| 周天交织 | 有效重叠引爆后按表提高移速、刷新不叠加；暂停不耗时；固定 tick 截止保证批量追帧不延长；保留留阵参数和消耗全部参与阵；BossClear、过期、移除与引爆分别处理 | `CombatTribulationDomains.cs`、`CombatSessionFeedback.cs`、`CombatTickJob.cs` |
| 战斗统计 | 唯一事件／攻击／归属／目标标识；按实际扣血、剩余生命封顶；直接武器、反击、脉冲、引爆独立归账；修饰不重复领伤害；承伤独立；有效模拟时钟用于 DPS | `CombatFeedbackEvent.cs`、`CombatBuildFeedback.cs`、ECS 会话／TickJob |
| HUD 与结算 | 复用现有表现；小型机制状态；选卡余量与替换数；结算主要来源、职业贡献、目标差距、可展开明细、保存状态与失败重试；按表节流，不逐敌人刷新 UI | `CombatBuildFeedbackUi.cs`、正式 `BuildFeedbackPanel.prefab` |
| 两个内容目标 | 磐岳单次反击不同有效目标数＋同局胜利；周天若干次达到不同有效目标阈值的引爆＋同局胜利；失败只保留最佳，不跨局拼出胜利目标；基础机制可以完成 | `TbContentGoal`、`BuildFeedbackProfileTests.cs` |
| 内容启用 | 只解锁新增平行玩法修饰；原角色／形态／基础卡不重新上锁；解锁后默认不自动启用；入口可选择，单局启用快照固定 | `TribulationEntryFeedback.cs`、PlayMode 真实入口用例 |
| Profile | 显式目录、版本、解锁／启用 ID、最佳进度、稳定 runId＋settlementId；独占锁后重读，临时刷盘并原子替换，处理标记与解锁同事务；失败内存不提前确认且可重试；已知旧封套迁移，损坏／未来版本拒绝 | `BuildFeedbackProfileStore.cs`、`Tools/ProfileProbe` |
| 观测 | 合法池规模、候选、操作、选中 ID、资源与随机位置；首张修饰／首次有效收益时间；Boss 阶段首次到达、死亡直接来源 | `CombatDraftDecision`、`CombatBuildFeedback` |

普通升级与炼器保持分开；磐岳武器槽与周天武器槽差异、炼器转向保级、五级成长／进化沿用原实现。周天 50002 缺少 41002 升级项未擅自补卡。没有实现放逐、锁定、完整 RunSave、货币、装备或永久属性树。

## 配置与资产

正式源头仍为 Excel → Luban C#／bytes → YooAsset → `IConfigService.Tables` → `CombatRunDefinition`。

- 新表：`s_抽选策略_DraftPolicyConfig`（101／102）、`s_选卡资格_UpgradeRuleConfig`、`j_职业修饰_MechanismCardConfig`（61001／61002）、`j_内容目标_ContentGoalConfig`（62001／62002）、`z_构筑反馈_BuildFeedbackUiConfig`（1）。
- 扩展：`__tables__` 注册、`__enums__` 的 MechanismModifier、`UpgradeOption` 42001／42002、普通池 2／102 引用、正式资源 970002；对应生成类、Tables 和 bytes 同步。
- 重掷／跳过、关联与救济参数、职业修饰倍率／时长、目标阈值、刷新间隔、持久化文件协议、玩家文本均消费正式表；无运行时业务默认副本。
- `weaponRange` 标签明确表示现有武器范围被动的收益，不能据此声称它扩大本命职业反击／留阵几何范围。
- 新增正式反馈 Prefab，并给既有选角 Prefab 增加目标与开关绑定；没有新增 Scene、预览纹理、预览材质或预览网格。未修改已有技能效果绑定。

可在新机器从正式源重现：

```powershell
pwsh -NoProfile -File Tools/Config/generate.ps1
pwsh -NoProfile -File Tools/Config/validate.ps1
pwsh -NoProfile -File Tools/Config/validate-combat.ps1
dotnet build Tools/ProfileProbe/ProfileProbe.csproj -o .local/build-feedback-p0-20261027/profile-probe
```

## 实际验证结果

全部 Unity 操作经核验为本项目的 8082 MCP；未使用桌面点击、其他工程实例或 Win64 构建。

| 验证 | 最终结果 |
|---|---|
| Luban 生成及再生成一致性 | 通过；生成物与 Excel 源一致 |
| 配置／运行规则／动画检查 | 29,005／42／1,945 项通过 |
| 配置检查工具与 ProfileProbe 编译 | 0 错误、0 警告 |
| 普通全量 EditMode | 300 项：297 通过、0 失败、3 项 Explicit 跳过；job `537e3a8b744c467fa44fd25763a9138a` |
| 普通全量 PlayMode | 16 项：11 通过、0 失败、5 项 Explicit 跳过；job `06f1161ec3594168ae01532053784937` |
| P0 专项 | 包含双角色确定性选卡、无效重掷不扣费、部分替换、内容快照、资格／救济、伤害去重与过量、环境奖励排除、破围额外目标、交织有效命中／暂停／到期／批量追帧、Boss 清阵取消未提交引爆 |
| Profile 专项 | 双角色目标、同局胜利约束、重复提交、占用导致保存失败后重试、旧版本迁移、损坏／未来版本拒绝、两个实际独立进程退出与重开读取解锁／启用 |
| 真实 UI 专项 | YooAsset 正式资源、EventSystem 射线点击重掷／跳过、受控职业命中与 Boss 结算、隔离 Profile 保存、返回入口启用、句柄与 World 清理均通过 |

首次失败记录保留在本地验证目录：早期 UI 失败包含测试相机／清理夹具问题，以及本功能画布与现有 Overlay HUD 不兼容、结算首次刷新被节流导致延迟保存；均已修正并复测。首轮全量 EditMode 的 7 项失败来自两个直接构造 TickJob 的既有测试夹具缺少新增 NativeList，已补充 using 生命周期，原移动断言未削弱；后续全量无失败。

截图：`outputs/combat-config/runtime-review/p0-draft-controls.png`、`p0-build-summary.png`。均为本次真实 UI 回归截屏，非正式美术预览资产。UI 用例受控设置目标位置、蓄能与 Boss 时间／生命，只证明边界集成，不冒充自然四分钟通关或数值可达性结论。

详细测试响应、生成／校验日志、开工备份与保护检查位于忽略目录 `.local/build-feedback-p0-20261027/`。真实玩家存档没有用于测试；测试仅使用临时根 `roguelike-feedback-profile-tests` 的唯一隔离子目录，ProfileProbe 拒绝其他目录。

## 并行保护与参考限制

开始前备份已有修改；最终核对原 CombatHud、CombatUiRuntime、原 UI 构建器、RenderCatalog、EntityVisuals、VisualResources、SkillConfig 与 EAnimationAction 和开工备份一致。共享 ECS／Run 文件只合并本功能必需变更；未提交、推送、重置或切换分支。工作区仍包含用户并行修改，不能把整个 Git diff 归为本任务。

Tower 相关 Battle／UIKit／Window／TableData／Generate／ExcelTools 参考读取被执行环境文件访问规则拒绝；没有绕过或重试无关路径。按项目规范记录不可访问后沿本项目职责边界实现：Run 拥有决策、ECS 发有效事件、协调器消费职业结算、UI 拥有资源与取消、Profile 拥有原子存储。拒绝迁入 HotFix、网络、全局单例及第二套配置真值。

## 尚未覆盖与人工验收

- 用户人工验收尚未进行；P0 是“已实现且自动化通过”，不等于全部功能最终验收完成。
- 没有退出／重启当前 Unity 或打包 Player；跨进程持久化已用同一存储实现和正式 bytes 的独立 .NET 进程验证，实际发行包启动／退出尚未验证。
- 未做断电／硬杀进程的故障注入；原子替换、失败重试和幂等提交已有测试，不能承诺所有硬件／文件系统故障情形。
- 两职业自然战斗下的目标可达性、选卡收益和建议初值仍待体验与平衡测试。
- Explicit 压力与性能专项本轮没有运行。已有性能门禁仍为 4/6，2000 档未通过；本轮功能通过不代表该性能问题修复。
- 完整局中续玩明确后置；退出中途战斗不恢复选卡或战斗快照。

核查分支 `main`，HEAD `444ce91475ec714588e94ebb079a151ef83704b5` 未改变。最终 MCP 清场记录：项目路径正确、非 Play、未编译、SampleScene 未脏、运行器／入口／World 均为 0。以上为用户持续并行开发中的非原子工作区快照，后续变动须重新核对。
后续用户追加的全战斗地图边界显示与统一设置接入点，见 [通用边界说明](arena-boundary-visibility-20261027.md)；该需求在 P0 验收后单独实施，最新合并回归结果记录于该说明。
