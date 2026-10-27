# Ground 按行走范围与视野自动覆盖

用户已明确要求 Ground 宽高由当前行走范围和设备可见范围计算。采用左右各半个视野宽、上下各半个视野高：总宽 = 2 × arenaHalfWidthPixels + 视野宽，总高 = 2 × arenaHalfHeightPixels + 视野高。

视野高读取 TbMap.cameraViewportHeightPixels，视野宽由当前摄像机 aspect 计算。这里使用同一套逻辑像素，不直接将设备物理像素加入地图尺寸。完整地块按 TbMap.tileSizePixels 在两侧对称向外取整；除法、对称倍数、取整和坐标换算均为几何技术逻辑，无新增业务常量或配置默认值。

现有 TbMap 的 arenaHalfWidthPixels、arenaHalfHeightPixels、cameraViewportHeightPixels、tileSizePixels、纹理、种子及权重已足够，无需新增或修改源表、表结构及生成物。mapWidthPixels/mapHeightPixels 暂保留为无视口地图模板及旧工具配置，不再决定交互战斗 Ground 的实际大小。

采用现有 CombatMapDefinition → CombatTilemapBackground → CombatRuntimeRunner 链路。增加视口派生布局并在视野跨地块阈值变化时更新 Tilemap；复用已加载纹理、Sprite 和 Tile，不重新加载资源。格子取样锚定地图中心，窗口比例变化时不改变已有世界格子的纹理。相机跟随使用实际地面布局边界，不再使用旧固定宽高限制。

保持原资源所有权、异步取消和退出回收顺序；不新增 UI、持久化对象或预览资产，不改碰撞、技能、角色尺寸、地图行走范围或其他工程。Tower 的 Battle/Manager 参考路径当前不可访问，已做本次范围检查；依据现有项目结构实现，不引入旧配置链。

验收：验证横屏、竖屏、超宽比例下四边与四角均有地面覆盖；验证尺寸取整、重复设置不重建、窗口变化不重载资源及已有世界格子稳定；验证真实入口、镜头跟随、重新进入和资源清理。Unity 验证完成后供用户试玩，不构建、提交或推送。

## 实施与验证结果

已在原项目通过 8082 的 Rouge MCP 安装三处运行时代码和对应测试，配置说明同步更新。TbMap 及 Luban 生成代码和 bytes 均未修改；542 个保护文件（场景、战斗 Prefab、源工作簿和运行时配置）与本轮基线一致，无新增预览资产。

- Unity 编译通过，控制台无编译错误。
- `CombatMapTests`：12 项通过，覆盖两张正式地图的横屏、竖屏和超宽比例、四角覆盖、非法比例、重铺后格子稳定及资源复用与释放。
- `RealResourcesInputCameraAndRepeatedExit` 与 `DestroyDuringLoadingReleasesPartialWorld`：2 项 PlayMode 测试通过；真实资源入局两次，暂停时切换比例并移动到四角，确认相机不被旧尺寸限制、完整可见范围有地面覆盖，以及取消和退出回收。
- `Tools/Config/validate-combat.ps1`：配置 28985、运行时 42、动画 1945 项检查通过。
- 实际选角按钮入局检查：当前 1334×750 视口，行走范围 1920×1280 逻辑像素，可见范围约 2668×1500；Ground 为向外补齐地块后的 4608×2816，共 792 格。已观察真实游戏画面并经返回按钮回到选角，供用户继续试玩。

证据保存在 `.local/ground-viewport-20261027/`。上述为自动化与实际入口检查；用户人工试玩确认仍待进行。未构建、提交或推送。
