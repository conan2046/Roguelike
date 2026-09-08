# 战斗表现阶段交接

## 接续方式与边界

- 验收顺序：先在 Unity Editor 完成自动化与交互验收，用户亲自验收并确认通过后，才构建和验收 Win64 包。不得提前打包。

- 2026-09-05 用户要求统一回到原项目目录工作，不自动创建 worktree。战斗修改已同步至原项目，当前分支 main，基线 HEAD 为 `fec632225646604d967742361abf26a8cf8f308b`；仍未提交或推送。原目录已有改动保留。
- 不重新实现，不重置或覆盖用户改动。旧 worktree 仅用于历史归档，不再作为开发或验收位置。部分 Git 状态仅为换行/stat 噪声，检查实际 diff。
- 用户已确认本轮可见动画范围及 Shader/过滤入表、渲染 API 常量边界；接续本范围无需重复确认。后续正式输入/UI/启动路由仍先讨论。
- 不清理或删除旧美术，不修改 Scene/Prefab。用户允许操作或重启 Unity，但每次先确认实例对应的项目，不影响无关任务。
- 所有业务配置经 Excel → Luban → C#/bytes；无默认业务值硬编码。手写函数需完整 XML 注释。
- 不明确的资源语义、业务规则直接询问用户，禁止凭后缀或相似资源猜测。

## 最终规则（覆盖历史对话中的过渡说法）

| 对象 | 待机 | 移动 | 攻击/施法表现 |
|---|---|---|---|
| 主角 | `_fd` | `_pb` | 移动中自动释放技能，不播放攻击或施法动作，不停顿 |
| 怪物 | `_zd` | `_pb` | 普攻播放 `_gj`，攻击前对齐待机朝向 |

- 不实施乘骑系统；“fd 仅乘骑”“主角待机改用 zd”“统一两方向”均已作废。
- 不加入元素属性。受击、施法及主角 `_zd` 等未使用片段保留源文件，配置标记 Unused。
- btm1 普攻经用户看图确认：右下/左下 `_gj` A0，对齐 `_zd`/`_pb` A0；右上/左上 `_gj` A1，对齐 A3；左侧镜像。
- 待机、移动仍使用五组加镜像的八方向。普攻从四个候选按最近夹角选择，等角按配置列表顺序；模拟与实体播放已实现攻击停步、锁定朝向。
- 普攻前摇是对齐后 `_zd` 到 `_gj` 开始的等待，普通攻击 `TbSkillCombat.attackWindupSeconds=0.2` 秒；Boss 另配技能行，不猜数值。随后 `_gj` 从 F0 播放，进入 F4（零基第五帧）判定一次。
- 只有 btm1 的两组攻击映射已核对，不向其他怪物推断相同结构。

## 已完成

### 配置

- `TbAttribute`：14 项属性，`TbAttributeProfile`：玩家/怪物基础与概率压力档案。
- `TbSkillCombat`、`TbCombatRules`：伤害、投递、碰撞、时间与模拟参数。
- `TbPerformanceScenario` 4：10 怪、手动移动、正常生命、不补怪；5：1000 怪、静止输入、实际受伤后恢复存活玩家、补怪。
- `TbAnimationDirection`：八方向向量、动作组、镜像。
- `TbCombatPresentation`：时钟、输入轴/键位、UI、相机参数；`monsterHoldMoveFrame=false`。
- `TbAttackDirection`：四方向攻击映射；`facingDirectionId` 复用方向表的向量、镜像、待机/移动组。`attackActionIndex` 指普攻组号，`hitFrameIndex` 当前均为 4。
- `TbVisualSet.attackClipId`：仅怪物表现 20001 指向攻击片段 20002。主角和其他未核验怪物留空。
- 当前主角 10001：待机 10002、移动 10004；怪物 10001：待机 20007、移动 20003、攻击 20002。

### 正式代码

- `Features/Combat`：属性来源聚合、两段取整伤害、独立确定性随机、无敌/免伤/命中/暴击/生命与生命周期。
- `Features/Combat/Ecs`：`CombatSession` 拥有实体/原生池，显式驱动 Burst job；固定步进、空间格寻敌、弹丸相对扫掠、稳定请求排序、死亡补怪、暂停重开。
- `CombatSession.Advance` 返回前完成任务；`ReadUnit`、`UnitEntity`、`ProjectileEntity` 可供表现读取；暂停或玩家死亡不推进模拟。
- `Animation/CocosAniData`：正确的字节计数、小端模块/帧/动作解析，已搬迁并保留旧 meta GUID 和命名空间。
- `AniFrameLayout`、`Animation/Rendering/AniMeshFactory`：全部模块、偏移、UV 镜像和按表像素比例建立网格。
- `CombatAnimation`：加载期累计时序，按模拟秒数零逐帧分配采样；不自行读墙钟。
- `CombatDirections`、`CombatAttackDirections`：表驱动方向选择和实际动作索引校验。
- `Combat/Rendering`：共享 ANI/纹理/材质/预建普通及镜像帧网格；`CombatVisualPlayer` 只读模拟选帧；`CombatEntityVisuals` 绑定实体 MaterialMeshInfo、RenderBounds 和死亡隐藏。
- `Combat/Runtime`：表驱动输入、HP/怪物数/战斗时间 HUD、暂停与重开；独立拥有 World、会话、相机与共享资源。App 显式 `-combatScenario 4` 注册此入口，普通/性能/战斗互斥。

### 最新验证

- `pwsh -NoProfile -File Tools/Config/generate.ps1` 和 `validate.ps1` 通过。
- `validate-combat.ps1`：2472 项通过，其中动画相关 1934 项、战斗运行核心 42 项；10 万次概率抽样命中 79839、暴击 15923。
- Unity EditMode：58/58，通过文件 `outputs/combat-config/visual-final.xml`；保留前摇/F4 模拟回归，新增共享加载/释放、中途取消/失败清理、实体渲染索引绑定、暂停与死亡隐藏、八方向、生命周期重置、纯选帧零分配及 GPU 四方向阶段对照。
- 旧 ECS 压力测试验证过 1000 怪/70 秒固定模拟，不能作为真实画面 FPS 或整帧 GC 证明。
- ANI 审计 1150 片段：1149 配对合法；未启用的 20271 `Monster/btm450_zd.ani` 图集矩形越界，已记录未修源文件。
- 用户方向核对图：`outputs/combat-config/direction-review/btm1-attack-groups.png`、`btm1-idle-move-groups.png`。

## 历史接入清单（当前结果见下方第 20 阶段）

1. 展示资源生命周期：通过 `IResourceService` 按 ID 加载，预构建共享网格/纹理/材质，取消与退出正确释放。
2. ECS 实体动画：待机/移动切换、方向和镜像、死亡/复用/重开时重置播放状态；暂停使用模拟时钟。
3. 将已确认的近战模拟时序接到播放：`AttackActive && AttackAge < WindupSeconds` 时播放已对齐的 `_zd`；之后以 `AttackAge-WindupSeconds` 采样 `_gj`。`AttackOption` 含攻击组、对齐组和镜像，禁止重新逐帧选朝向。
4. 模拟已移除旧近战即时扣血，命中帧只生成一次统一请求；播放层不得再次扣血。命中重新检查范围、目标生命和代际；前一 tick 已死亡则取消。冷却从起手计算，完整动画结束前不重新起手。
5. 主角输入、血量/状态显示、暂停/继续与重开；主角自动施法不打断移动。
6. 应用独占启动路由，避免 Gameplay、旧性能运行器和新战斗运行器同时起局。普通启动保持兼容。
7. 验收：编译、自动化、实际画面/操作；之后再构建 Win64 做真正的千怪战斗性能采样。

旧 `PerformanceFeature` 仍拒绝 Combat；新战斗入口由 App 单独注册，不复用旧性能运行器。输入/HUD 已实现，仍需人工设备操作与 Win64 包启动验收，不能把测试输入或相机读回当作实机操作或千怪 FPS 证明。

详细历史及实现边界见 `rouge-combat-foundation-plan.md` 第 12–20 节；以本文件的最终规则和最新阶段为准。

## 本轮模拟接入补充

- `CombatSessionFactory.CreateAsync` 通过 `IResourceService` 按 ID 加载攻击/待机/移动 ANI，校验实际索引后构造会话，临时句柄自动释放。主线程调用并保留 Unity 同步上下文；纹理/材质/网格加载不在此工厂内。
- `CombatAttackOption` 将 ANI 累计命中时点与动画总时长复制到原生池。`CombatSession` 必须提供预加载结果，缺资源或近战前摇配置直接失败。
- 进度单位为原速秒，推进倍率为技能基础间隔/当前有效间隔；前摇与 `_gj` 均按此倍率推进。属性同步保留当前进度与已起手攻击快照。暂停冻结、死亡清除、复用/重开恢复模板。
- 第 18 阶段仅完成模拟；第 19 阶段补共享表现资源与实体播放；第 20 阶段接入输入、HUD 和应用路由，人工交互验收待完成。

## 表现接入说明

1. Unity 主线程 `await CombatVisualResources.LoadAsync(scenario, resources, token)`，接着通过 `CombatSessionFactory` 创建会话，并创建 `CombatEntityVisuals`。共享资源必须比所有实体绑定存活更久。
2. 每次 `session.Advance` 后调用 `visuals.Synchronize()`，世界的 Transform/Presentation 系统仍须正常推进。表现只消费模拟，绝不二次推进攻击或扣血。
3. 自有世界退出按 `visuals.Dispose()` → `session.Dispose()` → `world.Dispose()` → `visualResources.Dispose()` 顺序执行，世界先移除 GPU 注册，再销毁共享资源。
4. `TbCombatPresentation.shaderName` 和 `textureFilterMode` 是新增表字段；当前 Shader 为 URP Unlit，过滤为 Bilinear，支持改 Point。普通/镜像帧在加载期预建，每单位不创建独立材质、网格或 GameObject。
5. GPU 对照图 `outputs/combat-config/visual-review/monster-windup-f0-f4.png`：左至右按攻击表列表为右下、左下、右上、左上；上至下为前摇待机、攻击 F0、攻击 F4。采用真实播放器和材质的 Unity 预览绘制，不是完整应用场景截图。

### 战斗血条像素调参

- `Assets/GameContent/UI/Combat/Prefabs/CombatHealthBar.prefab` 仅是编辑器调参载体，挂到角色或怪物 `DamageFloatAnchor` 后观察比例；正式运行仍由 `CombatFeedbackVisuals` 创建 DOTS 实体。
- 根节点 `Scale=1`。宽度、高度和预览血量比例都在 `CombatHealthBarView` 中文 Inspector 中填写；子节点“显示画布（自动像素换算勿改）”读取 `TbCombatRules.worldUnitsPerPixel`，不需要人工换算或修改缩放。
- 点击“导出血条像素到 Luban”写入 `TbCombatPresentation.healthBarWidthPixelsMilli / healthBarHeightPixelsMilli` 并重新生成。DOTS 血条直接使用这两个最终像素值，不再按单位碰撞半径隐式改变宽度。

## 第 20 阶段：交互入口

- 启动协议：显式 `-combatScenario 4`；无参数保持普通启动，无业务默认场景 ID；与 `-performanceScenario` 冲突、重复或非法 ID 都拒绝。编辑器批量测试仍自行组合服务。
- 输入：既有 `TbCombatPresentation` 轴 `Horizontal`/`Vertical`，键 `Escape` 暂停/继续、`R` 重开。HUD 按表读取标题、状态格式、帮助、死亡文字、字号和面板尺寸；按钮与键盘共用控制方法。
- 时序：资源全部加载后 Ready；Update 采样输入并推进会话，LateUpdate 推进私有 World 的变换/表现，不重复挂载 PlayerLoop、不修改全局 timeScale。主角移动中仍自动发射，不增加施法动画。
- 场景：显式战斗只在运行态停用旧场景活动根节点，退出恢复；不保存 Scene/Prefab。独立战斗相机按配置场地与宽高比适配。
- 生命周期：暂停冻结模拟和动画，死亡冻结且只能重开；重开复用资源和实体。关闭取消加载并回收世界/相机/资源，恢复帧率/VSync/后台运行设置；组合根等启动任务清理完句柄再销毁 YooAsset。
- 本轮复用既有表字段，没有新增或改写 Excel 业务值。`validate.ps1` 与 `validate-combat.ps1` 通过，后者 2472 项。
- Unity EditMode **66/66**：`outputs/combat-config/runtime-final-editmode.xml`；PlayMode **3/3**：`outputs/combat-config/runtime-verified-playmode.xml`。新增覆盖互斥启动参数、场景活动状态恢复、真实 YooAsset/实体相机、运行器输入/暂停/死亡/重开、两轮进入退出，以及会话加载中退出后的晚到句柄/主世界/辅助流式世界回收。
- `outputs/combat-config/runtime-review/ecs-camera.png` 是真实 ECS 战斗相机读回，非预览绘制；该图不包含 HUD。截图固定暂停出生状态，并在有界时间内等待 GPU 准备，最终仍拒绝空图。
- 未包含：弹丸可视化/技能特效、正式美术 UI、元素属性、Boss 新数值、Win64 新包和千怪实画面性能采样。弹丸模拟与伤害已运行，但弹丸当前不可见。
- 待人工验收：实际键鼠移动和按钮、HUD 排版、完整命令行启动。没有提交或推送；原工作区已有改动保留。
- 已知技术提示：现有 URP Renderer 的 Forward 路径触发 Entities Graphics 建议使用 Forward+ 的警告；本轮没有擅自调整管线资产，当前相机测试通过，不据此承诺未来千怪兼容性或性能。

## 第 21 阶段：定时圆周刷怪（覆盖场景 4 旧出生规则）

- 用户确认半径 300 逻辑像素，每秒 15 只，圆心跟随角色当前位置，无玩法存活上限。
- 场景 4 使用新刷怪字段，开局空场，首批在模拟 1 秒生成；半径通过既有 worldUnitsPerPixel 换算。暂停/死亡冻结刷怪，重开重新计时。
- 动态增长并复用死亡槽；实体表现同步扩容且继续共享美术资源。怪物从完整圆周追入，保留原攻击语义；玩家仍受原场地约束。
- 旧固定出生测试采用隔离配置副本；新增用例使用真实场景 4 验证时点、圆周、扩容与渲染。验证结果见本轮验收记录。

## 波次与半径修正（最新规则）

- 用户修正半径为 1000 逻辑像素（当前换算 10 世界单位）。
- 一波 15 只，波内逐只间隔 200 毫秒（0.2 秒），不是瞬时生成十五只。原 1 秒作为波间等待，首波同样等待。
- 首波首只 1.0 秒、末只 3.8 秒；第二波首只 4.8 秒。固定 tick 向上量化间隔，消除 float 的 0.2 秒表示误差造成的额外一帧。
- 每只生成时取角色当前位置作圆心。暂停、死亡、重开、动态扩容及槽位复用规则不变。
- runtime 诊断红圈直接读取场景配置；仅本次 Editor Play 存活，不保存 Scene/Prefab，退出回收诊断材质与绘制对象。

## 圆柱碰撞编辑与批量预制体

- 用户授权新增角色/怪物 Prefab，保持原 Scene 不变。全部 30 个 Hero、79 个 Monster 已代码生成到 `Assets/Prefabs/Combat/Hero`、`Assets/Prefabs/Combat/Monster`。
- 统一入口 `Roguelike > Combat > 圆柱碰撞配置`，集中显示待导出修改并批量导出；组件面板仅编辑参数，没有导出按钮。使用方法见 `docs/combat-cylinder-authoring.md`。
- 源角色/怪物表增加五个 move*Pixels 圆柱字段，批量导出已执行。用户期间微调的 Hero 资产保留，已保存修改同步到表。后续新微调仍需通过统一窗口导出。
- ECS 已接入独立圆柱与连续移动扫掠；伤害 bodyRadius 保留。出生圆周占位检查与延迟重试已实现。
- 配置校验 2693 项通过，Luban 源表/生成物一致性通过；统一编辑器代码已使用 Unity 编译器和实际程序集引用独立编译通过。
- 首轮选定 EditMode 37 项，36 通过，1 项旧输入测试因新增移动阻挡失败；该用例已改为空路径。后续回归卡在旧 `MeleeWaitsConfiguredWindupThenHitsOnceAtF4` 无限等待循环，取消无效，MCP 无法取得最新回执。已将该循环改为有界等待，但运行中的旧代码需 Unity 恢复后才能替换。
- 当前尚未完成最终回归、统一窗口实机显示和完整交互验收。未构建 Win64，未提交或推送；没有强行关闭用户 Unity。

## 本次提交状态（2026-09-05，以本节覆盖历史待验收描述）

- 用户已反馈“测试可以了”，本轮 Unity 验收收口；该反馈不等于最新全量自动化测试全部通过。
- 神将分类修正为 6 个本体、79 个怪物、24 个独立武器预制体。六个本体 ID 为 10001、10006、10011、10016、10021、10026；TbHeroWeapon 10001–10030 映射每位神将的 0–4 档，0 档集成在本体。独立武器不参与移动碰撞，运行时换武器尚未接入。
- 已保存的角色和怪物圆柱微调保留并批量导出；最新配置校验 2663 项通过，Luban 源表与生成物一致性通过。证据位于本地 outputs/combat-config/hero-classification 的 validate.log 与 luban-consistency.log。
- 历史 66/66 EditMode、3/3 PlayMode 是此前交互入口版本结果，不能冒充本次最终全量结果。后续全量测试未完成；F4 单项曾通过。此前根据卡住的进度标签判断 F4 无限循环并不可靠，全量测试还包含昂贵的千怪负载。
- 千怪持续负载测试已标记 Explicit，普通交互验收不运行；已给攻击等待测试增加有限步数与失败断言。
- 用户要求 Win64 延后至全部功能完成后另行安排，本轮不构建、不进行千怪性能测试。弹丸可视化、元素属性、Boss 数值继续排除。
- 本次仅提交战斗相关代码、源表、生成物、已授权预制体及文档。原有 Scene 和项目设置修改保留在本机工作区，不纳入提交。

### 提交前预览资产减重

- 原预览将解码纹理作为子资产保存，约 1.16 GiB；在推送前改为编辑器 Gizmos 按需绘制及 HideAndDontSave 内存缓存，未将大型预览纳入最终提交历史。
- 109 个 Prefab 仅移除预览 MeshFilter/MeshRenderer 持久引用；表 ID、85 个圆柱参数、全部节点变换及 Prefab GUID 核对不变。
- Unity 实际验证 109 项源资源生成、缓存复用和销毁均通过；本体、怪物、独立武器的 Prefab 视图截图已记录在本地 outputs/combat-config/hero-classification/local-preview-*.png，打开预览不产生未保存修改。
- 预览须开启 Scene Gizmos；原 PNG/ANI 与配置为唯一重建来源，运行时战斗逻辑没有改动。

## M1 正式单局自动化收口（2026-09-05）

- 正式 `-stage 1` 已接通五技能表现、经验掉落与拾取、连续升级三选一、18:00 Boss、胜负结算、重开及五个可替换美术资源的 UI Prefab。
- 完整模拟时钟逐段覆盖四个刷新阶段，确认 30/50/60/75 波、150/400/720/1125 个普通怪、固定种子复现和唯一 Boss；修复了阶段结束时刻漏掉末只单位导致实际只有 211 波/2391 怪的问题。
- Boss 到点会先尝试本阶段完整周期的末只单位，再取消仍因出生占位阻塞的普通请求；Boss 请求保留并在解堵后只生成一次。
- 当前验证：Luban 源表与生成物一致，战斗配置 3064 项（含 `skill_1_f` 唯一镜像断言），工程编译 0 警告/0 错误，非压力 EditMode 112/112，PlayMode 4/4。
- `skill_1_f` 对应 `TbAnimationClip 30002` 已按表启用水平镜像；专项测试核对实际 Projectile 使用镜像网格，弹道旋转和碰撞数据未改。
- 正式 `-stage 1` 已用生产输入在可见 Unity 中重新启动，Console 0 error。原 `CombatRuntimeRunner.Update` 空引用来自 Play 中强制脚本域重载；Runner 现会识别残缺运行态、只报一次明确错误并关闭。正常重新进入与退出 Play 不出现 Missing Script 或 Persistent allocation 提示。
- Win64 构建继续延后；现阶段只剩项目方人工体验验收。
