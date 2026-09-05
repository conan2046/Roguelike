using UnityEngine;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>预制体上的圆柱编辑数据；运行时 ECS 只读取导出的 Luban 表，不消费此组件。</summary>
    [DisallowMultipleComponent]
    public sealed class CombatCylinderAuthoring : MonoBehaviour
    {
        [Tooltip("对应 TbCharacter 或 TbMonster 的 ID。")]
        public int ConfigId;
        public bool IsHero;
        [Tooltip("用于世界单位和逻辑像素换算的 TbCombatRules ID。")]
        public int CombatRulesId;
        [Tooltip("圆柱半径，预览世界单位；导出为逻辑像素。")]
        public float Radius;
        [Tooltip("圆柱完整高度，预览世界单位；Y 轴竖直向上。")]
        public float Height;
    }
}
