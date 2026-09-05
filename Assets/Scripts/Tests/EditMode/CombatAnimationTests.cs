using System;
using System.IO;
using cfg;
using Luban;
using NUnit.Framework;
using ProjectX.Migration;
using Roguelike.Features.Combat;
using UnityEngine;

namespace Roguelike.Tests
{
    /// <summary>在 Unity Mono 环境校验真实 ANI 的表驱动时序和方向，不以此代替画面验收。</summary>
    public sealed class CombatAnimationTests
    {
        /// <summary>对实际站立、行走与怪物行走资源验证八方向和静止保留朝向。</summary>
        /// <param name="clipId">仅测试夹具使用的真实动画 ID。</param>
        /// <remarks>只读 StreamingAssets，不创建 Unity 对象。</remarks>
        [TestCase(10002)]
        [TestCase(10004)]
        [TestCase(20003)]
        [TestCase(20007)]
        public void RealClipDirectionsAndFrameBoundaries(int clipId)
        {
            var tables = LoadTables();
            var config = tables.TbCombatPresentation.Get(1);
            var clip = tables.TbAnimationClip.Get(clipId);
            var data = CocosAniData.Parse(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, clip.AniResourceId_Ref.Path)));
            var animation = new CombatAnimation(data, config, clip.Loop);
            var directions = new CombatDirections(config, animation);
            Assert.That(directions.Read(directions.InitialIndex).Id, Is.EqualTo(config.InitialDirectionId));
            foreach (var d in config.DirectionIds_Ref)
            {
                int chosen = directions.Select(d.X, d.Y, directions.InitialIndex);
                Assert.That(directions.Read(chosen).Id, Is.EqualTo(d.Id));
                Assert.That(directions.Select(0, 0, chosen), Is.EqualTo(chosen));
                var action = data.actions[d.ActionIndex];
                long units = 0;
                for (int f = 0; f < action.frames.Length; f++)
                {
                    Assert.That(animation.Sample(d.ActionIndex, (double)units / config.SourceTickHz), Is.EqualTo(action.frames[f]));
                    int raw = action.durations[f];
                    units += Math.Max(config.MinDurationUnits, raw == 1 ? config.DurationOneUnits : raw);
                }
                Assert.That(animation.Sample(d.ActionIndex, (double)units / config.SourceTickHz),
                    Is.EqualTo(clip.Loop ? action.frames[0] : action.frames[action.frames.Length - 1]));
            }
        }

        /// <summary>主角选 fd、怪物选 zd，均使用真实待机片段；主角 zd 不接入播放。</summary>
        [Test]
        public void DefaultIdleFollowsActorCategory()
        {
            var tables = LoadTables();
            foreach (var visual in new[] { tables.TbCharacter.Get(10001).VisualSetId_Ref, tables.TbMonster.Get(10001).VisualSetId_Ref })
            {
                Assert.That(visual.StandClipId_Ref.SourceSuffix, Is.EqualTo(visual.Category == EVisualCategory.Character ? "fd" : "zd"));
                Assert.That(visual.StandClipId_Ref.Action, Is.EqualTo(EAnimationAction.Stand));
                Assert.That(visual.StandClipId_Ref.Loop, Is.True);
            }
            Assert.That(tables.TbAnimationClip.Get(10002).Action, Is.EqualTo(EAnimationAction.Stand));
            Assert.That(tables.TbAnimationClip.Get(10009).Action, Is.EqualTo(EAnimationAction.Unused));
            Assert.That(tables.TbCombatPresentation.Get(1).MonsterHoldMoveFrame, Is.False);
        }

        /// <summary>合成 ANI 测试兼容单位、非循环末帧及非法输入；夹具数值不进入正式配置。</summary>
        [Test]
        public void CompatibilityDurationAndInvalidInputs()
        {
            var config = LoadTables().TbCombatPresentation.Get(1);
            var data = new CocosAniData { frames = new CocosAniData.Frame[2], actions = new[] {
                new CocosAniData.Action { frames = new byte[] { 0, 1 }, durations = new byte[] { 1, 0 } } } };
            var animation = new CombatAnimation(data, config, false);
            double edge = (double)Math.Max(config.DurationOneUnits, config.MinDurationUnits) / config.SourceTickHz;
            Assert.That(animation.Sample(0, 0), Is.EqualTo(0));
            Assert.That(animation.Sample(0, edge), Is.EqualTo(1));
            Assert.That(animation.Sample(0, double.MaxValue), Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => animation.Sample(0, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => animation.Sample(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => animation.HoldFrame(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CombatDirections(config, animation));
        }

        /// <summary>核对用户确认的四方向攻击与待机/移动对齐，攻击方向只在调用时选择。</summary>
        [Test]
        public void AttackDirectionsMatchConfirmedGroups()
        {
            var tables = LoadTables();
            var config = tables.TbCombatPresentation.Get(1);
            var visual = tables.TbMonster.Get(10001).VisualSetId_Ref;
            var resolver = new CombatAttackDirections(config, LoadAnimation(visual.AttackClipId_Ref, config),
                LoadAnimation(visual.StandClipId_Ref, config), LoadAnimation(visual.MoveClipId_Ref, config));
            float[] xs = { 1, -1, 1, -1 }, ys = { -1, -1, 1, 1 };
            int[] attackGroups = { 0, 0, 1, 1 }, alignedGroups = { 0, 0, 3, 3 };
            for (int i = 0; i < xs.Length; i++)
            {
                int selected = resolver.Select(xs[i], ys[i], 0);
                var map = resolver.Read(selected);
                Assert.That(map.AttackActionIndex, Is.EqualTo(attackGroups[i]));
                Assert.That(map.FacingDirectionId_Ref.ActionIndex, Is.EqualTo(alignedGroups[i]));
                Assert.That(map.FacingDirectionId_Ref.FlipX, Is.EqualTo(xs[i] < 0));
                Assert.That(resolver.Select(0, 0, selected), Is.EqualTo(selected));
            }
            Assert.That(tables.TbCharacter.Get(10001).VisualSetId_Ref.AttackClipId, Is.Null);
            Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Select(float.NaN, 0, 0));
        }

        /// <summary>修改内存中的表顺序即改变等角裁决；验证重复配置在加载期被拒绝。</summary>
        [Test]
        public void AttackTieOrderIsDataDrivenAndDuplicatesAreRejected()
        {
            var tables = LoadTables();
            var config = tables.TbCombatPresentation.Get(1);
            var visual = tables.TbMonster.Get(10001).VisualSetId_Ref;
            var attack = LoadAnimation(visual.AttackClipId_Ref, config);
            var idle = LoadAnimation(visual.StandClipId_Ref, config);
            var move = LoadAnimation(visual.MoveClipId_Ref, config);
            var resolver = new CombatAttackDirections(config, attack, idle, move);
            Assert.That(resolver.Read(resolver.Select(0, -1, 0)).FacingDirectionId_Ref.FlipX, Is.False);
            var first = config.AttackDirectionIds_Ref[0];
            config.AttackDirectionIds_Ref[0] = config.AttackDirectionIds_Ref[1];
            config.AttackDirectionIds_Ref[1] = first;
            resolver = new CombatAttackDirections(config, attack, idle, move);
            Assert.That(resolver.Read(resolver.Select(0, -1, 0)).FacingDirectionId_Ref.FlipX, Is.True);
            config.AttackDirectionIds_Ref[1] = config.AttackDirectionIds_Ref[0];
            Assert.Throws<ArgumentException>(() => new CombatAttackDirections(config, attack, idle, move));
        }

        /// <summary>按生成的资源引用只读加载测试动画，使用正式采样器验证动作索引。</summary>
        /// <param name="clip">待机、移动或普攻片段。</param>
        /// <param name="config">时序配置。</param>
        /// <returns>加载完成的动画采样器。</returns>
        /// <remarks>不修改源资源，不创建 Unity 对象。</remarks>
        private static CombatAnimation LoadAnimation(AnimationClipConfig clip, CombatPresentationConfig config)
        {
            return new CombatAnimation(CocosAniData.Parse(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath,
                clip.AniResourceId_Ref.Path))), config, clip.Loop);
        }

        /// <summary>每个测试独立读取生成 bytes，避免可变外键夹具污染其他用例。</summary>
        /// <returns>已解析外键的 Luban 配置。</returns>
        /// <remarks>只读本工程 StreamingAssets 下的生成配置。</remarks>
        private static Tables LoadTables()
        {
            return new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "Config", "Luban", name + ".bytes"))));
        }
    }
}
