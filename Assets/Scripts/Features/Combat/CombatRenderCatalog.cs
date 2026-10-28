using System;
using System.Collections.Generic;
using cfg;

namespace Roguelike.Features.Combat
{
    /// <summary>入局时将 TbCombatPresentation.renderConfigIds 的显式模型和技能片段绑定建立为只读索引。</summary>
    public sealed class CombatRenderCatalog
    {
        // Unity 的透明队列边界属于渲染接口常量，具体模型及特效队列必须来自 TbCombatRender。
        public const int TransparentQueueMinimum = 3000;
        public const int RenderQueueMaximum = 5000;

        private readonly Dictionary<int, CombatRenderConfig> models = new Dictionary<int, CombatRenderConfig>();
        private readonly Dictionary<(int Skill, int Clip), CombatRenderConfig> skills = new Dictionary<(int, int), CombatRenderConfig>();

        public int ModelCount => models.Count;
        public int SkillClipCount => skills.Count;

        /// <summary>资源加载前验证当前表现方案的全部强引用、归属、唯一性和队列，禁止缺值回退到全局层级。</summary>
        /// <param name="presentation">提供 renderConfigIds 和 feedbackRenderQueue 的正式 Luban 行。</param>
        /// <remarks>只保留本次加载快照和托管字典，不创建 Unity 对象、不修改配置或其他会话。</remarks>
        /// <exception cref="ArgumentNullException">缺少表现方案。</exception>
        /// <exception cref="InvalidOperationException">目录为空、重复、归属错误、片段角色错误或队列超出透明/反馈边界。</exception>
        public CombatRenderCatalog(CombatPresentationConfig presentation)
        {
            if (presentation == null) throw new ArgumentNullException(nameof(presentation));
            if (presentation.FeedbackRenderQueue <= TransparentQueueMinimum || presentation.FeedbackRenderQueue > RenderQueueMaximum)
                throw new InvalidOperationException("TbCombatPresentation.feedbackRenderQueue must be inside Unity's transparent queue range.");
            if (presentation.RenderConfigIds_Ref == null || presentation.RenderConfigIds_Ref.Count == 0)
                throw new InvalidOperationException("TbCombatPresentation.renderConfigIds requires explicit render bindings.");
            var ids = new HashSet<int>();
            foreach (CombatRenderConfig binding in presentation.RenderConfigIds_Ref)
            {
                if (binding == null || !ids.Add(binding.Id))
                    throw new InvalidOperationException("TbCombatPresentation.renderConfigIds contains an unresolved or repeated binding.");
                ValidateQueue(binding.RenderQueue, presentation.FeedbackRenderQueue);
                if (binding.VisualSetId.HasValue == binding.SkillId.HasValue)
                    throw new InvalidOperationException($"TbCombatRender {binding.Id}: specify exactly one model or skill owner.");
                if (binding.VisualSetId.HasValue)
                {
                    if (binding.VisualSetId_Ref == null || binding.ClipId.HasValue ||
                        (binding.VisualSetId_Ref.Category != EVisualCategory.Character && binding.VisualSetId_Ref.Category != EVisualCategory.Monster))
                        throw new InvalidOperationException($"TbCombatRender {binding.Id}: invalid model owner or unexpected clip.");
                    if (models.ContainsKey(binding.VisualSetId.Value))
                        throw new InvalidOperationException($"TbCombatRender {binding.Id}: duplicate model binding.");
                    models.Add(binding.VisualSetId.Value, binding);
                    continue;
                }

                SkillConfig skill = binding.SkillId_Ref;
                AnimationClipConfig clip = binding.ClipId_Ref;
                if (skill == null || clip == null || !binding.ClipId.HasValue || skill.CombatProfileId_Ref == null)
                    throw new InvalidOperationException($"TbCombatRender {binding.Id}: unresolved skill or clip.");
                bool belongs = skill.ProjectileClipId == clip.Id && clip.Action == EAnimationAction.Projectile ||
                    skill.ImpactClipId == clip.Id && clip.Action == EAnimationAction.Impact ||
                    skill.AreaClipIds.Contains(clip.Id) && clip.Action == EAnimationAction.Area ||
                    skill.CastClipId == clip.Id && clip.Action == EAnimationAction.CastEffect && !clip.Loop;
                if (!belongs)
                    throw new InvalidOperationException($"TbCombatRender {binding.Id}: clip is not an explicit visual of this skill.");
                var key = (skill.Id, clip.Id);
                if (skills.ContainsKey(key))
                    throw new InvalidOperationException($"TbCombatRender {binding.Id}: duplicate skill-clip binding.");
                skills.Add(key, binding);
            }
        }

        /// <summary>编辑器保存前及加载目录时检查显式队列，确保模型和特效位于透明区间且低于战斗反馈。</summary>
        /// <param name="queue">TbCombatRender.renderQueue 或待导出的新值。</param>
        /// <param name="feedbackQueue">当前 TbCombatPresentation.feedbackRenderQueue。</param>
        /// <exception cref="InvalidOperationException">队列低于透明范围或达到反馈层。</exception>
        public static void ValidateQueue(int queue, int feedbackQueue)
        {
            if (queue < TransparentQueueMinimum || queue >= feedbackQueue || queue > RenderQueueMaximum)
                throw new InvalidOperationException("TbCombatRender.renderQueue must be transparent and below the configured combat feedback queue.");
        }

        /// <summary>加载模型待机、移动、攻击或白闪时取得同一显式模型层级。</summary>
        /// <param name="visualSetId">TbVisualSet.id。</param>
        /// <returns>TbCombatRender.renderQueue。</returns>
        /// <exception cref="InvalidOperationException">当前表现方案未绑定该模型。</exception>
        public int ReadModel(int visualSetId)
        {
            if (!models.TryGetValue(visualSetId, out CombatRenderConfig binding))
                throw new InvalidOperationException($"Missing TbCombatRender model binding for TbVisualSet {visualSetId}.");
            return binding.RenderQueue;
        }

        /// <summary>加载飞行、命中、随身施法或任意 Area 片段时读取当前技能自身的片段层级。</summary>
        /// <param name="skillId">TbSkill.id，允许同一动画由不同技能配置不同层级。</param>
        /// <param name="clipId">该技能实际引用的 TbAnimationClip.id。</param>
        /// <returns>TbCombatRender.renderQueue。</returns>
        /// <exception cref="InvalidOperationException">当前表现方案缺少该技能与片段绑定。</exception>
        public int ReadSkill(int skillId, int clipId)
        {
            if (!skills.TryGetValue((skillId, clipId), out CombatRenderConfig binding))
                throw new InvalidOperationException($"Missing TbCombatRender skill binding for TbSkill {skillId}, TbAnimationClip {clipId}.");
            return binding.RenderQueue;
        }
    }
}
