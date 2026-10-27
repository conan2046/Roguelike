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
    /// <summary>使用真实配置/ANI和独立World验证职业、环境伤害、冻结与重开；边界布置仅属于测试。</summary>
    public sealed class CombatTribulationIntegrationTests
    {
        /// <summary>铁壁只接受有效扣血，保护期不刷新；过期恢复原伤害且老关卡默认关闭。</summary>
        [Test]
        public void GuardChargesOnlyEffectiveHitsAndExpiresWithoutRefreshing()
        {
            var tables = LoadTables(); var config = tables.TbTribulationSignature.DataList.Single(s => s.Kind == ETribulationSignature.Guard);
            var guard = CombatGuardState.Capture(config);
            var target = new DamageTarget { Lifetime = 2, Faction = 1, Health = 100000, MaxHealth = 100000, Guard = guard, IsPlayer = true, DefenseParameter = 1 };
            var attack = new AttackSnapshot { Attack = 100, Hit = 100, Faction = 3, AttackerLifetime = 1 };
            var rules = DamageRules.Capture(tables.TbCombatRules.DataList.First());
            rules.RandomMinBp = rules.RandomMaxBp = 10000; rules.PlayerInvulnerability = 0;
            Assert.That(CombatDamage.Resolve(attack, ref target, 999, true, 0, 1, rules).Outcome, Is.EqualTo(DamageOutcome.Rejected));
            Assert.That(target.Guard.Charge, Is.Zero);
            target.ImmunityCharges = 1;
            Assert.That(CombatDamage.Resolve(attack, ref target, 2, true, 0, 1, rules).Outcome, Is.EqualTo(DamageOutcome.Immune));
            Assert.That(target.Guard.Charge, Is.Zero);
            long normal = 0;
            for (int i = 0; i < config.GuardChargeHits; i++) normal = CombatDamage.Resolve(attack, ref target, 2, true, i, 1, rules).HealthLost;
            Assert.That(target.Guard.Activations, Is.EqualTo(1)); double until = target.Guard.UntilSeconds;
            long reduced = CombatDamage.Resolve(attack, ref target, 2, true, config.GuardChargeHits, 1, rules).HealthLost;
            Assert.That(reduced, Is.EqualTo((long)Math.Floor(normal * config.GuardDamageTakenPermille / 1000d)));
            Assert.That(target.Guard.UntilSeconds, Is.EqualTo(until)); Assert.That(target.Guard.Charge, Is.Zero);
            Assert.That(CombatDamage.Resolve(attack, ref target, 2, true, until, 1, rules).HealthLost, Is.EqualTo(normal));
            Assert.That(target.Guard.Charge, Is.EqualTo(1));
            Assert.That(default(CombatGuardState).IsActive(0), Is.False);
        }

        /// <summary>环境落雷真正命中双方、只伤范围内目标，死亡事件唯一并产生正式区域表现序号。</summary>
        /// <remarks>创建并在using中释放测试World/会话；固定目标只用于几何和伤害边界。</remarks>
        [Test]
        public void EnvironmentalAreaUsesGeometryFactionAndUniqueDeathEvents()
        {
            var tables = LoadTables(); var definition = Entry(tables, ETribulationSignature.Guard);
            using var world = new World("Tribulation area geometry");
            using var session = CreateSession(world, definition, tables);
            Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 1, false, out int near), Is.True);
            Assert.That(session.TrySpawnMonster(definition.Monsters[0], definition.Map.SpawnRadiusPixels, 2, false, out int far), Is.True);
            PlaceMonster(world, session, near, float2.zero, 1); PlaceMonster(world, session, far, new float2(100, 100), 1);
            var rule = definition.Tribulation.RuleId_Ref; var player = session.ReadUnit(0);
            var source = AttackSnapshot.Capture(new CombatAttributes(rule.AttackProfileId_Ref), player.Target.Lifetime, 0, rule.EnvironmentFaction);
            var result = session.ApplyConfiguredArea(player.Position, rule.StrikeRadiusPixels * definition.CombatRules.WorldUnitsPerPixel,
                source, rule.PlayerDamagePermille, rule.EnemyDamagePermille, rule.BossDamagePermille, rule.EffectSkillId_Ref);
            Assert.That(result.EnemyHits, Is.EqualTo(1)); Assert.That(result.Kills, Is.EqualTo(1));
            Assert.That(session.ReadUnit(0).Target.Health, Is.LessThan(player.Target.Health));
            Assert.That(session.ReadUnit(far).Target.Health, Is.EqualTo(1)); Assert.That(session.DeathCount, Is.EqualTo(1));
            var again = session.ApplyConfiguredArea(player.Position, rule.StrikeRadiusPixels * definition.CombatRules.WorldUnitsPerPixel,
                source, rule.PlayerDamagePermille, rule.EnemyDamagePermille, rule.BossDamagePermille, rule.EffectSkillId_Ref);
            Assert.That(again.EnemyHits, Is.Zero); Assert.That(session.DeathCount, Is.EqualTo(1));
            Assert.That(session.ReadArea(1).AttackSequence, Is.GreaterThan(session.ReadArea(0).AttackSequence));
            var sameFaction = source; sameFaction.Faction = player.Target.Faction;
            long health = session.ReadUnit(0).Target.Health;
            session.ApplyConfiguredArea(player.Position, rule.StrikeRadiusPixels * definition.CombatRules.WorldUnitsPerPixel,
                sameFaction, 1000, 1000, 1000, rule.EffectSkillId_Ref);
            Assert.That(session.ReadUnit(0).Target.Health, Is.EqualTo(health));
        }

        /// <summary>周天经权威移动留阵；暂停冻结所有时钟，结算重开清除阵域及旧代次。</summary>
        /// <remarks>测试World和会话在using中释放，不保存场景或修改正式配置。</remarks>
        [Test]
        public void DomainMovementPauseAndRestartUseOneAuthoritativeClock()
        {
            var tables = LoadTables(); var definition = Entry(tables, ETribulationSignature.Domains);
            using var world = new World("Tribulation domain integration"); using var session = CreateSession(world, definition, tables);
            var model = new CombatRunModel(definition); var coordinator = new CombatRunCoordinator(model, session);
            for (int i = 0; i < definition.CombatRules.SimulationHz; i++) coordinator.Advance(session.StepSeconds, new float2(1, 0));
            Assert.That(coordinator.Domains.Domains.Count, Is.GreaterThan(0));
            Assert.That(coordinator.Domains.ElapsedMilli, Is.EqualTo(model.ElapsedMilli));
            long before = model.ElapsedMilli; ulong tick = session.Statistics.Tick; coordinator.TogglePause();
            coordinator.Advance(10, new float2(1, 0));
            Assert.That(session.Statistics.Tick, Is.EqualTo(tick)); Assert.That(coordinator.Tribulation.ElapsedMilli, Is.EqualTo(before));
            var old = coordinator.Tribulation; model.ApplyPlayerDamage(model.CurrentHealth);
            Assert.That(coordinator.Restart(), Is.True); Assert.That(old.State, Is.EqualTo(TribulationTrialState.Terminated));
            Assert.That(coordinator.Domains.Domains, Is.Empty); Assert.That(coordinator.Tribulation.ElapsedMilli, Is.Zero);
            Assert.That(coordinator.Tribulation.RunGeneration, Is.GreaterThan(old.RunGeneration));
        }

        /// <summary>两个职业以受伤生命验证百分比治疗取整；奖励冻结ECS并与升级排队，旧回调不能复领。</summary>
        /// <remarks>夹具World/会话在using中释放；受控伤害和经验仅布置领奖边界，不作为自然游玩证据。</remarks>
        /// <param name="kind">真实职业机制，覆盖最大生命不能整除千分比的职业。</param>
[TestCase(ETribulationSignature.Guard)]
        [TestCase(ETribulationSignature.Domains)]
        public void RefinementFreezesWorldAndSynchronizesActualWeaponOnce(ETribulationSignature kind)
        {
            var tables = LoadTables(); var definition = Entry(tables, kind);
            using var world = new World("Tribulation refinement integration"); using var session = CreateSession(world, definition, tables);
            var model = new CombatRunModel(definition); var coordinator = new CombatRunCoordinator(model, session);
            model.ApplyPlayerDamage(model.CurrentHealth - 25);
            session.SynchronizeRunPlayer(session.ReadUnit(0).Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth);
            var state = coordinator.Tribulation; var altar = state.Config.RuleId_Ref.Altars.First(); var rule = state.Config.RuleId_Ref;
            state.Advance(altar.AvailableAfterMilli, altar.XPixels, altar.YPixels, null);
            state.TryStart(model.RunGeneration, altar.Id, altar.XPixels, altar.YPixels);
            state.Advance(1, 0, 0, null); state.Advance(rule.WarningMilli, 0, 0, null);
            state.RecordEnemyHits(state.DueStrikes.First(), rule.RequiredEnemyHits);
            state.Advance(rule.TrialDurationMilli, 0, 0, null); state.ResolveTrialDeadline();
            Assert.That(state.State, Is.EqualTo(TribulationTrialState.RewardPending));
            ulong tick = session.Statistics.Tick; coordinator.Advance(10, new float2(1, 0));
            Assert.That(session.Statistics.Tick, Is.EqualTo(tick));
            var choice = state.Config.RefinementIds_Ref.First(); ulong reward = state.RewardGeneration;
            model.GrantExperience(definition.ExperienceLevels[model.Level - 1].RequiredExperience);
            Assert.That(coordinator.ChooseRefinement(model.RunGeneration, reward, choice.Id), Is.False);
            Assert.That(state.State, Is.EqualTo(TribulationTrialState.RewardPending));
            Assert.That(coordinator.ChooseUpgrade(model.UpgradePanelGeneration, model.CurrentChoices[0].Id), Is.True);
            Assert.That(session.Paused, Is.True, "Refinement must remain frozen after the other panel closes.");
            long expectedHealth = (long)Math.Min(model.MaxHealth, model.CurrentHealth + Math.Floor(model.MaxHealth * rule.SuccessHealPermille / 1000d));
            Assert.That(coordinator.ChooseRefinement(model.RunGeneration, reward, choice.Id), Is.True);
            Assert.That(session.ReadUnit(0).Target.Health, Is.EqualTo(expectedHealth));
            Assert.That(Enumerable.Range(0, session.PlayerWeaponCount).Select(session.ReadPlayerWeapon).Any(w => w.Active && w.SkillId == choice.ResultSkillId), Is.True);
            Assert.That(coordinator.ChooseRefinement(model.RunGeneration, reward, choice.Id), Is.False);
            Assert.That(coordinator.Advance(session.StepSeconds, float2.zero), Is.EqualTo(1));
        }

        /// <summary>从真实入口按机制寻找职业，避免测试复制正式关卡ID。</summary>
        /// <param name="tables">本次加载的正式表。</param><param name="kind">目标机制。</param><returns>完整的正式单局定义。</returns>
        private static CombatRunDefinition Entry(Tables tables, ETribulationSignature kind) => CombatRunDefinition.Create(tables,
            tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry).StageIds.Select(tables.TbStage.Get)
                .Single(s => s.TribulationSliceId_Ref.SignatureId_Ref.Kind == kind).Id);

        /// <summary>只在测试中布置目标，隔离几何、阵营及重复死亡边界，不作为自然游玩证据。</summary>
        /// <param name="world">测试World。</param><param name="session">正式会话。</param><param name="slot">目标槽。</param>
        /// <param name="position">测试位置。</param><param name="health">测试生命。</param>
        /// <remarks>直接修改测试实体；不写资源、不进入正式入口。</remarks>
        private static void PlaceMonster(World world, CombatSession session, int slot, float2 position, long health)
        {
            var unit = session.ReadUnit(slot); unit.Position = unit.PreviousPosition = position;
            unit.Target.Health = health; unit.Target.Evasion = 0;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), unit);
        }

        /// <summary>读取项目当前生成bytes，所有强引用由Luban解析。</summary><returns>本次独立表对象。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config/Luban", name + ".bytes"))));

        /// <summary>通过正式工厂读取真实ANI创建本测试World会话。</summary>
        /// <param name="world">测试所有者World。</param><param name="definition">正式关卡。</param><param name="tables">真实表。</param><returns>由调用方释放的会话。</returns>
        private static CombatSession CreateSession(World world, CombatRunDefinition definition, Tables tables) =>
            CombatSessionFactory.CreateAsync(world, definition, new RawResources(tables), CancellationToken.None).GetAwaiter().GetResult();

        private sealed class RawResources : IResourceService
        {
            private readonly Tables tables;
            /// <summary>保留测试资源ID映射。</summary><param name="tables">正式表。</param>
            public RawResources(Tables tables) { this.tables = tables; }
            /// <summary>立即读取真实ANI，避免测试阻塞Unity同步上下文。</summary>
            /// <param name="resourceId">正式资源ID。</param><param name="cancellationToken">取消令牌。</param><returns>短期内存句柄。</returns>
            public Task<IRawResourceHandle> LoadRawFileAsync(int resourceId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IRawResourceHandle>(new Raw(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, tables.TbResource.Get(resourceId).Path))));
            }
            /// <summary>本测试不加载Unity资源；误用立即失败。</summary>
            /// <typeparam name="TAsset">资源类型。</typeparam><param name="resourceId">资源ID。</param><param name="cancellationToken">取消令牌。</param>
            /// <returns>不会返回。</returns><exception cref="NotSupportedException">测试只支持ANI。</exception>
            public Task<IResourceHandle<TAsset>> LoadAssetAsync<TAsset>(int resourceId, CancellationToken cancellationToken) where TAsset : class => throw new NotSupportedException();
        }
        private sealed class Raw : IRawResourceHandle
        {
            public byte[] Data { get; private set; }
            /// <summary>接管读取出的字节。</summary><param name="data">真实ANI字节。</param>
            public Raw(byte[] data) { Data = data; }
            /// <summary>工厂解析完成后解除引用。</summary><remarks>不持有Unity对象或磁盘句柄。</remarks>
            public void Dispose() { Data = null; }
        }


/// <summary>六个正式候选逐一隔离其他开局武器后进入真实ECS投递并扣血；固定靶仅用于投递边界，不代替自然渡劫证据。</summary>
        /// <remarks>使用真实源表与ANI创建临时World/会话，using统一回收；不创建或保存预览资产。</remarks>
        /// <param name="refinementId">已公布的TbTribulationRefinement候选ID。</param>
        [TestCase(71001)]
        [TestCase(71002)]
        [TestCase(71003)]
        [TestCase(72001)]
        [TestCase(72002)]
        [TestCase(72003)]
        public void EveryRefinementReachesNativeDeliveryAndDealsDamage(int refinementId)
        {
            var tables = LoadTables(); var refinement = tables.TbTribulationRefinement.Get(refinementId);
            var slice = tables.TbTribulationSlice.DataList.Single(s => s.RefinementIds.Contains(refinementId));
            var definition = CombatRunDefinition.Create(tables, slice.StageId);
            using var world = new World("Refinement native delivery " + refinementId);
            using var session = CreateSession(world, definition, tables);
            var model = new CombatRunModel(definition);
            var coordinator = new CombatRunCoordinator(model, session);
            Assert.That(model.ApplyTribulationRefinement(refinement), Is.True);
            var refinedWeapons = model.Build.CaptureWeapons().Where(weapon => weapon.SkillId == refinement.ResultSkillId).ToArray();
            session.SynchronizePlayerSkills(new[] { refinement.ResultSkillId }, refinedWeapons);
            session.ApplyWeaponUpgrades(refinedWeapons, model.AttributeSnapshot);
            Assert.That(session.TrySpawnMonster(definition.Monsters.First(), definition.Map.SpawnRadiusPixels, 1, false, out int slot), Is.True);
            var target = session.ReadUnit(slot); float scale = definition.CombatRules.WorldUnitsPerPixel;
            var activeWeapon = Enumerable.Range(0, session.PlayerWeaponCount).Select(session.ReadPlayerWeapon).Single(w => w.Active);
            float targetDistance = activeWeapon.RingCount > 0 ? activeWeapon.AreaRadius / activeWeapon.RingCount : 80 * scale;
            target.Position = target.PreviousPosition = session.ReadUnit(0).Position + session.ReadUnit(0).BodyOffset + new float2(targetDistance, 0);
            target.BodyOffset = float2.zero; target.BodyHalfSegment = 0; target.Radius = 8 * scale;
            target.MoveSpeed = 0; target.Cooldown = 1000; target.Target.Evasion = 0;
            target.Target.Health = target.Target.MaxHealth = 1000000;
            world.EntityManager.SetComponentData(session.UnitEntity(slot), target);
            bool sawArea = false; int largestSameCastLayers = 0;
            for (int tick = 0; tick < definition.CombatRules.SimulationHz * 3; tick++)
            {
                coordinator.Advance(session.StepSeconds, float2.zero);
                int layers = 0;
                for (int index = 0; index < session.AreaCount; index++)
                    if (session.ReadArea(index).SkillId == refinement.ResultSkillId) { sawArea = true; layers++; }
                largestSameCastLayers = Math.Max(largestSameCastLayers, layers);
            }
            Assert.That(session.ReadUnit(slot).Target.Health, Is.LessThan(1000000));
            if (refinement.ResultSkillId_Ref.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile)
                Assert.That(session.Statistics.ProjectileSpawns, Is.GreaterThan(0));
            else Assert.That(sawArea, Is.True);
            if (refinement.ResultSkillId_Ref.CombatProfileId_Ref.AreaRingCount > 1)
                Assert.That(largestSameCastLayers, Is.GreaterThanOrEqualTo(refinement.ResultSkillId_Ref.CombatProfileId_Ref.AreaRingCount));
        }

/// <summary>受控Boss生命边界下，第三阶段真正投递范围伤害并清除寿命未到的阵域；不冒充自然通关。</summary>
        /// <remarks>测试专属World/实体在using中释放。只在夹具布置阶段定位Boss生命，后续由正式协调器推进预警、暂停、伤害及清阵。</remarks>
        [Test]
        public void BossThirdPhaseClearsLiveDomainsThroughNativeStrike()
        {
            var tables = LoadTables(); var definition = Entry(tables, ETribulationSignature.Domains);
            using var world = new World("Tribulation native boss clear"); using var session = CreateSession(world, definition, tables);
            var model = new CombatRunModel(definition); var coordinator = new CombatRunCoordinator(model, session);
            var signature = definition.Tribulation.SignatureId_Ref; var rule = definition.Tribulation.RuleId_Ref;
            int movementTicks = (int)Math.Ceiling(signature.DomainStepPixels * definition.CombatRules.WorldUnitsPerPixel / session.ReadUnit(0).MoveSpeed / session.StepSeconds) + 2;
            for (int tick = 0; tick < movementTicks; tick++) coordinator.Advance(session.StepSeconds, new float2(1, 0));
            Assert.That(coordinator.Domains.Domains.Count, Is.EqualTo(1));
            var field = coordinator.Domains.Domains.Single();
            Assert.That(session.TrySpawnMonster(definition.Boss.MonsterId_Ref, definition.Boss.SpawnRadiusPixels, 1, true, out int slot), Is.True);
            var boss = session.ReadUnit(slot); var phase = rule.BossPhases.Last();
            boss.Target.Health = boss.Target.MaxHealth * phase.HealthPermille / 1000;
            boss.MoveSpeed = 0; boss.Cooldown = 1000; world.EntityManager.SetComponentData(session.UnitEntity(slot), boss);
            coordinator.Advance(session.StepSeconds, float2.zero);
            Assert.That(coordinator.Tribulation.BossPhase, Is.EqualTo(rule.BossPhases.Count));
            Assert.That(coordinator.Tribulation.Warnings.Count, Is.EqualTo(phase.StrikeCount));
            long elapsed = coordinator.Tribulation.ElapsedMilli; coordinator.TogglePause();
            Assert.That(coordinator.Advance(phase.WarningMilli / 1000d * 2, float2.zero), Is.Zero);
            Assert.That(coordinator.Tribulation.ElapsedMilli, Is.EqualTo(elapsed));
            Assert.That(coordinator.Domains.Domains.Single().Id, Is.EqualTo(field.Id)); coordinator.TogglePause();
            int clearingStrikes = 0;
            for (int tick = 0; tick < Math.Ceiling(phase.WarningMilli / 1000d / session.StepSeconds) + 3; tick++)
            {
                coordinator.Advance(session.StepSeconds, float2.zero);
                clearingStrikes += coordinator.Tribulation.DueStrikes.Count(s => s.FromBoss && s.ClearsDomains);
            }
            Assert.That(clearingStrikes, Is.GreaterThan(0));
            Assert.That(coordinator.Domains.ElapsedMilli - field.BornMilli, Is.LessThan(signature.DomainDurationMilli));
            Assert.That(coordinator.Domains.ExplosionCount, Is.Zero);
            Assert.That(coordinator.Domains.Domains.Any(d => d.Id == field.Id), Is.False);
            Assert.That(session.ReadUnit(0).Target.Health, Is.LessThan(session.ReadUnit(0).Target.MaxHealth));
        }

}
}
