namespace Roguelike.Features.Combat
{
    /// <summary>直接伤害归属的技术分类；不携带策划数值，不把同次伤害重复归到修饰卡。</summary>
    public enum CombatDamageSource { Unspecified, Weapon, Enemy, Environment, Counter, DomainPulse, DomainDetonation }

    /// <summary>正式扣血后写入的纯值事件；EventId在本局单调递增，目标使用生命周期而非可复用槽。</summary>
    public struct CombatLifeDamageEvent
    {
        public ulong EventId, RootCastId, OwnerId, TargetId, Tick;
        public int SkillId;
        public long HealthLost;
        public long RemainingHealth;
        public CombatDamageSource Source;
        public bool PlayerTarget, RewardEligible, RangeBonusTarget;
    }
}
