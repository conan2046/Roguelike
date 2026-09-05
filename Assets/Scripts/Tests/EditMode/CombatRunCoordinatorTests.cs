using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Core.Resources;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Roguelike.Features.Combat.Runtime;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>正式单局协调器跨纯逻辑模型与 ECS 会话的确定性集成回归。</summary>
    public sealed class CombatRunCoordinatorTests
    {
        /// <summary>验证固定 tick 精确换算到 4.8 秒首波，并将表内真实怪物请求落入动态会话。</summary>
        [Test]
        public void FirstConfiguredSpawnLandsAtExactRunTime()
        {
            using var fixture = CreateFixture("Coordinator first spawn");
            int firstSpawnMilli = fixture.Definition.SpawnPhases[0].WaveIntervalMilli;
            int ticks = firstSpawnMilli * fixture.Definition.CombatRules.SimulationHz / 1000;

            AdvanceTicks(fixture.Coordinator, ticks - 1);
            Assert.That(fixture.Model.ElapsedMilli, Is.LessThan(firstSpawnMilli));
            Assert.That(fixture.Session.SpawnedMonsters, Is.Zero);

            AdvanceTicks(fixture.Coordinator, 1);
            Assert.That(fixture.Model.ElapsedMilli, Is.EqualTo(firstSpawnMilli));
            Assert.That(fixture.Session.SpawnedMonsters, Is.EqualTo(1));
            Assert.That(fixture.Definition.Monsters.Select(item => item.Id),
                Does.Contain(fixture.Session.ReadUnit(1).ConfigId));
        }

        /// <summary>验证五个正式技能拥有独立状态，三枚弹丸与两次目标位置范围释放在同 tick 分别产生。</summary>
        [Test]
        public void AllConfiguredWeaponsCastProjectileAndTargetAreaIndependently()
        {
            using var fixture = CreateFixture("Coordinator all player weapons");
            fixture.Session.SynchronizePlayerSkills(fixture.Definition.AvailableSkills.Select(item => item.Id));
            Assert.That(fixture.Session.PlayerWeaponCount, Is.EqualTo(5));
            Assert.That(Enumerable.Range(0, fixture.Session.PlayerWeaponCount)
                .Count(index => fixture.Session.ReadPlayerWeapon(index).Active), Is.EqualTo(5));

            int[] slots = new int[3];
            for (int index = 0; index < slots.Length; index++)
                Assert.That(fixture.Session.TrySpawnMonster(fixture.Definition.Monsters[index],
                    ConfigNumber.Decode(fixture.Definition.Map.SpawnRadiusPixelsMilli), (ulong)(index + 1), false,
                    out slots[index]), Is.True);
            float2[] positions = { new float2(2, 0), new float2(2.4f, 0), new float2(5.5f, 0) };
            long[] health = new long[slots.Length];
            for (int index = 0; index < slots.Length; index++)
            {
                CombatUnit monster = fixture.Session.ReadUnit(slots[index]);
                monster.Position = positions[index];
                monster.PreviousPosition = positions[index];
                monster.MoveSpeed = 0;
                monster.Cooldown = 1000;
                monster.Target.Evasion = 0;
                health[index] = monster.Target.Health;
                fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(slots[index]), monster);
            }

            Assert.That(fixture.Coordinator.Advance(fixture.Session.StepSeconds, float2.zero), Is.EqualTo(1));
            Assert.That(fixture.Session.Statistics.Attacks, Is.EqualTo(5));
            Assert.That(fixture.Session.Statistics.ProjectileSpawns, Is.EqualTo(3));
            Assert.That(fixture.Session.Statistics.AreaCasts, Is.EqualTo(2));
            Assert.That(fixture.Session.AreaCount, Is.EqualTo(2));
            Assert.That(Enumerable.Range(0, fixture.Session.AreaCount)
                .Select(index => fixture.Session.ReadArea(index).SkillId), Is.EquivalentTo(new[] { 20028, 20043 }));
            Assert.That(fixture.Session.ReadUnit(slots[0]).Target.Health, Is.LessThan(health[0]));
            Assert.That(fixture.Session.ReadUnit(slots[1]).Target.Health, Is.LessThan(health[1]));
            Assert.That(fixture.Session.ReadUnit(slots[2]).Target.Health, Is.EqualTo(health[2]));
            Assert.That(Enumerable.Range(0, fixture.Session.PlayerWeaponCount)
                .All(index => fixture.Session.ReadPlayerWeapon(index).Cooldown > 0), Is.True);
        }

        /// <summary>验证怪物 ECS 死亡只创建一份掉落，吸附拾取后经验只结算一次。</summary>
        [Test]
        public void MonsterDeathCreatesOneDropAndPickupOnce()
        {
            using var fixture = CreateFixture("Coordinator drop lifecycle");
            AdvanceToFirstSpawn(fixture);
            PreparePlayerKill(fixture, 1, new float2(1, 0));

            AdvanceUntil(fixture.Coordinator, () => fixture.Model.Kills == 1, 180);
            Assert.That(fixture.Coordinator.Drops.Count, Is.EqualTo(1));
            Assert.That(fixture.Model.Drops.Count, Is.EqualTo(1));
            ulong dropId = fixture.Coordinator.Drops.Keys.Single();

            AdvanceUntil(fixture.Coordinator, () => fixture.Coordinator.Drops.Count == 0, 180);
            Assert.That(fixture.Model.Experience, Is.EqualTo(fixture.Definition.Drop.ExperienceValue));
            Assert.That(fixture.Model.TryCollectDrop(dropId), Is.False);
            Assert.That(fixture.Model.Kills, Is.EqualTo(1));
        }

        /// <summary>验证升级选择冻结时钟，并将任一属性升级后的完整快照同步到 ECS 玩家。</summary>
        [Test]
        public void UpgradeFreezesAndSynchronizesPlayerAttributes()
        {
            using var fixture = CreateFixture("Coordinator upgrade synchronization");
            UpgradeOptionConfig attribute = null;
            for (int attempt = 0; attempt < 8 && attribute == null; attempt++)
            {
                GrantNextLevel(fixture.Model);
                attribute = fixture.Model.CurrentChoices.FirstOrDefault(item => item.Type == EUpgradeOptionType.Attribute);
                if (attribute == null)
                    Assert.That(fixture.Coordinator.ChooseUpgrade(fixture.Model.UpgradePanelGeneration,
                        fixture.Model.CurrentChoices[0].Id), Is.True);
            }
            Assert.That(attribute, Is.Not.Null);
            long frozenTime = fixture.Model.ElapsedMilli;
            Assert.That(fixture.Coordinator.Advance(1, new float2(1, 0)), Is.Zero);
            Assert.That(fixture.Model.ElapsedMilli, Is.EqualTo(frozenTime));

            Assert.That(fixture.Coordinator.ChooseUpgrade(fixture.Model.UpgradePanelGeneration, attribute.Id), Is.True);
            CombatAttributeSnapshot expected = fixture.Model.AttributeSnapshot;
            CombatUnit player = fixture.Session.ReadUnit(0);
            Assert.That(player.Attack.Attack, Is.EqualTo(expected.Attack).Within(1e-9));
            Assert.That(player.Target.MaxHealth, Is.EqualTo((long)expected.MaxHealth));
            Assert.That(player.MoveSpeed, Is.EqualTo((float)expected.MoveSpeed).Within(1e-6));
            Assert.That(player.Interval, Is.EqualTo(CombatMath.AttackInterval(
                fixture.Definition.InitialSkills[0].CombatProfileId_Ref.BaseInterval,
                expected.AttackSpeedMultiplier, fixture.Definition.CombatRules.MinAttackInterval)).Within(1e-9));
        }

        /// <summary>验证三选一解锁技能后，协调器只激活对应独立武器且不重置已有武器。</summary>
        [Test]
        public void SkillUnlockActivatesOneFormalWeapon()
        {
            using var fixture = CreateFixture("Coordinator skill unlock");
            Assert.That(Enumerable.Range(0, fixture.Session.PlayerWeaponCount)
                .Count(index => fixture.Session.ReadPlayerWeapon(index).Active), Is.EqualTo(1));
            UpgradeOptionConfig unlock = null;
            for (int attempt = 0; attempt < 5 && unlock == null; attempt++)
            {
                GrantNextLevel(fixture.Model);
                unlock = fixture.Model.CurrentChoices.FirstOrDefault(item =>
                    item.Type == EUpgradeOptionType.SkillUnlock);
                if (unlock == null)
                    Assert.That(fixture.Coordinator.ChooseUpgrade(fixture.Model.UpgradePanelGeneration,
                        fixture.Model.CurrentChoices[0].Id), Is.True);
            }
            Assert.That(unlock, Is.Not.Null);

            Assert.That(fixture.Coordinator.ChooseUpgrade(fixture.Model.UpgradePanelGeneration, unlock.Id), Is.True);
            Assert.That(Enumerable.Range(0, fixture.Session.PlayerWeaponCount)
                .Count(index => fixture.Session.ReadPlayerWeapon(index).Active), Is.EqualTo(2));
            int weaponIndex = Enumerable.Range(0, fixture.Session.PlayerWeaponCount)
                .Single(index => fixture.Session.ReadPlayerWeapon(index).SkillId == unlock.TargetSkillId.Value);
            Assert.That(fixture.Session.ReadPlayerWeapon(weaponIndex).Active, Is.True);
        }

        /// <summary>验证 18 分钟边界先落地阶段末只普通怪再生成唯一 Boss，死亡胜利后重开清空所有运行态。</summary>
        [Test]
        public void BossVictoryAndRestartResetRuntimeState()
        {
            using var fixture = CreateFixture("Coordinator boss settlement");
            int oneTickMilli = 1000 / fixture.Definition.CombatRules.SimulationHz;
            fixture.Model.Advance(fixture.Definition.Rule.BossTimeMilli - oneTickMilli);

            AdvanceUntil(fixture.Coordinator, () => fixture.Model.State == CombatRunState.Boss, 4);
            Assert.That(fixture.Session.Statistics.AliveMonsters, Is.EqualTo(2));
            Assert.That(Enumerable.Range(1, fixture.Session.UnitCount - 1)
                .Count(slot => !fixture.Session.ReadUnit(slot).IsBoss && fixture.Session.ReadUnit(slot).Target.Health > 0), Is.EqualTo(1));
            int bossSlot = Enumerable.Range(1, fixture.Session.UnitCount - 1)
                .Single(slot => fixture.Session.ReadUnit(slot).IsBoss && fixture.Session.ReadUnit(slot).Target.Health > 0);
            PreparePlayerKill(fixture, bossSlot, new float2(1, 0));

            AdvanceUntil(fixture.Coordinator, () => fixture.Model.State == CombatRunState.VictorySettlement, 180);
            Assert.That(fixture.Model.Result, Is.EqualTo(ERunResultType.Victory));
            Assert.That(fixture.Coordinator.Advance(1, float2.zero), Is.Zero);
            Assert.That(fixture.Coordinator.Restart(), Is.True);
            Assert.That(fixture.Model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(fixture.Model.ElapsedMilli, Is.Zero);
            Assert.That(fixture.Session.Statistics.AliveMonsters, Is.Zero);
            Assert.That(fixture.Session.ReadUnit(0).Target.Health, Is.EqualTo((long)fixture.Model.MaxHealth));
            Assert.That(fixture.Coordinator.Drops, Is.Empty);
            Assert.That(fixture.Coordinator.PendingSpawnCount, Is.Zero);
        }

        /// <summary>验证 Boss 到点会取消因出生圆周占位而阻塞的普通请求，解堵后只落地唯一 Boss。</summary>
        [Test]
        public void BossBoundaryCancelsBlockedNormalSpawnBeforeUniqueBoss()
        {
            using var fixture = CreateFixture("Coordinator blocked spawn at boss boundary");
            int oneTickMilli = 1000 / fixture.Definition.CombatRules.SimulationHz;
            fixture.Model.Advance(fixture.Definition.Rule.BossTimeMilli - oneTickMilli);
            CombatUnit player = fixture.Session.ReadUnit(0);
            var originalMovement = player.Movement;
            player.Movement.Radius = ConfigNumber.Decode(fixture.Definition.Map.SpawnRadiusPixelsMilli) + 100;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(0), player);

            Assert.That(fixture.Coordinator.Advance(fixture.Session.StepSeconds, float2.zero), Is.EqualTo(1));
            Assert.That(fixture.Model.State, Is.EqualTo(CombatRunState.Boss));
            Assert.That(fixture.Coordinator.PendingSpawnCount, Is.EqualTo(1));
            Assert.That(fixture.Session.Statistics.AliveMonsters, Is.Zero);

            player = fixture.Session.ReadUnit(0);
            player.Movement = originalMovement;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(0), player);
            Assert.That(fixture.Coordinator.Advance(fixture.Session.StepSeconds, float2.zero), Is.EqualTo(1));
            Assert.That(fixture.Coordinator.PendingSpawnCount, Is.Zero);
            Assert.That(fixture.Session.Statistics.AliveMonsters, Is.EqualTo(1));
            Assert.That(Enumerable.Range(1, fixture.Session.UnitCount - 1)
                .Single(slot => fixture.Session.ReadUnit(slot).Target.Health > 0),
                Is.EqualTo(Enumerable.Range(1, fixture.Session.UnitCount - 1)
                    .Single(slot => fixture.Session.ReadUnit(slot).IsBoss && fixture.Session.ReadUnit(slot).Target.Health > 0)));
        }

        /// <summary>推进到 TbSpawnPhase 第一条请求已由协调器消费。</summary>
        /// <param name="fixture">共享正式定义、模型与 ECS 会话的测试夹具。</param>
        private static void AdvanceToFirstSpawn(CoordinatorFixture fixture)
        {
            int ticks = fixture.Definition.SpawnPhases[0].WaveIntervalMilli *
                fixture.Definition.CombatRules.SimulationHz / 1000;
            AdvanceTicks(fixture.Coordinator, ticks);
            Assert.That(fixture.Session.SpawnedMonsters, Is.EqualTo(1));
        }

        /// <summary>把指定怪物放入玩家弹丸路径，并只修改隔离 ECS 测试世界中的热数据。</summary>
        /// <param name="fixture">拥有实体管理器的测试夹具。</param>
        /// <param name="monsterSlot">当前存活怪物槽。</param>
        /// <param name="position">相对玩家的测试位置。</param>
        private static void PreparePlayerKill(CoordinatorFixture fixture, int monsterSlot, float2 position)
        {
            CombatUnit player = fixture.Session.ReadUnit(0);
            player.Cooldown = 0;
            player.Attack.Attack = Math.Max(player.Attack.Attack, 1000000);
            player.Attack.Hit = 1;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(0), player);

            CombatUnit monster = fixture.Session.ReadUnit(monsterSlot);
            monster.Position = player.Position + position;
            monster.PreviousPosition = monster.Position;
            monster.MoveSpeed = 0;
            monster.Cooldown = 1000;
            monster.Target.Health = 1;
            monster.Target.Evasion = 0;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(monsterSlot), monster);
        }

        /// <summary>逐 tick 推进，直到条件成立或达到上限。</summary>
        /// <param name="coordinator">待推进的正式协调器。</param>
        /// <param name="condition">每个 tick 后检查的完成条件。</param>
        /// <param name="maximumTicks">失败前允许的最大 tick 数。</param>
        /// <exception cref="AssertionException">达到上限仍未满足条件。</exception>
        private static void AdvanceUntil(CombatRunCoordinator coordinator, Func<bool> condition, int maximumTicks)
        {
            for (int tick = 0; tick < maximumTicks && !condition(); tick++)
                coordinator.Advance(coordinator.Session.StepSeconds, float2.zero);
            Assert.That(condition(), Is.True, "Coordinator condition did not complete within the configured test ticks.");
        }

        /// <summary>按会话表内 simulationHz 执行指定数量固定 tick。</summary>
        /// <param name="coordinator">待推进的正式协调器。</param>
        /// <param name="ticks">非负 tick 数。</param>
        private static void AdvanceTicks(CombatRunCoordinator coordinator, int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
                Assert.That(coordinator.Advance(coordinator.Session.StepSeconds, float2.zero), Is.EqualTo(1));
        }

        /// <summary>给予当前等级恰好一级经验，使模型生成下一代升级候选。</summary>
        /// <param name="model">当前处于战斗状态的单局模型。</param>
        private static void GrantNextLevel(CombatRunModel model)
        {
            model.GrantExperience(model.Definition.ExperienceLevels[model.Level - 1].RequiredExperience);
        }

        /// <summary>从正式第一关配置、真实 ANI 与隔离 ECS World 创建协调器夹具。</summary>
        /// <param name="worldName">便于失败日志定位的世界名称。</param>
        /// <returns>负责按正确顺序释放会话与 World 的夹具。</returns>
        private static CoordinatorFixture CreateFixture(string worldName)
        {
            Tables tables = LoadTables();
            CombatRunDefinition definition = CombatRunDefinition.Create(tables, 1);
            var world = new World(worldName);
            try
            {
                CombatSession session = CombatSessionFactory.CreateAsync(world, definition,
                    new TestResources(tables), CancellationToken.None).GetAwaiter().GetResult();
                var model = new CombatRunModel(definition);
                return new CoordinatorFixture(world, definition, model, session,
                    new CombatRunCoordinator(model, session));
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }

        /// <summary>从 StreamingAssets 读取并解析已生成的 Luban bytes。</summary>
        /// <returns>已经解析引用关系的只读配置表集合。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(
            Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));

        /// <summary>统一持有协调器测试的 ECS World、定义、模型和会话。</summary>
        private sealed class CoordinatorFixture : IDisposable
        {
            public World World { get; }
            public CombatRunDefinition Definition { get; }
            public CombatRunModel Model { get; }
            public CombatSession Session { get; }
            public CombatRunCoordinator Coordinator { get; }

            /// <summary>保存测试资源所有权；调用方必须通过 using 释放。</summary>
            /// <param name="world">测试拥有的隔离 ECS World。</param>
            /// <param name="definition">正式关卡聚合定义。</param>
            /// <param name="model">纯逻辑单局模型。</param>
            /// <param name="session">ECS 战斗会话。</param>
            /// <param name="coordinator">待验证协调器。</param>
            public CoordinatorFixture(World world, CombatRunDefinition definition, CombatRunModel model,
                CombatSession session, CombatRunCoordinator coordinator)
            {
                World = world;
                Definition = definition;
                Model = model;
                Session = session;
                Coordinator = coordinator;
            }

            /// <summary>先释放会话持有的原生容器和实体，再销毁测试 World。</summary>
            public void Dispose()
            {
                Session.Dispose();
                World.Dispose();
            }
        }

        /// <summary>将 TbResource ANI ID 映射到 StreamingAssets 真实字节的测试资源服务。</summary>
        private sealed class TestResources : IResourceService
        {
            private readonly Tables tables;

            /// <summary>保存与正式定义相同的配置引用映射。</summary>
            /// <param name="tables">已解析的 Luban 表。</param>
            public TestResources(Tables tables) { this.tables = tables; }

            /// <summary>同步读取真实 ANI 并包装成立即完成的异步句柄。</summary>
            /// <param name="resourceId">TbResource.id。</param>
            /// <param name="cancellationToken">测试取消令牌。</param>
            /// <returns>持有 ANI 字节的测试句柄。</returns>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IRawResourceHandle>(new TestRaw(File.ReadAllBytes(Path.Combine(
                    Application.streamingAssetsPath, tables.TbResource.Get(resourceId).Path))));
            }

            /// <summary>本组测试不加载 UnityEngine.Object；误用立即失败。</summary>
            /// <typeparam name="TAsset">资源对象类型。</typeparam>
            /// <param name="resourceId">TbResource.id。</param>
            /// <param name="cancellationToken">测试取消令牌。</param>
            /// <returns>此实现不会返回句柄。</returns>
            /// <exception cref="NotSupportedException">协调器创建阶段不允许对象资源加载。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId,
                CancellationToken cancellationToken) where TAsset : class => throw new NotSupportedException();
        }

        /// <summary>仅在资源工厂调用期间持有真实 ANI 字节的测试句柄。</summary>
        private sealed class TestRaw : IRawResourceHandle
        {
            public byte[] Data { get; private set; }

            /// <summary>接管资源服务读取出的 ANI 字节。</summary>
            /// <param name="data">非空 ANI 内容。</param>
            public TestRaw(byte[] data) { Data = data; }

            /// <summary>释放后清除字节引用，避免跨测试保留大数组。</summary>
            public void Dispose() { Data = null; }
        }
    }
}
