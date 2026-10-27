using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using NUnit.Framework;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;

namespace Roguelike.Tests
{
    /// <summary>正式事件、精英死亡、宝箱交互与进化领取的确定性及原生协调回归；不写正式源表。</summary>
    public sealed class CombatRunEventTests
    {
        /// <summary>验证八普通怪、二精英与十二事件闭包，并比较大步长/分步推进的全部生成序号和时间。</summary>
        [Test]
        public void EventTimelineIsCompleteAndIndependentOfAdvanceChunkSize()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Event deterministic timeline");
            CombatRunDefinition definition = fixture.Definition;
            Assert.That(definition.Monsters.Count(item => !item.IsElite), Is.EqualTo(8));
            Assert.That(definition.Monsters.Count(item => item.IsElite), Is.EqualTo(2));
            Assert.That(definition.Events.Count, Is.EqualTo(12));
            var direct = new CombatRunModel(definition);
            var stepped = new CombatRunModel(definition);
            CombatSpawnRequest[] expected = direct.Advance(definition.Rule.BossTimeMilli).ToArray();
            var actual = new List<CombatSpawnRequest>();
            var fired = new List<int>();
            while (stepped.ElapsedMilli < definition.Rule.BossTimeMilli)
            {
                int delta = (int)Math.Min(7777, definition.Rule.BossTimeMilli - stepped.ElapsedMilli);
                actual.AddRange(stepped.Advance(delta));
                fired.AddRange(stepped.FiredEvents.Select(item => item.Config.Id));
            }
            Assert.That(actual.Select(item => (item.Sequence,item.ScheduledTimeMilli,item.Monster.Id,item.IsBoss)),
                Is.EqualTo(expected.Select(item => (item.Sequence,item.ScheduledTimeMilli,item.Monster.Id,item.IsBoss))));
            Assert.That(fired, Is.EqualTo(definition.Events.Select(item => item.Id)));
            Assert.That(expected.Count(item => item.RunEvent != null), Is.EqualTo(definition.Events.Count(item => item.Kind == ERunEventKind.EliteSpawn)));
            Assert.That(direct.Chests.Count, Is.EqualTo(definition.Events.Count(item => item.Kind == ERunEventKind.MapChest)));
            direct.Advance(1000);
            Assert.That(direct.FiredEvents, Is.Empty, "Boss state must not replay previous map events.");
        }

        /// <summary>逐条通过宝箱领取六条进化，验证原形不再激活、同槽替换、HUD 名称和旧代次拒绝。</summary>
        /// <param name="weaponId">本批正式武器 ID。</param>
        /// <remarks>前置构筑在隔离测试模型中直接准备；不是自然获取证明，领取和 ECS 同步走正式协调器。</remarks>
        [TestCase(50001)] [TestCase(50002)] [TestCase(50003)]
        [TestCase(50004)] [TestCase(50005)] [TestCase(50006)]
        public void EveryEvolutionClaimsThroughChestAndSynchronizesNativeWeapon(int weaponId)
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Chest evolution " + weaponId);
            CombatRunModel model = fixture.Model;
            RunWeaponConfig weapon = model.Build.Config.WeaponIds_Ref.Single(item => item.Id == weaponId);
            while (model.Build.CanUpgrade(weapon)) model.Build.Upgrade(weapon);
            model.Build.Upgrade(weapon.EvolutionId_Ref.PassiveId_Ref);
            int slots = model.Build.WeaponRanks.Count;
            ulong chest = model.RegisterChest(fixture.Definition.ChestProfiles[0]);
            Assert.That(model.TryCollectChest(chest), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.ChestReward));
            Assert.That(model.ChestEvolution.Id, Is.EqualTo(weapon.EvolutionId));
            ulong generation = model.ChestPanelGeneration;
            Assert.That(fixture.Coordinator.ClaimChest(generation,0), Is.True);
            Assert.That(fixture.Coordinator.ClaimChest(generation,0), Is.False);
            Assert.That(model.Build.WeaponRanks.Count, Is.EqualTo(slots));
            Assert.That(model.Build.WeaponDisplayName(weaponId), Is.EqualTo(weapon.EvolutionId_Ref.Name));
            Assert.That(model.ActiveSkills.ContainsKey(weapon.EvolutionId_Ref.ResultSkillId), Is.True);
            Assert.That(model.ActiveSkills.ContainsKey(weapon.Ranks.Last().SkillId), Is.False);
            Assert.That(Enumerable.Range(0,fixture.Session.PlayerWeaponCount).Select(fixture.Session.ReadPlayerWeapon)
                .Single(item => item.SkillId == weapon.EvolutionId_Ref.ResultSkillId).Active, Is.True);
            Assert.That(model.Chests, Is.Empty);
        }

        /// <summary>升级和两个箱在同 tick 到达时升级队列先展示，两个箱依次领取，重复拾取及旧面板均拒绝。</summary>
        [Test]
        public void UpgradeHasPriorityAndMultipleChestRewardsRemainQueued()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Chest interaction queue");
            CombatRunModel model = fixture.Model;
            model.GrantExperience(model.Definition.ExperienceLevels.Take(2).Sum(item => item.RequiredExperience));
            ulong first = model.RegisterChest(fixture.Definition.ChestProfiles[0]);
            ulong second = model.RegisterChest(fixture.Definition.ChestProfiles[0]);
            Assert.That(model.TryCollectChest(first), Is.True);
            Assert.That(model.TryCollectChest(first), Is.False);
            Assert.That(model.TryCollectChest(second), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.UpgradeChoice));
            Assert.That(model.CurrentChest, Is.Null);
            while (model.State == CombatRunState.UpgradeChoice)
                Assert.That(model.ChooseUpgrade(model.UpgradePanelGeneration,model.CurrentChoices[0].Id), Is.True);
            Assert.That(model.CurrentChest.Id, Is.EqualTo(first));
            long time = model.ElapsedMilli;
            Assert.That(model.Advance(9999), Is.Empty);
            Assert.That(model.ElapsedMilli, Is.EqualTo(time));
            ulong old = model.ChestPanelGeneration;
            Assert.That(model.ClaimChest(old,RewardOption(model)), Is.True);
            Assert.That(model.CurrentChest.Id, Is.EqualTo(second));
            Assert.That(model.ClaimChest(old,RewardOption(model)), Is.False);
            Assert.That(model.ClaimChest(model.ChestPanelGeneration,RewardOption(model)), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(model.Chests, Is.Empty);
        }

        /// <summary>候选全部耗尽时空箱仍可领取，结算和重开清空旧句柄与旧面板。</summary>
        /// <remarks>仅在隔离表副本关闭进化以覆盖普通奖励耗尽边界，不修改正式配置。</remarks>
        [Test]
        public void ExhaustedChestCanCloseAndRestartRejectsOldHandles()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Chest exhausted and restart");
            CombatRunModel model = fixture.Model;
            foreach (RunWeaponConfig weapon in model.Build.Config.WeaponIds_Ref)
                while (model.Build.CanUpgrade(weapon)) model.Build.Upgrade(weapon);
            foreach (RunPassiveConfig passive in model.Build.Config.PassiveIds_Ref)
                while (model.Build.CanUpgrade(passive)) model.Build.Upgrade(passive);
            ChestProfileConfig profile = fixture.Definition.ChestProfiles[0];
            typeof(ChestProfileConfig).GetField("AllowEvolution").SetValue(profile,false);
            ulong id = model.RegisterChest(profile);
            model.TryCollectChest(id);
            Assert.That(model.ChestChoices, Is.Empty);
            Assert.That(model.ChestEvolution, Is.Null);
            ulong generation = model.ChestPanelGeneration;
            Assert.That(model.ClaimChest(generation,0), Is.True);
            model.ApplyPlayerDamage(model.MaxHealth);
            Assert.That(model.Restart(), Is.True);
            Assert.That(model.TryCollectChest(id), Is.False);
            Assert.That(model.ClaimChest(generation,0), Is.False);
            Assert.That(model.Chests, Is.Empty);
            Assert.That(model.Build.EvolvedWeapons, Is.Empty);
        }

        /// <summary>原生首次精英死亡产生一箱且不胜利，真实移动拾取后领取普通成长并恢复模拟。</summary>
        /// <remarks>仅降低隔离 ECS 目标生命以控制死亡时机；攻击、死亡、掉箱、距离、暂停和同步均走正式路径。</remarks>
        [Test]
        public void EliteDeathCreatesOneChestAndWalkingClaimsItWithoutVictory()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Elite chest native lifecycle");
            MonsterConfig elite = fixture.Definition.Monsters.First(item => item.IsElite);
            Assert.That(fixture.Session.TrySpawnMonster(elite,fixture.Definition.Map.SpawnRadiusPixels,1,false,out int slot), Is.True);
            CombatUnit monster = fixture.Session.ReadUnit(slot);
            Assert.That(monster.IsElite, Is.True);
            Assert.That(monster.IsBoss, Is.False);
            monster.Position = new float2(2,0); monster.PreviousPosition = monster.Position;
            monster.MoveSpeed = 0; monster.Cooldown = 10000; monster.Target.Health = 1; monster.Target.Evasion = 0;
            ulong lifetime = monster.Target.Lifetime;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(slot),monster);
            for(int tick=0;tick<180 && fixture.Model.Kills==0;tick++) fixture.Coordinator.Advance(fixture.Session.StepSeconds,float2.zero);
            Assert.That(fixture.Model.Kills, Is.EqualTo(1));
            Assert.That(fixture.Model.Result, Is.Null);
            Assert.That(fixture.Coordinator.Chests.Count, Is.EqualTo(1));
            Assert.That(fixture.Model.RecordMonsterDeath(lifetime,false), Is.Zero);
            Assert.That(fixture.Coordinator.Chests.Count, Is.EqualTo(1));
            float2 chestPosition = fixture.Coordinator.Chests.Values.Single().Position;
            for(int tick=0;tick<180 && fixture.Model.State==CombatRunState.Playing;tick++)
                fixture.Coordinator.Advance(fixture.Session.StepSeconds,math.normalizesafe(chestPosition-fixture.Session.ReadUnit(0).Position));
            Assert.That(fixture.Model.State, Is.EqualTo(CombatRunState.ChestReward));
            ulong before = fixture.Session.Statistics.Tick;
            fixture.Coordinator.Advance(1,float2.zero);
            Assert.That(fixture.Session.Statistics.Tick, Is.EqualTo(before));
            Assert.That(fixture.Coordinator.ClaimChest(fixture.Model.ChestPanelGeneration,RewardOption(fixture.Model)), Is.True);
            Assert.That(fixture.Model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(fixture.Coordinator.Chests, Is.Empty);
            Assert.That(fixture.Model.Chests, Is.Empty);
        }

        /// <summary>把第一事件时间压缩到隔离会话首 tick，验证地图箱落在表定极坐标且一次性消费。</summary>
        /// <remarks>正式事件时间在时间线测试验证；本夹具只压缩时间，不注入奖励或调用拾取方法。</remarks>
        [Test]
        public void MapEventCreatesConfiguredPositionOnlyOnce()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Map chest position");
            RunEventConfig item = fixture.Definition.Events.First();
            typeof(RunEventConfig).GetField("TimeMilli").SetValue(item,1);
            float2 origin = fixture.Session.ReadUnit(0).Position;
            fixture.Coordinator.Advance(fixture.Session.StepSeconds,float2.zero);
            Assert.That(fixture.Coordinator.Chests.Count, Is.EqualTo(1));
            var chest = fixture.Coordinator.Chests.Values.Single();
            Assert.That(math.distance(chest.Position,origin), Is.EqualTo(item.SpawnDistancePixels*fixture.Definition.CombatRules.WorldUnitsPerPixel).Within(1e-5));
            fixture.Coordinator.Advance(fixture.Session.StepSeconds,float2.zero);
            Assert.That(fixture.Coordinator.Chests.Keys.Single(), Is.EqualTo(chest.Id));
        }

        /// <summary>两种精英均使用各自正式技能的前摇与实际原生伤害；镇域型在普通近战之外施压。</summary>
        /// <param name="monsterId">TbMonster 的本批独立精英。</param>
        [TestCase(10018)] [TestCase(10026)]
        public void BothEliteSkillsRespectWindupAndDealNativeDamage(int monsterId)
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Elite attack " + monsterId);
            MonsterConfig config = fixture.Definition.Monsters.Single(item => item.Id == monsterId);
            fixture.Session.SynchronizePlayerSkills(Array.Empty<int>());
            Assert.That(fixture.Session.TrySpawnMonster(config,fixture.Definition.Map.SpawnRadiusPixels,1,false,out int slot), Is.True);
            CombatUnit player = fixture.Session.ReadUnit(0);
            CombatUnit elite = fixture.Session.ReadUnit(slot);
            elite.Position = player.Position + new float2(elite.Radius+player.Radius+elite.Range*.75f,0);
            elite.PreviousPosition = elite.Position;
            elite.MoveSpeed = 0; elite.Cooldown = 0;
            fixture.World.EntityManager.SetComponentData(fixture.Session.UnitEntity(slot),elite);
            long health = player.Target.Health;
            fixture.Session.Advance(fixture.Session.StepSeconds,float2.zero);
            Assert.That(fixture.Session.ReadUnit(0).Target.Health, Is.EqualTo(health), "Elite damage skipped configured windup.");
            int bound = (int)Math.Ceiling((config.DefaultSkillId_Ref.CombatProfileId_Ref.BaseInterval*5 +
                config.DefaultSkillId_Ref.CombatProfileId_Ref.AttackWindupSeconds.Value)/fixture.Session.StepSeconds);
            for(int tick=0;tick<bound && fixture.Session.ReadUnit(0).Target.Health==health;tick++)
                fixture.Session.Advance(fixture.Session.StepSeconds,float2.zero);
            Assert.That(fixture.Session.ReadUnit(0).Target.Health, Is.LessThan(health));
            Assert.That(fixture.Session.Statistics.MeleeJudgements, Is.GreaterThan(0));
            Assert.That(fixture.Session.ReadUnit(slot).SkillId, Is.EqualTo(config.DefaultSkillId));
        }

        /// <summary>Boss 战中领箱恢复 Boss 状态；随后最终 Boss 死亡优先结算并废弃所有未领箱。</summary>
        [Test]
        public void BossRewardRestoresBossAndSettlementInvalidatesPendingChests()
        {
            using var fixture = CombatRunCoordinatorTests.CreateFixture("Boss chest resume");
            CombatRunModel model = fixture.Model;
            model.Advance(fixture.Definition.Rule.BossTimeMilli);
            ulong id = model.RegisterChest(fixture.Definition.ChestProfiles[0]);
            model.TryCollectChest(id);
            ulong generation = model.ChestPanelGeneration;
            Assert.That(model.ClaimChest(generation,RewardOption(model)), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.Boss));
            model.RecordMonsterDeath(123,true);
            Assert.That(model.State, Is.EqualTo(CombatRunState.VictorySettlement));
            Assert.That(model.Chests, Is.Empty);
            Assert.That(model.ClaimChest(generation,0), Is.False);
        }

        /// <summary>取得当前箱合法普通候选；进化或明确耗尽时使用协议空载荷。</summary>
        /// <param name="model">当前箱规则状态。</param>
        /// <returns>可提交的普通选项 ID 或零。</returns>
        private static int RewardOption(CombatRunModel model) => model.ChestChoices.Count > 0 ? model.ChestChoices[0].Id : 0;
    }
}
