using System.IO;
using System.Linq;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using Roguelike.Features.Combat.Run;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>使用正式生成配置验证抽选控制的原子性、独立随机流及角色内容锁。</summary>
    public sealed class CombatDraftControlTests
    {
        /// <summary>同种子同操作产生相同候选；特效或伤害抽样不影响选卡，旧回调不会重复扣次数。</summary>
        /// <param name="stageId">正式双职业关卡。</param>
        [TestCase(101)]
        [TestCase(102)]
        public void RerollAndSkipAreDeterministicAndRejectStaleOffers(int stageId)
        {
            var tables = LoadTables(); var definition = CombatRunDefinition.Create(tables, stageId);
            var first = new CombatRunModel(definition, new[] { definition.DraftPolicy.GoalId });
            var second = new CombatRunModel(definition, new[] { definition.DraftPolicy.GoalId });
            first.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);
            CombatRandom.Inclusive(42, 33, 44, CombatRandomPurpose.Damage, 1, 100);
            second.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);
            Assert.That(first.CurrentChoices.Select(o => o.Id), Is.EqualTo(second.CurrentChoices.Select(o => o.Id)));
            var before = first.CurrentChoices.Select(o => o.Id).ToArray(); ulong offer = first.UpgradePanelGeneration;
            Assert.That(first.RerollUpgrade(offer), Is.True);
            Assert.That(first.RerollUpgrade(offer), Is.False);
            Assert.That(second.RerollUpgrade(second.UpgradePanelGeneration), Is.True);
            Assert.That(first.CurrentChoices.Select(o => o.Id), Is.EqualTo(second.CurrentChoices.Select(o => o.Id)));
            Assert.That(first.CurrentChoices.Select(o => o.Id).Intersect(before), Is.Empty);
            Assert.That(first.RerollsRemaining, Is.EqualTo(definition.DraftPolicy.RerollCount - 1));
            Assert.That(first.CurrentChoices.Select(o => o.Id).Distinct().Count(), Is.EqualTo(first.CurrentChoices.Count));
            offer = first.UpgradePanelGeneration; int selected = first.CurrentChoices[0].Id;
            Assert.That(first.SkipUpgrade(offer), Is.True);
            Assert.That(first.SkipUpgrade(offer), Is.False);
            Assert.That(first.ChooseUpgrade(offer, selected), Is.False);
            Assert.That(first.SkipsRemaining, Is.EqualTo(definition.DraftPolicy.SkipCount - 1));
            Assert.That(first.UpgradeRanks, Is.Empty);
            Assert.That(first.State, Is.EqualTo(CombatRunState.Playing));
        }

        /// <summary>已解锁且开局启用是新卡必要条件；对方职业、错误模式和旧正式入口不能借高权重越过资格。</summary>
        [Test]
        public void ModifierEligibilityRespectsRoleModeAndContentSnapshot()
        {
            var tables = LoadTables(); var phan = CombatRunDefinition.Create(tables, 101);
            var zhou = CombatRunDefinition.Create(tables, 102);
            Assert.That(new CombatRunModel(phan).ExplainUpgradeExclusion(tables.TbUpgradeOption.Get(42001)), Is.EqualTo(DraftExclusion.Locked));
            Assert.That(new CombatRunModel(phan, new[] { 62001 }).ExplainUpgradeExclusion(tables.TbUpgradeOption.Get(42001)), Is.EqualTo(DraftExclusion.None));
            Assert.That(new CombatRunModel(zhou, new[] { 62001, 62002 }).ExplainUpgradeExclusion(tables.TbUpgradeOption.Get(42001)), Is.EqualTo(DraftExclusion.WrongCharacter));
            Assert.That(new CombatRunModel(CombatRunDefinition.Create(tables, 1), new[] { 62001 }).ExplainUpgradeExclusion(tables.TbUpgradeOption.Get(42001)), Is.Not.EqualTo(DraftExclusion.None));
            Assert.That(zhou.UpgradePool.Entries.Select(e => e.OptionId), Has.No.Member(41002));
        }

        /// <summary>合法候选恰好充满面板时不允许无效重掷；全部满级后免费继续且不消耗跳过。</summary>
        [Test]
        public void NoReplacementAndExhaustedCandidatesDoNotSpendBudgets()
        {
            var tables=LoadTables();var definition=CombatRunDefinition.Create(tables,101);
            definition.UpgradePool.Entries.RemoveAll(e=>!new[]{41007,41008,41010}.Contains(e.OptionId));
            var model=new CombatRunModel(definition);
            model.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);
            ulong offer=model.UpgradePanelGeneration,random=model.DraftRandomPosition;int budget=model.RerollsRemaining;
            Assert.That(model.CurrentChoices.Count,Is.EqualTo(definition.UpgradePool.DrawCount));
            Assert.That(model.CanReroll,Is.False);Assert.That(model.RerollUpgrade(offer),Is.False);
            Assert.That(model.RerollsRemaining,Is.EqualTo(budget));Assert.That(model.DraftRandomPosition,Is.EqualTo(random));
            model.ChooseUpgrade(offer,model.CurrentChoices.First().Id);
            foreach(var option in definition.UpgradePool.Entries.Select(e=>e.OptionId_Ref))
                while(model.Build.CanUpgrade(option.RunPassiveId_Ref))model.Build.Upgrade(option.RunPassiveId_Ref);
            int skips=model.SkipsRemaining;
            model.GrantExperience(definition.ExperienceLevels[model.Level-1].RequiredExperience-model.Experience);
            Assert.That(model.State,Is.EqualTo(CombatRunState.Playing));Assert.That(model.CurrentChoices,Is.Empty);
            Assert.That(model.PendingLevelUps,Is.Zero);Assert.That(model.SkipsRemaining,Is.EqualTo(skips));
        }

        /// <summary>仅有一个新候选时重掷只替换一个并保留必要旧项；同次重掷不额外累计救济未出现次数。</summary>
        [Test]
        public void PartialRerollReportsRealReplacementWithoutFarmingRelief()
        {
            var definition=CombatRunDefinition.Create(LoadTables(),101);
            definition.UpgradePool.Entries.RemoveAll(e=>!new[]{41007,41008,41010,41011}.Contains(e.OptionId));
            var model=new CombatRunModel(definition);model.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);
            int[] before=model.CurrentChoices.Select(o=>o.Id).ToArray();int misses=model.MissedSynergyOffers;
            Assert.That(model.RerollUpgrade(model.UpgradePanelGeneration),Is.True);
            Assert.That(model.LastReplacementCount,Is.EqualTo(1));Assert.That(model.CurrentChoices.Count(o=>before.Contains(o.Id)),Is.EqualTo(2));
            Assert.That(model.MissedSynergyOffers,Is.EqualTo(misses));
        }

        /// <summary>表内角色之外仍独立检查模式、前置和互斥，不允许权重绕过资格。</summary>
        [Test]
        public void PrerequisitesModesAndExclusionsRemainIndependentOfWeight()
        {
            var tables=LoadTables();var definition=CombatRunDefinition.Create(tables,101);
            var model=new CombatRunModel(definition,new[]{definition.DraftPolicy.GoalId});var option=tables.TbUpgradeOption.Get(42001);
            var rule=definition.DraftRules[option.Id];rule.StageIds.Clear();rule.StageIds.Add(102);
            Assert.That(model.ExplainUpgradeExclusion(option),Is.EqualTo(DraftExclusion.WrongStage));
            rule.StageIds.Clear();rule.StageIds.Add(definition.Stage.Id);rule.RequiredOptionIds.Add(41007);
            Assert.That(model.ExplainUpgradeExclusion(option),Is.EqualTo(DraftExclusion.MissingPrerequisite));
            definition.UpgradePool.Entries.RemoveAll(e=>e.OptionId!=41007);
            model.GrantExperience(definition.ExperienceLevels[0].RequiredExperience);model.ChooseUpgrade(model.UpgradePanelGeneration,41007);
            Assert.That(model.ExplainUpgradeExclusion(option),Is.EqualTo(DraftExclusion.None));
            rule.ExcludedOptionIds.Add(41007);Assert.That(model.ExplainUpgradeExclusion(option),Is.EqualTo(DraftExclusion.Exclusive));
        }

        /// <summary>无合法协同项时不显示救济；满足连续未出现阈值后，协同项一经展示即清零，无需玩家选择。</summary>
        [Test]
        public void ReliefOnlyAdvertisesLegalSynergyAndResetsOnAppearance()
        {
            var definition=CombatRunDefinition.Create(LoadTables(),101);var synergy=definition.UpgradePool.Entries.Single(e=>e.OptionId==41009);
            definition.UpgradePool.Entries.RemoveAll(e=>!new[]{41007,41008,41010}.Contains(e.OptionId));
            var model=new CombatRunModel(definition);
            for(int i=0;i<definition.DraftPolicy.ReliefMissCount;i++)
            {
                model.GrantExperience(definition.ExperienceLevels[model.Level-1].RequiredExperience-model.Experience);
                Assert.That(model.ReliefActive,Is.False);model.ChooseUpgrade(model.UpgradePanelGeneration,model.CurrentChoices.First().Id);
            }
            Assert.That(model.MissedSynergyOffers,Is.EqualTo(definition.DraftPolicy.ReliefMissCount));
            definition.UpgradePool.Entries.Clear();definition.UpgradePool.Entries.Add(synergy);
            model.GrantExperience(definition.ExperienceLevels[model.Level-1].RequiredExperience-model.Experience);
            Assert.That(model.ReliefActive,Is.True);Assert.That(model.CurrentChoices.Single().Id,Is.EqualTo(synergy.OptionId));
            Assert.That(model.MissedSynergyOffers,Is.Zero);
        }

        /// <summary>读取正式 bytes；本夹具不启动运行器，不读取或写入任何玩家 Profile。</summary>
        /// <returns>已完成强引用解析的生成表。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(
            Path.Combine(Application.streamingAssetsPath, "Config/Luban", name + ".bytes"))));
    }
}
