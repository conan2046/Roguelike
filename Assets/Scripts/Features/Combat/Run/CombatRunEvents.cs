using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>本代次场上宝箱规则状态；坐标与 Unity 资源由协调器及表现层拥有。</summary>
    public sealed class CombatChestState
    {
        public ulong Id { get; internal set; }
        public ChestProfileConfig Profile { get; internal set; }
        public bool Queued { get; internal set; }
    }

    /// <summary>本次 Advance 实际到期的时间事件；地图箱包含可幂等消费的本局箱句柄。</summary>
    public readonly struct CombatRunEventRequest
    {
        public readonly RunEventConfig Config;
        public readonly ulong ChestId;

        /// <summary>调度时把已验证源事件与已注册箱身份关联，不持有 Unity 对象。</summary>
        /// <param name="config">TbRunEvent 行。</param>
        /// <param name="chestId">地图箱句柄；精英事件为零。</param>
        public CombatRunEventRequest(RunEventConfig config, ulong chestId) { Config = config; ChestId = chestId; }
    }

    public sealed partial class CombatRunModel
    {
        private readonly SortedDictionary<ulong, CombatChestState> chests = new SortedDictionary<ulong, CombatChestState>();
        private readonly Queue<ulong> chestQueue = new Queue<ulong>();
        private readonly List<CombatRunEventRequest> firedEvents = new List<CombatRunEventRequest>();
        private int eventIndex;
        private ulong chestSequence, chestPanelGeneration;
        private CombatRunState resumeAfterChest;
        private RunEventConfig lastEventNotice;
        public IReadOnlyDictionary<ulong, CombatChestState> Chests => chests;
        public IReadOnlyList<CombatRunEventRequest> FiredEvents => firedEvents;
        public CombatChestState CurrentChest { get; private set; }
        public WeaponEvolutionConfig ChestEvolution { get; private set; }
        public IReadOnlyList<UpgradeOptionConfig> ChestChoices { get; private set; } = Array.Empty<UpgradeOptionConfig>();
        public ulong ChestPanelGeneration => chestPanelGeneration;
        public string EventNotice => lastEventNotice != null && ElapsedMilli < (long)lastEventNotice.TimeMilli + lastEventNotice.NoticeDurationMilli
            ? lastEventNotice.Notice : string.Empty;

        /// <summary>按事件时间切段普通刷新，确保大步长与小步长得到相同生成序号、随机权重和事件顺序。</summary>
        /// <param name="targetMilli">本次模拟真实到达的单局时间。</param>
        /// <param name="requests">本次按时间稳定输出的普通怪、精英及 Boss 请求。</param>
        /// <remarks>TbRunEvent 只到期一次；地图箱先注册规则句柄，再由协调器产生实际场上位置。</remarks>
        private void ScheduleEventsAndSpawns(long targetMilli, List<CombatSpawnRequest> requests)
        {
            firedEvents.Clear();
            while (eventIndex < definition.Events.Count && definition.Events[eventIndex].TimeMilli <= targetMilli)
            {
                RunEventConfig item = definition.Events[eventIndex++];
                ScheduleNormalSpawns(item.TimeMilli, requests);
                ulong chestId = 0;
                if (item.Kind == ERunEventKind.EliteSpawn)
                    requests.Add(new CombatSpawnRequest(RunGeneration, ++spawnSequence, item.TimeMilli, item.MonsterId_Ref, false, item));
                else chestId = RegisterChest(item.ChestProfileId_Ref);
                firedEvents.Add(new CombatRunEventRequest(item, chestId));
                lastEventNotice = item;
            }
            ScheduleNormalSpawns(targetMilli, requests);
        }

        /// <summary>地图事件或已幂等确认的精英死亡创建本局宝箱；不自动发奖或打开面板。</summary>
        /// <param name="profile">当前定义预加载闭包内的 TbChestProfile。</param>
        /// <returns>本代次唯一宝箱句柄；已经结算时为零。</returns>
        /// <exception cref="InvalidOperationException">传入关卡目录外的宝箱规则。</exception>
        /// <remarks>死亡调用方必须先检查 RecordMonsterDeath 首次返回的经验句柄，避免重复掉箱。</remarks>
        internal ulong RegisterChest(ChestProfileConfig profile)
        {
            if (Result.HasValue) return 0;
            if (profile == null || !definition.ChestProfiles.Contains(profile)) throw new InvalidOperationException("TbChestProfile is outside the run catalog.");
            ulong id = ComposeRunHandle(++chestSequence);
            chests.Add(id, new CombatChestState { Id = id, Profile = profile });
            return id;
        }

        /// <summary>协调器确认玩家处于拾取距离后将宝箱排队；升级面板优先，箱面板不覆盖已有交互。</summary>
        /// <param name="chestId">本代次场上宝箱句柄。</param>
        /// <returns>首次拾取并排队时为真，重复或旧代次返回假。</returns>
        /// <remarks>只修改规则状态；场上表现由协调器移除，奖励仍须通过 ClaimChest 领取。</remarks>
        internal bool TryCollectChest(ulong chestId)
        {
            if (Result.HasValue || !chests.TryGetValue(chestId, out CombatChestState chest) || chest.Queued) return false;
            chest.Queued = true;
            chestQueue.Enqueue(chestId);
            OpenNextChest();
            return true;
        }

        /// <summary>当前交互空闲时展示下一箱；按 TbChestProfile 优先一条可进化链，否则复用正式候选池。</summary>
        /// <remarks>保存 Playing/Boss 原状态并冻结战斗；面板使用全局递增代次拒绝旧 UI 回调。</remarks>
        private void OpenNextChest()
        {
            if (!IsSimulationActive(State) || PendingLevelUps > 0 || chestQueue.Count == 0) return;
            CurrentChest = chests[chestQueue.Dequeue()];
            ChestEvolution = CurrentChest.Profile.AllowEvolution ? Build.EligibleEvolutions().FirstOrDefault() : null;
            ChestChoices = Array.Empty<UpgradeOptionConfig>();
            if (ChestEvolution == null)
            {
                IReadOnlyList<UpgradeOptionConfig> saved = currentChoices;
                if (TryBuildUpgradeChoices(CurrentChest.Profile.NormalRewardPoolId_Ref)) ChestChoices = currentChoices;
                currentChoices = saved;
            }
            chestPanelGeneration = ++panelGeneration;
            resumeAfterChest = State;
            State = CombatRunState.ChestReward;
        }

        /// <summary>校验本箱、代次与候选后只领取一次；进化同槽替换，普通奖励复用升级规则，耗尽箱可直接清点。</summary>
        /// <param name="expectedGeneration">面板创建时记录的 ChestPanelGeneration。</param>
        /// <param name="optionId">普通候选 ID；进化或候选耗尽时为零。</param>
        /// <returns>成功领取并进入下一交互或恢复模拟时为真。</returns>
        /// <remarks>改变构筑、属性或生命；调用方随后必须同步 ECS。结算、重开及重复点击均会拒绝旧回调。</remarks>
        public bool ClaimChest(ulong expectedGeneration, int optionId)
        {
            if (State != CombatRunState.ChestReward || CurrentChest == null || expectedGeneration != chestPanelGeneration) return false;
            if (ChestEvolution != null)
            {
                if (optionId != 0 || !Build.TryEvolve(ChestEvolution.Id)) return false;
                SynchronizeBuildSkills();
            }
            else if (ChestChoices.Count > 0)
            {
                UpgradeOptionConfig option = ChestChoices.FirstOrDefault(item => item.Id == optionId);
                if (option == null || !IsUpgradeEligible(option)) return false;
                int rank = GetUpgradeRank(option.Id) + 1;
                ApplyUpgrade(option, rank);
                upgradeRanks[option.Id] = rank;
            }
            else if (optionId != 0) return false;
            chests.Remove(CurrentChest.Id);
            CurrentChest = null;
            ChestEvolution = null;
            ChestChoices = Array.Empty<UpgradeOptionConfig>();
            State = resumeAfterChest;
            OpenNextChest();
            return true;
        }

        /// <summary>胜负或重开时废弃所有未领取箱与回调，避免上一代奖励继续修改构筑。</summary>
        /// <remarks>清空本局宝箱、队列、当前候选和面板代次；不修改源配置或已获得构筑。</remarks>
        private void ClearChestInteractions()
        {
            chests.Clear(); chestQueue.Clear(); CurrentChest = null; ChestEvolution = null;
            ChestChoices = Array.Empty<UpgradeOptionConfig>(); chestPanelGeneration = 0;
        }

        /// <summary>构造或重开时恢复 TbRunEvent 时间游标与本代次箱序列；面板全局序列由主状态机保留。</summary>
        /// <remarks>同时清理旧箱交互、事件提示和本帧通知，不释放由协调器或 UI 拥有的 Unity 对象。</remarks>
        private void ResetEventsForRun()
        {
            ClearChestInteractions(); eventIndex = 0; chestSequence = 0; firedEvents.Clear(); lastEventNotice = null;
        }
    }
}
