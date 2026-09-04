# rouge 肉鸽战斗场景架构改造方案（v2 · 含移动端 ECS）

> 修订触发：后续需迁移移动端；历史移动端项目均用 ECS 否则手机卡顿。
> 本版将全量 ECS/DOTS 从「不引入」提升为「移动端必须、PC 同步采用同一套 sim」，并相应调整渲染与热更选型。

## 0. 与 v1 的核心差异

| 项 | v1（仅 Steam） | v2（Steam + 移动端） |
|---|---|---|
| ECS/DOTS | 不引入，压测不达标再考虑 | **采用**：移动端必须，PC 用同一套 sim |
| 表现层渲染 | SpriteRenderer flipbook | **实例化 quad flipbook**（ECS 无 SpriteRenderer，且天然 GPU 合批） |
| YooAsset/Addressables | 不上 | 移动构建引入（分包/远程资源） |
| HybridCLR | 不上 | 移动构建引入（C# 热修） |
| 渲染管线 | 不限 | 项目需切 **URP**（BiRP 不支持 Entities Graphics） |

## 1. 技术决策矩阵（修订版）

| 技术 | PC (Steam) | Mobile (未来) | 决策 |
|---|---|---|---|
| Unity.Entities ECS + Jobs + Burst | 用（同套 sim） | 必须 | **采用**，从 MVP 起作为模拟主干 |
| Entities Graphics 实例化 quad + flipbook shader | 用 | 用 | **统一渲染方案**，PC/移动同一套 |
| Luban 数据表 | 用（武器/敌人/波次/升级） | 用 | 数据表是唯一源头，Luban 生成 C# 与运行时数据，再装载进 ECS 组件 |
| YooAsset / Addressables | 不需要 | 需要 | 移动构建引入，PC 不引（`#if` 隔离） |
| HybridCLR | 不需要 | 需要 | 移动构建引入（C# 热修） |
| URP | 需要 | 需要 | 项目切 URP（Entities Graphics 前提） |
| MonoBehaviour / UGUI | 用（UI/场景/非性能敏感） | 用 | 仅元层与 UI，不参与大群模拟 |

## 2. 分层架构

```
[Luban 数据层]  数据表 → Luban 生成代码/数据 → 统一配置入口 → ECS 组件初始值
      ↓
[ECS 模拟层] Unity.Entities + Jobs + Burst   —— 唯一真源（确定性 seed RNG）
   Components: Position2D, Velocity2D, Health, Faction,
               AnimState(clip,time,frame), SpawnTimer, Separation ...
   Systems(均 Job/Burst):
     SpawnSystem → MovementSystem(朝玩家+分离) → AttackSystem(武器tick AoE)
     → DamageSystem → DeathSystem(回池) → AnimTickSystem(推进frame)
      ↓
[渲染层]    Entities Graphics：每个可视实体 = 1 个 quad mesh
           材质 + kapai sprite-sheet 图集，AnimState 写入实例缓冲
           shader 按 frame 计算 UV  →  自动按 mesh+material 合批
      ↓
[PC / Mobile 双出口]
   PC  : SteamPipe 更新，资源走 StreamingAssets
   Mobile: YooAsset 资源交付 + HybridCLR C# 热修
[UI/元层]  MonoBehaviour + UGUI（血条/选卡/暂停/场景管理，非性能敏感）
```

## 3. ECS 模拟设计（骨架）

- **确定性**：全局 `Unity.Mathematics.Random` 由 seed 初始化，所有随机事件走它（roguelike 可复现）。
- **无互相碰撞**：敌人重叠穿过，避免 O(n²)；仅做「敌人–玩家 / 敌人–武器AoE」单次判定。
- **Tick 节流**：AI 决策 0.1~0.2s 一次（Job 内批量），移动每帧插值；空间剔除用均匀网格（SpatialHash）。
- **对象池**：死亡实体不 Destroy，回收组件/置 inactive，避免 GC 尖峰。
- **坐标系**：kapai 的 Cocos y 向下 → 视觉坐标 `(x, mapH - cocosY)`，在 Spawn/转换系统里统一处理一次。

## 4. 渲染桥（实例化 flipbook，PC/移动统一）

1. 把 kapai 的 `CocosAniPlayer` 帧解析逻辑复用，输出「图集纹理 + 每帧 uv 矩形」。
2. 可视实体 = 一个 quad（2 三角面），共享同一图集材质 → Entities Graphics 自动实例化合批。
3. 每实体 `AnimState` 组件（clipId / time / frame）由 `AnimTickSystem` 在 Burst Job 中推进，写入实例属性缓冲。
4. flipbook shader：读取实例 `frame` → 计算图集内 uv → 输出颜色。
5. 收益：**1000+ 实例个位数 draw call**（与 Deep Rock Galactic: Survivor 的 Instancing 思路同源），移动端也能撑住。

> 说明：v1 建议的「SpriteRenderer flipbook」在 ECS 下不可行（ECS 没有 SpriteRenderer 组件），故统一收敛为实例化 quad 方案——它同时满足了 v1 的「GPU 实例化」升级口，只是从「可选」变为「标准」。

## 5. 迁移路线（PC → Mobile）

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M1 | 切 URP；写 `CocosAniInstancedPlayer`（flipbook shader + quad）；ECS 敌人/武器/生成系统骨架 | PC Standalone 跑通基础战斗闭环（500 单位） |
| M2 | Spawn Director（时间曲线出怪）+ 玩家自动攻击 + 升级选卡 | PC 目标 **60fps @ 1000 同屏**（Profiler 实测 draw call/GC/帧耗时） |
| M3 | 切 Android/iOS：IL2CPP + Burst 全开；YooAsset 接管资源；HybridCLR 接 C# 热修 | 同一套 sim 跨平台编译通过 |
| M4 | 中端真机压测定同屏上限；按需 LOD / 实例化裁剪 / Culling | 移动端中端机稳 30~60fps |

## 6. 诚实边界（未验证项）

- DOTS 2D 无原生 SpriteRenderer，flipbook 需自研 shader（有工作量；你有 ECS 经验可承接）。
- Entities Graphics 成熟于 URP，BiRP 不支持 → 必须切 URP，可能影响现有 kapai 美术导入设置。
- YooAsset / HybridCLR 增加构建复杂度，需 `#if` 平台隔离，避免污染 PC 构建。
- 移动端同屏上限、中端机实际帧率未真机实测。
- kapai `.ani` 解析已在 v1 迁移，本方案仅改「播放出口」为实例缓冲，解析逻辑复用。

## 7. 待确认决策门

- **ECS-from-MVP（推荐）** vs **PC-Mono-first 再转 ECS**：
  - 推荐前者——你已有移动端 ECS 经验，且移动端是硬目标，同一套 sim 避免二次重写；PC 仅作更快的验证环境。
  - 代价：ECS 内容迭代需额外执行 Luban 生成，但可保证数据单一来源和跨平台一致性。
- 确认后我开始：① 切 URP；② `CocosAniInstancedPlayer`；③ ECS 敌人/武器系统骨架。
