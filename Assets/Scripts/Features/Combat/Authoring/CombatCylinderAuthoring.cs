using UnityEngine;
using UnityEngine.Serialization;

namespace Roguelike.Features.Combat.Authoring
{
    /// <summary>角色或怪物预制体上的移动阻挡范围；运行时只读取导出的 Luban 表。</summary>
    [DisallowMultipleComponent]
    public sealed class CombatCylinderAuthoring : MonoBehaviour
    {
        [Tooltip("对应角色表或怪物表的编号；导出时用它找到要写入的配置行。")]
        public int ConfigId;
        [Tooltip("勾选表示主角，不勾选表示怪物；决定导出到角色表还是怪物表。")]
        public bool IsHero;
        [Tooltip("战斗规则编号；只用于把像素换算成 Unity 场景中的预览大小。")]
        public int CombatRulesId;
        [FormerlySerializedAs("Radius")]
        [Tooltip("移动阻挡圆柱的半径，单位：像素。它决定角色之间不能互相穿过的宽度。")]
        public float RadiusPixels;
        [FormerlySerializedAs("Height")]
        [Tooltip("移动阻挡圆柱的完整高度，单位：像素。它决定不同高度的单位是否发生阻挡。")]
        public float HeightPixels;
        [Tooltip("移动阻挡中心相对角色原点的横向偏移，单位：像素；正值向右。")]
        public float OffsetXPixels;
        [Tooltip("移动阻挡中心相对角色原点的前向偏移，单位：像素；正值向前。")]
        public float OffsetYPixels;
        [Tooltip("移动阻挡圆柱底面离地高度，单位：像素。")]
        public float ElevationPixels;
        [HideInInspector]
        public int DataVersion;
    }
}
