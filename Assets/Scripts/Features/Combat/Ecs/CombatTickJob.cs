using System;
using cfg;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>单个固定 tick 的 Burst 调度；全部输入为纯值与原生容器，不访问托管表或资源服务。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatTickJob : IJob
    {
        private static readonly ProfilerMarker MovementMarker = new ProfilerMarker("Roguelike.Combat.MoveAndIndex");
        private static readonly ProfilerMarker AttackMarker = new ProfilerMarker("Roguelike.Combat.CollectAttacks");
        private static readonly ProfilerMarker ProjectileMarker = new ProfilerMarker("Roguelike.Combat.SweepProjectiles");
        private static readonly ProfilerMarker AreaMarker = new ProfilerMarker("Roguelike.Combat.PulseAreas");
        private static readonly ProfilerMarker DamageMarker = new ProfilerMarker("Roguelike.Combat.ResolveDamage");
        private static readonly ProfilerMarker RecycleMarker = new ProfilerMarker("Roguelike.Combat.RecycleTransforms");
        [ReadOnly] public NativeArray<Entity> Units;
        [ReadOnly] public NativeArray<Entity> Projectiles;
        [ReadOnly] public NativeArray<CombatUnit> Templates;
        [ReadOnly] public NativeArray<CombatAttackOption> AttackOptions;
        public NativeArray<CombatWeaponState> PlayerWeapons;
        public ComponentLookup<CombatUnit> UnitData;
        public NativeArray<CombatUnit> UnitStates;
        public ComponentLookup<CombatProjectile> ProjectileData;
        public ComponentLookup<LocalTransform> Transforms;
        public NativeParallelMultiHashMap<int2, int> Grid;
        public NativeList<CombatDamageRequest> Requests;
        public NativeList<CombatImpactEvent> Impacts;
        public NativeList<CombatCastEvent> Casts;
        public NativeList<CombatLifeDamageEvent> LifeDamage;
        public bool RecordLifeDamage;
        public bool OverridePlayerMovement;
        public float PlayerBaseMoveSpeed, PlayerMoveMultiplier;
        public ulong PlayerMoveUntilTick;
        public NativeList<CombatAreaEvent> Areas;
        public NativeList<CombatPersistentArea> PersistentAreas;
        public NativeList<CombatDeathEvent> Deaths;
        public NativeArray<CombatCounters> Counters;
        public DamageRules Rules;
        public float2 Input, Arena;
        public float Delta, CellSize, MaximumRadius, MaximumMotion;
        public double Time, TargetRefresh;
        public ulong Seed;
        public bool Replenish, RestorePlayer, AllowMonstersOutsideArena, UsePlayerWeapons;

        /// <summary>由会话固定步进调用：移动、建网格、收集攻击、扫掠、排序结算、回收及变换同步。</summary>
        /// <remarks>Tick开始从ECS连续采集到会话独占UnitStates，所有阶段按原槽序读写缓冲，结束统一写回；调用方必须等待任务完成后再读或释放。Profiler阶段标记只观测技术耗时，不改变结算顺序或表内规则。</remarks>
        public void Execute()
        {
            var counters = Counters[0];
            if (counters.PlayerDead) return;
            for (int slot = 0; slot < Units.Length; slot++) UnitStates[slot] = UnitData[Units[slot]];
            if (OverridePlayerMovement)
            {
                var player = UnitStates[0];
                player.MoveSpeed = PlayerBaseMoveSpeed * (counters.Tick < PlayerMoveUntilTick ? PlayerMoveMultiplier : 1f);
                UnitStates[0] = player;
            }
            bool burst = true;
            MarkManagedExecution(ref burst);
            counters.UsedBurst = burst;
            using (MovementMarker.Auto())
            {
                Requests.Clear();
                Grid.Clear();
                MoveAndIndex(ref counters);
            }
            // 全部攻击先生成，伤害最后统一结算，允许同 tick 双方互杀。
            using (AttackMarker.Auto())
            {
                if (UsePlayerWeapons) CollectPlayerWeapons(ref counters);
                else CollectAttack(0, ref counters);
                for (int slot = 1; slot < Units.Length; slot++) CollectAttack(slot, ref counters);
            }
            using (ProjectileMarker.Auto()) SweepProjectiles(ref counters);
            using (AreaMarker.Auto())
                if (PersistentAreas.IsCreated) PulseAreas(ref counters);
            using (DamageMarker.Auto())
            {
                Requests.AsArray().Sort();
                for (int index = 0; index < Requests.Length; index++) ApplyDamage(Requests[index], ref counters);
            }
            using (RecycleMarker.Auto()) RecycleAndSynchronize(ref counters);
            for (int slot = 0; slot < Units.Length; slot++) UnitData[Units[slot]] = UnitStates[slot];
            counters.Tick++;
            Counters[0] = counters;
        }

        /// <summary>托管回退时清除执行标记；Burst 编译器丢弃此调用，以便验收实际执行路径。</summary>
        /// <param name="burst">原本为真的原生执行标记。</param>
        [BurstDiscard]
        private static void MarkManagedExecution(ref bool burst) { burst = false; }

        /// <summary>先建立空间索引，再按槽位扫掠圆柱并沿接触边缘滑动；每次移动更新网格。</summary>
        /// <param name="counters">累积候选与接触技术计数，不参与战斗决策。</param>
        /// <remarks>玩家和怪物均被其他活单位阻挡，不施加推力；攻击前摇中的怪物维持位置和朝向。</remarks>
        private void MoveAndIndex(ref CombatCounters counters)
        {
            float footprint = 0;
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitStates[slot];
                unit.PreviousPosition = unit.Position; UnitStates[slot] = unit;
                if (unit.Target.Health <= 0) continue;
                footprint = math.max(footprint, unit.Movement.Radius + unit.Movement.HalfSegment2D +
                    math.length(unit.Movement.Offset));
                Grid.Add((int2)math.floor(unit.Position / CellSize), slot);
            }
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitStates[slot];
                if (unit.Target.Health <= 0) continue;
                var player = UnitStates[0];
                float2 motion = Input * unit.MoveSpeed * Delta;
                if (slot != 0)
                {
                    motion = float2.zero;
                    float2 offset = (player.Position - unit.Position) + (player.BodyOffset - unit.BodyOffset);
                    float distance = math.sqrt(CombatGeometry.VerticalSegmentDistanceSquared(offset,
                        unit.BodyHalfSegment + player.BodyHalfSegment));
                    float stop = unit.Range + unit.Radius + player.Radius;
                    if (!unit.AttackActive && distance > stop)
                        motion = offset / distance * math.min(unit.MoveSpeed * Delta, distance - stop);
                    if (!unit.AttackActive && distance > 0) unit.Facing = offset / distance;
                }
                // 先将期望终点限制在场地内，再扫掠，避免碰撞之后夹边界把单位夹进别人身体。
                if (slot == 0 || !AllowMonstersOutsideArena)
                    motion = math.clamp(unit.Position + motion + unit.Movement.Offset,
                        -Arena + MovementExtents(unit.Movement), Arena - MovementExtents(unit.Movement)) -
                        unit.Movement.Offset - unit.Position;
                Grid.Remove((int2)math.floor(unit.Position / CellSize), slot);
                unit.Position = MoveCylinder(slot, unit, motion, footprint, ref counters);
                UnitStates[slot] = unit;
                Grid.Add((int2)math.floor(unit.Position / CellSize), slot);
            }
        }

        /// <summary>网格中稳定选择最早接触并沿边滑动；同一可表示位置上的余量循环用倍增检查点识别收敛。</summary>
        /// <param name="slot">移动单位槽。</param><param name="unit">移动前组件。</param>
        /// <param name="motion">已限制场地的期望位移。</param><param name="footprint">本 tick 最大圆柱半径加中心偏移。</param>
        /// <param name="counters">只观测扫描、接触与零进展次数的技术计数。</param>
        /// <returns>不会穿过已有圆柱的终点。</returns>
        /// <remarks>函数内网格与其他单位状态不变，重复的同一位置/夹取余量必然再次产生同一接触与投影。提前返回的位置与旧循环耗尽槽数上界一致；不使用容差，不改变投影、阻挡、接触顺序或剩余位移。</remarks>
        private float2 MoveCylinder(int slot, in CombatUnit unit, float2 motion, float footprint, ref CombatCounters counters)
        {
            float2 position = unit.Position;
            int previousSelected = -1;
            float2 checkpointPosition = default, checkpointMotion = default;
            bool hasCheckpoint = false;
            long period = 1, span = 0;
            // 接触求解次数由候选单位数界定；这是算法终止上界，不是策划移动参数。
            for (int contact = 0; contact <= Units.Length && math.lengthsq(motion) > 0; contact++)
            {
                counters.MovementSweeps++;
                if (contact == Units.Length) counters.MovementLimitHits++;
                if (slot == 0 || !AllowMonstersOutsideArena)
                    motion = math.clamp(position + motion + unit.Movement.Offset,
                        -Arena + MovementExtents(unit.Movement), Arena - MovementExtents(unit.Movement)) -
                        unit.Movement.Offset - position;
                // 只在位置完全不变的区间使用Brent倍增检查点；位置推进立即重置，
                // 因而只跳过原实现已陷入的有限精度循环，不改变耗尽上界时的终点。
                bool samePosition = hasCheckpoint && math.all(math.asuint(position) == math.asuint(checkpointPosition));
                if (samePosition && math.all(math.asuint(motion) == math.asuint(checkpointMotion)))
                { counters.MovementConverged++; break; }
                if (!samePosition || span == period)
                {
                    checkpointPosition = position; checkpointMotion = motion;
                    period = samePosition ? period * 2 : 1;
                    span = 0; hasCheckpoint = true;
                }
                span++;
                float2 center = position + unit.Movement.Offset;
                float extent = unit.Movement.Radius + footprint;
                int2 lower = (int2)math.floor((math.min(center, center + motion) - extent) / CellSize);
                int2 upper = (int2)math.floor((math.max(center, center + motion) + extent) / CellSize);
                int selected = -1; float fraction = 1; float2 normal = float2.zero;
                for (int y = lower.y; y <= upper.y; y++)
                    for (int x = lower.x; x <= upper.x; x++)
                    {
                        if (!Grid.TryGetFirstValue(new int2(x, y), out int otherSlot, out var iterator)) continue;
                        do
                        {
                            if (otherSlot == slot) continue;
                            counters.MovementCandidates++;
                            var other = UnitStates[otherSlot];
                            if (other.Target.Health <= 0 || !unit.Movement.HeightOverlaps(other.Movement)) continue;
                            float2 relative = center - other.Position - other.Movement.Offset;
                            float radius = unit.Movement.Radius + other.Movement.Radius;
                            float halfSegment = unit.Movement.HalfSegment2D + other.Movement.HalfSegment2D;
                            if (!CombatCylinder.Sweep(relative, motion, radius, halfSegment,
                                out float hit, out float2 hitNormal)) continue;
                            if (hit < fraction || (hit == fraction && (selected < 0 || otherSlot < selected)))
                            { selected = otherSlot; fraction = hit; normal = hitNormal; }
                        } while (Grid.TryGetNextValue(out otherSlot, ref iterator));
                    }
                if (selected < 0) return position + motion;
                counters.MovementContacts++;
                if (selected == previousSelected) counters.MovementRepeatedContacts++;
                previousSelected = selected;
                // 接触比例退一个 float ULP，抵消乘加舍入造成的数值重叠，不增加配置间隙。
                float safe = fraction > 0 ? math.asfloat(math.asint(fraction) - 1) : 0;
                float2 before = position;
                position += motion * safe;
                if (math.all(before == position)) counters.MovementZeroProgress++;
                float2 remainder = motion * (1 - fraction);
                float2 slide = remainder - normal * math.min(0, math.dot(remainder, normal));
                if (math.lengthsq(slide) >= math.lengthsq(motion)) break;
                motion = slide;
            }
            return position;
        }

        /// <summary>对范围内网格候选做精确距离与阵营筛选；等距使用生成序号。</summary>
        /// <param name="unit">寻敌单位。</param>
        /// <param name="configuredRange">由TbSkillCombat.rangePixels换算并聚合构筑后的世界距离。</param>
        /// <returns>最近合法目标的池槽，找不到返回无效索引。</returns>
        private int FindNearest(in CombatUnit unit, float configuredRange)
        {
            float search = configuredRange + unit.Radius + unit.BodyHalfSegment +
                math.length(unit.BodyOffset) + MaximumRadius;
            int2 lower = (int2)math.floor((unit.Position - search) / CellSize);
            int2 upper = (int2)math.floor((unit.Position + search) / CellSize);
            int selected = -1;
            float best = float.PositiveInfinity;
            ulong lifetime = ulong.MaxValue;
            for (int y = lower.y; y <= upper.y; y++)
                for (int x = lower.x; x <= upper.x; x++)
                {
                    if (!Grid.TryGetFirstValue(new int2(x, y), out int slot, out var iterator)) continue;
                    do
                    {
                        var target = UnitStates[slot];
                        if (target.Target.Health <= 0 || target.Target.Faction == unit.Target.Faction) continue;
                        float2 bodyOffset = (target.Position - unit.Position) + (target.BodyOffset - unit.BodyOffset);
                        float halfSegment = unit.BodyHalfSegment + target.BodyHalfSegment;
                        float distance = CombatGeometry.VerticalSegmentDistanceSquared(bodyOffset, halfSegment);
                        float range = configuredRange + unit.Radius + target.Radius;
                        if (!CombatGeometry.WithinCapsuleReach(bodyOffset, halfSegment, range)) continue;
                        if (distance < best || (distance == best && target.Target.Lifetime < lifetime))
                        { selected = slot; best = distance; lifetime = target.Target.Lifetime; }
                    } while (Grid.TryGetNextValue(out slot, ref iterator));
                }
            return selected;
        }

        /// <summary>正式玩家逐把推进冷却；按 TbSkillCombat 发射阵型、瞬时范围或持续区域。</summary>
        /// <param name="counters">本局攻击、弹丸、范围释放和唯一序号计数。</param>
        /// <remarks>全部武器共享当前玩家属性快照，但冷却、投递参数和表现 ID 独立读取各自 TbSkill；实际释放只追加一次施法事件，与散射弹丸数无关，不改变攻击序号或移动。</remarks>
        private void CollectPlayerWeapons(ref CombatCounters counters)
        {
            CombatUnit player = UnitStates[0];
            if (player.Target.Health <= 0) return;
            for (int index = 0; index < PlayerWeapons.Length; index++)
            {
                CombatWeaponState weapon = PlayerWeapons[index];
                if (!weapon.Active) continue;
                weapon.Cooldown = math.max(0, weapon.Cooldown - Delta);
                int targetSlot = FindNearest(player, weapon.Range);
                if (weapon.Cooldown <= 0 && targetSlot >= 0)
                {
                    CombatUnit target = UnitStates[targetSlot];
                    AttackSnapshot attack = weapon.Attack;
                    attack.Sequence = ++counters.NextAttack;
                    attack.AttackerLifetime = player.Target.Lifetime;
                    attack.SkillId = weapon.SkillId;
                    attack.Source = CombatDamageSource.Weapon;
                    if (weapon.Delivery == ESkillDeliveryType.Projectile)
                        SpawnPlayerProjectile(player, target, weapon, attack, ref counters);
                    else if (weapon.AreaDuration > 0)
                        PersistentAreas.Add(new CombatPersistentArea { Weapon = weapon, Attack = attack,
                            Position = weapon.FollowsOwner ? player.Position + player.BodyOffset : target.Position + target.BodyOffset,
                            OwnerLifetime = player.Target.Lifetime });
                    else
                        CastArea(weapon.FollowsOwner ? player.Position + player.BodyOffset : target.Position + target.BodyOffset,
                            weapon, attack, player.Target.Lifetime, ref counters);
                    Casts.Add(new CombatCastEvent { SkillId = weapon.SkillId, CasterSlot = 0,
                        CasterLifetime = player.Target.Lifetime, AttackSequence = attack.Sequence, Tick = counters.Tick + 1 });
                    weapon.Attack = attack;
                    weapon.Cooldown = weapon.Interval;
                    counters.Attacks++;
                }
                PlayerWeapons[index] = weapon;
            }
        }

        /// <summary>按 TbSkillCombat 阵型发射全部弹丸；各弹丸独立序号及生命周期命中记录。</summary>
        /// <param name="player">发射时玩家位置和朝向。</param>
        /// <param name="target">锁定时目标位置。</param>
        /// <param name="weapon">TbSkill 转换的弹丸参数。</param>
        /// <param name="attack">本次唯一攻击快照。</param>
        /// <param name="counters">弹丸并发与生成统计。</param>
        /// <exception cref="InvalidOperationException">表驱动并发容量计算不足。</exception>
        private void SpawnPlayerProjectile(in CombatUnit player, in CombatUnit target, in CombatWeaponState weapon,
            in AttackSnapshot attack, ref CombatCounters counters)
        {
            float2 aim = math.normalizesafe(target.Position + target.BodyOffset - player.Position,
                math.normalizesafe(player.Facing));
            for (int shot = 0; shot < weapon.ProjectileCount; shot++)
            {
                float angle = weapon.Pattern == EProjectilePattern.Radial ? math.PI * 2 * shot / weapon.ProjectileCount :
                    weapon.Pattern == EProjectilePattern.Fan ? weapon.SpreadRadians * (shot / (float)(weapon.ProjectileCount - 1) - 0.5f) : 0;
                math.sincos(angle, out float sine, out float cosine);
                float2 direction = new float2(aim.x * cosine - aim.y * sine, aim.x * sine + aim.y * cosine);
                AttackSnapshot snapshot = attack;
                if (shot > 0) snapshot.Sequence = ++counters.NextAttack;
                SpawnPatternProjectile(player.Position, direction, weapon, snapshot, ref counters);
            }
        }

        /// <summary>取得一个会话实体池空槽，并清空前一代弹丸的穿透、连锁和已命中记录。</summary>
        /// <param name="origin">本次施法出生位置。</param>
        /// <param name="direction">阵型计算后的单位方向。</param>
        /// <param name="weapon">TbSkillCombat 投递快照。</param>
        /// <param name="attack">该弹丸唯一攻击快照。</param>
        /// <param name="counters">弹丸生成及并发计数。</param>
        /// <exception cref="InvalidOperationException">源表推导的池容量不满足实际并发。</exception>
        private void SpawnPatternProjectile(float2 origin, float2 direction, in CombatWeaponState weapon,
            in AttackSnapshot attack, ref CombatCounters counters)
        {
            for (int index = 0; index < Projectiles.Length; index++)
            {
                if (ProjectileData[Projectiles[index]].Active) continue;
                ProjectileData[Projectiles[index]] = new CombatProjectile
                {
                    Active = true,
                    BornThisTick = true,
                    Attack = attack,
                    Position = origin,
                    Velocity = direction * weapon.ProjectileSpeed,
                    SkillId = weapon.SkillId,
                    CollisionOffset = new float2(direction.y, -direction.x) * weapon.ProjectileOffset.x +
                        direction * weapon.ProjectileOffset.y,
                    Remaining = weapon.ProjectileLifetime,
                    Radius = weapon.ProjectileRadius,
                    LocalCollisionOffset = weapon.ProjectileOffset,
                    PierceRemaining = weapon.PierceCount,
                    ChainRemaining = weapon.ChainCount,
                    ChainRange = weapon.ChainRange,
                    ImpactRadius = weapon.ImpactRadius,
                    ImpactVisualRadiusMultiplier = weapon.ImpactVisualRadiusMultiplier
                };
                counters.ProjectileSpawns++;
                counters.ActiveProjectiles++;
                counters.PeakProjectiles = math.max(counters.PeakProjectiles, counters.ActiveProjectiles);
                return;
            }
            throw new InvalidOperationException("Combat projectile pool capacity invariant failed.");
        }

        /// <summary>在固定或随身中心生成一次范围脉冲；每个敌方生命周期最多一个本脉冲请求。</summary>
        /// <param name="center">本次脉冲的逻辑中心。</param>
        /// <param name="weapon">TbSkill.areaRadiusPixels 入局换算后的范围技能。</param>
        /// <param name="attack">所有受击者共享的单次释放快照。</param>
        /// <param name="ownerLifetime">施法者生命周期，供随身表现拒绝旧代次。</param>
        /// <param name="counters">范围释放统计。</param>
        /// <remarks>目标随后死亡或池槽复用时由请求中的 TargetLifetime 拒绝陈旧结算。</remarks>
        private void CastArea(float2 center, in CombatWeaponState weapon,
            in AttackSnapshot attack, ulong ownerLifetime, ref CombatCounters counters)
        {
            for (int slot = 1; slot < Units.Length; slot++)
            {
                CombatUnit candidate = UnitStates[slot];
                if (candidate.Target.Health <= 0 || candidate.Target.Faction == attack.Faction) continue;
                if (!CombatGeometry.WithinCapsuleReach(candidate.Position + candidate.BodyOffset - center, candidate.BodyHalfSegment,
                    weapon.AreaRadius + candidate.Radius)) continue;
                if (weapon.RingCount > 0 && !TouchesRing(candidate, center, weapon)) continue;
                Requests.Add(new CombatDamageRequest
                {
                    Attack = attack,
                    TargetSlot = slot,
                    TargetLifetime = candidate.Target.Lifetime
                });
            }
            int layers = weapon.RingCount > 0 ? weapon.RingCount : 1;
            for (int layer = 1; layer <= layers; layer++) Areas.Add(new CombatAreaEvent
            {
                SkillId = weapon.SkillId,
                Position = center,
                Radius = weapon.AreaRadius * layer / layers,
                AttackSequence = layers == 1 ? attack.Sequence : ++counters.NextAttack,
                Tick = counters.Tick + 1,
                FollowsOwner = weapon.FollowsOwner, OwnerLifetime = ownerLifetime
            });
            counters.AreaCasts++;
        }

        /// <summary>固定时钟推进持续区域；逐个补齐到期脉冲，拥有者死亡或换代立即释放区域。</summary>
        /// <param name="counters">每次脉冲的唯一攻击序号和范围统计。</param>
        /// <remarks>区域快照由会话原生列表拥有；逆序移除避免漏项，重开与 Dispose 由会话统一清空。</remarks>
        private void PulseAreas(ref CombatCounters counters)
        {
            CombatUnit owner = UnitStates[0];
            for (int index = PersistentAreas.Length - 1; index >= 0; index--)
            {
                CombatPersistentArea area = PersistentAreas[index];
                if (owner.Target.Health <= 0 || owner.Target.Lifetime != area.OwnerLifetime)
                { PersistentAreas.RemoveAt(index); continue; }
                if (area.Weapon.FollowsOwner) area.Position = owner.Position + owner.BodyOffset;
                area.Age += Delta;
                // 次数上界由整数毫秒推导，避免浮点累加把寿命终点错误算成额外一次脉冲。
                while (area.PulseIndex < area.Weapon.PulseCount && area.PulseIndex * area.Weapon.PulseInterval <= area.Age)
                {
                    AttackSnapshot pulse = area.Attack;
                    pulse.Sequence = ++counters.NextAttack;
                    bool hasCenter = true;
                    if (area.Weapon.Retargets)
                    {
                        int targetSlot = FindAreaTarget(area, owner);
                        hasCenter = targetSlot >= 0;
                        if (hasCenter)
                        {
                            CombatUnit target = UnitStates[targetSlot];
                            area.Position = target.Position + target.BodyOffset;
                            area.TargetLifetimes.Add(target.Target.Lifetime);
                        }
                    }
                    if (hasCenter) CastArea(area.Position, area.Weapon, pulse, area.OwnerLifetime, ref counters);
                    area.PulseIndex++;
                }
                if (area.Age >= area.Weapon.AreaDuration) PersistentAreas.RemoveAt(index);
                else PersistentAreas[index] = area;
            }
        }

        /// <summary>连续落点按施法者的技能范围选择未使用目标，距离相同时按生命周期稳定排序。</summary>
        /// <param name="area">TbSkillCombat.areaRetargets 对应的区域及已用目标记录。</param>
        /// <param name="owner">本代次仍存活的施法者。</param>
        /// <returns>下一落点目标槽，没有未使用目标时返回 -1。</returns>
        private int FindAreaTarget(in CombatPersistentArea area, in CombatUnit owner)
        {
            int selected = -1;
            float nearest = float.PositiveInfinity;
            ulong lifetime = ulong.MaxValue;
            for (int slot = 1; slot < Units.Length; slot++)
            {
                CombatUnit target = UnitStates[slot];
                if (target.Target.Health <= 0 || target.Target.Faction == area.Attack.Faction) continue;
                bool used = false;
                for (int index = 0; index < area.TargetLifetimes.Length; index++)
                    if (area.TargetLifetimes[index] == target.Target.Lifetime) { used = true; break; }
                float2 offset = target.Position + target.BodyOffset - owner.Position - owner.BodyOffset;
                if (used || !CombatGeometry.WithinCapsuleReach(offset, target.BodyHalfSegment + owner.BodyHalfSegment,
                    area.Weapon.Range + target.Radius + owner.Radius)) continue;
                float distance = math.lengthsq(offset);
                if (distance < nearest || (distance == nearest && target.Target.Lifetime < lifetime))
                { selected = slot; nearest = distance; lifetime = target.Target.Lifetime; }
            }
            return selected;
        }

        /// <summary>圆柱受击胶囊与任一表驱动同心环带相交时命中；同一脉冲不按环数重复伤害。</summary>
        /// <param name="target">目标身体半径与纵向中轴。</param>
        /// <param name="center">本次随身区域中心。</param>
        /// <param name="weapon">TbSkillCombat.areaRingCount/areaRingWidthPixels 转换的环带。</param>
        /// <returns>目标身体触及至少一个环带时为真。</returns>
        private static bool TouchesRing(in CombatUnit target, float2 center, in CombatWeaponState weapon)
        {
            float2 offset = target.Position + target.BodyOffset - center;
            float nearest = math.max(0, math.sqrt(CombatGeometry.VerticalSegmentDistanceSquared(offset, target.BodyHalfSegment)) - target.Radius);
            float farthest = math.max(math.length(offset + new float2(0, target.BodyHalfSegment)),
                math.length(offset - new float2(0, target.BodyHalfSegment))) + target.Radius;
            for (int ring = 1; ring <= weapon.RingCount; ring++)
            {
                float outer = weapon.AreaRadius * ring / weapon.RingCount;
                if (nearest <= outer && farthest >= outer - weapon.RingWidth) return true;
            }
            return false;
        }

        /// <summary>校验缓存目标的生命、代际与范围；近战采用圆边缘距离。</summary>
        /// <param name="unit">持有目标缓存的单位。</param>
        /// <returns>缓存当前是否可用于发起攻击。</returns>
        private bool TargetValid(in CombatUnit unit)
        {
            if (unit.TargetSlot < 0 || unit.TargetSlot >= Units.Length) return false;
            var target = UnitStates[unit.TargetSlot];
            float reach = unit.Range + unit.Radius + target.Radius;
            return target.Target.Health > 0 && target.Target.Lifetime == unit.TargetLifetime &&
                target.Target.Faction != unit.Target.Faction && CombatGeometry.WithinCapsuleReach(
                    (target.Position - unit.Position) + (target.BodyOffset - unit.BodyOffset),
                    unit.BodyHalfSegment + target.BodyHalfSegment, reach);
        }

        /// <summary>先推进已有近战动作；冷却就绪且目标合法时起手并锁定，弹丸仍复用空槽立即发射。</summary>
        /// <param name="slot">本局固定单位槽位。</param>
        /// <param name="counters">攻击与池统计。</param>
        /// <remarks>只生成请求或弹丸，不改变目标生命；兼容玩家入口在成功发射的同 tick 写入随身施法事件，不引入前摇。</remarks>
        private void CollectAttack(int slot, ref CombatCounters counters)
        {
            var unit = UnitStates[slot];
            if (unit.Target.Health <= 0) return;
            unit.Cooldown = math.max(0, unit.Cooldown - Delta);
            if (unit.AttackActive)
            {
                unit.AttackAge += Delta * unit.BaseInterval / unit.Interval;
                JudgeMelee(ref unit, ref counters);
                if (unit.AttackAge >= unit.WindupSeconds + unit.AttackOption.DurationSeconds)
                    unit.AttackActive = false;
                UnitStates[slot] = unit;
                return;
            }
            bool valid = TargetValid(unit);
            if (!valid || Time >= unit.NextTargetRefresh)
            {
                int targetSlot = slot == 0 ? FindNearest(unit, unit.Range) : 0;
                unit.TargetSlot = targetSlot;
                unit.TargetLifetime = targetSlot < 0 ? 0 : UnitStates[targetSlot].Target.Lifetime;
                unit.NextTargetRefresh = Time + TargetRefresh;
                valid = TargetValid(unit);
            }
            if (unit.Cooldown <= 0 && valid)
            {
                var attack = unit.Attack;
                attack.Sequence = ++counters.NextAttack;
                attack.AttackerLifetime = unit.Target.Lifetime;
                attack.SkillId = unit.SkillId;
                attack.Source = slot == 0 ? CombatDamageSource.Weapon : CombatDamageSource.Enemy;
                if (unit.Delivery == ESkillDeliveryType.Melee)
                {
                    unit.PendingAttack = attack;
                    unit.LockedTargetSlot = unit.TargetSlot;
                    unit.LockedTargetLifetime = unit.TargetLifetime;
                    unit.AttackActive = true;
                    unit.AttackHitProcessed = false;
                    unit.AttackAge = 0;
                    CombatUnit locked = UnitStates[unit.TargetSlot];
                    float2 offset = (locked.Position - unit.Position) + (locked.BodyOffset - unit.BodyOffset);
                    if (math.lengthsq(offset) == 0) offset = unit.Facing;
                    double best = double.NegativeInfinity;
                    for (int i = 0; i < unit.AttackOptionCount; i++)
                    {
                        var option = AttackOptions[unit.AttackOptionStart + i];
                        double dot = (double)offset.x * option.X + (double)offset.y * option.Y;
                        if (dot > best) { best = dot; unit.AttackOption = option; }
                    }
                    unit.Facing = new float2(unit.AttackOption.X, unit.AttackOption.Y);
                    // 支持表内零前摇/首帧命中，但不额外添加旧的即时伤害请求。
                    JudgeMelee(ref unit, ref counters);
                }
                else
                {
                    bool spawned = false;
                    for (int index = 0; index < Projectiles.Length; index++)
                    {
                        if (ProjectileData[Projectiles[index]].Active) continue;
                        var target = UnitStates[unit.TargetSlot];
                        // 初始重叠时沿用配置初始化的单位朝向，使局部碰撞偏移仍有确定方向。
                        float2 direction = math.normalizesafe(target.Position + target.BodyOffset - unit.Position, math.normalizesafe(unit.Facing));
                        ProjectileData[Projectiles[index]] = new CombatProjectile { Active = true, BornThisTick = true, Attack = attack,
                            Position = unit.Position, Velocity = direction * unit.ProjectileSpeed, SkillId = unit.SkillId,
                            CollisionOffset = new float2(direction.y, -direction.x) * unit.ProjectileOffset.x + direction * unit.ProjectileOffset.y,
                            Remaining = unit.ProjectileLifetime, Radius = unit.ProjectileRadius };
                        counters.ProjectileSpawns++;
                        counters.ActiveProjectiles++;
                        counters.PeakProjectiles = math.max(counters.PeakProjectiles, counters.ActiveProjectiles);
                        spawned = true;
                        break;
                    }
                    if (!spawned) throw new InvalidOperationException("Combat projectile pool capacity invariant failed.");
                    if (slot == 0) Casts.Add(new CombatCastEvent { SkillId = unit.SkillId, CasterSlot = slot,
                        CasterLifetime = unit.Target.Lifetime, AttackSequence = attack.Sequence, Tick = counters.Tick + 1 });
                }
                counters.Attacks++;
                unit.Attack = attack;
                unit.Cooldown = unit.Interval;
            }
            UnitStates[slot] = unit;
        }

        /// <summary>原速进度进入配置前摇之后的 F 命中帧时，仅判定一次；离开范围或生命周期失效均挥空。</summary>
        /// <param name="unit">本次攻击已锁定的状态；命中帧来自 TbAttackDirection，前摇来自 TbSkillCombat。</param>
        /// <param name="counters">几何判定与挥空计数，区别于概率闪避。</param>
        /// <remarks>生成统一伤害请求，不直接扣血；已判定标志在入队前设置，补算不能重复触发。</remarks>
        private void JudgeMelee(ref CombatUnit unit, ref CombatCounters counters)
        {
            if (unit.AttackHitProcessed || unit.AttackAge < unit.WindupSeconds + unit.AttackOption.HitSeconds) return;
            unit.AttackHitProcessed = true;
            counters.MeleeJudgements++;
            if (unit.LockedTargetSlot < 0 || unit.LockedTargetSlot >= Units.Length)
            { counters.MeleeWhiffs++; return; }
            var target = UnitStates[unit.LockedTargetSlot];
            float reach = unit.Range + unit.Radius + target.Radius;
            if (target.Target.Health <= 0 || target.Target.Lifetime != unit.LockedTargetLifetime ||
                target.Target.Faction == unit.Target.Faction || !CombatGeometry.WithinCapsuleReach(
                    (target.Position - unit.Position) + (target.BodyOffset - unit.BodyOffset),
                    unit.BodyHalfSegment + target.BodyHalfSegment, reach))
            { counters.MeleeWhiffs++; return; }
            Requests.Add(new CombatDamageRequest { Attack = unit.PendingAttack, TargetSlot = unit.LockedTargetSlot,
                TargetLifetime = unit.LockedTargetLifetime });
        }

        /// <summary>相对运动扫掠依次命中未访问生命周期；按表消耗穿透或连锁预算，拒绝池槽复用旧命中。</summary>
        /// <param name="counters">候选与活动池统计。</param>
        /// <remarks>碰撞中心使用 TbSkill 导出的局部偏移，在发射时按方向旋转；表现原点保持不变。首次接触事件同时保存目标槽位、生命周期及 TbCharacter/TbMonster 受击挂点位置，供表现跟随同一目标。生成伤害请求但不立即扣血，确保同 tick 请求按统一顺序结算。</remarks>
        private void SweepProjectiles(ref CombatCounters counters)
        {
            for (int index = 0; index < Projectiles.Length; index++)
            {
                var projectile = ProjectileData[Projectiles[index]];
                if (!projectile.Active) continue;
                bool bornThisTick = projectile.BornThisTick;
                float travelTime = (float)math.min(Delta, projectile.Remaining);
                float2 start = projectile.Position + projectile.CollisionOffset;
                float2 end = start + projectile.Velocity * travelTime;
                float padding = projectile.Radius + MaximumRadius + MaximumMotion;
                int2 lower = (int2)math.floor((math.min(start, end) - padding) / CellSize);
                int2 upper = (int2)math.floor((math.max(start, end) + padding) / CellSize);
                int selected = -1;
                float first = float.PositiveInfinity;
                ulong lifetime = ulong.MaxValue;
                bool continuePiercing;
                bool redirected = false;
                do
                {
                    continuePiercing = false;
                    selected = -1; first = float.PositiveInfinity; lifetime = ulong.MaxValue;
                    for (int y = lower.y; y <= upper.y; y++)
                        for (int x = lower.x; x <= upper.x; x++)
                        {
                            if (!Grid.TryGetFirstValue(new int2(x, y), out int slot, out var iterator)) continue;
                            do
                            {
                                var target = UnitStates[slot];
                                if (target.Target.Health <= 0 || target.Target.Faction == projectile.Attack.Faction ||
                                    HasHit(projectile, target.Target.Lifetime)) continue;
                                counters.Candidates++;
                                float2 targetStart = (bornThisTick ? target.Position : target.PreviousPosition) + target.BodyOffset;
                                float2 targetEnd = math.lerp(targetStart, target.Position + target.BodyOffset, travelTime / Delta);
                                if (CombatGeometry.SweepVerticalCapsule(start - targetStart,
                                    (end - targetEnd) - (start - targetStart),
                                    projectile.Radius + target.Radius, target.BodyHalfSegment,
                                    out float fraction, out _) &&
                                    (fraction < first || (fraction == first && target.Target.Lifetime < lifetime)))
                                { selected = slot; first = fraction; lifetime = target.Target.Lifetime; }
                            } while (Grid.TryGetNextValue(out slot, ref iterator));
                        }
                    if (selected >= 0)
                    {
                        projectile.Position = math.lerp(start, end, first) - projectile.CollisionOffset;
                        // 非正式压测弹丸没有多目标预算，不需要在内联表中保存生命周期。
                        if (projectile.PierceRemaining > 0 || projectile.ChainRemaining > 0 || projectile.HitLifetimes.Length > 0)
                            projectile.HitLifetimes.Add(lifetime);
                        Requests.Add(new CombatDamageRequest { Attack = projectile.Attack, TargetSlot = selected, TargetLifetime = lifetime });
                        CombatUnit target = UnitStates[selected];
                        float2 targetStart = bornThisTick ? target.Position : target.PreviousPosition;
                        float2 targetEnd = math.lerp(targetStart, target.Position, travelTime / Delta);
                        float2 hitEffectPosition = math.lerp(targetStart, targetEnd, first) + target.HitEffectOffset;
                        Impacts.Add(new CombatImpactEvent { SkillId = projectile.SkillId, TargetSlot = selected, Position = hitEffectPosition,
                            AttackSequence = projectile.Attack.Sequence, Tick = counters.Tick + 1, TargetLifetime = lifetime,
                            RadiusMultiplier = projectile.ImpactVisualRadiusMultiplier });
                        if (projectile.ImpactRadius > 0) SplashProjectile(projectile, selected, target.Position + target.BodyOffset, ref counters);
                        if (projectile.PierceRemaining > 0)
                        {
                            projectile.PierceRemaining--;
                            continuePiercing = true;
                        }
                        else if (projectile.ChainRemaining > 0)
                        {
                            projectile.ChainRemaining--;
                            int next = FindChainTarget(projectile);
                            if (next >= 0)
                            {
                                CombatUnit destination = UnitStates[next];
                                float2 direction = math.normalizesafe(destination.Position + destination.BodyOffset - projectile.Position,
                                    math.normalizesafe(projectile.Velocity));
                                projectile.Velocity = direction * math.length(projectile.Velocity);
                                projectile.CollisionOffset = new float2(direction.y, -direction.x) * projectile.LocalCollisionOffset.x +
                                    direction * projectile.LocalCollisionOffset.y;
                                // 转向后的剩余运动从下一固定 tick 开始；本 tick 的命中顺序不会依赖池槽扫描顺序。
                                selected = -1;
                                redirected = true;
                                break;
                            }
                        }
                    }
                } while (continuePiercing);
                if (selected < 0 && !redirected)
                {
                    projectile.Position = end - projectile.CollisionOffset;
                }
                projectile.BornThisTick = false;
                projectile.Age += travelTime;
                projectile.Remaining -= travelTime;
                if (selected >= 0 || projectile.Remaining <= 0)
                { projectile.Active = false; counters.ActiveProjectiles--; }
                ProjectileData[Projectiles[index]] = projectile;
            }
        }

        /// <summary>查找本弹丸已命中的目标生命周期；生命周期不同的复用槽允许重新成为目标。</summary>
        /// <param name="projectile">持有内联命中记录的弹丸。</param>
        /// <param name="lifetime">当前目标生命周期。</param>
        /// <returns>本次弹丸此前已经接触目标时为真。</returns>
        private static bool HasHit(in CombatProjectile projectile, ulong lifetime)
        {
            for (int index = 0; index < projectile.HitLifetimes.Length; index++)
                if (projectile.HitLifetimes[index] == lifetime) return true;
            return false;
        }

        /// <summary>命中后在 TbSkillCombat.chainRangePixels 内寻找最近未访问敌方，距离相同按生命周期排序。</summary>
        /// <param name="projectile">当前接触点、阵营、表内跳转半径及命中记录。</param>
        /// <returns>合法下一目标槽；没有候选时返回 -1。</returns>
        private int FindChainTarget(in CombatProjectile projectile)
        {
            int selected = -1;
            float nearest = float.PositiveInfinity;
            ulong lifetime = ulong.MaxValue;
            for (int slot = 1; slot < Units.Length; slot++)
            {
                CombatUnit target = UnitStates[slot];
                if (target.Target.Health <= 0 || target.Target.Faction == projectile.Attack.Faction || HasHit(projectile, target.Target.Lifetime)) continue;
                float2 offset = target.Position + target.BodyOffset - projectile.Position;
                if (!CombatGeometry.WithinCapsuleReach(offset, target.BodyHalfSegment, projectile.ChainRange + target.Radius)) continue;
                float distance = math.lengthsq(offset);
                if (distance < nearest || (distance == nearest && target.Target.Lifetime < lifetime))
                { selected = slot; nearest = distance; lifetime = target.Target.Lifetime; }
            }
            return selected;
        }

        /// <summary>命中处爆发按 TbSkillCombat.impactRadiusPixels 对其他敌方各投递一次，主目标保留原直击请求。</summary>
        /// <param name="projectile">本次弹丸的伤害、阵营和爆发范围快照。</param>
        /// <param name="primarySlot">已经接收直击请求的主目标。</param>
        /// <param name="center">命中目标受击中心。</param>
        /// <param name="counters">实际触发的爆发次数。</param>
        /// <remarks>爆发与穿透/连锁在源表互斥；该弹丸此后回池，不会再次扫掠或重复爆发。</remarks>
        private void SplashProjectile(in CombatProjectile projectile, int primarySlot, float2 center, ref CombatCounters counters)
        {
            for (int slot = 1; slot < Units.Length; slot++)
            {
                CombatUnit target = UnitStates[slot];
                if (slot == primarySlot || target.Target.Health <= 0 || target.Target.Faction == projectile.Attack.Faction ||
                    !CombatGeometry.WithinCapsuleReach(target.Position + target.BodyOffset - center, target.BodyHalfSegment,
                        projectile.ImpactRadius + target.Radius)) continue;
                Requests.Add(new CombatDamageRequest { Attack = projectile.Attack, TargetSlot = slot, TargetLifetime = target.Target.Lifetime });
            }
            counters.SplashCasts++;
        }

        /// <summary>计算单位移动形状在战斗 XY 平面的轴对齐半尺寸。</summary>
        /// <param name="movement">单位的纯值移动形状。</param>
        /// <returns>X 为半径，Y 为胶囊半中轴加半径。</returns>
        private static float2 MovementExtents(in CombatCylinder movement)
        {
            return new float2(movement.Radius, movement.Radius + movement.HalfSegment2D);
        }

        /// <summary>调用统一伤害核心；测试恢复策略仍先执行真实扣血及受击无敌。</summary>
        /// <param name="request">已排序的几何接触请求。</param>
        /// <param name="counters">分类战斗统计。</param>
        /// <remarks>更新生命；恢复策略仅作用存活玩家，不隐式复活。</remarks>
        private void ApplyDamage(in CombatDamageRequest request, ref CombatCounters counters)
        {
            var unit = UnitStates[request.TargetSlot];
            var result = CombatDamage.Resolve(request.Attack, ref unit.Target, request.TargetLifetime, true, Time, Seed, Rules);
            switch (result.Outcome)
            {
                case DamageOutcome.Hit: counters.Hits++; break;
                case DamageOutcome.Critical: counters.Hits++; counters.Criticals++; break;
                case DamageOutcome.Evaded: counters.Evades++; break;
                case DamageOutcome.Invulnerable: counters.Invulnerable++; break;
                case DamageOutcome.Immune: counters.Immunities++; break;
            }
            if (result.HealthLost > 0)
            {
                counters.DamageEvents++;
                counters.HealthLost += result.HealthLost;
                if (RecordLifeDamage) LifeDamage.Add(new CombatLifeDamageEvent { EventId = (ulong)counters.DamageEvents,
                    RootCastId = request.Attack.Sequence, OwnerId = request.Attack.AttackerLifetime,
                    TargetId = request.TargetLifetime, Tick = counters.Tick + 1, SkillId = request.Attack.SkillId,
                    HealthLost = result.HealthLost, RemainingHealth = unit.Target.Health, Source = request.Attack.Source, PlayerTarget = unit.Target.IsPlayer,
                    RewardEligible = !request.Attack.SuppressMechanismRewards });
                if (RestorePlayer && unit.Target.IsPlayer) unit.Target.Heal(unit.Target.MaxHealth);
            }
            if (result.Killed)
            {
                counters.Deaths++;
                Deaths.Add(new CombatDeathEvent
                {
                    Slot = request.TargetSlot,
                    ConfigId = unit.ConfigId,
                    VisualSetId = unit.VisualSetId,
                    Position = unit.Position,
                    Lifetime = unit.Target.Lifetime,
                    Tick = counters.Tick + 1,
                    IsPlayer = unit.Target.IsPlayer,
                    IsBoss = unit.IsBoss
                });
            }
            UnitStates[request.TargetSlot] = unit;
        }

        /// <summary>请求全部结算后复用死亡怪物槽，并为新生命周期恢复完整模板；同步位置供后续表现读取。</summary>
        /// <param name="counters">死亡、补怪、存活数和生命周期计数。</param>
        /// <remarks>不销毁 ECS 实体；失活单位变换缩放归零，退出时由会话统一释放。</remarks>
        private void RecycleAndSynchronize(ref CombatCounters counters)
        {
            counters.AliveMonsters = 0;
            counters.PlayerDead = UnitStates[0].Target.Health <= 0;
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitStates[slot];
                if (unit.Target.Health <= 0)
                {
                    unit.AttackActive = false;
                    unit.AttackHitProcessed = false;
                    unit.AttackAge = 0;
                    unit.PendingAttack = default;
                }
                if (slot != 0 && unit.Target.Health <= 0 && Replenish && !counters.PlayerDead)
                {
                    unit = Templates[slot];
                    unit.Target.Lifetime = ++counters.NextLifetime;
                    unit.Attack.AttackerLifetime = unit.Target.Lifetime;
                    UnitStates[slot] = unit;
                    counters.Replenished++;
                }
                if (slot != 0 && unit.Target.Health > 0) counters.AliveMonsters++;
                UnitStates[slot] = unit;
                var transform = Transforms[Units[slot]];
                transform.Position = new float3(unit.Position, 0);
                transform.Scale = unit.Target.Health > 0 ? 1 : 0;
                Transforms[Units[slot]] = transform;
            }
            for (int index = 0; index < Projectiles.Length; index++)
            {
                var projectile = ProjectileData[Projectiles[index]];
                var transform = Transforms[Projectiles[index]];
                transform.Position = new float3(projectile.Position, 0);
                transform.Scale = projectile.Active ? 1 : 0;
                Transforms[Projectiles[index]] = transform;
            }
        }
    }
}
