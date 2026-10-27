# 表现桥修复交接

2026-10-25。用户已确认表现方案V1.0并授权接续实施；R0定位、R1共享快照/变化时写入、R3自动化与正式入口回归、R4完整六组已执行。**4/6预算通过，2000两组仍失败，M1.5未通过。**

- 报告：`rouge-performance-presentation-repair-report-20261025.md`；原批准方案：`rouge-performance-presentation-repair-plan-V1.0.md`。
- FPS（基础/六超武）：500=163.05/143.32，1000=107.12/106.79，2000=61.99/53.08。2000帧P95=18.45/26.35ms；平均FPS达标不代表P95通过。
- 表现桥P95（基础/六超武）：2000从6.26/6.56ms降至3.33/3.34ms。六组真实Burst、3600采样Tick、热路径GC0、欠账稳定、初始重叠/越界0、人数与起止可见数保持。
- `CombatSession.CapturePresentationUnits`使用会话独占NativeList，从权威ECS只读lookup任务采集。每次同步刷新，主体/血条共享借用，不用过期Tick缓存；扩容来自实际槽数，Dispose释放。独立Synchronize仍重新采集。
- 主体缓存已提交可见性/网格/材质；血条只在位置/比例/可见性变化时写组件。ANI同Tick跳过前比较所有选帧输入，同Tick外部死亡和复用已专测。
- 2000实际EG命令/实例中位=6007/6007、6146/6146；Render DrawCall中位=6013/6152。DepthSorted路径每命令一个实例；不能把已开启的enableInstancing当成修复。Frame Debugger尝试返回空事件，未取得单事件GPU证据。
- **原R2补充已由用户明确批准并执行原型。** 两个原型未通过零差异门禁，未进入正式提交；当前仅新的同深度稳定排序取舍待答复，见本文件末尾更新及最新原型报告。
- 补充建议先仅做血条内存批次原型，同状态/同相机像素对照保持后再落地；不含Shader接口或URP变更，不允许降低外观标准。未经诊断不得取消DepthSorted或重排透明单位。
- 完整样本：`.local/performance-20261025/20261025-022101/`；冻结入口`accepted-presentation-repair.json`。本批RPC、聚焦diff、保护哈希和XML审计在`presentation-repair/`。CPU批样本与原生位级证据保留，勿混用非法旧阵列或重跑前批CPU优化。
- 普通Edit最终210项/207通过/0失败/3 Explicit跳过；Play8项/5通过/0失败/3性能Explicit跳过。新增两个Edit测试通过；早期失败是测试适配器不支持导入纹理及地址还原错误，已修复。勿整类选择CombatSessionTests触发全部Explicit专项。
- 正式入口升级卡EventSystem射线选择连锁灵梭；按真实战斗规则与输入帧推进后02:12.016自然失败、99击杀。结算按钮射线重开回到等级1/击杀0/时间0/仅技能20001，生命周期增加。再验证120Tick移动至(7.155418,3.577709)、相机跟随、暂停冻结。未注入生命/经验/伤害/输赢；不冒充硬件键盘事件通过。
- 最终Play=false、Scene dirty=false、Runner/World=0、Console错误/警告0；Burst=false、同步编译=false、Profiler/CPU area=false、vSync=1、targetFrameRate=-1、runInBackground=true。用户预启的8081服务保留，不关闭其他进程或改全局配置。
- 冻结15045项文件，源Excel、生成C#/bytes、Scene/Prefab、Shader和ProjectSettings哈希保持。八份本任务C#改动、方案确认及本交接/报告为本批范围；XML审计32个变更函数0缺项，diff检查通过，无新增Assets、预览资产、暂存、提交、推送或Win64构建。

后续更新：用户已明确批准原R2补充，已继续实施原型。直接合批和保守混合原型均未通过零差异门禁；混合原型仍有30个重叠像素变化，未接入正式路径。最新结果见`rouge-performance-healthbar-prototype-report-20261025.md`；新的明确取舍见`rouge-performance-healthbar-stable-order-decision-20261025.md`，仅该“允许固定同深度重叠顺序”的新取舍待答复，不得再次索要原R2补充许可。另已修复隐藏/复用时配置缩放恢复，并补独立测试TearDown以确保失败清理。人工画面/可玩性、Player/GPU与Win64仍后置，不宣称完成。

当前任务文件已达21.15MiB，按AGENTS的20MB门槛收口。下一步必须新任务接续，不读取或复制本任务完整会话；先读最新原型报告、稳定排序取舍和本交接。当前未提交、未推送、未构建，8081预启服务保留。

最新用户确认：用户已明确选择“允许稳定排序，继续合批”。稳定同深度血条顺序已获批准，不能再次索要原R2补充或该排序取舍的许可。新任务直接先建立固定顺序参考基线与合批原型，按新基线验证RGBA，保持资源、源颜色/Alpha、尺寸、锚点、前后层深度及Shader/URP接口；通过后落地并执行普通Edit/Play、真实入口及六组完整容量验收。旧原型失败证据继续保留，不将旧失败断言改为无条件通过。当前任务因超过20MiB保持收口，不在此继续堆积。

## 本次续接状态（稳定排序已实施）

正式血条合批已落地；七状态RGBA零差异、普通Edit/Play及正式入口自动回归通过。完整容量仍4/6，2000两组未通过；下一步修复探针采样时点及Recorder环回，再定位新增血条桥接开销。用户已要求打开Unity看效果，当前主窗口已恢复可见、正式关卡保持Play。最新报告与续接见`rouge-performance-healthbar-batching-report-20261025.md`和`HANDOFF-healthbar-batching-20261025.md`；勿把旧“正式合批尚未落地”当当前状态。
