# Windows Offline 构建与资源门禁方案

- 日期：2026-09-12
- 范围：`OfflineWindowsBuild`、YooAsset 包拓扑、Luban/TbResource 路由、资源与生命周期门禁
- 本轮性质：双包方案已完成配置、生成物、代码和静态检查；未启动 Unity、未执行 Player 构建、未修改 Prefab/Scene

## 1. 结论

当前 Windows Offline 构建存在确定性阻断：`OfflineWindowsBuild.Build()` 在调用 YooAsset 构建前执行 `ValidateRawOnlyPackageContent()`，而 `Assets/GameContent` 已包含 17 个正式非 `.meta` 资产，因此构建会在资源打包前直接失败。

推荐采用双包方案：

| 包 | 内容 | 构建管线 | 运行时用途 |
|---|---|---|---|
| `DefaultPackage` | `Config/Luban`、`ProjectX` | `RawFileBuildPipeline` / `RawBundle` | Luban bytes、ANI、旧 PNG、地图 DAT 等原始字节 |
| `GameAssetPackage` | `Assets/GameContent` | `BuiltinBuildPipeline` / `AssetBundle` | Prefab、Unity 导入纹理、字体、Shader 及其依赖 |

保留 `DefaultPackage` 名称可以减少对现有配置启动链和 6156 条 ProjectX 资源的扰动。`GameAssetPackage` 先使用 Unity/YooAsset 已自带的 Builtin 管线；当前没有性能或构建规模证据要求引入 SBP。

## 2. Tower 定向参考审查

按资源生命周期、配置和构建范围检查规范约定的 Tower `Client/Tower/Assets` 参考目录，本机当前不可访问该目录，因此没有可验证的同类入口、配置读取或释放实现可采用。本方案继续以当前项目的单机 Steam 范围、Luban 唯一数据源、YooAsset 2.3.19 和既有资源所有权约束为准。

## 3. 当前调用链与根因

### 3.1 构建链

```text
Tools/Build/build-windows-offline.ps1
  -> Tools/Config/validate.ps1
  -> Unity batchmode: OfflineWindowsBuild.BuildFromCommandLine
     -> ProjectYooAssetSetup.Configure
     -> ValidateRawOnlyPackageContent       [当前在此失败]
     -> RawFileBuildPipeline(DefaultPackage)
     -> 校验包文件
     -> BuildPipeline.BuildPlayer
     -> 校验 Player 内置包
  -> 启动 Player，等待启动成功日志
```

`Assets/GameContent` 当前有 17 个正式文件：6 张地图地块纹理、3 张战斗 UI 纹理、5 个 UI Prefab、1 个战斗血条 Prefab、1 个字体和 1 个 Shader。`TbResource` 直接登记其中 15 个资源；血条 Prefab 和 Shader 作为正式依赖存在，不能通过删除目录规避构建阻断。

### 3.2 运行时链

```text
GameApplicationBootstrap
  -> YooAssetPackageInitializer(DefaultPackage)
  -> LubanConfigService: 按 luban-config 标签 LoadRawFileAsync
  -> YooAssetResourceService
       LoadRawFileAsync(resourceId) -> DefaultPackage
       LoadAssetAsync<T>(resourceId) -> 当前仍是 DefaultPackage
```

`TbResource` 当前共有 6171 条有效记录：

| 路径域 | 条数 | 现有加载语义 |
|---|---:|---|
| `ProjectX/...` | 6156 | Raw：4631 ANI、1524 旧纹理、1 地图 DAT |
| `GameContent/...` | 15 | Unity Asset：5 Prefab、1 字体、6 地图纹理、3 UI 纹理 |

同一个 `Texture` 类型同时存在于 Raw 的 `ProjectX` 和 AssetBundle 的 `GameContent`，因此不能只按 `EResourceType` 选择包。

### 3.3 YooAsset 2.3.19 的关键边界

YooAsset 2.3.19 在 `TaskCreateManifest` 中把 `BuildParameters.BuildBundleType` 写入包清单；`PackageBundle` 再把这个包级值赋给每个 Bundle。Builtin/SBP 使用 `AssetBundle`，RawFile 使用 `RawBundle`。

虽然 `AssetBundleResult.ReadBundleFileData()` 存在，但它读取的是整个 AssetBundle 文件；`RawFileHandle.GetRawFileData()` 不会从 AssetBundle 内提取被打包的原始文件。因此：

- 单个 Raw 包无法加载 Prefab、字体等 Unity 对象。
- 单个 AssetBundle 包会破坏现有 `LoadRawFileAsync` 的 ANI、PNG、DAT 和 Luban bytes 语义。
- Collector 的 `PackRawFile` 只决定打包命名/粒度，不能让一个 AssetBundle 包在运行时混用 `RawBundle`。

## 4. 方案比较

| 方案 | 结论 | 原因 |
|---|---|---|
| 删除或移走 `GameContent` | 拒绝 | 17 个文件均为正式资源或依赖，15 条已由 `TbResource` 引用 |
| 单包继续 Raw | 不可行 | Prefab、字体、导入纹理无法按 Unity Asset 加载 |
| 单包改 Builtin | 不推荐 | 需要把 6156 条 Raw 资源和全部 Luban 启动逻辑改造成 Unity Asset 读取，迁移面过大 |
| Raw + Builtin 双包 | **采用** | 保留旧 Raw 语义，只为正式 Unity 资产新增最小边界 |
| Raw + SBP 双包 | 暂缓 | 项目未安装 `com.unity.scriptablebuildpipeline`，当前没有规模或耗时数据证明收益 |

## 5. 后续实现设计

### 5.1 Luban 表结构

在 `TbResource` 增加包归属枚举字段，例如 `package`：

| 枚举 | 规则 |
|---|---|
| `Raw` | `ProjectX/...`，由 `DefaultPackage` 提供，只允许 Raw API |
| `GameAsset` | `GameContent/...`，由 `GameAssetPackage` 提供，只允许 Unity Asset API |

包归属必须写入 Excel 并由 Luban 生成，避免在运行时代码中通过路径前缀或资源 ID 范围硬编码。现有 6171 行做一次机械迁移，并由静态检查锁定“路径域—包归属—加载 API”的一致性。

### 5.2 Collector 与构建

1. `ProjectYooAssetSetup.Configure()` 创建两个互斥 Collector Package。
2. `DefaultPackage` 只收集 `Config/Luban` 与 `StreamingAssets/ProjectX`，继续使用 `PackRawFile`。
3. `GameAssetPackage` 只收集 `GameContent`，继续使用地址规则和 `PackDirectory`，允许自动收集 Shader 依赖。
4. Offline 构建先生成 Raw 包，再用 `BuiltinBuildParameters` + LZ4 生成 Asset 包。
5. 两个包共用版本号，但分别校验版本、哈希、Manifest、Catalog 和 Bundle 类型。
6. YooAsset 的内置复制根目录按包名隔离，两个包各自使用 `ClearAndCopyAll` 不会互删。

### 5.3 启动、路由与释放

1. `YooAssetPackageInitializer` 同时拥有两个包；EditorSimulate 和 Offline 都逐包初始化并激活本地 Manifest。
2. `LubanConfigService` 只使用 `DefaultPackage` 的 `luban-config` 标签。
3. `YooAssetResourceService` 读取生成的 `TbResource.package`：Raw API 只接受 `Raw`，Asset API 只接受 `GameAsset`；调用不匹配时立即失败并报告资源 ID、表内类型和包归属。
4. 初始化中途失败或取消时，已完成初始化的包保持由初始化器统一拥有，并在组合根退出时一次销毁 YooAsset；已创建的加载 Handle 仍按现有 `catch -> Release` 规则释放。
5. 应用退出顺序保持：取消启动任务 -> 关闭战斗/UI/World -> 等待晚到加载清理 -> 销毁两个 Package 的 YooAsset 全局状态。

## 6. 门禁覆盖矩阵

| 门禁 | 检查项 | 当前覆盖 | 缺口与新增验收 |
|---|---|---|---|
| G0 工作区边界 | 不覆盖用户 Scene/Prefab；无预览资产；只提交任务文件 | 有 Git 人工检查规范 | 增加构建前变更白名单与预览资产扫描 |
| G1 Luban 一致性 | Excel、schema、生成 C#、bytes 一致 | `Tools/Config/validate.ps1`；Combat 静态规则 | 增加 `TbResource.package` 非空、合法组合和全表计数检查 |
| G2 资源引用 | 每条表路径存在、地址唯一、包归属正确 | 当前已静态确认 6171/6171 路径存在 | 增加全表“路径域—包—API”检查；检查 15 个直接 GameContent 地址和 2 个 Unity 依赖 |
| G3 Collector 拓扑 | 两包互斥；Raw/AssetBundle 类型正确；无地址冲突 | 当前只有单包设置 | 新增 Editor 测试读取 Collector 配置和构建 Manifest |
| G4 作者数据所有权 | Prefab→Excel 字段冲突不能静默覆盖 | `combat-prefab-ownership.json` 与 `ConfigWorkbookWriterTests` 已覆盖初始化、冲突、非法值 | 保留现有门禁；扩展字段时必须同步 baseline/hash，不将 GameContent 打包修复混入作者配置 |
| G5 World 隔离 | 战斗 World 只含 Unity 基础设施系统 | `CombatWorldFactoryTests` 已覆盖白名单与项目系统排除 | Unity 可用后回归，不因资源双包修改 World 逻辑 |
| G6 取消/释放 | 部分加载失败、取消、重复退出、World 先销毁、资源句柄归还 | `CombatVisualTests`、`CombatRuntimeTests`、`CombatMapTests` 已有实质覆盖 | 新增“双包第二包初始化失败/取消”“错误 API 路由不泄漏”“初始化器重复 Dispose” |
| G7 EditorSimulate | Luban Raw、旧 ANI/PNG、正式 Prefab/纹理/字体可加载 | 当前只验证单包 Luban + 首条 Raw；战斗 PlayMode 间接覆盖部分正式资源 | 新增双包启动测试；Manifest 全地址检查 + 每类代表资源真实加载 |
| G8 Offline 构建 | 两包均写入 Player；包类型与地址完整 | 当前在构建前被 Raw-only 校验阻断 | Unity 空闲后构建 Win64；校验两个包目录和 Manifest，而非仅文件存在 |
| G9 Player 冒烟 | Built Player 真实进入正式关卡并加载 GameContent | 当前脚本只等待普通启动成功日志 | 冒烟入口显式传入测试关卡 ID，验证 UI/地图/字体加载和退出清理 |
| G10 人工验收 | 真实输入、画面、暂停/重开/退出 | 尚未执行本阶段验收 | 自动门禁通过后由用户 Play 验收；Win64 包仅在用户明确要求时构建 |

## 7. 实施状态与后续顺序

已完成：

1. `TbResource.package` 与 `EResourcePackage` 已写入 Excel，并由 Luban 生成 C# 与 bytes。
2. Collector 已拆为 `DefaultPackage` Raw 包和 `GameAssetPackage` Builtin AssetBundle 包。
3. 初始化器、Luban 服务、资源服务和 Offline 构建已按包归属路由。
4. 静态配置检查已覆盖路径、包归属、加载 API 和物理文件存在性。
5. PowerShell 7 配置校验、战斗静态校验、脚本语法检查和相关 `.csproj` 编译均通过。

Unity 空闲后执行 G3、G5～G9；最后交给用户完成 G10。Unity 未刷新前，本地 IDE 工程文件不作为正式交付物，新增生成代码会在 Unity 重新生成项目文件后纳入程序集。

## 8. 风险与回滚

| 风险 | 控制 |
|---|---|
| Texture 同类型跨 Raw/Asset 两种存储 | 使用 Luban `package` 字段显式路由，不按类型猜测 |
| 双包部分初始化成功后第二包失败 | 初始化器统一持有并在失败/退出路径销毁；补部分成功测试 |
| 构建只检查文件存在、漏检地址 | 读取 Manifest 校验包类型、资源地址和表内映射 |
| `PackDirectory` 带入未登记资源 | 允许 Prefab 依赖，但门禁输出“直接登记/依赖/未引用”三类清单；未引用资产视为错误 |
| 引入 SBP 增加依赖与构建差异 | 当前不引入；先用 Builtin 建立可工作的基线，再以构建时间和包体数据决定 |
| 回滚影响 | 双包改动应保持单一提交，源表、生成代码、bytes、Collector、运行时和测试同时回滚 |

## 9. 本轮边界

- 已通过源码与目录静态证据确认构建阻断和单包类型冲突，并完成双包静态实施。
- 未运行 Unity，因此未证明 Builtin 包能够在本项目实际构建、EditorSimulate 双包初始化或 Win64 Player 冒烟成功。
- 当前旧报告中“资源所有权与异步取消矩阵缺失”已部分过时：现有测试已覆盖多种中断与释放路径；真正缺口集中在新增双包的部分初始化、错误路由和 Offline provider。
