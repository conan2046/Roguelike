using System;

namespace Roguelike.Features.Combat
{
    /// <summary>随机用途域分离；枚举只标识算法流，不承载概率配置。</summary>
    public enum CombatRandomPurpose : ulong
    {
        Hit,
        Critical,
        Damage,
        SpawnAngle,
        SpawnMonster,
        UpgradeChoice
    }

    /// <summary>基于攻击与目标生命周期的无共享状态随机流。</summary>
    public static class CombatRandom
    {
        /// <summary>每个目标独立派生抽样；拒绝采样消除模偏差，不影响其他攻击。</summary>
        /// <param name="seed">测试场景或局内种子。</param>
        /// <param name="attackSequence">攻击序号；SpawnAngle 域传本局已生成怪物序号。</param>
        /// <param name="targetLifetime">目标生成序号，池复用时必须更新；出生角度没有目标，传零键。</param>
        /// <param name="purpose">命中、暴击、伤害或出生角度的独立用途域。</param>
        /// <param name="minimum">包含的下界，伤害取 TbCombatRules 字段。</param>
        /// <param name="maximum">包含的上界，伤害取 TbCombatRules 字段。</param>
        /// <returns>闭区间内均匀离散样本。</returns>
        /// <exception cref="InvalidOperationException">抽样区间非法。</exception>
        public static int Inclusive(ulong seed, ulong attackSequence, ulong targetLifetime,
            CombatRandomPurpose purpose, int minimum, int maximum)
        {
            if (minimum < 0 || maximum < minimum) throw new InvalidOperationException("Invalid random range.");
            ulong state = Mix(Mix(Mix(seed) ^ attackSequence) ^ targetLifetime) ^ Mix((ulong)purpose);
            ulong width = (ulong)((long)maximum - minimum + 1);
            ulong threshold = unchecked(0UL - width) % width;
            ulong sample;
            do { state = unchecked(state + 0x9E3779B97F4A7C15UL); sample = Mix(state); }
            while (sample < threshold);
            return (int)(minimum + (long)(sample % width));
        }

        /// <summary>将键扩散为独立算法状态；常量为 SplitMix64 技术常量而非游戏数值。</summary>
        /// <param name="value">待扩散的整数键。</param>
        /// <returns>扩散后的无符号位模式。</returns>
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
