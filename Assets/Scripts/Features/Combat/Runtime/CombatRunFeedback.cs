using System;
using cfg;
using Roguelike.Features.Combat.Run;

namespace Roguelike.Features.Combat.Runtime
{
    public sealed partial class CombatRunCoordinator
    {
        private long weaveUntilMilli;
        private ulong lastConsumedDomainEvent;
        public CombatBuildFeedback BuildFeedback { get; private set; }
        public double WeaveRemainingSeconds => Math.Max(0, weaveUntilMilli - BuildFeedback.CombatMilli) / 1000d;
        public float CounterRadiusPixels
        {
            get
            {
                var signature = Tribulation.Config.SignatureId_Ref;
                var modifier = model.SelectedMechanism(ETribulationSignature.Guard);
                bool eligible = session.ReadUnit(0).Target.Guard.ActivationRewardEligible;
                return signature.CounterRadiusPixels * (modifier != null && eligible ? modifier.CounterRangePermille / 1000f : 1f);
            }
        }

        /// <summary>每个新单局绑定表内目标并清除统计和临时增益；不读取 Profile。</summary>
        private void InitializeBuildFeedback()
        { BuildFeedback = new CombatBuildFeedback(model.Definition.DraftPolicy?.GoalId_Ref); weaveUntilMilli = 0; lastConsumedDomainEvent = 0; }

        /// <summary>正式模拟前应用交织当前倍率，到期恢复当前基础属性，暂停不会推进增益时钟。</summary>
        private void ApplyFeedbackMovement()
        {
            var modifier = model.SelectedMechanism(ETribulationSignature.Domains);
            double multiplier = modifier != null && WeaveRemainingSeconds > 0 ? modifier.MoveSpeedPermille / 1000d : 1d;
            ulong untilTick = weaveUntilMilli > 0 ? checked((ulong)Math.Ceiling(weaveUntilMilli * (double)model.Definition.CombatRules.SimulationHz / 1000d)) : 0;
            session.SetFeedbackMoveSpeed(session.ReadUnit(0).Target.Lifetime, model.AttributeSnapshot.MoveSpeed, multiplier, untilTick);
        }

        /// <summary>范围结算成功后只对首次有效引爆刷新交织，倍率和时长来自 TbMechanismCard。</summary>
        /// <param name="castId">正式范围攻击序号。</param><param name="targets">实际扣血的不同敌人数。</param>
        private void AcceptDetonation(ulong castId, int targets)
        {
            if (!BuildFeedback.RecordMechanismCast(castId, CombatDamageSource.DomainDetonation, targets, true)) return;
            var modifier = model.SelectedMechanism(ETribulationSignature.Domains);
            if (modifier == null) return;
            weaveUntilMilli = checked(BuildFeedback.CombatMilli + modifier.DurationMilli);
            BuildFeedback.RecordWeaveBenefit(); model.MarkMechanismBenefit(BuildFeedback.CombatMilli);
        }

        /// <summary>职业和普通伤害均结算后一次聚合事件；不逐敌人刷新UI，修饰收益不重复加入伤害。</summary>
        private void ConsumeBuildFeedback()
        {
            ulong owner = session.ReadUnit(0).Target.Lifetime;
            for (int i = 0; i < session.LifeDamageCount; i++) BuildFeedback.RecordDamage(session.ReadLifeDamage(i), owner);
            BuildFeedback.GuardActivations = session.ReadUnit(0).Target.Guard.Activations;
            if (BuildFeedback.CounterBonusTargets > 0 && model.SelectedMechanism(ETribulationSignature.Guard) != null) model.MarkMechanismBenefit(BuildFeedback.CombatMilli);
        }

        /// <summary>普通升级重掷仅提交模型决策，不改变炼器、属性或 ECS。</summary>
        /// <param name="offerId">UI记录的候选组代次。</param><returns>成功替换候选时为真。</returns>
        public bool RerollUpgrade(ulong offerId) => !Paused && model.RerollUpgrade(offerId);

        /// <summary>普通升级跳过后恢复正确模拟冻结状态，不影响已排队炼器奖励。</summary>
        /// <param name="offerId">UI记录的候选组代次。</param><returns>成功放弃本次能力时为真。</returns>
        public bool SkipUpgrade(ulong offerId)
        {
            if (Paused || !model.SkipUpgrade(offerId)) return false;
            session.Paused = Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation; return true;
        }
    }
}
