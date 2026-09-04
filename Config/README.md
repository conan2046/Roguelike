# Luban 配置数据

## 目录

- `Datas/*.xlsx`：Luban 表、结构及枚举定义。
- `Datas/Tables/*.xlsx`：业务源数据，Excel 是唯一数据源。
- `luban.conf`：Luban v4.11.0 生成配置。

## 当前表层级

- L0：`TbResource`、`TbAttribute`、`TbItem`
- L1：`TbAnimationClip`、`TbVisualSet`、`TbMap`
- L2：`TbCharacter`、`TbMonster`、`TbSkill`
- L3 预留：`TbStage`
- 测试：`TbPerformanceScenario`

资源路径只能写入 `TbResource`。其他表必须引用基础表 ID。

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
