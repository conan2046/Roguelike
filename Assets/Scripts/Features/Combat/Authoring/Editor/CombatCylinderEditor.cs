using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>圆柱预制体的尺寸、中心拖拽及配置导出入口。</summary>
    [CustomEditor(typeof(CombatCylinderAuthoring))]
    public sealed class CombatCylinderEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制 Inspector 时仅展示尺寸字段和统一管理入口说明。</summary>
        /// <remarks>组件面板不执行导出；保存后的微调统一由圆柱碰撞配置窗口导出。</remarks>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Y 轴向上；移动节点调整圆柱中心。地面 X/Z 对应战斗 X/Y。保存后统一在 Roguelike > Combat > 圆柱碰撞配置 中导出。", MessageType.Info);
        }

        /// <summary>Unity Scene 选中组件时绘制半径、高度手柄；参数和节点位移均支持撤销。</summary>
        /// <remarks>只修改所选 Prefab 编辑对象，不在运行时分配碰撞体。</remarks>
        private void OnSceneGUI()
        {
            var shape = (CombatCylinderAuthoring)target;
            // Prefab Stage 切换/导入后，Unity 可能在旧 Inspector 销毁前再派发一次 Scene 重绘。
            if (shape == null) return;
            var center = shape.transform.position;
            Handles.color = Color.green;
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(Quaternion.identity, center, shape.Radius);
            Vector3 top = Handles.Slider(center + Vector3.up * shape.Height / 2, Vector3.up);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "调整圆柱尺寸");
                shape.Radius = Mathf.Max(0, radius);
                shape.Height = Mathf.Max(0, (top.y - center.y) * 2);
                EditorUtility.SetDirty(shape);
                PrefabUtility.RecordPrefabInstancePropertyModifications(shape);
            }
            Handles.Label(center + Vector3.up * shape.Height / 2, "MovementCollision");
        }

        /// <summary>编辑器重绘时显示上下圆面及母线，圆柱无半球端盖；数值来自预制体编辑组件。</summary>
        /// <param name="shape">当前可见的圆柱编辑节点。</param>
        /// <param name="gizmoType">Unity 选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawCylinder(CombatCylinderAuthoring shape, GizmoType gizmoType)
        {
            if (shape == null) return;
            Handles.color = shape.IsHero ? Color.cyan : Color.green;
            Vector3 top = shape.transform.position + Vector3.up * shape.Height / 2;
            Vector3 bottom = shape.transform.position - Vector3.up * shape.Height / 2;
            Handles.DrawWireDisc(top, Vector3.up, shape.Radius);
            Handles.DrawWireDisc(bottom, Vector3.up, shape.Radius);
            foreach (var axis in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                Handles.DrawLine(top + axis * shape.Radius, bottom + axis * shape.Radius);
        }
    }
}
