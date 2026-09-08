using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>以中文像素参数编辑单位受击判定圆。</summary>
    [CustomEditor(typeof(CombatBodyCollisionAuthoring))]
    public sealed class CombatBodyCollisionEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制 Inspector 时显示用途、像素参数和导出入口。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("【受击判定范围】技能碰到这个圆才算命中。它不负责移动阻挡，也不改变角色图片大小。", MessageType.Info);
            Draw("RadiusPixels", "半径（像素）", "受击范围大小。", true);
            Draw("OffsetXPixels", "横向偏移（像素）", "正值向右。", false);
            Draw("OffsetYPixels", "前向偏移（像素）", "正值向前。", false);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("修改后：保存 Prefab → Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。", MessageType.None);
        }

        /// <summary>绘制一个本地化像素字段，并按需要限制正数。</summary>
        /// <param name="propertyName">序列化字段名。</param>
        /// <param name="label">中文显示名。</param>
        /// <param name="tooltip">鼠标悬停说明。</param>
        /// <param name="positive">是否必须保持非负。</param>
        private void Draw(string propertyName, string label, string tooltip, bool positive)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip));
            if (positive && property.floatValue < 0f) property.floatValue = 0f;
        }

        /// <summary>Scene 视图提供受击圆半径拖拽手柄。</summary>
        /// <remarks>只更新当前 Prefab 的像素字段。</remarks>
        private void OnSceneGUI()
        {
            var shape = target as CombatBodyCollisionAuthoring;
            var movement = shape == null || shape.transform.parent == null ? null :
                shape.transform.parent.GetComponentInChildren<CombatCylinderAuthoring>(true);
            if (shape == null || movement == null || movement.transform.parent == null) return;
            float scale = CombatCylinderEditor.PixelScale(movement.CombatRulesId);
            Vector3 center = movement.transform.parent.position + new Vector3(shape.OffsetXPixels, 0f, shape.OffsetYPixels) * scale;
            Handles.color = movement.IsHero ? Color.cyan : Color.red;
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(Quaternion.identity, center, shape.RadiusPixels * scale);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "调整受击判定范围");
                shape.RadiusPixels = Mathf.Max(0f, radius / scale);
                EditorUtility.SetDirty(shape);
                PrefabUtility.RecordPrefabInstancePropertyModifications(shape);
            }
            Handles.Label(center + Vector3.right * shape.RadiusPixels * scale, "受击判定范围");
        }

        /// <summary>未选中时也绘制受击圆，便于和移动阻挡范围同时对照。</summary>
        /// <param name="shape">受击判定组件。</param>
        /// <param name="type">Unity 选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawBody(CombatBodyCollisionAuthoring shape, GizmoType type)
        {
            var movement = shape == null || shape.transform.parent == null ? null :
                shape.transform.parent.GetComponentInChildren<CombatCylinderAuthoring>(true);
            if (movement == null || movement.transform.parent == null) return;
            float scale = CombatCylinderEditor.PixelScale(movement.CombatRulesId);
            Vector3 center = movement.transform.parent.position + new Vector3(shape.OffsetXPixels, 0f, shape.OffsetYPixels) * scale;
            Handles.color = movement.IsHero ? Color.cyan : Color.red;
            Handles.DrawWireDisc(center, Vector3.up, shape.RadiusPixels * scale);
        }
    }
}
