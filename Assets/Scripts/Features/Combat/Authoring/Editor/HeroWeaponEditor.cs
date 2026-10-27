using UnityEditor;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>保留独立武器原有关联编辑，并显示其模型的 Luban 渲染层级。</summary>
    [CustomEditor(typeof(HeroWeaponAuthoring))]
    public sealed class HeroWeaponEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制武器标识时追加模型队列编辑入口，不修改 Transform 或资产引用。</summary>
        /// <remarks>默认关联字段按原方式编辑；层级单独保存回 TbCombatRender，运行时不读取该组件。</remarks>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var weapon = target as HeroWeaponAuthoring;
            if (weapon != null) CombatRenderInspector.DrawWeapon(weapon.WeaponConfigId);
        }
    }
}
