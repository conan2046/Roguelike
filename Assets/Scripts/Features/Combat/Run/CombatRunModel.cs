using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>正式单局核心状态；暂停选择和结算状态不会推进模拟时钟。</summary>
    public enum CombatRunState
    {
        Playing,
        UpgradeChoice,
        Boss,
        VictorySettlement,
        DefeatSettlement
    }

    /// <summary>经验掉落在纯逻辑层可观察的生命周期状态。</summary>
    public enum ExperienceDropState
    {
        Dropped,
        Magnetized
    }

    /// <summary>由单局调度器发给 ECS 的确定性生成请求。</summary>
    public readonly struct CombatSpawnRequest
    {
        /// <summary>用表数据和当前单局代次创建一条生成请求。</summary>
        /// <param name="runGeneration">产生请求的单局代次。</param>
        /// <param name="sequence">本代次内严格递增的生成序号。</param>
        /// <param name="scheduledTimeMilli">按 TbSpawnPhase 或 TbStageRule 计算的计划时间。</param>
        /// <param name="monster">TbMonster 引用。</param>
        /// <param name="isBoss">是否来自 TbBossEncounter。</param>
        public CombatSpawnRequest(ulong runGeneration, ulong sequence, long scheduledTimeMilli, MonsterConfig monster, bool isBoss)
        {
            RunGeneration = runGeneration;
            Sequence = sequence;
            ScheduledTimeMilli = scheduledTimeMilli;
            Monster = monster;
            IsBoss = isBoss;
        }

        public ulong RunGeneration { get; }
        public ulong Sequence { get; }
        public long ScheduledTimeMilli { get; }
        public MonsterConfig Monster { get; }
        public bool IsBoss { get; }
    }

    /// <summary>不依赖 Unity 对象的正式单局状态机，负责表驱动的刷怪、经验、升级、Boss、结算与重开。</summary>
    public sealed class CombatRunModel
    {
        private static readonly IReadOnlyList<CombatSpawnRequest> NoSpawns = Array.Empty<CombatSpawnRequest>();
        private readonly CombatRunDefinition definition;
        private readonly Dictionary<int, int> upgradeRanks = new Dictionary<int, int>();
        private readonly SortedDictionary<int, SkillConfig> activeSkills = new SortedDictionary<int, SkillConfig>();
        private readonly HashSet<ulong> recordedDeaths = new HashSet<ulong>();
        private readonly Dictionary<ulong, ExperienceDropState> drops = new Dictionary<ulong, ExperienceDropState>();
        private IReadOnlyList<UpgradeOptionConfig> currentChoices = Array.Empty<UpgradeOptionConfig>();
        private CombatAttributes attributes;
        private CombatRunState resumeAfterUpgrade;
        private int phaseIndex;
        private int unitsInWave;
        private long nextSpawnTimeMilli;
        private ulong spawnSequence;
        private ulong dropSequence;
        private ulong choiceSequence;
        private ulong panelGeneration;

        /// <summary>从已经完成跨表验证的定义创建第一代单局。</summary>
        /// <param name="definition">由 TbStage 聚合的只读正式关卡定义。</param>
        /// <exception cref="ArgumentNullException">定义为空。</exception>
        public CombatRunModel(CombatRunDefinition definition)
        {
            this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
            RunGeneration = 1;
            ResetRunState();
        }

        public CombatRunDefinition Definition => definition;
        public CombatRunState State { get; private set; }
        public ulong RunGeneration { get; private set; }
        public long ElapsedMilli { get; private set; }
        public int Level { get; private set; }
        public int Experience { get; private set; }
        public int PendingLevelUps { get; private set; }
        public int Kills { get; private set; }
        public int CompletedWaves { get; private set; }
        public bool BossSpawned { get; private set; }
        public ERunResultType? Result { get; private set; }
        public double CurrentHealth { get; private set; }
        public double MaxHealth => attributes.Get(EAttributeType.MaxHealth);
        /// <summary>获取当前基础属性与升级来源聚合后的纯值快照，供 ECS 同步且不暴露修改入口。</summary>
        public CombatAttributeSnapshot AttributeSnapshot => CombatAttributeSnapshot.Capture(attributes);
        public ulong UpgradePanelGeneration => panelGeneration;
        public IReadOnlyList<UpgradeOptionConfig> CurrentChoices => currentChoices;
        public IReadOnlyDictionary<int, int> UpgradeRanks => upgradeRanks;
        public IReadOnlyDictionary<int, SkillConfig> ActiveSkills => activeSkills;
        public IReadOnlyDictionary<ulong, ExperienceDropState> Drops => drops;

        /// <summary>在 Playing 或 Boss 状态推进表驱动时钟，并返回本次跨过的全部生成事件。</summary>
        /// <param name="deltaMilli">本次固定步进的毫秒数，必须非负。</param>
        /// <returns>按计划时间排列的普通怪与 Boss 生成请求。</returns>
        /// <exception cref="ArgumentOutOfRangeException">步进为负。</exception>
        /// <remarks>UpgradeChoice 和结算状态直接返回空集合，确保战斗与计时完全冻结。</remarks>
        public IReadOnlyList<CombatSpawnRequest> Advance(int deltaMilli)
        {
            if (deltaMilli < 0) throw new ArgumentOutOfRangeException(nameof(deltaMilli));
            if (deltaMilli == 0 || !IsSimulationActive(State)) return NoSpawns;

            long target = checked(ElapsedMilli + deltaMilli);
            if (State == CombatRunState.Boss)
            {
                ElapsedMilli = target;
                return NoSpawns;
            }

            var requests = new List<CombatSpawnRequest>();
            long playingTarget = Math.Min(target, definition.Rule.BossTimeMilli);
            ScheduleNormalSpawns(playingTarget, requests);
            ElapsedMilli = playingTarget;
            if (target >= definition.Rule.BossTimeMilli && !BossSpawned)
            {
                BossSpawned = true;
                State = CombatRunState.Boss;
                spawnSequence++;
                requests.Add(new CombatSpawnRequest(RunGeneration, spawnSequence, definition.Rule.BossTimeMilli,
                    definition.Boss.MonsterId_Ref, true));
            }
            if (State == CombatRunState.Boss) ElapsedMilli = target;
            return requests;
        }

        /// <summary>记录一个由 ECS 生命周期标识的怪物死亡，并为普通怪创建唯一经验掉落。</summary>
        /// <param name="monsterLifetime">怪物池槽位与代际组成的全局唯一生命周期标识。</param>
        /// <param name="isBoss">是否为本局 Boss。</param>
        /// <returns>普通怪首次死亡产生的掉落 ID；重复死亡、Boss 或结算后返回零。</returns>
        /// <remarks>会更新击杀数、掉落集合，并可能立即写入胜利结算。</remarks>
        public ulong RecordMonsterDeath(ulong monsterLifetime, bool isBoss)
        {
            if (!IsSimulationActive(State)) return 0;
            if (isBoss)
            {
                if (State != CombatRunState.Boss || !BossSpawned || !recordedDeaths.Add(monsterLifetime)) return 0;
                Kills++;
                if (definition.Boss.VictoryOnDeath) Settle(ERunResultType.Victory);
                return 0;
            }

            if (!recordedDeaths.Add(monsterLifetime)) return 0;
            Kills++;
            dropSequence++;
            ulong id = ComposeRunHandle(dropSequence);
            drops.Add(id, ExperienceDropState.Dropped);
            return id;
        }

        /// <summary>玩家进入 TbDropProfile.magnetRadius 时将一个未拾取掉落锁定为吸附态。</summary>
        /// <param name="dropId">RecordMonsterDeath 返回的本局掉落 ID。</param>
        /// <returns>是否首次从 Dropped 进入 Magnetized。</returns>
        /// <remarks>只修改掉落逻辑状态；位置移动由 ECS 按 TbDropProfile.magnetSpeedMilli 执行。</remarks>
        public bool TryMagnetizeDrop(ulong dropId)
        {
            if (!IsSimulationActive(State) || !drops.TryGetValue(dropId, out var state) || state != ExperienceDropState.Dropped)
                return false;
            drops[dropId] = ExperienceDropState.Magnetized;
            return true;
        }

        /// <summary>玩家进入 TbDropProfile.pickupRadius 时消费掉落并结算一次表内经验。</summary>
        /// <param name="dropId">处于 Dropped 或 Magnetized 的本局掉落 ID。</param>
        /// <returns>是否首次成功拾取。</returns>
        /// <remarks>成功时移除掉落实体状态，并可能进入 UpgradeChoice 冻结战斗。</remarks>
        public bool TryCollectDrop(ulong dropId)
        {
            if (!IsSimulationActive(State) || !drops.Remove(dropId)) return false;
            GrantExperience(definition.Drop.ExperienceValue);
            return true;
        }

        /// <summary>向玩家结算经验并将跨过的每一级逐个加入升级队列。</summary>
        /// <param name="amount">来自 TbDropProfile 或受控测试入口的正经验值。</param>
        /// <exception cref="ArgumentOutOfRangeException">经验值非正。</exception>
        /// <exception cref="InvalidOperationException">在暂停选择或结算状态尝试结算经验。</exception>
        /// <remarks>首次产生待选等级时保存当前 Playing/Boss 状态，并立刻打开一代候选面板。</remarks>
        public void GrantExperience(int amount)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (!IsSimulationActive(State)) throw new InvalidOperationException("Experience can only be granted while simulation is active.");
            Experience = checked(Experience + amount);
            while (Level - 1 < definition.ExperienceLevels.Count)
            {
                var next = definition.ExperienceLevels[Level - 1];
                if (Experience < next.RequiredExperience) break;
                Experience -= next.RequiredExperience;
                Level++;
                PendingLevelUps++;
            }
            if (PendingLevelUps > 0)
            {
                resumeAfterUpgrade = State;
                State = CombatRunState.UpgradeChoice;
                BuildUpgradeChoices();
            }
        }

        /// <summary>校验面板代次和候选身份后应用一次升级，双击或旧 UI 回调不会重复生效。</summary>
        /// <param name="expectedPanelGeneration">UI 创建时记录的 UpgradePanelGeneration。</param>
        /// <param name="optionId">当前三选一中的 TbUpgradeOption.id。</param>
        /// <returns>是否成功应用；陈旧代次、非候选或非选择状态返回 false。</returns>
        /// <remarks>修改技能、属性或生命；队列未空时立即生成下一代面板，否则恢复进入选择前的战斗状态。</remarks>
        public bool ChooseUpgrade(ulong expectedPanelGeneration, int optionId)
        {
            if (State != CombatRunState.UpgradeChoice || expectedPanelGeneration != panelGeneration) return false;
            var option = currentChoices.FirstOrDefault(item => item.Id == optionId);
            if (option == null) return false;

            int rank = GetUpgradeRank(option.Id) + 1;
            ApplyUpgrade(option, rank);
            upgradeRanks[option.Id] = rank;
            PendingLevelUps--;
            currentChoices = Array.Empty<UpgradeOptionConfig>();
            if (PendingLevelUps > 0) BuildUpgradeChoices();
            else State = resumeAfterUpgrade;
            return true;
        }

        /// <summary>按伤害系统已经确定的顺序结算玩家受伤，并只产生一次失败状态。</summary>
        /// <param name="damage">最终非负伤害。</param>
        /// <returns>本次调用是否首次触发失败结算。</returns>
        /// <exception cref="ArgumentOutOfRangeException">伤害不是有限非负数。</exception>
        /// <remarks>会修改当前生命；失败后冻结后续模拟。</remarks>
        public bool ApplyPlayerDamage(double damage)
        {
            if (double.IsNaN(damage) || double.IsInfinity(damage) || damage < 0) throw new ArgumentOutOfRangeException(nameof(damage));
            if (!IsSimulationActive(State)) return false;
            CurrentHealth = Math.Max(0, CurrentHealth - damage);
            if (CurrentHealth > 0) return false;
            Settle(ERunResultType.Defeat);
            return true;
        }

        /// <summary>从胜负结算创建下一代单局并清空所有易变状态和随机序列。</summary>
        /// <returns>是否成功重开；尚未结算时返回 false。</returns>
        /// <remarks>只复用只读 CombatRunDefinition；旧掉落 ID 和面板代次都会失效。</remarks>
        public bool Restart()
        {
            if (State != CombatRunState.VictorySettlement && State != CombatRunState.DefeatSettlement) return false;
            RunGeneration++;
            ResetRunState();
            return true;
        }

        /// <summary>根据当前时间跨过的阶段边界调度普通怪，阶段末尾会取消未发出的旧波次。</summary>
        /// <param name="targetMilli">本次 Playing 状态要推进到的绝对时刻。</param>
        /// <param name="requests">追加生成请求的输出集合。</param>
        private void ScheduleNormalSpawns(long targetMilli, ICollection<CombatSpawnRequest> requests)
        {
            while (phaseIndex < definition.SpawnPhases.Count)
            {
                var phase = definition.SpawnPhases[phaseIndex];
                long phaseLimit = Math.Min(targetMilli, phase.EndTimeMilli);
                while (nextSpawnTimeMilli <= phaseLimit && nextSpawnTimeMilli < phase.EndTimeMilli)
                {
                    spawnSequence++;
                    requests.Add(new CombatSpawnRequest(RunGeneration, spawnSequence, nextSpawnTimeMilli,
                        SelectMonster(phase, spawnSequence), false));
                    unitsInWave++;
                    if (unitsInWave == phase.UnitsPerWave)
                    {
                        unitsInWave = 0;
                        CompletedWaves++;
                        nextSpawnTimeMilli += phase.WaveIntervalMilli;
                    }
                    else nextSpawnTimeMilli += phase.UnitIntervalMilli;
                }

                if (targetMilli < phase.EndTimeMilli) return;
                phaseIndex++;
                unitsInWave = 0;
                if (phaseIndex < definition.SpawnPhases.Count)
                {
                    var next = definition.SpawnPhases[phaseIndex];
                    nextSpawnTimeMilli = (long)next.BeginTimeMilli + next.WaveIntervalMilli;
                }
            }
        }

        /// <summary>按 TbSpawnPhase.monsterWeights 对单个生成序号做确定性权重抽取。</summary>
        /// <param name="phase">当前刷新阶段。</param>
        /// <param name="sequence">本局生成序号。</param>
        /// <returns>选中的 TbMonster 引用。</returns>
        private MonsterConfig SelectMonster(SpawnPhaseConfig phase, ulong sequence)
        {
            int total = checked(phase.MonsterWeights.Sum(item => item.Weight));
            int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)definition.Rule.RandomSeed), sequence, 0,
                CombatRandomPurpose.SpawnMonster, 1, total);
            foreach (var item in phase.MonsterWeights)
            {
                sample -= item.Weight;
                if (sample <= 0) return item.MonsterId_Ref;
            }
            throw new InvalidOperationException($"TbSpawnPhase {phase.Id}: weight selection failed.");
        }

        /// <summary>过滤满阶、已解锁技能和当前不可恢复的选项，再按权重无放回生成一代候选。</summary>
        /// <exception cref="InvalidOperationException">合法候选不足 TbUpgradePool.drawCount。</exception>
        private void BuildUpgradeChoices()
        {
            var remaining = definition.UpgradeOptions.Where(IsUpgradeEligible).OrderBy(item => item.Id).ToList();
            if (remaining.Count < definition.UpgradePool.DrawCount)
                throw new InvalidOperationException($"TbUpgradePool {definition.UpgradePool.Id}: insufficient eligible options.");

            choiceSequence++;
            var selected = new List<UpgradeOptionConfig>(definition.UpgradePool.DrawCount);
            for (int index = 0; index < definition.UpgradePool.DrawCount; index++)
            {
                int total = checked(remaining.Sum(item => item.Weight));
                int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)definition.Rule.RandomSeed), choiceSequence,
                    (ulong)index, CombatRandomPurpose.UpgradeChoice, 1, total);
                int selectedIndex = 0;
                for (; selectedIndex < remaining.Count; selectedIndex++)
                {
                    sample -= remaining[selectedIndex].Weight;
                    if (sample <= 0) break;
                }
                selected.Add(remaining[selectedIndex]);
                remaining.RemoveAt(selectedIndex);
            }
            panelGeneration++;
            currentChoices = selected;
        }

        /// <summary>判断 TbUpgradeOption 在当前生命、技能和阶数下是否可进入候选。</summary>
        /// <param name="option">待检查的池内选项。</param>
        /// <returns>当前面板可以选择时返回 true。</returns>
        private bool IsUpgradeEligible(UpgradeOptionConfig option)
        {
            if (GetUpgradeRank(option.Id) >= option.MaxRank) return false;
            if (option.Type == EUpgradeOptionType.SkillUnlock)
                return option.TargetSkillId_Ref != null && !activeSkills.ContainsKey(option.TargetSkillId_Ref.Id);
            if (option.Type == EUpgradeOptionType.Heal) return CurrentHealth < MaxHealth;
            return option.Type == EUpgradeOptionType.Attribute;
        }

        /// <summary>按配置类型应用单次技能解锁、属性来源或立即恢复。</summary>
        /// <param name="option">当前面板已验证的选项。</param>
        /// <param name="rank">应用后的新阶数。</param>
        /// <remarks>属性修改读取 TbUpgradeOption.modifiers，并按 Add、AddPercent、Multiply 进入聚合器固定乘区。</remarks>
        private void ApplyUpgrade(UpgradeOptionConfig option, int rank)
        {
            if (option.Type == EUpgradeOptionType.SkillUnlock)
            {
                activeSkills.Add(option.TargetSkillId_Ref.Id, option.TargetSkillId_Ref);
                return;
            }
            if (option.Type == EUpgradeOptionType.Heal)
            {
                CurrentHealth = Math.Min(MaxHealth, CurrentHealth + option.HealMilli.Value / 1000d);
                return;
            }

            double previousMax = MaxHealth;
            var modifiers = option.Modifiers.GroupBy(item => item.AttributeId_Ref.AttributeType)
                .Select(CreateAttributeModifier).ToArray();
            ulong source = (unchecked((ulong)(uint)option.Id) << 32) | unchecked((uint)rank);
            attributes.Replace(source, modifiers);
            if (MaxHealth < previousMax) CurrentHealth = Math.Min(CurrentHealth, MaxHealth);
        }

        /// <summary>将同一属性的 RunAttributeModifier 列表折叠为 CombatAttributes 的三个固定结算区。</summary>
        /// <param name="group">同一 TbAttribute.attributeType 的配置修改。</param>
        /// <returns>按 Add、AddPercent、Multiply 表达的聚合修改。</returns>
        private static AttributeModifier CreateAttributeModifier(IGrouping<EAttributeType, RunAttributeModifier> group)
        {
            var result = new AttributeModifier { Attribute = group.Key };
            double multiplier = 1;
            bool hasMultiplier = false;
            foreach (var item in group)
            {
                switch (item.Operation)
                {
                    case EAttributeModifyOperation.Add:
                        result.Flat += item.ValueMilli / 1000d;
                        break;
                    case EAttributeModifyOperation.AddPercent:
                        result.PercentBp = checked(result.PercentBp + checked((int)(item.ValueMilli * 10)));
                        break;
                    case EAttributeModifyOperation.Multiply:
                        multiplier *= item.ValueMilli / 1000d;
                        hasMultiplier = true;
                        break;
                }
            }
            result.Multiplier = hasMultiplier ? multiplier : 0;
            return result;
        }

        /// <summary>读取当前升级阶数；未选择过的选项为零阶。</summary>
        /// <param name="optionId">TbUpgradeOption.id。</param>
        /// <returns>本代次已经应用的次数。</returns>
        private int GetUpgradeRank(int optionId) => upgradeRanks.TryGetValue(optionId, out int rank) ? rank : 0;

        /// <summary>写入唯一结算结果并切换到对应冻结状态。</summary>
        /// <param name="result">TbRunResultType 定义的胜利或失败。</param>
        /// <remarks>清空当前升级面板和待升级队列，避免结算后的陈旧 UI 继续修改本局。</remarks>
        private void Settle(ERunResultType result)
        {
            if (Result.HasValue) return;
            Result = result;
            State = result == ERunResultType.Victory ? CombatRunState.VictorySettlement : CombatRunState.DefeatSettlement;
            PendingLevelUps = 0;
            currentChoices = Array.Empty<UpgradeOptionConfig>();
        }

        /// <summary>为构造或重开恢复表内初始角色、技能、波次和全部统计。</summary>
        /// <remarks>重建 CombatAttributes 并重置随机调用序列；panelGeneration 保持递增以拒绝旧 UI 回调。</remarks>
        private void ResetRunState()
        {
            State = CombatRunState.Playing;
            ElapsedMilli = 0;
            Level = 1;
            Experience = 0;
            PendingLevelUps = 0;
            Kills = 0;
            CompletedWaves = 0;
            BossSpawned = false;
            Result = null;
            phaseIndex = 0;
            unitsInWave = 0;
            spawnSequence = 0;
            dropSequence = 0;
            choiceSequence = 0;
            upgradeRanks.Clear();
            activeSkills.Clear();
            recordedDeaths.Clear();
            drops.Clear();
            currentChoices = Array.Empty<UpgradeOptionConfig>();
            attributes = new CombatAttributes(definition.Character.AttributeProfileId_Ref);
            CurrentHealth = MaxHealth;
            foreach (var skill in definition.InitialSkills) activeSkills.Add(skill.Id, skill);
            var first = definition.SpawnPhases[0];
            nextSpawnTimeMilli = (long)first.BeginTimeMilli + first.WaveIntervalMilli;
        }

        /// <summary>判断当前状态是否允许固定步进、伤害、掉落和经验结算。</summary>
        /// <param name="state">当前单局状态。</param>
        /// <returns>Playing 或 Boss 返回 true。</returns>
        private static bool IsSimulationActive(CombatRunState state) =>
            state == CombatRunState.Playing || state == CombatRunState.Boss;

        /// <summary>把单局代次写入高位，确保重开后的掉落 ID 不接受上一局异步回调。</summary>
        /// <param name="sequence">本代次内的非零掉落序号。</param>
        /// <returns>包含 RunGeneration 的唯一句柄。</returns>
        private ulong ComposeRunHandle(ulong sequence) => unchecked((RunGeneration << 32) | (sequence & uint.MaxValue));
    }
}
