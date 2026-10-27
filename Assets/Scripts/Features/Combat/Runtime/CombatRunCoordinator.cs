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
    public sealed partial class CombatRunCoordinator
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
            if (model.Build != null) session.ApplyWeaponUpgrades(model.Build.CaptureWeapons(), model.AttributeSnapshot);
            InitializeTribulation();
        }

        /// <summary>每渲染帧先推进 ECS 固定 tick，再按真实执行 tick 推进单局时钟并消费全部事件。</summary>
        /// <param name="elapsedSeconds">未缩放渲染时间。</param>
        /// <param name="movement">玩家输入方向。</param>
        /// <returns>本帧实际执行的固定 tick 数。</returns>
        /// <remarks>升级、炼器领奖、暂停和结算冻结会话与规则时钟；TbTribulationRule/Signature在真实固定步后投递正式伤害，再统一消费死亡。生成失败的请求保序等待，不静默丢怪。</remarks>
        /// <exception cref="ArgumentOutOfRangeException">经过时间非法。</exception>
        public int Advance(double elapsedSeconds, float2 movement)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            if (Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation)
            {
                session.Paused = true;
                return 0;
            }

            session.Paused = false;
            int steps = session.Advance(elapsedSeconds, movement);
            int deltaMilli = ConvertTicksToMilliseconds(steps);
            AdvanceTribulation(deltaMilli);
            ConsumeCombatEvents();
            if (!IsSimulationActive(model.State))
            {
                if (model.Result.HasValue) { runtimeChests.Clear(); TerminateTribulation(); }
                session.Paused = true;
                return steps;
            }

            if (deltaMilli > 0)
            {
                EnqueueSpawns(model.Advance(deltaMilli));
                DrainSpawns();
                ConsumeRunEvents();
                AdvanceDrops(steps * session.StepSeconds);
                CollectNearbyChests();
            }
            session.Paused = Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation;
            return steps;
        }

        /// <summary>切换玩家主动暂停；升级和结算的强制冻结不受此开关覆盖。</summary>
        public void TogglePause()
        {
            if (IsSimulationActive(model.State)) Paused = !Paused;
            session.Paused = Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation;
        }

        /// <summary>应用当前升级面板中的一项选择，并把属性和生命精确同步回 ECS 玩家。</summary>
        /// <param name="panelGeneration">UI 创建时记录的面板代次。</param>
        /// <param name="optionId">当前候选 TbUpgradeOption.id。</param>
        /// <returns>选择通过模型校验并完成同步时为真。</returns>
        /// <remarks>同步活动技能、被动属性和TbRunWeapon.ranks局部倍率；TbTribulationRule待领奖保持冻结，已发射快照不追溯变更。</remarks>
        /// <exception cref="InvalidOperationException">已验证的选择后玩家生命周期异常改变。</exception>
        public bool ChooseUpgrade(ulong panelGeneration, int optionId)
        {
            if (!model.ChooseUpgrade(panelGeneration, optionId)) return false;
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Formal player lifecycle changed during upgrade selection.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys, model.Build?.CaptureWeapons());
            if (model.Build != null) session.ApplyWeaponUpgrades(model.Build.CaptureWeapons(), model.AttributeSnapshot);
            session.Paused = !IsSimulationActive(model.State) || Paused || TribulationBlocksSimulation;
            return true;
        }

        /// <summary>从胜负结算开始下一代单局，并清除实体、掉落、待生成请求和时钟余数。</summary>
        /// <returns>模型处于结算状态并成功重开时为真。</returns>
        /// <remarks>复用配置、ANI和实体池；上一代掉落/面板失效，并重建TbTribulationSlice职业、阵域及试炼代次。</remarks>
        /// <exception cref="InvalidOperationException">重开后玩家不能同步到正式属性或切片规则无效。</exception>
        public bool Restart()
        {
            if (!model.Restart()) return false;
            session.Restart();
            pendingSpawns.Clear();
            drops.Clear();
            runtimeChests.Clear();
            tickMilliRemainder = 0;
            Paused = false;
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Formal player failed to synchronize after restart.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys, model.Build?.CaptureWeapons());
            if (model.Build != null) session.ApplyWeaponUpgrades(model.Build.CaptureWeapons(), model.AttributeSnapshot);
            InitializeTribulation();
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
                {
                    drops.Add(dropId, new CombatRuntimeDrop { Id = dropId, Position = death.Position, State = ExperienceDropState.Dropped });
                    MonsterConfig monster = model.Definition.Monsters.First(item => item.Id == death.ConfigId);
                    if (monster.DeathChestProfileId_Ref != null)
                    {
                        ulong chestId = model.RegisterChest(monster.DeathChestProfileId_Ref);
                        if (chestId != 0) runtimeChests.Add(chestId, new CombatRuntimeChest
                            { Id = chestId, Position = death.Position, Profile = monster.DeathChestProfileId_Ref });
                    }
                }
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
                float radius = request.IsBoss
                    ? model.Definition.Boss.SpawnRadiusPixels
                    : request.RunEvent != null ? request.RunEvent.SpawnDistancePixels : model.Definition.Map.SpawnRadiusPixels;
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
            float worldUnitsPerPixel = model.Definition.CombatRules.WorldUnitsPerPixel;
            float magnetRadius = model.Definition.Drop.MagnetRadiusPixels * worldUnitsPerPixel;
            float pickupRadius = model.Definition.Drop.PickupRadiusPixels * worldUnitsPerPixel;
            float speed = model.Definition.Drop.MagnetSpeedPixelsPerSecond * worldUnitsPerPixel;
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
