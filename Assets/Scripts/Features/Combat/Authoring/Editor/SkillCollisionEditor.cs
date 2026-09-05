using cfg;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>技能圆形范围的可视编辑器；地面 X/Z 为技能局部右向/前向。</summary>
    [CustomEditor(typeof(SkillCollisionAuthoring))]
    public sealed class SkillCollisionEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制组件 Inspector 时展示编辑字段；保存资产后从技能菜单统一导出。</summary>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (GUILayout.Button("刷新技能表现并俯视碰撞范围")) SkillPrefabPreview.RefreshCurrent();
            EditorGUILayout.HelpBox("X 为局部右向，Z 为发射前向，Y 必须为 0。圆形为地面命中范围。保存后通过 Roguelike > Combat > 技能碰撞配置 导出；非弹丸条目仅预览，导出字段保持空。", MessageType.Info);
        }

        /// <summary>Unity Scene 重绘时提供圆形半径手柄，输入量化到世界单位三位小数。</summary>
        /// <remarks>只修改选中的编辑组件，支持 Undo；不写源表或创建运行时 Collider。</remarks>
        private void OnSceneGUI()
        {
            var shape = target as SkillCollisionAuthoring;
            if (shape == null) return;
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(Quaternion.identity, shape.transform.position, shape.Radius);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "调整技能碰撞半径");
                shape.Radius = ConfigNumber.Decode(ConfigNumber.Encode(Mathf.Max(0, radius)));
                EditorUtility.SetDirty(shape);
                PrefabUtility.RecordPrefabInstancePropertyModifications(shape);
            }
        }

        /// <summary>Unity 绘制 Gizmo 时显示真实地面圆形及中心；不以特效贴图边缘代替碰撞范围。</summary>
        /// <param name="shape">技能碰撞节点。</param>
        /// <param name="type">编辑器选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawCircle(SkillCollisionAuthoring shape, GizmoType type)
        {
            if (shape == null) return;
            Handles.color = Color.cyan;
            Handles.DrawWireDisc(shape.transform.position, Vector3.up, shape.Radius);
            Handles.Label(shape.transform.position, "Skill " + shape.SkillId + " Collision");
        }
    }
}
