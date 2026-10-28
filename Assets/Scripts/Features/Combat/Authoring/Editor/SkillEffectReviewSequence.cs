using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using cfg;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>只在编辑器审核中存在的一段组合播放及其独占内存资源。</summary>
    public sealed class SkillEffectReviewStage
    {
        public AnimationClipConfig Clip;
        public SkillEffectReviewPlayback Player;
        public string Label;
        public double StartSeconds;
        public double PlaySeconds;
        public bool Loop;
        public Vector2 FromOffset;
        public Vector2 ToOffset;
        public Vector2 CurrentOffset;
        public bool Visible;
        public bool FollowsCaster;
        public double EndSeconds => StartSeconds + PlaySeconds;
    }

    /// <summary>技能组的编辑器组合演示：随身施法与飞行同时开始，或下降衔接地面爆炸，共用原始锚点与取景。</summary>
    public sealed class SkillEffectReviewSequence : IDisposable
    {
        /// <summary>用户指定的审核飞行演示时长，不导出到任何正式技能配置。</summary>
        public const double DefaultFlightSeconds = 2d;
        private readonly List<SkillEffectReviewStage> stages = new List<SkillEffectReviewStage>();
        private SkillEffectReviewStage current;
        private SkillEffectReviewStage castStage;
        private CombatPresentationConfig presentation;
        private Scene scene;
        private int slot;
        private int actionIndex;
        private bool flipProjectile;
        private int projectileFacingMode;
        public IReadOnlyList<SkillEffectReviewStage> Stages => stages;
        public Bounds ViewBounds { get; private set; }
        public double Duration { get; private set; }
        public string Summary { get; private set; }
        public string Error { get; private set; }
        public double Elapsed { get; private set; }
        public AnimationClipConfig CurrentClip => current?.Clip;
        public int FrameIndex => current?.Player.FrameIndex ?? 0;
        public int FrameCount => current?.Player.FrameCount ?? 0;
        public int ActionCount => stages.Count == 0 ? 0 : stages.Max(stage => stage.Player.ActionCount);
        public string StageLabel => current?.Label ?? "准备播放";
        public Vector2 CasterOffset { get; private set; }

        /// <summary>当前页或演示方式切换时一次加载组合需要的帧；之后切阶段不再读盘或创建资源。</summary>
        /// <param name="skill">TbSkill 当前技能组，资源关系来自 TbVisualSet 与 TbAnimationClip。</param>
        /// <param name="selected">分段查看或选择同方向变体所用片段。</param>
        /// <param name="config">TbCombatPresentation 的正式帧时钟与 Shader。</param>
        /// <param name="action">ANI 动作索引。</param><param name="targetScene">审核场景；无效时只在窗口绘制。</param>
        /// <param name="targetSlot">四格中的位置。</param><param name="combined">是否连贯组合演示。</param>
        /// <param name="extraProjectileFlip">人工审核记录的飞行额外水平翻转，仅 f 段使用，不影响起手或命中。</param>
        /// <param name="facingMode">最终飞行朝向：正式配置、水平镜像或原始方向；显式选项优先于额外翻转。</param>
        /// <remarks>临时资源由本实例独占并在 Dispose 时释放；组合规则只用于人工审核，不修改正式技能时序或分类。</remarks>
        public void Load(SkillConfig skill, AnimationClipConfig selected, CombatPresentationConfig config, int action,
            Scene targetScene, int targetSlot, bool combined, bool extraProjectileFlip = false, int facingMode = 0)
        {
            Dispose(); Error = null; presentation = config; scene = targetScene; slot = targetSlot; actionIndex = action;
            flipProjectile = extraProjectileFlip;
            projectileFacingMode = facingMode;
            try
            {
                var clips = skill.VisualSetId_Ref.ClipIds_Ref;
                string suffix = ReadSuffix(selected);
                int split = suffix.IndexOf('_');
                string variant = split < 0 ? "" : suffix.Substring(split);
                var flight = Find(clips, "f", variant);
                var impact = Find(clips, "h", variant);
                var groundOrCast = Find(clips, "s", variant);
                if (!combined)
                    Add(selected, "分段查看", 0, 0, true);
                else if (flight != null)
                {
                    if (groundOrCast != null)
                    {
                        castStage = Add(groundOrCast, "随身施法", 0, 0, false);
                        castStage.FollowsCaster = true;
                    }
                    var flying = Add(flight, "飞行 2 秒", 0, DefaultFlightSeconds, true);
                    if (impact != null) Add(impact, "命中", flying.EndSeconds, 0, false);
                    float distance = Math.Max(1f, stages.Max(stage => stage.Player.GeometryBounds.size.x)) * 1.5f;
                    flying.FromOffset = Vector2.left * distance; flying.ToOffset = Vector2.zero;
                    if (castStage != null)
                    {
                        // 施法者独立向下走动，以直观检查随身层；弹丸仍从发射瞬间位置独立飞向落点。
                        castStage.FromOffset = flying.FromOffset;
                        castStage.ToOffset = castStage.FromOffset + Vector2.down *
                            Math.Max(1f, stages.Max(stage => stage.Player.GeometryBounds.size.y)) * .35f;
                    }
                }
                else if (impact != null)
                {
                    var main = Add(impact, "主体 / 命中", 0, 0, false);
                    if (groundOrCast != null)
                    {
                        double contact = main.Player.GroundContactSeconds;
                        if (contact >= 0) main.Label = "下降 / 砸中";
                        Add(groundOrCast, contact >= 0 ? "落地爆炸" : "附加效果", contact >= 0 ? contact : main.EndSeconds, 0, false);
                    }
                }
                else Add(selected, "单段效果", 0, 0, false);
                Duration = stages.Max(stage => stage.EndSeconds);
                bool first = true;
                foreach (var stage in stages)
                    foreach (var offset in new[] { stage.FromOffset, stage.ToOffset })
                    {
                        Bounds bounds = stage.Player.GeometryBounds; bounds.center += (Vector3)offset;
                        if (first) { ViewBounds = bounds; first = false; }
                        else { var union = ViewBounds; union.Encapsulate(bounds); ViewBounds = union; }
                    }
                Summary = string.Join(" → ", stages.Where(stage => !stage.FollowsCaster).Select(stage => Path.GetFileNameWithoutExtension(stage.Clip.AniResourceId_Ref.Path) +
                    (stage.Label == "飞行 2 秒" ? "（飞行2秒）" : "")));
                if (castStage != null) Summary = Path.GetFileNameWithoutExtension(castStage.Clip.AniResourceId_Ref.Path) + "（随施法者，同时播放）＋ " + Summary;
                if (castStage == null && stages.Count > 1 && stages[1].StartSeconds < stages[0].EndSeconds)
                    Summary += " · 触地时衔接地面层";
                Sample(0);
            }
            catch (Exception exception) { Dispose(); Error = exception.Message; Summary = "组合装载失败"; }
        }

        /// <summary>从正式资源路径读取完整 f/h/s 及方向尾部，避免 SourceSuffix 仅保存末段而丢失方向信息。</summary>
        /// <param name="clip">TbAnimationClip 指向的正式 ANI。</param><returns>技能资源完整后缀；其他命名保留表中原后缀。</returns>
        public static string ReadSuffix(AnimationClipConfig clip)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(clip.AniResourceId_Ref.Path), @"^skill_\d+_(.+)$");
            return match.Success ? match.Groups[1].Value : clip.SourceSuffix ?? "";
        }

        /// <summary>依据正式文件名的完整后缀精确配对相同方向的 f/h/s；允许公共无方向段，拒绝随意接入其他方向。</summary>
        /// <param name="clips">当前技能组正式片段。</param><param name="role">指定的原始后缀类别。</param>
        /// <param name="variant">后缀中的方向或变体尾部。</param><returns>精确变体或公共片段；不存在时为空。</returns>
        private static AnimationClipConfig Find(IEnumerable<AnimationClipConfig> clips, string role, string variant) =>
            clips.FirstOrDefault(clip => ReadSuffix(clip) == role + variant) ?? clips.FirstOrDefault(clip => ReadSuffix(clip) == role);

        /// <summary>装载组合阶段，记录一次性或循环时长，并在失败时立即释放尚未交接所有权的资源。</summary>
        /// <param name="clip">正式片段。</param><param name="label">阶段说明。</param><param name="start">起始秒数。</param>
        /// <param name="seconds">演示时长；非正值使用 ANI 完整时长。</param><param name="loop">是否允许在该阶段内循环。</param>
        /// <returns>已由组合持有的阶段。</returns><exception cref="InvalidDataException">片段无法解码。</exception>
        /// <remarks>只在组或模式切换时创建资源，播放热路径不创建纹理、材质或网格。</remarks>
        private SkillEffectReviewStage Add(AnimationClipConfig clip, string label, double start, double seconds, bool loop)
        {
            var player = new SkillEffectReviewPlayback();
            string suffix = ReadSuffix(clip);
            bool flight = suffix == "f" || suffix.StartsWith("f_", StringComparison.Ordinal);
            bool extraFlip = flipProjectile && flight;
            if (flight && projectileFacingMode != 0) extraFlip = (projectileFacingMode == 1) ^ clip.FlipX;
            player.Load(clip, presentation, actionIndex, scene, slot, extraFlip);
            if (player.Error != null) { string message = player.Error; player.Dispose(); throw new InvalidDataException(message); }
            var stage = new SkillEffectReviewStage { Clip = clip, Player = player, Label = label,
                StartSeconds = start, PlaySeconds = seconds > 0 ? seconds : player.Duration, Loop = loop };
            stages.Add(stage); return stage;
        }

        /// <summary>编辑器时钟推进时同时采样随身施法和独立飞行，或在落点触地时启动地面层，命中只播一次。</summary>
        /// <param name="seconds">从本卡片重播起经过的秒数。</param>
        /// <remarks>随身层使用施法者位置，不阻挡发射，也不随弹丸移动；落地层保持世界落点。移动轨迹只用于审核演示。</remarks>
        public void Sample(double seconds)
        {
            if (Error != null || stages.Count == 0 || Duration <= 0) return;
            Elapsed = Math.Max(0, seconds) % Duration; current = null;
            CasterOffset = castStage == null ? Vector2.zero : Vector2.Lerp(castStage.FromOffset, castStage.ToOffset,
                Mathf.Clamp01((float)(Elapsed / DefaultFlightSeconds)));
            foreach (var stage in stages)
            {
                double age = Elapsed - stage.StartSeconds;
                // 使用同一绝对结束边界，避免浮点减法在飞行结束时把上一阶段多留一帧。
                stage.Visible = Elapsed >= stage.StartSeconds && Elapsed < stage.EndSeconds;
                if (stage.Visible)
                {
                    if (stage.Loop) stage.Player.Sample(age); else stage.Player.SampleOnce(age);
                    stage.CurrentOffset = stage.FollowsCaster ? CasterOffset :
                        Vector2.Lerp(stage.FromOffset, stage.ToOffset, (float)(age / stage.PlaySeconds));
                    current = stage;
                }
                stage.Player.SetSceneLayout(ViewBounds, stage.CurrentOffset, stage.Visible);
            }
        }

        /// <summary>窗口重绘时只清屏一次，按各层绑定位置绘制，并以十字标示移动中的施法者位置。</summary>
        /// <param name="rect">卡片画面区域。</param><param name="zoom">审核取景缩放。</param>
        public void Draw(Rect rect, float zoom)
        {
            EditorGUI.DrawRect(rect, new Color(.10f, .12f, .16f));
            if (Error != null) { GUI.Label(rect, "无法播放：" + Error, EditorStyles.wordWrappedLabel); return; }
            foreach (var stage in stages)
                if (stage.Visible) stage.Player.DrawLayer(rect, zoom, ViewBounds, stage.CurrentOffset);
            if (castStage != null)
            {
                float scale = Math.Min((rect.width - 20) / Math.Max(1f, ViewBounds.size.x),
                    (rect.height - 20) / Math.Max(1f, ViewBounds.size.y)) * zoom;
                float x = rect.center.x + (CasterOffset.x - ViewBounds.center.x) * scale;
                float y = rect.center.y - (CasterOffset.y - ViewBounds.center.y) * scale;
                GUI.BeginClip(rect);
                x -= rect.x; y -= rect.y;
                EditorGUI.DrawRect(new Rect(x - 5, y - 1, 10, 2), Color.yellow);
                EditorGUI.DrawRect(new Rect(x - 1, y - 5, 2, 10), Color.yellow);
                GUI.Label(new Rect(x + 8, y - 9, 65, 20), "施法者", EditorStyles.miniLabel);
                GUI.EndClip();
            }
        }

        /// <summary>离开卡片、关闭工具、Play 切换或域重载时释放所有阶段的独占内存。</summary>
        /// <remarks>不修改审核记录或正式资源；允许重复释放。</remarks>
        public void Dispose()
        {
            foreach (var stage in stages) stage.Player.Dispose();
            stages.Clear(); current = null; castStage = null; CasterOffset = Vector2.zero; Duration = 0; Elapsed = 0;
        }
    }
}
