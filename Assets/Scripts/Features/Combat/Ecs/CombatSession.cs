using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Run;
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
        private readonly CombatSessionSettings settings;
        private readonly CombatRulesConfig rules;
        private readonly DamageRules damageRules;
        private NativeList<Entity> units;
        private NativeArray<Entity> projectiles;
        private NativeList<CombatUnit> templates;
        private NativeArray<CombatCounters> counters;
        private NativeParallelMultiHashMap<int2, int> grid;
        private NativeList<CombatDamageRequest> requests;
        private NativeList<CombatImpactEvent> impacts;
        private NativeList<CombatAreaEvent> areas;
        private NativeList<CombatDeathEvent> deaths;
        private NativeList<CombatAttackOption> attackOptions;
        private NativeArray<CombatWeaponState> playerWeapons;
        private NativeArray<CombatWeaponState> playerWeaponTemplates;
        private float maximumRadius, maximumMotion;
        private int requestCapacityMultiplier;
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
        public int PlayerWeaponCount => playerWeapons.IsCreated ? playerWeapons.Length : 0;
        public CombatCounters Statistics => counters[0];

        /// <summary>配置就绪后验证 TbPerformanceScenario 引用，创建本局 ECS 实体及由表推导容量的池。</summary>
        /// <param name="world">调用者拥有的存活 ECS 世界；会话退出不销毁此世界。</param>
        /// <param name="scenario">IConfigService.Tables 提供的 Combat 测试场景。</param>
        /// <param name="attacksByVisual">资源服务预加载 ANI 后由 CombatAttackOption.Build 生成，键为 TbVisualSet.id；不得传入业务兜底时长。</param>
        /// <remarks>创建并持有实体/原生容器，必须先 Dispose 会话再销毁世界；部分失败会回收已创建资源。</remarks>
        /// <exception cref="InvalidOperationException">配置不完整、范围非法或不是本期支持的单玩家弹丸/近战怪组合。</exception>
        public CombatSession(World world, PerformanceScenarioConfig scenario, IReadOnlyDictionary<int, CombatAttackOption[]> attacksByVisual)
            : this(world, CombatSessionSettings.FromScenario(scenario), attacksByVisual)
        {
        }

        /// <summary>从正式 TbStage 聚合定义创建动态生成会话，不读取 TbPerformanceScenario。</summary>
        /// <param name="world">调用者拥有的存活 ECS 世界。</param>
        /// <param name="definition">已完成跨表验证的正式单局定义。</param>
        /// <param name="attacksByVisual">全部普通怪与 Boss 的预加载攻击时序。</param>
        /// <remarks>初始只创建玩家；怪物由 CombatRunModel 的生成请求进入 SpawnMonster。</remarks>
        /// <exception cref="InvalidOperationException">世界或预加载攻击数据不可用。</exception>
        public CombatSession(World world, CombatRunDefinition definition, IReadOnlyDictionary<int, CombatAttackOption[]> attacksByVisual)
            : this(world, CombatSessionSettings.FromRun(definition), attacksByVisual)
        {
        }

        /// <summary>使用统一只读设置创建 ECS 容器并保留正式、测试各自的生成策略。</summary>
        /// <param name="world">调用者拥有的存活 ECS 世界。</param>
        /// <param name="settings">从正式关卡或测试场景转换的设置。</param>
        /// <param name="attacksByVisual">怪物表现对应的真实 ANI 攻击时序。</param>
        /// <remarks>创建并持有实体和原生容器；调用方必须先释放会话再销毁世界。</remarks>
        /// <exception cref="InvalidOperationException">世界或攻击资源不完整。</exception>
        private CombatSession(World world, CombatSessionSettings settings, IReadOnlyDictionary<int, CombatAttackOption[]> attacksByVisual)
        {
            if (world == null || !world.IsCreated) throw new InvalidOperationException("Combat requires a live ECS World.");
            if (settings == null) throw new InvalidOperationException("Combat session settings are required.");
            this.world = world;
            manager = world.EntityManager;
            this.settings = settings;
            requestCapacityMultiplier = checked(1 + settings.AvailablePlayerSkills.Count(item =>
                item.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.TargetArea));
            timedSpawn = settings.UseLegacyTimedSpawn;
            rules = settings.Rules;
            damageRules = DamageRules.Capture(rules);
            StepSeconds = 1d / rules.SimulationHz;
            system = world.GetOrCreateSystemManaged<CombatTickSystem>();
            try
            {
                int count = checked(settings.InitialMonsterCount + 1);
                templates = new NativeList<CombatUnit>(count, Allocator.Persistent);
                templates.Resize(count, NativeArrayOptions.ClearMemory);
                units = new NativeList<Entity>(count, Allocator.Persistent);
                units.Resize(count, NativeArrayOptions.ClearMemory);
                counters = new NativeArray<CombatCounters>(1, Allocator.Persistent);
                attackOptions = new NativeList<CombatAttackOption>(Allocator.Persistent);
                var offsets = new Dictionary<int, int>();
                foreach (var monster in settings.Monsters)
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
                var player = settings.Character;
                templates[0] = BuildUnit(settings.CharacterProfile, settings.PlayerSkill,
                    player.BodyRadiusPixels.Value * rules.WorldUnitsPerPixel,
                    new float2(player.BodyOffsetXPixels.Value, player.BodyOffsetYPixels.Value) * rules.WorldUnitsPerPixel,
                    BuildHitEffectOffset(player.HitEffectOffsetXPixels, player.HitEffectOffsetYPixels,
                        player.BodyOffsetXPixels.Value, player.BodyOffsetYPixels.Value),
                    BuildMovement(player.MoveRadiusPixels, player.MoveHeightPixels, player.MoveOffsetXPixels,
                        player.MoveOffsetYPixels, player.MoveElevationPixels, player.CollisionShape), settings.PlayerStart, true);
                var playerTemplate = templates[0];
                playerTemplate.ConfigId = player.Id;
                playerTemplate.VisualSetId = player.VisualSetId;
                templates[0] = playerTemplate;
                playerWeapons = new NativeArray<CombatWeaponState>(settings.IsFormalRun ? settings.AvailablePlayerSkills.Count : 0,
                    Allocator.Persistent);
                playerWeaponTemplates = new NativeArray<CombatWeaponState>(playerWeapons.Length, Allocator.Persistent);
                for (int index = 0; index < playerWeapons.Length; index++)
                {
                    SkillConfig skill = settings.AvailablePlayerSkills[index];
                    CombatWeaponState weapon = BuildWeapon(skill, playerTemplate.Attack,
                        settings.InitialPlayerSkillIds.Contains(skill.Id));
                    playerWeapons[index] = weapon;
                    playerWeaponTemplates[index] = weapon;
                }
                int rows = (settings.InitialMonsterCount + settings.SpawnColumns - 1) / settings.SpawnColumns;
                for (int slot = 1; slot < count; slot++)
                {
                    var monster = settings.Monsters[(slot - 1) % settings.Monsters.Count];
                    var position = new float2(((slot - 1) % settings.SpawnColumns - (settings.SpawnColumns - 1) * 0.5f) * settings.HorizontalSpacing,
                        ((slot - 1) / settings.SpawnColumns - (rows - 1) * 0.5f) * settings.VerticalSpacing);
                    templates[slot] = BuildUnit(settings.GetMonsterProfile(monster), monster.DefaultSkillId_Ref,
                        monster.BodyRadiusPixels.Value * rules.WorldUnitsPerPixel,
                        new float2(monster.BodyOffsetXPixels.Value, monster.BodyOffsetYPixels.Value) * rules.WorldUnitsPerPixel,
                        BuildHitEffectOffset(monster.HitEffectOffsetXPixels, monster.HitEffectOffsetYPixels,
                            monster.BodyOffsetXPixels.Value, monster.BodyOffsetYPixels.Value),
                        BuildMovement(monster.MoveRadiusPixels, monster.MoveHeightPixels, monster.MoveOffsetXPixels,
                            monster.MoveOffsetYPixels, monster.MoveElevationPixels, monster.CollisionShape), position, false);
                    var template = templates[slot];
                    template.ConfigId = monster.Id;
                    template.VisualSetId = monster.VisualSetId;
                    template.AttackOptionStart = offsets[monster.VisualSetId];
                    template.AttackOptionCount = attacksByVisual[monster.VisualSetId].Length;
                    templates[slot] = template;
                }
                for (int slot = 0; slot < count; slot++)
                {
                    var template = templates[slot];
                    maximumRadius = math.max(maximumRadius,
                        template.Radius + template.BodyHalfSegment + math.length(template.BodyOffset));
                    maximumMotion = math.max(maximumMotion, template.MoveSpeed * (float)StepSeconds);
                    units[slot] = manager.CreateEntity(typeof(CombatUnit), typeof(LocalTransform), typeof(LocalToWorld));
                }
                // 每个可解锁弹丸按最短攻击间隔推导并发上界；加一覆盖同 tick 出生先于旧弹丸过期。
                long projectileCapacity = settings.IsFormalRun ? 0 :
                    checked((long)Math.Ceiling(templates[0].ProjectileLifetime / rules.MinAttackInterval) + 1);
                if (settings.IsFormalRun)
                    foreach (SkillConfig skill in settings.AvailablePlayerSkills)
                        if (skill.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile)
                            projectileCapacity = checked(projectileCapacity +
                                (long)Math.Ceiling(skill.CombatProfileId_Ref.ProjectileLifetime.Value /
                                    rules.MinAttackInterval) + 1);
                if (projectileCapacity > int.MaxValue - count)
                    throw new InvalidOperationException("TbSkillCombat: projectile capacity overflow.");
                projectiles = new NativeArray<Entity>((int)projectileCapacity, Allocator.Persistent);
                for (int index = 0; index < projectiles.Length; index++)
                    projectiles[index] = manager.CreateEntity(typeof(CombatProjectile), typeof(LocalTransform), typeof(LocalToWorld));
                grid = new NativeParallelMultiHashMap<int2, int>(count, Allocator.Persistent);
                requests = new NativeList<CombatDamageRequest>(checked(count * requestCapacityMultiplier + projectiles.Length),
                    Allocator.Persistent);
                impacts = new NativeList<CombatImpactEvent>(Allocator.Persistent);
                areas = new NativeList<CombatAreaEvent>(Allocator.Persistent);
                deaths = new NativeList<CombatDeathEvent>(Allocator.Persistent);
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
            impacts.Clear();
            areas.Clear();
            deaths.Clear();
            if (Paused || Statistics.PlayerDead) return 0;
            double debt = BacklogSeconds + elapsedSeconds;
            CombatMath.NonNegative(debt);
            BacklogSeconds = debt;
            if (settings.StationaryInput) movement = float2.zero;
            else if (math.lengthsq(movement) > 1) movement = math.normalizesafe(movement);
            int steps = 0;
            while (BacklogSeconds >= StepSeconds && steps < rules.MaxCatchUpSteps && !Statistics.PlayerDead)
            {
                system.RunTick(new CombatTickJob
                {
                    Units = units.AsArray(), Projectiles = projectiles, Templates = templates.AsArray(), Counters = counters,
                    Grid = grid, Requests = requests, Rules = damageRules, Input = movement,
                    Impacts = impacts,
                    Areas = areas,
                    Deaths = deaths,
                    PlayerWeapons = playerWeapons,
                    AttackOptions = attackOptions.AsArray(),
                    Arena = settings.Arena,
                    Delta = (float)StepSeconds, Time = Statistics.Tick * StepSeconds, TargetRefresh = rules.TargetRefreshSeconds,
                    CellSize = rules.SpatialCellSize, MaximumRadius = maximumRadius, MaximumMotion = maximumMotion,
                    Seed = unchecked((ulong)(uint)settings.RandomSeed), Replenish = settings.ReplenishOnDeath,
                    AllowMonstersOutsideArena = timedSpawn,
                    RestorePlayer = settings.RestorePlayerAfterDamage,
                    UsePlayerWeapons = settings.IsFormalRun
                });
                // Job 已完成后才允许结构变更；每只怪物取此刻的角色圆心，下一 tick 开始追踪。
                if (timedSpawn && !Statistics.PlayerDead)
                    while (Statistics.Tick >= nextSpawnTick)
                    {
                        if (WaveNumber == 0 || SpawnedInWave == settings.SpawnBatchCount)
                        { WaveNumber = checked(WaveNumber + 1); SpawnedInWave = 0; }
                        if (!SpawnOne()) { nextSpawnTick = checked(Statistics.Tick + 1); break; }
                        SpawnedInWave++;
                        nextSpawnTick = checked(nextSpawnTick + SpawnDelayTicks(SpawnedInWave == settings.SpawnBatchCount
                            ? settings.SpawnIntervalSeconds : settings.SpawnUnitIntervalSeconds));
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
                if ((timedSpawn || settings.IsFormalRun) && slot != 0) unit.Target.Health = 0;
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
            for (int index = 0; index < playerWeapons.Length; index++)
            {
                CombatWeaponState weapon = playerWeaponTemplates[index];
                weapon.Attack.AttackerLifetime = ReadUnit(0).Target.Lifetime;
                playerWeapons[index] = weapon;
            }
            counters[0] = new CombatCounters { NextLifetime = nextLifetime, AliveMonsters = timedSpawn || settings.IsFormalRun ? 0 : settings.InitialMonsterCount };
            nextSpawnTick = timedSpawn ? SpawnDelayTicks(settings.SpawnIntervalSeconds) : 0;
            WaveNumber = 0; SpawnedInWave = 0; SpawnedMonsters = 0;
            grid.Clear(); requests.Clear(); impacts.Clear(); areas.Clear();
            deaths.Clear();
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
            float radius = settings.SpawnRadiusPixels * rules.WorldUnitsPerPixel;
            float2 center = ReadUnit(0).Position;
            var stats = counters[0];
            for (int slot = 1, remaining = batch; remaining > 0 && slot < units.Length; slot++)
            {
                if (ReadUnit(slot).Target.Health > 0) continue;
                var unit = templates[slot];
                // int.MaxValue 是抽样精度技术边界；2π 是整圆换算，不承载策划参数。
                int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)settings.RandomSeed), checked((ulong)SpawnedMonsters), 0, CombatRandomPurpose.SpawnAngle, 0, int.MaxValue);
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

        /// <summary>消费正式单局的一条怪物生成请求，在玩家当前位置的配置圆周创建普通怪或唯一 Boss。</summary>
        /// <param name="monster">请求携带的 TbMonster 引用。</param>
        /// <param name="spawnRadiusPixels">TbMap 或 TbBossEncounter 配置的逻辑像素半径。</param>
        /// <param name="spawnSequence">CombatRunModel 在本代次内分配的稳定生成序号。</param>
        /// <param name="isBoss">请求是否来自 TbBossEncounter。</param>
        /// <param name="slot">成功时返回实体池槽；圆周暂时没有空位时为无效槽。</param>
        /// <returns>成功生成时为真；圆周被占时返回假，由运行器保留原请求后续重试。</returns>
        /// <remarks>只允许正式会话调用；复用相同怪物类型的死亡槽，生命周期递增以拒绝旧伤害和 UI 事件。</remarks>
        /// <exception cref="InvalidOperationException">测试会话调用、怪物不属于本关、Boss 重复存活或配置几何非法。</exception>
        public bool TrySpawnMonster(MonsterConfig monster, float spawnRadiusPixels, ulong spawnSequence, bool isBoss, out int slot)
        {
            EnsureAlive();
            if (!settings.IsFormalRun) throw new InvalidOperationException("Dynamic run spawning requires a formal combat session.");
            if (monster == null) throw new InvalidOperationException("Combat spawn request is missing TbMonster.");
            monster = settings.GetMonster(monster.Id);
            CombatMath.Positive(spawnRadiusPixels);
            if (spawnSequence == 0) throw new InvalidOperationException("Combat spawn sequence must be positive.");
            if (isBoss)
            {
                for (int index = 1; index < units.Length; index++)
                {
                    var existing = ReadUnit(index);
                    if (existing.IsBoss && existing.Target.Health > 0)
                        throw new InvalidOperationException("Formal combat cannot spawn a second living Boss.");
                }
            }

            slot = -1;
            for (int index = 1; index < units.Length; index++)
            {
                var candidate = ReadUnit(index);
                if (candidate.Target.Health <= 0 && candidate.ConfigId == monster.Id)
                {
                    slot = index;
                    break;
                }
            }
            if (slot < 0) slot = AddMonsterSlot(monster);

            float radius = spawnRadiusPixels * rules.WorldUnitsPerPixel;
            float2 center = ReadUnit(0).Position;
            int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)settings.RandomSeed), spawnSequence, 0,
                CombatRandomPurpose.SpawnAngle, 0, int.MaxValue);
            double angle = sample / ((double)int.MaxValue + 1) * (2 * Math.PI);
            var template = templates[slot];
            if (!FindSpawnPosition(template.Movement, center, radius, angle, out var position))
            {
                slot = -1;
                return false;
            }

            var stats = counters[0];
            template.Position = position;
            template.PreviousPosition = template.SpawnPosition = position;
            template.Facing = math.normalizesafe(center - position);
            template.IsBoss = isBoss;
            template.Target.Lifetime = checked(++stats.NextLifetime);
            template.Attack.AttackerLifetime = template.Target.Lifetime;
            manager.SetComponentData(units[slot], template);
            manager.SetComponentData(units[slot], LocalTransform.FromPosition(new float3(position, 0)));
            stats.AliveMonsters++;
            counters[0] = stats;
            SpawnedMonsters = checked(SpawnedMonsters + 1);
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
                    float halfSegment = shape.HalfSegment2D + other.Movement.HalfSegment2D;
                    float2 offset = other.Position + other.Movement.Offset - shape.Offset - center;
                    float2 relative = position + shape.Offset - other.Position - other.Movement.Offset;
                    if (CombatGeometry.VerticalSegmentDistanceSquared(relative, halfSegment) >= sum * sum) continue;
                    if (!TryFindSpawnCapsuleExit(radius, offset, sum, halfSegment, angle, out double end)) return false;
                    // 单精度角度前进一步，避免圆周投影舍入再次落入刚越过的接触弧。
                    float positive = (float)(end + 2 * Math.PI);
                    angle = math.asfloat(math.asint(positive) + 1) - 2 * Math.PI;
                    blocked = true; break;
                }
                if (!blocked) return true;
            }
            return false;
        }

        /// <summary>求固定出生圆与一个纵向胶囊重叠弧在正角度方向上的首个出口。</summary>
        /// <param name="spawnRadius">出生圆半径。</param>
        /// <param name="capsuleCenter">胶囊中心相对出生圆心的位置。</param>
        /// <param name="clearance">两个胶囊端帽半径之和。</param>
        /// <param name="halfSegment">两个胶囊中轴半长之和。</param>
        /// <param name="angle">当前已确认位于重叠弧内的角度。</param>
        /// <param name="exitAngle">沿正方向离开胶囊的最早边界角。</param>
        /// <returns>是否存在不超过一整圈的出口。</returns>
        private static bool TryFindSpawnCapsuleExit(float spawnRadius, float2 capsuleCenter, float clearance,
            float halfSegment, double angle, out double exitAngle)
        {
            exitAngle = double.PositiveInfinity;
            ConsiderSpawnCircleBoundary(spawnRadius, capsuleCenter + new float2(0f, halfSegment),
                clearance, capsuleCenter, halfSegment, angle, ref exitAngle);
            ConsiderSpawnCircleBoundary(spawnRadius, capsuleCenter - new float2(0f, halfSegment),
                clearance, capsuleCenter, halfSegment, angle, ref exitAngle);
            if (spawnRadius > 0f)
            {
                ConsiderSpawnVerticalBoundary(spawnRadius, capsuleCenter.x - clearance, capsuleCenter,
                    clearance, halfSegment, angle, ref exitAngle);
                ConsiderSpawnVerticalBoundary(spawnRadius, capsuleCenter.x + clearance, capsuleCenter,
                    clearance, halfSegment, angle, ref exitAngle);
            }
            return double.IsFinite(exitAngle) && exitAngle <= angle + Math.PI * 2d;
        }

        /// <summary>把胶囊端帽圆与出生圆的交点中、交点后位于胶囊外的候选加入最早出口。</summary>
        /// <param name="spawnRadius">出生圆半径。</param>
        /// <param name="circleCenter">端帽圆心相对出生圆心的位置。</param>
        /// <param name="circleRadius">端帽圆半径。</param>
        /// <param name="capsuleCenter">完整胶囊中心。</param>
        /// <param name="halfSegment">完整胶囊中轴半长。</param>
        /// <param name="angle">当前重叠角。</param>
        /// <param name="best">当前最早出口；找到更早候选时修改。</param>
        private static void ConsiderSpawnCircleBoundary(float spawnRadius, float2 circleCenter, float circleRadius,
            float2 capsuleCenter, float halfSegment, double angle, ref double best)
        {
            double distance = math.length(circleCenter);
            if (!(spawnRadius > 0f) || distance <= 0d ||
                distance > spawnRadius + circleRadius || distance < Math.Abs(spawnRadius - circleRadius)) return;
            double cosine = (spawnRadius * spawnRadius + distance * distance - circleRadius * circleRadius) /
                (2d * spawnRadius * distance);
            double centerAngle = Math.Atan2(circleCenter.y, circleCenter.x);
            double half = Math.Acos(Math.Max(-1d, Math.Min(1d, cosine)));
            ConsiderSpawnExit(centerAngle - half, spawnRadius, capsuleCenter, circleRadius, halfSegment, angle, ref best);
            ConsiderSpawnExit(centerAngle + half, spawnRadius, capsuleCenter, circleRadius, halfSegment, angle, ref best);
        }

        /// <summary>把胶囊直边与出生圆的交点中、交点后位于胶囊外的候选加入最早出口。</summary>
        /// <param name="spawnRadius">出生圆半径。</param>
        /// <param name="x">胶囊一条竖直边相对出生圆心的横坐标。</param>
        /// <param name="capsuleCenter">完整胶囊中心。</param>
        /// <param name="clearance">胶囊半径。</param>
        /// <param name="halfSegment">胶囊中轴半长。</param>
        /// <param name="angle">当前重叠角。</param>
        /// <param name="best">当前最早出口；找到更早候选时修改。</param>
        private static void ConsiderSpawnVerticalBoundary(float spawnRadius, float x, float2 capsuleCenter,
            float clearance, float halfSegment, double angle, ref double best)
        {
            double ratio = x / spawnRadius;
            if (ratio < -1d || ratio > 1d) return;
            double first = Math.Acos(ratio);
            double second = Math.PI * 2d - first;
            if (Math.Abs(spawnRadius * Math.Sin(first) - capsuleCenter.y) <= halfSegment)
                ConsiderSpawnExit(first, spawnRadius, capsuleCenter, clearance, halfSegment, angle, ref best);
            if (Math.Abs(spawnRadius * Math.Sin(second) - capsuleCenter.y) <= halfSegment)
                ConsiderSpawnExit(second, spawnRadius, capsuleCenter, clearance, halfSegment, angle, ref best);
        }

        /// <summary>规范化边界角，并用边界后的单精度可区分位置确认它确实是胶囊出口。</summary>
        /// <param name="candidate">未规范化的边界角。</param>
        /// <param name="spawnRadius">出生圆半径。</param>
        /// <param name="capsuleCenter">胶囊中心相对出生圆心的位置。</param>
        /// <param name="clearance">胶囊半径。</param>
        /// <param name="halfSegment">胶囊中轴半长。</param>
        /// <param name="angle">当前重叠角。</param>
        /// <param name="best">当前最早出口；候选有效且更早时修改。</param>
        private static void ConsiderSpawnExit(double candidate, float spawnRadius, float2 capsuleCenter,
            float clearance, float halfSegment, double angle, ref double best)
        {
            while (candidate <= angle) candidate += Math.PI * 2d;
            if (candidate > angle + Math.PI * 2d || candidate >= best) return;
            float probe = math.asfloat(math.asint((float)candidate) + 1);
            float2 point = spawnRadius * new float2(math.cos(probe), math.sin(probe)) - capsuleCenter;
            if (CombatGeometry.VerticalSegmentDistanceSquared(point, halfSegment) >= clearance * clearance)
                best = candidate;
        }

        /// <summary>将表内秒数向上量化为模拟 tick；先还原 float 的十进制精度，避免 0.2 秒被浮点误差抬至多一帧。</summary>
        /// <param name="seconds">TbPerformanceScenario 的波间或波内生成间隔。</param>
        /// <returns>不少于一个 tick 的等待长度。</returns>
        /// <exception cref="OverflowException">配置等待时间超出整数时间轴可表示范围。</exception>
        private ulong SpawnDelayTicks(float seconds) => checked((ulong)Math.Ceiling((decimal)seconds * rules.SimulationHz));

        /// <summary>刷怪批次缺少空闲槽时扩展单位、模板和空间索引容量，弹丸仍按唯一发射者寿命上界独立持有。</summary>
        /// <param name="count">本批存活数加所需空槽的总量，包含玩家槽。</param>
        /// <remarks>只供旧测试定时生成模式调用；按该场景怪物目录轮转类型。新增实体由本会话退出时释放。</remarks>
        private void GrowUnits(int count)
        {
            units.Capacity = Math.Max(units.Capacity, count);
            templates.Capacity = Math.Max(templates.Capacity, count);
            grid.Capacity = Math.Max(grid.Capacity, count);
            requests.Capacity = Math.Max(requests.Capacity,
                checked(count * requestCapacityMultiplier + projectiles.Length));
            while (units.Length < count)
            {
                var monster = settings.Monsters[(units.Length - 1) % settings.Monsters.Count];
                AddMonsterSlot(monster);
            }
        }

        /// <summary>为指定真实怪物追加一个固定表现类型的池槽，初始保持失活。</summary>
        /// <param name="monster">本会话目录中的 TbMonster。</param>
        /// <returns>新槽在单位数组中的索引。</returns>
        /// <remarks>只在固定 Job 完成后的主线程调用；槽位后续仅复用同一 ConfigId，表现绑定无需重建。</remarks>
        private int AddMonsterSlot(MonsterConfig monster)
        {
            var template = BuildUnit(settings.GetMonsterProfile(monster), monster.DefaultSkillId_Ref,
                monster.BodyRadiusPixels.Value * rules.WorldUnitsPerPixel,
                new float2(monster.BodyOffsetXPixels.Value, monster.BodyOffsetYPixels.Value) * rules.WorldUnitsPerPixel,
                BuildHitEffectOffset(monster.HitEffectOffsetXPixels, monster.HitEffectOffsetYPixels,
                    monster.BodyOffsetXPixels.Value, monster.BodyOffsetYPixels.Value),
                BuildMovement(monster.MoveRadiusPixels, monster.MoveHeightPixels,
                    monster.MoveOffsetXPixels, monster.MoveOffsetYPixels, monster.MoveElevationPixels, monster.CollisionShape),
                float2.zero, false);
            template.ConfigId = monster.Id;
            template.VisualSetId = monster.VisualSetId;
            template.AttackOptionStart = attackOffsets[monster.VisualSetId];
            template.AttackOptionCount = attackCounts[monster.VisualSetId];
            var entity = manager.CreateEntity(typeof(CombatUnit), typeof(LocalTransform), typeof(LocalToWorld));
            int slot = units.Length;
            units.Add(entity);
            templates.Add(template);
            var inactive = template;
            inactive.Target.Health = 0;
            manager.SetComponentData(entity, inactive);
            manager.SetComponentData(entity, LocalTransform.FromScale(0));
            maximumRadius = math.max(maximumRadius,
                template.Radius + template.BodyHalfSegment + math.length(template.BodyOffset));
            maximumMotion = math.max(maximumMotion, template.MoveSpeed * (float)StepSeconds);
            grid.Capacity = Math.Max(grid.Capacity, units.Length);
            requests.Capacity = Math.Max(requests.Capacity,
                checked(units.Length * requestCapacityMultiplier + projectiles.Length));
            return slot;
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

        /// <summary>表现层在固定 tick 完成后读取弹丸池槽，不转移原生容器所有权。</summary>
        /// <param name="slot">弹丸池槽。</param>
        /// <returns>当前弹丸状态副本。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatProjectile ReadProjectile(int slot) { EnsureAlive(); return manager.GetComponentData<CombatProjectile>(projectiles[slot]); }

        /// <summary>读取正式玩家指定武器的独立冷却、投递和攻击快照。</summary>
        /// <param name="index">按 CombatRunDefinition.availableSkills 排列的武器索引。</param>
        /// <returns>当前武器纯值状态。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatWeaponState ReadPlayerWeapon(int index) { EnsureAlive(); return playerWeapons[index]; }

        /// <summary>获取本次 Advance 内首次接触事件数量；下一次 Advance 开始时清空。</summary>
        public int ImpactCount { get { EnsureAlive(); return impacts.Length; } }

        /// <summary>表现层按稳定写入顺序读取一次命中事件。</summary>
        /// <param name="index">零起始事件索引。</param>
        /// <returns>包含技能、位置、攻击序号和 tick 的事件副本。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatImpactEvent ReadImpact(int index) { EnsureAlive(); return impacts[index]; }

        /// <summary>获取本次 Advance 内目标位置群体技能释放事件数量。</summary>
        public int AreaCount { get { EnsureAlive(); return areas.Length; } }

        /// <summary>表现层按稳定攻击序号读取一次目标位置释放事件。</summary>
        /// <param name="index">零起始事件索引。</param>
        /// <returns>技能、锁定位置、攻击序号和 tick。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatAreaEvent ReadArea(int index) { EnsureAlive(); return areas[index]; }

        /// <summary>获取本次 Advance 内首次死亡事件数量；下一次 Advance 开始时清空。</summary>
        public int DeathCount { get { EnsureAlive(); return deaths.Length; } }

        /// <summary>正式单局按稳定伤害顺序读取死亡事件，供经验、Boss 和结算逻辑消费。</summary>
        /// <param name="index">零起始事件索引。</param>
        /// <returns>包含真实配置身份、位置、生命周期和模拟 tick 的事件副本。</returns>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        public CombatDeathEvent ReadDeath(int index) { EnsureAlive(); return deaths[index]; }

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
            return SynchronizeAttributes(slot, expectedLifetime, CombatAttributeSnapshot.Capture(attributes));
        }

        /// <summary>正式单局升级后以纯值快照同步玩家防守、攻击、移动和冷却比例。</summary>
        /// <param name="slot">当前生命周期的单位槽。</param>
        /// <param name="expectedLifetime">同步请求记录的实体生命周期。</param>
        /// <param name="attributes">CombatRunModel 从 Luban 基础值及升级来源导出的属性快照。</param>
        /// <returns>生命周期仍匹配且单位存活时为真。</returns>
        /// <remarks>修改当前 ECS 单位，不改变出生模板；已起手攻击仍使用原快照。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="InvalidOperationException">新属性值无法形成有效战斗状态。</exception>
        public bool SynchronizeAttributes(int slot, ulong expectedLifetime, in CombatAttributeSnapshot attributes)
        {
            EnsureAlive();
            var unit = ReadUnit(slot);
            if (unit.Target.Lifetime != expectedLifetime || unit.Target.Health <= 0) return false;
            var skill = slot == 0 ? settings.PlayerSkill.CombatProfileId_Ref :
                settings.GetMonster(unit.ConfigId).DefaultSkillId_Ref.CombatProfileId_Ref;
            double interval = CombatMath.AttackInterval(skill.BaseInterval, attributes.AttackSpeedMultiplier, rules.MinAttackInterval);
            unit.Cooldown = CombatMath.RescaleCooldown(unit.Cooldown, unit.Interval, interval);
            unit.Interval = interval;
            unit.Target.Synchronize(attributes);
            unit.Attack = AttackSnapshot.Capture(attributes, unit.Target.Lifetime, 0, unit.Target.Faction);
            unit.MoveSpeed = (float)attributes.MoveSpeed * rules.WorldUnitsPerPixel;
            if (slot == 0 && settings.IsFormalRun)
            {
                for (int index = 0; index < playerWeapons.Length; index++)
                {
                    CombatWeaponState weapon = playerWeapons[index];
                    SkillCombatConfig weaponConfig = settings.AvailablePlayerSkills[index].CombatProfileId_Ref;
                    double weaponInterval = CombatMath.AttackInterval(weaponConfig.BaseInterval,
                        attributes.AttackSpeedMultiplier, rules.MinAttackInterval);
                    weapon.Cooldown = CombatMath.RescaleCooldown(weapon.Cooldown, weapon.Interval, weaponInterval);
                    weapon.Interval = weaponInterval;
                    weapon.Attack = AttackSnapshot.Capture(attributes, unit.Target.Lifetime, 0, unit.Target.Faction);
                    playerWeapons[index] = weapon;
                }
            }
            maximumMotion = math.max(maximumMotion, unit.MoveSpeed * (float)StepSeconds);
            manager.SetComponentData(units[slot], unit);
            return true;
        }

        /// <summary>升级选择后以 CombatRunModel.activeSkills 切换正式玩家武器；每把武器保留独立冷却。</summary>
        /// <param name="skillIds">当前模型已解锁的 TbSkill.id 集合。</param>
        /// <remarks>新解锁武器立即 Ready；已有武器不重置冷却。只修改正式会话自有原生状态。</remarks>
        /// <exception cref="InvalidOperationException">测试会话调用、技能重复或包含本关目录外技能。</exception>
        public void SynchronizePlayerSkills(IEnumerable<int> skillIds)
        {
            EnsureAlive();
            if (!settings.IsFormalRun) throw new InvalidOperationException("Player weapon synchronization requires a formal run.");
            if (skillIds == null) throw new InvalidOperationException("Formal active skill collection is required.");
            int[] ids = skillIds.ToArray();
            if (ids.Distinct().Count() != ids.Length)
                throw new InvalidOperationException("Formal active skill collection contains duplicates.");
            var requested = new HashSet<int>(ids);
            var known = new HashSet<int>(settings.AvailablePlayerSkills.Select(item => item.Id));
            if (requested.Any(id => !known.Contains(id)))
                throw new InvalidOperationException("Formal active skill is outside the stage catalog.");
            for (int index = 0; index < playerWeapons.Length; index++)
            {
                CombatWeaponState weapon = playerWeapons[index];
                bool active = requested.Contains(weapon.SkillId);
                if (active && !weapon.Active) weapon.Cooldown = 0;
                weapon.Active = active;
                playerWeapons[index] = weapon;
            }
        }

        /// <summary>正式升级或重开后同步玩家属性，并将模型持有的当前生命精确写回 ECS。</summary>
        /// <param name="expectedLifetime">UI 操作开始时的玩家生命周期。</param>
        /// <param name="attributes">CombatRunModel 的当前属性快照。</param>
        /// <param name="currentHealth">升级、治疗或重开后的模型生命。</param>
        /// <returns>玩家仍为同一存活生命周期且完成同步时为真。</returns>
        /// <remarks>会修改玩家生命；仅用于模型已经验证的正式状态，不发布伤害或死亡事件。</remarks>
        /// <exception cref="InvalidOperationException">生命不是有限正数或超过聚合后的生命上限。</exception>
        public bool SynchronizeRunPlayer(ulong expectedLifetime, in CombatAttributeSnapshot attributes, double currentHealth)
        {
            if (double.IsNaN(currentHealth) || double.IsInfinity(currentHealth) || currentHealth <= 0 ||
                currentHealth > attributes.MaxHealth || currentHealth != Math.Floor(currentHealth))
                throw new InvalidOperationException("Formal player health is outside the synchronized attribute range.");
            if (!SynchronizeAttributes(0, expectedLifetime, attributes)) return false;
            var player = ReadUnit(0);
            player.Target.Health = (long)currentHealth;
            manager.SetComponentData(units[0], player);
            return true;
        }

        /// <summary>把一条正式 TbSkill 转换为独立武器纯值；数值全部来自技能战斗表和玩家属性快照。</summary>
        /// <param name="skillConfig">CombatRunDefinition.availableSkills 中的技能。</param>
        /// <param name="attack">玩家当前基础攻击快照。</param>
        /// <param name="active">是否属于 TbStage.initialSkillIds。</param>
        /// <returns>冷却就绪的 Projectile 或 TargetArea 武器。</returns>
        /// <exception cref="InvalidOperationException">技能投递类型或对应字段不完整。</exception>
        private CombatWeaponState BuildWeapon(SkillConfig skillConfig, in AttackSnapshot attack, bool active)
        {
            SkillCombatConfig combat = skillConfig?.CombatProfileId_Ref ??
                throw new InvalidOperationException("Formal player weapon requires TbSkillCombat.");
            float range = combat.RangePixels * rules.WorldUnitsPerPixel;
            CombatMath.NonNegative(range);
            CombatMath.Positive(combat.BaseInterval);
            var weapon = new CombatWeaponState
            {
                Attack = attack,
                SkillId = skillConfig.Id,
                Delivery = combat.DeliveryType,
                Range = range,
                BaseInterval = combat.BaseInterval,
                Interval = CombatMath.AttackInterval(combat.BaseInterval,
                    new CombatAttributes(settings.CharacterProfile).Get(EAttributeType.AttackSpeedMultiplier),
                    rules.MinAttackInterval),
                Active = active
            };
            if (combat.DeliveryType == ESkillDeliveryType.Projectile)
            {
                if (!combat.ProjectileSpeedPixelsPerSecond.HasValue || !combat.ProjectileLifetime.HasValue ||
                    !skillConfig.ProjectileRadiusPixels.HasValue || !skillConfig.ProjectileOffsetXPixels.HasValue ||
                    !skillConfig.ProjectileOffsetYPixels.HasValue)
                    throw new InvalidOperationException($"TbSkill {skillConfig.Id}: incomplete projectile weapon.");
                float projectileSpeed = combat.ProjectileSpeedPixelsPerSecond.Value * rules.WorldUnitsPerPixel;
                CombatMath.Positive(projectileSpeed);
                CombatMath.Positive(combat.ProjectileLifetime.Value);
                CombatMath.Positive(skillConfig.ProjectileRadiusPixels.Value);
                weapon.ProjectileSpeed = projectileSpeed;
                weapon.ProjectileLifetime = combat.ProjectileLifetime.Value;
                weapon.ProjectileRadius = skillConfig.ProjectileRadiusPixels.Value * rules.WorldUnitsPerPixel;
                weapon.ProjectileOffset = new float2(skillConfig.ProjectileOffsetXPixels.Value,
                    skillConfig.ProjectileOffsetYPixels.Value) * rules.WorldUnitsPerPixel;
                return weapon;
            }
            if (combat.DeliveryType != ESkillDeliveryType.TargetArea || !skillConfig.AreaRadiusPixels.HasValue)
                throw new InvalidOperationException($"TbSkill {skillConfig.Id}: incomplete target-area weapon.");
            weapon.AreaRadius = skillConfig.AreaRadiusPixels.Value * rules.WorldUnitsPerPixel;
            CombatMath.Positive(weapon.AreaRadius);
            return weapon;
        }

        /// <summary>入局时将 TbAttributeProfile、TbSkillCombat 与身体半径转为非托管模板，并把移动速度由逻辑像素换算到模拟坐标。</summary>
        /// <param name="profile">完整属性方案。</param>
        /// <param name="skillConfig">TbSkill 技能，碰撞半径与偏移独立于其 TbSkillCombat 引用。</param>
        /// <param name="radius">TbCharacter/TbMonster.bodyRadiusPixels 换算后的模拟半径。</param>
        /// <param name="bodyOffset">TbCharacter/TbMonster 受击中心像素偏移换算后的模拟偏移。</param>
        /// <param name="hitEffectOffset">TbCharacter/TbMonster 命中特效挂点像素偏移换算后的模拟偏移。</param>
        /// <param name="movement">TbCharacter/TbMonster 导出的独立移动圆柱。</param>
        /// <param name="position">入口配置转换后的出生位置。</param>
        /// <param name="player">区分玩家弹丸与怪物近战角色。</param>
        /// <returns>冷却就绪且目标为空的出生模板。</returns>
        /// <exception cref="InvalidOperationException">属性、几何或技能配置不合法。</exception>
        private CombatUnit BuildUnit(AttributeProfileConfig profile, SkillConfig skillConfig, float radius, float2 bodyOffset,
            float2 hitEffectOffset, CombatCylinder movement, float2 position, bool player)
        {
            var skill = skillConfig.CombatProfileId_Ref ?? throw new InvalidOperationException("Missing skill combat profile.");
            var attributes = new CombatAttributes(profile);
            float range = skill.RangePixels * rules.WorldUnitsPerPixel;
            CombatMath.Positive(radius);
            if (!math.all(math.isfinite(position)) || !math.all(math.isfinite(bodyOffset)) || !math.all(math.isfinite(hitEffectOffset)) ||
                math.abs(position.x + bodyOffset.x) + radius > settings.Arena.x ||
                math.abs(position.y + bodyOffset.y) + radius > settings.Arena.y)
                throw new InvalidOperationException("Combat unit is outside the configured arena.");
            CombatMath.NonNegative(range);
            if (skill.DeliveryType != (player ? ESkillDeliveryType.Projectile : ESkillDeliveryType.Melee))
                throw new InvalidOperationException($"TbSkillCombat {skill.Id}: unsupported actor delivery role.");
            if (player)
            {
                if (!skill.ProjectileSpeedPixelsPerSecond.HasValue || !skillConfig.ProjectileRadiusPixels.HasValue || !skill.ProjectileLifetime.HasValue || !skillConfig.ProjectileOffsetXPixels.HasValue || !skillConfig.ProjectileOffsetYPixels.HasValue)
                    throw new InvalidOperationException($"TbSkillCombat {skill.Id}: missing projectile fields.");
                CombatMath.Positive(skill.ProjectileSpeedPixelsPerSecond.Value); CombatMath.Positive(skillConfig.ProjectileRadiusPixels.Value); CombatMath.Positive(skill.ProjectileLifetime.Value);
            }
            else
            {
                if (!skill.AttackWindupSeconds.HasValue)
                    throw new InvalidOperationException($"TbSkillCombat {skill.Id}: missing attackWindupSeconds.");
                CombatMath.NonNegative(skill.AttackWindupSeconds.Value);
            }
            int faction = (int)(player ? CombatFaction.Player : CombatFaction.Monster);
            bool unifiedCapsule = movement.UsesCapsule2D;
            return new CombatUnit { Target = DamageTarget.Spawn(attributes, 0, faction, player),
                Attack = AttackSnapshot.Capture(attributes, 0, 0, faction), Position = position, PreviousPosition = position, SpawnPosition = position,
                Radius = unifiedCapsule ? movement.Radius : radius,
                BodyOffset = unifiedCapsule ? movement.Offset : bodyOffset,
                HitEffectOffset = hitEffectOffset,
                BodyHalfSegment = unifiedCapsule ? movement.HalfSegment2D : 0f,
                Movement = movement,
                MoveSpeed = (float)attributes.Get(EAttributeType.MoveSpeed) * rules.WorldUnitsPerPixel, Range = range,
                Interval = CombatMath.AttackInterval(skill.BaseInterval, attributes.Get(EAttributeType.AttackSpeedMultiplier), rules.MinAttackInterval),
                Delivery = skill.DeliveryType, TargetSlot = -1,
                BaseInterval = skill.BaseInterval,
                WindupSeconds = player ? 0 : skill.AttackWindupSeconds.Value,
                Facing = new float2(settings.Presentation.InitialDirectionId_Ref.X, settings.Presentation.InitialDirectionId_Ref.Y),
                // 近战不消费弹丸字段；零仅为空布局，不作为弹丸参数兜底。
                SkillId = skillConfig.Id,
                ProjectileOffset = player ? new float2(skillConfig.ProjectileOffsetXPixels.Value, skillConfig.ProjectileOffsetYPixels.Value) * rules.WorldUnitsPerPixel : float2.zero,
                ProjectileSpeed = player ? skill.ProjectileSpeedPixelsPerSecond.Value * rules.WorldUnitsPerPixel : 0,
                ProjectileLifetime = player ? skill.ProjectileLifetime.Value : 0, ProjectileRadius = player ? skillConfig.ProjectileRadiusPixels.Value * rules.WorldUnitsPerPixel : 0 };
        }

        /// <summary>从角色或怪物表构建移动形状，按 collisionShape 决定保留旧圆柱还是启用统一纵向胶囊。</summary>
        /// <param name="radius">表内半径像素。</param>
        /// <param name="height">表内总高像素。</param>
        /// <param name="x">表内中心横向偏移像素。</param>
        /// <param name="y">表内中心纵向偏移像素。</param>
        /// <param name="elevation">旧圆柱底面高度；统一胶囊不使用。</param>
        /// <param name="shape">TbCharacter/TbMonster.collisionShape。</param>
        /// <returns>可由 Burst 使用的纯值碰撞形状。</returns>
        private CombatCylinder BuildMovement(float? radius, float? height, float? x, float? y,
            float? elevation, EUnitCollisionShape? shape)
        {
            CombatCylinder movement = CombatCylinder.FromConfig(radius, height, x, y, elevation,
                rules.WorldUnitsPerPixel);
            return shape == EUnitCollisionShape.VerticalCapsule ? movement.AsVerticalCapsule2D() : movement;
        }

        /// <summary>把单位命中特效挂点从逻辑像素转换为模拟偏移；旧性能场景未迁移时复用其受击中心。</summary>
        /// <param name="hitX">TbCharacter/TbMonster.hitEffectOffsetXPixels 像素值。</param>
        /// <param name="hitY">TbCharacter/TbMonster.hitEffectOffsetYPixels 像素值。</param>
        /// <param name="bodyX">现有受击中心横向像素。</param>
        /// <param name="bodyY">现有受击中心纵向像素。</param>
        /// <returns>可直接写入 CombatUnit.HitEffectOffset 的世界偏移。</returns>
        /// <exception cref="InvalidOperationException">正式关卡缺字段，或只配置了一个轴。</exception>
        /// <remarks>兼容分支仅服务 UseLegacyTimedSpawn 性能场景；正式 TbStage 必须显式配置并由 Prefab 导出。</remarks>
        private float2 BuildHitEffectOffset(float? hitX, float? hitY, float bodyX, float bodyY)
        {
            if (hitX.HasValue != hitY.HasValue)
                throw new InvalidOperationException("Combat hit-effect anchor must configure both axes.");
            if (!hitX.HasValue && settings.IsFormalRun)
                throw new InvalidOperationException("Formal combat unit requires a hit-effect anchor.");
            return new float2(hitX ?? bodyX, hitY ?? bodyY) * rules.WorldUnitsPerPixel;
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
            if (impacts.IsCreated) impacts.Dispose();
            if (areas.IsCreated) areas.Dispose();
            if (deaths.IsCreated) deaths.Dispose();
            if (attackOptions.IsCreated) attackOptions.Dispose();
            if (playerWeapons.IsCreated) playerWeapons.Dispose();
            if (playerWeaponTemplates.IsCreated) playerWeaponTemplates.Dispose();
        }
    }
}
