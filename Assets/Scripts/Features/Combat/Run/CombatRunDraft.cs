using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>资格排除的技术原因，调试时区分不可用与低权重，不作为策划默认配置。</summary>
    public enum DraftExclusion { None, WrongCharacter, WrongStage, MissingPrerequisite, Exclusive, Locked, AlreadySelected, Unsupported, RankOrSlot }

    /// <summary>一次普通选卡原子决策的只读证据；随机位置与资源余额一起记录。</summary>
    public sealed class CombatDraftDecision
    {
        public string Operation { get; internal set; }
        public ulong OfferId { get; internal set; }
        public ulong RandomPosition { get; internal set; }
        public int[] Candidates { get; internal set; }
        public int LegalCount { get; internal set; }
        public int Rerolls { get; internal set; }
        public int Skips { get; internal set; }
        public int Misses { get; internal set; }
        public int? SelectedOptionId { get; internal set; }
        public long SimulationMilli { get; internal set; }
    }

    public sealed partial class CombatRunModel
    {
        private readonly HashSet<int> enabledContents;
        private readonly HashSet<int> selectedMechanisms = new HashSet<int>();
        private readonly List<CombatDraftDecision> draftDecisions = new List<CombatDraftDecision>();
        public string RunId { get; private set; }
        public int RerollsRemaining { get; private set; }
        public int SkipsRemaining { get; private set; }
        public int MissedSynergyOffers { get; private set; }
        public int LastReplacementCount { get; private set; }
        public bool BuildFormed { get; private set; }
        public long? FirstModifierMilli { get; private set; }
        public long? FirstBenefitMilli { get; private set; }
        public bool ReliefActive { get; private set; }
        public IReadOnlyList<CombatDraftDecision> DraftDecisions => draftDecisions;
        public ulong DraftRandomPosition => choiceSequence;
        public bool CanReroll => definition.DraftPolicy != null && State == CombatRunState.UpgradeChoice &&
            RerollsRemaining > 0 && ResolveDraftPool(out var legal) != null && legal.Any(e => currentChoices.All(c => c.Id != e.OptionId));

        /// <summary>开局按 TbDraftPolicy 分配本局次数，清除旧局修饰和观测；不读取或写入真实存档。</summary>
        /// <remarks>创建唯一 RunId；已启用且已解锁的内容快照由组合根传入，单局内不改变。</remarks>
        private void ResetDraft()
        {
            RunId = Guid.NewGuid().ToString("N");
            selectedMechanisms.Clear(); draftDecisions.Clear();
            RerollsRemaining = definition.DraftPolicy?.RerollCount ?? 0;
            SkipsRemaining = definition.DraftPolicy?.SkipCount ?? 0;
            MissedSynergyOffers = 0; LastReplacementCount = 0; BuildFormed = false; ReliefActive = false;
            FirstModifierMilli = null; FirstBenefitMilli = null;
        }

        /// <summary>候选过滤先读取 TbUpgradeRule 的角色、模式、前置、互斥与 TbMechanismCard 内容锁。</summary>
        /// <param name="option">当前池引用的选项。</param><param name="reason">不合格时输出稳定调试原因。</param>
        /// <returns>通过补充资格协议时为真；槽位和等级仍由原有消费者验证。</returns>
        private bool IsDraftEligible(UpgradeOptionConfig option, out DraftExclusion reason)
        {
            reason = DraftExclusion.None;
            if (definition.DraftRules.TryGetValue(option.Id, out var rule))
            {
                if (rule.CharacterIds.Count > 0 && !rule.CharacterIds.Contains(definition.Character.Id)) reason = DraftExclusion.WrongCharacter;
                else if (rule.StageIds.Count > 0 && !rule.StageIds.Contains(definition.Stage.Id)) reason = DraftExclusion.WrongStage;
                else if (rule.RequiredOptionIds.Any(id => GetUpgradeRank(id) == 0)) reason = DraftExclusion.MissingPrerequisite;
                else if (rule.ExcludedOptionIds.Any(id => GetUpgradeRank(id) > 0)) reason = DraftExclusion.Exclusive;
            }
            if (reason != DraftExclusion.None) return false;
            if (option.Type != EUpgradeOptionType.MechanismModifier) return true;
            if (definition.DraftPolicy == null || !definition.MechanismCards.TryGetValue(option.Id, out var card)) reason = DraftExclusion.Unsupported;
            else if (!enabledContents.Contains(card.RequiredContentId)) reason = DraftExclusion.Locked;
            else if (selectedMechanisms.Contains(card.Id)) reason = DraftExclusion.AlreadySelected;
            return reason == DraftExclusion.None;
        }

        /// <summary>调试候选时给出资格或原有等级／槽位排除原因，不改变随机流。</summary>
        /// <param name="option">TbUpgradeOption 引用。</param><returns>None 表示当前确实可以抽取。</returns>
        public DraftExclusion ExplainUpgradeExclusion(UpgradeOptionConfig option)
        {
            if (option == null) return DraftExclusion.Unsupported;
            if (!IsDraftEligible(option, out var reason)) return reason;
            return IsUpgradeEligible(option) ? DraftExclusion.None : DraftExclusion.RankOrSlot;
        }

        /// <summary>从 TbDraftPolicy 与已持有能力的明确标签判定协同，多标签只命中一次。</summary>
        /// <param name="optionId">待计算权重的选项。</param><returns>当前构筑与该选项存在表内关系时为真。</returns>
        private bool IsSynergy(int optionId)
        {
            if (!definition.DraftRules.TryGetValue(optionId, out var rule)) return false;
            var tags = new HashSet<string>(definition.DraftPolicy.SynergyTags, StringComparer.Ordinal);
            foreach (int id in upgradeRanks.Where(p => p.Value > 0).Select(p => p.Key))
                if (definition.DraftRules.TryGetValue(id, out var held)) tags.UnionWith(held.SynergyTags);
            return rule.SynergyTags.Any(tags.Contains);
        }

        /// <summary>按现有耗尽策略解析合法普通升级池；不推进随机、救济或面板代次。</summary>
        /// <param name="legal">按 optionId 稳定排序的合格候选。</param><returns>可以生成候选的池；耗尽时为空。</returns>
        /// <exception cref="InvalidOperationException">后备链成环。</exception>
        private UpgradePoolConfig ResolveDraftPool(out List<UpgradePoolEntry> legal)
        {
            var visited = new HashSet<int>(); var pool = definition.UpgradePool;
            while (pool != null)
            {
                if (!visited.Add(pool.Id)) throw new InvalidOperationException("Draft fallback cycle.");
                legal = pool.Entries.Where(e => IsUpgradeEligible(e.OptionId_Ref)).OrderBy(e => e.OptionId).ToList();
                var action = UpgradeOfferPolicy.Resolve(pool.ExhaustionPolicy, pool.DrawCount, legal.Count, pool.FallbackPoolId_Ref != null);
                if (action == UpgradeOfferAction.UseFallbackPool) { pool = pool.FallbackPoolId_Ref; continue; }
                if (action != UpgradeOfferAction.EndSelection) return pool;
                break;
            }
            legal = new List<UpgradePoolEntry>(); return null;
        }

        /// <summary>按 TbDraftPolicy 对合格池加协同／软救济倍率，使用独立 UpgradeChoice 流无放回抽选。</summary>
        /// <param name="remaining">本次允许抽取的合格候选副本。</param><param name="count">实际展示数量。</param>
        /// <param name="position">待提交的抽选流位置。</param><param name="offset">同组继续抽样的索引起点。</param>
        /// <returns>不重复的正式配置引用。</returns>
        /// <exception cref="OverflowException">配置权重超出整数抽样协议。</exception>
        private List<UpgradeOptionConfig> DrawDraft(List<UpgradePoolEntry> remaining, int count, ulong position, int offset = 0)
        {
            var selected = new List<UpgradeOptionConfig>(); var policy = definition.DraftPolicy;
            for (int i = 0; i < count; i++)
            {
                var weights = remaining.Select(e =>
                {
                    long factor = 1000;
                    if (IsSynergy(e.OptionId))
                    {
                        factor = policy.AssociationPermille;
                        if (!BuildFormed && MissedSynergyOffers >= policy.ReliefMissCount)
                            factor = factor * policy.ReliefPermille / 1000;
                        factor = Math.Min(factor, policy.WeightCapPermille);
                    }
                    return checked((int)Math.Max(1, e.Weight * factor / 1000));
                }).ToArray();
                int sample = CombatRandom.Inclusive(unchecked((ulong)(uint)definition.Rule.RandomSeed), position,
                    (ulong)(i + offset), CombatRandomPurpose.UpgradeChoice, 1, checked(weights.Sum()));
                int index = 0; for (; index < weights.Length; index++) { sample -= weights[index]; if (sample <= 0) break; }
                selected.Add(remaining[index].OptionId_Ref); remaining.RemoveAt(index);
            }
            return selected;
        }

        /// <summary>正常升级创建一代候选并更新救济；只有展示到协同项才清零，重掷不增加未出现计数。</summary>
        /// <returns>成功展示非空候选时为真；零候选免费继续。</returns>
        private bool TryBuildDraftChoices()
        {
            var pool = ResolveDraftPool(out var legal);
            ReliefActive = !BuildFormed && MissedSynergyOffers >= definition.DraftPolicy.ReliefMissCount && legal.Any(e => IsSynergy(e.OptionId));
            if (pool == null) return false;
            int legalCount = legal.Count; ulong next = checked(choiceSequence + 1);
            var selected = DrawDraft(legal, Math.Min(pool.DrawCount, legal.Count), next);
            currentChoices = selected; choiceSequence = next; panelGeneration++; LastReplacementCount = 0;
            if (selected.Any(o => IsSynergy(o.Id))) MissedSynergyOffers = 0;
            else if (!BuildFormed && legalCount > 0) MissedSynergyOffers++;
            RecordDraftDecision("offer", legalCount); return true;
        }

        /// <summary>普通升级整组重掷，优先新候选；所有计算成功后才一起提交次数、候选与代次。</summary>
        /// <param name="offerId">点击时的 UpgradePanelGeneration。</param><returns>至少替换一个候选并成功提交时为真。</returns>
        public bool RerollUpgrade(ulong offerId)
        {
            if (offerId != panelGeneration || !CanReroll) return false;
            var pool = ResolveDraftPool(out var legal); int legalCount = legal.Count;
            var fresh = legal.Where(e => currentChoices.All(c => c.Id != e.OptionId)).ToList();
            int count = Math.Min(pool.DrawCount, legal.Count); int replaced = Math.Min(count, fresh.Count);
            if (replaced == 0) return false;
            ulong next = checked(choiceSequence + 1); var selected = DrawDraft(fresh, replaced, next);
            var retained = legal.Where(e => currentChoices.Any(c => c.Id == e.OptionId)).ToList();
            if (selected.Count < count) selected.AddRange(DrawDraft(retained, count - selected.Count, next, selected.Count));
            currentChoices = selected; choiceSequence = next; panelGeneration++; RerollsRemaining--; LastReplacementCount = replaced;
            if (selected.Any(o => IsSynergy(o.Id))) MissedSynergyOffers = 0;
            ReliefActive = !BuildFormed && MissedSynergyOffers >= definition.DraftPolicy.ReliefMissCount && legal.Any(e => IsSynergy(e.OptionId));
            RecordDraftDecision("reroll", legalCount); return true;
        }

        /// <summary>消耗一次普通升级跳过，放弃能力但保留已获得等级；旧回调不能再次消费。</summary>
        /// <param name="offerId">点击时的面板代次。</param><returns>成功提交本次跳过时为真。</returns>
        public bool SkipUpgrade(ulong offerId)
        {
            if (definition.DraftPolicy == null || State != CombatRunState.UpgradeChoice || offerId != panelGeneration || SkipsRemaining <= 0) return false;
            SkipsRemaining--; PendingLevelUps--; panelGeneration++; RecordDraftDecision("skip", 0);
            currentChoices = Array.Empty<UpgradeOptionConfig>();
            if (PendingLevelUps > 0) ContinueUpgradeQueue(); else { State = resumeAfterUpgrade; OpenNextChest(); }
            return true;
        }

        /// <summary>只在取得修饰且出现有效玩法收益时标记一次构筑成形，不发额外奖励。</summary>
        /// <param name="simulationMilli">协调器已经完成的实际战斗时钟。</param>
        public void MarkMechanismBenefit(long simulationMilli)
        {
            if (selectedMechanisms.Count == 0) return;
            if (!BuildFormed) FirstBenefitMilli = simulationMilli;
            BuildFormed = true; ReliefActive = false;
        }

        /// <summary>协调器查询本局选中的职业修饰；未选时为空，不提供第二套效果参数。</summary>
        /// <param name="kind">当前 TbTribulationSignature.kind。</param><returns>已取得的 TbMechanismCard。</returns>
        public MechanismCardConfig SelectedMechanism(ETribulationSignature kind) =>
            definition.MechanismCards.Values.FirstOrDefault(c => c.SignatureKind == kind && selectedMechanisms.Contains(c.Id));

        /// <summary>每次普通面板决策记录原子提交后的状态，供回归和调试读取。</summary>
        /// <param name="operation">技术操作名称。</param><param name="legalCount">生成时合格池规模。</param>
        /// <param name="selectedOptionId">选择提交时的正式选项ID；展示、重掷和跳过没有选中项。</param>
        private void RecordDraftDecision(string operation, int legalCount, int? selectedOptionId = null)
        {
            draftDecisions.Add(new CombatDraftDecision { Operation = operation, OfferId = panelGeneration,
                RandomPosition = choiceSequence, Candidates = currentChoices.Select(o => o.Id).ToArray(),
                LegalCount = legalCount, Rerolls = RerollsRemaining, Skips = SkipsRemaining, Misses = MissedSynergyOffers,
                SelectedOptionId = selectedOptionId, SimulationMilli = ElapsedMilli });
        }
    }
}
