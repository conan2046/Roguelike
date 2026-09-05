using UnityEngine;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>技能预制体的圆形碰撞编辑节点；导出到 TbSkill 后由 ECS 消费，运行时不读取该组件。</summary>
    [DisallowMultipleComponent]
    public sealed class SkillCollisionAuthoring : MonoBehaviour
    {
        [Tooltip("对应 TbSkill.id；每个技能预制体必须唯一。")]
        public int SkillId;
        [Tooltip("世界单位半径；导出为 TbSkill.projectileRadiusMilli，最多三位小数。")]
        public float Radius;
    }
}
