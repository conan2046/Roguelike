# TopBar 真实移动画面漏边修复

## 现场与上一轮验证限制

用户在 1334×750 Game 窗口中看到顶边和左边时隐时现的背景缝隙。现场 TopBar 的 RectTransform 和 CanvasRenderer 顶点仍覆盖 `(0, 750)` 到 `(1334, 750)`；因此上次只比较矩形角点的“漂移为零”，不足以证明最终绘制没有漏边。相机父节点方案不再作为固定 HUD 的最终实现。

## 当前实现

- CombatHud.prefab 使用 ScreenSpaceOverlay，并开启 pixelPerfect；只修改这两项 Canvas 设置，保留全部节点锚点、偏移、宽高、缩放、字体和颜色。
- CombatUiRuntime 按 Prefab 的覆盖模式加载固定 HUD，不再强制 ScreenSpaceCamera 或绑定战斗相机。HUD 在运行器下由原对象持有，关闭和加载失败时原释放链负责销毁。
- CombatUiPrefabBuilder 同步创建覆盖模式和像素对齐，仅影响缺失资产的创建；本次没有运行批量重建，也没有覆盖已有手调布局。
- 屏幕模式与像素对齐是 Unity 渲染技术设置，保存在正式 Prefab；没有新增玩法参数、业务常量或第二套布局。数据入口仍为 TbCombatUiSet 的原资源引用，不改 Luban 数据。
- 正式 HUD 截图改为 Game 帧末完整屏幕读回，以包含 Overlay；Camera.Render 继续用于只需战斗相机的场景效果对比，不用于证明固定 HUD 的屏幕覆盖。

本轮仅修复固定 HUD 漏边；护环脚点、范围缩放、角色移动、伤害、技能、进化均未改变。Tower 对应参考路径不可访问，沿用本工程的 UI 资源加载、取消及释放分工。没有新增全局运行状态或保存纹理、材质、网格等预览资产。

## 验证

- FormalHudEdgesRemainCoveredInRenderedMovingFrames：隔离实例暂用纯色顶栏标识覆盖，正式 Prefab 颜色保持原样；冻结伤害和升级，在独立 World 移动权威玩家位置，由生产 LateUpdate 跟随相机。使用 WaitForEndOfFrame 后的 ScreenCapture 像素，检测真实顶部整行和左侧边缘。
- 分辨率 1334×750、1280×720、1001×563，每种八个方向共 24 帧，合计 72 帧；每帧 topMissing=0、leftMissing=0。测试同时确认实际相机发生移动、锚点和尺寸未变。
- 临时 Game 尺寸由测试独占创建，结束恢复原选择并移除新增项，保留用户原有尺寸；首次清理索引错误已修正并重新通过。
- 第一轮 4 项 PlayMode 回归通过：真实像素覆盖、脚下护环与 HUD、随身施法、正式 UI/升级/胜负结算/重开。
- 另补充 UI 射线点击：等待实际面板完成绘制后，用 EventSystem.RaycastAll 命中按钮或标签，再沿层级执行指针点击；升级卡选择、胜利重开和失败重开均通过。原先只调用 onClick 的断言已替换，新增面板必须等待实际绘制才能进行真实点击。
- 生成器在临时内存场景中验证新建 HUD 为 Overlay + pixelPerfect，销毁后关闭临时场景，没有保存资产或改动用户场景。

正式帧末像素报告和工具输出在 `.local/hud-gap-pixel-fix-20261027/`。`live-pixel-before.txt` 是最初在 Editor.update 时机的尝试，捕获时机不正确，不能用作验收数据；以 rendered-coverage.txt 的 72 行帧末结果为准。

技能审核工具已移除 InitializeOnLoad、sceneOpened 和 delayCall 的自动恢复入口；开始 Play 时保存并关闭，仅菜单手动打开。正式战斗收尾不再调用 OpenReviewScene，当前场景回到构建设置中的 SampleScene。

已从 SampleScene 正常启动正式关卡，确认 Ready=True、HUD=ScreenSpaceOverlay、pixelPerfect=True、屏幕为用户当前 1334×750、审核窗口数为 0、Console 错误为 0。临时测试尺寸全部移除，保留用户原有 10 个选项；当前 Unity 暂停供用户检查。

普通颜色正式 UI 截图在 `outputs/combat-config/runtime-review/formal-stage.png`。没有提交、推送或构建。
