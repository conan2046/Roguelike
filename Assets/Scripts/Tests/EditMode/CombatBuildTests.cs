using System;
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
    /// <summary>用正式 Luban bytes 验证独立槽位、武器星级和被动来源替换，不修改源工作簿。</summary>
    public sealed class CombatBuildTests
    {
        /// <summary>确认通用与磐岳配置各自生效，开局武器实际占槽且不占用被动槽。</summary>
        [Test]
        public void FormalStageUsesApprovedRoleCapacityAndIndependentSlots()
        {
            Tables tables = LoadTables();
            var definition = CombatRunDefinition.Create(tables, 1);
            var build = new CombatRunModel(definition).Build;
            Assert.That(build.Config.CharacterId, Is.EqualTo(definition.Character.Id));
            Assert.That(build.Config.WeaponSlotCount, Is.EqualTo(4));
            Assert.That(build.Config.PassiveSlotCount, Is.EqualTo(6));
            Assert.That(tables.TbRunBuild.Get(1).WeaponSlotCount, Is.EqualTo(6));
            Assert.That(build.Config.WeaponDamagePermille, Is.EqualTo(1150));
            Assert.That(build.WeaponRanks.Count, Is.EqualTo(build.Config.InitialWeaponIds.Count));
            Assert.That(build.PassiveRanks, Is.Empty);
            Assert.That(build.Config.WeaponIds.Count, Is.EqualTo(6));
            Assert.That(build.Config.PassiveIds.Count, Is.EqualTo(6));
        }

        /// <summary>武器满槽只阻止获得新武器，不阻止已有武器升星或独立被动占槽。</summary>
        [Test]
        public void FullWeaponSlotsRejectNewWeaponButAllowHeldRankAndPassive()
        {
            RunBuildConfig config = CombatRunDefinition.Create(LoadTables(), 1).BuildConfig;
            var build = new CombatBuildState(config);
            foreach (RunWeaponConfig weapon in config.WeaponIds_Ref)
                if (!build.WeaponRanks.ContainsKey(weapon.Id) && build.WeaponRanks.Count < config.WeaponSlotCount)
                    build.Upgrade(weapon);
            RunWeaponConfig blocked = config.WeaponIds_Ref.First(weapon => !build.WeaponRanks.ContainsKey(weapon.Id));
            Assert.That(build.CanUpgrade(blocked), Is.False);
            Assert.Throws<InvalidOperationException>(() => build.Upgrade(blocked));
            RunWeaponConfig held = config.InitialWeaponIds_Ref[0];
            Assert.That(build.CanUpgrade(held), Is.True);
            build.Upgrade(held);
            Assert.That(build.WeaponRanks.Count, Is.EqualTo(config.WeaponSlotCount));
            Assert.That(build.CanUpgrade(config.PassiveIds_Ref[0]), Is.True);
            build.Upgrade(config.PassiveIds_Ref[0]);
            Assert.That(build.PassiveRanks.Count, Is.EqualTo(1));
        }

        /// <summary>构筑入口拒绝混入旧技能解锁卡，防止配置绕过已确认的武器槽上限。</summary>
        [Test]
        public void FormalBuildRejectsLegacySkillUnlockInItsPool()
        {
            Tables tables = LoadTables();
            tables.TbStage.Get(1).StageRuleId_Ref.UpgradePoolId_Ref.Entries.Add(tables.TbUpgradePool.Get(1).Entries[0]);
            var error = Assert.Throws<InvalidOperationException>(() => CombatRunDefinition.Create(tables, 1));
            Assert.That(error.Message, Does.Contain("bypasses the formal build slots"));
        }

        /// <summary>角色专属构筑不能被另一角色静默复用，通用方案仍可按同一验证入口检查。</summary>
        [Test]
        public void RoleSpecificBuildRejectsDifferentCharacter()
        {
            Tables tables = LoadTables();
            RunBuildConfig role = tables.TbStage.Get(1).RunBuildId_Ref;
            Assert.Throws<InvalidOperationException>(() => CombatBuildState.Validate(role, role.CharacterId.Value + 1));
            Assert.DoesNotThrow(() => CombatBuildState.Validate(tables.TbRunBuild.Get(1), role.CharacterId.Value));
        }

        /// <summary>每把武器都能经模型的正式选卡入口逐星到达上限，并导出独立倍率；满星后候选耗尽而非重复升星。</summary>
        /// <param name="weaponId">本批六个正式 TbRunWeapon ID。</param>
        [TestCase(50001)]
        [TestCase(50002)]
        [TestCase(50003)]
        [TestCase(50004)]
        [TestCase(50005)]
        [TestCase(50006)]
        public void EveryWeaponRanksThroughChoiceAndFiltersAtMaximum(int weaponId)
        {
            Tables tables = LoadTables();
            UpgradeOptionConfig option = tables.TbUpgradeOption.DataList.Single(item => item.RunWeaponId == weaponId);
            CombatRunModel model = CreateSingleOptionModel(tables, option.Id);
            RunWeaponConfig weapon = option.RunWeaponId_Ref;
            int initial = model.GetOptionRank(option);
            for (int star = initial + 1; star <= weapon.Ranks.Count; star++)
            {
                GrantNextLevel(model);
                Assert.That(model.CurrentChoices.Single().Id, Is.EqualTo(option.Id));
                ulong panel = model.UpgradePanelGeneration;
                Assert.That(model.ChooseUpgrade(panel, option.Id), Is.True);
                Assert.That(model.ChooseUpgrade(panel, option.Id), Is.False, "A stale card must not apply twice.");
                Assert.That(model.Build.WeaponRanks[weaponId], Is.EqualTo(star));
                CombatWeaponUpgrade snapshot = model.Build.CaptureWeapons().Single(item => item.SkillId == weapon.Ranks[star - 1].SkillId);
                Assert.That(snapshot.DamageMultiplier, Is.EqualTo(weapon.Ranks[star - 1].DamagePermille / 1000d *
                    model.Build.Config.WeaponDamagePermille / 1000d).Within(1e-9));
                Assert.That(model.ActiveSkills.ContainsKey(snapshot.SkillId), Is.True);
            }
            Assert.That(model.Build.CanUpgrade(weapon), Is.False);
            GrantNextLevel(model);
            Assert.That(model.CurrentChoices, Is.Empty);
            Assert.That(model.State, Is.EqualTo(CombatRunState.Playing));
            Assert.That(model.PendingLevelUps, Is.Zero);
        }

        /// <summary>逐级选择每种被动后，只应用本级总效果；最大生命增长不被误当成治疗。</summary>
        /// <param name="passiveId">本批六个正式 TbRunPassive ID。</param>
        [TestCase(51001)]
        [TestCase(51002)]
        [TestCase(51003)]
        [TestCase(51004)]
        [TestCase(51005)]
        [TestCase(51006)]
        public void EveryPassiveReplacesPreviousRankInsteadOfStackingIt(int passiveId)
        {
            Tables tables = LoadTables();
            UpgradeOptionConfig option = tables.TbUpgradeOption.DataList.Single(item => item.RunPassiveId == passiveId);
            CombatRunModel model = CreateSingleOptionModel(tables, option.Id);
            var baseAttributes = new CombatAttributes(model.Definition.Character.AttributeProfileId_Ref);
            double initialHealth = model.CurrentHealth;
            foreach (RunPassiveRank effect in option.RunPassiveId_Ref.Ranks)
            {
                GrantNextLevel(model);
                Assert.That(model.ChooseUpgrade(model.UpgradePanelGeneration, option.Id), Is.True);
                double original = baseAttributes.Get(effect.AttributeId_Ref.AttributeType);
                double expected = effect.Operation == EAttributeModifyOperation.Add ? original + effect.ValueMilli / 1000d :
                    effect.Operation == EAttributeModifyOperation.AddPercent ? original * (1 + effect.ValueMilli / 1000d) :
                    original * effect.ValueMilli / 1000d;
                if (effect.AttributeId_Ref.ValueKind == EAttributeValueKind.Integer) expected = Math.Floor(expected);
                Assert.That(ReadAttribute(model.AttributeSnapshot, effect.AttributeId_Ref.AttributeType), Is.EqualTo(expected).Within(1e-9));
                Assert.That(model.Build.PassiveRanks.Count, Is.EqualTo(1));
                Assert.That(model.Build.PassiveRanks[passiveId], Is.EqualTo(effect.Rank));
                Assert.That(model.CurrentHealth, Is.EqualTo(initialHealth));
            }
            GrantNextLevel(model);
            Assert.That(model.CurrentChoices, Is.Empty);
        }

        /// <summary>死亡重开必须清除构筑等级和旧候选句柄，初始武器重新从配置等级开始。</summary>
        [Test]
        public void RestartReconstructsBuildAndRejectsOldUpgradePanel()
        {
            Tables tables = LoadTables();
            UpgradeOptionConfig option = tables.TbUpgradeOption.DataList.First(item => item.Type == EUpgradeOptionType.RunPassive);
            CombatRunModel model = CreateSingleOptionModel(tables, option.Id);
            GrantNextLevel(model);
            ulong panel = model.UpgradePanelGeneration;
            model.ChooseUpgrade(panel, option.Id);
            Assert.That(model.Build.PassiveRanks, Is.Not.Empty);
            model.ApplyPlayerDamage(model.MaxHealth);
            Assert.That(model.Restart(), Is.True);
            Assert.That(model.Build.PassiveRanks, Is.Empty);
            Assert.That(model.Build.WeaponRanks.Values.All(rank => rank == 1), Is.True);
            GrantNextLevel(model);
            Assert.That(model.ChooseUpgrade(panel, option.Id), Is.False);
        }

        /// <summary>只在隔离表副本收窄候选，用正式耗尽策略覆盖所有等级边界；不改 Excel 或正式池。</summary>
        /// <param name="tables">本用例独占的真实 bytes 解析副本。</param>
        /// <param name="optionId">需要连续验证的正式候选。</param>
        /// <returns>已按完整定义校验的模型。</returns>
        private static CombatRunModel CreateSingleOptionModel(Tables tables, int optionId)
        {
            tables.TbStage.Get(1).StageRuleId_Ref.UpgradePoolId_Ref.Entries.RemoveAll(entry => entry.OptionId != optionId);
            return new CombatRunModel(CombatRunDefinition.Create(tables, 1));
        }

        /// <summary>测试中按当前正式经验需求进入下一面板，不注入武器或属性状态。</summary>
        /// <param name="model">尚未结算的隔离模型。</param>
        private static void GrantNextLevel(CombatRunModel model) => model.GrantExperience(
            model.Definition.ExperienceLevels[model.Level - 1].RequiredExperience);

        /// <summary>用独立映射读取受本批被动影响的快照字段，检查实际聚合结果。</summary>
        /// <param name="snapshot">模型导出的快照。</param>
        /// <param name="type">配置的属性语义。</param>
        /// <returns>对应实际属性值。</returns>
        /// <exception cref="ArgumentOutOfRangeException">测试未定义此属性映射。</exception>
        private static double ReadAttribute(CombatAttributeSnapshot snapshot, EAttributeType type)
        {
            switch (type)
            {
                case EAttributeType.Attack: return snapshot.Attack;
                case EAttributeType.AttackSpeedMultiplier: return snapshot.AttackSpeedMultiplier;
                case EAttributeType.SkillRangeMultiplier: return snapshot.SkillRangeMultiplier;
                case EAttributeType.MaxHealth: return snapshot.MaxHealth;
                case EAttributeType.MoveSpeed: return snapshot.MoveSpeed;
                case EAttributeType.Defense: return snapshot.Defense;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        /// <summary>读取并解析现有 Luban bytes，为每个用例提供独立配置副本。</summary>
        /// <returns>强引用已解析的 Tables。</returns>
        private static Tables LoadTables() => new Tables(name => new ByteBuf(File.ReadAllBytes(
            Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
    }
}
