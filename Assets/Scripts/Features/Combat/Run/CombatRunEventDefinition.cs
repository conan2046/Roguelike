using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    public sealed partial class CombatRunDefinition
    {
        public IReadOnlyList<RunEventConfig> Events { get; private set; }
        public IReadOnlyList<ChestProfileConfig> ChestProfiles { get; private set; }

        /// <summary>聚合 TbStageRule.runEventIds 后验证时间顺序、互斥载荷、精英身份与宝箱领取资源。</summary>
        /// <exception cref="InvalidOperationException">事件、宝箱或精英配置不满足本局运行边界。</exception>
        private void ValidateEvents()
        {
            Require(Events.All(item => item != null) && Events.Select(item => item.Id).Distinct().Count() == Events.Count,
                "TbStageRule.runEventIds: missing or duplicate event.");
            Require(Events.SequenceEqual(Events.OrderBy(item => item.TimeMilli).ThenBy(item => item.Id)),
                "TbStageRule.runEventIds: events must be ordered by time and ID.");
            foreach (RunEventConfig item in Events)
            {
                Require(item.TimeMilli > 0 && item.TimeMilli <= Rule.BossTimeMilli && item.SpawnDistancePixels > 0 &&
                        !float.IsInfinity(item.SpawnDistancePixels) && item.NoticeDurationMilli > 0 && !string.IsNullOrWhiteSpace(item.Notice),
                    $"TbRunEvent {item.Id}: invalid timeline, distance or notice.");
                if (item.Kind == ERunEventKind.EliteSpawn)
                    Require(item.MonsterId_Ref != null && item.MonsterId_Ref.IsElite && item.MonsterId_Ref.DeathChestProfileId_Ref != null &&
                            item.MonsterId != Boss.MonsterId && !item.ChestProfileId.HasValue && !item.ChestAngleDegrees.HasValue,
                        $"TbRunEvent {item.Id}: invalid elite payload.");
                else
                    Require(item.Kind == ERunEventKind.MapChest && item.ChestProfileId_Ref != null && !item.MonsterId.HasValue &&
                            item.ChestAngleDegrees.HasValue && !float.IsNaN(item.ChestAngleDegrees.Value) && !float.IsInfinity(item.ChestAngleDegrees.Value),
                        $"TbRunEvent {item.Id}: invalid map chest payload.");
            }
            foreach (ChestProfileConfig profile in ChestProfiles)
            {
                Require(profile.PickupRadiusPixels > 0 && !float.IsInfinity(profile.PickupRadiusPixels) &&
                        profile.PickupRadiusPixels < Math.Min(Map.ArenaHalfWidthPixels, Map.ArenaHalfHeightPixels) &&
                        profile.NormalRewardPoolId_Ref != null && !string.IsNullOrWhiteSpace(profile.Name) &&
                        !string.IsNullOrWhiteSpace(profile.NormalTitle) && !string.IsNullOrWhiteSpace(profile.EvolutionTitle) &&
                        !string.IsNullOrWhiteSpace(profile.EmptyName) && !string.IsNullOrWhiteSpace(profile.EmptyDescription) &&
                        !string.IsNullOrWhiteSpace(profile.ClaimText) && (!profile.AllowEvolution || BuildConfig != null),
                    $"TbChestProfile {profile.Id}: invalid pickup, reward or display data.");
                RequirePrefab(profile.WorldPrefabResourceId_Ref, "world chest");
            }
            Require(SpawnPhases.All(phase => phase.MonsterWeights.All(weight => !weight.MonsterId_Ref.IsElite)),
                "TbSpawnPhase: elite must enter through its event, not ordinary weights.");
            Require(!Boss.MonsterId_Ref.IsElite && !Boss.MonsterId_Ref.DeathChestProfileId.HasValue,
                "TbBossEncounter: final boss cannot use elite death rewards.");
        }
    }
}
