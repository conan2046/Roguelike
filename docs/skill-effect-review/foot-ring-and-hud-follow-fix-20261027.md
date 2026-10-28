# 护环脚点与 TopBar 移动缝隙修复

后续用户实际画面仍发现漏边，证明本文中只验证矩形角点的 HUD 方案不足。固定 HUD 已改为屏幕覆盖层，并补充三种尺寸、72 个移动帧的真实屏幕像素验收，详见 [TopBar 真实移动画面漏边修复](hud-rendered-edge-fix-20261027.md)。以下保留当时脚点修正与几何验证记录。

## 原因与修正

- 黄色光圈是玩法 20043 / 护体罡环的 `skill_66`（30097）。正式角色的身体中心高于根脚点约 72.66 像素，原范围表现跟随时叠加了 BodyOffset，导致光圈跑到身体位置。跟随型范围层改为角色根位置加 TbSkill.areaVisualOffset；20043 与进化 20037 都采用该语义。落点范围层保持事件位置，伤害判定和范围事件不变。
- TopBar 属于 ScreenSpaceCamera HUD。相机在 LateUpdate 移动，HUD 原来与相机同级挂在静止运行器下，Canvas 排版后相机移动时出现一帧屏幕位置差。仅启用 pixelPerfect 未解决；将 HUD 实例挂在本局战斗相机下后，父子变换在同帧继承位移。维持原 Prefab 的锚点、宽高、偏移、字体、颜色和 Canvas 缩放，不逐帧重写布局。

## 核验范围

本轮为已授权的表现缺陷修复，无新增策划参数或源表变更。采用现有 CombatEntityVisuals / CombatUiRuntime 的事件、加载、取消及释放链；HUD 的所有权仍由 CombatUiRuntime 持有，关闭时先销毁 HUD，再由运行器销毁相机。没有新增全局状态、预览资产或业务常量。Tower 对应参考路径仍不可访问，按本工程现有分层核验。

现场通过已核验的 8082 Unity MCP 读取到角色 BodyOffset 为 `(0.00485, 0.7266)`；黄色光圈正好叠加该偏移。相机在 Canvas 排版后采样位移，原层级使左边缘变化约 13 像素、顶边变化约 8 像素；相机父节点方案的左边缘误差约 0.00016 像素，顶边保持不变。该样本为受控相机位移，用于重现更新顺序，不代表正常移动每帧都会漂移同样距离。

## 验证结果

- Unity MCP EditMode 19/19 通过：既有范围技能的基础/进化与升级放大、脚点跟随、播放结束回收，以及随身施法层回归。
- Unity MCP PlayMode 3/3 通过：正式护环与 HUD 屏幕稳定性、正式随身施法、正式关卡 UI/升级/结算/重开全流程。
- 新测试在 24 次相机位移采样中，TopBar 屏幕左上角最大漂移为 0 像素，锚点、宽高与 anchoredPosition 保持不变；正式相机截图中黄色护环已位于脚下。
- 编译通过，git diff --check 通过；本轮没有更改源表、Scene 或 HUD Prefab。测试退出后曾留有一条旧运行器在域重载后失效的保护性提示，记录保存在 post-test-console.json；随后通过正式启动菜单正常重进，确认 Ready、HUD 父节点为 Combat Camera、Console 错误为 0。

修正后截图：`outputs/combat-config/runtime-review/guard-feet-and-stable-hud.png`。代码备份与工具原始记录保存在 `.local/foot-ring-hud-fix-20261027/`。为编译已退出用户原先暂停的测试局；收尾正常重开正式关卡，并停在 Unity 编辑器暂停状态供查看。没有提交、推送或构建。
