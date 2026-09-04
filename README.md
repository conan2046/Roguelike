# Roguelike

Unity 2022.3 LTS 项目，使用 ECS/DOTS、Entities Graphics 和 URP。

## 强制开发规范

- 项目采用数据驱动：配置数据全部来自数据表，并统一使用 [Luban](https://www.datable.cn/docs/intro) 转换。
- 禁止在代码、Prefab、Scene 或 ScriptableObject 中硬编码游戏配置数据。
- 每个正式功能依次执行：方案讨论 → 数据表完善与生成 → 代码实现 → 测试验收；测试任务除外。
- 完整约束见 [`AGENTS.md`](AGENTS.md)。

## 环境要求

- Unity `2022.3.62f3`（版本记录在 `ProjectSettings/ProjectVersion.txt`）
- Git（Unity Package Manager 需要它拉取已固定版本的 MCP for Unity）
- Windows、macOS 或 Linux 上对应平台的 Unity Editor 模块
- 修改数据表并重新生成时，需要 [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## 拉取并运行

```powershell
git clone https://github.com/conan2046/Roguelike.git
cd Roguelike
```

1. 用 Unity Hub 添加仓库根目录并使用 `2022.3.62f3` 打开。
2. 等待 Unity Package Manager 完成依赖恢复和首次脚本编译。
3. 打开 `Assets/Scenes/SampleScene.unity`。
4. 点击 Play 运行。

`SampleScene` 已加入 Build Settings，构建玩家版本时可直接选择目标平台后执行 Build。
`Assets/Scenes/MCPDemo.unity` 保留了当前 Kapai 地图移动演示，可用于检查地图、角色与输入迁移效果。

### Windows 离线包

Windows Player 固定使用 YooAsset `OfflinePlayMode`。构建脚本会先校验 Luban，完整重建
`DefaultPackage` 并同步到内置资源目录，然后构建 Player 和执行启动冒烟验证。

将 `$UnityEditorPath` 设为本机 Unity 2022.3.62f3 的 `Unity.exe` 路径后执行：

```powershell
./Tools/Build/build-windows-offline.ps1 `
  -UnityEditorPath $UnityEditorPath `
  -PackageVersion '1.0.0'
```

通过验证的输出位于 `Builds/Windows/<版本>/`。构建过程不会修改 Scene、Prefab 或业务配置表；
`Bundles`、`Assets/StreamingAssets/yoo` 和 `Builds` 均为可重新生成的本地产物，不纳入版本管理。

### 独立 Player 性能测试

性能场景、实体数量、分辨率、预热/采样时间和通过门槛全部来自 Luban
`TbPerformanceScenario`。以下命令构建离线 Win64 Player，并运行场景 1 的 1000 实体测试：

```powershell
./Tools/Performance/run-performance-scenario.ps1 `
  -UnityEditorPath $UnityEditorPath `
  -PackageVersion '1.0.0-perf' `
  -ScenarioId 1
```

验证结果、Player 日志和截图位于
`Builds/Windows/<版本>/Performance/Scenario-<ID>/`。脚本仅在实体数、平均 FPS 和 P95 帧耗时达到表内门槛时返回成功；
GC Profiler 标记在当前 Player 可用时同时执行表内分配上限校验。

## Luban 数据表

Excel 是配置数据的唯一来源。表结构和源数据位于 `Config/Datas`，Luban `4.11.0` 已固定在 `Tools/Luban`。

生成配置：

```powershell
# Windows
./Tools/Config/generate.ps1
```

```bash
# macOS/Linux
bash ./Tools/Config/generate.sh
```

验证已提交生成物与 Excel 源一致：

```powershell
# Windows
./Tools/Config/validate.ps1
```

```bash
# macOS/Linux
bash ./Tools/Config/validate.sh
```

生成的 C# 代码位于 `Assets/Scripts/Generated/Config`，二进制数据位于 `Assets/StreamingAssets/Config/Luban`，两者均已提交。新机器只需拉取仓库并打开 Unity；未修改数据表时，无需安装或运行 Luban，也无需单独执行生成脚本。

## 版本库规则

- 必须提交 `Assets` 下资源和目录对应的 `.meta` 文件，确保所有机器保持相同 GUID。
- 不提交 `Library`、`Temp`、`Logs`、`UserSettings`、IDE 工程文件和编辑器截图。
- `Packages/manifest.json` 与 `Packages/packages-lock.json` 必须同时提交，禁止把 Git 包依赖改回浮动分支。
- Luban 的表结构、生成配置、生成代码和运行时数据必须纳入版本管理，确保新机器可复现。
