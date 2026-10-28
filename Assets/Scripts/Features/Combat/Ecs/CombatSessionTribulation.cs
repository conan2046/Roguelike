using System;
using cfg;
using Unity.Mathematics;
using Unity.Transforms;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>范围结算的实际结果；试炼只累计有效扣血的怪物数。</summary>
    public readonly struct CombatConfiguredAreaResult
    {
        public readonly int EnemyHits, Kills;
        public readonly long HealthLost;
        public readonly ulong CastId;
        /// <summary>在权威结算完成后返回该次面积攻击的命中与死亡汇总。</summary>
        /// <param name="hits">实际受伤敌人数。</param><param name="kills">首次死亡数。</param><param name="loss">实际扣血总量。</param>
        /// <param name="castId">本次正式攻击实例序号。</param>
        public CombatConfiguredAreaResult(int hits, int kills, long loss, ulong castId = 0) { EnemyHits = hits; Kills = kills; HealthLost = loss; CastId = castId; }
    }

    public sealed partial class CombatSession
    {
        /// <summary>新局初始化时给当前玩家配置铁壁；其他职业不调用，旧关卡默认状态保持关闭。</summary>
        /// <param name="signature">TbTribulationSignature的Guard规则。</param>
        /// <remarks>完成本World任务后更新玩家组件；不会覆盖生命、位置或技能。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="ArgumentException">职业规则不合法。</exception>
        public void ConfigureTribulationGuard(TribulationSignatureConfig signature)
        {
            EnsureAlive(); manager.CompleteAllTrackedJobs();
            var player = manager.GetComponentData<CombatUnit>(units[0]);
            player.Target.Guard = CombatGuardState.Capture(signature);
            manager.SetComponentData(units[0], player); unitStates[0] = player;
        }

        /// <summary>切片职业及环境攻击通过正式几何、随机、免伤、生命与死亡链投递一次面积伤害。</summary>
        /// <param name="center">源表像素换算后的锁定世界坐标。</param><param name="radius">源表范围换算后的世界半径。</param>
        /// <param name="source">玩家当前攻击或TbTribulationRule.attackProfileId的环境属性快照。</param>
        /// <param name="playerPermille">对玩家的源表攻击倍率。</param><param name="enemyPermille">对普通怪的源表攻击倍率。</param>
        /// <param name="bossPermille">对Boss的源表攻击倍率。</param><param name="effectSkill">已预加载且具有区域表现的正式TbSkill。</param>
        /// <param name="sourceKind">直接来源；职业脉冲、引爆和反击独立记账。</param><param name="rewardEligible">排除环境或反射等奖励来源。</param>
        /// <param name="baselineRadius">破围未修饰的正式半径，用于记录扩展后真正新增命中；其他来源为零。</param>
        /// <returns>真正扣血敌人数和唯一死亡汇总；空挥不算试炼命中。</returns>
        /// <remarks>必须在Advance之后、消费该帧事件之前调用。追加正式死亡及区域事件并更新ECS/统计；下一次Advance清空事件。所有来源共用NextAttack保证表现和随机序号唯一。</remarks>
        /// <exception cref="ObjectDisposedException">会话已释放。</exception>
        /// <exception cref="ArgumentException">几何、倍率或表现配置不可用。</exception>
        public CombatConfiguredAreaResult ApplyConfiguredArea(float2 center, float radius, AttackSnapshot source,
            int playerPermille, int enemyPermille, int bossPermille, SkillConfig effectSkill,
            CombatDamageSource sourceKind = CombatDamageSource.Environment, bool rewardEligible = false, float baselineRadius = 0)
        {
            EnsureAlive();
            if (!math.all(math.isfinite(center)) || !math.isfinite(radius) || radius <= 0 || playerPermille < 0 ||
                enemyPermille < 0 || bossPermille < 0 || effectSkill == null || effectSkill.CombatProfileId_Ref.DeliveryType != ESkillDeliveryType.TargetArea)
                throw new ArgumentException("Configured area requires valid geometry, multipliers and a preloaded area skill.");
            manager.CompleteAllTrackedJobs();
            var stats = counters[0]; source.Sequence = ++stats.NextAttack; stats.AreaCasts++; stats.Attacks++;
            source.SkillId = effectSkill.Id; source.Source = sourceKind;
            source.SuppressMechanismRewards = !rewardEligible || sourceKind != CombatDamageSource.Enemy;
            int hits = 0, kills = 0; long healthLost = 0;
            for (int slot = 0; slot < units.Length; slot++)
            {
                var unit = manager.GetComponentData<CombatUnit>(units[slot]);
                if (unit.Target.Health <= 0 || unit.Target.Faction == source.Faction || !CombatGeometry.WithinCapsuleReach(
                    unit.Position + unit.BodyOffset - center, unit.BodyHalfSegment, radius + unit.Radius)) continue;
                int multiplier = unit.Target.IsPlayer ? playerPermille : unit.IsBoss ? bossPermille : enemyPermille;
                if (multiplier == 0) continue;
                var attack = source; attack.Attack *= multiplier / 1000d;
                stats.Candidates++;
                var result = CombatDamage.Resolve(attack, ref unit.Target, unit.Target.Lifetime, true,
                    stats.Tick * StepSeconds, unchecked((ulong)(uint)settings.RandomSeed), damageRules);
                switch (result.Outcome)
                {
                    case DamageOutcome.Hit: stats.Hits++; break;
                    case DamageOutcome.Critical: stats.Hits++; stats.Criticals++; break;
                    case DamageOutcome.Evaded: stats.Evades++; break;
                    case DamageOutcome.Invulnerable: stats.Invulnerable++; break;
                    case DamageOutcome.Immune: stats.Immunities++; break;
                }
                if (result.HealthLost > 0)
                {
                    stats.DamageEvents++; stats.HealthLost += result.HealthLost; healthLost += result.HealthLost;
                    if (settings.TrackBuildFeedback) lifeDamage.Add(new CombatLifeDamageEvent {
                        EventId = (ulong)stats.DamageEvents, RootCastId = source.Sequence, OwnerId = source.AttackerLifetime,
                        TargetId = unit.Target.Lifetime, Tick = stats.Tick, SkillId = effectSkill.Id,
                        Source = sourceKind, HealthLost = result.HealthLost, RemainingHealth = unit.Target.Health, PlayerTarget = unit.Target.IsPlayer,
                        RewardEligible = rewardEligible, RangeBonusTarget = baselineRadius > 0 &&
                            !CombatGeometry.WithinCapsuleReach(unit.Position + unit.BodyOffset - center,
                                unit.BodyHalfSegment, baselineRadius + unit.Radius) });
                    if (!unit.Target.IsPlayer) hits++;
                }
                if (result.Killed)
                {
                    kills++; stats.Deaths++;
                    if (unit.Target.IsPlayer) stats.PlayerDead = true; else stats.AliveMonsters--;
                    deaths.Add(new CombatDeathEvent { Slot = slot, ConfigId = unit.ConfigId, VisualSetId = unit.VisualSetId,
                        Position = unit.Position, Lifetime = unit.Target.Lifetime, Tick = stats.Tick, IsPlayer = unit.Target.IsPlayer, IsBoss = unit.IsBoss });
                    manager.SetComponentData(units[slot], LocalTransform.FromScale(0));
                }
                manager.SetComponentData(units[slot], unit); unitStates[slot] = unit;
            }
            areas.Add(new CombatAreaEvent { SkillId = effectSkill.Id, Position = center, Radius = radius,
                AttackSequence = source.Sequence, Tick = stats.Tick, OwnerLifetime = source.AttackerLifetime, FollowsOwner = false });
            counters[0] = stats;
            return new CombatConfiguredAreaResult(hits, kills, healthLost, source.Sequence);
        }
    }
}
