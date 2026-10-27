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
    /// <summary>使用真实Luban生成物验证切片定义、试炼时序、领奖幂等、法器转向及阵域规则；不启动Unity场景。</summary>
    public sealed class CombatTribulationTests
    {
        /// <summary>两个正式入口均保留本命且开局含弹道武器，炼器资源闭包完整，原stage1继续不启用切片。</summary>
        [Test]
        public void TwoRoleDefinitionsHaveRealSkillClosuresAndLeaveOriginalStageUnchanged()
        {
            var tables = LoadTables();
            var entry = tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry);
            var definitions = entry.StageIds.Select(id => CombatRunDefinition.Create(tables, id)).ToArray();
            Assert.That(definitions.Length, Is.EqualTo(2));
            Assert.That(definitions.Select(d => d.Tribulation.SignatureId_Ref.Kind).Distinct().Count(), Is.EqualTo(2));
            Assert.That(definitions.Select(d => d.Tribulation.WeaponId).Distinct().Count(), Is.EqualTo(2));
            foreach (var definition in definitions)
            {
                Assert.That(definition.InitialSkills.Any(skill => skill.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Projectile), Is.True);
                Assert.That(definition.BuildConfig.InitialWeaponIds, Does.Contain(definition.Tribulation.WeaponId));
                Assert.That(definition.Tribulation.RefinementIds.Count, Is.EqualTo(3));
                foreach (var refinement in definition.Tribulation.RefinementIds_Ref)
                    Assert.That(definition.AvailableSkills.Any(s => s.Id == refinement.ResultSkillId), Is.True);
            }
            var original = CombatRunDefinition.Create(tables, 1);
            Assert.That(original.Tribulation, Is.Null);
            Assert.That(original.TribulationSkills, Is.Empty);
            Assert.That(original.Rule.DurationMilli, Is.EqualTo(1080000));
            Assert.That(original.InitialSkills.Single().Id, Is.EqualTo(20001));
        }

        /// <summary>雷坛必须真实解锁且在距离内；重复启动和旧单局代次不能消耗新机会。</summary>
        [Test]
        public void AltarStartRequiresUnlockDistanceAndCurrentGeneration()
        {
            var slice = LoadTables().TbTribulationSlice.DataList.First();
            var altar = slice.RuleId_Ref.Altars.First();
            var state = new CombatTribulationModel(slice, 7);
            Assert.That(state.TryStart(7, altar.Id, altar.XPixels, altar.YPixels), Is.False);
            state.Advance(altar.AvailableAfterMilli, 0, 0, null);
            Assert.That(state.TryStart(6, altar.Id, altar.XPixels, altar.YPixels), Is.False);
            Assert.That(state.TryStart(7, altar.Id, altar.XPixels + slice.RuleId_Ref.ActivationRadiusPixels * 2, altar.YPixels), Is.False);
            Assert.That(state.TryStart(7, altar.Id, altar.XPixels, altar.YPixels), Is.True);
            Assert.That(state.TryStart(7, altar.Id, altar.XPixels, altar.YPixels), Is.False);
            Assert.That(state.SpentAltars.Count, Is.EqualTo(1));
        }

        /// <summary>雷击锁点不跟随移动，真实命中回报只能计一次；截止后单次领取并让同槽法器实际转向。</summary>
        [Test]
        public void LockedWarningsCountOnceAndRefinementPreservesWeaponSlotAndStar()
        {
            var tables = LoadTables(); var definition = CombatRunDefinition.Create(tables, tables.TbTribulationUi.DataList.Single(ui => ui.IsEntry).StageIds.First());
            var slice = definition.Tribulation; var altar = slice.RuleId_Ref.Altars.First();
            var state = new CombatTribulationModel(slice, 1); var model = new CombatRunModel(definition);
            state.Advance(altar.AvailableAfterMilli, altar.XPixels, altar.YPixels, null);
            Assert.That(state.TryStart(1, altar.Id, altar.XPixels, altar.YPixels), Is.True);
            state.Advance(1, 25, 50, null);
            var locked = state.Warnings.First();
            state.Advance(slice.RuleId_Ref.WarningMilli, 500, 600, null);
            Assert.That(state.DueStrikes.First().XPixels, Is.EqualTo(25));
            Assert.That(state.DueStrikes.First().YPixels, Is.EqualTo(50));
            state.RecordEnemyHits(locked, slice.RuleId_Ref.RequiredEnemyHits);
            state.RecordEnemyHits(locked, slice.RuleId_Ref.RequiredEnemyHits);
            Assert.That(state.EnemyHits, Is.EqualTo(slice.RuleId_Ref.RequiredEnemyHits));
            state.Advance(slice.RuleId_Ref.TrialDurationMilli, 500, 600, null);
            state.ResolveTrialDeadline();
            Assert.That(state.State, Is.EqualTo(TribulationTrialState.RewardPending));
            ulong reward = state.RewardGeneration;
            var choice = slice.RefinementIds_Ref.First();
            Assert.That(state.TryClaim(1, reward + 1, choice.Id, out _), Is.False);
            Assert.That(state.TryClaim(1, reward, choice.Id, out var refinement), Is.True);
            int slots = model.Build.WeaponRanks.Count;
            int star = model.Build.WeaponRanks[slice.WeaponId];
            Assert.That(model.ApplyTribulationRefinement(refinement), Is.True);
            Assert.That(model.Build.CaptureWeapons().Select(weapon => weapon.SkillId), Does.Contain(choice.ResultSkillId));
            Assert.That(model.Build.WeaponRanks.Count, Is.EqualTo(slots));
            Assert.That(model.Build.WeaponRanks[slice.WeaponId], Is.EqualTo(star));
            Assert.That(model.ActiveSkills.ContainsKey(choice.ResultSkillId), Is.True);
            Assert.That(state.TryClaim(1, reward, choice.Id, out _), Is.False);
            model.Build.Upgrade(slice.WeaponId_Ref);
            Assert.That(model.Build.CaptureWeapons().Select(weapon => weapon.SkillId), Does.Contain(choice.ResultSkillId));
            var different = slice.RefinementIds_Ref.Skip(1).First();
            Assert.That(model.ApplyTribulationRefinement(different), Is.True);
            Assert.That(model.Build.CaptureWeapons().Select(weapon => weapon.SkillId), Does.Contain(different.ResultSkillId));
            Assert.That(model.ActiveSkills.ContainsKey(choice.ResultSkillId), Is.False);
        }

        /// <summary>命中不足不会发奖；结算终止预警，旧模型永久拒绝引劫及领奖。</summary>
        [Test]
        public void FailedTrialDoesNotGrantRewardAndTerminationRejectsDelayedActions()
        {
            var slice = LoadTables().TbTribulationSlice.DataList.First(); var altar = slice.RuleId_Ref.Altars.First();
            var state = new CombatTribulationModel(slice, 3);
            state.Advance(altar.AvailableAfterMilli, 0, 0, null); state.TryStart(3, altar.Id, altar.XPixels, altar.YPixels);
            state.Advance(slice.RuleId_Ref.TrialDurationMilli, 0, 0, null); state.ResolveTrialDeadline();
            Assert.That(state.State, Is.EqualTo(TribulationTrialState.Failed));
            Assert.That(state.TryClaim(3, state.RewardGeneration, slice.RefinementIds.First(), out _), Is.False);
            state.Terminate(); state.Advance(slice.RuleId_Ref.TrialDurationMilli, 0, 0, 1000);
            Assert.That(state.Warnings, Is.Empty); Assert.That(state.DueStrikes, Is.Empty);
            Assert.That(state.TryStart(3, altar.Id, altar.XPixels, altar.YPixels), Is.False);
        }

        /// <summary>直行留下可持续命中的分离阵域，折返重叠产生一次真实引爆请求并消耗参与阵域。</summary>
        [Test]
        public void DomainRouteChangesProducePersistentGroundAndReturnDetonation()
        {
            var config = LoadTables().TbTribulationSignature.DataList.Single(s => s.Kind == ETribulationSignature.Domains);
            var state = new CombatTribulationDomains(config);
            state.Advance(1, 0, 0);
            state.Advance(16, config.DomainStepPixels, 0);
            Assert.That(state.Domains.Count, Is.EqualTo(1));
            Assert.That(state.Pulses.Any(p => !p.Explosion), Is.True);
            state.Advance(16, config.DomainStepPixels * 2, 0);
            Assert.That(state.Domains.Count, Is.EqualTo(2)); Assert.That(state.ExplosionCount, Is.Zero);
            state.Advance(16, config.DomainStepPixels, 0);
            Assert.That(state.ExplosionCount, Is.EqualTo(1));
            Assert.That(state.Pulses.Count(p => p.Explosion), Is.EqualTo(1));
            Assert.That(state.Domains.Count, Is.EqualTo(1));
            var remaining = state.Domains.Single();
            Assert.That(state.ClearIntersecting(remaining.XPixels, remaining.YPixels, config.DomainRadiusPixels), Is.EqualTo(1));
            Assert.That(state.Domains, Is.Empty);
        }

        /// <summary>阵域容量与寿命按表生效；跨过寿命的大步长仍补齐已经到期的脉冲。</summary>
        [Test]
        public void DomainCapacityExpiryAndDuePulsesRemainBounded()
        {
            var config = LoadTables().TbTribulationSignature.DataList.Single(s => s.Kind == ETribulationSignature.Domains);
            var state = new CombatTribulationDomains(config);
            state.Advance(1, 0, 0);
            double x = (config.MaxDomains + 3) * config.DomainStepPixels;
            state.Advance(16, x, 0);
            Assert.That(state.Domains.Count, Is.EqualTo(config.MaxDomains));
            state.Advance(config.DomainDurationMilli, x, 0);
            Assert.That(state.Domains, Is.Empty);
            Assert.That(state.Pulses.Count, Is.GreaterThan(0));
            state.Clear(); Assert.That(state.Pulses, Is.Empty);
        }

        /// <summary>Boss实际生命比例选择表内阶段，高阶段雷区数量与破阵标记不会沿用第一阶段。</summary>
        [Test]
        public void BossHealthChangesSelectConfiguredPressurePhases()
        {
            var slice = LoadTables().TbTribulationSlice.DataList.First(); var state = new CombatTribulationModel(slice, 2);
            var phases = slice.RuleId_Ref.BossPhases;
            state.Advance(1, 0, 0, 1000);
            Assert.That(state.BossPhase, Is.EqualTo(1)); Assert.That(state.Warnings.Count, Is.EqualTo(phases[0].StrikeCount));
            state.Advance(phases[0].IntervalMilli, 0, 0, phases.Last().HealthPermille);
            Assert.That(state.BossPhase, Is.EqualTo(phases.Count));
            Assert.That(state.Warnings.Count, Is.GreaterThanOrEqualTo(phases.Last().StrikeCount));
            Assert.That(state.Warnings.Any(s => s.FromBoss && s.ClearsDomains), Is.True);
        }

        /// <summary>每项测试从正式生成bytes创建独立表实例，不改源表、资源或默认参数。</summary>
        /// <returns>当前项目的完整Luban表。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.dataPath, "StreamingAssets/Config/Luban", name + ".bytes"))));
    }
}
