using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Runtime
{
    /// <summary>场上未拾取宝箱的位置；奖励和幂等领取由纯逻辑模型持有。</summary>
    public sealed class CombatRuntimeChest
    {
        public ulong Id { get; internal set; }
        public float2 Position { get; internal set; }
        public ChestProfileConfig Profile { get; internal set; }
    }

    public sealed partial class CombatRunCoordinator
    {
        private readonly SortedDictionary<ulong, CombatRuntimeChest> runtimeChests = new SortedDictionary<ulong, CombatRuntimeChest>();
        public IReadOnlyDictionary<ulong, CombatRuntimeChest> Chests => runtimeChests;

        /// <summary>本次模型 Advance 后只消费本次到期地图箱，按 TbRunEvent 极坐标和地图边界确定可达位置。</summary>
        /// <remarks>精英通过普通生成队列落地，死亡箱由首次死亡处理创建；不从显示提示反推或重复生成。</remarks>
        private void ConsumeRunEvents()
        {
            float scale = model.Definition.CombatRules.WorldUnitsPerPixel;
            float2 center = session.ReadUnit(0).Position;
            foreach (var request in model.FiredEvents)
            {
                if (request.ChestId == 0 || runtimeChests.ContainsKey(request.ChestId)) continue;
                RunEventConfig item = request.Config;
                float angle = math.radians(item.ChestAngleDegrees.Value);
                float2 position = center + new float2(math.cos(angle), math.sin(angle)) * item.SpawnDistancePixels * scale;
                float margin = item.ChestProfileId_Ref.PickupRadiusPixels * scale;
                float2 boundary = new float2(model.Definition.Map.ArenaHalfWidthPixels, model.Definition.Map.ArenaHalfHeightPixels) * scale - margin;
                position = math.clamp(position, -boundary, boundary);
                runtimeChests.Add(request.ChestId, new CombatRuntimeChest { Id = request.ChestId, Position = position, Profile = item.ChestProfileId_Ref });
            }
        }

        /// <summary>经验处理之后检查同 tick 的全部近身宝箱，升级优先，其他箱按句柄稳定排队。</summary>
        /// <remarks>首次拾取后移除场上位置；模型仍保留待领取奖励，冻结及重复回调不会再次拾取。</remarks>
        private void CollectNearbyChests()
        {
            if (model.Result.HasValue) { runtimeChests.Clear(); return; }
            float2 player = session.ReadUnit(0).Position;
            foreach (ulong id in runtimeChests.Keys.ToArray())
            {
                CombatRuntimeChest chest = runtimeChests[id];
                float radius = chest.Profile.PickupRadiusPixels * model.Definition.CombatRules.WorldUnitsPerPixel;
                if (math.distance(player, chest.Position) <= radius && model.TryCollectChest(id)) runtimeChests.Remove(id);
            }
        }

        /// <summary>宝箱按钮提交真实面板代次后领取，并同步属性、生命、原形/超武激活与局部修正。</summary>
        /// <param name="panelGeneration">创建宝箱面板时的代次。</param>
        /// <param name="optionId">普通成长候选；进化或空奖励为零。</param>
        /// <returns>成功领取并完成 ECS 同步时为真。</returns>
        /// <exception cref="InvalidOperationException">领取期间玩家生命代次发生不合法改变。</exception>
        /// <remarks>保持进化冷却比例，已出生攻击仍用旧快照；下一个宝箱或TbTribulationRule试炼待领奖继续冻结战斗。</remarks>
        public bool ClaimChest(ulong panelGeneration, int optionId)
        {
            if (!model.ClaimChest(panelGeneration, optionId)) return false;
            var player = session.ReadUnit(0);
            if (!session.SynchronizeRunPlayer(player.Target.Lifetime, model.AttributeSnapshot, model.CurrentHealth))
                throw new InvalidOperationException("Formal player lifecycle changed while claiming chest.");
            session.SynchronizePlayerSkills(model.ActiveSkills.Keys, model.Build?.CaptureWeapons());
            if (model.Build != null) session.ApplyWeaponUpgrades(model.Build.CaptureWeapons(), model.AttributeSnapshot);
            session.Paused = Paused || !IsSimulationActive(model.State) || TribulationBlocksSimulation;
            return true;
        }
    }
}
