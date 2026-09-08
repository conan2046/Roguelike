namespace cfg
{
    /// <summary>配置整数缩放协议；千分单位是文件格式常量，不是可调玩法参数。</summary>
    public static class ConfigNumber
    {
        public const int Scale = 1000;
        /// <summary>配置装载或编辑预览时将千分整数还原为原始单位；调用方在入局时缓存模拟所需值。</summary>
        /// <param name="value">Luban 中以原值乘一千存储的整数。</param>
        /// <returns>还原单位后的浮点值。</returns>
        public static float Decode(int value) => value / (float)Scale;
        /// <summary>属性装载时还原长整数千分值，避免属性上限放大后超过 Int32。</summary>
        /// <param name="value">AttributeValue 或 TbAttribute 中的千分长整数。</param>
        /// <returns>原始属性单位。</returns>
        public static double Decode(long value) => value / (double)Scale;
        /// <summary>编辑器导出时按三位小数四舍五入并检查整数范围；零与空值由调用方区分。</summary>
        /// <param name="value">原始单位的编辑数值。</param>
        /// <returns>可写入 Luban int 字段的千分整数。</returns>
        /// <exception cref="System.OverflowException">值非有限或超出千分整数范围。</exception>
        public static int Encode(double value) => checked((int)System.Math.Round(value * Scale, System.MidpointRounding.AwayFromZero));
    }

    /// <summary>AttributeValue 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class AttributeValue
    {
        /// <summary>TbAttributeProfile.values 属性基础值。 读取 AttributeValue.valueMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public double Value => ConfigNumber.Decode(ValueMilli);
    }

    /// <summary>AnimationDirectionConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class AnimationDirectionConfig
    {
        /// <summary>方向向量X 读取 AnimationDirectionConfig.xMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float X => ConfigNumber.Decode(XMilli);
        /// <summary>方向向量Y 读取 AnimationDirectionConfig.yMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float Y => ConfigNumber.Decode(YMilli);
    }

    /// <summary>SkillConfig 的比例参数换算入口；像素字段由 Luban 直接提供，缩放仍使用千分比协议。</summary>
    public sealed partial class SkillConfig
    {
        /// <summary>弹丸表现相对缩放；由千分比还原。</summary>
        public float? ProjectileVisualScale => ProjectileVisualScalePermille.HasValue ? ConfigNumber.Decode(ProjectileVisualScalePermille.Value) : (float?)null;
        /// <summary>命中特效表现相对缩放；由千分比还原。</summary>
        public float? ImpactVisualScale => ImpactVisualScalePermille.HasValue ? ConfigNumber.Decode(ImpactVisualScalePermille.Value) : (float?)null;
        /// <summary>范围特效表现相对缩放；由千分比还原。</summary>
        public float? AreaVisualScale => AreaVisualScalePermille.HasValue ? ConfigNumber.Decode(AreaVisualScalePermille.Value) : (float?)null;
    }

    /// <summary>SkillCombatConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class SkillCombatConfig
    {
        /// <summary>基础攻击间隔，秒 读取 SkillCombatConfig.baseIntervalMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float BaseInterval => ConfigNumber.Decode(BaseIntervalMilli);
        /// <summary>弹丸寿命，秒 读取 SkillCombatConfig.projectileLifetimeMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? ProjectileLifetime => ProjectileLifetimeMilli.HasValue ? ConfigNumber.Decode(ProjectileLifetimeMilli.Value) : (float?)null;
        /// <summary>普通近战 zd→gj 前摇秒数；弹丸留空 读取 SkillCombatConfig.attackWindupSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? AttackWindupSeconds => AttackWindupSecondsMilli.HasValue ? ConfigNumber.Decode(AttackWindupSecondsMilli.Value) : (float?)null;
    }

    /// <summary>AttributeConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class AttributeConfig
    {
        /// <summary>合法下限；调试值 读取 AttributeConfig.minValueMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public double MinValue => ConfigNumber.Decode(MinValueMilli);
        /// <summary>合法上限；调试值 读取 AttributeConfig.maxValueMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public double MaxValue => ConfigNumber.Decode(MaxValueMilli);
    }

    /// <summary>PerformanceScenarioConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class PerformanceScenarioConfig
    {
        /// <summary>预热秒数，预热期间不计入结果 读取 PerformanceScenarioConfig.warmupSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float WarmupSeconds => ConfigNumber.Decode(WarmupSecondsMilli);
        /// <summary>正式采样秒数 读取 PerformanceScenarioConfig.sampleSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float SampleSeconds => ConfigNumber.Decode(SampleSecondsMilli);
        /// <summary>平均帧率通过线 读取 PerformanceScenarioConfig.targetAverageFpsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float TargetAverageFps => ConfigNumber.Decode(TargetAverageFpsMilli);
        /// <summary>P95帧耗时上限，毫秒 读取 PerformanceScenarioConfig.maxP95FrameTimeMsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MaxP95FrameTimeMs => ConfigNumber.Decode(MaxP95FrameTimeMsMilli);
        /// <summary>将 TbPerformanceScenario.horizontalSpacingPixels 按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float HorizontalSpacing => HorizontalSpacingPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>将 TbPerformanceScenario.verticalSpacingPixels 按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float VerticalSpacing => VerticalSpacingPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>测试实体渲染缩放 读取 PerformanceScenarioConfig.entityScaleMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float EntityScale => ConfigNumber.Decode(EntityScaleMilli);
        /// <summary>将 TbPerformanceScenario.moveAmplitudePixels 按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float MoveAmplitude => MoveAmplitudePixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>实体最小移动角速度 读取 PerformanceScenarioConfig.moveSpeedMinMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MoveSpeedMin => ConfigNumber.Decode(MoveSpeedMinMilli);
        /// <summary>实体最大移动角速度 读取 PerformanceScenarioConfig.moveSpeedMaxMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MoveSpeedMax => ConfigNumber.Decode(MoveSpeedMaxMilli);
        /// <summary>将 TbPerformanceScenario.cameraPaddingPixels 按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float CameraPadding => CameraPaddingPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>将可选场地半宽像素按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float? ArenaHalfWidth => ArenaHalfWidthPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>将可选场地半高像素按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float? ArenaHalfHeight => ArenaHalfHeightPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>将可选玩家出生X像素按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float? PlayerStartX => PlayerStartXPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>将可选玩家出生Y像素按 TbCombatRules.worldUnitsPerPixel 转为世界单位。</summary>
        public float? PlayerStartY => PlayerStartYPixels * CombatRulesId_Ref.WorldUnitsPerPixel;
        /// <summary>波间等待秒数；上一波最后一只之后计时，首波同样等待 读取 PerformanceScenarioConfig.spawnIntervalSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? SpawnIntervalSeconds => SpawnIntervalSecondsMilli.HasValue ? ConfigNumber.Decode(SpawnIntervalSecondsMilli.Value) : (float?)null;
        /// <summary>波内每只生成间隔，模拟秒 读取 PerformanceScenarioConfig.spawnUnitIntervalSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? SpawnUnitIntervalSeconds => SpawnUnitIntervalSecondsMilli.HasValue ? ConfigNumber.Decode(SpawnUnitIntervalSecondsMilli.Value) : (float?)null;
    }

    /// <summary>CombatPresentationConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class CombatPresentationConfig
    {
        /// <summary>背景红 读取 CombatPresentationConfig.backgroundRMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float BackgroundR => ConfigNumber.Decode(BackgroundRMilli);
        /// <summary>背景绿 读取 CombatPresentationConfig.backgroundGMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float BackgroundG => ConfigNumber.Decode(BackgroundGMilli);
        /// <summary>背景蓝 读取 CombatPresentationConfig.backgroundBMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float BackgroundB => ConfigNumber.Decode(BackgroundBMilli);
        /// <summary>调试弹丸红 读取 CombatPresentationConfig.projectileRMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float ProjectileR => ConfigNumber.Decode(ProjectileRMilli);
        /// <summary>调试弹丸绿 读取 CombatPresentationConfig.projectileGMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float ProjectileG => ConfigNumber.Decode(ProjectileGMilli);
        /// <summary>调试弹丸蓝 读取 CombatPresentationConfig.projectileBMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float ProjectileB => ConfigNumber.Decode(ProjectileBMilli);
        /// <summary>实际扣血后的白闪持续秒数 读取 CombatPresentationConfig.hitFlashDurationSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HitFlashDurationSeconds => ConfigNumber.Decode(HitFlashDurationSecondsMilli);
        /// <summary>白闪颜色红色通道 读取 CombatPresentationConfig.hitFlashRMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HitFlashR => ConfigNumber.Decode(HitFlashRMilli);
        /// <summary>白闪颜色绿色通道 读取 CombatPresentationConfig.hitFlashGMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HitFlashG => ConfigNumber.Decode(HitFlashGMilli);
        /// <summary>白闪颜色蓝色通道 读取 CombatPresentationConfig.hitFlashBMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HitFlashB => ConfigNumber.Decode(HitFlashBMilli);
        /// <summary>白闪叠加不透明度 读取 CombatPresentationConfig.hitFlashOpacityMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HitFlashOpacity => ConfigNumber.Decode(HitFlashOpacityMilli);
    }

    /// <summary>CombatRulesConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class CombatRulesConfig
    {
        /// <summary>技能最短间隔，秒 读取 CombatRulesConfig.minAttackIntervalMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MinAttackInterval => ConfigNumber.Decode(MinAttackIntervalMilli);
        /// <summary>玩家正数扣血后的无敌时间，秒 读取 CombatRulesConfig.playerInvulnerabilitySecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float PlayerInvulnerabilitySeconds => ConfigNumber.Decode(PlayerInvulnerabilitySecondsMilli);
        /// <summary>寻敌刷新周期，秒 读取 CombatRulesConfig.targetRefreshSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float TargetRefreshSeconds => ConfigNumber.Decode(TargetRefreshSecondsMilli);
        /// <summary>将 TbCombatRules.spatialCellSizePixels 按 worldUnitsPerPixel 转为世界单位。</summary>
        public float SpatialCellSize => SpatialCellSizePixels * WorldUnitsPerPixel;
    }
}
