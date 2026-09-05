using System;
using System.Collections.Generic;
using System.Linq;
using cfg;

namespace Roguelike.Features.Combat.Run
{
    /// <summary>从 TbStage 聚合并验证的正式单局只读定义；运行时代码只消费这里保留的 Luban 引用。</summary>
    public sealed class CombatRunDefinition
    {
        /// <summary>获取正式关卡入口。</summary>
        public StageConfig Stage { get; private set; }
        /// <summary>获取开放战场配置。</summary>
        public MapConfig Map { get; private set; }
        /// <summary>获取单局时序、掉落、升级和 Boss 聚合规则。</summary>
        public StageRuleConfig Rule { get; private set; }
        /// <summary>获取战斗固定步进及伤害规则。</summary>
        public CombatRulesConfig CombatRules { get; private set; }
        /// <summary>获取相机和 ANI 播放表现规则。</summary>
        public CombatPresentationConfig Presentation { get; private set; }
        /// <summary>获取正式 UI 资源方案；Prefab 数据阶段完成前允许为空。</summary>
        public CombatUiSetConfig UiSet { get; private set; }
        /// <summary>获取玩家初始角色配置。</summary>
        public CharacterConfig Character { get; private set; }
        /// <summary>获取玩家开局拥有的技能。</summary>
        public IReadOnlyList<SkillConfig> InitialSkills { get; private set; }
        /// <summary>获取本关可能解锁的全部技能，用于一次性预加载。</summary>
        public IReadOnlyList<SkillConfig> AvailableSkills { get; private set; }
        /// <summary>获取按时间连续排序的普通怪刷新阶段。</summary>
        public IReadOnlyList<SpawnPhaseConfig> SpawnPhases { get; private set; }
        /// <summary>获取本关可能生成的全部普通怪。</summary>
        public IReadOnlyList<MonsterConfig> Monsters { get; private set; }
        /// <summary>获取连续的逐级经验表。</summary>
        public IReadOnlyList<ExperienceLevelConfig> ExperienceLevels { get; private set; }
        /// <summary>获取普通怪经验掉落规则。</summary>
        public DropProfileConfig Drop { get; private set; }
        /// <summary>获取三选一池。</summary>
        public UpgradePoolConfig UpgradePool { get; private set; }
        /// <summary>获取池中按表顺序排列的升级选项。</summary>
        public IReadOnlyList<UpgradeOptionConfig> UpgradeOptions { get; private set; }
        /// <summary>获取最终 Boss 遭遇。</summary>
        public BossEncounterConfig Boss { get; private set; }

        /// <summary>隐藏构造入口，确保调用方不能绕过真实表引用校验。</summary>
        private CombatRunDefinition()
        {
        }

        /// <summary>从统一配置入口解析一个正式关卡并完成跨表约束校验。</summary>
        /// <param name="tables">Luban 生成的唯一配置入口。</param>
        /// <param name="stageId">命令行显式请求的 TbStage.id。</param>
        /// <returns>可以交给状态机和运行时的只读单局定义。</returns>
        /// <exception cref="ArgumentNullException">配置入口为空。</exception>
        /// <exception cref="InvalidOperationException">关卡不存在、引用悬空或业务约束不成立。</exception>
        public static CombatRunDefinition Create(Tables tables, int stageId)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            var stage = tables.TbStage.GetOrDefault(stageId);
            Require(stage != null, "TbStage does not contain requested stage " + stageId + ".");
            Require(stage.MapId_Ref != null && stage.StageRuleId_Ref != null && stage.CombatRulesId_Ref != null &&
                    stage.PresentationId_Ref != null && stage.InitialCharacterId_Ref != null,
                $"TbStage {stageId}: unresolved core reference.");

            var definition = new CombatRunDefinition
            {
                Stage = stage,
                Map = stage.MapId_Ref,
                Rule = stage.StageRuleId_Ref,
                CombatRules = stage.CombatRulesId_Ref,
                Presentation = stage.PresentationId_Ref,
                UiSet = stage.UiSetId_Ref,
                Character = stage.InitialCharacterId_Ref,
                Drop = stage.StageRuleId_Ref.DropProfileId_Ref,
                UpgradePool = stage.StageRuleId_Ref.UpgradePoolId_Ref,
                Boss = stage.StageRuleId_Ref.BossEncounterId_Ref
            };
            definition.ValidateStage();

            definition.SpawnPhases = definition.Rule.SpawnPhaseIds_Ref.ToArray();
            definition.ValidatePhases();
            definition.Monsters = definition.SpawnPhases.SelectMany(item => item.MonsterWeights)
                .Select(item => item.MonsterId_Ref).Distinct().OrderBy(item => item.Id).ToArray();

            definition.ExperienceLevels = tables.TbExperienceLevel.DataList
                .Where(item => item.GroupId == definition.Rule.ExperienceLevelGroupId)
                .OrderBy(item => item.Level).ToArray();
            definition.ValidateExperience();

            Require(definition.UpgradePool != null && definition.UpgradePool.OptionIds_Ref != null,
                $"TbStageRule {definition.Rule.Id}: unresolved upgrade pool.");
            definition.UpgradeOptions = definition.UpgradePool.OptionIds_Ref.ToArray();
            definition.ValidateUpgrades();

            Require(stage.InitialSkillIds_Ref != null && stage.InitialSkillIds_Ref.Count > 0 &&
                    stage.InitialSkillIds_Ref.All(item => item != null), $"TbStage {stage.Id}: unresolved initial skill.");
            definition.InitialSkills = stage.InitialSkillIds_Ref.ToArray();
            definition.AvailableSkills = definition.InitialSkills
                .Concat(definition.UpgradeOptions.Where(item => item.TargetSkillId_Ref != null).Select(item => item.TargetSkillId_Ref))
                .GroupBy(item => item.Id).Select(group => group.First()).OrderBy(item => item.Id).ToArray();
            foreach (var skill in definition.AvailableSkills) definition.ValidatePlayerSkill(skill);
            foreach (var monster in definition.Monsters) ValidateMonster(monster, "spawn phase");
            definition.ValidateBoss();
            return definition;
        }

        /// <summary>在正式展示入口启动前验证 UI Prefab 资源及业务文本已完成配置。</summary>
        /// <exception cref="InvalidOperationException">UI 表缺行、资源不是 Prefab 或显示文本为空。</exception>
        public void RequireUiReady()
        {
            Require(UiSet != null, $"TbStage {Stage.Id}: uiSetId is required by the formal runtime.");
            RequirePrefab(UiSet.HudPrefabResourceId_Ref, "HUD");
            RequirePrefab(UiSet.BossBarPrefabResourceId_Ref, "Boss bar");
            RequirePrefab(UiSet.UpgradePanelPrefabResourceId_Ref, "upgrade panel");
            RequirePrefab(UiSet.SettlementPrefabResourceId_Ref, "settlement panel");
            Require(!string.IsNullOrWhiteSpace(UiSet.LevelUpTitle) && !string.IsNullOrWhiteSpace(UiSet.VictoryText) &&
                    !string.IsNullOrWhiteSpace(UiSet.DefeatText) && !string.IsNullOrWhiteSpace(UiSet.RestartText),
                $"TbCombatUiSet {UiSet.Id}: display text is incomplete.");
        }

        /// <summary>验证关卡、地图、固定步进及玩家基础引用。</summary>
        /// <exception cref="InvalidOperationException">时间、场地、角色或规则配置非法。</exception>
        private void ValidateStage()
        {
            Require(Rule.DurationMilli > 0 && Rule.BossTimeMilli > 0 && Rule.BossTimeMilli <= Rule.DurationMilli,
                $"TbStageRule {Rule.Id}: invalid duration or boss time.");
            Require(CombatRules.SimulationHz > 0 && CombatRules.MaxCatchUpSteps > 0,
                $"TbCombatRules {CombatRules.Id}: invalid fixed-step limits.");
            Require(Map.ArenaHalfWidthMilli > 0 && Map.ArenaHalfHeightMilli > 0 && Map.SpawnRadiusPixelsMilli > 0 &&
                    Map.CameraPaddingMilli >= 0 && Math.Abs(Map.PlayerStartXMilli) < Map.ArenaHalfWidthMilli &&
                    Math.Abs(Map.PlayerStartYMilli) < Map.ArenaHalfHeightMilli, $"TbMap {Map.Id}: invalid arena or player start.");
            Require(Character.AttributeProfileId_Ref != null && Character.VisualSetId_Ref != null && Character.BodyRadiusMilli > 0 &&
                    Character.MoveRadiusPixelsMilli > 0 && Character.MoveHeightPixelsMilli > 0,
                $"TbCharacter {Character.Id}: formal combat data is incomplete.");
            Require(Drop != null && Drop.ExperienceItemId_Ref != null && Drop.ExperienceValue > 0 &&
                    Drop.MagnetRadiusMilli > Drop.PickupRadiusMilli && Drop.PickupRadiusMilli > 0 && Drop.MagnetSpeedMilli > 0,
                $"TbStageRule {Rule.Id}: invalid drop profile.");
        }

        /// <summary>验证刷新阶段无空洞、无重叠并覆盖到 Boss 时刻。</summary>
        /// <exception cref="InvalidOperationException">阶段顺序、波次间隔或权重非法。</exception>
        private void ValidatePhases()
        {
            Require(Rule.SpawnPhaseIds_Ref != null && Rule.SpawnPhaseIds_Ref.Count > 0 &&
                    Rule.SpawnPhaseIds_Ref.All(item => item != null), $"TbStageRule {Rule.Id}: unresolved spawn phase.");
            int expectedBegin = 0;
            foreach (var phase in SpawnPhases)
            {
                Require(phase.BeginTimeMilli == expectedBegin && phase.EndTimeMilli > phase.BeginTimeMilli &&
                        phase.UnitsPerWave > 0 && phase.UnitIntervalMilli > 0 && phase.WaveIntervalMilli > 0,
                    $"TbSpawnPhase {phase.Id}: invalid timeline or wave cadence.");
                Require(phase.MonsterWeights != null && phase.MonsterWeights.Count > 0 &&
                        phase.MonsterWeights.All(item => item?.MonsterId_Ref != null && item.Weight > 0),
                    $"TbSpawnPhase {phase.Id}: invalid monster weights.");
                Require(phase.MonsterWeights.Select(item => item.MonsterId).Distinct().Count() == phase.MonsterWeights.Count,
                    $"TbSpawnPhase {phase.Id}: duplicate monster weight.");
                expectedBegin = phase.EndTimeMilli;
            }
            Require(expectedBegin == Rule.BossTimeMilli,
                $"TbStageRule {Rule.Id}: spawn phases must end exactly at boss time.");
        }

        /// <summary>验证经验组从 2 级开始连续且每级阈值为正。</summary>
        /// <exception cref="InvalidOperationException">经验组为空、断级或阈值非法。</exception>
        private void ValidateExperience()
        {
            Require(ExperienceLevels.Count > 0, $"Experience group {Rule.ExperienceLevelGroupId}: no rows.");
            int expected = 2;
            foreach (var level in ExperienceLevels)
            {
                Require(level.Level == expected && level.RequiredExperience > 0,
                    $"Experience group {Rule.ExperienceLevelGroupId}: expected level {expected}.");
                expected++;
            }
        }

        /// <summary>验证升级池、选项类型、资格字段和属性修改协议。</summary>
        /// <exception cref="InvalidOperationException">候选数量、权重、引用或类型组合非法。</exception>
        private void ValidateUpgrades()
        {
            Require(UpgradePool.DrawCount > 0 && UpgradeOptions.Count >= UpgradePool.DrawCount &&
                    UpgradeOptions.All(item => item != null), $"TbUpgradePool {UpgradePool.Id}: invalid draw set.");
            Require(UpgradeOptions.Select(item => item.Id).Distinct().Count() == UpgradeOptions.Count,
                $"TbUpgradePool {UpgradePool.Id}: duplicate option.");
            foreach (var option in UpgradeOptions)
            {
                Require(Enum.IsDefined(typeof(EUpgradeOptionType), option.Type) && option.MaxRank > 0 && option.Weight > 0,
                    $"TbUpgradeOption {option.Id}: invalid type, rank or weight.");
                foreach (var modifier in option.Modifiers)
                    Require(modifier?.AttributeId_Ref != null && Enum.IsDefined(typeof(EAttributeModifyOperation), modifier.Operation) &&
                            (modifier.Operation != EAttributeModifyOperation.Multiply || modifier.ValueMilli > 0),
                        $"TbUpgradeOption {option.Id}: invalid attribute modifier.");
                switch (option.Type)
                {
                    case EUpgradeOptionType.SkillUnlock:
                        Require(option.TargetSkillId_Ref != null && option.Modifiers.Count == 0 && !option.HealMilli.HasValue,
                            $"TbUpgradeOption {option.Id}: invalid skill unlock fields.");
                        break;
                    case EUpgradeOptionType.Attribute:
                        Require(option.TargetSkillId_Ref == null && option.Modifiers.Count > 0 && !option.HealMilli.HasValue,
                            $"TbUpgradeOption {option.Id}: invalid attribute fields.");
                        break;
                    case EUpgradeOptionType.Heal:
                        Require(option.TargetSkillId_Ref == null && option.Modifiers.Count == 0 && option.HealMilli > 0 &&
                                option.HealMilli % 1000 == 0, $"TbUpgradeOption {option.Id}: invalid heal fields.");
                        break;
                }
            }
        }

        /// <summary>验证玩家技能属于本期支持的弹丸或目标位置群体投递，并具有完整表现引用。</summary>
        /// <param name="skill">初始或升级可解锁技能。</param>
        /// <exception cref="InvalidOperationException">战斗、碰撞或 ANI 表现字段缺失。</exception>
        private void ValidatePlayerSkill(SkillConfig skill)
        {
            Require(skill?.VisualSetId_Ref != null && skill.CombatProfileId_Ref != null,
                "TbSkill: unresolved player skill.");
            var combat = skill.CombatProfileId_Ref;
            Require(combat.BaseIntervalMilli > 0 && combat.RangeMilli >= 0,
                $"TbSkillCombat {combat.Id}: invalid common fields.");
            if (combat.DeliveryType == ESkillDeliveryType.Projectile)
            {
                Require(combat.ProjectileSpeedMilli > 0 && combat.ProjectileLifetimeMilli > 0 && skill.ProjectileRadiusMilli > 0 &&
                        skill.ProjectileOffsetXMilli.HasValue && skill.ProjectileOffsetYMilli.HasValue &&
                        skill.ProjectileClipId_Ref != null && (!skill.ImpactClipId.HasValue || skill.ImpactClipId_Ref != null),
                    $"TbSkill {skill.Id}: incomplete projectile fields.");
                return;
            }
            Require(combat.DeliveryType == ESkillDeliveryType.TargetArea && skill.AreaRadiusMilli > 0 &&
                    skill.AreaClipIds_Ref != null && skill.AreaClipIds_Ref.Count > 0 && skill.AreaClipIds_Ref.All(item => item != null),
                $"TbSkill {skill.Id}: incomplete target-area fields.");
        }

        /// <summary>验证普通怪或 Boss 的属性、近战技能、碰撞和基础 ANI 集合。</summary>
        /// <param name="monster">权重或 Boss 表引用的怪物。</param>
        /// <param name="source">错误信息中的引用来源。</param>
        /// <exception cref="InvalidOperationException">怪物无法进入正式战斗。</exception>
        private static void ValidateMonster(MonsterConfig monster, string source)
        {
            Require(monster?.AttributeProfileId_Ref != null && monster.DefaultSkillId_Ref?.CombatProfileId_Ref != null &&
                    monster.VisualSetId_Ref?.StandClipId_Ref != null && monster.VisualSetId_Ref.MoveClipId_Ref != null &&
                    monster.VisualSetId_Ref.AttackClipId_Ref != null && monster.BodyRadiusMilli > 0 &&
                    monster.MoveRadiusPixelsMilli > 0 && monster.MoveHeightPixelsMilli > 0 && monster.MovementType.HasValue &&
                    Enum.IsDefined(typeof(EMovementType), monster.MovementType.Value) &&
                    monster.DefaultSkillId_Ref.CombatProfileId_Ref.DeliveryType == ESkillDeliveryType.Melee,
                $"TbMonster from {source}: formal combat data is incomplete.");
        }

        /// <summary>验证 Boss 只引用独立、可战斗怪物并提供有效生成和胜利规则。</summary>
        /// <exception cref="InvalidOperationException">Boss 引用或规则非法。</exception>
        private void ValidateBoss()
        {
            Require(Boss?.MonsterId_Ref != null && Boss.SpawnRadiusPixelsMilli > 0 &&
                    !string.IsNullOrWhiteSpace(Boss.HealthBarText), $"TbStageRule {Rule.Id}: invalid boss encounter.");
            ValidateMonster(Boss.MonsterId_Ref, "boss encounter");
        }

        /// <summary>验证正式 UI 表引用的资源确实是项目内 Prefab。</summary>
        /// <param name="resource">TbCombatUiSet 引用的 TbResource 行。</param>
        /// <param name="label">错误信息使用的 UI 部件名。</param>
        /// <exception cref="InvalidOperationException">资源缺失、类型错误或地址为空。</exception>
        private static void RequirePrefab(ResourceConfig resource, string label)
        {
            Require(resource != null && resource.Type == EResourceType.Prefab && !string.IsNullOrWhiteSpace(resource.Path),
                "TbCombatUiSet: invalid " + label + " prefab resource.");
        }

        /// <summary>用统一异常类型报告正式单局配置阻断，避免下游以默认值继续运行。</summary>
        /// <param name="condition">必须成立的跨表约束。</param>
        /// <param name="message">定位到具体 Luban 表或字段的错误信息。</param>
        /// <exception cref="InvalidOperationException">约束不成立。</exception>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
