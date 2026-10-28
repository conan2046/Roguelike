using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    public sealed partial class CombatRunDefinition
    {
        public DraftPolicyConfig DraftPolicy { get; private set; }
        public IReadOnlyDictionary<int, UpgradeRuleConfig> DraftRules { get; private set; }
        public IReadOnlyDictionary<int, MechanismCardConfig> MechanismCards { get; private set; }

        /// <summary>聚合关卡可选的抽选与内容目标协议；在验证升级载荷之前调用，不改变原武器或炼器配置。</summary>
        /// <param name="tables">统一 Luban 容器；数据来自 TbDraftPolicy、TbUpgradeRule、TbMechanismCard 和 TbContentGoal。</param>
        /// <exception cref="InvalidOperationException">补充表引用、次数、倍率、目标或修饰载荷非法。</exception>
        private void InitializeDraft(Tables tables)
        {
            DraftPolicy = tables.TbDraftPolicy.DataList.SingleOrDefault(p => p.StageId == Stage.Id);
            DraftRules = tables.TbUpgradeRule.DataList.ToDictionary(r => r.OptionId);
            MechanismCards = tables.TbMechanismCard.DataList.ToDictionary(c => c.OptionId);
            foreach (var rule in DraftRules.Values)
            {
                Require(rule.OptionId_Ref != null && rule.CharacterIds_Ref.All(c => c != null) &&
                    rule.StageIds_Ref.All(s => s != null) && rule.RequiredOptionIds_Ref.All(o => o != null) &&
                    rule.ExcludedOptionIds_Ref.All(o => o != null), "TbUpgradeRule: unresolved eligibility reference.");
                Require(!rule.RequiredOptionIds.Intersect(rule.ExcludedOptionIds).Any(), "TbUpgradeRule: contradictory requirements.");
            }
            foreach (var card in MechanismCards.Values)
                Require(card.OptionId_Ref?.Type == EUpgradeOptionType.MechanismModifier && card.OptionId_Ref.MaxRank == 1 &&
                    card.RequiredContentId_Ref?.ModifierId == card.Id && card.CounterRangePermille > 0 && card.MoveSpeedPermille > 0 &&
                    (card.SignatureKind == ETribulationSignature.Guard || card.DurationMilli > 0), "TbMechanismCard: invalid single-level modifier.");
            if (DraftPolicy == null) return;
            var policy = DraftPolicy; var goal = policy.GoalId_Ref; var ui = policy.UiId_Ref;
            Require(policy.StageId_Ref == Stage && policy.RerollCount >= 0 && policy.SkipCount >= 0 &&
                policy.AssociationPermille > 0 && policy.ReliefMissCount > 0 && policy.ReliefPermille > 0 &&
                policy.WeightCapPermille >= policy.AssociationPermille && policy.WeightCapPermille >= policy.ReliefPermille,
                "TbDraftPolicy: invalid budget or weight policy.");
            Require(goal?.CharacterId == Character.Id && goal.StageIds.Contains(Stage.Id) && goal.StageIds_Ref.All(s => s != null) &&
                goal.RequiredEvents > 0 && goal.MinimumTargets > 0 && goal.ModifierId_Ref != null,
                "TbContentGoal: unavailable or invalid goal for this stage.");
            Require(ui?.PrefabResourceId_Ref?.Type == EResourceType.Prefab && ui.RefreshIntervalMilli > 0 &&
                ui.FormedNoticeMilli > 0 && ui.PrimarySourceCount > 0 &&
                ui.ProfileSchemaVersion > 0 && !string.IsNullOrWhiteSpace(ui.ProfileFileName) &&
                System.IO.Path.GetFileName(ui.ProfileFileName) == ui.ProfileFileName, "TbBuildFeedbackUi: invalid resource or storage protocol.");
        }
    }
}
