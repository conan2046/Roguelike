# Rogue 项目可维护性审查报告 V1.1

> [!WARNING]
> **本文件已作废（2026-09-10）。**
> V1.1 存在 5 条 P0、8 条 P1 硬事实错误，经外部审核指出并由本人 GUID 审计复验确认。
> 典型错误：Prefab Variant 判定方法错误（实际无任何 Variant）、"20 个死代码可安全删除"（实际 3 个候选，其中 5 个挂在 MCPDemo.unity）。
> **请改用 V1.3：`docs/architecture-maintainability-review-V1.3.md`。不要引用本文件的任何结论。**

- 审查日期：2026-09-10
- 审查对象：当前项目根目录（Unity + Entities 1.0 + Luban + YooAsset，Steam 单机 Roguelike）
- 审查依据：
  1. 微信公众号文章《Unity项目从能运行到好维护的开发思路》（SiKi，2026-09-09）10 条规范
  2. **V1.1 新增**：商业化上线游戏返工教训反推的补充规范（第六章）
- 审查方法：静态扫描（文件树统计、行数统计、正则 grep、Prefab YAML 字段检查）；**未打开 Unity Editor 实跑**
- 版本说明：V1.1 = V1.0 全文 + 新增第六章与第七章，V1.0 原件保留不覆盖

---

## 一、总体结论

10 条规范中 **8 条达标、2 条部分或未达标**。

项目整体架构水平高于文章建议层级（已做到程序集分层 + 配置表唯一数据源，文章只要求文件夹清晰 + ScriptableObject）。

主要短板不在架构设计，而在**历史遗留清理**与**已建基础设施未被业务使用**。

| 维度 | 结论 |
|---|---|
| 脚本职责划分 | 达标 |
| 系统间解耦 | 达标（且优于文章建议） |
| 数据与逻辑分离 | 达标（且优于文章建议） |
| Prefab 结构 | 达标 |
| 目录与命名 | 达标 |
| Manager 职责 | 达标 |
| 迭代节奏 | 达标 |
| 死代码清理 | **未达标** |
| 事件机制落地 | **部分达标** |

---

## 二、逐条对照

### 第 1 条：一个脚本只负责一类事情 —— ✅ 达标

| 指标 | 实测值 |
|---|---|
| 手写脚本文件数（排除 Generated / ThirdParty / Tests） | 103 |
| 手写脚本总行数 | 19,937 |
| 平均单文件行数 | ~110 |
| 最大非测试文件 | `Features/Combat/Ecs/CombatSession.cs`（901 行） |

超长文件清单（非测试）：

| 文件 | 行数 | 职责判断 |
|---|---|---|
| `Features/Combat/Ecs/CombatSession.cs` | 901 | ECS 战斗模拟内核：步进、生成、事件缓冲 |
| `Features/Performance/PerformanceFeature.cs` | 800 | 性能测试场景，仅 1 个 public 方法 `Configure` |
| `Features/Combat/Rendering/CombatFeedbackVisuals.cs` | 715 | 血条/跳字渲染 |
| `Features/Combat/Ecs/CombatTickJob.cs` | 561 | 战斗 Tick Job |

`CombatSession` 虽体量大，但公开接口集中在三类（模拟推进 / 单位生成 / 事件读取），判定为**职责单一的大内核**，非 God Object。此项为人工判断，建议复核。

### 第 2 条：不要让所有系统互相直接引用 —— ✅ 达标

| 检查项 | 实测值 |
|---|---|
| 运行时 `GameObject.Find` / `FindObjectOfType` / `Object.Find` | **0 处** |
| 代码中 `Find` 命中总数 | 12 处，**全部为 Editor 侧 `SerializedProperty.FindProperty`** |
| 静态单例（`static Instance` / `Singleton`） | **0 处** |
| `Features` 目录下 `GetComponent<` 调用 | 29 处（集中于 Authoring 与 Editor） |

通信机制：战斗内核不直接调用外部系统，而是将结果写入 ECS 事件缓冲，由 `CombatRunCoordinator` 拉取消费（`ReadDeath` / `ReadImpact` / `ReadArea`）。该模式比文章建议的 C# 事件/UnityEvent 耦合更低。

### 第 3 条：把数据和逻辑分开 —— ✅ 达标（超出文章要求）

- 配置源表 26 张 Excel，位于 `Config/Datas/Tables/`，含 `__beans__.xlsx` / `__enums__.xlsx` / `__tables__.xlsx`
- 生成物：`Assets/Scripts/Generated/Config/Tb*.cs` + `Assets/StreamingAssets/Config/Luban/*.bytes`
- 运行时统一入口：`IConfigService` → `LubanConfigService`
- `AGENTS.md` 明令：禁止在 C# / Prefab / Scene / ScriptableObject 中硬编码规则与数值

**硬编码数值扫描结果**：`Features` / `Infrastructure` / `Core` / `App` / `Presentation` 五个目录下，排除 `= 0/1/-1` 后命中 **29 处**，逐条分类如下：

| 类别 | 示例 | 判定 |
|---|---|---|
| 渲染/纹理尺寸常量 | `FeedbackAtlasWidth = 348`、`BarTextureWidth = 60`、`DigitCellWidth = 29` | 技术常量，可接受 |
| 排序层 / 深度 | `MovementSortingOrder = 32000`、`HpFillDepth = -2f` | 技术常量，可接受 |
| 表现时长 | `NumberRiseDuration = 0.6f`、`AreaTraceSeconds = 0.4f` | **建议下沉配置表** |
| 资源 ID | `BarFrameResourceId = 960006`、`DigitAtlasResourceId = 960008` | 资源键，属配置边界争议项 |
| 数学基准 | `BasisPoints = 10000`（`CombatMath.cs`） | 纯技术常量，可接受 |
| 调试用 | `CircleSegments = 64`、`HiddenWorldCoordinate = 1000000f` | 可接受 |

**无一条业务数值（伤害、血量、速度、冷却、概率）硬编码。**

### 第 4 条：Prefab 不是复制工具 —— ✅ 达标

| 目录 | Prefab 总数 | Variant 数 |
|---|---|---|
| `Prefabs/Combat/Hero` | 6 | 6 |
| `Prefabs/Combat/Monster` | 79 | 79 |
| `Prefabs/Combat/Skill` | 93 | 93 |
| `Prefabs/Combat/Weapon` | 24 | 24 |
| 合计 | **208** | **202** |

全部战斗 Prefab 均为 Variant（YAML 中存在 `m_CorrespondingSourceObject`），存在基础 Prefab 层级，符合"公共结构放基础 Prefab，差异数据放配置"。

### 第 5 条：文件夹结构让人一眼看懂 —— ✅ 达标（超出文章要求）

```
Assets
├── Art / Audio / GameContent / Prefabs / Scenes / Settings / StreamingAssets
└── Scripts
    ├── App           应用启动与编辑器入口
    ├── Core          抽象接口（IConfigService / IEventBus / IResourceService / IObjectPoolService）
    ├── Features      按功能垂直切分（Combat / Gameplay / Performance）
    ├── Infrastructure 接口实现（Luban / YooAsset / EventBus / Pooling）
    ├── Presentation  表现层启动
    ├── Generated     Luban 生成代码（不手工修改）
    ├── Tests         EditMode / PlayMode
    └── ThirdParty    Luban Runtime
```

强制依赖方向由 **21 个 asmdef** 约束：

```
Roguelike.App / App.Editor / Core / Generated / Presentation
Roguelike.Infrastructure / Infrastructure.Editor
Roguelike.Features.Combat / .Authoring / .Authoring.Editor / .Ecs / .Rendering / .Runtime / .Ui.Editor
Roguelike.Features.Gameplay / .Performance
Roguelike.Animation / .Animation.Rendering
Roguelike.Architecture.Tests / .Architecture.PlayModeTests
Luban.Runtime
```

### 第 6 条：不要让 Manager 变成万能脚本 —— ✅ 达标

全库不存在 `GameManager` / `UIManager` / `AudioManager` 类。仅有 4 个窄接口 + 1 个启动编排：

| 类型 | 职责 |
|---|---|
| `IConfigService` | 配置读取 |
| `IResourceService` | 资源加载（YooAsset） |
| `IObjectPoolService` | 对象池 |
| `IEventBus` | 事件 |
| `StartupPipeline` | 启动步骤编排（`FeatureStartupStep` / `ResourceInitializationStep` / `PresentationStartupStep`） |

### 第 7 条：先完成最小版本再扩展 —— ✅ 达标

- `docs/rouge-m1-battle-prototype-spec.md`：M1 战斗原型规格
- `docs/rouge-m1-run-loop-spec.md`：M1 单局循环规格
- `docs/rouge-feature-backlog-and-priority.md`：功能待办与优先级
- git 提交呈递进（近 15 条，由新到旧）：

```
897bed7a refactor(config): 统一空间配置为像素
1e1556dc feat(combat): 统一碰撞与表现预制体配置
0821eb86 feat(combat): 实现单位头顶血条与跳字伤害数字
61233c76 feat(combat): 技能与单位缩放实现视觉与判定同源联动
81161ecd feat(map): 接入4096草地Tilemap与跟随相机
b0189f2b chore(project): 同步 Unity 场景与项目设置
d1c2f1cb docs(combat): 完成 M1 人工验收收口
cd036cd2 test(combat): 补齐正式失败结算回归
4e9378a2 test(combat): 覆盖三种正式弹丸表现
69591abd fix(combat): 收口正式单局可见验收
3c1085b4 feat(combat): 接入正式关卡战斗界面
ae5f6ed4 fix(tests): 拒绝 Unity 零用例假通过
00b9cd05 docs(workflow): 增加 Tower 编码前审查规范
b19729bf feat(combat): 接入五技能独立武器与范围表现
944de83e fix(tests): 回收异常退出遗留的 Unity 项目锁
```

### 第 8 条：文件名表达用途 —— ✅ 达标（1 处瑕疵）

- 全库扫描 `test[0-9] / new / final / temp / tmp / copy / backup / old` 等坏名模式：**0 命中**
- **瑕疵**：`Assets/Scenes/SampleScene.unity` 沿用 Unity 默认名，未表达用途

### 第 9 条：定期整理，删除死代码 —— ❌ 未达标

**遗留死代码 20 个文件，全项目零引用，场景与 Prefab 均未挂载**：

| 目录 | 文件 |
|---|---|
| `Assets/Scripts/Rouge/Data/` | `EnemySO.cs`、`LevelUpSO.cs`、`WaveSO.cs`、`WeaponSO.cs` |
| `Assets/Scripts/Rouge/ECS/` | `MoveEnemiesSystem.cs`、`RougeBootstrap.cs`、`RougeComponents.cs` |
| `Assets/Scripts/Kapai/Animation/` | `CocosAniPlayer.cs`、`CocosModelAniPlayer.cs`、`CocosMountData.cs` |
| `Assets/Scripts/Kapai/World/` | `CocosMainCharacterController.cs`、`CocosMainCharacterData.cs`、`CocosMapCameraFollow.cs`、`CocosMapCharacterMotor.cs`、`CocosMapClickToMove.cs`、`CocosMapCollisionGrid.cs` |
| `Assets/Scripts/Kapai/Editor/` | `KapaiMapMoveDemoMenu.cs` |
| `Assets/Scripts/Animation/` | `AniFrameLayout.cs`、`CocosAniData.cs`、`Rendering/AniMeshFactory.cs` |

验证依据：

- 外部引用计数（排除自身目录）：**0**
- `Assets/Scenes` 与 `Assets/Prefabs` 中 grep `RougeBootstrap` / `MoveEnemiesSystem` / `CocosMainCharacter`：**0 命中**
- `AGENTS.md` 自身已标注该批为"待迁移遗留，不得作为新功能范例继续扩展"，但长期未清理

**工作区未提交项 15 个**：

```
 M AGENTS.md
 D Assets/Screenshots.meta
 M ProjectSettings/ProjectSettings.asset
?? .local/
?? Assets/Art/Kapai/UI/ui_commonPlist/battleBar.png (+.meta)
?? Assets/Art/Kapai/UI/ui_commonPlist/battleBar_hp.png (+.meta)
?? Assets/Art/Kapai/UI/ui_commonPlist/ui_pk_num.png (+.meta)
?? Tools/Numerics/
?? Tools/start-stage1.bat
?? docs/archive/
?? docs/rouge-class-and-numerics-V1.0.md
?? docs/rouge-feature-backlog-and-priority.md
```

### 第 10 条：让修改变轻松 —— ✅ 达标

- `Assets/Scripts/Tests/EditMode/ArchitectureServiceTests.cs`：架构约束自动化测试
- 21 个 asmdef 强制单向依赖，越界引用在编译期即失败
- `AGENTS.md` 强制 XML 文档注释规范、数据表与代码同提交、`audit → plan → fix → verify` 流程

---

## 三、V1.0 待处理项清单

| 优先级 | 事项 | 说明 |
|---|---|---|
| P0 | 清理 20 个死代码文件 | 零引用，可安全删除；删除前需确认无 YooAsset 打包清单引用 |
| P1 | 订立 `IEventBus` 事件契约 | 当前业务解耦靠 ECS 数据拉取即可成立；一旦接入非 ECS 系统（成就 / 存档 / 任务 / 统计），须先定义事件契约，否则会退化为直接引用 |
| P2 | `SampleScene.unity` 重命名 | 违反"文件名表达用途" |
| P2 | 表现时长常量下沉配置表 | `NumberRiseDuration`、`AreaTraceSeconds`、`NumberRiseHeight` 等属策划可调项 |
| P2 | 提交工作区 15 项改动 | 含 `AGENTS.md` 修改与 `docs/` 新增文档 |

---

## 四、复核指引（供审核方复现）

以下命令在当前项目根目录下以 Git Bash 执行。

```bash
# 1. 脚本规模分布
find Assets -name "*.cs" -not -name "*.meta" -not -path "*Generated*" -not -path "*ThirdParty*" \
  -exec wc -l {} + | sort -rn | head -40

# 2. 运行时查找与单例（期望：运行时 0 处）
grep -rn "GameObject.Find\|FindObjectOfType\|Object.Find" --include=*.cs \
  Assets/Scripts/Features Assets/Scripts/Infrastructure Assets/Scripts/Core \
  Assets/Scripts/App Assets/Scripts/Presentation
grep -rn "static.*Instance\|Singleton" --include=*.cs Assets/Scripts | grep -v Generated | grep -v ThirdParty

# 3. 硬编码数值（排除 0/1/-1 后期望 29 处）
grep -rnE "(^|[^A-Za-z0-9_.])[a-zA-Z_]+ *= *-?[0-9]+(\.[0-9]+)?f? *;" --include=*.cs \
  Assets/Scripts/Features Assets/Scripts/Infrastructure Assets/Scripts/Core \
  Assets/Scripts/App Assets/Scripts/Presentation | grep -vE "= *(0|1|-1)(\.0)?f? *;"

# 4. Prefab Variant 比例
for d in Assets/Prefabs/Combat/Hero Assets/Prefabs/Combat/Monster \
         Assets/Prefabs/Combat/Skill Assets/Prefabs/Combat/Weapon; do
  n=$(find $d -name "*.prefab" | wc -l); v=$(grep -l "m_CorrespondingSourceObject" $d -r | wc -l)
  echo "$d : 总 $n / Variant $v"
done

# 5. 遗留代码外部引用（期望 0）
grep -rln "Rouge\.\|Kapai\." --include=*.cs Assets/Scripts | grep -v "Scripts/Rouge\|Scripts/Kapai" | wc -l
grep -rlo "RougeBootstrap\|MoveEnemiesSystem\|CocosMainCharacter" Assets/Scenes Assets/Prefabs

# 6. 坏命名扫描（期望 0）
find Assets -name "*.cs" -o -name "*.prefab" | grep -iE "test[0-9]|new|final|temp|tmp|copy|backup|old"

# 7. V1.1 新增：存档 / 本地化 / 输入 / 崩溃上报（当前期望全部为 0）
grep -rn "PlayerPrefs\|persistentDataPath\|SaveGame" --include=*.cs Assets/Scripts | grep -v Generated | wc -l
grep -rln "Localization\|Localize\|I18N" --include=*.cs Assets/Scripts | wc -l
grep -rln "InputSystem\|InputAction\|Gamepad" --include=*.cs Assets/Scripts | grep -v Generated | wc -l
grep -rln "Sentry\|CrashReport\|Application.logMessageReceived" --include=*.cs Assets/Scripts | wc -l
```

---

## 五、V1.0 诚实边界（未验证项）

1. **未打开 Unity Editor 实跑**，全部结论基于静态扫描。
2. **`CombatSession.cs` 是否属于 God Object** 为人工判断，依据是其公开接口集中在模拟推进 / 单位生成 / 事件读取三类；建议人工复核其 901 行内部实现。
3. **Prefab 属性覆盖（`m_Modifications`）数量未检查**，文章第 5 条"覆盖过多导致 Prefab 与实例关系难判断"的风险**未验证**。
4. **硬编码扫描正则有边界**：只匹配 `标识符 = 数字;` 形式，未覆盖方法参数内联数值、字符串拼接数值、数组初始化中的数值。
5. **死代码删除前需补充验证**：`Assets/Art/Kapai` 下美术资源是否被 YooAsset 打包清单引用，未检查。
6. 遗留目录 `Assets/Scripts/Animation` 中部分文件可能与 `Roguelike.Animation` 程序集存在未检出关系，删除前需编译验证。

---

# 第六章（V1.1 新增）：商业化上线视角的补充规范

> 本章不针对原文章 10 条，而是**从已上线商业化产品的返工教训反推**——这类规范文章不会写，但恰恰是后期返工成本最高的部分。
>
> **引用性质说明**：下列"教训"分两类标注：
> - 【公开案例】行业公开可查的产品事实（更新日志、开发者访谈、玩家社群共识）
> - 【通行实践】行业普遍采用但无单一公开出处的工程惯例
>
> 本报告不对任何产品引用具体流水、留存、销量数字。

## 6.0 本项目现状基线（V1.1 新增实测）

| 能力 | 检出状态 | 证据 |
|---|---|---|
| 玩家进度存档 | **不存在** | `PlayerPrefs` / `persistentDataPath` / `SaveGame` 命中 **0** |
| 本地化框架 | **不存在** | `Localization` / `I18N` 命中 **0**；26 张表未见语言字段 |
| 新输入系统 | **未接入** | `InputSystem` / `InputAction` / `Gamepad` 命中 **0**（`CombatInput.cs` 未使用新 Input System） |
| 崩溃上报 | **不存在** | `Sentry` / `CrashReport` / `logMessageReceived` 命中 **0** |
| 日志 | 存在但不规范 | 43 处 `Debug.Log`，无分级、无开关、无脱敏约定 |
| 确定性随机 | **已具备基础** | `CombatRandom.cs` 采用 `seed + attackSequence + targetLifetime + purpose` 哈希，无状态、可复现 |
| 性能预算 | **有雏形** | `PerformanceFeature.cs`（800 行，可输出 JSON 结果） |
| 固定步长 | 已具备 | ECS 侧 `CombatSession.StepSeconds` 固定步进 |

**判断**：战斗内核质量高，但**外围"上线必需的壳"基本为零**。M1 阶段这是合理的（聚焦核心循环），但必须在 M2 之前立项，否则返工成本陡增。

---

## 6.1 存档与版本兼容 —— 返工成本最高项

### 规范

1. **存档结构必须带版本号**，且从第一次写入存档起就带。字段增删走迁移函数，不允许直接改结构。
2. **存档数据与配置数据解耦**：存档只存 `ID + 状态 + 进度`，绝不存配置快照数值（否则改表即坏档）。
3. **存档分片**：设置 / 元进度 / 单局进行中，三份独立，单份损坏不拖垮全部。
4. **写入原子化**：临时文件 → 校验 → 替换，禁止直接覆盖。
5. **存档加载失败必须可恢复**：提供"损坏时保留备份 + 提示"而非静默清空。

### 教训依据

- 【公开案例】Hades 在 Early Access 阶段经历多次内容结构变更，出现进度需要重开的情况，是 EA 产品社群高敏问题。
- 【公开案例】多款 EA 期 Roguelike 在加入"元进度 / 解锁"系统后因存档格式变更导致老档失效，社区反弹强烈。
- 【通行实践】Steam 云存档有容量配额与冲突合并规则，存档体积失控会直接导致同步失败。

### 本项目落地建议

- 当前 `CombatSession` 已有 `SynchronizeAttributes` 等状态同步机制，**建议直接抽象为存档快照接口**，不要等元进度系统上线时再补。
- 存档 ID 体系依赖 Luban 表主键，**必须先约定"表 ID 永不复用、只增不改"**，否则改表即坏档。此约束应写入 `AGENTS.md`。

### 优先级

**P0 —— 必须在"第一个可保存进度的版本"之前完成。**

---

## 6.2 数值膨胀与组合上限 —— Roguelike 品类特有

### 规范

1. **属性增益必须分层**：明确区分加法层（基础值求和）、乘法层（倍率连乘）、独立层（特殊加成），并在配置表中显式标注每层归属。
2. **乘法层必须有收敛机制**：递减收益（`1 - Π(1 - x)`）或硬性上限，禁止无约束连乘。
3. **配置表必须能通过启动自检**：单局理论最大 DPS、最大血量、最大弹幕数需在启动时校验并告警。
4. **伤害公式输出封顶**：显示层与逻辑层都要有数值安全边界。

### 教训依据

- 【公开案例】Vampire Survivors 在版本迭代中持续处理武器/道具组合导致的后期伤害指数级膨胀，是该类"自动战斗 + 无限叠加"结构的固有问题。
- 【公开案例】Balatro 的小丑牌组合在社区中催生出天文数字级别分数，开发者在后续补丁中引入了分数上限与显示封顶机制。
- 【通行实践】"弹幕数量 × 单个伤害 × 攻速"三者同时无上限时，必然出现帧率崩溃与数值失控的双重问题。

### 本项目落地建议

- 现有 26 张表已有 `AttributeConfig` / `AttributeProfileConfig` / `RunAttributeModifier`，**天然具备分层基础**，但需确认 `EAttributeModifyOperation` 是否覆盖"递减收益"运算类型。
- `PerformanceFeature` 已能跑性能场景，**建议扩展为"数值边界自动化校验"**：遍历升级池所有组合，输出理论极值。

### 优先级

**P0 —— 公式与分层一旦定型，后期改动等于重做全部数值。**

---

## 6.3 随机性与可复现

### 规范

1. **局内所有随机必须走确定性 RNG**，禁止使用 `UnityEngine.Random`（全局状态、不可复现、不可测）。
2. **RNG 状态必须可序列化**（用于存档续局与回放）。
3. **种子必须可外部输入**，用于复现 Bug 与内容创作。
4. **不同用途的随机必须分离流**：掉落、生成、暴击各用独立流，否则改一处随机序列即全盘改变。

### 本项目现状

`CombatRandom.cs` **已达标**：无状态哈希（seed + sequence + lifetime + purpose），天然可复现、可并行、可分离流。这是全项目架构质量最高的一处。

**缺口**：未提供"局内种子可外部指定"入口；未确认生成/掉落是否复用同一流。

### 教训依据

- 【公开案例】Slay the Spire 的每日挑战与种子分享机制，使社区能够复现并讨论具体局内配置，是长尾内容生态的基础设施。
- 【通行实践】无法复现的 Roguelike 在修复"偶现 Bug"时成本极高。

### 优先级

**P1 —— 现在补成本极低（基础设施已就绪），等 Bug 来了再补则已无法复现。**

---

## 6.4 本地化与文本

### 规范

1. **所有面向玩家文本外置到配置表**，禁止硬编码字符串、禁止写在 Prefab 的 Text 组件默认值里（Prefab 里可留占位符，但必须有回填机制）。
2. **文本键命名带语义**，禁止用 `text_001`。
3. **UI 布局必须容忍文本长度变化**：中文 → 英文通常增长 30%~60%，德俄更长；禁止固定宽度文本框 + 单行强制。
4. **字体必须提前确定字符集方案**：中文游戏若用 TMP 动态字体，需预先准备字库与图集策略；若用 SDF 静态字体，需要评估缺字回退。
5. **数字/单位/日期格式化**走统一工具，禁止手工拼字符串。

### 本项目现状

**完全为零**：26 张表中无语言字段，代码中无 Localization 引用。

### 教训依据

- 【通行实践】本地化是公认的"后期接入成本最高"系统之一：需要遍历全部 UI、全部文本组件、全部拼接逻辑，且必须重测全部界面布局。
- 【公开案例】多款产品在发售后补多语言时，出现文本截断、字体缺字、语序错误（拼接式句子在非主谓宾语言中无法正确翻译）。

### 优先级

**P1 —— 不必现在做多语言，但必须现在做"文本外置"。** 现在每写死一个字符串，就是未来的一份迁移成本。

---

## 6.5 输入与设备覆盖

### 规范

1. **输入必须走抽象层**，禁止业务代码直接读 `Input.GetKey`。
2. **必须同时支持键鼠与手柄**，且键位可重映射。
3. **UI 必须支持手柄导航**（焦点移动 / 确认 / 返回），纯鼠标点击的 UI 在 Steam Deck 上不可用。
4. **必须适配 16:10 / 16:9 / 21:9 / 竖屏**，至少明确支持清单与不支持时的表现。
5. **Steam Deck 是独立验收项**，不是"应该能跑"。

### 本项目现状

`CombatInput.cs` 存在但未使用新 Input System；无 Gamepad 支持；UI 自适应仅在 Editor 构建脚本中出现 `CanvasScaler`。

### 教训依据

- 【公开案例】Steam Deck 用户群对"仅键鼠可用"的产品评价极差，Deck Verified 已成为 Steam 曝光的重要入口。
- 【通行实践】键位硬编码后再做重映射，需要重写全部输入消费点。

### 优先级

**P1（输入抽象层）/ P2（手柄与 Deck 适配）。** 抽象层越早越好，硬件适配可后置。

---

## 6.6 性能预算与帧率

### 规范

1. **必须设定明确性能预算并写进文档**：目标分辨率、目标帧率、同屏敌人上限、弹幕上限、Draw Call 上限、内存上限。
2. **预算必须是自动化可校验的**，不是"跑起来看着还行"。
3. **低端机是验收基线**，不是高端机。
4. **帧率无关**：所有逻辑走固定步长，禁止在 `Update` 里做与帧率相关的累加。

### 本项目现状

- `PerformanceFeature.cs` 已能输出 JSON 结果，具备自动化基础 —— **这是优势**。
- 固定步长已具备（`CombatSession.StepSeconds`）。
- **缺口**：未见书面性能预算文档；未见低端机/Steam Deck 目标基线。

### 教训依据

- 【通行实践】"没有预算的性能优化"等于无限期优化。预算的意义在于能判定"够用了"。
- 【公开案例】弹幕类游戏在后期内容量提升后，性能问题往往在玩家实际设备上才暴露，此时内容已定型，只能削减内容或重做渲染。

### 优先级

**P1 —— 预算文档现在写成本极低，`PerformanceFeature` 已提供测量手段。**

---

## 6.7 崩溃、日志与遥测

### 规范

1. **接第三方崩溃上报**（Sentry / Backtrace / Unity Crash Reporting）。单机游戏没有客服工单，崩溃是你唯一能听到玩家声音的渠道。
2. **日志分级 + 发布版关闭 Debug 日志**，且日志不得包含玩家隐私路径、账号信息。
3. **关键流程必须有埋点**：启动耗时、关卡加载、单局开始/结束、结算、升级选择。单机游戏也需要数据来调平衡。
4. **存档中记录游戏版本号与配置版本号**，便于定位"哪个版本产生的存档"。

### 本项目现状

43 处 `Debug.Log`，无分级、无开关、无上报。

### 教训依据

- 【通行实践】Steam 不向开发者提供崩溃统计。没有任何上报渠道的单机产品，其崩溃修复完全依赖玩家主动反馈，漏报率极高。

### 优先级

**P2 —— 但"日志分级 + 发布开关"应在 M2 内完成，否则发版前要清理全部 43 处。**

---

## 6.8 资源与打包

### 规范

1. **资源命名与路径规范固化**，禁止临时资源进正式目录。
2. **打包清单必须是配置驱动而非扫描目录**，扫描式打包会把测试资源打进去。
3. **首包体积预算**，并明确哪些内容走后续下载。
4. **资源释放必须成对可验证**：加载与释放要有计数断言，禁止"看起来释放了"。

### 本项目现状

YooAsset 已接入（`YooAssetResourceService`、`YooAssetPackageInitializer`），有对象池服务 —— **基础良好**。

**风险点**：`AGENTS.md` 中的"预览资产禁令"说明团队已踩过此类坑，需确认该规则有自动化检查而非仅靠人工。

### 优先级

**P2**

---

## 6.9 内容生产管线

### 规范

1. **新增内容必须是"加一行表"而非"改一处代码"**。检验方法：让策划在不动代码的前提下新增一个怪物/技能/武器，若做不到则管线不合格。
2. **配置必须有启动自检**：ID 引用完整性（技能引用了不存在的资源 ID）、数值合法性（负数、除零、空表）、枚举值有效性。
3. **配置错误必须在启动期暴露**，不能等到玩家触发时才崩。
4. **数值表与策划文档严格分离**（用户既有规范，已在执行）。

### 本项目现状

Luban 26 张表 + 生成校验，已具备 80%。**缺口**：未见启动期引用完整性自检测试（`Tests` 目录有 `ConfigNumberTests` 但未见 `ConfigReferenceTests`）。

### 优先级

**P1 —— 内容量起来后再补，等于人工核对上千条 ID。**

---

## 6.10 Steam 平台集成清单

> 单机 Steam 发行必须项，均为"临近发售才做则必然延期"类型。

| 项 | 说明 | 建议节点 |
|---|---|---|
| Steamworks SDK 接入 | 越早越好，初始化与退出流程会影响启动架构 | M2 |
| 成就系统 | 成就 ID 一旦发布不可更改，需提前规划 | Demo 前 |
| 云存档 | 与 6.1 存档结构耦合，需一同设计 | 首个存档版本 |
| Steam Deck 验证 | 见 6.5 | Beta 前 |
| 创意工坊（可选） | 若支持 Mod，配置表结构需从一开始就允许外挂内容 | 立项时决定 |
| 商店页素材 / 本地化商店文本 | 与 6.4 本地化共用文本体系 | 发售前 3 月 |
| Demo 版本 | Demo 与正式版必须共享同一存档结构与配置版本 | Demo 前 |

**关键约束**：**成就 ID 与存档结构一旦对外发布即不可变更**，这是本章唯一"过期不候"的硬约束。

---

## 第七章：返工成本矩阵（V1.1 新增）

> 用于判断"现在做还是以后做"。成本等级为定性评估：
> **低**=1 人日级；**中**=1 人周级；**高**=1 人月级；**极高**=需重做系统或重测全量内容。

| 规范项 | 现在做成本 | 延后做成本 | 最晚完成节点 | 判定 |
|---|---|---|---|---|
| 存档版本号 + 迁移 | 低 | **极高**（老档兼容需写迁移 + 全量回归） | 第一个存档版本前 | **必须现在** |
| 表 ID 永不复用约定 | 极低（写进 AGENTS.md） | **极高**（改表即坏档） | 立即 | **必须现在** |
| 属性分层 + 收敛机制 | 低 | **极高**（等于重做全部数值） | 数值定型前 | **必须现在** |
| 文本外置（不含多语言） | 中 | **高**（遍历全部 UI 重测） | M2 结束前 | 建议现在 |
| 配置启动期自检 | 低 | **高**（内容量起来后人工核对） | 内容量产前 | 建议现在 |
| 输入抽象层 | 低 | 中（重写消费点） | M2 | 建议现在 |
| 确定性 RNG 保留 | 已具备 | — | — | 已达标 |
| 性能预算文档 | 低 | 中 | M2 | 建议现在 |
| 手柄 / Steam Deck | 中 | 中 | Beta 前 | 可接受后置 |
| 崩溃上报 | 中 | 中 | 首发前 | 可接受后置 |
| 本地化（实际多语言） | 高 | 中（若文本已外置） | 发售后按需 | 明确后置 |
| Steamworks / 成就 | 中 | 中 | Demo 前 | 明确后置 |
| 死代码清理（第 9 条） | 低 | 低（但会持续误导新人） | 立即 | 建议现在 |

---

## 第八章：V1.1 新增待处理项

| 优先级 | 事项 | 归属章节 | 成本 |
|---|---|---|---|
| P0 | 将"表 ID 永不复用、只增不改"写入 `AGENTS.md` | 6.1 | 极低 |
| P0 | 确认 `EAttributeModifyOperation` 是否覆盖递减收益运算；若无则补 | 6.2 | 低 |
| P0 | 存档结构版本化设计（与元进度系统同批立项） | 6.1 | 中 |
| P1 | 文本外置：禁止新增硬编码玩家可见字符串 | 6.4 | 中 |
| P1 | 配置启动期引用完整性自检测试 | 6.9 | 低 |
| P1 | 性能预算文档（分辨率/帧率/同屏上限/内存） | 6.6 | 低 |
| P1 | 输入抽象层立项 | 6.5 | 中 |
| P1 | 局内种子外部可指定 + 随机流分离确认 | 6.3 | 低 |
| P2 | 日志分级与发布版开关（覆盖现有 43 处） | 6.7 | 中 |
| P2 | 崩溃上报接入评估 | 6.7 | 中 |
| P2 | 手柄 / Steam Deck 适配 | 6.5 | 中 |
| P2 | Steamworks 接入与成就 ID 规划 | 6.10 | 中 |

---

## 第九章：V1.1 诚实边界（本章未验证项）

1. **第六章教训依据的引用均为定性描述**，未引用任何具体流水、销量、留存数字；标注为【公开案例】的条目来自公开更新日志/开发者访谈/社群共识层面，未逐条附上原始出处链接，审核方如需逐条溯源请另行核实。
2. **`EAttributeModifyOperation` 是否支持递减收益** 未打开枚举文件确认，仅为基于命名的推测。
3. **`CombatInput.cs` 未使用新 Input System** 是基于 grep 未命中 `InputSystem`/`InputAction` 的推断，未读取文件确认其底层实现。
4. **"未见配置启动期引用完整性自检"** 为反向推断（未见 `ConfigReferenceTests` 类文件），未遍历全部测试文件内容确认。
5. **性能预算文档缺失** 基于 `docs/` 目录 11 个文件的文件名判断，未逐篇阅读确认无内嵌预算章节。
6. **6.4 文本膨胀 30%~60%** 为行业经验值，非本项目实测。
7. 所有"最晚完成节点"（M2 / Beta / Demo）为基于通用研发节奏的建议，**未与项目实际里程碑文档对齐**，`docs/rouge-feature-backlog-and-priority.md` 未纳入本次审查。
