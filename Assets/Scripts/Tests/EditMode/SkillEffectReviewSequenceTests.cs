using System;
using System.Linq;
using cfg;
using NUnit.Framework;
using Roguelike.Features.Combat.Authoring.Editor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Roguelike.Tests
{
    /// <summary>验证审核组合的阶段边界、落点锚点和内存生命周期，不写正式审核记录。</summary>
    public sealed class SkillEffectReviewSequenceTests
    {
        /// <summary>测试准备时从当前 Luban bytes 读取指定技能组并建立独立内存组合。</summary>
        /// <param name="id">正式 TbSkill.id。</param><param name="suffix">正式文件名中的完整后缀。</param>
        /// <param name="combined">是否组合演示。</param><param name="extraFlip">审核中的飞行额外翻转。</param>
        /// <returns>由用例负责释放的组合播放器。</returns>
        /// <remarks>不创建场景或持久资产；测试结束必须 Dispose。</remarks>
        private static SkillEffectReviewSequence Load(int id, string suffix, bool combined = true, bool extraFlip = false)
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var skill = tables.TbSkill.Get(id);
            var presentation = tables.TbPerformanceScenario.DataList.First(row => row.Kind == EPerformanceKind.Combat).PresentationId_Ref;
            var selected = skill.VisualSetId_Ref.ClipIds_Ref.First(clip => SkillEffectReviewSequence.ReadSuffix(clip) == suffix);
            var sequence = new SkillEffectReviewSequence();
            sequence.Load(skill, selected, presentation, 0, default(Scene), 0, combined, extraFlip);
            return sequence;
        }

        /// <summary>验证飞行从发射即开始且恰好演示两秒，在命中边界衔接 h 首帧，保持同一落点与取景。</summary>
        [Test]
        public void FlightLastsTwoSecondsThenImpactStartsFromFirstFrame()
        {
            using (var sequence = Load(20001, "f"))
            {
                Assert.That(sequence.Error, Is.Null);
                var flight = sequence.Stages.First(stage => stage.Clip.SourceSuffix == "f");
                var hit = sequence.Stages.First(stage => stage.Clip.SourceSuffix == "h");
                Assert.That(flight.PlaySeconds, Is.EqualTo(2d));
                Assert.That(flight.StartSeconds, Is.Zero);
                Assert.That(hit.StartSeconds - flight.StartSeconds, Is.EqualTo(2d).Within(1e-12));
                var view = sequence.ViewBounds;
                sequence.Sample(flight.StartSeconds + 1d);
                Assert.That(flight.Visible, Is.True); Assert.That(hit.Visible, Is.False);
                Assert.That(flight.CurrentOffset.x, Is.GreaterThan(flight.FromOffset.x).And.LessThan(0f));
                sequence.Sample(hit.StartSeconds);
                Assert.That(flight.Visible, Is.False); Assert.That(hit.Visible, Is.True);
                Assert.That(hit.Player.FrameIndex, Is.Zero); Assert.That(hit.CurrentOffset, Is.EqualTo(flight.ToOffset));
                Assert.That(sequence.ViewBounds, Is.EqualTo(view));
            }
        }

        /// <summary>验证真实落点 h 自身下降，在触地时启动 s 的地面爆炸，两层同时保持原始零点而非分别居中。</summary>
        [Test]
        public void LandingStartsGroundExplosionAtNativeContactAndPreservesAnchor()
        {
            using (var sequence = Load(20003, "s"))
            {
                Assert.That(sequence.Error, Is.Null); Assert.That(sequence.Stages.Count, Is.EqualTo(2));
                var fall = sequence.Stages.First(stage => stage.Clip.SourceSuffix == "h");
                var ground = sequence.Stages.First(stage => stage.Clip.SourceSuffix == "s");
                Assert.That(fall.Player.GroundContactSeconds, Is.GreaterThan(0d));
                Assert.That(ground.StartSeconds, Is.EqualTo(fall.Player.GroundContactSeconds));
                Assert.That(ground.StartSeconds, Is.LessThan(fall.EndSeconds));
                sequence.Sample(ground.StartSeconds * .5d);
                Assert.That(fall.Visible, Is.True); Assert.That(ground.Visible, Is.False);
                sequence.Sample(ground.StartSeconds);
                Assert.That(fall.Visible, Is.True); Assert.That(ground.Visible, Is.True);
                Assert.That(ground.Player.FrameIndex, Is.Zero);
                Assert.That(fall.CurrentOffset, Is.EqualTo(Vector2.zero));
                Assert.That(ground.CurrentOffset, Is.EqualTo(Vector2.zero));
                Assert.That(sequence.ViewBounds.Contains(fall.Player.GeometryBounds.max), Is.True);
                Assert.That(sequence.ViewBounds.Contains(ground.Player.GeometryBounds.min), Is.True);
                Assert.That(sequence.Duration, Is.EqualTo(Math.Max(fall.EndSeconds, ground.EndSeconds)));
            }
        }

        /// <summary>验证随身 s 与 f 同时开始，角色移动时 s 跟随施法者，弹丸独立移动而无需等待施法动画。</summary>
        [Test]
        public void CastFollowsMovingCasterWithoutBlockingProjectileLaunch()
        {
            using (var sequence = Load(20001, "f"))
            {
                Assert.That(sequence.Error, Is.Null);
                var cast = sequence.Stages.First(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip) == "s");
                var flight = sequence.Stages.First(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip) == "f");
                Assert.That(cast.FollowsCaster, Is.True); Assert.That(flight.FollowsCaster, Is.False);
                Assert.That(cast.StartSeconds, Is.Zero); Assert.That(flight.StartSeconds, Is.Zero);
                sequence.Sample(0);
                Assert.That(cast.Visible, Is.True); Assert.That(flight.Visible, Is.True);
                var casterStart = sequence.CasterOffset;
                sequence.Sample(Math.Min(cast.PlaySeconds * .5d, .5d));
                Assert.That(cast.CurrentOffset, Is.EqualTo(sequence.CasterOffset));
                Assert.That(sequence.CasterOffset, Is.Not.EqualTo(casterStart));
                Assert.That(cast.CurrentOffset, Is.Not.EqualTo(flight.CurrentOffset));
                Assert.That(sequence.Stages.First(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip) == "h").StartSeconds, Is.EqualTo(2d));
            }
        }

        /// <summary>验证落地爆炸 s 保持世界落点，不被随身施法规则错误绑定到施法者。</summary>
        [Test]
        public void GroundExplosionStaysAtImpactPointInsteadOfFollowingCaster()
        {
            using (var sequence = Load(20003, "s"))
            {
                var ground = sequence.Stages.First(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip) == "s");
                sequence.Sample(ground.StartSeconds + ground.PlaySeconds * .5d);
                Assert.That(ground.FollowsCaster, Is.False);
                Assert.That(ground.CurrentOffset, Is.EqualTo(Vector2.zero));
                Assert.That(sequence.CasterOffset, Is.EqualTo(Vector2.zero));
            }
        }

        /// <summary>验证分段检查仍只加载所选片段，不被默认组合覆盖。</summary>
        [Test]
        public void SingleClipModeStillDisplaysOnlySelectedSuffix()
        {
            using (var sequence = Load(20003, "s", false))
            {
                Assert.That(sequence.Error, Is.Null);
                Assert.That(sequence.Stages.Count, Is.EqualTo(1));
                Assert.That(sequence.CurrentClip.SourceSuffix, Is.EqualTo("s"));
                Assert.That(sequence.Stages[0].Loop, Is.True);
            }
        }

        /// <summary>验证左右变体只配相同后缀，缺少精确配对时不随意连接相反方向资源。</summary>
        [Test]
        public void DirectionVariantsNeverUseOppositeDirectionGroundEffect()
        {
            using (var left = Load(20009, "h_l"))
            using (var detail = Load(20009, "h_l_d"))
            {
                Assert.That(left.Error, Is.Null); Assert.That(detail.Error, Is.Null);
                Assert.That(left.Stages.Select(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip)), Is.EqualTo(new[] { "h_l", "s_l" }));
                Assert.That(detail.Stages.Select(stage => SkillEffectReviewSequence.ReadSuffix(stage.Clip)), Is.EqualTo(new[] { "h_l_d" }));
            }
        }

        /// <summary>验证暂停时重复采样同一时刻保持帧、阶段和位置，整轮结束从最初阶段重播。</summary>
        [Test]
        public void SameTimeSampleIsStableAndFullCycleRestarts()
        {
            using (var sequence = Load(20001, "f"))
            {
                var flight = sequence.Stages.First(stage => stage.Clip.SourceSuffix == "f");
                double time = flight.StartSeconds + .8d;
                sequence.Sample(time); int frame = sequence.FrameIndex; var offset = flight.CurrentOffset;
                sequence.Sample(time);
                Assert.That(sequence.FrameIndex, Is.EqualTo(frame)); Assert.That(flight.CurrentOffset, Is.EqualTo(offset));
                sequence.Sample(sequence.Duration);
                Assert.That(sequence.Elapsed, Is.Zero); Assert.That(sequence.Stages[0].Visible, Is.True);
                Assert.That(sequence.Stages[0].Player.FrameIndex, Is.Zero);
            }
        }

        /// <summary>验证加载后多次推进阶段不重新生成纹理，释放组合后所有独占纹理回到原数量。</summary>
        [Test]
        public void SamplingDoesNotCreateTexturesAndDisposeReleasesAllStages()
        {
            int before = Resources.FindObjectsOfTypeAll<Texture2D>().Count(texture => texture.name == "SkillReview_MemoryTexture");
            using (var sequence = Load(20001, "f"))
            {
                int loaded = Resources.FindObjectsOfTypeAll<Texture2D>().Count(texture => texture.name == "SkillReview_MemoryTexture");
                Assert.That(loaded - before, Is.EqualTo(sequence.Stages.Count));
                for (int index = 0; index < 120; index++) sequence.Sample(index * .1d);
                Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Count(texture => texture.name == "SkillReview_MemoryTexture"), Is.EqualTo(loaded));
            }
            Assert.That(Resources.FindObjectsOfTypeAll<Texture2D>().Count(texture => texture.name == "SkillReview_MemoryTexture"), Is.EqualTo(before));
        }

        /// <summary>验证 skill_32 的审核修正只镜像飞行段，起手和命中保持正式配置，原图与表引用不被改写。</summary>
        [Test]
        public void FlightDirectionCorrectionMirrorsOnlyProjectile()
        {
            using (var original = Load(20032, "f"))
            using (var corrected = Load(20032, "f", true, true))
            {
                Assert.That(original.Error, Is.Null); Assert.That(corrected.Error, Is.Null);
                foreach (var stage in corrected.Stages)
                {
                    var source = original.Stages.First(item => item.Clip.Id == stage.Clip.Id);
                    bool flight = SkillEffectReviewSequence.ReadSuffix(stage.Clip) == "f";
                    Assert.That(stage.Player.AppliedFlipX, Is.EqualTo(source.Player.AppliedFlipX ^ flight));
                    Assert.That(stage.Clip.FlipX, Is.EqualTo(source.Clip.FlipX));
                    if (flight)
                    {
                        Assert.That(stage.Player.GeometryBounds.center.x, Is.EqualTo(-source.Player.GeometryBounds.center.x).Within(.001f));
                        Assert.That(stage.Player.GeometryBounds.size, Is.EqualTo(source.Player.GeometryBounds.size));
                    }
                }
            }
        }

        /// <summary>验证单段查看与组合播放消费同一飞行方向修正，不出现切到单段后再次反向。</summary>
        [Test]
        public void SingleFlightViewUsesTheSameDirectionCorrection()
        {
            using (var sequence = Load(20032, "f", false, true))
            {
                Assert.That(sequence.Error, Is.Null);
                Assert.That(sequence.Stages.Count, Is.EqualTo(1));
                Assert.That(sequence.Stages[0].Player.AppliedFlipX, Is.EqualTo(!sequence.Stages[0].Clip.FlipX));
            }
        }

        /// <summary>验证审核明确选择最终镜像时不受正式 flipX 更新影响，原始方向也能显式覆盖正式镜像。</summary>
        [Test]
        public void ExplicitFinalFacingDoesNotDoubleFlipFormalConfiguration()
        {
            var tables = CombatCylinderPipeline.ReadTables(); var skill = tables.TbSkill.Get(20032);
            var clip = skill.ProjectileClipId_Ref;
            var presentation = tables.TbPerformanceScenario.DataList.First(row => row.Kind == EPerformanceKind.Combat).PresentationId_Ref;
            using (var mirrored = new SkillEffectReviewSequence())
            using (var original = new SkillEffectReviewSequence())
            {
                mirrored.Load(skill, clip, presentation, 0, default(Scene), 0, false, false, 1);
                original.Load(skill, clip, presentation, 0, default(Scene), 0, false, false, 2);
                Assert.That(mirrored.Stages[0].Player.AppliedFlipX, Is.True);
                Assert.That(original.Stages[0].Player.AppliedFlipX, Is.False);
            }
        }

        /// <summary>验证当前全部正式技能组均可装载默认组合，单段与未知后缀保留可查看的原始动画。</summary>
        [Test]
        public void EveryFormalGroupLoadsItsDefaultSequence()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            var presentation = tables.TbPerformanceScenario.DataList.First(row => row.Kind == EPerformanceKind.Combat).PresentationId_Ref;
            foreach (var skill in tables.TbSkill.DataList)
                using (var sequence = new SkillEffectReviewSequence())
                {
                    sequence.Load(skill, skill.VisualSetId_Ref.ClipIds_Ref[0], presentation, 0, default(Scene), 0, true);
                    Assert.That(sequence.Error, Is.Null, "Skill " + skill.Id);
                    Assert.That(sequence.Duration, Is.GreaterThan(0d));
                    sequence.Sample(sequence.Duration * .5d);
                    Assert.That(sequence.Stages.Any(stage => stage.Visible), Is.True, "Skill " + skill.Id);
                }
        }
    }
}
