using System;

namespace Roguelike.Features.Combat.Ecs
{
    public sealed partial class CombatSession
    {
        private bool feedbackMovementConfigured;
        private float feedbackBaseMoveSpeed, feedbackMoveMultiplier;
        private ulong feedbackMoveUntilTick;
        public int LifeDamageCount => lifeDamage.IsCreated ? lifeDamage.Length : 0;

        /// <summary>协调器在 Advance 之后按顺序借读实际扣血事件，下次 Advance 前完成消费。</summary>
        /// <param name="index">本次事件索引。</param><returns>无 Unity 对象引用的规范事件。</returns>
        /// <exception cref="IndexOutOfRangeException">索引不属于本次会话事件范围。</exception>
        public CombatLifeDamageEvent ReadLifeDamage(int index) => lifeDamage[index];

        /// <summary>交织开始、刷新、到期或属性升级后设置真实移速；基值仍来自模型的 Luban 属性快照。</summary>
        /// <param name="lifetime">当前玩家代次。</param><param name="baseSpeedPixels">当前正式属性移速。</param>
        /// <param name="multiplier">TbMechanismCard.moveSpeedPermille 换算的倍率；无增益时为乘法单位元。</param>
        /// <param name="untilTick">增益截止固定Tick，批量追帧也在同一模拟边界恢复。</param>
        /// <remarks>只改当前玩家 MoveSpeed，不改模板、输入、永久属性或其他单位；重开由模板恢复。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">移速或倍率非法。</exception>
        public void SetFeedbackMoveSpeed(ulong lifetime, double baseSpeedPixels, double multiplier, ulong untilTick)
        {
            EnsureAlive();
            if (double.IsNaN(baseSpeedPixels) || double.IsInfinity(baseSpeedPixels) || baseSpeedPixels < 0 ||
                double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
            var player = ReadUnit(0);
            if (player.Target.Lifetime != lifetime || player.Target.Health <= 0) return;
            feedbackMovementConfigured = true; feedbackBaseMoveSpeed = (float)(baseSpeedPixels * rules.WorldUnitsPerPixel);
            feedbackMoveMultiplier = (float)multiplier; feedbackMoveUntilTick = untilTick;
            float speed = feedbackBaseMoveSpeed * (Statistics.Tick < untilTick ? feedbackMoveMultiplier : 1f);
            // 加速不得使空间候选搜索继续使用较小的旧运动上界；到期保留保守上界不会改变规则。
            maximumMotion = Math.Max(maximumMotion, (float)(feedbackBaseMoveSpeed * feedbackMoveMultiplier * StepSeconds));
            if (player.MoveSpeed == speed) return;
            player.MoveSpeed = speed; manager.SetComponentData(units[0], player); unitStates[0] = player;
        }
    }
}
