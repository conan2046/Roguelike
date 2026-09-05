using System;

namespace Roguelike.Features.Combat
{
    /// <summary>无托管配置依赖的战斗数学；输入由已验证的 Luban 快照提供。</summary>
    public static class CombatMath
    {
        // 万分比单位换算，不是可调游戏规则。
        public const int BasisPoints = 10000;

        /// <summary>结算时执行参考文档的两次取整，不设置最低伤害。</summary>
        /// <param name="attack">属性方案聚合后的有效攻击。</param>
        /// <param name="defense">目标当前防御。</param>
        /// <param name="defenseParameter">目标当前防御参数。</param>
        /// <param name="randomBp">TbCombatRules 随机范围内的本次抽样。</param>
        /// <param name="criticalMultiplierBp">TbCombatRules.criticalMultiplierBp。</param>
        /// <param name="critical">已独立判定的暴击结果。</param>
        /// <returns>非负整数伤害，尚未截断到目标生命。</returns>
        /// <exception cref="InvalidOperationException">输入非法或中间结果溢出。</exception>
        public static long Damage(double attack, double defense, double defenseParameter,
            int randomBp, int criticalMultiplierBp, bool critical)
        {
            NonNegative(attack); NonNegative(defense); Positive(defenseParameter);
            if (randomBp < 0 || criticalMultiplierBp < BasisPoints)
                throw new InvalidOperationException("TbCombatRules: invalid damage multiplier.");
            if (attack == 0) return 0;
            double denominator = attack + defense * defenseParameter;
            Positive(denominator);
            double numerator = attack * attack;
            NonNegative(numerator);
            double basis = Math.Floor(numerator / denominator * (randomBp / (double)BasisPoints));
            double result = Math.Floor(basis * (critical ? criticalMultiplierBp / (double)BasisPoints : 1));
            NonNegative(result);
            // 2^63 is the first double outside the signed long range.
            if (result >= 9223372036854775808d) throw new InvalidOperationException("Combat damage overflow.");
            return (long)result;
        }

        /// <summary>命中或暴击判定前计算向下取整的万分比阈值。</summary>
        /// <param name="attacker">攻击方数值乘对应参数后的有效值。</param>
        /// <param name="defender">防守方数值乘对应参数后的有效值。</param>
        /// <param name="zeroPolicy">TbCombatRules 对应零分母阈值。</param>
        /// <returns>包含两端的万分比阈值。</returns>
        /// <exception cref="InvalidOperationException">数值非有限、负数或策略越界。</exception>
        public static int ProbabilityThreshold(double attacker, double defender, int zeroPolicy)
        {
            NonNegative(attacker); NonNegative(defender);
            if (zeroPolicy < 0 || zeroPolicy > BasisPoints) throw new InvalidOperationException("Invalid zero-denominator policy.");
            double sum = attacker + defender;
            NonNegative(sum);
            return sum == 0 ? zeroPolicy : (int)Math.Floor(attacker / sum * BasisPoints);
        }

        /// <summary>技能初始化或属性变更时计算有效间隔，不引入默认攻速。</summary>
        /// <param name="baseInterval">TbSkillCombat.baseInterval。</param>
        /// <param name="speed">最终 AttackSpeedMultiplier 属性。</param>
        /// <param name="minimum">TbCombatRules.minAttackInterval。</param>
        /// <returns>合法的有效攻击间隔。</returns>
        /// <exception cref="InvalidOperationException">任一输入或结果不合法。</exception>
        public static double AttackInterval(double baseInterval, double speed, double minimum)
        {
            Positive(baseInterval); Positive(speed); Positive(minimum);
            double result = Math.Max(baseInterval / speed, minimum);
            Positive(result);
            return result;
        }

        /// <summary>攻速变化时保留冷却剩余比例，已就绪状态保持就绪。</summary>
        /// <param name="remaining">原剩余秒数。</param>
        /// <param name="oldInterval">原有效间隔。</param>
        /// <param name="newInterval">新有效间隔。</param>
        /// <returns>换算后的剩余秒数。</returns>
        /// <exception cref="InvalidOperationException">输入或结果非法。</exception>
        public static double RescaleCooldown(double remaining, double oldInterval, double newInterval)
        {
            NonNegative(remaining); Positive(oldInterval); Positive(newInterval);
            if (remaining > oldInterval) throw new InvalidOperationException("Cooldown exceeds interval.");
            double result = remaining / oldInterval * newInterval;
            NonNegative(result);
            return result;
        }

        /// <summary>在配置转换与纯计算入口拒绝负数和非有限输入。</summary>
        /// <param name="value">待检查值。</param>
        /// <exception cref="InvalidOperationException">值非法。</exception>
        public static void NonNegative(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new InvalidOperationException("Combat value must be finite and non-negative.");
        }

        /// <summary>在参数与时间入口额外拒绝零，防止除零。</summary>
        /// <param name="value">待检查值。</param>
        /// <exception cref="InvalidOperationException">值不为有限正数。</exception>
        public static void Positive(double value)
        {
            NonNegative(value);
            if (value == 0) throw new InvalidOperationException("Combat parameter must be positive.");
        }
    }
}
