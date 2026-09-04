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

## 版本库规则

- 必须提交 `Assets` 下资源和目录对应的 `.meta` 文件，确保所有机器保持相同 GUID。
- 不提交 `Library`、`Temp`、`Logs`、`UserSettings`、IDE 工程文件和编辑器截图。
- `Packages/manifest.json` 与 `Packages/packages-lock.json` 必须同时提交，禁止把 Git 包依赖改回浮动分支。
- Luban 的表结构、生成配置、生成代码和运行时数据必须纳入版本管理，确保新机器可复现。
