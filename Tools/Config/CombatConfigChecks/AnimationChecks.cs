using System;
using System.IO;
using System.Linq;
using cfg;
using ProjectX.Migration;
using Roguelike.Features.Combat;

/// <summary>只读验证真实角色 ANI、表现外键、方向映射与时序边界。</summary>
internal static class AnimationChecks
{
    private static int count;

    /// <summary>命令行回归时从生成 bytes 及其同级 StreamingAssets 加载当前战斗片段。</summary>
    /// <param name="tables">已解析外键的真实配置。</param>
    /// <param name="bytesDirectory">Config/Luban 数据目录。</param>
    /// <returns>通过的断言数量。</returns>
    /// <exception cref="InvalidOperationException">业务配置或采样回归失败。</exception>
    internal static int Run(Tables tables, string bytesDirectory)
    {
        count = 0;
        string streaming = Path.GetFullPath(Path.Combine(bytesDirectory, "../.."));
        var config = tables.TbCombatPresentation.Get(1);
        Check(!string.IsNullOrWhiteSpace(config.ShaderName), "Configured shader is required");
        Check(config.TextureFilterMode == "Point" || config.TextureFilterMode == "Bilinear", "Supported configured texture filter");
        Check(!config.MonsterHoldMoveFrame, "Default combat uses real zd idle, not a frozen move frame");
        foreach (var visual in tables.TbVisualSet.DataList.Where(v => v.Category == EVisualCategory.Character || v.Category == EVisualCategory.Monster))
        {
            string idleSuffix = visual.Category == EVisualCategory.Character ? "fd" : "zd";
            var stand = visual.ClipIds_Ref.SingleOrDefault(c => c.SourceSuffix == idleSuffix);
            Check(visual.StandClipId == stand?.Id, "Idle follows actor category");
            Check(visual.Category != EVisualCategory.Character || visual.AttackClipId == null, "Hero has no attack animation");
            if (visual.AttackClipId != null)
                Check(visual.ClipIds.Contains(visual.AttackClipId.Value) && visual.AttackClipId_Ref.Action == EAnimationAction.Attack && !visual.AttackClipId_Ref.Loop, "Explicit attack reference belongs to visual set");
            foreach (var clip in visual.ClipIds_Ref)
            {
                EAnimationAction? expected = clip.SourceSuffix switch {
                    "zd" => visual.Category == EVisualCategory.Monster ? EAnimationAction.Stand : EAnimationAction.Unused,
                    "fd" => EAnimationAction.Stand,
                    "pb" => EAnimationAction.Move, "gj" => visual.AttackClipId == clip.Id ? EAnimationAction.Attack : EAnimationAction.Unused,
                    "bj" => EAnimationAction.Unused, "sw" => EAnimationAction.Death,
                    "sf1" => EAnimationAction.Unused, "sf2" => EAnimationAction.Unused,
                    "sf3" => EAnimationAction.Unused, "sf4" => EAnimationAction.Unused, _ => null };
                if (expected == null) continue;
                Check(clip.Action == expected, "Unified actor suffix semantics");
                Check(clip.Loop == (expected == EAnimationAction.Stand || expected == EAnimationAction.Move), "Actor loop policy");
            }
        }
        foreach (var scenario in tables.TbPerformanceScenario.DataList)
            Check(scenario.Kind != EPerformanceKind.Combat || scenario.PresentationId_Ref != null, "Combat presentation reference");
        int[] clips = { tables.TbCharacter.Get(10001).VisualSetId_Ref.StandClipId.Value,
            tables.TbCharacter.Get(10001).VisualSetId_Ref.MoveClipId.Value,
            tables.TbMonster.Get(10001).VisualSetId_Ref.StandClipId.Value,
            tables.TbMonster.Get(10001).VisualSetId_Ref.MoveClipId.Value };
        foreach (int id in clips)
        {
            var clip = tables.TbAnimationClip.Get(id);
            var data = CocosAniData.Parse(File.ReadAllBytes(Path.Combine(streaming, clip.AniResourceId_Ref.Path)));
            var animation = new CombatAnimation(data, config, clip.Loop);
            var directions = new CombatDirections(config, animation);
            Check(directions.Read(directions.InitialIndex).Id == config.InitialDirectionId, "Initial direction");
            for (int i = 0; i < config.DirectionIds_Ref.Count; i++)
            {
                var direction = config.DirectionIds_Ref[i];
                int selected = directions.Select(direction.X, direction.Y, directions.InitialIndex);
                Check(directions.Read(selected).Id == direction.Id, "Exact direction selection");
                Check(directions.Select(0, 0, selected) == selected, "Idle retains facing");
                Check(animation.Sample(direction.ActionIndex, 0) == animation.HoldFrame(direction.ActionIndex, 0), "Initial action frame");
            }
        }
        var monster = tables.TbMonster.Get(10001).VisualSetId_Ref;
        /// <summary>本次测试内按表引用加载真实片段，使用当前表现时钟构造采样器。</summary>
        /// <param name="clip">已解析资源引用的片段。</param>
        /// <returns>用于方向索引校验的生产采样器。</returns>
        CombatAnimation LoadAnimation(AnimationClipConfig clip) => new CombatAnimation(
            CocosAniData.Parse(File.ReadAllBytes(Path.Combine(streaming, clip.AniResourceId_Ref.Path))), config, clip.Loop);
        var attackDirections = new CombatAttackDirections(config, LoadAnimation(monster.AttackClipId_Ref),
            LoadAnimation(monster.StandClipId_Ref), LoadAnimation(monster.MoveClipId_Ref));
        var attackOptions = CombatAttackOption.Build(monster, config, CocosAniData.Parse(
            File.ReadAllBytes(Path.Combine(streaming, monster.AttackClipId_Ref.AniResourceId_Ref.Path))));
        var attackAnimation = LoadAnimation(monster.AttackClipId_Ref);
        foreach (var option in attackOptions)
        {
            var map = tables.TbAttackDirection.Get(option.MappingId);
            Check(map.HitFrameIndex == 4, "Confirmed zero-based F4");
            Check(option.HitSeconds == attackAnimation.FrameStartSeconds(map.AttackActionIndex, map.HitFrameIndex), "Hit time from actual ANI");
            Check(option.DurationSeconds > option.HitSeconds, "Hit before animation completes");
        }
        foreach (var map in config.AttackDirectionIds_Ref)
        {
            var facing = map.FacingDirectionId_Ref;
            int selected = attackDirections.Select(facing.X, facing.Y, 0);
            Check(attackDirections.Read(selected).Id == map.Id, "Attack diagonal selects configured mapping");
            Check(attackDirections.Select(0, 0, selected) == selected, "Coincident target retains attack facing");
        }
        Check(attackDirections.Read(attackDirections.Select(0, -1, 0)).Id == config.AttackDirectionIds[0], "Equal-angle tie follows table order");
        Reject(() => attackDirections.Select(float.NaN, 0, 0));
        Reject(() => attackDirections.Read(-1));
        var fixture = new CocosAniData { frames = new CocosAniData.Frame[3], actions = new[] {
            new CocosAniData.Action { frames = new byte[] { 2, 0, 1 }, durations = new byte[] { 0, 1, 3 } } } };
        var looping = new CombatAnimation(fixture, config, true);
        var once = new CombatAnimation(fixture, config, false);
        double first = (double)config.MinDurationUnits / config.SourceTickHz;
        double second = (double)(config.MinDurationUnits + Math.Max(config.MinDurationUnits, config.DurationOneUnits)) / config.SourceTickHz;
        double total = (double)(config.MinDurationUnits + Math.Max(config.MinDurationUnits, config.DurationOneUnits) + Math.Max(config.MinDurationUnits, 3)) / config.SourceTickHz;
        Check(looping.Sample(0, 0) == 2, "First frame");
        Check(looping.Sample(0, first) == 0, "Exact first boundary");
        Check(looping.Sample(0, second) == 1, "Exact second boundary");
        Check(looping.Sample(0, total) == 2, "Exact loop boundary");
        Check(once.Sample(0, total * 100) == 1, "Nonloop holds last frame");
        fixture.actions[0].frames[0] = 1;
        Check(looping.Sample(0, 0) == 2, "Loaded frame snapshot is independent");
        Reject(() => looping.Sample(0, double.NaN));
        Reject(() => looping.Sample(0, double.PositiveInfinity));
        Reject(() => looping.Sample(0, -1));
        Reject(() => looping.Sample(-1, 0));
        Reject(() => looping.HoldFrame(0, 3));
        Reject(() => looping.FrameStartSeconds(0, -1));
        Reject(() => looping.FrameStartSeconds(0, 3));
        Check(looping.FrameStartSeconds(0, 1) == first, "Frame entry uses cumulative durations");
        Check(looping.DurationSeconds(0) == total, "Duration includes last frame");
        for (int i = 0; i < 1000; i++) looping.Sample(0, i * first);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) looping.Sample(0, i * first);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Sampling allocates zero managed bytes after warmup");
        Console.WriteLine($"ANIMATION_CHECKS_PASS checks={count} clips={clips.Length}");
        return count;
    }

    /// <summary>断言采样行为符合配置；不修改运行时状态。</summary>
    /// <param name="condition">期望为真的条件。</param>
    /// <param name="message">失败时的定位信息。</param>
    /// <exception cref="InvalidOperationException">断言不成立。</exception>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        count++;
    }

    /// <summary>边界回归时确保非法索引与时间被明确拒绝。</summary>
    /// <param name="action">包含非法输入的测试操作。</param>
    /// <exception cref="InvalidOperationException">生产代码未拒绝输入。</exception>
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { count++; return; }
        throw new InvalidOperationException("Invalid animation input accepted.");
    }
}
