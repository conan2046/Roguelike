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
                    CombatHealthBarPipeline.ExportSavedPrefab();
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
            { "healthBarWidthPixels", "healthBarHeightPixels" };

        /// <summary>从正式 CombatHealthBar Prefab 导出像素宽高并重新生成 Luban。</summary>
        /// <exception cref="InvalidOperationException">Prefab 缺失血条调参组件或导出链失败时抛出。</exception>
        /// <remarks>供 Unity 菜单与指定 projectPath 的 BatchMode 共用；不读取角色/怪物中的预览实例。</remarks>
        [MenuItem("Roguelike/战斗/血条配置/导出血条 Prefab 并生成配置")]
        public static void ExportSavedPrefab()
        {
            SkillCollisionPipeline.EnsureEditable();
            Export(LoadSavedPrefab());
        }

        /// <summary>预览血条 Prefab 将写入的字段和值，不修改源表或 Luban。</summary>
        /// <exception cref="InvalidOperationException">Prefab 缺失、参数非法或检测到 Excel 人工修改冲突。</exception>
        [MenuItem("Roguelike/战斗/血条配置/预览血条导出变更（不写表）")]
        public static void PreviewSavedPrefab()
        {
            SkillCollisionPipeline.EnsureEditable();
            CombatHealthBarView view = LoadSavedPrefab();
            ValidateSize(view);
            string path = PresentationWorkbookPath();
            var transaction = new AuthoringExportTransaction("combat-health-bar", true);
            ConfigWorkbookWriter.Patch(path, Fields, Values(view), transaction);
            transaction.Complete();
            Debug.Log("血条配置 Dry Run 完成：未写入源表。查看 Library/CombatAuthoringDryRun.log。");
        }

        /// <summary>命令行自动化入口，行为与菜单导出一致。</summary>
        /// <remarks>仅供当前项目 BatchMode 验证调用。</remarks>
        public static void ExportFromCommandLine() => ExportSavedPrefab();

        /// <summary>命令行血条 Dry Run 入口；不保存 Prefab、Excel 或 Luban。</summary>
        /// <remarks>仅供当前项目 BatchMode 冲突检查调用。</remarks>
        public static void PreviewFromCommandLine() => PreviewSavedPrefab();

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
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!EditorUtility.IsPersistent(view))
                throw new InvalidOperationException("血条配置只能从正式 CombatHealthBar Prefab 资产导出。");
            ValidateSize(view);
            AssetDatabase.SaveAssetIfDirty(view.transform.root.gameObject);
            string path = PresentationWorkbookPath();
            Dictionary<int, float[]> values = Values(view);
            var preview = new AuthoringExportTransaction("combat-health-bar", true);
            ConfigWorkbookWriter.Patch(path, Fields, values, preview);
            preview.Complete();
            byte[] backup = File.ReadAllBytes(path);
            try
            {
                var transaction = new AuthoringExportTransaction("combat-health-bar", false);
                ConfigWorkbookWriter.Patch(path, Fields, values, transaction);
                transaction.Complete();
                CombatCollisionPipeline.RunLuban();
                AssetDatabase.Refresh();
                ValidateSaved(view);
                transaction.Commit();
            }
            catch
            {
                File.WriteAllBytes(path, backup);
                throw;
            }

            Debug.Log($"血条像素配置导出完成：Dry Run 无冲突，{view.WidthPixels}×{view.HeightPixels}，运行时 DOTS 只读取 Luban。");
        }

        /// <summary>加载作为正式编辑源的唯一血条 Prefab。</summary>
        /// <returns>Prefab 根节点上的血条调参组件。</returns>
        /// <exception cref="InvalidOperationException">正式 Prefab 或调参组件缺失。</exception>
        private static CombatHealthBarView LoadSavedPrefab()
        {
            const string path = "Assets/GameContent/UI/Combat/Prefabs/CombatHealthBar.prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            CombatHealthBarView view = prefab == null ? null : prefab.GetComponent<CombatHealthBarView>();
            return view ?? throw new InvalidOperationException(path + " 缺少 CombatHealthBarView。");
        }

        /// <summary>解析血条 Prefab 唯一允许写入的战斗表现源表。</summary>
        /// <returns>CombatPresentationConfig 工作簿路径。</returns>
        private static string PresentationWorkbookPath() =>
            Directory.GetFiles("Config/Datas/Tables", "*_CombatPresentationConfig.xlsx").Single();

        /// <summary>将血条 Prefab 像素字段量化为本次工作簿提案。</summary>
        /// <param name="view">血条编辑源。</param>
        /// <returns>按表现方案 ID 索引的宽高像素。</returns>
        private static Dictionary<int, float[]> Values(CombatHealthBarView view) =>
            new Dictionary<int, float[]>
            {
                [view.PresentationId] = new[]
                {
                    (float)Math.Round(view.WidthPixels, 3, MidpointRounding.AwayFromZero),
                    (float)Math.Round(view.HeightPixels, 3, MidpointRounding.AwayFromZero)
                }
            };

        /// <summary>拒绝无法成为正式配置的血条像素尺寸。</summary>
        /// <param name="view">血条编辑源。</param>
        /// <exception cref="InvalidOperationException">宽度或高度不是正数。</exception>
        private static void ValidateSize(CombatHealthBarView view)
        {
            if (!(view.WidthPixels > 0f) || !float.IsFinite(view.WidthPixels) ||
                !(view.HeightPixels > 0f) || !float.IsFinite(view.HeightPixels))
                throw new InvalidOperationException("血条宽度和高度必须是有限正数像素。");
        }

        /// <summary>核对 Luban 生成后的血条像素仍与 Prefab 编辑源一致。</summary>
        /// <param name="view">本次导出的血条编辑源。</param>
        /// <exception cref="InvalidOperationException">生成配置与 Prefab 不一致。</exception>
        private static void ValidateSaved(CombatHealthBarView view)
        {
            CombatPresentationConfig saved = CombatCylinderPipeline.ReadTables()
                .TbCombatPresentation.Get(view.PresentationId);
            if (Mathf.Abs(saved.HealthBarWidthPixels - view.WidthPixels) > 0.00051f ||
                Mathf.Abs(saved.HealthBarHeightPixels - view.HeightPixels) > 0.00051f)
                throw new InvalidOperationException("Luban 生成后的血条像素尺寸与 Prefab 不一致。");
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
