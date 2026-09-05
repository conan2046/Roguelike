# Luban 配置数据

## 目录

- `Datas/*.xlsx`：Luban 表、结构及枚举定义。
- `Datas/Tables/*.xlsx`：业务源数据，Excel 是唯一数据源。
- `luban.conf`：Luban v4.11.0 生成配置。

## 命名规范

- 业务工作簿：`拼音首字母_中文名_英文名.xlsx`，例如 `g_怪物_MonsterConfig.xlsx`。
- 文件名用于策划识别和目录排序；Luban 打表以工作簿内的工作表名及 `__tables__.xlsx` 定义为准。
- 重命名工作簿时必须同步修改 `__tables__.xlsx` 的 `input` 路径，禁止修改工作表名来迁就文件名。
- 所有工作表的有效区域统一使用水平、垂直居中显示。
- `id` 是表内唯一标识及跨表引用依据；禁止再增加含义重复的通用 `key` 字段。

## 源表保护

- `Tools/Config/generate.ps1` 与 `Tools/Config/generate.sh` 只读取 Excel，禁止反向修改或重建源表。
- 策划手工修改过的 Excel 属于权威数据，任何导入、迁移或批量重建脚本不得覆盖，除非修改人明确要求。
- 自动化修改表格前必须确认文件未被 Excel 占用，并与最近已知版本核对差异。

## 当前表层级

- L0：`TbResource`、`TbAttribute`、`TbItem`
- L1：`TbAnimationClip`、`TbVisualSet`、`TbMap`
- L2：`TbCharacter`、`TbMonster`、`TbSkill`
- L3 预留：`TbStage`
- 全局常量：`GameConfig`
- 测试：`TbPerformanceScenario`

### 战斗基础配置

战斗公式与属性采用《战斗公式及属性调整V1.0》的非元素部分；配置、属性/伤害模块及独立 ECS 会话已实现。ECS 会话已接收真实表配置并运行隔离模拟，但尚未接入应用启动、输入界面及动画表现。

| 表 | 当前配置 | 用途 |
|---|---|---|
| TbAttribute | ID 1–14 | 生命、攻击、防御、移速、攻速以及命中、闪避、暴击、抗暴和对应参数 |
| TbAttributeProfile | ID 1–4 | 玩家/怪物闭环调试、玩家/怪物概率压测 |
| TbSkillCombat | ID 1–2 | 直线弹丸、近战攻击参数 |
| TbCombatRules | ID 1 | 随机 80%–120%、暴击 150%、零分母概率、模拟与坐标设置 |
| TbCharacter / TbMonster | 各启用 ID 10001 | 关联属性方案、默认技能和碰撞体半径 |
| TbSkill | 启用 ID 20001、20002 | 分别关联弹丸和近战机制，原表现引用保持不变 |
| TbPerformanceScenario | 新增 ID 4、5 | 10 怪基础闭环、1000 怪概率战斗压测；旧 ID 1–3 原值保留 |

`TbAttributeProfile.values` 使用 `属性ID:数值;属性ID:数值`，由 `AttributeValue` 结构解析；必需属性完整填写，不保留代码默认值。其余角色、怪物和技能条目的战斗字段为空，表示仅有资源目录数据，不能作为战斗单位使用。技能视觉用途与完整角色组装仍需在运行时阶段验收。

新增 `EAttributeType`、`EAttributeValueKind`、`ESkillDeliveryType`、`ETestHealthPolicy`、`ETestInputPolicy`，以及 `EPerformanceKind.Combat`。未增加元素属性或元素伤害分支。

初始调试参数：玩家生命/攻击为 100/20，怪物为 40/8；默认必中且不暴击。概率压测方案使用命中/闪避 100/25、暴击/抗暴 25/100，各参数为 1，即 80% 命中、20% 暴击。所有这些值都是工程调试输入，不是最终平衡数值。

场景 4 使用正常生命和手动输入，场景 5 使用显式的扣血后恢复生命策略、静止输入和死亡补怪，便于维持采样。该策略不能作为正常玩法或生存平衡结果。旧移动运行器遇到 Combat 类型会明确失败，不能把新场景报告为移动压测通过。

修改表后运行以下检查；战斗校验需 .NET 8 SDK，无额外 NuGet 包或 Excel/Python 依赖：

```powershell
./Tools/Config/generate.ps1
./Tools/Config/validate.ps1
./Tools/Config/validate-combat.ps1
```

最后一项直接编译当前生成 C#、`Assets/Scripts/Features/Combat` 正式源码和共享 ANI 解析器，读取当前 bytes，验证属性完整性、引用、数值范围、投递类型、场地与压力策略；通过独立 decimal 参考结果核对正式伤害公式，并测试来源替换/移除、生命上限、冷却、免伤顺序、生命周期与独立随机分布。当前通过 2453 项检查，其中战斗运行时断言 42 项、动画断言 1916 项，另执行 100000 次概率抽样。动画检查读取主角/怪物待机和移动四个实际片段，并检查全部角色/怪物集合的类别映射、时长兼容、边界、循环、末帧保持及采样分配。这不证明 ECS 战斗循环、画面播放、Burst 或千怪战斗性能通过。

## 战斗表现配置

- `TbAnimationDirection`：方向 ID、向量、ANI 动作索引与水平镜像，映射来自既有 `CocosModelAniPlayer.FaceActionIndex/FaceFlipped`，不在新运行时代码中复制方向分支。
- `TbCombatPresentation`：源时钟、时长兼容规则、出生方向、方向列表、怪物静止策略、输入轴/按键、界面文本/尺寸和相机/诊断弹丸颜色。规则来自既有播放器及本轮确认的基础表现范围。
- `TbPerformanceScenario.presentationId`：Combat 场景 4/5 引用方案 1，旧场景 1–3 留空；新增字段改变 bytes 布局，生成 C# 和 bytes 必须一起发布。
- `CombatAnimation` 加载时构造累计时长，以调用方提供的模拟秒数采样；`CombatDirections` 校验并归一化方向向量，静止保留历史朝向。`idleFrameIndex` 指动作内帧索引，不是 ANI 全局帧索引。
- 最终确认：主角默认待机 `_fd`，怪物默认待机 `_zd`，两者移动均 `_pb`。此前“怪物没有站立片段”“fd 仅乘骑”“主角改用两方向 zd”的判断均不再适用；已补齐怪物待机引用并关闭移动帧占位。
- 主角 `_zd` 及角色/怪物 `_gj`、`_bj`、`_sf1`–`_sf4` 标记 Unused，不接入本轮播放。施法不暂停移动，也不要求施法动作。死亡分类保留；不实现乘骑系统。所有源资源、ID 和集合成员保留，不删除美术文件。未识别后缀仍保留 Unknown。
- 当前四个启用片段均支持已有五动作组方向映射；已撤销主角两方向适配需求。未来新增片段仍须按其真实动作结构校验，不能仅凭同类文件推断。
- 怪物普攻另由 `TbVisualSet.attackClipId` 显式启用；目前只有已目视核验的 btm1（表现 20001、攻击片段 20002）启用，其余包括主角留空。该怪物的 `_gj` 恢复 Attack 分类，不再属于 Unused。
- `TbAttackDirection` 保存攻击动作组和 `facingDirectionId`，后者引用 `TbAnimationDirection` 的向量、镜像以及待机/移动动作组。表现表 `attackDirectionIds` 决定候选与等角优先顺序。右下/左下用攻击组 0、对齐组 0；右上/左上用攻击组 1、对齐组 3，左侧镜像。
- `CombatAttackDirections` 在加载时校验三份真实动画的组号，开始攻击时按最大点积选择方向；零相对位移保留传入索引，等角按配置列表先后。调用方应锁定本次选择，不在攻击中逐帧重选。本轮未接 ECS 攻击阶段、播放状态机或命中时点。
- 当前数据与纯采样逻辑已具备；输入、相机、UI 和实体渲染尚未消费这些新字段，不能据此认为交互战斗入口已完成。

ECS 层位于独立程序集 `Features/Combat/Ecs`，由 Unity EditMode 的 `CombatSessionTests` 验证，不属于上述 .NET 检查范围。它直接使用场景 ID 4/5：固定步进、场地/输入策略、空间网格、弹丸与近战、稳定伤害排序、暂停重开和死亡补怪均消费当前表；弹丸池上界从技能寿命和规则最短间隔推导，不另存默认数量。所有隔离模拟结果均不替代 Win64 动画与帧率验收，旧性能入口仍拒绝 Combat 场景，直到正式展示与采样入口接入。

本次扩展改变了角色、怪物、技能和性能场景 bytes 的布局，并新增三份表数据；生成代码与 bytes 必须一起使用。发布 Player 时重新构建完整离线资源包，不将新版 bytes 单独覆盖到旧 Player。

资源路径只能写入 `TbResource`。其他表必须引用基础表 ID。

## 怪物普攻时序配置

渲染参数位于 `TbCombatPresentation`：`shaderName` 必填且运行时可找到，并须支持 URP Unlit 材质接口；`textureFilterMode` 为 `Point` 或 `Bilinear`。当前方案沿用 URP Unlit/Bilinear，未知配置明确拒绝，不回退默认 Shader。Unity 材质属性名与透明混合枚举经确认属于渲染接口常量，不要求策划配置。

- `TbSkillCombat.attackWindupSeconds`：对齐朝向后保持怪物 `_zd`，到开始 `_gj` 的基础前摇秒数。近战必填、有限且非负；当前普通普攻机制 ID 2 配 `0.2`，弹丸机制留空。Boss 后续使用独立技能战斗行，不在代码里判断 Boss 或补默认值。
- `TbAttackDirection.hitFrameIndex`：进入 `_gj` 的动作帧时判定一次，零基索引；当前四方向映射 ID 1–4 均为 F4。不是图集帧编号，也不是前摇时长。
- 命中基础时点 = 前摇 + ANI 中 F4 之前累计时长；完整动作时点 = 前摇 + ANI 总时长。ANI 单位换算读取 `TbCombatPresentation`。攻速按基础间隔/有效间隔推进原速进度，200 毫秒为 1 倍速基础值。
- 改前摇、帧号或方向后运行生成和两项校验；缺配/越界拒绝入局，不回退即时伤害。当前只启用已核对的 btm1 攻击资源。

## ID 规范

- ID 只要求在所属表内唯一，不跨表做全局唯一；关联字段必须使用 Luban `#ref` 明确目标表。
- 普通功能表默认从 `1` 或 `10001` 开始递增；是否分段由数据含义决定，禁止哈希随机 ID。
- `TbResource` 使用 6 位结构 `CTNNNN`：`C` 为类别，`T` 为资源类型，`NNNN` 为该分组序号。
  - 类别：`1`角色、`2`怪物、`3`技能、`4`地图、`9`其他。
  - 类型：`1`动画、`2`纹理、`3`音频、`4`地图数据、`9`其他二进制。
  - 示例：`110001` 表示第 1 个角色动画资源，`220001` 表示第 1 个怪物纹理资源。
- `TbAnimationClip`、`TbVisualSet` 使用 `CNNNN`：类别位加 4 位分组序号。
- `TbCharacter`、`TbMonster` 从 `10001` 递增。
- `TbSkill` 当前沿用旧项目规则 `20000 + 资源编号`，例如 `skill_101` 对应 `20101`。
- `TbStage`、`TbPerformanceScenario` 从 `1` 递增；单例 `GameConfig` 的 ID 固定为 `1`。
- 新增数据只追加未使用 ID；已发布 ID 不复用、不因排序变化重排。

## 生成

Windows：

```powershell
./Tools/Config/generate.ps1
```

macOS/Linux：

```bash
./Tools/Config/generate.sh
```

生成的 C# 与二进制数据一并提交，因此新机器无需安装 Luban 即可打开和运行项目。修改 Excel 后必须重新生成并执行验证脚本。

## 定时圆周波次刷怪

TbPerformanceScenario 的 spawnRadiusPixels、spawnIntervalSeconds、spawnBatchCount、spawnUnitIntervalSeconds 四字段需同时填写或同时为空。场景 4 为半径 1000 逻辑像素、波间等待 1 秒、每波 15 只、波内间隔 0.2 秒。半径经 TbCombatRules.worldUnitsPerPixel 换算。

首只在满波间等待时出生；同波后续逐只间隔 0.2 秒；上一波最后一只出生后，等待 1 秒再开始下一波。每只出生取当时角色位置为圆心，不限制玩法存活数量。entityCount 只用于初始预留槽位，禁止同时开启 replenishOnDeath。其他场景保持旧阵列行为。
