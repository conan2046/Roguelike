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
        public DamageTarget Target;
        public CombatCylinder Movement;
        public AttackSnapshot Attack;
        public float2 Position, PreviousPosition, SpawnPosition;
        public float2 ProjectileOffset;
        public int SkillId;
        public float Radius, MoveSpeed, Range, ProjectileSpeed, ProjectileLifetime, ProjectileRadius;
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
        public double Remaining;
        public float Radius;
        public bool Active, BornThisTick;
    }

    /// <summary>每局计数，不将无敌拒绝、命中零伤害混作有效扣血。</summary>
    public struct CombatCounters
    {
        public ulong Tick, NextAttack, NextLifetime;
        public long Attacks, ProjectileSpawns, Candidates, Hits, Evades, Criticals, Invulnerable, Immunities;
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
        /// <returns>是否处于接触范围；仅容忍平方边界的一个 float ULP。</returns>
        public static bool WithinReach(float2 offset, float reach)
        {
            float squared = reach * reach;
            float boundary = math.asfloat(math.asint(squared) + 1);
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
    }
}
