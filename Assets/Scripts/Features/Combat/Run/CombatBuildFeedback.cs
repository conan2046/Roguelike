using System;
using System.Collections.Generic;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>每局有效生命伤害和职业目标聚合；按正式事件顺序去重，不保留每目标每帧历史。</summary>
    public sealed class CombatBuildFeedback
    {
        private ulong lastEventId;
        private readonly HashSet<ulong> mechanismCasts = new HashSet<ulong>();
        private readonly SortedDictionary<CombatDamageSource, long> sources = new SortedDictionary<CombatDamageSource, long>();
        private readonly SortedDictionary<int, long> weapons = new SortedDictionary<int, long>();
        private readonly ContentGoalConfig goal;
        private readonly SortedDictionary<int, long> bossPhases = new SortedDictionary<int, long>();
        public IReadOnlyDictionary<int, long> BossPhaseEntries => bossPhases;
        public CombatDamageSource? DeathSource { get; private set; }
        public int? DeathSkillId { get; private set; }
        public ulong? DeathRootCastId { get; private set; }
        public long CombatMilli { get; private set; }
        public long TotalDamage { get; private set; }
        public long IncomingDamage { get; private set; }
        public long CounterBonusTargets { get; private set; }
        public int GoalEvents { get; private set; }
        public int BestTargets { get; private set; }
        public int EffectiveDetonations { get; private set; }
        public int EffectiveCounters { get; private set; }
        public int CounterTargets { get; private set; }
        private readonly List<int> detonationTargets = new List<int>();
        public IReadOnlyList<int> DetonationTargets => detonationTargets;
        public int WeaveActivations { get; private set; }
        public ulong GuardActivations { get; set; }
        public IReadOnlyDictionary<CombatDamageSource, long> DamageBySource => sources;
        public IReadOnlyDictionary<int, long> WeaponDamage => weapons;
        public double Dps => CombatMilli > 0 ? TotalDamage * 1000d / CombatMilli : 0;

        /// <summary>新单局绑定 TbContentGoal；没有该功能的关卡只聚合数据，不提供目标奖励。</summary>
        /// <param name="goal">本局模式允许的正式目标；可以为空。</param>
        public CombatBuildFeedback(ContentGoalConfig goal) { this.goal = goal; }

        /// <summary>协调器只在实际模拟步完成后推进有效战斗时钟，暂停、选卡和结算不调用。</summary>
        /// <param name="milliseconds">本次完成的模拟毫秒。</param>
        /// <exception cref="ArgumentOutOfRangeException">负时间。</exception>
        public void AdvanceClock(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            CombatMilli = checked(CombatMilli + milliseconds);
        }

        /// <summary>按单调 EventId 消费正式实际扣血；只计归属玩家的直接伤害，承伤独立。</summary>
        /// <param name="damage">来自会话的规范顺序事件；不接收过量、零伤或盾吸收估算。</param>
        /// <param name="ownerId">本局玩家生命周期。</param><returns>本事件首次被接受时为真。</returns>
        public bool RecordDamage(in CombatLifeDamageEvent damage, ulong ownerId)
        {
            if (damage.EventId == 0 || damage.EventId <= lastEventId || damage.HealthLost <= 0) return false;
            lastEventId = damage.EventId;
            if (damage.PlayerTarget)
            {
                IncomingDamage = checked(IncomingDamage + damage.HealthLost);
                if (damage.RemainingHealth == 0) { DeathSource = damage.Source; DeathSkillId = damage.SkillId; DeathRootCastId = damage.RootCastId; }
                return true;
            }
            if (damage.OwnerId != ownerId || damage.Source == CombatDamageSource.Environment || damage.Source == CombatDamageSource.Enemy) return true;
            TotalDamage = checked(TotalDamage + damage.HealthLost);
            sources.TryGetValue(damage.Source, out long source); sources[damage.Source] = checked(source + damage.HealthLost);
            if (damage.Source == CombatDamageSource.Weapon)
            { weapons.TryGetValue(damage.SkillId, out long skill); weapons[damage.SkillId] = checked(skill + damage.HealthLost); }
            if (damage.Source == CombatDamageSource.Counter && damage.RangeBonusTarget && damage.RewardEligible) CounterBonusTargets++;
            return true;
        }

        /// <summary>正式范围结算完毕后按攻击实例计一次有效机制；多目标只增加本次命中数，Boss清阵不会进入此入口。</summary>
        /// <param name="castId">正式唯一攻击序号。</param><param name="source">反击或重叠引爆。</param>
        /// <param name="distinctTargets">同一面积请求中实际扣血的不同敌人数。</param><param name="rewardEligible">是否排除了环境、自伤或递归来源。</param>
        /// <returns>首次有效重叠引爆时为真，供交织刷新；不是目标完成标志。</returns>
        public bool RecordMechanismCast(ulong castId, CombatDamageSource source, int distinctTargets, bool rewardEligible)
        {
            if (source != CombatDamageSource.Counter && source != CombatDamageSource.DomainDetonation) return false;
            if (distinctTargets <= 0 || castId == 0 || !mechanismCasts.Add(castId)) return false;
            if (source == CombatDamageSource.Counter) { EffectiveCounters++; CounterTargets = checked(CounterTargets + distinctTargets); }
            else if (source == CombatDamageSource.DomainDetonation) { EffectiveDetonations++; detonationTargets.Add(distinctTargets); }
            else return false;
            bool matches = rewardEligible && goal != null && (goal.SignatureKind == ETribulationSignature.Guard
                ? source == CombatDamageSource.Counter : source == CombatDamageSource.DomainDetonation);
            if (matches)
            {
                BestTargets = Math.Max(BestTargets, distinctTargets);
                if (distinctTargets >= goal.MinimumTargets) GoalEvents++;
            }
            return rewardEligible && source == CombatDamageSource.DomainDetonation;
        }

        /// <summary>实际交织增益开始或刷新时记录收益；不会重复计入伤害。</summary>
        public void RecordWeaveBenefit() { WeaveActivations++; }

        /// <summary>真实Boss阶段首次到达时记录有效战斗时钟，不补造未实际经历的阶段。</summary>
        /// <param name="phase">TbTribulationRule.bossPhases中当前实际阶段的显示序号。</param>
        public void RecordBossPhase(int phase) { if (phase > 0 && !bossPhases.ContainsKey(phase)) bossPhases.Add(phase, CombatMilli); }

        /// <summary>局末根据 TbContentGoal 的有效事件阈值与胜利要求计算解锁资格。</summary>
        /// <param name="victory">正式 Boss 死亡胜利结算。</param><returns>本局完整目标达成时为真。</returns>
        public bool GoalCompleted(bool victory) => goal != null && GoalEvents >= goal.RequiredEvents && (!goal.RequiresVictory || victory);
    }
}
