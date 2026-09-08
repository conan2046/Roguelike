using UnityEngine;
using UnityEngine.Serialization;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>技能预制体的命中范围编辑节点；导出到 TbSkill 后由 ECS 消费。</summary>
    [DisallowMultipleComponent]
    public sealed class SkillCollisionAuthoring : MonoBehaviour
    {
        /// <summary>技能碰撞节点的用途；决定导出的配置字段。</summary>
        public enum CollisionKind
        {
            Projectile,
            TargetArea
        }

        [Tooltip("对应技能表的编号；导出时用它找到要写入的配置行。")]
        public int SkillId;
        [Tooltip("弹丸命中范围：随弹丸移动；范围技能命中范围：在目标位置一次性判定。")]
        public CollisionKind Kind;
        [FormerlySerializedAs("Radius")]
        [Tooltip("命中判定圆半径，单位：像素。")]
        public float RadiusPixels;
        [Tooltip("命中中心相对技能原点的横向偏移，单位：像素；正值向右。范围技能不使用。")]
        public float OffsetXPixels;
        [Tooltip("命中中心相对技能原点的前向偏移，单位：像素；正值向前。范围技能不使用。")]
        public float OffsetYPixels;
        [HideInInspector]
        public int DataVersion;
    }
}
