# 血条稳定排序与合批交接

## 2026-10-26 最新端口定位

用户明确将 Rouge MCP 改为 `http://127.0.0.1:8082/mcp`；8081 属于另一个工程，不再调用。已在8082读取实例、钉选 `rouge@6107edba5f4d75bd`，并从项目资源核实 `projectRoot` 是当前项目根目录、`assetsPath` 是其 `Assets` 子目录；当次Unity PID62688、窗口标题以`rouge - SampleScene`开头。项目本地mcp_client.py已改为8082并使用独立会话文件；验证记录在`.local/mcp-routing-20261026/`，当前定位在`.local/unity-mcp-routing.json`。PID会变化，后续操作必须重新核对，不能沿用历史26900。用户已反馈试玩“ok”；此反馈不代表2000档性能门禁通过。

2026-10-25。正式合批已落地、七种RGBA零差异、普通Edit/Play和真实入口回归通过；完整容量4/6，M1.5未通过。当前用户正在Unity看效果，保持Play，不触发编译/测试打断。

先读`rouge-performance-healthbar-batching-report-20261025.md`和原stable-order-decision。批准边界仍为固定同深度槽序及内存合批，不需要再次索要许可。

- 本轮实际改动：CombatFeedbackVisuals.cs、CombatVisualTests.cs、CombatPerformanceProbeTests.cs；R1与其他既有脏文件保留。Runtime用OrderedHealthBarBatch两层动态网格，每层槽序递增，源图集/材质/UV/深度保持，无新资产。
- 像素原型071442、正式路径071748，七状态差异均0、JobWorkers15；旧失败门禁断言保留。
- Edit207通过/0失败/3跳过；Play5通过/0失败/5跳过。专项分别独立通过，但性能专项传输成功不表示预算成功。
- 六组正式样本072131，500/1000通过；2000基础49.26FPS/P95 27.476ms、六技能44.18FPS/P95 29.484ms。绘制2013/2149，EG命令2005/2141。2000基础只有3599Tick，不能称六组均3600。
- 已定位测试仪表两问题：显式容量1的阶段Recorder缺WrapAround而固定首次读数；采样终点按预热起点截断可能少一帧。先修测试计时与Recorder再定位血条CPU上传；不直接改预算或统计值。代码尚未修改这两点。
- cpu-comparison.json说明基础每Tick均值7.128→7.408ms，桥接均值2.532→5.211ms；模拟P95双Tick追赶放大。下一步重点定位新增网格CPU变换/上传，必要时在已批准纯渲染范围优化。不要无证据归因模拟Job或外部游戏。
- 同机Steam game.exe占约3CPU核；已异步询问用户“退出/暂停后再采样”或“保持当前并行负载”。尚无文字答复，用户后续要求打开Unity看效果。
- 本轮正式自然失败132016ms/99杀、射线升级/重开、120Tick移动/相机跟随/暂停均通过。formal/有新截图与RPC。
- 当前Unity PID26900，明确projectPath为原项目，主窗口已恢复可见，正式关卡Play供用户操作。不要自动关闭或编译打断；用户看完继续时先核实状态。8081服务保留，未重启。当前预览不是2000压力场景，不把Stats面板瞬时FPS当性能结论。
- MCP脚本stable.py，根`.local/performance-20261025/stable-order/`。before与baseline已冻结，audit.py可查源文件/XML/保护哈希；report.py是尚未使用的6/6模板，不得在预算失败时盲目生成通过报告。checkpoint.py生成本次如实报告。不要运行旧finish.py。
- 无worktree/子代理/提交/推送/Win64，无Scene/Prefab/表/Shader修改，无序列化预览资产。手写函数XML完整。用户人工验收与性能门禁均不能提前宣称完成。
