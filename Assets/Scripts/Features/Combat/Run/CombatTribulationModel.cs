using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>雷坛试炼和奖励的单局状态；终止后拒绝旧输入与领奖。</summary>
    public enum TribulationTrialState { Idle, Active, RewardPending, Failed, Terminated }

    /// <summary>锁点后不再跟随玩家的预警数据，位置及半径均为源表逻辑像素。</summary>
    public readonly struct TribulationStrike
    {
        public readonly ulong Sequence;
        public readonly double XPixels, YPixels, RadiusPixels;
        public readonly long WarningAtMilli, HitAtMilli;
        public readonly bool FromBoss, ClearsDomains;

        /// <summary>按当前权威位置和Luban时序创建一次不可变雷击预警。</summary>
        /// <param name="sequence">本局严格递增序号。</param><param name="xPixels">锁定横坐标。</param><param name="yPixels">锁定纵坐标。</param>
        /// <param name="radiusPixels">命中半径。</param><param name="warningAtMilli">预警时点。</param><param name="hitAtMilli">实际命中时点。</param>
        /// <param name="fromBoss">是否来自Boss。</param><param name="clearsDomains">命中时是否清除相交阵域。</param>
        public TribulationStrike(ulong sequence, double xPixels, double yPixels, double radiusPixels, long warningAtMilli, long hitAtMilli, bool fromBoss, bool clearsDomains)
        {
            Sequence = sequence; XPixels = xPixels; YPixels = yPixels; RadiusPixels = radiusPixels;
            WarningAtMilli = warningAtMilli; HitAtMilli = hitAtMilli; FromBoss = fromBoss; ClearsDomains = clearsDomains;
        }
    }

    /// <summary>消费TbTribulationSlice/Rule的确定性试炼模型；不加载资源，不读写Unity对象或生命。</summary>
    public sealed class CombatTribulationModel
    {
        private readonly HashSet<int> spent = new HashSet<int>();
        private readonly List<TribulationStrike> warnings = new List<TribulationStrike>();
        private readonly List<TribulationStrike> due = new List<TribulationStrike>();
        private readonly HashSet<ulong> recordedStrikes = new HashSet<ulong>();
        private ulong nextSequence;
        private long trialStartMilli, nextTrialWarningMilli, nextBossWarningMilli;
        public TribulationSliceConfig Config { get; }
        public ulong RunGeneration { get; }
        public ulong RewardGeneration { get; private set; }
        public long ElapsedMilli { get; private set; }
        public long TrialRemainingMilli => State == TribulationTrialState.Active ? Math.Max(0, Config.RuleId_Ref.TrialDurationMilli - (ElapsedMilli - trialStartMilli)) : 0;
        public int EnemyHits { get; private set; }
        public int ActiveAltarId { get; private set; }
        public int BossPhase { get; private set; }
        public int? CurrentRefinementId { get; private set; }
        public TribulationTrialState State { get; private set; }
        public IReadOnlyCollection<int> SpentAltars => spent;
        public IReadOnlyList<TribulationStrike> Warnings => warnings;
        public IReadOnlyList<TribulationStrike> DueStrikes => due;

        /// <summary>进入一个新的正式单局代次；配置由CombatRunDefinition验证，时钟从本局开始。</summary>
        /// <param name="config">TbTribulationSlice强引用。</param><param name="runGeneration">正式单局代次。</param>
        /// <exception cref="ArgumentNullException">配置或规则缺失。</exception>
        public CombatTribulationModel(TribulationSliceConfig config, ulong runGeneration)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            if (config.RuleId_Ref == null) throw new ArgumentNullException(nameof(config));
            RunGeneration = runGeneration;
        }

        /// <summary>查询已解锁且未消耗的最近雷坛；不消耗机会，也不隐式启动试炼。</summary>
        /// <param name="xPixels">玩家权威横坐标像素。</param><param name="yPixels">玩家权威纵坐标像素。</param>
        /// <returns>最近可用雷坛；没有可用坛时为空。</returns>
        public TribulationAltar NearestAvailableAltar(double xPixels, double yPixels)
        {
            TribulationAltar best = null; double distance = double.PositiveInfinity;
            foreach (var altar in Config.RuleId_Ref.Altars)
            {
                if (spent.Contains(altar.Id) || ElapsedMilli < altar.AvailableAfterMilli) continue;
                double squared = SquaredDistance(xPixels, yPixels, altar.XPixels, altar.YPixels);
                if (squared >= distance) continue;
                best = altar; distance = squared;
            }
            return best;
        }

        /// <summary>真实UI请求靠近雷坛引劫；同时验证单局代次、解锁、距离与当前状态。</summary>
        /// <param name="generation">UI捕获的本局代次。</param><param name="altarId">表内雷坛序号。</param>
        /// <param name="xPixels">玩家权威横坐标。</param><param name="yPixels">玩家权威纵坐标。</param>
        /// <returns>首次合法启动时为真。</returns><remarks>立即消耗此坛机会，重置本次命中计数；不直接修改战斗生命。</remarks>
        public bool TryStart(ulong generation, int altarId, double xPixels, double yPixels)
        {
            if (generation != RunGeneration || State == TribulationTrialState.Terminated || State == TribulationTrialState.Active || State == TribulationTrialState.RewardPending) return false;
            var altar = Config.RuleId_Ref.Altars.FirstOrDefault(a => a.Id == altarId);
            double radius = Config.RuleId_Ref.ActivationRadiusPixels;
            if (altar == null || spent.Contains(altar.Id) || ElapsedMilli < altar.AvailableAfterMilli || SquaredDistance(xPixels, yPixels, altar.XPixels, altar.YPixels) > radius * radius) return false;
            spent.Add(altar.Id); ActiveAltarId = altar.Id; EnemyHits = 0;
            trialStartMilli = nextTrialWarningMilli = ElapsedMilli;
            recordedStrikes.Clear();
            State = TribulationTrialState.Active;
            return true;
        }

        /// <summary>仅由实际执行的模拟步推进，收集试炼和Boss已经到期的雷击；命中后再调用ResolveTrialDeadline。</summary>
        /// <param name="milliseconds">实际模拟步换算的完整毫秒。</param><param name="xPixels">当前玩家横坐标。</param><param name="yPixels">当前玩家纵坐标。</param>
        /// <param name="bossHealthPermille">实际存活Boss生命比例；无Boss时为空。</param>
        /// <remarks>暂停和强制选择不调用此入口。DueStrikes仅供当前同步借用，下一次推进会清空；位置锁定后不追随玩家。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">时间为负。</exception>
        public void Advance(int milliseconds, double xPixels, double yPixels, int? bossHealthPermille)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            due.Clear();
            if (State == TribulationTrialState.Terminated || State == TribulationTrialState.RewardPending || milliseconds == 0) return;
            ElapsedMilli = checked(ElapsedMilli + milliseconds);
            var rule = Config.RuleId_Ref;
            if (State == TribulationTrialState.Active)
            {
                long finalHit = trialStartMilli + rule.TrialDurationMilli;
                while (nextTrialWarningMilli <= ElapsedMilli && nextTrialWarningMilli + rule.WarningMilli <= finalHit)
                {
                    warnings.Add(new TribulationStrike(++nextSequence, xPixels, yPixels, rule.StrikeRadiusPixels,
                        nextTrialWarningMilli, nextTrialWarningMilli + rule.WarningMilli, false, false));
                    nextTrialWarningMilli += rule.StrikeIntervalMilli;
                }
            }
            if (bossHealthPermille.HasValue && bossHealthPermille.Value > 0)
            {
                int phaseIndex = 0;
                for (int i = 0; i < rule.BossPhases.Count; i++) if (bossHealthPermille.Value <= rule.BossPhases[i].HealthPermille) phaseIndex = i;
                if (BossPhase == 0) nextBossWarningMilli = ElapsedMilli;
                BossPhase = phaseIndex + 1;
                var phase = rule.BossPhases[phaseIndex];
                while (nextBossWarningMilli <= ElapsedMilli)
                {
                    for (int i = 0; i < phase.StrikeCount; i++)
                    {
                        double angle = Math.PI * 2 * i / phase.StrikeCount;
                        double offset = phase.StrikeCount == 1 ? 0 : rule.BossSpreadPixels;
                        warnings.Add(new TribulationStrike(++nextSequence, xPixels + Math.Cos(angle) * offset, yPixels + Math.Sin(angle) * offset,
                            phase.RadiusPixels, nextBossWarningMilli, nextBossWarningMilli + phase.WarningMilli, true, phase.ClearsDomains));
                    }
                    nextBossWarningMilli += phase.IntervalMilli;
                }
            }
            for (int i = 0; i < warnings.Count;)
            {
                if (warnings[i].HitAtMilli > ElapsedMilli) { i++; continue; }
                due.Add(warnings[i]); warnings.RemoveAt(i);
            }
            due.Sort(CompareStrikes);
        }

        /// <summary>会话完成真实环境伤害后累加本次试炼命中，不接受Boss自身招式或过期试炼的进度。</summary>
        /// <param name="strike">已经到期并完成结算的雷击。</param><param name="enemyHits">实际命中且造成正伤害的怪物数量。</param>
        /// <remarks>该入口不发奖励；同一批次所有雷击结算后才判断截止状态。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">命中数量为负。</exception>
        public void RecordEnemyHits(TribulationStrike strike, int enemyHits)
        {
            if (enemyHits < 0) throw new ArgumentOutOfRangeException(nameof(enemyHits));
            if (State == TribulationTrialState.Active && !strike.FromBoss && due.Any(s => s.Sequence == strike.Sequence) && recordedStrikes.Add(strike.Sequence))
                EnemyHits = checked(EnemyHits + enemyHits);
        }

        /// <summary>本次到期雷击全部结算后，在试炼时限到达时只产生一次成功奖励或失败。</summary>
        /// <remarks>先结算最后一击再判定目标，避免边界丢失真实命中；成功会提升面板代次并冻结后续切片推进。</remarks>
        public void ResolveTrialDeadline()
        {
            if (State != TribulationTrialState.Active || ElapsedMilli - trialStartMilli < Config.RuleId_Ref.TrialDurationMilli) return;
            State = EnemyHits >= Config.RuleId_Ref.RequiredEnemyHits ? TribulationTrialState.RewardPending : TribulationTrialState.Failed;
            if (State == TribulationTrialState.RewardPending) RewardGeneration++;
        }

        /// <summary>领取当前炼器候选；校验本局和面板代次后返回应由正式构筑消费者应用的源表行。</summary>
        /// <param name="runGeneration">UI捕获的单局代次。</param><param name="rewardGeneration">UI捕获的奖励面板代次。</param>
        /// <param name="refinementId">本职业候选的源表ID。</param><param name="refinement">成功时返回合法炼器配置。</param>
        /// <returns>首次合法领取时为真。</returns><remarks>只记录选定道途；武器同槽替换和ECS同步由协调器执行。</remarks>
        public bool TryClaim(ulong runGeneration, ulong rewardGeneration, int refinementId, out TribulationRefinementConfig refinement)
        {
            refinement = null;
            if (State != TribulationTrialState.RewardPending || runGeneration != RunGeneration || rewardGeneration != RewardGeneration || CurrentRefinementId == refinementId) return false;
            refinement = Config.RefinementIds_Ref.FirstOrDefault(r => r.Id == refinementId);
            if (refinement == null) return false;
            CurrentRefinementId = refinement.Id; State = TribulationTrialState.Idle;
            return true;
        }

        /// <summary>死亡、结算、重开或退出前终止本代次试炼并使旧UI回调失效。</summary>
        /// <remarks>清空预警与到期队列，不产生延迟雷击；旧模型永久不可再次启动或领奖。</remarks>
        public void Terminate()
        {
            State = TribulationTrialState.Terminated; RewardGeneration++;
            warnings.Clear(); due.Clear();
        }

        /// <summary>对同一同步步到期的雷击按计划命中时点和创建序号稳定排序。</summary>
        /// <param name="first">第一道雷击。</param><param name="second">第二道雷击。</param><returns>稳定全序比较。</returns>
        private static int CompareStrikes(TribulationStrike first, TribulationStrike second)
        {
            int result = first.HitAtMilli.CompareTo(second.HitAtMilli);
            return result != 0 ? result : first.Sequence.CompareTo(second.Sequence);
        }

        /// <summary>按统一逻辑像素坐标计算距离平方，避免引入Unity对象或单位换算默认值。</summary>
        /// <param name="x">第一点横坐标。</param><param name="y">第一点纵坐标。</param><param name="otherX">第二点横坐标。</param><param name="otherY">第二点纵坐标。</param>
        /// <returns>两点的欧氏距离平方。</returns>
        private static double SquaredDistance(double x, double y, double otherX, double otherY) => (x - otherX) * (x - otherX) + (y - otherY) * (y - otherY);
    }
}
