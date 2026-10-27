# Rogue 项目开发坑位清单 V2.0

- 审查日期：2026-09-10
- 快照：commit `897bed7a489f16b4158f3214fbbf944774898514`，分支 `main`
- 版本：**V2.0 为当前有效版本**，V1.0 已作废（文件内已加横幅）
- 方法：静态扫描 + 源码阅读 + 自研可达性审计工具；**未运行 Unity Editor、未执行构建、未跑测试**
- 用途：给评测方使用。每条给出**证据位置**，请先验证证据是否成立，再判断后果是否被高估或低估

---

## 第零章：等级定义与三维度

V1.0 未定义 P0/P1/P2，导致"当前玩家损害""未来返工成本""实施时间门槛"混在一起。V2.0 拆分：

**影响等级**（出问题后损失多大）

| 等级 | 定义 |
|---|---|
| P0 | 阻断构建、阻断发布，或造成已发布功能不可用 |
| P1 | 造成明确的功能缺陷、性能损失或可预见的重大返工 |
| P2 | 增加维护成本或技术债，不产生当前可见损害 |
| P3 | 条件项：仅在某些前提成立时才是问题 |

**最迟处理节点**：首发前 / Demo 前 / 内容量产前 / 存档前 / 任意时点

**当前研发优先级**：M2 阶段是否需要投入（高 / 中 / 低 / 不投入）

三条列独立看。**排期看"当前研发优先级"，不要看"影响等级"。**

---

## 第一章：P0（唯一一项，且比 V1.0 认定的严重）

### 坑 1【影响 P0】Windows Offline 构建在当前 HEAD 必然失败

| 项 | 内容 |
|---|---|
| 现象 | `OfflineWindowsBuild.Build` 在资源构建前调用 `ValidateRawOnlyPackageContent()`，该函数发现 `Assets/GameContent` 下存在任何非 `.meta` 文件即抛 `BuildFailedException` |
| 证据 | `Assets/Scripts/Infrastructure/Editor/OfflineWindowsBuild.cs:77` 调用校验、`:78` 才 `BuildResourcePackage`；校验函数体 `:134-153`；`Assets/GameContent` 实测 **17 个非 meta 文件** |
| 实测清单 | `CombatHud.prefab`、`BossHealthBar.prefab`、`CombatHealthBar.prefab`、`SettlementPanel.prefab`、`UpgradeCard.prefab`、`UpgradeChoicePanel.prefab`、`CombatHitFlash.shader`、`MicrosoftArial.ttf`、`UI/Combat/battleBar.png`、`battleBar_hp.png`、`ui_pk_num.png`、`Maps/Tilesets/Grassland/floor_01~06.png` |
| 加剧因素 | 这 41 个文件（含 meta）**全部已 tracked**，是有意提交的正式资源，不是临时垃圾。且构建管线固定为 `RawFileBuildPipeline`（`:102`），而校验要求 GameContent 走 AssetBundle package——两者当前直接冲突 |
| 触发条件 | 执行 Windows Offline 构建即触发 |
| 后果 | **构建在资源打包前终止**，无法产出可发布 Player |
| 最迟处理节点 | 首发前 |
| M2 优先级 | **高**（构建可用是任何验收的前置条件） |

**为什么它不是"可能带 junk"**：V1.0 把它写成"目录扫描可能带入 junk"（P0 误用）。实际是**硬失败**，且失败点在一组正式战斗 UI 资源上——不能靠"删掉 junk"解决，必须二选一：给 GameContent 建立 AssetBundle 收集规则，或调整离线包的资源来源策略。

**未验证**：未实际执行构建。结论来自代码路径静态推断 + 目录实测。

---

## 第二章：P1

### 坑 2【影响 P1】战斗 World 无差别装入全部默认系统

| 项 | 内容 |
|---|---|
| 现象 | `CombatRuntimeRunner.CreateWorld` 用 `GetAllSystems(WorldSystemFilterFlags.Default)` 创建战斗 World，未使用白名单 |
| 证据 | `Assets/Scripts/Features/Combat/Runtime/CombatRuntimeRunner.cs:159` |
| 已确认受影响的系统 | `MoveEnemiesSystem`（`Rouge/ECS/MoveEnemiesSystem.cs:12`，`SystemBase`，无 `[DisableAutoCreation]`）、`PerformanceMotionSystem`（`Features/Performance/PerformanceEntityComponents.cs:38`，`ISystem` + `[UpdateInGroup(SimulationSystemGroup)]`，无 `[DisableAutoCreation]`） |
| 加剧因素 | 项目未禁用 Entities 默认 World 自动启动（全库无 `UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP`），而 `CombatRuntimeRunner` 又另建 World 手动推进 → 同一系统可能**同时存在于默认 World 与战斗 World** |
| 全项目仅有的防线 | `CombatTickSystem.cs:8` 带 `[DisableAutoCreation]`——说明团队知道该机制，但另外两个系统漏了 |
| 后果 | 每个未显式禁用的 `SystemBase`/`ISystem` 都会进入战斗 World 参与发现与调度。`PerformanceMotionSystem` 是正式性能测试系统，若误入正式战斗 World 属功能污染 |
| 最迟处理节点 | 内容量产前 |
| M2 优先级 | **中** |

**机制纠正（V1.0 错误）**：V1.0 称根因是"遗留目录没有 asmdef"。**错误**。即使加了 asmdef，其中的 `SystemBase`/`ISystem` 仍会被发现。真正的根因是**战斗 World 无差别调用 `GetAllSystems(Default)`**，防线只有三条：类级/程序集级 `[DisableAutoCreation]`、`WorldSystemFilter`、或改用显式系统白名单。V1.0 的"坑 2"整条删除，机制并入本条。

### 坑 3【影响 P1】正式运行链存在资源 ID 与表现参数硬编码

| 项 | 内容 |
|---|---|
| 证据 | `CombatFeedbackVisuals.cs:23-25` `BarFrameResourceId = 960006`、`BarFillResourceId = 960007`、`DigitAtlasResourceId = 960008`；同文件伤害数字 `29f` / `30f` / `1.25f`；`:39-43` `NumberRiseDuration` 等 |
| 违反 | 资源 ID 硬编码直接违反 `AGENTS.md`"资源键不得硬编码"条款 |
| 后果 | 资源键变更需改代码；表现手感调参需程序介入 |
| 最迟处理节点 | 内容量产前 |
| M2 优先级 | **中** |

**V1.0 错误**：称"现代正式链业务数值硬编码未检出"。不成立——资源 ID 就在正式渲染链上。

### 坑 4【影响 P1】Prefab 与 Excel 双向数据源的覆盖风险

| 项 | 内容 |
|---|---|
| 证据 | `CombatCollisionPipeline.cs:20-25` `[MenuItem("Roguelike/战斗/碰撞配置/导出全部预制体并生成配置")]`，注释明确"保存当前 Prefab 后，将全部碰撞像素参数写入源表并只运行一次 Luban" |
| 现象 | **作者侧数据源是 Prefab**，Excel 是导出产物；运行时只消费 Luban。**若有人手工改 Excel，下次执行该 MenuItem 会被静默覆盖** |
| 后果 | 字段级数据丢失；且丢失不会报错 |
| 最迟处理节点 | 内容量产前 |
| M2 优先级 | **中** |

**说明**：V1.0 把此项列为"待验证怀疑项 B6"，但已有明确代码证据，应按已确认处理。同时"Luban 唯一数据源"的准确表述应为：**运行时唯一数据源是 Luban；作者侧存在 Prefab→Excel 的字段级源**。

---

## 第三章：P2 / P3

| # | 项 | 影响 | 证据 | 最迟节点 | M2 优先级 |
|---|---|---|---|---|---|
| 5 | 遗留链业务硬编码：`RougeBootstrap.cs:20` `enemyCount = 200` 及生成范围、移动速度 | P2 | 源码 | 任意 | 低 |
| 6 | 发布日志治理：43 处 `Debug.Log`，缺统一策略、发布采集边界、敏感字段规则、运行时开关 | P2 | 全库统计 | 外测前 | 低 |
| 7 | 确定性边界未锁定（随机函数确定 ≠ 整局模拟确定） | P2 | `CombatRandom` 已就绪但遍历顺序/Jobs/Burst/配置版本未锁定 | 做续局/回放前 | 低 |
| 8 | 资源所有权与异步取消矩阵缺失 | P2 | `AGENTS.md` 有要求，未见对应测试 | 内容量产前 | 中 |
| 9 | 性能预算缺目标硬件与完整维度（GPU/CPU/内存/加载尖峰/1% Low/DrawCall） | P2 | `TbPerformanceScenario` 已有部分门槛 | Deck 验证前 | 低 |
| 10 | Prefab 无 Variant（208 个平铺，Monster 79 / Skill 93） | **P3 条件项** | 全库无 `PrefabInstance:` 文档 | — | 不投入 |
| 11 | 输入缺 Action Map / 手柄 / 重映射持久化 / UI 导航 | P2 | `ICombatInput` 已存在，无手柄支持 | UI 冻结前 | 低 |
| 12 | `SampleScene.unity` 沿用默认名但是正式入口 | P3 | README 记载 | 任意 | 不投入 |

**坑 10 说明**：Variant 数为 0 可静态确认，但只要生成管线完整、可复现、且禁止手工改 Prefab，平铺就是合法的产物形式。当前应作为"待验证的维护风险"，不应直接判为坑。

**坑 6 修正**：V1.0 称"无分级"不准确——项目已使用 `Log/Error/Exception`。真正缺的是统一策略与发布边界。

---

## 第四章：观察项（不是坑）

| 项 | 说明 |
|---|---|
| `IEventBus` 未接入业务 | V1.0 标 P1 但自己写"现在够用"，自相矛盾。未接入可能是预留接口或过早抽象，不等于未来必然直接耦合。降为观察项 |
| 表 ID 稳定性 | **V1.0 坑 7 事实错误，已删除**。`Config/README.md:129` 已明确"新增数据只追加未使用 ID；已发布 ID 不复用、不因排序变化重排"。最多讨论是否同步到 `AGENTS.md` |
| YooAsset `PackDirectory` | V1.0 归因错误：`PackDirectory` 是"如何按父目录分 Bundle"的打包规则，不是"收集整个目录"的原因；整目录收集来自 `GameContent` 根 Collector。现象部分成立，但已被坑 1 吸收，不单列 |

---

## 第五章：待评测方验证（我未确认）

| # | 怀疑项 | 建议验证方式 |
|---|---|---|
| B1 | `CombatSession` 901 行是否为 God Object | 变更原因分析：每类变更触及哪些字段，能否按职责切分 |
| B2 | Prefab override 是否失控 | 统计 208 个 Prefab 的 `m_Modifications` 字段数 |
| B3 | 配置校验是否覆盖全表内容（当前只确认选中关卡） | 检查是否存在全表扫描 CI |
| B4 | 21 个 asmdef 是否存在循环依赖 | 生成依赖图检测环 |
| B5 | 反射 / `ICustomBootstrap` / YooAsset 地址表是否引用了 ISOLATED 脚本 | grep 反射 API 与 collector 配置 |
| B7 | 坑 1 是否已有团队已知规避路径（如另有构建脚本先建 AssetBundle package） | 检查 `Tools/Build` 下其他入口 |

---

## 第六章：明确不是坑（避免评测方误改）

| 项 | 说明 |
|---|---|
| 21 个程序集分层 | 是优势，不要合并 |
| `CombatRandom` 无状态哈希 | 设计正确，不要改成有状态 RNG |
| 固定步长 `CombatSession.StepSeconds` | 正确，不要改成帧率相关 |
| `CombatTickSystem` 的 `[DisableAutoCreation]` | 正确用法，是全项目唯一正确示范 |
| `MCPDemo.unity` 及 5 个挂载脚本 | `README.md:32` 记载为有意保留的 Kapai 迁移验证资产 |
| 战斗 UI 文本外置到 `TbCombatUiSet` | 已数据驱动，多语言框架可后置 |
| `ICombatInput` 抽象 | 已存在，缺的是 Action Map 与手柄，不是抽象层 |
| 表 ID 规范 | `Config/README.md:129` 已有明确规定，不要重复制定 |
| 跨表引用校验 | `CombatRunDefinition.Create` 已实现 |

**修正**：V1.0 写"4 个 SO 带 `[CreateAssetMenu]`，明确不是坑"——**证据不成立**。`[CreateAssetMenu]` 只证明"可以创建资产"，不证明已有资产被引用、运行时可达或仍有产品价值。4 个 SO 应归为**待验证**，不是"明确不是坑"。

---

## 第七章：复现命令

```bash
# 坑 1（P0）
find Assets/GameContent -type f ! -name "*.meta" | wc -l      # 期望 17
grep -n "ValidateRawOnlyPackageContent" Assets/Scripts/Infrastructure/Editor/OfflineWindowsBuild.cs
git ls-files Assets/GameContent | wc -l                        # 确认已 tracked

# 坑 2（P1）
grep -rn "GetAllSystems" --include=*.cs Assets/Scripts
grep -rln ": *SystemBase\|: *ISystem\b" --include=*.cs Assets/Scripts
grep -rn "DisableAutoCreation\|UNITY_DISABLE_AUTOMATIC_SYSTEM_BOOTSTRAP" \
  --include=*.cs Assets/Scripts ProjectSettings

# 坑 3（P1）
sed -n '20,45p' Assets/Scripts/Features/Combat/Rendering/CombatFeedbackVisuals.cs

# 坑 4（P1）
sed -n '15,30p' Assets/Scripts/Features/Combat/Authoring/Editor/CombatCollisionPipeline.cs

# 坑 6
grep -rn "Debug.Log" --include=*.cs Assets/Scripts | grep -v Generated | wc -l

# 坑 10
grep -rl "^PrefabInstance:" Assets/Prefabs | wc -l             # 期望 0
find Assets/Prefabs -name "*.prefab" | wc -l

# 事实核对：表 ID 规范已存在
grep -n "不复用" Config/README.md
```

---

## 第八章：本报告自身边界

1. **未执行构建**。坑 1 的结论来自代码路径静态推断 + 目录实测，未经实际构建验证。若团队已有规避路径（如先建 AssetBundle package 的其他入口），本条不成立。
2. **未运行 Unity Editor**，坑 2 中"系统同时存在于两个 World"为机制推断，未在 Systems 窗口确认。
3. 审计工具未覆盖：反射、`ICustomBootstrap`、YooAsset 地址表、非 `.cs` 文本、超过 8MB 的文件、`.meta` 作为引用源。
4. 代码引用匹配使用非限定类型名，存在同名误报与注释误报。
5. 工作区存在未提交改动，commit SHA 只能固定已提交部分。
6. V1.0 曾出现 5 处事实错误（已在本版修正）：坑 2 机制错误、坑 7 事实错误、"零运行时 Find"绝对化（实测 `PerformanceFeature.cs:435` 有 `FindObjectsOfType<Camera>()`）、"正式链无硬编码"、"Luban 唯一数据源"范围不诚实。**评测方如发现本版仍有同类问题，请以证据列为准重新验证。**
