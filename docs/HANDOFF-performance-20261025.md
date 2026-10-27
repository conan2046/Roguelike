> 本页保留修复前历史基线；最新CPU修复/六组复测见 [CPU修复报告](rouge-performance-cpu-repair-report-20261025.md) 与 [最新交接](HANDOFF-cpu-repair-20261025.md)。CPU方案已确认并实施，2000档仍未通过。

# M1.5 性能诊断交接

2026-10-25。合法测试场地的500/1000/2000×基础/六超武六组采集完成，全部未通过现有60FPS预算；不能关闭M1.5。核心内容B0～B4的功能完成状态不变。

- 报告：`rouge-performance-report-20261025.md`。
- 下一批正式CPU修复方案：`rouge-performance-cpu-repair-plan-V1.0.md`，待确认；本轮没有改正式移动求解算法。
- 基线：`.local/performance-20261025/20261024-204225/`；短诊断：`20261024-205829/`。accepted-baseline.json固定两组入口；latest.json不是完整结果入口。
- 初始/六超武平均FPS：500档7.43/7.19，1000档2.59/2.95，2000档1.32/1.29。出生重叠/越界=0、全部在视野内、Burst=true、存活负载不掉。
- 阶段定位：500档整体模拟P95约125.7ms，移动/建索引约124.5ms，占已标记阶段累计时间99.28%。优先修原生状态访问和移动接触退化；具体子循环占比仍需计数。
- 新增测试文件CombatPerformanceProbeTests.cs及meta；CombatTickJob仅增加六阶段Profiler标记和注释，不改变业务顺序。正式Excel、C#/bytes、Scene/Prefab未改。
- 普通EditMode 199通过/2 Explicit跳过，普通PlayMode 5通过/2新Explicit跳过。两项性能采集用例各1/1通过不等于预算通过。历史误选/非法夹具/超时组排除验收。
- 测试使用1920×1080内存目标、固定扩大测试场地/镜头、补怪/玩家恢复；无经验/箱/UI调度，不代表正式关卡或Player/GPU验收。
- 普通PlayMode会用LogAssert.Expect模拟域失效，Console中出现的一条“Combat runtime state was invalidated”是已断言的预期异常；保留原始日志后清理并复查，不能当作新增运行缺陷。
- 收口检查：`.local/performance-20261025/final-audit.json`、MCP最终清理/Console记录。Play关闭、场景未改脏、Runner/World=0；Burst/Profiler/帧设置恢复。未提交、推送或构建。

继续时先读性能报告和CPU方案，勿重跑旧非法阵列。所有Unity操作只经核验的本项目MCP；当前服务沿用用户预先启动的8081，不关闭其他项目或全局服务。不要git add -A。

## 最终收口检查

105项原已跟踪脏改动中104项字节不变，唯一代码交叠是CombatTickJob的阶段Profiler标记；没有业务计算顺序变更。Scene差异0、新预览资产0、暂存0，16个新增/修改函数XML检查无缺项。最终Play=false、Scene dirty=false、Runner/World=0；Burst/CPU Profiler关闭，原启动覆盖仍为-stage 1。普通PlayMode已断言的域失效异常单独留证后清理，清理后Console Error/Warning=0。测试与编译结束，未提交、推送或构建。
