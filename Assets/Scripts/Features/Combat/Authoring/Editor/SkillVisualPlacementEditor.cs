using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>以中文说明技能表现节点的位置与统一缩放编辑方式。</summary>
    [CustomEditor(typeof(SkillVisualPlacementAuthoring))]
    public sealed class SkillVisualPlacementEditor : UnityEditor.Editor
    {
        private static readonly string[] KindNames = { "弹丸表现", "命中特效表现", "范围特效表现" };

        /// <summary>Unity 绘制 Inspector 时显示节点用途、约束和预览刷新入口。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty kind = serializedObject.FindProperty("Kind");
            kind.enumValueIndex = EditorGUILayout.Popup(new GUIContent("表现用途", "决定写入 TbSkill 的哪组表现位置与缩放字段。"), kind.enumValueIndex, KindNames);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("使用上方 Transform 直接调整特效：Prefab 中看到的位置和大小就是运行时结果。Position X/Y 调位置；Scale X/Y/Z 必须相同并控制大小。", MessageType.Info);
            if (GUILayout.Button("刷新表现并对准视图")) SkillPrefabPreview.RefreshCurrent();
            EditorGUILayout.HelpBox("保存 Prefab 后执行：Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。", MessageType.None);
        }
    }
}
