using System;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>弹丸出生时保存的纯值攻击快照，不依赖攻击者实体继续存活。</summary>
    public struct AttackSnapshot
    {
        public double Attack, Hit, Critical;
        public ulong AttackerLifetime, Sequence;
        public int Faction;

        /// <summary>生成攻击时快照有效攻击和已乘参数的对抗值。</summary>
        /// <param name="attributes">从 TbAttributeProfile 创建的最终属性。</param>
        /// <param name="lifetime">攻击者生命周期序号。</param>
        /// <param name="sequence">该次攻击唯一序号。</param>
        /// <param name="faction">运行时阵营标识。</param>
        /// <returns>可复制到 ECS 或弹丸的纯值结构。</returns>
        public static AttackSnapshot Capture(CombatAttributes attributes, ulong lifetime, ulong sequence, int faction)
        {
            return new AttackSnapshot
            {
                Attack = attributes.Get(EAttributeType.Attack),
                Hit = attributes.Get(EAttributeType.Hit) * attributes.Get(EAttributeType.HitParameter),
                Critical = attributes.Get(EAttributeType.Critical) * attributes.Get(EAttributeType.CriticalParameter),
                AttackerLifetime = lifetime, Sequence = sequence, Faction = faction
            };
        }
    }

    /// <summary>防御与生命的结算状态；池复用必须以新生命周期重新初始化。</summary>
    public struct DamageTarget
    {
        public ulong Lifetime;
        public int Faction, ImmunityCharges;
        public long Health, MaxHealth;
        public double Defense, DefenseParameter, Evasion, CriticalResistance, InvulnerableUntil;
        public bool IsPlayer;

        /// <summary>出生时由完整属性建立生命，禁止用此入口对死亡单位隐式回血。</summary>
        /// <param name="attributes">TbAttributeProfile 聚合结果。</param>
        /// <param name="lifetime">新生命周期序号。</param>
        /// <param name="faction">阵营标识。</param>
        /// <param name="isPlayer">是否使用 TbCombatRules 玩家受击无敌规则。</param>
        /// <returns>清除免伤与无敌状态的出生快照。</returns>
        public static DamageTarget Spawn(CombatAttributes attributes, ulong lifetime, int faction, bool isPlayer)
        {
            var target = new DamageTarget { Lifetime = lifetime, Faction = faction, IsPlayer = isPlayer };
            target.Synchronize(attributes);
            target.Health = target.MaxHealth;
            return target;
        }

        /// <summary>属性变化时同步防守热数据；最大生命增长不回血，下降只截断，不复活。</summary>
        /// <param name="attributes">TbAttributeProfile 及已配置修改来源的最终值。</param>
        /// <remarks>只修改本实例的防守快照、生命上限和必要的当前生命截断。</remarks>
        /// <exception cref="InvalidOperationException">生命上限不能以 long 表示。</exception>
        public void Synchronize(CombatAttributes attributes)
        {
            double maximum = attributes.Get(EAttributeType.MaxHealth);
            if (maximum >= 9223372036854775808d) throw new InvalidOperationException("MaxHealth overflow.");
            MaxHealth = (long)maximum;
            Health = Math.Min(Health, MaxHealth);
            Defense = attributes.Get(EAttributeType.Defense);
            DefenseParameter = attributes.Get(EAttributeType.DefenseParameter);
            Evasion = attributes.Get(EAttributeType.Evasion) * attributes.Get(EAttributeType.EvasionParameter);
            CriticalResistance = attributes.Get(EAttributeType.CriticalResistance) * attributes.Get(EAttributeType.CriticalResistanceParameter);
        }

        /// <summary>恢复入口独立于伤害；只能为尚存活单位补血，测试恢复策略也走此入口。</summary>
        /// <param name="amount">上层配置规则提供的非负恢复量。</param>
        /// <returns>实际恢复生命。</returns>
        /// <remarks>修改当前生命，不发布事件或创建对象。</remarks>
        /// <exception cref="InvalidOperationException">恢复量为负。</exception>
        public long Heal(long amount)
        {
            if (amount < 0) throw new InvalidOperationException("Negative healing.");
            if (Health <= 0) return 0;
            long restored = Math.Min(amount, MaxHealth - Health);
            Health += restored;
            return restored;
        }
    }

    public enum DamageOutcome { Rejected, Invulnerable, Immune, Evaded, Hit, Critical }

    /// <summary>一次结算结果；死亡只在本次首次归零时置位。</summary>
    public struct DamageResult
    {
        public DamageOutcome Outcome;
        public long Damage, HealthLost;
        public bool Killed;
    }

    /// <summary>纯值规则快照，Job 不访问托管 Luban 对象。</summary>
    public struct DamageRules
    {
        public int RandomMinBp, RandomMaxBp, CriticalMultiplierBp, ZeroHitBp, ZeroCriticalBp;
        public double PlayerInvulnerability;

        /// <summary>配置就绪后从 TbCombatRules 校验并复制本结算器所需字段。</summary>
        /// <param name="config">统一配置服务提供的战斗规则。</param>
        /// <returns>不含配置对象引用的快照。</returns>
        /// <exception cref="InvalidOperationException">必需字段不合法。</exception>
        public static DamageRules Capture(CombatRulesConfig config)
        {
            if (config == null) throw new InvalidOperationException("TbCombatRules: missing row.");
            if (config.DamageRandomMinBp < 0 || config.DamageRandomMaxBp < config.DamageRandomMinBp || config.CriticalMultiplierBp < CombatMath.BasisPoints)
                throw new InvalidOperationException($"TbCombatRules {config.Id}: invalid damage range.");
            CombatMath.ProbabilityThreshold(0, 0, config.ZeroHitDenominatorRateBp);
            CombatMath.ProbabilityThreshold(0, 0, config.ZeroCritDenominatorRateBp);
            CombatMath.NonNegative(config.PlayerInvulnerabilitySeconds);
            return new DamageRules { RandomMinBp = config.DamageRandomMinBp, RandomMaxBp = config.DamageRandomMaxBp,
                CriticalMultiplierBp = config.CriticalMultiplierBp, ZeroHitBp = config.ZeroHitDenominatorRateBp,
                ZeroCriticalBp = config.ZeroCritDenominatorRateBp, PlayerInvulnerability = config.PlayerInvulnerabilitySeconds };
        }
    }

    public static class CombatDamage
    {
        /// <summary>几何候选接触后执行生命周期、阵营、免伤、独立随机和生命结算。</summary>
        /// <param name="attack">攻击出生快照。</param>
        /// <param name="target">接触时的目标状态。</param>
        /// <param name="expectedLifetime">请求记录的目标生命周期，拒绝池复用旧请求。</param>
        /// <param name="contact">是否通过本次几何判定。</param>
        /// <param name="time">暂停时不推进的模拟秒数。</param>
        /// <param name="seed">本局固定种子。</param>
        /// <param name="rules">TbCombatRules 的已验证快照。</param>
        /// <returns>拒绝原因、伤害、实际扣血及唯一死亡标记。</returns>
        /// <remarks>只修改目标生命、次数免伤和玩家无敌截止时间；不检查攻击者当前生命，允许同 tick 互杀。</remarks>
        /// <exception cref="InvalidOperationException">有效请求含非法计算输入。</exception>
        public static DamageResult Resolve(in AttackSnapshot attack, ref DamageTarget target, ulong expectedLifetime,
            bool contact, double time, ulong seed, in DamageRules rules)
        {
            if (!contact || target.Health <= 0 || target.Lifetime != expectedLifetime || attack.Faction == target.Faction)
                return new DamageResult { Outcome = DamageOutcome.Rejected };
            CombatMath.NonNegative(time);
            if (time < target.InvulnerableUntil) return new DamageResult { Outcome = DamageOutcome.Invulnerable };
            if (target.ImmunityCharges > 0)
            {
                target.ImmunityCharges--;
                return new DamageResult { Outcome = DamageOutcome.Immune };
            }
            int hit = CombatMath.ProbabilityThreshold(attack.Hit, target.Evasion, rules.ZeroHitBp);
            if (CombatRandom.Inclusive(seed, attack.Sequence, target.Lifetime, CombatRandomPurpose.Hit, 1, CombatMath.BasisPoints) > hit)
                return new DamageResult { Outcome = DamageOutcome.Evaded };
            int criticalThreshold = CombatMath.ProbabilityThreshold(attack.Critical, target.CriticalResistance, rules.ZeroCriticalBp);
            bool critical = CombatRandom.Inclusive(seed, attack.Sequence, target.Lifetime, CombatRandomPurpose.Critical, 1, CombatMath.BasisPoints) <= criticalThreshold;
            int random = CombatRandom.Inclusive(seed, attack.Sequence, target.Lifetime, CombatRandomPurpose.Damage, rules.RandomMinBp, rules.RandomMaxBp);
            long damage = CombatMath.Damage(attack.Attack, target.Defense, target.DefenseParameter, random, rules.CriticalMultiplierBp, critical);
            long loss = Math.Min(target.Health, damage);
            double until = time + rules.PlayerInvulnerability;
            CombatMath.NonNegative(until);
            target.Health -= loss;
            if (loss > 0 && target.IsPlayer) target.InvulnerableUntil = until;
            return new DamageResult { Outcome = critical ? DamageOutcome.Critical : DamageOutcome.Hit,
                Damage = damage, HealthLost = loss, Killed = target.Health == 0 };
        }
    }
}
