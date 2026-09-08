using UnityEngine;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>角色或怪物预制体上的受击判定范围；运行时只读取导出的 Luban 表。</summary>
    [DisallowMultipleComponent]
    public sealed class CombatBodyCollisionAuthoring : MonoBehaviour
    {
        [Tooltip("受击判定圆的半径，单位：像素。技能碰到这个圆才算命中。")]
        public float RadiusPixels;
        [Tooltip("受击判定中心相对角色原点的横向偏移，单位：像素；正值向右。")]
        public float OffsetXPixels;
        [Tooltip("受击判定中心相对角色原点的前向偏移，单位：像素；正值向前。")]
        public float OffsetYPixels;
    }
}
