using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Ecs;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>正式经验掉落的运行时位置与状态；规则状态由 CombatRunModel 持有。</summary>
    public sealed class CombatRuntimeDrop
    {
        public ulong Id { get; internal set; }
        public float2 Position { get; internal set; }
        public ExperienceDropState State { get; internal set; }
    }

    /// <summary>连接纯逻辑单局与 ECS 会话，统一处理时钟、刷怪、死亡、经验、升级、Boss、结算和重开。</summary>
    public sealed class CombatRunCoordinator
    {
        private readonly CombatRunModel model;
        private readonly CombatSession session;
        private readonly Queue<CombatSpawnRequest> pendingSpawns = new Queue<CombatSpawnRequest>();
        private readonly SortedDictionary<ulong, CombatRuntimeDrop> drops = new SortedDictionary<ulong, CombatRuntimeDrop>();
        private long tickMilliRemainder;

        public CombatRunModel Model => model;
        public CombatSession Session => session;
        public IReadOnlyDictionary<ulong, CombatRuntimeDrop> Drops => drops;
        public int PendingSpawnCount => pendingSpawns.Count;
        public bool Paused { get; private set; }

        /// <summary>用同一正式定义创建的模型与会话建立运行时协调器。</summary>
        /// <param name="model">负责表驱动规则和结算的单局模型。</param>
        /// <param name="session">负责 ECS 移动、攻击和伤害的正式会话。</param>
        /// <exception cref="ArgumentNullException">任一依赖为空。</exception>
        /// <exception cref="InvalidOperationException">模型与会话的初始玩家状态不一致。</exception>
        public CombatRunCoordinator(CombatRunModel model, CombatSession session)
        {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            var player = session.ReadUnit(0);
            if (player.ConfigId != model.Definition.Character.Id || player.Target.Health != (long)model.CurrentHealth)
                throw new InvalidOperationException("Formal run model and ECS player do not share the same initial configuration.");
        }

        /// <summary>每渲染帧先推进 ECS 固定 tick，再按真实执行 tick 推进单局时钟并消费全部事件。</summary>
        /// <param name="elapsedSeconds">未缩放渲染时间。</param>
        /// <param name="movement">玩家输入方向。</param>
        /// <returns>本帧实际执行的固定 tick 数。</returns>
        /// <remarks>升级、暂停和结算冻结会话与规则时钟；生成失败的请求保序等待，不静默丢怪。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">经过时间非法。</exception>
        public int Advance(double elapsedSeconds, float2 movement)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            if (Paused || !IsSimulationActive(model.State))
            {
                session.Paused = true;
                return 0;
            }

            session.Paused = false;
            int steps = session.Advance(elapsedSeconds, movement);
            ConsumeCombatEvents();
            if (!IsSimulationActive(model.State))
            {
                session.Paused = true;
                return steps;
            }

            int deltaMilli = ConvertTicksToMilliseconds(steps);
            if (deltaMilli > 0)
            {
                EnqueueSpawns(model.Advance(deltaMilli));
                DrainSpawns();
                AdvanceDrops(steps * session.StepSeconds);
            }
            session.Paused = Paused || !IsSimulationActive(model.State);
            return steps;
        }

        /// <summary>切换玩家主动暂停；升级和结算的强制冻结不受此开关覆盖。</summary>
        public void TogglePause()
        {
            if (IsSimulationActive(model.State)) Paused = !Paused;
            session.Paused = Paused || !IsSimulationActive(model.State);
        }

        /// <summary>应用当前升级面板中的一项选择，并把属性和生命精确同步回 ECS 玩家。</summary>
        /// <param name="panelGeneration">UI 创建时记录的面板代次。</param>
        /// <param name="optionId">当前候选 TbUpgradeOption.id。</param>
        /// <returns>选择通过模型校验并完成同步时为真。</returns>
        /// <remarks>技能解锁由后续多武器调度入口消费；本方法已经同步属性与治疗结果。</remarks>
        public bool ChooseUpgrade(ulong panelGeneration, int optionId)
        {
            if (!model.ChooseUpgrade(panelGeneration, optionId)) return false;
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Formal player lifecycle changed during upgrade selection.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys);
            session.Paused = !IsSimulationActive(model.State) || Paused;
            return true;
        }

        /// <summary>从胜负结算开始下一代单局，并清除实体、掉落、待生成请求和时钟余数。</summary>
        /// <returns>模型处于结算状态并成功重开时为真。</returns>
        /// <remarks>复用配置、ANI 和实体池；上一代次的掉落及面板句柄失效。</remarks>
        public bool Restart()
        {
            if (!model.Restart()) return false;
            session.Restart();
            pendingSpawns.Clear();
            drops.Clear();
            tickMilliRemainder = 0;
            Paused = false;
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Formal player failed to synchronize after restart.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys);
            return true;
        }

        /// <summary>把一次 Advance 累计的死亡事件转换为普通经验掉落、Boss 胜利或玩家失败。</summary>
        /// <remarks>事件按伤害稳定顺序消费；每个怪物生命周期由 CombatRunModel 再做幂等校验。</remarks>
        private void ConsumeCombatEvents()
        {
            for (int index = 0; index < session.DeathCount; index++)
            {
                var death = session.ReadDeath(index);
                if (death.IsPlayer) continue;
                ulong dropId = model.RecordMonsterDeath(death.Lifetime, death.IsBoss);
                if (dropId != 0)
                    drops.Add(dropId, new CombatRuntimeDrop { Id = dropId, Position = death.Position, State = ExperienceDropState.Dropped });
            }
            var player = session.ReadUnit(0);
            double loss = Math.Max(0, model.CurrentHealth - player.Target.Health);
            if (loss > 0) model.ApplyPlayerDamage(loss);
        }

        /// <summary>将真实执行 tick 以有理数换算为整数毫秒，余数跨帧保留避免 60Hz 长局漂移。</summary>
        /// <param name="steps">本渲染帧执行的固定 tick 数。</param>
        /// <returns>本次可提交给 CombatRunModel 的完整毫秒数。</returns>
        private int ConvertTicksToMilliseconds(int steps)
        {
            tickMilliRemainder = checked(tickMilliRemainder + checked((long)steps * 1000));
            int milliseconds = checked((int)(tickMilliRemainder / model.Definition.CombatRules.SimulationHz));
            tickMilliRemainder %= model.Definition.CombatRules.SimulationHz;
            return milliseconds;
        }

        /// <summary>把模型生成请求按计划顺序入队；Boss 到点时先尝试阶段最后一只普通怪，再取消仍阻塞的普通请求。</summary>
        /// <param name="requests">CombatRunModel 本次时间跨度产生的请求。</param>
        private void EnqueueSpawns(IReadOnlyList<CombatSpawnRequest> requests)
        {
            foreach (var request in requests)
            {
                if (request.IsBoss)
                {
                    // 同一固定 tick 到期的阶段最后一只普通怪先尝试落地；此前因圆周占位仍阻塞的请求在 Boss 到点时统一取消。
                    DrainSpawns();
                    pendingSpawns.Clear();
                }
                pendingSpawns.Enqueue(request);
            }
        }

        /// <summary>依次尝试正式生成；圆周被占时保留队首，下一次有效 tick 重试。</summary>
        private void DrainSpawns()
        {
            while (pendingSpawns.Count > 0)
            {
                var request = pendingSpawns.Peek();
                float radius = ConfigNumber.Decode(request.IsBoss
                    ? model.Definition.Boss.SpawnRadiusPixelsMilli
                    : model.Definition.Map.SpawnRadiusPixelsMilli);
                if (!session.TrySpawnMonster(request.Monster, radius, request.Sequence, request.IsBoss, out _)) return;
                pendingSpawns.Dequeue();
            }
        }

        /// <summary>按 TbDropProfile 半径和速度更新掉落，并在接触玩家时只结算一次经验。</summary>
        /// <param name="elapsedSeconds">本帧真实完成固定 tick 对应的模拟秒数。</param>
        /// <remarks>按掉落 ID 稳定顺序处理；首次升级触发后立即停止，剩余掉落保持原状态。</remarks>
        private void AdvanceDrops(double elapsedSeconds)
        {
            if (elapsedSeconds <= 0 || drops.Count == 0) return;
            float magnetRadius = ConfigNumber.Decode(model.Definition.Drop.MagnetRadiusMilli);
            float pickupRadius = ConfigNumber.Decode(model.Definition.Drop.PickupRadiusMilli);
            float speed = ConfigNumber.Decode(model.Definition.Drop.MagnetSpeedMilli);
            float2 player = session.ReadUnit(0).Position;
            foreach (ulong id in drops.Keys.ToArray())
            {
                var drop = drops[id];
                float distance = math.distance(drop.Position, player);
                if (drop.State == ExperienceDropState.Dropped && distance <= magnetRadius && model.TryMagnetizeDrop(id))
                    drop.State = ExperienceDropState.Magnetized;
                if (drop.State == ExperienceDropState.Magnetized)
                {
                    float travel = speed * (float)elapsedSeconds;
                    drop.Position = math.lerp(drop.Position, player, distance <= travel || distance == 0 ? 1 : travel / distance);
                    distance = math.distance(drop.Position, player);
                }
                if (distance <= pickupRadius && model.TryCollectDrop(id))
                {
                    drops.Remove(id);
                    if (!IsSimulationActive(model.State)) return;
                }
            }
        }

        /// <summary>判断规则状态是否允许 ECS、刷怪和掉落继续推进。</summary>
        /// <param name="state">当前 CombatRunState。</param>
        /// <returns>Playing 或 Boss 时为真。</returns>
        private static bool IsSimulationActive(CombatRunState state) =>
            state == CombatRunState.Playing || state == CombatRunState.Boss;
    }
}
