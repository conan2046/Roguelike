using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>从 TbRunWeapon.ranks 导出的本武器修正，不持有 Unity 对象或可变配置。</summary>
    public readonly struct CombatWeaponUpgrade
    {
        public readonly int SkillId, OriginalSkillId;
        public readonly double DamageMultiplier, IntervalMultiplier, RangeMultiplier;

        /// <summary>入局、升星或炼器后将TbRunWeapon与TbTribulationRefinement千分比换成结算倍率。</summary>
        /// <param name="rank">TbRunWeapon.ranks 当前星级。</param>
        /// <param name="compensationPermille">TbRunBuild.weaponDamagePermille 角色补偿。</param>
        /// <param name="evolvedSkillId">已领取进化或炼器的结果技能；空表示当前星级技能。</param>
        /// <param name="refinement">已领取的TbTribulationRefinement；空表示保留原武器规则。</param>
        /// <exception cref="ArgumentNullException">星级配置为空。</exception>
        public CombatWeaponUpgrade(RunWeaponRank rank, int compensationPermille, int? evolvedSkillId = null, TribulationRefinementConfig refinement = null)
        {
            if (rank == null) throw new ArgumentNullException(nameof(rank));
            OriginalSkillId = rank.SkillId;
            SkillId = evolvedSkillId ?? rank.SkillId;
            DamageMultiplier = rank.DamagePermille / 1000d * compensationPermille / 1000d;
            IntervalMultiplier = rank.IntervalPermille / 1000d;
            RangeMultiplier = rank.RangePermille / 1000d;
            if (refinement != null)
            {
                DamageMultiplier *= refinement.DamagePermille / 1000d;
                IntervalMultiplier *= refinement.IntervalPermille / 1000d;
                RangeMultiplier *= refinement.RangePermille / 1000d;
            }
        }
    }

    /// <summary>独立管理 TbRunBuild 的武器槽、被动槽和逐级状态；单局重开时整体重建。</summary>
    public sealed partial class CombatBuildState
    {
        private readonly SortedDictionary<int, int> weaponRanks = new SortedDictionary<int, int>();
        private readonly SortedDictionary<int, int> passiveRanks = new SortedDictionary<int, int>();
        private readonly HashSet<int> evolvedWeapons = new HashSet<int>();
        public RunBuildConfig Config { get; }
        public IReadOnlyDictionary<int, int> WeaponRanks => weaponRanks;
        public IReadOnlyDictionary<int, int> PassiveRanks => passiveRanks;
        public IReadOnlyCollection<int> EvolvedWeapons => evolvedWeapons;
        /// <summary>仅在本代次构筑变更时递增，供 HUD 避免逐帧重建名称字符串。</summary>
        public ulong Revision { get; private set; }

        /// <summary>从已验证的 TbRunBuild 创建一代构筑并将初始武器放入实际槽位。</summary>
        /// <param name="config">TbStage.runBuildId 引用的构筑配置。</param>
        /// <exception cref="ArgumentNullException">配置为空。</exception>
        public CombatBuildState(RunBuildConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            foreach (RunWeaponConfig weapon in config.InitialWeaponIds_Ref) weaponRanks.Add(weapon.Id, weapon.Ranks[0].Star);
            Revision++;
        }

        /// <summary>定义聚合时检查构筑目录、初始占槽、连续等级与强引用，禁止入局后用默认值补齐。</summary>
        /// <param name="config">TbRunBuild 行。</param>
        /// <param name="characterId">TbStage.initialCharacterId。</param>
        /// <exception cref="InvalidOperationException">引用、槽数、星级或效果字段不合法。</exception>
        public static void Validate(RunBuildConfig config, int characterId)
        {
            Require(config != null && config.WeaponSlotCount > 0 && config.PassiveSlotCount > 0 &&
                    config.WeaponDamagePermille > 0 && (!config.CharacterId.HasValue || config.CharacterId == characterId),
                "TbRunBuild: invalid capacity, compensation or character restriction.");
            Require(config.WeaponIds_Ref != null && config.PassiveIds_Ref != null && config.InitialWeaponIds_Ref != null &&
                    config.InitialWeaponIds_Ref.Count > 0 && config.InitialWeaponIds_Ref.Count <= config.WeaponSlotCount,
                $"TbRunBuild {config.Id}: invalid initial slots or catalogs.");
            Require(config.WeaponIds_Ref.All(item => item != null) && config.PassiveIds_Ref.All(item => item != null) &&
                    config.InitialWeaponIds_Ref.All(item => item != null && config.WeaponIds.Contains(item.Id)) &&
                    config.WeaponIds.Distinct().Count() == config.WeaponIds.Count &&
                    config.PassiveIds.Distinct().Count() == config.PassiveIds.Count &&
                    config.InitialWeaponIds.Distinct().Count() == config.InitialWeaponIds.Count,
                $"TbRunBuild {config.Id}: unresolved or duplicate catalog member.");
            Require(!string.IsNullOrWhiteSpace(config.WeaponSlotsFormat) && !string.IsNullOrWhiteSpace(config.PassiveSlotsFormat) &&
                    !string.IsNullOrWhiteSpace(config.EntryFormat) && !string.IsNullOrWhiteSpace(config.PassiveEntryFormat) &&
                    !string.IsNullOrWhiteSpace(config.WeaponRankFormat) && !string.IsNullOrWhiteSpace(config.PassiveRankFormat) &&
                    config.EntrySeparator != null,
                $"TbRunBuild {config.Id}: missing display formats.");
            foreach (RunWeaponConfig weapon in config.WeaponIds_Ref)
            {
                Require(!string.IsNullOrWhiteSpace(weapon.Name) && weapon.Ranks.Count > 0,
                    $"TbRunWeapon {weapon.Id}: empty name or rank list.");
                for (int index = 0; index < weapon.Ranks.Count; index++)
                {
                    RunWeaponRank rank = weapon.Ranks[index];
                    Require(rank != null && rank.Star == index + 1 && rank.SkillId_Ref != null &&
                            rank.DamagePermille > 0 && rank.IntervalPermille > 0 && rank.RangePermille > 0,
                        $"TbRunWeapon {weapon.Id}: invalid rank {index + 1}.");
                }
                WeaponEvolutionConfig evolution = weapon.EvolutionId_Ref;
                Require(evolution != null && evolution.WeaponId == weapon.Id && evolution.WeaponId_Ref == weapon &&
                        evolution.RequiredStar > 0 && evolution.RequiredStar <= weapon.Ranks.Count &&
                        config.PassiveIds.Contains(evolution.PassiveId) && evolution.PassiveId_Ref != null &&
                        evolution.RequiredPassiveRank > 0 && evolution.RequiredPassiveRank <= evolution.PassiveId_Ref.Ranks.Count &&
                        evolution.ResultSkillId_Ref != null && !weapon.Ranks.Any(rank => rank.SkillId == evolution.ResultSkillId) &&
                        !string.IsNullOrWhiteSpace(evolution.Name) && !string.IsNullOrWhiteSpace(evolution.Description),
                    $"TbWeaponEvolution {weapon.EvolutionId}: invalid weapon, prerequisite or result.");
            }
            foreach (RunPassiveConfig passive in config.PassiveIds_Ref)
            {
                Require(!string.IsNullOrWhiteSpace(passive.Name) && passive.Ranks.Count > 0,
                    $"TbRunPassive {passive.Id}: empty name or rank list.");
                for (int index = 0; index < passive.Ranks.Count; index++)
                {
                    RunPassiveRank rank = passive.Ranks[index];
                    Require(rank != null && rank.Rank == index + 1 && rank.AttributeId_Ref != null &&
                            Enum.IsDefined(typeof(EAttributeModifyOperation), rank.Operation) &&
                            (rank.Operation != EAttributeModifyOperation.Multiply || rank.ValueMilli > 0),
                        $"TbRunPassive {passive.Id}: invalid rank {index + 1}.");
                }
            }
            var skillOwners = new Dictionary<int, int>();
            foreach (RunWeaponConfig weapon in config.WeaponIds_Ref)
                foreach (int skill in weapon.Ranks.Select(rank => rank.SkillId).Append(weapon.EvolutionId_Ref.ResultSkillId).Distinct())
                {
                    Require(!skillOwners.ContainsKey(skill), $"TbRunBuild {config.Id}: skill {skill} belongs to multiple weapons.");
                    skillOwners.Add(skill, weapon.Id);
                }
        }

        /// <summary>候选过滤时同时检查武器目录、已占槽及当前星级。</summary>
        /// <param name="weapon">TbUpgradeOption.runWeaponId 对应武器。</param>
        /// <returns>可以获得或升星时为真。</returns>
        public bool CanUpgrade(RunWeaponConfig weapon) => weapon != null && Config.WeaponIds.Contains(weapon.Id) &&
            !evolvedWeapons.Contains(weapon.Id) &&
            (weaponRanks.TryGetValue(weapon.Id, out int rank) ? rank < weapon.Ranks.Count : weaponRanks.Count < Config.WeaponSlotCount);

        /// <summary>候选过滤时独立检查被动目录、被动槽和等级。</summary>
        /// <param name="passive">TbUpgradeOption.runPassiveId 对应被动。</param>
        /// <returns>可以获得或升级时为真。</returns>
        public bool CanUpgrade(RunPassiveConfig passive) => passive != null && Config.PassiveIds.Contains(passive.Id) &&
            (passiveRanks.TryGetValue(passive.Id, out int rank) ? rank < passive.Ranks.Count : passiveRanks.Count < Config.PassiveSlotCount);

        /// <summary>已通过选卡代次校验后获得武器或提升已有武器，不额外占槽。</summary>
        /// <param name="weapon">池中强引用的武器。</param>
        /// <exception cref="InvalidOperationException">槽满、满星或目录不匹配。</exception>
        /// <remarks>只修改本代次武器等级；调用方随后刷新活动技能和 ECS 快照。</remarks>
        internal void Upgrade(RunWeaponConfig weapon)
        {
            if (!CanUpgrade(weapon)) throw new InvalidOperationException("TbRunWeapon upgrade is not eligible.");
            weaponRanks[weapon.Id] = weaponRanks.TryGetValue(weapon.Id, out int rank) ? rank + 1 : weapon.Ranks[0].Star;
            Revision++;
        }

        /// <summary>选卡成功后获得或升级被动，返回本等级总效果以替换旧来源。</summary>
        /// <param name="passive">池中强引用的被动。</param>
        /// <returns>TbRunPassive.ranks 中升级后的完整等级效果。</returns>
        /// <exception cref="InvalidOperationException">槽满、满级或目录不匹配。</exception>
        /// <remarks>只更新本代次被动等级；属性聚合由调用方按固定来源替换。</remarks>
        internal RunPassiveRank Upgrade(RunPassiveConfig passive)
        {
            if (!CanUpgrade(passive)) throw new InvalidOperationException("TbRunPassive upgrade is not eligible.");
            int rank = passiveRanks.TryGetValue(passive.Id, out int previous) ? previous + 1 : passive.Ranks[0].Rank;
            passiveRanks[passive.Id] = rank;
            Revision++;
            return passive.Ranks[rank - 1];
        }

        /// <summary>构筑变化后按武器ID导出TbRunWeapon星级倍率；已领取TbTribulationRefinement优先于原形/进化形态。</summary>
        /// <returns>仅包含已占槽武器，不暴露配置目录中尚未获得的武器。</returns>
        public IReadOnlyList<CombatWeaponUpgrade> CaptureWeapons()
        {
            return weaponRanks.Select(pair =>
            {
                var weapon = Config.WeaponIds_Ref.First(item => item.Id == pair.Key);
                var refinement = ReadRefinement(pair.Key);
                int? replacement = refinement != null ? (int?)refinement.ResultSkillId :
                    evolvedWeapons.Contains(pair.Key) ? (int?)weapon.EvolutionId_Ref.ResultSkillId : null;
                return new CombatWeaponUpgrade(weapon.Ranks[pair.Value - 1], Config.WeaponDamagePermille, replacement, refinement);
            }).ToArray();
        }

        /// <summary>宝箱准备奖励时筛选TbWeaponEvolution完整前置；已炼器武器不再进入旧进化链，升星/被动本身不触发进化。</summary>
        /// <returns>按 TbWeaponEvolution.priority、id 稳定排序的可进化链。</returns>
        public IReadOnlyList<WeaponEvolutionConfig> EligibleEvolutions() => Config.WeaponIds_Ref
            .Where(weapon => !refinements.ContainsKey(weapon.Id) && !evolvedWeapons.Contains(weapon.Id) && weaponRanks.TryGetValue(weapon.Id, out int star) &&
                star >= weapon.EvolutionId_Ref.RequiredStar && passiveRanks.TryGetValue(weapon.EvolutionId_Ref.PassiveId, out int rank) &&
                rank >= weapon.EvolutionId_Ref.RequiredPassiveRank)
            .Select(weapon => weapon.EvolutionId_Ref).OrderBy(evolution => evolution.Priority).ThenBy(evolution => evolution.Id).ToArray();

        /// <summary>已通过宝箱代次和领取校验后同槽进化；拒绝缺少前置或重复领取。</summary>
        /// <param name="evolutionId">本宝箱展示的 TbWeaponEvolution.id。</param>
        /// <returns>本次发生进化时为真。</returns>
        /// <remarks>只修改当前单局构筑；调用方必须随后同步模型技能与 ECS，不能在升星回调中调用。</remarks>
        internal bool TryEvolve(int evolutionId)
        {
            WeaponEvolutionConfig evolution = EligibleEvolutions().FirstOrDefault(item => item.Id == evolutionId);
            if (evolution == null) return false;
            evolvedWeapons.Add(evolution.WeaponId);
            Revision++;
            return true;
        }

        /// <summary>构筑 HUD 按当前形态显示正式源表名称，避免进化后仍显示原武器名。</summary>
        /// <param name="weaponId">当前目录中的 TbRunWeapon.id。</param>
        /// <returns>普通武器名、已领取TbWeaponEvolution.name或TbTribulationRefinement.name。</returns>
        public string WeaponDisplayName(int weaponId)
        {
            var refinement = ReadRefinement(weaponId);
            if (refinement != null) return refinement.Name;
            RunWeaponConfig weapon = Config.WeaponIds_Ref.First(item => item.Id == weaponId);
            return evolvedWeapons.Contains(weaponId) ? weapon.EvolutionId_Ref.Name : weapon.Name;
        }

        /// <summary>数据定义聚合阶段输出精确失败信息，避免不完整配置进入正式单局。</summary>
        /// <param name="condition">待验证条件。</param>
        /// <param name="message">包含表名和上下文的错误。</param>
        /// <exception cref="InvalidOperationException">条件不满足。</exception>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
