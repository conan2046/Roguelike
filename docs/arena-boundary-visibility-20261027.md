# 通用战斗地图边界显示

用户要求每个战斗场景都显示真实可行走范围红框，后续由设置页面控制显示。本轮交付统一运行时开关，默认开启；纯菜单不生成地图边框，不建设完整设置页或永久设置存档。

## 配置与职责

- 继续使用 Excel → Luban → YooAsset 的唯一配置链。`z_战斗表现_CombatPresentationConfig.xlsx` 在 BF:BK 新增 `arenaBoundaryDefaultVisible`、`arenaBoundaryColor`、`arenaBoundaryWidthPixels`、`arenaBoundarySortingOrder`、`arenaBoundaryRenderQueue`、`arenaBoundaryShaderName`，生成 C# 和 `tbcombatpresentation.bytes` 同步。
- 初值复用原临时红框样式：开启、红色、两个显示像素、既有最前世界排序／队列与引擎 Shader；运行时代码不复制这些配置默认值。矩形四点拓扑与 Unity 排序／队列合法范围属于技术协议。
- `CombatSession.MovementBoundsHalfSize` 只读暴露与 `CombatTickJob.Arena` 完全同源的会话设置。正式 Stage／试炼来自地图配置，Combat 测试场景来自该场景的实际移动设置。背景 `FitViewport` 范围不作为行走边界。
- `CombatArenaBoundaryVisuals` 只持有内存 LineRenderer 与独占材质；`CombatRuntimeRunner` 创建、同步和释放。普通 Stage、试炼、Combat 测试入口统一接入，不再依赖 `UNITY_EDITOR` 或 Gizmos。试炼标记器原红框已移除，职业圆环保持原职责。
- 设置接入点为 `runner.SetArenaBoundaryVisible(bool)`，状态可读 `runner.ArenaBoundaryVisible`；同一运行器结算重开保留该显示选择，退出后新运行器重新读取表内默认。后续设置页可在运行器就绪后应用用户选择。本轮无全局／永久偏好保存。
- 隐藏只控制可视对象，不改变地图尺寸、相机、碰撞、伤害或移动边界。相机视口变化只重新换算显示线宽。没有修改现有 HUD、Scene 或 Prefab，没有生成预览资产。

Tower 同类生命周期参考在此前定向读取时受环境路径规则拒绝，未绕过；此处采用当前项目运行器所有权／配置读取／取消退出架构，拒绝引入全局单例或第二套配置真值。新增功能已获用户明确授权，按配置生成、实现、验收顺序推进。

## 验证

- Luban 生成与再生成一致性通过；配置 29,005、运行规则 42、动画 1,945 项通过。
- 原表现表已有 228 个非空单元格值保留，仅导出 XML 格式空白规范化；新增六列。地图源表、`tbmap.bytes`、`tbcombatrules.bytes` 和 SampleScene 与 HEAD 字节相同。
- 普通全量 EditMode：304 项，301 通过、0 失败、3 项 Explicit 跳过。MCP job `cd6bbe24a1044b93821f8f3b3e020054`。
- 普通全量 PlayMode 最终：17 项，12 通过、0 失败、5 项 Explicit 跳过。MCP job `bc12d550ba724abf910b6115b9b73ab1`。
- 新 PlayMode 用例连续进入 Stage 1、磐岳 101、周天 102 与 Combat 4，使用正式资源、无 Profile 的隔离运行器：默认显示、真实相机红色像素显示／隐藏差值、恢复、实际移动边界不变、正式结算后重开、单实例不重复、退出材质／对象／World／句柄清理均通过。
- 首轮新增用例误在正式战斗中直接请求重开而失败；已修正测试前置为受控失败结算，没有放宽正式重开规则或像素断言。原有用例继续通过，失败响应保留在忽略的本地验证目录。
- 截图已目视确认普通图与试炼图的红线实际显示：`outputs/combat-config/runtime-review/boundary-stage1-visible.png`、`boundary-trial-visible.png`。测试将玩家逻辑位置放到边缘并暂停，仅为可见性验证，不代表自然跑图或平衡结论。

用户原来运行的是 Stage 1（角色10001／地图1），其旧红框只存在于试炼标记器，故当时没有边界线。本次先按用户要求使用现有菜单将本地启动覆盖从 `-stage 1` 切换为 `-tribulation`，确认试炼选角就绪；为配置编译及隔离测试暂退 Play，完成后再次恢复同一选角入口，不长期替用户选择职业。

最终入口状态另以本任务完成时的 MCP 实际核验为准。未提交、推送或打包；测试未接触真实玩家存档。用户人工体验与发行包验证待进行，原性能门禁 4/6、2000 档未通过的结论不变，本轮未运行 Explicit 压力专项。
最终 MCP 已核验：工程为 rouge，Play=true、compiling=false，启动覆盖 -tribulation；试炼选角面板可见、磐岳／周天按钮就绪、ActiveRunner=null，纯菜单边框实例为0，SampleScene 未脏。未替用户选择职业。
