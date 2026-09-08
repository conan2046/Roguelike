using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>说明单位命中特效挂点的编辑与导出协议。</summary>
    [CustomEditor(typeof(CombatHitEffectAnchorAuthoring))]
    public sealed class CombatHitEffectAnchorEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制 Inspector 时提示用户通过 Transform 拖拽挂点。</summary>
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("【命中特效挂点】使用上方 Transform 的 Position 或场景移动手柄调整。保存 Prefab 后导出，XY 会换算成逻辑像素写入角色/怪物 Luban 表。", MessageType.Info);
            EditorGUILayout.HelpBox("弹丸命中后在“目标当前位置 + 此挂点偏移”播放命中特效；运行时不实例化本 Prefab，也不启用 Physics2D。", MessageType.None);
        }

        /// <summary>Scene 视图绘制十字和中文标签，便于在角色图像上定位命中点。</summary>
        /// <param name="anchor">当前挂点组件。</param>
        /// <param name="type">Unity 选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawAnchor(CombatHitEffectAnchorAuthoring anchor, GizmoType type)
        {
            if (anchor == null) return;
            float size = HandleUtility.GetHandleSize(anchor.transform.position) * 0.08f;
            Handles.color = Color.cyan;
            Handles.DrawLine(anchor.transform.position - Vector3.right * size, anchor.transform.position + Vector3.right * size);
            Handles.DrawLine(anchor.transform.position - Vector3.up * size, anchor.transform.position + Vector3.up * size);
            Handles.Label(anchor.transform.position + Vector3.up * size, "命中特效挂点");
        }
    }
}
