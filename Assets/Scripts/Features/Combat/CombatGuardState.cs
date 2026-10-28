using System;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>可由Burst复制的铁壁状态；默认未启用，规则仅来自TbTribulationSignature。</summary>
    public struct CombatGuardState
    {
        public int ChargeHits, Charge;
        public double DurationSeconds, DamageTakenMultiplier, UntilSeconds;
        public ulong Activations;
        public int EligibleChargeHits;
        public bool ActivationRewardEligible;

        /// <summary>选定铁壁职业后从职业表复制必要参数；每次新单局重新创建。</summary>
        /// <param name="config">TbTribulationSignature的Guard行。</param>
        /// <returns>尚未蓄能或激活的铁壁快照。</returns>
        /// <exception cref="ArgumentException">职业或铁壁参数不合法。</exception>
        public static CombatGuardState Capture(TribulationSignatureConfig config)
        {
            if (config == null || config.Kind != ETribulationSignature.Guard || config.GuardChargeHits <= 0 ||
                config.GuardDurationMilli <= 0 || config.GuardDamageTakenPermille < 0 || config.GuardDamageTakenPermille > 1000)
                throw new ArgumentException("TbTribulationSignature: invalid guard configuration.", nameof(config));
            return new CombatGuardState { ChargeHits = config.GuardChargeHits,
                DurationSeconds = config.GuardDurationMilli / 1000d, DamageTakenMultiplier = config.GuardDamageTakenPermille / 1000d };
        }

        /// <summary>伤害计算时读取暂停感知的模拟截止时间；未启用状态不进入减伤分支。</summary>
        /// <param name="time">当前固定模拟秒数。</param><returns>本次结算是否受铁壁保护。</returns>
        public bool IsActive(double time) => ChargeHits > 0 && time < UntilSeconds;

        /// <summary>仅实际扣血且存活后蓄能；保护期间不蓄能、不刷新，达到阈值才产生一次反击信号。</summary>
        /// <param name="time">本次实际受击模拟秒数。</param>
        /// <param name="rewardEligible">真实敌人攻击且不是自伤、环境或反射回流时为真；只控制新增修饰与目标收益。</param>
        /// <remarks>修改本目标纯值状态；Activations由协调器消费，不创建表现或直接伤害其他单位。</remarks>
        public void RecordEffectiveHit(double time, bool rewardEligible = true)
        {
            if (ChargeHits <= 0 || IsActive(time)) return;
            Charge++;
            if (rewardEligible) EligibleChargeHits++;
            if (Charge < ChargeHits) return;
            ActivationRewardEligible = EligibleChargeHits == ChargeHits;
            EligibleChargeHits = 0;
            Charge = 0; UntilSeconds = time + DurationSeconds; Activations++;
        }
    }
}
