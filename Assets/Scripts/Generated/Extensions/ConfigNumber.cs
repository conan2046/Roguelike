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

    /// <summary>CombatPresentationConfig 的像素表现参数换算入口；Prefab 只负责编辑，运行时只读 Luban。</summary>
    public sealed partial class CombatPresentationConfig
    {
        /// <summary>战斗血条最终显示宽度，逻辑像素；由 CombatHealthBar Prefab 导出。</summary>
        public float HealthBarWidthPixels => ConfigNumber.Decode(HealthBarWidthPixelsMilli);

        /// <summary>战斗血条最终显示高度，逻辑像素；由 CombatHealthBar Prefab 导出。</summary>
        public float HealthBarHeightPixels => ConfigNumber.Decode(HealthBarHeightPixelsMilli);
    }

    /// <summary>MonsterConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class MonsterConfig
    {
        /// <summary>受击判定半径，逻辑像素；由怪物预制体导出。</summary>
        public float? BodyRadiusPixels => BodyRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(BodyRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>受击判定中心横向偏移，逻辑像素；由怪物预制体导出。</summary>
        public float? BodyOffsetXPixels => BodyOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(BodyOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>受击判定中心前向偏移，逻辑像素；由怪物预制体导出。</summary>
        public float? BodyOffsetYPixels => BodyOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(BodyOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效挂点横向偏移，逻辑像素；由怪物预制体导出。</summary>
        public float? HitEffectOffsetXPixels => HitEffectOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(HitEffectOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效挂点纵向偏移，逻辑像素；由怪物预制体导出。</summary>
        public float? HitEffectOffsetYPixels => HitEffectOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(HitEffectOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>移动圆柱半径，逻辑像素；由预制体导出 读取 MonsterConfig.moveRadiusPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveRadiusPixels => MoveRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(MoveRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>移动圆柱高度，逻辑像素；由预制体导出 读取 MonsterConfig.moveHeightPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveHeightPixels => MoveHeightPixelsMilli.HasValue ? ConfigNumber.Decode(MoveHeightPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底心横向偏移，逻辑像素 读取 MonsterConfig.moveOffsetXPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveOffsetXPixels => MoveOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(MoveOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底心地面纵向偏移，逻辑像素 读取 MonsterConfig.moveOffsetYPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveOffsetYPixels => MoveOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(MoveOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底面离地高度，逻辑像素 读取 MonsterConfig.moveElevationPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveElevationPixels => MoveElevationPixelsMilli.HasValue ? ConfigNumber.Decode(MoveElevationPixelsMilli.Value) : (float?)null;
    }

    /// <summary>SkillConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class SkillConfig
    {
        /// <summary>弹丸命中半径，逻辑像素；由技能预制体导出。</summary>
        public float? ProjectileRadiusPixels => ProjectileRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(ProjectileRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>弹丸命中中心横向偏移，逻辑像素；由技能预制体导出。</summary>
        public float? ProjectileOffsetXPixels => ProjectileOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(ProjectileOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>弹丸命中中心前向偏移，逻辑像素；由技能预制体导出。</summary>
        public float? ProjectileOffsetYPixels => ProjectileOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(ProjectileOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>范围技能命中半径，逻辑像素；由技能预制体导出。</summary>
        public float? AreaRadiusPixels => AreaRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(AreaRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>弹丸表现横向偏移，逻辑像素；由“弹丸表现”节点导出。</summary>
        public float? ProjectileVisualOffsetXPixels => ProjectileVisualOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(ProjectileVisualOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>弹丸表现纵向偏移，逻辑像素；由“弹丸表现”节点导出。</summary>
        public float? ProjectileVisualOffsetYPixels => ProjectileVisualOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(ProjectileVisualOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>弹丸表现相对缩放；由千分比还原。</summary>
        public float? ProjectileVisualScale => ProjectileVisualScalePermille.HasValue ? ConfigNumber.Decode(ProjectileVisualScalePermille.Value) : (float?)null;
        /// <summary>命中特效表现横向偏移，逻辑像素；由“命中特效表现”节点导出。</summary>
        public float? ImpactVisualOffsetXPixels => ImpactVisualOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(ImpactVisualOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效表现纵向偏移，逻辑像素；由“命中特效表现”节点导出。</summary>
        public float? ImpactVisualOffsetYPixels => ImpactVisualOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(ImpactVisualOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效表现相对缩放；由千分比还原。</summary>
        public float? ImpactVisualScale => ImpactVisualScalePermille.HasValue ? ConfigNumber.Decode(ImpactVisualScalePermille.Value) : (float?)null;
        /// <summary>范围特效表现横向偏移，逻辑像素；由“范围特效表现”节点导出。</summary>
        public float? AreaVisualOffsetXPixels => AreaVisualOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(AreaVisualOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>范围特效表现纵向偏移，逻辑像素；由“范围特效表现”节点导出。</summary>
        public float? AreaVisualOffsetYPixels => AreaVisualOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(AreaVisualOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>范围特效表现相对缩放；由千分比还原。</summary>
        public float? AreaVisualScale => AreaVisualScalePermille.HasValue ? ConfigNumber.Decode(AreaVisualScalePermille.Value) : (float?)null;
    }

    /// <summary>SkillCombatConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class SkillCombatConfig
    {
        /// <summary>基础攻击间隔，秒 读取 SkillCombatConfig.baseIntervalMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float BaseInterval => ConfigNumber.Decode(BaseIntervalMilli);
        /// <summary>攻击范围，按碰撞体边缘，世界单位 读取 SkillCombatConfig.rangeMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float Range => ConfigNumber.Decode(RangeMilli);
        /// <summary>弹丸速度，世界单位每秒 读取 SkillCombatConfig.projectileSpeedMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? ProjectileSpeed => ProjectileSpeedMilli.HasValue ? ConfigNumber.Decode(ProjectileSpeedMilli.Value) : (float?)null;
        /// <summary>弹丸寿命，秒 读取 SkillCombatConfig.projectileLifetimeMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? ProjectileLifetime => ProjectileLifetimeMilli.HasValue ? ConfigNumber.Decode(ProjectileLifetimeMilli.Value) : (float?)null;
        /// <summary>普通近战 zd→gj 前摇秒数；弹丸留空 读取 SkillCombatConfig.attackWindupSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? AttackWindupSeconds => AttackWindupSecondsMilli.HasValue ? ConfigNumber.Decode(AttackWindupSecondsMilli.Value) : (float?)null;
    }

    /// <summary>CharacterConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class CharacterConfig
    {
        /// <summary>受击判定半径，逻辑像素；由角色预制体导出。</summary>
        public float? BodyRadiusPixels => BodyRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(BodyRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>受击判定中心横向偏移，逻辑像素；由角色预制体导出。</summary>
        public float? BodyOffsetXPixels => BodyOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(BodyOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>受击判定中心前向偏移，逻辑像素；由角色预制体导出。</summary>
        public float? BodyOffsetYPixels => BodyOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(BodyOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效挂点横向偏移，逻辑像素；由角色预制体导出。</summary>
        public float? HitEffectOffsetXPixels => HitEffectOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(HitEffectOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>命中特效挂点纵向偏移，逻辑像素；由角色预制体导出。</summary>
        public float? HitEffectOffsetYPixels => HitEffectOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(HitEffectOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>移动圆柱半径，逻辑像素；由预制体导出 读取 CharacterConfig.moveRadiusPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveRadiusPixels => MoveRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(MoveRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>移动圆柱高度，逻辑像素；由预制体导出 读取 CharacterConfig.moveHeightPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveHeightPixels => MoveHeightPixelsMilli.HasValue ? ConfigNumber.Decode(MoveHeightPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底心横向偏移，逻辑像素 读取 CharacterConfig.moveOffsetXPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveOffsetXPixels => MoveOffsetXPixelsMilli.HasValue ? ConfigNumber.Decode(MoveOffsetXPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底心地面纵向偏移，逻辑像素 读取 CharacterConfig.moveOffsetYPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveOffsetYPixels => MoveOffsetYPixelsMilli.HasValue ? ConfigNumber.Decode(MoveOffsetYPixelsMilli.Value) : (float?)null;
        /// <summary>圆柱底面离地高度，逻辑像素 读取 CharacterConfig.moveElevationPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? MoveElevationPixels => MoveElevationPixelsMilli.HasValue ? ConfigNumber.Decode(MoveElevationPixelsMilli.Value) : (float?)null;
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
        /// <summary>实体水平间距 读取 PerformanceScenarioConfig.horizontalSpacingMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float HorizontalSpacing => ConfigNumber.Decode(HorizontalSpacingMilli);
        /// <summary>实体垂直间距 读取 PerformanceScenarioConfig.verticalSpacingMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float VerticalSpacing => ConfigNumber.Decode(VerticalSpacingMilli);
        /// <summary>测试实体渲染缩放 读取 PerformanceScenarioConfig.entityScaleMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float EntityScale => ConfigNumber.Decode(EntityScaleMilli);
        /// <summary>实体往复移动振幅 读取 PerformanceScenarioConfig.moveAmplitudeMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MoveAmplitude => ConfigNumber.Decode(MoveAmplitudeMilli);
        /// <summary>实体最小移动角速度 读取 PerformanceScenarioConfig.moveSpeedMinMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MoveSpeedMin => ConfigNumber.Decode(MoveSpeedMinMilli);
        /// <summary>实体最大移动角速度 读取 PerformanceScenarioConfig.moveSpeedMaxMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float MoveSpeedMax => ConfigNumber.Decode(MoveSpeedMaxMilli);
        /// <summary>正交相机边缘留白 读取 PerformanceScenarioConfig.cameraPaddingMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float CameraPadding => ConfigNumber.Decode(CameraPaddingMilli);
        /// <summary>开放场地半宽，世界单位 读取 PerformanceScenarioConfig.arenaHalfWidthMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? ArenaHalfWidth => ArenaHalfWidthMilli.HasValue ? ConfigNumber.Decode(ArenaHalfWidthMilli.Value) : (float?)null;
        /// <summary>开放场地半高，世界单位 读取 PerformanceScenarioConfig.arenaHalfHeightMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? ArenaHalfHeight => ArenaHalfHeightMilli.HasValue ? ConfigNumber.Decode(ArenaHalfHeightMilli.Value) : (float?)null;
        /// <summary>玩家出生X 读取 PerformanceScenarioConfig.playerStartXMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? PlayerStartX => PlayerStartXMilli.HasValue ? ConfigNumber.Decode(PlayerStartXMilli.Value) : (float?)null;
        /// <summary>玩家出生Y 读取 PerformanceScenarioConfig.playerStartYMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? PlayerStartY => PlayerStartYMilli.HasValue ? ConfigNumber.Decode(PlayerStartYMilli.Value) : (float?)null;
        /// <summary>跟随角色圆周出生半径，逻辑像素；空表示旧阵列 读取 PerformanceScenarioConfig.spawnRadiusPixelsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? SpawnRadiusPixels => SpawnRadiusPixelsMilli.HasValue ? ConfigNumber.Decode(SpawnRadiusPixelsMilli.Value) : (float?)null;
        /// <summary>波间等待秒数；上一波最后一只之后计时，首波同样等待 读取 PerformanceScenarioConfig.spawnIntervalSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? SpawnIntervalSeconds => SpawnIntervalSecondsMilli.HasValue ? ConfigNumber.Decode(SpawnIntervalSecondsMilli.Value) : (float?)null;
        /// <summary>波内每只生成间隔，模拟秒 读取 PerformanceScenarioConfig.spawnUnitIntervalSecondsMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float? SpawnUnitIntervalSeconds => SpawnUnitIntervalSecondsMilli.HasValue ? ConfigNumber.Decode(SpawnUnitIntervalSecondsMilli.Value) : (float?)null;
    }

    /// <summary>CombatPresentationConfig 的统一单位换算入口；原始整数保留在 Luban 生成字段中，不维护第二套默认配置。</summary>
    public sealed partial class CombatPresentationConfig
    {
        /// <summary>调试面板像素宽 读取 CombatPresentationConfig.panelWidthMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float PanelWidth => ConfigNumber.Decode(PanelWidthMilli);
        /// <summary>调试面板像素高 读取 CombatPresentationConfig.panelHeightMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float PanelHeight => ConfigNumber.Decode(PanelHeightMilli);
        /// <summary>相机距XY平面距离 读取 CombatPresentationConfig.cameraDepthMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float CameraDepth => ConfigNumber.Decode(CameraDepthMilli);
        /// <summary>相机近裁面 读取 CombatPresentationConfig.cameraNearMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float CameraNear => ConfigNumber.Decode(CameraNearMilli);
        /// <summary>相机远裁面 读取 CombatPresentationConfig.cameraFarMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float CameraFar => ConfigNumber.Decode(CameraFarMilli);
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
        /// <summary>空间网格边长，世界单位 读取 CombatRulesConfig.spatialCellSizeMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float SpatialCellSize => ConfigNumber.Decode(SpatialCellSizeMilli);
        /// <summary>资源像素转换为世界单位的比例 读取 CombatRulesConfig.worldUnitsPerPixelMilli，除以 ConfigNumber.Scale 还原原单位。</summary>
        public float WorldUnitsPerPixel => ConfigNumber.Decode(WorldUnitsPerPixelMilli);
    }
}
