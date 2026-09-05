using System.IO;
using cfg;
using Luban;
using NUnit.Framework;
using Roguelike.Features.Combat;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>在 Unity Editor 内使用实际 Luban bytes 验证独立战斗底座。</summary>
    public sealed class CombatFoundationTests
    {
        /// <summary>EditMode 运行时逐项验证参考文档的两段取整公式。</summary>
        /// <param name="attack">用例攻击。</param>
        /// <param name="defense">用例防御。</param>
        /// <param name="parameter">用例防御参数。</param>
        /// <param name="random">用例万分比浮动。</param>
        /// <param name="critical">是否暴击。</param>
        /// <param name="expected">独立参考结果。</param>
        [TestCase(20, 0, 1, 10000, false, 20)]
        [TestCase(20, 10, 1, 10000, false, 13)]
        [TestCase(20, 10, 2, 10000, false, 10)]
        [TestCase(20, 100, 1, 10000, false, 3)]
        [TestCase(20, 0, 1, 8000, false, 16)]
        [TestCase(20, 0, 1, 12000, false, 24)]
        [TestCase(20, 0, 1, 10000, true, 30)]
        [TestCase(20, 10, 1, 10000, true, 19)]
        [TestCase(1, 10000, 1, 10000, false, 0)]
        [TestCase(0, 0, 1, 10000, false, 0)]
        public void DamageMatchesReference(double attack, double defense, double parameter, int random, bool critical, long expected)
        {
            var rules = LoadTables().TbCombatRules.Get(1);
            Assert.That(CombatMath.Damage(attack, defense, parameter, random, rules.CriticalMultiplierBp, critical), Is.EqualTo(expected));
        }

        /// <summary>EditMode 中检查真实属性方案的来源替换与最大生命不回血规则。</summary>
        [Test]
        public void AttributeReplacementPreservesCurrentHealth()
        {
            var attributes = new CombatAttributes(LoadTables().TbAttributeProfile.Get(1));
            var target = DamageTarget.Spawn(attributes, 1, 1, true);
            target.Health = 30;
            var modifier = new AttributeModifier { Attribute = EAttributeType.MaxHealth, Flat = 100 };
            attributes.Replace(1, modifier);
            attributes.Replace(1, modifier);
            target.Synchronize(attributes);
            Assert.That(target.MaxHealth, Is.EqualTo(200));
            Assert.That(target.Health, Is.EqualTo(30));
            attributes.Remove(1);
            target.Synchronize(attributes);
            Assert.That(target.MaxHealth, Is.EqualTo(100));
            Assert.That(target.Health, Is.EqualTo(30));
        }

        /// <summary>验证局内属性严格按 Add、AddPercent、Multiply 三个乘区顺序结算。</summary>
        [Test]
        public void AttributeModifiersApplyFixedThreeStageOrder()
        {
            var attributes = new CombatAttributes(LoadTables().TbAttributeProfile.Get(1));
            double baseline = attributes.Get(EAttributeType.Attack);
            attributes.Replace(1, new AttributeModifier
            {
                Attribute = EAttributeType.Attack,
                Flat = 10,
                PercentBp = 1000,
                Multiplier = 2
            });
            Assert.That(attributes.Get(EAttributeType.Attack), Is.EqualTo((baseline + 10) * 1.1 * 2).Within(0.000001));
        }

        /// <summary>EditMode 中验证无敌优先级和过期目标生命周期拒绝。</summary>
        [Test]
        public void InvulnerabilityAndLifetimeProtectTarget()
        {
            var tables = LoadTables();
            var attributes = new CombatAttributes(tables.TbAttributeProfile.Get(1));
            var rules = DamageRules.Capture(tables.TbCombatRules.Get(1));
            var attack = AttackSnapshot.Capture(attributes, 1, 1, 1);
            var target = DamageTarget.Spawn(attributes, 2, 2, true);
            target.ImmunityCharges = 1;
            target.InvulnerableUntil = 1;
            Assert.That(CombatDamage.Resolve(attack, ref target, 2, true, 0, 7, rules).Outcome, Is.EqualTo(DamageOutcome.Invulnerable));
            Assert.That(target.ImmunityCharges, Is.EqualTo(1));
            Assert.That(CombatDamage.Resolve(attack, ref target, 1, true, 2, 7, rules).Outcome, Is.EqualTo(DamageOutcome.Rejected));
            Assert.That(target.ImmunityCharges, Is.EqualTo(1));
            Assert.That(CombatDamage.Resolve(attack, ref target, 2, true, 2, 7, rules).Outcome, Is.EqualTo(DamageOutcome.Immune));
            Assert.That(target.ImmunityCharges, Is.Zero);
        }

        /// <summary>测试开始时通过生成 Tables 加载真实源表生成物，不维护第二套默认配置。</summary>
        /// <returns>完整解析引用的配置集合。</returns>
        /// <remarks>只读 StreamingAssets/Config/Luban；不启动 YooAsset 或更改全局服务。</remarks>
        /// <exception cref="IOException">测试所需生成 bytes 不可读取。</exception>
        private static Tables LoadTables()
        {
            return new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
        }
    }
}
