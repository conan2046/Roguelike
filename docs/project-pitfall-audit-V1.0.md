# Rogue 项目开发坑位清单 V1.0

> [!WARNING]
> **本文件已作废（2026-09-10）。** V1.0 存在 5 处事实错误：
> 坑 2 机制错误（asmdef 不是 DOTS 自动发现根因）、坑 7 事实错误（表 ID 规范已存在于 `Config/README.md:129`）、
> "零运行时 Find"绝对化（`PerformanceFeature.cs:435` 有 `FindObjectsOfType<Camera>()`）、
> "正式链无硬编码"（`CombatFeedbackVisuals.cs:23` 有资源 ID）、P0 等级滥用。
> 且**漏掉了真正的 P0：Windows Offline 构建在当前 HEAD 必然失败**。
> **请改用 V2.0：`docs/project-pitfall-audit-V2.0.md`。**

- 审查日期：2026-09-10
- 审查对象：当前项目根目录（Unity + Entities 1.0 + Luban + YooAsset，Steam 单机 Survivor-like）
- 快照：commit `897bed7a489f16b4158f3214fbbf944774898514`，分支 `main`
- 用途：**给评测方用的坑位清单**。每条给出现象、证据、触发条件、后果。
  评测时请优先验证「证据」列是否成立，再判断「后果」是否被高估或低估。
- 方法：静态扫描 + 源码阅读 + 自研可达性审计工具；**未运行 Unity Editor、未编译、未跑测试**

---

## 一句话结论

项目架构骨架健康（21 程序集分层、Luban 唯一数据源、确定性随机、零运行时 Find 与单例），
但存在三类实质性坑：**DOTS 自动发现污染、遗留资产与正式链同程序集扫描范围、打包为目录扫描**。
其中第一类已经产生实际运行时后果。

---

## 一、已确凿的坑（证据已复核）

### 坑 1【P0】遗留 ECS 系统被自动发现并每帧执行

| 项 | 内容 |
|---|---|
| 现象 | `MoveEnemiesSystem` 是 M1.1 验证系统，仍会被加入战斗 World 并每帧 `OnUpdate` |
| 证据 | `Assets/Scripts/Rouge/ECS/MoveEnemiesSystem.cs:12` 声明 `public partial class MoveEnemiesSystem : SystemBase`，**无** `[DisableAutoCreation]`；`Assets/Scripts/Features/Combat/Runtime/CombatRuntimeRunner.cs:159` 使用 `DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default)` |
| 触发条件 | 每次进入战斗 |
| 后果 | 该系统参与系统发现、创建、查询生命周期。当前可能查不到 `EnemyTag` 实体，但每帧仍有一次查询调度开销；且其依赖的组件定义若变更，会在战斗 World 初始化期引入不确定性 |
| 处置方向 | 加 `[DisableAutoCreation]`，或迁出被扫描范围。**不是删文件** |

### 坑 2【P0】无 asmdef 目录的代码会落入自动扫描范围

| 项 | 内容 |
|---|---|
| 现象 | `Assets/Scripts/Rouge/` 与 `Assets/Scripts/Kapai/` **没有任何 asmdef**，编译进 `Assembly-CSharp` 默认程序集 |
| 证据 | `find Assets/Scripts/Rouge Assets/Scripts/Kapai -name "*.asmdef"` 返回空；`Assets/Scripts/` 根目录无 asmdef |
| 触发条件 | `GetAllSystems(WorldSystemFilterFlags.Default)` 扫描所有已加载程序集，含 `Assembly-CSharp` |
| 后果 | **未来任何写在这两个目录（或任意无 asmdef 区）的 `SystemBase` / `ISystem`，都会自动进入战斗 World**，无需任何引用。这是一个会持续复发的坑，不是一次性问题 |
| 处置方向 | 给遗留目录加 asmdef 并排除，或整体移出 `Assets/` |

**坑 1 与坑 2 是同一机制的表与里**：坑 1 是当前实例，坑 2 是复发通道。只修坑 1 不修坑 2，下次写 ECS 还会踩。

### 坑 3【P0】YooAsset 正式内容走目录扫描打包

| 项 | 内容 |
|---|---|
| 现象 | `GameContent` 使用 `PackDirectory` 整目录收集 |
| 证据 | `Assets/Scripts/Infrastructure/Editor/ProjectYooAssetSetup.cs:73` `AddCollector(package, "GameContent", GameContentPath, nameof(PackDirectory), string.Empty)` |
| 触发条件 | 每次打包 |
| 后果 | 任何临时放进 `GameContent` 的测试资源、废弃资源都会进包；无法从清单层面证明"包内无 junk" |
| 处置方向 | 改为显式资源清单或增加收集期校验 |

### 坑 4【P1】遗留链存在业务硬编码

| 项 | 内容 |
|---|---|
| 证据 | `Assets/Scripts/Rouge/ECS/RougeBootstrap.cs:20` `public int enemyCount = 200;`，另有生成范围与移动速度 |
| 后果 | 现代正式链已数据驱动，遗留链是例外。若后续有人参照遗留链写新代码，会破坏 `AGENTS.md` 的数据驱动约束 |
| 说明 | 现代链（`Features/`）业务数值硬编码未检出，此项**不影响当前正式玩法** |

### 坑 5【P1】表现时长常量在代码里，策划不可调

| 项 | 内容 |
|---|---|
| 证据 | `CombatFeedbackVisuals.cs` 中 `NumberRiseDuration = 0.6f`、`NumberRiseHeight = 0.35f`、`AreaTraceSeconds = 0.4f` 等 |
| 后果 | 手感调参需要程序介入；同类常量共 29 处（其余为渲染技术常量，如纹理尺寸、sortingOrder，可保留） |
| 处置方向 | 时长/位移类下沉到 `TbCombatPresentation` 或新增表现参数表 |

### 坑 6【P1】日志无分级、无发布开关

| 项 | 内容 |
|---|---|
| 证据 | 43 处 `Debug.Log`，无分级封装、无发布版关闭机制、无脱敏约定 |
| 后果 | 单机游戏没有客服工单，日志是少数可获取现场信息的渠道；当前状态下既无法按需采集，也无法保证不泄露本机路径 |
| 处置方向 | 统一日志门面 + 级别 + 发布开关 |

### 坑 7【P1】表 ID 稳定性约定缺失，会在 M4 存档时爆雷

| 项 | 内容 |
|---|---|
| 现象 | `AGENTS.md` 未写入"表 ID 永不复用、只增不改"约束 |
| 证据 | 全库检索 `AGENTS.md` 无相关条款；`docs/rouge-feature-backlog-and-priority.md` 中 #9 存档系统排在 **M4** |
| 触发条件 | M4 做存档时，若此前有表 ID 复用或删除，历史存档将无法正确还原 |
| 后果 | 存档兼容问题的修复成本极高，且此时内容量已大 |
| 处置方向 | **现在写进 `AGENTS.md`，成本接近零** |

### 坑 8【P1】确定性随机的边界未锁定

| 项 | 内容 |
|---|---|
| 现状 | `CombatRandom.cs` 采用 `seed + sequence + lifetime + purpose` 无状态哈希，质量高，**随机抽样可复现** |
| 坑 | "随机函数确定" ≠ "整局模拟确定"。未锁定：实体遍历顺序、浮点策略、Jobs/Burst 差异、配置版本、全部 sequence counter |
| 触发条件 | 需要做存档续局、回放、种子分享时 |
| 后果 | 届时才发现无法复现，返工成本高 |
| 处置方向 | 先明确范围（存档恢复一致 / 同版本回放一致 / 跨版本一致 / 跨硬件一致），再按需锁定 |

### 坑 9【P1】`IEventBus` 已建但未接入业务

| 项 | 内容 |
|---|---|
| 证据 | 全库仅 5 处引用（定义、实现、Bootstrap、测试）。当前战斗解耦靠 ECS 数据拉取（`ReadDeath` / `ReadImpact` / `ReadArea`） |
| 后果 | 现在够用；但接入非 ECS 系统（成就 / 存档 / 任务 / 统计）时若无契约，会退化成直接引用 |
| 处置方向 | 等真要接非 ECS 系统时订契约，不必现在做 |

### 坑 10【P2】Prefab 平铺，无 Variant

| 项 | 内容 |
|---|---|
| 证据 | 208 个 Prefab，`Monster` 79 个、`Skill` 93 个各自独立；全库未检出 `PrefabInstance:` 文档，Variant 数为 0 |
| 后果 | 公共结构变更理论上需改 79 / 93 个文件 |
| 重要限定 | 这些大概率是**生成管线产物**（`CombatCollisionPipeline`、`CombatHealthBarPrefabBuilder` 等从配置生成）。若管线可靠，平铺不是问题，**改管线即可，不应手工改 Prefab** |
| 处置方向 | 先确认生成管线覆盖范围，再决定是否引入 Variant |

---

## 二、待评测方验证的坑（我未确认，可能不存在）

以下为**怀疑项**，我没有足够证据，请评测方优先验证：

| # | 怀疑项 | 为什么怀疑 | 建议验证方式 |
|---|---|---|---|
| B1 | `CombatSession` 901 行是否为 God Object | 同时承担 ECS 容器所有权、实体池、生成定位、容量推导、状态同步、事件缓存、重启与释放 | 变更原因分析：列出每类变更会触及的字段；检查能否按职责切分 |
| B2 | Prefab override 是否失控 | 208 个 Prefab 均未检查 `m_Modifications` | Editor 打开抽查，或脚本统计 override 字段数 |
| B3 | 配置校验是否覆盖全内容 | `CombatRunDefinition.Create` 已校验**选中关卡**，未确认是否校验全部表内容 | 检查是否有全表扫描 CI |
| B4 | 是否存在 asmdef 循环依赖 | 21 个程序集未做依赖图分析 | 生成依赖图检测环 |
| B5 | 反射 / `ICustomBootstrap` / YooAsset 地址表是否引用了 ISOLATED 脚本 | 审计工具未覆盖这三类入口 | grep 反射 API 与 collector 配置 |
| B6 | 表与 Prefab 双向同步冲突 | `CombatCollisionPipeline` 注释称"Prefab 是编辑源，Luban 表已更新"，为单向导出 | 确认手工改表后是否会被管线静默覆盖 |

---

## 三、明确不是坑（避免评测方误改）

| 项 | 说明 |
|---|---|
| 21 个程序集分层 | 是优势，强制依赖方向，不要合并 |
| `CombatRandom` 无状态哈希 | 设计正确，不要改成有状态 RNG |
| 固定步长 `CombatSession.StepSeconds` | 正确，不要改成帧率相关 |
| `Rouge/Data` 4 个 SO | 带 `[CreateAssetMenu]`，是编辑器资产入口，不是死代码 |
| `MCPDemo.unity` 及其 5 个挂载脚本 | `README.md:32` 记载为有意保留的 Kapai 迁移验证资产 |
| 战斗 UI 文本外置到 `TbCombatUiSet` | 已数据驱动，多语言框架可后置 |
| `ICombatInput` 抽象 | 已存在，缺的是 Action Map 与手柄，不是抽象层 |

---

## 四、复现命令

```bash
# 坑 1/2：确认遗留目录无 asmdef + 系统无 DisableAutoCreation
find Assets/Scripts/Rouge Assets/Scripts/Kapai -name "*.asmdef" | wc -l        # 期望 0
grep -rn "DisableAutoCreation" --include=*.cs Assets/Scripts
grep -rn "GetAllSystems" --include=*.cs Assets/Scripts

# 坑 3：打包方式
grep -n "AddCollector" Assets/Scripts/Infrastructure/Editor/ProjectYooAssetSetup.cs

# 坑 4/5：硬编码
grep -n "enemyCount" Assets/Scripts/Rouge/ECS/RougeBootstrap.cs
grep -rnE "[a-zA-Z_]+ *= *-?[0-9]+(\.[0-9]+)?f? *;" --include=*.cs \
  Assets/Scripts/Features | grep -vE "= *(0|1|-1)(\.0)?f? *;"

# 坑 6：日志
grep -rn "Debug.Log" --include=*.cs Assets/Scripts | grep -v Generated | wc -l

# 坑 10：Prefab Variant
grep -rl "^PrefabInstance:" Assets/Prefabs | wc -l                              # 期望 0
find Assets/Prefabs -name "*.prefab" | wc -l

# 坑 7：AGENTS.md 是否有表 ID 约定
grep -n "ID" AGENTS.md | grep -i "复用\|reuse\|stable"
```

---

## 五、本报告自身的边界（请评测方一并审视）

1. **未运行 Unity Editor、未编译、未跑 EditMode/PlayMode/Standalone/Profiler**。所有"会执行、会进包、能复现"的判断均为静态推断。
2. **坑 1 的"每帧执行"未经运行时确认**。严格说，证据只支持"会被自动创建并加入 World"，是否产生实际开销需在 Systems 窗口或 World 系统列表确认。
3. 审计工具未覆盖：反射、`ICustomBootstrap`、YooAsset collector 地址表、非 `.cs` 文本（JSON/YAML/VDF/PS1）、超过 8MB 的文件、`.meta` 作为引用源。
4. 代码引用匹配使用**非限定类型名**，存在同名误报与注释误报可能。
5. 工作区存在未提交改动，commit SHA 只能固定已提交部分。
6. 本报告未标注每条结论的置信度，评测方如对某条存疑，请以「证据」列为准重新验证。
