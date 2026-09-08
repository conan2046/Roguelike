using System;
using System.Linq;
using cfg;
using UnityEditor;
using UnityEngine;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>以中文像素参数编辑技能弹丸或范围技能的命中圆。</summary>
    [CustomEditor(typeof(SkillCollisionAuthoring))]
    public sealed class SkillCollisionEditor : UnityEditor.Editor
    {
        private static readonly string[] KindNames = { "弹丸命中范围", "范围技能命中范围" };

        /// <summary>Unity 绘制 Inspector 时显示中文用途、像素字段和统一导出入口。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty kind = serializedObject.FindProperty("Kind");
            var shape = target as SkillCollisionAuthoring;
            var circle = shape == null ? null : shape.GetComponent<CircleCollider2D>();
            EditorGUILayout.HelpBox(kind.enumValueIndex == 0
                ? "【弹丸命中范围】这个圆随技能飞行；碰到目标的受击判定圆才算命中。"
                : "【范围技能命中范围】技能在锁定位置释放时，用这个圆一次性判断命中目标。", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SkillId"), new GUIContent("技能编号", "对应技能表中的编号。"));
            kind.enumValueIndex = EditorGUILayout.Popup(new GUIContent("节点用途", "决定写入弹丸字段还是范围技能字段。"), kind.enumValueIndex, KindNames);
            if (circle != null)
            {
                EditorGUILayout.HelpBox("下方 CircleCollider2D 在 Prefab 中看到的大小和位置，就是运行时命中范围；直接拖拽圆心和半径即可。DOTS 不启用 Physics2D。", MessageType.Info);
                serializedObject.ApplyModifiedProperties();
                DrawRuntimeGeometry(shape);
                if (GUILayout.Button("刷新表现并对准命中范围")) SkillPrefabPreview.RefreshCurrent();
                EditorGUILayout.HelpBox("修改后：保存 Prefab → Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。", MessageType.None);
                return;
            }
            SerializedProperty radius = serializedObject.FindProperty("RadiusPixels");
            EditorGUILayout.PropertyField(radius, new GUIContent("半径（像素）", "命中判定圆大小。"));
            radius.floatValue = Mathf.Max(0f, radius.floatValue);
            if (kind.enumValueIndex == 0)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("OffsetXPixels"), new GUIContent("横向偏移（像素）", "相对技能原点，正值向右。"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("OffsetYPixels"), new GUIContent("前向偏移（像素）", "相对技能原点，正值向前。"));
            }
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("刷新表现并对准命中范围")) SkillPrefabPreview.RefreshCurrent();
            EditorGUILayout.HelpBox("修改后：保存 Prefab → Roguelike > 战斗 > 碰撞配置 > 导出全部预制体并生成配置。", MessageType.None);
        }

        /// <summary>Scene 视图提供技能命中圆半径拖拽手柄。</summary>
        /// <remarks>手柄结果量化到千分之一像素，只修改当前 Prefab。</remarks>
        private void OnSceneGUI()
        {
            var shape = target as SkillCollisionAuthoring;
            if (shape == null || shape.transform.parent == null) return;
            if (shape.GetComponent<CircleCollider2D>() != null) return;
            float scale = PixelScale();
            Vector3 center = Center(shape, scale);
            Handles.color = shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile ? Color.yellow : Color.magenta;
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(Quaternion.identity, center, shape.RadiusPixels * scale);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(shape, "调整技能命中范围");
                shape.RadiusPixels = ConfigNumber.Decode(ConfigNumber.Encode(Mathf.Max(0f, radius / scale)));
                EditorUtility.SetDirty(shape);
                PrefabUtility.RecordPrefabInstancePropertyModifications(shape);
            }
            Handles.Label(center + Vector3.right * shape.RadiusPixels * scale,
                shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile ? "弹丸命中范围" : "范围技能命中范围");
        }

        /// <summary>编辑器重绘时显示技能真实命中圆，不以特效贴图边缘代替。</summary>
        /// <param name="shape">技能命中组件。</param>
        /// <param name="type">Unity 选择状态。</param>
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.InSelectionHierarchy)]
        private static void DrawCircle(SkillCollisionAuthoring shape, GizmoType type)
        {
            if (shape == null || shape.transform.parent == null) return;
            if (shape.GetComponent<CircleCollider2D>() != null) return;
            float scale = PixelScale();
            Handles.color = shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile ? Color.yellow : Color.magenta;
            Handles.DrawWireDisc(Center(shape, scale), Vector3.forward, shape.RadiusPixels * scale);
        }

        /// <summary>计算忽略视觉根缩放后的技能碰撞中心。</summary>
        /// <param name="shape">技能命中组件。</param>
        /// <param name="scale">像素到场景坐标比例。</param>
        /// <returns>场景视图中的命中中心。</returns>
        private static Vector3 Center(SkillCollisionAuthoring shape, float scale)
        {
            float x = shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile ? shape.OffsetXPixels : 0f;
            float y = shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile ? shape.OffsetYPixels : 0f;
            return shape.transform.parent.position + new Vector3(x, y, 0f) * scale;
        }

        /// <summary>在 Inspector 中显示当前 CircleCollider2D 保存后将写入 Luban 并供 DOTS 使用的最终像素几何。</summary>
        /// <param name="shape">当前技能命中节点。</param>
        /// <remarks>只读取 Prefab 变换和 TbCombatRules.worldUnitsPerPixel，不修改资产或配置。</remarks>
        private static void DrawRuntimeGeometry(SkillCollisionAuthoring shape)
        {
            try
            {
                int[] values = shape.Kind == SkillCollisionAuthoring.CollisionKind.Projectile
                    ? SkillCollisionPipeline.Values(shape)
                    : SkillCollisionPipeline.AreaValues(shape);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("运行时最终值（导出后）", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("半径", ConfigNumber.Decode(values[0]).ToString("0.###") + " 像素");
                if (values.Length > 1)
                {
                    EditorGUILayout.LabelField("位置 X", ConfigNumber.Decode(values[1]).ToString("0.###") + " 像素");
                    EditorGUILayout.LabelField("位置 Y", ConfigNumber.Decode(values[2]).ToString("0.###") + " 像素");
                }
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox("无法计算运行时最终值：" + exception.Message, MessageType.Warning);
            }
        }

        /// <summary>从当前唯一战斗场景规则读取像素换算比例。</summary>
        /// <returns>一个逻辑像素对应的场景长度。</returns>
        /// <exception cref="InvalidOperationException">战斗规则不唯一或比例非法。</exception>
        internal static float PixelScale()
        {
            var tables = CombatCylinderPipeline.ReadTables();
            int[] rules = tables.TbPerformanceScenario.DataList.Where(item => item.Kind == EPerformanceKind.Combat && item.CombatRulesId.HasValue)
                .Select(item => item.CombatRulesId.Value).Distinct().ToArray();
            if (rules.Length != 1) throw new InvalidOperationException("技能预览需要唯一的战斗规则编号。");
            float scale = tables.TbCombatRules.Get(rules[0]).WorldUnitsPerPixel;
            if (!(scale > 0f)) throw new InvalidOperationException("战斗规则中的像素比例必须大于零。");
            return scale;
        }
    }
}
