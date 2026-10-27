# Rogue 项目可维护性审查报告 V1.3

> [!WARNING]
> **本文件已被 V1.4 取代（2026-09-10）。**
> V1.3 仍有硬错误：规模统计写成 123 文件（实际 120）；`MoveEnemiesSystem` 被误判为死代码候选，
> 实际它继承 `SystemBase` 且无 `[DisableAutoCreation]`，会被 Entities 自动发现并每帧执行。
> 死代码工具原为"信号扫描"，未做可达性分析。
> **请改用 V1.4：`docs/architecture-maintainability-review-V1.4.md`。**

- 审查日期：2026-09-10
- 审查对象：当前项目根目录（Unity + Entities 1.0 + Luban + YooAsset，Steam 单机 Survivor-like）
- 审查快照：commit `897bed7a489f16b4158f3214fbbf944774898514`，分支 `main`，dirty 文件 17 个
- 版本状态：**V1.3 为唯一有效版本**，V1.0 / V1.1 已作废（文件内已加作废横幅）
- 审查方法：静态扫描 + GUID 引用审计工具；**未运行 Unity Editor、未编译、未跑测试**

---

# 第零章：为什么有 V1.3

V1.0 与 V1.1 存在硬事实错误，经外部审核指出并由本人逐条复验确认。**这一章必须看完，否则会误用旧结论。**

## 0.1 已撤回的错误结论

| # | 错误结论 | 复验结果 | 验证方式 |
|---|---|---|---|
| 1 | "20 个死代码文件，可安全删除" | **错**。实际候选 3 个，其中 5 个脚本 GUID 挂在 `MCPDemo.unity` | GUID 审计工具 |
| 2 | "208 个 Prefab、202 个 Variant，Variant 100%" | **错**。`m_CorrespondingSourceObject: {fileID: 0}`，全库 `PrefabInstance:` 文档数 = 0 | YAML 字段值检查 |
| 3 | "103 文件 / 19,937 行 / 平均 110 行" | **错**，口径混用。同口径为 103 文件 / 15,491 行 / 平均 150.4 | 行数统计 |
| 4 | "未见配置启动期引用完整性自检" | **错**。`CombatRunDefinition.Create` 已做跨表校验 | 源码阅读 |
| 5 | "输入抽象层需立项" | **错**。`ICombatInput` 已存在 | 源码阅读 |
| 6 | "性能预算文档缺失" | **过度断言**。门槛已表驱动 | README + 架构文档 |
| 7 | "本地化与文本完全为零" | **错**。战斗 UI 文本已外置到 `TbCombatUiSet` | 源码阅读 |
| 8 | "26 张 Excel" | 实际 27 业务表 + 3 元表 = 30 | 目录统计 |
| 9 | "`PerformanceFeature` 仅 1 个 public 方法" | 错，至少 3 个公开入口 | 源码阅读 |
| 10 | "8 条达标 / 2 条部分达标" 总评 | 口径混乱，与章节标注矛盾 | 自洽检查 |

## 0.2 根因

不是个别数字错误，而是方法学问题：**用低成本启发式（正则 grep）产出高置信度结论**。

| 错误模式 | 具体表现 |
|---|---|
| 未检出即判不存在 | 用类名 grep 判死代码（Unity 靠 GUID 引用） |
| 只查字段存在不查值 | 用"有 `m_CorrespondingSourceObject`"判 Variant，实际值为 `{fileID: 0}` |
| 用代理指标替代实质 | 用公开方法数量替代职责分析；用提交标题顺序证明迭代节奏 |
| 口径混用 | 分子排除测试、分母含测试 |
| 承认边界后仍下绝对结论 | 承认正则漏检，仍写"无一条业务数值硬编码" |

**V1.3 的约束**：凡无法用工具直接验证的判断，一律标注"未验证"，不给达标/未达标评级。

---

# 第一章：审查基线

## 1.1 规模数据（同口径）

| 口径 | 文件数 | 行数 | 平均行数 |
|---|---|---|---|
| Assets 内，排除 Generated / ThirdParty（**含测试**） | 123 | 19,937 | 162.1 |
| 再排除 Tests | **103** | **15,491** | **150.4** |

## 1.2 配置

- 源表：**27 张业务表**（`Config/Datas/Tables/`）+ **3 张元表**（`__beans__` / `__enums__` / `__tables__`）= **30 张 xlsx**
- 生成物：`Assets/Scripts/Generated/Config/Tb*.cs` + `Assets/StreamingAssets/Config/Luban/*.bytes`
- 运行时入口：`IConfigService` → `LubanConfigService`

## 1.3 Prefab

- 总数 208；战斗相关 202（Hero 6 / Monster 79 / Skill 93 / Weapon 24）
- **Variant 数量：0（未检出 `PrefabInstance:` 文档）** → 是否存在基础 Prefab 层级**未验证**，需在 Editor 人工确认

## 1.4 程序集

21 个 asmdef，分层为 `App / Core / Infrastructure / Features / Presentation / Generated / Tests / Animation / ThirdParty`。

---

# 第二章：逐条评审（修正版）

**不给汇总分数。** 每条独立评级：达标 / 部分达标 / 未验证 / 未达标。

## 第 1 条 一个脚本只负责一类事情 —— 部分达标

- 平均 150.4 行，拆分粒度合理
- 超长文件：`CombatSession.cs` 901 行、`PerformanceFeature.cs` 800 行、`CombatFeedbackVisuals.cs` 715 行、`CombatTickJob.cs` 561 行
- **未验证**：`CombatSession` 是否为 God Object。它同时承担 ECS 容器所有权、实体池、生成定位、容量推导、状态同步、事件缓存、重启与释放——需做变更原因分析后才能定性

## 第 2 条 系统间不直接引用 —— 部分达标

确凿事实：

| 检查项 | 结果 |
|---|---|
| 运行时 `GameObject.Find` / `FindObjectOfType` | 0 处（12 处 Find 全为 Editor 的 `SerializedProperty.FindProperty`） |
| 静态单例 | 0 处 |

**但这只证明没用两种坏写法，不等于低耦合。** `CombatRunCoordinator` 直接持有 Model 与 Session；`Combat.Runtime` 直接依赖 Core / Combat / Ecs / Rendering / UGUI。
**未验证**：asmdef 依赖图、循环依赖、跨层 API 面积。当前只能判"有程序集边界"。

## 第 3 条 数据与逻辑分开 —— 达标

- 业务数值硬编码：**当前正则未检出**
- 29 处常量集中在渲染/调试域（纹理尺寸、sortingOrder、Z 深度、BasisPoints）
- **边界**：正则只匹配 `标识符 = 数字;`，未覆盖方法参数、数组、构造器、字符串拼接

## 第 4 条 Prefab 结构 —— 未验证

撤回 V1.0/V1.1 的"Variant 100%"结论。全库未检出 `PrefabInstance:` 文档。**需在 Unity Editor 人工确认**是否存在基础 Prefab 与 Variant 层级。

## 第 5 条 目录结构 —— 达标

`Art / Audio / GameContent / Prefabs / Scenes / Scripts / StreamingAssets / Settings` 分层清晰，`Scripts` 下再按 21 个程序集切分。

## 第 6 条 Manager 不变万能 —— 未验证

事实：不存在名为 `GameManager` / `UIManager` 的类，只有 4 个窄接口（`IConfigService` / `IResourceService` / `IObjectPoolService` / `IEventBus`）+ `StartupPipeline`。
**但"无 GameManager 类名"不等于无万能对象**——见第 1 条 `CombatSession`。

## 第 7 条 先最小版本再扩展 —— 未验证

提交标题只能证明时间顺序，不能证明需求未蔓延或每阶段 DoD。**需 M1/M2 验收记录佐证。**

## 第 8 条 命名表达用途 —— 达标

- 坏名扫描（`test[0-9]` / `new` / `final` / `temp` / `copy` / `old`）：0 命中
- **修正**：`SampleScene.unity` 是 README 记载的正式启动场景，非瑕疵

## 第 9 条 定期整理删死代码 —— 结论见第三章

V1.0/V1.1 的"20 文件可安全删除"已作废。真实候选 3 个，见 3.2。

## 第 10 条 让修改变轻松 —— 部分达标

- 21 个 asmdef 提供编译期依赖边界（确凿）
- `ArchitectureServiceTests` 测的是服务行为（启动顺序、EventBus 退订、对象池复用），**不是架构适应度测试**，不应据此判达标
- `AGENTS.md` 强制 XML 注释规范、数据表与代码同提交、`audit → plan → fix → verify` 流程

---

# 第三章：死代码审计（工具复验结果）

## 3.1 工具

- 路径：`Tools/Audit/dead_code_audit.py`
- 运行：`/c/Users/Admin/.workbuddy/binaries/python/envs/default/Scripts/python.exe Tools/Audit/dead_code_audit.py`
- 耗时：**8.9 秒**（12,607 文本文件 / 123 脚本）
- 输出：`outputs/dead-code-audit/dead-code-audit.{json,md}`

判定维度：GUID 引用 / 代码类名引用 / 字符串引用 / 编辑器与运行时隐式入口 / 测试程序集。

## 3.2 结果

| 分类 | 数量 |
|---|---|
| IN_USE | 100 |
| TEST（NUnit 自动发现） | 17 |
| REVIEW | 0 |
| **CANDIDATE** | **3** |

| CANDIDATE | GUID |
|---|---|
| `Assets/Scripts/Kapai/World/CocosMapClickToMove.cs` | `b3ec2744a5e65ff46a678412c75772de` |
| `Assets/Scripts/Rouge/ECS/MoveEnemiesSystem.cs` | `707e0b50d93df504eaa08fd32e5f01a6` |
| `Assets/Scripts/Rouge/ECS/RougeBootstrap.cs` | `b1d5f59b94a47a042a9c0d22d62b6335` |

## 3.3 V1.1 误判项的翻案原因

| 文件 | 翻案原因 |
|---|---|
| `CocosAniPlayer`、`CocosModelAniPlayer`、`CocosMainCharacterController`、`CocosMapCameraFollow`、`CocosMapCharacterMotor` | GUID 挂在 `MCPDemo.unity`；README:32 记载该场景为有意保留的 Kapai 迁移验证资产 |
| `CocosMapCollisionGrid` | GUID 引用为 0，但类名被 2 个脚本引用 |
| `EnemySO`、`LevelUpSO`、`WaveSO`、`WeaponSO` | 带 `[CreateAssetMenu]`，编辑器菜单入口 |
| 3 个 `Combat*Editor` 类 | `[CustomEditor]` 特性绑定，代码不出现类名 |

**结论：原"20 个死代码"中 17 个为误判。**

## 3.4 删除建议

**3 个候选当前不建议删除。** 理由：

1. 全部位于 `Rouge/` 与 `Kapai/` 遗留目录，维护成本为零
2. 工具未覆盖反射、YooAsset collector、构建脚本路径字符串三类入口
3. `AGENTS.md` 已标注该批为"待迁移遗留，不得作为新功能范例扩展"，风险已被约束
4. 建议与 M2 内容量产时的目录整理一并处理

---

# 第四章：商业化上线风险清单

> 每条给出**验收判据**，不是泛泛建议。优先级对齐 `docs/rouge-feature-backlog-and-priority.md`。

## 4.1 已具备的能力（V1.1 误判为缺失）

| 能力 | 位置 |
|---|---|
| 跨表引用完整性校验 | `CombatRunDefinition.Create`（`CombatRunDefinition.cs:57`） |
| 战斗 UI 文本外置与格式校验 | `TbCombatUiSet` + `CombatRunDefinition.cs:122` |
| 输入抽象契约 | `ICombatInput` / `CombatInputFrame` / `UnityCombatInput` |
| 性能门槛（表驱动） | `TbPerformanceScenario` + README + 架构文档 `60fps @ 1000 同屏` |
| 确定性随机 | `CombatRandom`（无状态哈希，purpose 分流） |
| 固定步长 | `CombatSession.StepSeconds` |

## 4.2 缺失风险项

| # | 项 | 级别 | 验收判据 |
|---|---|---|---|
| 1 | 运行生命周期契约 | P0 | 启动/暂停/失焦/休眠/恢复/异常退出/强退各状态有落盘用例；强退后可恢复到最近合法落盘点 |
| 2 | 存档提交协议 | P0 | 写入中断电，重启后存档处于"旧档完整"或"新档完整"之一，无中间态；覆盖磁盘满、无权限、非 ASCII 路径、降级拒绝 |
| 3 | 确定性边界 | P0 | 中途快照 → 恢复 → 最终状态哈希一致（需锁定遍历顺序、浮点策略、Jobs/Burst、配置版本、全部 sequence counter） |
| 4 | Steam 构建与回滚 | P0 | AppID/DepotID/beta branch/BuildID/manifest/符号文件/回滚/增量补丁大小/干净安装验证齐备 |
| 5 | Demo 与正式版边界 | P0 | Demo 为独立 AppID（非编译开关）；共享 depot、共享云存档、解锁继承、内容裁剪已定义 |
| 6 | Steam 不可用与离线降级 | P1 | API 初始化失败时游戏可玩；成就幂等 + 本地排队 + 重试 |
| 7 | 资源所有权与异步取消 | P1 | YooAsset Handle / ECS World / NativeContainer / Scene 切换 / 初始化中途取消的所有权矩阵与泄漏测试 |
| 8 | 输入完整协议 | P1 | 热插拔、多控制器、死区、连发、重映射冲突、控制器字形、鼠柄切换、屏幕键盘 |
| 9 | 设置与无障碍 | P1 | 设备相关设置不进 Cloud；超宽屏/多显示器/DPI；音量分组；字幕/字号/色弱/闪烁/震动开关 |
| 10 | 隐私与诊断治理 | P1 | 遥测告知、最小采集、脱敏、保留周期、离线队列、BuildID/符号上传 |
| 11 | 补丁兼容 QA 矩阵 | P1 | 干净安装/跨版本升级/回滚/坏档/云冲突/低磁盘/断电/Deck 休眠/离线/首次无网络 |
| 12 | 供应链与发布安全 | P2 | Package 固定版本、许可证、构建账号与上传凭据隔离、第三方 SDK 升级回滚 |

## 4.3 已撤回的建议（勿采纳）

| 撤回项 | 原因 |
|---|---|
| 把 `SynchronizeAttributes` 抽为存档快照接口 | 它是 ECS 运行态同步方法，非持久化 DTO；复用会把存档绑死在战斗内核结构上 |
| "存档绝不存配置快照" | 过绝对。跨版本续局需保存 `saveSchemaVersion + contentVersion + seed + tick + 序列计数 + 稳定 ID` |
| 新增 `DiminishingReturn` 枚举解决数值膨胀 | 混淆了玩法平衡、工程安全（溢出）、内容约束三层，需不同表结构与测试 |
| "Steam 平台集成均为必须项" | Steamworks 功能可选；硬要求是商店页声明的功能必须在送审版本实现 |
| "Steam 不提供崩溃统计" | Steam 有 Error Reporting，但接近停服且仅支持 Win32，对 Win64 项目仍需另选方案 |

---

# 第五章：风险门限（对齐 backlog 真实里程碑）

| 事项 | 门限 | backlog 依据 |
|---|---|---|
| 存档结构版本化 | 首次公开存档前 | #9 存档系统 = M4 |
| 表 ID 稳定约定 | 内容量产前 | #4 卡池规则 = M3 |
| 属性聚合顺序与溢出边界 | 卡池规则锁定时 | #4 = M3 |
| 全表扫描 CI | 任意时点，成本低 | 正式关卡校验已存在 |
| 输入 Action Map / UI 导航 | UI 冻结前 | #13 输入层统一 = M3 |
| 手柄 / Steam Deck | 公开 Demo 前 | #14 Steam = M5 |
| 文本键 / 伪本地化 / 字体回退 / 布局压测 | UI 冻结前 | 翻译可后置，此四项不可 |
| Steamworks bootstrap / 离线降级 | Demo 前 | 平台初始化影响启动架构 |
| 成就内容与 ID | 送审前 | ID 发布后不可变更 |
| 死代码清理 | 审计后，成本未知 | 建议随 M2 目录整理一并处理 |

---

# 第六章：复现方法

```bash
# 死代码审计
/c/Users/Admin/.workbuddy/binaries/python/envs/default/Scripts/python.exe \
  Tools/Audit/dead_code_audit.py

# 规模统计（排除 Tests 口径）
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" \
  -not -path "*Tests*" -exec cat {} + | wc -l
find Assets -name "*.cs" -not -path "*Generated*" -not -path "*ThirdParty*" \
  -not -path "*Tests*" | wc -l

# Prefab Variant（当前期望 0）
grep -rl "^PrefabInstance:" Assets/Prefabs | wc -l

# 配置表
ls Config/Datas/Tables/*.xlsx | wc -l   # 27
find Config -name "*.xlsx" | wc -l      # 30

# 运行时查找与单例（期望 0）
grep -rn "GameObject.Find\|FindObjectOfType\|Object.Find" --include=*.cs \
  Assets/Scripts/Features Assets/Scripts/Infrastructure Assets/Scripts/Core \
  Assets/Scripts/App Assets/Scripts/Presentation
grep -rn "static.*Instance\|Singleton" --include=*.cs Assets/Scripts \
  | grep -v Generated | grep -v ThirdParty
```

---

# 第七章：诚实边界（未验证项）

## 7.1 执行边界

1. **未运行 Unity 编译、EditMode、PlayMode、Standalone、Profiler** —— 所有"可运行、可释放、可复现、性能达标"均未证实
2. **未打开 Unity Editor** —— Prefab 层级、Scene 结构、override 均未在编辑器中确认
3. 审查时工作区 dirty 17 个文件，**`AGENTS.md` 本身处于未提交修改状态**，以其为基准存在版本漂移

## 7.2 方法学边界

4. 未检查反射、`RuntimeInitializeOnLoadMethod`（已由工具覆盖）、YooAsset collector、资源地址表、构建脚本路径引用
5. 硬编码扫描正则只匹配 `标识符 = 数字;`，漏检方法参数、数组、构造器、字符串拼接
6. 未做 asmdef 依赖图、循环检测、跨层 API、`autoReferenced`、public API 面积分析
7. 未审查 Scene/Prefab override、Missing Script、序列化字段迁移、Domain Reload

## 7.3 概念区分

8. 未区分"选中关卡配置校验"与"全内容配置校验"——当前只确认前者存在
9. 未区分"文本已外置"与"本地化系统已接入"——只有前者
10. 未区分"随机函数确定"与"整局模拟确定"——只确认前者
11. 未区分"无 GameManager 类名"与"无万能对象"
12. 未区分"有程序集边界"与"有架构适应度测试"

## 7.4 引用边界

13. **公开案例未逐条溯源**。V1.1 引用的 Hades / Balatro / Vampire Survivors 三条为定性描述，无原始出处链接，**不足以支撑具体工程结论**，溯源前不应作为决策依据
14. 第四章引用的 Steam 官方文档链接未逐条访问验证，来源为外部审核方提供，需复核
15. 未标注每条结论的置信度与否决条件

---

# 第八章：使用须知（给阅读本报告的 AI）

1. **不要引用 V1.0 / V1.1 的任何结论**，两文件已作废
2. **不要删除任何文件**，除非重跑 `Tools/Audit/dead_code_audit.py` 且结果仍为 CANDIDATE，并已人工排除反射与打包清单引用
3. **不要基于第四章做任何排期决策**，门限需与 `docs/rouge-feature-backlog-and-priority.md` 核对
4. 第七章列出的 15 项未验证内容，**如需据此行动，必须先补齐验证**
5. 本报告的"达标"仅指静态扫描层面通过，不含运行时行为验证
