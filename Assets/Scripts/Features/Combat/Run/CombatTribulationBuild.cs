using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>本命炼器属于本局构筑，同槽保持武器身份及星级；不修改正式配置对象。</summary>
    public sealed partial class CombatBuildState
    {
        private readonly Dictionary<int, TribulationRefinementConfig> refinements = new Dictionary<int, TribulationRefinementConfig>();

        /// <summary>导出技能或显示名称前查询该武器在本代次已经领取的炼器道途。</summary>
        /// <param name="weaponId">TbRunWeapon.id。</param><returns>已领取的TbTribulationRefinement；尚未炼器时为空。</returns>
        internal TribulationRefinementConfig ReadRefinement(int weaponId) => refinements.TryGetValue(weaponId, out var value) ? value : null;

        /// <summary>试炼模型确认真实奖励后，为已持有武器替换道途；保持原占槽与星级。</summary>
        /// <param name="refinement">本职业合法的TbTribulationRefinement行。</param><returns>首次选择或实际转向另一分支时为真。</returns>
        /// <remarks>只修改本代次字典并递增HUD修订号；协调器随后同步技能与冷却。不会增加第二个同名武器槽。</remarks>
        internal bool TryRefine(TribulationRefinementConfig refinement)
        {
            if (refinement == null || !weaponRanks.ContainsKey(refinement.WeaponId) || !Config.WeaponIds.Contains(refinement.WeaponId) ||
                ReadRefinement(refinement.WeaponId)?.Id == refinement.Id) return false;
            refinements[refinement.WeaponId] = refinement; Revision++;
            return true;
        }
    }

    /// <summary>把真实试炼奖励接入已有技能与生命消费者，不另建切片默认属性。</summary>
    public sealed partial class CombatRunModel
    {
        /// <summary>协调器验证试炼与面板代次后应用本职业炼器，并按TbTribulationRule成功奖励恢复生命。</summary>
        /// <param name="refinement">刚领取的正式炼器配置。</param><returns>构筑实际变化并完成模型同步时为真。</returns>
        /// <remarks>修改本局本命技能和生命；TbTribulationRule.successHealPermille的恢复量向下取整，保持正式会话整数生命约束。保持星级，旧投递仍使用出生快照；ECS同步由协调器紧接着执行。</remarks>
        internal bool ApplyTribulationRefinement(TribulationRefinementConfig refinement)
        {
            var slice = definition.Tribulation;
            if (slice == null || !IsSimulationActive(State) || refinement == null || !slice.RefinementIds.Contains(refinement.Id) || Build == null || !Build.TryRefine(refinement)) return false;
            SynchronizeBuildSkills();
            CurrentHealth = System.Math.Min(MaxHealth, CurrentHealth + System.Math.Floor(MaxHealth * slice.RuleId_Ref.SuccessHealPermille / 1000d));
            return true;
        }
    }
}
