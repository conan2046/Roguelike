# 随身施法效果正式接入

## 已确认行为与边界

依据用户确认：角色边走边释放；随身施法 `_s` 跟随施法者，与 `_f` 同时开始，不等待前摇。落点用途 `_s` 仍走范围表现，本次不改为随身层。此次“接入正式战斗”授权覆盖下述配置、播放和验证。

## 接入方案

- TbSkill 增加可空 castClipId、castVisualOffsetXPixels、castVisualOffsetYPixels、castVisualScalePermille；挂点是施法者根位置加配置像素偏移。缺少随身资源的组保持空，不借用别组。
- TbAnimationClip 的 EAnimationAction 增加独立 CastEffect 角色；片段非循环，生命周期取正式 ANI 时钟，不额外配置或硬编码两秒延迟。
- 正式施法组：玩法 20001→30004、20016/20032→30053、20027→30044、20044→30069、20030→30079。skill_14 没有 `_s`，保留既有飞行/命中。为每个玩法技能添加独立 TbCombatRender 绑定。
- CombatTickJob 在实际释放时写一次带施法者槽、生命周期、技能、攻击序号和 tick 的事件，散射多弹丸不重复写事件，也不增加攻击或伤害序号。CombatSession 拥有事件容器，Advance 清空、重开清空、Dispose 释放。
- CombatVisualResources 通过既有 YooAsset 入口预载显式随身片段；失败/取消走原释放链。CombatRenderCatalog 校验角色与归属；不按运行时文件名或后缀猜绑定。
- CombatEntityVisuals 独立池化施法表现实体，消费一次释放事件，按模拟 tick 播放、每次同步跟随施法者位置。结束回池；施法者死亡或生命周期改变立即隐藏；重开清空；退出销毁表现实体。暂停冻结时序，不影响角色移动和弹丸运动。

技术边界：事件、池槽、去重游标为技术状态；片段、偏移、缩放、队列均由 Luban 数据提供。保留现有伤害、冷却、攻击阵型、移动和碰撞数据，不新增 Prefab/Scene 或预览资产。

## 参考审查

本次针对 Tower 的 Battle Skill/Entity 参考路径检查不可访问，继续依据本工程分层。采用现有 CombatTickJob→CombatSession→CombatEntityVisuals 调用链与 ANI 资源所有权、取消释放和 Entities Graphics 内存对象；新增独立随身角色及事件，避免让施法表现依赖存活弹丸。拒绝套用角色施法动作停步、额外前摇、网络或全局单例。此功能没有新增 UI 绑定、存档或外部消息。

## 验收标准

1. Excel→Luban 生成、复生成一致性及配置校验通过；空绑定不加载任何随身资源，缺少引用/队列/非法角色则失败。
2. 正式关卡与测试入口都在发射同一 tick 播放随身层；多弹丸仅一层，重复同步不重复创建。
3. 移动时随身层跟随施法者，弹丸沿原弹道独立移动，地面范围层不改挂点。
4. 暂停冻结、ANI 播完回池、死亡/重开/退出清理；复用单位槽不能继承旧施法层。
5. Unity MCP 编辑器及实际 PlayMode 验证后交由用户检查画面；不构建或发布。

## 实施与验证结果

已完成正式配置与运行链接入。

| 玩法 ID | 名称 | 随身片段 | 资源组 |
|---|---|---|---|
| 20001 | 追星飞刃 | 30004 | skill_1_s |
| 20016 | 连锁灵梭 | 30053 | skill_32_s |
| 20027 | 焚天龙符 | 30044 | skill_27_s |
| 20030 | 御龙连梭 | 30079 | skill_50_s |
| 20032 | 兼容测试 skill_32 | 30053 | skill_32_s |
| 20044 | 万象星羽 | 30069 | skill_44_s |

本轮偏移均配置为 0 像素、缩放 1000 千分比，队列为 3010；这些值仅存在于源表，运行时代码没有第二套默认配置。播放时长使用对应 ANI 原始动作时长。

- 五份源表：TbSkill、TbAnimationClip、TbCombatRender、TbCombatPresentation 与 `__enums__.xlsx`；新增渲染绑定 1101—1106。原有源表单元格值/样式验证未变，新字段扩展至 V—Y 列；枚举新增行后仅平移其他枚举行。
- Luban 生成成功；validate.ps1 复生成比对通过。已更新生成 C# 中 SkillConfig 的字段/强引用及 EAnimationAction.CastEffect；四份相关 bytes 同步更新。
- validate-combat.ps1：配置检查 28,995 项、动画检查 1,945 项、运行规则检查 42 项通过。
- Unity MCP EditMode：59/59。覆盖随身片段错误角色/队列缺失拒绝、五个正式技能实际释放、散射一次触发、移动跟随、暂停、ANI 结束回池、死亡、施法者换代、重开后相同 tick/序号再次释放、资源释放、原有弹丸/范围/层级/碰撞与配置序列化兼容。
- Unity MCP PlayMode：2/2。新增 FormalStageMovesAndRendersCasterEffectWithoutWindup，使用正式关卡、真实 YooAsset、UI、相机和 ECS，检查发射同 tick 可移动、实际画面贡献、死亡/重开/退出；既有 RealResourcesInputCameraAndRepeatedExit 回归通过。
- 相机证据在同一冻结模拟帧对比施法层开/关，变化像素 998；PNG 和工具原始输出保存在 `.local/cast-effect-runtime-20261027/`。相机目标与读回纹理只在内存使用并释放，不产生 Unity 预览资产。
- 人工审核的使用决定、类型、元素、备注逐组对比无改动；未修改现有 Scene/Prefab、数值、冷却、移动、碰撞或进化规则。

Unity 入口：`Roguelike > 启动 > 正式关卡 (Stage)`。资源层和自动化验证已完成，整体观感仍由用户人工确认；未提交、推送、构建或发布。
