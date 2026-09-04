# rouge · M1 核心战斗 Prototype 功能规格（Functional Spec）

> 上游文档：`rouge-functional-framework.md`（M0 ✅ 关闭）。
> 定位：从「策划框架」进入**可执行开发规格**。本稿只拆 M1 的战斗链路，不扩功能框架。
> M1 目标（来自框架 §8）：验证**基础战斗手感与反馈是否成立**（≠ 完整单局好玩，那是 M2）。

## 0. 目标与验收口径
- **目标**：用最小资源跑通「移动→自动攻击→击杀→掉落→升级→Boss→结算」全链路，验证打击感 / 反馈 / 基础循环是否成立。
- **不做**：Build 详细规则、Meta、商店、事件、地图节奏、完整音效、完整 UI（用最简占位）。
- **验收（M1 完成标准）**：1 角色 + 1 武器 + 3 怪 + 经验球 + 升级三选一 + 1 Boss；玩家能移动/自动攻击/击杀/拾取/升级/打 Boss/结算；帧率 ≥60（M1 规模，少量单位）；无托管崩溃。

## 1. 范围（In / Out）
| In（M1） | Out（留给后续） |
|---|---|
| Player Movement、Enemy Spawn、Auto Attack、Projectile、Damage、Death、EXP、Level Up（三选一）、Boss、Settlement | Build 详细规则（Reroll/Lock/Skip/组合）、Meta、商店、事件、地图节奏节点、完整音效、完整 UI、ECS 性能验证（M1.5） |

## 2. 架构决策（待确认 · M1 编码前唯一阻塞项）
> 框架 §6 写的是 **ECS-from-MVP**（M1 即上 Unity.Entities）。但 v2.1 评审把 ECS 放在 **M1.5 做性能验证**，隐含 M1 先非 ECS。两条路线二选一，决定 M1 代码结构：

| 路径 | 做法 | 取舍 |
|---|---|---|
| **A. ECS-from-MVP（框架默认）** | M1 直接用 Unity.Entities + Jobs + Burst；Entities Graphics 实例化 quad + flipbook；§3 用 ECS 组件 | 移动端同套 sim，无二次重写；但 M1 调试成本略高（DOTS 上手） |
| **B. MonoBehaviour 先验证手感** | M1 用池化 MonoBehaviour 跑通手感；M1.5 再整体转 ECS | 手感验证最快；但 M1.5 有重写风险（你历史经验：手机不 ECS 会卡，倾向 A） |

**无论选哪条，M1.5 都是性能/动画链验证门（500/1000/2000 同屏）。** 本条不定，M1 不编码。

## 3. 模块功能规格（链路顺序）
> 组件名以 ECS 为基准写法（路径 A）；路径 B 时对应为 MonoBehaviour 字段。

### 3.1 Player Movement
- 输入：WASD / 摇杆；相机跟随（沿用 `CocosMapCameraFollow`）。
- 碰撞：沿用 `CocosMapCollisionGrid`（93×50 阻挡网格）。
- 组件：`PlayerTag` / `Translation`(MoveSpeed) / `InputMove`。
- 验收：平滑移动、撞墙阻挡、相机跟随无抖。

### 3.2 Enemy Spawn（MVP 版 SpawnDirector）
- 按时间曲线生成（M1 仅 0~3min 低压段，见框架 §2.7）；固定 Seed 复现。
- 组件：`EnemyTag` / `EnemyType` / `HP` / `MoveToPlayer`。
- 验收：3 种怪按曲线出现，重置同 Seed 结果一致。

### 3.3 Auto Attack
- 自动锁定最近敌人，固定间隔发射子弹（M1 单武器，无选靶策略）。
- 组件：`AttackInterval` / `AttackTimer` / `Damage`。
- 验收：持续自动开火，攻击间隔稳定。

### 3.4 Projectile
- 子弹实体：`Translation` / `Velocity` / `Damage` / `Lifetime`（超时回收）。
- 碰撞：M1 用简单圆/距离碰撞，不进 ECS 高级碰撞系统。
- 验收：子弹命中敌人扣血、超时消失、无泄漏。

### 3.5 Damage & Death
- 命中 → `HP -= Damage`；`HP ≤ 0` → 死亡 → 生成经验球。
- 组件：`HP` / `DamageEvent`。
- 验收：击杀反馈（闪白/顿帧/音效占位）、经验球掉落。

### 3.6 EXP & Pickup
- 经验球：`Translation` / `EXPValue` / `Magnet`（进入吸附半径向玩家移动）。
- 玩家 EXP 累积达阈值 → 触升级。
- 验收：拾取即涨经验、吸附手感、升级触发。

### 3.7 Level Up（三选一）
- 升级 → 暂停 → 三选一 UI（最简）。
- M1 卡池（因仅 1 武器，简化）：**武器强化（升 1★）/ 新被动 / 回血** 三选一（完整卡池规则见框架 §2.4，M2 落地）。
- 验收：暂停正确、三选项生效、武器星级 +1 实际改变伤害。

### 3.8 Boss
- 1 个 Boss，较高 HP，固定攻击模式（M1 不做多阶段）。
- 验收：Boss 出场、可击杀、击杀触发胜利结算。

### 3.9 Settlement
- 死亡 / 胜利 → 结算面板（击杀数 / 等级 / 时长）→ 返回主菜单。
- 验收：结算数据正确、可返回、Run Save 删除（见 §5.6 框架）。

## 4. 数据 Schema（Luban 数据表 · 数据层先立）
- 武器表：id, name, star(1~5), baseDamage, attackInterval, animKey。
- 敌人表：id, type, hp, speed, damage, expValue, animKey。
- 波次表：timeWindow, spawnTable（enemyId → weight/count）。
- 升级表：options[](type, payload)。
> 表名、主键、复合类型、生成目标与加载方式必须先完成方案讨论再落表；数据表是唯一源头，代码和 ScriptableObject 不保留重复默认值。数值为占位，M3 前由 game-numerical-designer 定。

## 5. M1 内部切分
- **M1.1**：URP 切换 + ECS 空场景跑通（实例化 quad 渲染 1 个会动敌人）。
- **M1.2**：Player 移动 + 自动攻击 + 子弹 + 敌人死亡掉落。
- **M1.3**：EXP + 升级三选一 + Boss + 结算。
- **M1.4**：手感调优（打击感 / 反馈 / 相机 / 最简音效占位）。

## 6. 验收清单（Definition of Done）
- [ ] 相关数据表、Luban 生成配置及生成物完整，新机器可复现生成。
- [ ] 功能代码只读取 Luban 配置，不含业务硬编码或重复默认值。
- [ ] URP 管线生效，渲染玩家 + 敌人。
- [ ] 移动 / 自动攻击 / 击杀 / 掉落 / 升级 / 三选一 / Boss / 结算 全链路通。
- [ ] Seed 复现 Spawn / Card。
- [ ] 帧率 ≥60（M1 规模）。
- [ ] 无托管崩溃；Run Save 死亡后删除。

## 诚实边界
- M1 不验证完整 Build 乐趣（那是 M2 的职责）。
- **§2 架构路径 A/B 未定**，不定不编码；选 B 则 M1.5 整体重写 ECS。
- 数值为占位，非平衡值。
- 动画桥 `CocosAniInstancedPlayer` 在 M1 可先用单帧图占位，M1.5 验证。
- 本稿不扩功能框架，仅拆 M1 执行规格。

## 7. 环境搭建步骤（Unity Editor · M1.1）
1. 打开项目，Package Manager 按 `manifest.json` 解析 **URP 14 + Entities 1.0 + Entities Graphics 1.0 + Jobs 0.70 + Burst 1.8**（解析报错则按 PM 提示锁定兼容补丁版本；引擎 2022.3.62f3c1）。
2. 创建 URP Asset：`Assets/Settings/URPAsset.asset`（Create → Rendering → URP Asset），在 **Project Settings → Graphics / Quality** 绑定为 Render Pipeline Asset。
3. 任一 URP/Lit 材质勾选 **DOTS Instancing**（Material 面板 → Advanced → Enable DOTS Instancing），作为 `enemyMaterial` 赋给场景中的 `RougeBootstrap`。
4. 新建 Scene：放 **Orthographic 俯视 Main Camera** + 一个挂 `RougeBootstrap` 的 GameObject（赋值 `enemyMesh = Quad`、`enemyMaterial`）。
5. 运行：应看到 ~200 个 quad 实体朝 -z 移动 → 证明 **ECS + Entities Graphics 实例化渲染管线打通**（M1.1 完成判据）。

> 注：Cocos 动画桥 `CocosAniInstancedPlayer` M1 先用单帧图占位，M1.5 再接 `.ani` flipbook。
