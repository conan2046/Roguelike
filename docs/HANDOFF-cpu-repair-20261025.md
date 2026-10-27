# CPU修复交接

2026-10-25。用户已批准CPU方案并连续实施；状态缓冲与精确接触循环消除已落地，P3六组采样执行完，**M1.5仍未通过：500/1000档通过，2000档两组失败**。

- 最新报告：`rouge-performance-cpu-repair-report-20261025.md`；批准方案：`rouge-performance-cpu-repair-plan-V1.0.md`。
- 平均FPS（基础/六超武）：500为140.84/125.53；1000为86.65/87.94；2000为47.38/41.35。六组都3600采样Tick、真实Burst、存活/可见数保持、初始重叠/越界0、局部热路径GC0、欠账稳定。
- 源表、生成物、碰撞体、移动公式、Shader/URP/透明策略均未改。不要把“整帧仍失败”解释成允许减怪、隐藏血条或缩小碰撞体。
- 缓冲由CombatSession独占；每Tick采集/提交，动态生成增长，重开覆盖，Dispose释放。CombatTickJob对原移动公式只加位级不变点/周期检测；不要保留或恢复早期投影实验。
- 原生新旧600 Tick逐位一致；千怪双世界确定性通过。普通Edit205通过/3 Explicit跳过，Play5通过/2 Explicit跳过；6类边界参照通过。没有将Explicit采集1/1冒充预算通过。
- 正式入口、升级卡射线点击、输入帧移动/相机/暂停通过；独立单局02:12.016自然失败99击杀，结算按钮射线重开通过。旧InputManager键盘事件未自动化，后续不可声称硬件键盘已测。
- 完整六组：`.local/performance-20261025/20261024-235204/`；冻结入口`accepted-cpu-repair.json`。历史首轮`20261024-204225`与非法阵列不可混用。
- 本批全部MCP结果与聚焦diff/哈希：`.local/performance-20261025/cpu-repair/`；真实入口与失败截图也在其中。早期对局中重开探针被规则拒绝，已用正确结算路线补验。
- 最终Play=false、Scene dirty=false、Runner/World=0、Console错误/警告0；原Burst/Profiler/帧设置及-stage 1已恢复。用户预先启动的8081服务保留，未改全局配置或操作其他Unity实例。
- 新增测试文件：CombatCpuTrajectoryTests.cs、CombatMovementConvergenceTests.cs及meta；原性能探针追加接触计数。所有现有脏改动保持；无提交/推送/Win64构建/预览资产。

后续更新（2026-10-25）：用户已确认`rouge-performance-presentation-repair-plan-V1.0.md`，并在新任务完成共享快照、变化时写入及六组完整复测；2000两组仍未通过。最新入口为`HANDOFF-presentation-repair-20261025.md`，勿再次索要原方案许可。进一步改变血条透明排序提交方式的R2补充仍待单独明确确认。人工试玩、Player与Win64仍后置；不得自动创建任务。
