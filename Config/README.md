# Luban 配置数据

## 模型与技能片段层级

`Tables/z_表现层级_CombatRenderConfig.xlsx`（`TbCombatRender`）按模型或技能片段配置透明队列；`TbCombatPresentation.renderConfigIds` 显式引用当前方案的完整绑定目录。

- 模型行填写 `visualSetId`，`skillId` / `clipId` 留空；同一模型的待机、移动、攻击及受击白闪使用同一队列。
- 技能行填写 `skillId` 和该技能实际引用的 `clipId`，`visualSetId` 留空。飞行、命中及多个范围片段分别绑定；同一动画被不同技能复用时允许不同队列。
- `renderQueue` 越大越靠前；必须位于 Unity 透明区间并低于当前 `feedbackRenderQueue`。运行时缺少绑定会报错，旧 `unitRenderQueue` / `skillRenderQueue` 已不参与模型与技能排序，也不作为默认值。
- 在单位的“移动阻挡范围”、技能的“命中范围”或独立表现节点 Inspector 可直接编辑层级，点击“保存层级到 Excel 并生成 Luban”。只修改本次选择的源表队列，不保存到组件，不改变 Prefab 位置、大小、碰撞或资源引用；重新进入战斗使用新快照。
- 编辑器导出会检查生成快照与源表的数字基线，拒绝覆盖外部改动；源表或生成失败则回滚本次源表和层级 bytes。
- 同纹理同队列复用材质，不同队列隔离材质；普通和白闪材质保持相同队列。所有运行时材质仍由每局共享资源集合统一释放，不写入任何预览资产。

## 局内构筑配置

`TbStage.runBuildId` 指向 `TbRunBuild`，后者维护武器/被动独立槽位、角色限制、武器补偿、初始武器及可用目录。`TbHeroWeapon` 仍只描述角色外观档位。

- `Tables/j_局内武器_RunWeaponConfig.xlsx`：局内武器身份与连续星级；每级引用正式技能，并配置本武器伤害、间隔、范围千分比。
- `Tables/j_局内被动_RunPassiveConfig.xlsx`：被动身份与连续等级；每级填写完整总效果，运行时替换同一来源，不累计旧等级。
- `Tables/j_局内构筑_RunBuildConfig.xlsx`：通用与角色专属槽位、目录及显示格式。武器显示星级，被动显示等级。
- 正式升级选项使用 `RunWeapon` / `RunPassive` 强引用，权重仍只写在升级池条目中。构筑池不得混入绕过槽位的旧技能/属性卡。
- `SkillRangeMultiplier` 属于正式属性语义，各属性方案必须显式提供基础值；缺值不由代码补默认值。

修改源表后仍使用本文原有 Luban 生成与一致性校验命令；新机器无需额外运行本地一次性整理脚本。

## 超武与局内事件（2026-10-25）

- `Tables/j_武器进化_WeaponEvolutionConfig.xlsx`：六条武器/被动/结果技能强引用，条件只产生资格，必须领取宝箱才替换；按priority、ID稳定选择一条。
- `TbSkillCombat`：显式维护阵型、弹数/扇角、伤害系数、穿透/连锁、命中爆发、持续/脉冲、跟随/换目标、环数/环宽；不用的字段明确置零/false或留空可空字段。纯消费者不提供玩法默认值。
- `Tables/j_局内事件_RunEventConfig.xlsx`（`TbRunEvent`）：12条有序时间事件，互斥的精英生成或地图箱载荷；距离为逻辑像素、角度为度。`TbStageRule.runEventIds`显式引用。
- `Tables/j_宝箱规则_ChestProfileConfig.xlsx`（`TbChestProfile`）：正式标记Prefab、拾取距离、普通奖励池、进化许可、标题/领取/耗尽文本；精英通过`TbMonster.deathChestProfileId`首次死亡掉箱。
- `TbCombatUiSet`：新宝箱奖励面板、事件HUD资源和状态格式；资源960009～960011指向GameAsset中的三个正式UI Prefab，路径不带Assets前缀。
- 正式stage1使用构筑2/升级池2，普通池1仅兼容保留。八普怪10010～10017按原四阶段，精英10018/10026走事件，最终Boss10032独立。
- 精英技能29001/29002使用独立近战机制14/15，技能ID不覆盖原资源目录。怪物及技能锚点从正式Prefab导出，手调命中特效偏移保留。

六链的本批条件为5星武器+配对被动至少1级+开箱，不包含局外品质门槛；局外品质后续接入。修改源表后生成和校验命令同下文，运行不依赖本地一次性编辑脚本。

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
- 战斗 Prefab 只拥有明确列出的碰撞、表现偏移/缩放和血条尺寸字段。导出前必须执行字段级 Dry Run，并以 `Config/Authoring/combat-prefab-ownership.json` 核对最近一次成功导出值；检测到 Excel 人工修改冲突时禁止覆盖。

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
| TbAttribute | ID 1–15 | 生命、攻击、防御、移速、攻速以及命中、闪避、暴击、抗暴和对应参数 |
| TbAttributeProfile | ID 1–15 | 玩家/怪物闭环调试、概率压测、八类普通怪、两精英与独立Boss |
| TbSkillCombat | ID 1–15 | 基础投递、十二玩家形态及两精英近战机制 |
| TbCombatRules | ID 1 | 随机 80%–120%、暴击 150%、零分母概率、模拟与坐标设置 |
| TbCharacter / TbMonster | 角色10001；测试怪10001、八普怪10010–10017、精英10018/10026、Boss10032 | 关联属性方案、默认技能、碰撞体和移动类型；正式单位使用 `VerticalCapsule` 合并碰撞 |
| TbSkill | 六基础20001/20014/20028/20033/20043/20016；六超武20044/20027/20050/20007/20037/20030 | 另有普通近战20002、精英29001/29002及兼容技能20032；资源目录不等于正式可用技能 |
| TbPerformanceScenario | 新增 ID 4、5 | 10 怪基础闭环、1000 怪概率战斗压测；旧 ID 1–3 原值保留 |

`TbAttributeProfile.values` 使用 `属性ID:数值;属性ID:数值`，由 `AttributeValue` 结构解析；必需属性完整填写，不保留代码默认值。其余未启用的角色、怪物和技能战斗字段为空，表示仅有资源目录数据，不能作为战斗单位使用。完整角色组装仍需在运行时阶段验收。

新增 `EAttributeType`、`EAttributeValueKind`、`ESkillDeliveryType`、`ETestHealthPolicy`、`ETestInputPolicy`、`EUpgradeOptionType`、`EAttributeModifyOperation`、`ERunResultType`、`EMovementType`、`EUnitCollisionShape`，以及 `EPerformanceKind.Combat`。构筑批次增加 `EProjectilePattern`、`ERunEventKind`；`EUnitCollisionShape.VerticalCapsule` 已覆盖 TbStage 1 当前出战角色及八普怪、两精英、Boss，未对全资源目录批量迁移。未增加元素属性或元素伤害分支。

初始调试参数：玩家生命/攻击为 100/20，怪物为 40/8；默认必中且不暴击。概率压测方案使用命中/闪避 100/25、暴击/抗暴 25/100，各参数为 1，即 80% 命中、20% 暴击。所有这些值都是工程调试输入，不是最终平衡数值。

场景 4 使用正常生命和手动输入，场景 5 使用显式的扣血后恢复生命策略、静止输入和死亡补怪，便于维持采样。该策略不能作为正常玩法或生存平衡结果。旧移动运行器遇到 Combat 类型会明确失败，不能把新场景报告为移动压测通过。

修改表后使用 PowerShell 7（`pwsh`）运行以下检查；不支持 Windows PowerShell 5，避免中文路径和输出编码异常。战斗校验需 .NET 8 SDK，无额外 NuGet 包或 Excel/Python 依赖：

```powershell
pwsh -NoProfile -File ./Tools/Config/generate.ps1
pwsh -NoProfile -File ./Tools/Config/validate.ps1
pwsh -NoProfile -File ./Tools/Config/validate-combat.ps1
```

最后一项直接编译当前生成 C#、`Assets/Scripts/Features/Combat` 正式源码和共享 ANI 解析器，读取当前 bytes，验证属性完整性、引用、数值范围、三种投递类型、M1 关卡依赖、四阶段完整周期、215 波、2395 个普通怪、怪物权重、经验曲线、升级池、Boss、场地与压力策略；通过独立 decimal 参考结果核对正式伤害公式，并测试来源替换/移除、生命上限、冷却、免伤顺序、生命周期与独立随机分布。当前通过 3493 项检查，其中战斗运行时断言 42 项、动画断言 1939 项，另执行 100000 次概率抽样。正式运行时和画面由 Unity EditMode/PlayMode 回归验证；千怪性能仍由独立压力回归负责。

## 战斗表现配置

- `TbAnimationDirection`：方向 ID、向量、ANI 动作索引与水平镜像，映射来自既有 `CocosModelAniPlayer.FaceActionIndex/FaceFlipped`，不在新运行时代码中复制方向分支。
- `TbAnimationClip.flipX`：片段自身的强制水平镜像，只切换预建镜像网格，不改变弹道角度、速度或碰撞轨迹。当前仅 `30002 / skill_1_f` 为 `true`，用于修正源 ANI 朝向。
- `TbCombatPresentation`：源时钟、时长兼容规则、出生方向、方向列表、怪物静止策略、输入轴/按键、界面文本/尺寸、血条像素宽高、反馈资源、伤害跳字参数和相机/诊断弹丸颜色。规则来自既有播放器及本轮确认的基础表现范围。
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

资源路径只能写入 `TbResource`。其他表必须引用基础表 ID。`package` 明确资源所属 YooAsset 包：
`ProjectX/...` 必须为 `Raw`，仅允许 Raw API；`GameContent/...` 必须为 `GameAsset`，仅允许 Unity Asset API。
纹理同时存在两种存储形态，禁止仅按 `EResourceType` 猜测加载方式。

## 怪物普攻时序配置

渲染参数位于 `TbCombatPresentation`：`shaderName` 必填且运行时可找到，并须支持 URP Unlit 材质接口；`textureFilterMode` 为 `Point` 或 `Bilinear`。当前方案沿用 URP Unlit/Bilinear，未知配置明确拒绝，不回退默认 Shader。Unity 材质属性名与透明混合枚举经确认属于渲染接口常量，不要求策划配置。

血条尺寸位于 `TbCombatPresentation.healthBarWidthPixels` 和 `healthBarHeightPixels`，直接存储逻辑像素浮点值。编辑源为 `CombatHealthBar.prefab` 的中文 Inspector；根节点缩放固定为 1，内部画布仅负责读取战斗规则完成场景显示换算。点击“导出血条像素到 Luban”后，运行时 DOTS 只消费生成配置，不读取或实例化该 Prefab。

血条底框、填充与伤害数字图集分别由 `healthBarFrameResourceId`、`healthBarFillResourceId`、`damageNumberAtlasResourceId` 引用 `TbResource` 的 Texture 行；`damageNumberGlyphs` 定义图集从左到右的唯一字形顺序，并必须覆盖 `0`–`9`。上升时长、上升像素、起止缩放、字距和字形缩放均来自同表对应字段。运行时按正式纹理真实宽高合成仅驻留内存的图集，并按字形数量等分数字图集；血条两张纹理尺寸不一致或图集宽度不能整除字形数时拒绝入局，不保留代码默认值。

- `TbSkillCombat.attackWindupSeconds`：对齐朝向后保持怪物 `_zd`，到开始 `_gj` 的基础前摇秒数。近战必填、有限且非负；当前普通普攻机制 ID 2 配 `0.2`，弹丸机制留空。Boss 后续使用独立技能战斗行，不在代码里判断 Boss 或补默认值。
- `TbAttackDirection.hitFrameIndex`：进入 `_gj` 的动作帧时判定一次，零基索引；当前四方向映射 ID 1–4 均为 F4。不是图集帧编号，也不是前摇时长。
- 命中基础时点 = 前摇 + ANI 中 F4 之前累计时长；完整动作时点 = 前摇 + ANI 总时长。ANI 单位换算读取 `TbCombatPresentation`。攻速按基础间隔/有效间隔推进原速进度，200 毫秒为 1 倍速基础值。
- 改前摇、帧号或方向后运行生成、产物一致性校验和战斗配置校验；缺配/越界拒绝入局，不回退即时伤害。当前启用既有 btm1 与 M1 五个怪物的已核对攻击资源。
- 测试关卡单位的命中特效位置由角色/怪物 Prefab 的 `命中特效挂点` 导出为 `hitEffectOffsetXPixels/Y`；技能的弹丸、命中、范围表现分别由中文表现节点导出位置与缩放。表现参数不改变 CircleCollider2D 或 DOTS 判定范围。

## M1 单局配置

正式单局入口为 `TbStage` ID 1，初始角色 10001、初始技能 20001。`TbStageRule` ID 1 配置 18 分钟普通怪阶段、四个连续刷新阶段、经验等级组 1、掉落方案 1、升级池 2、构筑 2、十二条事件和 Boss 遭遇 1 和确定性随机种子。`TbCombatUiSet` ID 1 已引用 HUD、Boss 血条、升级面板、升级卡、结算面板以及宝箱/事件正式 Prefab，`TbStage.uiSetId` 为 1。

| 表 | 当前 ID/范围 | 关键内容 |
|---|---|---|
| `TbSpawnPhase` | 1–4 | 时间段 0–3、3–8、8–13、13–18 分钟；每波 5/8/12/15 只 |
| `TbMonster` | 10010–10017、10018/10026 | 八普通怪、两事件精英；10011为Flying，其余为Ground |
| `TbBossEncounter` | 1 | Boss 10032_btm311，死亡触发胜利 |
| `TbExperienceLevel` | 1002–1020 | 等级组 1，目标等级 2–20，逐级显式经验值 |
| `TbDropProfile` | 1 | 经验物品 10001、单怪经验 1、吸附/拾取参数 |
| `TbUpgradePool` | 2（旧1保留） | 无放回抽取3项，构筑选项41001～41012及恢复40009 |
| `TbUpgradeOption` | 41001～41012、40009（旧选项保留） | 六武器、六被动、立即恢复；满槽/满级过滤 |

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
pwsh -NoProfile -File ./Tools/Config/generate.ps1
```

macOS/Linux：

```bash
./Tools/Config/generate.sh
```

生成的 C# 与二进制数据一并提交，因此新机器无需安装 Luban 即可打开和运行项目。修改 Excel 后必须重新生成并执行验证脚本。

## 定时圆周波次刷怪

TbPerformanceScenario 的 spawnRadiusPixels、spawnIntervalSeconds、spawnBatchCount、spawnUnitIntervalSeconds 四字段需同时填写或同时为空。场景 4 为半径 1000 逻辑像素、波间等待 1 秒、每波 15 只、波内间隔 0.2 秒。半径经 TbCombatRules.worldUnitsPerPixel 换算。

首只在满波间等待时出生；同波后续逐只间隔 0.2 秒；上一波最后一只出生后，等待 1 秒再开始下一波。每只出生取当时角色位置为圆心，不限制玩法存活数量。entityCount 只用于初始预留槽位，禁止同时开启 replenishOnDeath。其他场景保持旧阵列行为。
# Ground 与摄像机覆盖

交互战斗的 Ground 由 `TbMap.arenaHalfWidthPixels`、`arenaHalfHeightPixels`、`cameraViewportHeightPixels` 与摄像机实际宽高比推导：左右分别补半个可见视野宽度，上下分别补半个可见视野高度。总宽高等于完整行走宽高加完整视野宽高，再对称向外补齐 `tileSizePixels` 地块。屏幕物理像素不直接与逻辑像素相加。

调整行走范围改 `d_地图_MapConfig.xlsx` 的 `arenaHalfWidthPixels/arenaHalfHeightPixels`；窗口比例变化自动重铺 Ground，复用已加载纹理，世界格子的纹理保持稳定。`mapWidthPixels/mapHeightPixels` 保留为无视野模板及旧工具尺寸，交互战斗不再以其作为 Ground 或镜头边界。
