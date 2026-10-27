# Rogue 项目可维护性审查报告 V1.0

> [!WARNING]
> **本文件已作废（2026-09-10）。**
> V1.0 与 V1.1 存在多处硬事实错误，经外部审核与 GUID 审计复验确认，最危险的是"20 个死代码可安全删除"——实际只有 3 个候选。
> **请改用 V1.3：`docs/architecture-maintainability-review-V1.3.md`。不要引用本文件的任何结论。**

- 审查日期：2026-09-10
- 审查对象：当前项目根目录（Unity + Entities 1.0 + Luban + YooAsset）
- 审查依据：微信公众号文章《Unity项目从能运行到好维护的开发思路》（SiKi，2026-09-09）提出的 10 条规范
- 审查方法：静态扫描（文件树统计、行数统计、正则 grep、Prefab YAML 字段检查）；**未打开 Unity Editor 实跑**

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

提交格式符合 `AGENTS.md` 的 `<type>(<scope>): <中文摘要>` 要求。

### 第 8 条：文件名表达用途 —— ✅ 达标（1 处瑕疵）

- 全库扫描 `test[0-9] / new / final / temp / tmp / copy / backup / old` 等坏名模式：**0 命中**
- 唯一含 temp 关键词的文件为 `Infrastructure/Editor/OfflineWindowsBuild.cs`，属合理命名
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
| `Assets/Scripts/Animation/` | `AniFrameLayout.cs`、`CocosAniData.cs`（`Animation/Rendering/AniMeshFactory.cs` 同属） |

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

## 三、待处理项清单

| 优先级 | 事项 | 说明 |
|---|---|---|
| P0 | 清理 20 个死代码文件 | 零引用，可安全删除；删除前需确认无 Addressables/YooAsset 打包清单引用 |
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
```

---

## 五、诚实边界（本报告未验证项）

1. **未打开 Unity Editor 实跑**，全部结论基于静态扫描。
2. **`CombatSession.cs` 是否属于 God Object** 为人工判断，依据是其公开接口集中在模拟推进 / 单位生成 / 事件读取三类；建议人工复核其 901 行内部实现。
3. **Prefab 属性覆盖（`m_Modifications`）数量未检查**，文章第 5 条提到的"覆盖过多导致 Prefab 与实例关系难判断"这一风险**未验证**。
4. **硬编码扫描正则有边界**：只匹配 `标识符 = 数字;` 形式，未覆盖方法参数内联数值、字符串拼接数值、数组初始化中的数值。
5. **死代码删除前需补充验证**：`Assets/Art/Kapai` 下美术资源是否被 YooAsset 打包清单或 Addressable Group 引用，本报告未检查。
6. 遗留目录 `Assets/Scripts/Animation` 中部分文件可能与 `Roguelike.Animation` 程序集存在未检出关系，删除前需编译验证。
