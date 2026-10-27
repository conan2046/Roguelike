# Rogue 项目可维护性审查报告 V1.4

- 审查日期：2026-09-10
- 审查对象：当前项目根目录（Unity + Entities 1.0 + Luban + YooAsset，Steam 单机 Survivor-like）
- 审查快照：commit `897bed7a489f16b4158f3214fbbf944774898514`，分支 `main`
- 版本状态：**V1.4 为当前有效版本**。V1.0 / V1.1 / V1.3 已作废（文件内已加横幅）
- 审查方法：静态扫描 + GUID 可达性审计工具（V2，含 DOTS 隐式入口识别）；**未运行 Unity Editor、未编译、未跑测试**

---

# 第零章：V1.4 修正了什么

## 0.1 本轮新增的硬错误修正

| # | V1.3 错误 | 复验结果 |
|---|---|---|
| 1 | "Assets 内 123 文件 / 19,937 行" | **错**。实际 **120 文件 / 19,937 行 / 平均 166.1**。123 是全仓库口径（含 `Tools/`），两者被拼进同一行 |
| 2 | `MoveEnemiesSystem` 列为死代码 CANDIDATE | **错且危险**。它继承 `SystemBase` 且无 `[DisableAutoCreation]`，会被 Entities 自动发现 |
| 3 | 死代码结论基于"信号扫描" | 工具已升级为**可达性分析**，并识别 DOTS 隐式入口 |
| 4 | 第 1 条判"部分达标" | 降级为**未验证**（平均行数属代理指标，不能证明职责单一） |
| 5 | 第 3 条判"达标" | 降级为**部分达标**（`RougeBootstrap` 存在 `enemyCount = 200` 等硬编码） |
| 6 | 第 8 条判"达标" | 降级为**部分达标**（`SampleScene` 作为正式入口仍是泛化名，构成命名债务） |
| 7 | 风险项用单一"级别"标注 | 拆为**失败影响 / 最晚门限 / 当前优先级**三维度 |

## 0.2 最重要发现：一个活的遗留系统正在战斗 World 中运行

`MoveEnemiesSystem`（`Assets/Scripts/Rouge/ECS/MoveEnemiesSystem.cs`）不是死代码：

```
public partial class MoveEnemiesSystem : SystemBase   // 无 [DisableAutoCreation]
```

而战斗入口显式加载全部默认系统：

```
// CombatRuntimeRunner.cs:159
DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world,
    DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.Default));
```

**后果**：该 M1.1 验证系统（让 `EnemyTag` 实体朝 -z 移动）会被加入战斗 World 并每帧 `OnUpdate`。当前它可能查不到匹配实体，但仍参与系统发现、创建与查询生命周期。

这不是"可删的死代码"，而是**需要确认是否应从默认系统集排除的运行时项**。处理方向是 `[DisableAutoCreation]` 或迁出被扫描的程序集，而非删除文件。

---

# 第一章：审查基线

## 1.1 规模（同口径，已复验）

| 范围 | 文件数 | 行数 | 平均 |
|---|---:|---:|---:|
| Assets 内，排除 Generated / ThirdParty，**含** Tests | **120** | 19,937 | 166.1 |
| 再排除 Tests | 103 | 15,491 | 150.4 |
| 全仓库同排除规则（含 `Tools/`） | 124 | — | — |

复现：

```bash
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" | wc -l
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" -exec cat {} + | wc -l
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" -not -path "*Tests*" | wc -l
```

## 1.2 配置

- **27 张业务表**（`Config/Datas/Tables/`）+ **3 张元表** = **30 张 xlsx**
- 生成物：`Assets/Scripts/Generated/Config/Tb*.cs` + `Assets/StreamingAssets/Config/Luban/*.bytes`
- 运行时入口：`IConfigService` → `LubanConfigService`

## 1.3 Prefab

- 总数 208（战斗相关 202：Hero 6 / Monster 79 / Skill 93 / Weapon 24）
- **Variant 数量 = 0**：Text Serialization 下 Variant/Nested Prefab 会出现 `PrefabInstance:` 与 `m_SourcePrefab`，全库未检出 → **序列化资产中无 Variant，此事实已确认，无需 Editor 验证**
- **仍需 Editor 验证**：层级是否重复、override 是否失控、是否应采用 Variant、生成管线是否已替代 Variant 的复用价值

## 1.4 程序集

21 个 asmdef：`App / Core / Infrastructure / Features / Presentation / Generated / Tests / Animation / ThirdParty`。

---

# 第二章：逐条评审

**不给汇总分数。** 每条独立评级：达标 / 部分达标 / 未验证 / 未达标。

## 第 1 条 一个脚本只负责一类事情 —— 未验证

文件规模已统计（平均 150.4 行，最大 `CombatSession.cs` 901 行）。

**但规模不等于内聚性**：100 行脚本也可能混合五个职责，900 行状态机也可能高度内聚。`CombatSession` 同时承担 ECS 容器所有权、实体池、生成定位、容量推导、状态同步、事件缓存、重启与释放，尚未做变更原因分析，**不能判定为职责单一或 God Object**。

## 第 2 条 系统间不直接引用 —— 部分达标

| 检查项 | 结果 |
|---|---|
| 运行时 `GameObject.Find` / `FindObjectOfType` | 0 处（12 处 Find 全为 Editor 的 `SerializedProperty.FindProperty`） |
| 静态单例 | 0 处 |

只证明没用两种坏写法，不等于低耦合。`CombatRunCoordinator` 直接持有 Model 与 Session；`Combat.Runtime` 直接依赖 Core / Combat / Ecs / Rendering / UGUI。
**未验证**：asmdef 依赖图、循环依赖、跨层 API 面积。

## 第 3 条 数据与逻辑分开 —— 部分达标

分三层看：

| 层次 | 状态 |
|---|---|
| 现代正式战斗链 | 基本数据驱动，业务数值硬编码未检出 |
| 遗留验证链 | **有硬编码**：`RougeBootstrap.cs:20` `enemyCount = 200`，另有生成范围与移动速度 |
| 全项目结论 | **部分达标** |

**工具边界**：硬编码正则只匹配 `标识符 = 数字;`，未覆盖方法参数、构造参数、数组、字符串拼接。

## 第 4 条 Prefab 结构 —— 部分达标

- 确凿：Variant 数为 0（序列化层面已确认）
- 未验证：层级复用是否合理、override 是否失控、生成管线是否已替代 Variant 价值 → 需 Editor

## 第 5 条 目录结构 —— 达标

`Art / Audio / GameContent / Prefabs / Scenes / Scripts / StreamingAssets / Settings` 分层，Scripts 下再按 21 个程序集切分。

## 第 6 条 Manager 不变万能 —— 未验证

事实：无名为 `GameManager` / `UIManager` 的类，只有 4 个窄接口 + `StartupPipeline`。
但"无 GameManager 类名"不等于无万能对象——见第 1 条。

## 第 7 条 先最小版本再扩展 —— 未验证

提交标题只能证明时间顺序，不能证明需求未蔓延或每阶段 DoD。**需 M1/M2 验收记录佐证。**

## 第 8 条 命名表达用途 —— 部分达标

- 坏名扫描（`test[0-9]` / `new` / `final` / `temp` / `copy` / `old`）：0 命中
- **命名债务**：`SampleScene.unity` 是 README 记载的正式启动场景，但名称仍是 Unity 默认泛化名。正式程度不能赋予文件名业务语义，本项保留为债务

## 第 9 条 定期整理删死代码 —— 见第三章

## 第 10 条 让修改变轻松 —— 部分达标

- 21 个 asmdef 提供编译期依赖边界（确凿）
- `ArchitectureServiceTests` 测的是服务行为（启动顺序、EventBus 退订、对象池复用），**不是架构适应度测试**
- `AGENTS.md` 强制 XML 注释、数据表与代码同提交、`audit → plan → fix → verify`

---

# 第三章：死代码审计（V2 可达性分析）

## 3.1 工具 V2 的改进

相较 V1 的三处关键升级：

1. **识别 DOTS 隐式入口**：`SystemBase` / `ISystem` / `ISystemStartStop` / `ComponentSystemGroup` / `Baker` / `ICustomBootstrap` / `IJobEntity`，并识别 `[DisableAutoCreation]`
2. **可达性遍历**：从 root 沿出边传播，能识别"死代码岛内部互相引用"
3. **分类重构**：`ROOT` / `REACHABLE` / `ORPHAN` / `ISOLATED` / `TEST`

## 3.2 结果（120 脚本，20.7 秒）

| 分类 | 数量 | 含义 |
|---|---:|---|
| ROOT | 43 | 被 Scene/Prefab/asset 的 GUID 引用，或有编辑器 / Entities 自动入口 |
| REACHABLE | 58 | 从 ROOT 沿引用图可达 |
| ORPHAN | 0 | 有引用信号但不可达（死代码岛） |
| ISOLATED | 2 | 无任何引用信号且不可达 |
| TEST | 17 | NUnit 自动发现 |

## 3.3 ISOLATED（仅 2 个）

| 脚本 | GUID |
|---|---|
| `Assets/Scripts/Kapai/World/CocosMapClickToMove.cs` | `b3ec2744a5e65ff46a678412c75772de` |
| `Assets/Scripts/Rouge/ECS/RougeBootstrap.cs` | `b1d5f59b94a47a042a9c0d22d62b6335` |

## 3.4 V1.1 原"20 个死代码"的当前分布

| 分类 | 数量 | 文件 |
|---|---:|---|
| ROOT | 11 | `CocosAniPlayer`、`CocosModelAniPlayer`、`CocosMainCharacterController`、`CocosMapCameraFollow`、`CocosMapCharacterMotor`、`KapaiMapMoveDemoMenu`、`EnemySO`、`LevelUpSO`、`WaveSO`、`WeaponSO`、**`MoveEnemiesSystem`（ECS 自动发现）** |
| REACHABLE | 7 | `CocosMapCollisionGrid`、`CocosMountData`、`CocosMainCharacterData`、`RougeComponents`、`AniFrameLayout`、`CocosAniData`、`AniMeshFactory` |
| ISOLATED | 2 | `CocosMapClickToMove`、`RougeBootstrap` |

**准确表述**：V1.1 的"20 个死代码"结论已被推翻。当前工具确认其中 18 个为 ROOT 或 REACHABLE（**不可按死代码处理**），2 个仍为 ISOLATED。

其中 5 个 GUID 挂在 `Assets/Scenes/MCPDemo.unity`；`README.md:32` 记载该场景为有意保留的 Kapai 迁移验证资产。

## 3.5 仍不建议删除 ISOLATED 两项

1. 工具未覆盖反射、`ICustomBootstrap`、YooAsset collector、构建脚本路径字符串
2. 均在遗留目录，维护成本接近零
3. 建议随 M2 内容量产的目录整理一并处理

---

# 第四章：商业化上线风险清单（三维度）

> V1.3 把"失败影响"与"当前研发优先级"混为一列，会导致跳过 M2 内容提前做 Steam/存档。
> 本章拆为三个独立维度。**排期请以"当前优先级"为准，不要看失败影响。**

| # | 风险项 | 失败影响 | 最晚门限 | 当前优先级（M2） |
|---|---|---|---|---|
| 1 | 运行生命周期契约（落盘时机、Deck 休眠一致性） | 高 | 首次公开存档前 | 不投入 |
| 2 | 存档提交协议（原子性、校验和、磁盘满、非 ASCII 路径、降级拒绝） | 高 | 首次公开存档前 | 不投入 |
| 3 | 确定性边界 | 中 | 存档/回放功能前 | 低（先定范围） |
| 4 | Steam 构建与回滚 | 高 | 发行前 | 不投入 |
| 5 | Demo 与正式版边界（独立 AppID） | 中 | 确定发 Demo 才适用 | 不投入 |
| 6 | Steam 不可用与离线降级 | 中 | Demo 前 | 不投入 |
| 7 | 资源所有权与异步取消 | 高 | M2 内容量产前 | **中** |
| 8 | 输入完整协议（热插拔、重映射、字形） | 中 | UI 冻结前 | 低 |
| 9 | 设置、显示、音频与无障碍 | 中 | UI 冻结前 | 低 |
| 10 | 隐私与诊断治理 | 中 | 首发前 | 不投入 |
| 11 | 补丁兼容 QA 矩阵 | 高 | 首发前 | 不投入 |
| 12 | 供应链与发布安全 | 中 | 首发前 | 不投入 |

## 4.1 确定性范围需先定义

存档恢复一致、同版本回放一致、跨版本一致、跨 CPU/Burst 一致——四者成本差异极大。
**跨版本与跨硬件 bit-identical 不应默认设为 Steam 单机 P0**，建议先锁定"存档恢复一致 + 同版本回放一致"。

## 4.2 门限与 backlog 对齐

| 事项 | 门限 | backlog 依据 |
|---|---|---|
| 存档结构版本化 | 首次公开存档前 | #9 存档系统 = M4 |
| 表 ID 稳定约定 | 内容量产前 | #4 卡池规则 = M3 |
| 属性聚合顺序与溢出边界 | 卡池规则锁定时 | #4 = M3 |
| 输入 Action Map / UI 导航 | UI 冻结前 | #13 输入层统一 = M3 |
| 手柄 / Steam Deck | 公开 Demo 前 | #14 Steam = M5 |
| 文本键 / 伪本地化 / 字体回退 / 布局压测 | UI 冻结前 | 翻译可后置，此四项不可 |
| Steamworks bootstrap / 离线降级 | Demo 前 | 平台初始化影响启动架构 |
| 已发布 API Name（成就等） | 按稳定外部契约管理 | 发布后变更成本高 |

## 4.3 已撤回的建议

| 撤回项 | 原因 |
|---|---|
| 把 `SynchronizeAttributes` 抽为存档快照接口 | 它是 ECS 运行态同步方法，非持久化 DTO；复用会把存档绑死在战斗内核结构 |
| "存档绝不存配置快照" | 过绝对。跨版本续局需保存 `saveSchemaVersion + contentVersion + seed + tick + 序列计数 + 稳定 ID` |
| 新增 `DiminishingReturn` 枚举解决数值膨胀 | 混淆玩法平衡、工程安全（溢出）、内容约束三层，需不同表结构与测试 |
| "Steam 平台集成均为必须项" | Steamworks 功能可选；硬要求是商店页声明的功能必须在送审版本实现 |
| "Steam 不提供崩溃统计" | Steam 有 Error Reporting，但接近停服且仅支持 Win32，Win64 项目仍需另选方案 |
| "成就 ID 发布后不可变更" | 缺官方依据，改为"已发布 API Name 按稳定外部契约管理" |

---

# 第五章：复现方法

```bash
# 死代码可达性审计（约 21 秒）
# 使用项目托管 venv，路径见文档末尾环境说明
python Tools/Audit/dead_code_audit.py

# 规模统计
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" | wc -l
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" -exec cat {} + | wc -l

# Prefab Variant（期望 0）
grep -rl "^PrefabInstance:" Assets/Prefabs | wc -l

# 配置表
ls Config/Datas/Tables/*.xlsx | wc -l
find Config -name "*.xlsx" | wc -l

# 运行时查找与单例（期望 0）
grep -rn "GameObject.Find\|FindObjectOfType\|Object.Find" --include=*.cs \
  Assets/Scripts/Features Assets/Scripts/Infrastructure Assets/Scripts/Core \
  Assets/Scripts/App Assets/Scripts/Presentation
grep -rn "static.*Instance\|Singleton" --include=*.cs Assets/Scripts \
  | grep -v Generated | grep -v ThirdParty

# 硬编码数值（排除 0/1/-1）
grep -rnE "(^|[^A-Za-z0-9_.])[a-zA-Z_]+ *= *-?[0-9]+(\.[0-9]+)?f? *;" --include=*.cs \
  Assets/Scripts/Features Assets/Scripts/Infrastructure Assets/Scripts/Core \
  Assets/Scripts/App Assets/Scripts/Presentation | grep -vE "= *(0|1|-1)(\.0)?f? *;"

# 坏命名（期望 0）
find Assets -name "*.cs" -o -name "*.prefab" | grep -iE "test[0-9]|new|final|temp|tmp|copy|backup|old"

# DOTS 自动发现系统（检查是否有遗留系统被自动创建）
grep -rln ": *SystemBase\|: *ISystem\b" --include=*.cs Assets/Scripts
grep -rn "DisableAutoCreation" --include=*.cs Assets/Scripts

# 程序集清单
find Assets -name "*.asmdef" | wc -l
```

**环境说明**：脚本为本机托管 venv 下的 Python 3。新机器复现需先创建 venv 并安装依赖（当前脚本仅用标准库，无需额外包），再以 venv 解释器执行。

---

# 第六章：诚实边界（未验证项）

## 6.1 执行边界

1. **未运行 Unity 编译、EditMode、PlayMode、Standalone、Profiler**——"可运行、可释放、可复现、性能达标"均未证实
2. **未打开 Unity Editor**——Prefab 层级合理性、override、Missing Script、Domain Reload 未确认
3. commit SHA 只能固定已提交内容，**dirty 内容未固定**；未保存完整 dirty manifest 或文件哈希
4. 本报告、审计工具与输出**均未提交**，当前还不是可复现的仓库状态
5. `AGENTS.md` 处于未提交修改状态，以其为基准存在版本漂移

## 6.2 工具边界

6. 代码引用只匹配**非限定简单类型名**，存在同名类型误报（如 `Health` / `Damage` / `MoveSpeed`）与注释误报
7. 字符串扫描**只覆盖 `.cs` 文件**，未覆盖 JSON、YAML、VDF、PowerShell、批处理、YooAsset 配置
8. 跳过超过 8MB 的文本文件，可能漏掉大型 Scene / Prefab / asset 中的引用
9. 引用索引**完全跳过 `.meta` 内容**，部分 Importer 与外部对象映射可能漏检
10. 未覆盖反射调用、`ICustomBootstrap`、YooAsset collector、资源地址表、构建脚本路径字符串
11. `[CreateAssetMenu]` / `[MenuItem]` 仅证明**可达**，不证明产品仍在使用——4 个旧 ScriptableObject 应视为 REVIEW 而非确认有效

## 6.3 概念区分

12. 未区分"选中关卡配置校验"与"全内容配置校验"——只确认前者存在
13. 未区分"文本已外置"与"本地化系统已接入"——只有前者
14. 未区分"随机函数确定"与"整局模拟确定"——只确认前者
15. 未区分"无 GameManager 类名"与"无万能对象"
16. 未区分"有程序集边界"与"有架构适应度测试"

## 6.4 引用边界

17. **公开案例未逐条溯源**。Hades / Balatro / Vampire Survivors 为定性描述，无原始出处链接，不足以支撑具体工程结论
18. Steam 官方文档链接未逐条访问验证，来源为外部审核方，需复核
19. 未标注每条结论的置信度与否决条件

---

# 第七章：使用须知（给阅读本报告的 AI）

1. **不要引用 V1.0 / V1.1 / V1.3 的任何结论**，三个文件已作废
2. **不要删除任何文件**。当前 ISOLATED 仅 2 个，且在反射与打包清单未排除前不应处理
3. **`MoveEnemiesSystem` 不是死代码**，它是每帧执行的自动发现系统。如需停用，方向是 `[DisableAutoCreation]` 或迁出被扫描程序集，不是删文件
4. **排期看第四章"当前优先级"列**，不要按"失败影响"列决定投入顺序
5. 第六章 19 项未验证内容，如需据此行动必须先补齐验证
6. 本报告的"达标"仅指静态扫描层面通过，不含运行时行为验证
