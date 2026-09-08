using System;
using System.Collections.Generic;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Run;
using Unity.Mathematics;

namespace Roguelike.Features.Combat.Ecs
{
    /// <summary>把正式关卡与测试场景转换为 ECS 会话共同需要的只读输入，隔离 TbPerformanceScenario 专属行为。</summary>
    public sealed class CombatSessionSettings
    {
        private Dictionary<int, MonsterConfig> monstersById;

        public CombatRulesConfig Rules { get; private set; }
        public CombatPresentationConfig Presentation { get; private set; }
        public CharacterConfig Character { get; private set; }
        public AttributeProfileConfig CharacterProfile { get; private set; }
        public SkillConfig PlayerSkill { get; private set; }
        public IReadOnlyList<SkillConfig> AvailablePlayerSkills { get; private set; }
        public IReadOnlyList<int> InitialPlayerSkillIds { get; private set; }
        public IReadOnlyList<MonsterConfig> Monsters { get; private set; }
        public float2 Arena { get; private set; }
        public float2 PlayerStart { get; private set; }
        public float SpawnRadiusPixels { get; private set; }
        public int RandomSeed { get; private set; }
        public int InitialMonsterCount { get; private set; }
        public int SpawnColumns { get; private set; }
        public float HorizontalSpacing { get; private set; }
        public float VerticalSpacing { get; private set; }
        public bool UseLegacyTimedSpawn { get; private set; }
        public float SpawnIntervalSeconds { get; private set; }
        public float SpawnUnitIntervalSeconds { get; private set; }
        public int SpawnBatchCount { get; private set; }
        public bool ReplenishOnDeath { get; private set; }
        public bool RestorePlayerAfterDamage { get; private set; }
        public bool StationaryInput { get; private set; }
        public bool IsFormalRun { get; private set; }

        /// <summary>隐藏构造入口，确保设置只能从已解析的 Luban 配置创建。</summary>
        private CombatSessionSettings()
        {
        }

        /// <summary>将 Combat 测试场景转换为原行为完全一致的会话设置。</summary>
        /// <param name="scenario">统一配置入口解析完成的 TbPerformanceScenario 行。</param>
        /// <returns>保留阵列、定时补怪和测试策略的只读设置。</returns>
        /// <exception cref="InvalidOperationException">场景引用或测试参数不完整。</exception>
        public static CombatSessionSettings FromScenario(PerformanceScenarioConfig scenario)
        {
            ValidateScenario(scenario);
            var settings = new CombatSessionSettings
            {
                Rules = scenario.CombatRulesId_Ref,
                Presentation = scenario.PresentationId_Ref,
                Character = scenario.CharacterId_Ref,
                CharacterProfile = scenario.CharacterProfileOverrideId_Ref,
                monsterProfileOverride = scenario.MonsterProfileOverrideId_Ref,
                PlayerSkill = scenario.CharacterId_Ref.DefaultSkillId_Ref,
                AvailablePlayerSkills = new[] { scenario.CharacterId_Ref.DefaultSkillId_Ref },
                InitialPlayerSkillIds = new[] { scenario.CharacterId_Ref.DefaultSkillId_Ref.Id },
                Monsters = scenario.MonsterIds_Ref.ToArray(),
                Arena = new float2(scenario.ArenaHalfWidth.Value, scenario.ArenaHalfHeight.Value),
                PlayerStart = new float2(scenario.PlayerStartX.Value, scenario.PlayerStartY.Value),
                SpawnRadiusPixels = scenario.SpawnRadiusPixels ?? 0,
                RandomSeed = scenario.RandomSeed,
                InitialMonsterCount = scenario.EntityCount,
                SpawnColumns = scenario.SpawnColumns,
                HorizontalSpacing = scenario.HorizontalSpacing,
                VerticalSpacing = scenario.VerticalSpacing,
                UseLegacyTimedSpawn = scenario.SpawnIntervalSeconds.HasValue,
                SpawnIntervalSeconds = scenario.SpawnIntervalSeconds ?? 0,
                SpawnUnitIntervalSeconds = scenario.SpawnUnitIntervalSeconds ?? 0,
                SpawnBatchCount = scenario.SpawnBatchCount ?? 0,
                ReplenishOnDeath = scenario.ReplenishOnDeath.Value,
                RestorePlayerAfterDamage = scenario.PlayerHealthPolicy == ETestHealthPolicy.RestoreAfterDamage,
                StationaryInput = scenario.PlayerInputPolicy == ETestInputPolicy.Stationary,
                IsFormalRun = false
            };
            settings.monstersById = settings.Monsters.ToDictionary(item => item.Id);
            return settings;
        }

        /// <summary>将正式 TbStage 聚合定义转换为动态生成会话设置，不读取任何性能测试行。</summary>
        /// <param name="definition">已经完成跨表验证的正式单局定义。</param>
        /// <returns>以地图、角色、怪物和关卡随机种子组成的正式设置。</returns>
        /// <exception cref="ArgumentNullException">定义为空。</exception>
        public static CombatSessionSettings FromRun(CombatRunDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            ValidateRules(definition.CombatRules);
            var monsters = definition.Monsters.Concat(new[] { definition.Boss.MonsterId_Ref })
                .GroupBy(item => item.Id).Select(group => group.First()).OrderBy(item => item.Id).ToArray();
            var settings = new CombatSessionSettings
            {
                Rules = definition.CombatRules,
                Presentation = definition.Presentation,
                Character = definition.Character,
                CharacterProfile = definition.Character.AttributeProfileId_Ref,
                PlayerSkill = definition.InitialSkills[0],
                AvailablePlayerSkills = definition.AvailableSkills,
                InitialPlayerSkillIds = definition.InitialSkills.Select(item => item.Id).ToArray(),
                Monsters = monsters,
                Arena = new float2(ConfigNumber.Decode(definition.Map.ArenaHalfWidthMilli), ConfigNumber.Decode(definition.Map.ArenaHalfHeightMilli)),
                PlayerStart = new float2(ConfigNumber.Decode(definition.Map.PlayerStartXMilli), ConfigNumber.Decode(definition.Map.PlayerStartYMilli)),
                SpawnRadiusPixels = ConfigNumber.Decode(definition.Map.SpawnRadiusPixelsMilli),
                RandomSeed = definition.Rule.RandomSeed,
                InitialMonsterCount = 0,
                SpawnColumns = 1,
                ReplenishOnDeath = false,
                RestorePlayerAfterDamage = false,
                StationaryInput = false,
                IsFormalRun = true
            };
            settings.monstersById = monsters.ToDictionary(item => item.Id);
            return settings;
        }

        /// <summary>按单位真实配置 ID 取得本会话允许生成的怪物。</summary>
        /// <param name="monsterId">TbMonster.id。</param>
        /// <returns>本会话怪物目录中的解析引用。</returns>
        /// <exception cref="InvalidOperationException">请求怪物不属于本关或测试场景。</exception>
        public MonsterConfig GetMonster(int monsterId)
        {
            if (!monstersById.TryGetValue(monsterId, out var monster))
                throw new InvalidOperationException("Combat session cannot spawn TbMonster " + monsterId + ".");
            return monster;
        }

        /// <summary>按入口类型选择怪物属性；正式单局只读怪物自身方案，测试入口保留覆盖方案。</summary>
        /// <param name="monster">本会话目录中的 TbMonster。</param>
        /// <returns>用于创建 ECS 单位的属性方案。</returns>
        public AttributeProfileConfig GetMonsterProfile(MonsterConfig monster) =>
            IsFormalRun ? monster.AttributeProfileId_Ref : monsterProfileOverride;

        private AttributeProfileConfig monsterProfileOverride;

        /// <summary>在创建测试设置前验证旧测试协议，避免重构改变既有异常边界。</summary>
        /// <param name="value">待转换的 TbPerformanceScenario。</param>
        /// <exception cref="InvalidOperationException">任何必需引用、数值或策略非法。</exception>
        private static void ValidateScenario(PerformanceScenarioConfig value)
        {
            if (value == null || value.Kind != EPerformanceKind.Combat || value.CombatRulesId_Ref == null || value.PresentationId_Ref?.InitialDirectionId_Ref == null ||
                value.CharacterId_Ref?.DefaultSkillId_Ref?.CombatProfileId_Ref == null || !value.CharacterId_Ref.BodyRadiusPixels.HasValue ||
                !value.CharacterId_Ref.BodyOffsetXPixels.HasValue || !value.CharacterId_Ref.BodyOffsetYPixels.HasValue ||
                value.CharacterProfileOverrideId_Ref == null || value.MonsterProfileOverrideId_Ref == null ||
                value.MonsterIds_Ref == null || value.MonsterIds_Ref.Count == 0 || value.EntityCount <= 0 || value.SpawnColumns <= 0 ||
                !value.ArenaHalfWidth.HasValue || !value.ArenaHalfHeight.HasValue || !value.PlayerStartX.HasValue || !value.PlayerStartY.HasValue ||
                !value.ReplenishOnDeath.HasValue || !value.PlayerHealthPolicy.HasValue || !value.PlayerInputPolicy.HasValue)
                throw new InvalidOperationException("TbPerformanceScenario: incomplete combat configuration.");
            CombatMath.Positive(value.ArenaHalfWidth.Value); CombatMath.Positive(value.ArenaHalfHeight.Value);
            CombatMath.NonNegative(value.HorizontalSpacing); CombatMath.NonNegative(value.VerticalSpacing);
            bool anySpawn = value.SpawnRadiusPixels.HasValue || value.SpawnIntervalSeconds.HasValue || value.SpawnBatchCount.HasValue || value.SpawnUnitIntervalSeconds.HasValue;
            if (anySpawn)
            {
                if (!value.SpawnRadiusPixels.HasValue || !value.SpawnIntervalSeconds.HasValue || !value.SpawnBatchCount.HasValue || !value.SpawnUnitIntervalSeconds.HasValue || value.SpawnBatchCount.Value <= 0 || value.ReplenishOnDeath.Value)
                    throw new InvalidOperationException("Timed spawn requires complete parameters and cannot replenish on death.");
                CombatMath.Positive(value.SpawnRadiusPixels.Value); CombatMath.Positive(value.SpawnIntervalSeconds.Value);
                CombatMath.Positive(value.SpawnUnitIntervalSeconds.Value);
                CombatMath.Positive(value.CombatRulesId_Ref.WorldUnitsPerPixel);
                CombatMath.Positive(value.SpawnRadiusPixels.Value * value.CombatRulesId_Ref.WorldUnitsPerPixel);
            }
            if (!Enum.IsDefined(typeof(ETestHealthPolicy), value.PlayerHealthPolicy.Value) || !Enum.IsDefined(typeof(ETestInputPolicy), value.PlayerInputPolicy.Value))
                throw new InvalidOperationException($"TbPerformanceScenario {value.Id}: unknown policy.");
            foreach (var monster in value.MonsterIds_Ref)
                if (monster?.DefaultSkillId_Ref?.CombatProfileId_Ref == null || !monster.BodyRadiusPixels.HasValue ||
                    !monster.BodyOffsetXPixels.HasValue || !monster.BodyOffsetYPixels.HasValue)
                    throw new InvalidOperationException($"TbPerformanceScenario {value.Id}: monster is not combat-ready.");
            ValidateRules(value.CombatRulesId_Ref);
        }

        /// <summary>验证正式与测试会话共同依赖的固定步进、空间索引和攻击时间边界。</summary>
        /// <param name="rules">TbCombatRules 行。</param>
        /// <exception cref="InvalidOperationException">规则为空、频率、补算上限或正数参数非法。</exception>
        private static void ValidateRules(CombatRulesConfig rules)
        {
            if (rules == null || rules.SimulationHz <= 0 || rules.MaxCatchUpSteps <= 0)
                throw new InvalidOperationException("TbCombatRules: invalid fixed-step limits.");
            CombatMath.Positive(rules.MinAttackInterval);
            CombatMath.Positive(rules.SpatialCellSize);
            CombatMath.Positive(rules.TargetRefreshSeconds);
        }

    }
}
