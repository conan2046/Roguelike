using System;
using cfg;
using Unity.Entities;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>首版单玩家对怪物的封闭阵营语义，不承载可配置阵营关系或战斗数值。</summary>
    public enum CombatFaction { Player, Monster }

    /// <summary>战斗实体热数据；所有属性与技能数值由 Luban 转换，失活状态由生命决定。</summary>
    public struct CombatUnit : IComponentData
    {
        public int ConfigId, VisualSetId;
        public bool IsBoss;
        public DamageTarget Target;
        public CombatCylinder Movement;
        public AttackSnapshot Attack;
        public float2 Position, PreviousPosition, SpawnPosition, BodyOffset, HitEffectOffset;
        public float2 ProjectileOffset;
        public int SkillId;
        public float Radius, BodyHalfSegment, MoveSpeed, Range, ProjectileSpeed, ProjectileLifetime, ProjectileRadius;
        public double Interval, Cooldown, NextTargetRefresh;
        public int TargetSlot;
        public ulong TargetLifetime;
        public ESkillDeliveryType Delivery;
        public int AttackOptionStart, AttackOptionCount, LockedTargetSlot;
        public float2 Facing;
        public double BaseInterval, AttackAge, WindupSeconds;
        public bool AttackActive, AttackHitProcessed;
        public ulong LockedTargetLifetime;
        public AttackSnapshot PendingAttack;
        public CombatAttackOption AttackOption;
    }

    /// <summary>弹丸池槽；非活动槽保留实体，重新发射时清空所有运动与攻击状态。</summary>
    public struct CombatProjectile : IComponentData
    {
        public AttackSnapshot Attack;
        public float2 Position, Velocity;
        public float2 CollisionOffset;
        public int SkillId;
        public double Age, Remaining;
        public float Radius;
        public bool Active, BornThisTick;
    }

    /// <summary>正式玩家的一把独立武器状态；配置在创建会话时转为纯值，固定 tick 内不访问托管表。</summary>
    public struct CombatWeaponState
    {
        public AttackSnapshot Attack;
        public float2 ProjectileOffset;
        public int SkillId;
        public float Range, ProjectileSpeed, ProjectileLifetime, ProjectileRadius, AreaRadius;
        public double BaseInterval, Interval, Cooldown;
        public ESkillDeliveryType Delivery;
        public bool Active;
    }

    /// <summary>弹丸首次有效几何接触后写入的单帧表现事件；超时与越界回收不会产生该事件。</summary>
    public struct CombatImpactEvent
    {
        public int SkillId;
        public float2 Position;
        public ulong AttackSequence, Tick;
    }

    /// <summary>目标位置群体技能触发时写入的单帧表现事件；同一攻击序号只对应一次释放。</summary>
    public struct CombatAreaEvent
    {
        public int SkillId;
        public float2 Position;
        public ulong AttackSequence, Tick;
    }

    /// <summary>单位生命首次降至零时写入的单帧事件；配置身份随生命周期固定，不通过池槽反推。</summary>
    public struct CombatDeathEvent
    {
        public int Slot, ConfigId, VisualSetId;
        public float2 Position;
        public ulong Lifetime, Tick;
        public bool IsPlayer, IsBoss;
    }

    /// <summary>每局计数，不将无敌拒绝、命中零伤害混作有效扣血。</summary>
    public struct CombatCounters
    {
        public ulong Tick, NextAttack, NextLifetime;
        public long Attacks, ProjectileSpawns, AreaCasts, Candidates, Hits, Evades, Criticals, Invulnerable, Immunities;
        public long DamageEvents, HealthLost, Deaths, Replenished;
        public long MeleeJudgements, MeleeWhiffs;
        public int AliveMonsters, ActiveProjectiles, PeakProjectiles;
        public bool PlayerDead, UsedBurst;
    }

    /// <summary>单 tick 伤害请求；排序键不依赖 Entity/chunk 或空间哈希枚举顺序。</summary>
    public struct CombatDamageRequest : IComparable<CombatDamageRequest>
    {
        public int TargetSlot;
        public ulong TargetLifetime;
        public AttackSnapshot Attack;

        /// <summary>伤害阶段按目标生命周期、攻击者生命周期、攻击序号进行稳定全序比较。</summary>
        /// <param name="other">同 tick 另一请求。</param>
        /// <returns>标准排序比较结果。</returns>
        public int CompareTo(CombatDamageRequest other)
        {
            int result = TargetLifetime.CompareTo(other.TargetLifetime);
            if (result != 0) return result;
            result = Attack.AttackerLifetime.CompareTo(other.Attack.AttackerLifetime);
            return result != 0 ? result : Attack.Sequence.CompareTo(other.Attack.Sequence);
        }
    }

    /// <summary>场地几何纯计算，与贴图、世界轴和表现大小解耦。</summary>
    public static class CombatGeometry
    {
        /// <summary>攻击边缘按浮点可表示精度比较，避免移动停在边缘后因单个 ULP 舍入永远无法攻击。</summary>
        /// <param name="offset">两圆中心差。</param>
        /// <param name="reach">表内技能范围与双方身体半径之和。</param>
        /// <returns>是否处于接触范围；仅容忍平方边界的四个 float ULP。</returns>
        public static bool WithinReach(float2 offset, float reach)
        {
            float squared = reach * reach;
            // 移动扫掠会在接触比例前退一个 ULP；斜向乘加累计误差略大于单次平方误差，
            // 四个 ULP 只覆盖浮点运算噪声，不形成可感知的额外攻击距离。
            float boundary = math.asfloat(math.asint(squared) + 4);
            return math.lengthsq(offset) <= boundary;
        }

        /// <summary>对相对运动线段求最早圆形接触，覆盖初始重叠与高速穿越。</summary>
        /// <param name="start">相对目标起点的位置。</param>
        /// <param name="end">相对目标终点的位置。</param>
        /// <param name="radius">两个圆半径之和。</param>
        /// <param name="fraction">首次接触在线段上的归一化时间。</param>
        /// <returns>本段是否发生几何接触。</returns>
        public static bool Sweep(float2 start, float2 end, float radius, out float fraction)
        {
            float c = math.lengthsq(start) - radius * radius;
            fraction = 0;
            if (c <= 0) return true;
            float2 direction = end - start;
            float a = math.lengthsq(direction);
            if (a == 0) return false;
            float b = math.dot(start, direction);
            float discriminant = b * b - a * c;
            if (discriminant < 0) return false;
            fraction = (-b - math.sqrt(discriminant)) / a;
            return fraction >= 0 && fraction <= 1;
        }

        /// <summary>计算点到以原点为中心的纵向线段的平方距离。</summary>
        /// <param name="relative">点相对胶囊中心的偏移。</param>
        /// <param name="halfSegment">中轴线段半长。</param>
        /// <returns>与胶囊中轴线最近点的平方距离。</returns>
        public static float VerticalSegmentDistanceSquared(float2 relative, float halfSegment)
        {
            float outsideY = math.max(0f, math.abs(relative.y) - halfSegment);
            return relative.x * relative.x + outsideY * outsideY;
        }

        /// <summary>按两个纵向胶囊的中轴间距判断边缘是否进入配置范围。</summary>
        /// <param name="relative">两个胶囊中心差。</param>
        /// <param name="halfSegment">两个胶囊中轴半长之和。</param>
        /// <param name="reach">两半径与额外配置距离之和。</param>
        /// <returns>是否进入可命中距离。</returns>
        public static bool WithinCapsuleReach(float2 relative, float halfSegment, float reach)
        {
            float squared = reach * reach;
            float boundary = math.asfloat(math.asint(squared) + 4);
            return VerticalSegmentDistanceSquared(relative, halfSegment) <= boundary;
        }

        /// <summary>计算纵向胶囊中轴最近点指向查询点的法线。</summary>
        /// <param name="relative">查询点相对胶囊中心的偏移。</param>
        /// <param name="halfSegment">胶囊中轴半长。</param>
        /// <param name="fallback">查询点恰好在中轴上时的确定方向。</param>
        /// <returns>单位法线。</returns>
        public static float2 VerticalCapsuleNormal(float2 relative, float halfSegment, float2 fallback)
        {
            float2 closest = new float2(0f, math.clamp(relative.y, -halfSegment, halfSegment));
            return math.normalizesafe(relative - closest, math.normalizesafe(fallback, new float2(1f, 0f)));
        }

        /// <summary>计算移动点与纵向胶囊的最早连续接触。</summary>
        /// <param name="relative">移动点起点相对胶囊中心的位置。</param>
        /// <param name="motion">本 tick 相对位移。</param>
        /// <param name="radius">移动圆与目标胶囊半径之和。</param>
        /// <param name="halfSegment">目标胶囊与移动胶囊中轴半长之和。</param>
        /// <param name="fraction">首次接触在 motion 上的归一化比例。</param>
        /// <param name="normal">接触时从目标指向移动点的法线。</param>
        /// <returns>本段是否与胶囊接触。</returns>
        public static bool SweepVerticalCapsule(float2 relative, float2 motion, float radius,
            float halfSegment, out float fraction, out float2 normal)
        {
            fraction = 0f;
            normal = VerticalCapsuleNormal(relative, halfSegment, -motion);
            if (VerticalSegmentDistanceSquared(relative, halfSegment) <= radius * radius) return true;
            float best = float.PositiveInfinity;
            if (math.abs(motion.x) > 0.0000001f)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    float candidate = (side * radius - relative.x) / motion.x;
                    float y = relative.y + motion.y * candidate;
                    if (candidate >= 0f && candidate <= 1f && math.abs(y) <= halfSegment)
                        best = math.min(best, candidate);
                }
            }
            for (int cap = -1; cap <= 1; cap += 2)
            {
                float2 start = relative - new float2(0f, cap * halfSegment);
                float a = math.lengthsq(motion);
                float b = math.dot(start, motion);
                float c = math.lengthsq(start) - radius * radius;
                float discriminant = b * b - a * c;
                if (a > 0f && discriminant >= 0f)
                {
                    float candidate = (-b - math.sqrt(discriminant)) / a;
                    if (candidate >= 0f && candidate <= 1f) best = math.min(best, candidate);
                }
            }
            if (!math.isfinite(best)) return false;
            fraction = best;
            float2 contact = relative + motion * best;
            normal = VerticalCapsuleNormal(contact, halfSegment, -motion);
            return true;
        }
    }
}
