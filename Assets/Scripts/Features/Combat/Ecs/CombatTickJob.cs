using System;
using cfg;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>单个固定 tick 的 Burst 调度；全部输入为纯值与原生容器，不访问托管表或资源服务。</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct CombatTickJob : IJob
    {
        [ReadOnly] public NativeArray<Entity> Units;
        [ReadOnly] public NativeArray<Entity> Projectiles;
        [ReadOnly] public NativeArray<CombatUnit> Templates;
        [ReadOnly] public NativeArray<CombatAttackOption> AttackOptions;
        public NativeArray<CombatWeaponState> PlayerWeapons;
        public ComponentLookup<CombatUnit> UnitData;
        public ComponentLookup<CombatProjectile> ProjectileData;
        public ComponentLookup<LocalTransform> Transforms;
        public NativeParallelMultiHashMap<int2, int> Grid;
        public NativeList<CombatDamageRequest> Requests;
        public NativeList<CombatImpactEvent> Impacts;
        public NativeList<CombatAreaEvent> Areas;
        public NativeList<CombatDeathEvent> Deaths;
        public NativeArray<CombatCounters> Counters;
        public DamageRules Rules;
        public float2 Input, Arena;
        public float Delta, CellSize, MaximumRadius, MaximumMotion;
        public double Time, TargetRefresh;
        public ulong Seed;
        public bool Replenish, RestorePlayer, AllowMonstersOutsideArena, UsePlayerWeapons;

        /// <summary>由会话固定步进调用：移动、建网格、收集攻击、扫掠、排序结算、回收及变换同步。</summary>
        /// <remarks>同步修改会话拥有的 ECS 组件与原生容器；调用方必须等待任务完成后再读或释放。</remarks>
        public void Execute()
        {
            var counters = Counters[0];
            if (counters.PlayerDead) return;
            bool burst = true;
            MarkManagedExecution(ref burst);
            counters.UsedBurst = burst;
            Requests.Clear();
            Grid.Clear();
            MoveAndIndex();
            // 全部攻击先生成，伤害最后统一结算，允许同 tick 双方互杀。
            if (UsePlayerWeapons) CollectPlayerWeapons(ref counters);
            else CollectAttack(0, ref counters);
            for (int slot = 1; slot < Units.Length; slot++)
                CollectAttack(slot, ref counters);
            SweepProjectiles(ref counters);
            Requests.AsArray().Sort();
            for (int index = 0; index < Requests.Length; index++)
                ApplyDamage(Requests[index], ref counters);
            RecycleAndSynchronize(ref counters);
            counters.Tick++;
            Counters[0] = counters;
        }

        /// <summary>托管回退时清除执行标记；Burst 编译器丢弃此调用，以便验收实际执行路径。</summary>
        /// <param name="burst">原本为真的原生执行标记。</param>
        [BurstDiscard]
        private static void MarkManagedExecution(ref bool burst) { burst = false; }

        /// <summary>先建立空间索引，再按槽位扫掠圆柱并沿接触边缘滑动；每次移动更新网格。</summary>
        /// <remarks>玩家和怪物均被其他活单位阻挡，不施加推力；攻击前摇中的怪物维持位置和朝向。</remarks>
        private void MoveAndIndex()
        {
            float footprint = 0;
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitData[Units[slot]];
                unit.PreviousPosition = unit.Position; UnitData[Units[slot]] = unit;
                if (unit.Target.Health <= 0) continue;
                footprint = math.max(footprint, unit.Movement.Radius + math.length(unit.Movement.Offset));
                Grid.Add((int2)math.floor(unit.Position / CellSize), slot);
            }
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitData[Units[slot]];
                if (unit.Target.Health <= 0) continue;
                var player = UnitData[Units[0]];
                float2 motion = Input * unit.MoveSpeed * Delta;
                if (slot != 0)
                {
                    motion = float2.zero;
                    float2 offset = player.Position - unit.Position;
                    float distance = math.length(offset);
                    float stop = unit.Range + unit.Radius + player.Radius;
                    if (!unit.AttackActive && distance > stop)
                        motion = offset / distance * math.min(unit.MoveSpeed * Delta, distance - stop);
                    if (!unit.AttackActive && distance > 0) unit.Facing = offset / distance;
                }
                // 先将期望终点限制在场地内，再扫掠，避免碰撞之后夹边界把单位夹进别人身体。
                if (slot == 0 || !AllowMonstersOutsideArena)
                    motion = math.clamp(unit.Position + motion + unit.Movement.Offset,
                        -Arena + unit.Movement.Radius, Arena - unit.Movement.Radius) - unit.Movement.Offset - unit.Position;
                Grid.Remove((int2)math.floor(unit.Position / CellSize), slot);
                unit.Position = MoveCylinder(slot, unit, motion, footprint);
                UnitData[Units[slot]] = unit;
                Grid.Add((int2)math.floor(unit.Position / CellSize), slot);
            }
        }

        /// <summary>在扫掠包围盒的网格候选中选择最早接触，裁掉法向位移并重新扫掠切向余量。</summary>
        /// <param name="slot">移动单位槽。</param><param name="unit">移动前组件。</param>
        /// <param name="motion">已限制场地的期望位移。</param><param name="footprint">本 tick 最大圆柱半径加中心偏移。</param>
        /// <returns>不会穿过已有圆柱的终点。</returns>
        private float2 MoveCylinder(int slot, in CombatUnit unit, float2 motion, float footprint)
        {
            float2 position = unit.Position;
            // 接触求解次数由候选单位数界定；这是算法终止上界，不是策划移动参数。
            for (int contact = 0; contact <= Units.Length && math.lengthsq(motion) > 0; contact++)
            {
                if (slot == 0 || !AllowMonstersOutsideArena)
                    motion = math.clamp(position + motion + unit.Movement.Offset, -Arena + unit.Movement.Radius,
                        Arena - unit.Movement.Radius) - unit.Movement.Offset - position;
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
                            var other = UnitData[Units[otherSlot]];
                            if (other.Target.Health <= 0 || !unit.Movement.HeightOverlaps(other.Movement)) continue;
                            float2 relative = center - other.Position - other.Movement.Offset;
                            float radius = unit.Movement.Radius + other.Movement.Radius;
                            if (!CombatCylinder.Sweep(relative, motion, radius, out float hit)) continue;
                            if (hit < fraction || (hit == fraction && (selected < 0 || otherSlot < selected)))
                            { selected = otherSlot; fraction = hit; normal = math.normalizesafe(relative + motion * hit); }
                        } while (Grid.TryGetNextValue(out otherSlot, ref iterator));
                    }
                if (selected < 0) return position + motion;
                // 接触比例退一个 float ULP，抵消乘加舍入造成的数值重叠，不增加配置间隙。
                float safe = fraction > 0 ? math.asfloat(math.asint(fraction) - 1) : 0;
                position += motion * safe;
                float2 remainder = motion * (1 - fraction);
                float2 slide = remainder - normal * math.min(0, math.dot(remainder, normal));
                if (math.lengthsq(slide) >= math.lengthsq(motion)) break;
                motion = slide;
            }
            return position;
        }

        /// <summary>对范围内网格候选做精确距离与阵营筛选；等距使用生成序号。</summary>
        /// <param name="unit">寻敌单位。</param>
        /// <returns>最近合法目标的池槽，找不到返回无效索引。</returns>
        private int FindNearest(in CombatUnit unit, float configuredRange)
        {
            float search = configuredRange + unit.Radius + MaximumRadius;
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
                        var target = UnitData[Units[slot]];
                        if (target.Target.Health <= 0 || target.Target.Faction == unit.Target.Faction) continue;
                        float distance = math.lengthsq(target.Position - unit.Position);
                        float range = configuredRange + unit.Radius + target.Radius;
                        if (!CombatGeometry.WithinReach(target.Position - unit.Position, range)) continue;
                        if (distance < best || (distance == best && target.Target.Lifetime < lifetime))
                        { selected = slot; best = distance; lifetime = target.Target.Lifetime; }
                    } while (Grid.TryGetNextValue(out slot, ref iterator));
                }
            return selected;
        }

        /// <summary>正式玩家逐把推进独立冷却；Projectile 发射池化弹丸，TargetArea 在锁定位置生成一次范围请求。</summary>
        /// <param name="counters">本局攻击、弹丸、范围释放和唯一序号计数。</param>
        /// <remarks>全部武器共享当前玩家属性快照，但冷却、投递参数和表现 ID 独立读取各自 TbSkill。</remarks>
        private void CollectPlayerWeapons(ref CombatCounters counters)
        {
            CombatUnit player = UnitData[Units[0]];
            if (player.Target.Health <= 0) return;
            for (int index = 0; index < PlayerWeapons.Length; index++)
            {
                CombatWeaponState weapon = PlayerWeapons[index];
                if (!weapon.Active) continue;
                weapon.Cooldown = math.max(0, weapon.Cooldown - Delta);
                int targetSlot = FindNearest(player, weapon.Range);
                if (weapon.Cooldown <= 0 && targetSlot >= 0)
                {
                    CombatUnit target = UnitData[Units[targetSlot]];
                    AttackSnapshot attack = weapon.Attack;
                    attack.Sequence = ++counters.NextAttack;
                    attack.AttackerLifetime = player.Target.Lifetime;
                    if (weapon.Delivery == ESkillDeliveryType.Projectile)
                        SpawnPlayerProjectile(player, target, weapon, attack, ref counters);
                    else
                        CastTargetArea(player, target, weapon, attack, ref counters);
                    weapon.Attack = attack;
                    weapon.Cooldown = weapon.Interval;
                    counters.Attacks++;
                }
                PlayerWeapons[index] = weapon;
            }
        }

        /// <summary>从共享实体池取得一个空槽，按当前技能参数和锁定方向创建弹丸。</summary>
        /// <param name="player">发射时玩家位置和朝向。</param>
        /// <param name="target">锁定时目标位置。</param>
        /// <param name="weapon">TbSkill 转换的弹丸参数。</param>
        /// <param name="attack">本次唯一攻击快照。</param>
        /// <param name="counters">弹丸并发与生成统计。</param>
        /// <exception cref="InvalidOperationException">表驱动并发容量计算不足。</exception>
        private void SpawnPlayerProjectile(in CombatUnit player, in CombatUnit target, in CombatWeaponState weapon,
            in AttackSnapshot attack, ref CombatCounters counters)
        {
            for (int index = 0; index < Projectiles.Length; index++)
            {
                if (ProjectileData[Projectiles[index]].Active) continue;
                float2 direction = math.normalizesafe(target.Position - player.Position,
                    math.normalizesafe(player.Facing));
                ProjectileData[Projectiles[index]] = new CombatProjectile
                {
                    Active = true,
                    BornThisTick = true,
                    Attack = attack,
                    Position = player.Position,
                    Velocity = direction * weapon.ProjectileSpeed,
                    SkillId = weapon.SkillId,
                    CollisionOffset = new float2(direction.y, -direction.x) * weapon.ProjectileOffset.x +
                        direction * weapon.ProjectileOffset.y,
                    Remaining = weapon.ProjectileLifetime,
                    Radius = weapon.ProjectileRadius
                };
                counters.ProjectileSpawns++;
                counters.ActiveProjectiles++;
                counters.PeakProjectiles = math.max(counters.PeakProjectiles, counters.ActiveProjectiles);
                return;
            }
            throw new InvalidOperationException("Combat projectile pool capacity invariant failed.");
        }

        /// <summary>锁定最近目标当前位置，对配置半径内每个敌方生命周期生成一次同序号伤害请求。</summary>
        /// <param name="player">释放者阵营和身体数据。</param>
        /// <param name="target">触发时锁定的最近合法目标。</param>
        /// <param name="weapon">TbSkill.areaRadiusMilli 转换的范围技能。</param>
        /// <param name="attack">所有受击者共享的单次释放快照。</param>
        /// <param name="counters">范围释放统计。</param>
        /// <remarks>目标随后死亡或池槽复用时由请求中的 TargetLifetime 拒绝陈旧结算。</remarks>
        private void CastTargetArea(in CombatUnit player, in CombatUnit target, in CombatWeaponState weapon,
            in AttackSnapshot attack, ref CombatCounters counters)
        {
            float2 center = target.Position;
            for (int slot = 1; slot < Units.Length; slot++)
            {
                CombatUnit candidate = UnitData[Units[slot]];
                if (candidate.Target.Health <= 0 || candidate.Target.Faction == player.Target.Faction) continue;
                if (!CombatGeometry.WithinReach(candidate.Position - center, weapon.AreaRadius + candidate.Radius)) continue;
                Requests.Add(new CombatDamageRequest
                {
                    Attack = attack,
                    TargetSlot = slot,
                    TargetLifetime = candidate.Target.Lifetime
                });
            }
            Areas.Add(new CombatAreaEvent
            {
                SkillId = weapon.SkillId,
                Position = center,
                AttackSequence = attack.Sequence,
                Tick = counters.Tick + 1
            });
            counters.AreaCasts++;
        }

        /// <summary>校验缓存目标的生命、代际与范围；近战采用圆边缘距离。</summary>
        /// <param name="unit">持有目标缓存的单位。</param>
        /// <returns>缓存当前是否可用于发起攻击。</returns>
        private bool TargetValid(in CombatUnit unit)
        {
            if (unit.TargetSlot < 0 || unit.TargetSlot >= Units.Length) return false;
            var target = UnitData[Units[unit.TargetSlot]];
            float reach = unit.Range + unit.Radius + target.Radius;
            return target.Target.Health > 0 && target.Target.Lifetime == unit.TargetLifetime &&
                target.Target.Faction != unit.Target.Faction && CombatGeometry.WithinReach(target.Position - unit.Position, reach);
        }

        /// <summary>先推进已有近战动作；冷却就绪且目标合法时起手并锁定，弹丸仍复用空槽立即发射。</summary>
        /// <param name="slot">本局固定单位槽位。</param>
        /// <param name="counters">攻击与池统计。</param>
        /// <remarks>只生成请求或弹丸，不改变目标生命。</remarks>
        private void CollectAttack(int slot, ref CombatCounters counters)
        {
            var unit = UnitData[Units[slot]];
            if (unit.Target.Health <= 0) return;
            unit.Cooldown = math.max(0, unit.Cooldown - Delta);
            if (unit.AttackActive)
            {
                unit.AttackAge += Delta * unit.BaseInterval / unit.Interval;
                JudgeMelee(ref unit, ref counters);
                if (unit.AttackAge >= unit.WindupSeconds + unit.AttackOption.DurationSeconds)
                    unit.AttackActive = false;
                UnitData[Units[slot]] = unit;
                return;
            }
            bool valid = TargetValid(unit);
            if (!valid || Time >= unit.NextTargetRefresh)
            {
                int targetSlot = slot == 0 ? FindNearest(unit, unit.Range) : 0;
                unit.TargetSlot = targetSlot;
                unit.TargetLifetime = targetSlot < 0 ? 0 : UnitData[Units[targetSlot]].Target.Lifetime;
                unit.NextTargetRefresh = Time + TargetRefresh;
                valid = TargetValid(unit);
            }
            if (unit.Cooldown <= 0 && valid)
            {
                var attack = unit.Attack;
                attack.Sequence = ++counters.NextAttack;
                attack.AttackerLifetime = unit.Target.Lifetime;
                if (unit.Delivery == ESkillDeliveryType.Melee)
                {
                    unit.PendingAttack = attack;
                    unit.LockedTargetSlot = unit.TargetSlot;
                    unit.LockedTargetLifetime = unit.TargetLifetime;
                    unit.AttackActive = true;
                    unit.AttackHitProcessed = false;
                    unit.AttackAge = 0;
                    float2 offset = UnitData[Units[unit.TargetSlot]].Position - unit.Position;
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
                        var target = UnitData[Units[unit.TargetSlot]];
                        // 初始重叠时沿用配置初始化的单位朝向，使局部碰撞偏移仍有确定方向。
                        float2 direction = math.normalizesafe(target.Position - unit.Position, math.normalizesafe(unit.Facing));
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
                }
                counters.Attacks++;
                unit.Attack = attack;
                unit.Cooldown = unit.Interval;
            }
            UnitData[Units[slot]] = unit;
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
            var target = UnitData[Units[unit.LockedTargetSlot]];
            float reach = unit.Range + unit.Radius + target.Radius;
            if (target.Target.Health <= 0 || target.Target.Lifetime != unit.LockedTargetLifetime ||
                target.Target.Faction == unit.Target.Faction || !CombatGeometry.WithinReach(target.Position - unit.Position, reach))
            { counters.MeleeWhiffs++; return; }
            Requests.Add(new CombatDamageRequest { Attack = unit.PendingAttack, TargetSlot = unit.LockedTargetSlot,
                TargetLifetime = unit.LockedTargetLifetime });
        }

        /// <summary>对弹丸与运动目标做相对扫掠；首个几何接触即回收，命中随机不会导致穿透。</summary>
        /// <param name="counters">候选与活动池统计。</param>
        /// <remarks>碰撞中心使用 TbSkill 导出的局部偏移，在发射时按方向旋转；表现原点保持不变。生成伤害请求但不立即扣血，确保同 tick 请求按统一顺序结算。</remarks>
        private void SweepProjectiles(ref CombatCounters counters)
        {
            for (int index = 0; index < Projectiles.Length; index++)
            {
                var projectile = ProjectileData[Projectiles[index]];
                if (!projectile.Active) continue;
                float travelTime = (float)math.min(Delta, projectile.Remaining);
                float2 start = projectile.Position + projectile.CollisionOffset;
                float2 end = start + projectile.Velocity * travelTime;
                float padding = projectile.Radius + MaximumRadius + MaximumMotion;
                int2 lower = (int2)math.floor((math.min(start, end) - padding) / CellSize);
                int2 upper = (int2)math.floor((math.max(start, end) + padding) / CellSize);
                int selected = -1;
                float first = float.PositiveInfinity;
                ulong lifetime = ulong.MaxValue;
                for (int y = lower.y; y <= upper.y; y++)
                    for (int x = lower.x; x <= upper.x; x++)
                    {
                        if (!Grid.TryGetFirstValue(new int2(x, y), out int slot, out var iterator)) continue;
                        do
                        {
                            var target = UnitData[Units[slot]];
                            if (target.Target.Health <= 0 || target.Target.Faction == projectile.Attack.Faction) continue;
                            counters.Candidates++;
                            float2 targetStart = projectile.BornThisTick ? target.Position : target.PreviousPosition;
                            float2 targetEnd = math.lerp(targetStart, target.Position, travelTime / Delta);
                            if (CombatGeometry.Sweep(start - targetStart, end - targetEnd,
                                projectile.Radius + target.Radius, out float fraction) &&
                                (fraction < first || (fraction == first && target.Target.Lifetime < lifetime)))
                            { selected = slot; first = fraction; lifetime = target.Target.Lifetime; }
                        } while (Grid.TryGetNextValue(out slot, ref iterator));
                    }
                projectile.Position = end - projectile.CollisionOffset;
                projectile.BornThisTick = false;
                projectile.Age += travelTime;
                projectile.Remaining -= travelTime;
                if (selected >= 0)
                {
                    projectile.Position = math.lerp(start, end, first) - projectile.CollisionOffset;
                    Requests.Add(new CombatDamageRequest { Attack = projectile.Attack, TargetSlot = selected, TargetLifetime = lifetime });
                    Impacts.Add(new CombatImpactEvent { SkillId = projectile.SkillId, Position = projectile.Position,
                        AttackSequence = projectile.Attack.Sequence, Tick = counters.Tick + 1 });
                }
                if (selected >= 0 || projectile.Remaining <= 0)
                { projectile.Active = false; counters.ActiveProjectiles--; }
                ProjectileData[Projectiles[index]] = projectile;
            }
        }

        /// <summary>调用统一伤害核心；测试恢复策略仍先执行真实扣血及受击无敌。</summary>
        /// <param name="request">已排序的几何接触请求。</param>
        /// <param name="counters">分类战斗统计。</param>
        /// <remarks>更新生命；恢复策略仅作用存活玩家，不隐式复活。</remarks>
        private void ApplyDamage(in CombatDamageRequest request, ref CombatCounters counters)
        {
            var unit = UnitData[Units[request.TargetSlot]];
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
            UnitData[Units[request.TargetSlot]] = unit;
        }

        /// <summary>请求全部结算后复用死亡怪物槽，并为新生命周期恢复完整模板；同步位置供后续表现读取。</summary>
        /// <param name="counters">死亡、补怪、存活数和生命周期计数。</param>
        /// <remarks>不销毁 ECS 实体；失活单位变换缩放归零，退出时由会话统一释放。</remarks>
        private void RecycleAndSynchronize(ref CombatCounters counters)
        {
            counters.AliveMonsters = 0;
            counters.PlayerDead = UnitData[Units[0]].Target.Health <= 0;
            for (int slot = 0; slot < Units.Length; slot++)
            {
                var unit = UnitData[Units[slot]];
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
                    UnitData[Units[slot]] = unit;
                    counters.Replenished++;
                }
                if (slot != 0 && unit.Target.Health > 0) counters.AliveMonsters++;
                UnitData[Units[slot]] = unit;
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
