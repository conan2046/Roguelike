using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using cfg;
using Roguelike.Features.Combat.Runtime;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Roguelike.Features.Combat.Authoring.Editor
{
    /// <summary>为战斗血条 Prefab 提供中文像素参数、编辑预览和 Luban 导出。</summary>
    [CustomEditor(typeof(CombatHealthBarView))]
    public sealed class CombatHealthBarEditor : UnityEditor.Editor
    {
        private string error;

        /// <summary>Unity 选中血条调参组件时绘制中文说明、像素字段和导出按钮。</summary>
        /// <remarks>修改像素值会立即刷新内部 World Space Canvas；只有点击导出才写源 Excel 并运行 Luban。</remarks>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "用途：挂到角色或怪物的 DamageFloatAnchor 下预览血条。只填写像素宽高；根节点 Scale 固定为 1。运行时不会实例化此 Prefab，而是由 DOTS 读取 Luban 后创建血条实体。",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("presentationId"),
                    new GUIContent("战斗表现方案 ID", "对应 TbCombatPresentation.id。"));

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("widthPixels"),
                new GUIContent("血条宽度（像素）", "最终 DOTS 血条宽度。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("heightPixels"),
                new GUIContent("血条高度（像素）", "最终 DOTS 血条高度。"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("previewNormalizedHealth"),
                new GUIContent("预览血量比例", "只改变编辑器红色填充，0 为空、1 为满。"));
            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();

            var view = (CombatHealthBarView)target;
            if (changed) Refresh(view);

            EditorGUILayout.Space();
            if (GUILayout.Button("刷新像素预览")) Refresh(view);
            if (GUILayout.Button("导出血条像素到 Luban"))
            {
                try
                {
                    CombatHealthBarPipeline.Export(view);
                    error = null;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    Debug.LogException(exception);
                }
            }

            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        /// <summary>读取当前战斗规则的像素显示比例并刷新选中血条。</summary>
        /// <param name="view">当前 Inspector 对应的调参组件。</param>
        private void Refresh(CombatHealthBarView view)
        {
            try
            {
                CombatHealthBarPipeline.RefreshPreview(view);
                error = null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogException(exception);
            }
        }
    }

    /// <summary>把 CombatHealthBar Prefab 的最终像素宽高回写 TbCombatPresentation 并刷新预览。</summary>
    public static class CombatHealthBarPipeline
    {
        private static readonly string[] Fields =
            { "healthBarWidthPixelsMilli", "healthBarHeightPixelsMilli" };

        /// <summary>从正式 CombatHealthBar Prefab 导出像素宽高并重新生成 Luban。</summary>
        /// <exception cref="InvalidOperationException">Prefab 缺失血条调参组件或导出链失败时抛出。</exception>
        /// <remarks>供 Unity 菜单与指定 projectPath 的 BatchMode 共用；不读取角色/怪物中的预览实例。</remarks>
        [MenuItem("Roguelike/战斗/血条配置/导出血条 Prefab 并生成配置")]
        public static void ExportSavedPrefab()
        {
            const string path = "Assets/GameContent/UI/Combat/Prefabs/CombatHealthBar.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            CombatHealthBarView view = prefab == null ? null : prefab.GetComponent<CombatHealthBarView>();
            if (view == null) throw new InvalidOperationException(path + " 缺少 CombatHealthBarView。");
            Export(view);
        }

        /// <summary>命令行自动化入口，行为与菜单导出一致。</summary>
        /// <remarks>仅供当前项目 BatchMode 验证调用。</remarks>
        public static void ExportFromCommandLine() => ExportSavedPrefab();

        /// <summary>按 Prefab 的表现方案找到正式关卡战斗规则，并刷新内部显示画布。</summary>
        /// <param name="view">Prefab 资产或其预览实例上的血条调参组件。</param>
        /// <exception cref="InvalidOperationException">表现方案未被唯一正式关卡绑定或像素参数无效时抛出。</exception>
        /// <remarks>只修改当前对象的预览节点与 Prefab 覆盖，不写配置表。</remarks>
        internal static void RefreshPreview(CombatHealthBarView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            Tables tables = CombatCylinderPipeline.ReadTables();
            CombatRulesConfig rules = ResolveRules(tables, view.PresentationId);
            view.ApplyPreview(rules.WorldUnitsPerPixel);
            EditorUtility.SetDirty(view);
            if (PrefabUtility.IsPartOfPrefabInstance(view))
                PrefabUtility.RecordPrefabInstancePropertyModifications(view);
            SceneView.RepaintAll();
        }

        /// <summary>保存当前像素字段、写入源 Excel、运行 Luban，并核对生成配置等于 Prefab。</summary>
        /// <param name="view">作为本次编辑源的血条调参组件。</param>
        /// <exception cref="InvalidOperationException">Prefab 参数、表绑定、写表或生成结果无效时抛出。</exception>
        /// <remarks>失败时恢复 Excel 原文件；不会实例化运行时 GameObject，也不会创建预览资产。</remarks>
        internal static void Export(CombatHealthBarView view)
        {
            RefreshPreview(view);
            if (!(view.WidthPixels > 0f) || !(view.HeightPixels > 0f))
                throw new InvalidOperationException("血条宽度和高度必须大于零像素。");

            AssetDatabase.SaveAssets();
            string path = Directory.GetFiles("Config/Datas/Tables", "*_CombatPresentationConfig.xlsx").Single();
            byte[] backup = File.ReadAllBytes(path);
            try
            {
                ConfigWorkbookWriter.Patch(path, Fields, new Dictionary<int, int[]>
                {
                    [view.PresentationId] = new[]
                    {
                        ConfigNumber.Encode(view.WidthPixels),
                        ConfigNumber.Encode(view.HeightPixels)
                    }
                });
                CombatCollisionPipeline.RunLuban();
            }
            catch
            {
                File.WriteAllBytes(path, backup);
                throw;
            }

            AssetDatabase.Refresh();
            CombatPresentationConfig saved = CombatCylinderPipeline.ReadTables()
                .TbCombatPresentation.Get(view.PresentationId);
            if (saved.HealthBarWidthPixelsMilli != ConfigNumber.Encode(view.WidthPixels) ||
                saved.HealthBarHeightPixelsMilli != ConfigNumber.Encode(view.HeightPixels))
                throw new InvalidOperationException("Luban 生成后的血条像素尺寸与 Prefab 不一致。");
            Debug.Log($"血条像素配置导出完成：{view.WidthPixels}×{view.HeightPixels}，运行时 DOTS 只读取 Luban。");
        }

        /// <summary>查找使用指定表现方案的唯一正式关卡战斗规则。</summary>
        /// <param name="tables">已解析引用的当前 Luban 配置。</param>
        /// <param name="presentationId">CombatHealthBar 对应的 TbCombatPresentation.id。</param>
        /// <returns>唯一 TbCombatRules 行。</returns>
        /// <exception cref="InvalidOperationException">没有绑定或多个关卡给同一表现方案绑定不同战斗规则时抛出。</exception>
        private static CombatRulesConfig ResolveRules(Tables tables, int presentationId)
        {
            int[] rulesIds = tables.TbStage.DataList
                .Where(stage => stage.PresentationId == presentationId)
                .Select(stage => stage.CombatRulesId)
                .Distinct()
                .ToArray();
            if (rulesIds.Length != 1)
                throw new InvalidOperationException($"战斗表现方案 {presentationId} 必须绑定唯一正式关卡战斗规则。");
            return tables.TbCombatRules.Get(rulesIds[0]);
        }
    }
}
