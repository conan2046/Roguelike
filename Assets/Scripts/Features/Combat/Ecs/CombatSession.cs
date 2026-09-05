using System;
using System.Collections.Generic;
using cfg;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>独立战斗生命周期：统一配置入口供表，原生快照驱动 ECS，场景或性能入口共享此实现。</summary>
    public sealed class CombatSession : IDisposable
    {
        private readonly World world;
        private readonly EntityManager manager;
        private readonly CombatTickSystem system;
        private readonly PerformanceScenarioConfig scenario;
        private readonly CombatRulesConfig rules;
        private readonly DamageRules damageRules;
        private NativeList<Entity> units;
        private NativeArray<Entity> projectiles;
        private NativeList<CombatUnit> templates;
        private NativeArray<CombatCounters> counters;
        private NativeParallelMultiHashMap<int2, int> grid;
        private NativeList<CombatDamageRequest> requests;
        private NativeList<CombatAttackOption> attackOptions;
        private float maximumRadius, maximumMotion;
        private bool disposed;
        private readonly bool timedSpawn;
        private readonly Dictionary<int, int> attackOffsets = new Dictionary<int, int>();
        private readonly Dictionary<int, int> attackCounts = new Dictionary<int, int>();
        private ulong nextSpawnTick;
        public ulong WaveNumber { get; private set; }
        public int SpawnedInWave { get; private set; }
        public long SpawnedMonsters { get; private set; }

        public bool Paused { get; set; }
        public double BacklogSeconds { get; private set; }
        public double PeakBacklogSeconds { get; private set; }
        public double StepSeconds { get; }
        public int UnitCount => units.Length;
        public int ProjectileCapacity => projectiles.Length;
        public CombatCounters Statistics => counters[0];

        /// <summary>配置就绪后验证 TbPerformanceScenario 引用，创建本局 ECS 实体及由表推导容量的池。</summary>
        /// <param name="world">调用者拥有的存活 ECS 世界；会话退出不销毁此世界。</param>
        /// <param name="scenario">IConfigService.Tables 提供的 Combat 测试场景。</param>
        /// <param name="attacksByVisual">资源服务预加载 ANI 后由 CombatAttackOption.Build 生成，键为 TbVisualSet.id；不得传入业务兜底时长。</param>
        /// <remarks>创建并持有实体/原生容器，必须先 Dispose 会话再销毁世界；部分失败会回收已创建资源。</remarks>
        /// <exception cref="InvalidOperationException">配置不完整、范围非法或不是本期支持的单玩家弹丸/近战怪组合。</exception>
        public CombatSession(World world, PerformanceScenarioConfig scenario, IReadOnlyDictionary<int, CombatAttackOption[]> attacksByVisual)
        {
            if (world == null || !world.IsCreated) throw new InvalidOperationException("Combat requires a live ECS World.");
            ValidateScenario(scenario);
            this.world = world;
            manager = world.EntityManager;
            this.scenario = scenario;
            timedSpawn = scenario.SpawnIntervalSeconds.HasValue;
            rules = scenario.CombatRulesId_Ref;
            damageRules = DamageRules.Capture(rules);
            StepSeconds = 1d / rules.SimulationHz;
            system = world.GetOrCreateSystemManaged<CombatTickSystem>();
            try
            {
                int count = checked(scenario.EntityCount + 1);
                templates = new NativeList<CombatUnit>(count, Allocator.Persistent);
                templates.Resize(count, NativeArrayOptions.ClearMemory);
                units = new NativeList<Entity>(count, Allocator.Persistent);
                units.Resize(count, NativeArrayOptions.ClearMemory);
                counters = new NativeArray<CombatCounters>(1, Allocator.Persistent);
                attackOptions = new NativeList<CombatAttackOption>(Allocator.Persistent);
                var offsets = new Dictionary<int, int>();
                foreach (var monster in scenario.MonsterIds_Ref)
                {
                    int id = monster.VisualSetId;
                    if (offsets.ContainsKey(id)) continue;
                    if (attacksByVisual == null || !attacksByVisual.TryGetValue(id, out var options) || options == null || options.Length == 0)
                        throw new InvalidOperationException("Combat requires preloaded attack options for visual " + id);
                    offsets.Add(id, attackOptions.Length);
                    attackOffsets.Add(id, attackOptions.Length);
                    attackCounts.Add(id, options.Length);
                    foreach (var option in options)
                    {
                        CombatMath.NonNegative(option.HitSeconds); CombatMath.Positive(option.DurationSeconds);
                        if (option.HitSeconds >= option.DurationSeconds || !math.all(math.isfinite(new float2(option.X, option.Y))) ||
                            math.lengthsq(new float2(option.X, option.Y)) == 0 || option.ActionIndex < 0 || option.AlignedActionIndex < 0)
                            throw new InvalidOperationException("Invalid preloaded attack option.");
                        attackOptions.Add(option);
                    }
                }
                var player = scenario.CharacterId_Ref;
                templates[0] = BuildUnit(scenario.CharacterProfileOverrideId_Ref, player.DefaultSkillId_Ref,
                    player.BodyRadius.Value, CombatCylinder.FromConfig(player.MoveRadiusPixels, player.MoveHeightPixels, player.MoveOffsetXPixels, player.MoveOffsetYPixels, player.MoveElevationPixels, rules.WorldUnitsPerPixel), new float2(scenario.PlayerStartX.Value, scenario.PlayerStartY.Value), true);
                int rows = (scenario.EntityCount + scenario.SpawnColumns - 1) / scenario.SpawnColumns;
                for (int slot = 1; slot < count; slot++)
                {
                    var monster = scenario.MonsterIds_Ref[(slot - 1) % scenario.MonsterIds_Ref.Count];
                    var position = new float2(((slot - 1) % scenario.SpawnColumns - (scenario.SpawnColumns - 1) * 0.5f) * scenario.HorizontalSpacing,
                        ((slot - 1) / scenario.SpawnColumns - (rows - 1) * 0.5f) * scenario.VerticalSpacing);
                    templates[slot] = BuildUnit(scenario.MonsterProfileOverrideId_Ref, monster.DefaultSkillId_Ref,
                        monster.BodyRadius.Value, CombatCylinder.FromConfig(monster.MoveRadiusPixels, monster.MoveHeightPixels, monster.MoveOffsetXPixels, monster.MoveOffsetYPixels, monster.MoveElevationPixels, rules.WorldUnitsPerPixel), position, false);
                    var template = templates[slot];
                    template.AttackOptionStart = offsets[monster.VisualSetId];
                    template.AttackOptionCount = attacksByVisual[monster.VisualSetId].Length;
                    templates[slot] = template;
                }
                for (int slot = 0; slot < count; slot++)
                {
                    var template = templates[slot];
                    maximumRadius = math.max(maximumRadius, template.Radius);
                    maximumMotion = math.max(maximumMotion, template.MoveSpeed * (float)StepSeconds);
                    units[slot] = manager.CreateEntity(typeof(CombatUnit), typeof(LocalTransform), typeof(LocalToWorld));
                }
                // 唯一弹丸发射者的最短间隔与寿命推导上界；加一覆盖同 tick 出生先于旧弹丸过期。
                double capacity = Math.Ceiling(templates[0].ProjectileLifetime / rules.MinAttackInterval) + 1;
                if (capacity > int.MaxValue - count) throw new InvalidOperationException("TbSkillCombat: projectile capacity overflow.");
                projectiles = new NativeArray<Entity>((int)capacity, Allocator.Persistent);
                for (int index = 0; index < projectiles.Length; index++)
                    projectiles[index] = manager.CreateEntity(typeof(CombatProjectile), typeof(LocalTransform), typeof(LocalToWorld));
                grid = new NativeParallelMultiHashMap<int2, int>(count, Allocator.Persistent);
                requests = new NativeList<CombatDamageRequest>(count + projectiles.Length, Allocator.Persistent);
                Restart();
            }
            catch { Dispose(); throw; }
        }

        /// <summary>渲染帧提交采样输入后推进固定 tick；达到补算上限仍保留积压，不丢失模拟时间。</summary>
        /// <param name="elapsedSeconds">本渲染帧未缩放的有效经过时间。</param>
        /// <param name="movement">输入层提供的二维移动方向，归一化限制斜向速度。</param>
        /// <returns>本帧真正执行的固定 tick 数。</returns>
        /// <remarks>暂停或玩家死亡时冻结全部模拟；内部任务同步完成，返回后可安全读取实体。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="InvalidOperationException">时间或输入非有限。</exception>
        public int Advance(double elapsedSeconds, float2 movement)
        {
            EnsureAlive();
            CombatMath.NonNegative(elapsedSeconds);
            if (!math.all(math.isfinite(movement))) throw new InvalidOperationException("Combat input must be finite.");
            if (Paused || Statistics.PlayerDead) return 0;
            double debt = BacklogSeconds + elapsedSeconds;
            CombatMath.NonNegative(debt);
            BacklogSeconds = debt;
            if (scenario.PlayerInputPolicy == ETestInputPolicy.Stationary) movement = float2.zero;
            else if (math.lengthsq(movement) > 1) movement = math.normalizesafe(movement);
            int steps = 0;
            while (BacklogSeconds >= StepSeconds && steps < rules.MaxCatchUpSteps && !Statistics.PlayerDead)
            {
                system.RunTick(new CombatTickJob
                {
                    Units = units.AsArray(), Projectiles = projectiles, Templates = templates.AsArray(), Counters = counters,
                    Grid = grid, Requests = requests, Rules = damageRules, Input = movement,
                    AttackOptions = attackOptions.AsArray(),
                    Arena = new float2(scenario.ArenaHalfWidth.Value, scenario.ArenaHalfHeight.Value),
                    Delta = (float)StepSeconds, Time = Statistics.Tick * StepSeconds, TargetRefresh = rules.TargetRefreshSeconds,
                    CellSize = rules.SpatialCellSize, MaximumRadius = maximumRadius, MaximumMotion = maximumMotion,
                    Seed = unchecked((ulong)(uint)scenario.RandomSeed), Replenish = scenario.ReplenishOnDeath.Value,
                    AllowMonstersOutsideArena = timedSpawn,
                    RestorePlayer = scenario.PlayerHealthPolicy == ETestHealthPolicy.RestoreAfterDamage
                });
                // Job 已完成后才允许结构变更；每只怪物取此刻的角色圆心，下一 tick 开始追踪。
                if (timedSpawn && !Statistics.PlayerDead)
                    while (Statistics.Tick >= nextSpawnTick)
                    {
                        if (WaveNumber == 0 || SpawnedInWave == scenario.SpawnBatchCount.Value)
                        { WaveNumber = checked(WaveNumber + 1); SpawnedInWave = 0; }
                        if (!SpawnOne()) { nextSpawnTick = checked(Statistics.Tick + 1); break; }
                        SpawnedInWave++;
                        nextSpawnTick = checked(nextSpawnTick + SpawnDelayTicks(SpawnedInWave == scenario.SpawnBatchCount.Value
                            ? scenario.SpawnIntervalSeconds.Value : scenario.SpawnUnitIntervalSeconds.Value));
                    }
                BacklogSeconds -= StepSeconds;
                steps++;
            }
            PeakBacklogSeconds = Math.Max(PeakBacklogSeconds, BacklogSeconds);
            return steps;
        }

        /// <summary>重新开始时清空弹丸、请求、目标、冷却、统计及刷怪计时；定时模式清空怪物，旧阵列模式恢复出生数量。</summary>
        /// <remarks>复用已增长的实体与原生容器，不重复加载美术；生命周期继续递增以隔离旧事件，出生角度序列重置，解除暂停并清空积压。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public void Restart()
        {
            EnsureAlive();
            ulong nextLifetime = counters[0].NextLifetime;
            for (int slot = 0; slot < units.Length; slot++)
            {
                var unit = templates[slot];
                if (timedSpawn && slot != 0) unit.Target.Health = 0;
                unit.Target.Lifetime = ++nextLifetime;
                unit.Attack.AttackerLifetime = nextLifetime;
                manager.SetComponentData(units[slot], unit);
                manager.SetComponentData(units[slot], LocalTransform.FromPositionRotationScale(new float3(unit.Position, 0), quaternion.identity, unit.Target.Health > 0 ? 1 : 0));
            }
            for (int index = 0; index < projectiles.Length; index++)
            {
                manager.SetComponentData(projectiles[index], new CombatProjectile());
                manager.SetComponentData(projectiles[index], LocalTransform.FromScale(0));
            }
            counters[0] = new CombatCounters { NextLifetime = nextLifetime, AliveMonsters = timedSpawn ? 0 : scenario.EntityCount };
            nextSpawnTick = timedSpawn ? SpawnDelayTicks(scenario.SpawnIntervalSeconds.Value) : 0;
            WaveNumber = 0; SpawnedInWave = 0; SpawnedMonsters = 0;
            grid.Clear(); requests.Clear();
            Paused = false;
            BacklogSeconds = PeakBacklogSeconds = 0;
        }

        /// <summary>固定 tick 结束后按 TbPerformanceScenario 的波内时序，在角色当前位置的圆周生成一只。</summary>
        /// <remarks>半径经 TbCombatRules.worldUnitsPerPixel 换算；独立 SpawnAngle 随机域不消费伤害随机流。复用死亡槽且更新代际；无玩法数量上限，容量不足时在无运行 Job 的主线程扩容。</remarks>
        /// <exception cref="OverflowException">实体计数或生成序号超出机器整数表示范围。</exception>
        /// <returns>成功生成时为真；圆周全部被占时为假，调度器下一 tick 重试，不丢失波内数量。</returns>
        private bool SpawnOne()
        {
            const int batch = 1; // 单次调度恰好一个出生事件；每波数量仍从表读取。
            int free = units.Length - 1 - Statistics.AliveMonsters;
            if (free < batch) GrowUnits(checked(units.Length + batch - free));
            float radius = scenario.SpawnRadiusPixels.Value * rules.WorldUnitsPerPixel;
            float2 center = ReadUnit(0).Position;
            var stats = counters[0];
            for (int slot = 1, remaining = batch; remaining > 0 && slot < units.Length; slot++)
            {
                if (ReadUnit(slot).Target.Health > 0) continue;
                var unit = templates[slot];
                // int.MaxValue 是抽样精度技术边界；2π 是整圆换算，不承载策划参数。
                int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)scenario.RandomSeed), checked((ulong)SpawnedMonsters), 0, CombatRandomPurpose.SpawnAngle, 0, int.MaxValue);
                double angle = sample / ((double)int.MaxValue + 1) * (2 * Math.PI);
                if (!FindSpawnPosition(unit.Movement, center, radius, angle, out var position)) return false;
                unit.Position = position;
                unit.PreviousPosition = unit.SpawnPosition = unit.Position;
                unit.Facing = math.normalizesafe(center - unit.Position);
                unit.Target.Lifetime = checked(++stats.NextLifetime);
                unit.Attack.AttackerLifetime = unit.Target.Lifetime;
                manager.SetComponentData(units[slot], unit);
                manager.SetComponentData(units[slot], LocalTransform.FromPosition(new float3(unit.Position, 0)));
                SpawnedMonsters = checked(SpawnedMonsters + 1); stats.AliveMonsters++; remaining--;
            }
            counters[0] = stats;
            return true;
        }

        /// <summary>圆周生成遇到占位时沿圆周越过遮挡弧，保留出生半径；整圈被占时延迟当前出生事件。</summary>
        /// <param name="shape">待生成圆柱。</param><param name="center">当前玩家位置。</param>
        /// <param name="radius">配置出生半径。</param><param name="angle">独立随机流给出的起始角。</param>
        /// <param name="position">可用的单位脚底位置。</param><returns>是否找到空闲圆周位置。</returns>
        private bool FindSpawnPosition(CombatCylinder shape, float2 center, float radius, double angle, out float2 position)
        {
            double start = angle;
            position = default;
            // 每次前进至一个阻挡弧的末端；超过一圈或全部弧数量即确定无空位。
            for (int attempt = 0; attempt <= units.Length && angle - start < 2 * Math.PI; attempt++)
            {
                position = center + radius * new float2((float)Math.Cos(angle), (float)Math.Sin(angle));
                bool blocked = false;
                for (int slot = 0; slot < units.Length; slot++)
                {
                    var other = ReadUnit(slot);
                    if (other.Target.Health <= 0 || !shape.HeightOverlaps(other.Movement)) continue;
                    float sum = shape.Radius + other.Movement.Radius;
                    float2 offset = other.Position + other.Movement.Offset - shape.Offset - center;
                    if (math.lengthsq(position + shape.Offset - other.Position - other.Movement.Offset) >= sum * sum) continue;
                    double distance = math.length(offset);
                    if (distance == 0 || sum >= radius + distance) return false;
                    double half = Math.Acos(Math.Max(-1, Math.Min(1, (radius * radius + distance * distance - sum * sum) / (2 * radius * distance))));
                    double end = Math.Atan2(offset.y, offset.x) + half;
                    while (end < angle) end += 2 * Math.PI;
                    // 单精度角度前进一步，避免圆周投影舍入再次落入刚越过的接触弧。
                    float positive = (float)(end + 2 * Math.PI);
                    angle = math.asfloat(math.asint(positive) + 1) - 2 * Math.PI;
                    blocked = true; break;
                }
                if (!blocked) return true;
            }
            return false;
        }

        /// <summary>将表内秒数向上量化为模拟 tick；先还原 float 的十进制精度，避免 0.2 秒被浮点误差抬至多一帧。</summary>
        /// <param name="seconds">TbPerformanceScenario 的波间或波内生成间隔。</param>
        /// <returns>不少于一个 tick 的等待长度。</returns>
        /// <exception cref="OverflowException">配置等待时间超出整数时间轴可表示范围。</exception>
        private ulong SpawnDelayTicks(float seconds) => checked((ulong)Math.Ceiling((decimal)seconds * rules.SimulationHz));

        /// <summary>刷怪批次缺少空闲槽时扩展单位、模板和空间索引容量，弹丸仍按唯一发射者寿命上界独立持有。</summary>
        /// <param name="count">本批存活数加所需空槽的总量，包含玩家槽。</param>
        /// <remarks>只在同步 Job 完成后创建 ECS 实体；怪物类型按 TbPerformanceScenario.monsterIds 槽位轮转，与渲染绑定保持一致。新增实体由本会话退出时释放。</remarks>
        private void GrowUnits(int count)
        {
            units.Capacity = Math.Max(units.Capacity, count);
            templates.Capacity = Math.Max(templates.Capacity, count);
            grid.Capacity = Math.Max(grid.Capacity, count);
            requests.Capacity = Math.Max(requests.Capacity, checked(count + projectiles.Length));
            while (units.Length < count)
            {
                var monster = scenario.MonsterIds_Ref[(units.Length - 1) % scenario.MonsterIds_Ref.Count];
                var template = BuildUnit(scenario.MonsterProfileOverrideId_Ref, monster.DefaultSkillId_Ref,
                    monster.BodyRadius.Value, CombatCylinder.FromConfig(monster.MoveRadiusPixels, monster.MoveHeightPixels, monster.MoveOffsetXPixels, monster.MoveOffsetYPixels, monster.MoveElevationPixels, rules.WorldUnitsPerPixel), float2.zero, false);
                template.AttackOptionStart = attackOffsets[monster.VisualSetId];
                template.AttackOptionCount = attackCounts[monster.VisualSetId];
                var entity = manager.CreateEntity(typeof(CombatUnit), typeof(LocalTransform), typeof(LocalToWorld));
                units.Add(entity); templates.Add(template);
                var inactive = template; inactive.Target.Health = 0;
                manager.SetComponentData(entity, inactive);
                manager.SetComponentData(entity, LocalTransform.FromScale(0));
                maximumRadius = math.max(maximumRadius, template.Radius);
                maximumMotion = math.max(maximumMotion, template.MoveSpeed * (float)StepSeconds);
            }
        }

        /// <summary>表现与诊断在 tick 完成后读取单位组件副本，不暴露原生容器所有权。</summary>
        /// <param name="slot">玩家为首槽，其余为怪物槽。</param>
        /// <returns>当前单位状态。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatUnit ReadUnit(int slot) { EnsureAlive(); return manager.GetComponentData<CombatUnit>(units[slot]); }

        /// <summary>表现层为会话自有单位实体附加渲染组件时获取实体标识。</summary>
        /// <param name="slot">单位池槽。</param>
        /// <returns>会话拥有的 ECS 实体。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public Entity UnitEntity(int slot) { EnsureAlive(); return units[slot]; }

        /// <summary>表现层为弹丸池实体附加渲染组件时获取实体标识。</summary>
        /// <param name="slot">弹丸池槽。</param>
        /// <returns>会话拥有的 ECS 实体。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public Entity ProjectileEntity(int slot) { EnsureAlive(); return projectiles[slot]; }

        /// <summary>属性来源改变后同步防守、后续攻击与移动数据，保留冷却比例和当前攻击原速进度；已起手攻击快照不变。</summary>
        /// <param name="slot">当前生命周期的单位槽。</param>
        /// <param name="expectedLifetime">调用方保存的生命周期，防止延迟修改污染复用单位。</param>
        /// <param name="attributes">通过统一属性入口聚合的配置结果。</param>
        /// <returns>该生命周期仍然存活且已同步时为真。</returns>
        /// <remarks>修改当前 ECS 单位，不修改出生模板；重开或怪物复用时清空局内修改。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="InvalidOperationException">新属性间隔或生命上限非法。</exception>
        public bool SynchronizeAttributes(int slot, ulong expectedLifetime, CombatAttributes attributes)
        {
            EnsureAlive();
            var unit = ReadUnit(slot);
            if (unit.Target.Lifetime != expectedLifetime || unit.Target.Health <= 0) return false;
            var skill = slot == 0 ? scenario.CharacterId_Ref.DefaultSkillId_Ref.CombatProfileId_Ref :
                scenario.MonsterIds_Ref[(slot - 1) % scenario.MonsterIds_Ref.Count].DefaultSkillId_Ref.CombatProfileId_Ref;
            double interval = CombatMath.AttackInterval(skill.BaseInterval, attributes.Get(EAttributeType.AttackSpeedMultiplier), rules.MinAttackInterval);
            unit.Cooldown = CombatMath.RescaleCooldown(unit.Cooldown, unit.Interval, interval);
            unit.Interval = interval;
            unit.Target.Synchronize(attributes);
            unit.Attack = AttackSnapshot.Capture(attributes, unit.Target.Lifetime, 0, unit.Target.Faction);
            unit.MoveSpeed = (float)attributes.Get(EAttributeType.MoveSpeed);
            maximumMotion = math.max(maximumMotion, unit.MoveSpeed * (float)StepSeconds);
            manager.SetComponentData(units[slot], unit);
            return true;
        }

        /// <summary>入局时将 TbAttributeProfile、TbSkillCombat 与身体半径转为非托管模板。</summary>
        /// <param name="profile">完整属性方案。</param>
        /// <param name="skillConfig">TbSkill 技能，碰撞半径与偏移独立于其 TbSkillCombat 引用。</param>
        /// <param name="radius">TbCharacter/TbMonster.bodyRadius。</param>
        /// <param name="movement">TbCharacter/TbMonster 导出的独立移动圆柱。</param>
        /// <param name="position">TbPerformanceScenario 生成位置。</param>
        /// <param name="player">区分玩家弹丸与怪物近战角色。</param>
        /// <returns>冷却就绪且目标为空的出生模板。</returns>
        /// <exception cref="InvalidOperationException">属性、几何或技能配置不合法。</exception>
        private CombatUnit BuildUnit(AttributeProfileConfig profile, SkillConfig skillConfig, float radius, CombatCylinder movement, float2 position, bool player)
        {
            var skill = skillConfig.CombatProfileId_Ref ?? throw new InvalidOperationException("Missing skill combat profile.");
            var attributes = new CombatAttributes(profile);
            CombatMath.Positive(radius);
            if (!math.all(math.isfinite(position)) || math.abs(position.x) + radius > scenario.ArenaHalfWidth.Value ||
                math.abs(position.y) + radius > scenario.ArenaHalfHeight.Value)
                throw new InvalidOperationException($"TbPerformanceScenario {scenario.Id}: unit outside arena.");
            CombatMath.NonNegative(skill.Range);
            if (skill.DeliveryType != (player ? ESkillDeliveryType.Projectile : ESkillDeliveryType.Melee))
                throw new InvalidOperationException($"TbSkillCombat {skill.Id}: unsupported actor delivery role.");
            if (player)
            {
                if (!skill.ProjectileSpeed.HasValue || !skillConfig.ProjectileRadius.HasValue || !skill.ProjectileLifetime.HasValue || !skillConfig.ProjectileOffsetX.HasValue || !skillConfig.ProjectileOffsetY.HasValue)
                    throw new InvalidOperationException($"TbSkillCombat {skill.Id}: missing projectile fields.");
                CombatMath.Positive(skill.ProjectileSpeed.Value); CombatMath.Positive(skillConfig.ProjectileRadius.Value); CombatMath.Positive(skill.ProjectileLifetime.Value);
            }
            else
            {
                if (!skill.AttackWindupSeconds.HasValue)
                    throw new InvalidOperationException($"TbSkillCombat {skill.Id}: missing attackWindupSeconds.");
                CombatMath.NonNegative(skill.AttackWindupSeconds.Value);
            }
            int faction = (int)(player ? CombatFaction.Player : CombatFaction.Monster);
            return new CombatUnit { Target = DamageTarget.Spawn(attributes, 0, faction, player),
                Attack = AttackSnapshot.Capture(attributes, 0, 0, faction), Position = position, PreviousPosition = position, SpawnPosition = position,
                Radius = radius, Movement = movement, MoveSpeed = (float)attributes.Get(EAttributeType.MoveSpeed), Range = skill.Range,
                Interval = CombatMath.AttackInterval(skill.BaseInterval, attributes.Get(EAttributeType.AttackSpeedMultiplier), rules.MinAttackInterval),
                Delivery = skill.DeliveryType, TargetSlot = -1,
                BaseInterval = skill.BaseInterval,
                WindupSeconds = player ? 0 : skill.AttackWindupSeconds.Value,
                Facing = new float2(scenario.PresentationId_Ref.InitialDirectionId_Ref.X, scenario.PresentationId_Ref.InitialDirectionId_Ref.Y),
                // 近战不消费弹丸字段；零仅为空布局，不作为弹丸参数兜底。
                SkillId = skillConfig.Id,
                ProjectileOffset = player ? new float2(skillConfig.ProjectileOffsetX.Value, skillConfig.ProjectileOffsetY.Value) : float2.zero,
                ProjectileSpeed = player ? skill.ProjectileSpeed.Value : 0,
                ProjectileLifetime = player ? skill.ProjectileLifetime.Value : 0, ProjectileRadius = player ? skillConfig.ProjectileRadius.Value : 0 };
        }

        /// <summary>实体创建前验证必须的场景引用与固定步进参数，旧移动场景不能误入战斗。</summary>
        /// <param name="value">统一配置入口提供的场景。</param>
        /// <exception cref="InvalidOperationException">任何必需引用或数值非法。</exception>
        private static void ValidateScenario(PerformanceScenarioConfig value)
        {
            if (value == null || value.Kind != EPerformanceKind.Combat || value.CombatRulesId_Ref == null || value.PresentationId_Ref?.InitialDirectionId_Ref == null ||
                value.CharacterId_Ref?.DefaultSkillId_Ref?.CombatProfileId_Ref == null || !value.CharacterId_Ref.BodyRadius.HasValue ||
                value.CharacterProfileOverrideId_Ref == null || value.MonsterProfileOverrideId_Ref == null ||
                value.MonsterIds_Ref == null || value.MonsterIds_Ref.Count == 0 || value.EntityCount <= 0 || value.SpawnColumns <= 0 ||
                !value.ArenaHalfWidth.HasValue || !value.ArenaHalfHeight.HasValue || !value.PlayerStartX.HasValue || !value.PlayerStartY.HasValue ||
                !value.ReplenishOnDeath.HasValue || !value.PlayerHealthPolicy.HasValue || !value.PlayerInputPolicy.HasValue)
                throw new InvalidOperationException("TbPerformanceScenario: incomplete combat configuration.");
            CombatMath.Positive(value.ArenaHalfWidth.Value); CombatMath.Positive(value.ArenaHalfHeight.Value);
            CombatMath.NonNegative(value.HorizontalSpacing); CombatMath.NonNegative(value.VerticalSpacing);
            bool anySpawn = value.SpawnRadiusPixels.HasValue || value.SpawnIntervalSeconds.HasValue || value.SpawnBatchCount.HasValue || value.SpawnUnitIntervalSeconds.HasValue;
            if (anySpawn)
            {
                if (!value.SpawnRadiusPixels.HasValue || !value.SpawnIntervalSeconds.HasValue || !value.SpawnBatchCount.HasValue || !value.SpawnUnitIntervalSeconds.HasValue || value.SpawnBatchCount.Value <= 0 || value.ReplenishOnDeath.Value)
                    throw new InvalidOperationException("Timed spawn requires complete parameters and cannot replenish on death.");
                CombatMath.Positive(value.SpawnRadiusPixels.Value); CombatMath.Positive(value.SpawnIntervalSeconds.Value);
                CombatMath.Positive(value.SpawnUnitIntervalSeconds.Value);
                CombatMath.Positive(value.CombatRulesId_Ref.WorldUnitsPerPixel);
                CombatMath.Positive(value.SpawnRadiusPixels.Value * value.CombatRulesId_Ref.WorldUnitsPerPixel);
            }
            if (!Enum.IsDefined(typeof(ETestHealthPolicy), value.PlayerHealthPolicy.Value) || !Enum.IsDefined(typeof(ETestInputPolicy), value.PlayerInputPolicy.Value))
                throw new InvalidOperationException($"TbPerformanceScenario {value.Id}: unknown policy.");
            foreach (var monster in value.MonsterIds_Ref)
                if (monster?.DefaultSkillId_Ref?.CombatProfileId_Ref == null || !monster.BodyRadius.HasValue)
                    throw new InvalidOperationException($"TbPerformanceScenario {value.Id}: monster is not combat-ready.");
            var rules = value.CombatRulesId_Ref;
            if (rules.SimulationHz <= 0 || rules.MaxCatchUpSteps <= 0) throw new InvalidOperationException($"TbCombatRules {rules.Id}: invalid tick limits.");
            CombatMath.Positive(rules.MinAttackInterval); CombatMath.Positive(rules.SpatialCellSize); CombatMath.Positive(rules.TargetRefreshSeconds);
        }

        /// <summary>所有公开变更入口先验证生命周期，避免访问已释放原生内存。</summary>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        private void EnsureAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CombatSession));
        }

        /// <summary>退出或初始化失败时只释放本局创建的实体和原生容器，可重复调用。</summary>
        /// <remarks>不销毁调用者 World、其他单位、资源或场景对象；每个 tick 已同步完成。</remarks>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (world != null && world.IsCreated)
            {
                if (units.IsCreated) foreach (var entity in units) if (manager.Exists(entity)) manager.DestroyEntity(entity);
                if (projectiles.IsCreated) foreach (var entity in projectiles) if (manager.Exists(entity)) manager.DestroyEntity(entity);
            }
            if (units.IsCreated) units.Dispose();
            if (projectiles.IsCreated) projectiles.Dispose();
            if (templates.IsCreated) templates.Dispose();
            if (counters.IsCreated) counters.Dispose();
            if (grid.IsCreated) grid.Dispose();
            if (requests.IsCreated) requests.Dispose();
            if (attackOptions.IsCreated) attackOptions.Dispose();
        }
    }
}
