using System;
using cfg;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>以中文像素参数编辑单位移动阻挡圆柱，并在场景视图显示用途。</summary>
    [CustomEditor(typeof(CombatCylinderAuthoring))]
    public sealed class CombatCylinderEditor : UnityEditor.Editor
    {
        /// <summary>Unity 绘制 Inspector 时使用中文字段名和用途说明。</summary>
        /// <remarks>这里只修改预制体；运行时数据必须通过统一碰撞配置菜单导出。</remarks>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var shape = target as CombatCylinderAuthoring;
            var capsule = shape == null ? null : shape.GetComponent<CapsuleCollider2D>();
            if (capsule != null)
            {
                EditorGUILayout.HelpBox("【统一碰撞范围】移动阻挡与受击判定共用下方 CapsuleCollider2D。直接点“编辑碰撞器”拖动，Offset/Size 保存后按 Luban 战斗像素比例导出。", MessageType.Info);
                Field("ConfigId", "配置编号", "对应角色表或怪物表中的编号。", true);
                Field("IsHero", "是否主角", "勾选导出到角色表；不勾选导出到怪物表。", true);
                Field("CombatRulesId", "战斗规则编号", "用于像素和 Scene 单位之间换算。", true);
                serializedObject.ApplyModifiedProperties();
                EditorGUILayout.HelpBox("保存 Prefab 后执行：Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。DOTS 不启用 Physics2D。", MessageType.None);
                return;
            }
            EditorGUILayout.HelpBox("【移动阻挡范围】决定角色和怪物移动时是否互相穿过。所有尺寸都填像素；保存预制体后再一键导入配置表。", MessageType.Info);
            Field("ConfigId", "配置编号", "对应角色表或怪物表中的编号。", true);
            Field("IsHero", "是否主角", "勾选导出到角色表；不勾选导出到怪物表。", true);
            Field("CombatRulesId", "战斗规则编号", "用于像素和场景预览之间换算。", true);
            Field("RadiusPixels", "半径（像素）", "移动阻挡的宽度。", false);
            Field("HeightPixels", "高度（像素）", "完整圆柱高度。", false);
            Field("OffsetXPixels", "横向偏移（像素）", "正值向右。", false);
            Field("OffsetYPixels", "前向偏移（像素）", "正值向前。", false);
            Field("ElevationPixels", "离地高度（像素）", "圆柱底面相对地面的高度。", false);
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("修改后：保存 Prefab → Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。", MessageType.None);
        }

        /// <summary>按序列化字段绘制本地化标签，并限制尺寸字段不得为负数。</summary>
        /// <param name="propertyName">组件序列化字段名。</param>
        /// <param name="label">Inspector 中文标签。</param>
        /// <param name="tooltip">鼠标悬停说明。</param>
        /// <param name="identity">是否为身份字段；身份字段不做数值截断。</param>
        private void Field(string propertyName, string label, string tooltip, bool identity)
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip));
            if (!identity && property.propertyType == SerializedPropertyType.Float && property.floatValue < 0f)
                property.floatValue = 0f;
        }

        /// <summary>Unity Scene 选中组件时按像素换算绘制半径和高度手柄。</summary>
        /// <remarks>场景手柄只修改像素字段，不读取或写入配置表。</remarks>
        private void OnSceneGUI()
        {
            var shape = target as CombatCylinderAuthoring;
            if (shape == null) return;
            if (shape.GetComponent<CapsuleCollider2D>() != null) return;
            float scale = PixelScale(shape.CombatRulesId);
            Transform root = shape.transform.parent;
            if (root == null) return;
            Vector3 bottom = root.position + new Vector3(shape.OffsetXPixels, shape.ElevationPixels, shape.OffsetYPixels) * scale;
            Vector3 center = bottom + Vector3.up * shape.HeightPixels * scale * 0.5f;
            Handles.color = shape.IsHero ? Color.cyan : Color.green;
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(Quaternion.identity, center, shape.RadiusPixels * scale);
            Vector3 top = Handles.Slider(bottom + Vector3.up * shape.HeightPixels * scale, Vector3.up);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "调整移动阻挡范围");
                shape.RadiusPixels = Mathf.Max(0f, radius / scale);
                shape.HeightPixels = Mathf.Max(0f, (top.y - bottom.y) / scale);
                EditorUtility.SetDirty(shape);
                PrefabUtility.RecordPrefabInstancePropertyModifications(shape);
            }
            Handles.Label(top, "移动阻挡范围");
        }

        /// <summary>编辑器重绘时显示上下圆面和母线，帮助区分移动阻挡与受击判定。</summary>
        /// <param name="shape">当前可见的移动阻挡组件。</param>
        /// <param name="gizmoType">Unity 选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawCylinder(CombatCylinderAuthoring shape, GizmoType gizmoType)
        {
            if (shape == null || shape.transform.parent == null) return;
            if (shape.GetComponent<CapsuleCollider2D>() != null) return;
            float scale = PixelScale(shape.CombatRulesId);
            Vector3 bottom = shape.transform.parent.position + new Vector3(shape.OffsetXPixels, shape.ElevationPixels, shape.OffsetYPixels) * scale;
            Vector3 top = bottom + Vector3.up * shape.HeightPixels * scale;
            float radius = shape.RadiusPixels * scale;
            Handles.color = shape.IsHero ? Color.cyan : Color.green;
            Handles.DrawWireDisc(top, Vector3.up, radius);
            Handles.DrawWireDisc(bottom, Vector3.up, radius);
            foreach (Vector3 axis in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                Handles.DrawLine(top + axis * radius, bottom + axis * radius);
        }

        /// <summary>从当前 Luban 战斗规则读取像素到场景坐标比例。</summary>
        /// <param name="rulesId">碰撞组件保存的战斗规则编号。</param>
        /// <returns>一个逻辑像素对应的场景长度。</returns>
        /// <exception cref="InvalidOperationException">规则不存在或比例非法。</exception>
        internal static float PixelScale(int rulesId)
        {
            CombatRulesConfig rules = CombatCylinderPipeline.ReadTables().TbCombatRules.Get(rulesId);
            if (!(rules.WorldUnitsPerPixel > 0f)) throw new InvalidOperationException("战斗规则中的像素比例必须大于零。");
            return rules.WorldUnitsPerPixel;
        }
    }
}
