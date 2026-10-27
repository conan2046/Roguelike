using System;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>入局与离线校验共用的 TbSkillCombat 投递约束；不复制表内业务数值。</summary>
    public static class CombatSkillRules
    {
        /// <summary>配置生成后检查阵型、穿透、连锁和持续区域的互斥字段与时间边界。</summary>
        /// <param name="combat">TbSkill.combatProfileId 指向的机制行。</param>
        /// <exception cref="InvalidOperationException">机制缺失、字段冲突或存在不可结算的参数。</exception>
        public static void Validate(SkillCombatConfig combat)
        {
            if (combat == null) throw new InvalidOperationException("TbSkillCombat: missing profile.");
            bool projectile = combat.DeliveryType == ESkillDeliveryType.Projectile;
            bool area = combat.DeliveryType == ESkillDeliveryType.TargetArea;
            bool valid = combat.DamagePermille > 0 && combat.PierceCount >= 0 && combat.ChainCount >= 0 &&
                combat.ChainRangePixels >= 0 && !float.IsInfinity(combat.ChainRangePixels) &&
                combat.AreaDurationMilli >= 0 && combat.AreaPulseIntervalMilli >= 0 && combat.ImpactRadiusPixels >= 0 &&
                !float.IsInfinity(combat.ImpactRadiusPixels) && combat.AreaRingCount >= 0 &&
                combat.AreaRingWidthPixels >= 0 && !float.IsInfinity(combat.AreaRingWidthPixels);
            if (projectile)
                valid &= combat.ProjectilePattern.HasValue && Enum.IsDefined(typeof(EProjectilePattern), combat.ProjectilePattern.Value) &&
                    combat.ProjectileCount > 0 && combat.SpreadDegrees >= 0 && combat.SpreadDegrees < 360 &&
                    (combat.ProjectilePattern == EProjectilePattern.Fan ? combat.ProjectileCount > 1 && combat.SpreadDegrees > 0 : combat.SpreadDegrees == 0) &&
                    // 本批连锁以一个弹丸承载一次完整施法，命中记录覆盖整条链；不支持未共享去重记录的并行链。
                    (combat.ChainCount > 0 ? combat.ChainRangePixels > 0 && combat.PierceCount == 0 && combat.ProjectileCount == 1 : combat.ChainRangePixels == 0) &&
                    (combat.ImpactRadiusPixels == 0 || combat.PierceCount == 0 && combat.ChainCount == 0) &&
                    combat.AreaDurationMilli == 0 && combat.AreaPulseIntervalMilli == 0 && !combat.AreaFollowsOwner &&
                    !combat.AreaRetargets && combat.AreaRingCount == 0 && combat.AreaRingWidthPixels == 0;
            else
                valid &= !combat.ProjectilePattern.HasValue && combat.ProjectileCount == 0 && combat.SpreadDegrees == 0 &&
                    combat.PierceCount == 0 && combat.ChainCount == 0 && combat.ChainRangePixels == 0 && combat.ImpactRadiusPixels == 0 &&
                    (combat.AreaRetargets ? area && combat.AreaDurationMilli > 0 && !combat.AreaFollowsOwner : true) &&
                    (combat.AreaRingCount > 0 ? area && combat.AreaFollowsOwner && combat.AreaRingWidthPixels > 0 : combat.AreaRingWidthPixels == 0) &&
                    (area ? (combat.AreaDurationMilli > 0 ? combat.AreaPulseIntervalMilli > 0 &&
                        combat.AreaPulseIntervalMilli <= combat.AreaDurationMilli : combat.AreaPulseIntervalMilli == 0) :
                        combat.AreaDurationMilli == 0 && combat.AreaPulseIntervalMilli == 0 && !combat.AreaFollowsOwner);
            if (!valid) throw new InvalidOperationException($"TbSkillCombat {combat.Id}: invalid pattern, hit budget or area lifecycle.");
        }
    }
}
