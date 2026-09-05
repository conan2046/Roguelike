using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Core.Resources;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>真实配置驱动的隔离 ECS 世界测试；不依赖现有场景、摄像机或手工输入。</summary>
    public sealed class CombatSessionTests
    {
        /// <summary>每个用例进入时立即写明名称，避免编辑器进度窗口未重绘时误判实际运行点。</summary>
        [SetUp]
        public void TraceTestStart() => Debug.Log("COMBAT_TEST_BEGIN " + TestContext.CurrentContext.Test.Name);

        /// <summary>两种技能共享同一机制时，表内各自半径必须传到正式 ECS 扫掠并改变擦边命中结果。</summary>
        /// <param name="radiusMilli">TbSkill 半径千分整数。</param><param name="hit">横向间距为 0.4 的目标是否应命中。</param>
        [TestCase(80, false)]
        [TestCase(400, true)]
        public void SkillRadiusControlsIndependentProjectileCollision(int radiusMilli, bool hit)
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4);
            var source = scenario.CharacterId_Ref.DefaultSkillId_Ref;
            var skill = SkillWithCollision(source, radiusMilli, 0, 0);
            scenario.CharacterId_Ref.DefaultSkillId_Ref = skill;
            Assert.That(skill.CombatProfileId_Ref, Is.SameAs(source.CombatProfileId_Ref));
            using var world = new World("Independent skill radius"); using var session = CreateSession(world, scenario);
            Assert.That(session.ReadUnit(0).ProjectileRadius, Is.EqualTo(radiusMilli / 1000f));
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot); unit.Position = new float2(2, 0); unit.MoveSpeed = 0; unit.Cooldown = 1000;
                unit.Target.Health = slot == 1 ? unit.Target.MaxHealth : 0; unit.Target.Evasion = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            var entity = session.ProjectileEntity(0); var projectile = world.EntityManager.GetComponentData<CombatProjectile>(entity);
            Assert.That(projectile.Active, Is.True);
            projectile.Position = new float2(0, 0.4f); projectile.Velocity = new float2(600, 0);
            world.EntityManager.SetComponentData(entity, projectile);
            long health = session.ReadUnit(1).Target.Health;
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(1).Target.Health < health, Is.EqualTo(hit));
        }

        /// <summary>发射方向为右时，技能局部右/前偏移应旋转到世界下/右；扫掠消费偏移且不改变表现原点。</summary>
        [Test]
        public void SkillOffsetRotatesAndParticipatesInSweep()
        {
            var tables = LoadTables(); var scenario = tables.TbPerformanceScenario.Get(4);
            scenario.CharacterId_Ref.DefaultSkillId_Ref = SkillWithCollision(scenario.CharacterId_Ref.DefaultSkillId_Ref, 80, 500, 1000);
            using var world = new World("Skill local offset"); using var session = CreateSession(world, scenario);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot); unit.Position = new float2(2, 0); unit.MoveSpeed = 0; unit.Cooldown = 1000;
                unit.Target.Health = slot == 1 ? unit.Target.MaxHealth : 0; unit.Target.Evasion = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            var entity = session.ProjectileEntity(0); var projectile = world.EntityManager.GetComponentData<CombatProjectile>(entity);
            Assert.That(math.distance(projectile.CollisionOffset, new float2(1, -0.5f)), Is.LessThan(1e-6f));
            Assert.That(projectile.Position.y, Is.Zero);
            projectile.Position = new float2(0, 0.5f); projectile.Velocity = new float2(600, 0);
            world.EntityManager.SetComponentData(entity, projectile);
            long health = session.ReadUnit(1).Target.Health;
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(1).Target.Health, Is.LessThan(health));
        }

        /// <summary>隔离配置测试通过生成的二进制协议创建技能变体，保留相同机制和视觉引用，不写源表。</summary>
        /// <param name="source">实际 TbSkill 行。</param><param name="radius">半径千分整数。</param>
        /// <param name="x">局部右向偏移千分整数。</param><param name="y">局部前向偏移千分整数。</param>
        /// <returns>仅当前测试持有的技能配置。</returns>
        private static SkillConfig SkillWithCollision(SkillConfig source, int radius, int x, int y)
        {
            var b = new ByteBuf(); b.WriteInt(source.Id); b.WriteString(source.Name); b.WriteInt(source.VisualSetId);
            b.WriteBool(true); b.WriteInt(source.CombatProfileId.Value);
            b.WriteBool(true); b.WriteInt(radius); b.WriteBool(true); b.WriteInt(x); b.WriteBool(true); b.WriteInt(y);
            b.WriteBool(source.ProjectileClipId.HasValue); if (source.ProjectileClipId.HasValue) b.WriteInt(source.ProjectileClipId.Value);
            b.WriteBool(source.ImpactClipId.HasValue); if (source.ImpactClipId.HasValue) b.WriteInt(source.ImpactClipId.Value);
            b.WriteSize(source.AreaClipIds.Count); foreach (int clipId in source.AreaClipIds) b.WriteInt(clipId);
            b.WriteBool(source.AreaRadiusMilli.HasValue); if (source.AreaRadiusMilli.HasValue) b.WriteInt(source.AreaRadiusMilli.Value);
            return new SkillConfig(b)
            {
                CombatProfileId_Ref = source.CombatProfileId_Ref,
                VisualSetId_Ref = source.VisualSetId_Ref,
                ProjectileClipId_Ref = source.ProjectileClipId_Ref,
                ImpactClipId_Ref = source.ImpactClipId_Ref,
                AreaClipIds_Ref = source.AreaClipIds_Ref
            };
        }

        /// <summary>持续圆周出生并追逐静止玩家，校验活怪互不重叠且不能进入玩家圆柱。</summary>
        [Test]
        public void TimedCrowdCannotOverlapMovementCylinders()
        {
            using var world = new World("Cylinder crowd");
            using var session = CreateSession(world, LoadTables(true).TbPerformanceScenario.Get(4));
            for (int tick = 0; tick < 720; tick++)
            {
                for (int slot = 0; slot < session.UnitCount; slot++)
                {
                    var u = session.ReadUnit(slot); u.Cooldown = 1000;
                    world.EntityManager.SetComponentData(session.UnitEntity(slot), u);
                }
                session.Advance(session.StepSeconds, float2.zero);
                for (int a = 0; a < session.UnitCount; a++)
                    for (int b = a + 1; b < session.UnitCount; b++)
                    {
                        var first = session.ReadUnit(a); var second = session.ReadUnit(b);
                        if (first.Target.Health <= 0 || second.Target.Health <= 0 || !first.Movement.HeightOverlaps(second.Movement)) continue;
                        float distance = math.distance(first.Position + first.Movement.Offset, second.Position + second.Movement.Offset);
                        Assert.That(distance + 1e-5f, Is.GreaterThanOrEqualTo(first.Movement.Radius + second.Movement.Radius), "tick " + tick + " slots " + a + "/" + b);
                    }
            }
        }

        /// <summary>逐只波次在 1 秒首发、0.2 秒间隔后发；波满十五只后完整等待波间时长。</summary>
        [Test]
        public void WaveIntervalsDoNotCollapseIntoInstantBatches()
        {
            var s = LoadTables(true).TbPerformanceScenario.Get(4);
            using var world = new World("Wave schedule");
            using var session = CreateSession(world, s);
            int gap = (int)Math.Ceiling((decimal)s.SpawnIntervalSeconds.Value * s.CombatRulesId_Ref.SimulationHz);
            int unitGap = (int)Math.Ceiling((decimal)s.SpawnUnitIntervalSeconds.Value * s.CombatRulesId_Ref.SimulationHz);
            int last = gap + (s.SpawnBatchCount.Value - 1) * unitGap;
            bool outside = false;
            for (int tick = 1; tick <= last + gap; tick++)
            {
                var player = session.ReadUnit(0);
                player.Position = new float2(s.ArenaHalfWidth.Value - player.Movement.Radius - player.Movement.Offset.x, 0);
                for (int slot = 0; slot < session.UnitCount; slot++)
                { var u = slot == 0 ? player : session.ReadUnit(slot); u.MoveSpeed = 0; u.Cooldown = 1000; world.EntityManager.SetComponentData(session.UnitEntity(slot), u); }
                long before = session.SpawnedMonsters;
                session.Advance(session.StepSeconds, float2.zero);
                int expected = tick < gap ? 0 : tick <= last ? 1 + (tick - gap) / unitGap : tick < last + gap ? s.SpawnBatchCount.Value : s.SpawnBatchCount.Value + 1;
                Assert.That(session.SpawnedMonsters, Is.EqualTo(expected), "tick " + tick);
                if (session.SpawnedMonsters > before)
                {
                    var unit = session.ReadUnit((int)session.SpawnedMonsters);
                    Assert.That(math.distance(unit.SpawnPosition, player.Position), Is.EqualTo(s.SpawnRadiusPixels.Value * s.CombatRulesId_Ref.WorldUnitsPerPixel).Within(1e-5));
                    outside |= unit.SpawnPosition.x > s.ArenaHalfWidth.Value;
                }
            }
            Assert.That(outside, Is.True);
            Assert.That(session.WaveNumber, Is.EqualTo(2));
            Assert.That(session.SpawnedInWave, Is.EqualTo(1));
        }

        /// <summary>真实场景 4 在整秒按批生成，检查逻辑像素换算、移动圆心及边缘不夹回。</summary>
        [Test]
        public void TimedCircleUsesCurrentPlayerCenterAndExactRadius()
        {
            var scenario = LoadTables(true).TbPerformanceScenario.Get(4);
            using var world = new World("Timed circle geometry");
            using var session = CreateSession(world, scenario);
            Assert.That(session.Statistics.AliveMonsters, Is.Zero);
            int ticks = scenario.CombatRulesId_Ref.SimulationHz;
            for (int i = 0; i < ticks - 1; i++) session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.SpawnedMonsters, Is.Zero);
            var player = session.ReadUnit(0);
            player.Position = new float2(scenario.ArenaHalfWidth.Value - player.Movement.Radius - player.Movement.Offset.x, 0);
            player.Cooldown = 1000;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.SpawnedMonsters, Is.EqualTo(1));
            float radius = scenario.SpawnRadiusPixels.Value * scenario.CombatRulesId_Ref.WorldUnitsPerPixel;
            bool outside = false;
            for (int i = 1; i < session.UnitCount; i++)
            {
                var unit = session.ReadUnit(i);
                if (unit.Target.Health <= 0) continue;
                Assert.That(math.distance(unit.Position, player.Position), Is.EqualTo(radius).Within(1e-5));
                outside |= unit.Position.x > scenario.ArenaHalfWidth.Value;
            }
            Assert.That(math.distance(session.ReadUnit(1).Position, player.Position), Is.EqualTo(radius).Within(1e-5));
            var oldMonster = session.ReadUnit(1);
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(math.distance(session.ReadUnit(1).Position, player.Position), Is.LessThan(math.distance(oldMonster.Position, player.Position)));
        }

        /// <summary>六批实际生成突破旧容量；将已生成单位移离出生圆周，隔离占位与伤害后检查槽复用、暂停死亡冻结和重开确定性。</summary>
        [Test]
        public void TimedCircleGrowsReusesAndResetsWithoutAliveLimit()
        {
            var scenario = LoadTables(true).TbPerformanceScenario.Get(4);
            using var world = new World("Timed circle lifecycle");
            using var session = CreateSession(world, scenario);
            int ticks = scenario.CombatRulesId_Ref.SimulationHz;
            int waveTicks = (int)Math.Ceiling(((decimal)scenario.SpawnIntervalSeconds.Value + (scenario.SpawnBatchCount.Value - 1) * (decimal)scenario.SpawnUnitIntervalSeconds.Value) * ticks);
            for (int i = 0; i < waveTicks * 6; i++)
            {
                for (int slot = 0; slot < session.UnitCount; slot++)
                {
                    var u = session.ReadUnit(slot); u.MoveSpeed = 0; u.Cooldown = 1000;
                    // 本用例测容量而非拥堵；活怪持续堆在出生圆周会触发正确的延迟生成。
                    if (slot > 0) u.Position = new float2(100 + slot * 2, 0);
                    world.EntityManager.SetComponentData(session.UnitEntity(slot), u);
                }
                session.Advance(session.StepSeconds, float2.zero);
            }
            Assert.That(session.SpawnedMonsters, Is.EqualTo(6 * scenario.SpawnBatchCount.Value));
            Assert.That(session.Statistics.AliveMonsters, Is.EqualTo(session.SpawnedMonsters));
            int capacity = session.UnitCount;
            ulong oldLife = session.ReadUnit(1).Target.Lifetime;
            for (int slot = 1; slot < session.UnitCount; slot++)
            { var u = session.ReadUnit(slot); u.Target.Health = 0; world.EntityManager.SetComponentData(session.UnitEntity(slot), u); }
            for (int i = 0; i < ticks; i++) session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.UnitCount, Is.EqualTo(capacity));
            Assert.That(session.ReadUnit(1).Target.Lifetime, Is.GreaterThan(oldLife));
            Assert.That(session.Statistics.AliveMonsters, Is.EqualTo(1));
            session.Paused = true;
            long count = session.SpawnedMonsters;
            session.Advance(10, float2.zero);
            Assert.That(session.SpawnedMonsters, Is.EqualTo(count));
            session.Paused = false;
            var player = session.ReadUnit(0); player.Target.Health = 0; world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            session.Advance(session.StepSeconds, float2.zero); session.Advance(10, float2.zero);
            Assert.That(session.SpawnedMonsters, Is.EqualTo(count));
            session.Restart();
            Assert.That(session.Statistics.AliveMonsters, Is.Zero);
            Assert.That(session.SpawnedMonsters, Is.Zero);
            for (int i = 0; i < ticks; i++) session.Advance(session.StepSeconds, float2.zero);
            float2 first = session.ReadUnit(1).Position;
            session.Restart();
            for (int i = 0; i < ticks; i++) session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(1).Position, Is.EqualTo(first));
        }

        /// <summary>验证配置补算上限与积压保留，暂停不推进时间，重开清空状态并更换生命周期。</summary>
        [Test]
        public void FixedStepPauseRestartAndOwnership()
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(5);
            using var world = new World("Combat fixed-step test");
            Entity unrelated = world.EntityManager.CreateEntity();
            var session = CreateSession(world, scenario);
            Entity owned = session.UnitEntity(0);
            try
            {
                ulong lifetime = session.ReadUnit(0).Target.Lifetime;
                Assert.That(session.Advance(1, float2.zero), Is.EqualTo(scenario.CombatRulesId_Ref.MaxCatchUpSteps));
                Assert.That(session.BacklogSeconds, Is.EqualTo(1 - session.StepSeconds * scenario.CombatRulesId_Ref.MaxCatchUpSteps).Within(1e-12));
                var stats = session.Statistics;
                double debt = session.BacklogSeconds;
                session.Paused = true;
                Assert.That(session.Advance(10, new float2(1)), Is.Zero);
                Assert.That(session.Statistics.Tick, Is.EqualTo(stats.Tick));
                Assert.That(session.BacklogSeconds, Is.EqualTo(debt));
                session.Restart();
                Assert.That(session.ReadUnit(0).Target.Lifetime, Is.GreaterThan(lifetime));
                Assert.That(session.ReadUnit(0).Cooldown, Is.Zero);
                Assert.That(session.ReadUnit(0).Target.Health, Is.EqualTo(session.ReadUnit(0).Target.MaxHealth));
                Assert.That(session.Statistics.Tick, Is.Zero);
                Assert.That(session.Statistics.Attacks, Is.Zero);
                Assert.That(session.Statistics.ActiveProjectiles, Is.Zero);
                Assert.That(session.BacklogSeconds, Is.Zero);
                Assert.That(session.Paused, Is.False);
                Assert.That(session.SynchronizeAttributes(0, lifetime, new CombatAttributes(scenario.CharacterProfileOverrideId_Ref)), Is.False);
            }
            finally { session.Dispose(); session.Dispose(); }
            Assert.That(world.EntityManager.Exists(owned), Is.False);
            Assert.That(world.EntityManager.Exists(unrelated), Is.True);
            Assert.Throws<ObjectDisposedException>(() => session.Advance(1, float2.zero));
        }

        /// <summary>以实际千怪配置执行完整预热加采样时长的模拟，检查负载而非渲染性能。</summary>
        [Test]
        [Explicit("千怪持续负载需单独授权；普通交互验收不运行。")]
        public void ThousandMonsterSimulationMaintainsCombatWorkload()
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(5);
            using var world = new World("Combat sustained workload test");
            using var session = CreateSession(world, scenario);
            int ticks = (int)Math.Ceiling((scenario.WarmupSeconds + scenario.SampleSeconds) / session.StepSeconds);
            for (int tick = 0; tick < ticks; tick++)
            {
                Assert.That(session.Advance(session.StepSeconds, float2.zero), Is.EqualTo(1));
                Assert.That(session.Statistics.AliveMonsters, Is.EqualTo(scenario.EntityCount));
                Assert.That(session.Statistics.PlayerDead, Is.False);
            }
            var stats = session.Statistics;
            Assert.That(stats.Attacks, Is.GreaterThan(0));
            Assert.That(stats.ProjectileSpawns, Is.GreaterThan(0));
            Assert.That(stats.Candidates, Is.GreaterThan(0));
            Assert.That(stats.Hits, Is.GreaterThan(0));
            Assert.That(stats.Criticals, Is.GreaterThan(0));
            Assert.That(stats.Evades, Is.GreaterThan(0));
            Assert.That(stats.DamageEvents, Is.GreaterThan(0));
            Assert.That(stats.HealthLost, Is.GreaterThan(0));
            Assert.That(stats.Invulnerable, Is.GreaterThan(0));
            Assert.That(stats.Deaths, Is.GreaterThan(0));
            Assert.That(stats.Replenished, Is.EqualTo(stats.Deaths));
            Assert.That(stats.PeakProjectiles, Is.LessThanOrEqualTo(session.ProjectileCapacity));
            Assert.That(stats.UsedBurst, Is.True, "Workload must execute the native Burst path, not silently fall back to managed code.");
            Assert.That(session.BacklogSeconds, Is.Zero);
            Debug.Log($"COMBAT_ECS_WORKLOAD ticks={stats.Tick} alive={stats.AliveMonsters} attacks={stats.Attacks} " +
                $"projectiles={stats.ProjectileSpawns} hits={stats.Hits} evades={stats.Evades} criticals={stats.Criticals} " +
                $"damageEvents={stats.DamageEvents} healthLost={stats.HealthLost} deaths={stats.Deaths} replenished={stats.Replenished} " +
                $"invulnerable={stats.Invulnerable} peakProjectiles={stats.PeakProjectiles} burst={stats.UsedBurst}");
        }

        /// <summary>普通回归使用小规模隔离夹具验证相同种子和输入的确定性，保留完整状态比较。</summary>
        /// <remarks>从 TbPerformanceScenario 读取战斗配置，仅缩小测试副本的 EntityCount；不修改源表或运行时默认值。</remarks>
        [Test]
        [Category("Quick")]
        public void SameSeedAndInputProduceSameEcsState()
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(5);
            typeof(PerformanceScenarioConfig).GetField("EntityCount").SetValue(scenario, 12);
            AssertDeterministicState(scenario);
        }

        /// <summary>性能专项保留原千怪双世界确定性覆盖，普通回归不执行。</summary>
        /// <remarks>使用 TbPerformanceScenario 的原始 EntityCount；创建并释放两个隔离 World。</remarks>
        [Test]
        [Category("Stress")]
        [Explicit("千怪确定性专项，性能验收时单独运行。")]
        public void ThousandEntitySameSeedAndInputProduceSameEcsState()
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(5);
            AssertDeterministicState(scenario);
        }

        /// <summary>由快速或压力测试调用，以相同输入推进双世界并比较统计与全部单位状态。</summary>
        /// <param name="scenario">来自 TbPerformanceScenario 的隔离测试配置。</param>
        /// <remarks>模拟步数仅为测试工作量；两个 World 和会话在退出时释放。</remarks>
        private static void AssertDeterministicState(PerformanceScenarioConfig scenario)
        {
            using var worldA = new World("Combat deterministic A");
            using var worldB = new World("Combat deterministic B");
            using var a = CreateSession(worldA, scenario);
            using var b = CreateSession(worldB, scenario);
            for (int tick = 0; tick < 300; tick++)
            {
                a.Advance(a.StepSeconds, float2.zero);
                b.Advance(b.StepSeconds, float2.zero);
            }
            Assert.That(a.Statistics, Is.EqualTo(b.Statistics));
            for (int slot = 0; slot < a.UnitCount; slot++) Assert.That(a.ReadUnit(slot), Is.EqualTo(b.ReadUnit(slot)));
        }

        /// <summary>无目标不消费冷却；等距候选必须选择较早生成的生命实例。</summary>
        [Test]
        public void NearestTargetTieAndNoTargetCooldown()
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(4);
            using var world = new World("Combat targeting test");
            using var session = CreateSession(world, scenario);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(0).Cooldown, Is.Zero);
            Assert.That(session.Statistics.Attacks, Is.Zero);
            session.Restart();
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Position = new float2(slot == 1 ? -2 : 2, 0);
                unit.MoveSpeed = 0;
                if (slot > 2) unit.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(0).TargetSlot, Is.EqualTo(1));
            Assert.That(session.Statistics.ProjectileSpawns, Is.EqualTo(1));
        }

        /// <summary>手动输入按世界单位限制边界与斜向速度，静止测试策略忽略移动输入。</summary>
        [Test]
        public void InputPolicyAndArenaBounds()
        {
            var tables = LoadTables();
            using var world = new World("Combat input test");
            using var manual = CreateSession(world, tables.TbPerformanceScenario.Get(4));
            using var stationary = CreateSession(world, tables.TbPerformanceScenario.Get(5));
            // 输入归一化夹具需要空路径；单位阻挡由 CombatCylinderTests 独立覆盖。
            for (int slot = 1; slot < manual.UnitCount; slot++)
            {
                var monster = manual.ReadUnit(slot);
                monster.Position = new float2(-8, -6); monster.MoveSpeed = 0;
                world.EntityManager.SetComponentData(manual.UnitEntity(slot), monster);
            }
            float2 before = manual.ReadUnit(0).Position;
            manual.Advance(manual.StepSeconds, new float2(1, 1));
            Assert.That(math.distance(before, manual.ReadUnit(0).Position), Is.EqualTo(manual.ReadUnit(0).MoveSpeed * manual.StepSeconds).Within(1e-6));
            stationary.Advance(stationary.StepSeconds, new float2(1, 1));
            Assert.That(stationary.ReadUnit(0).Position, Is.EqualTo(float2.zero));
            for (int tick = 0; tick < 500; tick++) manual.Advance(manual.StepSeconds, new float2(1, 1));
            var player = manual.ReadUnit(0);
            Assert.That(math.abs(player.Position.x) + player.Radius, Is.LessThanOrEqualTo(tables.TbPerformanceScenario.Get(4).ArenaHalfWidth.Value));
            Assert.That(math.abs(player.Position.y) + player.Radius, Is.LessThanOrEqualTo(tables.TbPerformanceScenario.Get(4).ArenaHalfHeight.Value));
        }

        /// <summary>扫掠纯几何覆盖高速穿越、初始重叠、擦边和无接触。</summary>
        [Test]
        public void SweepFindsFirstContactWithoutTunneling()
        {
            Assert.That(CombatGeometry.Sweep(new float2(-10, 0), new float2(10, 0), 1, out float fraction), Is.True);
            Assert.That(fraction, Is.EqualTo(0.45f).Within(1e-6));
            Assert.That(CombatGeometry.Sweep(float2.zero, float2.zero, 1, out fraction), Is.True);
            Assert.That(fraction, Is.Zero);
            Assert.That(CombatGeometry.Sweep(new float2(-10, 1), new float2(10, 1), 1, out fraction), Is.True);
            Assert.That(fraction, Is.EqualTo(0.5f).Within(1e-6));
            Assert.That(CombatGeometry.Sweep(new float2(-10, 2), new float2(10, 2), 1, out _), Is.False);
        }

        /// <summary>正式 ECS 弹丸跨过目标时仍命中；首次几何接触即使闪避也只回收一次。</summary>
        /// <param name="evade">是否强制首个目标闪避，用于确认几何接触回收独立于概率判定。</param>
        [TestCase(false)]
        [TestCase(true)]
        public void EcsProjectileSweepsAndStopsAtFirstContact(bool evade)
        {
            var tables = LoadTables();
            using var world = new World("Combat projectile sweep test");
            using var session = CreateSession(world, tables.TbPerformanceScenario.Get(4));
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Position = new float2(slot == 1 ? 2 : 4, 0);
                unit.MoveSpeed = 0;
                unit.Target.Health = slot <= 2 ? unit.Target.MaxHealth : 0;
                if (evade) unit.Target.Evasion = 1;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            var projectileEntity = session.ProjectileEntity(0);
            var projectile = world.EntityManager.GetComponentData<CombatProjectile>(projectileEntity);
            Assert.That(projectile.Active, Is.True);
            projectile.Velocity = new float2(600, 0);
            if (evade) projectile.Attack.Hit = 0;
            world.EntityManager.SetComponentData(projectileEntity, projectile);
            long before = session.ReadUnit(1).Target.Health;
            long farther = session.ReadUnit(2).Target.Health;
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(world.EntityManager.GetComponentData<CombatProjectile>(projectileEntity).Active, Is.False);
            Assert.That(session.Statistics.ActiveProjectiles, Is.Zero);
            Assert.That(session.ReadUnit(2).Target.Health, Is.EqualTo(farther));
            if (evade)
            {
                Assert.That(session.ReadUnit(1).Target.Health, Is.EqualTo(before));
                Assert.That(session.Statistics.Evades, Is.EqualTo(1));
            }
            else Assert.That(session.ReadUnit(1).Target.Health, Is.LessThan(before));
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(2).Target.Health, Is.EqualTo(farther));
        }

        /// <summary>普通生命策略下玩家死亡即冻结模拟，不允许后续渲染帧继续消耗弹丸寿命或冷却。</summary>
        [Test]
        public void PlayerDeathFreezesSession()
        {
            using var world = new World("Combat player death test");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            var player = session.ReadUnit(0);
            player.Target.Health = 1;
            player.Cooldown = 100;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            var monster = session.ReadUnit(1);
            monster.Position = player.Position;
            world.EntityManager.SetComponentData(session.UnitEntity(1), monster);
            for (int tick = 0; tick < 120 && !session.Statistics.PlayerDead; tick++)
                session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.Statistics.PlayerDead, Is.True);
            var stats = session.Statistics;
            Assert.That(session.Advance(10, new float2(1)), Is.Zero);
            Assert.That(session.Statistics, Is.EqualTo(stats));
        }

        /// <summary>请求排序按生命周期和攻击序号，不依赖原始写入顺序。</summary>
        [Test]
        public void DamageRequestsHaveStableTotalOrder()
        {
            var requests = new[]
            {
                new CombatDamageRequest { TargetLifetime = 2, Attack = new AttackSnapshot { AttackerLifetime = 1, Sequence = 1 } },
                new CombatDamageRequest { TargetLifetime = 1, Attack = new AttackSnapshot { AttackerLifetime = 2, Sequence = 1 } },
                new CombatDamageRequest { TargetLifetime = 1, Attack = new AttackSnapshot { AttackerLifetime = 1, Sequence = 2 } },
                new CombatDamageRequest { TargetLifetime = 1, Attack = new AttackSnapshot { AttackerLifetime = 1, Sequence = 1 } }
            };
            Array.Sort(requests);
            Assert.That(requests[0].Attack.Sequence, Is.EqualTo(1));
            Assert.That(requests[1].Attack.Sequence, Is.EqualTo(2));
            Assert.That(requests[2].Attack.AttackerLifetime, Is.EqualTo(2));
            Assert.That(requests[3].TargetLifetime, Is.EqualTo(2));
        }

        /// <summary>同 tick 先产生双方攻击，按目标排序后即使攻击者死亡，其已生成弹丸仍可完成击杀。</summary>
        [Test]
        public void EcsAllowsMutualKillsInOneTick()
        {
            using var world = new World("Combat simultaneous death test");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            var player = session.ReadUnit(0);
            player.Target.Health = 1;
            player.Cooldown = 100;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Target.Health = slot == 1 ? 1 : 0;
                unit.Position = player.Position;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            var pending = session.ReadUnit(1);
            Assert.That(pending.AttackActive, Is.True);
            pending.AttackAge = pending.WindupSeconds + pending.AttackOption.HitSeconds;
            pending.PendingAttack.Hit = 1;
            world.EntityManager.SetComponentData(session.UnitEntity(1), pending);
            player = session.ReadUnit(0);
            player.Cooldown = 0;
            player.Attack.Hit = 1;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(0).Target.Health, Is.Zero);
            Assert.That(session.ReadUnit(1).Target.Health, Is.Zero);
            Assert.That(session.Statistics.Deaths, Is.EqualTo(2));
            Assert.That(session.Statistics.Attacks, Is.EqualTo(2));
        }

        /// <summary>实际死亡补怪复用实体而更换生命周期，清除生命/冷却/目标并拒绝旧属性变更。</summary>
        [Test]
        public void ReplenishmentReusesEntityWithCleanLifecycle()
        {
            var tables = LoadTables();
            var scenario = tables.TbPerformanceScenario.Get(5);
            using var world = new World("Combat recycling test");
            using var session = CreateSession(world, scenario);
            var player = session.ReadUnit(0);
            player.Attack.Hit = 1;
            player.Attack.Attack = 1000000;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            Entity original = session.UnitEntity(1);
            ulong lifetime = session.ReadUnit(1).Target.Lifetime;
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Position = slot == 1 ? new float2(0.4f, 0) : new float2(8, 6);
                unit.MoveSpeed = 0;
                unit.Target.Evasion = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
            session.Advance(session.StepSeconds, float2.zero);
            var replacement = session.ReadUnit(1);
            Assert.That(session.UnitEntity(1), Is.EqualTo(original));
            Assert.That(replacement.Target.Lifetime, Is.GreaterThan(lifetime));
            Assert.That(replacement.Target.Health, Is.EqualTo(replacement.Target.MaxHealth));
            Assert.That(replacement.TargetSlot, Is.EqualTo(-1));
            Assert.That(replacement.Cooldown, Is.Zero);
            Assert.That(replacement.AttackActive, Is.False);
            Assert.That(replacement.AttackAge, Is.Zero);
            Assert.That(replacement.PendingAttack, Is.EqualTo(default(AttackSnapshot)));
            Assert.That(replacement.Target.ImmunityCharges, Is.Zero);
            Assert.That(replacement.Target.InvulnerableUntil, Is.Zero);
            Assert.That(session.Statistics.Replenished, Is.EqualTo(1));
            Assert.That(session.SynchronizeAttributes(1, lifetime, new CombatAttributes(scenario.MonsterProfileOverrideId_Ref)), Is.False);
        }

        /// <summary>不同方向追击后均应在身体边缘开始近战，回归浮点舍入导致停步却不能攻击的问题。</summary>
        [Test]
        public void MeleeAttackStartsAtContactFromEveryDirection()
        {
            using var world = new World("Combat melee boundary test");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            for (int direction = 0; direction < 64; direction++)
            {
                session.Restart();
                var player = session.ReadUnit(0);
                player.Attack.Attack = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(0), player);
                float angle = direction * math.PI * 2 / 64;
                for (int slot = 1; slot < session.UnitCount; slot++)
                {
                    var unit = session.ReadUnit(slot);
                    unit.Position = new float2(math.cos(angle), math.sin(angle)) * 2;
                    if (slot != 1) unit.Target.Health = 0;
                    world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
                }
                for (int tick = 0; tick < 120; tick++) session.Advance(session.StepSeconds, float2.zero);
                Assert.That(session.ReadUnit(1).PendingAttack.Sequence, Is.GreaterThan(0), $"No melee attack from direction {direction}.");
            }
        }

        /// <summary>验证真实表前摇、F4、单次命中和攻击完成；逐 tick 比较跨帧边界。</summary>
        [Test]
        public void MeleeWaitsConfiguredWindupThenHitsOnceAtF4()
        {
            var tables = LoadTables();
            using var world = new World("Melee timeline");
            using var session = CreateSession(world, tables.TbPerformanceScenario.Get(4));
            PrepareMelee(world, session);
            session.Advance(session.StepSeconds, float2.zero);
            var unit = session.ReadUnit(1);
            Assert.That(unit.WindupSeconds, Is.EqualTo(0.2).Within(1e-7));
            Assert.That(unit.AttackActive, Is.True);
            Assert.That(unit.AttackAge, Is.Zero);
            Assert.That(session.Statistics.MeleeJudgements, Is.Zero);
            var position = unit.Position;
            var facing = unit.Facing;
            double hitAt = unit.WindupSeconds + unit.AttackOption.HitSeconds;
            double endAt = unit.WindupSeconds + unit.AttackOption.DurationSeconds;
            int maximumTicks = (int)Math.Ceiling(endAt * unit.Interval / unit.BaseInterval / session.StepSeconds) + 2;
            for (int tick = 0; unit.AttackActive && tick < maximumTicks; tick++)
            {
                session.Advance(session.StepSeconds, float2.zero);
                unit = session.ReadUnit(1);
                Assert.That(unit.Position, Is.EqualTo(position));
                Assert.That(unit.Facing, Is.EqualTo(facing));
                Assert.That(session.Statistics.MeleeJudgements, Is.EqualTo(unit.AttackAge < hitAt ? 0 : 1));
                Assert.That(unit.AttackActive, Is.EqualTo(unit.AttackAge < endAt));
            }
            Assert.That(unit.AttackActive, Is.False, "Melee timeline stopped advancing; playerDead=" + session.Statistics.PlayerDead);
            Assert.That(session.Statistics.DamageEvents, Is.EqualTo(1));
            Assert.That(session.Statistics.MeleeWhiffs, Is.Zero);
        }

        /// <summary>命中帧重新检查距离和生命周期；挥空也保留本次已消费的冷却。</summary>
        /// <param name="changeLifetime">改变代际而不是移动目标。</param>
        [TestCase(false)]
        [TestCase(true)]
        public void MeleeWhiffsWhenLockedTargetInvalid(bool changeLifetime)
        {
            using var world = new World("Melee whiff");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            PrepareMelee(world, session);
            session.Advance(session.StepSeconds, float2.zero);
            var facing = session.ReadUnit(1).Facing;
            var player = session.ReadUnit(0);
            if (changeLifetime) player.Target.Lifetime++;
            else player.Position += new float2(3, 3);
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            for (int tick = 0; tick < 120 && session.Statistics.MeleeJudgements == 0; tick++)
                session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.Statistics.MeleeWhiffs, Is.EqualTo(1));
            Assert.That(session.Statistics.DamageEvents, Is.Zero);
            Assert.That(session.Statistics.Attacks, Is.EqualTo(1));
            Assert.That(session.ReadUnit(1).Facing, Is.EqualTo(facing));
            Assert.That(session.ReadUnit(1).Cooldown, Is.GreaterThan(0));
        }

        /// <summary>暂停保持待命时序；死亡在命中帧前取消；重开不遗留攻击快照。</summary>
        [Test]
        public void PendingMeleePausesCancelsOnDeathAndRestartsCleanly()
        {
            using var world = new World("Melee lifecycle");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            PrepareMelee(world, session);
            session.Advance(session.StepSeconds, float2.zero);
            var unit = session.ReadUnit(1);
            session.Paused = true;
            session.Advance(10, float2.zero);
            Assert.That(session.ReadUnit(1), Is.EqualTo(unit));
            session.Paused = false;
            unit.Target.Health = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(1), unit);
            for (int tick = 0; tick < 120; tick++) session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(1).AttackActive, Is.False);
            Assert.That(session.ReadUnit(1).PendingAttack, Is.EqualTo(default(AttackSnapshot)));
            Assert.That(session.Statistics.MeleeJudgements, Is.Zero);
            session.Restart();
            Assert.That(session.ReadUnit(1).AttackAge, Is.Zero);
            Assert.That(session.ReadUnit(1).AttackActive, Is.False);
        }

        /// <summary>改变配置前摇快照和攻速后保持当前原速进度，后续按新速率推进，已起手伤害快照不变。</summary>
        /// <param name="windup">仅测试使用的不同前摇，覆盖零等待与独立长前摇。</param>
        [TestCase(0d)]
        [TestCase(0.7d)]
        public void MeleeTimingUsesUnitConfigurationAndPreservesAttackSnapshot(double windup)
        {
            var tables = LoadTables();
            var scenario = tables.TbPerformanceScenario.Get(4);
            using var world = new World("Melee speed");
            var skill = scenario.MonsterIds_Ref[0].DefaultSkillId_Ref;
            skill.CombatProfileId_Ref = SkillWithWindup(skill.CombatProfileId_Ref, (float)windup);
            using var session = CreateSession(world, scenario);
            PrepareMelee(world, session);
            var unit = session.ReadUnit(1);
            Assert.That(unit.WindupSeconds, Is.EqualTo(windup).Within(1e-7));
            session.Advance(session.StepSeconds, float2.zero);
            session.Advance(session.StepSeconds, float2.zero);
            unit = session.ReadUnit(1);
            var captured = unit.PendingAttack;
            double age = unit.AttackAge;
            var attributes = new CombatAttributes(scenario.MonsterProfileOverrideId_Ref);
            attributes.Replace(1, new AttributeModifier { Attribute = EAttributeType.AttackSpeedMultiplier, PercentBp = 10000 },
                new AttributeModifier { Attribute = EAttributeType.Attack, Flat = 100 });
            double oldInterval = unit.Interval;
            Assert.That(session.SynchronizeAttributes(1, unit.Target.Lifetime, attributes), Is.True);
            unit = session.ReadUnit(1);
            Assert.That(unit.Interval, Is.LessThan(oldInterval));
            Assert.That(unit.AttackAge, Is.EqualTo(age));
            Assert.That(unit.PendingAttack, Is.EqualTo(captured));
            double expected = age + (float)session.StepSeconds * unit.BaseInterval / unit.Interval;
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.ReadUnit(1).AttackAge, Is.EqualTo(expected).Within(1e-10));
            Assert.That(session.ReadUnit(1).WindupSeconds, Is.EqualTo(windup).Within(1e-7));
        }

        /// <summary>缺少或非法前摇必须拒绝入局，并回收构造失败时的原生池与实体。</summary>
        /// <param name="windup">测试生成表的可空前摇值。</param>
        [TestCase(null)]
        [TestCase(-0.1f)]
        public void MeleeRejectsInvalidWindup(float? windup)
        {
            var scenario = LoadTables().TbPerformanceScenario.Get(4);
            var skill = scenario.MonsterIds_Ref[0].DefaultSkillId_Ref;
            skill.CombatProfileId_Ref = SkillWithWindup(skill.CombatProfileId_Ref, windup);
            using var world = new World("Invalid melee config");
            Assert.Throws<InvalidOperationException>(() => CreateSession(world, scenario));
            using var query = world.EntityManager.CreateEntityQuery(typeof(CombatUnit));
            Assert.That(query.CalculateEntityCount(), Is.Zero);
        }

        /// <summary>补算跨过命中帧仍只消费一次请求，剩余动画时间不再重复判定。</summary>
        [Test]
        public void CatchUpCrossingMeleeHitDoesNotDuplicateDamage()
        {
            using var world = new World("Melee catchup");
            using var session = CreateSession(world, LoadTables().TbPerformanceScenario.Get(4));
            PrepareMelee(world, session);
            session.Advance(session.StepSeconds, float2.zero);
            var unit = session.ReadUnit(1);
            unit.AttackAge = unit.WindupSeconds + unit.AttackOption.HitSeconds - session.StepSeconds;
            world.EntityManager.SetComponentData(session.UnitEntity(1), unit);
            session.Advance(session.StepSeconds * 4, float2.zero);
            Assert.That(session.Statistics.MeleeJudgements, Is.EqualTo(1));
            Assert.That(session.Statistics.DamageEvents, Is.EqualTo(1));
            session.Advance(session.StepSeconds, float2.zero);
            Assert.That(session.Statistics.MeleeJudgements, Is.EqualTo(1));
        }

        /// <summary>配置测试用 Luban 序列化协议替换前摇，其他字段沿用实际表值。</summary>
        /// <param name="source">原始技能行。</param>
        /// <param name="windup">要覆盖的前摇。</param>
        /// <returns>通过生成代码解析的独立技能配置。</returns>
        private static SkillCombatConfig SkillWithWindup(SkillCombatConfig source, float? windup)
        {
            var buffer = new ByteBuf();
            buffer.WriteInt(source.Id); buffer.WriteInt((int)source.DeliveryType);
            buffer.WriteInt(source.BaseIntervalMilli); buffer.WriteInt(source.RangeMilli);
            buffer.WriteBool(source.ProjectileSpeed.HasValue);
            if (source.ProjectileSpeed.HasValue) buffer.WriteInt(source.ProjectileSpeedMilli.Value);
            buffer.WriteBool(source.ProjectileLifetime.HasValue);
            if (source.ProjectileLifetime.HasValue) buffer.WriteInt(source.ProjectileLifetimeMilli.Value);
            buffer.WriteBool(windup.HasValue);
            if (windup.HasValue) buffer.WriteInt(ConfigNumber.Encode(windup.Value));
            return new SkillCombatConfig(buffer);
        }

        /// <summary>每项时序测试开始前只保留一个重叠近战怪，禁用玩家攻击与随机闪避。</summary>
        /// <param name="world">隔离测试世界。</param>
        /// <param name="session">由真实表创建的会话。</param>
        /// <remarks>只调整测试组件，不修改正式配置或出生模板。</remarks>
        private static void PrepareMelee(World world, CombatSession session)
        {
            var player = session.ReadUnit(0);
            player.Cooldown = 100;
            player.Target.Evasion = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(0), player);
            for (int slot = 1; slot < session.UnitCount; slot++)
            {
                var unit = session.ReadUnit(slot);
                unit.Position = player.Position;
                unit.MoveSpeed = 0;
                unit.Attack.Hit = 1;
                if (slot > 1) unit.Target.Health = 0;
                world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
            }
        }

        /// <summary>隔离测试走正式工厂，资源替身仅将表内 ID 解析到真实 StreamingAssets 文件。</summary>
        /// <param name="world">测试拥有的世界。</param>
        /// <param name="scenario">真实测试场景配置。</param>
        /// <returns>需由测试释放的正式会话。</returns>
        /// <remarks>替身同步完成加载，不等待 Unity 同步上下文；会话创建实体。</remarks>
        private static CombatSession CreateSession(World world, PerformanceScenarioConfig scenario) =>
            CombatSessionFactory.CreateAsync(world, scenario, new TestResources(LoadTables()), CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>测试专用文件资源替身；正式代码不使用本地路径。</summary>
        private sealed class TestResources : IResourceService
        {
            private readonly Tables tables;
            /// <summary>测试初始化时保存真实资源映射。</summary>
            /// <param name="tables">生成表。</param>
            public TestResources(Tables tables) { this.tables = tables; }
            /// <summary>测试加载时将 ID 映射到实际 ANI 并返回立即完成的任务。</summary>
            /// <param name="resourceId">TbResource.id。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>内存句柄。</returns>
            /// <exception cref="IOException">资源文件不可读。</exception>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IRawResourceHandle>(new TestRaw(File.ReadAllBytes(Path.Combine(
                    Application.streamingAssetsPath, tables.TbResource.Get(resourceId).Path))));
            }
            /// <summary>本组测试不加载 Unity 对象，误用立即失败。</summary>
            /// <typeparam name="TAsset">资源类型。</typeparam>
            /// <param name="resourceId">资源 ID。</param>
            /// <param name="cancellationToken">取消令牌。</param>
            /// <returns>本实现不返回。</returns>
            /// <exception cref="NotSupportedException">不支持对象加载。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class => throw new NotSupportedException();
        }

        /// <summary>无 Unity 对象的测试内存句柄。</summary>
        private sealed class TestRaw : IRawResourceHandle
        {
            public byte[] Data { get; private set; }
            /// <summary>文件读取完成后接管测试字节。</summary>
            /// <param name="data">ANI 文件内容。</param>
            public TestRaw(byte[] data) { Data = data; }
            /// <summary>工厂离开加载作用域时清除测试句柄引用。</summary>
            public void Dispose() { Data = null; }
        }

        /// <summary>每个测试从实际生成 bytes 读取完整配置，测试变更只作用隔离 ECS 状态。</summary>
        /// <param name="timedSpawn">为真时保留正式刷怪参数；旧战斗时序夹具使用固定出生模式。</param>
        /// <returns>解析完成的 Luban 表。</returns>
        /// <remarks>只读文件，不创建应用服务或改变源表。</remarks>
        /// <exception cref="IOException">生成 bytes 不可读取。</exception>
        private static Tables LoadTables(bool timedSpawn = false)
        {
            var tables = new Tables(name => new ByteBuf(File.ReadAllBytes(
                Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
            // 既有近战/寻敌夹具需要出生即有固定目标；只修改测试内存副本，不改变正式场景 4。
            if (!timedSpawn)
                foreach (string field in new[] { "SpawnRadiusPixelsMilli", "SpawnIntervalSecondsMilli", "SpawnBatchCount", "SpawnUnitIntervalSecondsMilli" })
                    typeof(PerformanceScenarioConfig).GetField(field).SetValue(tables.TbPerformanceScenario.Get(4), null);
            return tables;
        }
    }
}
