using UnityEngine;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>技能某类表现的独立位置与缩放编辑节点；保存后导出到 TbSkill。</summary>
    [DisallowMultipleComponent]
    public sealed class SkillVisualPlacementAuthoring : MonoBehaviour
    {
        /// <summary>节点控制的技能表现类别。</summary>
        public enum VisualKind
        {
            Projectile,
            Impact,
            Area
        }

        [Tooltip("弹丸表现、命中特效表现或范围特效表现；决定导出的 TbSkill 字段。")]
        public VisualKind Kind;
    }
}
