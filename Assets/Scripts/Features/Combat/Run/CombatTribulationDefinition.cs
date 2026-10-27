using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>正式关卡的可选借劫切片定义；所有规则引用来自Luban，未配置的旧关卡保持原路径。</summary>
    public sealed partial class CombatRunDefinition
    {
        /// <summary>当前关卡的切片配置；普通关卡为空。</summary>
        public TribulationSliceConfig Tribulation { get; private set; }
        /// <summary>试炼、职业与所有炼器候选所需的正式技能预加载闭包。</summary>
        public IReadOnlyList<SkillConfig> TribulationSkills { get; private set; } = Array.Empty<SkillConfig>();

        /// <summary>基础关卡聚合完成后验证TbTribulationSlice及其规则、职业、炼器与UI强引用。</summary>
        /// <remarks>只保存源表引用；不创建Unity对象，不为缺失配置提供默认规则。</remarks>
        /// <exception cref="InvalidOperationException">切片引用、几何、时序或候选不合法。</exception>
        private void InitializeTribulation()
        {
            Tribulation = Stage.TribulationSliceId_Ref;
            Require(!Stage.TribulationSliceId.HasValue || Tribulation != null, $"TbStage {Stage.Id}: unresolved tribulation slice.");
            if (Tribulation == null) return;
            var slice = Tribulation;
            var rule = slice.RuleId_Ref;
            var signature = slice.SignatureId_Ref;
            var ui = slice.UiId_Ref;
            Require(slice.StageId_Ref == Stage && rule != null && signature != null && ui != null &&
                BuildConfig != null && slice.WeaponId_Ref != null && BuildConfig.InitialWeaponIds.Contains(slice.WeaponId),
                $"TbTribulationSlice {slice.Id}: unresolved references or initial weapon mismatch.");
            Require(!string.IsNullOrWhiteSpace(slice.RoleName) && !string.IsNullOrWhiteSpace(slice.RoleDescription),
                $"TbTribulationSlice {slice.Id}: missing role text.");
            Require(rule.Altars.Count > 0 && rule.Altars.All(a => a != null && a.Id > 0 && a.AvailableAfterMilli >= 0 &&
                float.IsFinite(a.XPixels) && float.IsFinite(a.YPixels) && Math.Abs(a.XPixels) <= Map.ArenaHalfWidthPixels && Math.Abs(a.YPixels) <= Map.ArenaHalfHeightPixels) &&
                rule.Altars.Select(a => a.Id).Distinct().Count() == rule.Altars.Count,
                $"TbTribulationRule {rule.Id}: invalid altar positions or identities.");
            Require(rule.ActivationRadiusPixels > 0 && rule.TrialDurationMilli > 0 && rule.RequiredEnemyHits > 0 &&
                rule.StrikeIntervalMilli > 0 && rule.WarningMilli > 0 && rule.WarningMilli <= rule.TrialDurationMilli && rule.StrikeRadiusPixels > 0 &&
                rule.AttackProfileId_Ref != null && rule.EffectSkillId_Ref != null && rule.PlayerDamagePermille > 0 &&
                rule.EnemyDamagePermille > 0 && rule.BossDamagePermille > 0 && rule.SuccessHealPermille >= 0 && rule.SuccessHealPermille <= 1000,
                $"TbTribulationRule {rule.Id}: invalid trial damage, geometry or timing.");
            Require(rule.BossPhases.Count > 0 && rule.BossPhases[0].HealthPermille == 1000 && rule.BossSpreadPixels >= 0 &&
                rule.AltarRadiusPixels > 0 && rule.LineWidthPixels > 0,
                $"TbTribulationRule {rule.Id}: invalid boss or marker geometry.");
            int previousThreshold = int.MaxValue;
            foreach (var phase in rule.BossPhases)
            {
                Require(phase.HealthPermille > 0 && phase.HealthPermille < previousThreshold && phase.IntervalMilli > 0 &&
                    phase.WarningMilli > 0 && phase.StrikeCount > 0 && phase.RadiusPixels > 0,
                    $"TbTribulationRule {rule.Id}: boss phases must descend with valid timed strikes.");
                previousThreshold = phase.HealthPermille;
            }
            Require(Enum.IsDefined(typeof(ETribulationSignature), signature.Kind), $"TbTribulationSignature {signature.Id}: invalid kind.");
            if (signature.Kind == ETribulationSignature.Guard)
                Require(signature.GuardChargeHits > 0 && signature.GuardDurationMilli > 0 && signature.GuardDamageTakenPermille > 0 &&
                    signature.GuardDamageTakenPermille <= 1000 && signature.CounterSkillId_Ref != null && signature.CounterRadiusPixels > 0 && signature.CounterDamagePermille > 0,
                    $"TbTribulationSignature {signature.Id}: invalid guard rules.");
            else
                Require(signature.DomainStepPixels > 0 && signature.DomainDurationMilli > 0 && signature.DomainPulseMilli > 0 && signature.DomainRadiusPixels > 0 &&
                    signature.DomainDamagePermille > 0 && signature.OverlapCount > 1 && signature.OverlapCount <= signature.MaxDomains &&
                    signature.OverlapRadiusPixels > 0 && signature.OverlapDamagePermille > 0 && signature.DomainSkillId_Ref != null,
                    $"TbTribulationSignature {signature.Id}: invalid domain rules.");
            Require(slice.RefinementIds_Ref.Count > 0 && slice.RefinementIds_Ref.All(r => r != null && r.WeaponId == slice.WeaponId &&
                r.ResultSkillId_Ref != null && r.DamagePermille > 0 && r.IntervalPermille > 0 && r.RangePermille > 0 &&
                !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Description)) &&
                slice.RefinementIds.Distinct().Count() == slice.RefinementIds.Count,
                $"TbTribulationSlice {slice.Id}: invalid refinement candidates.");
            Require(ui.StageIds.Contains(Stage.Id) && ui.LobbyPrefabResourceId_Ref != null && ui.HudPrefabResourceId_Ref != null &&
                ui.LobbyPrefabResourceId_Ref.Type == EResourceType.Prefab && ui.HudPrefabResourceId_Ref.Type == EResourceType.Prefab &&
                !string.IsNullOrWhiteSpace(ui.Title) && !string.IsNullOrWhiteSpace(ui.Intro),
                $"TbTribulationUi {ui.Id}: unresolved entry or UI resources.");
            TribulationSkills = slice.RefinementIds_Ref.Select(r => r.ResultSkillId_Ref)
                .Concat(new[] { rule.EffectSkillId_Ref, signature.CounterSkillId_Ref, signature.DomainSkillId_Ref })
                .Where(s => s != null).GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.Id).ToArray();
        }
    }
}
