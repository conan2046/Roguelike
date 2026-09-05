using System;
using System.IO;
using System.Linq;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>正式 TbStage 单局定义与无 Unity 状态机的高频确定性回归。</summary>
    public sealed class CombatRunModelTests
    {
        /// <summary>验证正式关卡可以从生成 bytes 聚合全部 M1 核心引用。</summary>
        [Test]
        public void DefinitionLoadsFormalStage()
        {
            var definition = CreateDefinition();
            Assert.That(definition.Stage.Id, Is.EqualTo(1));
            Assert.That(definition.SpawnPhases.Count, Is.EqualTo(4));
            Assert.That(definition.Monsters.Select(item => item.Id), Is.EquivalentTo(new[] { 10010, 10011, 10012, 10013 }));
            Assert.That(definition.Boss.MonsterId, Is.EqualTo(10032));
            Assert.That(definition.InitialSkills.Select(item => item.Id), Is.EqualTo(new[] { 20001 }));
            Assert.That(definition.AvailableSkills.Select(item => item.Id), Is.EquivalentTo(new[] { 20001, 20014, 20028, 20032, 20043 }));
        }

        /// <summary>验证第一波等待、阶段边界取消和新阶段独立等待均读取 TbSpawnPhase。</summary>
        [Test]
        public void SpawnCadenceUsesCurrentPhaseAtBoundaries()
        {
            var definition = CreateDefinition();
            var model = new CombatRunModel(definition);
            Assert.That(model.Advance(definition.SpawnPhases[0].WaveIntervalMilli - 1), Is.Empty);
            var first = model.Advance(1);
            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(first[0].ScheduledTimeMilli, Is.EqualTo(definition.SpawnPhases[0].WaveIntervalMilli));

            var toBoundary = model.Advance(definition.SpawnPhases[0].EndTimeMilli - (int)model.ElapsedMilli);
            Assert.That(toBoundary.Last().ScheduledTimeMilli, Is.EqualTo(definition.SpawnPhases[0].EndTimeMilli));
            Assert.That(model.CompletedWaves, Is.EqualTo(30));
            Assert.That(model.ElapsedMilli, Is.EqualTo(definition.SpawnPhases[1].BeginTimeMilli));
            Assert.That(model.Advance(definition.SpawnPhases[1].WaveIntervalMilli - 1), Is.Empty);
            var phaseTwoFirst = model.Advance(1);
            Assert.That(phaseTwoFirst.Single().ScheduledTimeMilli,
                Is.EqualTo((long)definition.SpawnPhases[1].BeginTimeMilli + definition.SpawnPhases[1].WaveIntervalMilli));
        }

        /// <summary>用不规则模拟步长跑完整 18 分钟，验证四阶段全部波次、普通怪数量、权重集合和唯一 Boss。</summary>
        [Test]
        public void FullTimelineProducesApprovedWavesAndMonstersDeterministically()
        {
            var definition = CreateDefinition();
            int[] expectedWaves = { 30, 50, 60, 75 };
            int[] expectedMonsters = { 150, 400, 720, 1125 };
            var direct = new CombatRunModel(definition);
            var directRequests = direct.Advance(definition.Rule.BossTimeMilli).ToArray();
            var stepped = new CombatRunModel(definition);
            var steppedRequests = new System.Collections.Generic.List<CombatSpawnRequest>();
            int[] steps = { 17, 83, 211, 997, 4000 };
            int stepIndex = 0;
            while (stepped.ElapsedMilli < definition.Rule.BossTimeMilli)
            {
                int remaining = checked((int)(definition.Rule.BossTimeMilli - stepped.ElapsedMilli));
                int delta = Math.Min(remaining, steps[stepIndex++ % steps.Length]);
                steppedRequests.AddRange(stepped.Advance(delta));
            }

            var directNormal = directRequests.Where(item => !item.IsBoss).ToArray();
            var steppedNormal = steppedRequests.Where(item => !item.IsBoss).ToArray();
            Assert.That(direct.CompletedWaves, Is.EqualTo(expectedWaves.Sum()));
            Assert.That(stepped.CompletedWaves, Is.EqualTo(expectedWaves.Sum()));
            Assert.That(directNormal.Length, Is.EqualTo(expectedMonsters.Sum()));
            Assert.That(steppedNormal.Length, Is.EqualTo(expectedMonsters.Sum()));
            Assert.That(directRequests.Count(item => item.IsBoss), Is.EqualTo(1));
            Assert.That(steppedRequests.Count(item => item.IsBoss), Is.EqualTo(1));

            for (int phaseIndex = 0; phaseIndex < definition.SpawnPhases.Count; phaseIndex++)
            {
                SpawnPhaseConfig phase = definition.SpawnPhases[phaseIndex];
                var phaseRequests = steppedNormal.Where(item =>
                    item.ScheduledTimeMilli > phase.BeginTimeMilli && item.ScheduledTimeMilli <= phase.EndTimeMilli).ToArray();
                Assert.That(phaseRequests.Length, Is.EqualTo(expectedMonsters[phaseIndex]), $"TbSpawnPhase {phase.Id} monster count changed.");
                Assert.That(phaseRequests.Select(item => item.Monster.Id).Distinct(),
                    Is.SubsetOf(phase.MonsterWeights.Select(item => item.MonsterId).ToArray()),
                    $"TbSpawnPhase {phase.Id} selected a monster outside its configured weights.");
            }

            Assert.That(steppedRequests.Select(item => (item.ScheduledTimeMilli, item.Monster.Id, item.IsBoss)),
                Is.EqualTo(directRequests.Select(item => (item.ScheduledTimeMilli, item.Monster.Id, item.IsBoss))));
        }

        /// <summary>验证跨 18 分钟的大步进和不规则小步进都只发出一次 Boss 请求。</summary>
        [Test]
        public void BossSpawnsOnceAcrossDifferentFrameSteps()
        {
            var definition = CreateDefinition();
            var direct = new CombatRunModel(definition);
            var directEvents = direct.Advance(definition.Rule.BossTimeMilli + 137);
            Assert.That(directEvents.Count(item => item.IsBoss), Is.EqualTo(1));
            Assert.That(direct.State, Is.EqualTo(CombatRunState.Boss));
            Assert.That(direct.Advance(1000).Count(item => item.IsBoss), Is.Zero);

            var stepped = new CombatRunModel(definition);
            int[] steps = { 17, 31, 47, 113 };
            int index = 0;
            int bossCount = 0;
            while (stepped.ElapsedMilli < definition.Rule.BossTimeMilli + 137)
            {
                int delta = steps[index++ % steps.Length];
                bossCount += stepped.Advance(delta).Count(item => item.IsBoss);
            }
            Assert.That(bossCount, Is.EqualTo(1));
            Assert.That(stepped.BossSpawned, Is.True);
        }

        /// <summary>验证同一怪物生命周期只掉落一次，同一掉落只能吸附和拾取一次。</summary>
        [Test]
        public void MonsterDeathAndExperienceDropAreIdempotent()
        {
            var model = new CombatRunModel(CreateDefinition());
            ulong drop = model.RecordMonsterDeath(77, false);
            Assert.That(drop, Is.Not.Zero);
            Assert.That(model.RecordMonsterDeath(77, false), Is.Zero);
            Assert.That(model.Kills, Is.EqualTo(1));
            Assert.That(model.TryMagnetizeDrop(drop), Is.True);
            Assert.That(model.TryMagnetizeDrop(drop), Is.False);
            Assert.That(model.TryCollectDrop(drop), Is.True);
            Assert.That(model.TryCollectDrop(drop), Is.False);
            Assert.That(model.Experience, Is.EqualTo(model.Definition.Drop.ExperienceValue));
        }

        /// <summary>验证单次经验跨两级时连续生成两代面板，整个选择期间模拟时钟冻结。</summary>
        [Test]
        public void MultiLevelExperienceQueuesUpgradePanelsAndFreezesTime()
        {
            var definition = CreateDefinition();
            var model = new CombatRunModel(definition);
            int amount = definition.ExperienceLevels[0].RequiredExperience + definition.ExperienceLevels[1].RequiredExperience;
            model.GrantExperience(amount);
            Assert.That(model.Level, Is.EqualTo(3));
            Assert.That(model.PendingLevelUps, Is.EqualTo(2));
            Assert.That(model.State, Is.EqualTo(CombatRunState.UpgradeChoice));
            Assert.That(model.CurrentChoices.Count, Is.EqualTo(definition.UpgradePool.DrawCount));
            Assert.That(model.Advance(1000), Is.Empty);
            Assert.That(model.ElapsedMilli, Is.Zero);

            ulong firstPanel = model.UpgradePanelGeneration;
            Assert.That(model.ChooseUpgrade(firstPanel, model.CurrentChoices[0].Id), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.UpgradeChoice));
            Assert.That(model.PendingLevelUps, Is.EqualTo(1));
            Assert.That(model.UpgradePanelGeneration, Is.GreaterThan(firstPanel));
            Assert.That(model.ChooseUpgrade(firstPanel, model.CurrentChoices[0].Id), Is.False);
            Assert.That(model.ChooseUpgrade(model.UpgradePanelGeneration, model.CurrentChoices[0].Id), Is.True);
            Assert.That(model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(model.PendingLevelUps, Is.Zero);
        }

        /// <summary>验证相同种子产生相同的无放回候选，选中的一次性技能不会再次进入池。</summary>
        [Test]
        public void UpgradeChoicesAreDeterministicDistinctAndFilterUnlockedSkills()
        {
            var left = new CombatRunModel(CreateDefinition());
            var right = new CombatRunModel(CreateDefinition());
            GrantNextLevel(left);
            GrantNextLevel(right);
            Assert.That(left.CurrentChoices.Select(item => item.Id), Is.EqualTo(right.CurrentChoices.Select(item => item.Id)));
            Assert.That(left.CurrentChoices.Select(item => item.Id).Distinct().Count(), Is.EqualTo(left.Definition.UpgradePool.DrawCount));

            var skill = left.CurrentChoices.FirstOrDefault(item => item.Type == EUpgradeOptionType.SkillUnlock);
            if (skill == null)
            {
                Assert.That(left.ChooseUpgrade(left.UpgradePanelGeneration, left.CurrentChoices[0].Id), Is.True);
                GrantNextLevel(left);
                skill = left.CurrentChoices.First(item => item.Type == EUpgradeOptionType.SkillUnlock);
            }
            Assert.That(left.ChooseUpgrade(left.UpgradePanelGeneration, skill.Id), Is.True);
            Assert.That(left.ActiveSkills.ContainsKey(skill.TargetSkillId.Value), Is.True);
            GrantNextLevel(left);
            Assert.That(left.CurrentChoices.Any(item => item.Id == skill.Id), Is.False);
        }

        /// <summary>验证结算只产生一次，重开恢复时间、实体状态、统计、技能和随机候选。</summary>
        [Test]
        public void SettlementAndRestartResetAllMutableState()
        {
            var definition = CreateDefinition();
            var model = new CombatRunModel(definition);
            var firstSpawns = model.Advance(definition.SpawnPhases[0].WaveIntervalMilli);
            Assert.That(firstSpawns, Is.Not.Empty);
            ulong drop = model.RecordMonsterDeath(11, false);
            model.TryCollectDrop(drop);
            Assert.That(model.ApplyPlayerDamage(model.MaxHealth), Is.True);
            Assert.That(model.ApplyPlayerDamage(1), Is.False);
            Assert.That(model.State, Is.EqualTo(CombatRunState.DefeatSettlement));
            Assert.That(model.Result, Is.EqualTo(ERunResultType.Defeat));

            ulong oldGeneration = model.RunGeneration;
            Assert.That(model.Restart(), Is.True);
            Assert.That(model.RunGeneration, Is.GreaterThan(oldGeneration));
            Assert.That(model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(model.ElapsedMilli, Is.Zero);
            Assert.That(model.Level, Is.EqualTo(1));
            Assert.That(model.Experience, Is.Zero);
            Assert.That(model.Kills, Is.Zero);
            Assert.That(model.CompletedWaves, Is.Zero);
            Assert.That(model.BossSpawned, Is.False);
            Assert.That(model.Result, Is.Null);
            Assert.That(model.Drops, Is.Empty);
            Assert.That(model.UpgradeRanks, Is.Empty);
            Assert.That(model.ActiveSkills.Keys, Is.EqualTo(definition.InitialSkills.Select(item => item.Id)));
            Assert.That(model.CurrentHealth, Is.EqualTo(model.MaxHealth));
            Assert.That(model.TryCollectDrop(drop), Is.False);
        }

        /// <summary>验证 Boss 首次死亡进入胜利结算，重复死亡不会重复计数。</summary>
        [Test]
        public void BossDeathSettlesVictoryOnce()
        {
            var model = new CombatRunModel(CreateDefinition());
            model.Advance(model.Definition.Rule.BossTimeMilli);
            Assert.That(model.RecordMonsterDeath(9001, true), Is.Zero);
            Assert.That(model.State, Is.EqualTo(CombatRunState.VictorySettlement));
            Assert.That(model.Result, Is.EqualTo(ERunResultType.Victory));
            Assert.That(model.Kills, Is.EqualTo(1));
            Assert.That(model.RecordMonsterDeath(9001, true), Is.Zero);
            Assert.That(model.Kills, Is.EqualTo(1));
        }

        /// <summary>给予当前等级表中恰好一级经验，供候选序列测试推进。</summary>
        /// <param name="model">当前处于 Playing 的单局。</param>
        private static void GrantNextLevel(CombatRunModel model)
        {
            int required = model.Definition.ExperienceLevels[model.Level - 1].RequiredExperience;
            model.GrantExperience(required);
        }

        /// <summary>从 StreamingAssets 的 Luban bytes 创建正式第一关定义。</summary>
        /// <returns>已完成跨表校验的 CombatRunDefinition。</returns>
        private static CombatRunDefinition CreateDefinition() => CombatRunDefinition.Create(LoadTables(), 1);

        /// <summary>只读加载项目生成配置，不启动资源系统或创建 Unity 对象。</summary>
        /// <returns>已经 ResolveRef 的 Luban Tables。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(
            Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
    }
}
