# Luban 配置数据

## 目录

- `Datas/*.xlsx`：Luban 表、结构及枚举定义。
- `Datas/Tables/*.xlsx`：业务源数据，Excel 是唯一数据源。
- `luban.conf`：Luban v4.11.0 生成配置。

## 命名规范

- 业务工作簿：`拼音首字母_中文名_英文名.xlsx`，例如 `g_怪物_MonsterConfig.xlsx`。
- 文件名用于策划识别和目录排序；Luban 打表以工作簿内的工作表名及 `__tables__.xlsx` 定义为准。
- 重命名工作簿时必须同步修改 `__tables__.xlsx` 的 `input` 路径，禁止修改工作表名来迁就文件名。
- 所有工作表的有效区域统一使用水平、垂直居中显示。
- `id` 是表内唯一标识及跨表引用依据；禁止再增加含义重复的通用 `key` 字段。

## 源表保护

- `Tools/Config/generate.ps1` 与 `Tools/Config/generate.sh` 只读取 Excel，禁止反向修改或重建源表。
- 策划手工修改过的 Excel 属于权威数据，任何导入、迁移或批量重建脚本不得覆盖，除非修改人明确要求。
- 自动化修改表格前必须确认文件未被 Excel 占用，并与最近已知版本核对差异。

## 当前表层级

- L0：`TbResource`、`TbAttribute`、`TbItem`
- L1：`TbAnimationClip`、`TbVisualSet`、`TbMap`
- L2：`TbCharacter`、`TbMonster`、`TbSkill`
- L3 预留：`TbStage`
- 全局常量：`GameConfig`
- 测试：`TbPerformanceScenario`

资源路径只能写入 `TbResource`。其他表必须引用基础表 ID。

## ID 规范

- ID 只要求在所属表内唯一，不跨表做全局唯一；关联字段必须使用 Luban `#ref` 明确目标表。
- 普通功能表默认从 `1` 或 `10001` 开始递增；是否分段由数据含义决定，禁止哈希随机 ID。
- `TbResource` 使用 6 位结构 `CTNNNN`：`C` 为类别，`T` 为资源类型，`NNNN` 为该分组序号。
  - 类别：`1`角色、`2`怪物、`3`技能、`4`地图、`9`其他。
  - 类型：`1`动画、`2`纹理、`3`音频、`4`地图数据、`9`其他二进制。
  - 示例：`110001` 表示第 1 个角色动画资源，`220001` 表示第 1 个怪物纹理资源。
- `TbAnimationClip`、`TbVisualSet` 使用 `CNNNN`：类别位加 4 位分组序号。
- `TbCharacter`、`TbMonster` 从 `10001` 递增。
- `TbSkill` 当前沿用旧项目规则 `20000 + 资源编号`，例如 `skill_101` 对应 `20101`。
- `TbStage`、`TbPerformanceScenario` 从 `1` 递增；单例 `GameConfig` 的 ID 固定为 `1`。
- 新增数据只追加未使用 ID；已发布 ID 不复用、不因排序变化重排。

## 生成

Windows：

```powershell
./Tools/Config/generate.ps1
```

macOS/Linux：

```bash
./Tools/Config/generate.sh
```

生成的 C# 与二进制数据一并提交，因此新机器无需安装 Luban 即可打开和运行项目。修改 Excel 后必须重新生成并执行验证脚本。
