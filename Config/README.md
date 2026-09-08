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

- L0：`TbResource`、`TbAttribute`、`TbItem`、`TbSkillCombat`
- L1：`TbAnimationClip`、`TbVisualSet`、`TbMap`、`TbAttributeProfile`、`TbExperienceLevel`
- L2：`TbCharacter`、`TbMonster`、`TbSkill`、`TbDropProfile`、`TbUpgradeOption`、`TbCombatUiSet`
- L3：`TbSpawnPhase`、`TbBossEncounter`、`TbUpgradePool`
- L4：`TbStageRule`、`TbStage`
- 全局常量：`GameConfig`
- 测试：`TbPerformanceScenario`

### 战斗基础配置

战斗公式与属性采用《战斗公式及属性调整V1.0》的非元素部分；配置、属性/伤害模块及独立 ECS 会话已实现。正式 `-stage 1` 已接入应用启动、输入、相机、实体动画表现和 Prefab UI；性能场景继续作为隔离测试入口。

| 表 | 当前配置 | 用途 |
|---|---|---|
| TbAttribute | ID 1–14 | 生命、攻击、防御、移速、攻速以及命中、闪避、暴击、抗暴和对应参数 |
| TbAttributeProfile | ID 1–9 | 玩家/怪物闭环调试、概率压测、M1 四类普通怪与独立 Boss |
| TbSkillCombat | ID 1–3 | 直线弹丸、近战、目标位置群体释放参数 |
| TbCombatRules | ID 1 | 随机 80%–120%、暴击 150%、零分母概率、模拟与坐标设置 |
| TbCharacter / TbMonster | 角色 10001；怪物 10001、10010–10013、10032 | 关联属性方案、默认技能、碰撞体和移动类型；测试关卡引用单位使用 `VerticalCapsule` 合并碰撞 |
| TbSkill | 启用 ID 20001、20002、20014、20028、20032、20043 | 三个弹道、一个近战、两个目标位置群体技能 |
| TbPerformanceScenario | 新增 ID 4、5 | 10 怪基础闭环、1000 怪概率战斗压测；旧 ID 1–3 原值保留 |

`TbAttributeProfile.values` 使用 `属性ID:数值;属性ID:数值`，由 `AttributeValue` 结构解析；必需属性完整填写，不保留代码默认值。其余未启用的角色、怪物和技能战斗字段为空，表示仅有资源目录数据，不能作为战斗单位使用。完整角色组装仍需在运行时阶段验收。

新增 `EAttributeType`、`EAttributeValueKind`、`ESkillDeliveryType`、`ETestHealthPolicy`、`ETestInputPolicy`、`EUpgradeOptionType`、`EAttributeModifyOperation`、`ERunResultType`、`EMovementType`、`EUnitCollisionShape`，以及 `EPerformanceKind.Combat`。`EUnitCollisionShape.VerticalCapsule` 当前只标记 TbStage 1 的角色 10001 与怪物 10010–10013/10032；其他单位尚未批量迁移。未增加元素属性或元素伤害分支。

初始调试参数：玩家生命/攻击为 100/20，怪物为 40/8；默认必中且不暴击。概率压测方案使用命中/闪避 100/25、暴击/抗暴 25/100，各参数为 1，即 80% 命中、20% 暴击。所有这些值都是工程调试输入，不是最终平衡数值。

场景 4 使用正常生命和手动输入，场景 5 使用显式的扣血后恢复生命策略、静止输入和死亡补怪，便于维持采样。该策略不能作为正常玩法或生存平衡结果。旧移动运行器遇到 Combat 类型会明确失败，不能把新场景报告为移动压测通过。

修改表后运行以下检查；战斗校验需 .NET 8 SDK，无额外 NuGet 包或 Excel/Python 依赖：

```powershell
./Tools/Config/generate.ps1
./Tools/Config/validate.ps1
./Tools/Config/validate-combat.ps1
```

最后一项直接编译当前生成 C#、`Assets/Scripts/Features/Combat` 正式源码和共享 ANI 解析器，读取当前 bytes，验证属性完整性、引用、数值范围、三种投递类型、M1 关卡依赖、四阶段完整周期、215 波、2395 个普通怪、怪物权重、经验曲线、升级池、Boss、场地与压力策略；通过独立 decimal 参考结果核对正式伤害公式，并测试来源替换/移除、生命上限、冷却、免伤顺序、生命周期与独立随机分布。当前通过 3064 项检查，其中战斗运行时断言 42 项、动画断言 1939 项，另执行 100000 次概率抽样。正式运行时和画面由 Unity EditMode/PlayMode 回归验证；千怪性能仍由独立压力回归负责。

## 战斗表现配置

- `TbAnimationDirection`：方向 ID、向量、ANI 动作索引与水平镜像，映射来自既有 `CocosModelAniPlayer.FaceActionIndex/FaceFlipped`，不在新运行时代码中复制方向分支。
- `TbAnimationClip.flipX`：片段自身的强制水平镜像，只切换预建镜像网格，不改变弹道角度、速度或碰撞轨迹。当前仅 `30002 / skill_1_f` 为 `true`，用于修正源 ANI 朝向。
- `TbCombatPresentation`：源时钟、时长兼容规则、出生方向、方向列表、怪物静止策略、输入轴/按键、界面文本/尺寸、血条像素宽高和相机/诊断弹丸颜色。规则来自既有播放器及本轮确认的基础表现范围。
- `TbPerformanceScenario.presentationId`：Combat 场景 4/5 引用方案 1，旧场景 1–3 留空；新增字段改变 bytes 布局，生成 C# 和 bytes 必须一起发布。
- `CombatAnimation` 加载时构造累计时长，以调用方提供的模拟秒数采样；`CombatDirections` 校验并归一化方向向量，静止保留历史朝向。`idleFrameIndex` 指动作内帧索引，不是 ANI 全局帧索引。
- 最终确认：主角默认待机 `_fd`，怪物默认待机 `_zd`，两者移动均 `_pb`。此前“怪物没有站立片段”“fd 仅乘骑”“主角改用两方向 zd”的判断均不再适用；已补齐怪物待机引用并关闭移动帧占位。
- 主角 `_zd` 及未启用的角色/怪物动作仍标记 Unused。M1 五个怪物的 `_gj` 已标记 Attack；`20001/20014/20032` 的飞行与命中片段分别标记 Projectile/Impact，`20028/20043` 的目标位置片段标记 Area。施法不暂停移动，也不要求施法动作。死亡分类保留；不实现乘骑系统。
- 当前四个启用片段均支持已有五动作组方向映射；已撤销主角两方向适配需求。未来新增片段仍须按其真实动作结构校验，不能仅凭同类文件推断。
- 怪物普攻由 `TbVisualSet.attackClipId` 显式启用；既有 btm1 与 M1 的 btm108、btm109、btm110、btm111、btm311 均已接入各自真实 `_gj`，主角保持留空。
- `TbAttackDirection` 保存攻击动作组和 `facingDirectionId`，后者引用 `TbAnimationDirection` 的向量、镜像以及待机/移动动作组。表现表 `attackDirectionIds` 决定候选与等角优先顺序。右下/左下用攻击组 0、对齐组 0；右上/左上用攻击组 1、对齐组 3，左侧镜像。
- `CombatAttackDirections` 在加载时校验三份真实动画的组号，开始攻击时按最大点积选择方向；零相对位移保留传入索引，等角按配置列表先后。调用方锁定本次选择，不在攻击中逐帧重选；ECS 攻击阶段、播放状态和命中时点已经接入正式表现桥。
- 输入、相机、Prefab UI 和实体渲染已消费正式表字段；完整交互入口由 PlayMode 真实资源测试验收。Editor 中若 Play 期间脚本域重载导致非序列化运行对象失效，Runner 会一次性关闭并报告明确生命周期错误，避免逐帧空引用。

ECS 层位于独立程序集 `Features/Combat/Ecs`，由 Unity EditMode 的 `CombatSessionTests` 验证，不属于上述 .NET 检查范围。性能场景 ID 4/5 验证固定步进、场地/输入策略、空间网格、弹丸与近战、稳定伤害排序、暂停重开和死亡补怪；正式 `TbStage` 入口另由 `CombatRunCoordinatorTests` 与 PlayMode 验证。弹丸池上界从技能寿命和规则最短间隔推导，不另存默认数量。所有隔离模拟结果仍不替代 Win64 帧率验收。

本次扩展改变了关卡、地图、怪物、技能、技能战斗、动画、表现集合、属性方案、物品和游戏配置 bytes，并新增 8 张业务表；生成代码与 bytes 必须一起使用。发布 Player 时重新构建完整离线资源包，不将新版 bytes 单独覆盖到旧 Player。

资源路径只能写入 `TbResource`。其他表必须引用基础表 ID。

## 怪物普攻时序配置

渲染参数位于 `TbCombatPresentation`：`shaderName` 必填且运行时可找到，并须支持 URP Unlit 材质接口；`textureFilterMode` 为 `Point` 或 `Bilinear`。当前方案沿用 URP Unlit/Bilinear，未知配置明确拒绝，不回退默认 Shader。Unity 材质属性名与透明混合枚举经确认属于渲染接口常量，不要求策划配置。

血条尺寸位于 `TbCombatPresentation.healthBarWidthPixels` 和 `healthBarHeightPixels`，直接存储逻辑像素浮点值。编辑源为 `CombatHealthBar.prefab` 的中文 Inspector；根节点缩放固定为 1，内部画布仅负责读取战斗规则完成场景显示换算。点击“导出血条像素到 Luban”后，运行时 DOTS 只消费生成配置，不读取或实例化该 Prefab。

- `TbSkillCombat.attackWindupSeconds`：对齐朝向后保持怪物 `_zd`，到开始 `_gj` 的基础前摇秒数。近战必填、有限且非负；当前普通普攻机制 ID 2 配 `0.2`，弹丸机制留空。Boss 后续使用独立技能战斗行，不在代码里判断 Boss 或补默认值。
- `TbAttackDirection.hitFrameIndex`：进入 `_gj` 的动作帧时判定一次，零基索引；当前四方向映射 ID 1–4 均为 F4。不是图集帧编号，也不是前摇时长。
- 命中基础时点 = 前摇 + ANI 中 F4 之前累计时长；完整动作时点 = 前摇 + ANI 总时长。ANI 单位换算读取 `TbCombatPresentation`。攻速按基础间隔/有效间隔推进原速进度，200 毫秒为 1 倍速基础值。
- 改前摇、帧号或方向后运行生成、产物一致性校验和战斗配置校验；缺配/越界拒绝入局，不回退即时伤害。当前启用既有 btm1 与 M1 五个怪物的已核对攻击资源。
- 测试关卡单位的命中特效位置由角色/怪物 Prefab 的 `命中特效挂点` 导出为 `hitEffectOffsetXPixels/Y`；技能的弹丸、命中、范围表现分别由中文表现节点导出位置与缩放。表现参数不改变 CircleCollider2D 或 DOTS 判定范围。

## M1 单局配置

正式单局入口为 `TbStage` ID 1，初始角色 10001、初始技能 20001。`TbStageRule` ID 1 配置 18 分钟普通怪阶段、四个连续刷新阶段、经验等级组 1、掉落方案 1、升级池 1、Boss 遭遇 1 和确定性随机种子。`TbCombatUiSet` ID 1 已引用 HUD、Boss 血条、升级面板、升级卡和结算面板五个正式 Prefab，`TbStage.uiSetId` 为 1。

| 表 | 当前 ID/范围 | 关键内容 |
|---|---|---|
| `TbSpawnPhase` | 1–4 | 时间段 0–3、3–8、8–13、13–18 分钟；每波 5/8/12/15 只 |
| `TbMonster` | 10010–10013 | btm108–btm111；10011 为 Flying，其余为 Ground |
| `TbBossEncounter` | 1 | Boss 10032_btm311，死亡触发胜利 |
| `TbExperienceLevel` | 1002–1020 | 等级组 1，目标等级 2–20，逐级显式经验值 |
| `TbDropProfile` | 1 | 经验物品 10001、单怪经验 1、吸附/拾取参数 |
| `TbUpgradePool` | 1 | 无放回抽取 3 项，引用选项 40001–40009 |
| `TbUpgradeOption` | 40001–40009 | 四个技能解锁、四个属性强化、一个立即恢复 |

所有空间距离、半径、偏移和速度统一使用 `*Pixels` / `*PixelsPerSecond` 浮点字段，直接填写逻辑像素，不乘 1000。运行时只在入局边界按 `TbCombatRules.worldUnitsPerPixel` 转为 Unity 世界单位。时间、颜色、属性精度等非空间 `*Milli` 仍以原值乘 1000 的整数保存；`*Permille` 仍表示千分比。比例强化的 `AddPercent` 以比例乘 1000 保存，例如 5% 写 50。

2026-09-05 已验证：Luban 生成成功；重新生成的 C# 与 bytes 哈希一致；战斗配置检查通过 3064 项，覆盖三种技能投递、`skill_1_f` 唯一镜像、四阶段完整周期、215 波、2395 个普通怪、四类普通怪权重、独立 Boss、经验曲线、升级池和正式 UI 引用。

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
